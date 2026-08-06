using Aware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aware.Presentation;

/// <summary>
/// One row of the accessible object list that mirrors the 3D view
/// (08-ACCESSIBILITY-TESTS). It carries the same identity as the geometry, so
/// selecting here and selecting in the viewport are the same act.
/// </summary>
public partial class SpatialObjectListItem : ObservableObject
{
    public SpatialObjectListItem(SpatialObject source)
    {
        Id = source.Id;
        DisplayName = source.DisplayName;
        SemanticClass = source.SemanticClass;
        Confidence = source.Confidence;
        IsExcluded = source.IsExcludedFromSuggestions;

        var evidence = source.ClassificationEvidence switch
        {
            EvidenceKind.Observed => "observed",
            EvidenceKind.Corrected => "corrected by you",
            _ => "inferred",
        };

        Detail = (source.Bounds.IsComplete
            ? $"{source.Bounds.WidthCm:0} × {source.Bounds.DepthCm:0} × {source.Bounds.HeightCm:0} cm · {evidence}"
            : $"Partly measured · {evidence}")
            + (IsExcluded ? " · excluded" : string.Empty);

        // Screen readers get the full sentence; the visual row stays terse.
        AutomationName =
            $"{DisplayName}, {Confidence * 100:0} percent confident, {evidence}" +
            (IsExcluded ? ", excluded from suggestions" : string.Empty);
    }

    public SpatialObjectId Id { get; }
    public string DisplayName { get; }
    public string SemanticClass { get; }
    public float Confidence { get; }
    public string Detail { get; }
    public string AutomationName { get; }
    public bool IsExcluded { get; }

    public string ConfidenceLabel => $"{Confidence * 100:0}%";

    [ObservableProperty]
    private bool isSelected;
}
