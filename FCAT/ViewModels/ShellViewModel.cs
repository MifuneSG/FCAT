using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

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
    private readonly UpdaterService _updater;

    public ShellViewModel(EsiAuthService auth, EsiService esi, CombatLogService combatLog,
                          SettingsService settings, AlertHub alertHub, SessionLog sessionLog,
                          SystemSearchService systemSearch, ZkillService zkill, UpdaterService updater)
    {
        _auth = auth;
        _esi = esi;
        _combatLog = combatLog;
        _settings = settings;
        _alertHub = alertHub;
        _sessionLog = sessionLog;
        _systemSearch = systemSearch;
        _zkill = zkill;
        _updater = updater;
        _auth.ActiveCharacterChanged += OnActiveCharacterChanged;
        CurrentPage = new LoginViewModel(_auth, this);

        // Restore the last active character from disk (no SSO needed if the refresh token is valid).
        _ = TryRestoreSessionAsync();

        // Quietly check GitHub for a newer release on launch (no-op when run from source).
        _ = CheckForUpdatesAsync(silent: true);
    }

    private async Task TryRestoreSessionAsync()
    {
        if (await _auth.RestoreSessionAsync())
            Application.Current.Dispatcher.Invoke(ShowMenu);
    }

    // ── Auto-update ──
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
                UpdateStatus = $"Update {version} ready — restart to apply.";
            }
            else if (!silent)
            {
                UpdateStatus = "You're on the latest version.";
            }
        }
        catch
        {
            if (!silent) UpdateStatus = "Update check failed — try again later.";
        }
    }

    [RelayCommand] private async Task CheckForUpdates() => await CheckForUpdatesAsync(silent: false);

    [RelayCommand] private void ApplyUpdate() => _updater.ApplyAndRestart();

    // ── Demo / Sandbox mode ──
    // Runs the app against a synthetic fleet so the FC can exercise Fleet Ops, the dashboard
    // fleet card + readiness, and the alert flow without a live fleet. Intel stays real.
    [ObservableProperty] private bool _demoMode;

    [RelayCommand] private void ToggleDemo() => DemoMode = !DemoMode;

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
            (AlertType.BoostLost,  "",             "Damnation — gang links dropped"),
            (AlertType.LogiChain,  "",             "Guardian ring lost a link — re-anchor"),
            (AlertType.DpsLoss,    "",             "~50% of DPS lost — 8 of 16 ships down"),
            (AlertType.CapTrouble, "",             "Large Micro Jump Drive"),
        };
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

    // ── Persistent shell chrome (nav rail + top bar) ──
    // The nav shell is hidden on the login page and shown once a character is authenticated.
    [ObservableProperty] private bool _isLoggedIn;

    // Which nav-rail item is active, for highlighting. "main"/"fleet"/"intel"/"ping"/"alerts"/"setup".
    [ObservableProperty] private string _activeNav = "main";

    [ObservableProperty] private string _shellCharacterName = string.Empty;
    [ObservableProperty] private string _shellPortraitUrl = string.Empty;

    /// <summary>Two-letter avatar fallback, e.g. "Mara Voidwalker" → "MV".</summary>
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

    /// <summary>The alert hub — nav-rail badge binds to its live alert count.</summary>
    public AlertHub Hub => _alertHub;

    private void PopulateShellIdentity()
    {
        IsLoggedIn = true;
        ShellCharacterName = _auth.AuthenticatedCharacterName;
        ShellPortraitUrl = $"https://images.evetech.net/characters/{_auth.AuthenticatedCharacterId}/portrait?size=64";
    }

    // ── Nav-rail commands ──
    [RelayCommand] private void NavMain()  => ShowMenu();
    [RelayCommand] private void NavIntel() => ShowIntel();
    [RelayCommand] private void NavPing()  => ShowPing();
    [RelayCommand] private void NavSetup() => ShowSettings();
    [RelayCommand] private void NavAccount() => ShowAccount();

    /// <summary>The account manager — add/switch characters + the alt status board.</summary>
    public void ShowAccount()
    {
        ActiveNav = "account";
        CurrentPage = new AccountViewModel(_auth, _esi, this);
    }

    // Keep the nav-rail avatar + identity in sync when the active character changes (or is removed).
    private void OnActiveCharacterChanged()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_auth.AuthenticatedCharacterId == 0)
            {
                IsLoggedIn = false;
                CurrentPage = new LoginViewModel(_auth, this);
            }
            else if (IsLoggedIn)
            {
                PopulateShellIdentity();   // refresh avatar/name for the new active character
            }
        });
    }

    /// <summary>Latest fleet id the dashboard detected — lets the nav rail enter ops directly.</summary>
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

    /// <summary>The session alert feed — a standalone page over the app-lifetime AlertHub.</summary>
    public void ShowAlerts()
    {
        ActiveNav = "alerts";
        _alertHub.MarkRead();   // opening the page clears the unread badge
        CurrentPage = new AlertsViewModel(_alertHub);
    }

    // The live fleet-monitoring session. Kept alive across navigation so the alert overlay and
    // combat-log/boost/cap-chain watching keep running while the FC browses Intel, Settings, etc.
    private FleetViewModel? _session;

    public void ShowMenu()
    {
        PopulateShellIdentity();
        ActiveNav = "main";
        CurrentPage = new MenuViewModel(_auth, _esi, this);
    }

    public void ShowSettings()
    {
        ActiveNav = "setup";
        CurrentPage = new SettingsViewModel(_settings, _alertHub, _systemSearch, this);
    }

    public void ShowSessionLog()
    {
        CurrentPage = new SessionLogViewModel(_sessionLog, this);
    }

    public void ShowPing()
    {
        ActiveNav = "ping";
        CurrentPage = new PingViewModel(_settings, _systemSearch, _esi, _auth, this);
    }

    // Intel tools — single combined window; reused so ESI lookups stay cached across visits.
    private IntelViewModel? _intel;
    public void ShowIntel()
    {
        ActiveNav = "intel";
        _intel ??= new IntelViewModel(_esi, _auth, _zkill, _systemSearch, _settings, this);
        CurrentPage = _intel;
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
