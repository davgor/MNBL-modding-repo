using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Diagnostics
{
    public static class RuntimeSurfaceProbe
    {
        // Main-thread diagnostic only. A mesh is not accepted as a campaign terrain backend.
        public static string Run(string store)
        {
            var samples = new List<object>();
            for (int seed = 42; seed <= 43; seed++)
            {
                var scene = Scene.CreateNewScene(true, false, DecalAtlasGroup.All, "mnbl_surface_probe");
                try
                {
                    var mesh = Mesh.CreateMesh(true);
                    Vec3 a = new Vec3(0, 0, 1), b = new Vec3(10, 0, 2), c = new Vec3(0, 10, seed - 35);
                    mesh.AddTriangle(a, b, c, new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), 0xffffffff, UIntPtr.Zero);
                    mesh.ComputeNormals();
                    mesh.UpdateBoundingBox();
                    var entity = GameEntity.CreateEmpty(scene);
                    entity.AddMesh(mesh);
                    var height = scene.GetTerrainHeight(new Vec2(2, 2));
                    samples.Add(new { seed, faceCount = mesh.GetFaceCount(), min = mesh.GetBoundingBoxMin().ToString(), max = mesh.GetBoundingBoxMax().ToString(), hasTerrain = scene.HasTerrainHeightmap, nativeTerrainHeight = height, expectedTriangleHeight = 1f + .2f + .2f * (seed - 36), navFaces = scene.GetNavMeshFaceCount() });
                }
                finally { scene.ClearAll(); }
            }
            File.WriteAllText(Path.Combine(store, "surface-probe.json"), new JavaScriptSerializer().Serialize(new {
                schema = "mnbl.surface-probe", version = 1, samples,
                scope = "isolated native scene and procedural triangle; no rendering, collision or campaign query replacement established",
                campaignBackendAccepted = false
            }));
            return "Surface measurements recorded. Campaign rendering, picking and navigation still require proof.";
        }
    }
}
