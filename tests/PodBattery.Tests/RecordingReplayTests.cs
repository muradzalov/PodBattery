using System.Globalization;

namespace PodBattery.Tests;

/// <summary>
/// Replays real broadcasts recorded in a busy office through the tracker. Each row is labelled with whether it
/// came from the owner's AirPods, so we can check the tracker never shows someone else's battery.
/// </summary>
public class RecordingReplayTests
{
    private const ushort ProUsbC = 0x2024;
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 1, 7, TimeSpan.Zero);

    private sealed record Row(double Seconds, ulong Device, int Rssi, bool Mine, PodAdvert Ad);

    private static List<Row> Load(string name) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Recordings", name))
            .Where(line => !line.StartsWith('#') && line.Length > 0)
            .Select(line => line.Split(','))
            .Select(p => new Row(
                double.Parse(p[0], CultureInfo.InvariantCulture),
                ulong.Parse(p[1], CultureInfo.InvariantCulture),
                int.Parse(p[2], CultureInfo.InvariantCulture),
                p[3] == "1",
                ProximityPairing.TryParse(Convert.FromHexString(p[4])) ?? throw new InvalidDataException(p[4])))
            .ToList();

    private static List<(Row Row, OfferResult Result)> Replay(IEnumerable<Row> rows, int lockRssi = -50)
    {
        var tracker = new PodTracker([ProUsbC], lockRssi);
        return rows.Select(r => (r, tracker.Offer(r.Device, r.Rssi, r.Ad, Start.AddSeconds(r.Seconds)))).ToList();
    }

    private static bool Accepted(OfferResult result) => result is not (OfferResult.NotMine or OfferResult.OtherModel);

    [Fact]
    public void Recording_has_many_neighbours_and_the_owners_two_case_openings()
    {
        var rows = Load("office.csv");
        Assert.True(rows.Where(r => !r.Mine).Select(r => r.Device).Distinct().Count() >= 15);
        Assert.Equal(5, rows.Where(r => r.Mine).Select(r => r.Device).Distinct().Count());
    }

    [Theory]
    [InlineData(-50)]
    [InlineData(-53)]   // a neighbour's single reading hits -53 here; must still not lock on
    [InlineData(-45)]   // stricter setting
    public void Never_accepts_someone_elses_airpods(int lockRssi)
    {
        var wrong = Replay(Load("office.csv"), lockRssi)
            .Where(x => Accepted(x.Result) && !x.Row.Mine)
            .Select(x => $"device {x.Row.Device} at {x.Row.Seconds}s ({x.Result}, {x.Row.Rssi} dBm)")
            .ToList();

        Assert.Empty(wrong);
    }

    [Fact]
    public void Locks_on_every_time_the_owner_opens_the_case_next_to_the_pc()
    {
        var replay = Replay(Load("office.csv"));
        var locks = replay.Where(x => x.Result == OfferResult.Locked).Select(x => x.Row.Seconds).ToList();

        Assert.Contains(locks, s => s is > 380 and < 400);     // first opening, ~6.5 minutes in
        Assert.Contains(locks, s => s is > 655 and < 670);     // second opening, ~11 minutes in
        Assert.Contains(locks, s => s is > 1060 and < 1075);   // taking them out to wear, ~17.8 minutes in
    }

    [Fact]
    public void Keeps_reading_the_owners_airpods_while_worn()
    {
        var replay = Replay(Load("office.csv"));
        var worn = replay.Where(x => x.Row.Mine && x.Row.Seconds > 1070).ToList();

        Assert.True(worn.Count > 50);
        Assert.All(worn, x => Assert.True(Accepted(x.Result), $"missed at {x.Row.Seconds}s ({x.Row.Rssi} dBm)"));
    }

    [Fact]
    public void Ends_showing_the_owners_levels()
    {
        var tracker = new PodTracker([ProUsbC], -50);
        foreach (var r in Load("office.csv"))
            tracker.Offer(r.Device, r.Rssi, r.Ad, Start.AddSeconds(r.Seconds));

        var pods = tracker.Snapshot!;
        Assert.Equal((90, 100), (pods.Left?.Percent, pods.Right?.Percent));
    }

    [Fact]
    public void Without_the_owner_present_nothing_is_ever_shown()
    {
        var neighboursOnly = Load("office.csv").Where(r => !r.Mine);
        Assert.DoesNotContain(Replay(neighboursOnly), x => Accepted(x.Result));
    }
}
