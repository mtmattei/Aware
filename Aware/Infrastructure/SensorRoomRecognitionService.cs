using System.Runtime.CompilerServices;
using Aware.Application;
using Aware.Domain;
using Aware.Platform;

namespace Aware.Infrastructure;

/// <summary>
/// Recognition driven by what the device can actually sense.
///
/// <para>The four stages from 02-UX-FLOWS still pace the reconstruction, because
/// they describe geometry assembling rather than sensing. What changes is that
/// the confidence and the closing message come from a real comparison between
/// the stored fingerprint and a live reading, so matching signals raise
/// confidence and contradictions lower it (08-ACCESSIBILITY-TESTS).</para>
///
/// <para>Where no sensors exist — desktop, the browser — the stored confidence
/// stands in and the experience is identical. That is the capability-tier
/// promise in the README: only the input changes.</para>
/// </summary>
public sealed class SensorRoomRecognitionService : IRoomRecognitionService
{
    private readonly IRoomFingerprintProvider _fingerprints;
    private readonly IFingerprintMatcher _matcher;
    private readonly IMotionSettings _motion;
    private readonly ILogger<SensorRoomRecognitionService> _log;

    public SensorRoomRecognitionService(
        IRoomFingerprintProvider fingerprints,
        IFingerprintMatcher matcher,
        IMotionSettings motion,
        ILogger<SensorRoomRecognitionService> log)
    {
        _fingerprints = fingerprints;
        _matcher = matcher;
        _motion = motion;
        _log = log;
    }

    /// <summary>The most recent live reading, for the privacy evidence view.</summary>
    public FingerprintReading? LastReading { get; private set; }

    public FingerprintComparison? LastComparison { get; private set; }

    public async IAsyncEnumerable<RecognitionState> RecognizeAsync(
        SpatialRoom room,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var scale = _motion.ReducedMotion ? .12f : 1f;

        // Sensing starts immediately and runs alongside the shell assembling, so
        // the reading is ready by the time the stages need its verdict.
        var reading = ReadAsync(ct);

        await Task.Delay(TimeSpan.FromMilliseconds(800 * scale), ct);
        yield return new RecognitionState("Walls and floor recognized", .19f, .46f, false);

        await Task.Delay(TimeSpan.FromMilliseconds(1100 * scale), ct);
        yield return new RecognitionState("Furniture geometry resolving", .45f, .67f, false);

        var live = await reading;
        LastReading = live;

        var comparison = live.HasAnySignal
            ? _matcher.Compare(room.Fingerprint, live)
            : null;
        LastComparison = comparison;

        await Task.Delay(TimeSpan.FromMilliseconds(1100 * scale), ct);
        yield return new RecognitionState(MatchingMessage(comparison), .71f, .88f, false);

        await Task.Delay(TimeSpan.FromMilliseconds(1200 * scale), ct);

        // With no sensors the stored confidence is the honest answer; with them,
        // the measured one is.
        var confidence = comparison?.Confidence ?? room.Confidence;
        yield return new RecognitionState(SettledMessage(confidence, comparison), 1f, confidence, true);
    }

    private async Task<FingerprintReading> ReadAsync(CancellationToken ct)
    {
        if (!_fingerprints.IsAvailable) return FingerprintReading.Unavailable;

        try
        {
            return await _fingerprints.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A refused permission or a missing sensor must degrade to the
            // simulated tier, never take recognition down.
            _log.LogWarning(ex, "Could not read the room fingerprint.");
            return FingerprintReading.Unavailable;
        }
    }

    private static string MatchingMessage(FingerprintComparison? comparison)
    {
        if (comparison is null) return "Known objects matching";

        return comparison.Agreements.Count switch
        {
            0 => "Known objects matching · no signal agrees yet",
            1 => $"Known objects matching · {comparison.Agreements[0].ToLowerInvariant()} agrees",
            _ => $"Known objects matching · {comparison.Agreements.Count} signals agree",
        };
    }

    private static string SettledMessage(float confidence, FingerprintComparison? comparison)
    {
        var percent = $"{confidence * 100:0}% confident";

        if (comparison is null) return $"{percent} · stable room model";

        if (comparison.Contradictions.Count > 0 && !comparison.IsRecognized)
            return $"{percent} · {comparison.Contradictions[0].ToLowerInvariant()} disagrees";

        return $"{percent} · {Describe(comparison.Agreements)}";
    }

    private static string Describe(IReadOnlyList<string> agreements) =>
        agreements.Count switch
        {
            0 => "no matching signals",
            1 => $"{agreements[0].ToLowerInvariant()} matches",
            _ => $"{agreements.Count} signals match",
        };
}
