using FCAT.Models;

namespace FCAT.Services;

/// <summary>One ship on the battle report: a kill (enemy loss) or a loss (friendly, for the SRP sheet).</summary>
public record BattleReportLine(string Pilot, string Ship, double Isk, string IskText, string ZkillUrl);

/// <summary>
/// Battle report for one op: ISK destroyed vs lost, efficiency, and the kill / loss lists, built from
/// the killmails zKillboard groups into the fight. Our side is decided from the fleet's own character
/// ids plus the FC's alliance, so victims on our side are losses (the SRP sheet) and the rest are kills.
/// </summary>
public class BattleReport
{
    public double IskDestroyed { get; init; }
    public double IskLost      { get; init; }
    public int    Efficiency   { get; init; }   // 0-100, ISK-weighted

    public IReadOnlyList<BattleReportLine> Kills  { get; init; } = [];   // enemy losses (what we killed)
    public IReadOnlyList<BattleReportLine> Losses { get; init; } = [];   // friendly losses (SRP sheet)

    public int    KillCount        => Kills.Count;
    public int    LossCount        => Losses.Count;
    public string IskDestroyedText => FormatIsk(IskDestroyed);
    public string IskLostText      => FormatIsk(IskLost);
    public string EfficiencyText   => $"{Efficiency}%";
    public bool   HasKills         => Kills.Count  > 0;
    public bool   HasLosses        => Losses.Count > 0;

    public static string FormatIsk(double isk) => isk switch
    {
        >= 1_000_000_000 => $"{isk / 1_000_000_000:0.0}B ISK",
        >= 1_000_000     => $"{isk / 1_000_000:0.0}M ISK",
        >= 1_000         => $"{isk / 1_000:0.0}K ISK",
        _                => $"{isk:0} ISK",
    };
}

/// <summary>The outcome of a build, so the AAR can tell "zKill has nothing yet" (wait and refresh)
/// apart from "zKill did not answer" (a bug or an outage, and refreshing will not help).
///
/// <paramref name="MainFight"/> is whichever anchor actually produced the fight, which is rarely the
/// first one: an op anchors on first blood, and first blood is usually something small dying at the
/// staging gate before the fleet moves out. Null until a build finds kills.</summary>
public record BattleReportResult(BattleReport? Report, bool ZkillAnswered, CombatAnchor? MainFight = null);

/// <summary>
/// Assembles a <see cref="BattleReport"/> from zKillboard's related-kills grouping + ESI killmail
/// detail. Killmails lag a few minutes behind a fight, so a report built right after the fight fills
/// in on a later rebuild - the AAR view offers a manual refresh for that.
/// </summary>
public class BattleReportService(ZkillService zkill, EsiService esi)
{
    /// <summary>
    /// Builds one aggregated report across every combat window the op racked up (see
    /// <see cref="CombatAnchor"/>) from zKill's grouped "battle" endpoint. zKill splits each fight into
    /// two teams; we pick the team our side is on (fleet char ids / FC alliance), count that team's
    /// victims as our losses (SRP) and the other team's victims as our kills, using the ISK value zKill
    /// already provides per kill. Deduped by kill id across windows. The report is null when zKill has
    /// nothing yet; <see cref="BattleReportResult.ZkillAnswered"/> says whether it was even asked
    /// successfully.
    /// </summary>
    public async Task<BattleReportResult> BuildAsync(IReadOnlyList<CombatAnchor> anchors,
                                                     IReadOnlyCollection<int> friendlyCharIds, int friendlyAllianceId)
    {
        if (anchors.Count == 0) return new(null, true);
        if (esi.DemoMode) return new(DemoData.BattleReport(), true, anchors[0]);

        var seen   = new HashSet<long>();
        var kills  = new List<BattleReportLine>();
        var losses = new List<BattleReportLine>();
        double destroyed = 0, lost = 0;

        // Is this ship one of ours - a pilot in the fleet, or anyone in our alliance?
        bool IsOurs(ZkillTeamShip s) =>
            (s.CharacterId is int c && friendlyCharIds.Contains(c))
            || (friendlyAllianceId != 0 && s.AllianceId == friendlyAllianceId);

        // How many ships on a team belong to our side - used to tell our team from the enemy's.
        int FriendlyScore(ZkillTeam? t) => t?.List?.Count(IsOurs) ?? 0;

        // Pull each destroyed ship (isVictim) on a team into a line list, with zKill's ISK value.
        // <paramref name="oursOnly"/> is set for our own team, because zKill's teams are the whole
        // SIDE of a fight, not our fleet: a blue from another alliance dying next to us belongs on
        // nobody's SRP sheet. The enemy team takes everything, since a kill is a kill.
        (List<BattleReportLine> lines, double sum) Collect(ZkillTeam? team, bool oursOnly = false)
        {
            var lines = new List<BattleReportLine>();
            double sum = 0;
            if (team?.List == null) return (lines, sum);
            foreach (var s in team.List.Where(x => x.IsVictim && x.KillId is long && (!oursOnly || IsOurs(x))))
            {
                if (!seen.Add(s.KillId!.Value)) continue;
                var value = team.Kills != null && team.Kills.TryGetValue(s.KillId!.Value.ToString(), out var k)
                            && k.Zkb != null ? k.Zkb.TotalValue : 0;
                lines.Add(new BattleReportLine(
                    s.CharacterName ?? "?", s.ShipName ?? "?", value, BattleReport.FormatIsk(value),
                    $"https://zkillboard.com/kill/{s.KillId}/"));
                sum += value;
            }
            return (lines, sum);
        }

        var answered = false;

        // The biggest fight the op had, by ships destroyed. This is what the Battle Report button
        // should open - see BattleReportResult.MainFight.
        CombatAnchor? mainFight = null;
        var mainFightSize = 0;

        foreach (var anchor in anchors)
        {
            var battle = await zkill.GetBattleAsync(anchor.SystemId, anchor.AtUtc);
            answered |= battle != null;
            var teams  = new[] { battle?.Summary?.TeamA, battle?.Summary?.TeamB }
                         .Where(t => t?.List is { Count: > 0 }).ToList();
            if (teams.Count == 0) continue;

            var ourTeam = teams.OrderByDescending(FriendlyScore).First();
            if (FriendlyScore(ourTeam) == 0) continue;   // our side isn't in this fight - skip it

            var (ourLosses, lostIsk) = Collect(ourTeam, oursOnly: true);
            losses.AddRange(ourLosses);
            lost += lostIsk;

            var here = ourLosses.Count;
            foreach (var enemy in teams.Where(t => t != ourTeam))
            {
                var (enemyLosses, killedIsk) = Collect(enemy);
                kills.AddRange(enemyLosses);
                destroyed += killedIsk;
                here += enemyLosses.Count;
            }

            if (here > mainFightSize) { mainFightSize = here; mainFight = anchor; }
        }

        if (kills.Count == 0 && losses.Count == 0) return new(null, answered);

        var total = destroyed + lost;
        return new BattleReportResult(new BattleReport
        {
            IskDestroyed = destroyed,
            IskLost      = lost,
            Efficiency   = total > 0 ? (int)Math.Round(100 * destroyed / total) : 0,
            Kills        = kills .OrderByDescending(k => k.Isk).ToList(),
            Losses       = losses.OrderByDescending(l => l.Isk).ToList(),
        }, answered, mainFight);
    }
}
