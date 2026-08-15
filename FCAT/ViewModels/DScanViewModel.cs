using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>One ship row on a d-scan result. Manual = added by hand, not seen on the scan.</summary>
public record DScanShip(string TypeName, int Count, string RoleTag, Brush Color, bool Manual = false)
{
    public string CountText => Manual ? $"{Count}×*" : $"{Count}×";
}

/// <summary>A ship class tally ("Interdictor 7"), the group names behind the hulls.</summary>
public record ScanClass(string Name, int Count);

/// <summary>An alliance or corporation row on a local-scan result.</summary>
public record ScanOrg(string Name, string Ticker, int Count, bool Blue)
{
    public string Tag       => Blue ? "BLUE" : "NEUT";
    public bool   HasTicker => Ticker.Length > 0;
    public bool   IsNeutral => !Blue;
}

/// <summary>
/// Combined scan tool: paste a directional scan OR your Local member list into one box and
/// Analyze. It auto-detects which it is per line - d-scan rows start with a numeric type ID,
/// Local names don't.
///
/// A d-scan gives ships by hull, ships by class, and the fleet's base-hull mass. Local gives the
/// alliance and corporation breakdowns side by side, each marked blue or neutral against your own
/// affiliation. Every pilot counts toward both boards, which is why the corp count runs higher
/// than the alliance count.
/// </summary>
public partial class DScanViewModel : ObservableObject
{
    private readonly EsiService _esi;
    private readonly EsiAuthService _auth;

    // Session caches
    private readonly Dictionary<int, int>    _group     = [];
    private readonly Dictionary<int, string> _groupName = [];
    private readonly Dictionary<int, int>    _category  = [];
    private readonly Dictionary<int, double> _mass      = [];
    private readonly Dictionary<int, string> _name      = [];
    private readonly Dictionary<int, string> _ticker    = [];

    private const int ShipCategory = 6;
    private int  _ownAllianceId, _ownCorpId;
    private bool _ownLoaded, _lastWasLocal;

    // The ships the paste actually contained, kept so manually added recons can be folded in
    // without re-parsing or re-hitting ESI.
    private Dictionary<int, int> _scanned = [];

    // Local, reduced to the comms callout: who isn't blue, one entry per neutral pilot.
    private List<(int Count, string Label)> _neutralComms = [];
    private int _localTotal, _localBlue;

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
    [NotifyCanExecuteChangedFor(nameof(CopyImageCommand))]
    private bool _hasResult;

    /// <summary>Which result boards to show - the two scan kinds have different breakdowns.</summary>
    [ObservableProperty] private bool _isShipResult;
    [ObservableProperty] private bool _isLocalResult;

    public ObservableCollection<FleetStat> RoleBreakdown { get; } = [];
    public ObservableCollection<DScanShip> Ships         { get; } = [];
    public ObservableCollection<ScanClass> ShipClasses   { get; } = [];
    public ObservableCollection<ScanOrg>   Alliances     { get; } = [];
    public ObservableCollection<ScanOrg>   Corporations  { get; } = [];

    public bool HasShipClasses => ShipClasses.Count > 0;

    // Shortened copies for the shareable image. The panel can scroll; a picture can't, and a Local
    // with eighty corps would render a strip too tall to read once a chat client scales it down.
    public ObservableCollection<DScanShip> ExportShips      { get; } = [];
    // Corps run to dozens where alliances run to a handful, so the image lays them out in two
    // columns under a full-width alliance board rather than beside it, where they left a hole.
    public ObservableCollection<ScanOrg>   ExportCorpsLeft  { get; } = [];
    public ObservableCollection<ScanOrg>   ExportCorpsRight { get; } = [];

    [ObservableProperty] private string _shipOverflow = string.Empty;
    [ObservableProperty] private string _corpOverflow = string.Empty;

    /// <summary>Explains the asterisk on hand-added rows, so a shared picture can't imply the
    /// d-scan actually saw them.</summary>
    [ObservableProperty] private string _manualNote = string.Empty;

    /// <summary>Which scan the image is of, and when it was taken - a shared scan without a
    /// timestamp is worthless ten minutes later.</summary>
    [ObservableProperty] private string _scanTitle = string.Empty;
    [ObservableProperty] private string _scanStamp = string.Empty;

    private const int ImageListCap = 22;

    private void BuildExportLists()
    {
        ExportShips.Clear();
        foreach (var s in Ships.Take(ImageListCap)) ExportShips.Add(s);
        var shipRest = Ships.Skip(ImageListCap).ToList();
        ShipOverflow = shipRest.Count > 0
            ? $"+{shipRest.Sum(s => s.Count)} more in {shipRest.Count} hull types"
            : string.Empty;

        ManualNote = Ships.Any(s => s.Manual) ? "* added by hand, not seen on d-scan" : string.Empty;

        var shown = Corporations.Take(ImageListCap).ToList();
        var half  = (shown.Count + 1) / 2;   // odd counts leave the longer half on the left
        ExportCorpsLeft.Clear();
        ExportCorpsRight.Clear();
        foreach (var c in shown.Take(half))      ExportCorpsLeft.Add(c);
        foreach (var c in shown.Skip(half))      ExportCorpsRight.Add(c);

        var corpRest = Corporations.Skip(ImageListCap).ToList();
        CorpOverflow = corpRest.Count > 0
            ? $"+{corpRest.Sum(c => c.Count)} pilots in {corpRest.Count} more corps"
            : string.Empty;

        ScanStamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " EVE";
    }

    // Fleet mass of everything on the scan (base hull only - ESI can't see other pilots' fits).
    [ObservableProperty] private bool   _hasMass;
    [ObservableProperty] private string _massText = string.Empty;
    [ObservableProperty] private string _massNote = string.Empty;

    private const string Hint = "Paste a d-scan or your Local member list, the tool detects which, then Analyze.";

    [RelayCommand]
    private void Clear()
    {
        Input = string.Empty;
        RoleBreakdown.Clear();
        Ships.Clear();
        ShipClasses.Clear();
        Alliances.Clear();
        Corporations.Clear();
        ClearRecons();
        _scanned = [];
        _neutralComms = [];
        _localTotal = _localBlue = 0;
        HasResult = IsShipResult = IsLocalResult = HasMass = false;
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
                Summary = "Nothing recognized. Paste a d-scan or a list of Local names.";
            else if (shipLines >= names.Count)
            {
                ClearRecons();               // a fresh scan supersedes the last manual additions
                _scanned = byType;
                await RenderShipsAsync();    // looks like a d-scan
            }
            else
                await AnalyzeLocalAsync(names);   // looks like a Local list
        }
        catch (Exception ex) { Summary = $"Couldn't analyze: {ex.Message}"; }
        finally { IsAnalyzing = false; }
    }

    // D-Scan
    /// <summary>Renders the scanned ships plus any hand-added recons as one result.</summary>
    private async Task RenderShipsAsync()
    {
        _lastWasLocal = false;

        var manual = await ReconCountsAsync();
        var byType = new Dictionary<int, int>(_scanned);
        foreach (var (tid, n) in manual) byType[tid] = byType.GetValueOrDefault(tid) + n;

        var ids = byType.Keys.ToList();

        // One type fetch carries the name, the group and the hull mass, so the class breakdown and
        // the mass readout cost nothing beyond what the role classifier already needed.
        var need = ids.Where(id => !_group.ContainsKey(id)).ToList();
        foreach (var (tid, info) in await _esi.GetShipTypeInfosAsync(need))
        {
            _group[tid] = info.GroupId;
            if (info.Name.Length > 0) _name[tid] = info.Name;
            if (info.Mass > 0) _mass[tid] = info.Mass;
        }
        // Older type entries may predate the name/mass fields being kept - fill any gaps by batch.
        var unnamed = ids.Where(id => !_name.ContainsKey(id)).ToList();
        if (unnamed.Count > 0)
            foreach (var kv in await _esi.ResolveNamesAsync(unnamed)) _name[kv.Key] = kv.Value;

        var groups = ids.Where(id => _group.ContainsKey(id)).Select(id => _group[id])
                        .Where(g => !_category.ContainsKey(g)).Distinct();
        foreach (var (gid, info) in await _esi.GetGroupInfosAsync(groups))
        {
            _category[gid]  = info.CategoryId;
            _groupName[gid] = info.Name;
        }

        var roleCounts  = new Dictionary<ShipRole, int>();
        var classCounts = new Dictionary<int, int>();
        var ships = new List<DScanShip>();
        int shipTotal = 0, other = 0, manualTotal = 0;
        double mass = 0;
        var massMissing = false;

        foreach (var (tid, count) in byType)
        {
            if (!_group.TryGetValue(tid, out var gid)) { other += count; continue; }
            if (_category.GetValueOrDefault(gid) != ShipCategory) { other += count; continue; }
            if (ShipRoleClassifier.IsCapsule(gid)) { other += count; continue; }

            var role = ShipRoleClassifier.Classify(tid, gid);
            roleCounts[role]   = roleCounts.GetValueOrDefault(role) + count;
            classCounts[gid]   = classCounts.GetValueOrDefault(gid) + count;
            shipTotal += count;

            if (_mass.TryGetValue(tid, out var kg)) mass += kg * count;
            else massMissing = true;

            var addedByHand = manual.ContainsKey(tid);
            if (addedByHand) manualTotal += manual[tid];
            ships.Add(new DScanShip(_name.GetValueOrDefault(tid, $"Type {tid}"), count,
                                    Tag(role), RoleBrush(role), addedByHand));
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

        ShipClasses.Clear();
        foreach (var c in classCounts.OrderByDescending(c => c.Value)
                                     .ThenBy(c => _groupName.GetValueOrDefault(c.Key, "")))
            ShipClasses.Add(new ScanClass(_groupName.GetValueOrDefault(c.Key, $"Group {c.Key}"), c.Value));
        OnPropertyChanged(nameof(HasShipClasses));

        HasMass  = shipTotal > 0 && mass > 0;
        MassText = WormholeMass.Format(mass);
        MassNote = massMissing
            ? "base hulls, and some did not resolve - the real total is higher"
            : "base hulls, fits not visible to ESI";

        Summary = $"D-scan · {shipTotal} ships"
                + (manualTotal > 0 ? $" · {manualTotal} added by hand" : string.Empty)
                + (other > 0 ? $" · {other} drones/structures ignored" : string.Empty);
        ScanTitle = "D-SCAN";
        HasResult = IsShipResult = true;
        IsLocalResult = false;
        BuildExportLists();
    }

    // Combat recons (Huginn, Lachesis, Rook, Curse) are immune to directional scan, so a clean
    // d-scan is not proof they are absent. The FC can add what they believe is out there; those
    // rows are marked with an asterisk so the readout never passes a guess off as a scan result.
    [ObservableProperty] private int _reconHuginn;
    [ObservableProperty] private int _reconLachesis;
    [ObservableProperty] private int _reconRook;
    [ObservableProperty] private int _reconCurse;

    public bool HasRecons => ReconHuginn + ReconLachesis + ReconRook + ReconCurse > 0;

    private static readonly string[] ReconNames = ["Huginn", "Lachesis", "Rook", "Curse"];
    private Dictionary<string, int>? _reconTypeIds;

    private void ClearRecons()
    {
        ReconHuginn = ReconLachesis = ReconRook = ReconCurse = 0;
    }

    /// <summary>Resolves the four combat-recon hulls to type ids once, then maps the FC's counts.</summary>
    private async Task<Dictionary<int, int>> ReconCountsAsync()
    {
        var wanted = new (string Name, int Count)[]
        {
            ("Huginn", ReconHuginn), ("Lachesis", ReconLachesis),
            ("Rook",   ReconRook),   ("Curse",    ReconCurse),
        }.Where(r => r.Count > 0).ToList();
        if (wanted.Count == 0) return [];

        _reconTypeIds ??= await _esi.ResolveTypeIdsAsync(ReconNames);

        var result = new Dictionary<int, int>();
        foreach (var (name, count) in wanted)
            if (_reconTypeIds.TryGetValue(name, out var tid)) result[tid] = count;
        return result;
    }

    [RelayCommand]
    private async Task ApplyRecons()
    {
        if (_scanned.Count == 0) { Summary = "Analyze a d-scan first, then add the recons you suspect."; return; }
        IsAnalyzing = true;
        try { await RenderShipsAsync(); }
        catch (Exception ex) { Summary = $"Couldn't add those: {ex.Message}"; }
        finally { IsAnalyzing = false; }
        OnPropertyChanged(nameof(HasRecons));
    }

    // Local
    private async Task AnalyzeLocalAsync(List<string> names)
    {
        _lastWasLocal = true;
        await EnsureOwnAffiliationAsync();

        var nameToId = await _esi.ResolveCharacterIdsAsync(names);
        var ids = nameToId.Values.Distinct().ToList();
        if (ids.Count == 0) { Summary = "Couldn't resolve any of those names. Are they exact character names?"; HasResult = false; return; }

        var affs = await _esi.GetAffiliationsAsync(ids);
        var orgIds = affs.SelectMany(a => new[] { a.AllianceId ?? 0, a.CorporationId })
                         .Where(x => x > 0 && !_name.ContainsKey(x)).Distinct();
        foreach (var kv in await _esi.ResolveNamesAsync(orgIds)) _name[kv.Key] = kv.Value;

        // Every pilot counts toward both boards - a pilot is in a corp AND (usually) an alliance,
        // so these are two views of the same people, not a split of them.
        var byAlliance = affs.Where(a => a.AllianceId is > 0)
                             .GroupBy(a => a.AllianceId!.Value)
                             .Select(g => new { Id = g.Key, Count = g.Count(), Blue = g.Key == _ownAllianceId })
                             .OrderByDescending(x => x.Count).ToList();

        // A corp is friendly if it's yours OR it flies under your alliance. Matching on corp id
        // alone marks every one of your own alliance's member corps hostile, which is most of a
        // home-system Local.
        var byCorp = affs.GroupBy(a => a.CorporationId)
                         .Select(g => new
                         {
                             Id = g.Key,
                             Count = g.Count(),
                             Blue = g.Key == _ownCorpId
                                 || (_ownAllianceId > 0 && g.Any(a => a.AllianceId == _ownAllianceId)),
                         })
                         .OrderByDescending(x => x.Count).ToList();

        // Tickers are one call per org. Alliances are few, so they get the recognisable short code;
        // corporations can run to dozens in a busy Local and are left as names.
        await EnsureAllianceTickersAsync(byAlliance.Select(a => a.Id));

        var unaffiliated = affs.Count(a => a.AllianceId is not > 0);
        int total = affs.Count;
        int friendly = _ownAllianceId > 0
            ? affs.Count(a => a.AllianceId == _ownAllianceId)
            : affs.Count(a => a.CorporationId == _ownCorpId);

        // Comms only cares who ISN'T blue. Built as a partition so each neutral pilot is counted
        // once: non-blue alliances by alliance, then the alliance-less by corp. Listing both boards
        // in full would double-count everyone and run to eighty-odd lines.
        _neutralComms = byAlliance.Where(a => !a.Blue)
            .Select(a => (a.Count, Label: _name.GetValueOrDefault(a.Id, $"ID {a.Id}")
                                        + (_ticker.TryGetValue(a.Id, out var t) ? $" [{t}]" : "")))
            .Concat(affs.Where(a => a.AllianceId is not > 0 && a.CorporationId != _ownCorpId)
                        .GroupBy(a => a.CorporationId)
                        .Select(g => (Count: g.Count(), Label: _name.GetValueOrDefault(g.Key, $"ID {g.Key}"))))
            .OrderByDescending(x => x.Count)
            .ToList();
        _localTotal = total;
        _localBlue  = friendly;

        Alliances.Clear();
        foreach (var a in byAlliance)
            Alliances.Add(new ScanOrg(_name.GetValueOrDefault(a.Id, $"ID {a.Id}"),
                                      _ticker.GetValueOrDefault(a.Id, string.Empty), a.Count, a.Blue));
        if (unaffiliated > 0)
            Alliances.Add(new ScanOrg("Pilots without alliance", string.Empty, unaffiliated, false));

        Corporations.Clear();
        foreach (var c in byCorp)
            Corporations.Add(new ScanOrg(_name.GetValueOrDefault(c.Id, $"ID {c.Id}"),
                                         string.Empty, c.Count, c.Blue));

        RoleBreakdown.Clear();
        if (friendly > 0)         RoleBreakdown.Add(new FleetStat("BLUE", friendly, BrBoost));
        if (total - friendly > 0) RoleBreakdown.Add(new FleetStat("OTHER", total - friendly, BrDps));

        Ships.Clear();
        ShipClasses.Clear();
        HasMass = false;

        var unresolved = names.Count - nameToId.Count;
        Summary = $"Local · {total} pilots · {friendly} blue · {total - friendly} neutral"
                + (unresolved > 0 ? $" · {unresolved} not found" : "");
        ScanTitle = "LOCAL SCAN";
        HasResult = IsLocalResult = true;
        IsShipResult = false;
        BuildExportLists();
    }

    private async Task EnsureAllianceTickersAsync(IEnumerable<int> allianceIds)
    {
        var need = allianceIds.Where(id => id > 0 && !_ticker.ContainsKey(id)).Distinct().ToList();
        if (need.Count == 0) return;

        var fetched = await Task.WhenAll(need.Select(async id => (id, info: await _esi.GetAlliancePublicInfoAsync(id))));
        foreach (var (id, info) in fetched)
            if (info != null && info.Ticker.Length > 0) _ticker[id] = info.Ticker;
    }

    private async Task EnsureOwnAffiliationAsync()
    {
        if (_ownLoaded) return;
        _ownLoaded = true;
        var me = (await _esi.GetAffiliationsAsync([_auth.AuthenticatedCharacterId])).FirstOrDefault();
        if (me != null) { _ownAllianceId = me.AllianceId ?? 0; _ownCorpId = me.CorporationId; }
    }

    // Copy for comms
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void CopySummary()
    {
        var sb = new StringBuilder();
        if (_lastWasLocal) BuildLocalComms(sb);
        else               BuildScanComms(sb);

        // Wrap in a Discord code block (```…```) so the scan pastes as fixed-width, un-mangled text.
        var fenced = "```\n" + sb.ToString().TrimEnd() + "\n```";
        try { Clipboard.SetText(fenced); Summary = "Copied as a Discord code block, paste into comms."; }
        catch (Exception ex) { Summary = $"Couldn't copy: {ex.Message}"; }
    }

    /// <summary>
    /// Copies the result as a picture. The scan reads at a glance in chat instead of arriving as a
    /// wall of fixed-width text, and it stays local - no upload, no link, nothing to configure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void CopyImage()
    {
        try
        {
            var card = new Views.ScanExportCard { DataContext = this };
            var image = VisualExporter.Render(card, ImageWidth);
            VisualExporter.CopyToClipboard(image);
            Summary = "Copied as an image, paste into comms.";
        }
        catch (Exception ex) { Summary = $"Couldn't copy the image: {ex.Message}"; }
    }

    /// <summary>Render width of the shared image, before oversampling.</summary>
    private const double ImageWidth = 660;

    /// <summary>How many neutral orgs a comms paste lists before collapsing the tail.</summary>
    private const int CommsListCap = 12;

    /// <summary>
    /// A local paste is a threat callout, not a census. It leads with the count, then names only
    /// who isn't blue - your own alliance is the part nobody needs read back to them.
    /// </summary>
    private void BuildLocalComms(StringBuilder sb)
    {
        var neutral = _localTotal - _localBlue;
        sb.AppendLine($"LOCAL  {_localTotal} pilots  ·  {_localBlue} blue  ·  {neutral} neutral");

        if (_neutralComms.Count == 0)
        {
            sb.AppendLine("All blue.");
            return;
        }

        sb.AppendLine();
        foreach (var (count, label) in _neutralComms.Take(CommsListCap))
            sb.AppendLine($"{count,4}  {label}");

        var rest = _neutralComms.Skip(CommsListCap).ToList();
        if (rest.Count > 0)
            sb.AppendLine($"{rest.Sum(r => r.Count),4}  in {rest.Count} more corps/alliances");
    }

    /// <summary>A d-scan paste stays whole - it's already short, and every hull matters.</summary>
    private void BuildScanComms(StringBuilder sb)
    {
        sb.AppendLine($"D-SCAN  {Ships.Sum(s => s.Count)} ships" + (HasMass ? $"  ·  {MassText}" : ""));
        if (RoleBreakdown.Count > 0)
            sb.AppendLine(string.Join("  ·  ", RoleBreakdown.Select(r => $"{r.Count} {r.Label}")));

        sb.AppendLine();
        foreach (var s in Ships)
            sb.AppendLine($"{s.Count,4}  {s.TypeName,-24} {s.RoleTag}" + (s.Manual ? " *" : ""));

        if (ShipClasses.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(string.Join("  ·  ", ShipClasses.Select(c => $"{c.Count} {c.Name}")));
        }
        if (Ships.Any(s => s.Manual)) sb.AppendLine("* added by hand, not seen on d-scan");
    }

    // Role tag / colour / sort priority (d-scan)
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
