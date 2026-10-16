using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace clony;

/// <summary>Partition-table and boot-sector rewrites on the COPY after partitions were moved to the end.</summary>
internal static partial class PartitionTable
{
    private const int MbrChsStartOffset = 1, MbrChsEndOffset = 5;
    private const int BootHiddenSectorsOffset = 0x1C, NtfsTotalSectorsOffset = 0x28;
    private static readonly byte[] ChsLbaMarker = { 0xFE, 0xFF, 0xFF }; // "use LBA", standard for > 8 GB

    /// <summary>
    /// Points the copy's partition table at the moved partitions. Returns the copy's byte ranges that now
    /// differ from the source on purpose (reported as Expected by the verify).
    /// </summary>
    public static List<(long Start, long Length)> ApplyGrowPlan(SafeFileHandle dst, DiskGeometry geo, DiskLayout layout,
        GrowPlan plan, Action<string> log)
    {
        int bps = geo.BytesPerSector;
        var changed = new List<(long, long)>();
        if (plan.Moved.Count > 0 && plan.DeltaBytes % bps != 0)
            throw new InvalidOperationException("Internal error: the move distance isn't a whole number of sectors.");

        if (layout.IsGpt) changed.AddRange(ShiftGpt(dst, geo, plan, log));
        else changed.AddRange(ShiftMbr(dst, bps, plan, log));
        changed.AddRange(PatchBootSectors(dst, bps, plan, log));
        NativeDisk.Flush(dst);
        return changed;
    }

    private static IEnumerable<(long, long)> ShiftMbr(SafeFileHandle dst, int bps, GrowPlan plan, Action<string> log)
    {
        if (plan.Moved.Count == 0) yield break;
        byte[] mbr = NativeDisk.Read(dst, 0, bps);
        long deltaLba = plan.DeltaBytes / bps;
        long growEndLba = plan.Grow.EndByte / bps;
        for (int i = 0; i < 4; i++)
        {
            int at = MbrEntryTable + i * MbrEntrySize;
            if (mbr[at + MbrTypeOffset] == 0) continue;
            long start = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(at + MbrStartOffset));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(at + MbrSizeOffset));
            if (start < growEndLba) continue;
            long moved = start + deltaLba;
            if (moved + size > uint.MaxValue) throw new InvalidOperationException("MBR can't address past 2 TB.");
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(at + MbrStartOffset), (uint)moved);
            ChsLbaMarker.CopyTo(mbr, at + MbrChsStartOffset);
            ChsLbaMarker.CopyTo(mbr, at + MbrChsEndOffset);
            log($"MBR entry {i + 1}: sector {start:N0} -> {moved:N0}" + (IsExtendedType(mbr[at + MbrTypeOffset]) ? " (extended, logical partitions move with it)" : ""));
        }
        NativeDisk.Write(dst, mbr, 0);
        yield return (0, bps);
    }

    private static IEnumerable<(long, long)> ShiftGpt(SafeFileHandle dst, DiskGeometry geo, GrowPlan plan, Action<string> log)
    {
        int bps = geo.BytesPerSector;
        byte[] primary = NativeDisk.Read(dst, bps, bps);
        if (!IsGpt(primary)) throw new InvalidDataException("The copy has no GPT header; nothing was changed.");
        var h = ParseHeader(primary);
        long entrySectors = h.EntrySectors(bps);
        long deltaLba = plan.DeltaBytes / bps;
        long growEndLba = plan.Grow.EndByte / bps;

        if (plan.Moved.Count > 0)
        {
            byte[] entries = NativeDisk.Read(dst, h.EntriesLba * bps, checked((int)(entrySectors * bps)));
            for (int i = 0; i < h.EntryCount; i++)
            {
                var e = entries.AsSpan(i * h.EntrySize, h.EntrySize);
                if (e[..16].IndexOfAnyExcept((byte)0) < 0) continue;
                long first = BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
                long last = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
                if (first < growEndLba) continue;
                BinaryPrimitives.WriteInt64LittleEndian(e[32..], first + deltaLba);
                BinaryPrimitives.WriteInt64LittleEndian(e[40..], last + deltaLba);
                log($"GPT entry {i + 1}: sector {first:N0} -> {first + deltaLba:N0}");
            }
            BinaryPrimitives.WriteUInt32LittleEndian(primary.AsSpan(88), Crc32.Compute(entries.AsSpan(0, h.EntryCount * h.EntrySize)));
            SetHeaderCrc(primary, h.HeaderSize);
            NativeDisk.Write(dst, entries, h.EntriesLba * bps);
            NativeDisk.Write(dst, primary, bps);
        }
        // Backup table to the real last sector + widen the usable area. Don't wipe the old backup spot:
        // a moved partition may sit there now.
        RelocateBackupGpt(dst, geo, log, wipeStale: false);
        yield return (0, 2L * bps);
        yield return (h.EntriesLba * bps, entrySectors * bps);
    }

    /// <summary>
    /// NTFS/FAT boot sectors store their own start sector ("hidden sectors"). Update it for moved partitions
    /// (and the NTFS backup boot sector at the volume's end), only where it held the old absolute start.
    /// </summary>
    private static IEnumerable<(long, long)> PatchBootSectors(SafeFileHandle dst, int bps, GrowPlan plan, Action<string> log)
    {
        long deltaLba = plan.DeltaBytes / bps;
        foreach (var p in plan.Moved)
        {
            long newStartLba = (p.StartByte + plan.DeltaBytes) / bps;
            var starts = GrowPlanner.IsExtended(p) ? LogicalStarts(dst, bps, newStartLba) : new List<long> { newStartLba };
            foreach (long lba in starts)
                foreach (var range in PatchOne(dst, bps, lba, lba - deltaLba, log))
                    yield return range;
        }
    }

    private static IEnumerable<(long, long)> PatchOne(SafeFileHandle dst, int bps, long newLba, long oldLba, Action<string> log)
    {
        byte[] boot = NativeDisk.Read(dst, newLba * bps, bps);
        string fs = BootFileSystem(boot);
        if (fs.Length == 0 || BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(BootHiddenSectorsOffset)) != oldLba) yield break;
        if (newLba > uint.MaxValue) yield break;

        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(BootHiddenSectorsOffset), (uint)newLba);
        NativeDisk.Write(dst, boot, newLba * bps);
        yield return (newLba * bps, bps);
        log($"{fs} boot sector at sector {newLba:N0}: start updated.");

        if (fs != "NTFS") yield break;
        long total = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(NtfsTotalSectorsOffset));
        long backupLba = newLba + total; // NTFS keeps a backup boot sector in the sector after the volume
        byte[] backup = NativeDisk.Read(dst, backupLba * bps, bps);
        if (BootFileSystem(backup) != "NTFS" || BinaryPrimitives.ReadUInt32LittleEndian(backup.AsSpan(BootHiddenSectorsOffset)) != oldLba) yield break;
        BinaryPrimitives.WriteUInt32LittleEndian(backup.AsSpan(BootHiddenSectorsOffset), (uint)newLba);
        NativeDisk.Write(dst, backup, backupLba * bps);
        yield return (backupLba * bps, bps);
    }

    internal static string BootFileSystem(byte[] boot)
    {
        if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA) return "";
        if (Encoding.ASCII.GetString(boot, 3, 8) == "NTFS    ") return "NTFS";
        if (Encoding.ASCII.GetString(boot, 82, 5) == "FAT32") return "FAT32";
        if (Encoding.ASCII.GetString(boot, 54, 3) == "FAT") return "FAT";
        return "";
    }

    /// <summary>Absolute start sectors of the logical partitions in an MBR extended container (EBR chain).</summary>
    internal static List<long> LogicalStarts(SafeFileHandle disk, int bps, long extendedLba)
    {
        var result = new List<long>();
        long ebr = extendedLba;
        for (int guard = 0; guard < 128; guard++)
        {
            byte[] s = NativeDisk.Read(disk, ebr * bps, bps);
            if (s[510] != 0x55 || s[511] != 0xAA) break;
            int e0 = MbrEntryTable, e1 = MbrEntryTable + MbrEntrySize;
            if (s[e0 + MbrTypeOffset] != 0)
                result.Add(ebr + BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(e0 + MbrStartOffset)));
            if (!IsExtendedType(s[e1 + MbrTypeOffset])) break;
            ebr = extendedLba + BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(e1 + MbrStartOffset));
        }
        return result;
    }
}
