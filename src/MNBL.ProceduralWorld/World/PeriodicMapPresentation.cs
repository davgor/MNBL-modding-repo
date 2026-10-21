using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.World
{
    // MMR-66: what the campaign map shows of a periodic world (docs/periodic-presentation.md). Gameplay stays canonical (MMR-63..65); this is
    // presentation only. SandBox.View, SandBox.ViewModelCollection and NavalDLC.View are not referenced: their types are reached by name and
    // patched with the periodic set (PeriodicPatches.ApplyAll; all or nothing, a missing required target refuses the set; War Sails views
    // are optional).
    //  * The camera target stays canonical (PeriodicMapCameraView).
    //  * Every visual that follows a campaign object is drawn at the object's image nearest the camera target (Display): parties and ships
    //    (their frame is set from GetVisualPosition every visible tick), anchors and tracks (framed on their own changes, and again whenever
    //    the camera wraps or has moved far: RefreshImages), storms. Selection and hover decals, blockade arcs, battle facings and quest arrows
    //    use the same images and periodic differences (Show). A visual's position is presentation only.
    //  * Settlement entities are static scene entities that gameplay reads (siege camp frames), so they are never moved. Each settlement gets
    //    one display copy at its strip image (the strips are half a width wide, so every canonical x has exactly one): a clone of its entity
    //    without tags (its script components stay inert), renamed, whose entities are registered to the settlement's own visual, so a click, hover or tooltip on the
    //    copy is the settlement itself. The copy mirrors the visibility of the settlement's entities whenever the settlement visual changes
    //    them (level, walls, siege preparations, discovery) and goes when the settlement visual's party is removed. A fixture's static copy
    //    of the same settlement is replaced by it.
    //  * Party and settlement nameplates are placed, culled and sorted at the same images, so each object has one label, beside the camera.
    internal static class PeriodicMapPresentation
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal sealed class DisplayCopy
        {
            public string Settlement, Entity, ReplacedFixtureEntity;
            public float CanonicalX, ImageX, Y;
            public GameEntity Copy;
            public object Visual;
            public List<GameEntity> Originals = new List<GameEntity>(), Mirrors = new List<GameEntity>();   // pairwise, in hierarchy order
            public List<UIntPtr> Registered = new List<UIntPtr>();
            public int Syncs, AddedChildren;
        }

        public static readonly List<DisplayCopy> Copies = new List<DisplayCopy>();
        public static readonly List<string> CopyErrors = new List<string>();
        private static PropertyInfo strategicEntity, mapEntity, visualsOfEntities, visualManagerProperty;
        private static AccessTools.FieldRef<object, bool> tracksDirty;
        private static AccessTools.FieldRef<object, CampaignVec2> anchorCache;
        private static Type tracksType, anchorsType;
        private static int nameplateCalls, refreshes;
        private static MethodInfo visibleOrFading;

        internal static void ApplyAll(Harmony harmony, List<Dictionary<string, object>> applied)
        {
            Release();
            PeriodicMapCameraView.Apply(harmony, applied);
            // Visual positions (each override of MapEntityVisual.GetVisualPosition a drawn frame comes from) and track frames.
            Postfix(harmony, applied, "SandBox.View.Map.Visuals.MobilePartyVisual", "GetVisualPosition", nameof(VisualPositionPostfix), true, "party visual -> image nearest the camera");
            Postfix(harmony, applied, "SandBox.View.Map.Visuals.TrackVisual", "GetVisualPosition", nameof(VisualPositionPostfix), true, "track visual -> image nearest the camera");
            Postfix(harmony, applied, "NavalDLC.View.Map.Visuals.NavalMobilePartyVisual", "GetVisualPosition", nameof(VisualPositionPostfix), false, "ship visual -> image nearest the camera");
            Postfix(harmony, applied, "NavalDLC.View.Map.Visuals.AnchorVisual", "GetVisualPosition", nameof(VisualPositionPostfix), false, "anchor visual -> image nearest the camera");
            Postfix(harmony, applied, "SandBox.View.Map.Managers.MapTracksVisualManager", "CalculateTrackFrame", nameof(TrackFramePostfix), true, "track frame -> image nearest the camera");
            // Decals, blockade arcs, storms, battle facings, quest arrows: positions shown at their images, differences periodic.
            Show(harmony, applied, "SandBox.View.Map.MapScreen", "TickCircles", true, "selection and hover decals -> images nearest the camera");
            Show(harmony, applied, "SandBox.View.Map.Visuals.MobilePartyVisual", "GetMapEventVisualRotation", true, "battle facing -> periodic difference");
            Show(harmony, applied, "Helpers.QuestHelper", "AddMapArrowFromPointToTarget", true, "quest arrow direction -> periodic difference");
            Show(harmony, applied, "NavalDLC.View.Map.Visuals.NavalMobilePartyVisual", "GetMapEventVisualRotation", false, "ship battle facing -> periodic difference");
            Show(harmony, applied, "NavalDLC.View.Map.Visuals.AnchorVisual", "CalculateAnchorFrame", false, "anchor frame direction -> periodic difference");
            Show(harmony, applied, "NavalDLC.View.NavalDLCViewHelpers+BlockadeVisualHelper", "GetPositionsOnBlockadeArc", false, "blockade arc -> images nearest the camera, periodic difference");
            Show(harmony, applied, "NavalDLC.View.NavalDLCViewHelpers+BlockadeVisualHelper", "InitializeBlockadeVisual", false, "blockade visual -> image nearest the camera, periodic difference");
            Show(harmony, applied, "NavalDLC.View.Map.Visuals.StormVisual", "VisualTick", false, "storm visual -> image nearest the camera");
            // Nameplates: position, visibility and distance at the images.
            nameplateCalls = 0;
            Transpile(harmony, applied, "SandBox.ViewModelCollection.Nameplate.PartyNameplateVM", "RefreshPosition", nameof(PositionToDisplayTranspiler), "party nameplate -> image nearest the camera");
            Transpile(harmony, applied, "SandBox.ViewModelCollection.Nameplate.PartyPlayerNameplateVM", "RefreshPosition", nameof(PositionToDisplayTranspiler), "player nameplate -> image nearest the camera");
            Transpile(harmony, applied, "SandBox.ViewModelCollection.Nameplate.SettlementNameplateVM", "CalculatePosition", nameof(WorldPosToDisplayTranspiler), "settlement nameplate position -> image nearest the camera");
            Transpile(harmony, applied, "SandBox.ViewModelCollection.Nameplate.SettlementNameplateVM", "IsVisible", nameof(WorldPosToDisplayTranspiler), "settlement nameplate visibility and distance -> image nearest the camera");
            // Settlement display copies, kept in step with the settlement visual.
            var visual = AccessTools.TypeByName("SandBox.View.Map.Visuals.SettlementVisual");
            var screen = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
            strategicEntity = visual == null ? null : visual.GetProperty("StrategicEntity", Members);
            mapEntity = visual == null ? null : visual.GetProperty("MapEntity", Members);
            visualsOfEntities = screen == null ? null : screen.GetProperty("VisualsOfEntities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (strategicEntity == null || mapEntity == null || visualsOfEntities == null) throw new MissingMemberException("SettlementVisual.StrategicEntity/MapEntity or MapScreen.VisualsOfEntities is not installed.");
            Postfix(harmony, applied, "SandBox.View.Map.Visuals.SettlementVisual", "OnStartup", nameof(SettlementStartupPostfix), true, "settlement visual -> one display copy at its strip image, registered to the settlement");
            foreach (var method in new[] { "SetSettlementLevelVisibility", "RefreshWallState", "RefreshSiegePreparations", "ValidateIsDirty" })
                Postfix(harmony, applied, "SandBox.View.Map.Visuals.SettlementVisual", method, nameof(SettlementSyncPostfix), true, "display copy mirrors the settlement's entity visibility (" + method + ")");
            Postfix(harmony, applied, "SandBox.View.Map.Visuals.SettlementVisual", "OnPartyRemoved", nameof(SettlementRemovedPostfix), true, "display copy removed with the settlement visual's party");
            var intersected = MNBL.ProceduralWorld.CampaignRegistry.PatchSignatures.Method("SandBox.View.Map.Managers.SettlementVisualManager", "OnVisualIntersected");   // MMR-284: by signature
            visibleOrFading = AccessTools.Method(AccessTools.TypeByName("SandBox.View.Map.Visuals.MapEntityVisual"), "IsVisibleOrFadingOut");
            if (visibleOrFading == null) throw new MNBL.ProceduralWorld.CampaignRegistry.PatchSignatureException(MNBL.ProceduralWorld.CampaignRegistry.PatchSignatures.Presentation, "SandBox.View.Map.Visuals.MapEntityVisual::IsVisibleOrFadingOut", "missing");
            harmony.Patch(intersected, prefix: new HarmonyMethod(typeof(PeriodicMapPresentation), nameof(SettlementIntersectedPrefix)));
            applied.Add(new Dictionary<string, object> { ["target"] = "SandBox.View.Map.Managers.SettlementVisualManager::OnVisualIntersected", ["patch"] = "settlement hover and selection -> the settlement the ray meets first", ["kind"] = "prefix" });
            // Visuals framed on their own changes, refreshed when the camera wraps or has moved far.
            tracksType = AccessTools.TypeByName("SandBox.View.Map.Managers.MapTracksVisualManager");
            if (tracksType == null || tracksType.GetField("_tracksDirty", Members) == null) throw new MissingMemberException("MapTracksVisualManager._tracksDirty is not installed.");
            tracksDirty = AccessTools.FieldRefAccess<bool>(tracksType, "_tracksDirty");
            anchorsType = AccessTools.TypeByName("NavalDLC.View.Map.Managers.AnchorVisualManager");
            anchorCache = anchorsType == null || anchorsType.GetField("_cachedPosition", Members) == null ? null : AccessTools.FieldRefAccess<CampaignVec2>(anchorsType, "_cachedPosition");
            var module = AccessTools.TypeByName("SandBox.View.SandBoxViewSubModule");
            visualManagerProperty = module == null ? null : module.GetProperty("SandBoxViewVisualManager", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (visualManagerProperty == null) throw new MissingMemberException("SandBoxViewSubModule.SandBoxViewVisualManager is not installed.");
        }

        // Removes the copies and their registrations.
        internal static void Release()
        {
            var registry = visualsOfEntities == null ? null : visualsOfEntities.GetValue(null) as IDictionary;
            foreach (var copy in Copies)
                if (registry != null) foreach (var p in copy.Registered) registry.Remove(p);
            Copies.Clear(); CopyErrors.Clear(); refreshes = 0;
            PeriodicMapCameraView.Release();
        }

        public static int NameplateCalls { get { return nameplateCalls; } }
        public static int Refreshes { get { return refreshes; } }

        // MMR-284: targets by their recorded signature (PatchSignatures: a War Sails view absent without War Sails is skipped; one whose signature
        // changed refuses the set, naming the patch). Transpilers also refuse when their IL has nothing to replace.
        private static MethodInfo Target(string typeName, string method, bool required)
        {
            var target = MNBL.ProceduralWorld.CampaignRegistry.PatchSignatures.Method(typeName, method);
            if (target == null && required) throw new MNBL.ProceduralWorld.CampaignRegistry.PatchSignatureException(MNBL.ProceduralWorld.CampaignRegistry.PatchSignatures.Presentation, typeName + "::" + method, "missing");
            return target;
        }

        private static void Postfix(Harmony harmony, List<Dictionary<string, object>> applied, string typeName, string method, string postfix, bool required, string label)
        {
            var target = Target(typeName, method, required);
            if (target == null) return;   // War Sails views are optional
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PeriodicMapPresentation), postfix));
            applied.Add(new Dictionary<string, object> { ["target"] = typeName + "::" + method, ["patch"] = label, ["kind"] = "postfix" });
        }

        private static int replacedNow;
        private static void Transpile(Harmony harmony, List<Dictionary<string, object>> applied, string typeName, string method, string transpiler, string label, bool required = true)
        {
            var target = Target(typeName, method, required);
            if (target == null) return;
            replacedNow = 0;
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(PeriodicMapPresentation), transpiler));
            if (replacedNow == 0) throw new MNBL.ProceduralWorld.CampaignRegistry.PatchSignatureException(MNBL.ProceduralWorld.CampaignRegistry.PatchSignatures.Presentation, typeName + "::" + method + " (" + label + ")", "its IL has nothing to replace");
            applied.Add(new Dictionary<string, object> { ["target"] = typeName + "::" + method, ["patch"] = label, ["kind"] = "transpiler", ["replacedCalls"] = replacedNow });
        }

        private static void Show(Harmony harmony, List<Dictionary<string, object>> applied, string typeName, string method, bool required, string label)
        {
            Transpile(harmony, applied, typeName, method, nameof(ShowTranspiler), label, required);
        }

        // ---- the display rule ----
        public static float Display(float x) { return PeriodicMapCameraView.Nearest(x); }
        public static Vec3 DisplayVec3(Vec3 v) { v.x = Display(v.x); return v; }
        public static Vec2 DisplayVec2(Vec2 v) { return new Vec2(Display(v.x), v.y); }
        public static MatrixFrame DisplayFrame(MatrixFrame f) { f.origin.x = Display(f.origin.x); return f; }
        // CampaignVec2.AsVec3() / Vec2.ToVec3(z) at the image nearest the camera (same stack shapes: the address of the position first).
        public static Vec3 DisplayAsVec3(ref CampaignVec2 position) { System.Threading.Interlocked.Increment(ref nameplateCalls); return DisplayVec3(position.AsVec3()); }
        public static Vec3 DisplayToVec3(ref Vec2 position, float z) { return DisplayVec3(position.ToVec3(z)); }

        private static void VisualPositionPostfix(ref Vec3 __result) { __result.x = Display(__result.x); }

        private static void TrackFramePostfix(ref MatrixFrame __result) { __result.origin.x = Display(__result.origin.x); }

        // The position each party (land or sea) is drawn at: its visual entity's frame (probes; null without a visual).
        internal static Vec3? DrawnPosition(MobileParty party)
        {
            foreach (var managerName in new[] { "SandBox.View.Map.Managers.MobilePartyVisualManager", "NavalDLC.View.Map.Managers.NavalMobilePartyVisualManager" })
            {
                var managerType = AccessTools.TypeByName(managerName);
                var currentProperty = managerType == null ? null : managerType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                var manager = currentProperty == null ? null : currentProperty.GetValue(null);
                var getter = manager == null ? null : managerType.GetMethod("GetVisualOfEntity", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(PartyBase) }, null);
                var visual = getter == null ? null : getter.Invoke(manager, new object[] { party.Party });
                var entityProperty = visual == null ? null : visual.GetType().GetProperty("StrategicEntity", Members);
                var entity = entityProperty == null ? null : entityProperty.GetValue(visual) as GameEntity;
                if (entity != null && (party.IsCurrentlyAtSea == managerName.StartsWith("NavalDLC", StringComparison.Ordinal) || managerName.StartsWith("SandBox", StringComparison.Ordinal) && !party.IsCurrentlyAtSea))
                    return entity.GetGlobalFrame().origin;
            }
            return null;
        }

        // Tracks and anchors are framed only when they change: after a camera wrap or a long camera move they take their images again. Runs
        // inside the camera's tick (an engine callback): it never throws, a failure is recorded.
        internal static void RefreshImages()
        {
            try
            {
                refreshes++;
                var manager = visualManagerProperty == null ? null : visualManagerProperty.GetValue(null);
                if (manager == null) return;
                if (getComponents == null) getComponents = manager.GetType().GetMethods(Members).First(m => m.Name == "GetComponents" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                var components = getComponents == null ? null : getComponents.Invoke(manager, null) as IEnumerable;
                if (components == null) return;
                foreach (var component in components)
                {
                    if (component == null) continue;
                    var type = component.GetType();
                    if (type == tracksType) tracksDirty(component) = true;
                    else if (anchorCache != null && type == anchorsType) anchorCache(component) = new CampaignVec2(new Vec2(float.NaN, float.NaN), false);
                }
            }
            catch (Exception e) { if (CopyErrors.Count < 20) CopyErrors.Add("refresh " + e.GetType().Name + ": " + e.Message); }
        }
        private static MethodInfo getComponents;

        // ---- transpilers ----
        private static IEnumerable<CodeInstruction> PositionToDisplayTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var asVec3 = AccessTools.Method(typeof(CampaignVec2), nameof(CampaignVec2.AsVec3));
            var display = AccessTools.Method(typeof(PeriodicMapPresentation), nameof(DisplayAsVec3));
            foreach (var i in instructions)
            {
                if (i.Calls(asVec3)) { i.opcode = OpCodes.Call; i.operand = display; replacedNow++; }
                yield return i;
            }
        }

        // SettlementNameplateVM's _worldPos read at the image nearest the camera: a load (ldfld) gets the display value; an address load
        // (ldflda, IsVisible's distance) gets the address of a local holding it.
        private static IEnumerable<CodeInstruction> WorldPosToDisplayTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var display = AccessTools.Method(typeof(PeriodicMapPresentation), nameof(DisplayVec3));
            LocalBuilder local = null;
            foreach (var i in instructions)
            {
                var field = i.operand as FieldInfo;
                bool worldPos = field != null && field.Name == "_worldPos";
                if (worldPos && i.opcode == OpCodes.Ldfld)
                {
                    yield return i; yield return new CodeInstruction(OpCodes.Call, display); replacedNow++;
                }
                else if (worldPos && i.opcode == OpCodes.Ldflda)
                {
                    if (local == null) local = generator.DeclareLocal(typeof(Vec3));
                    var load = new CodeInstruction(OpCodes.Ldfld, field) { labels = i.labels, blocks = i.blocks };
                    yield return load; yield return new CodeInstruction(OpCodes.Call, display);
                    yield return new CodeInstruction(OpCodes.Stloc, local); yield return new CodeInstruction(OpCodes.Ldloca, local);
                    replacedNow++;
                }
                else yield return i;
            }
        }

        // Positions shown at their images (AsVec3, ToVec3, visual positions, circle frames, storm positions) and position differences
        // periodic (Vec2 - Vec2 through the MMR-65 gate: positions only).
        private static IEnumerable<CodeInstruction> ShowTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var asVec3 = AccessTools.Method(typeof(CampaignVec2), nameof(CampaignVec2.AsVec3));
            var toVec3 = AccessTools.Method(typeof(Vec2), nameof(Vec2.ToVec3), new[] { typeof(float) });
            var subtract = AccessTools.Method(typeof(Vec2), "op_Subtraction", new[] { typeof(Vec2), typeof(Vec2) });
            var displayAsVec3 = AccessTools.Method(typeof(PeriodicMapPresentation), nameof(DisplayAsVec3));
            var displayToVec3 = AccessTools.Method(typeof(PeriodicMapPresentation), nameof(DisplayToVec3));
            var difference = AccessTools.Method(typeof(PeriodicSpatialQueries), nameof(PeriodicSpatialQueries.PositionDifference));
            var displayVec3 = AccessTools.Method(typeof(PeriodicMapPresentation), nameof(DisplayVec3));
            var displayVec2 = AccessTools.Method(typeof(PeriodicMapPresentation), nameof(DisplayVec2));
            var displayFrame = AccessTools.Method(typeof(PeriodicMapPresentation), nameof(DisplayFrame));
            foreach (var i in instructions)
            {
                var method = i.operand as MethodInfo;
                if (method != null && i.Calls(asVec3)) { i.opcode = OpCodes.Call; i.operand = displayAsVec3; replacedNow++; yield return i; }
                else if (method != null && i.Calls(toVec3)) { i.opcode = OpCodes.Call; i.operand = displayToVec3; replacedNow++; yield return i; }
                else if (method != null && i.Calls(subtract)) { i.opcode = OpCodes.Call; i.operand = difference; replacedNow++; yield return i; }
                else if (method != null && method.Name == "GetVisualPosition" && method.ReturnType == typeof(Vec3)) { yield return i; yield return new CodeInstruction(OpCodes.Call, displayVec3); replacedNow++; }
                else if (method != null && method.Name == "get_CircleLocalFrame" && method.ReturnType == typeof(MatrixFrame)) { yield return i; yield return new CodeInstruction(OpCodes.Call, displayFrame); replacedNow++; }
                else if (method != null && method.Name == "get_CurrentPosition" && method.ReturnType == typeof(Vec2) && method.DeclaringType != null && method.DeclaringType.Name == "Storm") { yield return i; yield return new CodeInstruction(OpCodes.Call, displayVec2); replacedNow++; }
                else yield return i;
            }
        }

        // ---- settlement hover and selection ----
        // The installed manager walks the collided entities from the last to the first: the first settlement it meets is hovered, the last
        // selected. The native list follows entity creation, not the ray, and display copies are created after the settlements, so along
        // one ray a copy and its settlement could resolve to different settlements where two overlap (a town and its village side by side:
        // seed 73's world, run 9524, the copy to the village and the settlement to the town). With a periodic world, the entries of every
        // visible settlement but the one the ray meets first move behind the count this manager reads, so both hover and selection are that
        // settlement. The arrays keep every entry for the other components, which read their own count.
        private static void SettlementIntersectedPrefix(UIntPtr[] intersectedEntityIDs, Intersection[] intersectionInfos, ref int entityCount, Vec3 worldMouseNear)
        {
            try
            {
                if (PeriodicWorld.Current == null || intersectedEntityIDs == null || intersectionInfos == null || entityCount < 2) return;
                var registry = visualsOfEntities.GetValue(null) as IDictionary;
                if (registry == null) return;
                Func<int, object> settlementVisual = i =>
                {
                    var v = registry.Contains(intersectedEntityIDs[i]) ? registry[intersectedEntityIDs[i]] : null;
                    return v != null && v.GetType().Name == "SettlementVisual" && (bool)visibleOrFading.Invoke(v, null) ? v : null;
                };
                object nearest = null; float best = float.MaxValue;
                for (int i = 0; i < entityCount; i++)
                {
                    var v = settlementVisual(i);
                    if (v == null) continue;
                    float d = (intersectionInfos[i].IntersectionPoint - worldMouseNear).LengthSquared;
                    if (d < best) { best = d; nearest = v; }
                }
                if (nearest == null) return;
                int kept = 0;
                for (int i = 0; i < entityCount; i++)
                {
                    var v = settlementVisual(i);
                    if (v != null && !ReferenceEquals(v, nearest)) continue;
                    if (i != kept)
                    {
                        var id = intersectedEntityIDs[kept]; intersectedEntityIDs[kept] = intersectedEntityIDs[i]; intersectedEntityIDs[i] = id;
                        var info = intersectionInfos[kept]; intersectionInfos[kept] = intersectionInfos[i]; intersectionInfos[i] = info;
                    }
                    kept++;
                }
                entityCount = kept;
            }
            catch (Exception e) { if (CopyErrors.Count < 20) CopyErrors.Add("pick " + e.GetType().Name + ": " + e.Message); }
        }

        // ---- settlement display copies ----
        private static void SettlementStartupPostfix(object __instance)
        {
            try { BuildCopy(__instance); }
            catch (Exception e) { if (CopyErrors.Count < 20) CopyErrors.Add(e.GetType().Name + ": " + e.Message); }   // an exception here would end the process
        }

        // MMR-247 X: the hang repro's bisect switch (Map2AcceptanceDecalLab): false stops the display copies mirroring the settlement visual
        // (counted in SyncsSkipped). The product never changes it.
        internal static bool CopySyncEnabled = true;
        internal static int SyncsSkipped;

        private static void SettlementSyncPostfix(object __instance)
        {
            if (!CopySyncEnabled) { SyncsSkipped++; return; }
            try { var copy = Copies.FirstOrDefault(c => c.Visual == __instance); if (copy != null) Sync(copy); }
            catch (Exception e) { if (CopyErrors.Count < 20) CopyErrors.Add("sync " + e.GetType().Name + ": " + e.Message); }
        }

        private static void SettlementRemovedPostfix(object __instance)
        {
            try
            {
                var copy = Copies.FirstOrDefault(c => c.Visual == __instance);
                if (copy == null) return;
                var registry = visualsOfEntities.GetValue(null) as IDictionary;
                if (registry != null) foreach (var p in copy.Registered) registry.Remove(p);
                if (copy.Copy != null && copy.Copy.Scene != null) copy.Copy.Scene.RemoveEntity(copy.Copy, 0);
                Copies.Remove(copy);
            }
            catch (Exception e) { if (CopyErrors.Count < 20) CopyErrors.Add("remove " + e.GetType().Name + ": " + e.Message); }
        }

        // The copy's entities take the visibility of the settlement's (pairwise, in hierarchy order).
        internal static void Sync(DisplayCopy copy)
        {
            int n = Math.Min(copy.Originals.Count, copy.Mirrors.Count);
            for (int i = 0; i < n; i++)
            {
                bool visible = copy.Originals[i].GetVisibilityExcludeParents();
                if (copy.Mirrors[i].GetVisibilityExcludeParents() != visible) copy.Mirrors[i].SetVisibilityExcludeParents(visible);
            }
            copy.Syncs++;
        }

        private static void BuildCopy(object visual)
        {
            var world = PeriodicWorld.Current;
            if (world == null) return;
            var entity = strategicEntity.GetValue(visual) as GameEntity;
            var party = mapEntity.GetValue(visual) as PartyBase;
            var settlement = party == null ? null : party.Settlement;
            if (entity == null || settlement == null || Copies.Any(c => c.Settlement == settlement.StringId)) return;
            var scene = entity.Scene;
            var frame = entity.GetGlobalFrame();
            float x = frame.origin.x;
            var strip = world.Coordinates.Images(x).Where(i => i.Shift != 0).ToList();
            if (strip.Count == 0) return;   // no strip image inside the terrain
            float image = (float)strip[0].X;
            string side = image > x ? "east" : "west";
            var record = new DisplayCopy { Settlement = settlement.StringId, CanonicalX = x, ImageX = image, Y = frame.origin.y, Entity = settlement.StringId + "_display_" + side, Visual = visual };
            // A fixture's static copy (tools/scenebin/build_periodic_world.py) is replaced by the runtime copy.
            var fixtureName = FixtureName(settlement.StringId, side);
            var fixture = fixtureName == null ? null : scene.FindEntityWithName(fixtureName);
            if (fixture != null) { record.ReplacedFixtureEntity = fixtureName; scene.RemoveEntity(fixture, 0); }
            var copy = GameEntity.CopyFrom(scene, entity, true, false);
            copy.Name = record.Entity;
            frame.origin.x = image;
            copy.SetGlobalFrame(in frame);
            record.Copy = copy;
            Pair(scene, entity, copy, record);
            // No tag on the copy: scene-wide tag queries (ports, drop-offs, siege spawns) find the settlement's entities only. Its script
            // components stay, inert (copied without script callbacks; removing them would call removal callbacks on uninitialised scripts).
            foreach (var e in record.Mirrors)
                foreach (var tag in (e.Tags ?? new string[0]).ToList()) e.RemoveTag(tag);
            var registry = (IDictionary)visualsOfEntities.GetValue(null);
            foreach (var e in record.Mirrors)
                if (!registry.Contains(e.Pointer)) { registry[e.Pointer] = visual; record.Registered.Add(e.Pointer); }
            Sync(record);
            Copies.Add(record);
        }

        // The settlement's entities and the copy's, pairwise in hierarchy order, walking both hierarchies together by child name. The native
        // copy leaves out children added at run time (an MMR-34 hideout's icon, SettlementEntityRuntime: seed 73's 115 hideouts had copies
        // without their icon); each child the copy lacks is copied on its own under its mirror parent, at the child's local frame.
        private static void Pair(Scene scene, GameEntity original, GameEntity mirror, DisplayCopy record)
        {
            record.Originals.Add(original); record.Mirrors.Add(mirror);
            var used = new HashSet<int>();
            for (int i = 0; i < original.ChildCount; i++)
            {
                var child = original.GetChild(i);
                GameEntity match = null;
                for (int j = 0; j < mirror.ChildCount && match == null; j++)
                    if (!used.Contains(j) && mirror.GetChild(j).Name == child.Name) { match = mirror.GetChild(j); used.Add(j); }
                if (match == null)
                {
                    match = GameEntity.CopyFrom(scene, child, true, false);
                    mirror.AddChild(match, false);
                    var local = child.GetLocalFrame();
                    match.SetLocalFrame(ref local, true);
                    used.Add(mirror.ChildCount - 1);
                    record.AddedChildren++;
                }
                Pair(scene, child, match, record);
            }
        }

        // The fixture names its copies <worldId>_display_<settlement id without the world prefix>_<side>; world ids are mnbl_<8 digits>.
        private static string FixtureName(string settlementId, string side)
        {
            var parts = settlementId.Split('_');
            if (parts.Length < 3 || parts[0] != "mnbl" || parts[1].Length != 8) return null;
            string worldId = parts[0] + "_" + parts[1];
            return worldId + "_display_" + settlementId.Substring(worldId.Length + 1) + "_" + side;
        }
    }
}
