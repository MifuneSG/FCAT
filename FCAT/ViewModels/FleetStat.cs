using System.Collections.Generic;
using System.Windows.Media;

namespace FCAT.ViewModels;

/// <summary>One hull line behind a role chip (e.g. "4 Guardian").</summary>
public record HullCount(string Ship, int Count)
{
    public string Line => $"{Count}  {Ship}";
}

/// <summary>
/// A role tally chip in the composition header (e.g. "4 LOGI"). Hulls breaks the count into its
/// ship mix for the hover tooltip; Detail is an optional extra line (used for DPS attrition).
/// </summary>
public record FleetStat(string Label, int Count, Brush Color, IReadOnlyList<HullCount> Hulls, string Detail = "")
{
    /// <summary>Chip with no hull breakdown (e.g. the d-scan role tallies).</summary>
    public FleetStat(string label, int count, Brush color) : this(label, count, color, []) { }

    public bool HasDetail => Detail.Length > 0;
}

/// <summary>One booster behind a link chip: who's running it and which charges.</summary>
public record BoostSource(string Pilot, string Charges);

/// <summary>A boost-link coverage chip (e.g. "Shield ×2", or "Armor -" when uncovered).</summary>
public record BoostStat(string Label, int Count, Brush Color, IReadOnlyList<BoostSource> Sources)
{
    public string CountText  => Count > 0 ? $"×{Count}" : "—";
    public bool   HasSources => Sources.Count > 0;
    public bool   NoCoverage => Sources.Count == 0;
}

/// <summary>A fleet-composition advisory chip (e.g. "No logistics", "Low logi 4%"). Explain is the
/// hover text: why it fired and the rule of thumb behind it.</summary>
public record FleetAdvisory(string Text, Brush Color, string Explain = "")
{
    public bool HasExplain => Explain.Length > 0;
}
