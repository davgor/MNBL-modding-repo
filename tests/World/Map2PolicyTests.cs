using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.World;

// MMR-248 (map epic 2): the managed strict readers of the frozen policy keys (World/Map2Policies.cs) accept the repository's policies, the
// shared biome table agrees with the playability face table, the mission-scene classes and the colour grades, and each reader refuses an
// unknown member and a value outside its rule. Usage: Map2PolicyTests REPO
internal static class Map2PolicyTests
{
    private static int checks, failures;
    private static string repo;

    private static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
    }
    private static void Refused(string what, Action body)
    {
        try { body(); Check(false, what + ": accepted"); }
        catch (InvalidDataException e) { Check(true, what + " -> " + e.Message); }
    }
    private static byte[] Bytes(string path) { return File.ReadAllBytes(Path.Combine(repo, path)); }
    private static IDictionary<string, object> Doc(string path) { return StrictJson.Parse(Bytes(path)); }
    private static byte[] Mutated(string path, string from, string to)
    {
        string text = new UTF8Encoding(false).GetString(Bytes(path));
        int at = text.IndexOf(from, StringComparison.Ordinal);
        if (at < 0) throw new Exception(path + ": text not found: " + from);
        return new UTF8Encoding(false).GetBytes(text.Substring(0, at) + to + text.Substring(at + from.Length));
    }

    private static int Main(string[] args)
    {
        repo = args[0];
        Check(WorldDataBundle.HostCapabilities.Contains(WorldDataBundle.FormatsV2Capability) && WorldDataBundle.FormatsV2Capability == "R11",
              "the host provides the one map epic 2 format capability R11 (cells v2, weights v2, campaign v2)");

        var table = BiomeTable.FromBytes(Bytes(BiomeTable.PolicyPath));
        var byCore = table.Rows.Values.ToDictionary(r => r.Core, StringComparer.Ordinal);
        Check(table.Layers.Count == 15 && byCore["land_ice"].Snow == "permanent" && byCore["cold_tundra"].TerrainType == "Steppe" && byCore["ocean"].Water && byCore["ocean"].TerrainType == null,
              "the shared biome table: 15 layers, land ice permanent white, cold tundra Steppe, water without a face");
        Check(table.MutatedType("fire", "grassland") == "Desert" && table.MutatedType("water", "land_ice") == "Snow" && table.MutatedType("blood", "forest") == "Desert" &&
              table.MutatedType("void", "desert") == "Forest" && !table.LandValid.Contains("Dune") && table.MutatedType("unknown", "desert") == null,
              "mutation remap: per school, core overrides, hidden schools take their look school's type, never Dune");

        var playability = Map2PolicyBlocks.FromPlayability(Doc("worldgen/policies/playability.json"));
        Check(table.Rows.Values.Where(r => !r.Water).All(r => playability.LandFaceGroups.ContainsKey(r.Core) && playability.LandFaceGroups[r.Core] == r.TerrainType) &&
              playability.LandFaceGroups.Count == table.Rows.Values.Count(r => !r.Water) && playability.PermanentWhiteMonths == table.PermanentWhiteMonths && playability.PermanentWhiteFace == "Snow",
              "playability cells.landFaceGroups is the shared table's face column");
        Check(playability.TownVillages == 3 && playability.CastleVillages == 2 && playability.Clearance == 4 && playability.MaxTownSnapCells >= 2 && playability.ForcedPorts && playability.RoadCellFlag,
              "playability: the 3/2 quota with the 4-unit clearance, town repairs and the road bit");

        var scenes = (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(Encoding.UTF8.GetString(Bytes("worldgen/policies/mission-scenes.json")).TrimStart('﻿'));
        var biomeClass = (IDictionary<string, object>)scenes["biomeClass"]; var faceClass = (IDictionary<string, object>)scenes["faceClass"];
        Check(table.Rows.Values.All(r => biomeClass.ContainsKey(r.Core) && (string)biomeClass[r.Core] == r.BattleClass),
              "mission-scenes.json biomeClass is the shared table's battle class column");
        Check(table.LandValid.All(t => faceClass.ContainsKey(t)), "every land-valid mutation type has a faceClass, the mutated cell's battle class (RT-26)");
        var grades = (IDictionary<string, object>)((IDictionary<string, object>)((IDictionary<string, object>)Doc("worldgen/policies/climate-flow.json"))["colorGrade"])["biomes"];
        Check(grades.All(g => { var id = Int32.Parse(g.Key.Split(' ')[0]); return table.Rows.ContainsKey(id) && table.Rows[id].ColourGrade == Convert.ToInt32(g.Value); }),
              "climate-flow.json colorGrade.biomes is the shared table's colour grade column");

        var roads = RoadsPolicy.FromBytes(Bytes(RoadsPolicy.PolicyPath));
        Check(roads.RoadFactor == 0.2 && roads.TerrainFactors["Snow"] == -0.15 && roads.TerrainFactors["Swamp"] == -0.2 && roads.CancelFordOnDecks && !roads.RoadAtSea &&
              roads.CultureFamilies.Count == 7 && roads.BridgePrefabs.Count == 4 && roads.WaterMaterial == "mnbl_water_surface" && roads.WaterTint == 0xFFFFFFFFu &&
              roads.WaterFallbackMaterial == "mainmap_decal_undervillage" && roads.WaterFallbackTint == 0xFF4D89D8u && roads.WaterFallbackAtlas.Length == 4 &&
              roads.WaterCell(roads.PoolColumn, 1).SequenceEqual(new[] { .5f, .5f, .5f, .5f }) && roads.FillScale <= 1,
              "roads.json: road +0.2, Snow -0.15, Swamp -0.2, ford cancelled on decks, a decal family per culture, the published water material with the tinted vanilla stamp as its fallback (MMR-247)");
        var magic = roads.Magic;
        Check(magic.Schools.Count == 8 && magic.Schools.All(s => table.Schools.ContainsKey(s)) && table.HiddenLooks.Values.All(magic.Schools.Contains) && magic.MaterialPrefix == "mnbl_magic_" &&
              table.Rows.Values.Where(r => !r.Water).All(r => magic.RowOf(r.Core) >= 0) && table.Rows.Values.Where(r => r.Water).All(r => magic.RowOf(r.Core) < 0) && magic.MaxDecals > 0
              && magic.TrunkColumn == 0 && magic.ForkColumn == 1 && magic.TipColumn == 2 && magic.BurstColumn == 3 && !magic.FlipU && !magic.FlipV && magic.MinWidth < magic.Width && magic.RevealHours == 48,
              "roads.json magic (MMR-260, veins MMR-288): a published material per visible school (every hidden school's look among them), an atlas row for every land biome and none for water; " +
              "atlas columns trunk 0, fork 1, tip 2, burst 3 (contract C1), flipU and flipV off, veins thinning from " + magic.Width + " to " + magic.MinWidth + ", a live cast revealed over 48 campaign hours");
        var magicWater = roads.MagicWater;
        Check(magicWater.Material == "mnbl_magicwater" && magicWater.Columns == 4 && magicWater.Rows == 8 && magic.Schools.Select(s => magicWater.SchoolRows[s]).SequenceEqual(Enumerable.Range(0, 8)) && magicWater.FallbackPrefix == magic.MaterialPrefix
              && magicWater.FallbackRow == 3 && magicWater.Mode == "surface" && magicWater.HalfDepth == 0.2 && magicWater.Opacity == 1.0 && magicWater.BedMaxDepth == 0.3 && magicWater.MaxDecals == 2500,
              "roads.json magicWater (MMR-290): one material mnbl_magicwater, atlas 4 kinds x 8 schools in the ground materials' school order, fallback the look's ground material (row 3 for a water core), surface mode, plane half depth 0.2, at most 2,500 stamps");
        // MMR-247 (D): the decal atlas rule and the vanilla fallbacks of the vein stamps.
        Check(roads.DecalAtlasGroup == "worldmap" && !roads.DrawUnresident && roads.WaterFallbackTexture == "mainmap_decal_undervillage_d" && roads.WaterMinHalfDepth == 0
              && magic.StrokeFallback.Material == "decal_khuzait_path_a" && magic.StrokeFallback.Turn && magic.StrokeFallback.Across > 1 && !magic.BurstFallback.Turn
              && magic.BurstFallback.Material == roads.WaterFallbackMaterial && magic.Schools.All(magic.FallbackColours.ContainsKey) && magic.FallbackColours.Count == 8,
              "roads.json decalAtlas (MMR-247 D): the world-map atlas, an unresident material gives way to its fallback; vein strokes fall back to the khuzait path stroke turned a quarter, bursts to the village stamp, a colour per visible school; water boxes keep W's depth (minHalfDepth 0)");
        Refused("an unknown decalAtlas.unresident", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"unresident\": \"fallback\"", "\"unresident\": \"skip\"")));
        Refused("a magic fallback colour missing a visible school", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"air\": [120, 170, 205]", "\"wind\": [120, 170, 205]")));
        Refused("a magic fallback stroke without its texture", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"texture\": \"decal_map_khuzait_path_d\", ", "")));

        // MMR-289: the magic features.
        var features = roads.Features;
        var looksPolicy = SettlementLooksPolicy.FromBytes(Bytes(SettlementLooksPolicy.PolicyPath));
        var pads = (IDictionary<string, object>)((IDictionary<string, object>)Doc("worldgen/policies/terrain-shaping.json")["pads"])["radius"];
        double padMargin = Convert.ToDouble(((IDictionary<string, object>)((IDictionary<string, object>)Doc(BiomeTable.PolicyPath)["flora"])["clearance"])["padMargin"]);
        Check(features.Schools.Count == 8 && magic.Schools.All(features.Schools.ContainsKey) && features.Schools.Values.All(s => s.Source.Parts.Count > 0 && s.Trunk.Count > 0 && s.Debris.Count > 0)
              && features.SettlementClearance >= pads.Values.Max(v => Convert.ToDouble(v)) + padMargin && features.SettlementClearance >= looksPolicy.RuinMinGap
              && features.RuinClearance > 0 && features.TrunkWidth >= magic.MinWidth && features.LiveTrunkWidth >= magic.MinWidth && features.MaxEntities >= features.MaxRoots && features.Meshes.ContainsKey("icicle"),
              "roads.json magicFeatures (MMR-289): a source, trunk and debris set per visible school over listed vanilla meshes; the settlement clearance " + features.SettlementClearance +
              " covers the terrain-shaping pads plus flora padMargin and the ruin markers' minGap; ruin clearance " + features.RuinClearance + "; trunk widths within the veins' range; budget " + features.MaxRoots + " roots / " + features.MaxEntities + " entities");
        Refused("an unknown member of roads.json magicFeatures", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"clearance\": {\"settlement\": 5.0,", "\"extra\": 1, \"clearance\": {\"settlement\": 5.0,")));
        Refused("a magic feature part naming an unlisted mesh", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "{\"meshes\": [\"burned_wood_a\"]", "{\"meshes\": [\"burned_wood_z\"]")));
        Refused("a translucent magic feature tint", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"tints\": [\"FF3A2A22\"]", "\"tints\": [\"803A2A22\"]")));
        Refused("a magic feature set for a school the overlay does not draw", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"air\": {\n        \"lean\": \"wind\",", "\"wind\": {\n        \"lean\": \"wind\",")));
        Refused("an unknown magic feature lean", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"lean\": \"inward\",", "\"lean\": \"sideways\",")));
        Refused("a magic feature clearance without ruin", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"settlement\": 5.0, \"ruin\": 2.0,", "\"settlement\": 5.0,")));
        Refused("an in-view feature cap above the world cap", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"windowRoots\": 200,", "\"windowRoots\": 2000,")));

        var population = PopulationPolicy.FromBytes(Bytes(PopulationPolicy.PolicyPath));
        Check(population.WandererTarget(45) == 28 && population.WandererTarget(66) == 40 && population.WandererTarget(57) == 35,
              "W is the smallest integer not below 0.6f x towns: A 28, B 40, War Sails 35");
        Check(PopulationPolicy.LordCultures.All(c => population.Mercenary.ContainsKey(c) && population.Outlaws[c].Count > 0) && population.TraitJoint.Sum(j => j.Value) == 443 &&
              population.WandererTemplates.Count == 78 && population.RotationDailyChance == 0.1, "population.json: a troop line per culture, 443 lords' traits, 78 wanderer templates");

        var looks = SettlementLooksPolicy.FromBytes(Bytes(SettlementLooksPolicy.PolicyPath));
        var economyDoc = Doc("worldgen/policies/economy.json");
        var villageTypes = ((IDictionary<string, object>)((IDictionary<string, object>)economyDoc["villageTypes"])["classes"]).Values.Cast<object[]>().SelectMany(a => a.Cast<string>()).Distinct().ToList();
        Check(looks.Scale == 0.5 && looks.WallsPerLevel == 2 && villageTypes.All(v => looks.Production.ContainsKey(v)) && looks.CultureCodes.Count == 7,
              "settlement-looks.json: 0.5 scale, 2 walls per level, a production piece for every installed village type");

        var economy = Map2PolicyBlocks.FromEconomy(economyDoc);
        Check(economy.TownVillages == 3 && economy.CastleVillages == 2 && economy.CoastalFishingShare > 0 && economy.HorseRanchShare["nord"] == 0 && economy.TownWalls["small"].Count == 3,
              "economy.json: 3/2 villages, the coastal fishing share, per-culture floors and the initial wall levels per tercile");
        var hideouts = Map2PolicyBlocks.FromStartup(Doc("worldgen/policies/startup.json"));
        Check(hideouts.Weights["desert"]["desert_bandits"] == 1 && hideouts.CoastCulture == "sea_raiders" && table.Rows.Values.Where(r => !r.Water).All(r => hideouts.Weights.ContainsKey(r.Core)),
              "startup.json hideouts.biome: bandit weights for every land biome, sea raiders on coasts");

        Refused("terrain-materials.json version 1", () => BiomeTable.FromBytes(Mutated(BiomeTable.PolicyPath, "\"version\": 2,", "\"version\": 1,")));
        Refused("Dune in the mutation set", () => BiomeTable.FromBytes(Mutated(BiomeTable.PolicyPath, "\"landValid\": [\"Plain\",", "\"landValid\": [\"Dune\", \"Plain\",")));
        Refused("an unknown member of the shared table", () => BiomeTable.FromBytes(Mutated(BiomeTable.PolicyPath, "\"mutation\": {", "\"mutation\": {\"extra\": 1,")));
        Refused("a layer mix that does not sum to 1", () => BiomeTable.FromBytes(Mutated(BiomeTable.PolicyPath, "{\"sand\": 0.6, \"arid_soil\": 0.4}", "{\"sand\": 0.6, \"arid_soil\": 0.3}")));
        Refused("a biome rock weight (MMR-255 removed the two-layer compile)", () => BiomeTable.FromBytes(Mutated(BiomeTable.PolicyPath, "\"core\": \"grassland\",", "\"core\": \"grassland\", \"rock\": 0,")));
        Refused("a layer tint mode 3", () => BiomeTable.FromBytes(Mutated(BiomeTable.PolicyPath, "\"tint\": {\"mode\": 0", "\"tint\": {\"mode\": 3")));
        Refused("an unknown member of roads.json", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"road\": 0.2,", "\"road\": 0.2, \"extra\": 1,")));
        Refused("a speed chain without the naval model", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"chain\": [\"Road\", \"Naval\", \"Default\"]", "\"chain\": [\"Road\", \"Default\"]")));
        // MMR-285, MMR-247: the water decals.
        Refused("a water tint outside 0..255", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"tint\": [77, 137, 216]", "\"tint\": [77, 137, 256]")));
        Refused("a water fallback that is the water material itself", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"fallback\": {\"material\": \"mainmap_decal_undervillage\"", "\"fallback\": {\"material\": \"mnbl_water_surface\"")));
        Refused("a water pool column that is the river column", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"poolColumn\": 1", "\"poolColumn\": 0")));
        Refused("a fill larger than its cell", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"fillScale\": 1.0", "\"fillScale\": 1.25")));
        Refused("a water opaque core above 1", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"opaqueCore\": 0.5", "\"opaqueCore\": 1.5")));
        Refused("an unknown member of roads.json water", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"riverWidth\": 6.0,", "\"riverWidth\": 6.0, \"extra\": 1,")));
        // MMR-260: the magic ground overlay.
        Refused("an unknown member of roads.json magic", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"revealHours\": 48.0,", "\"revealHours\": 48.0, \"extra\": 1,")));
        // MMR-288: the veins.
        Refused("a pre-vein magic key (halfExtent)", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"revealHours\": 48.0,", "\"revealHours\": 48.0, \"halfExtent\": 5.0,")));
        Refused("a vein alpha floor of 0", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"minAlpha\": 0.35", "\"minAlpha\": 0.0")));
        Refused("veins without minStamps", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"minStamps\": 4, ", "")));
        Refused("magic atlas columns naming one cell twice", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"tip\": 2,", "\"tip\": 1,")));
        Refused("a vein minimum width above the trunk width", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"minWidth\": 2.0,", "\"minWidth\": 7.5,")));
        Refused("a vein fork exponent below 1", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"forkExponent\": 2.0,", "\"forkExponent\": 0.5,")));
        Refused("magic atlas without flipU", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, ",\n      \"flipU\": false", "")));
        Refused("a magic atlas naming a core twice", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"cores\": [\"exposed_rock\"]", "\"cores\": [\"exposed_rock\", \"desert\"]")));
        Refused("a magic node without a default particle", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"default\": \"psys_mapicon_lightclouds\"", "\"other\": \"psys_mapicon_lightclouds\"")));
        // MMR-290: magic water.
        Refused("an unknown member of roads.json magicWater", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"halfDepth\": 0.2,", "\"halfDepth\": 0.2, \"extra\": 1,")));
        Refused("roads.json without magicWater", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"magicWater\": {", "\"magicWaterOld\": {")));
        Refused("a magic water material named as a school's ground material", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"material\": \"mnbl_magicwater\"", "\"material\": \"mnbl_magic_water\"")));
        Refused("a magic water mode other than surface, bed or shore", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"mode\": \"surface\"", "\"mode\": \"planes\"")));
        Refused("two schools on one magic water row", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"air\": 7}", "\"air\": 6}")));
        Refused("a magic water fallback row past the ground atlas", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"row\": 3}", "\"row\": 4}")));
        Refused("a magic water opacity above 1", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"opacity\": 1.0,", "\"opacity\": 1.5,")));
        // MMR-291: overlay meshes.
        var overlay = roads.Overlay;
        Check(overlay.Enabled && overlay.DrawsVeins && overlay.DrawsMagicWater && overlay.DrawsRivers && overlay.Chunk == 64 && overlay.MaxSegment == 1.0 && overlay.MinSegment == 0.25 && overlay.MaxError == 0.05
              && overlay.VeinMaterial == "mnbl_overlay_vein_{look}" && overlay.VeinTemplate == "torch_flame_base" && overlay.VeinTextures == magic.MaterialPrefix + "{look}" && overlay.VeinLift == 0.08
              && overlay.WaterMaterial == "mnbl_overlay_magicwater" && overlay.WaterTemplate == "torch_flame_base" && overlay.WaterTextures == magicWater.Material
              && overlay.RiverMaterials.SequenceEqual(new[] { "naval_worldmap_water", "worldmap_water" }) && overlay.RiverWidth == 4.0 && overlay.RiverDepth == 0.4 && overlay.RiverFills
              && magic.Schools.All(overlay.VeinGlow.ContainsKey) && !overlay.FlipV && overlay.VeinPass == "blend" && overlay.WaterPass == "depth" && overlay.AlphaTest == 0.3
              && overlay.Pass("veins") == "blend" && overlay.Pass("magicWater") == "depth" && overlay.Pass("rivers") == "blend",
              "roads.json overlayMeshes (MMR-291): veins, magic water and rivers as meshes in 64-unit chunks, draped every 1 to 0.25 units within 0.05; veins and magic water a published material by name, else a copy of vanilla torch_flame_base with " +
              "the published textures (mnbl_magic_{look}, mnbl_magicwater), rivers naval_worldmap_water then worldmap_water, 4 units wide and 0.4 deep, fills meshed; a glow per visible school; " +
              "MMR-291-W: magic water in the depth pass (alpha test 0.3), the veins blended");
        Refused("an unknown member of roads.json overlayMeshes", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"chunk\": 64,", "\"chunk\": 64, \"extra\": 1,")));
        Refused("roads.json without overlayMeshes", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"overlayMeshes\": {", "\"overlayMeshesOld\": {")));
        Refused("an overlay mode other than mesh or decal", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"rivers\": {\"mode\": \"mesh\"", "\"rivers\": {\"mode\": \"planes\"")));
        Refused("an overlay vein material named as a ground decal material", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"material\": \"mnbl_overlay_vein_{look}\"", "\"material\": \"mnbl_magic_{look}\"")));
        Refused("an overlay vein glow missing a visible school", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"earth\": 0.2, \"air\": 0.3}", "\"earth\": 0.2}")));
        Refused("an overlay vein lift within the draping tolerance", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"lift\": 0.08,", "\"lift\": 0.04,")));
        Refused("an overlay segment longer than a quarter chunk", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"segment\": {\"max\": 1.0,", "\"segment\": {\"max\": 20.0,")));
        Refused("overlay rivers without a material", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"materials\": [\"naval_worldmap_water\", \"worldmap_water\"]", "\"materials\": []")));
        Refused("an overlay pass that is not blend, depth, forward or afterPostfx (MMR-291-W)", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"pass\": \"depth\"", "\"pass\": \"above\"")));
        Refused("an overlay alpha test outside (0, 1)", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"alphaTest\": 0.3,", "\"alphaTest\": 76,")));
        Refused("overlay magic water without a pass", () => RoadsPolicy.FromBytes(Mutated(RoadsPolicy.PolicyPath, "\"lift\": 0.04, \"pass\": \"depth\",", "\"lift\": 0.04,")));
        Refused("a 120-fief population base (RT-03)", () => PopulationPolicy.FromBytes(Mutated(PopulationPolicy.PolicyPath, "\"fiefs\": 133,", "\"fiefs\": 120,")));
        Refused("a company line on a lord party template", () => PopulationPolicy.FromBytes(Mutated(PopulationPolicy.PolicyPath,
            "\"partyTemplate\": \"kingdom_hero_party_mercenary_aserai_template\"", "\"partyTemplate\": \"kingdom_hero_party_aserai_template\"")));
        Refused("three walls per level", () => SettlementLooksPolicy.FromBytes(Mutated(SettlementLooksPolicy.PolicyPath, "\"wallsPerLevel\": 2,", "\"wallsPerLevel\": 3,")));
        Refused("an unknown member of playability villages", () => Map2PolicyBlocks.FromPlayability(StrictJson.Parse(Mutated("worldgen/policies/playability.json", "\"townVillages\": 3,", "\"townVillages\": 3, \"extra\": 1,"))));
        Refused("a hideout weight for a culture that is not a bandit culture", () => Map2PolicyBlocks.FromStartup(StrictJson.Parse(Mutated("worldgen/policies/startup.json", "\"desert_bandits\": 1.0", "\"looters\": 1.0"))));

        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }
}
