using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encyclopedia;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-257: the ruins encyclopedia page: every FWGC++ ruin of the generated world (MapDressingRuntime.Ruins), with its name, civilization
    // and cause. No gameplay: a ruin has no detail page, so a click on a row goes nowhere (GetObject returns null and the installed
    // EncyclopediaManager.GoToLink then does nothing).
    //   * Registration: EncyclopediaManager.CreateEncyclopediaPages (SandBox's OnGameInitializationFinished) builds only the pages carrying the
    //     installed internal EncyclopediaModel attribute or OverrideEncyclopediaModel, and the base constructor reads its identifier types from
    //     the internal attribute alone (a page with only the override attribute throws there). So the page is made without its constructor
    //     (FormatterServices) and its private fields are set as the constructor would set them (identifier type, identifiers, no filters, the
    //     name sort), then added to the manager's page table, after SandBox built it (this module's OnGameInitializationFinished runs after
    //     SandBox's). Every installed member it reaches is checked first; a mismatch leaves the encyclopedia unchanged and is recorded.
    //   * The rows are read each time the list is built (a lazy sequence), so the page follows the world whose plan is installed.
    internal sealed class RuinsEncyclopediaPage : EncyclopediaPage
    {
        public const string Identifier = "MnblRuin";
        public static string LastError { get; private set; }
        public static bool Registered { get; private set; }

        private sealed class NameComparer : EncyclopediaListItemComparerBase
        {
            public override int Compare(EncyclopediaListItem x, EncyclopediaListItem y) { return String.Compare(x.Name, y.Name, StringComparison.CurrentCulture) * (IsAscending ? 1 : -1); }
            public override string GetComparedValueText(EncyclopediaListItem item) { return ""; }
        }

        private static IEnumerable<EncyclopediaListItem> Rows()
        {
            var plan = MapDressingRuntime.CurrentPlan();
            if (plan == null) yield break;
            foreach (var row in MapDressing.EncyclopediaRows(plan))
            {
                var ruin = plan.Ruins.First(r => r.Uid == row[0]);
                yield return new EncyclopediaListItem(ruin, row[1], row[2], row[0], Identifier, true, null);
            }
        }

        protected override IEnumerable<EncyclopediaListItem> InitializeListItems() { return Rows(); }
        protected override IEnumerable<EncyclopediaFilterGroup> InitializeFilterItems() { return new List<EncyclopediaFilterGroup>(); }
        protected override IEnumerable<EncyclopediaSortController> InitializeSortControllers() { return new List<EncyclopediaSortController>(); }
        public override string GetViewFullyQualifiedName() { return "EncyclopediaRuinPage"; }
        public override string GetStringID() { return "EncyclopediaRuin"; }
        public override TextObject GetName() { return new TextObject("{=mnbl_encyclopedia_ruins}Ruins"); }
        public override TextObject GetDescriptionText() { return new TextObject("{=mnbl_encyclopedia_ruins_text}The ruined cities of this world's history, with the civilization that built them and what ended them."); }
        public override MBObjectBase GetObject(string typeName, string stringID) { return null; }
        public override bool IsValidEncyclopediaItem(object o) { return false; }
        public override bool IsRelevant() { return MapDressingRuntime.Ruins().Count > 0; }

        // SubModule.OnGameInitializationFinished: adds the page to a generated campaign's encyclopedia. Never throws.
        internal static void Register(Game game)
        {
            Registered = false; LastError = null;
            try
            {
                var campaign = game == null ? null : game.GameType as Campaign;
                if (campaign == null || !(game.GameManager is GeneratedGameManager) || campaign.EncyclopediaManager == null) return;
                const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
                Func<Type, string, Type, FieldInfo> field = (owner, name, type) =>
                {
                    var f = owner.GetField(name, Members);
                    if (f == null || !f.FieldType.IsAssignableFrom(type)) throw new MissingFieldException(owner.FullName, name);
                    return f;
                };
                var pages = field(typeof(EncyclopediaManager), "_pages", typeof(Dictionary<Type, EncyclopediaPage>));
                var identifierTypes = field(typeof(EncyclopediaPage), "_identifierTypes", typeof(Type[]));
                var identifiers = field(typeof(EncyclopediaPage), "_identifiers", typeof(Dictionary<Type, string>));
                var filters = field(typeof(EncyclopediaPage), "_filters", typeof(List<EncyclopediaFilterGroup>));
                var items = field(typeof(EncyclopediaPage), "_items", typeof(IEnumerable<EncyclopediaListItem>));
                var sorts = field(typeof(EncyclopediaPage), "_sortControllers", typeof(List<EncyclopediaSortController>));
                var order = field(typeof(EncyclopediaPage), "<HomePageOrderIndex>k__BackingField", typeof(int));
                var table = (Dictionary<Type, EncyclopediaPage>)pages.GetValue(campaign.EncyclopediaManager);
                if (table == null) throw new InvalidOperationException("The encyclopedia has no page table yet.");
                if (table.ContainsKey(typeof(RuinRecord))) { Registered = true; return; }
                var page = (RuinsEncyclopediaPage)FormatterServices.GetUninitializedObject(typeof(RuinsEncyclopediaPage));
                identifierTypes.SetValue(page, new[] { typeof(RuinRecord) });
                identifiers.SetValue(page, new Dictionary<Type, string> { [typeof(RuinRecord)] = Identifier });
                filters.SetValue(page, new List<EncyclopediaFilterGroup>());
                items.SetValue(page, Rows());
                sorts.SetValue(page, new List<EncyclopediaSortController> { new EncyclopediaSortController(new TextObject("{=koX9okuG}None"), new NameComparer()) });
                order.SetValue(page, 900);
                table[typeof(RuinRecord)] = page;
                Registered = true;
            }
            catch (Exception error) { LastError = error.GetType().Name + ": " + error.Message; }
        }
    }
}
