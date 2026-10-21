using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-284 (findings §17 RT-41): every installed member a patch of this module (GeneratedPatches.Manifest) finds by name, with its full
    // signature: declaring type and assembly, static or instance, return type and parameter types (a field: its type). A patch locates its
    // target here and nowhere by a game build string, so a Bannerlord or War Sails update that keeps the member's signature keeps the patch,
    // and one that changes or removes it refuses that patch, naming it (PatchSignatureException). Transpilers also check the IL content they
    // replace (their own exact counts), and the MMR-281 and MMR-287 descriptors locate theirs by IL content alone (CompanionsTickPatch,
    // IncidentCulturePatch). Verify checks the whole table without patching: the setup screen (before Generate) and the loader (before a
    // load) refuse on its first failure. Pure reflection (no TaleWorlds or Harmony types), so tests/Campaign/GameUpdateTests.cs checks the
    // table against the installed assemblies without the game. Signatures are spelled as Format prints them (Name: namespace.Type, nested
    // types with '/', generic arguments in angle brackets, '&' by reference, '[]' arrays).
    internal static class PatchSignatures
    {
        public const string ColourGrade = "MMR-39 colour grade", CharacterCreation = "MMR-32 character creation", Startup = "MMR-34 startup",
            WandererIntroduction = "MMR-34 wanderer introduction", BattleScene = "MMR-42 battle scene record", SaveBlock = "MMR-46 activation save block",
            SaveBinding = "MMR-48 save binding", LoadRouting = "MMR-49 load routing", LoadGuard = "MMR-49 load guard", Periodic = "MMR-63/64/65 periodic world",
            Presentation = "MMR-66 periodic map presentation";

        public sealed class Target
        {
            public string Patch, Type, Assembly, Member, Signature;
            public bool IsField, Optional;   // Optional: the type is absent without War Sails, and the patch is then skipped
            public override string ToString() { return Type + "::" + Member + (IsField ? " : " : " ") + Signature; }
        }

        private static Target M(string patch, string assembly, string type, string member, string signature, bool optional = false)
        {
            return new Target { Patch = patch, Assembly = assembly, Type = type, Member = member, Signature = signature, Optional = optional };
        }
        private static Target F(string patch, string assembly, string type, string member, string fieldType)
        {
            return new Target { Patch = patch, Assembly = assembly, Type = type, Member = member, Signature = fieldType, IsField = true };
        }

        private const string Cs = "TaleWorlds.CampaignSystem", Core = "TaleWorlds.Core", Sb = "SandBox", SbView = "SandBox.View", SbVm = "SandBox.ViewModelCollection",
            NavalView = "NavalDLC.View", MbView = "TaleWorlds.MountAndBlade.View", CsVm = "TaleWorlds.CampaignSystem.ViewModelCollection";

        private const string V = "System.Void", B = "System.Boolean", Fl = "System.Single", Cv = "TaleWorlds.CampaignSystem.CampaignVec2", Mp = "TaleWorlds.CampaignSystem.Party.MobileParty",
            Nav = "TaleWorlds.CampaignSystem.Party.MobileParty/NavigationType", Vec2 = "TaleWorlds.Library.Vec2", Vec3 = "TaleWorlds.Library.Vec3", Frame = "TaleWorlds.Library.MatrixFrame",
            St = "TaleWorlds.CampaignSystem.Settlements.Settlement", Save = "TaleWorlds.SaveSystem.SaveGameFileInfo,System.Action<TaleWorlds.SaveSystem.Load.LoadResult>,System.Action",
            Visual = "SandBox.View.Map.Visuals.SettlementVisual", Camera = "SandBox.View.Map.MapCameraView", Helper = "Helpers.NavigationHelper";

        // The installed signatures, as read from Bannerlord v1.4.8.119303 / War Sails v1.2.8.119303 (every one checked at run time, never the build).
        public static readonly IList<Target> All = Array.AsReadOnly(new Target[] {
            M(ColourGrade, MbView, "TaleWorlds.MountAndBlade.View.Scripts.MapColorGradeManager", "Init", "instance " + V + "()"),
            F(ColourGrade, MbView, "TaleWorlds.MountAndBlade.View.Scripts.MapColorGradeManager", "colorGradeGrid", "System.Byte[]"),
            M(CharacterCreation, CsVm, "TaleWorlds.CampaignSystem.ViewModelCollection.CharacterCreation.CharacterCreationCultureStageVM", "SortCultureList",
              "instance " + V + "(TaleWorlds.Library.MBBindingList<TaleWorlds.CampaignSystem.ViewModelCollection.CharacterCreation.CharacterCreationCultureVM>)"),
            F(CharacterCreation, Cs, "TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationContent", "_characterCreationCultures",
              "System.Collections.Generic.Dictionary<TaleWorlds.CampaignSystem.CultureObject,System.Collections.Generic.KeyValuePair<System.Int32,System.Int32>>"),
            M(Startup, Cs, "TaleWorlds.CampaignSystem.CampaignBehaviors.NotablesCampaignBehavior", "SpawnNotablesAtGameStart", "instance " + V + "()"),
            M(Startup, Cs, "TaleWorlds.CampaignSystem.CampaignBehaviors.CompanionsCampaignBehavior", "OnNewGameCreated", "instance " + V + "(TaleWorlds.CampaignSystem.CampaignGameStarter)"),
            M(SaveBlock, Cs, "TaleWorlds.CampaignSystem.SaveHandler", "SaveAs", "instance " + V + "(System.String)"),
            M(SaveBlock, Cs, "TaleWorlds.CampaignSystem.SaveHandler", "QuickSaveCurrentGame", "instance " + V + "()"),
            M(SaveBlock, Cs, "TaleWorlds.CampaignSystem.SaveHandler", "SignalAutoSave", "instance " + V + "()"),
            M(SaveBlock, Cs, "TaleWorlds.CampaignSystem.SaveHandler", "ForceAutoSave", "instance " + V + "()"),
            M(SaveBinding, Core, "TaleWorlds.Core.MBSaveLoad", "OverwriteSaveAux",
              "static " + V + "(TaleWorlds.Core.CampaignSaveMetaDataArgs,System.String,System.Action<System.ValueTuple<TaleWorlds.Library.SaveResult,System.String>>)"),
            F(SaveBinding, Cs, "TaleWorlds.CampaignSystem.SaveHandler", "SaveArgsQueue", "System.Collections.Generic.Queue<TaleWorlds.CampaignSystem.SaveHandler/SaveArgs>"),
            M(LoadGuard, Core, "TaleWorlds.Core.MBSaveLoad", "LoadSaveGameData", "static TaleWorlds.SaveSystem.Load.LoadResult(System.String)"),
            M(LoadRouting, Sb, "SandBox.SandBoxSaveHelper", "TryLoadSave", "static " + V + "(" + Save + ")"),
            M(LoadRouting, Sb, "SandBox.SandBoxSaveHelper", "LoadGameAction", "static " + V + "(" + Save + ")"),
            F(LoadRouting, Sb, "SandBox.SandBoxSaveHelper", "_isInquiryActive", B),
            M(WandererIntroduction, Cs, "TaleWorlds.CampaignSystem.CampaignBehaviors.LordConversationsCampaignBehavior", "conversation_wanderer_introduction_on_condition", "instance " + B + "()"),
            M(WandererIntroduction, Cs, St, "FindFirst", "static " + St + "(System.Func<" + St + "," + B + ">)"),
            M(BattleScene, Cs, "TaleWorlds.CampaignSystem.GameComponents.DefaultSceneModel", "GetBattleSceneForMapPatch", "instance System.String(TaleWorlds.CampaignSystem.Map.MapPatchData," + B + ")"),
            // MMR-63/64/65: the periodic set (PeriodicPatches; the distance models' GetDistance overloads, the closest-settlement lambdas and the
            // PeriodicSpatialTable methods are located by name and IL content, with exact replacement counts).
            M(Periodic, Cs, Mp, "FindNextLocatable", "static " + Mp + "(TaleWorlds.CampaignSystem.Map.LocatableSearchData<" + Mp + ">&)"),
            M(Periodic, Cs, St, "FindNextLocatable", "static " + St + "(TaleWorlds.CampaignSystem.Map.LocatableSearchData<" + St + ">&)"),
            M(Periodic, Cs, "TaleWorlds.CampaignSystem.Party.PartyBase", "TaleWorlds.CampaignSystem.Map.IInteractablePoint.CanPartyInteract", "instance " + B + "(" + Mp + "," + Fl + ")"),
            M(Periodic, Cs, "TaleWorlds.CampaignSystem.Party.PartyBase", "CalculateVisibilityRangeOfMapPoint", "static " + Fl + "(" + Vec2 + ",TaleWorlds.CampaignSystem.Map.IMapPoint," + Fl + ")"),
            M(Periodic, Cs, "Helpers.MenuHelper", "EncounterAttackConsequence", "static " + V + "(TaleWorlds.CampaignSystem.GameMenus.MenuCallbackArgs)"),
            M(Periodic, Cs, "TaleWorlds.CampaignSystem.MapEvents.MapEvent", "GetEventDirection", "instance " + Vec2 + "(TaleWorlds.Core.BattleSideEnum," + Fl + "&)"),
            M(Periodic, Cs, Helper, "FindReachablePointAroundPosition", "static " + Cv + "(" + Cv + ",System.Int32[]," + Fl + "," + Fl + "," + B + ")"),
            M(Periodic, Cs, "TaleWorlds.CampaignSystem.GameState.MapState", "ProcessTravel", "instance " + V + "(" + Cv + ")"),
            M(Periodic, Cs, "Helpers.DistanceHelper", "FindClosestDistanceFromMobilePartyToMobileParty", "static " + Fl + "(" + Mp + "," + Mp + "," + Nav + "," + Fl + "&)"),
            M(Periodic, Cs, "Helpers.DistanceHelper", "GetDistanceBetweenMobilePartyToMobileParty", "static " + Fl + "(" + Mp + "," + Mp + "," + Nav + "," + Fl + "&)"),
            M(Periodic, Cs, "Helpers.SettlementHelper", "FindNearestSettlementToPoint", "static " + St + "(" + Cv + "&,System.Func<" + St + "," + B + ">)"),
            M(Periodic, Cs, Helper, "FindPointAroundPosition", "static " + Cv + "(" + Cv + "," + Nav + "," + Fl + "," + Fl + "," + B + "," + B + ")"),
            M(Periodic, Cs, Helper, "FindPointInsideArea", "static " + Cv + "(" + Vec2 + "," + Vec2 + "," + Nav + ")"),
            M(Periodic, Cs, Helper, "FindPointInsideArea", "static " + Cv + "(" + Vec2 + "," + Vec2 + "," + Cv + "," + Nav + "," + Fl + "," + Fl + "," + B + ")"),
            // MMR-66: the periodic map presentation (PeriodicMapPresentation, PeriodicMapCameraView; transpilers also count what they replace).
            M(Presentation, SbView, "SandBox.View.Map.Visuals.MobilePartyVisual", "GetVisualPosition", "instance " + Vec3 + "()"),
            M(Presentation, SbView, "SandBox.View.Map.Visuals.TrackVisual", "GetVisualPosition", "instance " + Vec3 + "()"),
            M(Presentation, NavalView, "NavalDLC.View.Map.Visuals.NavalMobilePartyVisual", "GetVisualPosition", "instance " + Vec3 + "()", true),
            M(Presentation, NavalView, "NavalDLC.View.Map.Visuals.AnchorVisual", "GetVisualPosition", "instance " + Vec3 + "()", true),
            M(Presentation, SbView, "SandBox.View.Map.Managers.MapTracksVisualManager", "CalculateTrackFrame", "instance " + Frame + "(TaleWorlds.CampaignSystem.Track)"),
            M(Presentation, SbView, "SandBox.View.Map.MapScreen", "TickCircles", "instance " + V + "()"),
            M(Presentation, SbView, "SandBox.View.Map.Visuals.MobilePartyVisual", "GetMapEventVisualRotation", "instance " + Fl + "()"),
            M(Presentation, Cs, "Helpers.QuestHelper", "AddMapArrowFromPointToTarget", "static " + V + "(TaleWorlds.Localization.TextObject," + Cv + "," + Cv + "," + Fl + "," + Fl + ")"),
            M(Presentation, NavalView, "NavalDLC.View.Map.Visuals.NavalMobilePartyVisual", "GetMapEventVisualRotation", "instance " + Fl + "()", true),
            M(Presentation, NavalView, "NavalDLC.View.Map.Visuals.AnchorVisual", "CalculateAnchorFrame", "instance " + Frame + "(TaleWorlds.CampaignSystem.Naval.AnchorPoint)", true),
            M(Presentation, NavalView, "NavalDLC.View.NavalDLCViewHelpers+BlockadeVisualHelper", "GetPositionsOnBlockadeArc",
              "static System.Collections.Generic.List<" + Vec3 + ">(" + St + ",System.Int32,System.Int32," + Fl + "," + Fl + ")", true),
            M(Presentation, NavalView, "NavalDLC.View.NavalDLCViewHelpers+BlockadeVisualHelper", "InitializeBlockadeVisual", "static " + V + "(" + Vec3 + ",TaleWorlds.Engine.GameEntity," + Vec3 + ")", true),
            M(Presentation, NavalView, "NavalDLC.View.Map.Visuals.StormVisual", "VisualTick", "instance " + V + "()", true),
            M(Presentation, SbVm, "SandBox.ViewModelCollection.Nameplate.PartyNameplateVM", "RefreshPosition", "instance " + V + "()"),
            M(Presentation, SbVm, "SandBox.ViewModelCollection.Nameplate.PartyPlayerNameplateVM", "RefreshPosition", "instance " + V + "()"),
            M(Presentation, SbVm, "SandBox.ViewModelCollection.Nameplate.SettlementNameplateVM", "CalculatePosition", "instance " + V + "(" + Vec3 + "&)"),
            M(Presentation, SbVm, "SandBox.ViewModelCollection.Nameplate.SettlementNameplateVM", "IsVisible", "instance " + B + "(" + Vec3 + "&)"),
            M(Presentation, SbView, Visual, "OnStartup", "instance " + V + "()"),
            M(Presentation, SbView, Visual, "SetSettlementLevelVisibility", "instance " + V + "()"),
            M(Presentation, SbView, Visual, "RefreshWallState", "instance " + V + "()"),
            M(Presentation, SbView, Visual, "RefreshSiegePreparations", "instance " + V + "(TaleWorlds.CampaignSystem.Party.PartyBase)"),
            M(Presentation, SbView, Visual, "ValidateIsDirty", "instance " + V + "()"),
            M(Presentation, SbView, Visual, "OnPartyRemoved", "instance " + V + "()"),
            M(Presentation, SbView, "SandBox.View.Map.Managers.SettlementVisualManager", "OnVisualIntersected",
              "instance " + B + "(TaleWorlds.Library.Ray,System.UIntPtr[],TaleWorlds.Engine.Intersection[],System.Int32," + Vec3 + "," + Vec3 + "," + Vec3 +
              ",SandBox.View.Map.Visuals.MapEntityVisual&,SandBox.View.Map.Visuals.MapEntityVisual&)"),
            M(Presentation, SbView, Camera, "OnBeforeTick", "instance " + V + "(SandBox.View.Map.MapCameraView/InputInformation&)"),
            M(Presentation, SbView, Camera, "UpdateMapCamera", "instance " + V + "(" + B + "," + Vec3 + ")"),
            M(Presentation, SbView, Camera, "GetCameraTargetForPosition", "instance " + Vec3 + "(" + Cv + ")"),
            M(Presentation, SbView, Camera, "GetCameraTargetForParty", "instance " + Vec3 + "(TaleWorlds.CampaignSystem.Party.PartyBase)"),
            M(Presentation, SbView, Camera, "StartCameraAnimation", "instance " + V + "(" + Cv + "," + Fl + ")"),
            M(Presentation, SbView, Camera, "GetMapCameraInput", "instance " + B + "(SandBox.View.Map.MapCameraView/InputInformation)") });

        // The recorded target (type, member and, for an overload, its signature); a member not recorded is a programming error.
        public static Target Of(string type, string member, string signature = null)
        {
            var found = All.Where(t => t.Type == type && t.Member == member && (signature == null || t.Signature == signature)).ToList();
            if (found.Count != 1) throw new InvalidOperationException("PatchSignatures records " + found.Count + " targets " + type + "::" + member + (signature == null ? "" : " " + signature) + ", not one.");
            return found[0];
        }

        // The installed method of a recorded target, or null for an optional one whose type is absent. Throws PatchSignatureException.
        public static MethodInfo Method(string type, string member, string signature = null) { return (MethodInfo)Locate(Of(type, member, signature), Types); }
        public static FieldInfo Field(string type, string member) { return (FieldInfo)Locate(Of(type, member), Types); }

        // The loaded type of that name, else the type loaded from its assembly by name (the game's own assembly resolver finds module DLLs).
        public static Func<Target, Type> Types = target =>
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = assembly.GetType(target.Type, false); } catch (Exception) { continue; }
                if (t != null) return t;
            }
            try { return Type.GetType(target.Type + ", " + target.Assembly, false); } catch (Exception) { return null; }
        };

        public const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public static MemberInfo Locate(Target target, Func<Target, Type> types)
        {
            var type = types(target);
            if (type == null)
            {
                if (target.Optional) return null;
                throw new PatchSignatureException(target.Patch, target.ToString(), "the type " + target.Type + " (" + target.Assembly + ") is not installed");
            }
            if (target.IsField)
            {
                var field = type.GetField(target.Member, Members);
                if (field != null && Name(field.FieldType) == target.Signature) return field;
                throw new PatchSignatureException(target.Patch, target.ToString(), field == null ? "no such field" : "installed: " + Name(field.FieldType));
            }
            var named = type.GetMethods(Members).Where(m => m.Name == target.Member).ToList();
            var match = named.Where(m => Format(m) == target.Signature).ToList();
            if (match.Count == 1) return match[0];
            throw new PatchSignatureException(target.Patch, target.ToString(), named.Count == 0 ? "no such method" : "installed: " + String.Join("; ", named.Select(Format)));
        }

        // Every recorded target that does not resolve (empty when the installed game and War Sails keep every signature).
        public static List<PatchSignatureException> Verify(Func<Target, Type> types = null, IEnumerable<Target> targets = null)
        {
            var failures = new List<PatchSignatureException>();
            foreach (var target in targets ?? All)
                try { Locate(target, types ?? Types); }
                catch (PatchSignatureException error) { failures.Add(error); }
            return failures;
        }

        public static string Format(MethodBase method)
        {
            var info = method as MethodInfo;
            return (method.IsStatic ? "static " : "instance ") + (info == null ? "void" : Name(info.ReturnType)) + "(" + String.Join(",", method.GetParameters().Select(p => Name(p.ParameterType))) + ")";
        }

        public static string Name(Type t)
        {
            if (t.IsByRef) return Name(t.GetElementType()) + "&";
            if (t.IsArray) return Name(t.GetElementType()) + "[]";
            if (t.IsGenericParameter) return t.Name;
            if (t.IsGenericType && !t.IsGenericTypeDefinition)
            {
                string definition = Name(t.GetGenericTypeDefinition());
                int tick = definition.IndexOf('`');
                return (tick < 0 ? definition : definition.Substring(0, tick)) + "<" + String.Join(",", t.GetGenericArguments().Select(Name)) + ">";
            }
            if (t.IsNested) return Name(t.DeclaringType) + "/" + t.Name;
            return String.IsNullOrEmpty(t.Namespace) ? t.Name : t.Namespace + "." + t.Name;
        }
    }

    // A patch target whose installed signature is not the recorded one (a game or War Sails update changed or removed it): the patch is
    // refused, never applied to a member it was not written for. Patch names the patch (PatchSignatures' names), Target the member.
    internal sealed class PatchSignatureException : InvalidOperationException
    {
        public PatchSignatureException(string patch, string target, string installed)
            : base("The " + patch + " patch is refused: its target " + target + " is not in the installed game (" + installed + ").")
        {
            Patch = patch; Target = target; Installed = installed;
        }
        public string Patch { get; private set; }
        public string Target { get; private set; }
        public string Installed { get; private set; }
    }
}
