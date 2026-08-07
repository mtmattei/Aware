using System.Numerics;
using Aware.Spatial;

namespace Aware.Tests;

/// <summary>
/// The Projection block of tests/TEST-CHECKLIST.md and 08-ACCESSIBILITY-TESTS:
/// centre preservation, resize, pitch clamp, depth ordering.
/// </summary>
public class ProjectionTests
{
    private static readonly Vector3 Target = new(0f, 1f, 0f);

    private static Matrix4x4 DefaultView() =>
        Math3D.CreateView(Math3D.DefaultYaw, Math3D.DefaultPitch, Math3D.DefaultDistance, Target);

    [Fact]
    public void OrbitTargetProjectsToTheViewportCentreLine()
    {
        var projected = Math3D.Project(Target, DefaultView(), focal: 500f,
            width: 1024f, height: 610f, verticalAnchor: .52f);

        Assert.Equal(512f, projected.X, precision: 2);
        Assert.Equal(610f * .52f, projected.Y, precision: 2);
    }

    [Theory]
    [InlineData(390, 844)]    // 05-DESIGN-SYSTEM reference viewport
    [InlineData(1024, 610)]
    [InlineData(1920, 1080)]
    [InlineData(844, 390)]    // landscape
    public void CentreIsPreservedAcrossViewportSizes(float width, float height)
    {
        var projected = Math3D.Project(Target, DefaultView(), focal: 500f,
            width, height, verticalAnchor: .52f);

        Assert.Equal(width / 2f, projected.X, precision: 2);
        Assert.Equal(height * .52f, projected.Y, precision: 2);
    }

    [Theory]
    [InlineData(-5f, Math3D.MinPitch)]
    [InlineData(-0.48f, Math3D.MinPitch)]
    [InlineData(-0.2f, -0.2f)]
    [InlineData(0.08f, Math3D.MaxPitch)]
    [InlineData(5f, Math3D.MaxPitch)]
    public void PitchClampsToTheBriefsRange(float input, float expected) =>
        Assert.Equal(expected, Math3D.ClampPitch(input), precision: 5);

    [Fact]
    public void PitchClampRangeMatchesTheMotionBrief()
    {
        Assert.Equal(-.48f, Math3D.MinPitch, precision: 5);
        Assert.Equal(.08f, Math3D.MaxPitch, precision: 5);
    }

    [Fact]
    public void CameraStaysAboveTheFloorAcrossTheWholePitchRange()
    {
        for (var pitch = Math3D.MinPitch; pitch <= Math3D.MaxPitch; pitch += .01f)
        {
            var camera = Math3D.CameraPosition(pitch: pitch, yaw: Math3D.DefaultYaw,
                distance: Math3D.DefaultDistance, target: Target);

            Assert.True(camera.Y > 0f,
                $"Camera dropped to or below the floor at pitch {pitch}: Y = {camera.Y}.");
        }
    }

    [Fact]
    public void NearerGeometryProjectsWithSmallerDepth()
    {
        var view = DefaultView();
        var camera = Math3D.CameraPosition(Math3D.DefaultYaw, Math3D.DefaultPitch,
            Math3D.DefaultDistance, Target);

        // A point pulled toward the camera must sort in front of the target.
        var toCamera = Vector3.Normalize(camera - Target);
        var nearer = Target + toCamera * 3f;

        var far = Math3D.Project(Target, view, 500f, 1024f, 610f, .52f);
        var near = Math3D.Project(nearer, view, 500f, 1024f, 610f, .52f);

        Assert.True(near.Depth < far.Depth,
            $"Depth ordering inverted: near = {near.Depth}, far = {far.Depth}.");
    }

    [Fact]
    public void DepthOrderingIsStableUnderOrbit()
    {
        var camera = Math3D.CameraPosition(Math3D.DefaultYaw, Math3D.DefaultPitch,
            Math3D.DefaultDistance, Target);
        var nearer = Target + Vector3.Normalize(camera - Target) * 3f;

        // Whatever the yaw, the point between the camera and the target keeps
        // its depth ordering once the camera moves with it.
        for (var yaw = -3f; yaw < 3f; yaw += .25f)
        {
            var view = Math3D.CreateView(yaw, Math3D.DefaultPitch, Math3D.DefaultDistance, Target);
            var orbitCamera = Math3D.CameraPosition(yaw, Math3D.DefaultPitch, Math3D.DefaultDistance, Target);
            var between = Target + Vector3.Normalize(orbitCamera - Target) * 3f;

            var far = Math3D.Project(Target, view, 500f, 1024f, 610f, .52f);
            var near = Math3D.Project(between, view, 500f, 1024f, 610f, .52f);

            Assert.True(near.Depth < far.Depth, $"Depth ordering inverted at yaw {yaw}.");
        }

        Assert.NotEqual(Vector3.Zero, nearer);
    }

    [Fact]
    public void ProjectionNeverDividesByZeroBehindTheCamera()
    {
        var view = DefaultView();
        var camera = Math3D.CameraPosition(Math3D.DefaultYaw, Math3D.DefaultPitch,
            Math3D.DefaultDistance, Target);

        // A point well behind the camera must still produce finite screen
        // coordinates rather than NaN or infinity.
        var behind = camera + Vector3.Normalize(camera - Target) * 10f;
        var projected = Math3D.Project(behind, view, 500f, 1024f, 610f, .52f);

        Assert.True(float.IsFinite(projected.X));
        Assert.True(float.IsFinite(projected.Y));
        Assert.True(projected.Depth > 0f);
    }
}
