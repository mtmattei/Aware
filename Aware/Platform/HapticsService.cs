using Aware.Application;

namespace Aware.Platform;

/// <summary>
/// Light haptic after a confirmed hit (06-MOTION-BRIEF). Desktop and WebAssembly
/// have no vibration surface, so the call is a no-op there rather than a branch
/// at every call site. Mobile implementations go in a partial
/// <c>HapticsService.Android.cs</c> / <c>.iOS.cs</c> under Platforms/, keeping
/// native types off this signature.
/// </summary>
public sealed class HapticsService : IHapticsService
{
    private readonly ILogger<HapticsService> _log;

    public HapticsService(ILogger<HapticsService> log) => _log = log;

    public void Play(HapticKind kind) =>
        _log.LogTrace("Haptic {Kind} requested; no vibration surface on this target.", kind);
}
