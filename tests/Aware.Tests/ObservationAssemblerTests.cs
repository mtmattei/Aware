using System.Numerics;
using Aware.Application;
using Aware.Domain;
using Aware.Infrastructure;
using Aware.Platform;

namespace Aware.Tests;

/// <summary>
/// Folding observations into a room. This is the step that turns a scan into a
/// stored model, so the properties worth pinning are the ones that would quietly
/// corrupt a room rather than fail loudly.
/// </summary>
public class ObservationAssemblerTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly ObservationAssembler Assembler = new();

    private static SpatialRoom Unmodelled() =>
        UnmodelledRoomFactory.Create("Kitchen", Fingerprint, When);

    private static readonly RoomFingerprint Fingerprint = new(
        WifiFeatureHash: string.Empty,
        BluetoothFeatureHash: "aa+bb",
        AmbientLightVector: new Vector3(120f, 0f, 0f),
        MagneticVector: new Vector3(12f, -44f, 61f),
        PressureHpa: 1011f,
        AcousticEmbeddingId: null);

    private static SpatialObservation Observation(
        params (Vector3 Position, Vector3 Size)[] surfaces) =>
        new(
            Timestamp: When,
            Source: "test",
            Quality: .8f,
            Geometry: [.. surfaces.Select(s => new ObservedPrimitive(
                SpatialPrimitiveKind.Box,
                s.Size,
                new SpatialTransform(s.Position, Vector3.Zero, Vector3.One),
                .9f))],
            ObjectCandidates: []);

    [Fact]
    public void ObservingGeometryMakesAnUnmodelledRoomModelled()
    {
        var room = Unmodelled();
        Assert.False(room.IsModelled);

        var after = Assembler.Apply(room, Observation((new Vector3(0f, 0f, 0f), new Vector3(4f, .02f, 3f))));

        Assert.True(after.IsModelled);
        Assert.Single(after.Shell);
    }

    /// <summary>
    /// The whole reason capture writes under the existing room: a recognition-only
    /// room must keep its identity, its name and its place while gaining a shape.
    /// </summary>
    [Fact]
    public void ScanningKeepsTheRoomsIdentityNameAndPlace()
    {
        var room = Unmodelled();

        var after = Assembler.Apply(room, Observation((Vector3.Zero, new Vector3(4f, .02f, 3f))));

        Assert.Equal(room.Id, after.Id);
        Assert.Equal(room.Name, after.Name);
        Assert.Equal(room.Fingerprint, after.Fingerprint);
        Assert.True(after.IsLinkedToPlace);
    }

    /// <summary>
    /// An adapter re-reports the same plane every frame as its estimate improves.
    /// Without this the shell grows without bound and the room fills with
    /// duplicate surfaces stacked on each other.
    /// </summary>
    [Fact]
    public void ReobservingTheSameSurfaceRefinesItInsteadOfDuplicating()
    {
        var room = Unmodelled();

        var first = Assembler.Apply(room, Observation((Vector3.Zero, new Vector3(3f, .02f, 2f))));
        // Same surface, seen again slightly larger and a few centimetres over.
        var second = Assembler.Apply(first, Observation((new Vector3(.08f, 0f, .05f), new Vector3(4f, .02f, 3f))));

        Assert.Single(second.Shell);
        Assert.Equal(4f, second.Shell[0].Size.X, precision: 3);
        Assert.Equal(first.Shell[0].Id, second.Shell[0].Id);
    }

    [Fact]
    public void GenuinelySeparateSurfacesAreKept()
    {
        var room = Unmodelled();

        var after = Assembler.Apply(room, Observation(
            (Vector3.Zero, new Vector3(4f, .02f, 3f)),
            (new Vector3(2f, 1.2f, 0f), new Vector3(4f, 2.4f, .02f))));

        Assert.Equal(2, after.Shell.Count);
    }

    /// <summary>
    /// Floor and wall are the only two materials the renderer distinguishes for
    /// the shell, and a surface at head height drawn as floor reads as a bug.
    /// </summary>
    [Fact]
    public void SurfacesAreMaterialisedByHeight()
    {
        var after = Assembler.Apply(Unmodelled(), Observation(
            (Vector3.Zero, new Vector3(4f, .02f, 3f)),
            (new Vector3(2f, 1.2f, 0f), new Vector3(4f, 2.4f, .02f))));

        Assert.Equal("floor", after.Shell[0].MaterialKey);
        Assert.Equal("wall", after.Shell[1].MaterialKey);
    }

    [Fact]
    public void EachScanAdvancesTheModelVersion()
    {
        var room = Unmodelled();
        var after = Assembler.Apply(room, Observation((Vector3.Zero, Vector3.One)));

        Assert.Equal(room.ModelVersion + 1, after.ModelVersion);
    }

    /// <summary>
    /// Scanning a room that already has a model must refine it, never wipe it —
    /// losing a mapped garage by scanning it again is the worst outcome here.
    /// </summary>
    [Fact]
    public void ScanningAModelledRoomKeepsWhatWasAlreadyThere()
    {
        var garage = SampleGarageFactory.Create();
        var shellBefore = garage.Shell.Count;
        var objectsBefore = garage.Objects.Count;

        var after = Assembler.Apply(garage, Observation((new Vector3(9f, 0f, 9f), Vector3.One)));

        Assert.Equal(shellBefore + 1, after.Shell.Count);
        Assert.Equal(objectsBefore, after.Objects.Count);
        Assert.All(garage.Objects, o => Assert.NotNull(after.FindObject(o.Id)));
    }

    /// <summary>
    /// A candidate seen twice is one object, not two, and re-seeing it must not
    /// discard the memories or corrections already attached to it.
    /// </summary>
    [Fact]
    public void AnObjectSeenTwiceStaysOneObject()
    {
        var room = Unmodelled();

        var candidate = new SpatialObservation(
            When, "test", .8f, [],
            [new ObservedObjectCandidate("workbench",
                new SpatialTransform(new Vector3(1f, .5f, 1f), Vector3.Zero, Vector3.One), .7f)]);

        var first = Assembler.Apply(room, candidate);
        var second = Assembler.Apply(first, candidate);

        Assert.Single(second.Objects);
        Assert.Equal(first.Objects[0].Id, second.Objects[0].Id);
    }

    /// <summary>
    /// Bounds arrive later than position, and Measure renders an unmeasured axis
    /// explicitly. Inventing dimensions here would put fabricated centimetres in
    /// front of the user.
    /// </summary>
    [Fact]
    public void AFreshlyObservedObjectHasNoInventedDimensions()
    {
        var room = Unmodelled();

        var after = Assembler.Apply(room, new SpatialObservation(
            When, "test", .8f, [],
            [new ObservedObjectCandidate("cabinet", SpatialTransform.Identity, .7f)]));

        var observed = Assert.Single(after.Objects);
        Assert.False(observed.Bounds.IsComplete);
        Assert.Equal(EvidenceKind.Observed, observed.ClassificationEvidence);
    }
}
