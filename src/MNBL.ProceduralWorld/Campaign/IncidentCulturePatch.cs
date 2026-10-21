using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-287 (findings §17 RT-29): four incidents whose trigger condition compares the last-visited settlement's MapFaction.StringId with
    // a vanilla kingdom id never fire on a generated world (generated kingdoms have world-prefixed ids). The world-lifetime transpiler
    // (GeneratedPatches.Manifest, id mnbl.proceduralworld.world) turns each of those comparisons into one of the faction's culture id:
    //   IFaction.get_StringId; ldstr "<kingdom id>"; String.op_Equality   becomes   FactionCultureId(IFaction); ldstr ...; op_Equality.
    // Every target is located by IL content, never by a compiler-generated name (which changes on any recompile) or a build string: the
    // condition is the first delegate (ldftn) after the incident's ldstr id in IncidentsCampaignBehaviour, and its comparisons are the
    // get_StringId calls on IFaction directly followed by one of the incident's kingdom literals and String.op_Equality. A condition is
    // patched only when its comparisons are exactly the expected literals; otherwise it is left as installed and logged: its incident stays
    // off (a kingdom-id comparison is never true on a generated world) and the campaign runs.
    internal static class IncidentCulturePatch
    {
        public const string BehaviourType = "TaleWorlds.CampaignSystem.CampaignBehaviors.IncidentsCampaignBehaviour";

        public sealed class Incident { public string Id, Name; public string[] Literals; }

        // The installed incidents (v1.4.8 IL) and the kingdom ids their conditions compare with, in comparison order.
        public static readonly Incident[] Incidents = {
            new Incident { Id = "incident_vedmaks_treasure", Name = "Vedmak's Treasure", Literals = new[] { "sturgia" } },
            new Incident { Id = "incident_the_deer_departed", Name = "The Deer Departed", Literals = new[] { "khuzait" } },
            new Incident { Id = "incident_kannic_splendors", Name = "Kannic Splendors", Literals = new[] { "aserai" } },
            new Incident { Id = "incident_solider_is_wanted_criminal", Name = "Soldier is Wanted Criminal", Literals = new[] { "empire", "vlandia" } } };

        public sealed class Resolution
        {
            public Incident Incident;
            public MethodInfo Condition;   // null when the incident's delegate was not found
            public List<int> Sites = new List<int>();   // instruction indices of the get_StringId calls to replace
            public string Problem;         // null when resolved
            public bool Resolved { get { return Problem == null; } }
        }

        // The instructions of a method body as Harmony reads them (offline tests pass the same reader).
        public static List<CodeInstruction> Instructions(MethodBase method) { return PatchProcessor.GetOriginalInstructions(method); }

        public static MethodInfo FactionCultureIdMethod { get { return AccessTools.Method(typeof(IncidentCulturePatch), nameof(FactionCultureId)); } }
        public static MethodInfo InstalledFactionStringId { get { return AccessTools.PropertyGetter(typeof(IFaction), nameof(IFaction.StringId)); } }
        private static readonly MethodInfo StringEquality = AccessTools.Method(typeof(string), "op_Equality", new[] { typeof(string), typeof(string) });

        // The replacement: the faction's culture id where the installed code reads the faction's own id (null stays null).
        public static string FactionCultureId(IFaction faction)
        {
            return faction == null || faction.Culture == null ? null : faction.Culture.StringId;
        }

        // Resolves every incident of the installed behaviour type. read: a method body's instructions (Instructions, or a test's copy).
        public static List<Resolution> Resolve(Type behaviour, Func<MethodBase, List<CodeInstruction>> read)
        {
            var result = Incidents.Select(i => new Resolution { Incident = i }).ToList();
            if (behaviour == null) { foreach (var r in result) r.Problem = BehaviourType + " is not installed"; return result; }
            var methods = behaviour.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.GetMethodBody() != null).OrderBy(m => m.MetadataToken).ToList();
            var bodies = new Dictionary<MethodBase, List<CodeInstruction>>();
            Func<MethodBase, List<CodeInstruction>> body = m => { List<CodeInstruction> b; if (!bodies.TryGetValue(m, out b)) bodies[m] = b = read(m); return b; };
            foreach (var r in result)
            {
                foreach (var m in methods)
                {
                    var code = body(m);
                    int at = code.FindIndex(i => i.opcode == OpCodes.Ldstr && (i.operand as string) == r.Incident.Id);
                    if (at < 0) continue;
                    var next = code.Skip(at + 1).FirstOrDefault(i => i.opcode == OpCodes.Ldftn);
                    r.Condition = next == null ? null : next.operand as MethodInfo;
                    break;
                }
                if (r.Condition == null) { r.Problem = "no delegate follows the incident id " + r.Incident.Id + " in " + BehaviourType; continue; }
                Locate(r, body(r.Condition));
            }
            return result;
        }

        // The comparisons of one condition: exactly the incident's literals, in order, each as IFaction.get_StringId; ldstr; op_Equality.
        public static void Locate(Resolution r, List<CodeInstruction> code)
        {
            r.Sites.Clear(); r.Problem = null;
            var found = new List<string>();
            var stringId = InstalledFactionStringId;
            for (int i = 0; i + 2 < code.Count; i++)
                if (code[i].Calls(stringId) && code[i + 1].opcode == OpCodes.Ldstr && code[i + 2].Calls(StringEquality))
                {
                    string literal = code[i + 1].operand as string;
                    if (!r.Incident.Literals.Contains(literal)) continue;
                    r.Sites.Add(i); found.Add(literal);
                }
            if (!found.SequenceEqual(r.Incident.Literals))
                r.Problem = "the condition " + (r.Condition == null ? "?" : r.Condition.Name) + " compares MapFaction.StringId with [" + String.Join(", ", found) +
                            "], not [" + String.Join(", ", r.Incident.Literals) + "]";
        }

        // The rewrite of one resolved condition's instructions (in place: labels and blocks stay on the replaced instruction).
        public static int Rewrite(List<CodeInstruction> code, Resolution r)
        {
            if (!r.Resolved) return 0;
            var helper = FactionCultureIdMethod;
            foreach (int i in r.Sites) { code[i].opcode = OpCodes.Call; code[i].operand = helper; }
            return r.Sites.Count;
        }

        private static readonly Dictionary<MethodBase, Resolution> planned = new Dictionary<MethodBase, Resolution>();
        private static int rewritten;
        internal static int Rewritten { get { return rewritten; } }

        // The log line of an incident left off: the condition stays as installed (a kingdom-id comparison, never true on a generated world).
        public static string Unresolved(Resolution r) { return "incident_culture_patch_unresolved:" + r.Incident.Id + ":" + r.Problem + " (the incident stays off)"; }

        // A resolved condition the transpiler rewrites when Harmony hands it over.
        internal static void Plan(Resolution r) { if (r.Resolved) planned[r.Condition] = r; }

        // Applies the transpiler to every resolved condition with the world's Harmony instance; logs each incident that stays off.
        // Never throws: an unresolved or failed condition only leaves its incident off.
        public static void Apply(Harmony harmony, Action<string> trace)
        {
            List<Resolution> resolutions;
            try { resolutions = Resolve(AccessTools.TypeByName(BehaviourType), Instructions); }
            catch (Exception e) { trace("incident_culture_patch_unresolved:all:" + e.GetType().Name + ":" + e.Message); return; }
            int patched = 0;
            foreach (var r in resolutions)
            {
                if (!r.Resolved) { trace(Unresolved(r)); continue; }
                Plan(r);
                int before = rewritten;
                try
                {
                    harmony.Patch(r.Condition, transpiler: new HarmonyMethod(typeof(IncidentCulturePatch), nameof(Transpiler)));
                    if (rewritten - before != r.Sites.Count) throw new InvalidOperationException("rewrote " + (rewritten - before) + " of " + r.Sites.Count + " comparisons");
                    patched++;
                }
                catch (Exception e)
                {
                    try { harmony.Unpatch(r.Condition, HarmonyPatchType.Transpiler, harmony.Id); } catch (Exception) { }
                    trace("incident_culture_patch_unresolved:" + r.Incident.Id + ":" + e.Message.Replace("\r", "").Replace("\n", " | ") + " (the incident stays off)");
                }
            }
            trace("incident_culture_patch_applied:" + patched + "/" + resolutions.Count);
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            Resolution r;
            if (original != null && planned.TryGetValue(original, out r))
            {
                // Re-locate on the instructions Harmony hands over (another mod's transpiler may run first): rewrite only an exact match.
                var mine = new Resolution { Incident = r.Incident, Condition = r.Condition };
                Locate(mine, code);
                rewritten += Rewrite(code, mine);
            }
            return code;
        }
    }
}
