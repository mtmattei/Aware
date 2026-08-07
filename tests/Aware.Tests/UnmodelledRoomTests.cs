using System.Numerics;
using System.Text.Json;
using Aware.Application;
using Aware.Domain;
using Aware.Infrastructure;

namespace Aware.Tests;

/// <summary>
/// A recognition-only room: a known place with no model of it yet. The point of
/// these is that geometry and place stay independent — a room can have either,
/// both, or neither, and nothing may conflate them.
/// </summary>
public class UnmodelledRoomTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly RoomFingerprint Kitchen = new(
        WifiFeatureHash: "wifi-kitchen-01",
        BluetoothFeatureHash: string.Empty,
        AmbientLightVector: new Vector3(240f, 0f, 0f),
        MagneticVector: new Vector3(18.4f, -3.2f, 39.9f),
        PressureHpa: 1011.4f,
        AcousticEmbeddingId: null);

    private static SpatialRoom Kitchen_() => UnmodelledRoomFactory.Create("Kitchen", Kitchen, When);

    [Fact]
    public void AnUnmodelledRoomKnowsItsPlaceAndNothingElse()
    {
        var room = Kitchen_();

        Assert.True(room.IsLinkedToPlace);
        Assert.False(room.IsModelled);
        Assert.Empty(room.Shell);
        Assert.Empty(room.Objects);
        Assert.Equal("Kitchen", room.Name);
    }

    /// <summary>
    /// Zero is "no geometry", not "geometry we doubt". Anything rendering it as a
    /// percentage would be stating a number about nothing.
    /// </summary>
    [Fact]
    public void ItCarriesNoModelConfidence()
    {
        Assert.Equal(0f, Kitchen_().Confidence);
        Assert.Equal(0, Kitchen_().ModelVersion);
    }

    [Fact]
    public void NamesAreTrimmed()
    {
        Assert.Equal("Kitchen", UnmodelledRoomFactory.Create("  Kitchen  ", Kitchen, When).Name);
    }

    /// <summary>
    /// Colliding with the sample's id would let re-seeding overwrite a real room.
    /// </summary>
    [Fact]
    public void EachRoomGetsItsOwnIdAndNeverTheSamples()
    {
        var a = Kitchen_();
        var b = UnmodelledRoomFactory.Create("Kitchen", Kitchen, When);

        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(SampleGarageFactory.GarageId, a.Id);
        Assert.NotEqual(SampleGarageFactory.GarageId, b.Id);
    }

    [Fact]
    public void TheSeededGarageIsModelled()
    {
        Assert.True(SampleGarageFactory.Create().IsModelled);
    }

    [Fact]
    public void ItRoundTripsWithItsPlaceIntactAndNoGeometry()
    {
        var json = JsonSerializer.Serialize(Kitchen_(), SpatialJsonContext.Default.SpatialRoom);
        var restored = JsonSerializer.Deserialize(json, SpatialJsonContext.Default.SpatialRoom)!;

        Assert.Equal(Kitchen, restored.Fingerprint);
        Assert.False(restored.IsModelled);
        Assert.DoesNotContain("isModelled", json);
    }

    // -- how it reads ------------------------------------------------------

    private static FingerprintComparison Match =>
        new(.97f, ["Magnetic signature", "Air pressure", "Ambient light"], []);

    private static FingerprintComparison Mismatch =>
        new(.08f, [], ["Magnetic signature", "Air pressure"]);

    [Fact]
    public void RecognizedHereReadsWithoutAPercentage()
    {
        var line = RecognitionMessages.Settled(0f, Match, canLink: false, isModelled: false);

        Assert.Equal("No model yet · you are here · 3 signals match", line);
        Assert.DoesNotContain("%", line);
    }

    /// <summary>
    /// A room with no model has no per-signal story worth telling about a place
    /// it is not in; the fact that it is the wrong place is the whole message.
    /// </summary>
    [Fact]
    public void SomewhereElseStaysShort()
    {
        Assert.Equal(
            "No model yet · you are somewhere else",
            RecognitionMessages.Settled(0f, Mismatch, canLink: false, isModelled: false));
    }

    [Fact]
    public void WithNoSensorsItSaysWhyItCannotRecognizeItself()
    {
        Assert.Equal(
            "No model yet · nothing to recognize it by",
            RecognitionMessages.Settled(0f, null, canLink: false, isModelled: false));
    }

    /// <summary>Every modelled-room line is unchanged by the new parameter.</summary>
    [Theory]
    [InlineData(false, "98% confident · stable room model")]
    public void ModelledLinesAreUntouched(bool canLink, string expected)
    {
        Assert.Equal(expected, RecognitionMessages.Settled(.98f, null, canLink));
        Assert.Equal(
            "98% confident · you are here · 3 signals match",
            RecognitionMessages.Settled(.98f, Match, canLink: false));
        Assert.Equal(
            "98% confident · you are somewhere else · 2 signals disagree",
            RecognitionMessages.Settled(.98f, Mismatch, canLink: false));
        Assert.Equal(
            "98% confident · not linked to a place yet",
            RecognitionMessages.Settled(.98f, null, canLink: true));
    }
}
