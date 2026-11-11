namespace PodBattery;

/// <summary>One decoded AirPods status broadcast. Percentages are null when the AirPods report "unknown".</summary>
public sealed record PodAdvert(
    ushort Model,
    int? Left,
    int? Right,
    int? Case,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    byte Lid,
    byte Status = 0)
{
    /// <summary>Both earbuds are in the case and the lid is open: what "open your case next to the PC" looks like.</summary>
    public bool CaseOpen => (Status & 0x04) != 0 && (Lid & 0x08) == 0;
}

/// <summary>
/// Decodes Apple's "proximity pairing" Bluetooth LE advertisement (Continuity message type 0x07),
/// which AirPods broadcast continuously while out of the case or while the case lid is open.
/// </summary>
/// <remarks>
/// Layout of the manufacturer data that follows Apple's company id (0x004C), as used by OpenPods,
/// AirPodsDesktop and LibrePods:
/// <code>
///  [0]      0x07 message type            [1]  remaining length
///  [2]      0x01 (0x00 = pairing mode, which uses a different layout)
///  [3..4]   model id, little-endian (same as the Bluetooth PID, e.g. 0x2024)
///  [5]      status: 0x20 = sent by the left earbud, 0x02 = sender in ear, 0x08 = other earbud in ear,
///           0x04 = both earbuds in the case
///  [6]      batteries: low nibble = sending earbud, high nibble = the other; 0-10 = 0-100 %, 0xF = unknown
///  [7]      high nibble = charging (0x1 sender, 0x2 other earbud, 0x4 case), low nibble = case battery
///  [8]      lid: bits 0-2 open/close counter, 0x08 = closed, high nibble undocumented (differs in case vs in ears)
///  [9]      colour                       [10]  connection state (0 none, 4 idle, 5 audio, 6 call)
///  [11..26] encrypted (exact percentages; needs keys only Apple devices get)
/// </code>
/// Because only the unencrypted part is readable, levels come in 10 % steps.
/// </remarks>
public static class ProximityPairing
{
    public const ushort AppleCompanyId = 0x004C;
    private const byte ProximityPairingType = 0x07;

    public static PodAdvert? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9 || data[0] != ProximityPairingType || data[2] != 0x01)
            return null;

        var model = (ushort)(data[3] | data[4] << 8);
        var swapped = (data[5] & 0x20) == 0;   // sent by the right earbud, so its own level is the low nibble
        int high = data[6] >> 4, low = data[6] & 0x0F;
        var flags = data[7] >> 4;

        var left = ToPercent(swapped ? high : low);
        var right = ToPercent(swapped ? low : high);
        var @case = ToPercent(data[7] & 0x0F);

        return new PodAdvert(
            model,
            left,
            right,
            @case,
            LeftCharging: left is not null && (flags & (swapped ? 0x2 : 0x1)) != 0,
            RightCharging: right is not null && (flags & (swapped ? 0x1 : 0x2)) != 0,
            CaseCharging: @case is not null && (flags & 0x4) != 0,
            Lid: data[8],
            Status: data[5]);
    }

    /// <summary>Known AirPods model ids (also the product id Windows shows for the paired device).</summary>
    public static IReadOnlyDictionary<ushort, string> KnownModels { get; } = new Dictionary<ushort, string>
    {
        [0x2002] = "AirPods",
        [0x200F] = "AirPods (2nd gen)",
        [0x2013] = "AirPods (3rd gen)",
        [0x200E] = "AirPods Pro",
        [0x2014] = "AirPods Pro 2",
        [0x2024] = "AirPods Pro 2 (USB-C)",
        [0x2027] = "AirPods Pro 3",
        [0x2019] = "AirPods 4",
        [0x201B] = "AirPods 4 (ANC)",
        [0x200A] = "AirPods Max",
        [0x201F] = "AirPods Max (USB-C)",
    };

    public static string ModelName(ushort model) =>
        KnownModels.TryGetValue(model, out var name) ? name : $"AirPods (model {model:X4})";

    private static int? ToPercent(int nibble) => nibble <= 10 ? nibble * 10 : null;
}
