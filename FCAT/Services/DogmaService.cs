using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// What a fit actually does, once EVE's dogma has been applied to it.
///
/// Every number here is the engine's, not an estimate. <see cref="Mass"/> is the one that changes an
/// existing answer: <see cref="WormholeMass"/> has always had to use bare hull mass because ESI does
/// not expose other pilots' fits, and said so in its own comments. A doctrine fit closes that.
/// </summary>
public record FitStats(
    double Dps,
    double DroneDps,
    double Alpha,
    double Ehp,
    double ArmorEhp,
    double ShieldEhp,
    double HullEhp,
    double Mass,
    double AlignTime,
    double Optimal = 0,
    double Falloff = 0,
    double MissileRange = 0)
{
    /// <summary>
    /// The fit's reach, the way an FC says it: "6.8 + 6.3" for a turret, a flat number for missiles.
    ///
    /// Turrets carry optimal and falloff and both matter - half damage at the end of falloff is
    /// still damage, so an FC picking ammo is really choosing between those two numbers. Missiles
    /// have neither; they simply stop, at flight time times velocity.
    /// </summary>
    public string RangeText
    {
        get
        {
            if (MissileRange > 0) return $"{MissileRange / 1000:0.#} km";
            if (Optimal <= 0)     return string.Empty;
            return Falloff > 0
                ? $"{Optimal / 1000:0.#} + {Falloff / 1000:0.#} km"
                : $"{Optimal / 1000:0.#} km";
        }
    }

    /// <summary>
    /// Everything the ship puts out. This is just <see cref="Dps"/>: the engine's
    /// damagePerSecondWithoutReload ALREADY includes drones, and <see cref="DroneDps"/> is the
    /// breakdown of how much of it they are, not an extra on top. Adding the two together looks
    /// reasonable and silently counts every drone twice - measured on a Megathron, guns alone read
    /// 1343, three Ogres made it 1533, and drone dps read 190.
    /// </summary>
    public double TotalDps => Dps;
}

/// <summary>
/// FCAT's link to EVEShipFit's dogma engine, which it calls in-process through a small native DLL
/// (<c>dogma-bridge/</c> in this repo, MIT, the same engine eveship.fit and zKillboard's fitting
/// view use).
///
/// <para><b>Nothing here touches the network.</b> <c>sde.dat</c> is a file that shipped with FCAT,
/// the DLL runs inside this process, and <c>calculate</c> is a pure function. No browser, no
/// subprocess, no upload. A fit an FC pulled from their alliance's auth never leaves the machine.</para>
///
/// <para>FCAT does not implement dogma itself and should not start: getting damage right means every
/// modifier in the right order, stacking penalties, and each ship bonus gated on the skill it
/// belongs to. An approximation that lands within ten percent looks perfectly plausible on a panel
/// and is exactly the sort of number an FC would plan a fight around.</para>
///
/// <para>Like everything the connector feeds, this is optional. It only ever runs on doctrine fits,
/// which only exist when an FC has connected an Alliance Auth. If the DLL or the data file is
/// missing, <see cref="IsAvailable"/> is false and every caller hides its numbers rather than
/// showing zeroes.</para>
/// </summary>
public sealed class DogmaService
{
    private const string Dll = "fcat_dogma.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int fcat_dogma_load_sde(byte[] bytes, nuint len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr fcat_dogma_calculate(byte[] fitJsonUtf8);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void fcat_dogma_free(IntPtr s);

    /// <summary>Derived stats the engine adds on top of CCP's, which is why they are negative.
    /// Defined in EVEShipFit/sde-patched; keep these in step with it when the engine is bumped.</summary>
    private const string AttrDps       = "-12";   // damagePerSecondWithoutReload
    private const string AttrDroneDps  = "-14";
    private const string AttrAlpha     = "-11";   // damageAlpha
    private const string AttrEhp       = "-43";
    private const string AttrArmorEhp  = "-28";
    private const string AttrShieldEhp = "-30";
    private const string AttrHullEhp   = "-29";
    private const string AttrAlign     = "-1";
    private const string AttrMass      = "4";     // CCP's own

    // Range lives on the ITEM, not the ship - a fit can mount two weapon families with different
    // reach, so there is no single ship-level answer.
    private const string AttrOptimal   = "54";
    private const string AttrFalloff   = "158";
    private const string AttrFlightMs  = "281";   // on the missile itself
    private const string AttrVelocity  = "37";    //  "   "     "       " 

    private readonly object _gate = new();
    private readonly Dictionary<string, FitStats?> _cache = [];

    private bool _tried;

    private bool _available;

    /// <summary>
    /// True once the engine is loaded and can answer. False on a build with no DLL, or if sde.dat is
    /// missing - both of which degrade to "no numbers", never to wrong ones.
    ///
    /// <para>Reading this LOADS the engine if it has not been loaded yet, which is deliberate:
    /// callers check availability before asking for a number, so a property that only became true
    /// after the first calculation would report false forever and quietly switch every fit statistic
    /// off. Call <see cref="PrepareAsync"/> first to get the one-off cost off the UI thread.</para>
    /// </summary>
    public bool IsAvailable
    {
        get { EnsureLoaded(); return _available; }
    }

    /// <summary>Load the engine off the UI thread. Reading sde.dat is ~9MB from disk, which is not
    /// something to do while an FC is waiting for the fleet panel to paint.</summary>
    public Task PrepareAsync() => Task.Run(EnsureLoaded);

    /// <summary>The EVE build sde.dat was made from, for the diagnostics report.</summary>
    public int EveBuild { get; private set; }

    /// <summary>
    /// Every skill at V. The engine gives NO bonus for a skill it was not told about, so "all V" has
    /// to be spelled out rather than implied - which is why the id list ships with the app.
    /// All V is the right assumption for a doctrine: it is what the fit was designed around, and it
    /// is what every other fitting tool shows by default.
    /// </summary>
    private string? _allVSkills;

    /// <summary>Loads the engine. Called lazily, so an FC with no auth never pays for it.</summary>
    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_tried) return;
            _tried = true;

            try
            {
                var baseDir = AppContext.BaseDirectory;
                var sdePath = Path.Combine(baseDir, "sde.dat");
                if (!File.Exists(sdePath))
                {
                    Log.Warn("dogma", $"sde.dat not found beside the app ({sdePath}) - fit stats are off");
                    return;
                }

                var bytes = File.ReadAllBytes(sdePath);
                var build = fcat_dogma_load_sde(bytes, (nuint)bytes.Length);
                if (build < 0)
                {
                    Log.Warn("dogma", $"the engine refused sde.dat (code {build}) - fit stats are off");
                    return;
                }

                EveBuild = build;
                _allVSkills = LoadAllVSkills();
                _available = true;
                Log.Info("dogma", $"engine loaded, EVE build {build}, {_allVSkills.Count(c => c == ':')} skills at V");
            }
            catch (DllNotFoundException)
            {
                Log.Warn("dogma", $"{Dll} is missing - fit statistics are off for this build");
            }
            catch (Exception ex)
            {
                Log.Warn("dogma", "could not start the dogma engine", ex);
            }
        }
    }

    private static string LoadAllVSkills()
    {
        // The list is an embedded asset rather than an ESI sweep: it is a few kilobytes, it never
        // needs the network, and it means fit numbers work offline like the rest of FCAT.
        using var stream = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("FCAT.Assets.skills.json");

        if (stream == null) return string.Empty;

        using var doc = JsonDocument.Parse(stream);
        var sb = new StringBuilder();
        foreach (var id in doc.RootElement.EnumerateArray())
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append('"').Append(id.GetInt32()).Append("\":5");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Run a doctrine fit through the engine. <paramref name="ammoTypeId"/> is the charge to load
    /// into every weapon; Alliance Auth does not record one (its importer drops the charge), so the
    /// caller decides and the number is only meaningful beside the ammo it was calculated with.
    ///
    /// Returns null when the engine is unavailable or the fit is one it cannot read.
    /// </summary>
    public FitStats? Calculate(AaFitting fit, int? ammoTypeId = null)
    {
        EnsureLoaded();
        if (!IsAvailable) return null;

        var key = $"{fit.Id}:{ammoTypeId ?? 0}";
        lock (_gate)
            if (_cache.TryGetValue(key, out var hit)) return hit;

        var stats = Run(fit, ammoTypeId);

        lock (_gate) _cache[key] = stats;
        return stats;
    }

    /// <summary>Format a damage or hitpoint figure the way an FC reads one: 34.2k, not 34,187.</summary>
    public static string Short(double value) =>
        value >= 1_000_000 ? $"{value / 1_000_000:0.#}m"
      : value >= 10_000    ? $"{value / 1_000:0}k"
      : value >= 1_000     ? $"{value / 1_000:0.#}k"
      :                      $"{value:0}";

    /// <summary>Drop cached results - after a refresh brings down changed fits, or an ammo change.</summary>
    public void Invalidate()
    {
        lock (_gate) _cache.Clear();
    }

    private FitStats? Run(AaFitting fit, int? ammoTypeId)
    {
        try
        {
            var json = BuildFitJson(fit, ammoTypeId);

            IntPtr result;
            lock (_gate) result = fcat_dogma_calculate(Encoding.UTF8.GetBytes(json + "\0"));
            if (result == IntPtr.Zero)
            {
                Log.Warn("dogma", $"the engine could not read fit {fit.Id} ({fit.Name})");
                return null;
            }

            string payload;
            try { payload = Marshal.PtrToStringUTF8(result) ?? string.Empty; }
            finally { fcat_dogma_free(result); }

            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                Log.Warn("dogma", $"{fit.ShipTypeName} / {fit.Name}: {error.GetString()}");
                return null;
            }

            var attrs = doc.RootElement.GetProperty("ship").GetProperty("attributes");

            double Value(string id) =>
                attrs.TryGetProperty(id, out var a) && a.TryGetProperty("value", out var v)
                    ? v.GetDouble() : 0;

            var (optimal, falloff, missile) = WeaponRange(doc.RootElement);

            return new FitStats(
                Dps:          Value(AttrDps),
                DroneDps:     Value(AttrDroneDps),
                Alpha:        Value(AttrAlpha),
                Ehp:          Value(AttrEhp),
                ArmorEhp:     Value(AttrArmorEhp),
                ShieldEhp:    Value(AttrShieldEhp),
                HullEhp:      Value(AttrHullEhp),
                Mass:         Value(AttrMass),
                AlignTime:    Value(AttrAlign),
                Optimal:      optimal,
                Falloff:      falloff,
                MissileRange: missile);
        }
        catch (Exception ex)
        {
            Log.Warn("dogma", $"fit {fit.Id} ({fit.Name}) failed", ex);
            return null;
        }
    }

    /// <summary>
    /// The reach of the fit's first real weapon, read off the calculated items.
    ///
    /// The first is good enough: a doctrine fit mounts one weapon family, and a fit that does not is
    /// not one an FC is reading a single range off anyway. Turrets answer with optimal and falloff;
    /// a launcher has neither, so its loaded missile answers instead with flight time times velocity.
    /// </summary>
    private static (double Optimal, double Falloff, double Missile) WeaponRange(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return (0, 0, 0);

        static double Attr(JsonElement owner, string id) =>
            owner.TryGetProperty("attributes", out var attrs)
            && attrs.TryGetProperty(id, out var a)
            && a.TryGetProperty("value", out var v)
                ? v.GetDouble() : 0;

        foreach (var item in items.EnumerateArray())
        {
            var optimal = Attr(item, AttrOptimal);
            var falloff = Attr(item, AttrFalloff);
            if (optimal > 0) return (optimal, falloff, 0);

            if (item.TryGetProperty("charge", out var charge) && charge.ValueKind == JsonValueKind.Object)
            {
                var flight   = Attr(charge, AttrFlightMs);
                var velocity = Attr(charge, AttrVelocity);
                if (flight > 0 && velocity > 0) return (0, 0, flight / 1000.0 * velocity);
            }
        }

        return (0, 0, 0);
    }

    /// <summary>The engine's fit shape. Slots keep the index the fit gave them, so a gap in a fit's
    /// high slots does not silently shuffle the guns along.</summary>
    private string BuildFitJson(AaFitting fit, int? ammoTypeId)
    {
        var items = new StringBuilder();

        foreach (var item in fit.Items)
        {
            var slot = item.Slot switch
            {
                FitSlot.High      => "high",
                FitSlot.Mid       => "medium",
                FitSlot.Low       => "low",
                FitSlot.Rig       => "rig",
                FitSlot.Subsystem => "subsystem",
                FitSlot.Service   => "service",
                FitSlot.DroneBay  => "drone_bay",
                FitSlot.Cargo     => "cargo",
                _                 => null,
            };
            if (slot == null) continue;   // FighterBay and Invalid: nothing useful to say yet

            if (items.Length > 0) items.Append(',');
            items.Append("{\"type_id\":").Append(item.TypeId);

            // Bays and cargo are stacks, not positions.
            if (item.Slot is FitSlot.DroneBay or FitSlot.Cargo)
                items.Append(",\"slot\":{\"type\":\"").Append(slot).Append("\"},\"quantity\":")
                     .Append(Math.Max(1, item.Quantity));
            else
                items.Append(",\"slot\":{\"type\":\"").Append(slot)
                     .Append("\",\"index\":").Append(item.SlotIndex).Append('}');

            // Guns have to be running to do damage, and drones have to be OUT: a drone sitting in
            // the bay contributes nothing, so a fit whose damage is all drones - an Ishtar, say -
            // would read as zero. An FC asking what the fleet does means with drones launched.
            var state = item.Slot switch
            {
                FitSlot.High     => "active",
                FitSlot.DroneBay => "active",
                FitSlot.Cargo    => "offline",   // cargo is carried, not running
                _                => "online",
            };
            items.Append(",\"state\":\"").Append(state).Append('"');

            if (ammoTypeId is > 0 && item.Slot == FitSlot.High)
                items.Append(",\"charge\":{\"type_id\":").Append(ammoTypeId.Value).Append('}');

            items.Append('}');
        }

        return "{\"fit\":{\"ship\":{\"type_id\":" + fit.ShipTypeId + "},"
             + "\"items\":[" + items + "],"
             + "\"character\":{\"skills\":{" + _allVSkills + "}}}}";
    }
}
