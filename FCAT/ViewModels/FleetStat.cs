using System.Windows.Media;

namespace FCAT.ViewModels;

/// <summary>A role tally chip in the composition header (e.g. "4 LOGI").</summary>
public record FleetStat(string Label, int Count, Brush Color);

/// <summary>A boost-link coverage chip (e.g. "Shield ×2", or "Armor —" when uncovered).</summary>
public record BoostStat(string Label, int Count, Brush Color)
{
    public string CountText => Count > 0 ? $"×{Count}" : "—";
}

/// <summary>A fleet-composition advisory chip (e.g. "No logistics", "Low logi 4%").</summary>
public record FleetAdvisory(string Text, Brush Color);
