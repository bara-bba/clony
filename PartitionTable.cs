using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace clony;

public sealed record PartitionInfo(int Number, string Name, long StartByte, long EndByte)
{
    public long SizeBytes => EndByte - StartByte;
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"Partition {Number}" : $"Partition {Number} — {Name}";
}

/// <summary>Partition layout of a disk. BackupAreaStartLba is where the GPT backup table starts (-1 for MBR).</summary>
public sealed record DiskLayout(string Style, IReadOnlyList<PartitionInfo> Partitions, long BackupAreaStartLba)
{
    public bool IsGpt => Style == "GPT";

    public int PartitionIndexAt(long byteOffset)
    {
        for (int i = 0; i < Partitions.Count; i++)
            if (byteOffset >= Partitions[i].StartByte && byteOffset < Partitions[i].EndByte) return i;
        return -1;
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

internal static partial class PartitionTable
{
    private static readonly byte[] GptSignature = Encoding.ASCII.GetBytes("EFI PART");
    private const int MbrEntryTable = 446, MbrEntrySize = 16, MbrTypeOffset = 4, MbrStartOffset = 8, MbrSizeOffset = 12;
    private const byte ProtectiveMbrType = 0xEE;

    private sealed record GptHeader(int HeaderSize, long AlternateLba, long LastUsableLba, long EntriesLba, int EntryCount, int EntrySize)
    {
        public long EntrySectors(int bps) => ((long)EntryCount * EntrySize + bps - 1) / bps;
    }

    public static DiskLayout Read(SafeFileHandle disk, DiskGeometry geo)
    {
        int bps = geo.BytesPerSector;
        byte[] header = NativeDisk.Read(disk, bps, bps);
        if (IsGpt(header)) return ReadGpt(disk, header, bps);

        byte[] mbr = NativeDisk.Read(disk, 0, bps);
        return mbr[510] == 0x55 && mbr[511] == 0xAA ? ReadMbr(mbr, bps) : new DiskLayout("RAW", Array.Empty<PartitionInfo>(), -1);
    }

    private static DiskLayout ReadGpt(SafeFileHandle disk, byte[] headerBytes, int bps)
    {
        var h = ParseHeader(headerBytes);
        long entrySectors = h.EntrySectors(bps);
        byte[] entries = NativeDisk.Read(disk, h.EntriesLba * bps, checked((int)(entrySectors * bps)));
        var parts = new List<PartitionInfo>();

        for (int i = 0; i < h.EntryCount; i++)
        {
            var e = entries.AsSpan(i * h.EntrySize, h.EntrySize);
            if (e[..16].IndexOfAnyExcept((byte)0) < 0) continue; // unused entry
            long first = BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
            long last = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
            string name = Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0');
            parts.Add(new PartitionInfo(i + 1, name, first * bps, (last + 1) * bps));
        }

        parts.Sort((a, b) => a.StartByte.CompareTo(b.StartByte));
        return new DiskLayout("GPT", parts, h.AlternateLba - entrySectors);
    }

    private static DiskLayout ReadMbr(byte[] mbr, int bps)
    {
        var parts = new List<PartitionInfo>();
        for (int i = 0; i < 4; i++)
        {
            int at = MbrEntryTable + i * MbrEntrySize;
            byte type = mbr[at + MbrTypeOffset];
            if (type == 0) continue;
            long start = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(at + MbrStartOffset));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(at + MbrSizeOffset));
            parts.Add(new PartitionInfo(i + 1, IsExtendedType(type) ? ExtendedName : "", start * bps, (start + size) * bps));
        }
        parts.Sort((a, b) => a.StartByte.CompareTo(b.StartByte));
        return new DiskLayout("MBR", parts, -1);
    }

    /// <summary>Name given to an MBR extended container (it holds logical partitions, not data).</summary>
    public const string ExtendedName = "Extended";

    internal static bool IsExtendedType(byte type) => type is 0x05 or 0x0F or 0x85;

    /// <summary>
    /// Regions a clone onto a LARGER GPT disk deliberately changes: protective MBR + primary
    /// GPT header (LBA 0-1) and the old backup table at the end of the source area.
    /// </summary>
    public static IReadOnlyList<(long Start, long Length)> ExpectedCloneChanges(DiskLayout source, DiskGeometry src, DiskGeometry dst)
    {
        if (!source.IsGpt || dst.LengthBytes == src.LengthBytes || source.BackupAreaStartLba < 0)
            return Array.Empty<(long, long)>();

        long backupStart = source.BackupAreaStartLba * src.BytesPerSector;
        return new[] { (0L, 2L * src.BytesPerSector), (backupStart, src.LengthBytes - backupStart) };
    }

    private static bool IsGpt(byte[] sector) => sector.AsSpan(0, 8).SequenceEqual(GptSignature);

    private static GptHeader ParseHeader(byte[] b)
    {
        var h = new GptHeader(
            HeaderSize: BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12)),
            AlternateLba: BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(32)),
            LastUsableLba: BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(48)),
            EntriesLba: BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(72)),
            EntryCount: BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(80)),
            EntrySize: BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(84)));

        if (h.HeaderSize < 92 || h.HeaderSize > b.Length || h.EntryCount is < 1 or > 4096 ||
            h.EntrySize is < 128 or > 4096 || h.EntrySize % 8 != 0)
            throw new InvalidDataException("The GPT header is damaged or uses an unsupported layout.");
        return h;
    }

    private static void SetHeaderCrc(byte[] header, int headerSize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), Crc32.Compute(header.AsSpan(0, headerSize)));
    }
}
