using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using FCAT.Models;

namespace FCAT.Services;

public class EsiAuthService(HttpClient httpClient)
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
        "esi-universe.read_structures.v1",
    ];

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public EsiToken? CurrentToken { get; private set; }
    public int AuthenticatedCharacterId { get; private set; }
    public string AuthenticatedCharacterName { get; private set; } = string.Empty;

    public async Task<bool> AuthenticateAsync()
    {
        var state = GenerateRandomString(16);
        var scopeString = string.Join(" ", Scopes);

        var authUrl = $"{AuthorizeUrl}?response_type=code" +
                      $"&client_id={Uri.EscapeDataString(ClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                      $"&scope={Uri.EscapeDataString(scopeString)}" +
                      $"&state={state}";

        // Open browser for user to log in
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        // Listen for the callback
        var code = await WaitForCallbackAsync(state);
        if (string.IsNullOrEmpty(code))
            return false;

        var token = await ExchangeCodeForTokenAsync(code);
        if (token == null)
            return false;

        CurrentToken = token;
        await ResolveCharacterFromTokenAsync(token.AccessToken);
        return true;
    }

    public async Task<string?> GetValidAccessTokenAsync()
    {
        if (CurrentToken == null)
            return null;

        if (CurrentToken.IsExpired)
            await RefreshTokenAsync();

        return CurrentToken?.AccessToken;
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
            var request = context.Request;
            var query = HttpUtility.ParseQueryString(request.Url?.Query ?? string.Empty);

            var responseHtml = "<html><body><h2>Authentication complete. You can close this window.</h2></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();

            var returnedState = query["state"];
            if (returnedState != expectedState)
                return null;

            return query["code"];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<EsiToken?> ExchangeCodeForTokenAsync(string code)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
        var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Add("Authorization", $"Basic {credentials}");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri
        });

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync();
        var token = JsonSerializer.Deserialize<EsiToken>(json);
        if (token != null)
            token.ExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);

        return token;
    }

    private async Task RefreshTokenAsync()
    {
        if (CurrentToken == null) return;

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
        var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Add("Authorization", $"Basic {credentials}");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = CurrentToken.RefreshToken
        });

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return;

        var json = await response.Content.ReadAsStringAsync();
        var token = JsonSerializer.Deserialize<EsiToken>(json);
        if (token != null)
        {
            token.ExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
            // Preserve refresh token if new one not provided
            if (string.IsNullOrEmpty(token.RefreshToken))
                token.RefreshToken = CurrentToken.RefreshToken;
            CurrentToken = token;
        }
    }

    private async Task ResolveCharacterFromTokenAsync(string accessToken)
    {
        // Decode the JWT to get character info
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return;

        var payload = parts[1];
        // Pad base64 if needed
        payload += new string('=', (4 - payload.Length % 4) % 4);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("sub", out var sub))
        {
            // sub is "CHARACTER:EVE:12345678"
            var subStr = sub.GetString() ?? string.Empty;
            var parts2 = subStr.Split(':');
            if (parts2.Length == 3 && int.TryParse(parts2[2], out var charId))
                AuthenticatedCharacterId = charId;
        }

        if (doc.RootElement.TryGetProperty("name", out var name))
            AuthenticatedCharacterName = name.GetString() ?? string.Empty;

        await Task.CompletedTask;
    }

    private static string GenerateRandomString(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Convert.ToHexString(bytes).ToLower();
    }
}
