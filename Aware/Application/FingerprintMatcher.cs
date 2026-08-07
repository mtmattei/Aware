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
    // Wi-Fi is by far the sharpest room discriminator, so it carries the most
    // weight. Light is the weakest: it changes with time of day and the lamp.
    private const float WifiWeight = .45f;
    private const float MagneticWeight = .25f;
    private const float PressureWeight = .20f;
    private const float LightWeight = .10f;

    /// <summary>Roughly one floor of a building.</summary>
    private const float PressureToleranceHpa = 1.5f;

    public FingerprintComparison Compare(RoomFingerprint stored, FingerprintReading live)
    {
        if (!live.HasAnySignal)
            return new FingerprintComparison(0f, [], []);

        var agreements = new List<string>();
        var contradictions = new List<string>();

        var score = 0f;
        var weight = 0f;

        Score(WifiWeight, WifiSimilarity(stored.WifiFeatureHash, live.Fingerprint.WifiFeatureHash),
            "Wi-Fi neighbourhood", ref score, ref weight, agreements, contradictions);

        Score(MagneticWeight, VectorSimilarity(stored.MagneticVector, live.Fingerprint.MagneticVector),
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
    /// Hashes are opaque by design (07-DATA-PRIVACY stores hashes, not network
    /// names), so this is necessarily a match / no-match rather than a degree.
    /// </summary>
    private static float? WifiSimilarity(string stored, string live)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(live)) return null;
        return string.Equals(stored, live, StringComparison.Ordinal) ? 1f : 0f;
    }

    private static float? VectorSimilarity(Vector3 stored, Vector3 live)
    {
        if (stored.LengthSquared() < 1e-6f || live.LengthSquared() < 1e-6f) return null;

        // Direction agreement matters more than magnitude: the field direction
        // is what distinguishes one corner of a building from another.
        var cosine = Vector3.Dot(Vector3.Normalize(stored), Vector3.Normalize(live));
        var direction = Math.Clamp((cosine + 1f) / 2f, 0f, 1f);

        var magnitude = 1f - Math.Clamp(
            MathF.Abs(stored.Length() - live.Length()) / MathF.Max(stored.Length(), 1e-3f),
            0f, 1f);

        return direction * .75f + magnitude * .25f;
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
