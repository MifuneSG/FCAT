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

        var characterStore = new CharacterStore();
        var authService = new EsiAuthService(httpClient, characterStore)
        {
            ClientId = AppSecrets.ClientId,
            ClientSecret = AppSecrets.ClientSecret
        }.InitStore();

        var esiService = new EsiService(httpClient, authService);
        var combatLogService = new CombatLogService();
        var settingsService = new SettingsService();

        // Paint the saved colour theme before any window renders.
        ThemeService.Apply(ThemeService.Parse(settingsService.Current.Theme));
        var sessionLog = new SessionLog();
        var alertHub = new AlertHub(settingsService, sessionLog);
        var systemSearch = new SystemSearchService(esiService);
        var zkillService = new ZkillService(httpClient);
        var battleReport = new BattleReportService(zkillService, esiService);
        var updater = new UpdaterService();

        var shell = new ShellViewModel(authService, esiService, combatLogService, settingsService, alertHub, sessionLog, systemSearch, zkillService, battleReport, updater);

        var window = new MainWindow(alertHub) { DataContext = shell };
        window.Show();
    }
}
