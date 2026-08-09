using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>One ESI scope row in the Settings access health check.</summary>
public record ScopeStatus(string Label, bool Granted);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SystemSearchService _systemSearch;
    private readonly ShellViewModel _shell;
    private readonly EsiAuthService _auth;

    /// <summary>The app-lifetime overlay/alert state - bound directly by the overlay controls.</summary>
    public AlertHub Overlay { get; }

    /// <summary>The shell - exposed so the Settings "Check for updates" control can reach its commands.</summary>
    public ShellViewModel Shell => _shell;

    public SettingsViewModel(SettingsService settings, AlertHub overlay,
                             SystemSearchService systemSearch, ShellViewModel shell, EsiAuthService auth)
    {
        _settings = settings;
        Overlay   = overlay;
        _systemSearch = systemSearch;
        _shell = shell;
        _auth = auth;

        EveLogsPath        = settings.Current.EveLogsPath;
        BoostChannelPrefix = settings.Current.BoostChannelPrefix;
        IntelChannelPrefix = settings.Current.IntelChannelPrefix;
        _formupSystemText  = settings.Current.FormupSystem;
        _formupSystemId    = settings.Current.FormupSystemId;

        _themeName = ThemeService.Name(ThemeService.Current);

        BuildScopeHealth();
        _ = LoadSystemsAsync();
    }

    // ESI access health - shows which scopes the active character granted, and flags any missing
    // (a character authorized before a newer scope was added won't have it until it's re-added).
    public ObservableCollection<ScopeStatus> ScopeHealth { get; } = [];
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _missingScopes;

    private static readonly (string Scope, string Label)[] ScopeLabels =
    [
        ("esi-fleets.read_fleet.v1",        "Read fleet roster"),
        ("esi-fleets.write_fleet.v1",       "Manage fleet (invite / move / kick / MOTD)"),
        ("esi-location.read_location.v1",   "Read your location"),
        ("esi-location.read_online.v1",     "Alt online status"),
        ("esi-location.read_ship_type.v1",  "Alt current ship"),
        ("esi-universe.read_structures.v1", "Resolve docked-structure names"),
    ];

    private void BuildScopeHealth()
    {
        ScopeHealth.Clear();
        IsLoggedIn = _auth.AuthenticatedCharacterId > 0;
        var granted = _auth.GrantedScopes();
        foreach (var (scope, label) in ScopeLabels)
            ScopeHealth.Add(new ScopeStatus(label, granted.Contains(scope)));
        MissingScopes = IsLoggedIn && ScopeHealth.Any(s => !s.Granted);
    }

    [RelayCommand]
    private void ClearRememberedPing()
    {
        _settings.Current.CustomPing = new CustomPingState();
        _settings.Save();
        StatusMessage = "Cleared saved custom ping details.";
    }

    // Colour theme
    // Applies + persists immediately (no Save-button needed) so the switch is instant and remembered.
    [ObservableProperty] private string _themeName = "Nebula";
    public bool IsNebula => ThemeName == "Nebula";
    public bool IsCarbon => ThemeName == "Carbon";
    public bool IsPhoton => ThemeName == "Photon";
    public bool IsRust   => ThemeName == "Rust";
    partial void OnThemeNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsNebula));
        OnPropertyChanged(nameof(IsCarbon));
        OnPropertyChanged(nameof(IsPhoton));
        OnPropertyChanged(nameof(IsRust));
    }

    [RelayCommand]
    private void SelectTheme(string name)
    {
        var theme = ThemeService.Parse(name);
        ThemeService.Apply(theme);
        ThemeName = ThemeService.Name(theme);
        _settings.Current.Theme = ThemeName;
        _settings.Save();
    }

    // Form-up system (autocomplete search)
    [ObservableProperty] private string _formupSystemText = string.Empty;
    [ObservableProperty] private bool   _systemsLoading;
    public ObservableCollection<SystemMatch> SystemSuggestions { get; } = [];

    private int  _formupSystemId;
    private bool _suppressSearch;   // stops the dropdown re-opening when we set the text programmatically

    private async Task LoadSystemsAsync()
    {
        SystemsLoading = true;
        await _systemSearch.EnsureLoadedAsync();
        SystemsLoading = false;
    }

    partial void OnFormupSystemTextChanged(string value)
    {
        if (_suppressSearch) return;
        SystemSuggestions.Clear();
        foreach (var m in _systemSearch.Search(value)) SystemSuggestions.Add(m);
        _formupSystemId = _systemSearch.ResolveId(value) ?? 0;   // 0 until a real system is matched
    }

    [RelayCommand]
    private void PickSystem(SystemMatch? match)
    {
        if (match == null) return;
        _suppressSearch = true;
        FormupSystemText = match.Name;
        _formupSystemId  = match.Id;
        _suppressSearch = false;
        SystemSuggestions.Clear();
    }

    [RelayCommand]
    private void ClearFormup()
    {
        _suppressSearch = true;
        FormupSystemText = string.Empty;
        _formupSystemId = 0;
        _suppressSearch = false;
        SystemSuggestions.Clear();
    }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GamelogsPath))]
    [NotifyPropertyChangedFor(nameof(ChatlogsPath))]
    [NotifyPropertyChangedFor(nameof(GamelogsFound))]
    [NotifyPropertyChangedFor(nameof(ChatlogsFound))]
    private string _eveLogsPath = string.Empty;

    [ObservableProperty] private string _boostChannelPrefix = "Boost";
    [ObservableProperty] private string _intelChannelPrefix = "Intel";
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Derived paths + existence indicators give the user immediate feedback
    public string GamelogsPath  => Path.Combine(EveLogsPath, "Gamelogs");
    public string ChatlogsPath  => Path.Combine(EveLogsPath, "Chatlogs");
    public bool   GamelogsFound => Directory.Exists(GamelogsPath);
    public bool   ChatlogsFound => Directory.Exists(ChatlogsPath);

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select your EVE logs folder (contains Gamelogs and Chatlogs)",
            InitialDirectory = Directory.Exists(EveLogsPath) ? EveLogsPath : AppSettings.DefaultLogsPath
        };
        if (dialog.ShowDialog() == true)
            EveLogsPath = dialog.FolderName;
    }

    [RelayCommand]
    private void ResetToDefault() => EveLogsPath = AppSettings.DefaultLogsPath;

    [RelayCommand]
    private void Save()
    {
        _settings.Current.EveLogsPath        = EveLogsPath.Trim();
        _settings.Current.BoostChannelPrefix = string.IsNullOrWhiteSpace(BoostChannelPrefix)
            ? "Boost" : BoostChannelPrefix.Trim();
        _settings.Current.IntelChannelPrefix = string.IsNullOrWhiteSpace(IntelChannelPrefix)
            ? "Intel" : IntelChannelPrefix.Trim();
        _settings.Current.FormupSystem       = FormupSystemText.Trim();
        _settings.Current.FormupSystemId     = _formupSystemId;

        // Alert configuration lives on the Alerts page now, and saves itself there.
        _settings.Save();

        StatusMessage = "Saved. Applies next time you enter a fleet.";
    }

    [RelayCommand]
    private void Back() => _shell.ShowMenu();
}
