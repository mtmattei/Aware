namespace Aware.Application;

/// <summary>
/// The words recognition puts on screen, in one place because two callers need
/// them: the recognition service as it settles, and the view model after the
/// user links a room to a place (which must not replay the whole assembly
/// animation just to restate the result).
///
/// <para><strong>Two confidences, one line.</strong> The percentage always
/// describes the geometry — the "98% confident" of 02-UX-FLOWS. Where the device
/// is standing is a separate fact and lives in the clauses after the separator.
/// Collapsing them made a correct sensor reading ("you are not in this room")
/// look like a collapsed model ("this room is 18% likely to be real").</para>
/// </summary>
public static class RecognitionMessages
{
    /// <summary>Stage three, while the fingerprint verdict is still resolving.</summary>
    public static string Matching(FingerprintComparison? comparison)
    {
        if (comparison is null) return "Known objects matching";

        return comparison.Agreements.Count switch
        {
            0 => "Known objects matching · no signal agrees yet",
            1 => $"Known objects matching · {comparison.Agreements[0].ToLowerInvariant()} agrees",
            _ => $"Known objects matching · {comparison.Agreements.Count} signals agree",
        };
    }

    /// <summary>
    /// The settled line. Shape is "{model confidence} · {where} · {evidence}".
    /// </summary>
    /// <param name="confidence">Confidence in the geometry, never in the location.</param>
    /// <param name="comparison">Null when no comparison was possible.</param>
    /// <param name="canLink">
    /// True when the device senses a place but this room is not linked to one.
    /// Distinguishes "unlinked" from "no sensors", which are the same null
    /// comparison but very different sentences.
    /// </param>
    public static string Settled(float confidence, FingerprintComparison? comparison, bool canLink)
    {
        var percent = $"{confidence * 100:0}% confident";

        // No sensors, so there is no place question to answer: the brief's line, verbatim.
        if (comparison is null && !canLink) return $"{percent} · stable room model";

        if (comparison is null) return $"{percent} · not linked to a place yet";

        return comparison.IsRecognized
            ? $"{percent} · you are here · {Describe(comparison.Agreements)}"
            : $"{percent} · you are somewhere else · {Explain(comparison.Contradictions)}";
    }

    private static string Describe(IReadOnlyList<string> agreements) =>
        agreements.Count switch
        {
            0 => "no matching signals",
            1 => $"{agreements[0].ToLowerInvariant()} matches",
            _ => $"{agreements.Count} signals match",
        };

    private static string Explain(IReadOnlyList<string> contradictions) =>
        contradictions.Count switch
        {
            0 => "no signal agrees",
            1 => $"{contradictions[0].ToLowerInvariant()} disagrees",
            _ => $"{contradictions.Count} signals disagree",
        };
}
