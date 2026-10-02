using Aware.Application;
using Aware.Domain;

namespace Aware.Infrastructure;

/// <summary>
/// Whether "Link to this place" may be offered for a room, decided in one place
/// because three callers were each deciding it themselves: the recognition
/// service as it settles, and the view model on load and on room switch.
///
/// <para><strong>The sample garage is never linkable.</strong> It is a seeded
/// model of a room that does not exist. Linking it to the kitchen the phone is
/// standing in would make the app say "you are here" over a drawing of a garage:
/// recognition honest, geometry fiction. SPEC called that out as the incoherence
/// recognition-only rooms were built to end, and left the sample's Link button
/// as an open question. The answer taken: the button exists to tie a room to a
/// place, and the sample is not a place.</para>
/// </summary>
public static class PlaceLinking
{
    public static bool CanLink(SpatialRoom? room, FingerprintReading? reading) =>
        room is { Fingerprint: null }
        && reading is { HasAnySignal: true }
        && !SampleGarageFactory.IsSample(room.Id);
}
