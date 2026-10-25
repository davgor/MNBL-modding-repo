using System;
using TaleWorlds.MountAndBlade;

namespace MNBL.CombatProbe
{
    // Mission-thread only. Call Tick after combat trials and Restore on mission exit.
    public sealed class AnimationProbe
    {
        public const string ActionName = "act_mnbl_probe_sweep";
        public const string SetName = "as_mnbl_probe";
        private readonly Agent agent;
        private AnimationSystemData original;
        private readonly bool suppliedOriginal;
        private bool changed, started;
        private float elapsed, nextSample;
        private int matchingSamples;
        private int captures;
        public bool Running { get; private set; }
        public bool Complete { get; private set; }

        public AnimationProbe(Agent agent) { this.agent = agent; }
        public AnimationProbe(Agent agent, AnimationSystemData original) : this(agent)
        { this.original = original; suppliedOriginal = true; }

        public bool Start()
        {
            if (started) return false;
            started = true;
            try
            {
                if (agent == null || !agent.IsActive() || agent.Character == null || !agent.IsHuman)
                    throw new InvalidOperationException("Active human agent with character required");
                if (!suppliedOriginal)
                {
                    if (CombatProbe.Active == null || CombatProbe.Active.Attacker != agent || !CombatProbe.Complete)
                        throw new InvalidOperationException("Default configuration reconstruction restricted to completed isolated combat fixture");
                    original = agent.Monster.FillAnimationSystemData(agent.ActionSet, agent.Character.GetStepSize(), false);
                }
                var action = ActionIndexCache.Create(ActionName);
                var set = MBActionSet.GetActionSet(SetName);
                bool clip = set.IsValid && action.Index >= 0 && MBActionSet.CheckActionAnimationClipExists(set, action);
                ProbeLog.Event("custom_animation_availability", new { action = action.Index, set = set.GetName(), clip,
                    animation = clip ? set.GetAnimationName(action) : null, proof = "availability_only" });
                if (!clip) { Complete = true; return false; }
                Sample("before");
                // Value copy preserves the complete known original monster/bone configuration.
                var custom = original;
                custom.ActionSet = set;
                changed = true;
                agent.SetActionSet(ref custom);
                bool accepted = agent.SetActionChannel(0, action, true);
                ProbeLog.Event("custom_animation_start", new { accepted, action = action.Index,
                    originalSet = original.ActionSet.GetName(), currentSet = agent.ActionSet.GetName(),
                    note = "Acceptance is not visual playback proof. Imported original clip must be witnessed." });
                if (!accepted) { Restore(); Complete = true; return false; }
                Running = true;
                Sample("start");
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        public void Tick(float dt)
        {
            if (!Running) return;
            try
            {
                if (!agent.IsActive()) throw new InvalidOperationException("Agent inactive during animation probe");
                elapsed += dt;
                if(captures<3 && elapsed>=0.35f+captures*0.6f) {ProbeLog.Capture("custom-animation-"+captures);captures++;}
                if (elapsed >= nextSample) { Sample("tick"); nextSample = elapsed + 0.1f; }
                if (elapsed >= 2.2f)
                {
                    ProbeLog.Event("custom_animation_observation_end", new { elapsed, matchingSamples,
                        result = matchingSamples > 0 ? "custom_action_observed_requires_visual_review" : "custom_action_never_observed" });
                    Restore(); Complete = true;
                }
            }
            catch (Exception e) { Fail(e); }
        }

        private void Sample(string phase)
        {
            var action = agent.GetCurrentAction(0);
            if (action.Index == ActionIndexCache.Create(ActionName).Index && action.Index >= 0) matchingSamples++;
            sbyte hand = agent.Monster.MainHandBoneIndex;
            sbyte spine = agent.Monster.SpineUpperBoneIndex;
            var handPose = hand >= 0 ? agent.GetBoneEntitialFrame(hand, true).origin : default(TaleWorlds.Library.Vec3);
            var spinePose = spine >= 0 ? agent.GetBoneEntitialFrame(spine, true).origin : default(TaleWorlds.Library.Vec3);
            ProbeLog.Event("custom_animation_sample", new { phase, elapsed, action = action.Index,
                progress = agent.GetCurrentActionProgress(0), set = agent.ActionSet.GetName(), handBone = hand, spineBone = spine,
                hand = new[] {handPose.x, handPose.y, handPose.z}, spine = new[] {spinePose.x, spinePose.y, spinePose.z} });
        }

        public void Restore()
        {
            Running = false;
            if (!changed) return;
            if (agent == null || !agent.IsActive())
            {
                ProbeLog.Event("custom_animation_restore_unavailable", new { reason = "Agent inactive; native state cannot be restored" });
                changed = false; return;
            }
            agent.SetActionSet(ref original);
            changed = false;
            ProbeLog.Event("custom_animation_restored", new { expected = original.ActionSet.GetName(), actual = agent.ActionSet.GetName(),
                restored = agent.ActionSet.Equals(original.ActionSet), note = "Full original animation configuration restored; prior transient action not resumed" });
            Sample("restored");
        }

        private void Fail(Exception e)
        {
            ProbeLog.Event("custom_animation_error", new { error = e.ToString() });
            try { Restore(); } catch (Exception restore) { ProbeLog.Event("custom_animation_restore_error", new { error = restore.ToString() }); }
            Running = false; Complete = true;
        }
    }
}
