using System.Numerics;
using Aware.Application;
using Aware.Domain;
using Aware.Infrastructure;

namespace Aware.Tests;

/// <summary>
/// Which room opens is the feature. Opening the wrong room is worse than opening
/// none, so these lean on the locator refusing rather than guessing.
/// </summary>
public class RoomLocatorTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly RoomLocator Locator = new(new FingerprintMatcher());

    private static RoomFingerprint Place(string tag, float lux, Vector3 magnetic, float hpa) =>
        new(tag, string.Empty, new Vector3(lux, 0f, 0f), magnetic, hpa, null);

    private static readonly RoomFingerprint Kitchen =
        Place("wifi-kitchen", 240f, new Vector3(18.4f, -3.2f, 39.9f), 1011.4f);

    private static readonly RoomFingerprint Study =
        Place("wifi-study", 60f, new Vector3(-24.1f, 9.7f, -31.5f), 1006.2f);

    private static FingerprintReading Reading(RoomFingerprint f) =>
        new(f, [new FingerprintSignal("Wi-Fi neighbourhood", "hashed", true)], When);

    private static SpatialRoom Room(string name, RoomFingerprint f) =>
        UnmodelledRoomFactory.Create(name, f, When);

    [Fact]
    public void ItOpensTheRoomYouAreStandingIn()
    {
        var rooms = new[] { Room("Kitchen", Kitchen), Room("Study", Study) };

        var located = Locator.Locate(rooms, Reading(Study));

        Assert.NotNull(located);
        Assert.Equal("Study", located.Room.Name);
        Assert.True(located.Comparison.IsRecognized);
    }

    [Fact]
    public void AnUnknownPlaceOpensNothing()
    {
        var rooms = new[] { Room("Kitchen", Kitchen), Room("Study", Study) };

        var elsewhere = Place("wifi-nowhere", 5f, new Vector3(2f, 44f, -7f), 990f);

        Assert.Null(Locator.Locate(rooms, Reading(elsewhere)));
    }

    /// <summary>
    /// A room that never claimed a location must not have a verdict invented for
    /// it — the seeded sample ships exactly like this.
    /// </summary>
    [Fact]
    public void UnlinkedRoomsAreNeverLocated()
    {
        var rooms = new[] { SampleGarageFactory.Create() };

        Assert.Null(SampleGarageFactory.Create().Fingerprint);
        Assert.Null(Locator.Locate(rooms, Reading(Kitchen)));
    }

    [Fact]
    public void WithNoSignalsNothingIsLocated()
    {
        var rooms = new[] { Room("Kitchen", Kitchen) };

        Assert.Null(Locator.Locate(rooms, FingerprintReading.Unavailable));
    }

    [Fact]
    public void NoRoomsAtAllIsNotAnError()
    {
        Assert.Null(Locator.Locate([], Reading(Kitchen)));
    }

    /// <summary>
    /// Two rooms recorded in the same place is legitimate — creating one warns but
    /// allows it. The result must not depend on enumeration order.
    /// </summary>
    [Fact]
    public void AnExactTieKeepsTheEarlierRoomEitherWayRound()
    {
        var first = Room("First", Kitchen);
        var second = Room("Second", Kitchen);

        Assert.Equal("First", Locator.Locate([first, second], Reading(Kitchen))!.Room.Name);
        Assert.Equal("Second", Locator.Locate([second, first], Reading(Kitchen))!.Room.Name);
    }

    [Fact]
    public void TheStrongerMatchWins()
    {
        // Same Wi-Fi as the kitchen, but every other signal is the study's, so it
        // clears the bar by less.
        var partial = Place("wifi-kitchen", 60f, new Vector3(-24.1f, 9.7f, -31.5f), 1006.2f);

        var rooms = new[] { Room("Partial", partial), Room("Kitchen", Kitchen) };
        var located = Locator.Locate(rooms, Reading(Kitchen))!;

        Assert.Equal("Kitchen", located.Room.Name);
    }
}
