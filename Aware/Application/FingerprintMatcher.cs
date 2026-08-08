using System.Numerics;
using Aware.Domain;

namespace Aware.Application;

/// <summary>
/// Weighted agreement across the signals a phone can read without a camera.
///
/// <para>Only signals present in <em>both</em> the stored room and the live
/// reading are scored; the rest drop out and the remaining weights renormalize,
/// so a device without a barometer is not punished for lacking one. A signal
/// that is present in both and disagrees is a contradiction, and it pulls
/// confidence down rather than merely failing to raise it.</para>
/// </summary>
public sealed class FingerprintMatcher : IFingerprintMatcher
{
    // Weights follow what was actually measured on a Pixel 8 (2026-08-07) rather
    // than what each signal sounds like it should be worth.
    //
    // Bluetooth carries the most because it is the only *set* signal the app can
    // still read: NEARBY_WIFI_DEVICES does not lift the location gate on
    // getScanResults, so Wi-Fi returns an empty list on Android 13+ and scores
    // nothing (see AndroidManifest.xml). BLUETOOTH_SCAN with neverForLocation does
    // work without a location permission.
    //
    // Magnetic magnitude is second and is the most reliable single number the
    // device produces. Pressure and light are deliberately small: both drift for
    // reasons that have nothing to do with which room you are in.
    private const float BluetoothWeight = .50f;
    private const float MagneticWeight = .30f;
    private const float PressureWeight = .10f;
    private const float LightWeight = .10f;
    private const float WifiWeight = .45f;

    /// <summary>
    /// Pressure drifts with weather, not just with height: the same room read
    /// 1015.0 hPa one morning and 1011.6 hPa that night, 3.4 hPa apart, while a
    /// storey is worth about 1.5. A tolerance tight enough to detect a floor
    /// change therefore makes every room contradict itself within a day, so this
    /// is wide enough to ignore weather and only catches gross changes.
    /// </summary>
    private const float PressureToleranceHpa = 6f;

    /// <summary>
    /// Field strength in microtesla, and deliberately loose.
    ///
    /// <para>Four readings in one kitchen measured 46.2, 45.5, 46.5 and 34.5 — a
    /// spread of 11.7 within a single room, because standing a step closer to an
    /// appliance moves the field as much as changing rooms does. A tolerance tight
    /// enough to separate rooms on magnetism alone therefore makes a room fail to
    /// recognize itself, which is the worse error: the app would sit in your
    /// kitchen insisting you were somewhere else.</para>
    ///
    /// <para>Bluetooth is the discriminator now, so this only has to avoid
    /// contradicting a room the set signal already agreed about. 20 covers the
    /// measured within-room spread while still scoring zero against the other
    /// room in the same home, which read 21.3.</para>
    /// </summary>
    private const float MagneticToleranceMicrotesla = 20f;

    public FingerprintComparison Compare(RoomFingerprint stored, FingerprintReading live)
    {
        if (!live.HasAnySignal)
            return new FingerprintComparison(0f, [], []);

        var agreements = new List<string>();
        var contradictions = new List<string>();

        var score = 0f;
        var weight = 0f;

        Score(WifiWeight, HashSimilarity(stored.WifiFeatureHash, live.Fingerprint.WifiFeatureHash),
            "Wi-Fi neighbourhood", ref score, ref weight, agreements, contradictions);

        Score(BluetoothWeight, SetSimilarity(stored.BluetoothFeatureHash, live.Fingerprint.BluetoothFeatureHash),
            "Bluetooth neighbourhood", ref score, ref weight, agreements, contradictions);

        Score(MagneticWeight, MagneticSimilarity(stored.MagneticVector, live.Fingerprint.MagneticVector),
            "Magnetic signature", ref score, ref weight, agreements, contradictions);

        Score(PressureWeight, PressureSimilarity(stored.PressureHpa, live.Fingerprint.PressureHpa),
            "Air pressure", ref score, ref weight, agreements, contradictions);

        Score(LightWeight, LightSimilarity(stored.AmbientLightVector, live.Fingerprint.AmbientLightVector),
            "Ambient light", ref score, ref weight, agreements, contradictions);

        var confidence = weight <= 0f ? 0f : Math.Clamp(score / weight, 0f, 1f);
        return new FingerprintComparison(confidence, agreements, contradictions);
    }

    private static void Score(
        float weight,
        float? similarity,
        string name,
        ref float score,
        ref float totalWeight,
        List<string> agreements,
        List<string> contradictions)
    {
        // null means the signal is missing on one side; it neither helps nor hurts.
        if (similarity is not { } value) return;

        score += weight * value;
        totalWeight += weight;

        if (value >= .6f) agreements.Add(name);
        else contradictions.Add(name);
    }

    /// <summary>
    /// Overlap between two sets of individually hashed identifiers, as
    /// intersection over union.
    ///
    /// <para>A single hash over the whole set was measured failing on a Pixel 8:
    /// the same kitchen produced <c>bt-2a3efbaf95b7</c> and, four minutes later,
    /// <c>bt-dd614e160991</c>. Bluetooth advertisers come and go between scans, so
    /// one device sleeping changed the whole blob and the room stopped recognizing
    /// itself. Comparing the sets instead means losing one advertiser of five
    /// costs 0.2, not everything.</para>
    ///
    /// <para>Each identifier is still hashed on its own before it is stored, so
    /// nothing here is a device name (07-DATA-PRIVACY) — only opaque tokens whose
    /// overlap can be counted.</para>
    /// </summary>
    private static float? SetSimilarity(string stored, string live)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(live)) return null;

        var a = stored.Split(FingerprintSetSeparator, StringSplitOptions.RemoveEmptyEntries);
        var b = live.Split(FingerprintSetSeparator, StringSplitOptions.RemoveEmptyEntries);

        if (a.Length == 0 || b.Length == 0) return null;

        var left = new HashSet<string>(a, StringComparer.Ordinal);
        var right = new HashSet<string>(b, StringComparer.Ordinal);

        var union = new HashSet<string>(left, StringComparer.Ordinal);
        union.UnionWith(right);

        left.IntersectWith(right);

        return union.Count == 0 ? null : (float)left.Count / union.Count;
    }

    /// <summary>
    /// Separates the per-device hashes inside a neighbourhood fingerprint. Shared
    /// with the platform providers that build them.
    /// </summary>
    public const char FingerprintSetSeparator = '+';

    /// <summary>
    /// Hashes are opaque by design (07-DATA-PRIVACY stores hashes, not device or
    /// network names), so this is necessarily a match / no-match rather than a
    /// degree. Still used for Wi-Fi, which is a single blob hash — and which
    /// contributes nothing on Android 13+ anyway.
    /// </summary>
    private static float? HashSimilarity(string stored, string live)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(live)) return null;
        return string.Equals(stored, live, StringComparison.Ordinal) ? 1f : 0f;
    }

    /// <summary>
    /// Field strength only, never direction.
    ///
    /// <para>The magnetometer reports in <em>device</em> coordinates, so the
    /// vector's direction describes which way the phone is being held far more
    /// than where it is standing. Measured on a Pixel 8: two readings in one
    /// kitchen thirteen minutes apart were <c>(4.6, -11.8, -44.4)</c> and
    /// <c>(18.8, 13.3, -39.5)</c> — 29 µT apart as vectors, 0.7 µT apart as
    /// magnitudes. The previous formula weighted direction 0.75 and magnitude
    /// 0.25, which is backwards: it scored orientation as if it were place, and
    /// its <c>(cos + 1) / 2</c> mapping never fell below 0.5 even for
    /// perpendicular fields, so it agreed with almost anything.</para>
    ///
    /// <para>Magnitude is orientation-invariant and is what indoor anomalies —
    /// rebar, appliances, wiring — actually shift from room to room.</para>
    /// </summary>
    private static float? MagneticSimilarity(Vector3 stored, Vector3 live)
    {
        if (stored.LengthSquared() < 1e-6f || live.LengthSquared() < 1e-6f) return null;

        var delta = MathF.Abs(stored.Length() - live.Length());
        return Math.Clamp(1f - delta / MagneticToleranceMicrotesla, 0f, 1f);
    }

    private static float? PressureSimilarity(float stored, float live)
    {
        if (stored <= 0f || live <= 0f) return null;

        var delta = MathF.Abs(stored - live);
        return Math.Clamp(1f - delta / (PressureToleranceHpa * 2f), 0f, 1f);
    }

    private static float? LightSimilarity(Vector3 stored, Vector3 live)
    {
        // Lux is carried in X; see AndroidFingerprintProvider.
        var a = stored.X;
        var b = live.X;
        if (a <= 0f || b <= 0f) return null;

        // Compared on a log scale: 40 lux versus 80 lux is a smaller change than
        // the raw difference suggests, and daylight swamps everything linearly.
        var ratio = MathF.Log2(MathF.Max(a, b) / MathF.Min(a, b));
        return Math.Clamp(1f - ratio / 4f, 0f, 1f);
    }
}
