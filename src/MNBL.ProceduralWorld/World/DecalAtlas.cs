using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-247 (D, acc5): the campaign map's decal atlas. Every scene decal on the campaign map is drawn by a tiled decal loop: the terrain's
    // (Shaders/Sources/pbr_shading_functions.rsh calculate_add_decal_deferred) and the map water's (water_surface_worldmap.rs), and both
    // sample one texture only, the scene's decal atlas (decal_atlas_texture, t63), at the rect each of the decal's textures holds in it
    // (DecalParams d_atlas_uv_d / _n / _s; get_atlassed_decal_texture_tc puts SetVectorArgument's cell inside that rect). The map scene is
    // created with DecalAtlasGroup.Worldmap (SandBox.MapScene.Load), whose atlas is the prebaked 4096 x 4096 texture decal_atlas_worldmap of
    // Native's core.tpac, laid out by Native's ModuleData/decal_textures_worldmap.xml (name, atlas_pos "x, y, w, h", is_dynamic).
    // TaleWorlds.Native.dll reads those lists from the Native module alone: its loader copies the module name "native", reads that module's
    // ModuleData/project.mbproj and loops the five groups, setting each listed texture's per-group rect. A texture another module publishes
    // therefore has no rect in any group: a decal whose material samples one reads tc 0, sample_tiled_decal returns alpha 0, and nothing draws.
    // acc5 showed it: mnbl_water_surface, mnbl_magicwater and mnbl_magic_<school> resolve, 2,826 of their decals are drawn, none is visible,
    // while acc4's tinted vanilla stamp (mainmap_decal_undervillage, listed) painted the rivers; MMR-249's runtime texture drew nothing likewise.
    //   * Parse / Load: a group's list (Rects: texture name -> atlas_pos; Dynamic: the is_dynamic ones).
    //   * Resident(texture): the texture has a rect in the group (no list read: every texture counts as resident, nothing is judged).
    //   * Choose: what an engine target draws for a decal: its material when it resolves and its diffuse texture is resident (or the policy draws
    //     unresident materials, roads.json decalAtlas.unresident "draw"), else its fallback when that resolves and is resident, else nothing.
    internal enum DecalChoice { Material, Fallback, None }

    internal sealed class DecalAtlas
    {
        public string Group, Source;
        public readonly SortedDictionary<string, float[]> Rects = new SortedDictionary<string, float[]>(StringComparer.Ordinal);
        public readonly SortedSet<string> Dynamic = new SortedSet<string>(StringComparer.Ordinal);

        private static readonly Regex Entry = new Regex("<decal_texture\\s+name=\"([^\"]+)\"\\s+atlas_pos=\"([^\"]+)\"\\s+is_dynamic=\"([^\"]+)\"", RegexOptions.CultureInvariant);

        // A decal_textures_<group>.xml (type decal_texture): every entry's name and atlas_pos (four numbers in [0, 1]); a list with no entry,
        // or an entry whose rect is malformed, is refused.
        public static DecalAtlas Parse(string xml, string group, string source)
        {
            var atlas = new DecalAtlas { Group = group, Source = source };
            foreach (Match m in Entry.Matches(xml ?? ""))
            {
                var rect = m.Groups[2].Value.Split(',').Select(v => Single.Parse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (rect.Length != 4 || rect.Any(v => v < 0 || v > 1)) throw new InvalidDataException(source + ": " + m.Groups[1].Value + " has no atlas rect of four numbers in [0, 1].");
                atlas.Rects[m.Groups[1].Value] = rect;
                if (m.Groups[3].Value == "true") atlas.Dynamic.Add(m.Groups[1].Value);
            }
            if (atlas.Rects.Count == 0) throw new InvalidDataException(source + " lists no decal texture.");
            return atlas;
        }

        public static DecalAtlas Load(string path, string group) { return Parse(File.ReadAllText(path), group, path); }

        // The list's path in a module folder (Native's: the engine reads no other module's).
        public static string PathIn(string moduleFolder, string group) { return Path.Combine(moduleFolder, "ModuleData", "decal_textures_" + group + ".xml"); }

        public bool Resident(string texture) { return texture == null || Rects.ContainsKey(texture); }
        public static bool Resident(DecalAtlas atlas, string texture) { return atlas == null || atlas.Resident(texture); }

        public static DecalChoice Choose(bool resolves, bool resident, bool fallbackResolves, bool fallbackResident, bool drawUnresident)
        {
            if (resolves && (resident || drawUnresident)) return DecalChoice.Material;
            if (fallbackResolves && (fallbackResident || drawUnresident)) return DecalChoice.Fallback;
            return DecalChoice.None;
        }

        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["group"] = Group, ["source"] = Source, ["textures"] = Rects.Count, ["dynamic"] = Dynamic.Count };
        }
    }
}
