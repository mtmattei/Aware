using System.Numerics;

namespace Aware.Domain;

public readonly record struct RoomId(string Value);
public readonly record struct SpatialObjectId(string Value);
public readonly record struct ProjectId(string Value);

public enum SpatialLens { Explore, Measure, Memories }

public enum SpatialPrimitiveKind { Box, Cylinder, Line, Torus }

/// <summary>
/// Distinguishes what the room actually observed from what it inferred.
/// 02-UX-FLOWS requires Memories to keep the two visually separate.
/// </summary>
public enum EvidenceKind { Observed, Inferred, Corrected }

/// <summary>
/// Dimensions are nullable per axis: a room can know an object's footprint
/// without ever having measured its height. Measure marks those explicitly
/// instead of inventing a number.
/// </summary>
public sealed record SpatialBounds(float? WidthCm, float? HeightCm, float? DepthCm)
{
    public bool IsComplete => WidthCm is not null && HeightCm is not null && DepthCm is not null;

    public static SpatialBounds Unknown { get; } = new(null, null, null);
}

public sealed record SpatialTransform(
    Vector3 Position,
    Vector3 RotationRadians,
    Vector3 Scale)
{
    public static SpatialTransform Identity { get; } =
        new(Vector3.Zero, Vector3.Zero, Vector3.One);

    public static SpatialTransform At(Vector3 position) =>
        new(position, Vector3.Zero, Vector3.One);
}

/// <summary>
/// One drawable semantic primitive. <paramref name="ReconstructionStage"/> maps
/// to the assembly windows in 06-MOTION-BRIEF (0 shell, 1 furniture,
/// 2 semantic objects, 3 small details).
/// </summary>
public sealed record SpatialPrimitive(
    string Id,
    SpatialPrimitiveKind Kind,
    Vector3 Size,
    SpatialTransform Transform,
    string MaterialKey,
    int ReconstructionStage,
    bool IsSelectable);

public sealed record ObjectMemory(
    string Id,
    string Title,
    string Detail,
    EvidenceKind Evidence,
    DateTimeOffset At);

public sealed record LocationHistoryEntry(
    string Description,
    EvidenceKind Evidence,
    DateTimeOffset At);

public sealed record SpatialProject(
    ProjectId Id,
    string Name,
    string Status,
    DateTimeOffset LastActivityAt);

public sealed record SpatialObject(
    SpatialObjectId Id,
    RoomId RoomId,
    string SemanticClass,
    string DisplayName,
    float Confidence,
    SpatialBounds Bounds,
    IReadOnlyList<SpatialPrimitive> Primitives,
    ProjectId? AttachedProjectId,
    DateTimeOffset LastObservedAt,
    IReadOnlyList<ObjectMemory> Memories,
    IReadOnlyList<LocationHistoryEntry> LocationHistory,
    EvidenceKind ClassificationEvidence = EvidenceKind.Inferred,
    SpatialBounds? OpeningBounds = null,
    bool IsExcludedFromSuggestions = false)
{
    /// <summary>World-space centre of every primitive, used to frame guides and labels.</summary>
    public Vector3 Centroid
    {
        get
        {
            if (Primitives.Count == 0) return Vector3.Zero;
            var sum = Vector3.Zero;
            foreach (var p in Primitives) sum += p.Transform.Position;
            return sum / Primitives.Count;
        }
    }
}

public sealed record RoomFingerprint(
    string WifiFeatureHash,
    string BluetoothFeatureHash,
    Vector3 AmbientLightVector,
    Vector3 MagneticVector,
    float PressureHpa,
    string? AcousticEmbeddingId);

public sealed record SpatialRoom(
    RoomId Id,
    string Name,
    string Type,
    float Confidence,
    int ModelVersion,
    DateTimeOffset LastObservedAt,
    RoomFingerprint Fingerprint,
    IReadOnlyList<SpatialPrimitive> Shell,
    IReadOnlyList<SpatialObject> Objects,
    IReadOnlyList<SpatialProject> Projects)
{
    public SpatialObject? FindObject(SpatialObjectId id) =>
        Objects.FirstOrDefault(o => o.Id == id);

    public SpatialProject? FindProject(ProjectId? id) =>
        id is { } value ? Projects.FirstOrDefault(p => p.Id == value) : null;

    /// <summary>
    /// Replaces one object while leaving every other object, and every ID, untouched.
    /// 03-ARCHITECTURE requires IDs to survive renaming, reclassification and geometry refinement.
    /// </summary>
    public SpatialRoom WithObject(SpatialObject updated) =>
        this with
        {
            Objects = Objects.Select(o => o.Id == updated.Id ? updated : o).ToArray()
        };

    public SpatialRoom WithoutObject(SpatialObjectId id) =>
        this with { Objects = Objects.Where(o => o.Id != id).ToArray() };
}
