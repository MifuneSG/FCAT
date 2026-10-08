using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>One entry in a sound dropdown: what gets saved, and what the FC reads.</summary>
public record SoundChoice(string Value, string Label);

/// <summary>One entry in the overlay auto-clear dropdowns. 0 seconds means it never expires.</summary>
public record ClearChoice(int Seconds, string Label);

/// <summary>One entry in a severity dropdown.</summary>
public record SeverityChoice(AlertSeverity Value, string Label);

/// <summary>
/// The Alerts page, in three tabs.
///
/// FEED is what has happened. ALERTS is the one list of everything FCAT can raise, built-ins and
/// the FC's own rules together. OVERLAY is the window that draws over the game.
///
/// The split exists because those are three different jobs and they used to share one "Configure"
/// tab: a global sound switch, a sound importer, two overlay window buttons, three overlay timeout
/// dropdowns and the per-alert list all sat in one toolbar, with nothing saying which control
/// belonged to which. The controls are the same ones - they are just with the thing they act on.
/// </summary>

public partial class AlertsViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public AlertHub Hub { get; }
    public ObservableCollection<FcAlert> Alerts => Hub.Alerts;

    private readonly AltTracker _alts;

    public AlertsViewModel(AlertHub hub, SettingsService settings, AltTracker alts)
    {
        Hub = hub;
        _settings = settings;
        _alts = alts;
        _alertSoundsEnabled = settings.Current.AlertSoundsEnabled;
        _clearCritical = settings.Current.AlertClearSecondsCritical;
        _clearWarning  = settings.Current.AlertClearSeconds;
        _clearInfo     = settings.Current.AlertClearSecondsInfo;
        LoadSoundChoices();
        LoadRows();

        // Its own view, not the shared default one: the overlay window binds the same collection,
        // and a default view would filter what is drawn over the game too.
        FeedView = new CollectionViewSource { Source = Hub.Alerts }.View;
        FeedView.Filter = PassesFeedFilter;
        ((INotifyCollectionChanged)Hub.Alerts).CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShownCount));
            OnPropertyChanged(nameof(FeedEmptyText));
        };
    }

    /// <summary>
    /// How long each severity sits on the overlay before it clears itself. The session feed keeps
    /// everything regardless - this only governs what's drawn over the game.
    /// </summary>
    public ObservableCollection<ClearChoice> ClearChoices { get; } =
    [
        new(15, "15s"), new(30, "30s"), new(60, "1 min"),
        new(120, "2 min"), new(300, "5 min"), new(0, "Stays up"),
    ];

    [ObservableProperty] private int _clearCritical;
    [ObservableProperty] private int _clearWarning;
    [ObservableProperty] private int _clearInfo;

    partial void OnClearCriticalChanged(int value) => SaveClearTimes();
    partial void OnClearWarningChanged(int value)  => SaveClearTimes();
    partial void OnClearInfoChanged(int value)     => SaveClearTimes();

    private void SaveClearTimes()
    {
        _settings.Current.AlertClearSecondsCritical = ClearCritical;
        _settings.Current.AlertClearSeconds         = ClearWarning;
        _settings.Current.AlertClearSecondsInfo     = ClearInfo;
        _settings.Save();
    }

    /// <summary>Wipe the overlay without touching the session feed - for when a fight ends messy.</summary>
    [RelayCommand]
    private void ClearOverlayNow() => Hub.OverlayAlerts.Clear();

    // Feed / Alerts / Overlay tabs
    [ObservableProperty] private string _tab = "Feed";
    public bool IsFeedTab    => Tab == "Feed";
    public bool IsConfigTab  => Tab == "Alerts";
    public bool IsOverlayTab => Tab == "Overlay";

    /// <summary>The line under the page title. It used to describe the feed on every tab, which
    /// read as a lie while you were configuring something.</summary>
    public string TabHint => Tab switch
    {
        "Alerts"  => "Every alert FCAT can raise, yours and the built-ins",
        "Overlay" => "The window FCAT draws over your game",
        _         => "Every alert raised this session, newest first",
    };

    partial void OnTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsFeedTab));
        OnPropertyChanged(nameof(IsConfigTab));
        OnPropertyChanged(nameof(IsOverlayTab));
        OnPropertyChanged(nameof(TabHint));
    }

    [RelayCommand] private void SetTab(string t) => Tab = t;

    // ---- feed filter -------------------------------------------------------------------------
    // A busy fight fills the feed in seconds, and what you want afterwards is nearly always the
    // loud one. Filtering a view rather than a copy means new alerts still arrive live.
    public ICollectionView FeedView { get; }

    [ObservableProperty] private string _feedFilter = "All";

    public bool FeedIsAll      => FeedFilter == "All";
    public bool FeedIsCritical => FeedFilter == "Critical";
    public bool FeedIsWarning  => FeedFilter == "Warning";
    public bool FeedIsInfo     => FeedFilter == "Info";

    /// <summary>How many alerts the current filter leaves on screen - drives the count badge and
    /// tells the difference between "nothing has happened" and "nothing matches this filter".</summary>
    public int ShownCount => FeedView.Cast<object>().Count();

    /// <summary>What an empty feed says. An FC who filtered to Critical and sees "waiting for
    /// alerts" would read that as a quiet fleet rather than as their own filter.</summary>
    public string FeedEmptyText => Alerts.Count == 0
        ? "Waiting for alerts"
        : $"No {FeedFilter.ToLowerInvariant()} alerts this session";

    partial void OnFeedFilterChanged(string value)
    {
        OnPropertyChanged(nameof(FeedIsAll));      OnPropertyChanged(nameof(FeedIsCritical));
        OnPropertyChanged(nameof(FeedIsWarning));  OnPropertyChanged(nameof(FeedIsInfo));
        FeedView.Refresh();
        OnPropertyChanged(nameof(ShownCount));
        OnPropertyChanged(nameof(FeedEmptyText));
    }

    [RelayCommand] private void SetFeedFilter(string f) => FeedFilter = f;

    private bool PassesFeedFilter(object o) =>
        FeedFilter == "All" || (o is FcAlert a && a.Severity.ToString() == FeedFilter);

    // The one list of every alert FCAT can raise
    public ObservableCollection<AlertConfigRow> Rows { get; } = [];

    /// <summary>The list as the page draws it: one section per thing an alert can watch. Holds the
    /// same row objects as <see cref="Rows"/>, which stays flat because saving walks all of them.</summary>
    public ObservableCollection<AlertGroupRow> Groups { get; } = [];

    /// <summary>Every cue an alert can use - the built-in presets plus any .wav the FC imported.
    /// The stored value keeps the filename (that's what plays it); the label drops the extension.
    ///
    /// Only ever added to or removed from, never rebuilt. The sound dropdowns bind SelectedValue
    /// against this, so clearing it would blank every row's cue and save the blanks.</summary>
    public ObservableCollection<SoundChoice> SoundChoices { get; } = [];

    /// <summary>Just the imported ones, for the manage-sounds list.</summary>
    public ObservableCollection<SoundChoice> CustomSoundChoices { get; } = [];

    public bool HasCustomSounds => CustomSoundChoices.Count > 0;

    private static SoundChoice Choice(string fileName) => new(fileName,
        fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName);

    private void LoadSoundChoices()
    {
        foreach (var p in SoundService.Presets) SoundChoices.Add(new SoundChoice(p, p));
        foreach (var f in SoundService.CustomSounds()) AddSoundChoice(f);
    }

    private void AddSoundChoice(string fileName)
    {
        if (SoundChoices.Any(c => c.Value.Equals(fileName, StringComparison.OrdinalIgnoreCase))) return;
        var choice = Choice(fileName);
        SoundChoices.Add(choice);
        CustomSoundChoices.Add(choice);
        OnPropertyChanged(nameof(HasCustomSounds));
    }

    [ObservableProperty] private bool _alertSoundsEnabled = true;

    [RelayCommand]
    private void ToggleSounds()
    {
        AlertSoundsEnabled = !AlertSoundsEnabled;
        Save();
        OnPropertyChanged(nameof(AllCuesSilent));
    }

    /// <summary>
    /// Sounds are switched on, yet every listed alert is set to "None". That combination is silence
    /// with no sign of it anywhere on the page, and it is easy to land in: "None" is the first item
    /// in the sound list. Worth saying out loud, because the FC finds out by not being warned.
    /// </summary>
    public bool AllCuesSilent =>
        AlertSoundsEnabled && Rows.Count > 0 &&
        Rows.All(r => string.IsNullOrWhiteSpace(r.Sound) || r.Sound.Equals("None", StringComparison.OrdinalIgnoreCase));

    /// <summary>Rebuild the per-alt rows after a role was changed on the account page.</summary>
    public void ReloadRows() => LoadRows();

    private void LoadRows()
    {
        var s = _settings.Current;
        bool On(AlertType t) => !s.MutedAlertTypes.Contains(t.ToString());

        // Only types the FC moved are stored, so anything absent falls back to the shipped default.
        AlertSeverity Sev(AlertType t)
            => s.AlertSeverities.TryGetValue(t.ToString(), out var n) && Enum.TryParse<AlertSeverity>(n, out var v)
               ? v : FcAlert.DefaultSeverityFor(t);

        Rows.Clear();
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.Tackled, "Tackled",
            "You are scrammed or pointed", Sev(AlertType.Tackled), s.TackledSound, On(AlertType.Tackled)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.IntelHostile, "Intel call-out",
            "Your system or one next door named in intel", Sev(AlertType.IntelHostile), s.IntelHostileSound, On(AlertType.IntelHostile)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.DpsLoss, "DPS loss",
            "The fleet's damage line is dying", Sev(AlertType.DpsLoss), s.DpsLossSound, On(AlertType.DpsLoss)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.LogiRatio, "Logi thin",
            "Logi dropped below your ratio", Sev(AlertType.LogiRatio), s.LogiRatioSound, On(AlertType.LogiRatio)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.LogiChain, "Cap chain",
            "A cap-chain logi dropped", Sev(AlertType.LogiChain), s.LogiChainSound, On(AlertType.LogiChain)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.BoostLost, "Booster down",
            "A booster was podded, links lost", Sev(AlertType.BoostLost), s.BoostLostSound, On(AlertType.BoostLost)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.CapTrouble, "Cap out",
            "A module shut off from low cap", Sev(AlertType.CapTrouble), s.CapTroubleSound, On(AlertType.CapTrouble)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.NoFleetWarp, "Won't follow warp",
            "Someone has fleet warp switched off", Sev(AlertType.NoFleetWarp), s.NoFleetWarpSound, On(AlertType.NoFleetWarp)));
        Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.NoBooster, "Nobody boosting",
            "A fleet big enough to need links has none", Sev(AlertType.NoBooster), s.NoBoosterSound, On(AlertType.NoBooster)));

        // Always listed, even with no alt labelled. The group they sit in carries the reason they
        // are quiet, which is the part an FC could not previously find out.
        {
            Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.CloakDropped, "Cloak dropped",
                "Your cloak dropped, or would not go up", Sev(AlertType.CloakDropped), s.CloakDroppedSound, On(AlertType.CloakDropped)));
            Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.AltPodded, "Alt podded",
                "An alt's ship became a pod in space", Sev(AlertType.AltPodded), s.AltPoddedSound, On(AlertType.AltPodded)));
            Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.AltOffline, "Alt offline",
                "An alt dropped off", Sev(AlertType.AltOffline), s.AltOfflineSound, On(AlertType.AltOffline)));
            Rows.Add(AlertConfigRow.ForBuiltIn(AlertType.AltKills, "Kills near an alt",
                "Kills appeared in the system an alt is in", Sev(AlertType.AltKills), s.AltKillsSound, On(AlertType.AltKills)));
        }

        foreach (var r in s.CustomAlerts) Rows.Add(AlertConfigRow.ForRule(r));

        foreach (var row in Rows)
        {
            row.SoundChoices = SoundChoices;   // plain property: set it before Track, so it is not a "change"
            Track(row);
        }
        BuildGroups();
        OnPropertyChanged(nameof(AllCuesSilent));
    }

    /// <summary>
    /// Sorts the flat row list into the sections the page draws.
    ///
    /// Every section is listed whether or not it can fire right now. The alt alerts used to vanish
    /// entirely until a character was labelled Cyno or Scout, so an FC who had alts but had not
    /// labelled them saw no alt alerts and nothing saying why - the feature was invisible rather
    /// than off. A section with a reason on it is something you can act on.
    /// </summary>
    private void BuildGroups()
    {
        Groups.Clear();

        var fleet = new AlertGroupRow(AlertGroup.Fleet, "YOUR FLEET",
            "From the fleet roster and the boost channel. These are the ones that tell you something your own screen does not.");
        var intel = new AlertGroupRow(AlertGroup.Intel, "INTEL",
            "Call-outs in the in-game intel channel, while the Intel page is open.");
        var ship  = new AlertGroupRow(AlertGroup.Ship, "YOUR SHIP",
            "Your own client's game log. Cap-out ships switched off: it fires every time a module blinks, and your HUD is already showing you.");

        var alts  = new AlertGroupRow(AlertGroup.Alts, "YOUR ALTS",
            "Characters you fly alongside the fleet.",
            _alts.HasCovertAlt
                ? string.Empty
                : "Nothing is labelled Cyno or Scout yet, so these stay quiet. Set a role on a character on the Account page and they start working.");

        var own   = new AlertGroupRow(AlertGroup.Own, "YOUR OWN RULES",
            "Text you told FCAT to watch for.",
            Rows.Any(r => r.IsCustom) ? string.Empty : "None yet. \"New alert\" above walks you through one.");

        foreach (var g in new[] { fleet, intel, ship, alts, own })
        {
            foreach (var row in Rows.Where(r => r.Group == g.Group)) g.Rows.Add(row);
            Groups.Add(g);
        }
    }

    /// <summary>
    /// Writes the list back whenever a row changes. Rows carry their own on/off and sound state, so
    /// without this a muted alert or a changed cue only lasted until the app closed.
    /// </summary>
    private void Track(AlertConfigRow row) => row.PropertyChanged += (_, _) =>
    {
        Save();
        OnPropertyChanged(nameof(AllCuesSilent));
        foreach (var g in Groups) g.Refresh();   // the section switch reads "all off" off its rows
    };

    /// <summary>The row's cue, or the saved one if that alert isn't listed right now. Without the
    /// fallback, saving while the alt rows are hidden would blank their cues.</summary>
    private string SoundOf(AlertType t, string saved) => Rows.FirstOrDefault(r => r.BuiltIn == t)?.Sound ?? saved;

    /// <summary>Writes the whole list back to settings. Cheap, so every row change calls it.</summary>
    [RelayCommand]
    private void Save()
    {
        var s = _settings.Current;
        s.AlertSoundsEnabled = AlertSoundsEnabled;

        s.TackledSound      = SoundOf(AlertType.Tackled,      s.TackledSound);
        s.CapTroubleSound   = SoundOf(AlertType.CapTrouble,   s.CapTroubleSound);
        s.BoostLostSound    = SoundOf(AlertType.BoostLost,    s.BoostLostSound);
        s.LogiChainSound    = SoundOf(AlertType.LogiChain,    s.LogiChainSound);
        s.DpsLossSound      = SoundOf(AlertType.DpsLoss,      s.DpsLossSound);
        s.LogiRatioSound    = SoundOf(AlertType.LogiRatio,    s.LogiRatioSound);
        s.IntelHostileSound = SoundOf(AlertType.IntelHostile, s.IntelHostileSound);
        s.AltOfflineSound   = SoundOf(AlertType.AltOffline,   s.AltOfflineSound);
        s.AltPoddedSound    = SoundOf(AlertType.AltPodded,    s.AltPoddedSound);
        s.AltKillsSound     = SoundOf(AlertType.AltKills,     s.AltKillsSound);
        s.CloakDroppedSound = SoundOf(AlertType.CloakDropped, s.CloakDroppedSound);
        s.NoFleetWarpSound  = SoundOf(AlertType.NoFleetWarp,  s.NoFleetWarpSound);
        s.NoBoosterSound    = SoundOf(AlertType.NoBooster,    s.NoBoosterSound);

        // Only rewrite mutes for the alerts actually on screen. An alt alert muted while an alt was
        // labelled would otherwise come back unmuted the moment that label changed.
        var listed = Rows.Where(r => r.BuiltIn != null).Select(r => r.BuiltIn!.Value.ToString()).ToHashSet();
        s.MutedAlertTypes = s.MutedAlertTypes.Where(m => !listed.Contains(m))
            .Concat(Rows.Where(r => r.BuiltIn != null && !r.Enabled).Select(r => r.BuiltIn!.Value.ToString()))
            .Distinct().ToList();

        // Severities, same rule, with one extra: a row sitting on its shipped default is REMOVED
        // rather than written. Three alerts choose their own severity as things get worse, and
        // storing a value pins them - so merely opening this page must not freeze that escalation.
        foreach (var row in Rows.Where(r => r.BuiltIn != null))
        {
            var key = row.BuiltIn!.Value.ToString();
            if (row.SeverityChanged) s.AlertSeverities[key] = row.Severity.ToString();
            else                     s.AlertSeverities.Remove(key);
        }

        foreach (var row in Rows.Where(r => r.Rule != null))
        {
            row.Rule!.Enabled  = row.Enabled;
            row.Rule.Sound     = row.Sound;
            row.Rule.Severity  = row.Severity;
        }
        _settings.Save();
    }

    /// <summary>
    /// Fires an alert as if it had triggered, so a cue and its colour can be checked without
    /// waiting for a fight. Marked as a test, which keeps it out of the session log - auditioning
    /// a sound used to write a real line into the after-action report.
    /// </summary>
    [RelayCommand]
    private void Test(AlertConfigRow? row)
    {
        if (row == null) return;
        Hub.Raise(row.Rule != null
            ? new FcAlert
              {
                  Timestamp = DateTime.Now, AlertType = AlertType.Custom, IsTest = true,
                  CustomTag = row.Rule.Name, CustomHeadline = string.IsNullOrWhiteSpace(row.Rule.Headline) ? $"Test: {row.Name}" : row.Rule.Headline,
                  CustomSound = row.Sound, SeverityOverride = row.Severity,
                  Detail = $"Test fire - {row.Trigger}",
              }
            : new FcAlert
              {
                  Timestamp = DateTime.Now, AlertType = row.BuiltIn!.Value, IsTest = true,
                  Detail = "Test fire", SeverityOverride = row.Severity,
              });
    }

    [RelayCommand]
    private void Delete(AlertConfigRow? row)
    {
        if (row?.Rule == null) return;   // built-ins can be muted, not removed
        _settings.Current.CustomAlerts.RemoveAll(r => r.Id == row.Rule.Id);
        Rows.Remove(row);
        BuildGroups();
        _settings.Save();
    }

    /// <summary>Adds a user .wav so any alert can use it.</summary>
    [RelayCommand]
    private void ImportSound()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Choose an alert sound",
            Filter = "WAV audio (*.wav)|*.wav",
        };
        if (dlg.ShowDialog() != true) return;

        var name = SoundService.ImportSound(dlg.FileName);
        if (name == null)
        {
            Status = "Couldn't use that file. Alert sounds must be .wav (mp3 and ogg won't play).";
            return;
        }
        AddSoundChoice(name);
        Status = $"Added \"{name}\". Pick it from any alert's sound list.";
    }

    // Manage sounds
    [ObservableProperty] private bool _isManagingSounds;

    [RelayCommand] private void ManageSounds()      => IsManagingSounds = true;
    [RelayCommand] private void CloseManageSounds() => IsManagingSounds = false;

    [RelayCommand] private static void PreviewSound(SoundChoice? choice) => SoundService.Play(choice?.Value);

    /// <summary>Plays whatever the new-alert wizard currently has selected.</summary>
    public void PreviewNewSound() => SoundService.Play(NewSound);

    /// <summary>
    /// Removes an imported sound and the file behind it. Anything still using it is moved to a
    /// built-in first - dropping the reference would leave the alert silently muted instead.
    /// </summary>
    [RelayCommand]
    private void DeleteSound(SoundChoice? choice)
    {
        if (choice == null) return;
        const string Fallback = "Beep";

        var moved = 0;
        foreach (var row in Rows.Where(r => r.Sound.Equals(choice.Value, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            row.Sound = Fallback;   // fires the row's save
            moved++;
        }
        if (NewSound.Equals(choice.Value, StringComparison.OrdinalIgnoreCase)) NewSound = Fallback;

        SoundService.DeleteCustomSound(choice.Value);
        foreach (var c in SoundChoices.Where(c => c.Value.Equals(choice.Value, StringComparison.OrdinalIgnoreCase)).ToList())
            SoundChoices.Remove(c);
        foreach (var c in CustomSoundChoices.Where(c => c.Value.Equals(choice.Value, StringComparison.OrdinalIgnoreCase)).ToList())
            CustomSoundChoices.Remove(c);

        OnPropertyChanged(nameof(HasCustomSounds));
        if (!HasCustomSounds) IsManagingSounds = false;

        Status = moved > 0
            ? $"Deleted \"{choice.Label}\". {moved} alert{(moved == 1 ? "" : "s")} moved to {Fallback}."
            : $"Deleted \"{choice.Label}\".";
    }

    [ObservableProperty] private string _status = string.Empty;

    // New-alert flow
    // Asked as a short series of questions rather than one big form - you pick what to watch, what
    // to watch for, then how loud it should be.
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private int  _step;          // 1 = source, 2 = pattern, 3 = loudness + name
    [ObservableProperty] private string _newSource   = "IntelChannel";
    [ObservableProperty] private string _newMatch    = "Contains";
    [ObservableProperty] private string _newPattern  = string.Empty;
    [ObservableProperty] private string _newName     = string.Empty;
    [ObservableProperty] private string _newSeverity = "Warning";
    [ObservableProperty] private string _newSound    = "Beep";
    [ObservableProperty] private string _newError    = string.Empty;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;

    public bool SourceIsIntel   => NewSource == "IntelChannel";
    public bool SourceIsGameLog => NewSource == "GameLog";
    public bool MatchIsContains => NewMatch == "Contains";
    public bool MatchIsRegex    => NewMatch == "Regex";
    public bool SeverityIsInfo     => NewSeverity == "Info";
    public bool SeverityIsWarning  => NewSeverity == "Warning";
    public bool SeverityIsCritical => NewSeverity == "Critical";

    /// <summary>Says plainly what each source can see, so a rule isn't written against data EVE
    /// never writes.</summary>
    public string SourceHint => SourceIsGameLog
        ? "Your own game log - only your character's lines, and only while you're in a fleet session."
        : "Messages in the intel channel, while the Intel page is open.";

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1)); OnPropertyChanged(nameof(IsStep2)); OnPropertyChanged(nameof(IsStep3));
    }

    partial void OnNewSourceChanged(string value)
    {
        OnPropertyChanged(nameof(SourceIsIntel)); OnPropertyChanged(nameof(SourceIsGameLog));
        OnPropertyChanged(nameof(SourceHint));
    }

    partial void OnNewMatchChanged(string value)
    {
        OnPropertyChanged(nameof(MatchIsContains)); OnPropertyChanged(nameof(MatchIsRegex));
    }

    partial void OnNewSeverityChanged(string value)
    {
        OnPropertyChanged(nameof(SeverityIsInfo)); OnPropertyChanged(nameof(SeverityIsWarning));
        OnPropertyChanged(nameof(SeverityIsCritical));
    }

    [RelayCommand] private void SetNewSource(string s)   => NewSource   = s;
    [RelayCommand] private void SetNewMatch(string s)    => NewMatch    = s;
    [RelayCommand] private void SetNewSeverity(string s) => NewSeverity = s;

    [RelayCommand]
    private void StartCreate()
    {
        NewSource = "IntelChannel"; NewMatch = "Contains"; NewPattern = string.Empty;
        NewName = string.Empty; NewSeverity = "Warning"; NewSound = "Beep"; NewError = string.Empty;
        Step = 1;
        IsCreating = true;
    }

    [RelayCommand] private void CancelCreate() { IsCreating = false; NewError = string.Empty; }

    [RelayCommand]
    private void NextStep()
    {
        NewError = string.Empty;
        if (Step == 2)
        {
            var match = Enum.Parse<AlertRuleMatch>(NewMatch);
            if (string.IsNullOrWhiteSpace(NewPattern)) { NewError = "Enter the text to watch for."; return; }
            if (!CustomAlertService.IsValidPattern(match, NewPattern))
            {
                NewError = "That regex isn't valid. Check the brackets and escapes, or switch to \"contains\".";
                return;
            }
            if (string.IsNullOrWhiteSpace(NewName)) NewName = NewPattern.Trim();   // sensible default
        }
        if (Step < 3) Step++;
    }

    [RelayCommand] private void PrevStep() { NewError = string.Empty; if (Step > 1) Step--; }

    [RelayCommand]
    private void FinishCreate()
    {
        if (string.IsNullOrWhiteSpace(NewName)) { NewError = "Give the alert a short name - it's the tag in the feed."; return; }

        var rule = new CustomAlertRule
        {
            Name     = NewName.Trim(),
            Pattern  = NewPattern.Trim(),
            Source   = Enum.Parse<AlertRuleSource>(NewSource),
            Match    = Enum.Parse<AlertRuleMatch>(NewMatch),
            Severity = Enum.Parse<AlertSeverity>(NewSeverity),
            Sound    = NewSound,
        };
        _settings.Current.CustomAlerts.Add(rule);
        _settings.Save();
        var row = AlertConfigRow.ForRule(rule);
        row.SoundChoices = SoundChoices;
        Rows.Add(row);
        Track(row);
        BuildGroups();

        IsCreating = false;
        Status = $"Alert \"{rule.Name}\" added.";
    }
}
