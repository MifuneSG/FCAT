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
    private readonly AlertHub        _alertHub;
    private readonly SessionLog      _sessionLog;
    private readonly ShellViewModel  _shell;

    private CancellationTokenSource? _pollCts;
    // Serializes fleet-data loads so a 5s poll and a manual refresh (kick/move/invite) can't
    // run concurrently and race the shared caches / collections.
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // Persistent name cache — typeId/charId/sysId → display name, survives poll ticks
    private readonly Dictionary<int, string> _nameCache    = [];

    // Persistent ship role cache — typeId → group_id from ESI
    private readonly Dictionary<int, int>    _groupIdCache = [];

    // FC role overrides for this fleet, keyed by ship type (e.g. Legion → Logi). Session-only.
    private readonly Dictionary<int, ShipRole> _typeRoleOverrides = [];

    // Boost channel reader + per-pilot pod-state tracking for "boost lost" detection
    private readonly BoostChannelService _boost = new();
    private readonly Dictionary<int, bool> _inCapsule = [];
    // Last live (non-pod) hull seen per pilot, so the loss log can name what they actually lost.
    private readonly Dictionary<int, string> _lastShipName = [];
    private readonly List<FleetMemberViewModel> _currentMembers = [];

    // Roster tracking for the after-action log (pilot joins/leaves). First poll seeds the
    // baseline silently so we don't log the whole fleet as "joined" at startup.
    private Dictionary<int, string> _rosterNames = [];
    private bool _rosterInitialized;

    public long SessionFleetId { get; }

    public FleetViewModel(EsiAuthService auth, EsiService esi, CombatLogService combatLog,
                          SettingsService settings, AlertHub alertHub, SessionLog sessionLog,
                          ShellViewModel shell, long fleetId)
    {
        _auth      = auth;
        _esi       = esi;
        _combatLog = combatLog;
        _settings  = settings;
        _alertHub  = alertHub;
        _sessionLog = sessionLog;
        _shell     = shell;

        SessionFleetId = fleetId;
        CharacterName  = auth.AuthenticatedCharacterName;
        FleetIdDisplay = fleetId.ToString();

        _sessionLog.StartSession(fleetId, auth.AuthenticatedCharacterName);

        _combatLog.AlertRaised += OnAlertRaised;
        _combatLog.StartWatching(_settings.Current.GamelogsPath);

        _boost.Updated += OnBoostUpdated;
        _boost.StartWatching(_settings.Current.ChatlogsPath, _settings.Current.BoostChannelPrefix);
        BoostChannelName = _boost.ActiveChannel ?? "not found";

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
    // The alert feed lives on the app-lifetime AlertHub so it (and the overlay) survive navigation.
    public ObservableCollection<FcAlert>              Alerts           => _alertHub.Alerts;
    public ObservableCollection<MoveTargetViewModel>  MoveTargets      { get; } = [];
    public ObservableCollection<FleetStat>            RoleStats        { get; } = [];
    public ObservableCollection<BoostStat>            BoostStats       { get; } = [];
    public ObservableCollection<FleetAdvisory>        Advisories       { get; } = [];
    // Suggested cap-chain order (Guardian/Basilisk only), alphabetical so every logi derives the same ring.
    public ObservableCollection<string>               LogiChain        { get; } = [];
    public bool HasLogiChain => LogiChain.Count >= 2;

    // Pilots not in the configured form-up system (the straggler check).
    public ObservableCollection<string>               Stragglers       { get; } = [];
    public bool   FormupConfigured => !string.IsNullOrWhiteSpace(_settings.Current.FormupSystem);
    public bool   HasStragglers    => Stragglers.Count > 0;
    [ObservableProperty] private string _stragglerSummary = string.Empty;
    [ObservableProperty] private bool   _showFormupCard;   // visible only during form-up, hides once fleet departs

    // Form-up lifecycle: show the card while staging, hide it once the fleet moves out, then
    // silently log anyone still sitting in staging "long after" departure to the AAR.
    private enum FormupPhase { Forming, Departed }
    private FormupPhase _formupPhase = FormupPhase.Forming;
    private bool _everStaged;                 // true once a majority actually gathered in the form-up system
    private DateTime? _departedAt;
    private readonly HashSet<int> _leftBehindLogged = [];
    // Pilots podded at any point this session — they respawn at staging, so being there is expected,
    // not "left behind." Excluded from the left-behind check.
    private readonly HashSet<int> _poddedThisSession = [];
    private static readonly TimeSpan LeftBehindAfter = TimeSpan.FromMinutes(3);  // "long after the fleet is gone"

    // ── DPS-attrition tracking ──────────────────────────────────────────────────────
    // Warns the FC as the fleet's DPS line is whittled down by deaths, at 30% / 50% / 75% of the
    // committed baseline. "DPS" here is a HEADCOUNT of DPS-role hulls, not real fitted damage —
    // EVE exposes no fits — so the alert is worded "~%".
    //
    // The baseline is frozen only once the fleet is committed: when it leaves the form-up system,
    // or on the first loss if no form-up system is configured. That way pilots told to swap from
    // DPS into logi DURING form-up settle into their final role before the baseline is taken, so
    // they're never miscounted as a loss. Losses are counted strictly from pods (ship → capsule)
    // of pilots who were in the baseline DPS set — a voluntary re-ship or leaving fleet never counts.
    private static readonly int[] DpsLossThresholds = { 30, 50, 75 };
    private const int MinDpsBaseline = 5;                    // a % is meaningless on a handful of ships
    private readonly HashSet<int> _currentDpsIds      = [];  // DPS-role char ids this tick (combat fleets only)
    private HashSet<int>          _baselineDpsIds     = [];  // frozen committed DPS line
    private bool                  _dpsBaselineArmed;
    private readonly HashSet<int> _dpsLostIds         = [];  // baseline DPS pilots confirmed podded (no double-count)
    private readonly HashSet<int> _dpsThresholdsFired = [];  // thresholds already alerted (reset when re-armed)

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

        // ── 4. Death detection: a member that flipped to a pod lost their ship ──
        var deathAlerts = new List<FcAlert>();
        var deathLogs   = new List<string>();   // AAR lines for every loss (category DEATH)
        var newlyPodded = new List<int>();       // every pilot who flipped to a pod this tick (for DPS-attrition)
        foreach (var m in members)
        {
            var isCapsule = _groupIdCache.TryGetValue(m.ShipTypeId, out var g) && ShipRoleClassifier.IsCapsule(g);
            var wasCapsule = _inCapsule.TryGetValue(m.CharacterId, out var prev) && prev;

            if (isCapsule && !wasCapsule)
            {
                newlyPodded.Add(m.CharacterId);

                // Permanent AAR record of the loss. The current ship already reads "Capsule", so the
                // hull they actually lost comes from the previous tick's snapshot.
                var name = string.IsNullOrEmpty(m.CharacterName) ? $"#{m.CharacterId}" : m.CharacterName;
                var lost = _lastShipName.TryGetValue(m.CharacterId, out var ship) ? ship : "ship";
                deathLogs.Add($"{name} lost their {lost}");

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
            if (isCapsule) _poddedThisSession.Add(m.CharacterId);   // died this session — exempt from "left behind"
            else if (!string.IsNullOrEmpty(m.ShipTypeName))
                _lastShipName[m.CharacterId] = m.ShipTypeName;       // remember the live hull for the loss log
            _inCapsule[m.CharacterId] = isCapsule;
        }

        // ── 5. Roster diff for the AAR (joins / leaves) ───────────────────────
        var roster = members.ToDictionary(m => m.CharacterId,
                                          m => string.IsNullOrEmpty(m.CharacterName) ? $"#{m.CharacterId}" : m.CharacterName);
        var rosterEvents = new List<string>();
        if (_rosterInitialized)
        {
            foreach (var (id, name) in roster)
                if (!_rosterNames.ContainsKey(id)) rosterEvents.Add($"JOIN|{name} joined the fleet");
            foreach (var (id, name) in _rosterNames)
                if (!roster.ContainsKey(id)) rosterEvents.Add($"LEAVE|{name} left the fleet");
        }
        _rosterNames = roster;
        _rosterInitialized = true;

        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var d in deathLogs) _sessionLog.Record("DEATH", d);   // permanent AAR record of every loss
            foreach (var a in deathAlerts) RaiseAlert(a);
            // DPS-attrition: run before RebuildHierarchy so the pre-death DPS snapshot is still
            // available to arm the baseline on first blood (RebuildHierarchy recomputes it).
            if (TrackDpsLoss(newlyPodded) is { } dpsAlert) RaiseAlert(dpsAlert);
            foreach (var ev in rosterEvents)
            {
                var parts = ev.Split('|', 2);
                _sessionLog.Record(parts[0], parts[1]);
            }
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
            var vm = new FleetMemberViewModel(member, kickHandler, moveHandler, SetMemberRole);

            // Set ship role from group_id cache
            if (_groupIdCache.TryGetValue(member.ShipTypeId, out var groupId))
            {
                vm.ShipRole     = ShipRoleClassifier.Classify(member.ShipTypeId, groupId);
                vm.IsInCapsule  = ShipRoleClassifier.IsCapsule(groupId);
            }

            // FC role override for this hull wins (e.g. a logi-fit Legion in a T3 fleet).
            if (_typeRoleOverrides.TryGetValue(member.ShipTypeId, out var ovr)) vm.ShipRole = ovr;

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
        UpdateCapChain();
        UpdateFormup();
        StatusMessage = $"{MemberCount} pilots";
    }

    // ── FC role override ────────────────────────────────────────────────────────────
    // Hulls like T3 cruisers (Legion/Loki/Proteus/Tengu) can be DPS, logi or boosters depending on
    // fit — which FCAT can't see. The FC right-clicks a pilot and picks the role; it applies to every
    // pilot in that hull for this fleet (null = back to the hull default).
    private void SetMemberRole(FleetMemberViewModel member, ShipRole? role)
    {
        var typeId = member.ShipTypeId;
        if (role is { } r) _typeRoleOverrides[typeId] = r;
        else _typeRoleOverrides.Remove(typeId);

        foreach (var m in _currentMembers.Where(m => m.ShipTypeId == typeId))
            m.ShipRole = role ?? (_groupIdCache.TryGetValue(typeId, out var g)
                ? ShipRoleClassifier.Classify(typeId, g) : ShipRole.DPS);

        ComputeStats();
        UpdateCapChain();   // cap-chain ring uses hull type, not role — but recount role tallies
    }

    // ── Form-up / straggler tracking ───────────────────────────────────────────────
    // Phase 1 (Forming): the FORM-UP card lists who hasn't reached the staging system yet — quiet,
    // no alerts. Phase 2 (Departed): once a majority of the fleet leaves staging, the card hides and
    // a silent listener logs anyone STILL parked in staging "long after" departure to the AAR.
    // All comparisons are by system NAME (already resolved per member) — no extra ESI calls.
    private void UpdateFormup()
    {
        var formup = _settings.Current.FormupSystem?.Trim() ?? string.Empty;
        OnPropertyChanged(nameof(FormupConfigured));

        if (formup.Length == 0)
        {
            Stragglers.Clear();
            StragglerSummary = string.Empty;
            ShowFormupCard = false;
            OnPropertyChanged(nameof(HasStragglers));
            return;
        }

        bool InFormup(FleetMemberViewModel m) =>
            string.Equals(m.SolarSystemName, formup, StringComparison.OrdinalIgnoreCase);

        var known = _currentMembers.Where(m => !string.IsNullOrEmpty(m.SolarSystemName)).ToList();
        if (known.Count == 0) { ShowFormupCard = _formupPhase == FormupPhase.Forming; return; }

        var inCount = known.Count(InFormup);
        var share   = (double)inCount / known.Count;

        // Detect the fleet leaving staging: it must first have actually gathered there.
        if (_formupPhase == FormupPhase.Forming)
        {
            if (!_everStaged && inCount >= 2 && share >= 0.5) _everStaged = true;
            if (_everStaged && share < 0.5)
            {
                _formupPhase = FormupPhase.Departed;
                _departedAt  = DateTime.Now;
                _sessionLog.Record("FORM-UP", $"Fleet departed form-up system {formup}");
                ArmDpsBaseline();   // fleet is committed — freeze the DPS baseline now (roles have settled)
            }
        }

        if (_formupPhase == FormupPhase.Forming)
        {
            Stragglers.Clear();
            foreach (var m in known.Where(m => !InFormup(m))
                                   .OrderBy(m => m.SolarSystemName, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(m => m.CharacterName, StringComparer.OrdinalIgnoreCase))
                Stragglers.Add($"{m.CharacterName} — {m.SolarSystemName}");
            StragglerSummary = Stragglers.Count == 0 ? $"All pilots in {formup}" : $"{Stragglers.Count} not in {formup}";
            ShowFormupCard = true;
        }
        else // Departed — card gone; quietly note anyone left behind in staging.
        {
            Stragglers.Clear();
            ShowFormupCard = false;
            if (_departedAt is { } dep && DateTime.Now - dep >= LeftBehindAfter)
                foreach (var m in known.Where(m => InFormup(m) && !_poddedThisSession.Contains(m.CharacterId)))
                    if (_leftBehindLogged.Add(m.CharacterId))
                        _sessionLog.Record("LEFT BEHIND",
                            $"{m.CharacterName} still in {formup} ~{(int)(DateTime.Now - dep).TotalMinutes}m after the fleet moved out");
        }

        OnPropertyChanged(nameof(HasStragglers));
    }

    // ── Cap-chain advisory (Guardian/Basilisk) ──────────────────────────────────
    // We build our OWN ordered ring from the authorized ESI fleet list (alphabetical, so every
    // logi pilot derives the same order independently) and alert the FC when a chain member is
    // lost so the ring can be re-formed. We can see a logi leave / swap hull / get podded via
    // ESI — we cannot see who is actually transferring to whom (that isn't exposed), so this is
    // an advisory "membership changed" signal, not a live "the chain is broken in space" reading.
    private List<string> _lastChain = [];

    private void UpdateCapChain()
    {
        var chain = _currentMembers
            .Where(m => ShipRoleClassifier.CapChainHullTypeIds.Contains(m.ShipTypeId))
            .Select(m => m.CharacterName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LogiChain.Clear();
        foreach (var n in chain) LogiChain.Add(n);
        OnPropertyChanged(nameof(HasLogiChain));

        // Alert only when a previously-known ring LOSES a member (joins just re-order quietly).
        var lost = _lastChain.Except(chain, StringComparer.OrdinalIgnoreCase).ToList();
        if (_lastChain.Count >= 2 && lost.Count > 0)
        {
            var who = string.Join(", ", lost);
            var detail = chain.Count >= 2
                ? $"{who} dropped — re-form ring: {RingText(chain)}"
                : $"{who} dropped — cap chain collapsed ({chain.Count} left)";
            RaiseAlert(new FcAlert { Timestamp = DateTime.Now, AlertType = AlertType.LogiChain, Detail = detail });
        }

        _lastChain = chain;
    }

    /// <summary>"A → B → C → (A)" — shows the closed loop the cap chain should form.</summary>
    private static string RingText(List<string> names)
        => names.Count == 0 ? "" : string.Join(" → ", names) + $" → ({names[0]})";

    // ── DPS-attrition logic (all on the UI thread) ──────────────────────────────────
    /// <summary>Freezes the committed DPS line as the baseline to measure losses against.
    /// No-op if already armed or the fleet is too small for a % to be meaningful.</summary>
    private void ArmDpsBaseline()
    {
        if (_dpsBaselineArmed || _currentDpsIds.Count < MinDpsBaseline) return;
        _baselineDpsIds   = [.. _currentDpsIds];
        _dpsBaselineArmed = true;
        _dpsLostIds.Clear();
        _dpsThresholdsFired.Clear();
    }

    /// <summary>Records baseline DPS pilots podded this tick and returns a DPS-loss alert when a
    /// 30/50/75% threshold is freshly crossed (highest new one only). Arms the baseline on the
    /// first loss if a form-up departure hasn't already done so.</summary>
    private FcAlert? TrackDpsLoss(IReadOnlyCollection<int> newlyPoddedIds)
    {
        if (newlyPoddedIds.Count == 0) return null;

        if (!_dpsBaselineArmed) ArmDpsBaseline();   // no form-up departure yet → first blood arms it
        if (!_dpsBaselineArmed || _baselineDpsIds.Count == 0) return null;

        foreach (var id in newlyPoddedIds)
            if (_baselineDpsIds.Contains(id)) _dpsLostIds.Add(id);

        var pct = 100.0 * _dpsLostIds.Count / _baselineDpsIds.Count;

        var crossed = 0;
        foreach (var t in DpsLossThresholds)
            if (pct >= t && _dpsThresholdsFired.Add(t)) crossed = t;   // Add() is true only the first time
        if (crossed == 0) return null;

        return new FcAlert
        {
            Timestamp        = DateTime.Now,
            AlertType        = AlertType.DpsLoss,
            Detail           = $"~{crossed}% of DPS lost — {_dpsLostIds.Count} of {_baselineDpsIds.Count} ships down",
            CriticalOverride = crossed >= 75,   // red only at the worst step; 30/50 stay amber
        };
    }

    /// <summary>Re-arms the tracker between fights: once the DPS line is rebuilt to baseline strength
    /// (pilots reshipped / reinforcements arrived) after taking losses, the next fight is measured fresh.</summary>
    private void EvaluateDpsRearm()
    {
        if (_dpsBaselineArmed && _dpsLostIds.Count > 0 && _currentDpsIds.Count >= _baselineDpsIds.Count)
        {
            _baselineDpsIds = [.. _currentDpsIds];
            _dpsLostIds.Clear();
            _dpsThresholdsFired.Clear();
        }
    }

    // ── Composition + boost coverage header ─────────────────────────────────────
    private static readonly Brush BrLogi   = Frozen(0x3f, 0xae, 0x8f);
    private static readonly Brush BrBoost  = Frozen(0x5a, 0x8f, 0xd6);
    private static readonly Brush BrTackle = Frozen(0xd4, 0x6a, 0x6a);
    private static readonly Brush BrEwar    = Frozen(0x9b, 0x7b, 0xd4);
    private static readonly Brush BrSupport = Frozen(0x4d, 0xb8, 0xd4);
    private static readonly Brush BrCap     = Frozen(0xd4, 0xa4, 0x49);
    private static readonly Brush BrIndy   = Frozen(0x8a, 0x96, 0xab);
    private static readonly Brush BrDps    = Frozen(0xc6, 0xce, 0xdb);   // brightened — was a dim slate, hard to read
    private static readonly Brush BrAccent   = Frozen(0x4d, 0xb8, 0xd4);
    private static readonly Brush BrDim      = Frozen(0x5c, 0x64, 0x73);
    private static readonly Brush BrUncovered = Frozen(0x80, 0x8a, 0x9b); // muted but legible — for boost links with 0 coverage
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
        _currentDpsIds.Clear();   // repopulated below for combat fleets; left empty disables attrition
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
            => overrideMainline && m.ShipTypeId == dominantType && !_typeRoleOverrides.ContainsKey(m.ShipTypeId)
                ? ShipRole.DPS : m.ShipRole;

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

        // ── DPS-attrition snapshot (combat fleets only) ──────────────────────
        // Same definition the FC sees in the DPS tally above. Capital/mining fleets leave this
        // empty so the loss alert never arms for them.
        if (kind == FleetKind.Combat)
            foreach (var m in _currentMembers)
                if (EffectiveRole(m) is ShipRole.DPS or ShipRole.Unknown)
                    _currentDpsIds.Add(m.CharacterId);
        EvaluateDpsRearm();

        // ── Boost coverage ───────────────────────────────────────────────────
        // The boost row tracks the link types that matter for the fleet kind: combat fleets
        // care about Shield/Armor/Skirmish/Info; mining fleets about the Mining Foreman bursts.
        // The FC is usually the booster, so these come from the boost channel (Chatlogs).
        void AddBoost(string label, BoostCategory cat, Brush color)
        {
            var n = _currentMembers.Count(m => m.BoostCategories.Contains(cat));
            BoostStats.Add(new BoostStat(label, n, n > 0 ? color : BrUncovered));
        }
        if (kind == FleetKind.Mining)
        {
            AddBoost("Yield", BoostCategory.MiningYield,    BrLogi);
            AddBoost("Range", BoostCategory.MiningOptimal,  BrAccent);
            AddBoost("Presv", BoostCategory.MiningPreserve, BrCap);
        }
        else
        {
            AddBoost("Shield",   BoostCategory.Shield,   BrLogi);
            AddBoost("Armor",    BoostCategory.Armor,    BrCap);
            AddBoost("Skirmish", BoostCategory.Skirmish, BrAccent);
            AddBoost("Info",     BoostCategory.Info,     BrEwar);
        }

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

    [RelayCommand]
    private void OpenSessionLog() => _shell.ShowSessionLog();

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
    // All alerts flow through the app-lifetime AlertHub (sound, auto-clear, overlay) so they
    // persist regardless of which page is open.
    private void OnAlertRaised(FcAlert alert) => App.Current.Dispatcher.Invoke(() => _alertHub.Raise(alert));

    private void RaiseAlert(FcAlert alert) => _alertHub.Raise(alert);
}
