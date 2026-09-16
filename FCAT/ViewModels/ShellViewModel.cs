using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>One entry in the demo fleet picker: the sandbox roster, and what to call it.</summary>
public record DemoFleetChoice(DemoData.Preset Value, string Label);

public partial class ShellViewModel : ObservableObject
{
    private readonly EsiAuthService _auth;
    private readonly EsiService _esi;
    private readonly CombatLogService _combatLog;
    private readonly SettingsService _settings;
    private readonly AlertHub _alertHub;
    private readonly SessionLog _sessionLog;
    private readonly SystemSearchService _systemSearch;
    private readonly ZkillService _zkill;
    private readonly KillStreamService _killStream;
    private readonly BattleReportService _battleReport;
    private readonly UpdaterService _updater;
    private readonly AltTracker _altTracker;
    private readonly AaConnectorService _aa;
    private readonly EveTypeCache _types;
    private readonly DogmaService _dogma;

    /// <summary>The one shared alt poll, exposed so pages read it instead of polling their own.</summary>
    public AltTracker AltTracker => _altTracker;

    /// <summary>The Alliance Auth connector. Always present, usually switched off - pages ask it
    /// for doctrines and structures and get empty lists when the FC has no auth.</summary>
    public AaConnectorService Aa => _aa;

    /// <summary>EVE item attributes, cached to disk. Only ever filled for doctrines an FC pulled
    /// from their own auth, so it stays empty for everyone else.</summary>
    public EveTypeCache Types => _types;

    /// <summary>Real fit statistics from the dogma engine. Switches itself off when the native
    /// bridge or sde.dat is absent, so callers must check IsAvailable before showing a number.</summary>
    public DogmaService Dogma => _dogma;

    /// <summary>Reads a doctrine fit into named modules, weapons and drones.</summary>
    private FitAnalyzer? _fits;
    public FitAnalyzer Fits => _fits ??= new FitAnalyzer(_types);

    public ShellViewModel(EsiAuthService auth, EsiService esi, CombatLogService combatLog,
                          SettingsService settings, AlertHub alertHub, SessionLog sessionLog,
                          SystemSearchService systemSearch, ZkillService zkill,
                          KillStreamService killStream,
                          BattleReportService battleReport, UpdaterService updater,
                          AltTracker altTracker, AaConnectorService aa, EveTypeCache types,
                          DogmaService dogma)
    {
        _aa = aa;
        _types = types;
        _dogma = dogma;
        _auth = auth;
        _esi = esi;
        _combatLog = combatLog;
        _settings = settings;
        _alertHub = alertHub;
        _sessionLog = sessionLog;
        _systemSearch = systemSearch;
        _zkill = zkill;
        _killStream = killStream;
        _battleReport = battleReport;
        _updater = updater;
        _altTracker = altTracker;
        _auth.ActiveCharacterChanged += OnActiveCharacterChanged;
        CurrentPage = new LoginViewModel(_auth, this);

        // Restore the map overlay's on/off + lock state. Going through the property (not the field)
        // starts the location poll, so a restored overlay fills in by itself once the saved session
        // comes back - polling before that just retries harmlessly.
        _mapOverlayLocked = settings.Current.MapOverlayLocked;
        MapOverlayEnabled = settings.Current.MapOverlayEnabled;

        // Restore the last active character from disk (no SSO needed if the refresh token is valid).
        _ = TryRestoreSessionAsync();

        // Quietly check GitHub for a newer release on launch (no-op when run from source).
        _ = CheckForUpdatesAsync(silent: true);

        // Tranquility online-count in the top bar (like the launcher). Public status, no auth.
        _serverStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _serverStatusTimer.Tick += (_, _) => _ = RefreshServerStatusAsync();
        _serverStatusTimer.Start();
        _ = RefreshServerStatusAsync();
    }

    // Tranquility player count
    private readonly DispatcherTimer _serverStatusTimer;
    [ObservableProperty] private string _serverPlayers = string.Empty;

    private async Task RefreshServerStatusAsync()
    {
        var status = await _esi.GetServerStatusAsync();
        ServerPlayers = status is { Players: > 0 } ? $"{status.Players:N0}" : string.Empty;
    }

    private async Task TryRestoreSessionAsync()
    {
        if (await _auth.RestoreSessionAsync())
            Application.Current.Dispatcher.Invoke(ShowMenu);
    }

    // Auto-update
    [ObservableProperty] private bool   _updateReady;     // a new version is downloaded and ready
    [ObservableProperty] private string _updateVersion = string.Empty;
    [ObservableProperty] private string _updateStatus = string.Empty;   // shown on the Settings button

    /// <summary>Checks for + downloads an update. Surfaces a restart pill when one is staged.</summary>
    public async Task CheckForUpdatesAsync(bool silent)
    {
        if (!_updater.IsInstalled)
        {
            if (!silent) UpdateStatus = "Updates apply to the installed app only.";
            return;
        }
        try
        {
            if (!silent) UpdateStatus = "Checking…";
            var version = await _updater.CheckAndDownloadAsync();
            if (version != null)
            {
                UpdateVersion = version;
                UpdateReady = true;
                UpdateStatus = $"Update {version} ready. Restart to apply.";
            }
            else if (!silent)
            {
                UpdateStatus = "You're on the latest version.";
            }
        }
        catch
        {
            if (!silent) UpdateStatus = "Update check failed. Try again later.";
        }
    }

    [RelayCommand] private async Task CheckForUpdates() => await CheckForUpdatesAsync(silent: false);

    [RelayCommand] private void ApplyUpdate() => _updater.ApplyAndRestart();

    // Demo / Sandbox mode
    // Runs the app against a synthetic fleet so the FC can exercise Fleet Ops, the dashboard
    // fleet card + readiness, and the alert flow without a live fleet. Intel stays real.
    [ObservableProperty] private bool _demoMode;

    [RelayCommand] private void ToggleDemo() => DemoMode = !DemoMode;

    /// <summary>What the sandbox fleet can be. Each one exercises a different branch of the fleet
    /// classifier, so the advisories can be tried without waiting for that kind of fleet to form.</summary>
    public DemoFleetChoice[] DemoPresets { get; } =
    [
        new(DemoData.Preset.Combat,  "Subcap fleet"),
        new(DemoData.Preset.Whaling, "Whaling gang"),
        new(DemoData.Preset.Mining,  "Mining op"),
    ];

    private DemoFleetChoice? _demoPreset;
    public DemoFleetChoice DemoPreset
    {
        get => _demoPreset ??= DemoPresets[0];
        set
        {
            if (value == null || value == _demoPreset) return;
            _demoPreset = value;
            DemoData.ActivePreset = value.Value;
            OnPropertyChanged(nameof(DemoPreset));

            // Swapping the roster mid-demo means the running session is watching a fleet that no
            // longer exists, so it gets torn down and re-detected.
            if (DemoMode) { EndSession(); ShowMenu(); }
        }
    }

    partial void OnDemoModeChanged(bool value)
    {
        _esi.DemoMode = value;
        if (value)
        {
            _ = FireDemoAlertsAsync();   // a scripted burst so ALERTS / overlay / AAR populate
            ShowMenu();                  // dashboard re-detects the simulated fleet
        }
        else
        {
            EndSession();                // tear down the fake monitoring session
            ShowMenu();
        }
    }

    private async Task FireDemoAlertsAsync()
    {
        (AlertType type, string attacker, string detail)[] script =
        {
            (AlertType.Tackled,    "Vng. Hostile", ""),
            (AlertType.BoostLost,  "",             "Damnation down, gang links dropped"),
            (AlertType.LogiChain,  "",             "Guardian ring lost a link, re-anchor"),
            (AlertType.DpsLoss,    "",             "~50% of DPS lost, 8 of 16 ships down"),
            (AlertType.CapTrouble, "",             "Large Micro Jump Drive"),
        };
        _sessionLog.MarkCombat(DemoData.StagingSystemId, DemoData.StagingName);   // give the demo AAR a battle report
        foreach (var (type, attacker, detail) in script)
        {
            await Task.Delay(1400);
            if (!DemoMode) return;       // user turned it off mid-burst
            Application.Current.Dispatcher.Invoke(() => _alertHub.Raise(new FcAlert
            {
                Timestamp = DateTime.Now, AlertType = type, AttackerName = attacker, Detail = detail
            }));
        }
    }

    [ObservableProperty]
    private ObservableObject _currentPage = null!;

    // Persistent shell chrome (nav rail + top bar)
    // The nav shell is hidden on the login page and shown once a character is authenticated.
    [ObservableProperty] private bool _isLoggedIn;

    // Which nav-rail item is active, for highlighting. "main"/"fleet"/"intel"/"ping"/"alerts"/"setup".
    [ObservableProperty] private string _activeNav = "main";

    [ObservableProperty] private string _shellCharacterName = string.Empty;
    [ObservableProperty] private string _shellPortraitUrl = string.Empty;

    /// <summary>Two-letter avatar fallback, e.g. "Mara Voidwalker" -> "MV".</summary>
    public string CharacterInitials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ShellCharacterName)) return "··";
            var parts = ShellCharacterName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                ? $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant()
                : parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        }
    }

    partial void OnShellCharacterNameChanged(string value) => OnPropertyChanged(nameof(CharacterInitials));

    /// <summary>The alert hub - nav-rail badge binds to its live alert count.</summary>
    public AlertHub Hub => _alertHub;

    private void PopulateShellIdentity()
    {
        IsLoggedIn = true;
        ShellCharacterName = _auth.AuthenticatedCharacterName;
        ShellPortraitUrl = $"https://images.evetech.net/characters/{_auth.AuthenticatedCharacterId}/portrait?size=64";

        // The log watcher needs to know who we're flying to tell "your" alerts from an alt's.
        _combatLog.ActiveCharacterName = _auth.AuthenticatedCharacterName;
        _altTracker.Start();
    }

    // Nav-rail commands
    [RelayCommand] private void NavMain()  => ShowMenu();
    [RelayCommand] private void NavIntel() => ShowIntel();
    [RelayCommand] private void NavPing()  => ShowPing();
    [RelayCommand] private void NavSetup() => ShowSettings();
    [RelayCommand] private void NavAccount() => ShowAccount();

    /// <summary>The account manager - add/switch characters + the alt status board.</summary>
    public void ShowAccount()
    {
        ActiveNav = "account";
        CurrentPage = new AccountViewModel(_auth, _esi, this, _altTracker);
    }

    // Keep the nav-rail avatar + identity in sync when the active character changes (or is removed).
    private void OnActiveCharacterChanged()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_auth.AuthenticatedCharacterId == 0)
            {
                IsLoggedIn = false;
                _altTracker.Stop();   // nothing left to poll, and no token to poll it with
                CurrentPage = new LoginViewModel(_auth, this);
            }
            else if (IsLoggedIn)
            {
                PopulateShellIdentity();   // refresh avatar/name for the new active character
                OnAltRolesChanged();
            }
        });
    }

    /// <summary>
    /// A character's role changed, so any page showing per-alt rows is now stale. The alerts page
    /// builds its rows once on construction, and it outlives navigation, so it has to be told.
    /// </summary>
    public void OnAltRolesChanged()
    {
        if (CurrentPage is AlertsViewModel alerts) alerts.ReloadRows();
    }

    /// <summary>
    /// Re-point the gamelog watcher after the logs path is changed in setup. The watcher starts at
    /// app launch against the saved path, so without this a corrected path does nothing until restart.
    /// </summary>
    public void RestartLogWatcher() => _combatLog.StartWatching(_settings.Current.GamelogsPath);

    /// <summary>Latest fleet id the dashboard detected - lets the nav rail enter ops directly.</summary>
    [ObservableProperty] private long _detectedFleetId;

    /// <summary>True when there's something to enter: a live session or a detected fleet.</summary>
    public bool CanEnterFleet => _session != null || DetectedFleetId != 0;
    partial void OnDetectedFleetIdChanged(long value)
    {
        OnPropertyChanged(nameof(CanEnterFleet));
        NavFleetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEnterFleet))]
    private void NavFleet()
    {
        // Jump into the live session if one is running; otherwise enter the detected fleet.
        // If neither exists there's nothing to show, so stay put (rail item is disabled anyway).
        if (_session is { } s) { ActiveNav = "fleet"; CurrentPage = s; }
        else if (DetectedFleetId != 0) ShowFleet(DetectedFleetId);
    }

    [RelayCommand]
    private void NavAlerts() => ShowAlerts();

    /// <summary>The session alert feed - a standalone page over the app-lifetime AlertHub.</summary>
    public void ShowAlerts()
    {
        ActiveNav = "alerts";
        _alertHub.MarkRead();   // opening the page clears the unread badge
        CurrentPage = new AlertsViewModel(_alertHub, _settings, _altTracker);
    }

    // The live fleet-monitoring session. Kept alive across navigation so the alert overlay and
    // combat-log/boost/cap-chain watching keep running while the FC browses Intel, Settings, etc.
    private FleetViewModel? _session;

    /// <summary>The monitoring session, when one is running. Hunt reads it to see who has arrived.</summary>
    public FleetViewModel? ActiveSession => _session;

    public void ShowMenu()
    {
        PopulateShellIdentity();
        ActiveNav = "main";
        CurrentPage = new MenuViewModel(_auth, _esi, this);
    }

    public void ShowSettings()
    {
        ActiveNav = "setup";
        CurrentPage = new SettingsViewModel(_settings, _alertHub, _systemSearch, this, _auth, _aa);
    }

    public void ShowSessionLog()
    {
        CurrentPage = new SessionLogViewModel(_sessionLog, _battleReport, this);
    }

    public void ShowPing()
    {
        ActiveNav = "ping";
        CurrentPage = new PingViewModel(_settings, _systemSearch, _esi, _auth, this);
    }

    // Intel tools - single combined window; reused so ESI lookups stay cached across visits.
    private IntelViewModel? _intel;
    public void ShowIntel()
    {
        ActiveNav = "intel";
        _intel ??= new IntelViewModel(_esi, _auth, _zkill, _killStream, _systemSearch, _settings, this,
                                      SystemIntel, _alertHub, CustomAlerts, JumpDrives, _altTracker);
        CurrentPage = _intel;
    }

    /// <summary>Jump ranges per hull class, shared so the ESI lookup behind them happens once.</summary>
    private JumpDrives? _jumpDrives;
    public JumpDrives JumpDrives => _jumpDrives ??= new JumpDrives(_esi);

    /// <summary>The FC's own alert rules, evaluated against the intel channel and the gamelog.</summary>
    private CustomAlertService? _customAlerts;
    public CustomAlertService CustomAlerts => _customAlerts ??= new CustomAlertService(_settings, _alertHub);

    // The live current-system/constellation tracker: the Intel page's map renders it, the intel feed
    // listens to it for the region you're in, and the map overlay window binds to it too.
    private SystemIntelViewModel? _systemIntel;
    public SystemIntelViewModel SystemIntel => _systemIntel ??= new SystemIntelViewModel(_esi, _auth, _systemSearch, _altTracker);

    // Map overlay - the constellation map over the game, same idea as the alert overlay.
    // MainWindow owns the window itself and watches these.
    [ObservableProperty] private bool _mapOverlayEnabled;
    [ObservableProperty] private bool _mapOverlayLocked;

    [RelayCommand] private void ToggleMapOverlay()     => MapOverlayEnabled = !MapOverlayEnabled;
    [RelayCommand] private void ToggleMapOverlayLock() => MapOverlayLocked  = !MapOverlayLocked;

    partial void OnMapOverlayEnabledChanged(bool value)
    {
        _settings.Current.MapOverlayEnabled = value;
        _settings.Save();
        // The overlay needs the tracker polling even when the Intel page isn't open.
        if (value) SystemIntel.StartAuto();
        else       SystemIntel.StopAuto();
    }

    partial void OnMapOverlayLockedChanged(bool value)
    {
        _settings.Current.MapOverlayLocked = value;
        _settings.Save();
    }

    public double MapOverlayLeft   => _settings.Current.MapOverlayLeft;
    public double MapOverlayTop    => _settings.Current.MapOverlayTop;
    public double MapOverlayWidth  => _settings.Current.MapOverlayWidth;
    public double MapOverlayHeight => _settings.Current.MapOverlayHeight;

    /// <summary>Called by the overlay host when the window is moved or resized.</summary>
    public void PersistMapOverlay(double left, double top, double width, double height)
    {
        _settings.Current.MapOverlayLeft   = left;
        _settings.Current.MapOverlayTop    = top;
        _settings.Current.MapOverlayWidth  = width;
        _settings.Current.MapOverlayHeight = height;
        _settings.Save();
    }

    public void ShowFleet(long fleetId)
    {
        ActiveNav = "fleet";
        // Reuse the running session for the same fleet; otherwise end the old one and start fresh.
        if (_session is { } s && s.SessionFleetId == fleetId)
        {
            CurrentPage = _session;
            return;
        }
        _session?.Shutdown();
        _session = new FleetViewModel(_auth, _esi, _combatLog, _settings, _alertHub, _sessionLog, this, fleetId);
        CurrentPage = _session;
        OnPropertyChanged(nameof(CanEnterFleet));
        NavFleetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Leaves the fleet view but KEEPS the session monitoring in the background.</summary>
    public void BackToMenu() => ShowMenu();

    /// <summary>Fully ends the monitoring session (overlay alerts stop updating).</summary>
    public void EndSession()
    {
        _session?.Shutdown();
        _session = null;
        OnPropertyChanged(nameof(CanEnterFleet));
        NavFleetCommand.NotifyCanExecuteChanged();
    }
}
