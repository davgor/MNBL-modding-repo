using System;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MNBL.CombatProbe
{
    // Diagnostic only: mutate the managed boxed struct using named fields checked for existence;
    // never native offsets. No collision is invented: only an actual native callback qualifies.
    [HarmonyPatch(typeof(Mission), "MeleeHitCallback")]
    public static class CombatPatches
    {
        public sealed class ContactState { public bool Owned, Intervene, Shield, Duplicate, PreliminaryCrush; public int Victim, Attack; public float Hp; public ContactState Prior; }
        [ThreadStatic] internal static ContactState Current;
        private static readonly FieldInfo ShieldField=AccessTools.Field(typeof(AttackCollisionData),"_attackBlockedWithShield");
        private static readonly FieldInfo BackShieldField=AccessTools.Field(typeof(AttackCollisionData),"_collidedWithShieldOnBack");
        private static readonly FieldInfo ResultField=AccessTools.Field(typeof(AttackCollisionData),"_collisionResult");
        public static bool Prefix(Agent attacker,Agent victim,ref AttackCollisionData collisionData,ref float inOutMomentumRemaining,ref MeleeCollisionReaction colReaction,bool crushedThroughWithoutAgentCollision,out ContactState __state)
        {
            __state=new ContactState {Prior=Current};
            var p=CombatProbe.Active;
            if(p==null||attacker!=p.Attacker||victim==null||!p.Targets.Contains(victim)) return true;
            p.ObserveContact(collisionData.AttackProgress);
            bool rawShield=collisionData.AttackBlockedWithShield||collisionData.CollidedWithShieldOnBack;
            __state.Owned=true; __state.Intervene=p.Intervention; __state.Shield=rawShield||CombatNativeCrushPolicy.HasShieldDefense(p,victim.Index);
            __state.PreliminaryCrush=crushedThroughWithoutAgentCollision;
            __state.Victim=victim.Index; __state.Attack=p.AttackId; __state.Hp=victim.Health; Current=__state;
            if(p.Intervention&&!__state.PreliminaryCrush) lock(p.LedgerLock) __state.Duplicate=!p.Contacts.Add(victim.Index);
            ProbeLog.Event("contact_enter",new {p.Trial,p.AttackId,victim=victim.Index,shield=rawShield,shieldDefensePolicy=__state.Shield,preliminaryCrush=__state.PreliminaryCrush,duplicate=__state.Duplicate,result=collisionData.CollisionResult.ToString(),reaction=colReaction.ToString(),momentum=inOutMomentumRemaining,hp=victim.Health,progress=collisionData.AttackProgress,correctSideShieldBlock=collisionData.CorrectSideShieldBlock,attackBone=collisionData.AttackBoneIndex,collisionBone=collisionData.CollisionBoneIndex,bodyPart=collisionData.VictimHitBodyPart.ToString(),distanceOnWeapon=collisionData.CollisionDistanceOnWeapon,attackDirection=collisionData.AttackDirection.ToString(),thread=Thread.CurrentThread.ManagedThreadId,osThread=CombatProbe.GetCurrentThreadId(),nested=__state.Prior!=null});
            if(!p.Intervention) return true;
            // Native crush-through may emit a shield-only preliminary callback.
            // Shipping code deliberately omits RegisterBlow there. Do not consume
            // the victim's single damaging hit before a real body contact arrives.
            if(__state.PreliminaryCrush) { ProbeLog.Event("preliminary_crush_contact",new {p.Trial,p.AttackId,victim=victim.Index});return true; }
            if(__state.Duplicate) { colReaction=MeleeCollisionReaction.ContinueChecking; inOutMomentumRemaining=1f; return false; }
            if(__state.Shield)
            {
                if(ShieldField==null||BackShieldField==null||ResultField==null) throw new MissingFieldException("Collision contract changed");
                object boxed=collisionData;
                ShieldField.SetValue(boxed,false); BackShieldField.SetValue(boxed,false); ResultField.SetValue(boxed,(int)CombatCollisionResult.StrikeAgent);
                collisionData=(AttackCollisionData)boxed;
                // An incoming ContinueChecking otherwise skips damage; convert only this verified contact.
                if(colReaction==MeleeCollisionReaction.ContinueChecking) colReaction=MeleeCollisionReaction.Bounced;
            }
            return true;
        }
        public static void Postfix(Agent victim,ref AttackCollisionData collisionData,ref float inOutMomentumRemaining,ref MeleeCollisionReaction colReaction,ContactState __state)
        {
            if(__state==null||!__state.Owned) return;
            var p=CombatProbe.Active;
            var original=colReaction; float momentum=inOutMomentumRemaining;
            if(__state.Intervene) {colReaction=MeleeCollisionReaction.ContinueChecking;inOutMomentumRemaining=1f;collisionData.AttackerStunPeriod=0;}
            ProbeLog.Event("contact_exit",new {trial=p==null?"teardown":p.Trial,attack=__state.Attack,victim=__state.Victim,shield=__state.Shield,duplicate=__state.Duplicate,hpBefore=__state.Hp,hpAfter=victim==null?-1:victim.Health,damage=collisionData.InflictedDamage,reactionBefore=original.ToString(),reactionAfter=colReaction.ToString(),momentumBefore=momentum,momentumAfter=inOutMomentumRemaining,thread=Thread.CurrentThread.ManagedThreadId,osThread=CombatProbe.GetCurrentThreadId()});
            Current=__state.Prior;
        }
        public static Exception Finalizer(Exception __exception,ContactState __state) { if(__state!=null&&__state.Owned) Current=__state.Prior; return __exception; }
    }
    // Ask the native defense resolver to preserve its own ongoing attack state.
    // This callback precedes hit resolution; a late collision-result rewrite does
    // not alter the native collision record that schedules BlockedMelee.
    [HarmonyPatch(typeof(Mission),"GetDefendCollisionResults")]
    public static class CombatNativeCrushPolicy
    {
        private static readonly object Gate=new object();
        private static readonly HashSet<string> Defenses=new HashSet<string>();
        private static CombatProbe owner;
        private static string Key(CombatProbe p,int victim) {return p.Trial+":"+victim;}
        public static bool HasShieldDefense(CombatProbe p,int victim) {lock(Gate)return owner==p&&Defenses.Contains(Key(p,victim));}
        public static void Postfix(Agent attackerAgent,Agent defenderAgent,CombatCollisionResult collisionResult,float attackProgress,ref float attackerStunPeriod,ref bool crushedThrough)
        {
            var p=CombatProbe.Active;
            if(p==null||attackerAgent!=p.Attacker||defenderAgent==null||!p.Targets.Contains(defenderAgent))return;
            var offhand=defenderAgent.GetOffhandWieldedItemIndex();
            bool shield=offhand!=EquipmentIndex.None&&defenderAgent.Equipment[offhand].CurrentUsageItem!=null&&defenderAgent.Equipment[offhand].CurrentUsageItem.IsShield;
            bool before=crushedThrough;float stunBefore=attackerStunPeriod;
            if(p.Intervention&&shield) {
                lock(Gate) {if(owner!=p){owner=p;Defenses.Clear();}Defenses.Add(Key(p,defenderAgent.Index));}
                crushedThrough=true;attackerStunPeriod=0;
            }
            ProbeLog.Event("native_defense_policy",new {p.Trial,p.AttackId,victim=defenderAgent.Index,shield,result=collisionResult.ToString(),attackProgress,crushBefore=before,crushAfter=crushedThrough,stunBefore,stunAfter=attackerStunPeriod,thread=Thread.CurrentThread.ManagedThreadId});
        }
    }
    [HarmonyPatch(typeof(Mission), "CreateMeleeBlow")]
    public static class CombatShieldMitigation
    {
        public static void Postfix(ref Blow __result)
        {
            var c=CombatPatches.Current;
            if(c==null||!c.Owned||!c.Intervene||!c.Shield||c.Duplicate) return;
            int original=__result.InflictedDamage;
            __result.InflictedDamage=(int)Math.Round(original*0.35);
            __result.AttackerStunPeriod=0;
            ProbeLog.Event("shield_mitigation",new {c.Attack,c.Victim,bodyDamageBefore=original,bodyDamageAfter=__result.InflictedDamage,multiplier=0.35});
        }
    }
    [HarmonyPatch(typeof(Mission), "OnAgentHitBlocked")]
    public static class CombatBlockedTrace
    {
        public static void Prefix(Agent affectedAgent,Agent affectorAgent,ref AttackCollisionData collisionData)
        {
            var p=CombatProbe.Active;
            if(p!=null&&affectorAgent==p.Attacker) ProbeLog.Event("blocked_callback",new {p.Trial,p.AttackId,victim=affectedAgent==null?-1:affectedAgent.Index,shield=collisionData.AttackBlockedWithShield,result=collisionData.CollisionResult.ToString(),thread=Thread.CurrentThread.ManagedThreadId,osThread=CombatProbe.GetCurrentThreadId()});
        }
    }
}



