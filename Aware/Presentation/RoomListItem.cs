using Aware.Domain;

namespace Aware.Presentation;

/// <summary>
/// One row of the room list. Flattened for the same reason
/// <see cref="SpatialObjectListItem"/> is: the template binds plain values and
/// never reaches back into the domain record from XAML.
/// </summary>
public sealed record RoomListItem(
    RoomId Id,
    string Name,
    string Reason,
    bool IsCurrent)
{
    /// <summary>
    /// Name and reason in one string, so a screen reader gets why the room is
    /// listed without depending on the visual grouping of the two text blocks.
    /// </summary>
    public string AutomationName => IsCurrent
        ? $"{Name}, {Reason}, currently open"
        : $"{Name}, {Reason}";
}

