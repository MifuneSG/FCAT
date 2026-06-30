using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>One row on the character board — live status for one authorized character.</summary>
public partial class CharacterRow : ObservableObject
{
    public int    CharacterId { get; init; }
    public string Name        { get; init; } = string.Empty;
    public string PortraitUrl { get; init; } = string.Empty;

    [ObservableProperty] private string _role = "Main";
    [ObservableProperty] private bool   _isActive;
    [ObservableProperty] private bool   _online;
    [ObservableProperty] private string _systemName = "—";
    [ObservableProperty] private string _shipName   = "—";
    [ObservableProperty] private string _dockText   = string.Empty;   // "⚓ Docked · X" / "In space"
    [ObservableProperty] private string _fleetText  = string.Empty;   // "In your fleet" / "In fleet"

    /// <summary>Set by the VM so a role change from the combo box persists to the store.</summary>
    public Action<string>? RoleChanged;
    partial void OnRoleChanged(string value) => RoleChanged?.Invoke(value);
}

/// <summary>
/// Account manager: add/switch/remove characters and a live status board (online · system · ship)
/// for the FC's main + alts. Polls each character with its own token while the page is open.
/// </summary>
public partial class AccountViewModel : ObservableObject
{
    private readonly EsiAuthService _auth;
    private readonly EsiService     _esi;
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _cts;

    public ObservableCollection<CharacterRow> Characters { get; } = [];
    public string[] RoleOptions { get; } = ["Main", "Cyno", "Scout", "Hauler", "Titan", "Booster", "Tackle", "Other"];

    [ObservableProperty] private bool _isAdding;

    public AccountViewModel(EsiAuthService auth, EsiService esi, ShellViewModel shell)
    {
        _auth = auth; _esi = esi; _shell = shell;
        Rebuild();
    }

    private void Rebuild()
    {
        Characters.Clear();
        foreach (var c in _auth.Store.Characters)
        {
            var row = new CharacterRow
            {
                CharacterId = c.CharacterId,
                Name        = c.CharacterName,
                PortraitUrl = $"https://images.evetech.net/characters/{c.CharacterId}/portrait?size=64",
                Role        = c.Role,
                IsActive    = c.IsActive,
            };
            row.RoleChanged = r => _auth.Store.SetRole(row.CharacterId, r);
            Characters.Add(row);
        }
    }

    // ── Live polling (page-scoped) ──
    public void StartAuto() { _cts = new CancellationTokenSource(); _ = PollLoop(_cts.Token); }
    public void StopAuto()  { _cts?.Cancel(); }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }
        }
    }

    private async Task PollOnceAsync()
    {
        var raw = new List<(CharacterRow row, bool online, int sysId, int shipId, int stationId, string structName, long fleetId)>();
        foreach (var row in Characters.ToList())
        {
            var online = await _esi.GetCharacterOnlineAsync(row.CharacterId);
            int sysId = 0, shipId = 0, stationId = 0; string structName = string.Empty; long fleetId = 0;
            if (online?.Online == true)
            {
                var loc   = await _esi.GetCharacterLocationAsync(row.CharacterId);
                var ship  = await _esi.GetCharacterShipAsync(row.CharacterId);
                var fleet = await _esi.GetCharacterFleetAsync(row.CharacterId);   // null = not in a fleet
                sysId   = loc?.SolarSystemId ?? 0;
                shipId  = ship?.ShipTypeId ?? 0;
                stationId = loc?.StationId ?? 0;
                fleetId = fleet?.FleetId ?? 0;
                if (loc?.StructureId is long sid)
                    structName = (await _esi.GetStructureNameAsync(sid, row.CharacterId))?.Name ?? "structure";
            }
            raw.Add((row, online?.Online ?? false, sysId, shipId, stationId, structName, fleetId));
        }

        var ids = raw.SelectMany(r => new[] { r.sysId, r.shipId, r.stationId }).Where(i => i > 0).Distinct().ToList();
        var names = ids.Count > 0 ? await _esi.ResolveNamesAsync(ids) : [];
        long activeFleet = raw.FirstOrDefault(r => r.row.IsActive).fleetId;

        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var r in raw)
            {
                r.row.Online     = r.online;
                r.row.SystemName = r.sysId  > 0 && names.TryGetValue(r.sysId,  out var s)  ? s  : (r.online ? "?" : "—");
                r.row.ShipName   = r.shipId > 0 && names.TryGetValue(r.shipId, out var sh) ? sh : (r.online ? "?" : "—");

                r.row.DockText =
                    !r.online                              ? string.Empty :
                    r.stationId > 0                        ? "⚓ Docked · " + (names.TryGetValue(r.stationId, out var st) ? st : "station") :
                    !string.IsNullOrEmpty(r.structName)    ? "⚓ Docked · " + r.structName :
                                                             "In space";

                r.row.FleetText =
                    r.fleetId == 0                                   ? string.Empty :
                    r.fleetId == activeFleet && activeFleet != 0     ? "In your fleet" :
                                                                       "In fleet";
            }
        });
    }

    [RelayCommand]
    private async Task AddCharacter()
    {
        IsAdding = true;
        var ok = await _auth.AuthenticateAsync();
        IsAdding = false;
        if (ok) { Rebuild(); await PollOnceAsync(); }
    }

    [RelayCommand]
    private async Task SetActive(CharacterRow? row)
    {
        if (row == null || row.IsActive) return;
        if (await _auth.SetActiveCharacterAsync(row.CharacterId))
            foreach (var r in Characters) r.IsActive = r.CharacterId == row.CharacterId;
    }

    [RelayCommand]
    private void Remove(CharacterRow? row)
    {
        if (row == null) return;
        _auth.RemoveCharacter(row.CharacterId);
        Rebuild();
    }
}
