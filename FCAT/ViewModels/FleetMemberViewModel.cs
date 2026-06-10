using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;

namespace FCAT.ViewModels;

public partial class FleetMemberViewModel : ObservableObject
{
    private readonly FleetMember _member;
    private readonly Func<FleetMemberViewModel, Task>? _kickHandler;
    private readonly Action<FleetMemberViewModel>? _moveHandler;

    public FleetMemberViewModel(FleetMember member,
                                Func<FleetMemberViewModel, Task>? kickHandler = null,
                                Action<FleetMemberViewModel>? moveHandler = null)
    {
        _member = member;
        _kickHandler = kickHandler;
        _moveHandler = moveHandler;
        UpdateFrom(member);
    }

    /// <summary>True when this row supports kick/move actions (i.e. not the FC themselves).</summary>
    public bool CanManage => _kickHandler != null;

    [RelayCommand]
    private async Task Kick()
    {
        if (_kickHandler != null) await _kickHandler(this);
    }

    [RelayCommand]
    private void Move() => _moveHandler?.Invoke(this);

    [ObservableProperty] private string _characterName   = string.Empty;
    [ObservableProperty] private string _shipTypeName    = string.Empty;
    [ObservableProperty] private string _solarSystemName = string.Empty;
    [ObservableProperty] private string _roleName        = string.Empty;
    [ObservableProperty] private bool   _takesFleetWarp;

    /// <summary>
    /// Ship fleet role determined from ESI group_id lookup.
    /// Updating this property automatically notifies all badge-computed properties.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleBadge))]
    [NotifyPropertyChangedFor(nameof(RoleBadgeBrush))]
    [NotifyPropertyChangedFor(nameof(RoleBadgeVisible))]
    private ShipRole _shipRole = ShipRole.Unknown;

    public int    CharacterId => _member.CharacterId;
    public long   WingId      => _member.WingId;
    public long   SquadId     => _member.SquadId;
    public string Role        => _member.Role;
    public int    ShipTypeId  => _member.ShipTypeId;

    /// <summary>True for the squad's commander (ESI role "squad_commander") — highlighted in the list.</summary>
    public bool IsSquadCommander => _member.Role == "squad_commander";

    // ── Boost loadout (from the boost channel) ───────────────────────────────
    /// <summary>Compact category summary, e.g. "Shield · Skirmish". Empty if not a known booster.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBoosts))]
    private string _boostSummary = string.Empty;

    /// <summary>Full charge detail for the tooltip, e.g. "Shield Harmonizing Charge — Shield resist".</summary>
    [ObservableProperty] private string _boostDetail = string.Empty;

    public bool HasBoosts => !string.IsNullOrEmpty(BoostSummary);

    /// <summary>Distinct boost categories this pilot is providing (for fleet coverage tallies).</summary>
    public IReadOnlyList<BoostCategory> BoostCategories { get; set; } = [];

    /// <summary>True when the pilot is currently sitting in a pod (ship lost).</summary>
    [ObservableProperty] private bool _isInCapsule;

    // ── EVE image server URLs ─────────────────────────────────────────────────
    public string PortraitUrl => $"https://images.evetech.net/characters/{_member.CharacterId}/portrait?size=64";
    public string ShipIconUrl => $"https://images.evetech.net/types/{_member.ShipTypeId}/icon?size=32";

    // ── Role badge ────────────────────────────────────────────────────────────
    /// <summary>Short label shown on the fleet row — empty string means no badge (DPS / Unknown).</summary>
    public string RoleBadge => ShipRole switch
    {
        ShipRole.Logi        => "LOGI",
        ShipRole.CapLogi     => "FAX",
        ShipRole.Booster     => "BOOST",
        ShipRole.Titan       => "TITAN",
        ShipRole.Supercarrier => "SUPER",
        ShipRole.CapDPS      => "CAP",
        ShipRole.Tackle      => "TCKL",
        ShipRole.Bubble      => "INTD",
        ShipRole.EWAR        => "EWAR",
        ShipRole.Support     => "SUPP",
        ShipRole.Industrial  => "IND",
        ShipRole.Mining      => "MINE",
        _                    => string.Empty   // DPS and Unknown — no badge
    };

    public bool RoleBadgeVisible => !string.IsNullOrEmpty(RoleBadge);

    public SolidColorBrush RoleBadgeBrush => ShipRole switch
    {
        ShipRole.Logi or ShipRole.CapLogi       => Frozen(0x3f, 0xae, 0x8f),  // teal-green
        ShipRole.Booster                         => Frozen(0x5a, 0x8f, 0xd6),  // calm blue
        ShipRole.Titan or ShipRole.Supercarrier  => Frozen(0xd4, 0xa4, 0x49),  // gold
        ShipRole.CapDPS                          => Frozen(0xc9, 0x88, 0x3e),  // amber
        ShipRole.Tackle or ShipRole.Bubble       => Frozen(0xd4, 0x6a, 0x6a),  // rose
        ShipRole.EWAR                            => Frozen(0x9b, 0x7b, 0xd4),  // violet
        ShipRole.Support                         => Frozen(0x4d, 0xb8, 0xd4),  // cyan
        ShipRole.Industrial or ShipRole.Mining   => Frozen(0x6b, 0x76, 0x89),  // slate
        _                                        => Brushes.Transparent          // DPS / Unknown — no accent
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ── Data update ───────────────────────────────────────────────────────────
    public void UpdateFrom(FleetMember member)
    {
        CharacterName   = member.CharacterName   ?? string.Empty;
        ShipTypeName    = member.ShipTypeName    ?? string.Empty;
        SolarSystemName = member.SolarSystemName ?? string.Empty;
        RoleName        = member.RoleName;
        TakesFleetWarp  = member.TakesFleetWarp;
    }
}
