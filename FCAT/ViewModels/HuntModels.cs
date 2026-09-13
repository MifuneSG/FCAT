using CommunityToolkit.Mvvm.ComponentModel;

namespace FCAT.ViewModels;

/// <summary>How busy a system's ratting is, as a band rather than a raw number - the FC is choosing
/// between systems, not auditing them.</summary>
public enum RattingLevel
{
    Quiet,
    Light,
    Steady,
    Heavy
}

/// <summary>
/// One system on the hunt board: how far it is, how much ratting is happening, and what it opens up
/// if you stage there.
///
/// <para><b>OnwardTotal / OnwardNew / OnwardRegions</b> are the coverage question - jump there and how
/// many more systems come into range, how many of those the earlier hops could NOT already reach, and
/// how many regions that spans. That is what decides where a cyno is worth seeding.</para>
/// </summary>
public record HuntRow(
    int SystemId, string Name, string Region, double LightYears,
    int NpcKills, int NpcDelta, int PvpKills, RattingLevel Level,
    bool OutOfRegion, bool NewFromHere,
    int OnwardTotal, int OnwardNew, int OnwardRegions)
{
    /// <summary>Position on the board, assigned after ranking.</summary>
    public int Rank { get; set; }

    /// <summary>Activity bar width, scaled against the busiest system on the board.</summary>
    public double BarWidth { get; set; }

    public string RangeText        => $"{LightYears:0.0} ly";
    public string NpcText          => NpcKills     <= 0 ? "-" : NpcKills.ToString("N0");
    public string DeltaText        => NpcDelta     <= 0 ? string.Empty : $"+{NpcDelta:N0}";
    public string PvpText          => PvpKills     <= 0 ? string.Empty : PvpKills.ToString();
    public string OnwardText       => OnwardTotal  <= 0 ? "-" : OnwardTotal.ToString("N0");
    public string OnwardNewText    => OnwardNew    <= 0 ? string.Empty : $"+{OnwardNew:N0}";
    public string OnwardRegionsText => OnwardRegions <= 0 ? string.Empty : $"{OnwardRegions} reg";

    /// <summary>Ratting is climbing, not just historically busy - someone is out there now.</summary>
    public bool IsRising    => NpcDelta > 0;

    /// <summary>Somebody else is already fighting here.</summary>
    public bool IsContested => PvpKills > 0;

    /// <summary>Only reachable because of this hop - the earlier hops couldn't get here.</summary>
    public bool ShowNew     => NewFromHere;

    /// <summary>Worth naming the region only when it isn't the one the FC started in.</summary>
    public bool ShowRegion  => OutOfRegion && Region.Length > 0;
}

/// <summary>One staging hop in the chain. Only the last one can be removed, so the route stays a
/// route rather than something with a hole in the middle.</summary>
public partial class HuntHop : ObservableObject
{
    public int    SystemId { get; init; }
    public string Name     { get; init; } = string.Empty;

    [ObservableProperty] private bool _isLast;
}

/// <summary>A region the current range touches, and how many of its systems are in it. Doubles as a
/// filter chip.</summary>
public partial class HuntRegion : ObservableObject
{
    public string Name  { get; init; } = string.Empty;
    public int    Count { get; init; }

    public string Label => $"{Name}  {Count}";

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// A system plotted on the hunt map. Unlike the constellation map this is a range plot, not a gate
/// schematic - position is real space flattened, and <see cref="Size"/> carries ratting volume, so a
/// busy system reads as a bigger dot.
/// </summary>
public record HuntMapNode(
    double Left, double Top, int SystemId, string Name,
    bool InRange, bool IsOrigin, string Tone, string Ring, double Size,
    bool ShowLabel, string Tip, string RangeLine, string StatsLine)
{
    /// <summary>The highlight ring sits just outside the dot.</summary>
    public double RingSize => Size + 7.0;

    /// <summary>Width of the dot's layout box. Left/Top place its top-left corner.</summary>
    public const double NodeBox = 24;

    /// <summary>
    /// The label needs its own, wider box. WPF will not arrange a centred child wider than the space
    /// it is given, so a name laid out inside the dot's 24px box is simply cut off at 24px - which is
    /// what turned "F-88PJ" into "F-88P". This box is wide enough for any system name.
    /// </summary>
    public const double LabelBox = 160;

    /// <summary>Label box, shifted so its centre still lands on the dot.</summary>
    public double LabelLeft => Left - (LabelBox - NodeBox) / 2;
}

/// <summary>A line between two hops on the hunt map - the route, not a gate.</summary>
public record HuntMapLink(double X1, double Y1, double X2, double Y2);
