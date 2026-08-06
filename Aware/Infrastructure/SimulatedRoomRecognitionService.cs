using System.Runtime.CompilerServices;
using Aware.Application;
using Aware.Domain;
using Aware.Platform;

namespace Aware.Infrastructure;

/// <summary>
/// Emits the recognition stages from 02-UX-FLOWS on the 4.2 s assembly timeline
/// in 06-MOTION-BRIEF. Never an empty spinner: every step names what the room
/// just resolved.
/// </summary>
public sealed class SimulatedRoomRecognitionService : IRoomRecognitionService
{
    private readonly IMotionSettings _motion;

    public SimulatedRoomRecognitionService(IMotionSettings motion) => _motion = motion;

    public async IAsyncEnumerable<RecognitionState> RecognizeAsync(
        SpatialRoom room,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var steps = new (int DelayMs, RecognitionState State)[]
        {
            (800,  new RecognitionState("Walls and floor recognized", .19f, .46f, false)),
            (1100, new RecognitionState("Furniture geometry resolving", .45f, .67f, false)),
            (1100, new RecognitionState("Known objects matching", .71f, .88f, false)),
            (1200, new RecognitionState(
                $"{room.Confidence * 100:0}% confident · stable room model", 1f, room.Confidence, true)),
        };

        // Reduced motion still walks the stages so the state changes stay
        // understandable; it just does not linger on them.
        var scale = _motion.ReducedMotion ? .12f : 1f;

        foreach (var (delay, state) in steps)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delay * scale), ct);
            yield return state;
        }
    }
}
