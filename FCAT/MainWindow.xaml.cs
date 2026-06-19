using System.ComponentModel;
using System.Windows;
using FCAT.Services;

namespace FCAT;

public partial class MainWindow : Window
{
    private readonly AlertHub _hub;
    private AlertOverlayWindow? _overlay;

    public MainWindow(AlertHub hub)
    {
        InitializeComponent();
        _hub = hub;
        _hub.PropertyChanged += OnHubPropertyChanged;
        Closed += (_, _) => CloseOverlay();
    }

    private void OnHubPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AlertHub.OverlayEnabled):
                UpdateOverlay();
                break;
            case nameof(AlertHub.OverlayLocked):
                _overlay?.SetLocked(_hub.OverlayLocked);
                PersistPosition();
                break;
        }
    }

    private void UpdateOverlay()
    {
        if (_hub.OverlayEnabled)
        {
            if (_overlay == null)
            {
                _overlay = new AlertOverlayWindow
                {
                    DataContext = _hub,
                    Left = _hub.OverlayLeft,
                    Top  = _hub.OverlayTop,
                };
                _overlay.Show();
                _overlay.SetLocked(_hub.OverlayLocked);   // hwnd exists after Show()
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
        if (_overlay != null)
            _hub.PersistOverlay(_overlay.Left, _overlay.Top, _hub.OverlayLocked);
    }
}
