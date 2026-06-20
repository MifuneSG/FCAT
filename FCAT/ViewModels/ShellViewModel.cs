using CommunityToolkit.Mvvm.ComponentModel;
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

    public ShellViewModel(EsiAuthService auth, EsiService esi, CombatLogService combatLog,
                          SettingsService settings, AlertHub alertHub, SessionLog sessionLog,
                          SystemSearchService systemSearch)
    {
        _auth = auth;
        _esi = esi;
        _combatLog = combatLog;
        _settings = settings;
        _alertHub = alertHub;
        _sessionLog = sessionLog;
        _systemSearch = systemSearch;
        CurrentPage = new LoginViewModel(_auth, this);
    }

    [ObservableProperty]
    private ObservableObject _currentPage = null!;

    // The live fleet-monitoring session. Kept alive across navigation so the alert overlay and
    // combat-log/boost/cap-chain watching keep running while the FC browses Intel, Settings, etc.
    private FleetViewModel? _session;

    public void ShowMenu()
    {
        CurrentPage = new MenuViewModel(_auth, _esi, this);
    }

    public void ShowSettings()
    {
        CurrentPage = new SettingsViewModel(_settings, _alertHub, _systemSearch, this);
    }

    public void ShowSessionLog()
    {
        CurrentPage = new SessionLogViewModel(_sessionLog, this);
    }

    public void ShowPing()
    {
        CurrentPage = new PingViewModel(_settings, _systemSearch, _esi, _auth, this);
    }

    // Intel tools — single combined window; reused so ESI lookups stay cached across visits.
    private IntelViewModel? _intel;
    public void ShowIntel()
    {
        _intel ??= new IntelViewModel(_esi, _auth, this);
        CurrentPage = _intel;
    }

    public void ShowFleet(long fleetId)
    {
        // Reuse the running session for the same fleet; otherwise end the old one and start fresh.
        if (_session is { } s && s.SessionFleetId == fleetId)
        {
            CurrentPage = _session;
            return;
        }
        _session?.Shutdown();
        _session = new FleetViewModel(_auth, _esi, _combatLog, _settings, _alertHub, _sessionLog, this, fleetId);
        CurrentPage = _session;
    }

    /// <summary>Leaves the fleet view but KEEPS the session monitoring in the background.</summary>
    public void BackToMenu() => ShowMenu();

    /// <summary>Fully ends the monitoring session (overlay alerts stop updating).</summary>
    public void EndSession()
    {
        _session?.Shutdown();
        _session = null;
    }
}
