using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// The Intel Tools window — combines the intel features on one screen (like the fleet window):
/// a Scan panel (d-scan / local), a live System panel (current system + constellation map + hot
/// systems), and a combined intel Feed (zKill kills + in-game intel channel).
/// </summary>
public partial class IntelViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public DScanViewModel        Scan   { get; }
    public SystemIntelViewModel  System { get; }
    public IntelFeedViewModel    Feed   { get; }

    public IntelViewModel(EsiService esi, EsiAuthService auth, ZkillService zkill,
                          SystemSearchService systems, SettingsService settings, ShellViewModel shell)
    {
        _shell = shell;
        Scan   = new DScanViewModel(esi, auth);
        System = new SystemIntelViewModel(esi, auth);
        Feed   = new IntelFeedViewModel(esi, zkill, systems, settings);

        // Point the kill feed at whatever system the FC is in.
        System.SystemChanged += Feed.SetSystem;
    }

    [RelayCommand] private void BackToMenu() => _shell.ShowMenu();
}
