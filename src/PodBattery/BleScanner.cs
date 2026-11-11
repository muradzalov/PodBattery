using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;

namespace PodBattery;

/// <summary>Listens (passively, receive-only) for AirPods status broadcasts using Windows' built-in Bluetooth LE API.</summary>
internal sealed class BleScanner : IDisposable
{
    private const byte ManufacturerSpecificData = 0xFF;

    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Passive };
    private bool _startFailed;

    public BleScanner()
    {
        // Have Windows drop everything except Apple (0x004C, little-endian) proximity-pairing (0x07) messages.
        // Without this, a busy office delivers ~100+ unrelated broadcasts a second, which costs real CPU.
        _watcher.AdvertisementFilter.BytePatterns.Add(new BluetoothLEAdvertisementBytePattern(
            ManufacturerSpecificData, 0, new byte[] { 0x4C, 0x00, 0x07 }.AsBuffer()));
        // At most one report per device every 2 seconds: plenty for battery levels, and fewer wake-ups.
        _watcher.SignalStrengthFilter.SamplingInterval = TimeSpan.FromSeconds(2);
        _watcher.Received += OnReceived;
    }

    /// <summary>Raised on a background thread: address, signal strength (dBm), decoded message, raw bytes.</summary>
    public event Action<ulong, int, PodAdvert, byte[]>? Advert;

    /// <summary>False once Windows has stopped the scan, e.g. because Bluetooth is off.</summary>
    public bool IsRunning => !_startFailed && _watcher.Status is not
        (BluetoothLEAdvertisementWatcherStatus.Aborted or BluetoothLEAdvertisementWatcherStatus.Stopped);

    public void Start()
    {
        try
        {
            _watcher.Start();
        }
        catch (Exception) // Thrown when there's no usable Bluetooth radio; IsRunning reports it.
        {
            _startFailed = true;
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        // About 60 s after a device stops broadcasting, Windows reports it once more at -127 dBm with its last
        // payload. That's "gone out of range", not a broadcast, so it must not count as hearing the AirPods.
        if (e.RawSignalStrengthInDBm <= PodTracker.OutOfRange)
            return;

        foreach (var section in e.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != ProximityPairing.AppleCompanyId)
                continue;

            var bytes = section.Data.ToArray();
            if (ProximityPairing.TryParse(bytes) is { } ad)
                Advert?.Invoke(e.BluetoothAddress, e.RawSignalStrengthInDBm, ad, bytes);
        }
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        try
        {
            // Status still reads Created for a moment after Start(), so stop in that state too.
            if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created)
                _watcher.Stop();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }
}
