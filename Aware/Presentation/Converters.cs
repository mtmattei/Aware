using Aware.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Aware.Presentation;

/// <summary>True when the bound lens matches the parameter. Drives lens selector state.</summary>
public sealed class LensActiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is SpatialLens lens &&
        parameter is string name &&
        Enum.TryParse<SpatialLens>(name, ignoreCase: true, out var expected) &&
        lens == expected;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Lens selector chrome. WinUI has no data triggers, so active/inactive brushes
/// resolve through the converter rather than a style trigger.
/// </summary>
public sealed class LensBrushConverter : IValueConverter
{
    public string ActiveKey { get; set; } = "AwareSelectionBrush";
    public string InactiveKey { get; set; } = "AwareSurfaceBrush";

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var active =
            value is SpatialLens lens &&
            parameter is string name &&
            Enum.TryParse<SpatialLens>(name, ignoreCase: true, out var expected) &&
            lens == expected;

        var key = active ? ActiveKey : InactiveKey;
        return Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var brush) ? brush : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Set the parameter to "invert" to collapse when true.</summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

/// <summary>
/// Visible when the bound enum matches the parameter. Prefix the parameter with
/// "!" to invert.
/// </summary>
public sealed class EnumMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is null || parameter is not string name) return Visibility.Collapsed;

        var invert = name.StartsWith('!');
        if (invert) name = name[1..];

        var matches = string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);
        return matches != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            null => Visibility.Collapsed,
            string s => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible,
            System.Collections.ICollection c => c.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Observed, inferred and corrected facts must be distinguishable without
/// relying on colour (08-ACCESSIBILITY-TESTS), so evidence resolves to a shape:
/// a filled dot, a hollow dot, and a filled square.
///
/// <para>These were geometric glyphs (U+25CF / U+25CB) until WebAssembly showed
/// them as tofu — the Roboto that Material ships is latin-subset and has no
/// geometric shapes. Drawing the marker instead of typesetting it removes the
/// dependency on any font's coverage.</para>
/// </summary>
public sealed class EvidenceFillConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var filled = value is EvidenceKind.Observed or EvidenceKind.Corrected;
        var key = filled ? "AwareBlueprintBrush" : "AwareCanvasNearBrush";
        return Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var brush) ? brush : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Round for observed and inferred, square for a correction the user made.</summary>
public sealed class EvidenceCornerRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is EvidenceKind.Corrected ? new CornerRadius(1) : new CornerRadius(5);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class EvidenceNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is EvidenceKind kind
            ? kind switch
            {
                EvidenceKind.Observed => "Observed",
                EvidenceKind.Corrected => "Corrected",
                _ => "Inferred",
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is float f ? $"{f * 100:0}%" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
