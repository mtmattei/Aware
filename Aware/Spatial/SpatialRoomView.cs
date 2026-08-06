using System.Diagnostics;
using Aware.Application;
using Aware.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;
using Windows.System;

namespace Aware.Spatial;

public sealed class SpatialObjectSelectedEventArgs : EventArgs
{
    public SpatialObjectSelectedEventArgs(SpatialObjectId? objectId) => ObjectId = objectId;

    public SpatialObjectId? ObjectId { get; }
}

/// <summary>
/// The model viewport. It owns the orbit camera, projection, draw order, hit
/// testing and the frame clock; it owns no application state
/// (03-ARCHITECTURE state ownership).
/// </summary>
public sealed class SpatialRoomView : SKCanvasElement
{
    // 06-MOTION-BRIEF durations.
    private const float SelectionMs = 180f;
    private const float LensCrossfadeMs = 220f;
    private const float ReducedCrossfadeMs = 150f;
    private const float FramingMs = 420f;
    private const float InertiaMs = 320f;

    private const float TapSlopPx = 7f;
    private const float YawPerPixel = .008f;
    private const float PitchPerPixel = .004f;

    private const float AnchorRest = .52f;
    private const float AnchorRaised = .44f;

    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(
            nameof(Snapshot), typeof(SpatialRenderSnapshot), typeof(SpatialRoomView),
            new PropertyMetadata(null, OnSnapshotChanged));

    public static readonly DependencyProperty IsTrayOpenProperty =
        DependencyProperty.Register(
            nameof(IsTrayOpen), typeof(bool), typeof(SpatialRoomView),
            new PropertyMetadata(false, OnAnimatedStateChanged));

    public static readonly DependencyProperty ReducedMotionProperty =
        DependencyProperty.Register(
            nameof(ReducedMotion), typeof(bool), typeof(SpatialRoomView),
            new PropertyMetadata(false, OnAnimatedStateChanged));

    private readonly SpatialRoomRenderer _renderer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private float _yaw = Math3D.DefaultYaw;
    private float _pitch = Math3D.DefaultPitch;

    private float _assemblyMs;
    private float _selectionFactor;
    private float _annotationAlpha = 1f;
    private float _anchor = AnchorRest;
    private float _framingProgress;

    private SpatialObjectId? _fadingObjectId;
    private float _fadingFactor;

    private SpatialLens _lastLens = SpatialLens.Explore;
    private long _lastTickMs;

    private bool _dragging;
    private Point _dragStart;
    private Point _lastPoint;
    private long _lastMoveMs;
    private float _startYaw;
    private float _startPitch;
    private float _yawVelocity;
    private float _pitchVelocity;
    private float _inertiaRemainingMs;

    public SpatialRoomView()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCanceled;
        KeyDown += OnKeyDown;

        _timer.Tick += OnTick;
        Unloaded += OnUnloaded;
    }

    public SpatialRenderSnapshot? Snapshot
    {
        get => (SpatialRenderSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public bool IsTrayOpen
    {
        get => (bool)GetValue(IsTrayOpenProperty);
        set => SetValue(IsTrayOpenProperty, value);
    }

    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    public event EventHandler<SpatialObjectSelectedEventArgs>? ObjectSelected;

    public static bool IsSupported => IsSupportedOnCurrentPlatform();

    /// <summary>Returns the camera to the default framing (keyboard Home).</summary>
    public void ResetCamera()
    {
        _yaw = Math3D.DefaultYaw;
        _pitch = Math3D.DefaultPitch;
        _yawVelocity = _pitchVelocity = _inertiaRemainingMs = 0f;
        Invalidate();
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        var frame = new RenderFrame(
            _yaw,
            _pitch,
            Math3D.DefaultDistance,
            _assemblyMs,
            _selectionFactor,
            _fadingObjectId,
            _fadingFactor,
            _annotationAlpha,
            _anchor,
            ReducedMotion);

        _renderer.Render(canvas, (float)area.Width, (float)area.Height, frame);
    }

    // ------------------------------------------------------------------
    // State changes
    // ------------------------------------------------------------------

    private static void OnSnapshotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SpatialRoomView)d).ApplySnapshot(e.NewValue as SpatialRenderSnapshot, e.OldValue as SpatialRenderSnapshot);

    private static void OnAnimatedStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SpatialRoomView)d).EnsureRunning();

    private void ApplySnapshot(SpatialRenderSnapshot? snapshot, SpatialRenderSnapshot? previous)
    {
        var previousSelection = previous?.SelectedObjectId;
        _renderer.SetSnapshot(snapshot);

        if (snapshot is null)
        {
            Invalidate();
            return;
        }

        if (previousSelection != snapshot.SelectedObjectId)
        {
            // Hand the outgoing object its own fade so a re-selection reads as a
            // transition rather than a cut.
            if (previousSelection is { } outgoing && outgoing != snapshot.SelectedObjectId)
            {
                _fadingObjectId = outgoing;
                _fadingFactor = _selectionFactor;
            }

            _selectionFactor = 0f;
        }

        // A lens switch crossfades annotations without touching the camera or
        // rebuilding geometry (06-MOTION-BRIEF).
        if (_lastLens != snapshot.ActiveLens)
        {
            _lastLens = snapshot.ActiveLens;
            _annotationAlpha = 0f;
        }

        EnsureRunning();
        Invalidate();
    }

    // ------------------------------------------------------------------
    // Frame clock
    // ------------------------------------------------------------------

    private void EnsureRunning()
    {
        if (_timer.IsEnabled) return;
        _lastTickMs = _clock.ElapsedMilliseconds;
        _timer.Start();
    }

    private void OnTick(object? sender, object e)
    {
        var now = _clock.ElapsedMilliseconds;
        var dt = MathF.Min(now - _lastTickMs, 64f);
        _lastTickMs = now;

        var reduced = ReducedMotion;
        var animating = false;

        if (Snapshot is not null &&
            !_renderer.IsAssemblyComplete(_assemblyMs, reduced))
        {
            _assemblyMs += dt;
            animating = true;
        }

        animating |= Advance(ref _selectionFactor,
            Snapshot?.SelectedObjectId is not null ? 1f : 0f,
            dt / (reduced ? ReducedCrossfadeMs : SelectionMs));

        if (_fadingObjectId is not null)
        {
            var stillFading = Advance(ref _fadingFactor, 0f, dt / (reduced ? ReducedCrossfadeMs : SelectionMs));
            if (!stillFading) _fadingObjectId = null;
            animating |= stillFading;
        }

        animating |= Advance(ref _annotationAlpha, 1f,
            dt / (reduced ? ReducedCrossfadeMs : LensCrossfadeMs));

        // Framing runs as normalized progress so it can carry the tray's own
        // cubic-bezier(.18,.82,.22,1) rather than a linear ramp.
        animating |= Advance(ref _framingProgress, IsTrayOpen ? 1f : 0f,
            dt / (reduced ? ReducedCrossfadeMs : FramingMs));

        _anchor = AnchorRest + (AnchorRaised - AnchorRest) * CubicBezierEase.Tray.Ease(_framingProgress);

        animating |= AdvanceInertia(dt, reduced);

        Invalidate();

        if (!animating) _timer.Stop();
    }

    /// <summary>Moves <paramref name="value"/> toward a target; returns true while still moving.</summary>
    private static bool Advance(ref float value, float target, float step)
    {
        if (MathF.Abs(target - value) < .0015f)
        {
            value = target;
            return false;
        }

        var delta = MathF.Min(MathF.Abs(step), MathF.Abs(target - value));
        value += MathF.Sign(target - value) * delta;
        return true;
    }

    private bool AdvanceInertia(float dt, bool reduced)
    {
        if (reduced || _inertiaRemainingMs <= 0f || _dragging) return false;

        _inertiaRemainingMs = MathF.Max(_inertiaRemainingMs - dt, 0f);
        var decay = _inertiaRemainingMs / InertiaMs;

        _yaw += _yawVelocity * dt * decay;
        _pitch = Math3D.ClampPitch(_pitch + _pitchVelocity * dt * decay);

        if (_inertiaRemainingMs <= 0f)
        {
            _yawVelocity = _pitchVelocity = 0f;
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Pointer input — direct orbit while pressed, no easing during drag
    // ------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _inertiaRemainingMs = 0f;
        _yawVelocity = _pitchVelocity = 0f;

        _dragStart = _lastPoint = e.GetCurrentPoint(this).Position;
        _lastMoveMs = _clock.ElapsedMilliseconds;
        _startYaw = _yaw;
        _startPitch = _pitch;

        Focus(FocusState.Pointer);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var point = e.GetCurrentPoint(this).Position;
        var now = _clock.ElapsedMilliseconds;
        var dt = MathF.Max(now - _lastMoveMs, 1f);

        _yaw = _startYaw + (float)(point.X - _dragStart.X) * YawPerPixel;
        _pitch = Math3D.ClampPitch(_startPitch + (float)(point.Y - _dragStart.Y) * PitchPerPixel);

        _yawVelocity = (float)(point.X - _lastPoint.X) * YawPerPixel / dt;
        _pitchVelocity = (float)(point.Y - _lastPoint.Y) * PitchPerPixel / dt;

        _lastPoint = point;
        _lastMoveMs = now;

        Invalidate();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var point = e.GetCurrentPoint(this).Position;
        var travelled = MathF.Sqrt(
            (float)((point.X - _dragStart.X) * (point.X - _dragStart.X) +
                    (point.Y - _dragStart.Y) * (point.Y - _dragStart.Y)));

        _dragging = false;
        ReleasePointerCapture(e.Pointer);

        if (travelled < TapSlopPx)
        {
            ObjectSelected?.Invoke(this,
                new SpatialObjectSelectedEventArgs(_renderer.HitTest((float)point.X, (float)point.Y)));
        }
        else if (!ReducedMotion &&
                 MathF.Abs(_yawVelocity) + MathF.Abs(_pitchVelocity) > .00012f)
        {
            _inertiaRemainingMs = InertiaMs;
            EnsureRunning();
        }

        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
    }

    // ------------------------------------------------------------------
    // Keyboard orbit (08-ACCESSIBILITY-TESTS)
    // ------------------------------------------------------------------

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        const float yawStep = .09f;
        const float pitchStep = .045f;

        switch (e.Key)
        {
            case VirtualKey.Left: _yaw -= yawStep; break;
            case VirtualKey.Right: _yaw += yawStep; break;
            case VirtualKey.Up: _pitch = Math3D.ClampPitch(_pitch - pitchStep); break;
            case VirtualKey.Down: _pitch = Math3D.ClampPitch(_pitch + pitchStep); break;
            case VirtualKey.Home: ResetCamera(); e.Handled = true; return;
            default: return;
        }

        Invalidate();
        e.Handled = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _renderer.Dispose();
    }
}
