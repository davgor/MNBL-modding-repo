using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Diagnostics
{
    internal static class RuntimeNavigationProbe
    {
        internal static string Run(string store)
        {
            var samples = new List<object>();
            var prefabs = Path.Combine(new FileInfo(typeof(RuntimeNavigationProbe).Assembly.Location).Directory.Parent.Parent.FullName, "NavMeshPrefabs");
            var existing = Directory.Exists(prefabs) ? Directory.GetFiles(prefabs, "mnbl_probe_42_*.bin") : new string[0];
            foreach (int seed in new[] { 0, 42, 43 })
            {
                var imported = seed == 0 ? "ship_connection_plank_navmesh_1" : NavigationFixture.Write(seed, null);
                File.AppendAllText(Path.Combine(store, "navigation-probe-progress.log"), DateTime.UtcNow.ToString("O") + " importing " + imported + "\n");
                var scene = Scene.CreateNewScene(true, false, DecalAtlasGroup.All, "mnbl_nav_probe");
                try
                {
                    var frame = MatrixFrame.Identity;
                    scene.ImportNavigationMeshPrefabWithFrame(imported, frame);
                    int count = scene.GetNavMeshFaceCount();
                    if (count < 0 || count > 1000) throw new InvalidOperationException("Unexpected navigation prefab face count: " + count);
                    var faces = new List<object>();
                    var centers = new Vec3[count];
                    for (int i = 0; i < count; i++)
                    {
                        scene.GetNavMeshCenterPosition(i, ref centers[i]);
                        var record = scene.GetNavMeshPathFaceRecord(i);
                        faces.Add(new { index = i, group = record.FaceGroupIndex, island = record.FaceIslandIndex, x = centers[i].x, y = centers[i].y, z = centers[i].z });
                    }
                    var routes = new List<object>();
                    for (int j = 1; j < count; j++)
                    {
                        float distance = 0;
                        bool found = scene.GetPathDistanceBetweenAIFaces(0, j, new Vec2(centers[0].x, centers[0].y), new Vec2(centers[j].x, centers[j].y), 0, 1000, out distance, new int[0], 0, 0);
                        routes.Add(new { from = 0, to = j, found, distance });
                    }
                    samples.Add(new { seed, imported, count, faces, routes });
                }
                finally { scene.ClearAll(); }
            }
            if (existing.Length > 0)
            {
                var name = Path.GetFileNameWithoutExtension(existing[0]);
                var prior = Scene.CreateNewScene(true, false, DecalAtlasGroup.All, "mnbl_existing_nav_probe");
                try
                {
                    prior.ImportNavigationMeshPrefabWithFrame(name, MatrixFrame.Identity);
                    samples.Add(new { seed = 42, imported = name, count = prior.GetNavMeshFaceCount(), priorSessionFile = true });
                }
                finally { prior.ClearAll(); }
                foreach (int seed in new[] { 43, 42, 43 })
                {
                    NavigationFixture.Write(seed, name);
                    var scene = Scene.CreateNewScene(true, false, DecalAtlasGroup.All, "mnbl_rewritten_nav_probe");
                    try
                    {
                        scene.ImportNavigationMeshPrefabWithFrame(name, MatrixFrame.Identity);
                        int count = scene.GetNavMeshFaceCount();
                        var centers = new Vec3[count];
                        var heights = new List<float>();
                        for (int i = 0; i < count; i++)
                        {
                            scene.GetNavMeshCenterPosition(i, ref centers[i]);
                            heights.Add(centers[i].z);
                        }
                        var routes = new List<object>();
                        for (int j = 1; j < count; j++)
                        {
                            float distance;
                            bool found = scene.GetPathDistanceBetweenAIFaces(0, j, new Vec2(centers[0].x, centers[0].y), new Vec2(centers[j].x, centers[j].y), 0, 1000, out distance, new int[0], 0, 0);
                            routes.Add(new { from = 0, to = j, found, distance });
                        }
                        samples.Add(new { seed, imported = name, count, rewrittenExistingFile = true, heights, routes });
                    }
                    finally { scene.ClearAll(); }
                }
            }
            File.WriteAllText(Path.Combine(store, "navigation-probe.json"), new JavaScriptSerializer().Serialize(new
            {
                schema = "mnbl.navigation-probe", version = 1, prefab = "ship_connection_plank_navmesh_1", samples,
                scope = "isolated official prefab and runtime-authored two-quad imports/path distances; no land/sea transitions, wrapping or campaign movement",
                campaignBackendAccepted = false
            }));
            return "Navigation import measurements recorded; campaign navigation remains unproved.";
        }
    }
}
