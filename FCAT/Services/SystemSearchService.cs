using System.IO;
using System.Text.Json;

namespace FCAT.Services;

/// <summary>One system in the autocomplete index.</summary>
public record SystemMatch(int Id, string Name);

/// <summary>
/// Provides instant local autocomplete for solar-system names (used by the form-up picker).
///
/// EVE's API has no public prefix-search, so we build the index once from ESI (all system IDs →
/// names) and cache it to %APPDATA%\FCAT\systems-index.json. After the first run it loads from
/// disk, so searching is in-memory and immediate — no per-keystroke network calls, no new scope.
/// </summary>
public class SystemSearchService(EsiService esi)
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCAT", "systems-index.json");

    private List<SystemMatch> _systems = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsLoaded { get; private set; }

    /// <summary>Loads the index from disk, or builds it from ESI the first time. Safe to call repeatedly.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (IsLoaded) return;
        await _gate.WaitAsync();
        try
        {
            if (IsLoaded) return;

            if (TryLoadCache()) { IsLoaded = true; return; }

            var ids = await esi.GetAllSystemIdsAsync();
            if (ids.Count == 0) return;                       // ESI hiccup — stay unloaded, retry later

            var names = await esi.ResolveNamesAsync(ids);     // chunks of 1000 internally
            _systems = names.Where(kv => !string.IsNullOrEmpty(kv.Value))
                            .Select(kv => new SystemMatch(kv.Key, kv.Value))
                            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                            .ToList();
            if (_systems.Count == 0) return;
            SaveCache();
            IsLoaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Best matches for a typed prefix — startswith first, then contains.</summary>
    public IReadOnlyList<SystemMatch> Search(string query, int max = 8)
    {
        if (!IsLoaded || string.IsNullOrWhiteSpace(query)) return [];
        query = query.Trim();
        var starts   = _systems.Where(s => s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        var contains = _systems.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                        && !s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        return starts.Concat(contains).Take(max).ToList();
    }

    /// <summary>Exact (case-insensitive) name → system id, or null if not a real system.</summary>
    public int? ResolveId(string name)
    {
        var hit = _systems.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit?.Id;
    }

    private bool TryLoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return false;
            var loaded = JsonSerializer.Deserialize<List<SystemMatch>>(File.ReadAllText(CachePath));
            if (loaded is { Count: > 0 }) { _systems = loaded; return true; }
        }
        catch { /* corrupt cache — rebuild */ }
        return false;
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(_systems));
        }
        catch { /* non-fatal — we just rebuild next launch */ }
    }
}
