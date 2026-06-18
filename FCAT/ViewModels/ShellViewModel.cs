using CommunityToolkit.Mvvm.ComponentModel;
using FCAT.Services;

namespace FCAT.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly EsiAuthService _auth;
    private readonly EsiService _esi;
    private readonly CombatLogService _combatLog;
    private readonly SettingsService _settings;

    public ShellViewModel(EsiAuthService auth, EsiService esi, CombatLogService combatLog, SettingsService settings)
    {
        _auth = auth;
        _esi = esi;
        _combatLog = combatLog;
        _settings = settings;
        CurrentPage = new LoginViewModel(_auth, this);
    }

    [ObservableProperty]
    private ObservableObject _currentPage = null!;

    public void ShowMenu()
    {
        CurrentPage = new MenuViewModel(_auth, _esi, this);
    }

    public void ShowSettings()
    {
        CurrentPage = new SettingsViewModel(_settings, this);
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
        CurrentPage = new FleetViewModel(_auth, _esi, _combatLog, _settings, this, fleetId);
    }

    public void BackToMenu()
    {
        if (CurrentPage is FleetViewModel fvm)
            fvm.Shutdown();
        ShowMenu();
    }
}
