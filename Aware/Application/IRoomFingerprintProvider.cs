using Aware.Domain;

namespace Aware.Application;

/// <summary>
/// One signal source that contributed (or failed to contribute) to a reading.
/// Surfaced so the user can inspect the evidence behind a recognition
/// (07-DATA-PRIVACY: "inspect evidence").
/// </summary>
public sealed record FingerprintSignal(string Name, string Detail, bool IsAvailable);

public sealed record FingerprintReading(
    RoomFingerprint Fingerprint,
    IReadOnlyList<FingerprintSignal> Signals,
    DateTimeOffset At)
{
    public static FingerprintReading Unavailable { get; } = new(
        new RoomFingerprint(string.Empty, string.Empty,
            System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, 0f, null),
        [],
        DateTimeOffset.MinValue);

    public bool HasAnySignal => Signals.Any(s => s.IsAvailable);
}

/// <summary>
/// Reads the ambient signals that identify a room. Sensor access lives behind
/// this port; native types never cross it (09-UNO-NOTES).
/// </summary>
public interface IRoomFingerprintProvider
{
    /// <summary>False where the platform exposes no usable sensors at all.</summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable summary of what this device can actually sense.</summary>
    string Summary { get; }

    Task<FingerprintReading> ReadAsync(CancellationToken ct);
}

public sealed record FingerprintComparison(
    float Confidence,
    IReadOnlyList<string> Agreements,
    IReadOnlyList<string> Contradictions)
{
    public bool IsRecognized => Confidence >= .6f;
}

/// <summary>
/// Scores a live reading against a stored room. Kept as a pure function of two
/// fingerprints so the "matching signals raise confidence, contradictions lower
/// confidence" rule in 08-ACCESSIBILITY-TESTS is directly testable.
/// </summary>
public interface IFingerprintMatcher
{
    FingerprintComparison Compare(RoomFingerprint stored, FingerprintReading live);
}
