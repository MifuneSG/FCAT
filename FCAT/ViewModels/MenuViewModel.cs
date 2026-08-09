using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

public partial class MenuViewModel : ObservableObject
{
    private readonly EsiAuthService _auth;
    private readonly EsiService _esi;
    private readonly ShellViewModel _shell;

    public MenuViewModel(EsiAuthService auth, EsiService esi, ShellViewModel shell)
    {
        _auth = auth;
        _esi = esi;
        _shell = shell;

        CharacterName = auth.AuthenticatedCharacterName;
        PortraitUrl = $"https://images.evetech.net/characters/{auth.AuthenticatedCharacterId}/portrait?size=128";

        // Keep the LAST ALERT tile live while the FC sits on the dashboard.
        RecentAlerts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(LatestAlert));
            OnPropertyChanged(nameof(HasAlerts));
        };

        _ = LoadCharacterInfoAsync();
        _ = LoadLocationAsync();
        _ = CheckFleetStatusAsync();
        _ = LoadAltsAsync();
    }

    [ObservableProperty] private string _characterName = string.Empty;
    [ObservableProperty] private string _corporationName = string.Empty;
    [ObservableProperty] private string _corporationTicker = string.Empty;
    [ObservableProperty] private string _allianceName = string.Empty;
    [ObservableProperty] private string _allianceTicker = string.Empty;
    [ObservableProperty] private string _portraitUrl = string.Empty;

    [ObservableProperty] private bool _isCheckingFleet = true;
    [ObservableProperty] private bool _isInFleet;
    [ObservableProperty] private long _detectedFleetId;
    [ObservableProperty] private string _fleetStatusText = "Checking fleet status...";

    // Command Center display
    [ObservableProperty] private string _securityStatusText = "—";
    [ObservableProperty] private string _currentSystemName = "Unknown";

    /// <summary>You're in a fleet but not its boss - ESI won't share any fleet data with FCAT.</summary>
    [ObservableProperty] private bool _isFleetBoss;
    public bool IsInFleetNotBoss => !IsFleetBoss && FleetStatusText.StartsWith("In a fleet");

    partial void OnIsFleetBossChanged(bool value)      => OnPropertyChanged(nameof(IsInFleetNotBoss));
    partial void OnFleetStatusTextChanged(string value) => OnPropertyChanged(nameof(IsInFleetNotBoss));

    // Fleet preview (only meaningful when IsInFleet)
    [ObservableProperty] private int    _pilotCount;
    [ObservableProperty] private string _fleetCompositionText = string.Empty;  // "3 wings · 5 squads"
    [ObservableProperty] private string _ownRoleText = string.Empty;           // "BOSS" / "WING CMDR" …
    [ObservableProperty] private string _formingText = string.Empty;           // "forming J5A-IX"

    // READINESS tile (fleet composition health; only when IsInFleet)
    [ObservableProperty] private string _logiText = "—";       // "17%"
    [ObservableProperty] private bool   _logiLow;              // <7% -> red (matches the combat-fleet advisory)
    [ObservableProperty] private string _tackleText = "—";
    [ObservableProperty] private bool   _hasTackle;
    [ObservableProperty] private string _boosterText = "—";
    [ObservableProperty] private bool   _hasBoosters;

    // THREAT tile (current-system activity from ESI)
    private int _currentSystemId;
    [ObservableProperty] private string _threatKillsText = "No recent kills";
    [ObservableProperty] private string _threatActivityText = "quiet";
    [ObservableProperty] private bool   _threatIsHot;

    // LAST ALERT tile
    public ObservableCollection<FcAlert> RecentAlerts => _shell.Hub.Alerts;
    public FcAlert? LatestAlert => RecentAlerts.Count > 0 ? RecentAlerts[0] : null;  // hub inserts newest at 0
    public bool HasAlerts => RecentAlerts.Count > 0;

    // FLEET ALTS card  where the FC's alts (scout, cyno, hauler, …) are sitting for the op.
    public ObservableCollection<CharacterRow> Alts { get; } = [];
    public bool HasAlts => Alts.Count > 0;

    /// <summary>First name only - for the "Good hunting, X." greeting.</summary>
    public string FirstName =>
        string.IsNullOrWhiteSpace(CharacterName) ? "Capsuleer" : CharacterName.Split(' ')[0];

    partial void OnCharacterNameChanged(string value) => OnPropertyChanged(nameof(FirstName));

    // Subtitle shown under character name: corp [ticker] · alliance or just corp [ticker]
    public string CharacterSubtitle =>
        string.IsNullOrEmpty(AllianceTicker)
            ? (string.IsNullOrEmpty(CorporationName) ? "CAPSULEER" : $"{CorporationName}  [{CorporationTicker}]")
            : $"{CorporationName}  [{CorporationTicker}]  ·  {AllianceName}  [{AllianceTicker}]";

    private async Task LoadCharacterInfoAsync()
    {
        var charInfo = await _esi.GetCharacterPublicInfoAsync(_auth.AuthenticatedCharacterId);
        if (charInfo == null) return;

        var corpInfo = await _esi.GetCorporationPublicInfoAsync(charInfo.CorporationId);
        if (corpInfo != null)
        {
            CorporationName = corpInfo.Name;
            CorporationTicker = corpInfo.Ticker;
        }

        if (charInfo.AllianceId.HasValue && charInfo.AllianceId.Value > 0)
        {
            var alliInfo = await _esi.GetAlliancePublicInfoAsync(charInfo.AllianceId.Value);
            if (alliInfo != null)
            {
                AllianceName = alliInfo.Name;
                AllianceTicker = alliInfo.Ticker;
            }
        }

        // Sec status comes from the same public character endpoint.
        SecurityStatusText = (charInfo.SecurityStatus >= 0 ? "+" : "") + charInfo.SecurityStatus.ToString("0.0");

        OnPropertyChanged(nameof(CharacterSubtitle));
    }

    // Current system - esi-location scope (held). Replaces the jump-clone stat, which would
    // need esi-clones (not in our scope set).
    private async Task LoadLocationAsync()
    {
        var loc = await _esi.GetCharacterLocationAsync(_auth.AuthenticatedCharacterId);
        if (loc == null) return;
        _currentSystemId = loc.SolarSystemId;
        var sys = await _esi.GetSystemAsync(loc.SolarSystemId);
        if (sys != null) CurrentSystemName = sys.Name;
        await LoadThreatAsync();
    }

    // THREAT tile: current-system kills + jumps. ESI's system_kills updates ~hourly, so this is
    // a slow-moving "is anything happening here" readout, not a live tracker. "Hot" matches the
    // Intel page heuristic: ≥2 ship/pod kills or ≥40 jumps.
    private async Task LoadThreatAsync()
    {
        if (_currentSystemId == 0) return;
        var kills = await _esi.GetSystemKillsAsync();
        var jumps = await _esi.GetSystemJumpsAsync();

        var k = kills.FirstOrDefault(x => x.SystemId == _currentSystemId);
        var j = jumps.FirstOrDefault(x => x.SystemId == _currentSystemId);
        int shipPod = (k?.ShipKills ?? 0) + (k?.PodKills ?? 0);
        int npc     = k?.NpcKills ?? 0;
        int jmp     = j?.ShipJumps ?? 0;

        ThreatKillsText = shipPod > 0
            ? $"{shipPod} kill{(shipPod == 1 ? "" : "s")} (1h)"
            : "No recent kills";
        ThreatIsHot = shipPod >= 2 || jmp >= 40;
        ThreatActivityText = shipPod >= 2 ? "HOT" : npc > 0 ? $"NPC {npc}" : jmp >= 40 ? $"{jmp} jumps" : "quiet";
    }

    partial void OnCorporationNameChanged(string value)  => OnPropertyChanged(nameof(CharacterSubtitle));
    partial void OnAllianceNameChanged(string value)     => OnPropertyChanged(nameof(CharacterSubtitle));
    partial void OnAllianceTickerChanged(string value)   => OnPropertyChanged(nameof(CharacterSubtitle));

    private async Task CheckFleetStatusAsync()
    {
        IsCheckingFleet = true;
        var charFleet = await _esi.GetCharacterFleetAsync(_auth.AuthenticatedCharacterId);

        if (charFleet != null)
        {
            // EVE only serves fleet members/wings to the fleet BOSS. Wing/squad commanders - and
            // even an FC who didn't create the fleet - get a 404, so say so instead of showing an
            // empty fleet that looks like a broken tool.
            IsFleetBoss = charFleet.FleetBossId == _auth.AuthenticatedCharacterId;
            IsInFleet = IsFleetBoss;
            DetectedFleetId = IsFleetBoss ? charFleet.FleetId : 0;

            if (IsFleetBoss)
            {
                FleetStatusText = "Active fleet detected";
                await LoadFleetSummaryAsync(charFleet.FleetId);
            }
            else
            {
                FleetStatusText = "In a fleet, but you're not fleet boss.";
            }
        }
        else
        {
            IsFleetBoss = false;
            IsInFleet = false;
            FleetStatusText = "No active fleet found.";
        }

        // Let the shell (nav rail) know what fleet is enterable.
        _shell.DetectedFleetId = IsInFleet ? DetectedFleetId : 0;

        IsCheckingFleet = false;
    }

    // Lightweight fleet preview for the dashboard card: head-count, wing/squad spread,
    // the FC's own role, and where they're sitting (staging).
    private async Task LoadFleetSummaryAsync(long fleetId)
    {
        var members = await _esi.GetFleetMembersAsync(fleetId);
        var wings   = await _esi.GetFleetWingsAsync(fleetId);

        PilotCount = members?.Count ?? 0;

        if (wings != null)
        {
            int wingCount = wings.Count;
            int squadCount = wings.Sum(w => w.Squads.Count);
            FleetCompositionText = $"{wingCount} wing{(wingCount == 1 ? "" : "s")} · {squadCount} squad{(squadCount == 1 ? "" : "s")}";
        }

        var me = members?.FirstOrDefault(m => m.CharacterId == _auth.AuthenticatedCharacterId);
        OwnRoleText = me?.Role switch
        {
            "fleet_commander" => "BOSS",
            "wing_commander"  => "WING CMDR",
            "squad_commander" => "SQUAD CMDR",
            "squad_member"    => "MEMBER",
            _                 => "—"
        };

        FormingText = string.IsNullOrEmpty(CurrentSystemName) || CurrentSystemName == "Unknown"
            ? "" : $"staging {CurrentSystemName}";

        await LoadReadinessAsync(members);
    }

    // READINESS tile: classify each hull (one batched group-id lookup) into logi / tackle / boosters.
    // Same classifier the fleet view uses, so the dashboard agrees with the detailed page.
    private async Task LoadReadinessAsync(List<FleetMember>? members)
    {
        if (members == null || members.Count == 0) return;

        var groupIds = await _esi.GetShipGroupIdsAsync(members.Select(m => m.ShipTypeId).Distinct());
        int logi = 0, tackle = 0, boosters = 0;
        foreach (var m in members)
        {
            var role = ShipRoleClassifier.Classify(m.ShipTypeId, groupIds.GetValueOrDefault(m.ShipTypeId));
            if (role is ShipRole.Logi or ShipRole.CapLogi) logi++;
            else if (role is ShipRole.Tackle or ShipRole.Bubble) tackle++;
            else if (role is ShipRole.Booster) boosters++;
        }

        int total = members.Count;
        int pct = total > 0 ? (int)Math.Round(100.0 * logi / total) : 0;
        LogiText = total > 0 ? $"{pct}%" : "—";
        LogiLow  = total > 0 && pct < 7;          // mirrors the "Low logi <7%" combat-fleet advisory
        HasTackle   = tackle > 0;
        TackleText  = tackle > 0 ? tackle.ToString() : "none";
        HasBoosters = boosters > 0;
        BoosterText = boosters > 0 ? boosters.ToString() : "none";
    }

    // Dashboard card: each alt (account store minus the active char) polled with its own token.
    private async Task LoadAltsAsync()
    {
        if (_esi.DemoMode)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Alts.Clear();
                foreach (var a in DemoData.Alts())        // synthetic alts - the store is empty in demo
                    Alts.Add(new CharacterRow
                    {
                        CharacterId = a.CharacterId,
                        Name        = a.Name,
                        PortraitUrl = $"https://images.evetech.net/characters/{a.CharacterId}/portrait?size=64",
                        Role        = a.Role,
                        Online      = true,
                        SystemName  = a.SystemName,
                        ShipName    = a.ShipName,
                        DockText    = a.DockText,
                        FleetText   = a.FleetText,
                    });
                OnPropertyChanged(nameof(HasAlts));
            });
            return;
        }

        var alts = _auth.Store.Characters
            .Where(c => c.CharacterId != _auth.AuthenticatedCharacterId)
            .ToList();

        // Seed the rows first so names/roles render while live statuses stream in.
        App.Current.Dispatcher.Invoke(() =>
        {
            Alts.Clear();
            foreach (var c in alts)
                Alts.Add(new CharacterRow
                {
                    CharacterId = c.CharacterId,
                    Name        = c.CharacterName,
                    Role        = c.Role,
                    PortraitUrl = $"https://images.evetech.net/characters/{c.CharacterId}/portrait?size=64",
                });
            OnPropertyChanged(nameof(HasAlts));
        });
        if (alts.Count == 0) return;

        var raw = new List<(int id, bool online, int sysId, int shipId, int stationId, string structName, long fleetId)>();
        foreach (var c in alts)
        {
            var online = await _esi.GetCharacterOnlineAsync(c.CharacterId);
            int sysId = 0, shipId = 0, stationId = 0; string structName = string.Empty; long fleetId = 0;
            if (online?.Online == true)
            {
                var loc   = await _esi.GetCharacterLocationAsync(c.CharacterId);
                var ship  = await _esi.GetCharacterShipAsync(c.CharacterId);
                var fleet = await _esi.GetCharacterFleetAsync(c.CharacterId);
                sysId = loc?.SolarSystemId ?? 0; shipId = ship?.ShipTypeId ?? 0;
                stationId = loc?.StationId ?? 0; fleetId = fleet?.FleetId ?? 0;
                if (loc?.StructureId is long sid)
                    structName = (await _esi.GetStructureNameAsync(sid, c.CharacterId))?.Name ?? "structure";
            }
            raw.Add((c.CharacterId, online?.Online ?? false, sysId, shipId, stationId, structName, fleetId));
        }

        var ids = raw.SelectMany(r => new[] { r.sysId, r.shipId, r.stationId }).Where(i => i > 0).Distinct().ToList();
        var names = ids.Count > 0 ? await _esi.ResolveNamesAsync(ids) : [];

        App.Current.Dispatcher.Invoke(() =>
        {
            foreach (var r in raw)
            {
                var row = Alts.FirstOrDefault(a => a.CharacterId == r.id);
                if (row == null) continue;
                row.Online     = r.online;
                row.SystemName = r.sysId  > 0 && names.TryGetValue(r.sysId,  out var s)  ? s  : (r.online ? "?" : "—");
                row.ShipName   = r.shipId > 0 && names.TryGetValue(r.shipId, out var sh) ? sh : (r.online ? "?" : "—");
                row.DockText =
                    !r.online                           ? "Offline" :
                    r.stationId > 0                     ? "⚓ " + (names.TryGetValue(r.stationId, out var st) ? st : "docked") :
                    !string.IsNullOrEmpty(r.structName) ? "⚓ " + r.structName :
                                                          "In space";
                row.FleetText =
                    r.fleetId == 0                                        ? string.Empty :
                    DetectedFleetId != 0 && r.fleetId == DetectedFleetId  ? "In your fleet" :
                                                                            "In another fleet";
            }
        });
    }

    [RelayCommand]
    private async Task RefreshFleetStatusAsync()
    {
        await CheckFleetStatusAsync();
        await LoadAltsAsync();
    }

    [RelayCommand]
    private void OpenSettings() => _shell.ShowSettings();

    [RelayCommand]
    private void OpenAccount() => _shell.ShowAccount();

    [RelayCommand]
    private void OpenDScan() => _shell.ShowIntel();

    [RelayCommand]
    private void OpenSessionLog() => _shell.ShowSessionLog();

    [RelayCommand]
    private void OpenPing() => _shell.ShowPing();

    [RelayCommand(CanExecute = nameof(CanEnterFleet))]
    private void EnterFleet() => _shell.ShowFleet(DetectedFleetId);

    private bool CanEnterFleet() => IsInFleet && DetectedFleetId != 0;

    partial void OnIsInFleetChanged(bool value)       => EnterFleetCommand.NotifyCanExecuteChanged();
    partial void OnDetectedFleetIdChanged(long value) => EnterFleetCommand.NotifyCanExecuteChanged();
}
