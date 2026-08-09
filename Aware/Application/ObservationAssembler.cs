using System.Numerics;
using Aware.Domain;
using Aware.Platform;

namespace Aware.Application;

/// <summary>
/// Folds what a capture adapter observed into the room being scanned.
///
/// <para>This is the piece that was missing for the model to be anything but
/// seeded: <see cref="Aware.Platform.ISpatialCaptureAdapter.CaptureAsync"/> has
/// always returned observations and nothing ever consumed them.</para>
///
/// <para>Kept a pure function of (room, observation) for the same reason the
/// matcher and locator are. It also has to be idempotent in the way capture
/// actually behaves: an adapter re-reports the same plane on every frame as its
/// estimate improves, so applying an observation twice must refine one primitive
/// rather than stack up duplicates.</para>
/// </summary>
public interface IObservationAssembler
{
    SpatialRoom Apply(SpatialRoom room, SpatialObservation observation);
}

public sealed class ObservationAssembler : IObservationAssembler
{
    /// <summary>
    /// Two primitives within this distance, of the same kind, are treated as the
    /// same surface seen again rather than a second one. Loose enough to absorb
    /// the drift in a plane's centre as ARCore extends it, tight enough that two
    /// real objects standing beside each other stay separate.
    /// </summary>
    private const float SameSurfaceMetres = .35f;

    /// <summary>
    /// The size of the marker drawn where an object was observed but not yet
    /// measured. Deliberately small and deliberately not derived from anything:
    /// it is a position, not a dimension, and <see cref="SpatialBounds.Unknown"/>
    /// keeps that distinction visible to Measure.
    /// </summary>
    private const float NominalObjectMetres = .3f;

    public SpatialRoom Apply(SpatialRoom room, SpatialObservation observation)
    {
        var shell = room.Shell.ToList();

        foreach (var observed in observation.Geometry)
        {
            var existing = shell.FindIndex(p =>
                p.Kind == observed.Kind &&
                Vector3.Distance(p.Transform.Position, observed.Transform.Position) <= SameSurfaceMetres);

            var primitive = new SpatialPrimitive(
                Id: existing >= 0 ? shell[existing].Id : NewPrimitiveId(observed),
                Kind: observed.Kind,
                Size: observed.Size,
                Transform: observed.Transform,
                MaterialKey: MaterialFor(observed),
                // Stage drives the progressive assembly animation. Everything a
                // live scan produces is structure, which is stage 0.
                ReconstructionStage: 0,
                IsSelectable: false);

            if (existing >= 0) shell[existing] = primitive;
            else shell.Add(primitive);
        }

        var objects = room.Objects.ToList();

        foreach (var candidate in observation.ObjectCandidates)
        {
            var existing = objects.FindIndex(o =>
                string.Equals(o.SemanticClass, candidate.SemanticClass, StringComparison.Ordinal) &&
                Vector3.Distance(o.Centroid, candidate.Transform.Position) <= SameSurfaceMetres);

            if (existing >= 0)
            {
                // Seen again: raise confidence toward what this pass saw and
                // restamp when, without touching the identity or the memories
                // already attached to it.
                objects[existing] = objects[existing] with
                {
                    Confidence = MathF.Max(objects[existing].Confidence, candidate.Confidence),
                    LastObservedAt = observation.Timestamp,
                };
                continue;
            }

            objects.Add(new SpatialObject(
                Id: new SpatialObjectId($"object-{Guid.NewGuid():N}"),
                RoomId: room.Id,
                SemanticClass: candidate.SemanticClass,
                DisplayName: DisplayNameFor(candidate.SemanticClass),
                Confidence: candidate.Confidence,
                // A capture reports where something is well before it reports how
                // big it is. Unknown is honest, and Measure already renders an
                // unmeasured axis explicitly rather than inventing a number.
                Bounds: SpatialBounds.Unknown,
                // A marker at where it was seen, not a claim about its size. An
                // object with no primitives has no centroid, cannot be drawn and
                // cannot be tapped — it would exist only in the list. The nominal
                // box says "something is here"; Bounds stays Unknown, so Measure
                // still refuses to state dimensions nobody measured.
                Primitives:
                [
                    new SpatialPrimitive(
                        Id: $"observed-{Guid.NewGuid():N}",
                        Kind: SpatialPrimitiveKind.Box,
                        Size: new Vector3(NominalObjectMetres, NominalObjectMetres, NominalObjectMetres),
                        Transform: candidate.Transform,
                        MaterialKey: "model",
                        ReconstructionStage: 1,
                        IsSelectable: true),
                ],
                AttachedProjectId: null,
                LastObservedAt: observation.Timestamp,
                Memories: [],
                LocationHistory: [],
                // Observed, not inferred: a sensor saw this, and the user can
                // still correct it. Nothing here classifies.
                ClassificationEvidence: EvidenceKind.Observed));
        }

        return room with
        {
            Shell = shell,
            Objects = objects,
            // Model confidence tracks the quality of what has been observed, and
            // is unrelated to whether the device is standing in the place.
            Confidence = MathF.Max(room.Confidence, observation.Quality),
            ModelVersion = room.ModelVersion + 1,
            LastObservedAt = observation.Timestamp,
        };
    }

    private static string NewPrimitiveId(ObservedPrimitive observed) =>
        $"shell-{observed.Kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";

    /// <summary>
    /// Floor-ish surfaces read as floor, everything else as wall. The renderer
    /// only distinguishes these two for the shell, and a capture adapter reports
    /// geometry rather than naming rooms' parts.
    /// </summary>
    private static string MaterialFor(ObservedPrimitive observed) =>
        observed.Transform.Position.Y <= .25f ? "floor" : "wall";

    private static string DisplayNameFor(string semanticClass)
    {
        if (string.IsNullOrWhiteSpace(semanticClass)) return "Unknown object";

        var spaced = semanticClass.Replace('_', ' ').Trim();
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}
