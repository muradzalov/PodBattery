using System.Text.Json;
using Microsoft.Win32;

namespace PodBattery;

/// <summary>Settings in %APPDATA%\PodBattery\settings.json, created with defaults on first run.</summary>
internal sealed class Settings
{
    // Forgiving about hand edits: any capitalisation, comments and trailing commas are fine.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PodBattery");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>
    /// Signal strength (dBm) at which AirPods count as "right next to this PC" for locking on.
    /// Closer to 0 = stricter. Raise it (e.g. -45) if it ever locks on to a neighbour's AirPods.
    /// </summary>
    public int LockRssi { get; set; } = -50;

    /// <summary>Write every AirPods broadcast and the decision made about it to adverts.log (for troubleshooting).</summary>
    public bool Log { get; set; }

    public static (Settings Settings, bool FirstRun) Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings(), false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            return (new Settings(), false);
        }

        // First run: write the defaults so they can be edited. Only call it a first run if that worked,
        // so an unwritable profile doesn't show the setup prompt on every launch.
        var settings = new Settings();
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
            return (settings, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (settings, false);
        }
    }
}

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PodBattery";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>Optional troubleshooting log of AirPods broadcasts (only when Log is on in settings.json).</summary>
internal sealed class AdvertLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private const long MaxBytes = 20 * 1024 * 1024;
    private bool _closed;

    public AdvertLog()
    {
        Directory.CreateDirectory(Settings.Folder);
        var path = Path.Combine(Settings.Folder, "adverts.log");
        var append = File.Exists(path) && new FileInfo(path).Length < MaxBytes;
        _writer = new StreamWriter(path, append) { AutoFlush = true };
        if (!append)
            _writer.WriteLine("time,address,rssi,result,data");
    }

    public void Write(ulong address, int rssi, OfferResult result, byte[] data)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _writer.WriteLine($"{DateTime.Now:MM-dd HH:mm:ss.f},{address:X12},{rssi},{result},{Convert.ToHexString(data)}");

            // It's for troubleshooting: stop rather than fill the disk if it's left on for weeks.
            if (_writer.BaseStream.Length > MaxBytes)
                _closed = true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _writer.Dispose();
        }
    }
}
