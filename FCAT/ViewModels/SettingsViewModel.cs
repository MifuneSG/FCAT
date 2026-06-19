using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ShellViewModel _shell;

    /// <summary>The app-lifetime overlay/alert state — bound directly by the overlay controls.</summary>
    public AlertHub Overlay { get; }

    public SettingsViewModel(SettingsService settings, AlertHub overlay, ShellViewModel shell)
    {
        _settings = settings;
        Overlay   = overlay;
        _shell = shell;

        EveLogsPath        = settings.Current.EveLogsPath;
        BoostChannelPrefix = settings.Current.BoostChannelPrefix;

        AlertSoundsEnabled = settings.Current.AlertSoundsEnabled;
        TackledSound       = settings.Current.TackledSound;
        CapTroubleSound    = settings.Current.CapTroubleSound;
        BoostLostSound     = settings.Current.BoostLostSound;
        _alertClearSeconds = settings.Current.AlertClearSeconds;
    }

    // ── Alert sounds ──
    [ObservableProperty] private bool   _alertSoundsEnabled = true;
    [ObservableProperty] private string _tackledSound    = "Alarm";
    [ObservableProperty] private string _capTroubleSound = "Beep";
    [ObservableProperty] private string _boostLostSound  = "Double Beep";

    // ── Auto-clear ──
    // Free-text seconds entry (like the boost-channel field). Backed by an int; non-numeric or
    // negative input is clamped to 0 (= keep alerts until the session ends).
    private int _alertClearSeconds = 60;

    public string AlertClearSecondsText
    {
        get => _alertClearSeconds.ToString();
        set
        {
            var seconds = int.TryParse(value, out var n) && n > 0 ? n : 0;
            if (SetProperty(ref _alertClearSeconds, seconds, nameof(AlertClearSecondsText)))
                OnPropertyChanged(nameof(AlertClearHint));
        }
    }

    public int AlertClearSeconds => _alertClearSeconds;

    public string AlertClearHint => _alertClearSeconds <= 0
        ? "Off — alerts stay until the session ends."
        : "Alerts disappear from the feed and overlay after this long.";

    private static string Cycle(string current)
    {
        var i = Array.IndexOf(SoundService.Presets, current);
        return SoundService.Presets[(i + 1) % SoundService.Presets.Length];
    }

    [RelayCommand] private void ToggleSounds() => AlertSoundsEnabled = !AlertSoundsEnabled;

    [RelayCommand] private void CycleTackled()    { TackledSound    = Cycle(TackledSound);    SoundService.Play(TackledSound); }
    [RelayCommand] private void CycleCapTrouble() { CapTroubleSound = Cycle(CapTroubleSound); SoundService.Play(CapTroubleSound); }
    [RelayCommand] private void CycleBoostLost()  { BoostLostSound  = Cycle(BoostLostSound);  SoundService.Play(BoostLostSound); }

    [RelayCommand] private void TestTackled()    => SoundService.Play(TackledSound);
    [RelayCommand] private void TestCapTrouble() => SoundService.Play(CapTroubleSound);
    [RelayCommand] private void TestBoostLost()  => SoundService.Play(BoostLostSound);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GamelogsPath))]
    [NotifyPropertyChangedFor(nameof(ChatlogsPath))]
    [NotifyPropertyChangedFor(nameof(GamelogsFound))]
    [NotifyPropertyChangedFor(nameof(ChatlogsFound))]
    private string _eveLogsPath = string.Empty;

    [ObservableProperty] private string _boostChannelPrefix = "Boost";
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

        _settings.Current.AlertSoundsEnabled = AlertSoundsEnabled;
        _settings.Current.TackledSound       = TackledSound;
        _settings.Current.CapTroubleSound    = CapTroubleSound;
        _settings.Current.BoostLostSound     = BoostLostSound;
        _settings.Current.AlertClearSeconds  = AlertClearSeconds;
        _settings.Save();

        StatusMessage = "Saved. Applies next time you enter a fleet.";
    }

    [RelayCommand]
    private void Back() => _shell.ShowMenu();
}
