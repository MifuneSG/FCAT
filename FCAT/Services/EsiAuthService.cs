using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// EVE SSO auth with multi-character support. The FC can authorize several characters (main + alts);
/// each character's refresh token is persisted (encrypted) via <see cref="CharacterStore"/>, and one
/// is the "active" character FCAT operates as. Per-character access tokens are cached in memory and
/// refreshed on demand, so the alt status board can poll each character independently.
/// </summary>
public class EsiAuthService(HttpClient httpClient, CharacterStore store)
{
    private const string AuthorizeUrl = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenUrl = "https://login.eveonline.com/v2/oauth/token";
    private const string RedirectUri = "http://localhost:7648/callback";
    private const int CallbackPort = 7648;

    private static readonly string[] Scopes =
    [
        "esi-fleets.read_fleet.v1",
        "esi-fleets.write_fleet.v1",
        "esi-location.read_location.v1",
        "esi-location.read_online.v1",      // alt status board: online/offline
        "esi-location.read_ship_type.v1",   // alt status board: current ship
        "esi-universe.read_structures.v1",
    ];

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    // In-memory access tokens keyed by character id (hydrated from stored refresh tokens).
    private readonly Dictionary<int, EsiToken> _tokens = [];

    // ── Active character (back-compat surface the rest of the app already uses) ──
    public EsiToken? CurrentToken { get; private set; }
    public int AuthenticatedCharacterId { get; private set; }
    public string AuthenticatedCharacterName { get; private set; } = string.Empty;

    /// <summary>Raised when the active character changes (login, switch, remove).</summary>
    public event Action? ActiveCharacterChanged;

    /// <summary>The persisted character list (for the account manager UI).</summary>
    public CharacterStore Store => store;

    public EsiAuthService InitStore() { store.Load(); return this; }

    // ── Add a character (runs the SSO browser flow) ──
    public async Task<bool> AuthenticateAsync()
    {
        var state = GenerateRandomString(16);
        var scopeString = string.Join(" ", Scopes);

        var authUrl = $"{AuthorizeUrl}?response_type=code" +
                      $"&client_id={Uri.EscapeDataString(ClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                      $"&scope={Uri.EscapeDataString(scopeString)}" +
                      $"&state={state}";

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var code = await WaitForCallbackAsync(state);
        if (string.IsNullOrEmpty(code)) return false;

        var token = await ExchangeCodeForTokenAsync(code);
        if (token == null) return false;

        var (charId, charName) = ParseCharacter(token.AccessToken);
        if (charId == 0) return false;

        _tokens[charId] = token;
        var first = store.Characters.Count == 0;
        store.Upsert(new StoredCharacter
        {
            CharacterId   = charId,
            CharacterName = charName,
            RefreshToken  = token.RefreshToken,
            Role          = first ? "Main" : "Alt",
        });

        // First character (or none active yet) becomes the active one.
        if (first || AuthenticatedCharacterId == 0)
            await SetActiveCharacterAsync(charId);

        return true;
    }

    /// <summary>Restores the last active character from disk without a browser round-trip.</summary>
    public async Task<bool> RestoreSessionAsync()
    {
        var active = store.Active;
        return active != null && await SetActiveCharacterAsync(active.CharacterId);
    }

    public async Task<bool> SetActiveCharacterAsync(int characterId)
    {
        var access = await GetValidAccessTokenAsync(characterId);   // hydrate / refresh
        if (access == null) return false;

        AuthenticatedCharacterId = characterId;
        AuthenticatedCharacterName =
            store.Characters.FirstOrDefault(c => c.CharacterId == characterId)?.CharacterName
            ?? AuthenticatedCharacterName;
        CurrentToken = _tokens.GetValueOrDefault(characterId);
        store.SetActive(characterId);
        ActiveCharacterChanged?.Invoke();
        return true;
    }

    public void RemoveCharacter(int characterId)
    {
        _tokens.Remove(characterId);
        var wasActive = AuthenticatedCharacterId == characterId;
        store.Remove(characterId);

        if (wasActive)
        {
            AuthenticatedCharacterId = 0;
            AuthenticatedCharacterName = string.Empty;
            CurrentToken = null;
            var next = store.Characters.FirstOrDefault();
            if (next != null) _ = SetActiveCharacterAsync(next.CharacterId);
            else ActiveCharacterChanged?.Invoke();
        }
    }

    /// <summary>Valid access token for a character (active one if not specified); null if unavailable.</summary>
    public async Task<string?> GetValidAccessTokenAsync(int? characterId = null)
    {
        var id = characterId ?? AuthenticatedCharacterId;
        if (id == 0) return null;

        if (!_tokens.TryGetValue(id, out var tok) || tok == null)
        {
            // Cold: hydrate from the stored refresh token.
            var stored = store.Characters.FirstOrDefault(c => c.CharacterId == id);
            if (stored == null || string.IsNullOrEmpty(stored.RefreshToken)) return null;
            tok = await RequestRefreshAsync(stored.RefreshToken);
            if (tok == null) return null;
            _tokens[id] = tok;
            PersistRefresh(id, tok);
        }
        else if (tok.IsExpired)
        {
            var refreshed = await RequestRefreshAsync(tok.RefreshToken);
            if (refreshed != null) { _tokens[id] = refreshed; tok = refreshed; PersistRefresh(id, tok); }
        }

        if (id == AuthenticatedCharacterId) CurrentToken = tok;   // keep back-compat field fresh
        return tok?.AccessToken;
    }

    // ── HTTP helpers ──
    private async Task<EsiToken?> ExchangeCodeForTokenAsync(string code)
    {
        var request = NewTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]   = "authorization_code",
            ["code"]         = code,
            ["redirect_uri"] = RedirectUri,
        });
        return await SendTokenRequestAsync(request, null);
    }

    private async Task<EsiToken?> RequestRefreshAsync(string refreshToken)
    {
        var request = NewTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = refreshToken,
        });
        return await SendTokenRequestAsync(request, refreshToken);
    }

    private HttpRequestMessage NewTokenRequest(Dictionary<string, string> form)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
        var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Add("Authorization", $"Basic {credentials}");
        request.Content = new FormUrlEncodedContent(form);
        return request;
    }

    private async Task<EsiToken?> SendTokenRequestAsync(HttpRequestMessage request, string? fallbackRefresh)
    {
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var token = JsonSerializer.Deserialize<EsiToken>(json);
        if (token != null)
        {
            token.ExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
            if (string.IsNullOrEmpty(token.RefreshToken) && fallbackRefresh != null)
                token.RefreshToken = fallbackRefresh;   // EVE doesn't always rotate the refresh token
        }
        return token;
    }

    /// <summary>If a refresh rotated the refresh token, persist the new one.</summary>
    private void PersistRefresh(int characterId, EsiToken tok)
    {
        var stored = store.Characters.FirstOrDefault(c => c.CharacterId == characterId);
        if (stored != null && !string.IsNullOrEmpty(tok.RefreshToken) && stored.RefreshToken != tok.RefreshToken)
        {
            stored.RefreshToken = tok.RefreshToken;
            store.Save();
        }
    }

    private async Task<string?> WaitForCallbackAsync(string expectedState)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{CallbackPort}/");
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var context = await listener.GetContextAsync().WaitAsync(cts.Token);
            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);

            var responseHtml = "<html><body style='font-family:Segoe UI;background:#0b0e14;color:#e6ebf2'>" +
                               "<h2>FCAT — authentication complete. You can close this window.</h2></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();

            return query["state"] == expectedState ? query["code"] : null;
        }
        catch (OperationCanceledException) { return null; }
        finally { listener.Stop(); }
    }

    /// <summary>Decodes the access-token JWT to (characterId, name).</summary>
    private static (int id, string name) ParseCharacter(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return (0, string.Empty);

        var payload = parts[1];
        payload += new string('=', (4 - payload.Length % 4) % 4);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);

        int id = 0;
        if (doc.RootElement.TryGetProperty("sub", out var sub))
        {
            // sub is "CHARACTER:EVE:12345678"
            var bits = (sub.GetString() ?? string.Empty).Split(':');
            if (bits.Length == 3) int.TryParse(bits[2], out id);
        }
        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        return (id, name);
    }

    private static string GenerateRandomString(int length)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(length)).ToLower();
}
