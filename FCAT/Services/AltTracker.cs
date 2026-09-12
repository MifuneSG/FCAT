using System.Windows;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// One shared poll of every authorized character, running for the whole session.
///
/// The dashboard and the account board used to poll their alts independently, each only while its
/// own page was open. That meant the same ESI calls twice over, and it meant an alt could go
/// offline or get podded with nobody watching. This is the single source: it runs from launch,
/// polls faster while a page is showing it, and raises alerts regardless of which page is open.
///
/// Alerts are deliberately limited to alts the FC labelled Cyno or Scout. Those are positioned
/// deliberately and their state is tactical - a hauler going offline is not news, a cyno going
/// offline mid-op is. See <see cref="IsCovertRole"/>.
/// </summary>
public class AltTracker(EsiService esi, EsiAuthService auth, AlertHub hub)
{
    /// <summary>Poll interval while a page is displaying the alts.</summary>
    private const int ForegroundSeconds = 20;

    /// <summary>Poll interval when nothing is watching - still running, just cheaper on ESI.</summary>
    private const int BackgroundSeconds = 60;

    private static readonly TimeSpan KillsMaxAge = TimeSpan.FromMinutes(2);

    private CancellationTokenSource? _cts;
    private int _viewers;

    // Lets a page opening cut the current wait short instead of sitting through a 60s background tick.
    private readonly object _wakeLock = new();
    private CancellationTokenSource? _wake;

    private readonly Dictionary<int, int>  _groupIdCache = [];
    private readonly Dictionary<int, bool> _wasOnline    = [];
    private readonly Dictionary<int, bool> _wasInCapsule = [];
    private bool _hasAltBaseline;

    private Dictionary<int, int> _pvpKills     = [];
    private Dictionary<int, int> _prevPvpKills = [];
    private bool     _hasKillBaseline;
    private DateTime _killsAt;

    public IReadOnlyList<AltStatus> Alts { get; private set; } = [];

    /// <summary>Raised after every poll, so views can re-read <see cref="Alts"/>.</summary>
    public event Action? Updated;

    /// <summary>True when the FC has at least one alt worth alerting on. Nothing alerts without it,
    /// so an FC who never labels an alt never gets alt alerts.</summary>
    public bool HasCovertAlt => auth.Store.Characters
        .Any(c => c.CharacterId != auth.AuthenticatedCharacterId && IsCovertRole(c.Role));

    public string ActiveRole => auth.Store.Characters
        .FirstOrDefault(c => c.CharacterId == auth.AuthenticatedCharacterId)?.Role ?? "Main";

    /// <summary>Cyno and Scout are the roles whose position is tactical. Everything else is a label.</summary>
    public static bool IsCovertRole(string role)
        => role.Equals("Cyno", StringComparison.OrdinalIgnoreCase)
        || role.Equals("Scout", StringComparison.OrdinalIgnoreCase);

    // Lifecycle
    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>A page is showing the alts - poll faster, and refresh now rather than on the next tick.</summary>
    public void StartAuto()
    {
        _viewers++;
        Start();
        WakeNow();
    }

    public void StopAuto()
    {
        if (_viewers > 0) _viewers--;
    }

    private void WakeNow()
    {
        lock (_wakeLock)
        {
            try { _wake?.Cancel(); }
            catch (ObjectDisposedException) { /* the tick already finished */ }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(); }
            catch { /* one bad tick shouldn't end the session's tracking */ }

            var seconds = _viewers > 0 ? ForegroundSeconds : BackgroundSeconds;

            CancellationTokenSource wake;
            lock (_wakeLock) _wake = wake = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try { await Task.Delay(TimeSpan.FromSeconds(seconds), wake.Token); }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; }
            finally
            {
                lock (_wakeLock) { _wake = null; wake.Dispose(); }
            }
        }
    }

    public async Task RefreshNowAsync()
    {
        try { await PollOnceAsync(); }
        catch { }
    }

    // Polling
    private async Task PollOnceAsync()
    {
        if (esi.DemoMode) { PublishDemo(); return; }

        var characters = auth.Store.Characters.ToList();
        if (characters.Count == 0)
        {
            if (Alts.Count > 0) Publish([]);
            return;
        }

        var read = new List<AltStatus>(characters.Count);
        foreach (var c in characters)
        {
            var status = new AltStatus
            {
                CharacterId       = c.CharacterId,
                Name              = c.CharacterName,
                Role              = c.Role,
                IsActiveCharacter = c.CharacterId == auth.AuthenticatedCharacterId,
            };

            status.Online = (await esi.GetCharacterOnlineAsync(c.CharacterId))?.Online ?? false;
            if (status.Online)
            {
                var loc   = await esi.GetCharacterLocationAsync(c.CharacterId);
                var ship  = await esi.GetCharacterShipAsync(c.CharacterId);
                var fleet = await esi.GetCharacterFleetAsync(c.CharacterId);

                status.SystemId   = loc?.SolarSystemId ?? 0;
                status.StationId  = loc?.StationId ?? 0;
                status.ShipTypeId = ship?.ShipTypeId ?? 0;
                status.FleetId    = fleet?.FleetId ?? 0;

                if (loc?.StructureId is long structureId)
                    status.StructureName =
                        (await esi.GetStructureNameAsync(structureId, c.CharacterId))?.Name ?? "structure";
            }
            read.Add(status);
        }

        var ids = read.SelectMany(r => new[] { r.SystemId, r.ShipTypeId, r.StationId })
                      .Where(i => i > 0).Distinct().ToList();
        var names = ids.Count > 0 ? await esi.ResolveNamesAsync(ids) : [];

        foreach (var r in read)
        {
            if (r.SystemId   > 0 && names.TryGetValue(r.SystemId,   out var sys)) r.SystemName  = sys;
            if (r.ShipTypeId > 0 && names.TryGetValue(r.ShipTypeId, out var ship)) r.ShipName   = ship;
            if (r.StationId  > 0 && names.TryGetValue(r.StationId,  out var stn)) r.StationName = stn;
        }

        await ResolveCapsulesAsync(read);
        await EnsureKillsAsync();
        Publish(read);
    }

    /// <summary>Works out which alts are sitting in a pod. The hull's group decides it, same
    /// classifier the fleet view uses.</summary>
    private async Task ResolveCapsulesAsync(List<AltStatus> read)
    {
        var unknown = read.Select(r => r.ShipTypeId)
                          .Where(id => id > 0 && !_groupIdCache.ContainsKey(id))
                          .Distinct().ToList();
        if (unknown.Count > 0)
            foreach (var (typeId, groupId) in await esi.GetShipGroupIdsAsync(unknown))
                _groupIdCache[typeId] = groupId;

        foreach (var r in read)
            r.InCapsule = _groupIdCache.TryGetValue(r.ShipTypeId, out var g) && ShipRoleClassifier.IsCapsule(g);
    }

    private async Task EnsureKillsAsync()
    {
        // ESI moves this hourly, so re-asking inside a couple of minutes buys nothing.
        if (DateTime.UtcNow - _killsAt < KillsMaxAge && _pvpKills.Count > 0) return;

        var kills = await esi.GetSystemKillsAsync();
        _hasKillBaseline = _pvpKills.Count > 0;
        _prevPvpKills    = _pvpKills;
        _pvpKills        = kills.ToDictionary(k => k.SystemId, k => k.ShipKills + k.PodKills);
        _killsAt         = DateTime.UtcNow;
    }

    private void Publish(List<AltStatus> read)
    {
        Alts = read;

        // No Application during a unit-test style run; just notify.
        if (Application.Current == null) { Updated?.Invoke(); return; }

        Application.Current.Dispatcher.Invoke(() =>
        {
            RaiseAltAlerts(read);
            Updated?.Invoke();
        });
    }

    private void PublishDemo() => Publish(DemoData.Alts().Select(a => new AltStatus
    {
        CharacterId   = a.CharacterId,
        Name          = a.Name,
        Role          = a.Role,
        Online        = true,
        SystemName    = a.SystemName,
        ShipName      = a.ShipName,
        StructureName = a.DockText.Contains("Docked") ? "Keepstar" : string.Empty,
        FleetId       = a.FleetText.Length > 0 ? DemoData.FleetId : 0,
    }).ToList());

    // Alerts
    /// <summary>
    /// Compares this poll against the last and speaks up about the three things that matter for a
    /// deliberately-positioned alt. Nothing fires on the first poll - there is nothing to compare
    /// against, and treating a cold start as a change would alert on every alt at launch.
    /// </summary>
    private void RaiseAltAlerts(List<AltStatus> read)
    {
        var watching = HasCovertAlt;

        foreach (var alt in read.Where(a => !a.IsActiveCharacter))
        {
            var knewOnline  = _wasOnline.TryGetValue(alt.CharacterId, out var wasOnline);
            var knewCapsule = _wasInCapsule.TryGetValue(alt.CharacterId, out var wasInCapsule);

            if (watching && _hasAltBaseline)
            {
                if (knewOnline && wasOnline && !alt.Online)
                    Raise(AlertType.AltOffline, $"{alt.Name} ({alt.Role}) dropped offline");

                // A pod in space is a loss. A pod docked is a reship, which is routine.
                if (knewCapsule && !wasInCapsule && alt.InCapsule && !alt.Docked)
                    Raise(AlertType.AltPodded,
                        $"{alt.Name} ({alt.Role}) is in a pod"
                        + (alt.SystemName.Length > 0 ? $" in {alt.SystemName}" : string.Empty));

                if (alt.Online && !alt.Docked && alt.SystemId > 0 && _hasKillBaseline)
                {
                    var now  = _pvpKills.GetValueOrDefault(alt.SystemId);
                    var was  = _prevPvpKills.GetValueOrDefault(alt.SystemId);
                    var diff = now - was;
                    if (diff > 0)
                        Raise(AlertType.AltKills,
                            $"{diff} kill{(diff == 1 ? "" : "s")} in {alt.SystemName}, where {alt.Name} ({alt.Role}) is");
                }
            }

            _wasOnline[alt.CharacterId]    = alt.Online;
            _wasInCapsule[alt.CharacterId] = alt.InCapsule;
        }

        _hasAltBaseline = true;
    }

    private void Raise(AlertType type, string detail)
        => hub.Raise(new FcAlert { Timestamp = DateTime.Now, AlertType = type, Detail = detail });

    /// <summary>
    /// A cloak failure reaches here from the log watcher. It only becomes an alert when it happened
    /// to a character the FC is actually tracking - the FC's own main decloaking while they fly it
    /// is something they can see on their own screen.
    /// </summary>
    public void ReportCloakDropped(FcAlert alert)
    {
        if (IsCovertRole(RoleOf(alert.SourceCharacter))) hub.Raise(alert);
    }

    private string RoleOf(string characterName)
        => characterName.Length == 0
            ? ActiveRole
            : auth.Store.Characters
                  .FirstOrDefault(c => c.CharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase))
                  ?.Role ?? string.Empty;
}
