using Aware.Application;
using Aware.Platform;

namespace Aware.Tests;

/// <summary>
/// The simulated provider exists so the link and recognition flows can be driven
/// where there are no sensors. That only works if its two places actually score
/// as different places — fixtures that drifted into agreement would make the
/// contradiction path silently unreachable while still looking exercised.
/// </summary>
public class SimulatedSensorTests
{
    private static readonly FingerprintMatcher Matcher = new();

    private static async Task<FingerprintReading> Read(string? mode) =>
        await new SimulatedFingerprintProvider(mode).ReadAsync(CancellationToken.None);

    [Fact]
    public async Task TheSamePlaceReadTwiceIsAMatch()
    {
        var first = await Read("1");
        var second = await Read("1");

        var comparison = Matcher.Compare(first.Fingerprint, second);

        Assert.True(comparison.IsRecognized);
        Assert.Empty(comparison.Contradictions);
        Assert.Equal(1f, comparison.Confidence, precision: 3);
    }

    [Fact]
    public async Task TheOtherPlaceContradictsEverySignal()
    {
        var here = await Read("1");
        var elsewhere = await Read("elsewhere");

        var comparison = Matcher.Compare(here.Fingerprint, elsewhere);

        Assert.False(comparison.IsRecognized);
        Assert.Empty(comparison.Agreements);
        // Five since Bluetooth joined the fingerprint as the sharp signal.
        Assert.Equal(5, comparison.Contradictions.Count);
    }

    [Fact]
    public async Task EverySimulatedSignalReportsAsAvailable()
    {
        var reading = await Read("1");

        Assert.True(reading.HasAnySignal);
        Assert.All(reading.Signals, s => Assert.True(s.IsAvailable));
    }

    /// <summary>
    /// The mode string is the whole opt-in. Anything unset or blank must leave a
    /// sensorless platform sensorless, or the capability tiers stop meaning
    /// anything and desktop silently starts claiming to sense a room.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("1", true)]
    [InlineData("elsewhere", true)]
    public void OptInIsDrivenEntirelyByTheModeString(string? mode, bool enabled)
    {
        var previous = Environment.GetEnvironmentVariable(
            SimulatedFingerprintProvider.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                SimulatedFingerprintProvider.EnvironmentVariable, mode);

            Assert.Equal(enabled, SimulatedFingerprintProvider.IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                SimulatedFingerprintProvider.EnvironmentVariable, previous);
        }
    }
}
