using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace MNBL.CombatProbe
{
    // Isolated custom-game fixture. All HP resets and teleports are setup, never proof of damage.
    public sealed class CombatProbe : MissionLogic
    {
        public static CombatProbe Active;
        public static bool Complete;
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        public Agent Attacker;
        public readonly List<Agent> Targets = new List<Agent>();
        public int AttackId;
        public bool Intervention;
        public bool ShieldTrial;
        public string Trial;
        public readonly object LedgerLock = new object();
        public readonly HashSet<int> Contacts = new HashSet<int>();
        public readonly Dictionary<int, int> DamageCounts = new Dictionary<int, int>();
        private Vec3 origin;
        private float elapsed, trialStart, sampleAt;
        private int trialIndex = -1;
        private bool ready, failed, released;
        private bool readied, swingObserved;
        private float lastContactProgress=-1;
        private Agent.ActionStage lastStage;
        private ActionIndexCache readyAction, releaseAction;
        private ProbeInputComponent controlledInput;
        private readonly Dictionary<int,ProbeInputComponent> defenderInputs=new Dictionary<int,ProbeInputComponent>();
        private bool screenshotRequested;
        private bool releaseStageObserved;
        private Team[] teams;

        public override void AfterStart()
        {
            base.AfterStart();
            Active = this;
            Complete = false;
            try
            {
                var landmark = Mission.Scene.FindEntityWithTag("attacker_mid");
                if (landmark == null) throw new InvalidOperationException("benchmark_battle_11 attacker_mid missing");
                origin = landmark.GetGlobalFrame().origin;
                var troop = MBObjectManager.Instance.GetObject<BasicCharacterObject>("imperial_legionary");
                if (troop == null) throw new InvalidOperationException("imperial_legionary not loaded");
                var items = MBObjectManager.Instance.GetObjectTypeList<ItemObject>();
                var weapon = items.Where(i => i.PrimaryWeapon != null && i.PrimaryWeapon.WeaponClass == WeaponClass.TwoHandedSword)
                    .OrderByDescending(i => i.PrimaryWeapon.WeaponLength).FirstOrDefault();
                var shield = items.FirstOrDefault(i => i.PrimaryWeapon != null && i.PrimaryWeapon.IsShield);
                if (weapon == null || shield == null) throw new InvalidOperationException("Fixture sword/shield missing");
                teams = new[] { Mission.Teams.Add(BattleSideEnum.Attacker, 0xff3355bb, 0xffeeeeee, new Banner()),
                    Mission.Teams.Add(BattleSideEnum.Defender, 0xffbb4433, 0xffeeeeee, new Banner()) };
                teams[0].SetIsEnemyOf(teams[1], true);
                Mission.Teams.Player = teams[0];
                Mission.SetMissionMode(MissionMode.Battle, true);
                Equipment atk = troop.FirstBattleEquipment.Clone(false);
                for (int i=0;i<4;i++) atk[(EquipmentIndex)i] = EquipmentElement.Invalid;
                atk[EquipmentIndex.Weapon0] = new EquipmentElement(weapon);
                Attacker = Spawn(troop, atk, 0, origin, AgentControllerType.AI);
                controlledInput=new ProbeInputComponent(Attacker);
                Attacker.AddComponent(controlledInput);
                Attacker.SetHasOnAiInputSetCallback(true);
                Attacker.WieldInitialWeapons();
                ProbeLog.Event("input_owner",new {mode="native_ai_callback",enabled=Attacker.GetHasOnAiInputSetCallback(),controller=Attacker.Controller.ToString(),humanAI=Attacker.HumanAIComponent!=null,note="Input is overwritten in native AI callback. No player-controller agent. Defenders use owned native AI directional defense input."});
                for (int i=0;i<3;i++)
                {
                    Equipment eq = troop.FirstBattleEquipment.Clone(false);
                    for (int j=0;j<4;j++) eq[(EquipmentIndex)j] = EquipmentElement.Invalid;
                    eq[EquipmentIndex.Weapon0] = new EquipmentElement(shield);
                    var v=Spawn(troop, eq, 1, Point(i), AgentControllerType.AI);
                    var input=new ProbeInputComponent(v);
                    v.AddComponent(input);
                    v.SetHasOnAiInputSetCallback(true);
                    defenderInputs.Add(v.Index,input);
                    v.WieldInitialWeapons();
                    Targets.Add(v);
                }
                ProbeLog.Event("fixture", new { weapon=weapon.StringId, length=weapon.PrimaryWeapon.WeaponLength, shield=shield.StringId,
                    attacker=Attacker.Index, victims=Targets.Select(a=>a.Index).ToArray(), model=MissionGameModels.Current.AgentApplyDamageModel.GetType().FullName,
                    mainThread=Thread.CurrentThread.ManagedThreadId, osThread=GetCurrentThreadId(), note="AI controller with owned native AI input callback; no scripted damage. OnScoreHit is an XP integration seam, not proof of campaign XP." });
                ready=true;
            }
            catch(Exception e) { Fail(e); }
        }

        private Agent Spawn(BasicCharacterObject troop, Equipment eq, int side, Vec3 p, AgentControllerType controller)
        {
            p.z=Mission.Scene.GetGroundHeightAtPosition(p)+0.1f;
            var data=new AgentBuildData(troop).Team(teams[side]).Equipment(eq).FixedEquipment(true)
                .NoHorses(true).Controller(controller).InitialPosition(p).InitialDirection(new Vec2(0,side==0?1:-1)).EquipmentSeed(1);
            if(controller==AgentControllerType.AI) data.Formation(teams[side].GetFormation(FormationClass.Infantry));
            var a=Mission.SpawnAgent(data,false);
            a.BaseHealthLimit=2000; a.HealthLimit=2000; a.Health=2000;
            var idle=ActionIndexCache.Create("act_idle_unarmed_1");
            ProbeLog.Event("spawn_animation",new {a.Index,controller=controller.ToString(),human=a.IsHuman,flags=a.GetAgentFlags().ToString(),active=a.IsActive(),actionSet=a.ActionSet.GetName(),valid=a.ActionSet.IsValid,idle=idle.Index,clip=a.ActionSet.IsValid&&MBActionSet.CheckActionAnimationClipExists(a.ActionSet,idle),missionTime=Mission.CurrentTime});
            if(a.ActionSet.IsValid && idle.Index>=0) {
                bool accepted=a.SetActionChannel(0,idle,true);
                ProbeLog.Event("fixture_idle",new {a.Index,accepted,action=a.GetCurrentAction(0).Index});
            }
            return a;
        }
        private Vec3 Point(int i)
        {
            var p=origin;
            // Geometry revision 2: avoid the right-ready weapon starting inside
            // the shield. Apply identically to all baseline/intervention trials.
            p.x+=(i-1)*0.65f; p.y+=1.65f;
            p.z=Mission.Scene.GetGroundHeightAtPosition(p)+0.1f;
            return p;
        }
        private void BeginTrial()
        {
            trialIndex++;
            if(trialIndex>=8) { Complete=true; ready=false; Intervention=false; controlledInput.Requested=0;foreach(var input in defenderInputs.Values)input.Requested=0; Attacker.MovementFlags=Agent.MovementControlFlag.None;
                ProbeLog.Event("combat_complete",new { trials=trialIndex, note="Inspect per-trial native contact and HP evidence; completion is not pass." }); return; }
            Intervention=(trialIndex%2)==1;
            ShieldTrial=(trialIndex%4)>=2;
            Trial=(Intervention?"intervention":"baseline")+(ShieldTrial?"_shield":"_unguarded")+"_"+(trialIndex<4?"left":"right");
            lock(LedgerLock) { Contacts.Clear(); DamageCounts.Clear(); swingObserved=false;lastContactProgress=-1; }
            string direction=trialIndex<4?"left":"right";
            readyAction=RequireAction(Attacker,"act_ready_slash"+direction+"_2h");
            releaseAction=RequireAction(Attacker,"act_release_slash"+direction+"_2h");
            controlledInput.Requested=0;
            Attacker.SetActionChannel(1,ActionIndexCache.act_none,true);
            Attacker.TeleportToPosition(origin); Attacker.LookDirection=new Vec3(0,1,0); Attacker.Health=2000;
            for(int i=0;i<Targets.Count;i++) { var a=Targets[i]; a.TeleportToPosition(Point(i)); a.LookDirection=new Vec3(0,-1,0); a.Health=2000; a.RestoreShieldHitPoints();
                a.SetActionChannel(1,ActionIndexCache.act_none,true);
                defenderInputs[a.Index].Requested=ShieldTrial?(int)(trialIndex<4?Agent.MovementControlFlag.DefendRight:Agent.MovementControlFlag.DefendLeft):0;
                ProbeLog.Event("shield_fixture",new {Trial,a.Index,main=a.GetPrimaryWieldedItemIndex().ToString(),offhand=a.GetOffhandWieldedItemIndex().ToString(),stage=a.GetCurrentActionStage(1).ToString(),type=a.GetCurrentActionType(1).ToString(),requestedInput=defenderInputs[a.Index].Requested,callbackEnabled=a.GetHasOnAiInputSetCallback(),controller=a.Controller.ToString()}); }
            released=false; readied=false;screenshotRequested=false;releaseStageObserved=false;lastStage=Attacker.GetCurrentActionStage(1);trialStart=elapsed;
            ProbeLog.Event("trial_begin",new { Trial, AttackId, Intervention, ShieldTrial,geometryRevision=2,forwardDistance=1.65f,lateralSpacing=0.65f, targets=Targets.Select(a=>new {a.Index,hp=a.Health,x=a.Position.x,y=a.Position.y}).ToArray() });
        }
        public override void OnMissionTick(float dt)
        {
            if(!ready||failed) return;
            try
            {
                elapsed+=dt;
                if(elapsed<3) return;
                if(trialIndex<0) BeginTrial();
                if(!ready) return;
                float t=elapsed-trialStart;
                if(t>=1f&&!readied) {readied=true;controlledInput.Requested=(int)(trialIndex<4?Agent.MovementControlFlag.AttackLeft:Agent.MovementControlFlag.AttackRight);ProbeLog.Event("input_hold_command",new {Trial,requested=controlledInput.Requested,callbacks=Interlocked.Read(ref controlledInput.Callbacks)});}
                if(t>=1.8f&&!released) {released=true;controlledInput.Requested=0;ProbeLog.Event("input_release_command",new {Trial,callbacks=Interlocked.Read(ref controlledInput.Callbacks)});}
                var stage=Attacker.GetCurrentActionStage(1);
                if(stage==Agent.ActionStage.AttackRelease&&lastStage!=Agent.ActionStage.AttackRelease) {
                    if(releaseStageObserved) ProbeLog.Event("unexpected_additional_release",new {Trial,AttackId,progress=Attacker.GetCurrentActionProgress(1)});
                    else {releaseStageObserved=true;if(!swingObserved)StartObservedSwing("native_stage_transition",Attacker.GetCurrentActionProgress(1));}
                }
                lastStage=stage;
                if(!screenshotRequested&&stage==Agent.ActionStage.AttackRelease&&Attacker.GetCurrentActionProgress(1)>=0.12f) {
                    screenshotRequested=true;
                    var path=Path.Combine(ProbeLog.Store,"combat-"+Process.GetCurrentProcess().Id+"-"+Trial+".png");
                    TaleWorlds.Engine.Utilities.TakeScreenshot(path);
                    ProbeLog.Event("release_screenshot_requested",new {Trial,AttackId,path,progress=Attacker.GetCurrentActionProgress(1),note="Engine capture requested; file existence/content must be checked after rendering."});
                }
                if(elapsed>=sampleAt) {sampleAt=elapsed+0.1f; ProbeLog.Event("action",new {Trial,AttackId,t,inputCallbacks=Interlocked.Read(ref controlledInput.Callbacks),inputThread=controlledInput.LastThread,inputWritten=controlledInput.LastWritten,requestedInput=controlledInput.Requested,missionTime=Mission.CurrentTime,active=Attacker.IsActive(),mainHand=Attacker.GetPrimaryWieldedItemIndex().ToString(),action0=Attacker.GetCurrentAction(0).Index,action1=Attacker.GetCurrentAction(1).Index,progress0=Attacker.GetCurrentActionProgress(0),progress1=Attacker.GetCurrentActionProgress(1),type1=Attacker.GetCurrentActionType(1).ToString(),stage1=Attacker.GetCurrentActionStage(1).ToString(),flags=Attacker.MovementFlags.ToString(), defenders=Targets.Select(v=>new {v.Index,inputCallbacks=Interlocked.Read(ref defenderInputs[v.Index].Callbacks),inputThread=defenderInputs[v.Index].LastThread,inputWritten=defenderInputs[v.Index].LastWritten,requestedInput=defenderInputs[v.Index].Requested,action=v.GetCurrentAction(1).Index,type=v.GetCurrentActionType(1).ToString(),stage=v.GetCurrentActionStage(1).ToString(),progress=v.GetCurrentActionProgress(1),flags=v.MovementFlags.ToString()}).ToArray()});}
                if(t>=5.5f) { ProbeLog.Event("trial_end",new {Trial,AttackId,targets=Targets.Select(a=>new {a.Index,hp=a.Health,damage=2000-a.Health}).ToArray(),hits=DamageCounts.ToArray()}); BeginTrial(); }
            }
            catch(Exception e) {Fail(e);}
        }
        public override void OnAgentHit(Agent victim, Agent attacker, in MissionWeapon weapon, in Blow blow, in AttackCollisionData collision)
        {
            if(attacker!=Attacker) return;
            lock(LedgerLock) { if(!DamageCounts.ContainsKey(victim.Index)) DamageCounts[victim.Index]=0; DamageCounts[victim.Index]++; }
            ProbeLog.Event("agent_hit",new {Trial,AttackId,victim=victim.Index,damage=blow.InflictedDamage,hp=victim.Health,thread=Thread.CurrentThread.ManagedThreadId});
        }
        public override void OnScoreHit(Agent victim,Agent attacker,WeaponComponentData weapon,bool isBlocked,bool isSiegeEngineHit,in Blow blow,in AttackCollisionData collision,float damagedHp,float hitDistance,float shotDifficulty)
        {
            if(attacker==Attacker) ProbeLog.Event("score_hit",new {Trial,AttackId,victim=victim.Index,isBlocked,damagedHp,thread=Thread.CurrentThread.ManagedThreadId});
        }
        public override void OnRemoveBehavior() { ProbeLog.Event("combat_teardown",new {AttackId,thread=Thread.CurrentThread.ManagedThreadId}); if(Active==this) Active=null; base.OnRemoveBehavior(); }
        private void Fail(Exception e) { failed=true; ready=false; Intervention=false;if(controlledInput!=null)controlledInput.Requested=0;foreach(var input in defenderInputs.Values)input.Requested=0; ProbeLog.Event("combat_error",new {error=e.ToString()}); }
        private ActionIndexCache RequireAction(Agent agent,string name) {
            var action=ActionIndexCache.Create(name);
            if(action.Index<0||!agent.ActionSet.IsValid||!MBActionSet.CheckActionAnimationClipExists(agent.ActionSet,action)) throw new InvalidOperationException("Stock action unavailable: "+name);
            ProbeLog.Event("action_resolved",new {Trial,agent=agent.Index,name,index=action.Index,animation=agent.ActionSet.GetAnimationName(action)});
            return action;
        }
        private void StartObservedSwing(string source,float progress) {
            lock(LedgerLock) {AttackId++;Contacts.Clear();swingObserved=true;lastContactProgress=-1;}
            ProbeLog.Event("native_swing_begin",new {Trial,AttackId,source,progress,missionTime=Mission.CurrentTime,stage=Attacker.GetCurrentActionStage(1).ToString()});
        }
        public void ObserveContact(float progress) {
            if(!swingObserved) StartObservedSwing("first_native_collision",progress);
            // This fixture issues exactly one release per trial. A regressing native
            // progress value is evidence to investigate, never permission to clear dedup.
            if(progress+0.02f<lastContactProgress) ProbeLog.Event("unexpected_contact_progress_reset",new {Trial,AttackId,previous=lastContactProgress,progress});
            lastContactProgress=progress;
        }
    }
    // Native calls this after producing AI input. Override all inputs at that seam,
    // not on MissionTick where stock AI could overwrite the requested attack.
    internal sealed class ProbeInputComponent : AgentComponent
    {
        public volatile int Requested;
        public long Callbacks;
        public volatile int LastThread;
        public volatile int LastWritten;
        public ProbeInputComponent(Agent agent):base(agent) {}
        public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag,ref Agent.MovementControlFlag movementFlag,ref Vec2 inputVector)
        {
            eventFlag=Agent.EventControlFlag.None;
            movementFlag=(Agent.MovementControlFlag)Requested;
            inputVector=Vec2.Zero;
            LastWritten=(int)movementFlag;
            LastThread=Thread.CurrentThread.ManagedThreadId;
            Interlocked.Increment(ref Callbacks);
        }
    }
}




