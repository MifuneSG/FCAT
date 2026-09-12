using System.Windows;
using System.Windows.Controls;

namespace FCAT.Views;

public partial class HintIcon : UserControl
{
    /// <summary>The hint shown on hover. Newlines are respected, so a hint can have paragraphs.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(HintIcon),
            new PropertyMetadata(string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public HintIcon() => InitializeComponent();
}
