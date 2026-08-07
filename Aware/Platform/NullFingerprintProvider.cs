using Aware.Application;

namespace Aware.Platform;

/// <summary>
/// The simulated tier. Desktop and WebAssembly expose no room-identifying
/// sensors, so recognition falls back to the stored confidence and the
/// experience is unchanged (README capability tiers).
/// </summary>
public sealed class NullFingerprintProvider : IRoomFingerprintProvider
{
    public bool IsAvailable => false;

    public string Summary => "No ambient sensors on this device";

    public Task<FingerprintReading> ReadAsync(CancellationToken ct) =>
        Task.FromResult(FingerprintReading.Unavailable);
}
