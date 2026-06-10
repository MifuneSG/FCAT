using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using FCAT.Models;

namespace FCAT.Services;

public class EsiService(HttpClient httpClient, EsiAuthService authService)
{
    private const string BaseUrl = "https://esi.evetech.net";

    private async Task<T?> GetAuthenticatedAsync<T>(string path)
    {
        var token = await authService.GetValidAccessTokenAsync();
        if (token == null) return default;

        var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("User-Agent", "FCAT/1.0 (Fleet Commander Assistance Tool)");

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return default;

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json);
    }

    private async Task<T?> GetPublicAsync<T>(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        request.Headers.Add("User-Agent", "FCAT/1.0 (Fleet Commander Assistance Tool)");

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return default;

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json);
    }

    public async Task<CharacterPublicInfo?> GetCharacterPublicInfoAsync(int characterId)
        => await GetPublicAsync<CharacterPublicInfo>($"/v5/characters/{characterId}/");

    public async Task<CorporationPublicInfo?> GetCorporationPublicInfoAsync(int corporationId)
        => await GetPublicAsync<CorporationPublicInfo>($"/v5/corporations/{corporationId}/");

    public async Task<AlliancePublicInfo?> GetAlliancePublicInfoAsync(int allianceId)
        => await GetPublicAsync<AlliancePublicInfo>($"/v3/alliances/{allianceId}/");

    public async Task<CharacterFleetInfo?> GetCharacterFleetAsync(int characterId)
        => await GetAuthenticatedAsync<CharacterFleetInfo>($"/v1/characters/{characterId}/fleet/");

    public async Task<List<FleetMember>?> GetFleetMembersAsync(long fleetId)
        => await GetAuthenticatedAsync<List<FleetMember>>($"/v1/fleets/{fleetId}/members/");

    public async Task<List<FleetWing>?> GetFleetWingsAsync(long fleetId)
        => await GetAuthenticatedAsync<List<FleetWing>>($"/v1/fleets/{fleetId}/wings/");

    public async Task<FleetInfo?> GetFleetInfoAsync(long fleetId)
        => await GetAuthenticatedAsync<FleetInfo>($"/v1/fleets/{fleetId}/");

    // ── Fleet write operations (require the logged-in char to be fleet boss) ──

    /// <summary>Removes a member from the fleet. member_id is the pilot's character_id.</summary>
    public async Task<bool> KickFleetMemberAsync(long fleetId, int characterId)
        => await SendAuthenticatedAsync(HttpMethod.Delete,
               $"/v1/fleets/{fleetId}/members/{characterId}/");

    /// <summary>
    /// Moves a member to a new role/position. ESI requires different fields per role:
    /// fleet_commander → none; wing_commander → wing only; squad_* → wing + squad.
    /// </summary>
    public async Task<bool> MoveFleetMemberAsync(long fleetId, int characterId,
                                                 string role, long? wingId, long? squadId)
    {
        object body = role switch
        {
            "fleet_commander" => new { role },
            "wing_commander"  => new { role, wing_id = wingId },
            _                 => new { role, wing_id = wingId, squad_id = squadId },
        };
        return await SendAuthenticatedAsync(HttpMethod.Put,
            $"/v1/fleets/{fleetId}/members/{characterId}/", body);
    }

    public async Task<bool> RenameWingAsync(long fleetId, long wingId, string name)
        => await SendAuthenticatedAsync(HttpMethod.Put, $"/v1/fleets/{fleetId}/wings/{wingId}/", new { name });

    public async Task<bool> RenameSquadAsync(long fleetId, long squadId, string name)
        => await SendAuthenticatedAsync(HttpMethod.Put, $"/v1/fleets/{fleetId}/squads/{squadId}/", new { name });

    /// <summary>Invites a character into a squad. Requires the inviter to be fleet boss.</summary>
    public async Task<bool> InviteFleetMemberAsync(long fleetId, int characterId,
                                                   long wingId, long squadId, string role = "squad_member")
        => await SendAuthenticatedAsync(HttpMethod.Post,
               $"/v1/fleets/{fleetId}/members/",
               new { character_id = characterId, role, squad_id = squadId, wing_id = wingId });

    public async Task<bool> CreateWingAsync(long fleetId)
        => await SendAuthenticatedAsync(HttpMethod.Post, $"/v1/fleets/{fleetId}/wings/");

    public async Task<bool> DeleteWingAsync(long fleetId, long wingId)
        => await SendAuthenticatedAsync(HttpMethod.Delete, $"/v1/fleets/{fleetId}/wings/{wingId}/");

    public async Task<bool> CreateSquadAsync(long fleetId, long wingId)
        => await SendAuthenticatedAsync(HttpMethod.Post, $"/v1/fleets/{fleetId}/wings/{wingId}/squads/");

    public async Task<bool> DeleteSquadAsync(long fleetId, long squadId)
        => await SendAuthenticatedAsync(HttpMethod.Delete, $"/v1/fleets/{fleetId}/squads/{squadId}/");

    /// <summary>Resolves a character name to its ID via POST /v1/universe/ids/ (public).</summary>
    public async Task<int?> ResolveCharacterIdAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/universe/ids/");
        request.Headers.Add("User-Agent", "FCAT/1.0 (Fleet Commander Assistance Tool)");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new[] { name.Trim() }), System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<UniverseIdsResult>(json);
        var chars = result?.Characters;
        if (chars == null || chars.Count == 0) return null;

        var exact = chars.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return (exact ?? chars[0]).Id;
    }

    private async Task<bool> SendAuthenticatedAsync(HttpMethod method, string path, object? body = null)
    {
        var token = await authService.GetValidAccessTokenAsync();
        if (token == null) return false;

        var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("User-Agent", "FCAT/1.0 (Fleet Commander Assistance Tool)");

        if (body != null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Fetches group_id for each ship type ID (GET /v3/universe/types/{id}/).
    /// Requests are made in parallel (max 8 concurrent) to stay ESI-friendly.
    /// Returns only successfully resolved entries.
    /// </summary>
    public async Task<Dictionary<int, int>> GetShipGroupIdsAsync(IEnumerable<int> typeIds)
    {
        var ids = typeIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var result    = new Dictionary<int, int>();
        var semaphore = new SemaphoreSlim(8, 8);
        var lockObj   = new object();

        var tasks = ids.Select(async id =>
        {
            await semaphore.WaitAsync();
            try
            {
                var info = await GetPublicAsync<EsiTypeInfo>($"/v3/universe/types/{id}/");
                if (info != null && info.GroupId > 0)
                    lock (lockObj) result[id] = info.GroupId;
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        return result;
    }

    /// <summary>Resolves inventory type names → type IDs via POST /v1/universe/ids/ (public).</summary>
    public async Task<Dictionary<string, int>> ResolveTypeIdsAsync(IEnumerable<string> names)
    {
        var list = names.Select(n => n.Trim()).Where(n => n.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0) return result;

        foreach (var chunk in list.Chunk(1000))
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/universe/ids/");
            request.Headers.Add("User-Agent", "FCAT/1.0 (Fleet Commander Assistance Tool)");
            request.Content = new StringContent(JsonSerializer.Serialize(chunk),
                System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) continue;

            var json = await response.Content.ReadAsStringAsync();
            var r = JsonSerializer.Deserialize<UniverseIdsResult>(json);
            if (r?.InventoryTypes != null)
                foreach (var t in r.InventoryTypes) result[t.Name] = t.Id;
        }
        return result;
    }

    /// <summary>Fetches category_id for each group (GET /v3/universe/groups/{id}/), parallel + capped.</summary>
    public async Task<Dictionary<int, int>> GetGroupCategoriesAsync(IEnumerable<int> groupIds)
    {
        var ids = groupIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var result    = new Dictionary<int, int>();
        var semaphore = new SemaphoreSlim(8, 8);
        var lockObj   = new object();

        var tasks = ids.Select(async id =>
        {
            await semaphore.WaitAsync();
            try
            {
                var info = await GetPublicAsync<EsiGroupInfo>($"/v1/universe/groups/{id}/");
                if (info != null)
                    lock (lockObj) result[id] = info.CategoryId;
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        return result;
    }

    public async Task<Dictionary<int, string>> ResolveNamesAsync(IEnumerable<int> ids)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return [];

        var result = new Dictionary<int, string>();

        // ESI /v3/universe/names/ accepts up to 1000 IDs
        foreach (var chunk in idList.Chunk(1000))
        {
            var token = await authService.GetValidAccessTokenAsync();
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v3/universe/names/");
            request.Headers.Add("User-Agent", "FCAT/1.0 (Fleet Commander Assistance Tool)");
            if (token != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            request.Content = new StringContent(
                JsonSerializer.Serialize(chunk),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) continue;

            var json = await response.Content.ReadAsStringAsync();
            var names = JsonSerializer.Deserialize<List<EsiNameResult>>(json);
            if (names == null) continue;

            foreach (var n in names)
                result[n.Id] = n.Name;
        }

        return result;
    }
}
