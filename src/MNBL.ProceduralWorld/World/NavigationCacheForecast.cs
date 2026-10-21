using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-282 (RT-38): how long a world's first campaign start, or a load that must rebuild its navigation caches, waits for them, and
    // whether a load will. Pure managed: the setup screen (WorldSetupViewModel) and the loader (GeneratedWorldLoader) use it, and the
    // offline harnesses test it (Test-WorldSetup, Test-WorldStore).
    //
    // The model (findings §2 and §17): the installed SandBoxNavigationCache build is dominated by its settlement-to-settlement distance
    // pairs, which grow with the square of the settlement count, so the wait is a power law in n = towns + castles + villages:
    //     seconds(n) = s0 * (n / n0) ^ p
    // fitted (least squares on log n, log seconds) through each measured world's median cache build. With one measured world p is 2 (the
    // pair count) and the curve passes through it. The measurements are worldgen/policies/one-shot-budget.json navigationCacheBuild:
    //   world A (seed 42): 286 settlements, 15 runs, median 381.1 s (the engine thread froze 383 s, docs/first-test-profile.md);
    //   world B (seed 73): 410 settlements, 4 runs, median 829.1 s;
    // which give p = 2.16 and reproduce both. MMR-247 calibrates it: it adds the epic build's two measured worlds there (their settlement
    // counts and the cache record's generateMilliseconds of Default, Naval and All) and drops the pre-epic rows. The times are the measuring
    // machine's; another computer's differ, which the texts say.
    internal sealed class CacheBuildModel
    {
        public sealed class Measurement { public string World, Source; public int Settlements; public double[] Seconds; public double Median; }
        public readonly List<Measurement> Measurements = new List<Measurement>();
        public double ReferenceSettlements { get; private set; }
        public double ReferenceSeconds { get; private set; }
        public double Exponent { get; private set; }
        public string Machine { get; private set; }

        // The budget's navigationCacheBuild section: { machine, worlds: [ { world, settlements, seconds: [...], source } ] }. Null without it.
        public static CacheBuildModel FromPolicy(IDictionary<string, object> section)
        {
            if (section == null) return null;
            var model = new CacheBuildModel();
            object machine; if (section.TryGetValue("machine", out machine)) model.Machine = machine as string;
            foreach (var row in ((object[])section["worlds"]).Cast<IDictionary<string, object>>())
            {
                var seconds = ((object[])row["seconds"]).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).Where(v => v > 0).OrderBy(v => v).ToArray();
                int n = Convert.ToInt32(row["settlements"], CultureInfo.InvariantCulture);
                if (seconds.Length == 0 || n <= 0) throw new ArgumentException("A navigation cache measurement needs a settlement count and at least one positive time.");
                object source; row.TryGetValue("source", out source);
                model.Measurements.Add(new Measurement { World = Convert.ToString(row["world"], CultureInfo.InvariantCulture), Settlements = n, Seconds = seconds, Median = Median(seconds), Source = source as string });
            }
            if (model.Measurements.Count == 0) throw new ArgumentException("The navigation cache model has no measured world.");
            model.Fit();
            return model;
        }

        private void Fit()
        {
            var x = Measurements.Select(m => Math.Log(m.Settlements)).ToArray(); var y = Measurements.Select(m => Math.Log(m.Median)).ToArray();
            double mx = x.Average(), my = y.Average(), sxx = x.Sum(v => (v - mx) * (v - mx));
            // One world, or worlds of one size: the pair count's square law through their mean.
            Exponent = sxx < 1e-9 ? 2.0 : Math.Max(1.0, Math.Min(3.0, x.Zip(y, (u, v) => (u - mx) * (v - my)).Sum() / sxx));
            ReferenceSettlements = Math.Exp(mx); ReferenceSeconds = Math.Exp(my);
        }

        public double Seconds(int settlements)
        {
            if (settlements <= 0) return 0;
            return ReferenceSeconds * Math.Pow(settlements / ReferenceSettlements, Exponent);
        }

        public static int Settlements(int towns, int castles, int villages) { return Math.Max(0, towns) + Math.Max(0, castles) + Math.Max(0, villages); }

        // Whole minutes, rounded up, at least 1 (the texts' MINUTES).
        public static int Minutes(double seconds) { return (int)Math.Max(1, Math.Ceiling(seconds / 60.0)); }

        private static double Median(double[] sorted)
        {
            int n = sorted.Length;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        }

        public Dictionary<string, object> Report()
        {
            return new Dictionary<string, object> { ["exponent"] = Math.Round(Exponent, 4), ["referenceSettlements"] = Math.Round(ReferenceSettlements, 2), ["referenceSeconds"] = Math.Round(ReferenceSeconds, 2),
                ["machine"] = Machine, ["worlds"] = Measurements.Select(m => (object)new Dictionary<string, object> { ["world"] = m.World, ["settlements"] = m.Settlements, ["medianSeconds"] = m.Median }).ToList() };
        }
    }

    // Before a load: will the campaign map find its navigation caches? The cache key (NavigationCacheKey) is only built from the live campaign,
    // mid-load, so the loader predicts it from the published caches' manifests: a published cache of the save's scene, made by this module
    // build (moduleSha256) under this game and War Sails (the key's game line), is reused unless a settlement moved; anything else rebuilds.
    // A rebuild warns first. The newest manifest of the same scene also records how long its build took on this computer, which is a better
    // estimate than the model.
    internal sealed class CacheForecast
    {
        public const string Missing = "missing", OtherModule = "module", OtherGame = "game", Published = "published";
        public bool Reuse;
        public string Kind, Reason;
        public double? MeasuredSeconds;   // this world's last cache build on this computer, from a manifest of its scene
        public string MeasuredFolder;

        public static CacheForecast Predict(IEnumerable<KeyValuePair<string, IDictionary<string, object>>> manifests, string scene, string moduleSha256, string game)
        {
            var forecast = new CacheForecast { Kind = Missing, Reason = "no published navigation cache names the scene " + scene };
            string newest = null;
            foreach (var kv in manifests ?? Enumerable.Empty<KeyValuePair<string, IDictionary<string, object>>>())
            {
                var m = kv.Value;
                if (m == null || Text(m, "scene") != scene) continue;
                if (Text(m, "schema") != NavigationCacheStore.Schema || Convert.ToInt32(Value(m, "version") ?? 0, CultureInfo.InvariantCulture) != NavigationCacheStore.Version) continue;
                double total = 0; bool timed = true;
                var durations = Value(m, "durations") as IDictionary<string, object>;
                foreach (var type in NavigationCacheStore.TypeNames)
                {
                    object ms;
                    if (durations != null && durations.TryGetValue(type + "GenerateMilliseconds", out ms)) total += Convert.ToDouble(ms, CultureInfo.InvariantCulture) / 1000.0; else timed = false;
                }
                string created = Text(m, "createdUtc") ?? "";
                if (timed && total > 0 && (newest == null || String.CompareOrdinal(created, newest) > 0)) { newest = created; forecast.MeasuredSeconds = total; forecast.MeasuredFolder = kv.Key; }
                if (forecast.Reuse) continue;
                if (Text(m, "moduleSha256") != moduleSha256) { forecast.Kind = OtherModule; forecast.Reason = "the published caches of " + scene + " were made by another module build"; continue; }
                if (KeyLine(Text(m, "key"), "game") != game) { if (forecast.Kind == Missing) forecast.Kind = OtherGame; forecast.Reason = "the published caches of " + scene + " were made under another game or War Sails version"; continue; }
                forecast.Reuse = true; forecast.Kind = Published; forecast.Reason = "published caches of " + scene + " from this module build and game (" + kv.Key + ")";
            }
            return forecast;
        }

        // The value of a key text's name=value line (NavigationCacheKey.Text), or null.
        public static string KeyLine(string keyText, string name)
        {
            foreach (var line in (keyText ?? "").Split('\n'))
                if (line.StartsWith(name + "=", StringComparison.Ordinal)) return line.Substring(name.Length + 1);
            return null;
        }

        private static object Value(IDictionary<string, object> m, string key) { object v; return m.TryGetValue(key, out v) ? v : null; }
        private static string Text(IDictionary<string, object> m, string key) { return Value(m, key) as string; }

        public Dictionary<string, object> Report()
        {
            return new Dictionary<string, object> { ["reuse"] = Reuse, ["kind"] = Kind, ["reason"] = Reason, ["measuredSeconds"] = MeasuredSeconds, ["measuredFolder"] = MeasuredFolder };
        }
    }

    // The player texts (localizable string ids; MMR-286 turns the loader's refusals into TextObjects the same way).
    internal static class CacheBuildText
    {
        public const string Preview = "{=mnbl_ws_wait_navigation}The first start of this world's campaign prepares its navigation: about {MINUTES} minutes for its {SETTLEMENTS} settlements on the computer the wait was measured on. The game may stop responding until it is done; do not close it. After an update of this mod, the world's next start or load prepares it once more.";
        public const string LoadTitle = "{=mnbl_load_rebuild_title}This load prepares the world's navigation";
        public const string Load = "{=mnbl_load_rebuild}Loading {SAVE} first prepares its world's navigation again ({REASON}). That takes about {MINUTES} minutes for its {SETTLEMENTS} settlements, during which the game may stop responding; do not close it. It happens once per world after an update of this mod or when the world's navigation files were removed.";
        public const string LoadMeasured = "{=mnbl_load_rebuild_measured}Loading {SAVE} first prepares its world's navigation again ({REASON}). Its last preparation on this computer took about {MINUTES} minutes, during which the game may stop responding; do not close it. It happens once per world after an update of this mod or when the world's navigation files were removed.";
        public const string LoadContinue = "{=mnbl_load_rebuild_continue}Load";
        public const string LoadCancel = "{=mnbl_load_rebuild_cancel}Cancel";
        public const string ReasonModule = "{=mnbl_load_rebuild_module}this version of the mod has not prepared it yet";
        public const string ReasonMissing = "{=mnbl_load_rebuild_missing}its navigation files are not on this computer";
        public const string ReasonGame = "{=mnbl_load_rebuild_game}the game or War Sails was updated";
        public static string Reason(string kind) { return kind == CacheForecast.OtherModule ? ReasonModule : kind == CacheForecast.OtherGame ? ReasonGame : ReasonMissing; }
    }
}
