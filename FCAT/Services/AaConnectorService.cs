using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>How the connection is doing, for the one status line setup shows.</summary>
public enum AaState
{
    /// <summary>No auth configured. This is the normal state for most of FCAT's users.</summary>
    Off,
    /// <summary>Configured, first fetch has not finished yet.</summary>
    Connecting,
    /// <summary>Talking to auth, data is current.</summary>
    Connected,
    /// <summary>Configured and we have data, but the last fetch failed - showing the cached copy.</summary>
    Stale,
    /// <summary>Configured, and we have nothing to show.</summary>
    Failed,
}

/// <summary>
/// The FCAT side of the Alliance Auth connector: doctrines, the fits behind them, and friendly
/// structures, pulled from the FC's own auth instance.
///
/// <para><b>Everything this serves is optional.</b> FCAT has to be a complete tool for an FC with
/// no Alliance Auth at all - that is most of them - so nothing here is allowed to become a
/// dependency. When it is unconfigured, offline or revoked, the properties return empty collections
/// and the features that read them hide their own UI rather than showing an error. There is no
/// "connect to continue" anywhere in the app and there must never be one.</para>
///
/// <para><b>Auth is identity, not authority.</b> The key says which auth user is asking; the server
/// then re-checks that user's permissions per request and filters with the source app's own
/// queryset. So a revoked key or a dropped group cuts the data off server-side on the next request,
/// and nothing cached here was ever something the FC could not already open in a browser.</para>
///
/// <para><b>The cache is the point, not an optimisation.</b> An FC opens FCAT mid-fight on a laptop
/// tethered to a phone; auth may be slow, down, or behind a VPN they are not on. The last good
/// snapshot is written to disk and loaded at launch, so doctrines and structures are there before -
/// and regardless of whether - the network answers.</para>
/// </summary>
public class AaConnectorService
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;

    /// <summary>Key and cached data together, DPAPI-encrypted to this Windows account - the same
    /// treatment the ESI refresh tokens get. The key is a credential, and structure locations and
    /// doctrine composition are alliance business that has no reason to sit on disk in the clear.</summary>
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCAT", "aa.dat");

    /// <summary>Structures move state - fuel burns down, timers start. Doctrines almost never change,
    /// but they cost one small GET to re-read, so both come down together on this interval.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(20);

    /// <summary>Auth is somebody's self-hosted Django box, not a CDN. Fail fast and try later rather
    /// than leaving a refresh hanging off the back of a slow VPN.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private string _apiKey = string.Empty;
    private CancellationTokenSource? _loop;

    public AaConnectorService(HttpClient http, SettingsService settings)
    {
        _http = http;
        _settings = settings;
        LoadStore();
    }

    /// <summary>Raised after a refresh changes anything. Fires on a background thread - dispatch.</summary>
    public event Action? Updated;

    /// <summary>The last good pull. Never null, so consumers never null-check before enumerating.</summary>
    public AaSnapshot Snapshot { get; private set; } = new();

    public AaState State { get; private set; } = AaState.Off;

    /// <summary>One line for the setup page. Empty when there is nothing worth saying.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>What this auth can serve, from the index endpoint. Both false until first contact.</summary>
    public AaSources Sources { get; private set; } = new();

    public string BaseUrl => _settings.Current.AaBaseUrl;

    /// <summary>True once there is somewhere to ask and something to ask with.</summary>
    public bool IsConfigured =>
        _settings.Current.AaEnabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>True when there is any connector data to show. Every AA-fed piece of UI hangs off
    /// this, so an FC without auth simply never sees those panels.</summary>
    public bool HasData =>
        Snapshot.Doctrines.Count > 0 || Snapshot.Structures.Count > 0;

    // Lookups the rest of the app uses

    public IReadOnlyList<AaDoctrine>  Doctrines  => Snapshot.Doctrines;
    public IReadOnlyList<AaFitting>   Fittings   => Snapshot.Fittings;
    public IReadOnlyList<AaStructure> Structures => Snapshot.Structures;

    public AaFitting? Fitting(int id) => Snapshot.Fittings.FirstOrDefault(f => f.Id == id);

    /// <summary>The fits that make up a doctrine, in the order the doctrine lists them.</summary>
    public List<AaFitting> FitsOf(AaDoctrine doctrine) =>
        doctrine.FittingIds.Select(Fitting).Where(f => f != null).Select(f => f!).ToList();

    /// <summary>Friendly structures in a system. Empty for every system when there is no auth.</summary>
    public List<AaStructure> StructuresIn(int systemId) =>
        Snapshot.Structures.Where(s => s.SystemId == systemId).ToList();

    /// <summary>Whether the fleet has anywhere to dock or tether in this system right now.</summary>
    public bool HasShelterIn(int systemId) =>
        Snapshot.Structures.Any(s => s.SystemId == systemId && s.CanShelter);

    /// <summary>Hull type ids that appear in any doctrine - "is this pilot in something we fly".</summary>
    public HashSet<int> DoctrineHullTypeIds()
    {
        var inDoctrines = Snapshot.Doctrines.SelectMany(d => d.FittingIds).ToHashSet();
        return Snapshot.Fittings.Where(f => inDoctrines.Contains(f.Id))
                                .Select(f => f.ShipTypeId)
                                .ToHashSet();
    }

    // Lifecycle

    /// <summary>Loads the cached snapshot and, if configured, starts the refresh loop. Safe to call
    /// when nothing is configured - it just sits at Off.</summary>
    public void Start()
    {
        if (!IsConfigured)
        {
            State = AaState.Off;
            return;
        }

        State = Snapshot.Doctrines.Count > 0 || Snapshot.Structures.Count > 0
            ? AaState.Stale      // we have the cache; treat it as stale until a fetch confirms
            : AaState.Connecting;

        _loop?.Cancel();
        _loop = new CancellationTokenSource();
        _ = LoopAsync(_loop.Token);
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct);
            try { await Task.Delay(RefreshEvery, ct); } catch { return; }
        }
    }

    /// <summary>Pull the index, then whatever it says this auth can serve.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) { State = AaState.Off; return; }

        var index = await GetAsync<AaIndex>("", ct);
        if (index == null)
        {
            // Keep serving the cache. An FC mid-fight on a phone tether should still see the
            // doctrine they pinged an hour ago rather than an empty panel.
            State  = HasData ? AaState.Stale : AaState.Failed;
            Status = HasData
                ? $"Auth unreachable - showing data from {Ago(Snapshot.FetchedUtc)}"
                : "Could not reach auth. Check the address and the key.";
            Updated?.Invoke();
            return;
        }

        Sources = index.Sources;

        var snapshot = new AaSnapshot
        {
            FetchedUtc       = DateTime.UtcNow,
            ConnectorVersion = index.Connector,
            AuthUser         = index.User,
            // Carry the previous lists forward, so a source app that is momentarily 500ing does not
            // blank out the half of the data that is still fine.
            Doctrines  = Snapshot.Doctrines,
            Fittings   = Snapshot.Fittings,
            Structures = Snapshot.Structures,
        };

        var parts = new List<string>();

        if (index.Sources.Doctrines)
        {
            var doctrines = await GetAsync<AaDoctrinePayload>("doctrines/", ct);
            if (doctrines != null)
            {
                snapshot.Doctrines = doctrines.Doctrines;
                snapshot.Fittings  = doctrines.Fittings;
                parts.Add($"{doctrines.Doctrines.Count} doctrines");
            }
        }
        else
        {
            snapshot.Doctrines = [];
            snapshot.Fittings  = [];
        }

        if (index.Sources.Structures)
        {
            var structures = await GetAsync<AaStructurePayload>("structures/", ct);
            if (structures != null)
            {
                snapshot.Structures = structures.Structures;
                parts.Add($"{structures.Structures.Count} structures");
            }
        }
        else
        {
            snapshot.Structures = [];
        }

        Snapshot = snapshot;
        State    = AaState.Connected;
        Status   = parts.Count > 0
            ? $"Connected as {index.User} - {string.Join(", ", parts)}"
            : $"Connected as {index.User} - this auth runs neither fittings nor structures";

        SaveStore();
        Log.Info("aa", $"refreshed: {Status}");
        Updated?.Invoke();
    }

    /// <summary>
    /// Try a URL and key without committing them, for the Test button in setup. Returns a line to
    /// show the FC either way - this is the one place in the connector where the failure has to be
    /// specific, because they are standing there trying to get it working.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestAsync(string baseUrl, string apiKey)
    {
        var root = NormaliseBaseUrl(baseUrl);
        if (root == null) return (false, "That doesn't look like a web address. Use your auth's, e.g. https://auth.example.com");
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "Paste the key from your auth's FCAT Connector page.");

        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{root}/fcat/api/");
            request.Headers.Add("X-FCAT-Key", apiKey.Trim());
            request.Headers.Add("User-Agent", UserAgent);

            using var response = await _http.SendAsync(request, cts.Token);

            // Auth redirects an unauthenticated request to its login page, so an HTML 200 means the
            // three API views were never added to APPS_WITH_PUBLIC_VIEWS - by far the most common
            // way this is mis-installed, and one the FC can do nothing about at their end.
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (response.IsSuccessStatusCode && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return (false, "Auth answered with a login page. Your admin needs to add \"fcatconnector\" to APPS_WITH_PUBLIC_VIEWS in local.py.");

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    return (false, "Auth doesn't recognise that key. Generate a new one on its FCAT Connector page.");
                case HttpStatusCode.Forbidden:
                    return (false, "Your auth account doesn't have connector access. Ask for the FCAT Connector permission.");
                case HttpStatusCode.NotFound:
                    return (false, "No connector at that address. Check the URL, or ask whether the plugin is installed.");
            }

            if (!response.IsSuccessStatusCode)
                return (false, $"Auth answered {(int)response.StatusCode}. Try again, or ask your admin to check its logs.");

            var index = JsonSerializer.Deserialize<AaIndex>(await response.Content.ReadAsStringAsync(cts.Token));
            if (index == null) return (false, "Auth answered, but not with anything FCAT could read.");

            var has = new List<string>();
            if (index.Sources.Doctrines)  has.Add("doctrines");
            if (index.Sources.Structures) has.Add("structures");

            return (true, has.Count > 0
                ? $"Connected as {index.User}. Serving {string.Join(" and ", has)}."
                : $"Connected as {index.User}, but this auth runs neither the fittings nor the structures app.");
        }
        catch (TaskCanceledException)
        {
            return (false, "Auth didn't answer in time. Check the address, and that you're on the VPN if it needs one.");
        }
        catch (Exception ex)
        {
            Log.Warn("aa", "test connection failed", ex);
            return (false, $"Couldn't reach that address ({ex.GetType().Name}).");
        }
    }

    /// <summary>Saves a verified URL and key, then starts pulling. Called after a successful test.</summary>
    public void Connect(string baseUrl, string apiKey)
    {
        _settings.Current.AaBaseUrl = NormaliseBaseUrl(baseUrl) ?? string.Empty;
        _settings.Current.AaEnabled = true;
        _settings.Save();

        _apiKey = apiKey.Trim();
        SaveStore();
        Start();
    }

    /// <summary>Forget the key and everything pulled with it. The FC's auth is untouched - the key
    /// keeps working until they revoke it there, which is the only place that can.</summary>
    public void Disconnect()
    {
        Stop();
        _apiKey  = string.Empty;
        Snapshot = new AaSnapshot();
        Sources  = new AaSources();
        State    = AaState.Off;
        Status   = string.Empty;

        _settings.Current.AaBaseUrl = string.Empty;
        _settings.Save();

        try { if (File.Exists(StorePath)) File.Delete(StorePath); }
        catch (Exception ex) { Log.Warn("aa", "could not delete the stored key", ex); }

        Updated?.Invoke();
    }

    // HTTP

    private const string UserAgent = "FCAT (Fleet Commander Assistance Tool; +https://github.com/MifuneSG/FCAT)";

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        var root = NormaliseBaseUrl(BaseUrl);
        if (root == null) return default;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{root}/fcat/api/{path}");
            request.Headers.Add("X-FCAT-Key", _apiKey);
            request.Headers.Add("User-Agent", UserAgent);

            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                // 501 is the connector saying this auth doesn't run that source app. It is the
                // designed answer, not a fault, so it stays out of the log.
                if (response.StatusCode != HttpStatusCode.NotImplemented)
                    Log.Warn("aa", $"GET /fcat/api/{path} -> {(int)response.StatusCode}");
                return default;
            }

            return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OperationCanceledException) { return default; }
        catch (Exception ex)
        {
            Log.Warn("aa", $"GET /fcat/api/{path} failed", ex);
            return default;
        }
    }

    /// <summary>
    /// Turn whatever the FC pasted into a scheme+host root. They will paste the address bar, so
    /// accept the connector page's own URL and a bare hostname as readily as the root.
    /// </summary>
    public static string? NormaliseBaseUrl(string input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return null;

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;

        // Drop the connector's own path if they copied it out of the browser, and any trailing slash.
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/fcat", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/fcat".Length];

        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    private static string Ago(DateTime utc)
    {
        if (utc == DateTime.MinValue) return "an earlier session";
        var age = DateTime.UtcNow - utc;
        if (age.TotalMinutes < 2)  return "a moment ago";
        if (age.TotalHours   < 1)  return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays    < 1)  return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    // Disk

    /// <summary>What goes in aa.dat. The key and the data it fetched travel together.</summary>
    private class Store
    {
        public string     ApiKey   { get; set; } = string.Empty;
        public AaSnapshot Snapshot { get; set; } = new();
    }

    private void LoadStore()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var json  = ProtectedData.Unprotect(File.ReadAllBytes(StorePath), null, DataProtectionScope.CurrentUser);
            var store = JsonSerializer.Deserialize<Store>(json);
            if (store == null) return;

            _apiKey  = store.ApiKey;
            Snapshot = store.Snapshot;
        }
        catch (Exception ex)
        {
            // Unreadable store - most likely copied from another machine, where DPAPI will not
            // decrypt it. Start clean; the FC pastes their key again.
            Log.Warn("aa", "could not read the stored key and cache", ex);
        }
    }

    private void SaveStore()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(new Store { ApiKey = _apiKey, Snapshot = Snapshot });
            File.WriteAllBytes(StorePath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            Log.Warn("aa", "could not save the key and cache", ex);
        }
    }
}

/// <summary>The doctrines endpoint's envelope - two flat lists, joined by fitting id.</summary>
internal class AaDoctrinePayload
{
    [System.Text.Json.Serialization.JsonPropertyName("doctrines")]
    public List<AaDoctrine> Doctrines { get; set; } = [];

    [System.Text.Json.Serialization.JsonPropertyName("fittings")]
    public List<AaFitting> Fittings { get; set; } = [];
}

internal class AaStructurePayload
{
    [System.Text.Json.Serialization.JsonPropertyName("structures")]
    public List<AaStructure> Structures { get; set; } = [];
}
