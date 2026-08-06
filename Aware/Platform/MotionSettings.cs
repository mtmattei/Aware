namespace Aware.Platform;

public interface IMotionSettings
{
    /// <summary>True when stagger, inertia and scale settling must be suppressed.</summary>
    bool ReducedMotion { get; }

    void SetUserOverride(bool? reducedMotion);

    event EventHandler? Changed;
}

/// <summary>
/// Reads the platform animation preference where one is exposed, and lets the
/// user override it in-app. 08-ACCESSIBILITY-TESTS requires reduced motion to be
/// honoured; being able to toggle it makes the behaviour verifiable on desktop.
/// </summary>
public sealed class MotionSettings : IMotionSettings
{
    private readonly bool _systemReducedMotion;
    private bool? _userOverride;

    public MotionSettings()
    {
        _systemReducedMotion = ReadSystemPreference();
    }

    public bool ReducedMotion => _userOverride ?? _systemReducedMotion;

    public event EventHandler? Changed;

    public void SetUserOverride(bool? reducedMotion)
    {
        if (_userOverride == reducedMotion) return;
        _userOverride = reducedMotion;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool ReadSystemPreference()
    {
        try
        {
            return !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            // Not every target surfaces the preference; default to full motion.
            return false;
        }
    }
}
