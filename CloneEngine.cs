using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace clony;

public sealed record CloneOptions(
    PhysicalDisk Source,
    PhysicalDisk Destination,
    int BufferBytes,        // read/write block size
    bool MoveBackupGpt,     // fix GPT so the extra space on a bigger disk is usable
    bool VerifyAfter,       // bit-for-bit compare after the copy
    bool DryRun,            // checks + plan only, writes nothing
    bool GrowMain = false,  // move partitions after the chosen one to the end and give it the extra space
    long? GrowAt = null);   // start byte of the partition that gets the space (null = the largest)

public sealed record CloneResult(bool Success, VerifyResult? Verify, string Message);

/// <summary>
/// Bit-for-bit disk clone. Every sector of the source is copied, so partition table, EFI, Windows, recovery
/// and boot data are identical and the copy boots as-is. With GrowMain, partitions after the main one are
/// written at the end of the bigger disk instead of the same offset, and the main partition is extended
/// into the space between. The source is only ever read; volumes on both disks are locked during the copy.
/// </summary>
public sealed class CloneEngine
{
    private readonly Action<string> _log;
    private readonly IProgress<CopyProgress>? _progress;

    public CloneEngine(Action<string> log, IProgress<CopyProgress>? progress)
    {
        _log = log;
        _progress = progress;
    }

    public Task<CloneResult> RunAsync(CloneOptions opt, CancellationToken ct) =>
        Task.Run(() => Run(opt, ct), CancellationToken.None);

    private CloneResult Run(CloneOptions opt, CancellationToken ct)
    {
        try
        {
            Guard(opt);
            DiskPart.EnsureOnline(opt.Source.Index, _log);
            DiskPart.EnsureOnline(opt.Destination.Index, _log);
            var (sg, dg, layout) = Inspect(opt);
            GrowPlan? plan = null;
            string? noGrow = null;
            if (opt.GrowMain) (plan, noGrow) = GrowPlanner.Build(layout, sg, dg, opt.GrowAt);
            LogPlan(opt, sg, dg, layout, plan, noGrow);
            if (opt.DryRun)
                return Done(true, null, "Dry run complete - nothing was written. Untick 'Dry run' to clone for real.");

            _log(Banner("PREPARE"));
            DiskPart.ClearReadOnly(opt.Destination.Index, _log);
            var extents = plan?.Extents ?? new[] { new CopyExtent(0, 0, sg.LengthBytes) };
            var grower = new CopyGrower(_log);
            VolumeLocks? srcLocks = null, dstLocks = null;
            try
            {
                srcLocks = VolumeLocks.Lock(opt.Source.Index, _log);
                dstLocks = VolumeLocks.Lock(opt.Destination.Index, _log);
                Copy(opt, sg, extents, ct);
                if (ct.IsCancellationRequested)
                    return Done(false, null, "Cancelled. The destination is INCOMPLETE and won't boot - run the clone again before using it.");

                var expected = new List<(long Start, long Length)>();
                if (plan != null) expected.AddRange(ApplyPlan(opt, layout, plan));
                else if (opt.MoveBackupGpt && layout.IsGpt && dg.LengthBytes > sg.LengthBytes)
                {
                    FixPartitionTable(opt);
                    expected.AddRange(PartitionTable.ExpectedCloneChanges(layout, sg, dg));
                }

                VerifyResult? verify = null;
                if (opt.VerifyAfter)
                {
                    _log(Banner("VERIFY"));
                    using var s = NativeDisk.OpenRead(opt.Source.Index);
                    using var d = NativeDisk.OpenRead(opt.Destination.Index);
                    verify = Verifier.CompareMapped(s, sg, d, dg, layout, extents, expected, opt.BufferBytes, _progress, ct);
                    _log(VerifySummary(verify));
                }
                bool ok = verify == null || verify.Identical;
                if (!ok)
                    return Done(false, verify, "Clone finished but the verify found unexpected differences - see the Verify page. Do NOT rely on this copy.");

                string? growNote = null;
                if (plan != null)
                {
                    _log(Banner("EXTEND"));
                    grower.HideSource(opt.Source.Index);
                    dstLocks.Dispose();
                    dstLocks = null;
                    string? failed = grower.Extend(opt.Destination.Index, plan, ct);
                    growNote = failed == null
                        ? $" {plan.Grow.Label} on the copy now uses the extra {PhysicalDisk.FormatSize(plan.DeltaBytes)}."
                        : $" The extra {PhysicalDisk.FormatSize(plan.DeltaBytes)} is free right after {plan.Grow.Label}, but wasn't added to it " +
                          $"({failed}). Extend it on the Partitions page when the original isn't plugged in.";
                    if (failed != null) _log("! " + growNote.Trim());
                }
                return Done(true, verify,
                    "Clone complete." + growNote + " The copy is left OFFLINE on purpose: eject it, put it in the PC and boot from it. " +
                    "Don't connect it together with the original again.");
            }
            finally
            {
                // The copy goes offline while locked, BEFORE the original reappears: Windows never sees two identical disks online.
                if (dstLocks == null) dstLocks = TryRelock(opt.Destination.Index);
                TakeCopyOffline(opt);
                dstLocks?.Dispose();
                srcLocks?.Dispose();
                grower.ShowSource(opt.Source.Index);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Done(false, null, $"ERROR: {ex.Message}");
        }
    }

    private List<(long Start, long Length)> ApplyPlan(CloneOptions opt, DiskLayout layout, GrowPlan plan)
    {
        _log(Banner("PARTITION TABLE"));
        using var dst = NativeDisk.OpenWrite(opt.Destination.Index);
        var changedOnCopy = PartitionTable.ApplyGrowPlan(dst, NativeDisk.GetGeometry(dst), layout, plan, _log);
        // The verify reports in source positions: map each changed range on the copy back through the extents.
        var result = new List<(long, long)>();
        foreach (var (start, length) in changedOnCopy)
            foreach (var e in plan.Extents)
                if (start >= e.DstStart && start < e.DstStart + e.Length) { result.Add((start - e.Shift, length)); break; }
        return result;
    }

    private VolumeLocks? TryRelock(int disk)
    {
        try { return VolumeLocks.Lock(disk, _log); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _log($"! Could not lock the copy's volumes again ({ex.Message}).");
            return null;
        }
    }

    private void TakeCopyOffline(CloneOptions opt)
    {
        try
        {
            using var dst = NativeDisk.OpenWrite(opt.Destination.Index);
            NativeDisk.SetOffline(dst);
            _log($"Disk {opt.Destination.Index} (the copy) is now offline.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            _log($"! Could not take the copy offline ({ex.Message}). Eject Disk {opt.Destination.Index} now, " +
                 "before Windows sees it next to the original.");
        }
    }

    private void Guard(CloneOptions opt)
    {
        if (opt.Source.Index == opt.Destination.Index)
            throw new InvalidOperationException("Source and destination are the same disk.");
        if (opt.Destination.IsSystemDisk)
            throw new InvalidOperationException("The destination is the disk Windows is running from. Refusing to overwrite it.");
        if (opt.Source.IsSystemDisk)
            throw new InvalidOperationException(
                "The source is the disk Windows is running from. A bit copy of a running system is inconsistent.\n" +
                "Connect the 512 GB SSD as a second disk (USB enclosure/dock) and clone it from here.");
    }

    private (DiskGeometry Src, DiskGeometry Dst, DiskLayout Layout) Inspect(CloneOptions opt)
    {
        using var src = NativeDisk.OpenRead(opt.Source.Index);
        using var dst = NativeDisk.OpenRead(opt.Destination.Index);
        var sg = NativeDisk.GetGeometry(src);
        var dg = NativeDisk.GetGeometry(dst);
        if (dg.BytesPerSector != sg.BytesPerSector)
            throw new InvalidOperationException($"Sector sizes differ ({sg.BytesPerSector} B vs {dg.BytesPerSector} B). A bit copy would not boot.");
        if (dg.LengthBytes < sg.LengthBytes)
            throw new InvalidOperationException($"Destination ({PhysicalDisk.FormatSize(dg.LengthBytes)}) is smaller than source ({PhysicalDisk.FormatSize(sg.LengthBytes)}).");
        return (sg, dg, PartitionTable.Read(src, sg));
    }

    private void LogPlan(CloneOptions opt, DiskGeometry sg, DiskGeometry dg, DiskLayout layout, GrowPlan? plan, string? noGrow)
    {
        _log(Banner(opt.DryRun ? "PLAN (DRY RUN)" : "PLAN"));
        _log($"Source      : {opt.Source.Caption}  [{sg.LengthBytes:N0} bytes, {sg.BytesPerSector} B sectors]");
        _log($"Destination : {opt.Destination.Caption}  [{dg.LengthBytes:N0} bytes]");
        _log($"Layout      : {layout.Style}, {layout.Partitions.Count} partition(s)");
        foreach (var p in layout.Partitions)
        {
            string where = plan == null ? "" : p == plan.Grow ? "   stays, gets the extra space"
                : plan.Moved.Contains(p) ? $"   -> moved to @ {p.StartByte + plan.DeltaBytes:N0}" : "   stays";
            _log($"   {p.Label,-40} {PhysicalDisk.FormatSize(p.SizeBytes),12}  @ {p.StartByte:N0}{where}");
        }
        long extra = dg.LengthBytes - sg.LengthBytes;
        if (plan != null)
        {
            _log($"Copy        : {PhysicalDisk.FormatSize(plan.BytesToCopy)}, block {PhysicalDisk.FormatSize(opt.BufferBytes)}" +
                 (plan.Moved.Count > 0 ? "; partitions after the main one are written at the end of the destination" : ""));
            _log($"Extra space : {PhysicalDisk.FormatSize(plan.DeltaBytes)} added to {plan.Grow.Label} at the end of the clone " +
                 "(the original is hidden from Windows for that moment)");
            if (!layout.IsGpt && plan.Moved.Count > 0)
                _log("Note        : on MBR disks Windows finds its recovery tools by position. If a recovery partition moves, " +
                     "'reagentc /info' on the copy may show it disabled; Windows itself boots normally.");
        }
        else
        {
            _log($"Copy        : all {PhysicalDisk.FormatSize(sg.LengthBytes)}, block {PhysicalDisk.FormatSize(opt.BufferBytes)}");
            if (extra > 0)
                _log($"Extra space : {PhysicalDisk.FormatSize(extra)} will be unallocated - " +
                     (noGrow != null ? $"can't give it to the main partition ({noGrow}); " : "") +
                     (!layout.IsGpt ? "MBR disk, extend into it later in Disk Management"
                      : opt.MoveBackupGpt ? "GPT fixed so Windows can extend into it"
                      : "GPT NOT fixed (tick the option to use the space)"));
        }
        _log($"Verify      : {(opt.VerifyAfter ? "yes, full bit-for-bit compare" + (plan?.Moved.Count > 0 ? " (moved partitions checked at their new place)" : "") : "no")}");
        _log("");
    }

    private void Copy(CloneOptions opt, DiskGeometry sg, IReadOnlyList<CopyExtent> extents, CancellationToken ct)
    {
        _log(Banner("COPY"));
        using var src = NativeDisk.OpenRead(opt.Source.Index);
        using var dst = NativeDisk.OpenWrite(opt.Destination.Index);
        int chunk = Math.Max(sg.BytesPerSector, opt.BufferBytes - opt.BufferBytes % sg.BytesPerSector);
        var front = new byte[chunk];
        var back = new byte[chunk];
        var throttle = new ProgressThrottle(_progress, "Copying");
        long total = extents.Sum(e => e.Length), done = 0;
        Task pendingWrite = Task.CompletedTask;

        foreach (var e in extents)
        {
            if (e.Shift != 0) _log($"Writing {PhysicalDisk.FormatSize(e.Length)} from byte {e.SrcStart:N0} to {e.DstStart:N0} on the destination.");
            long offset = e.SrcStart;
            while (offset < e.SrcEnd && !ct.IsCancellationRequested)
            {
                int len = (int)Math.Min(chunk, e.SrcEnd - offset);
                ReadSource(src, front, offset, len, opt.Source.Index);   // read next block while the previous one is written
                WaitWrite(pendingWrite, opt.Destination.Index);
                byte[] buf = front; long at = offset + e.Shift; int n = len;
                pendingWrite = Task.Run(() => NativeDisk.Write(dst, buf, at, n));
                (front, back) = (back, front);
                offset += len;
                done += len;
                throttle.Report(done, total);
            }
        }
        WaitWrite(pendingWrite, opt.Destination.Index);
        NativeDisk.Flush(dst);
        throttle.Report(done, total, force: true);
        _log($"Copied {done:N0} bytes in {throttle.Elapsed:hh\\:mm\\:ss}.");
    }

    private static void ReadSource(SafeFileHandle src, byte[] buffer, long offset, int len, int disk)
    {
        try { NativeDisk.ReadInto(src, buffer, offset, len); }
        catch (IOException ex) { throw new IOException($"Reading the source (Disk {disk}) at byte {offset:N0} failed: {ex.Message}", ex); }
    }

    private static void WaitWrite(Task write, int disk)
    {
        try { write.GetAwaiter().GetResult(); }
        catch (IOException ex) { throw new IOException($"Writing the destination (Disk {disk}) failed: {ex.Message}", ex); }
        catch (UnauthorizedAccessException ex) { throw new IOException($"Windows blocked writing to the destination (Disk {disk}): {ex.Message}", ex); }
    }

    private void FixPartitionTable(CloneOptions opt)
    {
        _log(Banner("PARTITION TABLE"));
        using var dst = NativeDisk.OpenWrite(opt.Destination.Index);
        PartitionTable.RelocateBackupGpt(dst, NativeDisk.GetGeometry(dst), _log);
    }

    internal static string VerifySummary(VerifyResult v) =>
        v.Cancelled ? "Verify cancelled - the result is incomplete."
        : v.Identical
            ? $"VERIFIED: identical ({v.BytesCompared:N0} bytes compared" +
              (v.ExpectedRanges > 0 ? $"; {v.ExpectedRanges} expected partition-table difference(s))" : ")")
            : $"MISMATCH: {v.UnexpectedRanges} unexpected difference range(s), {PhysicalDisk.FormatSize(v.UnexpectedBytes)}.";

    private CloneResult Done(bool ok, VerifyResult? verify, string message)
    {
        _log("");
        _log(">>> " + message);
        return new CloneResult(ok, verify, message);
    }

    private static string Banner(string t) => $"──────────── {t} ────────────";
}
