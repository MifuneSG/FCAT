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
    public HuntViewModel         Hunt   { get; }

    public IntelViewModel(EsiService esi, EsiAuthService auth, ZkillService zkill,
                          KillStreamService killStream,
                          SystemSearchService systems, SettingsService settings, ShellViewModel shell,
                          SystemIntelViewModel system, AlertHub alertHub, CustomAlertService customAlerts,
                          JumpDrives drives, AltTracker alts)
    {
        _shell = shell;
        Scan   = new DScanViewModel(esi);
        System = system;
        Feed   = new IntelFeedViewModel(esi, auth, zkill, killStream, systems, settings, alertHub, customAlerts);
        Hunt   = new HuntViewModel(esi, auth, systems, drives, settings, alts, shell);

        // Point the kill feed at whatever system the FC is in, and tell it which systems to shout about.
        System.SystemChanged += Feed.SetSystem;
        System.LocationContextChanged += Feed.SetWatchedSystems;
    }

    // Pane switcher: the minimap is primary (the constellation overview rides alongside it), with
    // the scan tools and the hunt board behind their own tabs.
    [ObservableProperty] private string _activePane = "Map";
    public bool IsMapPane   => ActivePane == "Map";
    public bool IsDscanPane => ActivePane == "Dscan";
    public bool IsHuntPane  => ActivePane == "Hunt";

    /// <summary>The current-system header belongs to the minimap, not the other panes.</summary>
    public bool ShowMapHeader => IsMapPane;

    partial void OnActivePaneChanged(string value)
    {
        OnPropertyChanged(nameof(IsMapPane));
        OnPropertyChanged(nameof(IsDscanPane));
        OnPropertyChanged(nameof(IsHuntPane));
        OnPropertyChanged(nameof(ShowMapHeader));

        // The kill feed follows the pane: a constellation alongside the minimap, the whole region
        // behind the hunt board, since that is the scale the FC is thinking at there.
        Feed.SetKillScope(IsHuntPane ? ZkillService.KillScope.Region
                                     : ZkillService.KillScope.Constellation);

        // Hunt loads the system index and reads jump ranges off ESI, so it waits until it's opened
        // rather than doing that work for an FC who never uses it.
        if (IsHuntPane) _ = Hunt.StartAsync();
    }

    [RelayCommand] private void SetPane(string pane) => ActivePane = pane;

    [RelayCommand] private void BackToMenu() => _shell.ShowMenu();
}
