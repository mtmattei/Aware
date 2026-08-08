using Aware.Domain;

namespace Aware.Application;

/// <summary>
/// The single support line under a room's name in the room list.
///
/// <para>It answers <em>why this room is in the list</em> rather than restating
/// the name, so the list is scannable without opening anything. Kept beside
/// <see cref="RecognitionMessages"/> and out of the view model for the same
/// reason: wording is the part most likely to be revised, and a pure function of
/// (room, recognized) is the part worth pinning with tests.</para>
/// </summary>
public static class RoomListMessages
{
    /// <param name="isRecognizedHere">
    /// True when the live reading matches this room's stored place. It outranks
    /// everything else the line could say: where the device is standing is the
    /// only fact that changes between one glance at the list and the next.
    /// </param>
    public static string Reason(SpatialRoom room, bool isRecognizedHere)
    {
        if (isRecognizedHere) return "Recognized here now";

        // A recognition-only room has no object count to report, and "0 objects"
        // would read as a room that was mapped and found empty.
        if (!room.IsModelled) return "No model yet";

        var objects = room.Objects.Count == 1 ? "1 object" : $"{room.Objects.Count} objects";

        return room.IsLinkedToPlace ? objects : $"{objects} · not linked to a place";
    }
}
