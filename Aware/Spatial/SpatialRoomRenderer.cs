using System.Numerics;
using Aware.Application;
using Aware.Domain;
using SkiaSharp;

namespace Aware.Spatial;

/// <summary>
/// Per-frame camera and animation state handed to the renderer. The renderer
/// holds no ownership of it, which keeps drawing a pure function of the snapshot
/// plus this struct.
/// </summary>
internal readonly record struct RenderFrame(
    float Yaw,
    float Pitch,
    float Distance,
    float AssemblyMs,
    float SelectionFactor,
    SpatialObjectId? FadingObjectId,
    float FadingFactor,
    float AnnotationAlpha,
    float VerticalAnchor,
    bool ReducedMotion);

/// <summary>
/// The ambient-occlusion clay renderer described in 04-RENDERING: semantic
/// primitives, deterministic projection, depth sorting, soft contact shadows,
/// mild depth fog, no textures and no decorative outlines.
/// </summary>
internal sealed class SpatialRoomRenderer : IDisposable
{
    // --- motion constants (06-MOTION-BRIEF) -------------------------------
    private static readonly (float Start, float End)[] StageWindows =
    [
        (0f, 800f), (700f, 1900f), (1500f, 3000f), (2500f, 4200f),
    ];

    private const float TransformDurationMs = 520f;
    private const float OpacityDurationMs = 380f;
    private const float ReducedFadeMs = 150f;
    private const float EntryLiftPx = 8f;
    private const float EntryScale = .92f;

    public const float AssemblyDurationMs = 4200f;

    /// <summary>
    /// Strongly asymmetric in X and Z. From the default orbit the viewer sees
    /// mostly +X and +Z faces; a light with similar X and Z components shades
    /// them to within a few levels of each other and the clay model reads flat.
    /// Driving Z slightly negative drops +Z faces into the side/rear band, which
    /// is what gives the model its edges.
    /// </summary>
    private static readonly Vector3 LightDirection =
        Vector3.Normalize(new Vector3(.70f, .68f, -.05f));

    private static readonly Vector3 OrbitTarget = new(0f, 1f, 0f);

    // --- reusable Skia state ----------------------------------------------
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _shadow = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _background = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _text = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPath _path = new();
    private readonly SKMaskFilter _contactBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f);
    private readonly SKPathEffect _dash = SKPathEffect.CreateDash([5f, 4f], 0f);
    private readonly SKFont _labelFont;
    private readonly SKFont _pinFont;

    private SKShader? _backgroundShader;
    private float _backgroundHeight = -1f;

    // --- snapshot-derived state (rebuilt only when the snapshot changes) ---
    private SpatialRenderSnapshot? _snapshot;
    private PrimitiveMesh[] _meshes = [];
    private float[] _stageStart = [];
    private int[] _vertexOffset = [];
    private ProjectedPoint[] _projected = [];
    private float[] _alpha = [];
    private float[] _settle = [];
    private float _focal = 400f;
    private float _fitX = 1f;
    private float _fitY = 1f;
    private Vector3 _roomCentre = Vector3.Zero;

    // --- per-frame scratch (allocated once, reused) ------------------------
    private DrawItem[] _items = new DrawItem[256];
    private float[] _keys = new float[256];
    private int[] _order = new int[256];
    private int _itemCount;

    private HitFace[] _hitFaces = new HitFace[128];
    private float[] _hitPoints = new float[1024];
    private int _hitFaceCount;
    private int _hitPointCount;

    public SpatialRoomRenderer()
    {
        var typeface = ResolveTypeface();
        _labelFont = new SKFont(typeface, 12.5f) { Subpixel = true };
        _pinFont = new SKFont(typeface, 12f) { Subpixel = true };
    }

    public SpatialRenderSnapshot? Snapshot => _snapshot;

    public void SetSnapshot(SpatialRenderSnapshot? snapshot)
    {
        var rebuildGeometry =
            _snapshot is null ||
            snapshot is null ||
            !ReferenceEquals(_snapshot.Primitives, snapshot.Primitives);

        _snapshot = snapshot;

        if (!rebuildGeometry || snapshot is null) return;

        _meshes = SpatialMesh.Build(snapshot.Primitives);
        _stageStart = BuildStageStarts(snapshot.Primitives);
        _alpha = new float[_meshes.Length];
        _settle = new float[_meshes.Length];
        _vertexOffset = new int[_meshes.Length];

        var total = 0;
        for (var i = 0; i < _meshes.Length; i++)
        {
            _vertexOffset[i] = total;
            total += _meshes[i].Vertices.Length;
        }

        _projected = new ProjectedPoint[total];
        ComputeFraming();
    }

    /// <summary>
    /// Measures the room's projected extent at the default camera once, so the
    /// focal length can frame the whole model on any viewport. Measuring at a
    /// fixed angle rather than per frame keeps orbiting from reading as zoom.
    /// </summary>
    private void ComputeFraming()
    {
        if (_meshes.Length == 0)
        {
            _fitX = _fitY = 1f;
            return;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in _meshes)
        {
            min = Vector3.Min(min, mesh.Min);
            max = Vector3.Max(max, mesh.Max);
        }

        var view = Math3D.CreateView(
            Math3D.DefaultYaw, Math3D.DefaultPitch, Math3D.DefaultDistance, OrbitTarget);

        float extentX = .001f, extentY = .001f;

        for (var corner = 0; corner < 8; corner++)
        {
            var world = new Vector3(
                (corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);

            var camera = Vector3.Transform(world, view);
            var z = MathF.Max(.05f, -camera.Z);

            extentX = MathF.Max(extentX, MathF.Abs(camera.X) / z);
            extentY = MathF.Max(extentY, MathF.Abs(camera.Y) / z);
        }

        _fitX = extentX;
        _fitY = extentY;
        _roomCentre = (min + max) * .5f;
    }

    private float FocalLength(float width, float height) =>
        MathF.Min(width * .44f / _fitX, height * .40f / _fitY);

    public bool IsAssemblyComplete(float assemblyMs, bool reducedMotion) =>
        assemblyMs >= (reducedMotion ? ReducedFadeMs : AssemblyDurationMs);

    // ------------------------------------------------------------------
    // Draw
    // ------------------------------------------------------------------

    public void Render(SKCanvas canvas, float width, float height, in RenderFrame frame)
    {
        DrawBackground(canvas, width, height);

        if (_snapshot is not { } snapshot || _meshes.Length == 0) return;

        var view = Math3D.CreateView(frame.Yaw, frame.Pitch, frame.Distance, OrbitTarget);
        var camera = Math3D.CameraPosition(frame.Yaw, frame.Pitch, frame.Distance, OrbitTarget);
        var focal = FocalLength(width, height);
        _focal = focal;

        ComputeAppearance(frame);
        FadeWallsInFront(camera);
        ProjectVertices(view, focal, width, height, frame.VerticalAnchor);

        BuildDrawList(camera);
        SortDrawList();
        ResetHitBuffer();

        var near = frame.Distance - 5.5f;
        var far = frame.Distance + 6.5f;
        var shadowsDrawn = false;

        for (var i = 0; i < _itemCount; i++)
        {
            ref var item = ref _items[_order[i]];

            // Contact shadows land on the floor, so they follow it and precede
            // everything standing on it. The sort key groups the floor first,
            // which makes that boundary the right seam.
            if (!shadowsDrawn && !IsFloor(_meshes[item.MeshIndex]))
            {
                DrawContactShadows(canvas, view, focal, width, height, frame.VerticalAnchor);
                shadowsDrawn = true;
            }

            if (item.IsStroke)
                DrawStroke(canvas, item, near, far, frame);
            else
                DrawFace(canvas, item, near, far, frame);
        }

        if (!shadowsDrawn)
            DrawContactShadows(canvas, view, focal, width, height, frame.VerticalAnchor);

        if (frame.AnnotationAlpha > .01f)
        {
            if (snapshot.ActiveLens == SpatialLens.Measure)
                DrawGuides(canvas, snapshot, view, focal, width, height, frame);
            else if (snapshot.ActiveLens == SpatialLens.Memories)
                DrawPins(canvas, snapshot, view, focal, width, height, frame);
        }
    }

    private void DrawBackground(SKCanvas canvas, float width, float height)
    {
        if (_backgroundShader is null || MathF.Abs(_backgroundHeight - height) > .5f)
        {
            _backgroundShader?.Dispose();
            _backgroundShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                [SpatialPalette.CanvasNear, SpatialPalette.CanvasFar],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _backgroundHeight = height;
            _background.Shader = _backgroundShader;
        }

        canvas.DrawRect(0, 0, width, height, _background);
    }

    /// <summary>
    /// Assembly stagger. Opacity and the transform run on different durations
    /// (300–450 ms and 420–620 ms in 06-MOTION-BRIEF), so they are tracked apart.
    /// Reduced motion collapses both to a single 150 ms crossfade with no stagger.
    /// </summary>
    private void ComputeAppearance(in RenderFrame frame)
    {
        for (var i = 0; i < _meshes.Length; i++)
        {
            if (frame.ReducedMotion)
            {
                _alpha[i] = Math.Clamp(frame.AssemblyMs / ReducedFadeMs, 0f, 1f);
                _settle[i] = 1f;
                continue;
            }

            var elapsed = frame.AssemblyMs - _stageStart[i];
            _alpha[i] = Math.Clamp(elapsed / OpacityDurationMs, 0f, 1f);
            _settle[i] = CubicBezierEase.Settle.Ease(Math.Clamp(elapsed / TransformDurationMs, 0f, 1f));
        }
    }

    /// <summary>
    /// Yaw is continuous, so the orbit passes behind the shell and a wall ends
    /// up between the camera and the room. Rather than clamp the orbit or let
    /// the room disappear, a wall the camera is standing outside of drops to a
    /// ghost. It still reads as an enclosure; it stops being an obstruction.
    /// </summary>
    private void FadeWallsInFront(Vector3 camera)
    {
        for (var m = 0; m < _meshes.Length; m++)
        {
            var mesh = _meshes[m];
            if (!mesh.IsShell || mesh.Source.MaterialKey != "wall") continue;

            // A wall is a thin slab, so its own thinnest horizontal axis is its
            // plane normal. Using the offset from the room centre instead skews
            // that normal for any segment that is not centred, which is every
            // segment either side of a door opening.
            var size = mesh.Max - mesh.Min;
            var offset = mesh.Centre - _roomCentre;

            var outward = size.X <= size.Z
                ? new Vector3(MathF.Sign(offset.X), 0f, 0f)
                : new Vector3(0f, 0f, MathF.Sign(offset.Z));

            if (outward.LengthSquared() < .0001f) continue;

            var toCamera = camera - mesh.Centre;
            toCamera.Y = 0f;
            if (toCamera.LengthSquared() < .0001f) continue;

            var facing = Vector3.Dot(outward, Vector3.Normalize(toCamera));
            _alpha[m] *= 1f - .84f * Math.Clamp(facing * 2.6f, 0f, 1f);
        }
    }

    private void ProjectVertices(in Matrix4x4 view, float focal, float width, float height, float anchor)
    {
        for (var m = 0; m < _meshes.Length; m++)
        {
            var mesh = _meshes[m];
            var offset = _vertexOffset[m];

            if (_alpha[m] <= 0f)
            {
                // Still project so hidden geometry keeps a stable buffer slot;
                // it is skipped in the draw list.
                for (var v = 0; v < mesh.Vertices.Length; v++)
                    _projected[offset + v] = default;
                continue;
            }

            var eased = _settle[m];
            var scale = EntryScale + (1f - EntryScale) * eased;
            var lift = EntryLiftPx * (1f - eased);
            var centre = mesh.Centre;

            for (var v = 0; v < mesh.Vertices.Length; v++)
            {
                var world = scale >= .999f
                    ? mesh.Vertices[v]
                    : centre + (mesh.Vertices[v] - centre) * scale;

                var p = Math3D.Project(world, view, focal, width, height, anchor);
                _projected[offset + v] = lift > .01f ? p with { Y = p.Y + lift } : p;
            }
        }
    }

    private void DrawContactShadows(
        SKCanvas canvas, in Matrix4x4 view, float focal, float width, float height, float anchor)
    {
        _shadow.MaskFilter = _contactBlur;

        for (var m = 0; m < _meshes.Length; m++)
        {
            var mesh = _meshes[m];
            if (mesh.IsShell || _alpha[m] <= .05f) continue;

            var lift = MathF.Max(mesh.Min.Y, 0f);
            if (lift > .45f) continue;

            var strength = (1f - lift / .45f) * .26f * _alpha[m];
            if (strength <= .004f) continue;

            var cx = (mesh.Min.X + mesh.Max.X) * .5f;
            var cz = (mesh.Min.Z + mesh.Max.Z) * .5f;
            var rx = (mesh.Max.X - mesh.Min.X) * .5f + .06f;
            var rz = (mesh.Max.Z - mesh.Min.Z) * .5f + .06f;

            _path.Rewind();
            for (var i = 0; i < 14; i++)
            {
                var angle = MathF.Tau * i / 14f;
                var p = Math3D.Project(
                    new Vector3(cx + MathF.Cos(angle) * rx, .012f, cz + MathF.Sin(angle) * rz),
                    view, focal, width, height, anchor);

                if (i == 0) _path.MoveTo(p.X, p.Y); else _path.LineTo(p.X, p.Y);
            }
            _path.Close();

            _shadow.Color = SpatialPalette.WithAlpha(SpatialPalette.Ink, strength);
            canvas.DrawPath(_path, _shadow);
        }

        _shadow.MaskFilter = null;
    }

    private void BuildDrawList(Vector3 camera)
    {
        _itemCount = 0;

        for (var m = 0; m < _meshes.Length; m++)
        {
            if (_alpha[m] <= .01f) continue;

            var mesh = _meshes[m];
            var offset = _vertexOffset[m];

            if (mesh.Stroke.Length > 0)
            {
                var depth = 0f;
                foreach (var index in mesh.Stroke) depth += _projected[offset + index].Depth;
                Add(new DrawItem(m, -1, depth / mesh.Stroke.Length, true));
                continue;
            }

            for (var f = 0; f < mesh.Faces.Length; f++)
            {
                var face = mesh.Faces[f];

                // Back-face cull against the camera position.
                if (Vector3.Dot(face.Normal, camera - face.Centre) <= 0f) continue;

                var depth = 0f;
                foreach (var index in face.Indices) depth += _projected[offset + index].Depth;
                Add(new DrawItem(m, f, depth / face.Indices.Length, false));
            }
        }
    }

    private void Add(in DrawItem item)
    {
        if (_itemCount == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
            Array.Resize(ref _keys, _items.Length);
            Array.Resize(ref _order, _items.Length);
        }

        // The floor sorts ahead of everything: it lies under the whole room, so
        // it is unconditionally behind. Walls are NOT — yaw is continuous, and
        // past a quarter turn a wall stands between the camera and the contents,
        // so walls take their chances in the ordinary depth sort.
        _items[_itemCount] = item;
        _keys[_itemCount] = (IsFloor(_meshes[item.MeshIndex]) ? 0f : 1000f) - item.Depth;
        _order[_itemCount] = _itemCount;
        _itemCount++;
    }

    private void SortDrawList()
    {
        if (_itemCount > 1)
            Array.Sort(_keys, _order, 0, _itemCount);
    }

    private void DrawFace(SKCanvas canvas, in DrawItem item, float near, float far, in RenderFrame frame)
    {
        var mesh = _meshes[item.MeshIndex];
        var face = mesh.Faces[item.FaceIndex];
        var offset = _vertexOffset[item.MeshIndex];
        var alpha = _alpha[item.MeshIndex];

        _path.Rewind();
        for (var i = 0; i < face.Indices.Length; i++)
        {
            var p = _projected[offset + face.Indices[i]];
            if (i == 0) _path.MoveTo(p.X, p.Y); else _path.LineTo(p.X, p.Y);
        }
        _path.Close();

        var colour = Shade(mesh.Source.MaterialKey, face.Normal, SelectionAmount(mesh, frame));
        colour = Fog(colour, item.Depth, near, far);

        _fill.Color = SpatialPalette.WithAlpha(colour, alpha);
        canvas.DrawPath(_path, _fill);

        if (mesh.Source.IsSelectable && mesh.Source.ObjectId is { } id && alpha > .55f)
            RecordHitFace(id, offset, face.Indices, item.Depth);
    }

    private void DrawStroke(SKCanvas canvas, in DrawItem item, float near, float far, in RenderFrame frame)
    {
        var mesh = _meshes[item.MeshIndex];
        var offset = _vertexOffset[item.MeshIndex];
        var alpha = _alpha[item.MeshIndex];

        _path.Rewind();
        for (var i = 0; i < mesh.Stroke.Length; i++)
        {
            var p = _projected[offset + mesh.Stroke[i]];
            if (i == 0) _path.MoveTo(p.X, p.Y); else _path.LineTo(p.X, p.Y);
        }
        if (mesh.StrokeClosed) _path.Close();

        // Stroked primitives have no single normal; shade them as a side face so
        // they sit in the same material family as the solids around them.
        var colour = Shade(mesh.Source.MaterialKey, new Vector3(0f, .25f, .96f), SelectionAmount(mesh, frame));
        colour = Fog(colour, item.Depth, near, far);

        _stroke.Color = SpatialPalette.WithAlpha(colour, alpha);
        _stroke.StrokeWidth = MathF.Max(1.4f, mesh.StrokeThickness * ScreenScale(item.Depth));
        canvas.DrawPath(_path, _stroke);

        if (mesh.Source.IsSelectable && mesh.Source.ObjectId is { } id && alpha > .55f)
            RecordHitStroke(id, offset, mesh, item.Depth);
    }

    /// <summary>
    /// How selected this primitive currently reads. The object losing selection
    /// keeps fading for its own 180 ms rather than snapping back, so swapping
    /// between two objects stays a transition instead of a cut.
    /// </summary>
    private float SelectionAmount(PrimitiveMesh mesh, in RenderFrame frame)
    {
        if (mesh.Source.ObjectId is not { } id) return 0f;
        if (_snapshot?.SelectedObjectId is { } selected && id == selected) return frame.SelectionFactor;
        if (frame.FadingObjectId is { } fading && id == fading) return frame.FadingFactor;
        return 0f;
    }

    private float ScreenScale(float depth) => _focal / MathF.Max(depth, .05f);

    private static bool IsFloor(PrimitiveMesh mesh) =>
        mesh.IsShell && mesh.Source.MaterialKey == "floor";

    // ------------------------------------------------------------------
    // Materials
    // ------------------------------------------------------------------

    private static SKColor Shade(string material, Vector3 normal, float selection)
    {
        var lambert = Vector3.Dot(normal, LightDirection);
        var t = Math.Clamp(.5f + .5f * lambert, 0f, 1f);

        // Push the midtones apart. Without it most normals land near 0.5 and the
        // whole model sits in one value.
        t = t < .5f
            ? .5f * MathF.Pow(t * 2f, 1.45f)
            : 1f - .5f * MathF.Pow((1f - t) * 2f, 1.45f);

        var (rear, side, front) = Ramp(material);

        var colour = t < .5f
            ? SpatialPalette.Lerp(rear, side, t * 2f)
            : SpatialPalette.Lerp(side, front, (t - .5f) * 2f);

        // Up-facing surfaces catch a little more of the ambient dome.
        if (normal.Y > .8f)
            colour = SpatialPalette.Lerp(colour, SpatialPalette.CanvasNear, (normal.Y - .8f) * 1.1f);

        if (selection <= .001f) return colour;

        var selected = t < .5f
            ? SpatialPalette.Lerp(SpatialPalette.SelectionDark, SpatialPalette.Selection, t * 2f)
            : SpatialPalette.Lerp(
                SpatialPalette.Selection,
                SpatialPalette.Lerp(SpatialPalette.Selection, SpatialPalette.CanvasNear, .38f),
                (t - .5f) * 2f);

        return SpatialPalette.Lerp(colour, selected, selection);
    }

    /// <summary>
    /// The dark end runs past ModelRear toward Muted. 05-DESIGN-SYSTEM names the
    /// three model tones as the mid-range; a rear face that stops at ModelRear
    /// leaves the model without a floor to its value range.
    /// </summary>
    private static readonly SKColor DeepRear =
        SpatialPalette.Lerp(SpatialPalette.ModelRear, SpatialPalette.Muted, .38f);

    private static (SKColor Rear, SKColor Side, SKColor Front) Ramp(string material) => material switch
    {
        "floor" => (
            SpatialPalette.Lerp(SpatialPalette.Floor, DeepRear, .5f),
            SpatialPalette.Floor,
            SpatialPalette.Lerp(SpatialPalette.Floor, SpatialPalette.ModelFront, .35f)),

        "wall" => (
            SpatialPalette.Lerp(DeepRear, SpatialPalette.ModelRear, .35f),
            SpatialPalette.ModelRear,
            SpatialPalette.ModelSide),

        "accent" => (
            SpatialPalette.Lerp(DeepRear, SpatialPalette.Blueprint, .2f),
            SpatialPalette.Lerp(SpatialPalette.ModelSide, SpatialPalette.Blueprint, .16f),
            SpatialPalette.Lerp(SpatialPalette.ModelFront, SpatialPalette.Blueprint, .10f)),

        "metal" => (
            SpatialPalette.Lerp(DeepRear, SpatialPalette.Muted, .3f),
            SpatialPalette.Lerp(SpatialPalette.ModelSide, SpatialPalette.Muted, .3f),
            SpatialPalette.Lerp(SpatialPalette.ModelFront, SpatialPalette.Muted, .2f)),

        _ => (DeepRear, SpatialPalette.ModelSide, SpatialPalette.ModelFront),
    };

    private static SKColor Fog(SKColor colour, float depth, float near, float far)
    {
        var t = Math.Clamp((depth - near) / MathF.Max(far - near, .001f), 0f, 1f);
        return SpatialPalette.Lerp(colour, SpatialPalette.CanvasFar, t * .22f);
    }

    // ------------------------------------------------------------------
    // Annotations
    // ------------------------------------------------------------------

    private void DrawGuides(
        SKCanvas canvas, SpatialRenderSnapshot snapshot,
        in Matrix4x4 view, float focal, float width, float height, in RenderFrame frame)
    {
        foreach (var guide in snapshot.Guides)
        {
            var a = Math3D.Project(guide.Start, view, focal, width, height, frame.VerticalAnchor);
            var b = Math3D.Project(guide.End, view, focal, width, height, frame.VerticalAnchor);

            var colour = guide.IsKnown ? SpatialPalette.Blueprint : SpatialPalette.Muted;
            _stroke.Color = SpatialPalette.WithAlpha(colour, frame.AnnotationAlpha);
            _stroke.StrokeWidth = 1.35f;
            _stroke.PathEffect = guide.IsKnown ? null : _dash;

            canvas.DrawLine(a.X, a.Y, b.X, b.Y, _stroke);

            // End ticks, perpendicular to the guide in screen space.
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = MathF.Max(MathF.Sqrt(dx * dx + dy * dy), .001f);
            var nx = -dy / length * 6.5f;
            var ny = dx / length * 6.5f;

            canvas.DrawLine(a.X - nx, a.Y - ny, a.X + nx, a.Y + ny, _stroke);
            canvas.DrawLine(b.X - nx, b.Y - ny, b.X + nx, b.Y + ny, _stroke);
            _stroke.PathEffect = null;

            // Three guides converge near a small object and their labels collide.
            // Pushing each label off its own line, on the side facing away from
            // the viewport centre, keeps them legible at any orbit angle.
            var away = (a.X + b.X) * .5f < width * .5f ? -1f : 1f;

            DrawLabel(canvas, guide.Label,
                (a.X + b.X) * .5f + nx / 6.5f * 17f * away,
                (a.Y + b.Y) * .5f + ny / 6.5f * 17f * away,
                colour, _labelFont, frame.AnnotationAlpha);
        }
    }

    private void DrawPins(
        SKCanvas canvas, SpatialRenderSnapshot snapshot,
        in Matrix4x4 view, float focal, float width, float height, in RenderFrame frame)
    {
        foreach (var pin in snapshot.Pins)
        {
            var p = Math3D.Project(pin.Position, view, focal, width, height, frame.VerticalAnchor);
            var observed = pin.Evidence == EvidenceKind.Observed;
            var colour = observed ? SpatialPalette.SelectionDark : SpatialPalette.Muted;

            _stroke.Color = SpatialPalette.WithAlpha(colour, frame.AnnotationAlpha * .85f);
            _stroke.StrokeWidth = 1.2f;
            // Inference is marked by shape and stroke, never by colour alone
            // (08-ACCESSIBILITY-TESTS).
            _stroke.PathEffect = observed ? null : _dash;
            canvas.DrawLine(p.X, p.Y, p.X, p.Y - 15f, _stroke);
            _stroke.PathEffect = null;

            if (observed)
            {
                _fill.Color = SpatialPalette.WithAlpha(colour, frame.AnnotationAlpha);
                canvas.DrawCircle(p.X, p.Y - 15f, 3.6f, _fill);
            }
            else
            {
                _stroke.StrokeWidth = 1.4f;
                canvas.DrawCircle(p.X, p.Y - 15f, 3.4f, _stroke);
            }

            DrawLabel(canvas, pin.Label, p.X, p.Y - 24f, colour, _pinFont, frame.AnnotationAlpha);
        }
    }

    private void DrawLabel(
        SKCanvas canvas, string text, float x, float y, SKColor colour, SKFont font, float alpha)
    {
        var textWidth = font.MeasureText(text);
        var padX = 7f;
        var padY = 4f;
        var boxHeight = font.Size + padY * 2f;

        _fill.Color = SpatialPalette.WithAlpha(SpatialPalette.CanvasNear, alpha * .94f);
        canvas.DrawRoundRect(
            x - textWidth / 2f - padX,
            y - boxHeight / 2f,
            textWidth + padX * 2f,
            boxHeight,
            boxHeight / 2f, boxHeight / 2f,
            _fill);

        _text.Color = SpatialPalette.WithAlpha(colour, alpha);
        canvas.DrawText(text, x, y + font.Size * .35f, SKTextAlign.Center, font, _text);
    }

    // ------------------------------------------------------------------
    // Hit testing
    // ------------------------------------------------------------------

    private void ResetHitBuffer()
    {
        _hitFaceCount = 0;
        _hitPointCount = 0;
    }

    private void RecordHitFace(SpatialObjectId id, int offset, int[] indices, float depth)
    {
        EnsureHitCapacity(indices.Length);

        var start = _hitPointCount;
        foreach (var index in indices)
        {
            var p = _projected[offset + index];
            _hitPoints[_hitPointCount++] = p.X;
            _hitPoints[_hitPointCount++] = p.Y;
        }

        _hitFaces[_hitFaceCount++] = new HitFace(id, start, indices.Length, depth);
    }

    /// <summary>
    /// Stroked primitives are hit-tested against their screen bounding box
    /// inflated by the stroke width; a rim is too thin for polygon containment.
    /// </summary>
    private void RecordHitStroke(SpatialObjectId id, int offset, PrimitiveMesh mesh, float depth)
    {
        EnsureHitCapacity(4);

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var index in mesh.Stroke)
        {
            var p = _projected[offset + index];
            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
        }

        var pad = MathF.Max(_stroke.StrokeWidth, 4f);
        minX -= pad; minY -= pad; maxX += pad; maxY += pad;

        var start = _hitPointCount;
        _hitPoints[_hitPointCount++] = minX; _hitPoints[_hitPointCount++] = minY;
        _hitPoints[_hitPointCount++] = maxX; _hitPoints[_hitPointCount++] = minY;
        _hitPoints[_hitPointCount++] = maxX; _hitPoints[_hitPointCount++] = maxY;
        _hitPoints[_hitPointCount++] = minX; _hitPoints[_hitPointCount++] = maxY;

        _hitFaces[_hitFaceCount++] = new HitFace(id, start, 4, depth);
    }

    private void EnsureHitCapacity(int points)
    {
        if (_hitFaceCount == _hitFaces.Length)
            Array.Resize(ref _hitFaces, _hitFaces.Length * 2);

        while (_hitPointCount + points * 2 > _hitPoints.Length)
            Array.Resize(ref _hitPoints, _hitPoints.Length * 2);
    }

    /// <summary>
    /// The front-most selectable object under the point. Faces were recorded in
    /// painter order (far to near), so walking backwards finds the nearest first.
    /// </summary>
    public SpatialObjectId? HitTest(float x, float y)
    {
        for (var i = _hitFaceCount - 1; i >= 0; i--)
        {
            ref var face = ref _hitFaces[i];
            if (Contains(face.Offset, face.Count, x, y))
                return face.ObjectId;
        }

        return null;
    }

    private bool Contains(int offset, int count, float x, float y)
    {
        var inside = false;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            var xi = _hitPoints[offset + i * 2];
            var yi = _hitPoints[offset + i * 2 + 1];
            var xj = _hitPoints[offset + j * 2];
            var yj = _hitPoints[offset + j * 2 + 1];

            if (yi > y != yj > y &&
                x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Per-primitive start time inside its stage window, spread so the last
    /// primitive of a stage still finishes before the window closes.
    /// Computed once per snapshot; the timeline never changes per frame.
    /// </summary>
    private static float[] BuildStageStarts(IReadOnlyList<RenderPrimitive> primitives)
    {
        var starts = new float[primitives.Count];
        var totals = new Dictionary<int, int>();
        var seen = new Dictionary<int, int>();

        foreach (var p in primitives)
        {
            totals.TryGetValue(p.ReconstructionStage, out var count);
            totals[p.ReconstructionStage] = count + 1;
        }

        for (var i = 0; i < primitives.Count; i++)
        {
            var stage = Math.Clamp(primitives[i].ReconstructionStage, 0, StageWindows.Length - 1);
            var window = StageWindows[stage];

            seen.TryGetValue(stage, out var index);
            seen[stage] = index + 1;

            var steps = MathF.Max(totals[primitives[i].ReconstructionStage] - 1, 1);
            var span = MathF.Max(window.End - window.Start - TransformDurationMs, 0f);
            starts[i] = window.Start + index * (span / steps);
        }

        return starts;
    }

    private static SKTypeface ResolveTypeface()
    {
        foreach (var family in new[] { "Segoe UI", "Inter", "Helvetica Neue", "Roboto", "Arial" })
        {
            var typeface = SKTypeface.FromFamilyName(family);
            if (typeface is not null && !string.Equals(typeface.FamilyName, "System-ui", StringComparison.OrdinalIgnoreCase))
                return typeface;
        }

        return SKTypeface.Default;
    }

    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _shadow.Dispose();
        _background.Dispose();
        _text.Dispose();
        _path.Dispose();
        _contactBlur.Dispose();
        _dash.Dispose();
        _labelFont.Dispose();
        _pinFont.Dispose();
        _backgroundShader?.Dispose();
    }

    private readonly record struct DrawItem(int MeshIndex, int FaceIndex, float Depth, bool IsStroke);

    private readonly record struct HitFace(SpatialObjectId ObjectId, int Offset, int Count, float Depth);
}
