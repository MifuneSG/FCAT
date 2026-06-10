using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FCAT;

public partial class AlertOverlayWindow : Window
{
    public AlertOverlayWindow() => InitializeComponent();

    private void DragHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ── Click-through toggle (so a locked overlay doesn't steal clicks from the game) ──
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;

    public void SetLocked(bool locked)
    {
        DragHandle.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;   // not yet shown

        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex = locked ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED
                    : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }
}
