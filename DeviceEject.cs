using System.Runtime.InteropServices;
using System.Text;

namespace clony;

/// <summary>"Safely remove hardware" for one disk: walks up from the disk to the removable device and ejects it.</summary>
internal static class DeviceEject
{
    private const int CR_SUCCESS = 0;
    private const uint CM_DRP_CAPABILITIES = 0x10;
    private const uint CM_DEVCAP_REMOVABLE = 0x04;
    private const int MaxLevels = 4;
    private const int VetoNameLength = 260;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint regType,
        out uint buffer, ref uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType, StringBuilder vetoName,
        int nameLength, uint flags);

    public static (bool Ok, string Message) Eject(string pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId))
            return (false, "Windows did not report a device ID for this disk.");
        if (CM_Locate_DevNodeW(out uint node, pnpDeviceId, 0) != CR_SUCCESS)
            return (false, "Device not found - it may already be removed. Press Refresh.");

        for (int level = 0; level < MaxLevels; level++)
        {
            if (IsRemovable(node)) return RequestEject(node);
            if (CM_Get_Parent(out uint parent, node, 0) != CR_SUCCESS) break;
            node = parent;
        }
        return (false, "Windows does not report this disk as removable, so it cannot be ejected here.");
    }

    private static bool IsRemovable(uint node)
    {
        uint length = sizeof(uint);
        return CM_Get_DevNode_Registry_PropertyW(node, CM_DRP_CAPABILITIES, out _, out uint caps, ref length, 0) == CR_SUCCESS
               && (caps & CM_DEVCAP_REMOVABLE) != 0;
    }

    private static (bool, string) RequestEject(uint node)
    {
        var vetoName = new StringBuilder(VetoNameLength);
        int cr = CM_Request_Device_EjectW(node, out int vetoType, vetoName, vetoName.Capacity, 0);
        if (cr == CR_SUCCESS && vetoType == 0) return (true, "Safe to remove.");
        string who = vetoName.Length > 0 ? $" ({vetoName})" : "";
        return (false, vetoType != 0 ? VetoText(vetoType) + who : $"Eject request failed (CONFIGRET {cr}).");
    }

    private static string VetoText(int vetoType) => vetoType switch
    {
        3 => "A program is about to close a file on it - try again in a moment.",
        4 => "A program is using it. Close Explorer windows / apps using the disk and retry.",
        5 => "A Windows service is using it.",
        6 => "Files on the disk are still open. Close them and retry.",
        7 => "Another device depends on it.",
        8 => "Its driver refused the request.",
        10 => "Not enough power to process the request.",
        13 => "Insufficient rights.",
        _ => $"Windows refused the eject (veto type {vetoType}).",
    };
}
