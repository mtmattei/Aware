using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Android.Content;
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

    private static bool _permissionRequested;

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

        var fingerprint = new RoomFingerprint(
            WifiFeatureHash: wifiHash ?? string.Empty,
            BluetoothFeatureHash: string.Empty,
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
    /// Asks once per process. The grant lands on the activity's own callback, so
    /// rather than thread that back here the read simply waits for the state to
    /// flip, and gives up if the user ignores or declines the prompt.
    /// </summary>
    private async Task EnsureWifiPermissionAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return;
        if (HasWifiPermission || _permissionRequested) return;
        _permissionRequested = true;

        if (Uno.UI.ContextHelper.Current is not Android.App.Activity activity)
            return;

        try
        {
            activity.RequestPermissions([WifiPermission], requestCode: 4711);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not request the Wi-Fi scan permission.");
            return;
        }

        var deadline = Environment.TickCount64 + PermissionWaitMs;
        while (Environment.TickCount64 < deadline && !HasWifiPermission)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
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
