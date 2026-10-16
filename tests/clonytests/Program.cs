using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using clony;
using Microsoft.Win32.SafeHandles;

// Image-file tests for "give the extra space to the main partition". No physical disk is touched.
static class Program
{
    const int Bps = 512;
    const long MB = 1024 * 1024;
    static int fails;
    static string Dir = "";

    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }

    static int Main(string[] args)
    {
        Dir = args[0];
        Mbr();
        Gpt();
        Refusals();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return fails;
    }

    // ---------------- helpers ----------------
    static SafeFileHandle Image(string name, long size)
    {
        string path = Path.Combine(Dir, name);
        if (File.Exists(path)) File.Delete(path);
        using (var fs = new FileStream(path, FileMode.CreateNew)) fs.SetLength(size);
        return File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
    }

    static void W(SafeFileHandle h, long off, byte[] data) => RandomAccess.Write(h, data, off);
    static byte[] R(SafeFileHandle h, long off, int n) { var b = new byte[n]; RandomAccess.Read(h, b, off); return b; }

    static void Fill(SafeFileHandle h, long start, long len, byte seed)
    {
        var buf = new byte[MB];
        for (long o = 0; o < len; o += MB)
        {
            int n = (int)Math.Min(MB, len - o);
            for (int i = 0; i < n; i++) buf[i] = (byte)(seed + (o / MB) * 7 + i % 251);
            RandomAccess.Write(h, buf.AsSpan(0, n), start + o);
        }
    }

    static byte[] NtfsBoot(long startLba, long sectors)
    {
        var b = new byte[Bps];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(b, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1C), (uint)startLba);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(0x28), sectors - 1);
        b[510] = 0x55; b[511] = 0xAA;
        return b;
    }

    static void NtfsVolume(SafeFileHandle h, long start, long len, byte seed)
    {
        Fill(h, start, len, seed);
        var boot = NtfsBoot(start / Bps, len / Bps);
        W(h, start, boot);
        W(h, start + len - Bps, boot); // backup boot sector = last sector
    }

    static void MbrEntry(byte[] s, int i, byte type, long startLba, long sectors, bool active = false)
    {
        int at = 446 + i * 16;
        s[at] = active ? (byte)0x80 : (byte)0;
        s[at + 4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(at + 8), (uint)startLba);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(at + 12), (uint)sectors);
        s[510] = 0x55; s[511] = 0xAA;
    }

    static void CopyExtents(SafeFileHandle src, SafeFileHandle dst, IEnumerable<CopyExtent> extents)
    {
        var buf = new byte[4 * MB];
        foreach (var e in extents)
            for (long o = e.SrcStart; o < e.SrcEnd; o += buf.Length)
            {
                int n = (int)Math.Min(buf.Length, e.SrcEnd - o);
                RandomAccess.Read(src, buf.AsSpan(0, n), o);
                RandomAccess.Write(dst, buf.AsSpan(0, n), o + e.Shift);
            }
    }

    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    // ---------------- MBR: same shape as the WD Blue (System Reserved, System, Recovery, Extended > Backup) ----
    static void Mbr()
    {
        long srcLen = 256 * MB, dstLen = 1024 * MB + 12345 * Bps; // odd size on purpose
        using var src = Image("mbr_src.img", srcLen);
        using var dst = Image("mbr_dst.img", dstLen);
        Fill(dst, 0, dstLen, 0xEE); // old junk on the destination

        long p1 = 1 * MB, p1l = 8 * MB, p2 = 9 * MB, p2l = 200 * MB, p3 = 209 * MB, p3l = 8 * MB;
        long ext = 217 * MB, extl = 30 * MB, log = ext + 1 * MB, logl = 28 * MB;
        var mbr = new byte[Bps];
        "BOOTCODE"u8.ToArray().CopyTo(mbr, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(440), 0xC0FFEE11); // disk signature
        MbrEntry(mbr, 0, 0x07, p1 / Bps, p1l / Bps, active: true);
        MbrEntry(mbr, 1, 0x07, p2 / Bps, p2l / Bps);
        MbrEntry(mbr, 2, 0x27, p3 / Bps, p3l / Bps);
        MbrEntry(mbr, 3, 0x0F, ext / Bps, extl / Bps);
        NtfsVolume(src, p1, p1l, 1); NtfsVolume(src, p2, p2l, 2); NtfsVolume(src, p3, p3l, 3);
        NtfsVolume(src, log, logl, 4);
        var ebr = new byte[Bps];
        MbrEntry(ebr, 0, 0x07, (log - ext) / Bps, logl / Bps); // relative to the EBR
        W(src, ext, ebr);
        W(src, 0, mbr);

        var sg = new DiskGeometry(srcLen, Bps);
        var dg = new DiskGeometry(dstLen, Bps);
        var layout = PartitionTable.Read(src, sg);
        var (plan, reason) = GrowPlanner.Build(layout, sg, dg);
        Check(plan != null, "mbr: plan built" + (reason != null ? $" ({reason})" : ""));
        if (plan == null) return;
        long delta = plan.DeltaBytes;
        Check(plan.Grow.StartByte == p2, "mbr: main partition = the largest (System)");
        Check(plan.Moved.Count == 2 && plan.Moved.Any(GrowPlanner.IsExtended), "mbr: recovery + extended container move");
        Check(delta % MB == 0 && delta == (dstLen - srcLen) / MB * MB, "mbr: shift is the extra space, 1 MB aligned");
        Check(plan.Extents.Sum(e => e.Length) == srcLen, "mbr: every source byte is copied exactly once");

        CopyExtents(src, dst, plan.Extents);
        var changed = PartitionTable.ApplyGrowPlan(dst, dg, layout, plan, _ => { });
        var after = PartitionTable.Read(dst, dg);

        var main = after.Partitions.Single(p => p.StartByte == p2);
        Check(main.SizeBytes == p2l, "mbr: main partition entry untouched (Windows extends it at the end)");
        var next = after.Partitions.Where(p => p.StartByte > p2).OrderBy(p => p.StartByte).First();
        Check(next.StartByte - main.EndByte == delta, "mbr: free space directly after the main partition = extra space");
        Check(after.Partitions.Any(p => p.StartByte == p3 + delta && p.SizeBytes == p3l), "mbr: recovery entry moved");
        Check(after.Partitions.Any(p => p.StartByte == ext + delta && p.SizeBytes == extl && GrowPlanner.IsExtended(p)), "mbr: extended entry moved");
        Check(after.Partitions.Max(p => p.EndByte) <= dstLen, "mbr: everything fits on the destination");
        Check(PartitionTable.LogicalStarts(dst, Bps, (ext + delta) / Bps).SequenceEqual(new[] { (log + delta) / Bps }), "mbr: logical partition found through the moved EBR");

        var d0 = R(dst, 0, Bps);
        Check(d0.AsSpan(0, 440).SequenceEqual(mbr.AsSpan(0, 440)) && U32(d0, 440) == 0xC0FFEE11, "mbr: boot code + disk signature kept (Windows boots by signature)");
        Check(d0[446] == 0x80, "mbr: System Reserved still active");
        Check(U32(R(dst, p3 + delta, Bps), 0x1C) == (p3 + delta) / Bps, "mbr: recovery boot sector start updated");
        Check(U32(R(dst, p3 + delta + p3l - Bps, Bps), 0x1C) == (p3 + delta) / Bps, "mbr: recovery backup boot sector updated");
        Check(U32(R(dst, log + delta, Bps), 0x1C) == (log + delta) / Bps, "mbr: logical (Backup) boot sector start updated");
        Check(U32(R(dst, p2, Bps), 0x1C) == p2 / Bps, "mbr: main partition boot sector unchanged");
        Check(R(dst, p2 + p2l - 3 * MB, (int)MB).SequenceEqual(R(src, p2 + p2l - 3 * MB, (int)MB)), "mbr: main partition data in place");
        Check(R(dst, p3 + delta + MB, (int)MB).SequenceEqual(R(src, p3 + MB, (int)MB)), "mbr: recovery data at new place");
        Check(R(dst, log + delta + 5 * MB, (int)MB).SequenceEqual(R(src, log + 5 * MB, (int)MB)), "mbr: backup data at new place");

        var expected = MapBack(changed, plan);
        var v = Verifier.CompareMapped(src, sg, dst, dg, layout, plan.Extents, expected, (int)(4 * MB), null, CancellationToken.None);
        Check(v.Identical && v.ExpectedRanges >= 4, $"mbr: verify identical, {v.ExpectedRanges} expected change(s) (table + boot sectors)");
        Check(v.BytesCompared == srcLen, "mbr: verify compared every source byte");

        W(dst, log + delta + 10 * MB, new byte[Bps]); // damage the moved Backup partition
        var bad = Verifier.CompareMapped(src, sg, dst, dg, layout, plan.Extents, expected, (int)(4 * MB), null, CancellationToken.None);
        Check(!bad.Identical && bad.Differences.Any(d => !d.Expected && d.StartByte == log + 10 * MB), "mbr: verify catches damage in a moved partition, at its source position");
    }

    static List<(long, long)> MapBack(IEnumerable<(long Start, long Length)> changed, GrowPlan plan)
    {
        var r = new List<(long, long)>();
        foreach (var (s, l) in changed)
            foreach (var e in plan.Extents)
                if (s >= e.DstStart && s < e.DstStart + e.Length) { r.Add((s - e.Shift, l)); break; }
        return r;
    }

    // ---------------- GPT: EFI, MSR, Windows, Recovery ----------------
    static void Gpt()
    {
        long srcLen = 96 * MB, dstLen = 400 * MB + 7 * Bps;
        using var src = Image("gpt_src.img", srcLen);
        using var dst = Image("gpt_dst.img", dstLen);
        Fill(dst, 0, dstLen, 0xAB);
        long last = srcLen / Bps - 1, entriesLba = 2, entrySectors = 32, entryCount = 128, entrySize = 128;

        var parts = new (long Start, long End, string Name)[]
        {
            (1 * MB, 5 * MB, "EFI"), (5 * MB, 6 * MB, "MSR"), (6 * MB, 80 * MB, "Windows"), (80 * MB, 94 * MB, "Recovery"),
        };
        var entries = new byte[entryCount * entrySize];
        for (int i = 0; i < parts.Length; i++)
        {
            var e = entries.AsSpan(i * (int)entrySize, (int)entrySize);
            Guid.NewGuid().ToByteArray().CopyTo(e);               // type (just non-zero)
            Guid.NewGuid().ToByteArray().CopyTo(e[16..]);
            BinaryPrimitives.WriteInt64LittleEndian(e[32..], parts[i].Start / Bps);
            BinaryPrimitives.WriteInt64LittleEndian(e[40..], parts[i].End / Bps - 1);
            Encoding.Unicode.GetBytes(parts[i].Name).CopyTo(e[56..]);
            if (parts[i].Name is "Windows" or "Recovery") NtfsVolume(src, parts[i].Start, parts[i].End - parts[i].Start, (byte)(10 + i));
            else Fill(src, parts[i].Start, parts[i].End - parts[i].Start, (byte)(10 + i));
        }
        byte[] Header(long current, long alternate, long entriesAt)
        {
            var h = new byte[Bps];
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(h, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), current);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), alternate);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 34);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), last - 33);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesAt);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(80), (int)entryCount);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(84), (int)entrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), Crc32.Compute(entries));
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32.Compute(h.AsSpan(0, 92)));
            return h;
        }
        var pmbr = new byte[Bps];
        MbrEntry(pmbr, 0, 0xEE, 1, last);
        W(src, 0, pmbr);
        W(src, Bps, Header(1, last, entriesLba));
        W(src, entriesLba * Bps, entries);
        W(src, (last - entrySectors) * Bps, entries);
        W(src, last * Bps, Header(last, 1, last - entrySectors));

        var sg = new DiskGeometry(srcLen, Bps);
        var dg = new DiskGeometry(dstLen, Bps);
        var layout = PartitionTable.Read(src, sg);
        var (plan, reason) = GrowPlanner.Build(layout, sg, dg);
        Check(plan != null, "gpt: plan built" + (reason != null ? $" ({reason})" : ""));
        if (plan == null) return;
        long delta = plan.DeltaBytes;
        Check(plan.Grow.Name == "Windows" && plan.Moved.Count == 1 && plan.Moved[0].Name == "Recovery", "gpt: Windows grows, Recovery moves");
        Check(plan.Extents.Last().SrcEnd == layout.BackupAreaStartLba * Bps, "gpt: old backup table not copied (rebuilt instead)");

        CopyExtents(src, dst, plan.Extents);
        var changed = PartitionTable.ApplyGrowPlan(dst, dg, layout, plan, _ => { });
        var after = PartitionTable.Read(dst, dg);
        var rec = after.Partitions.Single(p => p.Name == "Recovery");
        var win = after.Partitions.Single(p => p.Name == "Windows");
        Check(rec.StartByte == 80 * MB + delta && rec.SizeBytes == 14 * MB, "gpt: recovery entry moved");
        Check(win.StartByte == 6 * MB && win.SizeBytes == 74 * MB && rec.StartByte - win.EndByte == delta, "gpt: free space directly after Windows");

        long dLast = dstLen / Bps - 1;
        var ph = R(dst, Bps, Bps);
        var bh = R(dst, dLast * Bps, Bps);
        bool HeaderOk(byte[] h)
        {
            var c = (byte[])h.Clone();
            uint crc = U32(c, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(16), 0);
            return Crc32.Compute(c.AsSpan(0, 92)) == crc;
        }
        var pe = R(dst, BinaryPrimitives.ReadInt64LittleEndian(ph.AsSpan(72)) * Bps, (int)(entryCount * entrySize));
        var be = R(dst, BinaryPrimitives.ReadInt64LittleEndian(bh.AsSpan(72)) * Bps, (int)(entryCount * entrySize));
        Check(HeaderOk(ph) && HeaderOk(bh), "gpt: both header CRCs valid");
        Check(U32(ph, 88) == Crc32.Compute(pe) && pe.SequenceEqual(be), "gpt: entry CRC valid, backup entries = primary");
        Check(BinaryPrimitives.ReadInt64LittleEndian(ph.AsSpan(32)) == dLast && BinaryPrimitives.ReadInt64LittleEndian(bh.AsSpan(24)) == dLast, "gpt: backup header at the real last sector");
        Check(rec.EndByte / Bps - 1 <= BinaryPrimitives.ReadInt64LittleEndian(ph.AsSpan(48)), "gpt: moved recovery inside the usable area");
        Check(U32(R(dst, rec.StartByte, Bps), 0x1C) == rec.StartByte / Bps, "gpt: recovery boot sector start updated");
        Check(R(dst, rec.StartByte + 2 * MB, (int)MB).SequenceEqual(R(src, 82 * MB, (int)MB)), "gpt: recovery data at new place");

        var v = Verifier.CompareMapped(src, sg, dst, dg, layout, plan.Extents, MapBack(changed, plan), (int)(4 * MB), null, CancellationToken.None);
        Check(v.Identical, $"gpt: verify identical ({v.ExpectedRanges} expected change(s))");
    }

    static void Refusals()
    {
        var sg = new DiskGeometry(256 * MB, Bps);
        var layout = new DiskLayout("MBR", new[] { new PartitionInfo(1, "", MB, 200 * MB) }, -1);
        Check(GrowPlanner.Build(layout, sg, new DiskGeometry(256 * MB, Bps)).Plan == null, "refuse: destination not bigger");
        var inExt = new DiskLayout("MBR", new[]
        {
            new PartitionInfo(1, "", MB, 10 * MB),
            new PartitionInfo(2, PartitionTable.ExtendedName, 10 * MB, 250 * MB),
            new PartitionInfo(5, "", 11 * MB, 249 * MB),
        }, -1);
        var r = GrowPlanner.Build(inExt, sg, new DiskGeometry(1024 * MB, Bps));
        Check(r.Plan == null && r.Reason!.Contains("logical"), "refuse: main partition inside an extended container");
        var noMove = GrowPlanner.Build(layout, sg, new DiskGeometry(1024 * MB, Bps)).Plan;
        Check(noMove != null && noMove.Moved.Count == 0 && noMove.Extents.Count == 2, "no partitions after main: copy + plain extend");
    }
}
