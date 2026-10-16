using System;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace clony;

internal static partial class PartitionTable
{
    /// <summary>
    /// After a raw copy onto a bigger disk the GPT still says "the disk ends where the source ended".
    /// Move the backup table to the real last sector, widen the usable area and fix all CRCs, so
    /// Windows sees a valid GPT and the extra space shows up as unallocated (ready to extend).
    /// </summary>
    public static void RelocateBackupGpt(SafeFileHandle dst, DiskGeometry geo, Action<string> log)
    {
        int bps = geo.BytesPerSector;
        long newLast = geo.Sectors - 1;
        byte[] primary = NativeDisk.Read(dst, bps, bps);
        if (!IsGpt(primary)) { log("Source is not GPT - no partition table to move."); return; }

        var h = ParseHeader(primary);
        long entrySectors = h.EntrySectors(bps);
        if (h.AlternateLba == newLast) { log("Backup partition table is already at the end of the disk."); return; }
        if (h.AlternateLba > newLast || h.AlternateLba <= h.LastUsableLba)
            throw new InvalidDataException("Unexpected GPT layout on the copy; the partition table was left untouched.");

        byte[] entries = NativeDisk.Read(dst, h.EntriesLba * bps, checked((int)(entrySectors * bps)));

        BinaryPrimitives.WriteInt64LittleEndian(primary.AsSpan(32), newLast);
        BinaryPrimitives.WriteInt64LittleEndian(primary.AsSpan(48), newLast - entrySectors - 1);
        SetHeaderCrc(primary, h.HeaderSize);

        byte[] backup = (byte[])primary.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(backup.AsSpan(24), newLast);
        BinaryPrimitives.WriteInt64LittleEndian(backup.AsSpan(32), 1);
        BinaryPrimitives.WriteInt64LittleEndian(backup.AsSpan(72), newLast - entrySectors);
        SetHeaderCrc(backup, h.HeaderSize);

        // 1) wipe the stale backup copy (primary is still valid if anything fails here)
        long oldStart = h.AlternateLba - entrySectors;
        NativeDisk.Write(dst, new byte[(entrySectors + 1) * bps], oldStart * bps);
        // 2) new backup table + header at the real end of the disk
        NativeDisk.Write(dst, entries, (newLast - entrySectors) * bps);
        NativeDisk.Write(dst, backup, newLast * bps);
        // 3) primary header pointing at it
        NativeDisk.Write(dst, primary, bps);
        // 4) protective MBR covers the whole disk
        byte[] mbr = NativeDisk.Read(dst, 0, bps);
        if (mbr[MbrEntryTable + MbrTypeOffset] == ProtectiveMbrType)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(MbrEntryTable + MbrSizeOffset), (uint)Math.Min(newLast, uint.MaxValue));
            NativeDisk.Write(dst, mbr, 0);
        }
        NativeDisk.Flush(dst);
        log($"Backup partition table moved from sector {h.AlternateLba:N0} to {newLast:N0}.");
    }
}
