using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FCAT;

/// <summary>
/// The constellation map as an always-on-top window over the game - the same idea as the alert
/// overlay, but resizable, since a map is worth scaling to taste.
/// </summary>
public partial class MapOverlayWindow : Window
{
    public MapOverlayWindow() => InitializeComponent();

    // The layout puts the system you're in at (0,0); pin that to the middle of the window so the
    // overlay behaves like a game minimap - fixed scale, you centred, edges cropped.
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        MapCentre.X = e.NewSize.Width  / 2;
        MapCentre.Y = e.NewSize.Height / 2;
    }

    private void DragHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // Click-through toggle (so a locked overlay doesn't steal clicks from the game)
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;

    public void SetLocked(bool locked)
    {
        DragHandle.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        // A locked overlay can't be resized either - the grip would just eat clicks.
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;   // not yet shown

        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex = locked ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED
                    : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }
}
