using System.Numerics;
using Aware.Domain;

namespace Aware.Application;

public interface IRoomRepository
{
    Task<IReadOnlyList<SpatialRoom>> GetRoomsAsync(CancellationToken ct);
    Task<SpatialRoom?> GetRoomAsync(RoomId id, CancellationToken ct);
    Task SaveRoomAsync(SpatialRoom room, CancellationToken ct);
    Task DeleteRoomAsync(RoomId id, CancellationToken ct);
    Task DeleteAllAsync(CancellationToken ct);
}

public sealed record RecognitionState(
    string Message,
    float Progress,
    float Confidence,
    bool IsStable);

public interface IRoomRecognitionService
{
    IAsyncEnumerable<RecognitionState> RecognizeAsync(
        SpatialRoom room,
        CancellationToken ct);
}

public interface IRenderSnapshotFactory
{
    SpatialRenderSnapshot Create(
        SpatialRoom room,
        SpatialObjectId? selectedObjectId,
        SpatialLens lens,
        bool reducedMotion);
}

public interface IObjectActionResolver
{
    ObjectActionTray Resolve(
        SpatialRoom room,
        SpatialObject selectedObject,
        SpatialLens lens);
}

public sealed record ObjectAction(string Id, string Label, bool IsPrimary);

public sealed record TrayFact(string Label, string Value, EvidenceKind Evidence, bool IsKnown = true);

public sealed record ObjectActionTray(
    string Eyebrow,
    string Title,
    string Description,
    IReadOnlyList<TrayFact> Facts,
    IReadOnlyList<ObjectAction> Actions);

// ---------------------------------------------------------------------------
// Render contract. The renderer sees only this; it never touches the domain,
// sensors or storage (03-ARCHITECTURE).
// ---------------------------------------------------------------------------

public sealed record RenderPrimitive(
    string Id,
    SpatialObjectId? ObjectId,
    SpatialPrimitiveKind Kind,
    Vector3 Size,
    SpatialTransform Transform,
    string MaterialKey,
    int ReconstructionStage,
    bool IsSelectable);

/// <summary>A measurement guide attached to object bounds, drawn in the Measure lens only.</summary>
public sealed record RenderGuide(
    Vector3 Start,
    Vector3 End,
    string Label,
    bool IsKnown);

/// <summary>A point marker for the Memories lens, tagged with how the fact was arrived at.</summary>
public sealed record RenderPin(
    Vector3 Position,
    string Label,
    EvidenceKind Evidence);

public sealed record SpatialRenderSnapshot(
    RoomId RoomId,
    string RoomName,
    float RoomConfidence,
    IReadOnlyList<RenderPrimitive> Primitives,
    SpatialObjectId? SelectedObjectId,
    string? SelectedObjectLabel,
    Vector3? SelectionAnchor,
    SpatialLens ActiveLens,
    IReadOnlyList<RenderGuide> Guides,
    IReadOnlyList<RenderPin> Pins,
    bool ReducedMotion);

// ---------------------------------------------------------------------------
// Privacy and haptics (07-DATA-PRIVACY, 03-ARCHITECTURE service list)
// ---------------------------------------------------------------------------

public sealed record PrivacySettings(
    bool AwarenessPaused,
    bool CloudSyncEnabled,
    bool RetainImagery,
    bool AcousticSensingEnabled);

public interface IPrivacyService
{
    PrivacySettings Current { get; }
    Task<PrivacySettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(PrivacySettings settings, CancellationToken ct);
    Task ForgetAllSpatialDataAsync(CancellationToken ct);
    Task<string> ExportModelAsync(RoomId id, CancellationToken ct);
}

public enum HapticKind { Selection, Confirm, Warning }

public interface IHapticsService
{
    void Play(HapticKind kind);
}
