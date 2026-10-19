using System;
using System.Collections.Generic;

namespace MNBL.BattleScale.Core
{
    public struct BattlePoint
    {
        public readonly double X;
        public readonly double Y;
        public BattlePoint(double x, double y)
        {
            Guard.Finite(x, "x"); Guard.Finite(y, "y");
            if (Math.Abs(x) > 1e9 || Math.Abs(y) > 1e9) throw new ArgumentOutOfRangeException("position");
            X = x; Y = y;
        }
        public double DistanceSquared(BattlePoint other)
        { double x = X - other.X, y = Y - other.Y; return x * x + y * y; }
    }

    public sealed class BattleSettings
    {
        // Budget counts engine entities: a rider and horse consume two slots.
        public int EntityBudget { get; set; } = 512;
        public double PromotionDistance { get; set; } = 100;
        public double DemotionDistance { get; set; } = 150;
        public double FixedStepSeconds { get; set; } = 0.25;
        public int MaxCatchUpSteps { get; set; } = 8;
    }

    public sealed class FormationDefinition
    {
        public int Id { get; set; }
        public int Team { get; set; }
        public long Population { get; set; }
        public BattlePoint Position { get; set; }
        public bool Mounted { get; set; }
        public double AttackRange { get; set; } = 30;
        public double CasualtiesPerFighterSecond { get; set; } = 0.025;
        public int CombatFrontage { get; set; } = 128;
        public double WoundedFraction { get; set; } = 0.25;
    }

    public enum AgentOutcome { Returned, Killed, Wounded }

    public sealed class SpawnTicket
    {
        public long Id { get; private set; }
        public int FormationId { get; private set; }
        public int EntityCost { get; private set; }
        internal SpawnTicket(long id, int formationId, int cost)
        { Id = id; FormationId = formationId; EntityCost = cost; }
    }

    public sealed class FormationSnapshot
    {
        public int Id { get; internal set; }
        public int Team { get; internal set; }
        public BattlePoint Position { get; internal set; }
        public long InitialPopulation { get; internal set; }
        public long Ready { get; internal set; }
        public long Reserved { get; internal set; }
        public long Active { get; internal set; }
        public long Killed { get; internal set; }
        public long Wounded { get; internal set; }
        public bool IsNear { get; internal set; }
    }

    public sealed class SimulationPlan
    {
        public IReadOnlyList<SpawnTicket> SpawnTickets { get; internal set; }
        public IReadOnlyList<long> DemoteLeaseIds { get; internal set; }
    }

    // Single-threaded mission-owned state. Population is aggregate; only reserved or
    // materialized soldiers have records. Never infer casualties from engine despawn.
    public sealed class BattleSimulation
    {
        private sealed class Formation
        {
            internal int Id, Team, Cost, Frontage;
            internal long Initial, Ready, Reserved, Active, Killed, Wounded;
            internal BattlePoint Position;
            internal double RangeSquared, Lethality, WoundedFraction, DamageCarry, WoundCarry;
            internal int? Target;
            internal bool Near;
        }
        private sealed class Lease
        {
            internal SpawnTicket Ticket;
            internal Formation Formation;
            internal bool Active;
        }

        private readonly Dictionary<int, Formation> byId = new Dictionary<int, Formation>();
        private readonly List<Formation> formations = new List<Formation>();
        private readonly Dictionary<long, Lease> leases = new Dictionary<long, Lease>();
        private readonly int entityBudget, maxSteps;
        private readonly double promoteSquared, demoteSquared, step;
        private long nextLeaseId = 1;
        private int allocationCursor;
        private double accumulator;
        public int UsedEntityBudget { get; private set; }
        public long TickCount { get; private set; }
        public double DroppedTimeSeconds { get; private set; }

        public BattleSimulation(BattleSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            Guard.Finite(settings.PromotionDistance, "PromotionDistance");
            Guard.Finite(settings.DemotionDistance, "DemotionDistance");
            Guard.Finite(settings.FixedStepSeconds, "FixedStepSeconds");
            if (settings.EntityBudget < 0 || settings.EntityBudget > 100000) throw new ArgumentOutOfRangeException("EntityBudget");
            if (settings.PromotionDistance < 0 || settings.DemotionDistance <= settings.PromotionDistance || settings.DemotionDistance > 1e9)
                throw new ArgumentOutOfRangeException("DemotionDistance");
            if (settings.FixedStepSeconds < 0.001 || settings.FixedStepSeconds > 60) throw new ArgumentOutOfRangeException("FixedStepSeconds");
            if (settings.MaxCatchUpSteps < 1 || settings.MaxCatchUpSteps > 10000) throw new ArgumentOutOfRangeException("MaxCatchUpSteps");
            entityBudget = settings.EntityBudget; maxSteps = settings.MaxCatchUpSteps; step = settings.FixedStepSeconds;
            promoteSquared = settings.PromotionDistance * settings.PromotionDistance;
            demoteSquared = settings.DemotionDistance * settings.DemotionDistance;
        }

        public void AddFormation(FormationDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException("definition");
            Guard.Finite(definition.AttackRange, "AttackRange");
            Guard.Finite(definition.CasualtiesPerFighterSecond, "CasualtiesPerFighterSecond");
            Guard.Finite(definition.WoundedFraction, "WoundedFraction");
            if (definition.Population < 0) throw new ArgumentOutOfRangeException("Population");
            if (definition.CombatFrontage < 1 || definition.CombatFrontage > 100000) throw new ArgumentOutOfRangeException("CombatFrontage");
            if (definition.AttackRange < 0 || definition.AttackRange > 1e9) throw new ArgumentOutOfRangeException("AttackRange");
            if (definition.CasualtiesPerFighterSecond < 0 || definition.CasualtiesPerFighterSecond > 1000) throw new ArgumentOutOfRangeException("CasualtiesPerFighterSecond");
            if (definition.WoundedFraction < 0 || definition.WoundedFraction > 1) throw new ArgumentOutOfRangeException("WoundedFraction");
            if (byId.ContainsKey(definition.Id)) throw new ArgumentException("Duplicate formation id.", "definition");
            var formation = new Formation { Id = definition.Id, Team = definition.Team,
                Initial = definition.Population, Ready = definition.Population, Position = definition.Position,
                Cost = definition.Mounted ? 2 : 1, Frontage = definition.CombatFrontage,
                RangeSquared = definition.AttackRange * definition.AttackRange,
                Lethality = definition.CasualtiesPerFighterSecond, WoundedFraction = definition.WoundedFraction };
            byId.Add(formation.Id, formation); formations.Add(formation);
        }

        public FormationSnapshot GetFormation(int id)
        {
            Formation f = Find(id);
            return new FormationSnapshot { Id = f.Id, Team = f.Team, Position = f.Position,
                InitialPopulation = f.Initial, Ready = f.Ready, Reserved = f.Reserved, Active = f.Active,
                Killed = f.Killed, Wounded = f.Wounded, IsNear = f.Near };
        }

        public void SetPosition(int id, BattlePoint position) { Find(id).Position = position; }

        // Explicit engagement links avoid an all-pairs nearest-enemy search. The mission
        // integration chooses targets; null disengages. Friendly and self attacks fail.
        public void SetEngagement(int attackerId, int? targetId)
        {
            Formation attacker = Find(attackerId);
            if (targetId.HasValue && Find(targetId.Value).Team == attacker.Team)
                throw new ArgumentException("Target must be an opposing team.", "targetId");
            attacker.Target = targetId;
        }

        public SimulationPlan Plan(BattlePoint observer)
        {
            UpdateProximity(observer);
            var demotions = new List<long>();
            foreach (Lease lease in leases.Values)
                if (lease.Active && !lease.Formation.Near) demotions.Add(lease.Ticket.Id);
            var eligible = new List<Formation>();
            for (int i = 0; i < formations.Count; i++)
            {
                Formation f = formations[(allocationCursor + i) % formations.Count];
                if (f.Near && f.Ready > 0 && f.Cost <= entityBudget - UsedEntityBudget) eligible.Add(f);
            }
            var tickets = new List<SpawnTicket>();
            // Remove exhausted/ineligible cohorts by swapping, keeping total work
            // proportional to cohorts plus tickets rather than total army population.
            int cursor = 0;
            while (eligible.Count > 0)
            {
                if (cursor >= eligible.Count) cursor = 0;
                Formation f = eligible[cursor];
                if (f.Ready == 0 || f.Cost > entityBudget - UsedEntityBudget)
                { eligible[cursor] = eligible[eligible.Count - 1]; eligible.RemoveAt(eligible.Count - 1); continue; }
                if (nextLeaseId == long.MaxValue) throw new InvalidOperationException("Lease id space exhausted.");
                var ticket = new SpawnTicket(nextLeaseId++, f.Id, f.Cost);
                leases.Add(ticket.Id, new Lease { Ticket = ticket, Formation = f });
                f.Ready--; f.Reserved++; UsedEntityBudget += f.Cost; tickets.Add(ticket); cursor++;
            }
            if (formations.Count > 0) allocationCursor = (allocationCursor + 1) % formations.Count;
            return new SimulationPlan { SpawnTickets = tickets.AsReadOnly(), DemoteLeaseIds = demotions.AsReadOnly() };
        }

        public bool CommitSpawn(long ticketId)
        {
            Lease lease;
            if (!leases.TryGetValue(ticketId, out lease) || lease.Active) return false;
            lease.Formation.Reserved--; lease.Formation.Active++; lease.Active = true; return true;
        }

        public bool CancelSpawn(long ticketId)
        {
            Lease lease;
            if (!leases.TryGetValue(ticketId, out lease) || lease.Active) return false;
            lease.Formation.Reserved--; lease.Formation.Ready++;
            UsedEntityBudget -= lease.Ticket.EntityCost; leases.Remove(ticketId); return true;
        }

        public bool ReleaseAgent(long leaseId, AgentOutcome outcome)
        {
            if (outcome != AgentOutcome.Returned && outcome != AgentOutcome.Killed && outcome != AgentOutcome.Wounded)
                throw new ArgumentOutOfRangeException("outcome");
            Lease lease;
            if (!leases.TryGetValue(leaseId, out lease) || !lease.Active) return false;
            Formation f = lease.Formation;
            f.Active--;
            if (outcome == AgentOutcome.Returned) f.Ready++;
            else if (outcome == AgentOutcome.Killed) f.Killed++;
            else f.Wounded++;
            UsedEntityBudget -= lease.Ticket.EntityCost; leases.Remove(leaseId); return true;
        }

        // Catch-up work is bounded. A pause never creates an unbounded combat burst;
        // excess wall time is explicitly exposed via DroppedTimeSeconds.
        public int Advance(double elapsedSeconds, BattlePoint observer)
        {
            Guard.Finite(elapsedSeconds, "elapsedSeconds");
            if (elapsedSeconds < 0 || elapsedSeconds > 86400) throw new ArgumentOutOfRangeException("elapsedSeconds");
            UpdateProximity(observer);
            double limit = step * maxSteps;
            double incoming = accumulator + elapsedSeconds;
            if (incoming > limit) { DroppedTimeSeconds += incoming - limit; incoming = limit; }
            accumulator = incoming;
            int steps = 0;
            while (steps < maxSteps && accumulator + step * 1e-10 >= step)
            {
                CombatTick(); accumulator = Math.Max(0, accumulator - step); TickCount++; steps++;
            }
            return steps;
        }

        private void CombatTick()
        {
            // A deliberately simple deterministic model: bounded frontage, constant
            // casualty rate and persistent fractional damage. All attacks use the
            // start-of-tick population, then losses apply simultaneously. Only wholly
            // virtual, distant cohorts may participate; mixed cohorts are excluded.
            var damage = new Dictionary<int, double>();
            foreach (Formation f in formations)
            {
                if (!CanFight(f) || !f.Target.HasValue) continue;
                Formation target = byId[f.Target.Value];
                if (!CanFight(target) || f.Position.DistanceSquared(target.Position) > f.RangeSquared) continue;
                double amount = Math.Min(f.Ready, (long)f.Frontage) * f.Lethality * step;
                double existing; damage.TryGetValue(target.Id, out existing);
                damage[target.Id] = existing + amount;
            }
            // Formation insertion order, not dictionary traversal, governs updates.
            foreach (Formation f in formations)
            {
                double amount;
                if (!damage.TryGetValue(f.Id, out amount)) continue;
                double incoming = f.DamageCarry + amount;
                long casualties = incoming >= f.Ready ? f.Ready : (long)Math.Floor(incoming);
                f.DamageCarry = casualties == f.Ready ? 0 : incoming - casualties;
                double woundAmount = f.WoundCarry + casualties * f.WoundedFraction;
                long wounded = woundAmount >= casualties ? casualties : (long)Math.Floor(woundAmount);
                f.WoundCarry = woundAmount - wounded;
                f.Ready -= casualties; f.Wounded += wounded; f.Killed += casualties - wounded;
            }
        }

        private bool CanFight(Formation f)
        { return !f.Near && f.Ready > 0 && f.Active == 0 && f.Reserved == 0; }

        private void UpdateProximity(BattlePoint observer)
        {
            foreach (Formation f in formations)
            {
                double distance = f.Position.DistanceSquared(observer);
                if (f.Near) { if (distance > demoteSquared) f.Near = false; }
                else if (distance <= promoteSquared) f.Near = true;
            }
        }

        private Formation Find(int id)
        {
            Formation f;
            if (!byId.TryGetValue(id, out f)) throw new KeyNotFoundException("Unknown formation: " + id);
            return f;
        }
    }

    internal static class Guard
    {
        internal static void Finite(double value, string name)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(name); }
    }
}
