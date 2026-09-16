using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FCAT.Services;

/// <summary>An EVE item, reduced to the parts FCAT reasons about.</summary>
public class EveType
{
    public int    TypeId  { get; set; }
    public string Name    { get; set; } = string.Empty;
    public int    GroupId { get; set; }

    /// <summary>Dogma attributes by id. 64 is the damage multiplier, 51 the rate of fire, 114-118
    /// the four damage types, and so on - see DogmaAttr.</summary>
    public Dictionary<int, double> Attributes { get; set; } = [];

    /// <summary>Dogma effect ids. A ship's role bonuses are in here, which is how FCAT reads a
    /// hull's weapon bonuses without a hand-written table per ship.</summary>
    public List<int> Effects { get; set; } = [];

    public double Attr(int id, double fallback = 0) => Attributes.GetValueOrDefault(id, fallback);
}

/// <summary>One dogma effect: what it modifies, and with which attribute.</summary>
public class EveEffect
{
    public int    EffectId { get; set; }
    public string Name     { get; set; } = string.Empty;
    public List<EveModifier> Modifiers { get; set; } = [];
}

public class EveModifier
{
    public string Domain              { get; set; } = string.Empty;   // shipID, charID, itemID…
    public string Func                { get; set; } = string.Empty;   // LocationRequiredSkillModifier…
    public int    ModifiedAttributeId { get; set; }
    public int    ModifyingAttributeId { get; set; }
    public int    Operator            { get; set; }                   // 4 = postMul, 6 = postPercent
}

/// <summary>
/// A local copy of the EVE item database, filled in from ESI on demand and kept on disk.
///
/// <para>ESI serves type data one id at a time - there is no bulk form - so resolving a doctrine of
/// a dozen fits means a few hundred requests the first time it is seen. That is fine exactly once:
/// item attributes only change when CCP patches, so a type fetched today is still right next month.
/// Hence the disk cache, which is the whole point of this class rather than a nicety.</para>
///
/// <para>It is only ever used for things the FC opted into - a doctrine pulled from their Alliance
/// Auth. Nothing here runs for an FC who has no auth configured.</para>
/// </summary>
public class EveTypeCache
{
    private readonly HttpClient _http;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCAT", "types.json");

    /// <summary>Bumped when the shape below changes, or when a patch is known to have moved
    /// attributes enough to be worth re-reading everything. A mismatch drops the file.</summary>
    private const int CacheVersion = 1;

    /// <summary>ESI has no published rate limit, only an error budget, but this is somebody's game
    /// server and a doctrine sweep is a few hundred calls. Six at a time is brisk and polite.</summary>
    private const int Concurrency = 6;

    private readonly Dictionary<int, EveType>   _types   = [];
    private readonly Dictionary<int, EveEffect> _effects = [];

    /// <summary>Per charge group, whether the things in it do damage. This is how a missile launcher
    /// is told from a probe launcher without a hand-kept list of launcher groups: a launcher holds no
    /// damage numbers itself, so the only honest question is whether what it FIRES does damage.</summary>
    private readonly Dictionary<int, bool> _chargeGroupDoesDamage = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _dirty;

    public EveTypeCache(HttpClient http)
    {
        _http = http;
        Load();
    }

    /// <summary>A type already in the cache, or null. Never touches the network - for the hot paths
    /// that cannot await, once <see cref="EnsureAsync"/> has been run over the ids they need.</summary>
    public EveType? Known(int typeId) => _types.GetValueOrDefault(typeId);

    public EveEffect? KnownEffect(int effectId) => _effects.GetValueOrDefault(effectId);

    /// <summary>Whether a charge group is ammunition, i.e. whether firing it hurts anything.
    /// Unknown groups answer false, so an unresolved lookup understates rather than inventing a gun.</summary>
    public bool ChargeGroupDoesDamage(int groupId) => _chargeGroupDoesDamage.GetValueOrDefault(groupId);

    /// <summary>Fetch every type that isn't cached yet, then persist. Safe to call with ids already
    /// held - those cost nothing.</summary>
    public async Task EnsureAsync(IEnumerable<int> typeIds, CancellationToken ct = default)
    {
        var missing = typeIds.Where(id => id > 0 && !_types.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0) return;

        Log.Info("types", $"fetching {missing.Count} item type(s) from ESI");

        using var throttle = new SemaphoreSlim(Concurrency);
        await Task.WhenAll(missing.Select(async id =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var type = await FetchTypeAsync(id, ct);
                if (type == null) return;
                lock (_types) { _types[id] = type; _dirty = true; }
            }
            finally { throttle.Release(); }
        }));

        // A ship's bonuses live in its effects, so pull the ones the new types reference.
        var wantedEffects = missing.Select(Known)
                                   .Where(t => t != null)
                                   .SelectMany(t => t!.Effects)
                                   .Where(e => !_effects.ContainsKey(e))
                                   .Distinct()
                                   .ToList();

        if (wantedEffects.Count > 0)
        {
            using var effectThrottle = new SemaphoreSlim(Concurrency);
            await Task.WhenAll(wantedEffects.Select(async id =>
            {
                await effectThrottle.WaitAsync(ct);
                try
                {
                    var effect = await FetchEffectAsync(id, ct);
                    if (effect == null) return;
                    lock (_effects) { _effects[id] = effect; _dirty = true; }
                }
                finally { effectThrottle.Release(); }
            }));
        }

        // Work out which of the new modules' charge groups are ammunition. Two fetches per group,
        // once ever - the answer cannot change without a patch.
        var chargeGroups = missing.Select(Known)
                                  .Where(t => t != null)
                                  .SelectMany(t => ChargeGroupIds(t!))
                                  .Where(g => !_chargeGroupDoesDamage.ContainsKey(g))
                                  .Distinct()
                                  .ToList();

        foreach (var groupId in chargeGroups)
        {
            var doesDamage = await GroupDoesDamageAsync(groupId, ct);
            lock (_types) { _chargeGroupDoesDamage[groupId] = doesDamage; _dirty = true; }
        }

        await SaveAsync();
    }

    /// <summary>The charge groups a module accepts (dogma attributes 604-609).</summary>
    public static IEnumerable<int> ChargeGroupIds(EveType type)
    {
        for (var attr = 604; attr <= 609; attr++)
        {
            var value = (int)type.Attr(attr);
            if (value > 0) yield return value;
        }
    }

    /// <summary>Does anything in this group carry damage? Asked of one representative member, since
    /// a group in EVE is a family of the same thing in different flavours.</summary>
    private async Task<bool> GroupDoesDamageAsync(int groupId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://esi.evetech.net/latest/universe/groups/{groupId}/");
            request.Headers.Add("User-Agent", "FCAT (Fleet Commander Assistance Tool)");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;

            var group = JsonSerializer.Deserialize<EsiGroup>(await response.Content.ReadAsStringAsync(ct));
            var sample = group?.Types.FirstOrDefault(id => id > 0) ?? 0;
            if (sample == 0) return false;

            var type = Known(sample) ?? await FetchTypeAsync(sample, ct);
            if (type == null) return false;
            lock (_types) { _types[sample] = type; _dirty = true; }

            return type.Attr(114) + type.Attr(116) + type.Attr(117) + type.Attr(118) > 0;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Log.Warn("types", $"group {groupId} failed", ex);
            return false;
        }
    }

    private async Task<EveType?> FetchTypeAsync(int typeId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://esi.evetech.net/latest/universe/types/{typeId}/");
            request.Headers.Add("User-Agent", "FCAT (Fleet Commander Assistance Tool)");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("types", $"type {typeId} -> {(int)response.StatusCode}");
                return null;
            }

            var raw = JsonSerializer.Deserialize<EsiType>(await response.Content.ReadAsStringAsync(ct));
            if (raw == null) return null;

            return new EveType
            {
                TypeId     = typeId,
                Name       = raw.Name,
                GroupId    = raw.GroupId,
                Attributes = raw.DogmaAttributes.GroupBy(a => a.AttributeId)
                                                .ToDictionary(g => g.Key, g => g.First().Value),
                Effects    = raw.DogmaEffects.Select(e => e.EffectId).ToList(),
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn("types", $"type {typeId} failed", ex);
            return null;
        }
    }

    private async Task<EveEffect?> FetchEffectAsync(int effectId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://esi.evetech.net/latest/dogma/effects/{effectId}/");
            request.Headers.Add("User-Agent", "FCAT (Fleet Commander Assistance Tool)");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var raw = JsonSerializer.Deserialize<EsiEffect>(await response.Content.ReadAsStringAsync(ct));
            if (raw == null) return null;

            return new EveEffect
            {
                EffectId  = effectId,
                Name      = raw.Name,
                Modifiers = (raw.Modifiers ?? []).Select(m => new EveModifier
                {
                    Domain               = m.Domain ?? string.Empty,
                    Func                 = m.Func ?? string.Empty,
                    ModifiedAttributeId  = m.ModifiedAttributeId,
                    ModifyingAttributeId = m.ModifyingAttributeId,
                    Operator             = m.Operator,
                }).ToList(),
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn("types", $"effect {effectId} failed", ex);
            return null;
        }
    }

    // Disk

    private class CacheFile
    {
        public int Version { get; set; }
        public Dictionary<int, EveType>   Types   { get; set; } = [];
        public Dictionary<int, EveEffect> Effects { get; set; } = [];
        public Dictionary<int, bool>      ChargeGroups { get; set; } = [];
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(FilePath));
            if (file == null || file.Version != CacheVersion) return;   // stale shape - refetch lazily

            foreach (var (id, type)   in file.Types)   _types[id]   = type;
            foreach (var (id, effect) in file.Effects) _effects[id] = effect;
            foreach (var (id, dmg)    in file.ChargeGroups) _chargeGroupDoesDamage[id] = dmg;

            Log.Info("types", $"loaded {_types.Count} cached item type(s)");
        }
        catch (Exception ex)
        {
            Log.Warn("types", "could not read the type cache - it will refill from ESI", ex);
        }
    }

    private async Task SaveAsync()
    {
        if (!_dirty) return;

        await _gate.WaitAsync();
        try
        {
            CacheFile file;
            lock (_types)
            {
                file = new CacheFile
                {
                    Version = CacheVersion,
                    Types        = new Dictionary<int, EveType>(_types),
                    Effects      = new Dictionary<int, EveEffect>(_effects),
                    ChargeGroups = new Dictionary<int, bool>(_chargeGroupDoesDamage),
                };
                _dirty = false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(file));
        }
        catch (Exception ex)
        {
            Log.Warn("types", "could not save the type cache", ex);
        }
        finally { _gate.Release(); }
    }

    // The ESI shapes, which are not the shapes worth keeping

    private class EsiType
    {
        [JsonPropertyName("name")]     public string Name    { get; set; } = string.Empty;
        [JsonPropertyName("group_id")] public int    GroupId { get; set; }

        [JsonPropertyName("dogma_attributes")]
        public List<EsiTypeAttribute> DogmaAttributes { get; set; } = [];

        [JsonPropertyName("dogma_effects")]
        public List<EsiTypeEffect> DogmaEffects { get; set; } = [];
    }

    private class EsiTypeAttribute
    {
        [JsonPropertyName("attribute_id")] public int    AttributeId { get; set; }
        [JsonPropertyName("value")]        public double Value       { get; set; }
    }

    private class EsiTypeEffect
    {
        [JsonPropertyName("effect_id")] public int EffectId { get; set; }
    }

    private class EsiGroup
    {
        [JsonPropertyName("types")] public List<int> Types { get; set; } = [];
    }

    private class EsiEffect
    {
        [JsonPropertyName("name")]      public string Name { get; set; } = string.Empty;
        [JsonPropertyName("modifiers")] public List<EsiModifier>? Modifiers { get; set; }
    }

    private class EsiModifier
    {
        [JsonPropertyName("domain")]                 public string? Domain { get; set; }
        [JsonPropertyName("func")]                   public string? Func   { get; set; }
        [JsonPropertyName("modified_attribute_id")]  public int ModifiedAttributeId  { get; set; }
        [JsonPropertyName("modifying_attribute_id")] public int ModifyingAttributeId { get; set; }
        [JsonPropertyName("operator")]               public int Operator { get; set; }
    }
}
