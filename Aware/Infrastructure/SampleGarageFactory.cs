using System.Numerics;
using Aware.Domain;

namespace Aware.Infrastructure;

/// <summary>
/// The seeded garage from 01-PRODUCT-BRIEF. Saved dimensions are measured off the
/// geometry so the Measure lens and the model agree, except where an axis is
/// deliberately left unmeasured to exercise "unknown dimensions are explicitly marked".
/// </summary>
public static class SampleGarageFactory
{
    public static readonly RoomId GarageId = new("room-garage-001");

    /// <summary>
    /// Whether a room is the seeded sample rather than somewhere the user has
    /// been. The sample is identified by its id: it is re-seeded under the same
    /// one whenever the store is empty, and a real room is never given it.
    /// </summary>
    public static bool IsSample(RoomId id) => id == GarageId;
    private static readonly ProjectId CabinetProject = new("project-cabinet-001");

    // Room shell footprint, in metres.
    private const float WallZ = -2.72f;
    private const float WallThickness = .18f;
    private const float WallHeight = 2.9f;
    private const float DoorOpeningLeft = -.02f;
    private const float DoorOpeningRight = 2.42f;
    private const float DoorOpeningTop = 2.13f;

    public static SpatialRoom Create()
    {
        var now = DateTimeOffset.Now;

        var shell = BuildShell();

        var objects = new[]
        {
            Workbench(now),
            Cabinet(now),
            ToolChest(now),
            StorageShelf(now),
            Bicycle(now),
            GarageDoor(now),
            PaintCans(now),
        };

        var projects = new[]
        {
            new SpatialProject(
                CabinetProject,
                "Cabinet build",
                "Unfinished · 3 steps left",
                now.AddDays(-9)),
        };

        return new SpatialRoom(
            GarageId,
            "Your Garage",
            "garage",
            .98f,
            ModelVersion: 7,
            LastObservedAt: now,
            // Deliberately unlinked. The earlier seed carried invented signal values,
            // so a device with real sensors scored the sample against a place that
            // never existed and reported a contradiction — a correct measurement of
            // fiction, which reads as a broken app. An unlinked room says the honest
            // thing instead, and the user can link it to wherever they are.
            Fingerprint: null,
            shell,
            objects,
            projects);
    }

    // -- shell -------------------------------------------------------------

    private static IReadOnlyList<SpatialPrimitive> BuildShell()
    {
        const float halfWidth = 3.7f;
        const float halfDepth = 2.8f;

        var leftSegmentWidth = DoorOpeningLeft + halfWidth;
        var rightSegmentWidth = halfWidth - DoorOpeningRight;
        var headerWidth = DoorOpeningRight - DoorOpeningLeft;

        return
        [
            Box("shell-floor", new Vector3(halfWidth * 2, .18f, halfDepth * 2),
                new Vector3(0, -.09f, 0), "floor", 0),

            // Back wall, split around the garage-door opening so the room reads
            // as enclosed without hiding what is behind it.
            Box("shell-wall-back-left", new Vector3(leftSegmentWidth, WallHeight, WallThickness),
                new Vector3(-halfWidth + leftSegmentWidth / 2, WallHeight / 2, WallZ), "wall", 0),

            Box("shell-wall-back-right", new Vector3(rightSegmentWidth, WallHeight, WallThickness),
                new Vector3(halfWidth - rightSegmentWidth / 2, WallHeight / 2, WallZ), "wall", 0),

            Box("shell-wall-back-header", new Vector3(headerWidth, WallHeight - DoorOpeningTop, WallThickness),
                new Vector3((DoorOpeningLeft + DoorOpeningRight) / 2,
                    DoorOpeningTop + (WallHeight - DoorOpeningTop) / 2, WallZ), "wall", 0),

            Box("shell-wall-left", new Vector3(WallThickness, WallHeight, halfDepth * 2),
                new Vector3(-halfWidth + WallThickness / 2, WallHeight / 2, 0), "wall", 0),
        ];
    }

    // -- objects -----------------------------------------------------------

    private static SpatialObject Workbench(DateTimeOffset now)
    {
        const float topY = 1.12f;
        const float legHeight = 1.02f;
        var centre = new Vector3(-.8f, 0, -.95f);

        var primitives = new List<SpatialPrimitive>
        {
            Box("bench-top", new Vector3(3.05f, .2f, 1f),
                centre with { Y = topY }, "model", 1, selectable: true),
        };

        var i = 0;
        foreach (var (dx, dz) in new[] { (-1.25f, -.35f), (1.25f, -.35f), (-1.25f, .35f), (1.25f, .35f) })
            primitives.Add(Box($"bench-leg-{++i}",
                new Vector3(.22f, legHeight, .22f),
                new Vector3(centre.X + dx, legHeight / 2, centre.Z + dz),
                "model", 1, selectable: true));

        return Object(
            "object-workbench-001", "workbench", "Workbench", .99f, primitives,
            Measured(primitives),
            attachedProject: CabinetProject,
            lastObservedAt: now.AddHours(-6),
            classification: EvidenceKind.Observed,
            memories:
            [
                new ObjectMemory("mem-bench-1", "Cut list marked",
                    "Cabinet side panels marked out on the bench.", EvidenceKind.Observed, now.AddDays(-9)),
                new ObjectMemory("mem-bench-2", "Usual glue-up spot",
                    "Most finishing work in this room happens here.", EvidenceKind.Inferred, now.AddDays(-2)),
            ]);
    }

    private static SpatialObject Cabinet(DateTimeOffset now)
    {
        var primitives = new List<SpatialPrimitive>
        {
            Box("cabinet-carcass", new Vector3(1.35f, 1.15f, .72f),
                new Vector3(1.45f, .575f, -.92f), "accent", 2, selectable: true),
            Box("cabinet-door", new Vector3(.64f, 1.02f, .04f),
                new Vector3(1.13f, .6f, -.54f), "accent", 3, selectable: true),
        };

        return Object(
            "object-cabinet-001", "cabinet", "Cabinet project", .97f, primitives,
            Measured(primitives),
            attachedProject: CabinetProject,
            lastObservedAt: now.AddDays(-9),
            classification: EvidenceKind.Observed,
            memories:
            [
                new ObjectMemory("mem-cab-1", "Doors dry-fitted",
                    "Both doors hung once, then removed for finishing.", EvidenceKind.Observed, now.AddDays(-9)),
                new ObjectMemory("mem-cab-2", "Two coats remaining",
                    "Finish schedule inferred from the paint on the shelf.", EvidenceKind.Inferred, now.AddDays(-12)),
            ],
            locationHistory:
            [
                new LocationHistoryEntry("Moved from the back wall to the middle bay.",
                    EvidenceKind.Observed, now.AddDays(-21)),
            ]);
    }

    private static SpatialObject ToolChest(DateTimeOffset now)
    {
        var primitives = new List<SpatialPrimitive>
        {
            Box("tool-chest-body", new Vector3(1.25f, 1.35f, .72f),
                new Vector3(2.25f, .675f, 1.25f), "model", 2, selectable: true),
            Box("tool-chest-drawer", new Vector3(1.19f, .14f, .06f),
                new Vector3(2.25f, 1.02f, 1.62f), "model", 3, selectable: true),
        };

        return Object(
            "object-toolchest-001", "tool_chest", "Tool chest", .97f, primitives,
            Measured(primitives),
            attachedProject: null,
            lastObservedAt: now.AddDays(-2),
            classification: EvidenceKind.Observed,
            memories:
            [
                new ObjectMemory("mem-chest-1", "Top drawer left open",
                    "Drawer front has read as open since Tuesday.", EvidenceKind.Inferred, now.AddDays(-2)),
            ]);
    }

    private static SpatialObject StorageShelf(DateTimeOffset now)
    {
        const float shelfX = -2.55f;
        const float shelfZ = 1.55f;
        const float height = 2.0f;

        var primitives = new List<SpatialPrimitive>
        {
            Box("shelf-upright-left", new Vector3(.08f, height, .5f),
                new Vector3(shelfX - .56f, height / 2, shelfZ), "model", 1, selectable: true),
            Box("shelf-upright-right", new Vector3(.08f, height, .5f),
                new Vector3(shelfX + .56f, height / 2, shelfZ), "model", 1, selectable: true),
        };

        var level = 0;
        foreach (var y in new[] { .35f, .9f, 1.45f, 1.96f })
            primitives.Add(Box($"shelf-board-{++level}", new Vector3(1.2f, .05f, .5f),
                new Vector3(shelfX, y, shelfZ), "model", 1, selectable: true));

        // Depth was never captured: the shelf sits flat against the wall and no
        // observation ever saw its back.
        var measured = Measured(primitives) with { DepthCm = null };

        return Object(
            "object-shelf-001", "storage_shelf", "Storage shelf", .96f, primitives,
            measured,
            attachedProject: null,
            lastObservedAt: now.AddDays(-4),
            classification: EvidenceKind.Inferred);
    }

    private static SpatialObject Bicycle(DateTimeOffset now)
    {
        const float z = 1.75f;
        const float wheelRadius = .34f;

        var primitives = new List<SpatialPrimitive>
        {
            Torus("bike-wheel-rear", new Vector3(wheelRadius * 2, .06f, wheelRadius * 2),
                new Vector3(0f, wheelRadius, z), Vector3.Zero, "metal", 2),
            Torus("bike-wheel-front", new Vector3(wheelRadius * 2, .06f, wheelRadius * 2),
                new Vector3(1.06f, wheelRadius, z), Vector3.Zero, "metal", 2),

            Cylinder("bike-down-tube", new Vector3(.05f, .74f, .05f),
                new Vector3(.44f, .5f, z), new Vector3(0, 0, -.95f), "metal", 2),
            Cylinder("bike-seat-tube", new Vector3(.05f, .56f, .05f),
                new Vector3(.16f, .6f, z), new Vector3(0, 0, .22f), "metal", 2),
            Cylinder("bike-top-tube", new Vector3(.045f, .74f, .045f),
                new Vector3(.42f, .86f, z), new Vector3(0, 0, MathF.PI / 2), "metal", 2),
            Cylinder("bike-fork", new Vector3(.04f, .72f, .04f),
                new Vector3(1.0f, .66f, z), new Vector3(0, 0, -.16f), "metal", 2),
            Cylinder("bike-handlebar", new Vector3(.035f, .42f, .035f),
                new Vector3(.94f, 1.0f, z), new Vector3(MathF.PI / 2, 0, 0), "metal", 3),

            Box("bike-saddle", new Vector3(.26f, .06f, .12f),
                new Vector3(.09f, .92f, z), "metal", 3),
        };

        return Object(
            "object-bicycle-001", "bicycle", "Bicycle", .94f, primitives,
            Measured(primitives),
            attachedProject: null,
            lastObservedAt: now.AddDays(-34),
            classification: EvidenceKind.Observed,
            memories:
            [
                new ObjectMemory("mem-bike-1", "Rear tyre flat",
                    "Rear wheel has not changed position in five weeks.", EvidenceKind.Inferred, now.AddDays(-34)),
            ],
            locationHistory:
            [
                new LocationHistoryEntry("Moved in from the driveway.", EvidenceKind.Observed, now.AddDays(-34)),
            ]);
    }

    private static SpatialObject GarageDoor(DateTimeOffset now)
    {
        const float openingWidth = DoorOpeningRight - DoorOpeningLeft;
        var centreX = (DoorOpeningLeft + DoorOpeningRight) / 2;

        var primitives = new List<SpatialPrimitive>
        {
            Box("door-jamb-left", new Vector3(.1f, DoorOpeningTop, .26f),
                new Vector3(DoorOpeningLeft - .05f, DoorOpeningTop / 2, WallZ), "model", 1, selectable: true),
            Box("door-jamb-right", new Vector3(.1f, DoorOpeningTop, .26f),
                new Vector3(DoorOpeningRight + .05f, DoorOpeningTop / 2, WallZ), "model", 1, selectable: true),
            Box("door-lintel", new Vector3(openingWidth + .2f, .12f, .26f),
                new Vector3(centreX, DoorOpeningTop + .06f, WallZ), "model", 1, selectable: true),
        };

        return Object(
            "object-garagedoor-001", "garage_door", "Garage door", .99f, primitives,
            new SpatialBounds(
                WidthCm: MathF.Round(openingWidth * 100 + 20),
                HeightCm: MathF.Round((DoorOpeningTop + .12f) * 100),
                DepthCm: 26),
            attachedProject: null,
            lastObservedAt: now.AddHours(-6),
            classification: EvidenceKind.Observed,
            openingBounds: new SpatialBounds(
                WidthCm: MathF.Round(openingWidth * 100),
                HeightCm: MathF.Round(DoorOpeningTop * 100),
                DepthCm: null));
    }

    private static SpatialObject PaintCans(DateTimeOffset now)
    {
        var primitives = new List<SpatialPrimitive>();
        var i = 0;
        foreach (var dx in new[] { -.34f, -.06f, .22f })
            primitives.Add(Cylinder($"paint-can-{++i}", new Vector3(.19f, .24f, .19f),
                new Vector3(-2.55f + dx, 1.6f, 1.55f), Vector3.Zero, "model", 3, selectable: true));

        return Object(
            "object-paintcans-001", "paint_cans", "Paint cans", .88f, primitives,
            Measured(primitives),
            attachedProject: null,
            lastObservedAt: now.AddDays(-12),
            classification: EvidenceKind.Inferred,
            memories:
            [
                new ObjectMemory("mem-paint-1", "Matched to the hallway colour",
                    "Label colour is close to the hallway wall reading.", EvidenceKind.Inferred, now.AddDays(-12)),
            ]);
    }

    // -- builders ----------------------------------------------------------

    private static SpatialObject Object(
        string id,
        string semanticClass,
        string displayName,
        float confidence,
        IReadOnlyList<SpatialPrimitive> primitives,
        SpatialBounds bounds,
        ProjectId? attachedProject,
        DateTimeOffset lastObservedAt,
        EvidenceKind classification,
        IReadOnlyList<ObjectMemory>? memories = null,
        IReadOnlyList<LocationHistoryEntry>? locationHistory = null,
        SpatialBounds? openingBounds = null) =>
        new(new SpatialObjectId(id), GarageId, semanticClass, displayName, confidence,
            bounds, primitives, attachedProject, lastObservedAt,
            memories ?? [], locationHistory ?? [], classification, openingBounds);

    private static SpatialPrimitive Box(
        string id, Vector3 size, Vector3 position, string material, int stage, bool selectable = false) =>
        new(id, SpatialPrimitiveKind.Box, size, SpatialTransform.At(position), material, stage, selectable);

    private static SpatialPrimitive Cylinder(
        string id, Vector3 size, Vector3 position, Vector3 rotation,
        string material, int stage, bool selectable = true) =>
        new(id, SpatialPrimitiveKind.Cylinder, size,
            new SpatialTransform(position, rotation, Vector3.One), material, stage, selectable);

    private static SpatialPrimitive Torus(
        string id, Vector3 size, Vector3 position, Vector3 rotation,
        string material, int stage, bool selectable = true) =>
        new(id, SpatialPrimitiveKind.Torus, size,
            new SpatialTransform(position, rotation, Vector3.One), material, stage, selectable);

    /// <summary>Saved dimensions measured off the reconstructed geometry, in centimetres.</summary>
    private static SpatialBounds Measured(IReadOnlyList<SpatialPrimitive> primitives)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var p in primitives)
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

        var size = (max - min) * 100f;
        return new SpatialBounds(
            MathF.Round(size.X),
            MathF.Round(size.Y),
            MathF.Round(size.Z));
    }
}
