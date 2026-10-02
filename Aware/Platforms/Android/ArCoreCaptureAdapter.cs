using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Android.Content;
using Android.Content.PM;
using Android.Opengl;
using Aware.Domain;
using Google.AR.Core;
using Google.AR.Core.Exceptions;
using Java.Lang;
using Exception = System.Exception;
// Frame collides with the XAML navigation control, Plane with System.Numerics.
using ArFrame = Google.AR.Core.Frame;
using ArPlane = Google.AR.Core.Plane;

namespace Aware.Platform;

/// <summary>
/// Real room geometry, from ARCore's plane detection.
///
/// <para>ARCore types stop here. Everything crossing
/// <see cref="ISpatialCaptureAdapter"/> is a plain record in Aware's own
/// coordinates (09-UNO-NOTES), which is what lets the simulation adapter remain a
/// drop-in on every other platform.</para>
///
/// <para><strong>Planes, not objects.</strong> ARCore reports horizontal and
/// vertical surfaces with extents and a pose; it does not say "workbench". So this
/// yields shell geometry and no object candidates, and the room's contents stay
/// whatever the user names through the correction flow. Claiming semantic
/// classification here would be inventing evidence.</para>
///
/// <para><strong>Every scan runs on its own thread with its own GL context.</strong>
/// ARCore needs a camera texture to write each frame into before
/// <c>Session.Update()</c> will return anything, even when nothing is drawn, and
/// GL textures only exist inside a context that is current on the calling thread.
/// An <c>await</c> resumes wherever the scheduler puts it — here, the UI thread,
/// which has no GL context of its own under Uno's Skia renderer — so creating the
/// texture inline would return texture 0 and every frame would be silently dropped:
/// a 45 s scan that observes nothing. The worker below makes a 1×1 pbuffer context
/// current, creates the texture in it, and keeps resume, update and pause on that
/// same thread, handing observations back over a channel.</para>
/// </summary>
public sealed class ArCoreCaptureAdapter : ISpatialCaptureAdapter, IDisposable
{
    private const int NoTexture = 0;

    /// <summary>
    /// ~10 Hz. Faster gains nothing: planes are extended over seconds, and every
    /// frame allocates a managed observation.
    /// </summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);

    private readonly ILogger<ArCoreCaptureAdapter> _log;
    private readonly Context? _context;

    private Session? _session;

    public ArCoreCaptureAdapter(ILogger<ArCoreCaptureAdapter> log)
    {
        _log = log;
        _context = Uno.UI.ContextHelper.Current;
    }

    public Task<SpatialCaptureCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        var available = false;
        var depth = false;

        try
        {
            if (_context is not null)
            {
                // Reports whether ARCore is installed and this hardware is
                // supported, without prompting or installing anything.
                var availability = ArCoreApk.Instance!.CheckAvailability(_context);
                available = availability?.IsSupported == true;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not check ARCore availability.");
        }

        if (!available)
            return Task.FromResult(new SpatialCaptureCapabilities(
                SpatialCaptureCapability.Simulation, false, false, false, false));

        try
        {
            depth = EnsureSession()?.IsDepthModeSupported(Config.DepthMode.Automatic) == true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not query depth support.");
        }

        return Task.FromResult(new SpatialCaptureCapabilities(
            HighestCapability: depth
                ? SpatialCaptureCapability.DepthAssisted
                : SpatialCaptureCapability.CameraAndMotion,
            HasCamera: true,
            HasMotion: true,
            HasDepth: depth,
            HasUwb: false));
    }

    public async IAsyncEnumerable<SpatialObservation> CaptureAsync(
        SpatialCaptureRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Asked at the moment of scanning rather than at launch: the camera is
        // only ever used while a scan is running, and a permission prompt on first
        // open would ask for something the app has not yet offered to do.
        await EnsureCameraPermissionAsync(ct);

        if (!HasCameraPermission)
        {
            _log.LogWarning("Capture requested without the camera permission.");
            yield break;
        }

        var session = EnsureSession();
        if (session is null) yield break;

        // Unbounded on purpose: the consumer applies each observation to the
        // model on the UI thread, and at 10 Hz a backlog of a few frames is
        // cheaper than deciding which plane snapshot to drop.
        var observations = Channel.CreateUnbounded<SpatialObservation>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stopToken = stop.Token;

        // LongRunning gives the scan a thread of its own rather than a pool
        // thread. The EGL context created inside is bound to that thread, so the
        // whole scan has to stay on it from first call to last.
        var worker = Task.Factory.StartNew(
            () => RunScan(session, request.MaximumDuration, observations.Writer, stopToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            await foreach (var observation in observations.Reader.ReadAllAsync(ct))
                yield return observation;
        }
        finally
        {
            // Reached on completion, on cancellation and when the consumer stops
            // enumerating early. The worker pauses the session and tears down its
            // GL context on its way out; waiting for it keeps that teardown from
            // overlapping the next scan's setup.
            stop.Cancel();
            try { await worker; }
            catch (Exception ex) { _log.LogWarning(ex, "The scan worker did not stop cleanly."); }
        }
    }

    /// <summary>
    /// The whole of one scan, on one thread: GL context, camera texture, resume,
    /// the update loop, pause, teardown. Runs synchronously so nothing can hop
    /// threads between the context being made current and the last GL call.
    /// </summary>
    private void RunScan(
        Session session,
        TimeSpan maximumDuration,
        ChannelWriter<SpatialObservation> writer,
        CancellationToken ct)
    {
        OffscreenGlContext? gl = null;
        var textureId = NoTexture;
        var resumed = false;

        try
        {
            gl = OffscreenGlContext.Create();
            if (gl is null)
            {
                writer.TryComplete(new InvalidOperationException(
                    $"No offscreen GL context could be created for the camera texture (EGL error 0x{EGL14.EglGetError():X})."));
                return;
            }

            textureId = CreateCameraTexture();
            if (textureId == NoTexture)
            {
                writer.TryComplete(new InvalidOperationException(
                    $"The camera texture could not be created (GL error 0x{GLES20.GlGetError():X})."));
                return;
            }

            session.SetCameraTextureName(textureId);
            session.Resume();
            resumed = true;

            var deadline = DateTimeOffset.Now + maximumDuration;

            while (DateTimeOffset.Now < deadline && !ct.IsCancellationRequested)
            {
                // Wakes early on cancellation instead of sleeping through it.
                if (ct.WaitHandle.WaitOne(FrameInterval)) break;

                var observation = ReadPlanes(session);
                if (observation is not null) writer.TryWrite(observation);
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The ARCore scan failed.");
            writer.TryComplete(ex);
        }
        finally
        {
            if (resumed)
            {
                try { session.Pause(); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not pause the ARCore session."); }
            }

            // The texture dies with its context; deleting it first just keeps
            // the GL error state clean for the next scan on a fresh context.
            if (textureId != NoTexture)
                GLES20.GlDeleteTextures(1, [textureId], 0);

            gl?.Dispose();
        }
    }

    /// <summary>
    /// Every tracked plane as a thin box in Aware's coordinates.
    ///
    /// <para>ARCore and Aware share a convention — right-handed, Y up, metres — so
    /// positions carry across directly. A plane's extent is reported along its own
    /// X and Z, which for a horizontal plane is its footprint and for a vertical
    /// one is its width and height.</para>
    /// </summary>
    private SpatialObservation? ReadPlanes(Session session)
    {
        ArFrame frame;

        try
        {
            frame = session.Update()!;
        }
        catch (CameraNotAvailableException ex)
        {
            _log.LogWarning(ex, "The camera became unavailable mid-scan.");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "An ARCore frame could not be read.");
            return null;
        }

        // Nothing is worth reporting until ARCore knows where the phone is; poses
        // read while tracking is paused are stale and would smear the model.
        if (frame.Camera?.TrackingState != TrackingState.Tracking) return null;

        var geometry = new List<ObservedPrimitive>();

        foreach (var trackable in session.GetAllTrackables(Class.FromType(typeof(ArPlane)))!)
        {
            if (trackable is not ArPlane plane) continue;
            if (plane.TrackingState != TrackingState.Tracking) continue;

            // A plane that has been merged into a larger one keeps reporting; its
            // geometry now belongs to the other and would double-count.
            if (plane.SubsumedBy is not null) continue;

            var pose = plane.CenterPose;
            if (pose is null) continue;

            // Orientation from the pose's own normal rather than the plane's type
            // enum: Java's getType() collides with the nested Plane.Type class and
            // is not cleanly bound. An ARCore plane's normal is its pose's +Y
            // axis, so a normal pointing mostly up or down is a floor or ceiling
            // and anything else is a wall. This is also less brittle — it depends
            // on geometry rather than on how the binding named a member.
            var isHorizontal = MathF.Abs(UpAxisOf(pose).Y) > .7f;

            var extentX = plane.ExtentX;
            var extentZ = plane.ExtentZ;

            // Surfaces are drawn as thin slabs rather than zero-height quads, so
            // the renderer's existing box path shades them like the seeded shell.
            var size = isHorizontal
                ? new Vector3(extentX, .02f, extentZ)
                : new Vector3(extentX, extentZ, .02f);

            geometry.Add(new ObservedPrimitive(
                Kind: SpatialPrimitiveKind.Box,
                Size: size,
                Transform: new SpatialTransform(
                    new Vector3(pose.Tx(), pose.Ty(), pose.Tz()),
                    new Vector3(0f, YawOf(pose), 0f),
                    Vector3.One),
                // Area stands in for how sure the plane is: ARCore extends a
                // surface as it becomes more certain of it.
                Confidence: System.Math.Clamp(extentX * extentZ / 4f, .3f, .99f)));
        }

        if (geometry.Count == 0) return null;

        return new SpatialObservation(
            Timestamp: DateTimeOffset.Now,
            Source: "ARCore",
            Quality: System.Math.Clamp(geometry.Count / 8f, .3f, .98f),
            Geometry: geometry,
            // ARCore detects surfaces, not furniture. Naming what a surface is
            // belongs to the user's correction flow, not to a guess made here.
            ObjectCandidates: []);
    }

    /// <summary>
    /// The pose's local +Y in world space, which for an ARCore plane is its
    /// surface normal.
    /// </summary>
    private static Vector3 UpAxisOf(Pose pose)
    {
        var axis = new float[3];
        pose.GetTransformedAxis(1, 1f, axis, 0);
        return new Vector3(axis[0], axis[1], axis[2]);
    }

    /// <summary>
    /// Rotation about Y, recovered from the pose quaternion. Only yaw is kept:
    /// the renderer's primitives are axis-aligned boxes with a single rotation
    /// per axis, and a wall's tilt is noise rather than shape.
    /// </summary>
    private static float YawOf(Pose pose)
    {
        var q = new float[4];
        pose.GetRotationQuaternion(q, 0);

        // q is (x, y, z, w).
        return MathF.Atan2(
            2f * (q[3] * q[1] + q[0] * q[2]),
            1f - 2f * (q[1] * q[1] + q[2] * q[2]));
    }

    private Session? EnsureSession()
    {
        if (_session is not null) return _session;
        if (_context is null) return null;

        try
        {
            var session = new Session(_context);

            var config = new Config(session);
            config.SetPlaneFindingMode(Config.PlaneFindingMode.HorizontalAndVertical);
            // Blocking would stall the scan thread for a whole camera frame on
            // every update; ARCore is explicitly designed to be polled.
            config.SetUpdateMode(Config.UpdateMode.LatestCameraImage);

            if (session.IsDepthModeSupported(Config.DepthMode.Automatic))
                config.SetDepthMode(Config.DepthMode.Automatic);

            session.Configure(config);

            return _session = session;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not create an ARCore session.");
            return null;
        }
    }

    /// <summary>
    /// The external texture ARCore writes camera frames into. Must be called with
    /// a GL context current on this thread; without one <c>glGenTextures</c>
    /// returns 0 and sets <c>GL_INVALID_OPERATION</c>, which the caller reports.
    /// </summary>
    private static int CreateCameraTexture()
    {
        var ids = new int[1];
        GLES20.GlGenTextures(1, ids, 0);
        var id = ids[0];
        if (id == NoTexture) return NoTexture;

        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, id);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        return id;
    }

    /// <summary>
    /// Prompts once and waits for the answer. Declining is not an error: the scan
    /// simply produces nothing and the room keeps whatever model it had.
    /// </summary>
    private async Task EnsureCameraPermissionAsync(CancellationToken ct)
    {
        // Runtime permissions arrived in API 23; below that a declared permission
        // is granted at install time and HasCameraPermission already returns true.
        if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return;
        if (HasCameraPermission) return;
        if (Uno.UI.ContextHelper.Current is not Android.App.Activity activity) return;

        try
        {
            activity.RequestPermissions([Android.Manifest.Permission.Camera], requestCode: 4712);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not request the camera permission.");
            return;
        }

        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline && !HasCameraPermission)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
        }
    }

    private bool HasCameraPermission =>
        _context is not null &&
        (!OperatingSystem.IsAndroidVersionAtLeast(23) ||
         _context.CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted);

    public void Dispose()
    {
        try { _session?.Close(); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not close the ARCore session."); }

        _session = null;
    }

    /// <summary>
    /// A GL ES 2 context made current on the calling thread against a 1×1
    /// pbuffer. Nothing is ever drawn into it; it exists so that the camera
    /// texture has a context to live in and <c>Session.Update()</c> has one to
    /// write into. Thread-affine: create, use and dispose on the same thread.
    /// </summary>
    private sealed class OffscreenGlContext : IDisposable
    {
        private readonly EGLDisplay _display;
        private readonly EGLSurface _surface;
        private readonly EGLContext _context;

        private OffscreenGlContext(EGLDisplay display, EGLSurface surface, EGLContext context)
        {
            _display = display;
            _surface = surface;
            _context = context;
        }

        public static OffscreenGlContext? Create()
        {
            // The EGL sentinels are Java objects; Equals compares their handles,
            // where == would only compare managed peers.
            var display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
            if (display is null || display.Equals(EGL14.EglNoDisplay)) return null;

            // Initialising an already-initialised display (the renderer's) is
            // allowed and only reports the version; it does not reset anything.
            if (!EGL14.EglInitialize(display, new int[1], 0, new int[1], 0)) return null;

            int[] configAttributes =
            [
                EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
                EGL14.EglSurfaceType, EGL14.EglPbufferBit,
                EGL14.EglRedSize, 8,
                EGL14.EglGreenSize, 8,
                EGL14.EglBlueSize, 8,
                EGL14.EglAlphaSize, 8,
                EGL14.EglNone,
            ];

            var configs = new EGLConfig[1];
            var configCount = new int[1];
            if (!EGL14.EglChooseConfig(display, configAttributes, 0, configs, 0, 1, configCount, 0)
                || configCount[0] == 0
                || configs[0] is not { } config)
                return null;

            int[] contextAttributes = [EGL14.EglContextClientVersion, 2, EGL14.EglNone];
            var context = EGL14.EglCreateContext(display, config, EGL14.EglNoContext, contextAttributes, 0);
            if (context is null || context.Equals(EGL14.EglNoContext)) return null;

            int[] surfaceAttributes = [EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone];
            var surface = EGL14.EglCreatePbufferSurface(display, config, surfaceAttributes, 0);
            if (surface is null || surface.Equals(EGL14.EglNoSurface))
            {
                EGL14.EglDestroyContext(display, context);
                return null;
            }

            if (!EGL14.EglMakeCurrent(display, surface, surface, context))
            {
                EGL14.EglDestroySurface(display, surface);
                EGL14.EglDestroyContext(display, context);
                return null;
            }

            return new OffscreenGlContext(display, surface, context);
        }

        public void Dispose()
        {
            EGL14.EglMakeCurrent(_display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
            EGL14.EglDestroySurface(_display, _surface);
            EGL14.EglDestroyContext(_display, _context);
            // Drops this thread's EGL bookkeeping. The display itself is shared
            // with Uno's renderer and is deliberately never terminated here.
            EGL14.EglReleaseThread();
        }
    }
}
