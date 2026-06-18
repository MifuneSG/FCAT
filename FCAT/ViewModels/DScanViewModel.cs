using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>One result row — a ship type (d-scan) or an affiliation (local).</summary>
public record DScanShip(string TypeName, int Count, string RoleTag, Brush Color);

/// <summary>
/// Combined scan tool: paste a directional scan OR your Local member list into one box and
/// Analyze. It auto-detects which it is per line — d-scan rows start with a numeric type ID,
/// Local names don't — and shows the matching breakdown (ship roles, or corp/alliance standings).
/// </summary>
public partial class DScanViewModel : ObservableObject
{
    private readonly EsiService _esi;
    private readonly EsiAuthService _auth;

    // Session caches
    private readonly Dictionary<int, int>    _group    = [];
    private readonly Dictionary<int, int>    _category = [];
    private readonly Dictionary<int, string> _name     = [];

    private const int ShipCategory = 6;
    private int  _ownAllianceId, _ownCorpId;
    private bool _ownLoaded, _lastWasLocal;

    public DScanViewModel(EsiService esi, EsiAuthService auth)
    {
        _esi = esi;
        _auth = auth;
    }

    [ObservableProperty] private string _input   = string.Empty;
    [ObservableProperty] private string _summary = Hint;
    [ObservableProperty] private bool   _isAnalyzing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySummaryCommand))]
    private bool _hasResult;

    public ObservableCollection<FleetStat> RoleBreakdown { get; } = [];
    public ObservableCollection<DScanShip> Ships         { get; } = [];

    private const string Hint = "Paste a d-scan or your Local member list — the tool detects which — then Analyze.";

    [RelayCommand]
    private void Clear()
    {
        Input = string.Empty;
        RoleBreakdown.Clear();
        Ships.Clear();
        HasResult = false;
        Summary = Hint;
    }

    [RelayCommand]
    private async Task Analyze()
    {
        // Split the paste: numeric-leading lines are d-scan ship rows; the rest are names.
        var byType = new Dictionary<int, int>();
        var names  = new List<string>();
        foreach (var raw in (Input ?? string.Empty).Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var tok = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length > 0 && int.TryParse(tok[0], out var tid) && tid > 0)
                byType[tid] = byType.GetValueOrDefault(tid) + 1;
            else
                names.Add(line);
        }
        names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shipLines = byType.Values.Sum();

        IsAnalyzing = true;
        try
        {
            if (shipLines == 0 && names.Count == 0)
                Summary = "Nothing recognized — paste a d-scan or a list of Local names.";
            else if (shipLines >= names.Count)
                await AnalyzeShipsAsync(byType);          // looks like a d-scan
            else
                await AnalyzeLocalAsync(names);           // looks like a Local list
        }
        catch (Exception ex) { Summary = $"Couldn't analyze: {ex.Message}"; }
        finally { IsAnalyzing = false; }
    }

    // ── D-Scan ──────────────────────────────────────────────────────────────────
    private async Task AnalyzeShipsAsync(Dictionary<int, int> byType)
    {
        _lastWasLocal = false;
        var ids = byType.Keys.ToList();
        foreach (var kv in await _esi.ResolveNamesAsync(ids.Where(id => !_name.ContainsKey(id)))) _name[kv.Key] = kv.Value;
        foreach (var kv in await _esi.GetShipGroupIdsAsync(ids.Where(id => !_group.ContainsKey(id)))) _group[kv.Key] = kv.Value;
        var groups = ids.Where(id => _group.ContainsKey(id)).Select(id => _group[id]).Where(g => !_category.ContainsKey(g)).Distinct();
        foreach (var kv in await _esi.GetGroupCategoriesAsync(groups)) _category[kv.Key] = kv.Value;

        var roleCounts = new Dictionary<ShipRole, int>();
        var ships = new List<DScanShip>();
        int shipTotal = 0, other = 0;
        foreach (var (tid, count) in byType)
        {
            if (!_group.TryGetValue(tid, out var gid)) { other += count; continue; }
            if (_category.GetValueOrDefault(gid) != ShipCategory) { other += count; continue; }
            if (ShipRoleClassifier.IsCapsule(gid)) { other += count; continue; }

            var role = ShipRoleClassifier.Classify(tid, gid);
            roleCounts[role] = roleCounts.GetValueOrDefault(role) + count;
            shipTotal += count;
            ships.Add(new DScanShip(_name.GetValueOrDefault(tid, $"Type {tid}"), count, Tag(role), RoleBrush(role)));
        }
        ships.Sort((a, b) => { var p = Priority(a.RoleTag).CompareTo(Priority(b.RoleTag)); return p != 0 ? p : b.Count.CompareTo(a.Count); });

        RoleBreakdown.Clear();
        void AddRole(string label, Brush c, params ShipRole[] roles)
        {
            var n = roles.Sum(r => roleCounts.GetValueOrDefault(r));
            if (n > 0) RoleBreakdown.Add(new FleetStat(label, n, c));
        }
        AddRole("LOGI",   BrLogi,    ShipRole.Logi, ShipRole.CapLogi);
        AddRole("BOOST",  BrBoost,   ShipRole.Booster);
        AddRole("TACKLE", BrTackle,  ShipRole.Tackle, ShipRole.Bubble);
        AddRole("EWAR",   BrEwar,    ShipRole.EWAR);
        AddRole("SUPP",   BrSupport, ShipRole.Support);
        AddRole("CAP",    BrCap,     ShipRole.Titan, ShipRole.Supercarrier, ShipRole.CapDPS);
        AddRole("DPS",    BrDps,     ShipRole.DPS, ShipRole.Unknown);
        AddRole("INDY",   BrIndy,    ShipRole.Industrial, ShipRole.Mining);

        Ships.Clear();
        foreach (var s in ships) Ships.Add(s);
        Summary = other > 0 ? $"D-scan · {shipTotal} ships · {other} drones/structures ignored" : $"D-scan · {shipTotal} ships";
        HasResult = true;
    }

    // ── Local ───────────────────────────────────────────────────────────────────
    private async Task AnalyzeLocalAsync(List<string> names)
    {
        _lastWasLocal = true;
        await EnsureOwnAffiliationAsync();

        var nameToId = await _esi.ResolveCharacterIdsAsync(names);
        var ids = nameToId.Values.Distinct().ToList();
        if (ids.Count == 0) { Summary = "Couldn't resolve any of those names — are they exact character names?"; HasResult = false; return; }

        var affs = await _esi.GetAffiliationsAsync(ids);
        var orgIds = affs.SelectMany(a => new[] { a.AllianceId ?? 0, a.CorporationId }).Where(x => x > 0 && !_name.ContainsKey(x)).Distinct();
        foreach (var kv in await _esi.ResolveNamesAsync(orgIds)) _name[kv.Key] = kv.Value;

        var groups = affs.GroupBy(a => a.AllianceId is > 0 ? ("a", a.AllianceId!.Value) : ("c", a.CorporationId));
        var rows = new List<DScanShip>();
        int total = affs.Count, friendly = 0;
        foreach (var g in groups)
        {
            var (kind, id) = g.Key;
            var count = g.Count();
            var label = (kind == "c" ? "Corp: " : "") + _name.GetValueOrDefault(id, $"ID {id}");
            var blue  = (kind == "a" && id == _ownAllianceId) || (kind == "c" && id == _ownCorpId);
            if (blue) friendly += count;
            rows.Add(new DScanShip(label, count, blue ? "BLUE" : "NEUT", blue ? BrBoost : BrDps));
        }
        rows.Sort((a, b) =>
        {
            var ab = a.RoleTag == "BLUE" ? 0 : 1; var bb = b.RoleTag == "BLUE" ? 0 : 1;
            return ab != bb ? ab.CompareTo(bb) : b.Count.CompareTo(a.Count);
        });

        RoleBreakdown.Clear();
        if (friendly > 0)         RoleBreakdown.Add(new FleetStat("BLUE", friendly, BrBoost));
        if (total - friendly > 0) RoleBreakdown.Add(new FleetStat("OTHER", total - friendly, BrDps));

        Ships.Clear();
        foreach (var r in rows) Ships.Add(r);

        var unresolved = names.Count - nameToId.Count;
        Summary = $"Local · {total} pilots · {friendly} friendly · {total - friendly} other"
                + (unresolved > 0 ? $" · {unresolved} not found" : "");
        HasResult = true;
    }

    private async Task EnsureOwnAffiliationAsync()
    {
        if (_ownLoaded) return;
        _ownLoaded = true;
        var me = (await _esi.GetAffiliationsAsync([_auth.AuthenticatedCharacterId])).FirstOrDefault();
        if (me != null) { _ownAllianceId = me.AllianceId ?? 0; _ownCorpId = me.CorporationId; }
    }

    // ── Copy for comms ──────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void CopySummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine(_lastWasLocal ? $"Local — {Ships.Sum(s => s.Count)} pilots" : $"D-Scan — {Ships.Sum(s => s.Count)} ships");
        if (RoleBreakdown.Count > 0)
            sb.AppendLine(string.Join("  ·  ", RoleBreakdown.Select(r => $"{r.Count} {r.Label}")));
        sb.AppendLine("---");
        foreach (var s in Ships) sb.AppendLine($"{s.Count}x {s.TypeName} [{s.RoleTag}]");

        try { Clipboard.SetText(sb.ToString()); Summary = "Copied to clipboard — paste into comms."; }
        catch (Exception ex) { Summary = $"Couldn't copy: {ex.Message}"; }
    }

    // ── Role tag / colour / sort priority (d-scan) ──────────────────────────────
    private static string Tag(ShipRole r) => r switch
    {
        ShipRole.Logi => "LOGI", ShipRole.CapLogi => "FAX", ShipRole.Booster => "BOOST",
        ShipRole.Titan => "TITAN", ShipRole.Supercarrier => "SUPER", ShipRole.CapDPS => "CAP",
        ShipRole.Tackle => "TACKLE", ShipRole.Bubble => "DICTOR", ShipRole.EWAR => "EWAR",
        ShipRole.Support => "SUPPORT", ShipRole.Industrial => "IND", ShipRole.Mining => "MINE",
        _ => "DPS",
    };

    private static int Priority(string tag) => tag switch
    {
        "TITAN" or "SUPER" or "CAP" or "FAX" => 0,
        "LOGI" => 1, "BOOST" => 2, "EWAR" => 3,
        "TACKLE" or "DICTOR" => 4, "SUPPORT" => 5, "DPS" => 6, _ => 7,
    };

    private static Brush RoleBrush(ShipRole r) => r switch
    {
        ShipRole.Logi or ShipRole.CapLogi      => BrLogi,
        ShipRole.Booster                       => BrBoost,
        ShipRole.Tackle or ShipRole.Bubble     => BrTackle,
        ShipRole.EWAR                          => BrEwar,
        ShipRole.Support                       => BrSupport,
        ShipRole.Titan or ShipRole.Supercarrier or ShipRole.CapDPS => BrCap,
        ShipRole.Industrial or ShipRole.Mining => BrIndy,
        _                                      => BrDps,
    };

    private static readonly Brush BrLogi    = Frozen(0x3f, 0xae, 0x8f);
    private static readonly Brush BrBoost   = Frozen(0x5a, 0x8f, 0xd6);
    private static readonly Brush BrTackle  = Frozen(0xd4, 0x6a, 0x6a);
    private static readonly Brush BrEwar    = Frozen(0x9b, 0x7b, 0xd4);
    private static readonly Brush BrSupport = Frozen(0x4d, 0xb8, 0xd4);
    private static readonly Brush BrCap     = Frozen(0xd4, 0xa4, 0x49);
    private static readonly Brush BrIndy    = Frozen(0x6b, 0x76, 0x89);
    private static readonly Brush BrDps     = Frozen(0x9a, 0xa3, 0xb3);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
