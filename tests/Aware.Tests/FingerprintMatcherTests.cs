using System.Numerics;
using Aware.Application;
using Aware.Domain;

namespace Aware.Tests;

/// <summary>
/// "Matching signals raise confidence, contradictions lower confidence"
/// (08-ACCESSIBILITY-TESTS), scored across the signals a phone can read without
/// a camera.
/// </summary>
public class FingerprintMatcherTests
{
    private static readonly FingerprintMatcher Matcher = new();

    private static RoomFingerprint Stored { get; } = new(
        WifiFeatureHash: "wifi-9f2c41",
        BluetoothFeatureHash: string.Empty,
        AmbientLightVector: new Vector3(120f, 0f, 0f),
        MagneticVector: new Vector3(12f, -44f, 61f),
        PressureHpa: 1008f,
        AcousticEmbeddingId: null);

    private static FingerprintReading Live(RoomFingerprint fingerprint) =>
        new(fingerprint, [new FingerprintSignal("test", "test", true)], DateTimeOffset.Now);

    [Fact]
    public void AnIdenticalReadingIsFullyRecognized()
    {
        var result = Matcher.Compare(Stored, Live(Stored));

        Assert.True(result.IsRecognized);
        Assert.Equal(1f, result.Confidence, precision: 3);
        Assert.Empty(result.Contradictions);
        Assert.Contains("Wi-Fi neighbourhood", result.Agreements);
    }

    [Fact]
    public void ADifferentRoomIsNotRecognized()
    {
        var elsewhere = Stored with
        {
            WifiFeatureHash = "wifi-different",
            MagneticVector = -Stored.MagneticVector,
            PressureHpa = 1035f,
            AmbientLightVector = new Vector3(6000f, 0f, 0f),
        };

        var result = Matcher.Compare(Stored, Live(elsewhere));

        Assert.False(result.IsRecognized);
        Assert.Contains("Wi-Fi neighbourhood", result.Contradictions);
    }

    [Fact]
    public void EachContradictionLowersConfidence()
    {
        var all = Matcher.Compare(Stored, Live(Stored)).Confidence;
        var oneOff = Matcher.Compare(Stored, Live(Stored with { PressureHpa = 1040f })).Confidence;
        var twoOff = Matcher.Compare(Stored, Live(Stored with
        {
            PressureHpa = 1040f,
            WifiFeatureHash = "wifi-elsewhere",
        })).Confidence;

        Assert.True(oneOff < all, $"One contradiction should lower confidence: {oneOff} vs {all}.");
        Assert.True(twoOff < oneOff, $"Two contradictions should lower it further: {twoOff} vs {oneOff}.");
    }

    [Fact]
    public void MissingSignalsAreExcludedRatherThanCountedAgainstTheRoom()
    {
        // A device with no barometer and no light sensor still matches on Wi-Fi
        // and magnetics alone.
        var partial = Stored with
        {
            PressureHpa = 0f,
            AmbientLightVector = Vector3.Zero,
        };

        var result = Matcher.Compare(Stored, Live(partial));

        Assert.True(result.IsRecognized);
        Assert.Equal(1f, result.Confidence, precision: 3);
        Assert.DoesNotContain("Air pressure", result.Agreements);
        Assert.DoesNotContain("Air pressure", result.Contradictions);
    }

    [Fact]
    public void AReadingWithNoSignalsAtAllScoresZero()
    {
        var result = Matcher.Compare(Stored, FingerprintReading.Unavailable);

        Assert.Equal(0f, result.Confidence);
        Assert.Empty(result.Agreements);
        Assert.Empty(result.Contradictions);
    }

    /// <summary>
    /// Deliberately no longer detects a change of floor. A tolerance tight enough
    /// for one storey (~1.5 hPa) makes a room contradict itself within a day:
    /// measured on a Pixel 8, one room read 1015.0 hPa in the morning and 1011.6
    /// that night, 3.4 hPa of pure weather. Identity has to survive the weather,
    /// so the storey-sized signal is what gets given up.
    /// </summary>
    [Fact]
    public void PressureToleratesADayOfWeather()
    {
        var drift = Matcher.Compare(Stored, Live(Stored with { PressureHpa = 1008f + 3.4f }));
        Assert.Contains("Air pressure", drift.Agreements);

        // Only a gross change still reads as somewhere else.
        var farOff = Matcher.Compare(Stored, Live(Stored with { PressureHpa = 1008f - 12f }));
        Assert.Contains("Air pressure", farOff.Contradictions);
    }

    [Fact]
    public void LightIsComparedOnALogScaleSoALampDoesNotBreakTheMatch()
    {
        var dimmer = Matcher.Compare(Stored, Live(Stored with
        {
            AmbientLightVector = new Vector3(70f, 0f, 0f),
        }));

        Assert.Contains("Ambient light", dimmer.Agreements);

        // Daylight against a lit room is a different story.
        var daylight = Matcher.Compare(Stored, Live(Stored with
        {
            AmbientLightVector = new Vector3(20000f, 0f, 0f),
        }));

        Assert.Contains("Ambient light", daylight.Contradictions);
    }

    /// <summary>
    /// The inverse of what this used to assert, and the correction matters.
    /// The magnetometer reports in device coordinates, so direction tracks how
    /// the phone is being held, not where it is: two readings in one kitchen
    /// thirteen minutes apart were 29 µT apart as vectors and 0.7 µT apart as
    /// magnitudes. Scoring direction was scoring the user's wrist.
    /// </summary>
    [Fact]
    public void MagneticUsesStrengthAndIgnoresDirection()
    {
        // Phone turned over. Same place, so this must still agree.
        var flipped = Matcher.Compare(Stored, Live(Stored with
        {
            MagneticVector = -Stored.MagneticVector,
        }));
        Assert.Contains("Magnetic signature", flipped.Agreements);

        // A materially weaker field is a different place, whatever way it points.
        var weaker = Matcher.Compare(Stored, Live(Stored with
        {
            MagneticVector = Stored.MagneticVector * .5f,
        }));
        Assert.Contains("Magnetic signature", weaker.Contradictions);
    }

    [Fact]
    public void WifiAloneCarriesEnoughWeightToRecognizeARoom()
    {
        // Only Wi-Fi is present on both sides, and it matches.
        var wifiOnly = new RoomFingerprint(Stored.WifiFeatureHash, string.Empty,
            Vector3.Zero, Vector3.Zero, 0f, null);

        var result = Matcher.Compare(Stored, Live(wifiOnly));

        Assert.True(result.IsRecognized);
        Assert.Equal(["Wi-Fi neighbourhood"], result.Agreements);
    }
}
