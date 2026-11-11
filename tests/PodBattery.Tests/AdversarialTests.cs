using static PodBattery.Tests.RawReplay;

namespace PodBattery.Tests;

/// <summary>
/// Each test states the behaviour we want; a failing test reproduces a way the tracker goes wrong.
/// Real recordings: ads.csv (12:01-12:21), ads-inear.csv (12:18-12:22, owner wearing D00000000016),
/// probe-sampled.csv (12:36-12:42, recorded with BleScanner's exact watcher settings incl. SamplingInterval = 2 s).
/// </summary>
public class AdversarialTests
{
    private const ulong Rotated = 0xE00000000002;          // the lookalike neighbour's next address (spliced rotation)
    private const ulong YourCase = 0xC0FFEE000001;          // your AirPods' next address (spliced)
    private static readonly HashSet<ulong> Owner = [User, 0xD0000000000D, 0xD0000000000E, 0xD00000000011, 0xD00000000012, YourCase];

    /// <summary>The neighbours keep broadcasting after the recordings end: replay 12:18:43-12:22:41 (without the owner) twice more.</summary>
    private static List<Raw> Extended(List<Raw> rows, int loops = 2)
    {
        double from = At("12:18:43"), to = At("12:22:41"), length = to - from;
        var neighbours = rows.Where(r => r.Addr != User && r.Rssi != -127 && r.T >= from && r.T <= to).ToList();
        return rows.Concat(Enumerable.Range(1, loops).SelectMany(k => neighbours.Select(r => r with { T = r.T + k * length })))
            .OrderBy(r => r.T).ToList();
    }

    private static IEnumerable<double> Range(string from, string to, double step)
    {
        for (var t = At(from); t <= At(to); t += step)
            yield return t;
    }

    /// <summary>Your real case-open broadcasts (12:12:06-12:12:16, -36..-41 dBm, lid 0x31) moved to <paramref name="at"/> under a new address.</summary>
    private static IEnumerable<Raw> YourCaseOpened(double at) =>
        Load("ads.csv").Where(r => r.Addr == 0xD00000000011 && r.Rssi != -127)
            .Select(r => r with { Addr = YourCase, T = r.T - At("12:12:06") + at, Tag = "your case" });

    // ---------------------------------------------------------------------------------------------------------------
    // 1. Leaving the desk while wearing them.

    /// <remarks>
    /// Known residual risk, kept as a regression bound rather than zero: you walk away wearing your AirPods and, within
    /// about ±30 s, a pair with the same model, lid state and battery levels (±10%) changes its address at a similar
    /// signal strength. That looks exactly like your own address change. With the current rules it happens in 5 of these
    /// 518 timings (358 before the adversarial review). Opening your case next to the PC always corrects it.
    /// </remarks>
    [Fact]
    public void Leaving_the_desk_rarely_hands_your_airpods_to_a_lookalike_that_changes_address_meanwhile()
    {
        // Real: you wear D00000000016 (100/100, lid 0x11, ~-56 dBm), locked at 12:18:57 from your real case opening.
        // At `leave` you walk out of range (no fade modelled: a real fade only lowers the follow floor further).
        // Real neighbour D00000000013 (100/100, lid 0x11, ~-70 dBm, there since 12:14:29) changes address at `rot`,
        // 6 s gap - like the 5 real rotations in ads.csv (+0.7..+5.5 s).
        var baseRows = Extended(Merged());
        var wrong = new List<(double Leave, double Rot, string What)>();
        var total = 0;
        foreach (var leave in Range("12:19:20", "12:21:30", 10))
        foreach (var rot in Range("12:16:00", "12:25:00", 15))
        {
            total++;
            var run = Run(NewTracker(), Sampled(Rotate(Stop(baseRows, User, leave), Lookalike, rot, Rotated)));
            var hit = run.FirstOrDefault(x => Accepted(x.Result) && !Owner.Contains(x.Row.Addr));
            if (hit.Row is not null)
                wrong.Add((leave, rot, $"left {Hms(leave)}, neighbour rotated {Hms(rot)} -> {hit.Result} {hit.Row.Addr:X12} at {Hms(hit.Row.T)} ({hit.Row.Rssi} dBm)"));
        }

        var offsets = wrong.Select(w => w.Rot - w.Leave).ToList();
        Assert.True(wrong.Count <= 5,
            $"{wrong.Count}/{total} timings show the neighbour's AirPods; neighbour rotating {offsets.DefaultIfEmpty().Min():+0;-0} s .. " +
            $"{offsets.DefaultIfEmpty().Max():+0;-0} s around your departure is enough.\n" + string.Join("\n", wrong.Take(4).Select(w => w.What)));
    }

    [Fact]
    public void Coming_back_and_opening_your_case_next_to_the_pc_undoes_a_wrong_follow()
    {
        // You leave at 12:20:30; the lookalike neighbour's new address shows up 3 s after your last broadcast - exactly
        // what your own rotation looks like. The real lookalike sits ~14 dB weaker than you, which the follow rules now
        // reject, so it's moved 10 dB closer (as a nearer desk would be) to force the wrong follow. You come back at
        // 12:24 and open your case right next to the PC (your real case-open readings, -36..-41 dBm).
        var leave = At("12:20:30");
        var rows = Rotate(Stop(Extended(Merged()), User, leave), Lookalike, leave - 3, Rotated)
            .Select(r => r.Addr == Rotated && r.Rssi > -127 ? r with { Rssi = r.Rssi + 10 } : r).ToList();
        var run = Run(NewTracker(), Sampled(rows.Concat(YourCaseOpened(At("12:24:00"))).OrderBy(r => r.T)));

        Assert.Contains(run, x => x.Row.Addr == Rotated && x.Result == OfferResult.Followed);   // the wrong follow (precondition)
        var yours = run.Where(x => x.Row.Addr == YourCase).Select(x => x.Result).ToList();
        Assert.True(yours.Contains(OfferResult.Locked),
            $"your case next to the PC: {string.Join(",", yours)}; the neighbour's broadcasts keep 'quiet' under RelockAfter, so it never re-locks");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2. Windows' "out of range" report.

    [Fact]
    public void Windows_out_of_range_report_is_ignored()
    {
        // ads.csv: every address that stops gets one more report 60 s later with RSSI -127 and the last payload
        // (also with BleScanner's exact settings: probe-sampled.csv has 28 of them in 6 minutes).
        var rows = Load("ads.csv");
        const ulong YourBud = 0xD00000000012;   // your case opening at 12:12
        var oor = rows.Single(r => r.Addr == YourBud && r.Rssi == -127);
        Assert.InRange(oor.T - rows.Last(r => r.Addr == YourBud && r.Rssi != -127).T, 59, 61);

        var tracker = NewTracker();
        Run(tracker, Sampled(rows.Where(r => r.T < oor.T)));
        var lastSeen = tracker.Snapshot!.LastSeen;

        var result = tracker.Offer(oor.Addr, oor.Rssi, oor.Ad, Day.AddSeconds(oor.T));

        Assert.True(lastSeen == tracker.Snapshot!.LastSeen,
            $"{result}: LastSeen moved {(tracker.Snapshot.LastSeen - lastSeen).TotalSeconds:N0} s forward, our RSSI became -127 (follow floor -85) and the follow window restarted");
    }

    [Fact]
    public void Out_of_range_report_alone_turns_a_safe_departure_into_a_wrong_follow()
    {
        // Left 12:20:00, neighbour rotated 12:21:00. Without the -127 report nothing is followed;
        // with it (as Windows really sends it), the neighbour is followed 10 s after the report.
        var baseRows = Extended(Merged());
        List<(Raw Row, OfferResult Result)> Replay(bool oor) =>
            Run(NewTracker(), Sampled(Rotate(Stop(baseRows, User, At("12:20:00"), oor), Lookalike, At("12:21:00"), Rotated)));

        Assert.DoesNotContain(Replay(oor: false), x => Accepted(x.Result) && !Owner.Contains(x.Row.Addr));
        var wrong = Replay(oor: true).Where(x => Accepted(x.Result) && !Owner.Contains(x.Row.Addr)).ToList();
        Assert.True(wrong.Count == 0, wrong.Count == 0 ? "" : $"with the -127 report: {wrong[0].Result} {wrong[0].Row.Addr:X12} at {Hms(wrong[0].Row.T)} ({wrong[0].Row.Rssi} dBm)");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 3. Starting the app with neighbours already around.

    [Fact]
    public void A_neighbour_already_broadcasting_when_the_app_started_is_not_a_new_address()
    {
        // ads-inear.csv starts at 12:18:42 = PodBattery starting at login. Real lock on your AirPods at 12:18:57.
        // You leave at 12:20:30. D00000000013 has broadcast since 12:14:29 (ads.csv), but its First is 12:18:43.
        var run = Run(NewTracker(), Sampled(Stop(Load("ads-inear.csv"), User, At("12:20:30"), windowsOutOfRangeReport: false)));
        var wrong = run.Where(x => x.Row.Addr == Lookalike && Accepted(x.Result)).ToList();
        Assert.True(wrong.Count == 0, $"{wrong.Count} of the neighbour's broadcasts shown, first {wrong.FirstOrDefault().Result} at {Hms(wrong.FirstOrDefault().Row?.T ?? 0)}");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 4. A short reception gap while you're still wearing them.

    [Fact]
    public void A_reception_gap_while_worn_does_not_adopt_a_lookalike()
    {
        // probe-sampled.csv (BleScanner's exact watcher settings): D0000000001A, 90/90 in ear, median -52 dBm, has real
        // gaps of 10.0, 12.1, 14.1 and 14.0 s while staying in range. Worn AirPods are never locked on automatically,
        // so this starts with "Find my AirPods" active, which locks on to them.
        // Spliced: the real RSSI series of the closest neighbour (D00000000015, ~-65 dBm) as a pair also at 90/90 in ear,
        // whose address changed at 12:38:30.
        const ulong Worn = 0xD0000000001A, Next = 0xE000000000AA;
        var probe = Load("probe-sampled.csv");
        var wornAd = probe.First(r => r.Addr == Worn).Ad;
        var neighbour = Load("ads-inear.csv").Where(r => r.Addr == OneBud && r.Rssi != -127)
            .Select(r => r with { Addr = Next, T = r.T - At("12:18:42") + At("12:38:30"), Ad = wornAd, Tag = "lookalike" });
        var tracker = NewTracker();
        tracker.StartFinding(Day.AddSeconds(probe[0].T), TimeSpan.FromMinutes(1));
        var run = Run(tracker, Sampled(probe.Concat(neighbour).OrderBy(r => r.T)));

        Assert.Contains(run, x => x.Row.Addr == Worn && x.Result == OfferResult.Locked);
        var wrong = run.Where(x => x.Row.Addr == Next && Accepted(x.Result)).ToList();
        var stillWorn = wrong.Count == 0 ? 0 : run.Count(x => x.Row.Addr == Worn && x.Row.T > wrong[0].Row.T);
        Assert.True(wrong.Count == 0,
            $"{wrong.Count} lookalike broadcasts shown, first {wrong.FirstOrDefault().Result} at {Hms(wrong.FirstOrDefault().Row?.T ?? 0)}, " +
            $"while yours kept broadcasting ({stillWorn} more of yours afterwards, so the display flips between the two)");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 5. Find my AirPods after a wrong follow.

    [Fact]
    public void Find_my_airpods_drops_addresses_followed_before()
    {
        const ulong Mine = 0xA1, Neighbour = 0xB1;
        var t0 = Day.AddHours(9);
        var inEar = new PodAdvert(ProUsbC, 100, 100, null, false, false, false, 0x11);
        var tracker = NewTracker();
        tracker.StartFinding(t0, TimeSpan.FromSeconds(60));                                   // Find, worn AirPods held at the PC
        tracker.Offer(Mine, -45, inEar, t0);
        tracker.Offer(Mine, -45, inEar, t0.AddSeconds(1));
        Assert.Equal(OfferResult.Locked, tracker.Offer(Mine, -45, inEar, t0.AddSeconds(2)));
        for (var s = 3; s <= 9; s += 2)
            tracker.Offer(Mine, -56, inEar, t0.AddSeconds(s));                                // worn, typical -56 dBm
        var results = new List<OfferResult>();
        for (var s = 8; s <= 30; s += 2)
            results.Add(tracker.Offer(Neighbour, -58, inEar, t0.AddSeconds(s)));              // a lookalike changes address
        Assert.Contains(OfferResult.Followed, results);                                        // yours went quiet: wrong follow

        // The display looks off, so you use Find and hold your (in-ear) AirPods next to the PC.
        tracker.StartFinding(t0.AddSeconds(60), TimeSpan.FromSeconds(60));
        tracker.Offer(Mine, -45, inEar, t0.AddSeconds(61));
        tracker.Offer(Mine, -45, inEar, t0.AddSeconds(62));
        Assert.Equal(OfferResult.Locked, tracker.Offer(Mine, -45, inEar, t0.AddSeconds(63)));

        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -66, inEar, t0.AddSeconds(64)));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 6. Lock-on smoothing: two samples, but the first one carries 70 % of the weight.

    [Fact]
    public void A_single_spike_followed_by_a_normal_reading_does_not_lock_on()
    {
        var tracker = NewTracker();
        var ad = new PodAdvert(ProUsbC, 50, null, null, false, false, false, 0x10);
        tracker.Offer(OneBud, -44, ad, Day);                                                     // one spike
        Assert.Equal(OfferResult.NotMine, tracker.Offer(OneBud, -63, ad, Day.AddSeconds(2)));    // EMA -49.7: fails, Locked
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 7. Coming back after more than FollowWindow with a new address while wearing them (documents current behaviour).

    [Fact]
    public void Documented_after_a_long_meeting_your_worn_airpods_are_not_picked_up_without_the_case()
    {
        // Your real in-ear stream after 12:19:30 (-44..-73 dBm, EMA below -50), as a new address 40 min after you left.
        var tracker = NewTracker();
        var ear = Load("ads-inear.csv").Where(r => r.Addr == User && r.Rssi != -127 && r.T < At("12:19:30")).ToList();
        Run(tracker, Sampled(ear));
        var later = Load("ads-inear.csv").Where(r => r.Addr == User && r.Rssi != -127 && r.T >= At("12:19:30"))
            .Select(r => r with { Addr = YourCase, T = r.T + 2400 });
        var run = Run(tracker, Sampled(later));
        Assert.DoesNotContain(run, x => Accepted(x.Result));   // passes today: grey "not in range" until you open the case at the PC
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Holds up: following real address changes (neighbour pairs adopted as if they were yours).

    [Theory]
    [InlineData(0xD00000000001, 0xD0000000000C, "12:05:00")]   // 70/-,   ~-80 dBm, new address +0.7 s
    [InlineData(0xD00000000003, 0xD00000000010, "12:09:00")]   // 60/70,  ~-79 dBm, +2.8 s
    [InlineData(0xD00000000007, 0xD00000000013, "12:13:00")]   // 100/100, ~-74 dBm, +5.5 s
    [InlineData(0xD00000000008, 0xD00000000015, "12:15:00")]   // 50/- (one bud), ~-63 dBm, +1.3 s
    public void Follows_real_address_changes(ulong oldAddr, ulong newAddr, string adoptAt)
    {
        var rows = Sampled(Merged());
        var t = At(adoptAt);
        var tracker = NewTracker();
        Run(tracker, rows.Where(r => r.T < t));
        var ad = rows.Last(r => r.Addr == oldAddr && r.T < t).Ad;
        tracker.StartFinding(Day.AddSeconds(t), TimeSpan.FromSeconds(60));
        var holding = Enumerable.Range(0, 10).Select(i => tracker.Offer(oldAddr, -45, ad, Day.AddSeconds(t + i))).ToList();   // case held at the PC
        Assert.Contains(OfferResult.Locked, holding);

        var lastOld = rows.Last(r => r.Addr == oldAddr && r.Rssi != -127).T;
        var run = Run(tracker, rows.Where(r => r.T > t + 10 && r.T < lastOld + 240));
        Assert.Contains(run, x => x.Row.Addr == newAddr && x.Result == OfferResult.Followed);
        Assert.DoesNotContain(run, x => x.Row.Addr != newAddr && x.Row.Addr != oldAddr && !Owner.Contains(x.Row.Addr) && Accepted(x.Result));
    }
}
