using System;
using System.Collections.Generic;
using System.Linq;

namespace clony;

/// <summary>One partition as Windows storage management reports it. Snapshot only.</summary>
public sealed record VolumePartition(
    int DiskNumber,
    int PartitionNumber,
    long Offset,
    long Size,
    char? DriveLetter,
    string Label,
    string FileSystem,
    long FreeBytes,
    string Kind,
    bool IsBoot,
    bool IsSystem,
    bool IsActive)
{
    public long End => Offset + Size;

    /// <summary>Name in the style of Disk Management: "C: Windows", "EFI System Partition", "Partition 3".</summary>
    public string Name
    {
        get
        {
            string letter = DriveLetter is { } l ? $"{l}:" : "";
            string name = Label.Length > 0 ? Label : Kind is "Basic data" or "" ? $"Partition {PartitionNumber}" : Kind;
            return (letter + " " + name).Trim();
        }
    }

    public string SizeLine => FileSystem.Length > 0
        ? $"{PhysicalDisk.FormatSize(Size)} {FileSystem}"
        : PhysicalDisk.FormatSize(Size);

    /// <summary>Like diskmgmt's status line: "Healthy (Boot, Page File, Basic Data Partition)".</summary>
    public string StatusLine
    {
        get
        {
            var flags = new List<string>();
            if (IsBoot) flags.Add("Boot");
            if (IsSystem) flags.Add("System");
            if (IsActive) flags.Add("Active");
            if (Kind.Length > 0) flags.Add(Kind);
            return flags.Count > 0 ? string.Join(", ", flags) : "Healthy";
        }
    }

    public string UsageLine => FileSystem.Length > 0 && Size > 0
        ? $"{PhysicalDisk.FormatSize(FreeBytes)} free of {PhysicalDisk.FormatSize(Size)}"
        : "No file system Windows can read";
}

/// <summary>A slice of the disk bar: a partition, or unallocated space between/after partitions.</summary>
public sealed record DiskSegment(long Offset, long Size, VolumePartition? Partition)
{
    public bool IsFree => Partition == null;
}

public static class PartitionMap
{
    /// <summary>
    /// Gaps up to this size are alignment padding (partitions start on 1 MB boundaries, GPT keeps ~1 MB
    /// at each end), not usable space, so they aren't drawn as unallocated.
    /// </summary>
    public const long MinFreeBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Partitions in disk order, with unallocated gaps larger than <see cref="MinFreeBytes"/> between and after them.
    /// An MBR "Extended" container is skipped: the logical partitions inside it are shown instead.
    /// </summary>
    public static IReadOnlyList<DiskSegment> Build(long diskSize, IEnumerable<VolumePartition> partitions)
    {
        if (diskSize < 0) throw new ArgumentOutOfRangeException(nameof(diskSize));
        var result = new List<DiskSegment>();
        long cursor = 0;
        foreach (var p in partitions.Where(p => p.Size > 0 && p.Kind != "Extended").OrderBy(p => p.Offset))
        {
            if (p.Offset < cursor) continue; // overlapping entry: never draw two slices over one area
            if (p.Offset - cursor > MinFreeBytes) result.Add(new DiskSegment(cursor, p.Offset - cursor, null));
            result.Add(new DiskSegment(p.Offset, p.Size, p));
            cursor = p.End;
        }
        if (diskSize - cursor > MinFreeBytes) result.Add(new DiskSegment(cursor, diskSize - cursor, null));
        return result;
    }

    /// <summary>
    /// Bar widths: proportional to size, but no slice narrower than <paramref name="minShare"/> of the bar,
    /// so a 16 MB MSR partition next to a 900 GB one is still clickable (Disk Management does the same).
    /// </summary>
    public static IReadOnlyList<double> Weights(IReadOnlyList<DiskSegment> segments, double minShare = 0.07)
    {
        long total = segments.Sum(s => s.Size);
        if (total <= 0) return segments.Select(_ => 1.0).ToList();
        return segments.Select(s => Math.Max((double)s.Size / total, minShare)).ToList();
    }

    /// <summary>Validates a requested new size against Windows' supported range. Returns null when OK.</summary>
    public static string? ValidateNewSize(long current, long min, long max, long requested)
    {
        if (min <= 0 || max <= 0 || min > max) return "Windows can't resize this partition.";
        if (requested == current) return "That is already the current size.";
        if (requested < min) return $"Can't shrink below {PhysicalDisk.FormatSize(min)}: Windows has files it can't move past that point.";
        if (requested > max) return $"Can't extend past {PhysicalDisk.FormatSize(max)}: there's no more free space directly after this partition.";
        return null;
    }
}
