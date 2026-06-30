using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using FCAT.Models;

namespace FCAT.Services;

public class EsiService(HttpClient httpClient, EsiAuthService authService)
{
    private const string BaseUrl = "https://esi.evetech.net";

    /// <summary>When true, fleet-related calls return a synthetic fleet (Demo / Sandbox mode).</summary>
    public bool DemoMode { get; set; }

    private async Task<T?> GetAuthenticatedAsync<T>(string path, int? asCharacterId = null)
    {
        var token = await authService.GetValidAccessTokenAsync(asCharacterId);
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
        => DemoMode ? DemoData.Fleet()
                    : await GetAuthenticatedAsync<CharacterFleetInfo>($"/v1/characters/{characterId}/fleet/", characterId);

    /// <summary>Resolve a citadel/structure name (needs esi-universe.read_structures; uses the given char's token).</summary>
    public async Task<EsiNameOnly?> GetStructureNameAsync(long structureId, int asCharacterId)
        => await GetAuthenticatedAsync<EsiNameOnly>($"/v2/universe/structures/{structureId}/", asCharacterId);

    public async Task<List<FleetMember>?> GetFleetMembersAsync(long fleetId)
        => DemoMode ? DemoData.Members(authService.AuthenticatedCharacterId, authService.AuthenticatedCharacterName)
                    : await GetAuthenticatedAsync<List<FleetMember>>($"/v1/fleets/{fleetId}/members/");

    public async Task<List<FleetWing>?> GetFleetWingsAsync(long fleetId)
        => DemoMode ? DemoData.Wings()
                    : await GetAuthenticatedAsync<List<FleetWing>>($"/v1/fleets/{fleetId}/wings/");

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

    /// <summary>Sets the fleet MOTD (PUT /fleets/{id}/). is_free_move is preserved by passing it back.</summary>
    public async Task<bool> SetFleetMotdAsync(long fleetId, string motd, bool isFreeMove)
        => await SendAuthenticatedAsync(HttpMethod.Put, $"/v1/fleets/{fleetId}/",
               new { is_free_move = isFreeMove, motd });

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
        if (DemoMode) return DemoData.GroupIds(typeIds);

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

    // ── System intel ──
    public async Task<int?> ResolveSystemIdAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/universe/ids/");
        request.Headers.Add("User-Agent", "FCAT/1.0 (Fleet Commander Assistance Tool)");
        request.Content = new StringContent(JsonSerializer.Serialize(new[] { name.Trim() }),
            System.Text.Encoding.UTF8, "application/json");
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<UniverseIdsResult>(json);
        // /universe/ids/ returns systems under a "systems" array — reuse a small inline read
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("systems", out var systems) && systems.GetArrayLength() > 0)
            return systems[0].GetProperty("id").GetInt32();
        return null;
    }

    /// <summary>All solar system IDs in New Eden (GET /v1/universe/systems/).</summary>
    public async Task<List<int>>         GetAllSystemIdsAsync()        => await GetPublicAsync<List<int>>("/v1/universe/systems/") ?? [];
    public async Task<EsiSystem?>        GetSystemAsync(int id)        => await GetPublicAsync<EsiSystem>($"/v4/universe/systems/{id}/");
    public async Task<EsiStargate?>      GetStargateAsync(int id)      => await GetPublicAsync<EsiStargate>($"/v1/universe/stargates/{id}/");
    public async Task<EsiConstellation?> GetConstellationAsync(int id) => await GetPublicAsync<EsiConstellation>($"/v1/universe/constellations/{id}/");
    public async Task<string?>           GetRegionNameAsync(int id)    => (await GetPublicAsync<EsiNameOnly>($"/v1/universe/regions/{id}/"))?.Name;

    /// <summary>Full killmail detail (public — needs the killmail id + zKill hash).</summary>
    public async Task<EsiKillmail?> GetKillmailAsync(long killmailId, string hash)
        => await GetPublicAsync<EsiKillmail>($"/v1/killmails/{killmailId}/{hash}/");

    public async Task<List<SystemKills>> GetSystemKillsAsync() => await GetPublicAsync<List<SystemKills>>("/v2/universe/system_kills/") ?? [];
    public async Task<List<SystemJumps>> GetSystemJumpsAsync() => await GetPublicAsync<List<SystemJumps>>("/v1/universe/system_jumps/") ?? [];
    public async Task<List<SovEntry>>    GetSovMapAsync()      => await GetPublicAsync<List<SovEntry>>("/v1/sovereignty/map/") ?? [];

    /// <summary>A character's current solar system, queried with that character's own token
    /// (needs esi-location). Works for the active char and any added alt.</summary>
    public async Task<CharacterLocation?> GetCharacterLocationAsync(int characterId)
        => await GetAuthenticatedAsync<CharacterLocation>($"/v2/characters/{characterId}/location/", characterId);

    /// <summary>A character's online status (needs esi-location.read_online).</summary>
    public async Task<CharacterOnline?> GetCharacterOnlineAsync(int characterId)
        => await GetAuthenticatedAsync<CharacterOnline>($"/v3/characters/{characterId}/online/", characterId);

    /// <summary>A character's current ship (needs esi-location.read_ship_type).</summary>
    public async Task<CharacterShip?> GetCharacterShipAsync(int characterId)
        => await GetAuthenticatedAsync<CharacterShip>($"/v2/characters/{characterId}/ship/", characterId);

    /// <summary>Resolves character names → IDs via POST /v1/universe/ids/ (public, batched).</summary>
    public async Task<Dictionary<string, int>> ResolveCharacterIdsAsync(IEnumerable<string> names)
    {
        var list = names.Select(n => n.Trim()).Where(n => n.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0) return result;

        foreach (var chunk in list.Chunk(500))
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/universe/ids/");
            request.Headers.Add("User-Agent", "FCAT/1.0 (Fleet Commander Assistance Tool)");
            request.Content = new StringContent(JsonSerializer.Serialize(chunk),
                System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) continue;

            var json = await response.Content.ReadAsStringAsync();
            var r = JsonSerializer.Deserialize<UniverseIdsResult>(json);
            if (r?.Characters != null)
                foreach (var c in r.Characters) result[c.Name] = c.Id;
        }
        return result;
    }

    /// <summary>Fetches corp/alliance affiliation for characters via POST /v1/characters/affiliation/.</summary>
    public async Task<List<CharAffiliation>> GetAffiliationsAsync(IEnumerable<int> characterIds)
    {
        var list = characterIds.Distinct().ToList();
        var result = new List<CharAffiliation>();
        if (list.Count == 0) return result;

        foreach (var chunk in list.Chunk(1000))
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/characters/affiliation/");
            request.Headers.Add("User-Agent", "FCAT/1.0 (Fleet Commander Assistance Tool)");
            request.Content = new StringContent(JsonSerializer.Serialize(chunk),
                System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) continue;

            var json = await response.Content.ReadAsStringAsync();
            var affs = JsonSerializer.Deserialize<List<CharAffiliation>>(json);
            if (affs != null) result.AddRange(affs);
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
        if (DemoMode) return DemoData.Names(ids, authService.AuthenticatedCharacterId, authService.AuthenticatedCharacterName);

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
