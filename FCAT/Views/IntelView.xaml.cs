using System.Windows;
using System.Windows.Controls;
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

    // The constellation map fills its panel; tell the VM the real size so it re-projects the
    // nodes to fit (prevents the pile-up seen in large constellations).
    private void OnMapSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is IntelViewModel vm)
            vm.System.SetCanvasSize(e.NewSize.Width, e.NewSize.Height);
    }
}
