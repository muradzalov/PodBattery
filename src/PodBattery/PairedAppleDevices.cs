using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PodBattery;

/// <summary>Finds the model ids of AirPods paired with this PC, so other models can be ignored.</summary>
internal static partial class PairedAppleDevices
{
    private const string Enumerator = "BTHENUM";
    private const int FilterEnumerator = 0x1;
    private const int FilterPresent = 0x100;
    private const int Success = 0x0;
    private const int BufferSmall = 0x1A;

    // Paired devices show up as e.g. BTHENUM\{0000110B-...}_VID&0001004C_PID&2024\...
    // where 004C is Apple's vendor id and the PID is the same model id the AirPods broadcast.
    // AirPods and Beats models are all 0x20xx, which skips other Apple devices such as a paired iPhone.
    [GeneratedRegex(@"_VID&0001004C_PID&(20[0-9A-F]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex AirPodsPid();

    public static HashSet<ushort> ModelIds()
    {
        var models = new HashSet<ushort>();
        foreach (var id in DeviceIds())
        {
            var match = AirPodsPid().Match(id);
            if (match.Success)
                models.Add(Convert.ToUInt16(match.Groups[1].Value, 16));
        }
        return models;
    }

    private static string[] DeviceIds()
    {
        // The list can grow between asking for its size and reading it, so retry a few times.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, Enumerator, FilterEnumerator | FilterPresent) != Success || length == 0)
                return [];

            var buffer = new char[length];
            var result = CM_Get_Device_ID_ListW(Enumerator, buffer, length, FilterEnumerator | FilterPresent);
            if (result == Success)
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (result != BufferSmall)
                return [];
        }
        return [];
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int length, string filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, int length, int flags);
}
