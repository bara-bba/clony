using System;
using System.ComponentModel;
using System.IO;
using System.Threading;

namespace clony;

/// <summary>
/// Last step of a "give the extra space to Windows" clone: lets Windows extend the main partition and its
/// file system on the copy. That needs the copy online, and an identical disk online at the same time makes
/// Windows change one disk's ID - so the source is hidden first (temporary offline: nothing is written to
/// it, and it comes back by itself on replug or restart if clony were to stop half-way).
/// </summary>
internal sealed class CopyGrower
{
    private const int SettleMs = 3000, PollMs = 1500, PollAttempts = 10;
    private readonly Action<string> _log;

    public CopyGrower(Action<string> log) => _log = log;

    public bool SourceHidden { get; private set; }

    /// <summary>Hides the source from Windows for the duration of the extend. Writes nothing to it.</summary>
    public void HideSource(int sourceDisk)
    {
        using var h = NativeDisk.OpenWrite(sourceDisk); // attribute change needs a write handle; no data is written
        NativeDisk.SetOffline(h, persist: false);
        SourceHidden = true;
        _log($"Disk {sourceDisk} (the original) hidden from Windows for a moment - temporary, nothing written to it.");
    }

    public void ShowSource(int sourceDisk)
    {
        if (!SourceHidden) return;
        try
        {
            using var h = NativeDisk.OpenWrite(sourceDisk);
            NativeDisk.SetOnline(h, persist: false);
            SourceHidden = false;
            _log($"Disk {sourceDisk} (the original) is visible again.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            _log($"! Could not bring Disk {sourceDisk} back ({ex.Message}). Unplug and replug it - nothing on it was changed.");
        }
    }

    /// <summary>
    /// Copy's volumes must be UNLOCKED when this runs. Returns null when the main partition now fills the
    /// extra space, or the reason it didn't (the space is then still free right after it).
    /// </summary>
    public string? Extend(int copyDisk, GrowPlan plan, CancellationToken ct)
    {
        using (var h = NativeDisk.OpenWrite(copyDisk)) NativeDisk.UpdateProperties(h);
        Thread.Sleep(SettleMs);
        PartitionManager.RefreshCache();
        if (DiskEnumerator.IsOffline(copyDisk) == true)
            return "Windows kept the copy offline";

        long target = plan.Grow.SizeBytes + plan.DeltaBytes;
        for (int attempt = 1; attempt <= PollAttempts && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                if (PartitionManager.FindPartitionNumberAt(copyDisk, plan.Grow.StartByte) is int number)
                {
                    var range = PartitionManager.GetSupportedSize(copyDisk, number);
                    // Windows may still be catching up with the new table: wait until it sees the free space.
                    if (range.Max >= target - GrowPlanner.Alignment)
                    {
                        _log($"Extending partition {number} on the copy to {PhysicalDisk.FormatSize(range.Max)} (Windows extends the file system too)...");
                        PartitionManager.Resize(copyDisk, number, range.Max);
                        _log($"Partition {number} now uses the extra space.");
                        return null;
                    }
                }
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                if (attempt == PollAttempts) return ex.Message;
            }
            Thread.Sleep(PollMs);
            if (attempt % 3 == 0) PartitionManager.RefreshCache();
        }
        return ct.IsCancellationRequested ? "cancelled" : "Windows didn't see the free space after the main partition in time";
    }

    private static bool IsRetryable(Exception ex) =>
        ex is InvalidOperationException or System.Management.ManagementException or System.Runtime.InteropServices.COMException;
}
