using System.Numerics;
using Aware.Application;
using Aware.Domain;

namespace Aware.Platform;

/// <summary>
/// The sensing counterpart to <see cref="SimulationCaptureAdapter"/>: stands in
/// for ambient sensors on platforms that have none, so the link and recognition
/// flows can be exercised and demonstrated without a phone in hand.
///
/// <para><strong>Opt-in only.</strong> Without the environment variable the
/// registration stays <see cref="NullFingerprintProvider"/>, so desktop and the
/// browser still report no sensors and settle on the brief's
/// "98% confident · stable room model". Simulating by default would quietly
/// break the capability-tier promise in the README, which is the one thing the
/// tiers exist to keep honest.</para>
///
/// <list type="bullet">
///   <item><c>AWARE_SIMULATE_SENSORS=1</c> — a consistent place. Link a room
///   here and every later reading matches it.</item>
///   <item><c>AWARE_SIMULATE_SENSORS=elsewhere</c> — a different place, so a
///   room linked in the first mode reports a contradiction. This is the path
///   that is otherwise only reachable by physically walking out of the room.</item>
/// </list>
/// </summary>
public sealed class SimulatedFingerprintProvider : IRoomFingerprintProvider
{
    public const string EnvironmentVariable = "AWARE_SIMULATE_SENSORS";

    private readonly bool _elsewhere;

    public SimulatedFingerprintProvider(string? mode) =>
        _elsewhere = string.Equals(mode, "elsewhere", StringComparison.OrdinalIgnoreCase);

    public static string? Mode => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(Mode);

    /// <summary>
    /// Values are fixed rather than jittered. A reading that drifts would make a
    /// match look convincing while making it impossible to tell a real
    /// regression from noise.
    /// </summary>
    private static readonly RoomFingerprint Here = new(
        WifiFeatureHash: "wifi-sim-here",
        BluetoothFeatureHash: "bt-sim-here",
        AmbientLightVector: new Vector3(180f, 0f, 0f),
        MagneticVector: new Vector3(22.5f, -8.1f, 41.2f),
        PressureHpa: 1013.2f,
        AcousticEmbeddingId: null);

    /// <summary>
    /// Genuinely elsewhere under the measured matcher, not merely different
    /// numbers: the field strength differs by more than the magnetic tolerance,
    /// and the pressure gap is wider than a day of weather rather than the
    /// storey-sized step it used to be.
    /// </summary>
    private static readonly RoomFingerprint Elsewhere = new(
        WifiFeatureHash: "wifi-sim-elsewhere",
        BluetoothFeatureHash: "bt-sim-elsewhere",
        AmbientLightVector: new Vector3(15f, 0f, 0f),
        MagneticVector: new Vector3(-30.2f, 12.4f, -18.6f),
        PressureHpa: 1004f,
        AcousticEmbeddingId: null);

    public bool IsAvailable => true;

    public string Summary => _elsewhere
        ? "Room signals: simulated, a different place"
        : "Room signals: simulated";

    public Task<FingerprintReading> ReadAsync(CancellationToken ct)
    {
        var fingerprint = _elsewhere ? Elsewhere : Here;

        return Task.FromResult(new FingerprintReading(
            fingerprint,
            [
                new FingerprintSignal("Wi-Fi neighbourhood", "simulated, hashed", true),
                new FingerprintSignal("Bluetooth neighbourhood", "simulated, hashed", true),
                new FingerprintSignal("Magnetic signature", $"{fingerprint.MagneticVector.Length():0.0} µT", true),
                new FingerprintSignal("Air pressure", $"{fingerprint.PressureHpa:0.0} hPa", true),
                new FingerprintSignal("Ambient light", $"{fingerprint.AmbientLightVector.X:0} lux", true),
            ],
            DateTimeOffset.Now));
    }
}
