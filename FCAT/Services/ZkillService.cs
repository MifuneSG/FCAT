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

    /// <summary>Recent killmails for a system (newest first), or empty on failure.</summary>
    public async Task<List<ZkillEntry>> GetRecentSystemKillsAsync(int systemId)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://zkillboard.com/api/systemID/{systemId}/");
            request.Headers.Add("User-Agent", UserAgent);
            request.Headers.Add("Accept-Encoding", "gzip");

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<ZkillEntry>>(json) ?? [];
        }
        catch
        {
            return [];   // network/parse hiccup — feed just won't update this tick
        }
    }
}
