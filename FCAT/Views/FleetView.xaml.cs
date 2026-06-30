using System.Windows.Controls;
using System.Windows.Input;
using FCAT.ViewModels;

namespace FCAT.Views;

public partial class FleetView : UserControl
{
    public FleetView()
    {
        InitializeComponent();
        // Esc closes whichever modal overlay is open. Handled in code-behind (not a per-control
        // KeyBinding) because the Move picker has no focusable input, so a binding wouldn't fire.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not FleetViewModel vm) return;

        if (vm.IsMovePickerOpen)
        {
            vm.CancelMoveCommand.Execute(null);
            e.Handled = true;
        }
        else if (vm.IsRenameOpen)
        {
            vm.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }
}
