using System;
using Velopack;

namespace FCAT;

/// <summary>
/// Custom entry point so Velopack runs FIRST — before any WPF startup. During install,
/// update and uninstall, Velopack relaunches the app with hook arguments; VelopackApp.Run()
/// handles those and exits, so the main window never flashes during those operations.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must be the very first thing the app does.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
