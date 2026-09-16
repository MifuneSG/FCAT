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
    double AlignTime)
{
    /// <summary>Guns plus drones - what the FC means by "what does this do".</summary>
    public double TotalDps => Dps + DroneDps;
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

    private readonly object _gate = new();
    private readonly Dictionary<string, FitStats?> _cache = [];

    private bool _tried;

    /// <summary>True once the engine is loaded and can answer. False on a build with no DLL, or if
    /// sde.dat is missing - both of which have to degrade to "no numbers", never to wrong ones.</summary>
    public bool IsAvailable { get; private set; }

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
                IsAvailable = true;
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

            return new FitStats(
                Dps:       Value(AttrDps),
                DroneDps:  Value(AttrDroneDps),
                Alpha:     Value(AttrAlpha),
                Ehp:       Value(AttrEhp),
                ArmorEhp:  Value(AttrArmorEhp),
                ShieldEhp: Value(AttrShieldEhp),
                HullEhp:   Value(AttrHullEhp),
                Mass:      Value(AttrMass),
                AlignTime: Value(AttrAlign));
        }
        catch (Exception ex)
        {
            Log.Warn("dogma", $"fit {fit.Id} ({fit.Name}) failed", ex);
            return null;
        }
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

            // Guns have to be running to do damage; everything else merely online. Drones in the bay
            // stay put - a drone only adds damage once it is out, and how many an FC launches is
            // their call, not something a doctrine fit records.
            items.Append(",\"state\":\"").Append(item.Slot == FitSlot.High ? "active" : "online").Append('"');

            if (ammoTypeId is > 0 && item.Slot == FitSlot.High)
                items.Append(",\"charge\":{\"type_id\":").Append(ammoTypeId.Value).Append('}');

            items.Append('}');
        }

        return "{\"fit\":{\"ship\":{\"type_id\":" + fit.ShipTypeId + "},"
             + "\"items\":[" + items + "],"
             + "\"character\":{\"skills\":{" + _allVSkills + "}}}}";
    }
}
