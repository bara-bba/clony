using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace clony;

/// <summary>A physical disk snapshot. Read-only: nothing here changes a disk.</summary>
public sealed record PhysicalDisk(
    int Index,
    string Model,
    string Serial,
    long SizeBytes,
    string Bus,
    string PartitionStyle,
    string PnpDeviceId,
    bool IsSystemDisk,
    bool IsOffline,
    IReadOnlyList<string> VolumeLetters)
{
    private static readonly string[] EjectableBuses = { "USB", "SD", "MMC", "1394" };

    public string SizeDisplay => FormatSize(SizeBytes);

    /// <summary>Identifies the physical device, so a renumbered disk is never mistaken for another.</summary>
    public string Identity => $"{Model}|{Serial}|{SizeBytes}|{PnpDeviceId}";

    public string Title => $"Disk {Index} — {Model}";

    public string Details =>
        $"{SizeDisplay} · {Bus} · {PartitionStyle}" +
        (VolumeLetters.Count > 0 ? $" · {string.Join(" ", VolumeLetters)}" : "") +
        (string.IsNullOrWhiteSpace(Serial) ? "" : $" · S/N {Serial}") +
        (IsOffline ? " · OFFLINE" : "") +
        (IsSystemDisk ? " · WINDOWS IS RUNNING FROM THIS DISK" : "");

    public string Caption => $"Disk {Index} — {Model} ({SizeDisplay}, {Bus})" + (IsSystemDisk ? "  • SYSTEM" : "");

    public bool CanEject => !IsSystemDisk && PnpDeviceId.Length > 0 && EjectableBuses.Contains(Bus);

    public string EjectLabel => IsSystemDisk ? "System" : CanEject ? "Eject" : "Internal";

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }
}

public static class DiskEnumerator
{
    private sealed record StorageInfo(bool IsSystem, bool IsOffline, string Bus, string Style, long Size);

    public static List<PhysicalDisk> Enumerate()
    {
        var letters = BuildLetterMap();
        var storage = QueryStorage();
        string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var result = new List<PhysicalDisk>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Index, Model, SerialNumber, Size, InterfaceType, PNPDeviceID FROM Win32_DiskDrive");
        foreach (ManagementObject mo in searcher.Get())
        {
            if (mo["Index"] == null) continue;
            int index = Convert.ToInt32(mo["Index"]);
            var vols = letters.TryGetValue(index, out var l) ? l : new List<string>();
            storage.TryGetValue(index, out var st);
            long wmiSize = mo["Size"] == null ? 0 : Convert.ToInt64(mo["Size"]);

            result.Add(new PhysicalDisk(
                Index: index,
                Model: (mo["Model"]?.ToString() ?? "Unknown").Trim(),
                Serial: (mo["SerialNumber"]?.ToString() ?? "").Trim(),
                SizeBytes: st?.Size > 0 ? st.Size : wmiSize,
                Bus: st?.Bus ?? mo["InterfaceType"]?.ToString() ?? "?",
                PartitionStyle: st?.Style ?? "?",
                PnpDeviceId: mo["PNPDeviceID"]?.ToString() ?? "",
                IsSystemDisk: (st?.IsSystem ?? false) || vols.Any(v => string.Equals(v, systemDrive, StringComparison.OrdinalIgnoreCase)),
                IsOffline: st?.IsOffline ?? false,
                VolumeLetters: vols));
        }
        return result.OrderBy(d => d.Index).ToList();
    }

    /// <summary>Current offline state of a disk, or null when Windows storage management can't tell.</summary>
    public static bool? IsOffline(int index) =>
        QueryStorage().TryGetValue(index, out var st) ? st.IsOffline : null;

    private static Dictionary<int, StorageInfo> QueryStorage()
    {
        var map = new Dictionary<int, StorageInfo>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Number, IsSystem, IsBoot, IsOffline, BusType, PartitionStyle, Size FROM MSFT_Disk"));
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["Number"] == null) continue;
                map[Convert.ToInt32(mo["Number"])] = new StorageInfo(
                    IsSystem: (mo["IsSystem"] as bool? ?? false) || (mo["IsBoot"] as bool? ?? false),
                    IsOffline: mo["IsOffline"] as bool? ?? false,
                    Bus: BusName(mo["BusType"] == null ? 0 : Convert.ToInt32(mo["BusType"])),
                    Style: (mo["PartitionStyle"] == null ? 0 : Convert.ToInt32(mo["PartitionStyle"])) switch { 1 => "MBR", 2 => "GPT", _ => "RAW" },
                    Size: mo["Size"] == null ? 0 : Convert.ToInt64(mo["Size"]));
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return map;
    }

    private static string BusName(int busType) => busType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 17 => "NVMe", _ => "Other",
    };

    private static Dictionary<int, List<string>> BuildLetterMap()
    {
        var map = new Dictionary<int, List<string>>();
        using var drives = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
        foreach (ManagementObject drive in drives.Get())
        {
            if (drive["Index"] == null) continue;
            var list = new List<string>();
            foreach (ManagementObject partition in drive.GetRelated("Win32_DiskPartition"))
                foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                    if (logical["DeviceID"]?.ToString() is { Length: > 0 } letter) list.Add(letter);
            map[Convert.ToInt32(drive["Index"])] = list;
        }
        return map;
    }
}
