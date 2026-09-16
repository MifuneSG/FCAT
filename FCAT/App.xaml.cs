using System.Net;
using System.Reflection;
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
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        Log.Start(version);

        // A crash otherwise leaves nothing behind: the window disappears and the user has a story
        // rather than a stack. These two cover both threads an FCAT crash can come off.
        DispatcherUnhandledException += (_, args) =>
            Log.Error("crash", "Unhandled exception on the UI thread", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("crash", "Unhandled exception", args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("crash", "Unobserved task exception", args.Exception);
            args.SetObserved();   // already logged; do not take the process down for it
        };

        // Negotiate compression through the handler. Asking for gzip in a request header without
        // this leaves the response body compressed and the JSON unparseable, which is silent when
        // the caller treats a parse failure as "no data" - that is exactly how the kill feed and
        // the battle report came to show nothing at all.
        var httpHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var httpClient = new HttpClient(httpHandler)
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

        // The live kill feed follows zKillboard's R2Z2 sequence. Start it with the app rather than
        // with the Intel page: it only ever knows about kills that land while it is watching, so
        // the FC's recent-kills window is only populated if it has been running.
        var killStream = new KillStreamService(httpClient);

        // The FC's own Alliance Auth, when they have one. Unconfigured this does nothing at all and
        // costs nothing - FCAT is a complete tool without it and most of its users never set it up.
        var aaConnector = new AaConnectorService(httpClient, settingsService);

        // Item attributes for the fits that come back with those doctrines. Fills itself from ESI
        // the first time a doctrine is opened and then lives on disk - type data only moves when CCP
        // patches. Nothing fetches through it unless an FC has connected an auth.
        var typeCache = new EveTypeCache(httpClient);

        // Real fit statistics, via EVEShipFit's dogma engine in a native DLL beside the exe. Loads
        // itself the first time something asks for a number, so an FC with no auth never pays for it.
        var dogma = new DogmaService();
        var battleReport = new BattleReportService(zkillService, esiService);
        var updater = new UpdaterService();
        var altTracker = new AltTracker(esiService, authService, alertHub);

        var shell = new ShellViewModel(authService, esiService, combatLogService, settingsService, alertHub,
                                       sessionLog, systemSearch, zkillService, killStream, battleReport,
                                       updater, altTracker, aaConnector, typeCache, dogma);
        killStream.Start();
        aaConnector.Start();

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
        Log.Info("app", "Window shown");
    }

    /// <summary>The watcher raises on FileSystemWatcher threads; everything downstream is UI state.</summary>
    private static void Dispatch(Action action) => Current?.Dispatcher.BeginInvoke(action);
}
