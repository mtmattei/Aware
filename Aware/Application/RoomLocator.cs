using Aware.Domain;

namespace Aware.Application;

public sealed record RoomMatch(SpatialRoom Room, FingerprintComparison Comparison);

/// <summary>
/// Picks the stored room a live reading belongs to. Kept a pure function of
/// (rooms, reading) for the same reason <see cref="IFingerprintMatcher"/> is:
/// "which room am I in" is the rule most worth testing and the easiest to get
/// subtly wrong.
/// </summary>
public interface IRoomLocator
{
    /// <summary>The best-matching room, or null when none clears the bar.</summary>
    RoomMatch? Locate(IReadOnlyList<SpatialRoom> rooms, FingerprintReading live);
}

public sealed class RoomLocator : IRoomLocator
{
    private readonly IFingerprintMatcher _matcher;

    public RoomLocator(IFingerprintMatcher matcher) => _matcher = matcher;

    public RoomMatch? Locate(IReadOnlyList<SpatialRoom> rooms, FingerprintReading live)
    {
        if (!live.HasAnySignal) return null;

        RoomMatch? best = null;

        foreach (var room in rooms)
        {
            // An unlinked room has no place to be compared against. Scoring it
            // would invent a verdict about a room that never claimed a location.
            if (room.Fingerprint is not { } stored) continue;

            var comparison = _matcher.Compare(stored, live);
            if (!comparison.IsRecognized) continue;

            // Strictly greater, so an exact tie keeps the earlier room and the
            // result does not depend on enumeration order.
            if (best is null || comparison.Confidence > best.Comparison.Confidence)
                best = new RoomMatch(room, comparison);
        }

        return best;
    }
}
