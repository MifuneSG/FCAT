using System.Windows;

namespace FCAT;

/// <summary>
/// Hint text shown inside an empty TextBox, the way a chat box says "Type a message" until you
/// start typing.
///
/// WPF has no watermark of its own, so this is an attached property the <c>DarkTextBox</c> template
/// draws behind the content host and hides as soon as the box has any text. It stays visible while
/// the box is focused but still empty, which is when it is most wanted - the user has clicked in and
/// is deciding what to type.
///
/// Use it for a field whose CONTENT needs explaining, not as a replacement for a label. A hint that
/// vanishes the moment someone types cannot carry anything they will need again.
/// </summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Placeholder), new PropertyMetadata(string.Empty));

    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);
    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
}
