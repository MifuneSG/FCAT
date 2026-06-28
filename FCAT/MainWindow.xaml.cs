using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FCAT.Services;

namespace FCAT;

public partial class MainWindow : Window
{
    private readonly AlertHub _hub;
    private AlertOverlayWindow? _overlay;
    private readonly DispatcherTimer _clock;

    public MainWindow(AlertHub hub)
    {
        InitializeComponent();
        _hub = hub;
        _hub.PropertyChanged += OnHubPropertyChanged;
        Closed += (_, _) => CloseOverlay();

        // EVE time = UTC, ticking once a second in the top bar.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => EveClock.Text = DateTime.UtcNow.ToString("HH:mm:ss") + "  EVE";
        EveClock.Text = DateTime.UtcNow.ToString("HH:mm:ss") + "  EVE";
        _clock.Start();

        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "❐" : "▢";

        // Frameless windows maximize over the taskbar by default — hook WM_GETMINMAXINFO
        // to clamp the maximized size to the monitor's work area.
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        };

        // Honour the saved overlay on/off state at launch (PropertyChanged only fires on later toggles).
        Loaded += (_, _) => UpdateOverlay();
    }

    // ── Maximize-to-work-area (respect the taskbar) ──
    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            const int MONITOR_DEFAULTTONEAREST = 0x2;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = mi.rcWork.Top  - mi.rcMonitor.Top;
                    mmi.ptMaxSize.X     = mi.rcWork.Right  - mi.rcWork.Left;
                    mmi.ptMaxSize.Y     = mi.rcWork.Bottom - mi.rcWork.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ── Frameless window controls ──
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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
