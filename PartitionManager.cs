using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace clony;

/// <summary>Supported resize range reported by Windows for one partition.</summary>
public sealed record ResizeRange(long Min, long Max);

/// <summary>
/// Partition listing and resize through Windows storage management (MSFT_Partition), the same API
/// Disk Management and PowerShell's Resize-Partition use. Listing is read-only; only Resize writes.
/// </summary>
public static class PartitionManager
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    private static readonly Dictionary<string, string> GptKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"] = "EFI System Partition",
        ["{e3c9e316-0b5c-4db8-817d-f92df00215ae}"] = "Microsoft Reserved",
        ["{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}"] = "Basic data",
        ["{de94bba4-06d1-4d40-a16a-bfd50179d6ac}"] = "Recovery Partition",
    };

    public static string KindFromTypes(string? gptType, int? mbrType)
    {
        if (!string.IsNullOrEmpty(gptType))
            return GptKinds.TryGetValue(gptType.Trim(), out var k) ? k : "Unknown type";
        return mbrType switch
        {
            null or 0 => "",
            0x07 or 0x0B or 0x0C or 0x0E or 0x06 => "Basic data",
            0x27 => "Recovery Partition",
            0xEF => "EFI System Partition",
            0x05 or 0x0F => "Extended",
            _ => $"Type 0x{mbrType:X2}",
        };
    }

    public static Dictionary<int, List<VolumePartition>> EnumerateAll()
    {
        var result = new Dictionary<int, List<VolumePartition>>();
        try
        {
            var scope = Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_Partition"));
            foreach (ManagementObject mo in searcher.Get())
            {
                var p = ToInfo(mo);
                if (!result.TryGetValue(p.DiskNumber, out var list)) result[p.DiskNumber] = list = new List<VolumePartition>();
                list.Add(p);
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (COMException) { }
        foreach (var list in result.Values) list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return result;
    }

    /// <summary>Smallest and largest size Windows can resize this partition to right now.</summary>
    public static ResizeRange GetSupportedSize(int disk, int partition)
    {
        using var mo = Find(disk, partition);
        var outParams = mo.InvokeMethod("GetSupportedSize", null, null)
                        ?? throw new InvalidOperationException("Windows returned no size range.");
        CheckReturn(outParams, "read the supported size range");
        return new ResizeRange(ToLong(outParams["SizeMin"]), ToLong(outParams["SizeMax"]));
    }

    /// <summary>Shrinks or extends the partition (and its NTFS/ReFS file system) to <paramref name="newSize"/> bytes.</summary>
    public static void Resize(int disk, int partition, long newSize)
    {
        using var mo = Find(disk, partition);
        var inParams = mo.GetMethodParameters("Resize");
        inParams["Size"] = (ulong)newSize;
        var outParams = mo.InvokeMethod("Resize", inParams, null)
                        ?? throw new InvalidOperationException("Windows returned no result for the resize.");
        CheckReturn(outParams, "resize the partition");
    }

    /// <summary>Partition number Windows currently uses for the partition starting at <paramref name="offset"/>, or null.</summary>
    public static int? FindPartitionNumberAt(int disk, long offset)
    {
        using var searcher = new ManagementObjectSearcher(Connect(), new ObjectQuery(
            $"SELECT PartitionNumber, Offset FROM MSFT_Partition WHERE DiskNumber = {disk}"));
        foreach (ManagementObject mo in searcher.Get())
            if (ToLong(mo["Offset"]) == offset) return Convert.ToInt32(mo["PartitionNumber"]);
        return null;
    }

    /// <summary>Asks Windows storage management to re-read every disk (like Update-HostStorageCache). Best effort.</summary>
    public static void RefreshCache()
    {
        try
        {
            using var setting = new ManagementClass(StorageNamespace + ":MSFT_StorageSetting");
            setting.InvokeMethod("UpdateHostStorageCache", null);
        }
        catch (ManagementException) { }
        catch (COMException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ManagementScope Connect()
    {
        var scope = new ManagementScope(StorageNamespace);
        scope.Connect();
        return scope;
    }

    private static ManagementObject Find(int disk, int partition)
    {
        using var searcher = new ManagementObjectSearcher(Connect(), new ObjectQuery(
            $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {disk} AND PartitionNumber = {partition}"));
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault()
               ?? throw new InvalidOperationException($"Partition {partition} on Disk {disk} no longer exists - press Refresh.");
    }

    private static VolumePartition ToInfo(ManagementObject mo)
    {
        string label = "", fs = "";
        long free = 0;
        try
        {
            foreach (ManagementObject vol in mo.GetRelated("MSFT_Volume"))
            {
                label = vol["FileSystemLabel"]?.ToString() ?? "";
                fs = vol["FileSystem"]?.ToString() ?? "";
                free = ToLong(vol["SizeRemaining"]);
                break;
            }
        }
        catch (ManagementException) { }

        int letter = mo["DriveLetter"] == null ? 0 : Convert.ToInt32(mo["DriveLetter"]);
        return new VolumePartition(
            DiskNumber: Convert.ToInt32(mo["DiskNumber"]),
            PartitionNumber: Convert.ToInt32(mo["PartitionNumber"]),
            Offset: ToLong(mo["Offset"]),
            Size: ToLong(mo["Size"]),
            DriveLetter: letter is >= 'A' and <= 'Z' ? (char)letter : null,
            Label: label,
            FileSystem: fs,
            FreeBytes: free,
            Kind: KindFromTypes(mo["GptType"]?.ToString(), mo["MbrType"] == null ? null : Convert.ToInt32(mo["MbrType"])),
            IsBoot: mo["IsBoot"] as bool? ?? false,
            IsSystem: mo["IsSystem"] as bool? ?? false,
            IsActive: mo["IsActive"] as bool? ?? false);
    }

    private static void CheckReturn(ManagementBaseObject outParams, string what)
    {
        uint code = outParams["ReturnValue"] == null ? 0 : Convert.ToUInt32(outParams["ReturnValue"]);
        if (code == 0) return;
        string detail = ExtendedMessage(outParams["ExtendedStatus"]);
        throw new InvalidOperationException($"Windows could not {what}: {Describe(code)} (code {code}).{detail}");
    }

    private static string ExtendedMessage(object? extended)
    {
        if (extended is not ManagementBaseObject status) return "";
        string? msg = status["Message"]?.ToString();
        return string.IsNullOrWhiteSpace(msg) ? "" : "\n" + msg.Trim();
    }

    // Documented MSFT_Partition return codes. Anything else relies on Windows' ExtendedStatus message.
    internal static string Describe(uint code) => code switch
    {
        1 => "not supported",
        2 => "unspecified error",
        3 => "timed out",
        4 => "failed",
        5 => "invalid parameter",
        4097 => "size not supported",
        40001 => "access denied - run clony as Administrator",
        40002 => "not enough resources to complete the operation",
        _ => "error",
    };

    private static long ToLong(object? v) => v == null ? 0 : Convert.ToInt64(v);
}
