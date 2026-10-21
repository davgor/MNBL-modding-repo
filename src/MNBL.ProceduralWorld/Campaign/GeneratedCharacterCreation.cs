using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterCreationContent;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-32: character creation lists exactly the cultures the generated world presents through (docs/heroes.md). The installed
    // CharacterCreationCampaignBehavior adds aserai, battania, empire, khuzait, sturgia and vlandia and NavalCharacterCreationCampaignBehavior
    // adds nord, each as a content handler (priorities 800 and 1000, installed IL). This behaviour registers a content handler after both
    // and, once every handler has added its cultures (AfterInitializeContent), removes the cultures the world does not present through.
    // CharacterCreationContent has no removal method, so the private _characterCreationCultures dictionary is edited by reflection;
    // its name and type are checked against v1.4.8.119303 and a mismatch throws instead of leaving the full list. A world culture the
    // installed handlers did not add is reported as missing; nothing is added.
    // The installed culture screen (CharacterCreationCultureStageVM.SortCultureList) places the six vanilla cultures with
    // Enumerable.Single, which throws when one is missing (run 18560 crashed there). While a restricted list is active, a Harmony prefix
    // (Bannerlord.Harmony, id mnbl.proceduralworld.creation) skips that sort and keeps the list's own order; with all six present, or
    // when no restriction is active, the installed sort runs unchanged. The patch is removed when character creation finalizes.
    internal sealed class GeneratedCharacterCreation : CampaignBehaviorBase, ICharacterCreationContentHandler
    {
        public const int Priority = 2000;
        public const string HarmonyId = "mnbl.proceduralworld.creation";
        private static readonly string[] VanillaSorted = { "aserai", "battania", "empire", "khuzait", "sturgia", "vlandia" };
        private static Harmony harmony;
        private static bool restricted;
        public bool SortSkipped { get; private set; }
        private static GeneratedCharacterCreation active;
        private readonly HashSet<string> cultures;
        private readonly Action<string> trace;
        public List<string> Kept { get; private set; }
        public List<string> Removed { get; private set; }
        public List<string> Missing { get; private set; }

        public GeneratedCharacterCreation(IEnumerable<string> cultures, Action<string> trace)
        {
            this.cultures = new HashSet<string>(cultures, StringComparer.Ordinal);
            if (this.cultures.Count == 0) throw new ArgumentException("A generated world presents through at least one culture.");
            this.trace = trace ?? (s => { });
        }

        public override void RegisterEvents() { CampaignEvents.OnCharacterCreationInitializedEvent.AddNonSerializedListener(this, OnInitialized); }
        public override void SyncData(IDataStore dataStore) { }
        private void OnInitialized(CharacterCreationManager manager)
        {
            manager.RegisterCharacterCreationContentHandler(this, Priority);
            if (harmony == null)
            {
                // MMR-284: by signature (PatchSignatures); a changed sort refuses the patch, naming it.
                MNBL.ProceduralWorld.CampaignRegistry.GeneratedPatches.RequireSupportedBuild("character creation");
                var sort = MNBL.ProceduralWorld.CampaignRegistry.PatchSignatures.Method("TaleWorlds.CampaignSystem.ViewModelCollection.CharacterCreation.CharacterCreationCultureStageVM", "SortCultureList");
                harmony = new Harmony(HarmonyId);
                harmony.Patch(sort, prefix: new HarmonyMethod(typeof(GeneratedCharacterCreation), nameof(SortPrefix)));
                trace("character_creation_sort_patch_applied");
            }
            active = this;
        }

        // Skips the installed sort only when the restricted list lacks one of the cultures it places; otherwise the installed sort runs.
        private static bool SortPrefix(object listToWorkOn)
        {
            if (!restricted || active == null) return true;
            var list = listToWorkOn as IList;
            var present = new HashSet<string>(StringComparer.Ordinal);
            if (list != null)
                foreach (var item in list)
                {
                    var culture = item == null ? null : AccessTools.Property(item.GetType(), "Culture")?.GetValue(item) as CultureObject;
                    if (culture != null) present.Add(culture.StringId);
                }
            if (VanillaSorted.All(present.Contains)) return true;
            active.SortSkipped = true;
            return false;
        }

        void ICharacterCreationContentHandler.InitializeContent(CharacterCreationManager manager) { }
        void ICharacterCreationContentHandler.OnStageCompleted(CharacterCreationStageBase stage) { }
        void ICharacterCreationContentHandler.OnCharacterCreationFinalize(CharacterCreationManager manager) { Release(trace); }

        // Also when the game ends (MMR-34): a new game that ends before creation finalizes leaves no patch or active filter behind.
        internal static void Release(Action<string> trace)
        {
            restricted = false; active = null;
            if (harmony != null) { harmony.UnpatchAll(HarmonyId); harmony = null; (trace ?? (s => { }))("character_creation_sort_patch_removed"); }
        }

        void ICharacterCreationContentHandler.AfterInitializeContent(CharacterCreationManager manager)
        {
            var field = MNBL.ProceduralWorld.CampaignRegistry.PatchSignatures.Field(typeof(CharacterCreationContent).FullName, "_characterCreationCultures");
            var dictionary = field.GetValue(manager.CharacterCreationContent) as IDictionary;
            if (dictionary == null) throw new InvalidOperationException("CharacterCreationContent._characterCreationCultures is null.");
            var listed = dictionary.Keys.Cast<CultureObject>().ToList();
            Removed = listed.Where(c => !cultures.Contains(c.StringId)).Select(c => c.StringId).OrderBy(c => c, StringComparer.Ordinal).ToList();
            foreach (var culture in listed.Where(c => !cultures.Contains(c.StringId))) dictionary.Remove(culture);
            Kept = dictionary.Keys.Cast<CultureObject>().Select(c => c.StringId).OrderBy(c => c, StringComparer.Ordinal).ToList();
            Missing = cultures.Where(c => !Kept.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
            restricted = Removed.Count > 0;
            trace("character_creation_cultures:kept=" + String.Join(",", Kept) + ";removed=" + String.Join(",", Removed) + ";missing=" + String.Join(",", Missing));
        }
    }
}
