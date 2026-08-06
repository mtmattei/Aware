using System.Numerics;
using Aware.Domain;

namespace Aware.Platform;

public enum SpatialCaptureCapability
{
    Simulation,
    CameraAndMotion,
    DepthAssisted,
    FullRoomReconstruction,
}

public sealed record SpatialCaptureCapabilities(
    SpatialCaptureCapability HighestCapability,
    bool HasCamera,
    bool HasMotion,
    bool HasDepth,
    bool HasUwb)
{
    public string Summary => HighestCapability switch
    {
        SpatialCaptureCapability.FullRoomReconstruction => "Full room reconstruction available",
        SpatialCaptureCapability.DepthAssisted => "Depth-assisted capture available",
        SpatialCaptureCapability.CameraAndMotion => "Camera and motion capture available",
        _ => "Simulated models only on this device",
    };
}

public sealed record SpatialCaptureRequest(
    RoomId? ExistingRoomId,
    bool IncludeImages,
    bool IncludeDepth,
    TimeSpan MaximumDuration);

public sealed record ObservedPrimitive(
    SpatialPrimitiveKind Kind,
    Vector3 Size,
    SpatialTransform Transform,
    float Confidence);

public sealed record ObservedObjectCandidate(
    string SemanticClass,
    SpatialTransform Transform,
    float Confidence);

public sealed record SpatialObservation(
    DateTimeOffset Timestamp,
    string Source,
    float Quality,
    IReadOnlyList<ObservedPrimitive> Geometry,
    IReadOnlyList<ObservedObjectCandidate> ObjectCandidates);

/// <summary>
/// The single boundary every capture tier crosses. Native framework types
/// (ARKit, ARCore, camera buffers) never travel through it (09-UNO-NOTES).
/// </summary>
public interface ISpatialCaptureAdapter
{
    Task<SpatialCaptureCapabilities> GetCapabilitiesAsync(CancellationToken ct);

    IAsyncEnumerable<SpatialObservation> CaptureAsync(
        SpatialCaptureRequest request,
        CancellationToken ct);
}
