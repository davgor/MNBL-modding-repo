using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Engine;
using TaleWorlds.ModuleManager;

namespace MNBL.ProceduralWorld.World
{
    // MMR-247 (D): the engine side of World/DecalAtlas.cs. The campaign map's atlas list is read once from the Native module (ModuleData/
    // decal_textures_<roads.json decalAtlas.group>.xml, the list the engine itself reads), and each material an engine target draws is judged by
    // its diffuse texture (Material.GetTexture(DiffuseMap).Name): MapDressingRuntime's target (roads, water, MMR-260/288's magic ground) and
    // MagicWaterRuntime's choose through a DecalAtlasChooser. Never throws: a list that cannot be read judges nothing (Error says why).
    internal static class DecalAtlasRuntime
    {
        private static DecalAtlas atlas; private static bool read, drawUnresident; private static double waterMinHalfDepth;
        public static string Error { get; private set; }
        public static DecalAtlas Atlas { get { Read(); return atlas; } }
        public static bool DrawUnresident { get { Read(); return drawUnresident; } }
        // roads.json water.minHalfDepth: the least half depth a water stamp's box draws with (MapDressingRuntime's target).
        public static double WaterMinHalfDepth { get { Read(); return waterMinHalfDepth; } }
        private static void Read()
        {
            if (read) return;
            read = true;
            try
            {
                var policy = RoadsPolicy.Installed();
                drawUnresident = policy.DrawUnresident; waterMinHalfDepth = policy.WaterMinHalfDepth;
                atlas = DecalAtlas.Load(DecalAtlas.PathIn(ModuleHelper.GetModuleFullPath("Native"), policy.DecalAtlasGroup), policy.DecalAtlasGroup);
            }
            catch (Exception e) { atlas = null; Error = e.GetType().Name + ": " + e.Message; }
        }
        public static string Diffuse(Material material)
        {
            if (material == null) return null;
            try { var t = material.GetTexture(Material.MBTextureType.DiffuseMap); return t == null ? null : t.Name; }
            catch (Exception) { return null; }
        }
        public static Dictionary<string, object> Record()
        {
            var a = Atlas;
            var r = a == null ? new Dictionary<string, object> { ["status"] = "unread: nothing judged", ["error"] = Error } : a.Record();
            r["unresident"] = DrawUnresident ? "draw" : "fallback"; r["waterMinHalfDepth"] = WaterMinHalfDepth;
            return r;
        }
    }

    // One engine target's materials (each resolved once, with its diffuse texture and whether the atlas holds it) and what it chose per decal.
    internal sealed class DecalAtlasChooser
    {
        public sealed class Entry { public Material Material; public string Texture; public bool Resident; }
        private readonly Dictionary<string, Entry> materials = new Dictionary<string, Entry>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Unresident = new SortedDictionary<string, int>(StringComparer.Ordinal);   // decals whose material has no rect
        public int Fallbacks, NotDrawn;

        public Entry Of(string name)
        {
            Entry e;
            if (name == null) return null;
            if (!materials.TryGetValue(name, out e))
            {
                var m = Material.GetFromResource(name);
                var texture = DecalAtlasRuntime.Diffuse(m);
                materials[name] = e = new Entry { Material = m, Texture = texture, Resident = DecalAtlas.Resident(DecalAtlasRuntime.Atlas, texture) };
            }
            return e;
        }

        // The material a decal draws with (fallback: its decal's Fallback), or null when nothing draws.
        public Material Choose(RoadDecal d, out bool fallback)
        {
            var m = Of(d.Material); var f = Of(d.Fallback);
            var choice = DecalAtlas.Choose(m.Material != null, m.Resident, f != null && f.Material != null, f != null && f.Resident, DecalAtlasRuntime.DrawUnresident);
            if (m.Material != null && !m.Resident) { int n; Unresident.TryGetValue(d.Material, out n); Unresident[d.Material] = n + 1; }
            fallback = choice == DecalChoice.Fallback;
            if (fallback) Fallbacks++;
            else if (choice == DecalChoice.None) NotDrawn++;
            return choice == DecalChoice.Material ? m.Material : fallback ? f.Material : null;
        }

        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["atlas"] = DecalAtlasRuntime.Record(), ["unresident"] = new SortedDictionary<string, int>(Unresident, StringComparer.Ordinal),
                ["textures"] = materials.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object> {
                    ["resolves"] = kv.Value.Material != null, ["diffuse"] = kv.Value.Texture, ["resident"] = kv.Value.Resident }),
                ["fallbacks"] = Fallbacks, ["notDrawn"] = NotDrawn };
        }
    }
}
