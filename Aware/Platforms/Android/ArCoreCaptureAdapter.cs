using System.Numerics;
using System.Runtime.CompilerServices;
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
/// </summary>
public sealed class ArCoreCaptureAdapter : ISpatialCaptureAdapter, IDisposable
{
    /// <summary>
    /// ARCore needs a GL texture to attach the camera image to before
    /// <c>Session.Update()</c> will return frames, even when nothing is drawn.
    /// Tracking is driven by that image, so there is no headless mode.
    /// </summary>
    private const int NoTexture = 0;

    private readonly ILogger<ArCoreCaptureAdapter> _log;
    private readonly Context? _context;

    private Session? _session;
    private int _textureId = NoTexture;

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

        // The texture is created once and reused: ARCore rejects a session whose
        // camera texture changes underneath it.
        EnsureCameraTexture();
        session.SetCameraTextureName(_textureId);

        try
        {
            session.Resume();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not resume the ARCore session.");
            yield break;
        }

        var deadline = DateTimeOffset.Now + request.MaximumDuration;

        try
        {
            while (DateTimeOffset.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();

                // ~10 Hz. Faster gains nothing: planes are extended over seconds,
                // and every frame allocates a managed observation.
                await Task.Delay(100, ct);

                var observation = ReadPlanes(session);
                if (observation is not null) yield return observation;
            }
        }
        finally
        {
            try { session.Pause(); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not pause the ARCore session."); }
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
    /// Rotation about Y, recovered from the pose quaternion. Only yaw is kept:
    /// the renderer's primitives are axis-aligned boxes with a single rotation
    /// per axis, and a wall's tilt is noise rather than shape.
    /// </summary>
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
            // Blocking would stall the UI thread on every frame; ARCore is
            // explicitly designed to be polled.
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

    private void EnsureCameraTexture()
    {
        if (_textureId != NoTexture) return;

        var ids = new int[1];
        GLES20.GlGenTextures(1, ids, 0);
        _textureId = ids[0];

        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _textureId);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlLinear);
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
}
