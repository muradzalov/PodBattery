namespace PodBattery.Tests;

public class PodTrackerTests
{
    private const ushort ProUsbC = 0x2024;
    private const ulong Mine = 0xA1, MineOtherBud = 0xA2, MineRotated = 0xA3, Neighbour = 0xB1, Neighbour2 = 0xB2;
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private static PodTracker NewTracker() => new([ProUsbC], lockRssi: -50);

    /// <summary>Worn AirPods (status 0x2B: both earbuds in ears), unless <paramref name="status"/> says otherwise.</summary>
    private static PodAdvert Ad(int? left, int? right, int? @case = null, byte lid = 0x11, ushort model = ProUsbC, byte status = 0x2B) =>
        new(model, left, right, @case, false, false, false, lid, status);

    /// <summary>Both earbuds in the case with the lid open, as broadcast by real AirPods (status 0x34, lid 0x31).</summary>
    private static PodAdvert CaseOpen(int left, int right, int @case) => Ad(left, right, @case, lid: 0x31, status: 0x34);

    /// <summary>Three strong broadcasts a second apart, ending at <paramref name="at"/>: the third one can lock on in Find mode.</summary>
    private static OfferResult HoldClose(PodTracker tracker, ulong address, int rssi, PodAdvert ad, DateTimeOffset at)
    {
        Assert.NotEqual(OfferResult.Locked, tracker.Offer(address, rssi, ad, at.AddSeconds(-2)));
        Assert.NotEqual(OfferResult.Locked, tracker.Offer(address, rssi, ad, at.AddSeconds(-1)));
        return tracker.Offer(address, rssi, ad, at);
    }

    /// <summary>"Find my AirPods", then the worn AirPods held next to the PC.</summary>
    private static PodTracker LockedOnMine(int rssi = -45)
    {
        var tracker = NewTracker();
        tracker.StartFinding(T0.AddSeconds(-5), TimeSpan.FromSeconds(60));
        Assert.Equal(OfferResult.Locked, HoldClose(tracker, Mine, rssi, Ad(60, 70), T0));
        return tracker;
    }

    // ---- Locking on ----

    [Fact]
    public void Ignores_models_not_paired_with_this_pc()
    {
        var tracker = NewTracker();
        Assert.Equal(OfferResult.OtherModel, tracker.Offer(Mine, -40, CaseOpen(100, 100, 80) with { Model = 0x2014 }, T0));
        Assert.Null(tracker.Snapshot);
    }

    [Fact]
    public void Locks_on_when_a_case_is_opened_right_next_to_the_pc()
    {
        var tracker = NewTracker();
        Assert.Equal(OfferResult.Locked, tracker.Offer(Mine, -42, CaseOpen(100, 90, 80), T0));

        var pods = tracker.Snapshot!;
        Assert.Equal((100, 90, 80), (pods.Left?.Percent, pods.Right?.Percent, pods.Case?.Percent));
    }

    [Fact]
    public void Never_locks_on_by_itself_to_airpods_worn_nearby()
    {
        // A neighbour wearing one earbud (30%) right by the laptop, for a long time, while yours are away.
        var tracker = NewTracker();
        var oneEarbud = Ad(30, null, lid: 0x10, status: 0x22);
        for (var s = 0; s < 60; s += 2)
            Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -42, oneEarbud, T0.AddSeconds(s)));
        Assert.Null(tracker.Snapshot);
    }

    [Fact]
    public void A_case_opened_a_few_desks_away_is_ignored()
    {
        var tracker = NewTracker();
        for (var s = 0; s < 20; s += 2)
            Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -62, CaseOpen(100, 100, 80), T0.AddSeconds(s)));
    }

    [Fact]
    public void A_closed_case_does_not_count_as_opened()
    {
        var justClosed = Ad(100, 100, 80, lid: 0x39, status: 0x34);
        Assert.Equal(OfferResult.NotMine, NewTracker().Offer(Mine, -40, justClosed, T0));
    }

    [Fact]
    public void A_spike_does_not_lock_on_when_the_address_is_usually_weaker()
    {
        var tracker = NewTracker();
        tracker.Offer(Neighbour, -66, CaseOpen(100, 100, 80), T0);
        tracker.Offer(Neighbour, -64, CaseOpen(100, 100, 80), T0.AddSeconds(2));
        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -48, CaseOpen(100, 100, 80), T0.AddSeconds(4)));
    }

    [Fact]
    public void Find_mode_locks_on_to_worn_airpods_held_next_to_the_pc()
    {
        var pods = LockedOnMine().Snapshot!;

        Assert.Equal(60, pods.Left?.Percent);
        Assert.Equal(70, pods.Right?.Percent);
        Assert.Null(pods.Case);
    }

    [Fact]
    public void Find_mode_needs_a_sustained_signal_not_a_single_reading()
    {
        var tracker = NewTracker();
        tracker.StartFinding(T0, TimeSpan.FromSeconds(60));
        tracker.Offer(Neighbour, -66, Ad(50, null), T0);
        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -48, Ad(50, null), T0.AddSeconds(2)));
        Assert.Null(tracker.Snapshot);
    }

    [Fact]
    public void Lock_threshold_is_inclusive()
    {
        Assert.Equal(OfferResult.Locked, NewTracker().Offer(Mine, -50, CaseOpen(100, 100, 80), T0));
        Assert.Equal(OfferResult.NotMine, NewTracker().Offer(Mine, -51, CaseOpen(100, 100, 80), T0));
    }

    [Fact]
    public void Picks_up_the_other_earbud_just_after_locking()
    {
        var tracker = LockedOnMine(rssi: -40);
        Assert.Equal(OfferResult.Sibling, HoldClose(tracker, MineOtherBud, -47, Ad(null, 70), T0.AddSeconds(3)));
        Assert.Equal(OfferResult.Known, tracker.Offer(MineOtherBud, -80, Ad(60, 70), T0.AddSeconds(30)));
    }

    [Fact]
    public void Does_not_take_a_close_but_different_pair_as_the_other_earbud()
    {
        var tracker = LockedOnMine(rssi: -40);
        Assert.Equal(OfferResult.NotMine, HoldClose(tracker, Neighbour, -45, Ad(20, 30), T0.AddSeconds(3)));
    }

    [Fact]
    public void Only_picks_up_the_other_earbud_shortly_after_locking()
    {
        var tracker = LockedOnMine(rssi: -40);
        var late = T0 + PodTracker.SiblingWindow + TimeSpan.FromSeconds(2);
        tracker.Offer(Mine, -40, Ad(60, 70), late.AddSeconds(-2));
        Assert.Equal(OfferResult.NotMine, HoldClose(tracker, MineOtherBud, -45, Ad(60, 70), late));
    }

    // ---- Reading ----

    [Fact]
    public void Keeps_reading_a_known_address_even_when_far_away()
    {
        var tracker = LockedOnMine();
        Assert.Equal(OfferResult.Known, tracker.Offer(Mine, -80, Ad(50, 70), T0.AddSeconds(5)));
        Assert.Equal(50, tracker.Snapshot!.Left?.Percent);
    }

    [Fact]
    public void Remembers_last_level_when_an_earbud_goes_unreported()
    {
        var tracker = LockedOnMine();
        tracker.Offer(Mine, -60, Ad(60, null), T0.AddSeconds(5));
        Assert.Equal(70, tracker.Snapshot!.Right?.Percent);
    }

    [Fact]
    public void Forgets_addresses_unheard_for_a_long_time()
    {
        var tracker = LockedOnMine();
        var later = T0 + PodTracker.ForgetAfter + TimeSpan.FromSeconds(1);
        Assert.Equal(OfferResult.NotMine, tracker.Offer(Mine, -70, Ad(60, 70), later));
    }

    // ---- Following an address change ----
    // Locked at -45 dBm, so a new address must read at least -53 (8 dB slack) and be heard twice.

    /// <summary>Two broadcasts a second apart, ending at <paramref name="at"/>; returns what the second one got.</summary>
    private static OfferResult Twice(PodTracker tracker, ulong address, int rssi, PodAdvert ad, DateTimeOffset at)
    {
        tracker.Offer(address, rssi, ad, at.AddSeconds(-1));
        return tracker.Offer(address, rssi, ad, at);
    }

    [Fact]
    public void Follows_to_a_new_address_once_the_old_one_goes_quiet()
    {
        var tracker = LockedOnMine(rssi: -45);
        Assert.Equal(OfferResult.Followed, Twice(tracker, MineRotated, -50, Ad(60, 70), T0.AddSeconds(22)));
        Assert.Equal(OfferResult.Known, tracker.Offer(MineRotated, -75, Ad(50, 70), T0.AddSeconds(25)));
    }

    [Fact]
    public void A_new_address_heard_only_once_is_not_followed_yet()
    {
        var tracker = LockedOnMine(rssi: -45);
        Assert.Equal(OfferResult.NotMine, tracker.Offer(MineRotated, -50, Ad(60, 70), T0.AddSeconds(22)));
    }

    [Fact]
    public void Follows_a_new_address_that_appears_up_to_half_a_minute_later()
    {
        // Recorded address changes started 0.7-30.7 s after the old address's last broadcast.
        var tracker = LockedOnMine(rssi: -45);
        Assert.Equal(OfferResult.Followed, Twice(tracker, MineRotated, -52, Ad(60, 70), T0.AddSeconds(31)));
    }

    [Fact]
    public void Does_not_follow_an_address_that_appears_well_after_ours_stopped()
    {
        // Ours went quiet (walked off, or case closed) and a lookalike shows up a minute later: not a rotation.
        var tracker = LockedOnMine(rssi: -45);
        Assert.Equal(OfferResult.NotMine, Twice(tracker, Neighbour, -50, Ad(60, 70), T0.AddSeconds(60)));
    }

    [Fact]
    public void Does_not_follow_an_address_that_was_already_there_shortly_before()
    {
        var tracker = NewTracker();
        tracker.Offer(Neighbour, -50, Ad(60, 70), T0.AddSeconds(-30));
        tracker.StartFinding(T0.AddSeconds(-5), TimeSpan.FromSeconds(60));
        Assert.Equal(OfferResult.Locked, HoldClose(tracker, Mine, -45, Ad(60, 70), T0));
        Assert.Equal(OfferResult.NotMine, Twice(tracker, Neighbour, -50, Ad(60, 70), T0.AddSeconds(22)));
    }

    [Fact]
    public void Windows_out_of_range_report_is_not_a_reading()
    {
        var tracker = LockedOnMine();
        Assert.Equal(OfferResult.Known, tracker.Offer(Mine, PodTracker.OutOfRange, Ad(60, 70), T0.AddSeconds(60)));
        Assert.Equal(T0, tracker.Snapshot!.LastSeen);
    }

    [Fact]
    public void Opening_your_case_next_to_the_pc_overrides_a_follow()
    {
        var tracker = LockedOnMine();
        Assert.Equal(OfferResult.Followed, Twice(tracker, Neighbour, -50, Ad(60, 70), T0.AddSeconds(22)));

        Assert.Equal(OfferResult.Locked, tracker.Offer(MineRotated, -40, CaseOpen(100, 100, 80), T0.AddSeconds(60)));
        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -50, Ad(60, 70), T0.AddSeconds(61)));
        Assert.Equal(100, tracker.Snapshot!.Left?.Percent);
    }

    [Fact]
    public void Does_not_follow_while_the_known_address_is_still_broadcasting()
    {
        var tracker = LockedOnMine();
        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -55, Ad(60, 70), T0.AddSeconds(3)));
    }

    [Theory]
    [InlineData(20, 20, 0x11, -50)]   // different battery levels
    [InlineData(60, 70, 0x31, -50)]   // different lid state (case open vs in ears)
    [InlineData(60, 70, 0x11, -55)]   // more than 8 dB weaker than ours was
    public void Does_not_follow_airpods_that_look_different(int left, int right, byte lid, int rssi)
    {
        var tracker = LockedOnMine(rssi: -45);
        Assert.Equal(OfferResult.NotMine, Twice(tracker, Neighbour, rssi, Ad(left, right, lid: lid), T0.AddSeconds(22)));
    }

    [Fact]
    public void Does_not_follow_an_address_that_was_around_long_before_ours_went_quiet()
    {
        var tracker = NewTracker();
        tracker.Offer(Neighbour, -60, Ad(60, 70), T0.AddMinutes(-4));
        tracker.StartFinding(T0.AddSeconds(-5), TimeSpan.FromSeconds(60));
        Assert.Equal(OfferResult.Locked, HoldClose(tracker, Mine, -45, Ad(60, 70), T0));

        Assert.Equal(OfferResult.NotMine, Twice(tracker, Neighbour, -50, Ad(60, 70), T0.AddSeconds(22)));
    }

    [Fact]
    public void Prefers_the_strongest_of_several_lookalike_candidates()
    {
        var tracker = LockedOnMine(rssi: -45);
        for (var s = 5; s <= 19; s += 2)
        {
            tracker.Offer(Neighbour, -53, Ad(60, 70), T0.AddSeconds(s));
            tracker.Offer(MineRotated, -51, Ad(60, 70), T0.AddSeconds(s + 0.5));
        }

        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -53, Ad(60, 70), T0.AddSeconds(21)));
        Assert.Equal(OfferResult.Followed, tracker.Offer(MineRotated, -51, Ad(60, 70), T0.AddSeconds(21.5)));
    }

    [Fact]
    public void Does_not_follow_after_the_follow_window()
    {
        var tracker = LockedOnMine();
        var later = T0 + PodTracker.FollowWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(OfferResult.NotMine, Twice(tracker, MineRotated, -50, Ad(60, 70), later));
    }

    // ---- Re-locking ----

    [Fact]
    public void A_close_neighbour_cannot_take_over_while_yours_are_active()
    {
        var tracker = LockedOnMine();
        tracker.Offer(Mine, -60, Ad(60, 70), T0.AddSeconds(25));
        Assert.Equal(OfferResult.NotMine, HoldClose(tracker, Neighbour, -40, CaseOpen(20, 20, 50), T0.AddSeconds(90)));
        Assert.Equal(60, tracker.Snapshot!.Left?.Percent);
    }

    [Fact]
    public void Relocks_on_close_airpods_once_yours_have_been_gone_a_while()
    {
        var tracker = LockedOnMine();
        var later = T0 + PodTracker.RelockAfter + TimeSpan.FromSeconds(2);

        Assert.Equal(OfferResult.Locked, tracker.Offer(MineRotated, -42, CaseOpen(100, 100, 80), later));

        var pods = tracker.Snapshot!;
        Assert.Equal(100, pods.Left?.Percent);
        Assert.Equal(80, pods.Case?.Percent);
        Assert.Equal(OfferResult.NotMine, tracker.Offer(Mine, -80, Ad(60, 70), later.AddSeconds(1)));
    }

    [Fact]
    public void Find_mode_switches_to_whichever_airpods_are_held_next_to_the_pc()
    {
        var tracker = LockedOnMine();
        tracker.StartFinding(T0.AddSeconds(20), TimeSpan.FromSeconds(60));
        Assert.True(tracker.IsFinding(T0.AddSeconds(21)));

        Assert.Equal(OfferResult.NotMine, HoldClose(tracker, Neighbour, -60, Ad(60, 70, lid: 0x31), T0.AddSeconds(23)));
        Assert.Equal(OfferResult.Locked, HoldClose(tracker, Neighbour2, -44, Ad(30, 30, lid: 0x05), T0.AddSeconds(26)));

        Assert.False(tracker.IsFinding(T0.AddSeconds(27)));
        Assert.Equal(30, tracker.Snapshot!.Left?.Percent);
        Assert.Equal(30, tracker.Snapshot.Right?.Percent);
    }

    [Fact]
    public void Find_mode_confirms_when_your_known_airpods_are_held_close()
    {
        var tracker = LockedOnMine();
        tracker.StartFinding(T0.AddSeconds(20), TimeSpan.FromSeconds(60));
        Assert.Equal(OfferResult.Locked, HoldClose(tracker, Mine, -40, Ad(60, 70), T0.AddSeconds(23)));
        Assert.False(tracker.IsFinding(T0.AddSeconds(24)));
    }

    [Fact]
    public void Find_mode_times_out()
    {
        var tracker = NewTracker();
        tracker.StartFinding(T0, TimeSpan.FromSeconds(60));
        Assert.True(tracker.IsFinding(T0.AddSeconds(59)));
        Assert.False(tracker.IsFinding(T0.AddSeconds(60)));
    }
}
