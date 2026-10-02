using System.Numerics;
using Aware.Application;
using Aware.Domain;
using Aware.Infrastructure;
using Aware.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aware.Tests;

/// <summary>
/// Who may be linked to a place. The sample garage is the case that matters:
/// linking a fictional room to a real place is the incoherence SPEC set out to
/// end, and the button that allowed it stayed on the sample after step 7 shipped.
/// </summary>
public class PlaceLinkingTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly RoomFingerprint Kitchen = new(
        WifiFeatureHash: "wifi-kitchen-01",
        BluetoothFeatureHash: string.Empty,
        AmbientLightVector: new Vector3(240f, 0f, 0f),
        MagneticVector: new Vector3(18.4f, -3.2f, 39.9f),
        PressureHpa: 1011.4f,
        AcousticEmbeddingId: null);

    private static Task<FingerprintReading> Sensed() =>
        new SimulatedFingerprintProvider("1").ReadAsync(CancellationToken.None);

    /// <summary>A modelled room the user built, not yet tied to a place.</summary>
    private static SpatialRoom UnlinkedRealRoom() =>
        SampleGarageFactory.Create() with { Id = new RoomId("room-workshop-002"), Name = "Workshop" };

    [Fact]
    public async Task ARealUnlinkedRoomCanBeLinkedWhereSomethingIsSensed()
    {
        Assert.True(PlaceLinking.CanLink(UnlinkedRealRoom(), await Sensed()));
    }

    [Fact]
    public async Task TheSampleGarageIsNeverLinkable()
    {
        var garage = SampleGarageFactory.Create();

        Assert.Null(garage.Fingerprint);
        Assert.True(SampleGarageFactory.IsSample(garage.Id));
        Assert.False(PlaceLinking.CanLink(garage, await Sensed()));
    }

    [Fact]
    public async Task ARoomAlreadyLinkedIsNotOfferedAgain()
    {
        var kitchen = UnmodelledRoomFactory.Create("Kitchen", Kitchen, When);

        Assert.False(PlaceLinking.CanLink(kitchen, await Sensed()));
    }

    [Fact]
    public void NothingIsLinkableWithoutASignal()
    {
        Assert.False(PlaceLinking.CanLink(UnlinkedRealRoom(), FingerprintReading.Unavailable));
        Assert.False(PlaceLinking.CanLink(UnlinkedRealRoom(), null));
        Assert.False(PlaceLinking.CanLink(null, FingerprintReading.Unavailable));
    }

    /// <summary>
    /// A real room never collides with the sample's id, so the rule cannot
    /// accidentally silence the button on something the user made.
    /// </summary>
    [Fact]
    public void AnAddedPlaceIsNotMistakenForTheSample()
    {
        var kitchen = UnmodelledRoomFactory.Create("Kitchen", Kitchen, When);

        Assert.False(SampleGarageFactory.IsSample(kitchen.Id));
    }

    /// <summary>
    /// The line recognition settles on is where the user would otherwise read
    /// "not linked to a place yet" and go looking for the button. With sensors
    /// live and the sample open, it must read as a stable model and nothing more.
    /// </summary>
    [Fact]
    public async Task RecognitionNeverInvitesTheSampleToBeLinked()
    {
        var service = new SensorRoomRecognitionService(
            new SimulatedFingerprintProvider("1"),
            new FingerprintMatcher(),
            new StillMotion(),
            NullLogger<SensorRoomRecognitionService>.Instance);

        RecognitionState? settled = null;
        await foreach (var state in service.RecognizeAsync(SampleGarageFactory.Create(), null, CancellationToken.None))
            settled = state;

        Assert.NotNull(settled);
        Assert.True(settled.IsStable);
        Assert.Equal("98% confident · stable room model", settled.Message);
    }

    private sealed class StillMotion : IMotionSettings
    {
        public bool ReducedMotion => true;
        public void SetUserOverride(bool? reducedMotion) { }
        public event EventHandler? Changed { add { } remove { } }
    }
}
