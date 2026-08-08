using System.Numerics;
using Aware.Application;
using Aware.Domain;

namespace Aware.Tests;

/// <summary>
/// Regression tests built from readings actually taken on a Pixel 8 on
/// 2026-08-07, not from invented numbers.
///
/// <para>The bug these pin: walking into two other rooms and relaunching still
/// opened the kitchen. Wi-Fi contributes nothing on Android 13+ (the location
/// gate on getScanResults, see AndroidManifest.xml), so the match ran on
/// magnetic, pressure and light — and the magnetic term scored the phone's
/// <em>orientation</em> rather than its place, while pressure drifts with the
/// weather. Every room agreed with every other room.</para>
/// </summary>
public class FieldMeasuredMatchTests
{
    private static readonly FingerprintMatcher Matcher = new();

    /// <summary>Kitchen, 22:06. ‖B‖ = 46.2 µT.</summary>
    private static readonly RoomFingerprint Kitchen = Measured(
        light: 0.51124996f,
        magnetic: new Vector3(4.6116f, -11.773f, -44.4324f),
        pressure: 1011.7278f);

    /// <summary>
    /// The same kitchen thirteen minutes later, phone held differently.
    /// ‖B‖ = 45.5 µT — but 29 µT away from <see cref="Kitchen"/> as a vector.
    /// </summary>
    private static readonly RoomFingerprint KitchenAgain = Measured(
        light: 0.80125f,
        magnetic: new Vector3(18.848999f, 13.297999f, -39.5036f),
        pressure: 1011.56805f);

    /// <summary>A different room the same evening. ‖B‖ = 21.3 µT.</summary>
    private static readonly RoomFingerprint OtherRoom = Measured(
        light: 10.648749f,
        magnetic: new Vector3(-3.1842f, -14.3228f, -15.4086f),
        pressure: 1015.00116f);

    private static RoomFingerprint Measured(float light, Vector3 magnetic, float pressure) =>
        new(
            // Empty on every Android 13+ device: the scan is refused.
            WifiFeatureHash: string.Empty,
            BluetoothFeatureHash: string.Empty,
            AmbientLightVector: new Vector3(light, 0f, 0f),
            MagneticVector: magnetic,
            PressureHpa: pressure,
            AcousticEmbeddingId: null);

    private static FingerprintReading Live(RoomFingerprint fingerprint) =>
        new(fingerprint, [new FingerprintSignal("test", "test", true)], DateTimeOffset.Now);

    /// <summary>
    /// The failure the user hit: standing somewhere else, the kitchen still won.
    /// </summary>
    [Fact]
    public void AnotherRoomIsNotRecognizedAsTheKitchen()
    {
        var result = Matcher.Compare(Kitchen, Live(OtherRoom));

        Assert.False(
            result.IsRecognized,
            $"a different room scored {result.Confidence:0.00} against the kitchen");
    }

    /// <summary>
    /// And the other half: the fix must not make a room stop recognizing itself
    /// when the phone is simply held at a different angle.
    /// </summary>
    [Fact]
    public void TheKitchenStillRecognizesItselfInADifferentOrientation()
    {
        var result = Matcher.Compare(Kitchen, Live(KitchenAgain));

        Assert.True(
            result.IsRecognized,
            $"the same room scored only {result.Confidence:0.00} against itself");
    }

    /// <summary>
    /// The two must be separated by a real margin, not by a hair either side of
    /// the bar, or ordinary drift will flip the verdict.
    /// </summary>
    [Fact]
    public void SameRoomOutscoresDifferentRoomByAClearMargin()
    {
        var same = Matcher.Compare(Kitchen, Live(KitchenAgain)).Confidence;
        var different = Matcher.Compare(Kitchen, Live(OtherRoom)).Confidence;

        Assert.True(
            same - different > .3f,
            $"margin too thin: same room {same:0.00}, different room {different:0.00}");
    }

    /// <summary>
    /// Direction is device-frame and therefore describes how the phone is being
    /// held. Two readings in one room 29 µT apart as vectors must still agree.
    /// </summary>
    [Fact]
    public void MagneticAgreementSurvivesReorientingThePhone()
    {
        var result = Matcher.Compare(Kitchen, Live(KitchenAgain));

        Assert.Contains("Magnetic signature", result.Agreements);
    }

    /// <summary>
    /// Weather moved the same room 3.4 hPa in one day, more than twice the old
    /// "one storey" tolerance. Pressure must not call that a contradiction.
    /// </summary>
    [Fact]
    public void WeatherDriftIsNotAContradiction()
    {
        var laterThatDay = Kitchen with { PressureHpa = Kitchen.PressureHpa + 3.4f };

        var result = Matcher.Compare(Kitchen, Live(laterThatDay));

        Assert.DoesNotContain("Air pressure", result.Contradictions);
        Assert.True(result.IsRecognized);
    }

    /// <summary>
    /// Bluetooth replaces Wi-Fi as the sharp signal, so a disagreeing
    /// neighbourhood has to be able to overrule everything else agreeing.
    /// </summary>
    [Fact]
    public void ADifferentBluetoothNeighbourhoodOverrulesTheRest()
    {
        var stored = Kitchen with { BluetoothFeatureHash = "bt-aaaaaaaaaaaa" };
        var elsewhere = KitchenAgain with { BluetoothFeatureHash = "bt-bbbbbbbbbbbb" };

        var result = Matcher.Compare(stored, Live(elsewhere));

        Assert.False(result.IsRecognized);
        Assert.Contains("Bluetooth neighbourhood", result.Contradictions);
    }

    [Fact]
    public void AMatchingBluetoothNeighbourhoodConfirmsTheRoom()
    {
        var stored = Kitchen with { BluetoothFeatureHash = "bt-aaaaaaaaaaaa" };
        var same = KitchenAgain with { BluetoothFeatureHash = "bt-aaaaaaaaaaaa" };

        var result = Matcher.Compare(stored, Live(same));

        Assert.True(result.IsRecognized);
        Assert.Contains("Bluetooth neighbourhood", result.Agreements);
    }
}
