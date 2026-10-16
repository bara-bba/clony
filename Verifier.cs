using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace clony;

/// <summary>A run of differing sectors. Expected = changed on purpose (partition-table relocation).</summary>
public sealed record DiffRange(long StartByte, long Length, string Location, bool Expected)
{
    public string StartText => $"{StartByte:N0}";
    public string SizeText => PhysicalDisk.FormatSize(Length);
    public string Kind => Expected ? "Expected" : "DIFFERENT";
}

public sealed record VerifyResult(
    long BytesCompared, long SourceLength, long DestinationLength, TimeSpan Duration, bool Cancelled,
    int ExpectedRanges, long ExpectedBytes, int UnexpectedRanges, long UnexpectedBytes,
    IReadOnlyList<DiffRange> Differences)
{
    public bool Identical => !Cancelled && UnexpectedRanges == 0;
    public int TotalRanges => ExpectedRanges + UnexpectedRanges;
}

/// <summary>Read-only comparison of two disks, sector by sector.</summary>
public static class Verifier
{
    /// <summary>
    /// Standalone verify: optionally locks both disks' volumes so Windows can't change them mid-check.
    /// Offline disks refuse reads, and bringing a fresh copy online next to its original makes Windows
    /// re-sign one of them, so an offline disk is refused instead of silently onlined.
    /// </summary>
    public static VerifyResult Compare(PhysicalDisk src, PhysicalDisk dst, int bufferBytes, bool lockVolumes,
        Action<string> log, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        foreach (var d in new[] { src, dst })
            if (DiskEnumerator.IsOffline(d.Index) == true)
                throw new InvalidOperationException(
                    $"Disk {d.Index} ({d.Model}) is offline, and offline disks can't be read. clony leaves a fresh copy offline " +
                    "on purpose - bringing it online next to the original makes Windows change the copy's disk ID. " +
                    "The verify that runs right after a clone covers this; to re-check later, bring it online in Disk Management first.");

        using var srcLocks = lockVolumes && !src.IsSystemDisk ? VolumeLocks.Lock(src.Index, log) : null;
        using var dstLocks = lockVolumes && !dst.IsSystemDisk ? VolumeLocks.Lock(dst.Index, log) : null;
        return CompareDisks(src.Index, dst.Index, bufferBytes, progress, ct);
    }

    public static VerifyResult CompareDisks(int srcIndex, int dstIndex, int bufferBytes,
        IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        using var src = NativeDisk.OpenRead(srcIndex);
        using var dst = NativeDisk.OpenRead(dstIndex);
        return CompareHandles(src, NativeDisk.GetGeometry(src), dst, NativeDisk.GetGeometry(dst), bufferBytes, progress, ct);
    }

    internal static VerifyResult CompareHandles(SafeFileHandle src, DiskGeometry sg, SafeFileHandle dst, DiskGeometry dg,
        int bufferBytes, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        if (dg.BytesPerSector != sg.BytesPerSector)
            throw new InvalidOperationException($"Sector sizes differ ({sg.BytesPerSector} B vs {dg.BytesPerSector} B) - these disks can't be bit-for-bit copies.");
        if (dg.LengthBytes < sg.LengthBytes)
            throw new InvalidOperationException("The second disk is smaller than the first, so it can't hold a full copy. Did you swap them?");
        var layout = PartitionTable.Read(src, sg);
        return CompareMapped(src, sg, dst, dg, layout, new[] { new CopyExtent(0, 0, sg.LengthBytes) },
            PartitionTable.ExpectedCloneChanges(layout, sg, dg), bufferBytes, progress, ct);
    }

    /// <summary>
    /// Compares each source extent with where it was copied to on the destination. Differences are reported
    /// in SOURCE positions; <paramref name="expected"/> (also source positions) are changes made on purpose.
    /// </summary>
    internal static VerifyResult CompareMapped(SafeFileHandle src, DiskGeometry sg, SafeFileHandle dst, DiskGeometry dg,
        DiskLayout layout, IReadOnlyList<CopyExtent> extents, IReadOnlyList<(long Start, long Length)> expected,
        int bufferBytes, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        int bps = sg.BytesPerSector;
        var acc = new DiffAccumulator(layout, expected);
        int chunk = Math.Max(bps, bufferBytes - bufferBytes % bps);
        var a = new byte[chunk];
        var b = new byte[chunk];
        var throttle = new ProgressThrottle(progress, "Verifying");
        long total = 0, done = 0;
        foreach (var e in extents) total += e.Length;
        bool cancelled = false;

        foreach (var extent in extents)
        {
            long offset = extent.SrcStart;
            while (offset < extent.SrcEnd)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                int len = (int)Math.Min(chunk, extent.SrcEnd - offset);
                long at = offset, dstAt = offset + extent.Shift;
                var readDst = Task.Run(() => NativeDisk.ReadInto(dst, b, dstAt, len));
                NativeDisk.ReadInto(src, a, at, len);
                readDst.GetAwaiter().GetResult();

                var sa = a.AsSpan(0, len);
                var sb = b.AsSpan(0, len);
                if (!sa.SequenceEqual(sb))
                    for (int i = 0; i < len; i += bps)
                        if (!sa.Slice(i, bps).SequenceEqual(sb.Slice(i, bps))) acc.Mark(at + i, bps);

                offset += len;
                done += len;
                throttle.Report(done, total);
            }
            if (cancelled) break;
        }
        acc.Close();
        throttle.Report(done, total, force: true);
        return acc.ToResult(done, sg.LengthBytes, dg.LengthBytes, throttle.Elapsed, cancelled);
    }
}

/// <summary>Merges differing sectors into ranges, split by partition and by expected/unexpected.</summary>
internal sealed class DiffAccumulator
{
    private const int MaxStored = 10_000;
    private readonly DiskLayout _layout;
    private readonly IReadOnlyList<(long Start, long Length)> _expected;
    private readonly List<DiffRange> _stored = new();
    private long _start = -1, _end;
    private bool _openExpected;
    private int _openPartition;
    private int _expectedRanges, _unexpectedRanges;
    private long _expectedBytes, _unexpectedBytes;

    public DiffAccumulator(DiskLayout layout, IReadOnlyList<(long Start, long Length)> expected)
    {
        _layout = layout;
        _expected = expected;
    }

    public void Mark(long start, int length)
    {
        bool expected = IsExpected(start);
        int partition = _layout.PartitionIndexAt(start);
        if (_start >= 0 && _end == start && expected == _openExpected && partition == _openPartition)
        {
            _end += length;
            return;
        }
        Close();
        (_start, _end, _openExpected, _openPartition) = (start, start + length, expected, partition);
    }

    public void Close()
    {
        if (_start < 0) return;
        long len = _end - _start;
        if (_openExpected) { _expectedRanges++; _expectedBytes += len; }
        else { _unexpectedRanges++; _unexpectedBytes += len; }
        if (_stored.Count < MaxStored) _stored.Add(new DiffRange(_start, len, Describe(), _openExpected));
        _start = -1;
    }

    public VerifyResult ToResult(long compared, long srcLength, long dstLength, TimeSpan duration, bool cancelled) =>
        new(compared, srcLength, dstLength, duration, cancelled,
            _expectedRanges, _expectedBytes, _unexpectedRanges, _unexpectedBytes, _stored);

    private bool IsExpected(long offset)
    {
        foreach (var (s, l) in _expected)
            if (offset >= s && offset < s + l) return true;
        return false;
    }

    private string Describe() =>
        _openPartition >= 0 ? _layout.Partitions[_openPartition].Label
        : _openExpected ? "Partition table"
        : "Outside any partition";
}
