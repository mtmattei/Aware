using Aware.Application;
using Aware.Domain;
using Aware.Infrastructure;

namespace Aware.Tests;

/// <summary>
/// The Actions block of 08-ACCESSIBILITY-TESTS: the workbench resolves its
/// project, Measure resolves dimensions, Memories resolves history — and the
/// same object keeps its identity through all three lenses.
/// </summary>
public class ObjectActionTests
{
    private static readonly SpatialRoom Room = SampleGarageFactory.Create();
    private static readonly ObjectActionResolver Resolver = new();

    private static SpatialObject Find(string id) =>
        Room.FindObject(new SpatialObjectId(id))
        ?? throw new InvalidOperationException($"Seeded room is missing {id}.");

    [Fact]
    public void ExploreResolvesTheWorkbenchProject()
    {
        var tray = Resolver.Resolve(Room, Find("object-workbench-001"), SpatialLens.Explore);

        Assert.Equal("Workbench", tray.Title);
        Assert.Contains(tray.Facts, f => f.Label == "Project" && f.Value == "Cabinet build");
        Assert.Contains(tray.Actions, a => a.Id == ObjectActionIds.ResumeProject && a.IsPrimary);
    }

    [Fact]
    public void ExploreOffersNoProjectResumeForAnObjectWithout()
    {
        var tray = Resolver.Resolve(Room, Find("object-bicycle-001"), SpatialLens.Explore);

        Assert.DoesNotContain(tray.Actions, a => a.Id == ObjectActionIds.ResumeProject);
        Assert.Contains(tray.Actions, a => a.Id == ObjectActionIds.Open);
    }

    [Fact]
    public void MeasureResolvesKnownDimensions()
    {
        var chest = Find("object-toolchest-001");
        var tray = Resolver.Resolve(Room, chest, SpatialLens.Measure);

        Assert.Equal("Known dimensions", tray.Eyebrow);
        Assert.Equal(
            $"{chest.Bounds.WidthCm:0} × {chest.Bounds.DepthCm:0} × {chest.Bounds.HeightCm:0} cm",
            tray.Description);
    }

    [Fact]
    public void MeasureMarksAnUnmeasuredAxisExplicitly()
    {
        var shelf = Find("object-shelf-001");
        var tray = Resolver.Resolve(Room, shelf, SpatialLens.Measure);

        Assert.Equal("Partial dimensions", tray.Eyebrow);

        var depth = Assert.Single(tray.Facts, f => f.Label == "Depth");
        Assert.False(depth.IsKnown);
        Assert.Equal("Never measured", depth.Value);

        // The axes that were measured must still read as known.
        Assert.True(Assert.Single(tray.Facts, f => f.Label == "Width").IsKnown);
    }

    [Fact]
    public void MemoriesResolvesHistoryAndDistinguishesInference()
    {
        var cabinet = Find("object-cabinet-001");
        var tray = Resolver.Resolve(Room, cabinet, SpatialLens.Memories);

        Assert.Contains(tray.Facts, f => f.Evidence == EvidenceKind.Observed);
        Assert.Contains(tray.Facts, f => f.Evidence == EvidenceKind.Inferred);
        Assert.Contains(tray.Facts, f => f.Value.Contains("back wall")); // location history
    }

    [Fact]
    public void MemoriesSaysSoWhenNothingIsRecorded()
    {
        var shelf = Find("object-shelf-001");
        var tray = Resolver.Resolve(Room, shelf, SpatialLens.Memories);

        Assert.Contains(tray.Facts, f => f.Value.Contains("Nothing recorded"));
    }

    [Fact]
    public void EveryLensKeepsTheSameObjectIdentity()
    {
        var chest = Find("object-toolchest-001");

        foreach (var lens in Enum.GetValues<SpatialLens>())
        {
            var tray = Resolver.Resolve(Room, chest, lens);
            Assert.Equal(chest.DisplayName, tray.Title);
            Assert.NotEmpty(tray.Actions);
        }
    }

    // -- compare fit -------------------------------------------------------

    [Fact]
    public void TheGarageDoorIsTheRoomsOpening()
    {
        var opening = ObjectActionResolver.FindOpening(Room, Find("object-cabinet-001"));

        Assert.NotNull(opening);
        Assert.Equal("Garage door", opening.DisplayName);
        Assert.Equal(244f, opening.OpeningBounds!.WidthCm);
        Assert.Equal(213f, opening.OpeningBounds.HeightCm);
    }

    [Fact]
    public void CabinetFitsThroughTheGarageDoorTurnedOnItsSide()
    {
        var cabinet = Find("object-cabinet-001");
        var door = Find("object-garagedoor-001");

        var verdict = ObjectActionResolver.CompareFit(cabinet, door);

        // 76 and 115 are the two smallest sides; against a 244 x 213 opening the
        // tightest clearance is 213 - 115 = 98 cm.
        Assert.Contains("Fits through", verdict);
        Assert.Contains("98 cm", verdict);
    }

    [Fact]
    public void FitCannotBeConfirmedWhenAnAxisWasNeverMeasured()
    {
        var shelf = Find("object-shelf-001");
        var door = Find("object-garagedoor-001");

        var verdict = ObjectActionResolver.CompareFit(shelf, door);

        Assert.Contains("unmeasured", verdict);
        Assert.DoesNotContain("Fits through", verdict);
    }

    [Fact]
    public void AnObjectTooLargeForTheOpeningIsRefused()
    {
        var door = Find("object-garagedoor-001");
        var oversized = Find("object-cabinet-001") with
        {
            Bounds = new SpatialBounds(WidthCm: 400, HeightCm: 400, DepthCm: 400),
        };

        var verdict = ObjectActionResolver.CompareFit(oversized, door);

        Assert.Contains("Does not fit", verdict);
    }
}
