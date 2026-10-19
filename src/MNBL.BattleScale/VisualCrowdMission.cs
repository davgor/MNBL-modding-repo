using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ObjectSystem;

namespace MNBL.BattleScale
{
    // An isolated render/allocation experiment. These figures cannot fight or receive damage.
    public sealed class VisualCrowdMission : MissionView
    {
        public static int TargetCount = 4096;
        public static double HoldSeconds = 30;
        public static bool DiagnosticCheckpoints = true;
        private const int CreationBatch = 32, AnimationBatch = 512;
        private sealed class Figure
        {
            internal AgentVisuals Visual;
            internal GameEntity Entity;
            internal Vec3 Position;
            internal string Tag;
            internal double LastAnimated;
            internal int Updates;
        }

        private readonly List<Figure> figures = new List<Figure>();
        private readonly List<double> frameIntervals = new List<double>();
        private readonly List<double> callbackCosts = new List<double>();
        private readonly Stopwatch wall = new Stopwatch();
        private BasicCharacterObject troop;
        private Monster monster;
        private Equipment equipment;
        private BodyProperties body;
        private MBActionSet actionSet;
        private Camera camera;
        private Vec3 center;
        private int target, columns, created, cleaned, animationCursor, baseRoots, afterCleanupRoots;
        private int lastValid, lastResourcesReady, lastFrustum, lastVisibilityTest, peakAgents, minimumUpdatesBeforeCleanup;
        private double populationReached = -1, previousFrame = -1, lastReport, totalCreationMs, totalAnimationMs;
        private double measuredHoldSeconds;
        private bool started, cleaning, done, failed;
        private bool firstScreenTickLogged;
        private double lastHeartbeat = -5;
        private string errorText;
        private object lastCameraEvidence;

        public override void OnMissionScreenInitialize()
        {
            base.OnMissionScreenInitialize();
            SubModule.Log("crowd OnMissionScreenInitialize; screen=" + (MissionScreen != null));
        }

        public override void OnMissionScreenActivate()
        {
            base.OnMissionScreenActivate();
            SubModule.Log("crowd OnMissionScreenActivate; screen=" + (MissionScreen != null));
        }

        public override void OnMissionTick(float dt)
        {
            if (!started || wall.Elapsed.TotalSeconds - lastHeartbeat < 5) return;
            lastHeartbeat = wall.Elapsed.TotalSeconds;
            SubModule.Log("crowd OnMissionTick heartbeat; seconds=" + lastHeartbeat + "; screen=" + (MissionScreen != null)
                + "; state=" + Mission.CurrentState + "; scene=" + Mission.SceneName + "; created=" + created
                + "; entities=" + figures.Count + "; cleaning=" + cleaning + "; done=" + done);
        }

        public override void AfterStart()
        {
            try
            {
                target = TargetCount;
                if (target < 1 || target > 8192 || HoldSeconds < 10 || HoldSeconds > 300)
                    throw new InvalidOperationException("Crowd probe configuration exceeds its bounded range.");
                if (Mission.AllAgents.Count != 0) throw new InvalidOperationException("Crowd probe requires a mission with zero agents.");
                troop = MBObjectManager.Instance.GetObject<BasicCharacterObject>("imperial_legionary");
                if (troop == null) throw new InvalidOperationException("Custom Battle infantry data was not loaded.");
                equipment = troop.FirstBattleEquipment;
                body = troop.GetBodyProperties(equipment, 12345);
                monster = TaleWorlds.Core.FaceGen.GetBaseMonsterFromRace(troop.Race);
                actionSet = MBGlobals.GetActionSetWithSuffix(monster, troop.IsFemale, "_facegen");
                if (!actionSet.IsValid) throw new InvalidOperationException("The verified display action set is unavailable.");
                var landmark = Mission.Scene.FindEntityWithTag("attacker_mid");
                if (landmark == null) throw new InvalidOperationException("Benchmark scene attacker_mid landmark is missing.");
                center = landmark.GetGlobalFrame().origin;
                center.z = Mission.Scene.GetGroundHeightAtPosition(center);
                columns = (int)Math.Ceiling(Math.Sqrt(target));
                baseRoots = Mission.Scene.RootEntityCount;
                Mission.SetMissionMode(MissionMode.Battle, true);
                wall.Start();
                started = true;
                SubModule.Log("visual crowd starting; target=" + target + "; no combat agents; display idle animation");
                WriteReport("creating", false);
            }
            catch (Exception error) { Fail(error); }
        }

        public override void OnMissionScreenTick(float dt)
        {
            if (!firstScreenTickLogged)
            {
                firstScreenTickLogged = true;
                SubModule.Log("crowd first OnMissionScreenTick; started=" + started + "; screen=" + (MissionScreen != null));
            }
            if (!started || done) return;
            double begin = wall.Elapsed.TotalSeconds;
            try
            {
                EnsureCamera();
                peakAgents = Math.Max(peakAgents, Mission.AllAgents.Count);
                if (Mission.AllAgents.Count != 0) throw new InvalidOperationException("Unexpected Agent allocation in isolated visual probe.");
                if (cleaning)
                {
                    CleanupBatch(CreationBatch);
                    if (figures.Count == 0)
                    {
                        afterCleanupRoots = Mission.Scene.RootEntityCount;
                        done = true;
                        bool accepted = !failed && created == target && lastValid == target && lastResourcesReady == target
                            && lastFrustum == target && peakAgents == 0 && minimumUpdatesBeforeCleanup > 0
                            && cleaned == created && afterCleanupRoots <= baseRoots && measuredHoldSeconds >= HoldSeconds;
                        WriteReport(accepted ? "passed" : "failed-acceptance", false);
                        ReleaseCamera();
                        if (SubModule.Probe) Utilities.QuitGame();
                    }
                    return;
                }

                if (created < target)
                {
                    var timer = Stopwatch.StartNew();
                    for (int batch = 0; batch < CreationBatch && created < target; batch++)
                    {
                        CreateFigure(created);
                        created++;
                        if (created % 256 == 0) WriteReport("creating-checkpoint", false);
                        if (timer.Elapsed.TotalMilliseconds >= 8) break;
                    }
                    totalCreationMs += timer.Elapsed.TotalMilliseconds;
                    if (created == target)
                    {
                        populationReached = wall.Elapsed.TotalSeconds;
                        SubModule.Log("visual crowd fully allocated; count=" + created + "; screenshot hold begins");
                        WriteReport("full-population-hold", true);
                    }
                }

                var animationTimer = Stopwatch.StartNew();
                Checkpoint("animation-batch-enter", animationCursor);
                int count = Math.Min(AnimationBatch, figures.Count);
                for (int n = 0; n < count; n++)
                {
                    if (animationCursor >= figures.Count) animationCursor = 0;
                    var figure = figures[animationCursor++];
                    double now = wall.Elapsed.TotalSeconds;
                    // Reduced-rate animation deliberately tests a cheaper visual tier.
                    float elapsed = (float)Math.Min(0.25, Math.Max(0, now - figure.LastAnimated));
                    if (animationCursor >= 1800 && animationCursor <= 2200) Checkpoint("animation-tick-enter", animationCursor - 1);
                    figure.Visual.Tick(null, elapsed, false, 0);
                    if (animationCursor >= 1800 && animationCursor <= 2200) Checkpoint("animation-tick-exit", animationCursor - 1);
                    figure.LastAnimated = now;
                    figure.Updates++;
                }
                totalAnimationMs += animationTimer.Elapsed.TotalMilliseconds;
                Checkpoint("animation-batch-exit", animationCursor);

                if (populationReached >= 0 && begin - populationReached >= 3 && previousFrame >= 0 && frameIntervals.Count < 20000)
                {
                    frameIntervals.Add((begin - previousFrame) * 1000);
                    callbackCosts.Add((wall.Elapsed.TotalSeconds - begin) * 1000);
                }
                previousFrame = begin;
                if (begin - lastReport >= 5)
                {
                    lastReport = begin;
                    WriteReport(populationReached < 0 ? "creating" : "full-population-hold", populationReached >= 0);
                }
                if (SubModule.Probe && populationReached >= 0 && begin - populationReached >= HoldSeconds)
                {
                    measuredHoldSeconds = begin - populationReached;
                    minimumUpdatesBeforeCleanup = figures.Min(x => x.Updates);
                    WriteReport("cleanup-starting", true);
                    cleaning = true;
                }
                if (populationReached < 0 && wall.Elapsed.TotalSeconds > 300)
                    throw new TimeoutException("Crowd creation exceeded the five-minute probe limit.");
            }
            catch (Exception error) { Fail(error); }
        }

        private void CreateFigure(int index)
        {
            Checkpoint("create-figure-enter", index);
            if (index == 0) SubModule.Log("crowd first CreateFigure begin");
            var figure = new Figure { Tag = "mnbl_visual_probe_" + index };
            // Keep every owned entity in the cleanup list even if visual construction fails.
            figures.Add(figure);
            Vec3 position = center;
            position.x += ((index % columns) - (columns - 1) * 0.5f) * 1.5f;
            position.y += ((index / columns) - (columns - 1) * 0.5f) * 1.5f;
            position.z = Mission.Scene.GetGroundHeightAtPosition(position) + 0.03f;
            if (index == 0) SubModule.Log("crowd first CreateFigure ground height complete");
            figure.Position = position;
            figure.Entity = GameEntity.CreateEmpty(Mission.Scene, false, true, true);
            Checkpoint("empty-entity-created", index);
            if (index == 0) SubModule.Log("crowd first CreateFigure empty entity complete");
            figure.Entity.Name = figure.Tag;
            figure.Entity.AddTag(figure.Tag);
            var frame = MatrixFrame.Identity;
            frame.origin = position;
            var idle = ActionIndexCache.act_inventory_idle_start;
            var data = new AgentVisualsData().Equipment(equipment).BodyProperties(body).Race(troop.Race)
                .Frame(frame).SkeletonType(troop.IsFemale ? SkeletonType.Female : SkeletonType.Male)
                .Entity(figure.Entity).ActionSet(actionSet).ActionCode(in idle).Scene(Mission.Scene)
                .Monster(monster).PrepareImmediately(false).UseMorphAnims(false)
                .ClothColor1(0xff3355bb).ClothColor2(0xffeeeeee);
            Checkpoint("native-visual-create-enter", index);
            figure.Visual = AgentVisuals.Create(data, figure.Tag, false, false, false);
            Checkpoint("native-visual-create-exit", index);
            if (index == 0) SubModule.Log("crowd first CreateFigure AgentVisuals.Create complete");
            if (figure.Visual == null || !figure.Visual.GetVisuals().IsValid())
                throw new InvalidOperationException("Native visual creation failed at " + index);
            Checkpoint("set-action-enter", index);
            figure.Visual.SetAction(in idle, (index % 97) / 97f, true);
            Checkpoint("set-action-exit", index);
            if (index == 0) SubModule.Log("crowd first CreateFigure SetAction complete");
            figure.Visual.SetVisible(true);
            var skeleton = figure.Visual.GetVisuals().GetSkeleton();
            if (skeleton == null) throw new InvalidOperationException("Visual has no skeleton at " + index);
            Checkpoint("prime-animation-enter", index);
            skeleton.TickAnimationsAndForceUpdate(0.01f, figure.Visual.GetVisuals().GetGlobalFrame(), true);
            Checkpoint("prime-animation-exit", index);
            if (index == 0) SubModule.Log("crowd first CreateFigure animation prime complete");
            skeleton.ManualInvalidate();
            figure.LastAnimated = wall.Elapsed.TotalSeconds;
            Checkpoint("create-figure-exit", index);
            if (index == 0) SubModule.Log("crowd first CreateFigure complete");
        }

        private void EnsureCamera()
        {
            if (camera != null || MissionScreen == null) return;
            SubModule.Log("crowd EnsureCamera CreateCamera begin");
            camera = Camera.CreateCamera();
            SubModule.Log("crowd EnsureCamera CreateCamera complete");
            float width = columns * 1.5f;
            Vec3 position = center + new Vec3(0, -width * 1.3f, width * 1.05f);
            camera.LookAt(position, center + new Vec3(0, 0, 1), new Vec3(0, 0, 1));
            SubModule.Log("crowd EnsureCamera LookAt complete");
            camera.SetFovVertical(0.9f, 16f / 9f, 0.1f, 2000f);
            SubModule.Log("crowd EnsureCamera SetFovVertical complete");
            MissionScreen.CustomCamera = camera;
            SubModule.Log("crowd EnsureCamera CustomCamera assigned");
            var frame = camera.Frame;
            Mission.SetCameraFrame(ref frame, 1);
            SubModule.Log("crowd EnsureCamera SetCameraFrame complete");
        }

        private void SnapshotVisibility()
        {
            lastValid = lastResourcesReady = lastFrustum = lastVisibilityTest = 0;
            Camera activeCamera = MissionScreen == null ? camera : MissionScreen.CombatCamera;
            foreach (var figure in figures)
            {
                if (figure.Visual == null || !figure.Visual.GetVisuals().IsValid()) continue;
                lastValid++;
                if (figure.Visual.GetVisuals().CheckResources(true)) lastResourcesReady++;
                if (activeCamera != null)
                {
                    if (activeCamera.EnclosesPoint(figure.Position + new Vec3(0, 0, 1))) lastFrustum++;
                    if (activeCamera.CheckEntityVisibility(figure.Entity)) lastVisibilityTest++;
                }
            }
            if (activeCamera != null)
            {
                var rendered = Mission.Scene.LastFinalRenderCameraFrame;
                lastCameraEvidence = new {
                    position = Vector(activeCamera.Position), direction = Vector(activeCamera.Direction),
                    verticalFov = activeCamera.GetFovVertical(), aspect = activeCamera.GetAspectRatio(),
                    near = activeCamera.Near, far = activeCamera.Far,
                    lastFinalRenderPosition = Vector(rendered.origin),
                    center = Vector(center), centerInsideFrustum = activeCamera.EnclosesPoint(center)
                };
            }
        }

        private void CleanupBatch(int maximum)
        {
            for (int n = 0; n < maximum && figures.Count > 0; n++)
            {
                var figure = figures[figures.Count - 1];
                if (figure.Visual != null)
                {
                    figure.Visual.Reset();
                    figure.Visual.GetVisuals().ManualInvalidate();
                    figure.Visual = null;
                }
                // Reset ownership is native. Reacquire from scene instead of dereferencing a possibly removed entity.
                var remaining = Mission.Scene.FindEntityWithTag(figure.Tag);
                if (remaining != null) remaining.Remove(35);
                if (figure.Entity != null) figure.Entity.ManualInvalidate();
                figure.Entity = null;
                figures.RemoveAt(figures.Count - 1);
                cleaned++;
            }
        }

        private void ReleaseCamera()
        {
            if (camera == null) return;
            if (MissionScreen != null && MissionScreen.CustomCamera == camera) MissionScreen.CustomCamera = null;
            camera.ReleaseCamera();
            camera = null;
        }

        public override void OnMissionScreenFinalize()
        {
            try
            {
                if (started && figures.Count > 0) CleanupBatch(figures.Count);
                ReleaseCamera();
            }
            catch (Exception error) { SubModule.Log("crowd finalization failed: " + error); }
            base.OnMissionScreenFinalize();
        }

        private static float[] Vector(Vec3 value) { return new[] { value.x, value.y, value.z }; }
        private void Checkpoint(string stage, int index)
        {
            if (!DiagnosticCheckpoints) return;
            File.WriteAllText(System.IO.Path.Combine(SubModule.Store, "last-stage.json"), new JavaScriptSerializer().Serialize(new {
                pid = Process.GetCurrentProcess().Id, utc = DateTime.UtcNow.ToString("O"), stage, index,
                created, figureCount = figures.Count, wallSeconds = wall.Elapsed.TotalSeconds
            }));
        }
        private static object Statistics(List<double> values)
        {
            var sorted = values.OrderBy(x => x).ToArray();
            if (sorted.Length == 0) return new { samples = 0 };
            return new { samples = sorted.Length, meanMs = sorted.Average(), p50Ms = sorted[(sorted.Length - 1) / 2],
                p95Ms = sorted[(int)((sorted.Length - 1) * 0.95)], maxMs = sorted[sorted.Length - 1] };
        }

        private void WriteReport(string status, bool inspectVisibility)
        {
            if (inspectVisibility) SnapshotVisibility();
            SubModule.Report(new {
                status, scope = "isolated-render-only-crowd", error = errorText,
                acceptanceScope = "allocation-resource-readiness-frustum-animation-cleanup; not independently verified rasterized soldier count",
                target, created, currentVisuals = figures.Count, cleaned, currentAgents = Mission.AllAgents.Count, peakAgents,
                validVisualsAtLastSnapshot = lastValid, resourcesReadyAtLastSnapshot = lastResourcesReady,
                soldierCentersInsideFrustumAtLastSnapshot = lastFrustum, entityVisibilityTestAtLastSnapshot = lastVisibilityTest,
                screenshotVerifiedVisibleCount = (int?)null, camera = lastCameraEvidence,
                wallSeconds = wall.Elapsed.TotalSeconds, populationReachedAtWallSeconds = populationReached,
                fullPopulationHoldSeconds = cleaning || done ? measuredHoldSeconds : populationReached < 0 ? 0 : wall.Elapsed.TotalSeconds - populationReached,
                creationBatch = CreationBatch, animationBatch = AnimationBatch, totalCreationMs, totalAnimationMs,
                diagnosticCheckpoints = DiagnosticCheckpoints,
                minAnimationUpdates = cleaning || done ? minimumUpdatesBeforeCleanup : figures.Count == 0 ? 0 : figures.Min(x => x.Updates),
                frameCallbackCadence = Statistics(frameIntervals), crowdCallbackCpuCost = Statistics(callbackCosts),
                workingSetBytes = Process.GetCurrentProcess().WorkingSet64, baseRoots,
                currentRootEntities = Mission.Scene.RootEntityCount, afterCleanupRoots,
                limitations = new[] {
                    "Created, resource-ready and camera visibility tests do not prove rasterized visible soldiers; external screenshot required.",
                    "Observed screen callback cadence is not GPU timing or a controlled FPS benchmark.",
                    "Display idle animation with reduced-rate round-robin updates; no marching, combat, AI, collision or damage.",
                    "Only infantry with one equipment/body archetype; no horses, campaign integration or Agent cap patch.",
                    "Frame timing includes normal scene work and periodic reporting; first three seconds after full population excluded."
                }
            });
        }

        private void Fail(Exception error)
        {
            if (failed) { SubModule.Log("crowd cleanup failed: " + error); done = true; if (SubModule.Probe) Utilities.QuitGame(); return; }
            failed = true;
            cleaning = true;
            errorText = error.ToString();
            SubModule.Log("crowd failed: " + errorText);
            WriteReport("failed", false);
            if (!started && SubModule.Probe) Utilities.QuitGame();
        }
    }
}
