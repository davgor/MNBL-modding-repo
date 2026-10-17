using System;
using System.Collections.Generic;
using System.Linq;
using NavalDLC;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using MNBL.ProceduralWorld.CampaignBootstrap;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-39: War Sails' naval map-scene wrapper for a generated world (docs/water-climate.md). War Sails' view builds its wrapper
    // (NavalDLC.View.NavalMapSceneWrapper) on our map scene each session: it sets up the water wake renderer and the drop-off ports and reads
    // PirateSpawnPoint entities, which a generated scene does not have. Installed right after it (our submodule's OnNewGameCreated and
    // OnAfterGameLoaded run before PiratesCampaignBehavior assigns its patrol zones), this decorator keeps that wrapper and its wake setup,
    // serves the world's generated pirate spawn points (NavalContentFactory.NavalPlan.PirateSpawnPoints), and forwards the wind (at the
    // canonical position on a periodic world; the synthesized field repeats every width, so the answer is the same) and Tick.
    internal sealed class GeneratedNavalMapSceneWrapper : INavalMapSceneWrapper
    {
        public readonly INavalMapSceneWrapper Inner;
        private readonly List<NavalContentFactory.SpawnPoint> spawnPoints;
        private Dictionary<string, List<(CampaignVec2, float)>> points;
        public static GeneratedNavalMapSceneWrapper Current { get; private set; }
        public static int SpawnPointQueries { get; private set; }
        public static string FrameError { get; private set; }

        private GeneratedNavalMapSceneWrapper(INavalMapSceneWrapper inner, IEnumerable<NavalContentFactory.SpawnPoint> spawnPoints)
        {
            Inner = inner;
            this.spawnPoints = spawnPoints.ToList();
        }

        // The served positions, built on first use (the map scene, and with it the periodic world, is loaded by then): a factory point is
        // logical and goes to its canonical physical position (PeriodicWorldCoordinates.ToPhysical); a fixture point is already physical.
        private Dictionary<string, List<(CampaignVec2, float)>> Points()
        {
            if (points != null) return points;
            points = new Dictionary<string, List<(CampaignVec2, float)>>(StringComparer.Ordinal);
            var world = PeriodicWorld.Current;
            foreach (var p in spawnPoints)
            {
                double x = p.X, y = p.Y;
                if (p.Logical)
                {
                    if (world == null) { FrameError = "logical spawn point " + p.Uid + " without a periodic world: not served"; continue; }
                    var physical = world.Coordinates.ToPhysical(p.X, p.Y); x = physical[0]; y = physical[1];
                }
                List<(CampaignVec2, float)> list;
                if (!points.TryGetValue(p.Clan, out list)) points[p.Clan] = list = new List<(CampaignVec2, float)>();
                list.Add((new CampaignVec2(new Vec2((float)x, (float)y), false), (float)p.Radius));
            }
            return points;
        }

        public IEnumerable<string> Clans { get { return Points().Keys.OrderBy(k => k, StringComparer.Ordinal); } }
        public int Count(string clan) { List<(CampaignVec2, float)> list; return Points().TryGetValue(clan, out list) ? list.Count : 0; }

        // Game end: War Sails drops its wrapper (NavalDLCManager.OnGameEnd); ours must not keep the ended map scene alive through Inner.
        internal static void Release() { Current = null; SpawnPointQueries = 0; FrameError = null; }

        public List<(CampaignVec2, float)> GetSpawnPoints(string clanId)
        {
            SpawnPointQueries++;
            List<(CampaignVec2, float)> list;
            if (clanId != null && Points().TryGetValue(clanId, out list)) return new List<(CampaignVec2, float)>(list);
            return Inner == null ? new List<(CampaignVec2, float)>() : Inner.GetSpawnPoints(clanId);
        }

        public Vec2 GetWindAtPosition(Vec2 position)
        {
            var world = PeriodicWorld.Current;
            if (world != null) position = new Vec2(world.Canonical(position.X), position.Y);
            return Inner == null ? Vec2.Zero : Inner.GetWindAtPosition(position);
        }

        public void Tick(float dt) { if (Inner != null) Inner.Tick(dt); }

        // Wraps the wrapper War Sails' view installed this session (once; a session that already has ours keeps it).
        internal static GeneratedNavalMapSceneWrapper Install(NavalContentFactory.NavalPlan plan, Action<string> trace)
        {
            var manager = NavalDLCManager.Instance;
            if (manager == null || plan == null) return null;
            var installed = manager.NavalMapSceneWrapper;
            var existing = installed as GeneratedNavalMapSceneWrapper;
            if (existing != null) return Current = existing;
            var wrapper = new GeneratedNavalMapSceneWrapper(installed, plan.PirateSpawnPoints);
            manager.NavalMapSceneWrapper = wrapper;
            Current = wrapper; SpawnPointQueries = 0; FrameError = null;
            if (trace != null) trace("naval_scene_wrapper_installed:inner=" + (installed == null ? "none" : installed.GetType().FullName) + ":pirate_points=" + plan.PirateSpawnPoints.Count);
            return wrapper;
        }
    }
}
