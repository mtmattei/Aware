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
    [System.Text.Json.Serialization.JsonIgnore]
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
    [System.Text.Json.Serialization.JsonIgnore]
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
    // How sure the app is of this geometry. Distinct from whether the device is
    // currently standing in the place; that comparison lives against Fingerprint.
    float Confidence,
    int ModelVersion,
    DateTimeOffset LastObservedAt,
    // The ambient signature of the physical place this model was built in, or
    // null when the model has never been linked to one. A seeded or imported room
    // starts null: inventing a fingerprint makes the device report a contradiction
    // against a place that was never measured.
    RoomFingerprint? Fingerprint,
    IReadOnlyList<SpatialPrimitive> Shell,
    IReadOnlyList<SpatialObject> Objects,
    IReadOnlyList<SpatialProject> Projects)
{
    /// <summary>True once this model has been linked to a physical place.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLinkedToPlace => Fingerprint is not null;

    /// <summary>
    /// False for a recognition-only room: a known place with no model of it yet.
    /// Geometry and place are independent, so both booleans are needed and a
    /// single "kind" discriminator would drift from the data it describes.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsModelled => Shell.Count > 0 || Objects.Count > 0;

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

    /// <summary>
    /// Binds this model to the place the device is standing in right now. Geometry,
    /// objects and every ID are untouched: linking says where the room is, not what
    /// is in it.
    /// </summary>
    public SpatialRoom LinkedTo(RoomFingerprint fingerprint) =>
        this with { Fingerprint = fingerprint };

    /// <summary>Releases the link without touching the model (07-DATA-PRIVACY).</summary>
    public SpatialRoom Unlinked() =>
        this with { Fingerprint = null };
}
