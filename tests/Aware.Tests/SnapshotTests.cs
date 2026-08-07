using Aware.Application;
using Aware.Domain;
using Aware.Infrastructure;

namespace Aware.Tests;

/// <summary>
/// The render contract: lenses change annotations, never geometry
/// (06-MOTION-BRIEF: "no camera reset, no geometry rebuild").
/// </summary>
public class SnapshotTests
{
    private static readonly SpatialRoom Room = SampleGarageFactory.Create();
    private static readonly SpatialObjectId Shelf = new("object-shelf-001");
    private static readonly SpatialObjectId Chest = new("object-toolchest-001");

    private static RenderSnapshotFactory Factory() => new();

    [Fact]
    public void SwitchingLensReusesTheSameGeometry()
    {
        var factory = Factory();

        var explore = factory.Create(Room, Chest, SpatialLens.Explore, reducedMotion: false);
        var measure = factory.Create(Room, Chest, SpatialLens.Measure, reducedMotion: false);
        var memories = factory.Create(Room, Chest, SpatialLens.Memories, reducedMotion: false);

        // Reference equality is the contract the renderer keys its mesh cache on.
        Assert.Same(explore.Primitives, measure.Primitives);
        Assert.Same(explore.Primitives, memories.Primitives);
    }

    [Fact]
    public void ChangingSelectionReusesTheSameGeometry()
    {
        var factory = Factory();

        var none = factory.Create(Room, null, SpatialLens.Explore, reducedMotion: false);
        var selected = factory.Create(Room, Chest, SpatialLens.Explore, reducedMotion: false);

        Assert.Same(none.Primitives, selected.Primitives);
        Assert.Null(none.SelectedObjectId);
        Assert.Equal(Chest, selected.SelectedObjectId);
    }

    [Fact]
    public void EditingTheRoomRebuildsGeometry()
    {
        var factory = Factory();
        var before = factory.Create(Room, null, SpatialLens.Explore, reducedMotion: false);

        var edited = Room.WithoutObject(Chest);
        var after = factory.Create(edited, null, SpatialLens.Explore, reducedMotion: false);

        Assert.NotSame(before.Primitives, after.Primitives);
        Assert.True(after.Primitives.Count < before.Primitives.Count);
    }

    [Fact]
    public void EveryPrimitiveIsProjectedIntoTheSnapshot()
    {
        var snapshot = Factory().Create(Room, null, SpatialLens.Explore, reducedMotion: false);
        var expected = Room.Shell.Count + Room.Objects.Sum(o => o.Primitives.Count);

        Assert.Equal(expected, snapshot.Primitives.Count);
        Assert.Equal(Room.Shell.Count, snapshot.Primitives.Count(p => p.ObjectId is null));
    }

    [Fact]
    public void GuidesAppearOnlyInMeasureAndOnlyWithASelection()
    {
        var factory = Factory();

        Assert.Empty(factory.Create(Room, Chest, SpatialLens.Explore, false).Guides);
        Assert.Empty(factory.Create(Room, Chest, SpatialLens.Memories, false).Guides);
        Assert.Empty(factory.Create(Room, null, SpatialLens.Measure, false).Guides);

        Assert.Equal(3, factory.Create(Room, Chest, SpatialLens.Measure, false).Guides.Count);
    }

    [Fact]
    public void AnUnmeasuredAxisProducesAGuideMarkedUnknown()
    {
        var guides = Factory().Create(Room, Shelf, SpatialLens.Measure, false).Guides;

        var unknown = Assert.Single(guides, g => !g.IsKnown);
        Assert.Contains("not measured", unknown.Label);

        // The measured axes still carry a real number.
        Assert.All(guides.Where(g => g.IsKnown), g => Assert.Contains("cm", g.Label));
    }

    [Fact]
    public void PinsAppearOnlyInMemories()
    {
        var factory = Factory();

        Assert.Empty(factory.Create(Room, Chest, SpatialLens.Explore, false).Pins);
        Assert.Empty(factory.Create(Room, Chest, SpatialLens.Measure, false).Pins);
        Assert.NotEmpty(factory.Create(Room, Chest, SpatialLens.Memories, false).Pins);
    }

    [Fact]
    public void MemoriesPinsCarryTheirEvidenceKind()
    {
        var pins = Factory().Create(Room, new SpatialObjectId("object-cabinet-001"),
            SpatialLens.Memories, false).Pins;

        Assert.Contains(pins, p => p.Evidence == EvidenceKind.Observed);
        Assert.Contains(pins, p => p.Evidence == EvidenceKind.Inferred);
    }

    [Fact]
    public void SelectionAnchorSitsOnTopOfTheSelectedObject()
    {
        var snapshot = Factory().Create(Room, Chest, SpatialLens.Explore, false);
        var chest = Room.FindObject(Chest)!;
        var (_, max) = RenderSnapshotFactory.WorldBounds(chest);

        Assert.NotNull(snapshot.SelectionAnchor);
        Assert.Equal(max.Y, snapshot.SelectionAnchor!.Value.Y, precision: 3);
        Assert.Equal("Tool chest", snapshot.SelectedObjectLabel);
    }

    [Fact]
    public void ReducedMotionIsCarriedThroughToTheRenderer()
    {
        Assert.True(Factory().Create(Room, null, SpatialLens.Explore, reducedMotion: true).ReducedMotion);
        Assert.False(Factory().Create(Room, null, SpatialLens.Explore, reducedMotion: false).ReducedMotion);
    }
}
