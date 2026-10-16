using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace clony;

/// <summary>Worst result seen in a matrix cell. Order matters: a higher value always wins.</summary>
public enum CellState : byte { Unscanned, Fast, Slow, VerySlow, Unreadable }

/// <summary>
/// The disk split into a fixed number of cells for the matrix view. Written by the scan thread,
/// read by the UI for drawing; a torn read of one byte only delays a colour by one frame.
/// </summary>
public sealed class ScanMap
{
    public const int CellCount = 2048;

    public ScanMap(long length, DiskLayout layout)
    {
        Length = length;
        Layout = layout;
        CellBytes = Math.Max(1, (length + CellCount - 1) / CellCount);
    }

    public long Length { get; }
    public DiskLayout Layout { get; }
    public long CellBytes { get; }
    public CellState[] States { get; } = new CellState[CellCount];
    public int[] WorstMs { get; } = new int[CellCount];
    public int[] UnreadablePieces { get; } = new int[CellCount];
    public int CurrentCell { get; set; } = -1;

    public int CellAt(long offset) => (int)Math.Clamp(offset / CellBytes, 0, CellCount - 1);
    public long CellStart(int cell) => Math.Min(Length, cell * CellBytes);
    public long CellEnd(int cell) => Math.Min(Length, (cell + 1) * CellBytes);

    public void Mark(long offset, long length, CellState state, int ms)
    {
        int last = CellAt(offset + Math.Max(0, length - 1));
        for (int c = CellAt(offset); c <= last; c++)
        {
            if (state > States[c]) States[c] = state;
            if (ms > WorstMs[c]) WorstMs[c] = ms;
            if (state == CellState.Unreadable) UnreadablePieces[c]++;
        }
    }

    public string LocationAt(long offset)
    {
        int p = Layout.PartitionIndexAt(offset);
        return p >= 0 ? Layout.Partitions[p].Label : "Outside any partition";
    }
}

/// <summary>A run of unreadable or very slow sectors.</summary>
public sealed record ScanFinding(long StartByte, long Length, string Location, bool Unreadable, int WorstMs)
{
    public string Kind => Unreadable ? "UNREADABLE" : "Very slow";
    public string StartText => $"{StartByte:N0}";
    public string SizeText => PhysicalDisk.FormatSize(Length);
    public string Detail => Unreadable ? "read failed" : $"{WorstMs:N0} ms";
}

public sealed record ScanResult(
    long StartByte, long ReachedByte, long DiskLength, TimeSpan Duration, bool Cancelled, string? StoppedReason,
    int UnreadableRanges, long UnreadableBytes, int VerySlowRanges, IReadOnlyList<ScanFinding> Findings)
{
    public bool Complete => !Cancelled && StoppedReason == null;
    public bool Healthy => Complete && UnreadableRanges == 0;
}

/// <summary>
/// Read-only surface scan: reads every sector of one disk, times each read and pins down
/// unreadable areas. Never opens the disk for writing and never locks its volumes.
/// </summary>
public static class SectorScanner
{
    public const int ChunkBytes = 4 * 1024 * 1024;
    public const int PieceBytes = 512 * 1024;
    private const int RecoverTimeoutMs = 60_000;
    private const int RecoverPollMs = 2_000;

    public static ScanResult Scan(int diskIndex, long startByte, int slowMs, Action<ScanMap> onMap,
        Action<string> log, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        if (DiskEnumerator.IsOffline(diskIndex) == true)
            throw new InvalidOperationException(
                $"Disk {diskIndex} is offline, and offline disks can't be read. Bring it online in Disk Management first " +
                "- but not a fresh copy while its original is plugged in, or Windows changes the copy's disk ID.");

        var handle = NativeDisk.OpenRead(diskIndex);
        try
        {
            var geo = NativeDisk.GetGeometry(handle);
            if (geo.LengthBytes <= 0)
                throw new InvalidOperationException($"Disk {diskIndex} reports a size of 0 bytes - it's not responding. Unplug it and plug it back in.");

            var map = new ScanMap(geo.LengthBytes, ReadLayout(handle, geo));
            onMap(map);
            var run = new ScanRun(diskIndex, geo, map, slowMs, log, ct) { Handle = handle };
            var result = run.Execute(Math.Clamp(startByte, 0, geo.LengthBytes), progress);
            handle = run.Handle;
            return result;
        }
        finally { handle?.Dispose(); }
    }

    private static DiskLayout ReadLayout(SafeFileHandle handle, DiskGeometry geo)
    {
        try { return PartitionTable.Read(handle, geo); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return new DiskLayout("Unknown", Array.Empty<PartitionInfo>(), -1);
        }
    }

    /// <summary>One scan in progress. Holds the handle so it can be reopened after a USB reset.</summary>
    private sealed class ScanRun
    {
        private readonly int _disk;
        private readonly DiskGeometry _geo;
        private readonly ScanMap _map;
        private readonly int _slowMs, _verySlowMs;
        private readonly Action<string> _log;
        private readonly CancellationToken _ct;
        private readonly FindingList _findings = new();
        private readonly byte[] _buffer = new byte[ChunkBytes];

        public ScanRun(int disk, DiskGeometry geo, ScanMap map, int slowMs, Action<string> log, CancellationToken ct)
        {
            (_disk, _geo, _map, _log, _ct) = (disk, geo, map, log, ct);
            _slowMs = slowMs;
            _verySlowMs = Math.Max(1000, slowMs * 4);
        }

        public SafeFileHandle? Handle { get; set; }

        public ScanResult Execute(long start, IProgress<CopyProgress>? progress)
        {
            long total = _geo.LengthBytes;
            long offset = start - start % PieceBytes;
            var throttle = new ProgressThrottle(progress, "Scanning");
            var clock = Stopwatch.StartNew();
            string? stopped = null;
            bool cancelled = false;

            _log($"Scanning Disk {_disk}: {PhysicalDisk.FormatSize(total)} from byte {offset:N0}. Read only.");
            while (offset < total)
            {
                if (_ct.IsCancellationRequested) { cancelled = true; break; }
                int len = (int)Math.Min(ChunkBytes, total - offset);
                _map.CurrentCell = _map.CellAt(offset);
                try
                {
                    if (!TryRead(offset, len, out int ms)) ScanPieces(offset, len);
                    else Record(offset, len, Classify(ms, len), ms);
                }
                catch (DriveLostException ex)
                {
                    Record(ex.Offset, ex.Length, CellState.Unreadable, ex.Ms);
                    stopped = ex.Message;
                    offset = ex.Offset + ex.Length;
                    break;
                }
                offset += len;
                // Report bytes scanned in THIS run so speed and time-left stay honest when starting mid-disk.
                throttle.Report(offset - start, total - start);
            }
            _map.CurrentCell = -1;
            _findings.Close();
            throttle.Report(offset - start, total - start, force: true);
            return new ScanResult(start, Math.Min(offset, total), total, clock.Elapsed, cancelled, stopped,
                _findings.UnreadableRanges, _findings.UnreadableBytes, _findings.VerySlowRanges, _findings.Stored);
        }

        /// <summary>A failed 4 MB read is retried as 512 KB pieces so only the bad part is marked.</summary>
        private void ScanPieces(long offset, int len)
        {
            _log($"Read failed at byte {offset:N0} ({_map.LocationAt(offset)}). Re-reading in 512 KB pieces - " +
                 "each unreadable piece can take up to a minute while the drive retries.");
            for (int done = 0; done < len && !_ct.IsCancellationRequested; done += PieceBytes)
            {
                long at = offset + done;
                int piece = Math.Min(PieceBytes, len - done);
                if (TryRead(at, piece, out int ms))
                {
                    Record(at, piece, Classify(ms, piece), ms);
                    continue;
                }
                Record(at, piece, CellState.Unreadable, ms);
                _log($"  UNREADABLE: {PhysicalDisk.FormatSize(piece)} at byte {at:N0} (sector {at / _geo.BytesPerSector:N0}, 0x{at / _geo.BytesPerSector:X}), after {ms:N0} ms");
            }
        }

        private bool TryRead(long offset, int len, out int ms)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                NativeDisk.ReadInto(Handle!, _buffer, offset, len);
                ms = (int)sw.ElapsedMilliseconds;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                ms = (int)sw.ElapsedMilliseconds;
                Recover(offset, len, ms, ex);
                return false;
            }
        }

        /// <summary>
        /// A failed read often comes with a USB reset that invalidates the handle. Reopen the same disk
        /// and check it still has the same size before carrying on; give up after a minute.
        /// </summary>
        private void Recover(long offset, int len, int ms, Exception cause)
        {
            Handle?.Dispose();
            Handle = null;
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < RecoverTimeoutMs && !_ct.IsCancellationRequested)
            {
                try
                {
                    var h = NativeDisk.OpenRead(_disk);
                    if (NativeDisk.GetGeometry(h).LengthBytes == _geo.LengthBytes) { Handle = h; return; }
                    h.Dispose();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { }
                _ct.WaitHandle.WaitOne(RecoverPollMs);
            }
            long resumeGb = (offset + ChunkBytes) / (1024L * 1024 * 1024) + 1;
            throw new DriveLostException(
                $"Disk {_disk} stopped responding after a failed read at byte {offset:N0} ({_map.LocationAt(offset)}): {cause.Message} " +
                $"Unplug it, plug it back in, and scan again starting from {resumeGb} GB to continue past this area.",
                offset, len, ms);
        }

        private CellState Classify(int ms, int len)
        {
            // Thresholds are per 4 MB; scale them for the smaller pieces.
            double scale = (double)len / ChunkBytes;
            return ms >= _verySlowMs * scale ? CellState.VerySlow : ms >= _slowMs * scale ? CellState.Slow : CellState.Fast;
        }

        private void Record(long offset, int len, CellState state, int ms)
        {
            _map.Mark(offset, len, state, ms);
            if (state is CellState.Unreadable or CellState.VerySlow)
                _findings.Add(offset, len, state == CellState.Unreadable, ms, _map.LocationAt(offset));
        }
    }

    private sealed class DriveLostException : Exception
    {
        public DriveLostException(string message, long offset, int length, int ms) : base(message)
        {
            (Offset, Length, Ms) = (offset, length, ms);
        }

        public long Offset { get; }
        public int Length { get; }
        public int Ms { get; }
    }

    /// <summary>Merges touching findings of the same kind and partition into one range.</summary>
    private sealed class FindingList
    {
        private const int MaxStored = 10_000;
        private readonly List<ScanFinding> _stored = new();
        private ScanFinding? _open;

        public IReadOnlyList<ScanFinding> Stored => _stored;
        public int UnreadableRanges { get; private set; }
        public long UnreadableBytes { get; private set; }
        public int VerySlowRanges { get; private set; }

        public void Add(long start, long len, bool unreadable, int ms, string location)
        {
            if (_open is { } o && o.StartByte + o.Length == start && o.Unreadable == unreadable && o.Location == location)
            {
                _open = o with { Length = o.Length + len, WorstMs = Math.Max(o.WorstMs, ms) };
                return;
            }
            Close();
            _open = new ScanFinding(start, len, location, unreadable, ms);
        }

        public void Close()
        {
            if (_open is not { } o) return;
            if (o.Unreadable) { UnreadableRanges++; UnreadableBytes += o.Length; }
            else VerySlowRanges++;
            if (_stored.Count < MaxStored) _stored.Add(o);
            _open = null;
        }
    }
}
