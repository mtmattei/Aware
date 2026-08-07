using System.Numerics;
using Aware.Application;
using Aware.Domain;
using Aware.Infrastructure;
using Aware.Platform;

namespace Aware.Tests;

/// <summary>
/// The Recognition block of 08-ACCESSIBILITY-TESTS — stable IDs survive
/// geometry updates, corrections override inference, confidence rises as
/// signals match — plus the integrity of the seeded model itself.
/// </summary>
public class RoomModelTests
{
    private static SpatialRoom Room() => SampleGarageFactory.Create();

    // -- stable identity ---------------------------------------------------

    [Fact]
    public void RenamingAnObjectKeepsItsIdAndItsGeometry()
    {
        var room = Room();
        var before = room.FindObject(new SpatialObjectId("object-toolchest-001"))!;

        var after = room.WithObject(before with { DisplayName = "Red tool chest" })
            .FindObject(new SpatialObjectId("object-toolchest-001"))!;

        Assert.Equal(before.Id, after.Id);
        Assert.Equal("Red tool chest", after.DisplayName);
        Assert.Same(before.Primitives, after.Primitives);
        Assert.Equal(before.Bounds, after.Bounds);
    }

    [Fact]
    public void ReclassifyingAnObjectKeepsItsId()
    {
        var room = Room();
        var before = room.FindObject(new SpatialObjectId("object-shelf-001"))!;

        var after = room
            .WithObject(before with
            {
                SemanticClass = "cabinet",
                ClassificationEvidence = EvidenceKind.Corrected,
            })
            .FindObject(new SpatialObjectId("object-shelf-001"))!;

        Assert.Equal(before.Id, after.Id);
        Assert.Equal("cabinet", after.SemanticClass);
        Assert.Equal(EvidenceKind.Corrected, after.ClassificationEvidence);
    }

    [Fact]
    public void CorrectionOverridesInference()
    {
        var room = Room();
        var inferred = room.FindObject(new SpatialObjectId("object-shelf-001"))!;
        Assert.Equal(EvidenceKind.Inferred, inferred.ClassificationEvidence);

        var corrected = inferred with { ClassificationEvidence = EvidenceKind.Corrected };

        // Once corrected the object must never read back as inference.
        Assert.NotEqual(EvidenceKind.Inferred, corrected.ClassificationEvidence);
    }

    [Fact]
    public void GeometryRefinementDoesNotDisturbOtherObjects()
    {
        var room = Room();
        var target = room.FindObject(new SpatialObjectId("object-cabinet-001"))!;

        var refined = room.WithObject(target with
        {
            Bounds = new SpatialBounds(200, 200, 200),
        });

        Assert.Equal(room.Objects.Count, refined.Objects.Count);

        foreach (var original in room.Objects.Where(o => o.Id != target.Id))
            Assert.Same(original, refined.FindObject(original.Id));
    }

    [Fact]
    public void ForgettingAnObjectRemovesOnlyThatObject()
    {
        var room = Room();
        var id = new SpatialObjectId("object-bicycle-001");

        var without = room.WithoutObject(id);

        Assert.Null(without.FindObject(id));
        Assert.Equal(room.Objects.Count - 1, without.Objects.Count);
    }

    [Fact]
    public void EveryObjectIdIsUnique()
    {
        var room = Room();
        var ids = room.Objects.Select(o => o.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void EveryPrimitiveIdIsUnique()
    {
        var room = Room();
        var ids = room.Shell.Concat(room.Objects.SelectMany(o => o.Primitives))
            .Select(p => p.Id)
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    // -- seeded model integrity -------------------------------------------

    /// <summary>
    /// The sample data shipped with the briefs described a different workbench
    /// than its own primitives, which would have made the Measure lens label
    /// guides that disagreed with the model. Saved bounds are now measured off
    /// the geometry; this keeps them that way.
    /// </summary>
    [Fact]
    public void SavedBoundsMatchTheReconstructedGeometry()
    {
        foreach (var obj in Room().Objects)
        {
            if (obj.Id.Value == "object-garagedoor-001") continue; // frame, not opening
            if (!obj.Bounds.IsComplete) continue;

            var (min, max) = RenderSnapshotFactory.WorldBounds(obj);
            var size = (max - min) * 100f;

            Assert.Equal(MathF.Round(size.X), obj.Bounds.WidthCm!.Value, 0);
            Assert.Equal(MathF.Round(size.Y), obj.Bounds.HeightCm!.Value, 0);
            Assert.Equal(MathF.Round(size.Z), obj.Bounds.DepthCm!.Value, 0);
        }
    }

    [Fact]
    public void TheStorageShelfDepthIsDeliberatelyUnmeasured()
    {
        var shelf = Room().FindObject(new SpatialObjectId("object-shelf-001"))!;

        Assert.False(shelf.Bounds.IsComplete);
        Assert.Null(shelf.Bounds.DepthCm);
        Assert.NotNull(shelf.Bounds.WidthCm);
    }

    [Fact]
    public void EveryReconstructionStageIsPopulated()
    {
        var room = Room();
        var stages = room.Shell.Concat(room.Objects.SelectMany(o => o.Primitives))
            .Select(p => p.ReconstructionStage)
            .Distinct()
            .OrderBy(s => s)
            .ToArray();

        // 06-MOTION-BRIEF defines four assembly windows; an empty stage would
        // leave a visible gap in the reconstruction.
        Assert.Equal([0, 1, 2, 3], stages);
    }

    [Fact]
    public void ShellPrimitivesAreNotSelectable()
    {
        Assert.All(Room().Shell, p => Assert.False(p.IsSelectable));
    }

    [Fact]
    public void PrimitiveCountStaysWithinTheRenderBudget()
    {
        var room = Room();
        var count = room.Shell.Count + room.Objects.Sum(o => o.Primitives.Count);

        // 04-RENDERING targets under 150 visible semantic primitives for the MVP.
        Assert.InRange(count, 1, 150);
    }

    [Fact]
    public void EveryAttachedProjectResolves()
    {
        var room = Room();

        foreach (var obj in room.Objects.Where(o => o.AttachedProjectId is not null))
            Assert.NotNull(room.FindProject(obj.AttachedProjectId));
    }

    // -- recognition -------------------------------------------------------

    [Fact]
    public async Task RecognitionRaisesConfidenceAndSettlesStable()
    {
        var room = Room();
        var service = new SimulatedRoomRecognitionService(new FakeMotionSettings(reducedMotion: true));

        var states = new List<RecognitionState>();
        await foreach (var state in service.RecognizeAsync(room, CancellationToken.None))
            states.Add(state);

        Assert.NotEmpty(states);

        // Progress and confidence never move backwards as signals accumulate.
        for (var i = 1; i < states.Count; i++)
        {
            Assert.True(states[i].Progress >= states[i - 1].Progress);
            Assert.True(states[i].Confidence >= states[i - 1].Confidence);
        }

        var final = states[^1];
        Assert.True(final.IsStable);
        Assert.Equal(1f, final.Progress, precision: 3);
        Assert.Equal(room.Confidence, final.Confidence, precision: 3);

        // Never an empty spinner: every step names what resolved (02-UX-FLOWS).
        Assert.All(states, s => Assert.False(string.IsNullOrWhiteSpace(s.Message)));
    }

    [Fact]
    public async Task RecognitionCanBeCancelledMidway()
    {
        using var cts = new CancellationTokenSource();
        var service = new SimulatedRoomRecognitionService(new FakeMotionSettings(reducedMotion: false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.RecognizeAsync(Room(), cts.Token))
                await cts.CancelAsync();
        });
    }

    private sealed class FakeMotionSettings(bool reducedMotion) : IMotionSettings
    {
        public bool ReducedMotion { get; private set; } = reducedMotion;

        public event EventHandler? Changed;

        public void SetUserOverride(bool? reducedMotion)
        {
            ReducedMotion = reducedMotion ?? false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
