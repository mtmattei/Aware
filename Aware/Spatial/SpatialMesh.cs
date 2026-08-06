using System.Numerics;
using Aware.Application;
using Aware.Domain;

namespace Aware.Spatial;

internal sealed class MeshFace
{
    public required int[] Indices { get; init; }
    public required Vector3 Normal { get; init; }
    public required Vector3 Centre { get; init; }
}

/// <summary>
/// A primitive tessellated into world-space geometry. Built once when the
/// snapshot changes, never per frame (04-RENDERING: no per-frame allocations).
/// </summary>
internal sealed class PrimitiveMesh
{
    public required RenderPrimitive Source { get; init; }
    public required Vector3[] Vertices { get; init; }
    public required MeshFace[] Faces { get; init; }

    /// <summary>Index path for stroked primitives (lines, torus rims). Empty for solids.</summary>
    public required int[] Stroke { get; init; }
    public required bool StrokeClosed { get; init; }
    public required float StrokeThickness { get; init; }

    public required Vector3 Centre { get; init; }
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    public bool IsShell => Source.ObjectId is null;
}

internal static class SpatialMesh
{
    private const int CylinderSegments = 14;
    private const int TorusSegments = 26;

    public static PrimitiveMesh[] Build(IReadOnlyList<RenderPrimitive> primitives)
    {
        var meshes = new PrimitiveMesh[primitives.Count];
        for (var i = 0; i < primitives.Count; i++)
            meshes[i] = Build(primitives[i]);
        return meshes;
    }

    private static PrimitiveMesh Build(RenderPrimitive p)
    {
        var rotation = Matrix4x4.CreateFromYawPitchRoll(
            p.Transform.RotationRadians.Y,
            p.Transform.RotationRadians.X,
            p.Transform.RotationRadians.Z);

        var size = p.Size * p.Transform.Scale;
        var origin = p.Transform.Position;

        return p.Kind switch
        {
            SpatialPrimitiveKind.Cylinder => Cylinder(p, size, rotation, origin),
            SpatialPrimitiveKind.Torus => Torus(p, size, rotation, origin),
            SpatialPrimitiveKind.Line => Line(p, size, rotation, origin),
            _ => Box(p, size, rotation, origin),
        };
    }

    private static PrimitiveMesh Box(RenderPrimitive p, Vector3 size, Matrix4x4 rotation, Vector3 origin)
    {
        var half = size * .5f;

        // 0..3 bottom (y-), 4..7 top (y+); each ring wound counter-clockwise
        // seen from outside.
        var vertices = new[]
        {
            World(new Vector3(-half.X, -half.Y, -half.Z), rotation, origin),
            World(new Vector3( half.X, -half.Y, -half.Z), rotation, origin),
            World(new Vector3( half.X, -half.Y,  half.Z), rotation, origin),
            World(new Vector3(-half.X, -half.Y,  half.Z), rotation, origin),
            World(new Vector3(-half.X,  half.Y, -half.Z), rotation, origin),
            World(new Vector3( half.X,  half.Y, -half.Z), rotation, origin),
            World(new Vector3( half.X,  half.Y,  half.Z), rotation, origin),
            World(new Vector3(-half.X,  half.Y,  half.Z), rotation, origin),
        };

        var faces = new[]
        {
            Face(vertices, [4, 5, 6, 7], Direction(Vector3.UnitY, rotation)),      // top
            Face(vertices, [3, 2, 1, 0], Direction(-Vector3.UnitY, rotation)),     // bottom
            Face(vertices, [2, 3, 7, 6], Direction(Vector3.UnitZ, rotation)),      // front (+z)
            Face(vertices, [0, 1, 5, 4], Direction(-Vector3.UnitZ, rotation)),     // back (-z)
            Face(vertices, [1, 2, 6, 5], Direction(Vector3.UnitX, rotation)),      // right (+x)
            Face(vertices, [3, 0, 4, 7], Direction(-Vector3.UnitX, rotation)),     // left (-x)
        };

        return Finish(p, vertices, faces, [], closed: false, thickness: 0f);
    }

    private static PrimitiveMesh Cylinder(RenderPrimitive p, Vector3 size, Matrix4x4 rotation, Vector3 origin)
    {
        var radiusX = size.X * .5f;
        var radiusZ = size.Z * .5f;
        var halfHeight = size.Y * .5f;

        var vertices = new Vector3[CylinderSegments * 2];
        var radial = new Vector3[CylinderSegments];

        for (var i = 0; i < CylinderSegments; i++)
        {
            var angle = MathF.Tau * i / CylinderSegments;
            var x = MathF.Cos(angle) * radiusX;
            var z = MathF.Sin(angle) * radiusZ;

            vertices[i] = World(new Vector3(x, -halfHeight, z), rotation, origin);
            vertices[i + CylinderSegments] = World(new Vector3(x, halfHeight, z), rotation, origin);
            radial[i] = Direction(Vector3.Normalize(new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle))), rotation);
        }

        var faces = new MeshFace[CylinderSegments + 2];

        for (var i = 0; i < CylinderSegments; i++)
        {
            var next = (i + 1) % CylinderSegments;
            var normal = Vector3.Normalize(radial[i] + radial[next]);
            faces[i] = Face(vertices, [i, next, next + CylinderSegments, i + CylinderSegments], normal);
        }

        var top = new int[CylinderSegments];
        var bottom = new int[CylinderSegments];
        for (var i = 0; i < CylinderSegments; i++)
        {
            top[i] = i + CylinderSegments;
            bottom[i] = CylinderSegments - 1 - i;
        }

        faces[CylinderSegments] = Face(vertices, top, Direction(Vector3.UnitY, rotation));
        faces[CylinderSegments + 1] = Face(vertices, bottom, Direction(-Vector3.UnitY, rotation));

        return Finish(p, vertices, faces, [], closed: false, thickness: 0f);
    }

    private static PrimitiveMesh Torus(RenderPrimitive p, Vector3 size, Matrix4x4 rotation, Vector3 origin)
    {
        // Major circle in the local XY plane; the tube is drawn as a stroke of
        // its diameter, which reads correctly for wheels and rims at this scale
        // and costs a fraction of a full torus tessellation.
        var majorRadius = size.X * .5f;
        var tube = MathF.Max(size.Y, .012f);

        var vertices = new Vector3[TorusSegments];
        var stroke = new int[TorusSegments];

        for (var i = 0; i < TorusSegments; i++)
        {
            var angle = MathF.Tau * i / TorusSegments;
            vertices[i] = World(
                new Vector3(MathF.Cos(angle) * majorRadius, MathF.Sin(angle) * majorRadius, 0),
                rotation, origin);
            stroke[i] = i;
        }

        return Finish(p, vertices, [], stroke, closed: true, thickness: tube);
    }

    private static PrimitiveMesh Line(RenderPrimitive p, Vector3 size, Matrix4x4 rotation, Vector3 origin)
    {
        var half = size.Y * .5f;
        var vertices = new[]
        {
            World(new Vector3(0, -half, 0), rotation, origin),
            World(new Vector3(0,  half, 0), rotation, origin),
        };

        return Finish(p, vertices, [], [0, 1], closed: false, thickness: MathF.Max(size.X, .012f));
    }

    private static PrimitiveMesh Finish(
        RenderPrimitive p,
        Vector3[] vertices,
        MeshFace[] faces,
        int[] stroke,
        bool closed,
        float thickness)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var sum = Vector3.Zero;

        foreach (var v in vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
            sum += v;
        }

        var pad = new Vector3(thickness * .5f);

        return new PrimitiveMesh
        {
            Source = p,
            Vertices = vertices,
            Faces = faces,
            Stroke = stroke,
            StrokeClosed = closed,
            StrokeThickness = thickness,
            Centre = sum / vertices.Length,
            Min = min - pad,
            Max = max + pad,
        };
    }

    private static Vector3 World(Vector3 local, in Matrix4x4 rotation, Vector3 origin) =>
        Vector3.Transform(local, rotation) + origin;

    private static Vector3 Direction(Vector3 local, in Matrix4x4 rotation) =>
        Vector3.Normalize(Vector3.TransformNormal(local, rotation));

    private static MeshFace Face(Vector3[] vertices, int[] indices, Vector3 normal)
    {
        var centre = Vector3.Zero;
        foreach (var i in indices) centre += vertices[i];

        return new MeshFace
        {
            Indices = indices,
            Normal = normal,
            Centre = centre / indices.Length,
        };
    }
}
