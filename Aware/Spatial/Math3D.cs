using System.Numerics;

namespace Aware.Spatial;

internal readonly record struct ProjectedPoint(float X, float Y, float Depth);

/// <summary>
/// Right-handed, Y up, perspective camera on a yaw/pitch orbit, focal length
/// derived from the viewport (04-RENDERING world model).
/// </summary>
internal static class Math3D
{
    /// <summary>Pitch clamp from 06-MOTION-BRIEF.</summary>
    public const float MinPitch = -.48f;
    public const float MaxPitch = .08f;

    public const float DefaultYaw = .95f;
    public const float DefaultPitch = -.18f;
    public const float DefaultDistance = 12.4f;

    /// <summary>Camera elevation when pitch is zero. Pitch offsets it downward.</summary>
    private const float BaseElevation = .43f;

    public static Vector3 CameraPosition(float yaw, float pitch, float distance, Vector3 target)
    {
        var elevation = BaseElevation - pitch;
        return target + new Vector3(
            MathF.Sin(yaw) * MathF.Cos(elevation),
            MathF.Sin(elevation),
            MathF.Cos(yaw) * MathF.Cos(elevation)) * distance;
    }

    public static Matrix4x4 CreateView(float yaw, float pitch, float distance, Vector3 target) =>
        Matrix4x4.CreateLookAt(CameraPosition(yaw, pitch, distance, target), target, Vector3.UnitY);

    public static ProjectedPoint Project(
        Vector3 world,
        in Matrix4x4 view,
        float focal,
        float width,
        float height,
        float verticalAnchor)
    {
        var camera = Vector3.Transform(world, view);
        var z = MathF.Max(.05f, -camera.Z);
        var scale = focal / z;
        return new ProjectedPoint(
            width * .5f + camera.X * scale,
            height * verticalAnchor - camera.Y * scale,
            z);
    }

    public static float ClampPitch(float pitch) => Math.Clamp(pitch, MinPitch, MaxPitch);
}

/// <summary>
/// The house easing curve, cubic-bezier(.18,.82,.22,1) from 06-MOTION-BRIEF,
/// solved by Newton iteration. Struct + no allocation so it is safe per frame.
/// </summary>
internal readonly struct CubicBezierEase
{
    public static readonly CubicBezierEase Tray = new(.18f, .82f, .22f, 1f);
    public static readonly CubicBezierEase Settle = new(.22f, .72f, .24f, 1f);

    private readonly float _x1, _y1, _x2, _y2;

    public CubicBezierEase(float x1, float y1, float x2, float y2)
    {
        _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
    }

    public float Ease(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        var guess = t;
        for (var i = 0; i < 5; i++)
        {
            var x = Curve(guess, _x1, _x2) - t;
            if (MathF.Abs(x) < 1e-4f) break;

            var slope = Slope(guess, _x1, _x2);
            if (MathF.Abs(slope) < 1e-6f) break;
            guess -= x / slope;
        }

        return Curve(Math.Clamp(guess, 0f, 1f), _y1, _y2);
    }

    private static float Curve(float t, float a, float b) =>
        ((1f - t) * (1f - t) * 3f * t * a) + ((1f - t) * 3f * t * t * b) + (t * t * t);

    private static float Slope(float t, float a, float b) =>
        (3f * (1f - t) * (1f - t) * a)
        + (6f * (1f - t) * t * (b - a))
        + (3f * t * t * (1f - b));
}
