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
/// the closing message reports a real comparison between the stored fingerprint
/// and a live reading, so matching signals raise the match and contradictions
/// lower it (08-ACCESSIBILITY-TESTS).</para>
///
/// <para>Model confidence and place match are deliberately kept apart; the
/// wording and the reasoning live in <see cref="RecognitionMessages"/>.</para>
///
/// <para>Where no sensors exist — desktop, the browser — there is no place
/// question to answer and the line is the brief's exactly. That is the
/// capability-tier promise in the README: only the input changes.</para>
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
        yield return new RecognitionState(
            room.IsModelled ? "Walls and floor recognized" : "Reading this place", .19f, .46f, false);

        if (room.IsModelled)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1100 * scale), ct);
            yield return new RecognitionState("Furniture geometry resolving", .45f, .67f, false);
        }

        var live = await reading;
        LastReading = live;

        // A comparison needs both halves. Sensors with no stored fingerprint is
        // not a mismatch, it is an unlinked room, and scoring it would manufacture
        // a contradiction out of nothing.
        var comparison = live.HasAnySignal && room.Fingerprint is { } stored
            ? _matcher.Compare(stored, live)
            : null;
        LastComparison = comparison;

        var canLink = live.HasAnySignal && room.Fingerprint is null;

        // "Known objects matching" would be a lie about a room with no objects,
        // so a recognition-only room skips straight to its verdict.
        if (room.IsModelled)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1100 * scale), ct);
            yield return new RecognitionState(RecognitionMessages.Matching(comparison), .71f, .88f, false);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1200 * scale), ct);

        // The model's own confidence, unchanged by where the device is standing.
        yield return new RecognitionState(
            RecognitionMessages.Settled(room.Confidence, comparison, canLink, room.IsModelled),
            1f, room.Confidence, true);
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
}
