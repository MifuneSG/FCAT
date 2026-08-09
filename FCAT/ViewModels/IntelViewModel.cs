using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// The Intel Tools window - everything intel on one screen: a Map pane (constellation schematic with
/// zoom/pan), an Overview pane (the same systems as a scannable board), a Scan panel (d-scan / local),
/// and a combined intel Feed (zKill kills + in-game intel channel) across the bottom.
/// </summary>
public partial class IntelViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    /// <summary>Exposed so the map pane can reach the map-overlay toggles.</summary>
    public ShellViewModel Shell => _shell;

    public DScanViewModel        Scan   { get; }
    public SystemIntelViewModel  System { get; }
    public IntelFeedViewModel    Feed   { get; }

    public IntelViewModel(EsiService esi, EsiAuthService auth, ZkillService zkill,
                          SystemSearchService systems, SettingsService settings, ShellViewModel shell,
                          SystemIntelViewModel system, AlertHub alertHub, CustomAlertService customAlerts)
    {
        _shell = shell;
        Scan   = new DScanViewModel(esi, auth);
        System = system;
        Feed   = new IntelFeedViewModel(esi, zkill, systems, settings, alertHub, customAlerts);

        // Point the kill feed at whatever system the FC is in, and tell it which systems to shout about.
        System.SystemChanged += Feed.SetSystem;
        System.LocationContextChanged += Feed.SetWatchedSystems;
    }

    // Pane switcher: Map is primary (the constellation overview rides alongside it), D-scan behind a tab.
    [ObservableProperty] private string _activePane = "Map";
    public bool IsMapPane   => ActivePane == "Map";
    public bool IsDscanPane => ActivePane == "Dscan";

    partial void OnActivePaneChanged(string value)
    {
        OnPropertyChanged(nameof(IsMapPane));
        OnPropertyChanged(nameof(IsDscanPane));
    }

    [RelayCommand] private void SetPane(string pane) => ActivePane = pane;

    [RelayCommand] private void BackToMenu() => _shell.ShowMenu();
}
