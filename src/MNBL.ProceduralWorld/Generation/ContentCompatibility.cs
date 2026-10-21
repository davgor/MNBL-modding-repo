using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Missions;
using TaleWorlds.ModuleManager;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-283: refused content, with every conflict that refused it (each naming its object and, when one provides it, its module).
    internal sealed class ContentConflictException : Exception
    {
        public readonly List<CatalogPin.Conflict> Conflicts;
        public ContentConflictException(List<CatalogPin.Conflict> conflicts)
            : base("The installed content conflicts with generated worlds: " + String.Join("; ", conflicts.Take(5)) + (conflicts.Count > 5 ? " (and " + (conflicts.Count - 5) + " more)" : ""))
        { Conflicts = conflicts; }
    }

    // MMR-283: content mods against generated worlds, checked on the setup screen before Generate (docs/world-activation.md, "Content mods"):
    // the live campaign catalog against the reduced pin (CatalogPin.Check: every object a world can reference, by key, element and read
    // fields, and the closure of the stock categories, templates and policy roots through the pinned engine defaults), the battle-scene table
    // the campaign will load against the catalogue (BattleTableCheck), and every scene the mission catalogue names installed in an active
    // module. Each refusal is a GenerationError naming the module and the object, in localizable text. The activation and the loader run
    // the same checks (WorldReconstruction.LiveCatalog and LoadConflicts, GeneratedBattleTerrain.CheckModuleTables).
    internal static class ContentCompatibility
    {
        private static ModuleSources modules;
        public const int ShownModules = 3;

        // The active modules in load order, read once per process (the active set does not change while the game runs).
        public static ModuleSources Modules()
        {
            if (modules == null) modules = new ModuleSources(ModuleHelper.GetActiveModules().Select(m => ModuleSources.Read(m.Id, ModuleHelper.GetModuleFullPath(m.Id), m.IsOfficial)).ToList());
            return modules;
        }

        // Engine thread, at the main menu. Never throws: a check that cannot run is itself a problem.
        public static List<GenerationError> SetupProblems()
        {
            try
            {
                var conflicts = Conflicts((label, work) => work());
                return Errors(conflicts);
            }
            catch (Exception error)
            {
                return new List<GenerationError> { GenerationErrors.ContentUnchecked(error.GetType().Name + ": " + error.Message) };
            }
        }

        // Every conflict of the installed content (engine runs a step on the engine thread), attributed to its module.
        public static List<CatalogPin.Conflict> Conflicts(Func<string, Func<byte[]>, byte[]> engine)
        {
            var pin = WorldReconstruction.InstalledPin();
            var conflicts = CatalogPin.Check(pin, CatalogPin.Parse(WorldReconstruction.LiveDocuments(engine, pin.Documents, pin.GameType)), GeneratedCampaignDataset.InstalledEngineCharacterIds);
            var battle = GeneratedBattleTerrain.CheckModuleTables();
            if (!battle.Ok) conflicts.Add(BattleConflict(battle));
            var sources = Modules();
            foreach (var scene in MissionScenes.Installed().Scenes.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
                if (sources.SceneProvider(scene.Id) == null) conflicts.Add(new CatalogPin.Conflict { Kind = CatalogPin.Scene, Key = "Scene." + scene.Id, Detail = "installed in " + scene.Module + " when the catalogue was made" });
            CatalogPin.Attribute(conflicts, sources);
            return conflicts;
        }

        public static CatalogPin.Conflict BattleConflict(BattleTableCheck check)
        {
            var culprit = check.Culprit();
            return new CatalogPin.Conflict { Kind = CatalogPin.Battle, Key = "Scene." + (culprit.Key ?? "?"), Module = culprit.Value,
                Official = culprit.Value != null && Modules().Modules.Any(m => m.Id == culprit.Value && m.Official),
                Detail = check.Lost.Count + " kinds of ground lose their battle scene (first " + (check.Lost.Count > 0 ? check.Lost[0] : "-") + "); changed " + String.Join(", ", check.Changed.Take(5).Select(r => r.Id)) +
                         "; removed " + String.Join(", ", check.Removed.Take(5).Select(r => r.Id)) + (check.FileProblems.Count > 0 ? "; " + String.Join("; ", check.FileProblems.Take(3).Select(p => p.Key + ": " + p.Value)) : "") };
        }

        // One error per module (the first ShownModules modules, then one for the rest), naming its first object; the others in the detail.
        public static List<GenerationError> Errors(List<CatalogPin.Conflict> conflicts)
        {
            var groups = conflicts.GroupBy(c => c.Module ?? "").ToList();
            var errors = groups.Take(ShownModules).Select(g => Error(g.First(), g.Skip(1))).ToList();
            if (groups.Count > ShownModules) errors.Add(Error(groups[ShownModules].First(), groups.Skip(ShownModules).SelectMany(g => g).Skip(1)));
            return errors;
        }

        public static GenerationError Error(CatalogPin.Conflict c, IEnumerable<CatalogPin.Conflict> more)
        {
            var others = (more ?? Enumerable.Empty<CatalogPin.Conflict>()).Select(x => x.ToString()).ToList();
            string detail = c + (others.Count > 0 ? "; also " + String.Join("; ", others.Take(20)) + (others.Count > 20 ? " (" + (others.Count - 20) + " more)" : "") : "");
            return GenerationErrors.Content(c.Kind, c.Key, c.Module, c.Official, c.Target, detail);
        }
    }
}
