using System.Numerics;
using Aware.Application;
using Aware.Domain;
using Aware.Spatial;
using SkiaSharp;

namespace Aware.Tests;

/// <summary>
/// The Hit testing block of tests/TEST-CHECKLIST.md: closest visible object
/// wins, empty space clears, and geometry that has not assembled yet is not
/// selectable.
/// </summary>
public class HitTestingTests
{
    private const float Width = 1024f;
    private const float Height = 610f;
    private const float Anchor = .52f;

    private static readonly Vector3 Target = new(0f, 1f, 0f);

    /// <summary>
    /// The orbit target always projects to the viewport centre line, whatever
    /// the focal length, so anything placed on the camera-to-target ray covers
    /// this pixel. That lets the tests locate geometry without reaching into the
    /// renderer's private framing maths.
    /// </summary>
    private static (float X, float Y) TargetPixel => (Width / 2f, Height * Anchor);

    private static Vector3 TowardCamera(float metres)
    {
        var camera = Math3D.CameraPosition(Math3D.DefaultYaw, Math3D.DefaultPitch,
            Math3D.DefaultDistance, Target);
        return Target + Vector3.Normalize(camera - Target) * metres;
    }

    private static RenderPrimitive Box(string id, Vector3 position, bool selectable = true, int stage = 0) =>
        new(id, new SpatialObjectId(id), SpatialPrimitiveKind.Box,
            new Vector3(1f, 1f, 1f), SpatialTransform.At(position),
            "model", stage, selectable);

    private static SpatialRenderSnapshot Snapshot(params RenderPrimitive[] primitives) =>
        new(new RoomId("room-test"), "Test room", 1f, primitives,
            SelectedObjectId: null, SelectedObjectLabel: null, SelectionAnchor: null,
            ActiveLens: SpatialLens.Explore, Guides: [], Pins: [], ReducedMotion: false);

    private static SpatialRoomRenderer Rendered(SpatialRenderSnapshot snapshot, float assemblyMs = 6000f)
    {
        var renderer = new SpatialRoomRenderer();
        renderer.SetSnapshot(snapshot);

        var frame = new RenderFrame(
            Math3D.DefaultYaw, Math3D.DefaultPitch, Math3D.DefaultDistance,
            AssemblyMs: assemblyMs,
            SelectionFactor: 0f,
            FadingObjectId: null,
            FadingFactor: 0f,
            AnnotationAlpha: 1f,
            VerticalAnchor: Anchor,
            ReducedMotion: false);

        using var surface = SKSurface.Create(new SKImageInfo((int)Width, (int)Height));
        renderer.Render(surface.Canvas, Width, Height, frame);

        return renderer;
    }

    [Fact]
    public void SelectableGeometryUnderThePointerIsHit()
    {
        using var renderer = Rendered(Snapshot(Box("only", Target)));

        var hit = renderer.HitTest(TargetPixel.X, TargetPixel.Y);

        Assert.Equal(new SpatialObjectId("only"), hit);
    }

    [Fact]
    public void ClosestObjectWinsWhenTwoOverlap()
    {
        // Both boxes sit on the camera-to-target ray, so they cover the same
        // pixel; only depth can separate them.
        var near = Box("near", TowardCamera(2.5f));
        var far = Box("far", TowardCamera(-2.5f));

        using var frontFirst = Rendered(Snapshot(near, far));
        using var backFirst = Rendered(Snapshot(far, near));

        // The answer must not depend on the order the primitives arrived in.
        Assert.Equal(new SpatialObjectId("near"), frontFirst.HitTest(TargetPixel.X, TargetPixel.Y));
        Assert.Equal(new SpatialObjectId("near"), backFirst.HitTest(TargetPixel.X, TargetPixel.Y));
    }

    [Fact]
    public void EmptySpaceReturnsNothing()
    {
        using var renderer = Rendered(Snapshot(Box("only", Target)));

        // Corners are far outside a one-metre box at the centre.
        Assert.Null(renderer.HitTest(2f, 2f));
        Assert.Null(renderer.HitTest(Width - 2f, 2f));
        Assert.Null(renderer.HitTest(2f, Height - 2f));
        Assert.Null(renderer.HitTest(Width - 2f, Height - 2f));
    }

    [Fact]
    public void GeometryThatHasNotAssembledYetIsNotSelectable()
    {
        var snapshot = Snapshot(Box("late", Target, stage: 3));

        using var atStart = Rendered(snapshot, assemblyMs: 0f);
        using var settled = Rendered(snapshot, assemblyMs: 6000f);

        Assert.Null(atStart.HitTest(TargetPixel.X, TargetPixel.Y));
        Assert.Equal(new SpatialObjectId("late"), settled.HitTest(TargetPixel.X, TargetPixel.Y));
    }

    [Fact]
    public void NonSelectablePrimitivesAreNeverHit()
    {
        using var renderer = Rendered(Snapshot(Box("scenery", Target, selectable: false)));

        Assert.Null(renderer.HitTest(TargetPixel.X, TargetPixel.Y));
    }

    [Fact]
    public void ShellPrimitivesWithNoObjectAreNeverHit()
    {
        var shell = new RenderPrimitive("floor", ObjectId: null, SpatialPrimitiveKind.Box,
            new Vector3(6f, .2f, 6f), SpatialTransform.At(Target), "floor", 0, IsSelectable: true);

        using var renderer = Rendered(Snapshot(shell));

        Assert.Null(renderer.HitTest(TargetPixel.X, TargetPixel.Y));
    }

    [Theory]
    [InlineData(390, 844)]     // 05-DESIGN-SYSTEM reference viewport
    [InlineData(1024, 610)]
    [InlineData(1920, 1080)]
    public void HitTestingHoldsAtAnyAspectRatio(int width, int height)
    {
        var renderer = new SpatialRoomRenderer();
        renderer.SetSnapshot(Snapshot(Box("only", Target)));

        var frame = new RenderFrame(
            Math3D.DefaultYaw, Math3D.DefaultPitch, Math3D.DefaultDistance,
            6000f, 0f, null, 0f, 1f, Anchor, false);

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        renderer.Render(surface.Canvas, width, height, frame);

        Assert.Equal(new SpatialObjectId("only"), renderer.HitTest(width / 2f, height * Anchor));
        renderer.Dispose();
    }

    [Fact]
    public void OrbitingDoesNotLoseTheObjectUnderTheCentre()
    {
        // The target pixel is the projection of the orbit target at every yaw,
        // so a box at the target must stay selectable all the way round.
        for (var yaw = -3f; yaw < 3f; yaw += .5f)
        {
            var renderer = new SpatialRoomRenderer();
            renderer.SetSnapshot(Snapshot(Box("only", Target)));

            var frame = new RenderFrame(yaw, Math3D.DefaultPitch, Math3D.DefaultDistance,
                6000f, 0f, null, 0f, 1f, Anchor, false);

            using (var surface = SKSurface.Create(new SKImageInfo((int)Width, (int)Height)))
                renderer.Render(surface.Canvas, Width, Height, frame);

            Assert.Equal(new SpatialObjectId("only"),
                renderer.HitTest(TargetPixel.X, TargetPixel.Y));

            renderer.Dispose();
        }
    }
}
