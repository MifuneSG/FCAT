using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FCAT.ViewModels;

namespace FCAT.Views;

public partial class IntelView : UserControl
{
    public IntelView() => InitializeComponent();

    // Drive the live current-system refresh + intel feed only while this view is on screen.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not IntelViewModel vm) return;
        vm.System.StartAuto();
        vm.Feed.StartAuto();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not IntelViewModel vm) return;
        vm.System.StopAuto();
        vm.Feed.StopAuto();
    }

    // Minimap centring: the layout puts the system you're in at (0,0), so pinning that to the middle
    // of the panel keeps you centred at a fixed scale, with the rest cropped at the edges. No zoom,
    // no pan, and no fitting - fitting is what shrank big constellations until they were unreadable.
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        MapCentre.X = e.NewSize.Width  / 2;
        MapCentre.Y = e.NewSize.Height / 2;
    }

    // Left-click a system to focus the panel on it (explore outward via exits).
    private void OnNodeClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MapNode node } && DataContext is IntelViewModel vm)
            vm.System.FocusSystem(node);
    }
}
