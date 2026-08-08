using System.Numerics;
using Aware.Application;
using Aware.Domain;
using Aware.Infrastructure;

namespace Aware.Tests;

/// <summary>
/// Pins the one support line under each room name. It is the only thing telling
/// the user why a room is in the list, so a wrong line is a quietly misleading
/// list rather than a visible break.
/// </summary>
public class RoomListMessagesTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly RoomFingerprint SomePlace = new(
        WifiFeatureHash: "wifi-kitchen-01",
        BluetoothFeatureHash: string.Empty,
        AmbientLightVector: new Vector3(240f, 0f, 0f),
        MagneticVector: new Vector3(18.4f, -3.2f, 39.9f),
        PressureHpa: 1011.4f,
        AcousticEmbeddingId: null);

    /// <summary>The sample garage, trimmed to a given number of objects.</summary>
    private static SpatialRoom Modelled(int objects, bool linked)
    {
        var garage = SampleGarageFactory.Create();

        return garage with
        {
            Fingerprint = linked ? SomePlace : null,
            Objects = garage.Objects.Take(objects).ToList(),
        };
    }

    private static SpatialRoom Unmodelled() =>
        UnmodelledRoomFactory.Create("Kitchen", SomePlace, When);

    [Fact]
    public void RecognizedHereOutranksEverythingElse()
    {
        var line = RoomListMessages.Reason(Modelled(3, linked: true), isRecognizedHere: true);

        Assert.Equal("Recognized here now", line);
    }

    [Fact]
    public void RecognizedHereWinsForARoomWithNoModelToo()
    {
        var line = RoomListMessages.Reason(Unmodelled(), isRecognizedHere: true);

        Assert.Equal("Recognized here now", line);
    }

    /// <summary>
    /// "0 objects" would read as a room that was mapped and found empty, which is
    /// the opposite of what a recognition-only room means.
    /// </summary>
    [Fact]
    public void AnUnmodelledRoomSaysSoRatherThanCountingZero()
    {
        var line = RoomListMessages.Reason(Unmodelled(), isRecognizedHere: false);

        Assert.Equal("No model yet", line);
    }

    [Fact]
    public void AModelledRoomReportsItsObjectCount()
    {
        var line = RoomListMessages.Reason(Modelled(3, linked: true), isRecognizedHere: false);

        Assert.Equal("3 objects", line);
    }

    [Fact]
    public void AModelledRoomWithNoPlaceSaysItIsUnlinked()
    {
        var line = RoomListMessages.Reason(Modelled(3, linked: false), isRecognizedHere: false);

        Assert.Equal("3 objects · not linked to a place", line);
    }

    [Fact]
    public void OneObjectIsSingular()
    {
        var line = RoomListMessages.Reason(Modelled(1, linked: true), isRecognizedHere: false);

        Assert.Equal("1 object", line);
    }

    /// <summary>
    /// A shell with no objects is still a model. Calling it "No model yet" would
    /// deny the geometry the renderer is about to draw.
    /// </summary>
    [Fact]
    public void AShellWithNoObjectsIsStillModelled()
    {
        var shellOnly = Modelled(0, linked: true);
        Assert.NotEmpty(shellOnly.Shell);

        Assert.Equal("0 objects", RoomListMessages.Reason(shellOnly, isRecognizedHere: false));
    }
}
