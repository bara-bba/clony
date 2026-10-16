using System;
using System.Diagnostics;

namespace clony;

public sealed record CopyProgress(string Phase, long Done, long Total, double BytesPerSecond, TimeSpan Elapsed)
{
    public double Percent => Total <= 0 ? 0 : 100.0 * Done / Total;

    public TimeSpan? Remaining => BytesPerSecond <= 0 ? null : TimeSpan.FromSeconds((Total - Done) / BytesPerSecond);

    public string Text =>
        $"{Phase}: {PhysicalDisk.FormatSize(Done)} / {PhysicalDisk.FormatSize(Total)} ({Percent:0.0}%)" +
        $" · {PhysicalDisk.FormatSize((long)BytesPerSecond)}/s" +
        (Remaining is { } r ? $" · {r:hh\\:mm\\:ss} left" : "");
}

/// <summary>Reports progress at most a few times per second so the UI thread isn't flooded.</summary>
internal sealed class ProgressThrottle
{
    private const int IntervalMs = 250;
    private readonly IProgress<CopyProgress>? _progress;
    private readonly string _phase;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastReportMs = -IntervalMs;

    public ProgressThrottle(IProgress<CopyProgress>? progress, string phase)
    {
        _progress = progress;
        _phase = phase;
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public void Report(long done, long total, bool force = false)
    {
        long now = _clock.ElapsedMilliseconds;
        if (_progress == null || (!force && now - _lastReportMs < IntervalMs)) return;
        _lastReportMs = now;
        double secs = Math.Max(_clock.Elapsed.TotalSeconds, 0.001);
        _progress.Report(new CopyProgress(_phase, done, total, done / secs, _clock.Elapsed));
    }
}
