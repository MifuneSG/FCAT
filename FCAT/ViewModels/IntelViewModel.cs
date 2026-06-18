using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// The Intel Tools window — combines the intel features on one screen (like the fleet window):
/// a Scan panel (d-scan / local) and a live System panel (your current system + neighbour map).
/// </summary>
public partial class IntelViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public DScanViewModel        Scan   { get; }
    public SystemIntelViewModel  System { get; }

    public IntelViewModel(EsiService esi, EsiAuthService auth, ShellViewModel shell)
    {
        _shell = shell;
        Scan   = new DScanViewModel(esi, auth);
        System = new SystemIntelViewModel(esi, auth);
    }

    [RelayCommand] private void BackToMenu() => _shell.ShowMenu();
}
