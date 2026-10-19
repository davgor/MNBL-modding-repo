using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Web.Script.Serialization;

// MMR-34: static audit of the installed campaign behaviours and game models (docs/campaign-behavior-compatibility.md).
// BehaviorScan GAME_ROOT IDS_JSON OUT_JSON ASSEMBLY...
//   IDS_JSON: {"settlement": [...], "hero": [...], "clan": [...], "kingdom": [...], "culture": [...]} - the installed world object ids
//   (tools/Scan-CampaignBehaviors.ps1 writes it from the hash-checked catalog).
// For every type deriving from CampaignBehaviorBase or GameModel (and every type nested in one, so lambdas and state machines count for
// their behaviour), the method bodies are decoded and these are recorded:
//   * worldIds: string literals equal to an installed settlement, hero, clan or kingdom id that is not also a culture id;
//   * sharedIds: literals equal to a clan or kingdom id that is also a culture id (bandit clans such as looters, kingdoms such as
//     sturgia), which may be a culture check or a world lookup and are reviewed by hand;
//   * lookups: calls that find a world object by id (Settlement.Find, Hero.Find, Clan.FindFirst, Kingdom lookups,
//     MBObjectManager.GetObject<T>(string)) and the literal just before them, when there is one; for a predicate lookup
//     (Settlement.FindFirst(s => s.StringId == "town_ES4")) the literals compared inside the lambda the call consumes (the
//     nearest ldftn before the call), written as lambda:"id";
//   * extents: map-extent and map-geometry API calls (Campaign.MapMinimumPosition/MaximumPosition/Diagonal, MapSceneWrapper);
//   * coordinates: Vec2/CampaignVec2/Vec3 constructions whose arguments are float constants larger than 10 (Calradia coordinates).
// It also records engineDefaults: the item ids TaleWorlds.CampaignSystem.DefaultItems creates in code (Create("grain") ...), which every
// campaign registers although no XML declares them.
// Nothing is executed: assemblies are loaded for reflection and their IL read.
internal static class BehaviorScan
{
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)).GroupBy(o => o.Value).ToDictionary(g => g.Key, g => g.First());

    private sealed class Instruction { public OpCode Code; public object Operand; }

    private static int Main(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usage: BehaviorScan GAME_ROOT IDS_JSON OUT_JSON ASSEMBLY..."); return 2; }
        string root = args[0];
        var dirs = new[] { Path.Combine(root, "bin", "Win64_Shipping_Client") }.Concat(Directory.GetDirectories(Path.Combine(root, "Modules")).Select(m => Path.Combine(m, "bin", "Win64_Shipping_Client"))).Where(Directory.Exists).ToList();
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var d in dirs) { var p = Path.Combine(d, name); if (File.Exists(p)) return Assembly.LoadFrom(p); }
            return null; };
        var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        var ids = (IDictionary<string, object>)json.DeserializeObject(File.ReadAllText(args[1]));
        var cultures = new HashSet<string>(((object[])ids["culture"]).Cast<string>(), StringComparer.Ordinal);
        var world = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "settlement", "hero", "clan", "kingdom" })
            foreach (string id in (object[])ids[kind]) if (!world.ContainsKey(id)) world[id] = cultures.Contains(id) ? kind + "|culture" : kind;
        var campaign = Assembly.LoadFrom(Path.Combine(dirs[0], "TaleWorlds.CampaignSystem.dll"));
        var core = Assembly.LoadFrom(Path.Combine(dirs[0], "TaleWorlds.Core.dll"));
        var behaviorBase = campaign.GetType("TaleWorlds.CampaignSystem.CampaignBehaviorBase", true);
        var modelBase = core.GetType("TaleWorlds.Core.GameModel", true);
        var types = new List<object>();
        foreach (var path in args.Skip(3))
        {
            var assembly = Assembly.LoadFrom(path);
            Type[] all;
            try { all = assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); }
            foreach (var type in all.Where(t => !t.IsNested && t.IsClass && !t.IsAbstract && (behaviorBase.IsAssignableFrom(t) || modelBase.IsAssignableFrom(t))).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var findings = new Findings();
                foreach (var t in Self(type)) Scan(t, findings, world);
                types.Add(new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["type"] = type.FullName, ["assembly"] = assembly.GetName().Name, ["kind"] = behaviorBase.IsAssignableFrom(type) ? "behavior" : "model",
                    ["baseType"] = type.BaseType == null ? null : type.BaseType.FullName,
                    ["worldIds"] = findings.WorldIds.Where(x => !x.Key.Contains("|culture")).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => (object)x.Value.OrderBy(m => m, StringComparer.Ordinal).ToList()),
                    ["sharedIds"] = findings.WorldIds.Where(x => x.Key.Contains("|culture")).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => (object)x.Value.OrderBy(m => m, StringComparer.Ordinal).ToList()),
                    ["lookups"] = findings.Lookups.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    ["extents"] = findings.Extents.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    ["coordinates"] = findings.Coordinates.OrderBy(x => x, StringComparer.Ordinal).ToList() });
            }
        }
        var defaultItems = new SortedSet<string>(StringComparer.Ordinal);
        var defaults = campaign.GetType("TaleWorlds.CampaignSystem.DefaultItems", true);
        foreach (var method in defaults.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            List<Instruction> code;
            try { code = Decode(method); } catch { continue; }
            for (int i = 1; i < code.Count; i++)
            {
                var called = code[i].Operand as MethodBase;
                var literal = code[i - 1].Operand as string;
                if (called != null && called.Name == "Create" && called.DeclaringType == defaults && literal != null) defaultItems.Add(literal);
            }
        }
        var output = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.behavior-scan", ["version"] = 1, ["types"] = types,
            ["engineDefaults"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["Item"] = defaultItems.ToList() } };
        File.WriteAllText(args[2], json.Serialize(output), new UTF8Encoding(false));
        Console.WriteLine("scanned " + types.Count + " behaviour and model types");
        return 0;
    }

    private sealed class Findings
    {
        public Dictionary<string, SortedSet<string>> WorldIds = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);   // id -> methods
        public HashSet<string> Lookups = new HashSet<string>(StringComparer.Ordinal), Extents = new HashSet<string>(StringComparer.Ordinal), Coordinates = new HashSet<string>(StringComparer.Ordinal);
    }

    // The type and every type nested in it, recursively.
    private static IEnumerable<Type> Self(Type type)
    {
        yield return type;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) foreach (var t in Self(nested)) yield return t;
    }

    private static readonly string[] LookupNames = { "Settlement.Find", "Settlement.FindFirst", "Hero.Find", "Hero.FindFirst", "Clan.FindFirst", "Clan.Find", "Kingdom.Find", "MBObjectManager.GetObject" };
    private static readonly string[] ExtentNames = { "Campaign.get_MapMinimumPosition", "Campaign.get_MapMaximumPosition", "Campaign.get_MapDiagonal", "Campaign.get_MapDiagonalSquared",
                                                      "Campaign.get_MapSceneWrapper", "Campaign.get_MaximumDistanceBetweenTwoConnectedSettlements" };

    private static void Scan(Type type, Findings findings, Dictionary<string, string> world)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            List<Instruction> code;
            try { code = Decode(method); } catch { continue; }
            string where = type.Name + "." + method.Name;
            for (int i = 0; i < code.Count; i++)
            {
                var s = code[i].Operand as string;
                string kind;
                if (s != null && world.TryGetValue(s, out kind))
                {
                    SortedSet<string> set; if (!findings.WorldIds.TryGetValue(kind + ":" + s, out set)) findings.WorldIds[kind + ":" + s] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.Add(where);
                }
                var called = code[i].Operand as MethodBase;
                if (called == null || called.DeclaringType == null) continue;
                string name = called.DeclaringType.Name.Split('`')[0] + "." + called.Name;
                if (LookupNames.Contains(name))
                {
                    string literal = i > 0 ? code[i - 1].Operand as string : null;
                    var generic = called.IsGenericMethod ? "<" + String.Join(",", called.GetGenericArguments().Select(a => a.Name)) + ">" : "";
                    string argument = literal != null ? "(\"" + literal + "\")" : "(computed)";
                    if (literal == null)
                    {
                        // A predicate lookup: the lambda the call consumes is the nearest ldftn within the delegate-caching preamble.
                        var lambda = code.Skip(Math.Max(0, i - 12)).Take(Math.Min(12, i)).Reverse().Where(x => x.Code == OpCodes.Ldftn).Select(x => x.Operand as MethodBase).FirstOrDefault(m => m != null);
                        if (lambda != null)
                        {
                            List<Instruction> body;
                            try { body = Decode(lambda); } catch { body = new List<Instruction>(); }
                            var compared = body.Select(x => x.Operand as string).Where(s2 => s2 != null && world.ContainsKey(s2)).Distinct().ToList();
                            if (compared.Count > 0) argument = "(" + String.Join(",", compared.Select(c => "lambda:\"" + c + "\"")) + ")";
                        }
                    }
                    findings.Lookups.Add(name + generic + argument + " in " + where);
                }
                if (ExtentNames.Contains(name)) findings.Extents.Add(name + " in " + where);
                if (called.IsConstructor && (called.DeclaringType.Name == "Vec2" || called.DeclaringType.Name == "CampaignVec2" || called.DeclaringType.Name == "Vec3"))
                {
                    var constants = code.Skip(Math.Max(0, i - 4)).Take(Math.Min(4, i)).Select(x => x.Operand).Where(o => o is float || o is double).Select(Convert.ToDouble).ToList();
                    if (constants.Count >= 2 && constants.Any(v => Math.Abs(v) > 10)) findings.Coordinates.Add(called.DeclaringType.Name + "(" + String.Join(", ", constants) + ") in " + where);
                }
            }
        }
    }

    private static List<Instruction> Decode(MethodBase method)
    {
        var body = method.GetMethodBody(); var list = new List<Instruction>();
        if (body == null) return list;
        byte[] il = body.GetILAsByteArray(); var module = method.Module;
        Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[pos++]));
            OpCode code;
            if (!Codes.TryGetValue(value, out code)) throw new InvalidDataException("unknown opcode");
            object operand = null;
            switch (code.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: pos += 1; break;
                case OperandType.InlineVar: pos += 2; break;
                case OperandType.InlineBrTarget: case OperandType.InlineI: case OperandType.InlineSig: pos += 4; break;
                case OperandType.InlineI8: pos += 8; break;
                case OperandType.ShortInlineR: operand = BitConverter.ToSingle(il, pos); pos += 4; break;
                case OperandType.InlineR: operand = BitConverter.ToDouble(il, pos); pos += 8; break;
                case OperandType.InlineString: try { operand = module.ResolveString(BitConverter.ToInt32(il, pos)); } catch { } pos += 4; break;
                case OperandType.InlineMethod: try { operand = module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, methodArgs); } catch { } pos += 4; break;
                case OperandType.InlineField: case OperandType.InlineType: case OperandType.InlineTok: pos += 4; break;
                case OperandType.InlineSwitch: int n = BitConverter.ToInt32(il, pos); pos += 4 + 4 * n; break;
                default: throw new InvalidDataException("operand " + code.OperandType);
            }
            list.Add(new Instruction { Code = code, Operand = operand });
        }
        return list;
    }
}
