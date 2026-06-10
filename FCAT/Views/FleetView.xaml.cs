using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FCAT.ViewModels;

namespace FCAT.Views;

public partial class FleetView : UserControl
{
    private AlertOverlayWindow? _overlay;
    private FleetViewModel? _vm;

    public FleetView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Teardown();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as FleetViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        switch (e.PropertyName)
        {
            case nameof(FleetViewModel.OverlayEnabled):
                UpdateOverlay();
                break;
            case nameof(FleetViewModel.OverlayLocked):
                _overlay?.SetLocked(_vm.OverlayLocked);
                PersistPosition();
                break;
        }
    }

    private void UpdateOverlay()
    {
        if (_vm == null) return;

        if (_vm.OverlayEnabled)
        {
            if (_overlay == null)
            {
                _overlay = new AlertOverlayWindow
                {
                    DataContext = _vm,
                    Left = _vm.OverlayLeft,
                    Top  = _vm.OverlayTop,
                };
                _overlay.Show();
                _overlay.SetLocked(_vm.OverlayLocked);   // hwnd exists after Show()
            }
        }
        else
        {
            CloseOverlay();
        }
    }

    private void CloseOverlay()
    {
        if (_overlay == null) return;
        PersistPosition();
        _overlay.Close();
        _overlay = null;
    }

    private void PersistPosition()
    {
        if (_overlay != null && _vm != null)
            _vm.PersistOverlay(_overlay.Left, _overlay.Top, _vm.OverlayLocked);
    }

    private void Teardown()
    {
        CloseOverlay();
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
    }
}
