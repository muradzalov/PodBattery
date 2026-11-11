namespace PodBattery.Tests;

public class PodTrackerTests
{
    private const ushort ProUsbC = 0x2024;
    private const ulong Mine = 0xA1, MineOtherBud = 0xA2, MineRotated = 0xA3, Neighbour = 0xB1, Neighbour2 = 0xB2;
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private static PodTracker NewTracker() => new([ProUsbC], lockRssi: -50);

    private static PodAdvert Ad(int? left, int? right, int? @case = null, byte lid = 0x11, ushort model = ProUsbC) =>
        new(model, left, right, @case, false, false, false, lid);

    /// <summary>Two strong broadcasts a second apart, ending at <paramref name="at"/>: the second one can lock on.</summary>
    private static OfferResult HoldClose(PodTracker tracker, ulong address, int rssi, PodAdvert ad, DateTimeOffset at)
    {
        Assert.NotEqual(OfferResult.Locked, tracker.Offer(address, rssi, ad, at.AddSeconds(-1)));
        return tracker.Offer(address, rssi, ad, at);
    }

    private static PodTracker LockedOnMine(int rssi = -45)
    {
        var tracker = NewTracker();
        Assert.Equal(OfferResult.Locked, HoldClose(tracker, Mine, rssi, Ad(60, 70), T0));
        return tracker;
    }

    // ---- Locking on ----

    [Fact]
    public void Ignores_models_not_paired_with_this_pc()
    {
        var tracker = NewTracker();
        Assert.Equal(OfferResult.OtherModel, tracker.Offer(Mine, -40, Ad(60, 70, model: 0x2014), T0));
        Assert.Null(tracker.Snapshot);
    }

    [Fact]
    public void Ignores_airpods_that_are_not_close_when_nothing_is_locked()
    {
        var tracker = NewTracker();
        Assert.Equal(OfferResult.NotMine, HoldClose(tracker, Neighbour, -55, Ad(60, 70), T0));
        Assert.Null(tracker.Snapshot);
    }

    [Fact]
    public void Locks_on_to_airpods_held_right_next_to_the_pc()
    {
        var pods = LockedOnMine().Snapshot!;

        Assert.Equal(60, pods.Left?.Percent);
        Assert.Equal(70, pods.Right?.Percent);
        Assert.Null(pods.Case);
    }

    [Fact]
    public void A_single_strong_reading_is_not_enough_to_lock_on()
    {
        var tracker = NewTracker();
        tracker.Offer(Neighbour, -66, Ad(50, null), T0);
        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -48, Ad(50, null), T0.AddSeconds(2)));
        Assert.Null(tracker.Snapshot);
    }

    [Fact]
    public void Lock_threshold_is_inclusive()
    {
        Assert.Equal(OfferResult.Locked, HoldClose(NewTracker(), Mine, -50, Ad(60, 70), T0));
        Assert.Equal(OfferResult.NotMine, HoldClose(NewTracker(), Mine, -51, Ad(60, 70), T0));
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

    [Fact]
    public void Follows_to_a_new_address_once_the_old_one_goes_quiet()
    {
        var tracker = LockedOnMine(rssi: -45);
        Assert.Equal(OfferResult.Followed, tracker.Offer(MineRotated, -55, Ad(60, 70), T0.AddSeconds(12)));
        Assert.Equal(OfferResult.Known, tracker.Offer(MineRotated, -75, Ad(50, 70), T0.AddSeconds(15)));
    }

    [Fact]
    public void Follows_a_new_address_that_started_before_the_old_one_stopped()
    {
        // AirPods can start broadcasting from the new address before the old one stops.
        var tracker = LockedOnMine(rssi: -45);
        Assert.Equal(OfferResult.NotMine, tracker.Offer(MineRotated, -52, Ad(60, 70), T0.AddSeconds(1)));
        tracker.Offer(Mine, -45, Ad(60, 70), T0.AddSeconds(60));
        Assert.Equal(OfferResult.Followed, tracker.Offer(MineRotated, -52, Ad(60, 70), T0.AddSeconds(75)));
    }

    [Fact]
    public void Does_not_follow_while_the_known_address_is_still_broadcasting()
    {
        var tracker = LockedOnMine();
        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -55, Ad(60, 70), T0.AddSeconds(3)));
    }

    [Theory]
    [InlineData(20, 20, 0x11, -55)]   // different battery levels
    [InlineData(60, 70, 0x31, -55)]   // different lid state (case open vs in ears)
    [InlineData(60, 70, 0x11, -70)]   // much weaker signal than ours was
    public void Does_not_follow_airpods_that_look_different(int left, int right, byte lid, int rssi)
    {
        var tracker = LockedOnMine(rssi: -45);
        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, rssi, Ad(left, right, lid: lid), T0.AddSeconds(12)));
    }

    [Fact]
    public void Does_not_follow_an_address_that_was_around_long_before_ours_went_quiet()
    {
        var tracker = NewTracker();
        tracker.Offer(Neighbour, -60, Ad(60, 70), T0.AddMinutes(-4));
        Assert.Equal(OfferResult.Locked, HoldClose(tracker, Mine, -45, Ad(60, 70), T0));

        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -52, Ad(60, 70), T0.AddSeconds(12)));
    }

    [Fact]
    public void Prefers_the_strongest_of_several_lookalike_candidates()
    {
        var tracker = LockedOnMine(rssi: -45);
        tracker.Offer(Neighbour, -56, Ad(60, 70), T0.AddSeconds(5));
        tracker.Offer(MineRotated, -52, Ad(60, 70), T0.AddSeconds(6));

        Assert.Equal(OfferResult.NotMine, tracker.Offer(Neighbour, -56, Ad(60, 70), T0.AddSeconds(12)));
        Assert.Equal(OfferResult.Followed, tracker.Offer(MineRotated, -52, Ad(60, 70), T0.AddSeconds(12.5)));
    }

    [Fact]
    public void Does_not_follow_after_the_follow_window()
    {
        var tracker = LockedOnMine();
        var later = T0 + PodTracker.FollowWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(OfferResult.NotMine, tracker.Offer(MineRotated, -55, Ad(60, 70), later));
    }

    // ---- Re-locking ----

    [Fact]
    public void A_close_neighbour_cannot_take_over_while_yours_are_active()
    {
        var tracker = LockedOnMine();
        tracker.Offer(Mine, -60, Ad(60, 70), T0.AddSeconds(25));
        Assert.Equal(OfferResult.NotMine, HoldClose(tracker, Neighbour, -40, Ad(20, 20, lid: 0x31), T0.AddSeconds(90)));
        Assert.Equal(60, tracker.Snapshot!.Left?.Percent);
    }

    [Fact]
    public void Relocks_on_close_airpods_once_yours_have_been_gone_a_while()
    {
        var tracker = LockedOnMine();
        var later = T0 + PodTracker.RelockAfter + TimeSpan.FromSeconds(2);

        Assert.Equal(OfferResult.Locked, HoldClose(tracker, MineRotated, -42, Ad(100, 100, 80, lid: 0x31), later));

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
