using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>One ship type present on a d-scan, with its count and role.</summary>
public record DScanShip(string TypeName, int Count, string RoleTag, Brush Color);

/// <summary>
/// Parses a pasted EVE directional-scan dump and breaks it down by ship role.
/// A d-scan export is tab-separated: column 0 is the ship's TYPE ID, so we read it directly
/// (no name resolution needed), then type_id → group_id → category. We keep category 6 (Ship),
/// dropping drones/structures/wrecks/pods, and classify each by group.
/// </summary>
public partial class DScanViewModel : ObservableObject
{
    private readonly EsiService _esi;
    private readonly ShellViewModel _shell;

    // Session caches so repeated scans are fast
    private readonly Dictionary<int, int>    _group    = [];
    private readonly Dictionary<int, int>    _category = [];
    private readonly Dictionary<int, string> _name     = [];

    private const int ShipCategory = 6;
    private const string Hint = "Paste a d-scan — select all in the scanner window (Ctrl+A) then Ctrl+C — and Analyze.";

    public DScanViewModel(EsiService esi, ShellViewModel shell)
    {
        _esi = esi;
        _shell = shell;
    }

    [ObservableProperty] private string _input       = string.Empty;
    [ObservableProperty] private string _summary     = Hint;
    [ObservableProperty] private bool   _isAnalyzing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySummaryCommand))]
    private bool _hasResult;

    public ObservableCollection<FleetStat> RoleBreakdown { get; } = [];
    public ObservableCollection<DScanShip> Ships         { get; } = [];

    [RelayCommand]
    private void Back() => _shell.ShowMenu();

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
        // ── Parse: the first token on each row is the ship's type ID ─────────
        // Split on tab OR space so it works whether the paste kept tabs or (as some
        // clients/clipboards do) collapsed them to spaces.
        var byType = new Dictionary<int, int>();
        foreach (var raw in (Input ?? string.Empty).Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var firstToken = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (firstToken.Length > 0 && int.TryParse(firstToken[0], out var tid) && tid > 0)
                byType[tid] = byType.GetValueOrDefault(tid) + 1;
        }

        if (byType.Count == 0)
        {
            Summary = "No d-scan rows found — paste a directional-scan copy (the rows start with a type ID).";
            return;
        }

        // ── Resolve + aggregate (network-bound, so guard against failures) ───
        IsAnalyzing = true;
        try
        {
            var ids = byType.Keys.ToList();

            var unnamedIds = ids.Where(id => !_name.ContainsKey(id)).ToList();
            if (unnamedIds.Count > 0)
                foreach (var kv in await _esi.ResolveNamesAsync(unnamedIds))
                    _name[kv.Key] = kv.Value;

            var ungroupedIds = ids.Where(id => !_group.ContainsKey(id)).ToList();
            if (ungroupedIds.Count > 0)
                foreach (var kv in await _esi.GetShipGroupIdsAsync(ungroupedIds))
                    _group[kv.Key] = kv.Value;

            var uncatGroups = ids.Where(id => _group.ContainsKey(id)).Select(id => _group[id])
                                 .Where(g => !_category.ContainsKey(g)).Distinct().ToList();
            if (uncatGroups.Count > 0)
                foreach (var kv in await _esi.GetGroupCategoriesAsync(uncatGroups))
                    _category[kv.Key] = kv.Value;

            // Aggregate ships only
            var roleCounts = new Dictionary<ShipRole, int>();
            var ships      = new List<DScanShip>();
            int shipTotal = 0, other = 0;

            foreach (var (tid, count) in byType)
            {
                if (!_group.TryGetValue(tid, out var gid)) { other += count; continue; }
                if (_category.GetValueOrDefault(gid) != ShipCategory) { other += count; continue; }   // drone/structure/wreck
                if (ShipRoleClassifier.IsCapsule(gid)) { other += count; continue; }                   // pods aren't a threat

                var role = ShipRoleClassifier.Classify(gid);
                roleCounts[role] = roleCounts.GetValueOrDefault(role) + count;
                shipTotal += count;
                ships.Add(new DScanShip(_name.GetValueOrDefault(tid, $"Type {tid}"), count, Tag(role), RoleBrush(role)));
            }

            ships.Sort((a, b) =>
            {
                var p = Priority(a.RoleTag).CompareTo(Priority(b.RoleTag));
                return p != 0 ? p : b.Count.CompareTo(a.Count);
            });

            RoleBreakdown.Clear();
            void AddRole(string label, Brush c, params ShipRole[] roles)
            {
                var n = roles.Sum(r => roleCounts.GetValueOrDefault(r));
                if (n > 0) RoleBreakdown.Add(new FleetStat(label, n, c));
            }
            AddRole("LOGI",   BrLogi,   ShipRole.Logi, ShipRole.CapLogi);
            AddRole("BOOST",  BrBoost,  ShipRole.Booster);
            AddRole("TACKLE", BrTackle,  ShipRole.Tackle, ShipRole.Bubble);
            AddRole("EWAR",   BrEwar,    ShipRole.EWAR);
            AddRole("SUPP",   BrSupport, ShipRole.Support);
            AddRole("CAP",    BrCap,     ShipRole.Titan, ShipRole.Supercarrier, ShipRole.CapDPS);
            AddRole("DPS",    BrDps,    ShipRole.DPS, ShipRole.Unknown);
            AddRole("INDY",   BrIndy,   ShipRole.Industrial, ShipRole.Mining);

            Ships.Clear();
            foreach (var s in ships) Ships.Add(s);

            Summary = other > 0
                ? $"{shipTotal} ships on scan · {other} drones/structures ignored"
                : $"{shipTotal} ships on scan";
            HasResult = shipTotal > 0;
        }
        catch (Exception ex)
        {
            Summary = $"Couldn't analyze: {ex.Message}";
        }
        finally { IsAnalyzing = false; }
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void CopySummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"D-Scan — {Ships.Sum(s => s.Count)} ships");
        if (RoleBreakdown.Count > 0)
            sb.AppendLine(string.Join("  ·  ", RoleBreakdown.Select(r => $"{r.Count} {r.Label}")));
        sb.AppendLine("---");
        foreach (var s in Ships)
            sb.AppendLine($"{s.Count}x {s.TypeName} [{s.RoleTag}]");

        try
        {
            Clipboard.SetText(sb.ToString());
            Summary = "Copied summary to clipboard — paste into comms.";
        }
        catch (Exception ex)
        {
            Summary = $"Couldn't copy: {ex.Message}";
        }
    }

    // ── Role tag / colour / sort priority ──────────────────────────────────────
    private static string Tag(ShipRole r) => r switch
    {
        ShipRole.Logi         => "LOGI",
        ShipRole.CapLogi      => "FAX",
        ShipRole.Booster      => "BOOST",
        ShipRole.Titan        => "TITAN",
        ShipRole.Supercarrier => "SUPER",
        ShipRole.CapDPS       => "CAP",
        ShipRole.Tackle       => "TACKLE",
        ShipRole.Bubble       => "DICTOR",
        ShipRole.EWAR         => "EWAR",
        ShipRole.Support      => "SUPPORT",
        ShipRole.Industrial   => "IND",
        ShipRole.Mining       => "MINE",
        _                     => "DPS",
    };

    private static int Priority(string tag) => tag switch
    {
        "TITAN" or "SUPER" or "CAP" or "FAX" => 0,
        "LOGI"   => 1,
        "BOOST"  => 2,
        "EWAR"   => 3,
        "TACKLE" or "DICTOR" => 4,
        "SUPPORT" => 5,
        "DPS"    => 6,
        _        => 7,
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

    private static readonly Brush BrLogi   = Frozen(0x3f, 0xae, 0x8f);
    private static readonly Brush BrBoost  = Frozen(0x5a, 0x8f, 0xd6);
    private static readonly Brush BrTackle = Frozen(0xd4, 0x6a, 0x6a);
    private static readonly Brush BrEwar    = Frozen(0x9b, 0x7b, 0xd4);
    private static readonly Brush BrSupport = Frozen(0x4d, 0xb8, 0xd4);
    private static readonly Brush BrCap     = Frozen(0xd4, 0xa4, 0x49);
    private static readonly Brush BrIndy   = Frozen(0x6b, 0x76, 0x89);
    private static readonly Brush BrDps    = Frozen(0x9a, 0xa3, 0xb3);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
