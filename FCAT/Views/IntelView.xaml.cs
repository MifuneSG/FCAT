using System.Windows;
using System.Windows.Controls;
using FCAT.ViewModels;

namespace FCAT.Views;

public partial class IntelView : UserControl
{
    public IntelView() => InitializeComponent();

    // Drive the live current-system refresh only while this view is on screen.
    private void OnLoaded(object sender, RoutedEventArgs e)
        => (DataContext as IntelViewModel)?.System.StartAuto();

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => (DataContext as IntelViewModel)?.System.StopAuto();
}
