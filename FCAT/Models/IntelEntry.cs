namespace FCAT.Models;

public enum IntelKind { Kill, Report }

/// <summary>Whose loss a killmail is, from the FC's point of view.</summary>
public enum KillSide { Unknown, Friendly, Other }

/// <summary>Normalised report status - clear / no-visual / incoming - for colour-coding.</summary>
public enum IntelStatus { None, Clear, NoVisual, Incoming }

/// <summary>One row in the combined intel feed - a killmail (zKill) or an intel-channel report,
/// parsed into columns: system · status · details.</summary>
public class IntelEntry
{
    public DateTime    Time   { get; init; }
    public IntelKind   Kind   { get; init; }
    public string      System { get; init; } = string.Empty;   // solar system (canonical)
    public IntelStatus Status { get; init; }
    public string      Detail { get; init; } = string.Empty;   // pilots/ship (reports) or "<ship> down" (kills)
    public string      Meta   { get; init; } = string.Empty;   // reporter name / ISK value
    public string?     Url    { get; init; }

    /// <summary>For kills: ours or theirs. Reports are always <see cref="KillSide.Unknown"/>.</summary>
    public KillSide    Side   { get; init; }

    /// <summary>
    /// "KILL" and "LOSS" rather than one word for both. An FC scanning the feed mid-fight needs to
    /// know in one glance whether that was one of ours going down.
    /// </summary>
    public string Tag => Kind == IntelKind.Kill
        ? (Side == KillSide.Friendly ? "LOSS" : "KILL")
        : "INTEL";
    public bool   IsKill    => Kind == IntelKind.Kill;
    public bool   HasUrl    => !string.IsNullOrEmpty(Url);
    public bool   HasStatus => Status != IntelStatus.None;

    public string StatusText => Status switch
    {
        IntelStatus.Clear    => "CLR",
        IntelStatus.NoVisual => "NV",
        IntelStatus.Incoming => "INC",
        _                    => string.Empty,
    };
}
