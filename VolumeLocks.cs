using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace clony;

/// <summary>
/// Locks and dismounts every volume on a disk, so Windows and other programs can't read or write
/// through the file system while clony works on the raw disk. The disk itself stays ONLINE: an offline
/// disk refuses raw I/O ("Incorrect function"). Disposing releases the locks.
/// </summary>
internal sealed class VolumeLocks : IDisposable
{
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_DISMOUNT_VOLUME = 0x00090020;
    private const int ExtentSize = 24, ExtentsHeader = 8, MaxExtents = 16;
    private const int LockAttempts = 20, LockRetryMs = 250;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        byte[]? outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder name, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextVolumeW(IntPtr find, StringBuilder name, int length);

    [DllImport("kernel32.dll")]
    private static extern bool FindVolumeClose(IntPtr find);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string volume, char[] names, int length, out int needed);

    private readonly List<SafeFileHandle> _handles = new();

    private VolumeLocks() { }

    public int Count => _handles.Count;

    /// <summary>Locks all volumes on the disk. Throws, naming the drive letters, if one stays in use.</summary>
    public static VolumeLocks Lock(int diskIndex, Action<string> log)
    {
        var locks = new VolumeLocks();
        try
        {
            foreach (string volume in VolumesOnDisk(diskIndex))
                locks.LockOne(volume, diskIndex, log);
            log($"Disk {diskIndex}: {locks.Count} volume(s) locked and dismounted.");
            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    private void LockOne(string volume, int diskIndex, Action<string> log)
    {
        string device = volume.TrimEnd('\\');
        var handle = CreateFileW(device, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open volume {Describe(volume)} on Disk {diskIndex}");

        for (int attempt = 1; ; attempt++)
        {
            if (DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero)) break;
            if (attempt == LockAttempts)
            {
                handle.Dispose();
                throw new InvalidOperationException(
                    $"Volume {Describe(volume)} on Disk {diskIndex} is in use and can't be locked. " +
                    "Close Explorer windows and programs using it, then try again.");
            }
            Thread.Sleep(LockRetryMs);
        }
        if (!DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero))
            log($"! Volume {Describe(volume)} locked but not dismounted (error {Marshal.GetLastWin32Error()}).");
        _handles.Add(handle);
    }

    /// <summary>Volume GUID paths ("\\?\Volume{...}\") with at least one extent on the given disk.</summary>
    private static List<string> VolumesOnDisk(int diskIndex)
    {
        var result = new List<string>();
        var name = new StringBuilder(260);
        IntPtr find = FindFirstVolumeW(name, name.Capacity);
        if (find == new IntPtr(-1)) return result;
        try
        {
            do
            {
                string volume = name.ToString();
                if (DiskNumbersOf(volume).Contains(diskIndex)) result.Add(volume);
            } while (FindNextVolumeW(find, name, name.Capacity));
        }
        finally { FindVolumeClose(find); }
        return result;
    }

    private static IEnumerable<int> DiskNumbersOf(string volume)
    {
        // Open with no access: enough for the extents query, and it never disturbs the volume.
        using var h = CreateFileW(volume.TrimEnd('\\'), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return Array.Empty<int>();
        var buffer = new byte[ExtentsHeader + ExtentSize * MaxExtents];
        if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out _, IntPtr.Zero))
            return Array.Empty<int>(); // CD-ROMs, virtual drives, ...
        int count = Math.Min(BitConverter.ToInt32(buffer, 0), MaxExtents);
        return Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(buffer, ExtentsHeader + i * ExtentSize)).ToList();
    }

    private static string Describe(string volume)
    {
        var names = new char[1024];
        if (GetVolumePathNamesForVolumeNameW(volume, names, names.Length, out _))
        {
            string letters = string.Join(" ", new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries));
            if (letters.Length > 0) return letters;
        }
        return "(no drive letter)";
    }

    public void Dispose()
    {
        foreach (var h in _handles) h.Dispose();   // closing the handle releases the lock
        _handles.Clear();
    }
}
