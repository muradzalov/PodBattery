using System.Reflection;

namespace PodBattery;

/// <summary>The notification-area icon, its tooltip and its menu.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FindFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryScannerEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecycleSilentScannerAfter = TimeSpan.FromMinutes(5);

    private static readonly Color Green = Color.FromArgb(0x2E, 0x7D, 0x32);
    private static readonly Color Amber = Color.FromArgb(0xF9, 0xA8, 0x25);
    private static readonly Color Red = Color.FromArgb(0xC6, 0x28, 0x28);
    private static readonly Color Grey = Color.FromArgb(0x6B, 0x6B, 0x6B);

    private readonly Lock _gate = new();
    private readonly PodTracker _tracker;
    private readonly AdvertLog? _log;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _left = new("Left");
    private readonly ToolStripMenuItem _right = new("Right");
    private readonly ToolStripMenuItem _case = new("Case");
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _startup;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly DateTimeOffset _launchedAt = Clock.Now;
    private BleScanner? _scanner;
    private DateTimeOffset _scannerStartedAt;
    private DateTimeOffset _nextScannerRetry;
    private long _lastAdvertTicks;
    private int _resumedFromSleep;
    private int _lockedSinceLastTick;
    private bool _announceNextLock;
    private bool _iconReshown;
    private string _iconKey = "";

    public TrayApp(bool forceLog)
    {
        var (settings, firstRun) = Settings.Load();
        _tracker = new PodTracker(ModelsToWatch(), settings.LockRssi);
        if (settings.Log || forceLog)
        {
            try
            {
                _log = new AdvertLog();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(ex);
            }
        }
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _startup = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = StartWithWindows.Enabled,
        };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _left, _right, _case, _status,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Find my AirPods…", null, (_, _) => StartFinding()),
            _startup,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        });

        _menu.Opening += (_, _) => UpdateMenu(Current());
        _tray = new NotifyIcon { ContextMenuStrip = _menu, Text = "PodBattery", Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowMenu();
        };

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();

        if (firstRun)
            StartFinding();
    }

    /// <summary>The models paired with this PC; if none are paired, any AirPods model.</summary>
    private static IEnumerable<ushort> ModelsToWatch()
    {
        var paired = PairedAppleDevices.ModelIds();
        return paired.Count > 0 ? paired : ProximityPairing.KnownModels.Keys;
    }

    private void StartFinding()
    {
        var models = ModelsToWatch(); // a device query: keep it outside the lock the Bluetooth thread uses
        lock (_gate)
        {
            _tracker.SetModels(models);
            _tracker.StartFinding(Clock.Now, FindFor);
        }
        _announceNextLock = true;
        _tray.ShowBalloonTip(5000, "Finding your AirPods", "Open your AirPods case right next to this PC.", ToolTipIcon.Info);
        Tick();
    }

    /// <summary>Called on a Bluetooth background thread for every AirPods broadcast nearby.</summary>
    private void OnAdvert(ulong address, int rssi, PodAdvert ad, byte[] data)
    {
        var now = Clock.Now;
        Interlocked.Exchange(ref _lastAdvertTicks, now.UtcTicks);

        OfferResult result;
        lock (_gate)
            result = _tracker.Offer(address, rssi, ad, now);

        if (result != OfferResult.OtherModel)
            _log?.Write(address, rssi, result, data);
        if (result == OfferResult.Locked)
            Interlocked.Exchange(ref _lockedSinceLastTick, 1);
    }

    /// <summary>Everything the icon, tooltip and menu show, read at one moment.</summary>
    private readonly record struct View(PodSnapshot? Pods, bool Fresh, bool Finding, bool BluetoothOff, DateTimeOffset Now);

    private View Current()
    {
        var now = Clock.Now;
        PodSnapshot? pods;
        bool finding;
        lock (_gate)
        {
            pods = _tracker.Snapshot;
            finding = _tracker.IsFinding(now);
        }
        var fresh = pods is not null && now - pods.LastSeen <= FreshFor;
        return new View(pods, fresh, finding, _scanner is not { IsRunning: true }, now);
    }

    private void Tick()
    {
        EnsureScanner(Clock.Now);
        var view = Current();

        // If Explorer was still starting up at login, adding the icon can silently fail; add it again once.
        if (!_iconReshown && view.Now - _launchedAt > TimeSpan.FromSeconds(10))
        {
            _iconReshown = true;
            _tray.Visible = false;
            _tray.Visible = true;
        }

        if (Interlocked.Exchange(ref _lockedSinceLastTick, 0) == 1 && _announceNextLock && view.Pods is { } pods)
        {
            _announceNextLock = false;
            _tray.ShowBalloonTip(4000, $"Found your {ProximityPairing.ModelName(pods.Model)}", Summary(pods), ToolTipIcon.Info);
        }
        if (!view.Finding)
            _announceNextLock = false;

        UpdateIcon(view);
        UpdateTooltip(view);
        if (_menu.Visible)
            UpdateMenu(view);
    }

    /// <summary>
    /// Starts scanning, and restarts it if Windows stopped it (Bluetooth switched off and on), after the PC wakes
    /// from sleep, or if it has gone completely silent for a while (a scan can stop delivering without saying so).
    /// </summary>
    private void EnsureScanner(DateTimeOffset now)
    {
        var lastAdvert = new DateTimeOffset(Interlocked.Read(ref _lastAdvertTicks), TimeSpan.Zero);
        var silentSince = lastAdvert > _scannerStartedAt ? lastAdvert : _scannerStartedAt;
        var recycle = Interlocked.Exchange(ref _resumedFromSleep, 0) == 1 ||
                      (_scanner is { IsRunning: true } && now - silentSince > RecycleSilentScannerAfter);

        if (!recycle && (_scanner is { IsRunning: true } || now < _nextScannerRetry))
            return;

        _nextScannerRetry = now + RetryScannerEvery;
        _scannerStartedAt = now;
        _scanner?.Dispose();
        _scanner = new BleScanner();
        _scanner.Advert += OnAdvert;
        _scanner.Start();
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            Interlocked.Exchange(ref _resumedFromSleep, 1);
    }

    private void UpdateIcon(View view)
    {
        var level = Lowest(view.Pods);
        var background = !view.Fresh || level is null ? Grey : level >= 50 ? Green : level >= 20 ? Amber : Red;
        var foreground = background == Amber ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
        var text = level?.ToString() ?? "?";

        var key = $"{text}|{background.ToArgb()}";
        if (key == _iconKey)
            return;

        var previous = _tray.Icon;
        _tray.Icon = IconRenderer.Render(text, background, foreground);
        _iconKey = key;
        previous?.Dispose();
    }

    private void UpdateTooltip(View view)
    {
        var tip = view.BluetoothOff ? "PodBattery\nBluetooth is off"
            : view.Finding ? "PodBattery\nOpen your AirPods case next to this PC"
            : view.Pods is not { } pods ? "PodBattery\nUse “Find my AirPods…” to set up"
            : $"{ProximityPairing.ModelName(pods.Model)}{(view.Fresh ? "" : " (not in range)")}\n{Summary(pods)}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private void UpdateMenu(View view)
    {
        var pods = view.Pods;
        foreach (var (item, value) in new[] { (_left, pods?.Left), (_right, pods?.Right), (_case, pods?.Case) })
        {
            item.Visible = pods is not null;
            item.ShortcutKeyDisplayString = Describe(value);
        }

        _status.Text = view.BluetoothOff ? "Bluetooth is off"
            : view.Finding ? "Looking… open your AirPods case next to this PC"
            : pods is null ? "Not found yet – use “Find my AirPods…”"
            : view.Fresh ? $"{ProximityPairing.ModelName(pods.Model)} · updated {Ago(view.Now - pods.LastSeen)}"
            : $"Not in range · last seen {LastSeen(pods.LastSeen, view.Now)}";
    }

    private static int? Lowest(PodSnapshot? pods)
    {
        int? left = pods?.Left?.Percent, right = pods?.Right?.Percent;
        return left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);
    }

    private static string Describe(PodValue? value) =>
        value is { } v ? $"{v.Percent}%{(v.Charging ? "  ⚡ charging" : "")}" : "—";

    private static string Summary(PodSnapshot pods)
    {
        static string Part(string name, PodValue? value) =>
            value is { } v ? $"{name} {v.Percent}%{(v.Charging ? " ⚡" : "")}" : $"{name} —";

        return $"{Part("Left", pods.Left)}   {Part("Right", pods.Right)}\n{Part("Case", pods.Case)}";
    }

    private static string Ago(TimeSpan elapsed) =>
        elapsed < TimeSpan.FromSeconds(5) ? "just now" : $"{(int)elapsed.TotalSeconds}s ago";

    private static string LastSeen(DateTimeOffset seen, DateTimeOffset now) =>
        seen.Date == now.Date ? seen.ToString("t") : seen.ToString("g");

    private void ShowMenu() =>
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);

    private void ToggleStartup()
    {
        try
        {
            StartWithWindows.Enabled = !StartWithWindows.Enabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _tray.ShowBalloonTip(4000, "PodBattery", "Couldn't change the startup setting: " + ex.Message, ToolTipIcon.Warning);
        }
        _startup.Checked = StartWithWindows.Enabled;
    }

    protected override void ExitThreadCore()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Dispose();
        _scanner?.Dispose();
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        _tray.Dispose();
        _menu.Dispose();
        _log?.Dispose();
        base.ExitThreadCore();
    }
}
