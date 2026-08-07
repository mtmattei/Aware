namespace Aware.Platform;

/// <summary>
/// Kept apart from <see cref="MotionSettings"/> so anything that only needs to
/// ask "is reduced motion on?" does not drag in the platform preference lookup.
/// The same split as <c>ISpatialCaptureAdapter</c> and its adapters.
/// </summary>
public interface IMotionSettings
{
    /// <summary>True when stagger, inertia and scale settling must be suppressed.</summary>
    bool ReducedMotion { get; }

    void SetUserOverride(bool? reducedMotion);

    event EventHandler? Changed;
}
