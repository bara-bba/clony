using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace clony;

/// <summary>Exact size and logical sector size of a disk, as reported by the driver.</summary>
public sealed record DiskGeometry(long LengthBytes, int BytesPerSector)
{
    public long Sectors => LengthBytes / BytesPerSector;
}

/// <summary>Raw access to \\.\PhysicalDriveN. Offsets and lengths must be sector multiples.</summary>
internal static class NativeDisk
{
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    private const uint IOCTL_DISK_SET_DISK_ATTRIBUTES = 0x0007C0F4;
    private const ulong DISK_ATTRIBUTE_OFFLINE = 1;
    private const int SetDiskAttributesSize = 40;
    private const int GeometryBytesPerSectorOffset = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        byte[]? outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    public static string PathOf(int diskIndex) => $@"\\.\PhysicalDrive{diskIndex}";

    public static SafeFileHandle OpenRead(int diskIndex) =>
        File.OpenHandle(PathOf(diskIndex), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public static SafeFileHandle OpenWrite(int diskIndex) =>
        File.OpenHandle(PathOf(diskIndex), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
            FileOptions.WriteThrough);

    public static DiskGeometry GetGeometry(SafeFileHandle device)
    {
        var length = new byte[8];
        if (!DeviceIoControl(device, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, length, (uint)length.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the disk size.");

        var geometry = new byte[256];
        if (!DeviceIoControl(device, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero, 0, geometry, (uint)geometry.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the disk sector size.");

        return new DiskGeometry(BitConverter.ToInt64(length, 0), BitConverter.ToInt32(geometry, GeometryBytesPerSectorOffset));
    }

    /// <summary>
    /// Persistently marks the disk offline through our own write handle, while its volumes are still
    /// locked — so Windows never sees two identical disks online and never re-signs the copy.
    /// </summary>
    public static void SetOffline(SafeFileHandle device)
    {
        // SET_DISK_ATTRIBUTES: Version, Persist, 3 reserved, Attributes, AttributesMask, 4x reserved DWORD
        var input = new byte[SetDiskAttributesSize];
        BitConverter.GetBytes(SetDiskAttributesSize).CopyTo(input, 0);
        input[4] = 1;                                               // persist across reboots
        BitConverter.GetBytes(DISK_ATTRIBUTE_OFFLINE).CopyTo(input, 8);
        BitConverter.GetBytes(DISK_ATTRIBUTE_OFFLINE).CopyTo(input, 16);
        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            if (!DeviceIoControl(device, IOCTL_DISK_SET_DISK_ATTRIBUTES, pinned.AddrOfPinnedObject(), (uint)input.Length,
                    null, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not take the copy offline.");
        }
        finally { pinned.Free(); }
    }

    public static int ReadInto(SafeFileHandle handle, byte[] buffer, long offset, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = RandomAccess.Read(handle, buffer.AsSpan(done, count - done), offset + done);
            if (n <= 0) throw new EndOfStreamException($"Unexpected end of disk at byte {offset + done:N0}.");
            done += n;
        }
        return count;
    }

    public static byte[] Read(SafeFileHandle handle, long offset, int count)
    {
        var buffer = new byte[count];
        ReadInto(handle, buffer, offset, count);
        return buffer;
    }

    public static void Write(SafeFileHandle handle, byte[] data, long offset, int count) =>
        RandomAccess.Write(handle, new ReadOnlySpan<byte>(data, 0, count), offset);

    public static void Write(SafeFileHandle handle, byte[] data, long offset) => Write(handle, data, offset, data.Length);

    public static void Flush(SafeFileHandle handle)
    {
        if (!FlushFileBuffers(handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not flush the disk write cache.");
    }
}
