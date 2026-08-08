using System.Numerics;
using Aware.Domain;

namespace Aware.Application;

/// <summary>
/// Projects the domain room into the immutable snapshot the renderer consumes.
/// Lens-specific annotations (Measure guides, Memories pins) are resolved here so
/// the renderer stays a pure drawing surface.
/// </summary>
public sealed class RenderSnapshotFactory : IRenderSnapshotFactory
{
    private const float GuideOffset = 0.55f;

    private SpatialRoom? _cachedRoom;
    private IReadOnlyList<RenderPrimitive> _cachedPrimitives = [];

    public SpatialRenderSnapshot Create(
        SpatialRoom room,
        SpatialObjectId? selectedObjectId,
        SpatialLens lens,
        bool reducedMotion)
    {
        // Selecting an object or switching lenses must not rebuild geometry
        // (06-MOTION-BRIEF: "no geometry rebuild"). Reusing the same list
        // instance lets the renderer keep its tessellated meshes.
        var primitives = BuildPrimitives(room);

        var selected = selectedObjectId is { } id ? room.FindObject(id) : null;

        return new SpatialRenderSnapshot(
            room.Id,
            room.Name,
            room.Confidence,
            primitives,
            selected?.Id,
            selected?.DisplayName,
            selected is null ? null : SelectionAnchor(selected),
            lens,
            selected is not null && lens == SpatialLens.Measure
                ? BuildGuides(selected)
                : [],
            lens == SpatialLens.Memories
                ? BuildPins(room, selected)
                : [],
            reducedMotion);
    }

    /// <summary>
    /// The ground a recognition-only room stands on. A room whose place is known
    /// but whose shape is not would otherwise render as a void, which reads as a
    /// broken renderer rather than as "shape unknown". A bare plate is the honest
    /// picture — there is a floor here and nothing measured on it — and it keeps
    /// orbit meaningful, so the room still behaves like a room.
    ///
    /// <para>02-UX-FLOWS forbids an empty spinner and puts artifacts before
    /// explanatory text, so this is deliberately geometry rather than a label.</para>
    /// </summary>
    private static readonly RenderPrimitive UnmodelledGround = new(
        "shell-floor-unmodelled",
        ObjectId: null,
        SpatialPrimitiveKind.Box,
        // Roughly the seeded room's footprint, so the camera framing every other
        // room uses needs no special case.
        Size: new Vector3(7.4f, .18f, 5.6f),
        Transform: new SpatialTransform(new Vector3(0f, -.09f, 0f), Vector3.Zero, Vector3.One),
        MaterialKey: "floor",
        ReconstructionStage: 0,
        IsSelectable: false);

    private IReadOnlyList<RenderPrimitive> BuildPrimitives(SpatialRoom room)
    {
        if (ReferenceEquals(_cachedRoom, room)) return _cachedPrimitives;

        if (!room.IsModelled)
        {
            _cachedRoom = room;
            return _cachedPrimitives = [UnmodelledGround];
        }

        var primitives = new List<RenderPrimitive>(
            room.Shell.Count + room.Objects.Sum(o => o.Primitives.Count));

        foreach (var p in room.Shell)
            primitives.Add(Convert(p, null));

        foreach (var obj in room.Objects)
            foreach (var p in obj.Primitives)
                primitives.Add(Convert(p, obj.Id));

        _cachedRoom = room;
        return _cachedPrimitives = primitives;
    }

    private static RenderPrimitive Convert(SpatialPrimitive p, SpatialObjectId? objectId) =>
        new(p.Id, objectId, p.Kind, p.Size, p.Transform, p.MaterialKey,
            p.ReconstructionStage, p.IsSelectable);

    private static Vector3 SelectionAnchor(SpatialObject obj)
    {
        var box = WorldBounds(obj);
        return new Vector3((box.Min.X + box.Max.X) * .5f, box.Max.Y, (box.Min.Z + box.Max.Z) * .5f);
    }

    private static IReadOnlyList<RenderGuide> BuildGuides(SpatialObject obj)
    {
        var (min, max) = WorldBounds(obj);

        // Guides sit just outside the object, on the faces nearest the viewer's
        // default orbit, so they never cross the geometry they describe.
        var y = min.Y + .01f;
        var zOut = max.Z + GuideOffset;
        var xOut = max.X + GuideOffset;

        return
        [
            new RenderGuide(
                new Vector3(min.X, y, zOut),
                new Vector3(max.X, y, zOut),
                Label(obj.Bounds.WidthCm, "Width"),
                obj.Bounds.WidthCm is not null),

            new RenderGuide(
                new Vector3(xOut, y, min.Z),
                new Vector3(xOut, y, max.Z),
                Label(obj.Bounds.DepthCm, "Depth"),
                obj.Bounds.DepthCm is not null),

            new RenderGuide(
                new Vector3(xOut, min.Y, zOut),
                new Vector3(xOut, max.Y, zOut),
                Label(obj.Bounds.HeightCm, "Height"),
                obj.Bounds.HeightCm is not null),
        ];
    }

    private static string Label(float? cm, string axis) =>
        cm is { } v ? $"{v:0} cm" : $"{axis} not measured";

    private static IReadOnlyList<RenderPin> BuildPins(SpatialRoom room, SpatialObject? selected)
    {
        var pins = new List<RenderPin>();

        // Unselected objects that carry history get a quiet marker, so memories are
        // visible in the room before anything is tapped.
        foreach (var obj in room.Objects)
        {
            if (obj.Id == selected?.Id) continue;
            if (obj.Memories.Count == 0 && obj.AttachedProjectId is null) continue;

            var box = WorldBounds(obj);
            pins.Add(new RenderPin(
                new Vector3((box.Min.X + box.Max.X) * .5f, box.Max.Y + .18f, (box.Min.Z + box.Max.Z) * .5f),
                obj.Memories.Count == 1 ? "1 memory" : $"{obj.Memories.Count} memories",
                EvidenceKind.Observed));
        }

        if (selected is not null)
        {
            var box = WorldBounds(selected);
            var x = (box.Min.X + box.Max.X) * .5f;
            var z = (box.Min.Z + box.Max.Z) * .5f;
            var y = box.Max.Y + .26f;

            foreach (var memory in selected.Memories.OrderByDescending(m => m.At))
            {
                pins.Add(new RenderPin(new Vector3(x, y, z), memory.Title, memory.Evidence));
                y += .30f;
            }
        }

        return pins;
    }

    /// <summary>Axis-aligned world bounds across every primitive of an object.</summary>
    internal static (Vector3 Min, Vector3 Max) WorldBounds(SpatialObject obj)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var p in obj.Primitives)
        {
            var half = p.Size * p.Transform.Scale * .5f;
            var rotation = Matrix4x4.CreateFromYawPitchRoll(
                p.Transform.RotationRadians.Y,
                p.Transform.RotationRadians.X,
                p.Transform.RotationRadians.Z);

            for (var corner = 0; corner < 8; corner++)
            {
                var local = new Vector3(
                    (corner & 1) == 0 ? -half.X : half.X,
                    (corner & 2) == 0 ? -half.Y : half.Y,
                    (corner & 4) == 0 ? -half.Z : half.Z);

                var world = Vector3.Transform(local, rotation) + p.Transform.Position;
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }
        }

        return obj.Primitives.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }
}
