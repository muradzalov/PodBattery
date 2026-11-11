namespace PodBattery;

public enum OfferResult
{
    /// <summary>Not one of the AirPods models paired with this PC.</summary>
    OtherModel,
    /// <summary>Same model, but judged to be someone else's.</summary>
    NotMine,
    /// <summary>From an address already known to be yours.</summary>
    Known,
    /// <summary>Locked on because the AirPods were right next to this PC.</summary>
    Locked,
    /// <summary>Your other earbud: close by and matching, just after locking on.</summary>
    Sibling,
    /// <summary>Followed your AirPods to a new address after the old one went quiet.</summary>
    Followed,
}

public readonly record struct PodValue(int Percent, bool Charging, DateTimeOffset At);

public sealed record PodSnapshot(ushort Model, PodValue? Left, PodValue? Right, PodValue? Case, DateTimeOffset LastSeen);

/// <summary>
/// Works out which AirPods broadcasts are <em>yours</em> in a room that may contain many identical AirPods.
/// </summary>
/// <remarks>
/// AirPods broadcast from anonymous Bluetooth addresses that change every 15 minutes or so, and nothing in the
/// readable part of the broadcast identifies the owner. Each earbud has its own address. So:
/// <list type="number">
/// <item><b>Lock on</b> to AirPods with a consistently very strong signal (case opened right next to the PC), when asked to via
/// <see cref="StartFinding"/> or once yours haven't been heard for <see cref="RelockAfter"/>. Shortly after locking,
/// a second strong, matching address is your other earbud.</item>
/// <item><b>Follow</b> a change of address. A pair starts broadcasting from its new address shortly before the old
/// one stops, so once yours has gone quiet, the strongest <em>recently appeared</em> address with matching battery
/// levels and a similar signal strength is yours. Addresses that were around long before can't be.</item>
/// </list>
/// </remarks>
public sealed class PodTracker
{
    public static readonly TimeSpan FollowAfterQuiet = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FollowWindow = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan NewAddressWindow = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan RelockAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SiblingWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(20);
    private const int FollowRssiFloor = -85;
    private const int FollowRssiSlack = 12;
    private const int BatteryTolerance = 10;

    /// <summary>What we know about one broadcasting address of a watched model.</summary>
    private sealed class Heard(DateTimeOffset now, int rssi, PodAdvert ad)
    {
        public DateTimeOffset First { get; } = now;
        public DateTimeOffset Last { get; set; } = now;
        /// <summary>Smoothed signal strength, over <see cref="Samples"/> broadcasts without a long gap.</summary>
        public double Rssi { get; set; } = rssi;
        public int Samples { get; set; } = 1;
        public PodAdvert Ad { get; set; } = ad;

        /// <summary>Consistently strong, not a one-off spike: a neighbour's AirPods can briefly read a few dB from the threshold.</summary>
        public bool IsClose(int lockRssi) => Samples >= 2 && Rssi >= lockRssi;
    }

    private readonly HashSet<ushort> _models = [];
    private readonly Dictionary<ulong, Heard> _heard = [];
    private readonly HashSet<ulong> _mine = [];
    private ushort _model;
    private byte _lid;
    private PodValue? _left, _right, _case;
    private DateTimeOffset _lastSeen;
    private DateTimeOffset _lockedAt;
    private double _rssi;
    private DateTimeOffset _findUntil;

    public PodTracker(IEnumerable<ushort> models, int lockRssi)
    {
        SetModels(models);
        LockRssi = lockRssi;
    }

    /// <summary>Signal strength (dBm) at or above which AirPods count as "right next to this PC".</summary>
    public int LockRssi { get; set; }

    public void SetModels(IEnumerable<ushort> models)
    {
        _models.Clear();
        _models.UnionWith(models);
    }

    public PodSnapshot? Snapshot =>
        _lastSeen == default ? null : new PodSnapshot(_model, _left, _right, _case, _lastSeen);

    public bool IsFinding(DateTimeOffset now) => now < _findUntil;

    /// <summary>For the next <paramref name="duration"/>, lock on to the first AirPods held right next to this PC.</summary>
    public void StartFinding(DateTimeOffset now, TimeSpan duration) => _findUntil = now + duration;

    public OfferResult Offer(ulong address, int rssi, PodAdvert ad, DateTimeOffset now)
    {
        if (!_models.Contains(ad.Model))
            return OfferResult.OtherModel;

        Forget(now);
        var heard = Hear(address, rssi, ad, now);
        var close = heard.IsClose(LockRssi);
        var finding = IsFinding(now);

        if (_mine.Contains(address))
        {
            Accept(address, rssi, ad, now);
            if (!(finding && close))
                return OfferResult.Known;
            Lock(now);
            return OfferResult.Locked;
        }

        var quiet = _lastSeen == default ? TimeSpan.MaxValue : now - _lastSeen;

        if (close && (finding || quiet >= RelockAfter))
        {
            if (!IsConsistent(ad))
                Reset();
            Accept(address, rssi, ad, now);
            Lock(now);
            return OfferResult.Locked;
        }

        if (close && now - _lockedAt <= SiblingWindow && IsConsistent(ad))
        {
            Accept(address, rssi, ad, now);
            return OfferResult.Sibling;
        }

        if (quiet >= FollowAfterQuiet && quiet <= FollowWindow && IsFollowCandidate(heard) &&
            !_heard.Any(other => other.Value != heard && now - other.Value.Last <= FollowAfterQuiet &&
                                 other.Value.Rssi > heard.Rssi && !_mine.Contains(other.Key) &&
                                 IsFollowCandidate(other.Value)))
        {
            Accept(address, rssi, ad, now);
            return OfferResult.Followed;
        }

        return OfferResult.NotMine;
    }

    /// <summary>Could this address be ours after an address change? It must have appeared around when ours went quiet.</summary>
    private bool IsFollowCandidate(Heard heard) =>
        heard.First >= _lastSeen - NewAddressWindow &&
        heard.Rssi >= Math.Max(FollowRssiFloor, _rssi - FollowRssiSlack) &&
        IsConsistent(heard.Ad);

    /// <summary>Does this broadcast look like the AirPods we last saw: same model and lid state, same battery levels?</summary>
    private bool IsConsistent(PodAdvert ad)
    {
        if (_lastSeen == default || ad.Model != _model || ad.Lid != _lid)
            return false;

        var compared = 0;
        foreach (var (seen, known) in new[] { (ad.Left, _left), (ad.Right, _right), (ad.Case, _case) })
        {
            if (seen is not int s || known is not PodValue k)
                continue;
            if (Math.Abs(s - k.Percent) > BatteryTolerance)
                return false;
            compared++;
        }
        return compared > 0;
    }

    private Heard Hear(ulong address, int rssi, PodAdvert ad, DateTimeOffset now)
    {
        if (!_heard.TryGetValue(address, out var heard))
            return _heard[address] = new Heard(now, rssi, ad);

        // Smooth signal strength while broadcasts keep coming; start afresh after a gap.
        if (now - heard.Last < FollowAfterQuiet)
        {
            heard.Rssi = heard.Rssi * 0.7 + rssi * 0.3;
            heard.Samples++;
        }
        else
        {
            heard.Rssi = rssi;
            heard.Samples = 1;
        }
        heard.Last = now;
        heard.Ad = ad;
        return heard;
    }

    private void Accept(ulong address, int rssi, PodAdvert ad, DateTimeOffset now)
    {
        _rssi = _lastSeen != default && now - _lastSeen < FollowAfterQuiet ? _rssi * 0.7 + rssi * 0.3 : rssi;
        _mine.Add(address);
        _lastSeen = now;
        _model = ad.Model;
        _lid = ad.Lid;
        if (ad.Left is int l) _left = new PodValue(l, ad.LeftCharging, now);
        if (ad.Right is int r) _right = new PodValue(r, ad.RightCharging, now);
        if (ad.Case is int c) _case = new PodValue(c, ad.CaseCharging, now);
    }

    private void Lock(DateTimeOffset now)
    {
        _lockedAt = now;
        _findUntil = default;
    }

    private void Reset()
    {
        _mine.Clear();
        _left = _right = _case = null;
        _lastSeen = default;
    }

    private void Forget(DateTimeOffset now)
    {
        foreach (var (address, heard) in _heard)
        {
            if (now - heard.Last > ForgetAfter)
            {
                _heard.Remove(address);
                _mine.Remove(address);
            }
        }
    }
}
