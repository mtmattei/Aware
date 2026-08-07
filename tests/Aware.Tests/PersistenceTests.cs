using System.Numerics;
using System.Text.Json;
using Aware.Domain;
using Aware.Infrastructure;

namespace Aware.Tests;

/// <summary>
/// The stored model is the user's own record of their home (07-DATA-PRIVACY), so
/// what it round-trips matters as much as what it renders. These go through the
/// source-generated context the app uses, not reflection, since that is the half
/// that breaks under WebAssembly trimming.
/// </summary>
public class PersistenceTests
{
    private static readonly RoomFingerprint AFingerprint = new(
        WifiFeatureHash: "wifi-test-0001",
        BluetoothFeatureHash: "bt-test-0001",
        AmbientLightVector: new Vector3(120f, 0f, 0f),
        MagneticVector: new Vector3(.12f, -.44f, .61f),
        PressureHpa: 1008f,
        AcousticEmbeddingId: null);

    private static SpatialRoom RoundTrip(SpatialRoom room)
    {
        var json = JsonSerializer.Serialize(room, SpatialJsonContext.Default.SpatialRoom);
        return JsonSerializer.Deserialize(json, SpatialJsonContext.Default.SpatialRoom)!;
    }

    [Fact]
    public void AnUnlinkedRoomSurvivesARoundTrip()
    {
        var room = SampleGarageFactory.Create();
        Assert.Null(room.Fingerprint);

        var restored = RoundTrip(room);

        Assert.Null(restored.Fingerprint);
        Assert.False(restored.IsLinkedToPlace);
        Assert.Equal(room.Objects.Count, restored.Objects.Count);
    }

    [Fact]
    public void ALinkedRoomKeepsEverySignalItStored()
    {
        var restored = RoundTrip(SampleGarageFactory.Create().LinkedTo(AFingerprint));

        Assert.Equal(AFingerprint, restored.Fingerprint);
    }

    /// <summary>
    /// Rooms saved before the fingerprint became optional carry the old shape.
    /// They must load as what they are — already-linked rooms — with no migration.
    /// </summary>
    [Fact]
    public void ARoomSavedBeforeFingerprintsWereOptionalStillLoads()
    {
        var json = JsonSerializer.Serialize(
            SampleGarageFactory.Create().LinkedTo(AFingerprint),
            SpatialJsonContext.Default.SpatialRoom);

        Assert.Contains("fingerprint", json);

        var restored = JsonSerializer.Deserialize(json, SpatialJsonContext.Default.SpatialRoom)!;

        Assert.True(restored.IsLinkedToPlace);
        Assert.Equal(AFingerprint.WifiFeatureHash, restored.Fingerprint!.WifiFeatureHash);
    }

    /// <summary>
    /// An unlinked room writes no fingerprint at all rather than a null or a
    /// zeroed one, so a user reading the export sees an absent fact, not a
    /// measurement of nothing.
    /// </summary>
    [Fact]
    public void AnUnlinkedRoomWritesNoFingerprintKey()
    {
        var json = JsonSerializer.Serialize(
            SampleGarageFactory.Create(), SpatialJsonContext.Default.SpatialRoom);

        Assert.DoesNotContain("fingerprint", json);
    }

    /// <summary>Derived values stay out of storage; they are recomputed on load.</summary>
    [Fact]
    public void DerivedPropertiesAreNotWritten()
    {
        var json = JsonSerializer.Serialize(
            SampleGarageFactory.Create(), SpatialJsonContext.Default.SpatialRoom);

        Assert.DoesNotContain("isLinkedToPlace", json);
        Assert.DoesNotContain("centroid", json);
    }
}
