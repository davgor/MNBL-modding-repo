using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.BattleScale.Core;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace MNBL.BattleScale
{
    // Opt-in laboratory mission only. Never attaches to campaign, siege or multiplayer missions.
    public sealed class LayeredBattleMission : MissionLogic
    {
        private sealed class Binding
        {
            internal long Lease;
            internal int Formation;
            internal int Slot;
            internal Agent Agent;
            internal float SpawnHealth;
            internal bool Demoting, Removed, Deleted, Released;
            internal bool ContactedEnemy;
            internal AgentOutcome Outcome = AgentOutcome.Returned;
        }
        private readonly Dictionary<Agent, Binding> agents = new Dictionary<Agent, Binding>();
        private BattleSimulation simulation;
        private BasicCharacterObject troop;
        private Team[] teams;
        private Vec3 first, second;
        private float elapsed, planElapsed, orderElapsed, reportElapsed, phaseSince;
        private int nativeLimit, nativeBudget, activeBudget, spawned, returned, killed, wounded, peakEntities, peakActive, phase;
        private bool failed, finished;
        private BattlePoint lastObserver;
        private readonly int[] spawnsByFormation = new int[4];
        private int nativeKilled, nativeWounded;

        public override void AfterStart()
        {
            try
            {
                nativeLimit = DefaultBattleMissionAgentSpawnLogic.MaxNumberOfAgentsForMission;
                nativeBudget = Math.Min(257, nativeLimit - 32);
                if (nativeBudget < 3) throw new InvalidOperationException("Insufficient native agent capacity.");
                activeBudget = Math.Min(128, (nativeBudget - 1) / 2);
                first = Landmark("attacker_mid"); second = Landmark("defend_mid");
                troop = MBObjectManager.Instance.GetObject<BasicCharacterObject>("imperial_legionary");
                if (troop == null) throw new InvalidOperationException("Custom Battle troop data was not loaded.");
                teams = new[] {
                    Mission.Teams.Add(BattleSideEnum.Attacker, 0xff3355bb, 0xffeeeeee, new Banner("11.4.124.4345.4345.768.768.1.0.0.163.0.5.512.512.769.764.1.0.0")),
                    Mission.Teams.Add(BattleSideEnum.Defender, 0xffbb4433, 0xffeeeeee, new Banner("11.45.126.4345.4345.768.768.1.0.0.462.0.13.512.512.769.764.1.0.0"))
                };
                teams[0].SetIsEnemyOf(teams[1], !SubModule.Probe);
                Mission.Teams.Player = teams[0];
                Mission.SetMissionMode(MissionMode.Battle, true);
                Mission.SetMissionCorpseFadeOutTimeInSeconds(10);
                simulation = new BattleSimulation(new BattleSettings { EntityBudget = activeBudget, PromotionDistance = 55, DemotionDistance = 80 });
                for (int i = 0; i < 4; i++)
                {
                    Vec3 origin = i < 2 ? first : second;
                    simulation.AddFormation(new FormationDefinition { Id = i, Team = i % 2, Population = 25000,
                        Position = new BattlePoint(origin.x, origin.y + (i % 2 == 0 ? -20 : 20)), AttackRange = 50 });
                }
                simulation.SetEngagement(0, 1); simulation.SetEngagement(1, 0);
                simulation.SetEngagement(2, 3); simulation.SetEngagement(3, 2);
                Vec3 playerPosition = first; playerPosition.y -= 30;
                var player = Spawn(playerPosition, 0, AgentControllerType.Player, 0);
                if (player == null) throw new InvalidOperationException("Player agent spawn failed.");
                lastObserver = new BattlePoint(playerPosition.x, playerPosition.y);
                SubModule.Log("mission started; nativeLimit=" + nativeLimit + "; logicalTroops=100000; nativeBudget=" + nativeBudget);
                InformationManager.DisplayMessage(new InformationMessage("Battle Scale experiment: 100,000 logical troops; at most " + activeBudget + " active soldiers. Distant battles are abstract. No campaign saves."));
            }
            catch (Exception error) { Fail(error); }
        }

        private Vec3 Landmark(string tag)
        {
            var entity = Mission.Scene.FindEntityWithTag(tag);
            if (entity == null) throw new InvalidOperationException("Scene landmark missing: " + tag);
            return entity.GetGlobalFrame().origin;
        }
        private Agent Spawn(Vec3 position, int side, AgentControllerType controller, long seed)
        {
            position.z = Mission.Scene.GetGroundHeightAtPosition(position) + 0.1f;
            var direction = new Vec2(0, side == 0 ? 1 : -1);
            var data = new AgentBuildData(troop).Team(teams[side]).NoHorses(true)
                .Controller(controller).InitialPosition(position).InitialDirection(direction).EquipmentSeed((int)(seed % int.MaxValue));
            if (controller == AgentControllerType.AI) data.Formation(teams[side].GetFormation(FormationClass.Infantry));
            return Mission.SpawnAgent(data, false);
        }

        public override void OnMissionTick(float dt)
        {
            if (failed || finished || simulation == null) return;
            try
            {
                elapsed += dt; planElapsed += dt; orderElapsed += dt; reportElapsed += dt;
                // A removed agent cannot fight, but still occupies a native slot until deletion.
                // Reconcile logical outcomes next tick; AllAgents independently gates physical capacity.
                foreach (Binding binding in agents.Values.Where(x => (x.Removed || x.Deleted) && !x.Released).ToArray())
                {
                    // Never represent one soldier in both simulations if callback ordering changes.
                    if (!binding.Deleted && binding.Agent.IsActive()) continue;
                    if (!simulation.ReleaseAgent(binding.Lease, binding.Outcome)) throw new InvalidOperationException("Logical lease release failed.");
                    binding.Released = true;
                    if (binding.Outcome == AgentOutcome.Returned) returned++;
                    else if (binding.Outcome == AgentOutcome.Killed) killed++; else wounded++;
                }
                foreach (Binding binding in agents.Values.Where(x => x.Deleted).ToArray()) agents.Remove(binding.Agent);
                if (SubModule.Probe)
                {
                    if (phase == 0 && elapsed >= 10 && Mission.MainAgent != null) { Mission.MainAgent.TeleportToPosition(second); phase = 1; phaseSince = elapsed; }
                    if (phase == 1 && spawnsByFormation[2] >= 48 && spawnsByFormation[3] >= 48 && Mission.MainAgent != null)
                    { Mission.MainAgent.TeleportToPosition(first); phase = 2; phaseSince = elapsed; }
                    if (phase == 2 && spawnsByFormation[0] >= 112 && spawnsByFormation[1] >= 112)
                    {
                        teams[0].SetIsEnemyOf(teams[1], true);
                        phase = 3; phaseSince = elapsed;
                    }
                    if (elapsed - phaseSince > 100 && phase < 3) throw new InvalidOperationException("Native slot reuse did not complete within 100 seconds.");
                }
                if (Mission.MainAgent != null) lastObserver = new BattlePoint(Mission.MainAgent.Position.x, Mission.MainAgent.Position.y);
                var observer = lastObserver;
                simulation.Advance(dt, observer);
                if (planElapsed >= 0.25f)
                {
                    planElapsed = 0;
                    SimulationPlan plan = simulation.Plan(observer);
                    var candidates = new HashSet<long>(plan.DemoteLeaseIds);
                    foreach (Binding b in agents.Values.ToArray())
                    {
                        // Never demote injured units or units in local contact. Their health/equipment state
                        // cannot be faithfully represented by this aggregate prototype.
                        if (!candidates.Contains(b.Lease) || b.Released || b.Demoting || b.ContactedEnemy || !b.Agent.IsActive() || b.Agent.Health < b.SpawnHealth - 0.01f) continue;
                        if (!SubModule.Probe && agents.Values.Any(other => other.Formation % 2 != b.Formation % 2 && other.Agent.IsActive()
                            && (other.Agent.Position - b.Agent.Position).LengthSquared < 35 * 35)) continue;
                        if (new BattlePoint(b.Agent.Position.x, b.Agent.Position.y).DistanceSquared(observer) < 80 * 80) continue;
                        b.Demoting = true;
                        b.Agent.FadeOut(true, false);
                    }
                    int batch = 0;
                    foreach (SpawnTicket ticket in plan.SpawnTickets)
                    {
                        if (batch >= 8 || Mission.AllAgents.Count >= nativeBudget) { simulation.CancelSpawn(ticket.Id); continue; }
                        Agent agent = null;
                        try
                        {
                            FormationSnapshot formation = simulation.GetFormation(ticket.FormationId);
                            // Spatial slots are separate from reservation ids, which also advance on cancellations.
                            var usedSlots = new HashSet<int>(agents.Values.Where(x => x.Formation == ticket.FormationId).Select(x => x.Slot));
                            int slot = 0;
                            while (usedSlots.Contains(slot)) slot++;
                            Vec3 position = new Vec3((float)formation.Position.X + (slot % 8) * 1.5f - 5,
                                (float)formation.Position.Y + (slot / 8) * (formation.Team == 0 ? -1.5f : 1.5f), 0);
                            agent = Spawn(position, formation.Team, AgentControllerType.AI, ticket.Id);
                            if (agent == null) throw new InvalidOperationException("Native spawn returned null.");
                            if (!simulation.CommitSpawn(ticket.Id)) throw new InvalidOperationException("Spawn reservation lost.");
                            agents.Add(agent, new Binding { Agent = agent, Formation = ticket.FormationId, Lease = ticket.Id, Slot = slot, SpawnHealth = agent.Health });
                            spawned++; spawnsByFormation[ticket.FormationId]++; batch++;
                        }
                        catch
                        {
                            simulation.CancelSpawn(ticket.Id);
                            if (agent != null) agent.FadeOut(true, false);
                            // Cancel every uncommitted reservation before aborting this mission.
                            foreach (SpawnTicket pending in plan.SpawnTickets) simulation.CancelSpawn(pending.Id);
                            throw;
                        }
                    }
                }
                if (orderElapsed >= 1 && (!SubModule.Probe || phase >= 3))
                {
                    orderElapsed = 0;
                    foreach (Team team in teams) team.GetFormation(FormationClass.Infantry).SetMovementOrder(MovementOrder.MovementOrderCharge);
                    var live = agents.Values.Where(x => !x.Demoting && x.Agent.IsActive()).ToArray();
                    foreach (Binding b in live)
                    {
                        Binding target = live.Where(x => x.Formation % 2 != b.Formation % 2)
                            .OrderBy(x => (x.Agent.Position - b.Agent.Position).LengthSquared).FirstOrDefault();
                        if (target != null)
                        {
                            if ((target.Agent.Position - b.Agent.Position).LengthSquared < 35 * 35) b.ContactedEnemy = true;
                            b.Agent.SetWatchState(Agent.WatchState.Alarmed); b.Agent.SetTargetAgent(target.Agent);
                        }
                    }
                }
                peakEntities = Math.Max(peakEntities, Mission.AllAgents.Count);
                peakActive = Math.Max(peakActive, agents.Values.Count(x => !x.Released && x.Agent.IsActive()));
                if (Mission.AllAgents.Count > nativeBudget) throw new InvalidOperationException("Native budget violated.");
                if (simulation.UsedEntityBudget > activeBudget) throw new InvalidOperationException("Active budget violated.");
                if (reportElapsed >= 5) { reportElapsed = 0; WriteReport("running"); }
                if (SubModule.Probe && phase == 3 && elapsed - phaseSince >= 30)
                {
                    finished = true;
                    bool passed = spawned > activeBudget && returned > 0 && peakEntities <= nativeBudget && peakActive <= activeBudget && spawnsByFormation.All(n => n > 0)
                        && nativeKilled + nativeWounded > 0
                        && Enumerable.Range(0, 4).Sum(i => simulation.GetFormation(i).Killed + simulation.GetFormation(i).Wounded) > 0;
                    WriteReport(passed ? "passed" : "failed-acceptance");
                    TaleWorlds.Engine.Utilities.QuitGame();
                }
            }
            catch (Exception error) { Fail(error); }
        }
        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState state, KillingBlow blow)
        {
            Binding b;
            if (!agents.TryGetValue(affectedAgent, out b)) return;
            if (b.Removed || b.Released) return;
            b.Removed = true;
            if (state == AgentState.Killed) { if (b.Outcome != AgentOutcome.Killed) nativeKilled++; b.Outcome = AgentOutcome.Killed; }
            else if (state == AgentState.Unconscious) { if (b.Outcome != AgentOutcome.Wounded) nativeWounded++; b.Outcome = AgentOutcome.Wounded; }
            else if (!b.Demoting) b.Outcome = AgentOutcome.Wounded; // Unexpected disappearance must not create a fresh healthy troop.
        }
        public override void OnAgentDeleted(Agent affectedAgent)
        {
            Binding b;
            if (!agents.TryGetValue(affectedAgent, out b)) return;
            // Unexpected deletion without removal is not evidence of a healthy returning troop.
            if (!b.Removed && !b.Demoting) b.Outcome = AgentOutcome.Wounded;
            b.Deleted = true;
        }

        private void WriteReport(string status)
        {
            foreach (int id in Enumerable.Range(0, 4))
            {
                var f = simulation.GetFormation(id);
                if (f.Ready + f.Reserved + f.Active + f.Killed + f.Wounded != f.InitialPopulation)
                    throw new InvalidOperationException("Population accounting failed for formation " + id);
            }
            SubModule.Report(new { status, scope = "experimental-isolated-mission", logicalTroops = 100000,
                nativeLimit, nativeBudget, activeBudget, peakEntities, peakActive, currentEntities = Mission.AllAgents.Count, spawned, returned, killed, wounded, nativeKilled, nativeWounded, spawnsByFormation, phase,
                missionSeconds = elapsed, simulationTicks = simulation.TickCount, droppedTimeSeconds = simulation.DroppedTimeSeconds,
                pendingDemotions = agents.Values.Count(x => x.Demoting), activeBindings = agents.Values.Count(x => x.Agent.IsActive()),
                retiringNativeEntities = agents.Values.Count(x => x.Released && !x.Deleted),
                agentSample = agents.Values.Take(2).Select(x => new { x.Formation, x.Demoting, x.SpawnHealth, health = x.Agent.Health, limit = x.Agent.HealthLimit, state = x.Agent.State.ToString() }).ToArray(),
                formations = Enumerable.Range(0, 4).Select(simulation.GetFormation).ToArray(),
                limitations = new[] { "No native cap modification", "No campaign integration", "No distant troop rendering", "Abstract combat is uncalibrated", "Injured or previously engaged units are not demoted", "Demo uses infantry only" } });
        }
        private void Fail(Exception error)
        {
            failed = true; SubModule.Log(error.ToString());
            SubModule.Report(new { status = "failed", error = error.ToString(), missionSeconds = elapsed });
            InformationManager.DisplayMessage(new InformationMessage("Battle Scale stopped: " + error.Message));
            if (SubModule.Probe) TaleWorlds.Engine.Utilities.QuitGame();
        }
    }
}
