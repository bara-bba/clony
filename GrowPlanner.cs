using System;
using System.Collections.Generic;
using System.Linq;

namespace clony;

/// <summary>One contiguous run copied from <see cref="SrcStart"/> on the source to <see cref="DstStart"/> on the copy.</summary>
public sealed record CopyExtent(long SrcStart, long DstStart, long Length)
{
    public long SrcEnd => SrcStart + Length;
    public long Shift => DstStart - SrcStart;
}

/// <summary>
/// "Give the extra space to Windows": the main partition stays where it is, every partition after it is
/// copied to the END of the bigger disk (same order, same spacing), so the extra space ends up directly
/// after the main partition and Windows can extend it.
/// </summary>
public sealed record GrowPlan(
    PartitionInfo Grow,                     // stays in place, gets the extra space
    IReadOnlyList<PartitionInfo> Moved,     // shifted by DeltaBytes (MBR extended container counts as one)
    long DeltaBytes,                        // how far the moved partitions shift = free space gained after Grow
    IReadOnlyList<CopyExtent> Extents)
{
    public long BytesToCopy => Extents.Sum(e => e.Length);
}

public static class GrowPlanner
{
    public const long Alignment = 1024 * 1024;          // keep moved partitions on 1 MB boundaries
    private const long MbrMaxSectors = uint.MaxValue;   // MBR stores 32-bit sector numbers

    /// <summary>
    /// Builds the plan, or returns the reason it can't be done (then clony falls back to a straight copy).
    /// <paramref name="growAt"/> picks the partition (by start byte) that gets the space; null = the largest.
    /// </summary>
    public static (GrowPlan? Plan, string? Reason) Build(DiskLayout layout, DiskGeometry src, DiskGeometry dst, long? growAt = null)
    {
        int bps = src.BytesPerSector;
        if (layout.Style is not ("MBR" or "GPT")) return (null, "the source has no partition table");
        if (dst.LengthBytes <= src.LengthBytes) return (null, "the destination isn't bigger than the source");

        var candidates = layout.Partitions.Where(p => !IsExtended(p));
        var grow = growAt is long at
            ? candidates.FirstOrDefault(p => p.StartByte == at)
            : candidates.OrderByDescending(p => p.SizeBytes).FirstOrDefault();
        if (grow == null)
            return (null, growAt != null
                ? "the chosen partition isn't a partition clony can grow (an MBR logical partition, or no longer on the disk)"
                : "the source has no data partition");
        if (layout.Partitions.Any(p => IsExtended(p) && p.StartByte <= grow.StartByte && p.EndByte >= grow.EndByte))
            return (null, "the main partition is a logical partition inside an MBR extended partition");

        // Copy end on the source: GPT keeps its backup table in the last sectors; that table is rebuilt, not copied.
        long srcEnd = layout.IsGpt && layout.BackupAreaStartLba > 0 ? layout.BackupAreaStartLba * bps : src.LengthBytes;

        long delta = (dst.LengthBytes - src.LengthBytes) / Alignment * Alignment;
        if (!layout.IsGpt)
        {
            long maxDelta = (MbrMaxSectors * bps - src.LengthBytes) / Alignment * Alignment; // MBR can't address past 2 TiB
            delta = Math.Min(delta, Math.Max(0, maxDelta));
        }
        if (delta < Alignment) return (null, "the extra space is less than 1 MB");

        var moved = layout.Partitions.Where(p => p.StartByte >= grow.EndByte).OrderBy(p => p.StartByte).ToList();
        if (layout.Partitions.Any(p => p != grow && p.StartByte < grow.EndByte && p.EndByte > grow.EndByte))
            return (null, "a partition overlaps the end of the main partition");

        var extents = new List<CopyExtent> { new(0, 0, grow.EndByte) };
        if (srcEnd > grow.EndByte) extents.Add(new CopyExtent(grow.EndByte, grow.EndByte + delta, srcEnd - grow.EndByte));
        return (new GrowPlan(grow, moved, delta, extents), null);
    }

    public static bool IsExtended(PartitionInfo p) => p.Name == PartitionTable.ExtendedName;
}
