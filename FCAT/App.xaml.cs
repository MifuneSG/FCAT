using System.Net.Http;
using System.Windows;
using FCAT.Models;
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
        var altTracker = new AltTracker(esiService, authService, alertHub);

        var shell = new ShellViewModel(authService, esiService, combatLogService, settingsService, alertHub,
                                       sessionLog, systemSearch, zkillService, battleReport, updater, altTracker);

        // The gamelog watcher runs for the whole session, not just while a fleet page is open. Every
        // client writes its own log, so this is also how an alt's tackle or decloak is noticed at all -
        // and none of that should depend on the FC happening to be looking at the fleet view.
        CombatLogService.CloakDroppedPattern = settingsService.Current.CloakDroppedLogPattern;
        combatLogService.CloakDropped += a => Dispatch(() => altTracker.ReportCloakDropped(a));
        combatLogService.AlertRaised  += a => Dispatch(() => alertHub.Raise(a));
        combatLogService.LineParsed   += l => Dispatch(() => shell.CustomAlerts.OnGameLogLine(l));
        combatLogService.StartWatching(settingsService.Current.GamelogsPath);

        var window = new MainWindow(alertHub) { DataContext = shell };
        window.Show();
    }

    /// <summary>The watcher raises on FileSystemWatcher threads; everything downstream is UI state.</summary>
    private static void Dispatch(Action action) => Current?.Dispatcher.BeginInvoke(action);
}
