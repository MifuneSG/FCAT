using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace FCAT;

/// <summary>
/// Letter-spacing for TextBlock. WPF has no tracking property - the only lever the text stack
/// exposes is the width of the glyphs actually in the run, so spacing is produced by interleaving
/// a space between characters and scaling THAT space's font size.
///
/// A space in Segoe UI advances ~0.26em, so a gap of <c>t</c> em needs a space rendered at
/// <c>t / 0.26</c> of the label's size. The value is therefore in em of the host TextBlock and
/// survives a font-size change, which is the point - uppercase needs proportionally more tracking
/// the smaller it gets, and a fixed pixel gap would drift as sizes change.
///
/// Uppercase is the case that needs this: caps have no ascenders or descenders to break up their
/// outline, so at label sizes they read as one bar of letters until they are tracked apart.
///
/// Static labels only. Writing Inlines detaches TextBlock.Text, so a TextBlock whose Text is bound
/// to changing data would lose its tracking on the next update - bind-and-track is not supported,
/// and none of the places this is used need it.
/// </summary>
public static class TextStyle
{
    /// <summary>Space between characters, in em. 0.05-0.15 suits uppercase labels; 0 disables.</summary>
    public static readonly DependencyProperty TrackingProperty =
        DependencyProperty.RegisterAttached(
            "Tracking", typeof(double), typeof(TextStyle),
            new PropertyMetadata(0d, OnTrackingChanged));

    public static void SetTracking(DependencyObject o, double value) => o.SetValue(TrackingProperty, value);
    public static double GetTracking(DependencyObject o) => (double)o.GetValue(TrackingProperty);

    /// <summary>
    /// The untracked text. Writing Inlines clears TextBlock.Text, so the original has to be kept
    /// somewhere to rebuild from - otherwise re-applying would space out the spaces.
    /// </summary>
    private static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.RegisterAttached(
            "SourceText", typeof(string), typeof(TextStyle), new PropertyMetadata(null));

    /// <summary>Advance width of a space relative to font size, for the UI font.</summary>
    private const double SpaceEm = 0.26;

    private static void OnTrackingChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock block) return;

        // Tracking is usually set from a Style, which applies before the text is in place. Waiting
        // for Loaded means the run is rebuilt once there is actually something to rebuild.
        if (!block.IsLoaded)
        {
            block.Loaded -= OnLoaded;
            block.Loaded += OnLoaded;
            return;
        }
        Apply(block);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock block) return;
        block.Loaded -= OnLoaded;
        Apply(block);
    }

    private static void Apply(TextBlock block)
    {
        var source = (string?)block.GetValue(SourceTextProperty) ?? block.Text;
        block.SetValue(SourceTextProperty, source);

        var tracking = GetTracking(block);
        if (tracking <= 0 || string.IsNullOrEmpty(source))
        {
            block.Text = source;
            return;
        }

        // Scaled so the gap lands at `tracking` em whatever size the label ends up at.
        var spaceSize = block.FontSize * tracking / SpaceEm;

        block.Inlines.Clear();
        for (var i = 0; i < source.Length; i++)
        {
            block.Inlines.Add(new Run(source[i].ToString()));
            // No trailing space: it would widen the block past its own text and throw off centring,
            // which is exactly where the nav rail would show it.
            if (i < source.Length - 1)
                block.Inlines.Add(new Run(" ") { FontSize = spaceSize });
        }
    }
}
