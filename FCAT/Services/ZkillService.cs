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
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("zkill", $"{path}/{id} -> {(int)response.StatusCode}");
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<ZkillEntry>>(json) ?? [];
        }
        catch (Exception ex)
        {
            // "No kills" and "could not read the kills" look identical from outside, so say which.
            Log.Warn("zkill", $"{path}/{id} failed", ex);
            return [];
        }
    }

    /// <summary>The grouped "battle" at a system + EVE-time (/api/related/&lt;sys&gt;/&lt;yyyyMMddHHmm&gt;/).
    /// zKill splits the fight into two teams with pilot/ship/alliance names + the zkb ISK value per kill.
    /// This is the source for the battle report - it has the fresh fight when the systemID list still
    /// doesn't. Null on failure.
    ///
    /// <para><b>The timestamp is an HOUR, not a moment.</b> zKill buckets a battle by the hour and
    /// rejects anything else outright: minutes of 30 return a 500 carrying
    /// <c>InvalidArgumentException: Minutes must be 00</c> from its own RelatedReport. Anchors are
    /// taken whenever first blood lands, so 59 minutes in 60 would have been thrown away. We round
    /// DOWN, which is also the right direction: the report for hour H spans H-1 to H+2, so a fight
    /// that runs past the hour is still inside its own bucket.</para></summary>
    public async Task<ZkillRelated?> GetBattleAsync(int systemId, DateTime atUtc)
    {
        var stamp = new DateTime(atUtc.Year, atUtc.Month, atUtc.Day, atUtc.Hour, 0, 0, DateTimeKind.Utc)
                        .ToString("yyyyMMddHHmm");
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://zkillboard.com/api/related/{systemId}/{stamp}/");
            request.Headers.Add("User-Agent", UserAgent);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("zkill", $"related/{systemId}/{stamp} -> {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ZkillRelated>(json);
        }
        catch (Exception ex)
        {
            // A swallowed parse failure here reads as "no kills", which is the one answer an FC
            // will believe and never question. Say what actually happened.
            Log.Warn("zkill", $"related/{systemId}/{stamp} failed", ex);
            return null;
        }
    }
}
