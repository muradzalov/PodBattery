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
/// <item><b>Lock on</b> to a case opened right next to the PC (both earbuds in the case, lid open, very strong
/// signal) once yours haven't been heard for <see cref="RelockAfter"/>, or when the current identity only came from
/// following. AirPods in someone's ears never trigger this. In Find mode (<see cref="StartFinding"/>), any AirPods
/// held next to the PC with a consistently very strong signal count. Shortly after locking, a second strong,
/// matching address is your other earbud.</item>
/// <item><b>Follow</b> a change of address. In recordings, a pair's new address starts broadcasting within about
/// 0.5-30 s of the old one's last broadcast. So once yours has been quiet for <see cref="FollowAfterQuiet"/>, the
/// strongest address that <em>first appeared in that moment</em>, with matching battery levels and lid state and a
/// similar signal strength, is yours. Addresses that were around before can't be.</item>
/// </list>
/// Windows also reports an address once more, with RSSI -127 and its last payload, about 60 s after it stops; that
/// isn't a broadcast, so it is ignored.
/// </remarks>
public sealed class PodTracker
{
    /// <summary>Reception gaps of 10-14 s happen while you're wearing them, so wait longer than that.</summary>
    public static readonly TimeSpan FollowAfterQuiet = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan FollowWindow = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan NewAddressBefore = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan NewAddressAfter = TimeSpan.FromSeconds(40);
    public static readonly TimeSpan RelockAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SiblingWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(20);
    public const int OutOfRange = -127;
    private const int LockSamples = 3;
    private const int FollowRssiFloor = -85;
    private const int FollowRssiSlack = 8;
    private const int FollowSamples = 2;
    private const int BatteryTolerance = 10;
    private static readonly TimeSpan SmoothingGap = TimeSpan.FromSeconds(10);

    /// <summary>What we know about one broadcasting address of a watched model.</summary>
    private sealed class Heard(DateTimeOffset now, int rssi, PodAdvert ad)
    {
        public DateTimeOffset First { get; } = now;
        public DateTimeOffset Last { get; set; } = now;
        /// <summary>Smoothed signal strength, over <see cref="Samples"/> broadcasts without a long gap.</summary>
        public double Rssi { get; set; } = rssi;
        public int Samples { get; set; } = 1;
        public PodAdvert Ad { get; set; } = ad;

        /// <summary>Consistently strong, not a spike: a neighbour's AirPods can briefly read a few dB from the threshold.</summary>
        public bool IsClose(int lockRssi) => Samples >= LockSamples && Rssi >= lockRssi;
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
    /// <summary>The current identity came from following an address change, not from being held next to the PC.</summary>
    private bool _followed;

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
        if (rssi <= OutOfRange)
            return _mine.Contains(address) ? OfferResult.Known : OfferResult.NotMine; // not a broadcast: nothing to learn

        Forget(now);
        var heard = Hear(address, rssi, ad, now);
        var finding = IsFinding(now);
        // On its own, lock on only to a case opened right next to the PC: AirPods in someone's ears never qualify,
        // however close they get. In Find mode you're deliberately holding yours there, in any state, so a sustained
        // strong signal is enough.
        var caseOpenedHere = ad.CaseOpen && rssi >= LockRssi && heard.Rssi >= LockRssi;
        var heldHere = heard.IsClose(LockRssi);
        var close = caseOpenedHere || (finding && heldHere);

        if (_mine.Contains(address))
        {
            if (finding && close)
            {
                Reset(); // "these are mine": drop anything followed before
                Accept(address, rssi, ad, now);
                Lock(now);
                return OfferResult.Locked;
            }
            Accept(address, rssi, ad, now);
            return OfferResult.Known;
        }

        var quiet = _lastSeen == default ? TimeSpan.MaxValue : now - _lastSeen;

        if (close && (finding || _followed || quiet >= RelockAfter))
        {
            if (finding || _followed || !IsConsistent(ad))
                Reset();
            Accept(address, rssi, ad, now);
            Lock(now);
            return OfferResult.Locked;
        }

        if ((caseOpenedHere || heldHere) && now - _lockedAt <= SiblingWindow && IsConsistent(ad))
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
            _followed = true;
            return OfferResult.Followed;
        }

        return OfferResult.NotMine;
    }

    /// <summary>Could this address be ours after an address change? It must have first appeared right around when ours went quiet.</summary>
    private bool IsFollowCandidate(Heard heard) =>
        heard.Samples >= FollowSamples &&
        heard.First >= _lastSeen - NewAddressBefore &&
        heard.First <= _lastSeen + NewAddressAfter &&
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
        if (now - heard.Last < SmoothingGap)
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
        _rssi = _lastSeen != default && now - _lastSeen < SmoothingGap ? _rssi * 0.7 + rssi * 0.3 : rssi;
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
        _followed = false;
    }

    private void Reset()
    {
        _mine.Clear();
        _left = _right = _case = null;
        _lastSeen = default;
        _followed = false;
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
