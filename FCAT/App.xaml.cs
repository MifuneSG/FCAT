using System.Net.Http;
using System.Windows;
using FCAT.Services;
using FCAT.ViewModels;

namespace FCAT;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var httpClient = new HttpClient
        {
            // Fail a stalled request fast instead of wedging a poll for the default 100s.
            Timeout = TimeSpan.FromSeconds(15)
        };

        var authService = new EsiAuthService(httpClient)
        {
            ClientId = AppSecrets.ClientId,
            ClientSecret = AppSecrets.ClientSecret
        };

        var esiService = new EsiService(httpClient, authService);
        var combatLogService = new CombatLogService();
        var settingsService = new SettingsService();

        var shell = new ShellViewModel(authService, esiService, combatLogService, settingsService);

        var window = new MainWindow { DataContext = shell };
        window.Show();
    }
}
