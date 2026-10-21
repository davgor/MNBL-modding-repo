using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-281: the patch of CompanionsCampaignBehavior's daily tick (GeneratedPatches.Manifest, world lifetime). Every target is located by signature
    // and IL content, never by a build string: the daily tick is the listener RegisterEvents passes to CampaignEvents.DailyTickEvent (the
    // ldftn after get_DailyTickEvent), an instance void method without parameters whose body calls exactly three of the behaviour's own
    // parameterless methods: the swap (its body calls LeaveSettlementAction.ApplyForCharacterOnly and EnterSettlementAction.ApplyForCharacterOnly),
    // the kill (KillCharacterAction.ApplyByRemove) and the spawn (through one of its own methods, HeroCreator.CreateSpecialHero); AdjustEquipments
    // is its instance void method of one Hero whose body reads Hero.BattleEquipment and Hero.CivilianEquipment. An unresolved target leaves
    // the installed tick unpatched and logged (wanderers then die and spawn as vanilla's). The private get__desiredTotalCompanionCount is never
    // patched (JIT inlining risk).
    internal static class CompanionsTickPatch
    {
        public const string BehaviourType = "TaleWorlds.CampaignSystem.CampaignBehaviors.CompanionsCampaignBehavior";

        public sealed class Resolution
        {
            public MethodInfo DailyTick, Swap, Kill, Spawn, AdjustEquipments;
            public string Problem;
            public bool Resolved { get { return Problem == null; } }
        }

        public static List<CodeInstruction> Instructions(MethodBase method) { return PatchProcessor.GetOriginalInstructions(method); }

        public static Resolution Resolve(Type behaviour, Func<MethodBase, List<CodeInstruction>> read)
        {
            var r = new Resolution();
            if (behaviour == null) { r.Problem = BehaviourType + " is not installed"; return r; }
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var methods = behaviour.GetMethods(all).Where(m => m.GetMethodBody() != null).OrderBy(m => m.MetadataToken).ToList();
            var register = methods.FirstOrDefault(m => m.Name == "RegisterEvents" && m.GetParameters().Length == 0);
            if (register == null) { r.Problem = "no RegisterEvents override"; return r; }
            var dailyEvent = AccessTools.PropertyGetter(typeof(CampaignEvents), nameof(CampaignEvents.DailyTickEvent));
            var code = read(register);
            int at = code.FindIndex(i => i.Calls(dailyEvent));
            var ldftn = at < 0 ? null : code.Skip(at + 1).TakeWhile(i => !(i.opcode == OpCodes.Call && i.operand is MethodInfo && ((MethodInfo)i.operand).DeclaringType == typeof(CampaignEvents)))
                                             .FirstOrDefault(i => i.opcode == OpCodes.Ldftn);
            r.DailyTick = ldftn == null ? null : ldftn.operand as MethodInfo;
            if (r.DailyTick == null || r.DailyTick.DeclaringType != behaviour || r.DailyTick.IsStatic || r.DailyTick.ReturnType != typeof(void) || r.DailyTick.GetParameters().Length != 0)
            { r.Problem = "RegisterEvents passes no instance void() listener of its own to CampaignEvents.DailyTickEvent"; return r; }
            var own = new HashSet<MethodInfo>(methods.Where(m => !m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 0));
            var callees = read(r.DailyTick).Where(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo && own.Contains((MethodInfo)i.operand))
                                           .Select(i => (MethodInfo)i.operand).ToList();
            Func<MethodInfo, Type, string, bool> calls = (m, type, name) => read(m).Any(i => i.operand is MethodInfo && ((MethodInfo)i.operand).DeclaringType == type && ((MethodInfo)i.operand).Name == name);
            var leave = typeof(TaleWorlds.CampaignSystem.Actions.LeaveSettlementAction); var enter = typeof(TaleWorlds.CampaignSystem.Actions.EnterSettlementAction);
            var kill = typeof(TaleWorlds.CampaignSystem.Actions.KillCharacterAction);
            r.Swap = callees.SingleOrDefault(m => calls(m, leave, "ApplyForCharacterOnly") && calls(m, enter, "ApplyForCharacterOnly"));
            r.Kill = callees.SingleOrDefault(m => calls(m, kill, "ApplyByRemove"));
            r.Spawn = callees.SingleOrDefault(m => read(m).Any(i => i.operand is MethodInfo && ((MethodInfo)i.operand).DeclaringType == behaviour &&
                                                                    calls((MethodInfo)i.operand, typeof(HeroCreator), "CreateSpecialHero")));
            if (callees.Count != 3 || callees.Distinct().Count() != 3 || r.Swap == null || r.Kill == null || r.Spawn == null || new[] { r.Swap, r.Kill, r.Spawn }.Distinct().Count() != 3)
            {
                r.Problem = "the daily tick " + r.DailyTick.Name + " calls [" + String.Join(", ", callees.Select(m => m.Name)) + "], not exactly a swap, a kill and a spawn";
                return r;
            }
            r.AdjustEquipments = methods.Where(m => !m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Hero))
                .SingleOrDefault(m => calls(m, typeof(Hero), "get_BattleEquipment") && calls(m, typeof(Hero), "get_CivilianEquipment"));
            if (r.AdjustEquipments == null) r.Problem = "no single instance void(Hero) method reads both Hero.BattleEquipment and Hero.CivilianEquipment (AdjustEquipments)";
            return r;
        }

        // The applied resolution (null while unpatched) and the generated campaign's handler (null: the installed tick runs).
        public static Resolution Current { get; private set; }
        public static Func<object, bool> Handler { get; set; }

        // Never throws: an unresolved target or a failed patch leaves the installed tick and is logged.
        public static bool Apply(Harmony harmony, Action<string> trace)
        {
            Resolution r;
            try { r = Resolve(AccessTools.TypeByName(BehaviourType), Instructions); }
            catch (Exception e) { trace("companions_tick_patch_unresolved:" + e.GetType().Name + ":" + e.Message + " (the installed daily tick runs)"); return false; }
            if (!r.Resolved) { trace("companions_tick_patch_unresolved:" + r.Problem + " (the installed daily tick runs)"); return false; }
            try { harmony.Patch(r.DailyTick, prefix: new HarmonyMethod(typeof(CompanionsTickPatch), nameof(Prefix))); }
            catch (Exception e)
            {
                try { harmony.Unpatch(r.DailyTick, HarmonyPatchType.Prefix, harmony.Id); } catch (Exception) { }
                trace("companions_tick_patch_unresolved:" + e.Message.Replace("\r", "").Replace("\n", " | ") + " (the installed daily tick runs)");
                return false;
            }
            Current = r;
            trace("companions_tick_patch_applied:" + r.DailyTick.Name + ":swap=" + r.Swap.Name + ":kill_skipped=" + r.Kill.Name + ":spawn_skipped=" + r.Spawn.Name);
            return true;
        }

        public static void Removed() { Current = null; }

        internal static bool Prefix(object __instance)
        {
            var handler = Handler;
            return handler == null || handler(__instance);
        }
    }
}
