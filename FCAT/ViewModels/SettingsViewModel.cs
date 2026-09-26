using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
    private readonly AaConnectorService _aa;

    /// <summary>The app-lifetime overlay/alert state - bound directly by the overlay controls.</summary>
    public AlertHub Overlay { get; }

    /// <summary>The shell - exposed so the Settings "Check for updates" control can reach its commands.</summary>
    public ShellViewModel Shell => _shell;

    public SettingsViewModel(SettingsService settings, AlertHub overlay,
                             SystemSearchService systemSearch, ShellViewModel shell, EsiAuthService auth,
                             AaConnectorService aa)
    {
        _settings = settings;
        Overlay   = overlay;
        _systemSearch = systemSearch;
        _shell = shell;
        _auth = auth;
        _aa = aa;

        _aaBaseUrlText = settings.Current.AaBaseUrl;
        ReadAaState();

        EveLogsPath        = settings.Current.EveLogsPath;
        BoostChannelPrefix = settings.Current.BoostChannelPrefix;
        IntelChannelPrefix = settings.Current.IntelChannelPrefix;
        Formup  = new SystemPicker(systemSearch);
        Capital = new SystemPicker(systemSearch);
        Formup .Set(settings.Current.FormupSystem,  settings.Current.FormupSystemId);
        Capital.Set(settings.Current.CapitalSystem, settings.Current.CapitalSystemId);

        _themeName = ThemeService.Name(ThemeService.Current);

        BuildScopeHealth();
        _ = LoadSystemsAsync();
    }

    // ESI access health - shows which scopes the active character granted, and flags any missing
    // (a character authorized before a newer scope was added won't have it until it's re-added).
    public ObservableCollection<ScopeStatus> ScopeHealth { get; } = [];
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _missingScopes;

    // Plain-English name per scope. The LIST of scopes comes from EsiAuthService, not from here -
    // this only supplies wording, so adding a scope to the login request can't quietly go unlisted.
    private static readonly Dictionary<string, string> ScopeLabels = new()
    {
        ["esi-fleets.read_fleet.v1"]        = "Read fleet roster",
        ["esi-fleets.write_fleet.v1"]       = "Manage fleet (invite / move / kick / MOTD)",
        ["esi-location.read_location.v1"]   = "Read your location",
        ["esi-location.read_online.v1"]     = "Alt online status",
        ["esi-location.read_ship_type.v1"]  = "Alt current ship",
        ["esi-universe.read_structures.v1"] = "Resolve docked-structure names",
    };

    private void BuildScopeHealth()
    {
        ScopeHealth.Clear();
        IsLoggedIn = _auth.AuthenticatedCharacterId > 0;
        var granted = _auth.GrantedScopes();
        foreach (var scope in EsiAuthService.RequiredScopes)
            ScopeHealth.Add(new ScopeStatus(ScopeLabels.GetValueOrDefault(scope, scope), granted.Contains(scope)));
        MissingScopes = IsLoggedIn && ScopeHealth.Any(s => !s.Granted);
    }

    // Alliance Auth connector
    // An FC pastes their auth's address and a key they generated there, and FCAT starts reading
    // their alliance's doctrines and friendly structures. It is entirely optional: with nothing set
    // up this section offers the two fields and the rest of the app behaves exactly as it always
    // has. Nothing anywhere else in FCAT is allowed to require it.

    [ObservableProperty] private string _aaBaseUrlText = string.Empty;
    [ObservableProperty] private string _aaKeyText     = string.Empty;
    [ObservableProperty] private string _aaStatus      = string.Empty;

    /// <summary>True once a key is stored - swaps the two input fields for the connected summary.</summary>
    [ObservableProperty] private bool _aaConnected;

    /// <summary>A test or refresh is in flight; disables the buttons so it isn't fired twice.</summary>
    [ObservableProperty] private bool _aaBusy;

    /// <summary>Red rather than green - set when the last thing that happened was a failure.</summary>
    [ObservableProperty] private bool _aaFailed;

    /// <summary>What came back last time, e.g. "12 doctrines · 34 fits · 8 structures".</summary>
    [ObservableProperty] private string _aaSummary = string.Empty;

    private void ReadAaState()
    {
        AaConnected = _aa.IsConfigured;
        AaFailed    = _aa.State is AaState.Failed or AaState.Stale;

        // The status line is for what just happened; the summary below carries the counts. Only
        // speak up here when something is wrong, so a healthy connection does not say it twice.
        if (AaFailed) AaStatus = _aa.Status;

        if (!_aa.IsConfigured) { AaSummary = string.Empty; return; }

        var snapshot = _aa.Snapshot;
        var parts = new List<string>();
        if (snapshot.Doctrines.Count  > 0) parts.Add($"{snapshot.Doctrines.Count} doctrines");
        if (snapshot.Fittings.Count   > 0) parts.Add($"{snapshot.Fittings.Count} fits");
        if (snapshot.Structures.Count > 0) parts.Add($"{snapshot.Structures.Count} structures");
        AaSummary = parts.Count > 0 ? string.Join(" · ", parts) : "Nothing to read from this auth yet";
    }

    /// <summary>Check the address and key before saving either, so a typo never half-configures it.</summary>
    [RelayCommand]
    private async Task ConnectAa()
    {
        if (AaBusy) return;
        AaBusy   = true;
        AaFailed = false;
        AaStatus = "Checking…";

        var (ok, message) = await _aa.TestAsync(AaBaseUrlText, AaKeyText);
        AaStatus = message;
        AaFailed = !ok;

        if (ok)
        {
            _aa.Connect(AaBaseUrlText, AaKeyText);
            AaKeyText     = string.Empty;   // it is stored encrypted now; no reason to keep it on screen
            AaBaseUrlText = _aa.BaseUrl;
            AaConnected   = true;

            // Connect starts the background loop, whose first pull lands a moment later. Wait for one
            // here so the panel shows what came back instead of an empty line until the page is
            // re-opened. Costs one extra pair of GETs, on a button press, against their own server.
            await _aa.RefreshAsync();
            ReadAaState();
        }

        AaBusy = false;
    }

    /// <summary>Pull again now, for an FC who just had a doctrine added or a structure reinforced.</summary>
    [RelayCommand]
    private async Task RefreshAa()
    {
        if (AaBusy) return;
        AaBusy   = true;
        AaStatus = "Refreshing…";
        await _aa.RefreshAsync();
        ReadAaState();
        AaBusy = false;
    }

    /// <summary>Forget the key and everything fetched with it. The key itself stays valid until the
    /// FC revokes it on their auth, which is the only place that can revoke one.</summary>
    [RelayCommand]
    private void DisconnectAa()
    {
        _aa.Disconnect();
        AaBaseUrlText = string.Empty;
        AaKeyText     = string.Empty;
        ReadAaState();
        AaStatus = "Disconnected. Revoke the key on your auth if you want it dead for good.";
    }

    /// <summary>Opens the FC's own auth page where keys are generated.</summary>
    [RelayCommand]
    private void OpenAaKeys()
    {
        var root = AaConnectorService.NormaliseBaseUrl(AaBaseUrlText);
        Open(root == null ? "https://github.com/MifuneSG/FCAT/tree/main/aa-connector" : $"{root}/fcat/");
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
    /// <summary>The fleet form-up system, used by the straggler check.</summary>
    public SystemPicker Formup { get; }

    /// <summary>The alliance capital, which prices every Ansiblex jump. See AnsiblexBridge.</summary>
    public SystemPicker Capital { get; }

    /// <summary>True while the system name index is still being built, so the boxes can say so.</summary>
    [ObservableProperty] private bool _systemsLoading;

    private async Task LoadSystemsAsync()
    {
        SystemsLoading = true;
        await _systemSearch.EnsureLoadedAsync();
        SystemsLoading = false;
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
    /// <summary>
    /// What the user should quote in a bug report. The informational version carries the -beta
    /// suffix that the plain assembly version drops; the +sha the SDK appends is noise here.
    /// </summary>
    public string VersionLine
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? asm.GetName().Version?.ToString()
                    ?? "unknown";
            var plus = v.IndexOf('+');
            if (plus > 0) v = v[..plus];
            return $"Version {v}";
        }
    }

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

    /// <summary>
    /// Everything a bug report needs, on the clipboard. FCAT swallows failures on purpose so a
    /// dropped poll cannot take the app down, which means the user sees an empty panel and has
    /// nothing to send. This is that missing half.
    /// </summary>
    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            var report = Log.Diagnostics(
                VersionLine.Replace("Version ", string.Empty),
                ("EVE logs", EveLogsPath),
                ("Gamelogs", GamelogsFound ? "found" : "MISSING"),
                ("Chatlogs", ChatlogsFound ? "found" : "MISSING"),
                ("Demo mode", _shell.DemoMode ? "on" : "off"));

            System.Windows.Clipboard.SetText(report);
            StatusMessage = "Diagnostics copied - paste it into the Discord.";
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "could not copy diagnostics", ex);
            StatusMessage = "Couldn't access the clipboard.";
        }
    }

    /// <summary>Show the log folder, for when the clipboard is not enough.</summary>
    [RelayCommand]
    private void OpenLogFolder() => Open(Log.Directory);

    [RelayCommand]
    private void OpenRepo() => Open("https://github.com/MifuneSG/FCAT");

    [RelayCommand]
    private void OpenDiscord() => Open("https://discord.gg/fFznkAFen8");

    private static void Open(string url) => Browser.Open(url);

    [RelayCommand]
    private void Save()
    {
        _settings.Current.EveLogsPath        = EveLogsPath.Trim();
        _settings.Current.BoostChannelPrefix = string.IsNullOrWhiteSpace(BoostChannelPrefix)
            ? "Boost" : BoostChannelPrefix.Trim();
        _settings.Current.IntelChannelPrefix = string.IsNullOrWhiteSpace(IntelChannelPrefix)
            ? "Intel" : IntelChannelPrefix.Trim();
        _settings.Current.FormupSystem       = Formup.Text.Trim();
        _settings.Current.FormupSystemId     = Formup.Id;
        _settings.Current.CapitalSystem      = Capital.Text.Trim();
        _settings.Current.CapitalSystemId    = Capital.Id;

        // Alert configuration lives on the Alerts page now, and saves itself there.
        _settings.Save();
        _shell.RestartLogWatcher();   // a corrected logs path should not need an app restart

        StatusMessage = "Saved. Channel names apply next time you enter a fleet.";
    }

    [RelayCommand]
    private void Back() => _shell.ShowMenu();
}
