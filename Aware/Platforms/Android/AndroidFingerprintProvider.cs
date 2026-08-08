using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
// Both Android.Bluetooth.LE and Android.Net.Wifi define ScanResult, and this file
// reads both radios.
using LeScanResult = Android.Bluetooth.LE.ScanResult;
using Android.Content.PM;
using Android.Hardware;
using Android.Net.Wifi;
using Aware.Application;
using Aware.Domain;

namespace Aware.Platform;

/// <summary>
/// Reads the room fingerprint from the phone's ambient sensors.
///
/// <para>Every signal is optional. A device without a barometer, or a user who
/// declines the Wi-Fi permission, contributes fewer signals and the matcher
/// renormalizes — nothing here throws its way out to the caller.</para>
///
/// <para>07-DATA-PRIVACY governs what leaves this class: Wi-Fi becomes a hash of
/// the access-point set, never the network names, and no raw scan or position is
/// retained. Native Android types stop at this boundary.</para>
/// </summary>
public sealed class AndroidFingerprintProvider : IRoomFingerprintProvider
{
    private const int SensorTimeoutMs = 2500;
    private const int PermissionWaitMs = 20_000;

    /// <summary>
    /// Only the strongest access points are hashed. The long tail of weak,
    /// intermittent APs would change the hash between two readings taken in the
    /// same spot and make the room look unrecognized.
    /// </summary>
    private const int AccessPointsHashed = 8;

    /// <summary>
    /// Persisted rather than held in a static, so the prompt happens once per
    /// install instead of once per launch. A declined permission asking again on
    /// every cold start is the behaviour this replaces.
    /// </summary>
    /// <summary>
    /// Versioned. The ask happens once per install, so widening the request to
    /// include BLUETOOTH_SCAN needs a new key — otherwise every existing install
    /// has already burned its single ask on Wi-Fi alone and would never be
    /// offered the signal that replaced it.
    /// </summary>
    private const string PermissionAskedKey = "aware.signals.permission.asked.v2";

    /// <summary>
    /// How long the BLE scan listens. Runs concurrently with the three sensor
    /// reads rather than after them, so it costs no extra launch time.
    /// </summary>
    private const int BluetoothScanMs = 2500;

    /// <summary>Same reasoning as <see cref="AccessPointsHashed"/>.</summary>
    private const int BluetoothDevicesHashed = 8;

    private readonly ILogger<AndroidFingerprintProvider> _log;
    private readonly Context? _context;
    private readonly SensorManager? _sensors;

    public AndroidFingerprintProvider(ILogger<AndroidFingerprintProvider> log)
    {
        _log = log;
        _context = Uno.UI.ContextHelper.Current;
        _sensors = _context?.GetSystemService(Context.SensorService) as SensorManager;
    }

    public bool IsAvailable => _sensors is not null;

    public string Summary
    {
        get
        {
            if (_sensors is null) return "No ambient sensors on this device";

            var present = new List<string>();
            if (_sensors.GetDefaultSensor(SensorType.MagneticField) is not null) present.Add("magnetometer");
            if (_sensors.GetDefaultSensor(SensorType.Light) is not null) present.Add("light");
            if (_sensors.GetDefaultSensor(SensorType.Pressure) is not null) present.Add("pressure");
            if (HasBluetoothPermission) present.Add("Bluetooth");
            if (HasWifiPermission) present.Add("Wi-Fi");

            return present.Count == 0
                ? "No ambient sensors on this device"
                : $"Room signals: {string.Join(", ", present)}";
        }
    }

    public async Task<FingerprintReading> ReadAsync(CancellationToken ct)
    {
        if (_sensors is null) return FingerprintReading.Unavailable;

        var signals = new List<FingerprintSignal>();

        await EnsureWifiPermissionAsync(ct);

        // Started before the sensors and awaited after them: the scan listens for
        // 2.5 s, which is roughly what the three sensor reads take anyway, so
        // overlapping them keeps launch the same length as before Bluetooth.
        var bluetooth = ReadBluetoothHashAsync(ct);

        var magnetic = await ReadVectorAsync(SensorType.MagneticField, ct);
        signals.Add(new FingerprintSignal("Magnetic signature",
            magnetic is { } m ? $"{m.Length():0.0} µT" : "Sensor unavailable",
            magnetic is not null));

        var light = await ReadScalarAsync(SensorType.Light, ct);
        signals.Add(new FingerprintSignal("Ambient light",
            light is { } l ? $"{l:0} lux" : "Sensor unavailable",
            light is not null));

        var pressure = await ReadScalarAsync(SensorType.Pressure, ct);
        signals.Add(new FingerprintSignal("Air pressure",
            pressure is { } p ? $"{p:0.0} hPa" : "Sensor unavailable",
            pressure is not null));

        var (wifiHash, apCount) = ReadWifiHash();
        signals.Add(new FingerprintSignal("Wi-Fi neighbourhood",
            wifiHash is null
                ? (HasWifiPermission ? "No access points in range" : "Permission not granted")
                : $"{apCount} access points, hashed",
            wifiHash is not null));

        var (btHash, btCount, btReason) = await bluetooth;
        signals.Add(new FingerprintSignal("Bluetooth neighbourhood",
            btHash is null ? btReason : $"{btCount} devices, hashed",
            btHash is not null));

        var fingerprint = new RoomFingerprint(
            WifiFeatureHash: wifiHash ?? string.Empty,
            BluetoothFeatureHash: btHash ?? string.Empty,
            // Lux is carried in X; the domain models light as a vector for a
            // future directional reading, which no phone sensor provides today.
            AmbientLightVector: new Vector3(light ?? 0f, 0f, 0f),
            MagneticVector: magnetic ?? Vector3.Zero,
            PressureHpa: pressure ?? 0f,
            AcousticEmbeddingId: null);

        return new FingerprintReading(fingerprint, signals, DateTimeOffset.Now);
    }

    // -- sensors -----------------------------------------------------------

    private async Task<Vector3?> ReadVectorAsync(SensorType type, CancellationToken ct)
    {
        var values = await ReadAsync(type, ct);
        return values is { Length: >= 3 } ? new Vector3(values[0], values[1], values[2]) : null;
    }

    private async Task<float?> ReadScalarAsync(SensorType type, CancellationToken ct)
    {
        var values = await ReadAsync(type, ct);
        return values is { Length: >= 1 } ? values[0] : null;
    }

    private async Task<float[]?> ReadAsync(SensorType type, CancellationToken ct)
    {
        var sensor = _sensors?.GetDefaultSensor(type);
        if (sensor is null || _sensors is null) return null;

        using var listener = new FirstReadingListener();

        if (!_sensors.RegisterListener(listener, sensor, SensorDelay.Normal))
            return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SensorTimeoutMs);

            return await listener.Reading.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A sensor that never fires within the window is treated as absent.
            _log.LogDebug("Sensor {Type} produced no reading within {Timeout} ms.", type, SensorTimeoutMs);
            return null;
        }
        finally
        {
            _sensors.UnregisterListener(listener);
        }
    }

    private sealed class FirstReadingListener : Java.Lang.Object, ISensorEventListener
    {
        private readonly TaskCompletionSource<float[]> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<float[]> Reading => _first.Task;

        public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy) { }

        public void OnSensorChanged(SensorEvent? e)
        {
            if (e?.Values is { Count: > 0 } values)
                _first.TrySetResult([.. values]);
        }
    }

    // -- Wi-Fi -------------------------------------------------------------

    /// <summary>
    /// Android 13 replaced the location permission for Wi-Fi scanning with a
    /// dedicated one; older releases still gate scan results behind location.
    /// </summary>
    private static string WifiPermission => OperatingSystem.IsAndroidVersionAtLeast(33)
        ? "android.permission.NEARBY_WIFI_DEVICES"
        : Android.Manifest.Permission.AccessFineLocation;

    /// <summary>
    /// Whether the one prompt has already been shown, surviving process restarts.
    /// Storage being unavailable falls back to asking next launch, which is the
    /// old behaviour rather than a new failure mode.
    /// </summary>
    private static bool HasAskedForPermission
    {
        get
        {
            try
            {
                return Windows.Storage.ApplicationData.Current.LocalSettings.Values
                    .TryGetValue(PermissionAskedKey, out var value) && value is true;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                Windows.Storage.ApplicationData.Current.LocalSettings
                    .Values[PermissionAskedKey] = value;
            }
            catch
            {
                // Non-fatal: the prompt simply reappears on the next launch.
            }
        }
    }

    private bool HasWifiPermission
    {
        get
        {
            if (_context is null) return false;

            // Runtime permissions arrived in API 23; before that a declared
            // permission is granted at install time.
            if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return true;

            return _context.CheckSelfPermission(WifiPermission) == Permission.Granted;
        }
    }

    /// <summary>
    /// Asks exactly once per install, then never again. The grant lands on the
    /// activity's own callback, so rather than thread that back here the read
    /// waits for the state to flip, and gives up if the user ignores the prompt.
    ///
    /// <para>Everything after that first ask is automatic:
    /// <see cref="HasWifiPermission"/> is read live on every reading, so a user
    /// who declines and later grants it in system settings starts contributing
    /// the Wi-Fi signal on the next recognition with no second prompt. Declining
    /// simply means one fewer signal, and the matcher renormalizes.</para>
    /// </summary>
    private async Task EnsureWifiPermissionAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return;
        if ((HasWifiPermission && HasBluetoothPermission) || HasAskedForPermission) return;

        if (Uno.UI.ContextHelper.Current is not Android.App.Activity activity)
            return;

        // Recorded before the prompt, and only once an activity exists to show
        // it — a launch with no activity must not burn the single ask.
        HasAskedForPermission = true;

        // Asked together in one prompt rather than in two sequential dialogs.
        // Either can be declined on its own; each simply removes one signal.
        var requested = new List<string> { WifiPermission };
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            requested.Add("android.permission.BLUETOOTH_SCAN");

        try
        {
            activity.RequestPermissions([.. requested], requestCode: 4711);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not request the Wi-Fi scan permission.");
            return;
        }

        var deadline = Environment.TickCount64 + PermissionWaitMs;
        while (Environment.TickCount64 < deadline && !HasBluetoothPermission && !HasWifiPermission)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
        }
    }

    // -- bluetooth ---------------------------------------------------------

    /// <summary>
    /// BLUETOOTH_SCAN is declared <c>neverForLocation</c>, which is what lets this
    /// work at all: unlike Wi-Fi scan results, it is not gated behind a location
    /// permission the app deliberately does not hold. It is therefore the only
    /// set-shaped signal Aware can still read on Android 13+.
    ///
    /// <para><strong>Names, not addresses.</strong> BLE advertising addresses are
    /// resolvable private addresses on most modern devices and rotate every few
    /// minutes, so hashing them would change the room's identity while the phone
    /// sat still. Advertised names do not rotate, and named advertisers are mostly
    /// the fixed appliances that make a room what it is — a television, a speaker,
    /// a thermostat. This is also exactly what 07-DATA-PRIVACY specifies:
    /// "Wi-Fi/Bluetooth hashes over device names". Unnamed advertisers are
    /// skipped rather than hashed.</para>
    /// </summary>
    private async Task<(string? Hash, int Count, string Reason)> ReadBluetoothHashAsync(
        CancellationToken ct)
    {
        // BLUETOOTH_SCAN with neverForLocation arrived in API 31. Below that a
        // scan needs location, which this app does not ask for, so the signal is
        // simply absent and the matcher renormalizes.
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
            return (null, 0, "Needs Android 12 or newer");

        if (!HasBluetoothPermission) return (null, 0, "Permission not granted");

        BluetoothLeScanner? scanner = null;
        LeScanCollector? collector = null;

        try
        {
            if (_context?.GetSystemService(Context.BluetoothService) is not BluetoothManager manager)
                return (null, 0, "No Bluetooth on this device");

            var adapter = manager.Adapter;
            if (adapter is null) return (null, 0, "No Bluetooth on this device");
            if (!adapter.IsEnabled) return (null, 0, "Bluetooth is off");

            scanner = adapter.BluetoothLeScanner;
            if (scanner is null) return (null, 0, "Bluetooth is off");

            collector = new LeScanCollector();
            scanner.StartScan(collector);

            await Task.Delay(BluetoothScanMs, ct);

            var names = collector.Strongest(BluetoothDevicesHashed);
            if (names.Count == 0) return (null, 0, "No named devices in range");

            // One hash per device, joined — not a single hash over the set.
            // Measured on a Pixel 8: hashing the set as a blob gave this kitchen
            // two different fingerprints four minutes apart, because advertisers
            // drop in and out between scans. Per-device tokens let the matcher
            // score partial overlap instead of treating one absent speaker as a
            // different room. Each name is still hashed before it is stored, so
            // no device name is retained (07-DATA-PRIVACY).
            var tokens = names.Select(HashIdentifier);

            return (
                string.Join(FingerprintMatcher.FingerprintSetSeparator, tokens),
                names.Count,
                string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not scan for Bluetooth devices.");
            return (null, 0, "Scan unavailable");
        }
        finally
        {
            // Leaving a scan running drains the battery long after the reading.
            if (scanner is not null && collector is not null)
            {
                try { scanner.StopScan(collector); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not stop the Bluetooth scan."); }
            }
        }
    }

    /// <summary>
    /// One short, opaque token per identifier. Truncated for the same reason the
    /// Wi-Fi digest is: long enough to distinguish devices, too short to be worth
    /// reversing, and readable in an exported model.
    /// </summary>
    private static string HashIdentifier(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10].ToLowerInvariant();

    /// <summary>
    /// Collects advertised names and the strongest signal seen for each. Callbacks
    /// arrive on a binder thread, so the dictionary is guarded.
    /// </summary>
    private sealed class LeScanCollector : ScanCallback
    {
        private readonly Dictionary<string, int> _strongest = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public override void OnScanResult(ScanCallbackType callbackType, LeScanResult? result) =>
            Record(result);

        public override void OnBatchScanResults(IList<LeScanResult>? results)
        {
            if (results is null) return;
            foreach (var result in results) Record(result);
        }

        private void Record(LeScanResult? result)
        {
            var name = result?.ScanRecord?.DeviceName;
            if (string.IsNullOrWhiteSpace(name)) return;

            name = name.Trim();

            lock (_gate)
            {
                if (!_strongest.TryGetValue(name, out var rssi) || result!.Rssi > rssi)
                    _strongest[name] = result!.Rssi;
            }
        }

        /// <summary>
        /// The strongest few, then sorted by name so the hash does not depend on
        /// the order advertisements happened to arrive in.
        /// </summary>
        public IReadOnlyList<string> Strongest(int count)
        {
            lock (_gate)
            {
                return [.. _strongest
                    .OrderByDescending(pair => pair.Value)
                    .Take(count)
                    .Select(pair => pair.Key)
                    .OrderBy(name => name, StringComparer.Ordinal)];
            }
        }
    }

    private bool HasBluetoothPermission
    {
        get
        {
            if (_context is null) return false;
            if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return false;

            return _context.CheckSelfPermission("android.permission.BLUETOOTH_SCAN")
                   == Permission.Granted;
        }
    }

    private (string? Hash, int Count) ReadWifiHash()
    {
        if (!HasWifiPermission) return (null, 0);

        try
        {
            if (_context?.GetSystemService(Context.WifiService) is not WifiManager wifi)
                return (null, 0);

            var results = wifi.ScanResults;
            if (results is null || results.Count == 0) return (null, 0);

            // Sorted so the hash does not depend on scan order, and truncated to
            // the strongest so a distant AP flickering in and out cannot change
            // the room's identity.
            var identifiers = results
                .OrderByDescending(r => r.Level)
                .Take(AccessPointsHashed)
                .Select(r => r.Bssid)
                .Where(b => !string.IsNullOrEmpty(b))
                .Select(b => b!.ToLowerInvariant())
                .OrderBy(b => b, StringComparer.Ordinal)
                .ToArray();

            if (identifiers.Length == 0) return (null, 0);

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', identifiers)));

            // Truncated: enough to distinguish rooms, too short to be worth
            // reversing, and readable in an exported model.
            return ($"wifi-{Convert.ToHexString(digest)[..12].ToLowerInvariant()}", identifiers.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read Wi-Fi scan results.");
            return (null, 0);
        }
    }
}
