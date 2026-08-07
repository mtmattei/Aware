using Aware.Domain;

namespace Aware.Infrastructure;

/// <summary>
/// Builds a room that is known as a <em>place</em> but not yet as a shape.
///
/// <para>The ambient sensors identify where you are; none of them can measure how
/// far away a wall is. Separating the two lets a user set up the room they are
/// standing in today, on hardware that has no depth camera, and have Aware
/// recognize it on return — with the model filling in later beneath the same
/// <see cref="RoomId"/> when capture lands (10-ROADMAP Phase 2).</para>
/// </summary>
public static class UnmodelledRoomFactory
{
    /// <summary>
    /// A room with a name, a place, and deliberately nothing else.
    /// <paramref name="name"/> is trimmed; callers must reject blank names before
    /// reaching here.
    /// </summary>
    public static SpatialRoom Create(string name, RoomFingerprint fingerprint, DateTimeOffset now) =>
        new(
            // Never SampleGarageFactory.GarageId: a real place must not collide
            // with the sample, or re-seeding would overwrite the user's room.
            Id: new RoomId($"room-{Guid.NewGuid():N}"),
            Name: name.Trim(),
            Type: "room",
            // Confidence describes geometry, and there is none. Zero here is not
            // low confidence in the place — that is the fingerprint comparison's
            // answer, and it is reported separately.
            Confidence: 0f,
            ModelVersion: 0,
            LastObservedAt: now,
            Fingerprint: fingerprint,
            Shell: [],
            Objects: [],
            Projects: []);
}
