using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FCAT.Services;

/// <summary>One killmail as R2Z2 hands it over: the full ESI body plus zKill's value envelope.</summary>
public class StreamKill
{
    [JsonPropertyName("killmail_id")] public long        KillmailId { get; set; }
    [JsonPropertyName("esi")]         public StreamEsi?  Esi        { get; set; }
    [JsonPropertyName("zkb")]         public StreamZkb?  Zkb        { get; set; }
}

public class StreamEsi
{
    [JsonPropertyName("killmail_time")]   public DateTime      Time     { get; set; }
    [JsonPropertyName("solar_system_id")] public int           SystemId { get; set; }
    [JsonPropertyName("victim")]          public StreamVictim? Victim   { get; set; }
}

public class StreamVictim
{
    [JsonPropertyName("ship_type_id")]   public int  ShipTypeId    { get; set; }
    [JsonPropertyName("corporation_id")] public int  CorporationId { get; set; }
    [JsonPropertyName("alliance_id")]    public int? AllianceId    { get; set; }
}

public class StreamZkb
{
    [JsonPropertyName("totalValue")] public double TotalValue { get; set; }
}

/// <summary>
/// Live killmail feed from zKillboard's R2Z2 service. This is the only source that is actually
/// current: the /api/systemID/ style list endpoints are cached and run hours behind, which is no
/// use for "what died near me just now".
///
/// The protocol is a sequence walk (documented at
/// https://github.com/zKillboard/zKillboard/wiki/API-(R2Z2), which replaced RedisQ):
/// read ephemeral/sequence.json for the head, then fetch ephemeral/&lt;n&gt;.json and step forward.
/// A 404 means nothing new has landed yet.
///
/// zKill's stated limits are respected here and should stay respected - they hand out hour-long IP
/// bans: wait at least six seconds after a 404, stay well under 15 requests a second, and always
/// send a non-blank User-Agent (Cloudflare blocks blank ones outright).
/// </summary>
public class KillStreamService(HttpClient httpClient) : IDisposable
{
    private const string Base = "https://r2z2.zkillboard.com/ephemeral/";
    private const string UserAgent = "FCAT/1.1 (Fleet Commander Assistance Tool; +https://github.com/MifuneSG/FCAT)";

    /// <summary>Wait after a 404 - zKill asks for six seconds minimum, so give it a little more.</summary>
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(7);

    /// <summary>Breather between consecutive hits, so a busy stream can't run away with the rate limit.</summary>
    private static readonly TimeSpan BusyWait = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan ErrorWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Pace of the backward walk that fills in what happened just before FCAT opened. Gentler than
    /// the 15/sec zKill allows, because this is bulk reading of their CDN and nothing here is urgent.
    /// </summary>
    private static readonly TimeSpan BackfillWait = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Hard stop on the backward walk. A busy evening can put well over a thousand killmails into
    /// half an hour, and an FC opening the app does not need us reading all of them - this bounds
    /// the work whatever the state of New Eden.
    /// </summary>
    private const int BackfillLimit = 900;

    /// <summary>How long a kill stays in the buffer. The feed shows a shorter window than this.</summary>
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(60);

    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<long, StreamKill> _recent = new();

    private long _head;                // where we joined the stream
    private int  _backfillAsked;       // something actually wants history
    private int  _backfillStarted;     // ...and it has been kicked off once

    /// <summary>Raised for each newly streamed killmail, on a background thread.</summary>
    public event Action<StreamKill>? KillSeen;

    /// <summary>True once the walker has a sequence position and is following the stream.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Everything seen in the retention window, newest first.</summary>
    public IReadOnlyList<StreamKill> Recent(TimeSpan within)
    {
        var cutoff = DateTime.UtcNow - within;
        return _recent.Values
                      .Where(k => k.Esi != null && k.Esi.Time.ToUniversalTime() >= cutoff)
                      .OrderByDescending(k => k.Esi!.Time)
                      .ToList();
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = WalkAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsRunning = false;
        Volatile.Write(ref _head, 0);
        Interlocked.Exchange(ref _backfillStarted, 0);
    }

    private async Task WalkAsync(CancellationToken ct)
    {
        long sequence = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (sequence == 0)
                {
                    sequence = await HeadAsync(ct);
                    if (sequence == 0) { await Task.Delay(ErrorWait, ct); continue; }
                    IsRunning = true;
                    _head = sequence;
                    TryStartBackfill();
                }

                var kill = await FetchAsync(sequence, ct);
                if (kill == null)
                {
                    // 404: the head has not moved yet. This is the normal resting state.
                    await Task.Delay(IdleWait, ct);
                    continue;
                }

                sequence++;
                if (kill.Esi != null)
                {
                    _recent[kill.KillmailId] = kill;
                    Prune();
                    KillSeen?.Invoke(kill);
                }

                await Task.Delay(BusyWait, ct);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Network hiccup, or we have been told to back off. Either way, wait it out rather
                // than hammering - zKill bans for an hour.
                try { await Task.Delay(ErrorWait, ct); } catch { return; }
            }
        }
    }

    /// <summary>
    /// Ask for the recent past to be filled in. Called when something first wants kills rather than
    /// at startup: the backward walk is hundreds of requests, and an FC who never opens Intel should
    /// not spend them - or zKill's bandwidth - on history nothing will display. Safe to call often;
    /// it runs at most once.
    /// </summary>
    public void EnsureBackfill()
    {
        Interlocked.Exchange(ref _backfillAsked, 1);
        TryStartBackfill();
    }

    private void TryStartBackfill()
    {
        if (Volatile.Read(ref _backfillAsked) == 0) return;   // nobody has asked yet
        if (Volatile.Read(ref _head) == 0) return;            // not following the stream yet
        if (Interlocked.Exchange(ref _backfillStarted, 1) == 1) return;

        var token = _cts?.Token ?? CancellationToken.None;
        _ = BackfillAsync(Volatile.Read(ref _head) - 1, token);
    }

    /// <summary>Walk backwards from the head until the kills fall outside the retention window.</summary>
    private async Task BackfillAsync(long from, CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow - Keep;
            for (var i = 0; i < BackfillLimit && !ct.IsCancellationRequested; i++)
            {
                var kill = await FetchAsync(from - i, ct);
                if (kill?.Esi == null) continue;   // a gap in the sequence, not the end of it

                if (kill.Esi.Time.ToUniversalTime() < cutoff) return;   // far enough back

                if (_recent.TryAdd(kill.KillmailId, kill)) KillSeen?.Invoke(kill);
                await Task.Delay(BackfillWait, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* backfill is a nicety - never let it take the live stream down */ }
    }

    private async Task<long> HeadAsync(CancellationToken ct)
    {
        using var response = await SendAsync(Base + "sequence.json", ct);
        if (!response.IsSuccessStatusCode) return 0;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("sequence", out var s) ? s.GetInt64() : 0;
    }

    /// <summary>The killmail at this sequence position, or null when it does not exist yet (404).</summary>
    private async Task<StreamKill?> FetchAsync(long sequence, CancellationToken ct)
    {
        using var response = await SendAsync($"{Base}{sequence}.json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"r2z2 {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<StreamKill>(json);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);   // blank is blocked by Cloudflare
        return await httpClient.SendAsync(request, ct);
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Keep;
        foreach (var stale in _recent.Where(kv => kv.Value.Esi == null || kv.Value.Esi.Time.ToUniversalTime() < cutoff)
                                     .Select(kv => kv.Key).ToList())
            _recent.TryRemove(stale, out _);
    }

    public void Dispose() => Stop();
}
