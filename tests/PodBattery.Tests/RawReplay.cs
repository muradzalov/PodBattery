using System.Globalization;

namespace PodBattery.Tests;

/// <summary>Helpers for replaying the raw recordings (real addresses, full payloads) through the tracker.</summary>
internal static class RawReplay
{
    public const ushort ProUsbC = 0x2024;
    public const ulong User = 0xD00000000016;        // the owner's AirPods, worn (ads-inear.csv)
    public const ulong Lookalike = 0xD00000000013;   // a neighbour: 100/100, lid 0x11, about -70 dBm
    public const ulong OneBud = 0xD00000000015;      // closest neighbour: one earbud in, 50%, lid 0x10, about -65 dBm
    public static readonly DateTimeOffset Day = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);

    public sealed record Raw(double T, ulong Addr, int Rssi, PodAdvert Ad, string Tag = "");

    public static double At(string hms) => TimeSpan.Parse(hms, CultureInfo.InvariantCulture).TotalSeconds;

    public static List<Raw> Load(string name) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Recordings", "raw", name))
            .Skip(1)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(','))
            .Where(p => p[3] == "2024")
            .Select(p => new Raw(At(p[0]), ulong.Parse(p[1], NumberStyles.HexNumber), int.Parse(p[2], CultureInfo.InvariantCulture),
                ProximityPairing.TryParse(Convert.FromHexString(p[4])) ?? throw new InvalidDataException(p[4])))
            .ToList();

    /// <summary>
    /// ads.csv until it ends (12:21:06), then ads-inear.csv (recorded at the same desk, overlapping) for the rest, so
    /// every neighbour's "first heard" time is the real one from 12:01 rather than when the second logger started.
    /// </summary>
    public static List<Raw> Merged()
    {
        var end = At("12:21:06.1");
        return Load("ads.csv").Where(r => r.T < end).Concat(Load("ads-inear.csv").Where(r => r.T >= end)).OrderBy(r => r.T).ToList();
    }

    /// <summary>What PodBattery actually receives: SignalStrengthFilter.SamplingInterval = 2 s (at most one report per address per 2 s).
    /// Windows' -127 "out of range" reports are kept.</summary>
    public static List<Raw> Sampled(IEnumerable<Raw> rows)
    {
        var last = new Dictionary<ulong, double>();
        var result = new List<Raw>();
        foreach (var r in rows.OrderBy(r => r.T))
        {
            if (r.Rssi != -127 && last.TryGetValue(r.Addr, out var t) && r.T - t < 2.0)
                continue;
            if (r.Rssi != -127)
                last[r.Addr] = r.T;
            result.Add(r);
        }
        return result;
    }

    /// <summary>Drops an address's broadcasts after <paramref name="at"/> (gone: walked out of range or case closed).
    /// Optionally adds the -127 report Windows raises 60 s after an address's last broadcast (seen for every address in ads.csv).</summary>
    public static List<Raw> Stop(IEnumerable<Raw> rows, ulong addr, double at, bool windowsOutOfRangeReport = true)
    {
        var kept = rows.Where(r => r.Addr != addr || r.T <= at).Where(r => !(r.Addr == addr && r.Rssi == -127)).ToList();
        var last = kept.Last(r => r.Addr == addr);
        if (windowsOutOfRangeReport)
            kept.Add(last with { T = last.T + 60, Rssi = -127, Tag = "oor" });
        return kept.OrderBy(r => r.T).ToList();
    }

    /// <summary>The address changes at <paramref name="at"/>: the new one starts <paramref name="gap"/> seconds after the old stops.</summary>
    public static List<Raw> Rotate(IEnumerable<Raw> rows, ulong addr, double at, ulong newAddr, double gap = 6, bool windowsOutOfRangeReport = true)
    {
        var list = rows.ToList();
        var lastOld = list.Where(r => r.Addr == addr && r.T < at && r.Rssi != -127).Last();
        var result = list.Where(r => r.Addr != addr || (r.T < at && r.Rssi != -127))
            .Concat(list.Where(r => r.Addr == addr && r.T >= at + gap && r.Rssi != -127).Select(r => r with { Addr = newAddr, Tag = "rotated" }))
            .ToList();
        if (windowsOutOfRangeReport)
            result.Add(lastOld with { T = lastOld.T + 60, Rssi = -127, Tag = "oor" });
        return result.OrderBy(r => r.T).ToList();
    }

    public static List<(Raw Row, OfferResult Result)> Run(PodTracker tracker, IEnumerable<Raw> rows) =>
        rows.Select(r => (r, tracker.Offer(r.Addr, r.Rssi, r.Ad, Day.AddSeconds(r.T)))).ToList();

    public static bool Accepted(OfferResult result) => result is not (OfferResult.NotMine or OfferResult.OtherModel);

    public static PodTracker NewTracker() => new([ProUsbC], lockRssi: -50);

    public static string Hms(double t) => TimeSpan.FromSeconds(t).ToString(@"hh\:mm\:ss\.f");
}
