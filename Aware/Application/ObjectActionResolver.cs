using Aware.Domain;

namespace Aware.Application;

/// <summary>
/// One object, three lenses. The identity, the actions and the facts all resolve
/// from the same <see cref="SpatialObject"/>; only the framing changes
/// (01-PRODUCT-BRIEF: "lenses, not separate apps").
/// </summary>
public sealed class ObjectActionResolver : IObjectActionResolver
{
    public ObjectActionTray Resolve(SpatialRoom room, SpatialObject selected, SpatialLens lens) =>
        lens switch
        {
            SpatialLens.Measure => Measure(room, selected),
            SpatialLens.Memories => Memories(room, selected),
            _ => Explore(room, selected),
        };

    private static ObjectActionTray Explore(SpatialRoom room, SpatialObject selected)
    {
        var project = room.FindProject(selected.AttachedProjectId);

        var facts = new List<TrayFact>
        {
            new("Recognized as", Humanize(selected.SemanticClass), selected.ClassificationEvidence),
            new("Confidence", $"{selected.Confidence * 100:0}%", EvidenceKind.Observed),
            new("Last seen", Relative(selected.LastObservedAt), EvidenceKind.Observed),
        };

        if (project is not null)
            facts.Add(new TrayFact("Project", project.Name, EvidenceKind.Observed));

        return new ObjectActionTray(
            "Known object",
            selected.DisplayName,
            project is not null
                ? $"Linked to {project.Name}, and to saved measurements."
                : "Recognized as a stable object in this room.",
            facts,
            [
                new ObjectAction(
                    project is not null ? ObjectActionIds.ResumeProject : ObjectActionIds.Open,
                    project is not null ? "Resume project" : "Open",
                    true),
                new ObjectAction(ObjectActionIds.Measure, "Measure", false),
            ]);
    }

    private static ObjectActionTray Measure(SpatialRoom room, SpatialObject selected)
    {
        var b = selected.Bounds;

        // Complete dimensions are already stated in the description; repeating
        // them per axis just makes the tray tall. Axes only get their own row
        // when one of them is missing and has to be called out.
        var facts = new List<TrayFact>();

        if (!b.IsComplete)
        {
            facts.Add(Dimension("Width", b.WidthCm));
            facts.Add(Dimension("Height", b.HeightCm));
            facts.Add(Dimension("Depth", b.DepthCm));
        }

        var opening = FindOpening(room, selected);
        if (opening is not null)
            facts.Add(new TrayFact(
                $"{opening.DisplayName} opening",
                DescribeOpening(opening.OpeningBounds!),
                EvidenceKind.Observed));

        var actions = new List<ObjectAction> { new(ObjectActionIds.MeasureAgain, "Measure again", true) };
        if (opening is not null)
            actions.Add(new ObjectAction(ObjectActionIds.CompareFit, $"Fit through {opening.DisplayName.ToLowerInvariant()}", false));

        return new ObjectActionTray(
            b.IsComplete ? "Known dimensions" : "Partial dimensions",
            selected.DisplayName,
            b.IsComplete
                ? Describe(b)
                : "Some dimensions have never been measured.",
            facts,
            actions);
    }

    private static ObjectActionTray Memories(SpatialRoom room, SpatialObject selected)
    {
        var project = room.FindProject(selected.AttachedProjectId);

        var facts = new List<TrayFact>();

        if (project is not null)
            facts.Add(new TrayFact("Project", $"{project.Name} · {project.Status}", EvidenceKind.Observed));

        foreach (var memory in selected.Memories.OrderByDescending(m => m.At).Take(3))
            facts.Add(new TrayFact(Relative(memory.At), memory.Detail, memory.Evidence));

        foreach (var move in selected.LocationHistory.OrderByDescending(m => m.At).Take(2))
            facts.Add(new TrayFact(Relative(move.At), move.Description, move.Evidence));

        if (facts.Count == 0)
            facts.Add(new TrayFact("History", "Nothing recorded for this object yet.", EvidenceKind.Observed));

        return new ObjectActionTray(
            "Attached memory",
            selected.DisplayName,
            project is not null
                ? $"{project.Name} is unfinished and attached to this object."
                : "No project is attached yet.",
            facts,
            [
                new ObjectAction(ObjectActionIds.ViewHistory, "View history", true),
                new ObjectAction(
                    project is not null ? ObjectActionIds.DetachProject : ObjectActionIds.AttachProject,
                    project is not null ? "Detach project" : "Attach project",
                    false),
            ]);
    }

    /// <summary>
    /// Compare-fit source: another object in the room whose saved opening
    /// dimensions are known (02-UX-FLOWS).
    /// </summary>
    internal static SpatialObject? FindOpening(SpatialRoom room, SpatialObject exclude) =>
        room.Objects.FirstOrDefault(o =>
            o.Id != exclude.Id &&
            o.OpeningBounds is { WidthCm: not null, HeightCm: not null });

    internal static string CompareFit(SpatialObject subject, SpatialObject opening)
    {
        var o = opening.OpeningBounds!;
        var b = subject.Bounds;

        if (b.WidthCm is null || b.HeightCm is null || b.DepthCm is null)
            return $"{subject.DisplayName} has an unmeasured dimension, so fit cannot be confirmed.";

        // The subject can be turned, so the smallest two of its three dimensions
        // are what must pass through the opening.
        var sides = new[] { b.WidthCm.Value, b.HeightCm.Value, b.DepthCm.Value };
        Array.Sort(sides);
        var (a, c) = (sides[0], sides[1]);

        var w = o.WidthCm!.Value;
        var h = o.HeightCm!.Value;
        var fits = (a <= w && c <= h) || (a <= h && c <= w);
        var clearance = MathF.Min(MathF.Max(w - a, 0), MathF.Max(h - c, 0));

        return fits
            ? $"Fits through the {opening.DisplayName.ToLowerInvariant()} turned on its side, with {clearance:0} cm to spare."
            : $"Does not fit through the {opening.DisplayName.ToLowerInvariant()} at {a:0} × {c:0} cm.";
    }

    private static TrayFact Dimension(string axis, float? cm) =>
        cm is { } v
            ? new TrayFact(axis, $"{v:0} cm", EvidenceKind.Observed)
            : new TrayFact(axis, "Never measured", EvidenceKind.Inferred, IsKnown: false);

    private static string Describe(SpatialBounds b) =>
        $"{Part(b.WidthCm)} × {Part(b.DepthCm)} × {Part(b.HeightCm)} cm";

    /// <summary>An opening has a width and a height; its depth says nothing useful.</summary>
    private static string DescribeOpening(SpatialBounds b) =>
        $"{Part(b.WidthCm)} × {Part(b.HeightCm)} cm";

    private static string Part(float? cm) => cm is { } v ? $"{v:0}" : "—";

    private static string Humanize(string semanticClass) =>
        string.Join(' ', semanticClass.Split('_')
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    private static string Relative(DateTimeOffset at)
    {
        var days = (DateTimeOffset.Now - at).TotalDays;
        return days switch
        {
            < 1 => "Today",
            < 2 => "Yesterday",
            < 14 => $"{days:0} days ago",
            < 60 => $"{days / 7:0} weeks ago",
            _ => $"{days / 30:0} months ago",
        };
    }
}

public static class ObjectActionIds
{
    public const string Open = "open";
    public const string ResumeProject = "resume-project";
    public const string Measure = "measure";
    public const string MeasureAgain = "measure-again";
    public const string CompareFit = "compare-fit";
    public const string ViewHistory = "view-history";
    public const string AttachProject = "attach-project";
    public const string DetachProject = "detach-project";
}
