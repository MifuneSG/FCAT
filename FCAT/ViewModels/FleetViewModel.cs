using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

public partial class FleetViewModel : ObservableObject
{
    private readonly EsiAuthService  _auth;
    private readonly EsiService      _esi;
    private readonly CombatLogService _combatLog;
    private readonly SettingsService _settings;
    private readonly ShellViewModel  _shell;

    private CancellationTokenSource? _pollCts;
    // Serializes fleet-data loads so a 5s poll and a manual refresh (kick/move/invite) can't
    // run concurrently and race the shared caches / collections.
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // Persistent name cache — typeId/charId/sysId → display name, survives poll ticks
    private readonly Dictionary<int, string> _nameCache    = [];

    // Persistent ship role cache — typeId → group_id from ESI
    private readonly Dictionary<int, int>    _groupIdCache = [];

    // Boost channel reader + per-pilot pod-state tracking for "boost lost" detection
    private readonly BoostChannelService _boost = new();
    private readonly Dictionary<int, bool> _inCapsule = [];
    private readonly List<FleetMemberViewModel> _currentMembers = [];

    public long SessionFleetId { get; }

    public FleetViewModel(EsiAuthService auth, EsiService esi, CombatLogService combatLog,
                          SettingsService settings, ShellViewModel shell, long fleetId)
    {
        _auth      = auth;
        _esi       = esi;
        _combatLog = combatLog;
        _settings  = settings;
        _shell     = shell;

        SessionFleetId = fleetId;
        CharacterName  = auth.AuthenticatedCharacterName;
        FleetIdDisplay = fleetId.ToString();

        _combatLog.AlertRaised += OnAlertRaised;
        _combatLog.StartWatching(_settings.Current.GamelogsPath);

        _boost.Updated += OnBoostUpdated;
        _boost.StartWatching(_settings.Current.ChatlogsPath, _settings.Current.BoostChannelPrefix);
        BoostChannelName = _boost.ActiveChannel ?? "not found";

        OverlayLocked = _settings.Current.OverlayLocked;

        StartPolling();
    }

    [ObservableProperty] private string _characterName  = string.Empty;
    [ObservableProperty] private string _fleetIdDisplay = string.Empty;
    [ObservableProperty] private int    _memberCount;
    [ObservableProperty] private bool   _isLive;
    [ObservableProperty] private string _statusMessage  = "Connecting...";
    [ObservableProperty] private bool   _fleetChangedWarning;
    [ObservableProperty] private long   _newDetectedFleetId;
    [ObservableProperty] private string _boostChannelName = string.Empty;
    [ObservableProperty] private string _inviteName = string.Empty;
    [ObservableProperty] private string _mainlineLabel = string.Empty;

    // Alert overlay (managed by the view's code-behind, which owns the actual Window)
    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private bool _overlayLocked;
    public double OverlayLeft => _settings.Current.OverlayLeft;
    public double OverlayTop  => _settings.Current.OverlayTop;

    [RelayCommand] private void ToggleOverlay()     => OverlayEnabled = !OverlayEnabled;
    [RelayCommand] private void ToggleOverlayLock()  => OverlayLocked  = !OverlayLocked;

    /// <summary>Called by the view when the overlay moves / locks, so position survives sessions.</summary>
    public void PersistOverlay(double left, double top, bool locked)
    {
        _settings.Current.OverlayLeft   = left;
        _settings.Current.OverlayTop    = top;
        _settings.Current.OverlayLocked = locked;
        _settings.Save();
    }

    // Move picker state
    [ObservableProperty] private bool _isMovePickerOpen;
    [ObservableProperty] private string _movePilotName = string.Empty;
    private FleetMemberViewModel? _moveCandidate;

    // Rename overlay state
    [ObservableProperty] private bool _isRenameOpen;
    [ObservableProperty] private string _renameTitle = string.Empty;
    [ObservableProperty] private string _renameText = string.Empty;

    public ObservableCollection<WingViewModel>        Wings            { get; } = [];
    public ObservableCollection<FleetMemberViewModel> FleetCommandLevel { get; } = [];
    public ObservableCollection<FcAlert>              Alerts           { get; } = [];
    public ObservableCollection<MoveTargetViewModel>  MoveTargets      { get; } = [];
    public ObservableCollection<FleetStat>            RoleStats        { get; } = [];
    public ObservableCollection<BoostStat>            BoostStats       { get; } = [];
    public ObservableCollection<FleetAdvisory>        Advisories       { get; } = [];

    // ── Polling ───────────────────────────────────────────────────────────────
    private void StartPolling()
    {
        _pollCts = new CancellationTokenSource();
        IsLive = true;
        _ = PollLoopAsync(_pollCts.Token);
    }

    /// <summary>
    /// Single self-paced poll loop: the next tick only fires once the previous poll completes,
    /// so slow ESI calls can never pile up overlapping polls. Survives transient errors.
    /// </summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        await SafePollAsync();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(ct))
                await SafePollAsync();
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task SafePollAsync()
    {
        try { await PollAsync(); }
        catch (Exception ex)
        {
            await App.Current.Dispatcher.InvokeAsync(() => StatusMessage = $"Poll error — retrying ({ex.Message})");
        }
    }

    public void Shutdown()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _combatLog.StopWatching();
        _combatLog.AlertRaised -= OnAlertRaised;
        _boost.Updated -= OnBoostUpdated;
        _boost.StopWatching();
        OverlayEnabled = false;   // triggers the view to close the overlay window
        IsLive = false;
    }

    private async Task PollAsync()
    {
        var charFleet = await _esi.GetCharacterFleetAsync(_auth.AuthenticatedCharacterId);
        if (charFleet == null)
        {
            await App.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Not in a fleet.");
            return;
        }

        if (charFleet.FleetId != SessionFleetId)
        {
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                NewDetectedFleetId = charFleet.FleetId;
                FleetChangedWarning = true;
                StatusMessage = $"New fleet detected: {charFleet.FleetId}";
            });
            return;
        }

        await LoadFleetDataAsync();
    }

    private async Task LoadFleetDataAsync()
    {
        // Only one load at a time (poll vs. manual refresh) to keep the caches/collections safe.
        await _loadGate.WaitAsync();
        try
        {
            await LoadFleetDataCoreAsync();
        }
        finally { _loadGate.Release(); }
    }

    private async Task LoadFleetDataCoreAsync()
    {
        var membersTask = _esi.GetFleetMembersAsync(SessionFleetId);
        var wingsTask   = _esi.GetFleetWingsAsync(SessionFleetId);
        await Task.WhenAll(membersTask, wingsTask);

        var members = membersTask.Result;
        var wings   = wingsTask.Result;
        if (members == null) return;

        // Re-read the boost channel each tick (FileSystemWatcher is unreliable on open files).
        _boost.Refresh();

        // ── 1. Resolve uncached names (character / ship / system) ─────────────
        var uncachedNameIds = members
            .SelectMany(m => new[] { m.CharacterId, m.ShipTypeId, m.SolarSystemId })
            .Where(id => id > 0 && !_nameCache.ContainsKey(id))
            .Distinct()
            .ToList();

        if (uncachedNameIds.Count > 0)
        {
            var resolved = await _esi.ResolveNamesAsync(uncachedNameIds);
            foreach (var (id, name) in resolved)
                _nameCache[id] = name;
        }

        // ── 2. Fetch group_ids for any ship types we haven't seen yet ─────────
        var uncachedTypeIds = members
            .Select(m => m.ShipTypeId)
            .Where(id => id > 0 && !_groupIdCache.ContainsKey(id))
            .Distinct()
            .ToList();

        if (uncachedTypeIds.Count > 0)
        {
            var groups = await _esi.GetShipGroupIdsAsync(uncachedTypeIds);
            foreach (var (typeId, groupId) in groups)
                _groupIdCache[typeId] = groupId;
        }

        // ── 3. Apply cached names to every member every tick ──────────────────
        foreach (var m in members)
        {
            if (_nameCache.TryGetValue(m.CharacterId,  out var cn))  m.CharacterName   = cn;
            if (_nameCache.TryGetValue(m.ShipTypeId,   out var sn))  m.ShipTypeName    = sn;
            if (_nameCache.TryGetValue(m.SolarSystemId,out var sys)) m.SolarSystemName = sys;
        }

        // ── 4. Death detection: a booster that flipped to a pod lost their links ──
        var deathAlerts = new List<FcAlert>();
        foreach (var m in members)
        {
            var isCapsule = _groupIdCache.TryGetValue(m.ShipTypeId, out var g) && ShipRoleClassifier.IsCapsule(g);
            var wasCapsule = _inCapsule.TryGetValue(m.CharacterId, out var prev) && prev;

            if (isCapsule && !wasCapsule)
            {
                var loadout = _boost.GetLoadout(m.CharacterName);
                if (loadout.Count > 0)
                {
                    var cats = string.Join(" · ", loadout.Select(c => c.Category).Distinct());
                    deathAlerts.Add(new FcAlert
                    {
                        Timestamp = DateTime.Now,
                        AlertType = AlertType.BoostLost,
                        Detail    = $"{m.CharacterName} podded — {cats} links down"
                    });
                    _boost.ClearPilot(m.CharacterName);   // links are gone with the ship
                }
            }
            _inCapsule[m.CharacterId] = isCapsule;
        }

        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var a in deathAlerts) RaiseAlert(a);
            RebuildHierarchy(members, wings ?? []);
        });
    }

    private void RebuildHierarchy(List<FleetMember> members, List<FleetWing> wings)
    {
        MemberCount = members.Count;
        Wings.Clear();
        FleetCommandLevel.Clear();
        _currentMembers.Clear();

        foreach (var wing in wings)
            Wings.Add(new WingViewModel(wing));

        // Squad commanders first so each squad lead sits at the top of its squad.
        foreach (var member in members.OrderBy(m => m.Role == "squad_commander" ? 0 : 1))
        {
            // FC command level (WingId < 0) is the boss running the tool — no self-kick/move.
            var manageable = member.WingId >= 0;
            var kickHandler = manageable ? KickMemberAsync : (Func<FleetMemberViewModel, Task>?)null;
            var moveHandler = manageable ? BeginMove : (Action<FleetMemberViewModel>?)null;
            var vm = new FleetMemberViewModel(member, kickHandler, moveHandler);

            // Set ship role from group_id cache
            if (_groupIdCache.TryGetValue(member.ShipTypeId, out var groupId))
            {
                vm.ShipRole     = ShipRoleClassifier.Classify(groupId);
                vm.IsInCapsule  = ShipRoleClassifier.IsCapsule(groupId);
            }

            ApplyBoost(vm);
            _currentMembers.Add(vm);

            // WingId < 0  →  fleet command level (FC / fleet boss)
            if (member.WingId < 0)
            {
                FleetCommandLevel.Add(vm);
                continue;
            }

            var wingVm = Wings.FirstOrDefault(w => w.WingId == member.WingId)
                         ?? CreateWing(member.WingId);

            // SquadId < 0  →  wing commander (at wing level, not in any squad)
            if (member.SquadId < 0)
            {
                wingVm.WingCommanders.Add(vm);
                continue;
            }

            var squadVm = wingVm.Squads.FirstOrDefault(s => s.SquadId == member.SquadId)
                          ?? CreateSquad(wingVm, member.SquadId);
            squadVm.Members.Add(vm);
        }

        ComputeStats();
        StatusMessage = $"{MemberCount} pilots";
    }

    // ── Composition + boost coverage header ─────────────────────────────────────
    private static readonly Brush BrLogi   = Frozen(0x3f, 0xae, 0x8f);
    private static readonly Brush BrBoost  = Frozen(0x5a, 0x8f, 0xd6);
    private static readonly Brush BrTackle = Frozen(0xd4, 0x6a, 0x6a);
    private static readonly Brush BrEwar    = Frozen(0x9b, 0x7b, 0xd4);
    private static readonly Brush BrSupport = Frozen(0x4d, 0xb8, 0xd4);
    private static readonly Brush BrCap     = Frozen(0xd4, 0xa4, 0x49);
    private static readonly Brush BrIndy   = Frozen(0x6b, 0x76, 0x89);
    private static readonly Brush BrDps    = Frozen(0x9a, 0xa3, 0xb3);
    private static readonly Brush BrAccent   = Frozen(0x4d, 0xb8, 0xd4);
    private static readonly Brush BrDim      = Frozen(0x5c, 0x64, 0x73);
    private static readonly Brush BrCritical = Frozen(0xe2, 0x57, 0x4c);
    private static readonly Brush BrAmber    = Frozen(0xc9, 0x88, 0x3e);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private enum FleetKind { Combat, Mining, Capital }

    private void ComputeStats()
    {
        var total = _currentMembers.Count;
        RoleStats.Clear();
        BoostStats.Clear();
        Advisories.Clear();
        MainlineLabel = string.Empty;
        if (total == 0) return;

        // ── Dominant hull + fleet kind ───────────────────────────────────────
        var dominant      = _currentMembers.GroupBy(m => m.ShipTypeId)
                                            .OrderByDescending(g => g.Count()).First();
        var dominantShare = (double)dominant.Count() / total;
        var dominantName  = dominant.First().ShipTypeName;
        var dominantRole  = dominant.First().ShipRole;
        var dominantType  = dominant.Key;

        var miningShare = (double)_currentMembers.Count(m => m.ShipRole is ShipRole.Mining or ShipRole.Industrial) / total;
        var capShare    = (double)_currentMembers.Count(m => m.ShipRole is ShipRole.Titan or ShipRole.Supercarrier or ShipRole.CapDPS or ShipRole.CapLogi) / total;

        var kind = miningShare >= 0.4 ? FleetKind.Mining
                 : capShare    >= 0.4 ? FleetKind.Capital
                 : FleetKind.Combat;

        // Mainline override: in a combat fleet, if one non-DPS hull is the clear body of the
        // fleet (e.g. a Nighthawk doctrine — a Command Ship used as mainline DPS), count those
        // pilots as DPS rather than as boosters/ewar/etc.
        var overrideMainline = kind == FleetKind.Combat && dominantShare >= 0.6 && dominantRole != ShipRole.DPS;

        ShipRole EffectiveRole(FleetMemberViewModel m)
            => overrideMainline && m.ShipTypeId == dominantType ? ShipRole.DPS : m.ShipRole;

        // ── Role tally (using effective roles) ───────────────────────────────
        void AddRole(string label, Brush color, Func<ShipRole, bool> match)
        {
            var n = _currentMembers.Count(m => match(EffectiveRole(m)));
            if (n > 0) RoleStats.Add(new FleetStat(label, n, color));
        }
        AddRole("LOGI",   BrLogi,   r => r is ShipRole.Logi or ShipRole.CapLogi);
        AddRole("BOOST",  BrBoost,  r => r is ShipRole.Booster);
        AddRole("TACKLE", BrTackle,  r => r is ShipRole.Tackle or ShipRole.Bubble);
        AddRole("EWAR",   BrEwar,    r => r is ShipRole.EWAR);
        AddRole("SUPP",   BrSupport, r => r is ShipRole.Support);
        AddRole("CAP",    BrCap,     r => r is ShipRole.Titan or ShipRole.Supercarrier or ShipRole.CapDPS);
        AddRole("INDY",   BrIndy,    r => r is ShipRole.Industrial or ShipRole.Mining);
        AddRole("DPS",    BrDps,     r => r is ShipRole.DPS or ShipRole.Unknown);

        // ── Boost coverage ───────────────────────────────────────────────────
        void AddBoost(string label, BoostCategory cat, Brush color)
        {
            var n = _currentMembers.Count(m => m.BoostCategories.Contains(cat));
            BoostStats.Add(new BoostStat(label, n, n > 0 ? color : BrDim));
        }
        AddBoost("Shield",   BoostCategory.Shield,   BrLogi);
        AddBoost("Armor",    BoostCategory.Armor,    BrCap);
        AddBoost("Skirmish", BoostCategory.Skirmish, BrAccent);
        AddBoost("Info",     BoostCategory.Info,     BrEwar);

        // ── Mainline / fleet-kind label ──────────────────────────────────────
        var sharePct = (int)Math.Round(dominantShare * 100);
        MainlineLabel = kind switch
        {
            FleetKind.Mining  => "Mining fleet",
            FleetKind.Capital => "Capital fleet",
            _ when dominantShare >= 0.5 => $"Mainline: {dominantName} ({sharePct}%)",
            _ => string.Empty
        };

        // ── Composition advisories (combat fleets only; ratios need a real fleet) ──
        if (kind != FleetKind.Combat || total < 8) return;

        var logi   = _currentMembers.Count(m => EffectiveRole(m) is ShipRole.Logi or ShipRole.CapLogi);
        var tackle = _currentMembers.Count(m => EffectiveRole(m) is ShipRole.Tackle or ShipRole.Bubble);
        var logiPct = (double)logi / total;

        if (logi == 0)
            Advisories.Add(new FleetAdvisory("No logistics", BrCritical));
        else if (logiPct < 0.07)
            Advisories.Add(new FleetAdvisory($"Low logi {(int)Math.Round(logiPct * 100)}% (~10% ideal)", BrAmber));

        if (tackle == 0)
            Advisories.Add(new FleetAdvisory("No tackle / interdiction", BrAmber));
    }

    private WingViewModel CreateWing(long id)
    {
        var w = new WingViewModel(id, $"Wing {Wings.Count + 1}");
        Wings.Add(w);
        return w;
    }

    private static SquadViewModel CreateSquad(WingViewModel wing, long id)
    {
        var s = new SquadViewModel(id, $"Squad {wing.Squads.Count + 1}");
        wing.Squads.Add(s);
        return s;
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    [RelayCommand]
    private void SwitchToNewFleet()
    {
        Shutdown();
        _shell.ShowFleet(NewDetectedFleetId);
    }

    [RelayCommand]
    private void BackToMenu() => _shell.BackToMenu();

    /// <summary>
    /// Confirms, then removes a pilot via ESI. Requires the logged-in character to be
    /// fleet boss — ESI rejects the call otherwise, which we surface as a status message.
    /// </summary>
    private async Task KickMemberAsync(FleetMemberViewModel member)
    {
        var name = string.IsNullOrEmpty(member.CharacterName) ? "this pilot" : member.CharacterName;

        var confirm = System.Windows.MessageBox.Show(
            $"Remove {name} from the fleet?",
            "Kick pilot",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        StatusMessage = $"Removing {name}…";
        var ok = await _esi.KickFleetMemberAsync(SessionFleetId, member.CharacterId);

        if (ok)
        {
            StatusMessage = $"Removed {name}";
            await LoadFleetDataAsync();   // refresh immediately rather than wait for the next tick
        }
        else
        {
            StatusMessage = "Kick failed — are you the fleet boss?";
        }
    }

    // ── Move pilot ──────────────────────────────────────────────────────────────
    /// <summary>Opens the move picker, listing every role/position the pilot can move to.</summary>
    private void BeginMove(FleetMemberViewModel member)
    {
        _moveCandidate = member;
        MovePilotName  = member.CharacterName;

        MoveTargets.Clear();
        MoveTargets.Add(new MoveTargetViewModel("Promote to Fleet Commander", "fleet_commander", null, null));
        foreach (var wing in Wings)
        {
            MoveTargets.Add(new MoveTargetViewModel($"{wing.Name}  —  Wing Commander", "wing_commander", wing.WingId, null));
            foreach (var squad in wing.Squads)
            {
                MoveTargets.Add(new MoveTargetViewModel($"{wing.Name}  ›  {squad.Name}", "squad_member", wing.WingId, squad.SquadId));
                MoveTargets.Add(new MoveTargetViewModel($"{wing.Name}  ›  {squad.Name}  —  Commander", "squad_commander", wing.WingId, squad.SquadId));
            }
        }

        IsMovePickerOpen = true;
    }

    [RelayCommand]
    private void CancelMove()
    {
        IsMovePickerOpen = false;
        _moveCandidate = null;
    }

    [RelayCommand]
    private async Task MoveTo(MoveTargetViewModel target)
    {
        var member = _moveCandidate;
        IsMovePickerOpen = false;
        _moveCandidate = null;
        if (member == null || target == null) return;

        StatusMessage = $"Moving {member.CharacterName}…";
        var ok = await _esi.MoveFleetMemberAsync(SessionFleetId, member.CharacterId, target.Role, target.WingId, target.SquadId);
        StatusMessage = ok ? $"Moved {member.CharacterName}" : "Move failed — are you the fleet boss?";
        if (ok) await LoadFleetDataAsync();
    }

    // ── Rename wing / squad ─────────────────────────────────────────────────────
    private long _renameWingId;
    private long _renameSquadId;

    [RelayCommand]
    private void BeginRenameWing(WingViewModel wing)
    {
        if (wing == null) return;
        _renameWingId  = wing.WingId;
        _renameSquadId = 0;
        RenameTitle = $"Rename {wing.Name}";
        RenameText  = wing.Name;
        IsRenameOpen = true;
    }

    [RelayCommand]
    private void BeginRenameSquad(SquadViewModel squad)
    {
        if (squad == null) return;
        _renameSquadId = squad.SquadId;
        _renameWingId  = 0;
        RenameTitle = $"Rename {squad.Name}";
        RenameText  = squad.Name;
        IsRenameOpen = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenameOpen = false;

    [RelayCommand]
    private async Task ConfirmRename()
    {
        var name = RenameText.Trim();
        IsRenameOpen = false;
        if (string.IsNullOrEmpty(name)) return;

        bool ok;
        if (_renameWingId != 0)
            ok = await _esi.RenameWingAsync(SessionFleetId, _renameWingId, name);
        else if (_renameSquadId != 0)
            ok = await _esi.RenameSquadAsync(SessionFleetId, _renameSquadId, name);
        else
            return;

        StatusMessage = ok ? "Renamed" : "Rename failed — are you the fleet boss?";
        if (ok) await LoadFleetDataAsync();
    }

    // ── Invite ──────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task Invite()
    {
        var name = InviteName.Trim();
        if (string.IsNullOrEmpty(name)) return;

        // Pick the first squad as the destination (ESI requires a squad for squad_member).
        var squad = Wings.SelectMany(w => w.Squads.Select(s => (w.WingId, s.SquadId))).FirstOrDefault();
        if (squad.SquadId == 0 && squad.WingId == 0)
        {
            StatusMessage = "Invite needs at least one squad — create one first.";
            return;
        }

        StatusMessage = $"Looking up {name}…";
        var charId = await _esi.ResolveCharacterIdAsync(name);
        if (charId == null)
        {
            StatusMessage = $"No character named \"{name}\".";
            return;
        }

        var ok = await _esi.InviteFleetMemberAsync(SessionFleetId, charId.Value, squad.WingId, squad.SquadId);
        StatusMessage = ok ? $"Invited {name}" : "Invite failed — are you the fleet boss?";
        if (ok) InviteName = string.Empty;
    }

    // ── Wing / squad structure ────────────────────────────────────────────────────
    [RelayCommand]
    private async Task AddWing()
    {
        StatusMessage = "Creating wing…";
        var ok = await _esi.CreateWingAsync(SessionFleetId);
        StatusMessage = ok ? "Wing created" : "Create failed — are you the fleet boss?";
        if (ok) await LoadFleetDataAsync();
    }

    [RelayCommand]
    private async Task AddSquad(WingViewModel wing)
    {
        if (wing == null) return;
        StatusMessage = "Creating squad…";
        var ok = await _esi.CreateSquadAsync(SessionFleetId, wing.WingId);
        StatusMessage = ok ? "Squad created" : "Create failed — are you the fleet boss?";
        if (ok) await LoadFleetDataAsync();
    }

    [RelayCommand]
    private async Task DeleteWing(WingViewModel wing)
    {
        if (wing == null) return;
        var confirm = System.Windows.MessageBox.Show(
            $"Delete {wing.Name}? Pilots in it will move to the fleet's default wing.",
            "Delete wing", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var ok = await _esi.DeleteWingAsync(SessionFleetId, wing.WingId);
        StatusMessage = ok ? "Wing deleted" : "Delete failed — are you the fleet boss?";
        if (ok) await LoadFleetDataAsync();
    }

    [RelayCommand]
    private async Task DeleteSquad(SquadViewModel squad)
    {
        if (squad == null) return;
        var confirm = System.Windows.MessageBox.Show(
            $"Delete {squad.Name}?",
            "Delete squad", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var ok = await _esi.DeleteSquadAsync(SessionFleetId, squad.SquadId);
        StatusMessage = ok ? "Squad deleted" : "Delete failed — are you the fleet boss?";
        if (ok) await LoadFleetDataAsync();
    }

    // ── Boost loadouts ──────────────────────────────────────────────────────────
    /// <summary>Attaches a pilot's posted boost charges (from the boost channel) to their row.</summary>
    private void ApplyBoost(FleetMemberViewModel vm)
    {
        var loadout = _boost.GetLoadout(vm.CharacterName);
        if (loadout.Count == 0)
        {
            vm.BoostSummary    = string.Empty;
            vm.BoostDetail     = string.Empty;
            vm.BoostCategories = [];
            return;
        }

        // Show the specific charges (short names), ordered by category for stability.
        var ordered = loadout.OrderBy(c => c.Category).ThenBy(c => c.Name).ToList();
        vm.BoostSummary    = string.Join(" · ", ordered.Select(c => c.Name.Replace(" Charge", "")));
        vm.BoostDetail     = string.Join("\n", ordered.Select(c => $"{c.Name} — {c.Effect}"));
        vm.BoostCategories = ordered.Select(c => c.Category).Distinct().ToList();
    }

    private void OnBoostUpdated()
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            BoostChannelName = _boost.ActiveChannel ?? "not found";
            foreach (var vm in _currentMembers) ApplyBoost(vm);
        });
    }

    // ── Alert handler ─────────────────────────────────────────────────────────
    private void OnAlertRaised(FcAlert alert) => App.Current.Dispatcher.Invoke(() => RaiseAlert(alert));

    /// <summary>Inserts an alert at the top of the feed. Must be called on the UI thread.</summary>
    private void RaiseAlert(FcAlert alert)
    {
        Alerts.Insert(0, alert);
        while (Alerts.Count > 100)       // keep the feed bounded
            Alerts.RemoveAt(Alerts.Count - 1);

        if (_settings.Current.AlertSoundsEnabled)
        {
            var preset = alert.AlertType switch
            {
                AlertType.Tackled    => _settings.Current.TackledSound,
                AlertType.CapTrouble => _settings.Current.CapTroubleSound,
                AlertType.BoostLost  => _settings.Current.BoostLostSound,
                _                    => "None",
            };
            // Throttle per type so tackle spam doesn't machine-gun the speaker.
            SoundService.PlayThrottled(preset, alert.AlertType.ToString(), TimeSpan.FromSeconds(2));
        }
    }
}
