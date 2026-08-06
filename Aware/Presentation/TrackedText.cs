using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Aware.Presentation;

/// <summary>
/// A letterspaced label, laid out one character per <see cref="TextBlock"/>.
///
/// <para><see cref="TextBlock.CharacterSpacing"/> is a silent no-op on the Uno
/// Skia text stack: it reports no error and moves the measured ink width by
/// zero. The eyebrow labels in 05-DESIGN-SYSTEM.md are tracked small caps, so
/// they need real spacing. A horizontal <see cref="StackPanel.Spacing"/> gives
/// it exactly and leaves the glyph shapes alone, unlike a horizontal
/// <c>ScaleTransform</c>, which thickens vertical strokes by the same
/// percentage it widens.</para>
///
/// <para>The whole run is announced as one string; the per-character blocks are
/// hidden from the accessibility tree so a screen reader never spells it out.</para>
/// </summary>
public sealed partial class TrackedText : StackPanel
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(TrackedText),
            new PropertyMetadata(string.Empty, OnRebuildRequired));

    public static readonly DependencyProperty TrackingProperty =
        DependencyProperty.Register(
            nameof(Tracking), typeof(double), typeof(TrackedText),
            new PropertyMetadata(1.75d, OnRebuildRequired));

    public static readonly DependencyProperty WordGapProperty =
        DependencyProperty.Register(
            nameof(WordGap), typeof(double), typeof(TrackedText),
            new PropertyMetadata(4d, OnRebuildRequired));

    public static readonly DependencyProperty TextStyleProperty =
        DependencyProperty.Register(
            nameof(TextStyle), typeof(Style), typeof(TrackedText),
            new PropertyMetadata(null, OnRebuildRequired));

    public TrackedText()
    {
        Orientation = Orientation.Horizontal;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Space between characters, in pixels. 1.75 px is 0.14em at the 12.5 px label size.</summary>
    public double Tracking
    {
        get => (double)GetValue(TrackingProperty);
        set => SetValue(TrackingProperty, value);
    }

    /// <summary>Extra width for a space, on top of the tracking either side of it.</summary>
    public double WordGap
    {
        get => (double)GetValue(WordGapProperty);
        set => SetValue(WordGapProperty, value);
    }

    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    private static void OnRebuildRequired(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TrackedText)d).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        Spacing = Tracking;

        var text = Text ?? string.Empty;
        AutomationProperties.SetName(this, text);

        foreach (var character in text)
        {
            // A TextBlock holding only whitespace is trimmed to zero width on
            // the Skia text stack -- a non-breaking space does not survive it
            // either -- so word gaps are carried by an empty fixed-width
            // spacer, which cannot be trimmed away.
            if (character == ' ')
            {
                Children.Add(new Border { Width = WordGap });
                continue;
            }

            var block = new TextBlock { Text = character.ToString() };

            if (TextStyle is not null) block.Style = TextStyle;

            AutomationProperties.SetAccessibilityView(block, AccessibilityView.Raw);
            Children.Add(block);
        }
    }
}
