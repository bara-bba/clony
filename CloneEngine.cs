using System;
using System.ComponentModel;
using System.IO;
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
    bool DryRun);           // checks + plan only, writes nothing

public sealed record CloneResult(bool Success, VerifyResult? Verify, string Message);

/// <summary>
/// Bit-for-bit disk clone: every sector of the source is copied to the same offset on the destination,
/// so partition table, EFI, Windows, recovery and boot data are identical and the copy boots as-is.
/// The source is only ever opened read-only. Both disks are taken offline during the copy so Windows
/// can't write to either one mid-copy; the source is brought back online afterwards.
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
            LogPlan(opt, sg, dg, layout);
            if (opt.DryRun)
                return Done(true, null, "Dry run complete - nothing was written. Untick 'Dry run' to clone for real.");

            _log(Banner("PREPARE"));
            DiskPart.ClearReadOnly(opt.Destination.Index, _log);
            using var srcLocks = VolumeLocks.Lock(opt.Source.Index, _log);
            using var dstLocks = VolumeLocks.Lock(opt.Destination.Index, _log);
            try
            {
                Copy(opt, sg, ct);
                if (ct.IsCancellationRequested)
                    return Done(false, null, "Cancelled. The destination is INCOMPLETE and won't boot - run the clone again before using it.");

                if (opt.MoveBackupGpt && layout.IsGpt && dg.LengthBytes > sg.LengthBytes)
                    FixPartitionTable(opt);

                VerifyResult? verify = null;
                if (opt.VerifyAfter)
                {
                    _log(Banner("VERIFY"));
                    verify = Verifier.CompareDisks(opt.Source.Index, opt.Destination.Index, opt.BufferBytes, _progress, ct);
                    _log(VerifySummary(verify));
                }

                bool ok = verify == null || verify.Identical;
                return Done(ok, verify, ok
                    ? "Clone complete. The copy is left OFFLINE on purpose: eject it, put it in the PC and boot from it. " +
                      "Don't connect it together with the original again."
                    : "Clone finished but the verify found unexpected differences - see the Verify page. Do NOT rely on this copy.");
            }
            finally
            {
                // Still holding the volume locks: Windows never gets to see two identical disks online.
                TakeCopyOffline(opt);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Done(false, null, $"ERROR: {ex.Message}");
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

    private void LogPlan(CloneOptions opt, DiskGeometry sg, DiskGeometry dg, DiskLayout layout)
    {
        _log(Banner(opt.DryRun ? "PLAN (DRY RUN)" : "PLAN"));
        _log($"Source      : {opt.Source.Caption}  [{sg.LengthBytes:N0} bytes, {sg.BytesPerSector} B sectors]");
        _log($"Destination : {opt.Destination.Caption}  [{dg.LengthBytes:N0} bytes]");
        _log($"Layout      : {layout.Style}, {layout.Partitions.Count} partition(s)");
        foreach (var p in layout.Partitions)
            _log($"   {p.Label,-40} {PhysicalDisk.FormatSize(p.SizeBytes),12}  @ {p.StartByte:N0}");
        _log($"Copy        : all {PhysicalDisk.FormatSize(sg.LengthBytes)}, block {PhysicalDisk.FormatSize(opt.BufferBytes)}");
        long extra = dg.LengthBytes - sg.LengthBytes;
        if (extra > 0)
            _log($"Extra space : {PhysicalDisk.FormatSize(extra)} will be unallocated - " +
                 (!layout.IsGpt ? "MBR disk, extend into it later in Disk Management"
                  : opt.MoveBackupGpt ? "GPT fixed so Windows can extend into it"
                  : "GPT NOT fixed (tick the option to use the space)"));
        _log($"Verify      : {(opt.VerifyAfter ? "yes, full bit-for-bit compare" : "no")}");
        _log("");
    }

    private void Copy(CloneOptions opt, DiskGeometry sg, CancellationToken ct)
    {
        _log(Banner("COPY"));
        using var src = NativeDisk.OpenRead(opt.Source.Index);
        using var dst = NativeDisk.OpenWrite(opt.Destination.Index);
        int chunk = Math.Max(sg.BytesPerSector, opt.BufferBytes - opt.BufferBytes % sg.BytesPerSector);
        var front = new byte[chunk];
        var back = new byte[chunk];
        var throttle = new ProgressThrottle(_progress, "Copying");
        long total = sg.LengthBytes, offset = 0;
        Task pendingWrite = Task.CompletedTask;

        while (offset < total && !ct.IsCancellationRequested)
        {
            int len = (int)Math.Min(chunk, total - offset);
            ReadSource(src, front, offset, len, opt.Source.Index);   // read next block while the previous one is written
            WaitWrite(pendingWrite, opt.Destination.Index);
            byte[] buf = front; long at = offset; int n = len;
            pendingWrite = Task.Run(() => NativeDisk.Write(dst, buf, at, n));
            (front, back) = (back, front);
            offset += len;
            throttle.Report(offset, total);
        }
        WaitWrite(pendingWrite, opt.Destination.Index);
        NativeDisk.Flush(dst);
        throttle.Report(offset, total, force: true);
        _log($"Copied {offset:N0} bytes in {throttle.Elapsed:hh\\:mm\\:ss}.");
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
