using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// The Ping / MOTD tool. Shared dropdowns + fields drive two outputs: a copy-paste PING (for
/// Discord/comms) and a fleet MOTD that can be pushed to the live fleet via ESI. Profiles let
/// each alliance (INIT) keep its own autofill lists and captured clickable channel links, with a
/// Custom profile for people who don't use alliance channels.
/// </summary>
public partial class PingViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SystemSearchService _systemSearch;
    private readonly EsiService _esi;
    private readonly EsiAuthService _auth;
    private readonly ShellViewModel _shell;

    public PingViewModel(SettingsService settings, SystemSearchService systemSearch,
                         EsiService esi, EsiAuthService auth, ShellViewModel shell)
    {
        _settings = settings;
        _systemSearch = systemSearch;
        _esi = esi;
        _auth = auth;
        _shell = shell;

        SeedProfilesIfEmpty();
        ApplyInitSeed();
        foreach (var p in _settings.Current.PingProfiles) Profiles.Add(p);
        _selectedProfile = Profiles.FirstOrDefault(p => p.Name == _settings.Current.ActivePingProfile)
                           ?? Profiles.FirstOrDefault();

        _fcName     = auth.AuthenticatedCharacterName;
        _formupText = settings.Current.FormupSystem;
        _formupId   = settings.Current.FormupSystemId;

        // The FC is normally the main anchor — prefill it (still editable). We already know the
        // FC's own character id, so it links in the MOTD without an ESI lookup.
        _mainAnchor = auth.AuthenticatedCharacterName;
        if (auth.AuthenticatedCharacterId > 0)
            _charIds[auth.AuthenticatedCharacterName] = auth.AuthenticatedCharacterId;

        LoadProfileLists();
        _ = _systemSearch.EnsureLoadedAsync();
        _ = ApplyAllianceLockAsync();
    }

    /// <summary>
    /// Hides alliance-locked profiles (e.g. INIT) from anyone not in that alliance. Fails closed:
    /// if we can't read the character's alliance, locked profiles stay hidden.
    /// NOTE: this gates *selection*, not the seed data baked into the app binary.
    /// </summary>
    private async Task ApplyAllianceLockAsync()
    {
        var info = await _esi.GetCharacterPublicInfoAsync(_auth.AuthenticatedCharacterId);
        var myAlliance = info?.AllianceId ?? 0;

        foreach (var locked in Profiles.Where(p => p.AllianceId != 0 && p.AllianceId != myAlliance).ToList())
            Profiles.Remove(locked);

        if (SelectedProfile == null || !Profiles.Contains(SelectedProfile))
            SelectedProfile = Profiles.FirstOrDefault();
    }

    private void SeedProfilesIfEmpty()
    {
        if (_settings.Current.PingProfiles.Count > 0) return;
        _settings.Current.PingProfiles.Add(new PingProfile { Name = "INIT", Alliance = true, AllianceId = InitProfileSeed.AllianceId });
        _settings.Current.PingProfiles.Add(new PingProfile { Name = "Custom" });
        _settings.Current.ActivePingProfile = "INIT";
        _settings.Save();
    }

    /// <summary>Populates the INIT profile with canonical alliance data when its lists are empty
    /// (so existing installs get the seeded channel links without clobbering any edits).</summary>
    private void ApplyInitSeed()
    {
        var init = _settings.Current.PingProfiles.FirstOrDefault(p => p.Name == "INIT");
        if (init == null) return;

        init.Alliance   = true;
        init.AllianceId = InitProfileSeed.AllianceId;   // ensure the lock is set on existing installs

        // Channels: seed once if empty.
        if (init.BoostLinks.Count == 0) init.BoostLinks.AddRange(InitProfileSeed.BoostLinks());
        if (init.LogiLinks.Count  == 0) init.LogiLinks.AddRange(InitProfileSeed.LogiLinks());

        // Doctrines + comms are authoritative from the seed — replace each load so stray test/custom
        // entries never linger (custom typed values are not persisted anywhere).
        init.Doctrines     = InitProfileSeed.Doctrines();
        init.CommsChannels = InitProfileSeed.CommsChannels();

        _settings.Save();
    }

    // ── Profiles ──
    public ObservableCollection<PingProfile> Profiles { get; } = [];

    [ObservableProperty] private PingProfile? _selectedProfile;

    /// <summary>True for alliance profiles (INIT): shows the doctrine/implants/channel-link fields.
    /// Custom (personal) profiles hide those alliance-specific bits.</summary>
    public bool IsAlliance => SelectedProfile?.Alliance ?? false;

    partial void OnSelectedProfileChanged(PingProfile? value)
    {
        if (value == null) return;
        _settings.Current.ActivePingProfile = value.Name;
        _settings.Save();
        OnPropertyChanged(nameof(IsAlliance));
        LoadProfileLists();
        Refresh();
    }

    private void LoadProfileLists()
    {
        BoostLinks.Clear();
        LogiLinks.Clear();
        Doctrines.Clear();
        CommsOptions.Clear();
        foreach (var c in SelectedProfile?.BoostLinks    ?? []) BoostLinks.Add(c);
        foreach (var c in SelectedProfile?.LogiLinks     ?? []) LogiLinks.Add(c);
        foreach (var d in SelectedProfile?.Doctrines     ?? []) Doctrines.Add(d);
        foreach (var c in SelectedProfile?.CommsChannels ?? []) CommsOptions.Add(c);
        SelectedDoctrine = null;
    }

    // ── Input fields (every change re-renders both outputs) ──
    [ObservableProperty] private string _hurf = string.Empty;
    [ObservableProperty] private string _fcName = string.Empty;
    [ObservableProperty] private string _formupText = string.Empty;
    [ObservableProperty] private string _commsText = string.Empty;
    [ObservableProperty] private DoctrinePreset? _selectedDoctrine;
    [ObservableProperty] private string _ships = string.Empty;
    [ObservableProperty] private string _implantsText = "No";
    [ObservableProperty] private string _mainAnchor = string.Empty;
    [ObservableProperty] private string _logiAnchor = string.Empty;
    [ObservableProperty] private string _fittings = "Default";
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private CapturedChannel? _selectedBoost;
    [ObservableProperty] private CapturedChannel? _selectedLogi;
    [ObservableProperty] private string _statusMessage = string.Empty;

    private int  _formupId;
    private bool _suppressSearch;
    private const int CharacterTypeId   = 1373;                // EVE "Character" type — for showinfo char links
    private const int SolarSystemTypeId = 5;                   // EVE "Solar System" type — for showinfo system links
    private readonly Dictionary<string, int?> _charIds = new(StringComparer.OrdinalIgnoreCase);

    partial void OnHurfChanged(string value)         => Refresh();
    partial void OnFcNameChanged(string value)       => Refresh();
    partial void OnShipsChanged(string value)        => Refresh();
    partial void OnImplantsTextChanged(string value) => Refresh();
    partial void OnMainAnchorChanged(string value) { Refresh(); _ = ResolveAnchorAsync(value); }
    partial void OnLogiAnchorChanged(string value) { Refresh(); _ = ResolveAnchorAsync(value); }
    partial void OnFittingsChanged(string value)     => Refresh();
    partial void OnNotesChanged(string value)        => Refresh();
    partial void OnSelectedBoostChanged(CapturedChannel? value) => Refresh();
    partial void OnSelectedLogiChanged(CapturedChannel? value)  => Refresh();

    [RelayCommand] private void ToggleImplants() => ImplantsText = ImplantsText.Equals("No", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";

    // ── Form-up system (autocomplete, shared/persisted with Settings) ──
    public ObservableCollection<SystemMatch> SystemSuggestions { get; } = [];

    partial void OnFormupTextChanged(string value)
    {
        if (!_suppressSearch)
        {
            SystemSuggestions.Clear();
            foreach (var m in _systemSearch.Search(value)) SystemSuggestions.Add(m);
            _formupId = _systemSearch.ResolveId(value) ?? 0;
        }
        Refresh();
    }

    [RelayCommand]
    private void PickSystem(SystemMatch? match)
    {
        if (match == null) return;
        _suppressSearch = true;
        FormupText = match.Name;
        _formupId  = match.Id;
        _suppressSearch = false;
        SystemSuggestions.Clear();
        // Persist immediately so the straggler tracker uses the same form-up system.
        _settings.Current.FormupSystem   = match.Name;
        _settings.Current.FormupSystemId = match.Id;
        _settings.Save();
    }

    // ── Comms + Doctrine dropdowns (from the profile) ──
    public ObservableCollection<string>         CommsOptions { get; } = [];   // editable combo: pick or free-type
    public ObservableCollection<DoctrinePreset> Doctrines    { get; } = [];   // pick from the profile's doctrines

    partial void OnCommsTextChanged(string value) => Refresh();

    partial void OnSelectedDoctrineChanged(DoctrinePreset? value)
    {
        if (value != null) Ships = value.Ships;   // auto-fill the priority line; FC can tweak it
        Refresh();
    }

    // ── Character-link resolution for anchors (name → showinfo char link) ──
    private async Task ResolveAnchorAsync(string name)
    {
        name = name.Trim();
        if (name.Length < 3 || _charIds.ContainsKey(name)) return;
        await Task.Delay(400);                                   // debounce typing
        if (!string.Equals(MainAnchor.Trim(), name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(LogiAnchor.Trim(), name, StringComparison.OrdinalIgnoreCase)) return;
        _charIds[name] = await _esi.ResolveCharacterIdAsync(name);
        Refresh();
    }

    /// <summary>A clickable character link if the name resolved, else plain text.</summary>
    private string LinkChar(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return string.Empty;
        return _charIds.TryGetValue(name, out var id) && id is > 0
            ? $"<url=showinfo:{CharacterTypeId}//{id}>{name}</url>"
            : name;
    }

    /// <summary>A clickable solar-system link if we have its id, else plain text.</summary>
    private string LinkSystem(string name, int id)
    {
        name = name.Trim();
        if (name.Length == 0) return string.Empty;
        return id > 0 ? $"<url=showinfo:{SolarSystemTypeId}//{id}>{name}</url>" : name;
    }

    // ── Clickable channel links (seeded per profile, e.g. INIT) ──
    public ObservableCollection<CapturedChannel> BoostLinks { get; } = [];
    public ObservableCollection<CapturedChannel> LogiLinks  { get; } = [];

    // ── Outputs ──
    public string PingText => BuildPing();
    public string MotdText => BuildMotd();

    private void Refresh()
    {
        OnPropertyChanged(nameof(PingText));
        OnPropertyChanged(nameof(MotdText));
    }

    private string BuildPing()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(Hurf)) sb.AppendLine(Hurf.Trim()).AppendLine();
        sb.AppendLine($"FC: {FcName}");
        sb.AppendLine($"Forming: {FormupText}");
        sb.Append($"Comms: {CommsText}");
        if (IsAlliance) sb.Append($"\nDoctrine: {SelectedDoctrine?.Name}");   // doctrine is alliance-specific
        return sb.ToString();
    }

    private string BuildMotd()
    {
        const string div = "----------";
        var sb = new StringBuilder();

        // Alliance-specific block (doctrine/ships/implants + channel links) — omitted for Custom.
        if (IsAlliance)
        {
            var dName = SelectedDoctrine?.Name ?? string.Empty;
            var dUrl  = SelectedDoctrine?.FittingUrl ?? string.Empty;
            var doctrine = string.IsNullOrWhiteSpace(dUrl)
                ? dName
                : $"<url={dUrl.Trim()}>{dName}</url>";   // link the doctrine to its fitting page
            sb.AppendLine($"Doctrine: {doctrine}");
            sb.AppendLine($"Ships: {Ships}");
            sb.AppendLine($"Implants: {ImplantsText}");
            sb.AppendLine(div);
        }

        sb.AppendLine($"Main Anchor: {LinkChar(MainAnchor)}");
        sb.AppendLine($"Logi Anchor: {LinkChar(LogiAnchor)}");
        sb.AppendLine($"Forming: {LinkSystem(FormupText, _formupId)}");
        sb.AppendLine($"Comms: {CommsText}");
        sb.AppendLine(div);
        sb.AppendLine($"Fittings: {Fittings}");
        sb.Append($"Notes: {Notes}");

        if (IsAlliance)
        {
            sb.AppendLine();
            sb.AppendLine($"Logi: {SelectedLogi?.Markup ?? string.Empty}");
            sb.Append($"Boosts: {SelectedBoost?.Markup ?? string.Empty}");
        }
        return sb.ToString();
    }

    // ── Actions ──
    [RelayCommand]
    private void CopyPing()
    {
        try { System.Windows.Clipboard.SetText(PingText); StatusMessage = "Ping copied to clipboard."; }
        catch { StatusMessage = "Couldn't access the clipboard."; }
    }

    [RelayCommand]
    private void CopyMotd()
    {
        try { System.Windows.Clipboard.SetText(MotdText); StatusMessage = "MOTD copied to clipboard."; }
        catch { StatusMessage = "Couldn't access the clipboard."; }
    }

    [RelayCommand]
    private async Task SetMotdOnFleetAsync()
    {
        var charFleet = await _esi.GetCharacterFleetAsync(_auth.AuthenticatedCharacterId);
        if (charFleet == null) { StatusMessage = "Not in a fleet — can't set the MOTD."; return; }

        var info = await _esi.GetFleetInfoAsync(charFleet.FleetId);
        var ok = await _esi.SetFleetMotdAsync(charFleet.FleetId, MotdText, info?.IsFreeMove ?? false);
        StatusMessage = ok ? "MOTD set on the fleet." : "Failed — are you the fleet boss?";
    }

    [RelayCommand] private void BackToMenu() => _shell.BackToMenu();
}
