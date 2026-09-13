using System.Net.Http;
using System.Text.Json;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// Thin wrapper over the public zKillboard API. zKill returns only killmail id + a "zkb" envelope
/// (hash, value); full detail (ship, victim, time) is fetched from ESI with that hash.
/// zKill asks third-party tools to send an identifying User-Agent and not hammer the API, so this
/// is polled conservatively (see the intel feed's interval).
/// </summary>
public class ZkillService(HttpClient httpClient)
{
    private const string UserAgent = "FCAT/1.1 (Fleet Commander Assistance Tool; +https://github.com/MifuneSG/FCAT)";

    /// <summary>How wide a net the kill feed casts - one system, the constellation, or the region.</summary>
    public enum KillScope { System, Constellation, Region }

    /// <summary>Recent killmails for a system (newest first), or empty on failure.</summary>
    public Task<List<ZkillEntry>> GetRecentSystemKillsAsync(int systemId) =>
        GetRecentKillsAsync(KillScope.System, systemId);

    /// <summary>
    /// Recent killmails for a system, constellation or region, newest first, empty on failure.
    /// (/api/systemID|constellationID|regionID/ -> bare array of {killmail_id, zkb}; the detail -
    /// ship, victim, time - is fetched from ESI with the hash.)
    ///
    /// These list endpoints are cached hard on zKill's side: measured 2026-09-13, the newest entry
    /// for Jita was over four hours old. So they are NOT a live feed and NOT usable for the battle
    /// report, which needs the fight as it happens - see <see cref="GetBattleAsync"/>. Anything
    /// reading this must expect kills to be hours old (zKill's "pastSeconds" modifier narrows the
    /// window but does not make the data any fresher).
    /// </summary>
    public async Task<List<ZkillEntry>> GetRecentKillsAsync(KillScope scope, int id)
    {
        var path = scope switch
        {
            KillScope.Constellation => "constellationID",
            KillScope.Region        => "regionID",
            _                       => "systemID",
        };

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://zkillboard.com/api/{path}/{id}/");
            request.Headers.Add("User-Agent", UserAgent);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<ZkillEntry>>(json) ?? [];
        }
        catch
        {
            return [];   // network/parse hiccup - caller just gets nothing this tick
        }
    }

    /// <summary>The grouped "battle" at a system + EVE-time (/api/related/&lt;sys&gt;/&lt;yyyyMMddHHmm&gt;/).
    /// zKill splits the fight into two teams with pilot/ship/alliance names + the zkb ISK value per kill.
    /// This is the source for the battle report - it has the fresh fight when the systemID list still
    /// doesn't. Null on failure. Uses a ~1h window around the timestamp (zKill's own grouping).</summary>
    public async Task<ZkillRelated?> GetBattleAsync(int systemId, DateTime atUtc)
    {
        try
        {
            var stamp = atUtc.ToString("yyyyMMddHHmm");
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://zkillboard.com/api/related/{systemId}/{stamp}/");
            request.Headers.Add("User-Agent", UserAgent);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ZkillRelated>(json);
        }
        catch
        {
            return null;
        }
    }
}
