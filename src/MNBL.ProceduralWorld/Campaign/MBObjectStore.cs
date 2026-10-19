using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.CampaignSystem.Settlements.Workshops;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-30: the live MBObjectManager behind IObjectStore. Game thread only. The census types are listed through
    // GetObjectTypeList<T>; the template types generated records reference are looked up by GetObject<T>(id). Element
    // names map to classes as the installed RegisterType<T>(elementName, ...) calls register them
    // (evidence/runtime/object-type-registrations.txt). An unknown element name is never passed to the engine
    // (MBObjectManager.GetObject(string, string) raises Debug.FailedAssert for one); it reads as absent.
    internal sealed class MBObjectStore : IObjectStore
    {
        private static readonly Dictionary<string, Func<MBObjectManager, IEnumerable<MBObjectBase>>> Typed =
            new Dictionary<string, Func<MBObjectManager, IEnumerable<MBObjectBase>>>(StringComparer.Ordinal) {
                ["Culture"] = m => m.GetObjectTypeList<CultureObject>(), ["NPCCharacter"] = m => m.GetObjectTypeList<CharacterObject>(),
                ["Hero"] = m => m.GetObjectTypeList<Hero>(), ["Kingdom"] = m => m.GetObjectTypeList<Kingdom>(), ["Faction"] = m => m.GetObjectTypeList<Clan>(),
                ["Settlement"] = m => m.GetObjectTypeList<Settlement>(), ["Town"] = m => m.GetObjectTypeList<Town>(), ["Village"] = m => m.GetObjectTypeList<Village>(),
                ["Hideout"] = m => m.GetObjectTypeList<Hideout>(), ["RetirementSettlementComponent"] = m => m.GetObjectTypeList<RetirementSettlementComponent>(),
                ["MobileParty"] = m => m.GetObjectTypeList<MobileParty>(), ["WorkshopType"] = m => m.GetObjectTypeList<WorkshopType>(),
                ["LocationComplexTemplate"] = m => m.GetObjectTypeList<LocationComplexTemplate>() };
        private static readonly Dictionary<string, Func<MBObjectManager, string, MBObjectBase>> Lookup =
            new Dictionary<string, Func<MBObjectManager, string, MBObjectBase>>(StringComparer.Ordinal) {
                ["SkillSet"] = (m, id) => m.GetObject<MBCharacterSkills>(id), ["VillageType"] = (m, id) => m.GetObject<VillageType>(id),
                ["Item"] = (m, id) => m.GetObject<ItemObject>(id), ["EquipmentRoster"] = (m, id) => m.GetObject<MBEquipmentRoster>(id),
                ["BodyProperty"] = (m, id) => m.GetObject<MBBodyProperty>(id), ["ItemCategory"] = (m, id) => m.GetObject<ItemCategory>(id),
                ["PartyTemplate"] = (m, id) => m.GetObject<PartyTemplateObject>(id), ["Skill"] = (m, id) => m.GetObject<SkillObject>(id) };
        private readonly MBObjectManager manager;
        private readonly Action<string> onLoad;
        private readonly Dictionary<string, Dictionary<string, MBObjectBase>> snapshot = new Dictionary<string, Dictionary<string, MBObjectBase>>(StringComparer.Ordinal);

        public MBObjectStore(StoreStage stage, Action<string> onLoad = null)
        {
            manager = MBObjectManager.Instance;
            if (manager == null) throw new InvalidOperationException("MBObjectManager is unavailable.");
            Stage = stage; this.onLoad = onLoad;
        }

        public StoreStage Stage { get; private set; }

        private MBObjectBase Find(string type, string id)
        {
            if (!Typed.ContainsKey(type))
            {
                Func<MBObjectManager, string, MBObjectBase> lookup;
                return Lookup.TryGetValue(type, out lookup) ? lookup(manager, id) : null;
            }
            Dictionary<string, MBObjectBase> map;
            if (!snapshot.TryGetValue(type, out map))
            {
                map = new Dictionary<string, MBObjectBase>(StringComparer.Ordinal);
                var list = Typed[type](manager);
                if (list != null) foreach (var o in list) if (o != null && o.StringId != null) map[o.StringId] = o;
                snapshot[type] = map;
            }
            MBObjectBase found;
            return map.TryGetValue(id, out found) ? found : null;
        }

        // GetObjectTypeList<T> is null for a type the object manager no longer holds (the campaign's temporary types after
        // initialization); the template types are permanent.
        public bool HasType(string type)
        {
            Func<MBObjectManager, IEnumerable<MBObjectBase>> list;
            return Typed.TryGetValue(type, out list) ? list(manager) != null : Lookup.ContainsKey(type);
        }

        public bool Contains(string type, string id) { return Find(type, id) != null; }
        public bool IsReady(string type, string id) { var o = Find(type, id); return o != null && o.IsReady; }

        // An approved shared template: an object some earlier module already registered, of a type that is never a world
        // instance.
        public bool IsSharedTemplate(string type, string id) { return !RegistrationPlan.IsWorldType(type) && IsReady(type, id); }

        // Village.Deserialize links a village to its town (Town.SetTradeBoundVillageInternal) only when the bound settlement is
        // already a town; this reads whether that happened. Settlement.IsTown excludes castles, so a castle's village is never
        // trade-bound at deserialization (VillageTradeBoundCampaignBehavior.UpdateTradeBounds gives it a town later): null, not
        // applicable (MMR-33 run 35960).
        public bool? TradeBound(string villageSettlementId, string boundSettlementId)
        {
            var village = Find("Settlement", villageSettlementId) as Settlement;
            var bound = Find("Settlement", boundSettlementId) as Settlement;
            if (village == null || bound == null || village.Village == null) return null;
            if (bound.Town != null && bound.Town.IsCastle) return null;
            if (bound.Town == null) return false;
            return bound.Town.TradeBoundVillages != null && bound.Town.TradeBoundVillages.Contains(village.Village);
        }

        public IEnumerable<KeyValuePair<string, bool>> Objects(string type)
        {
            Func<MBObjectManager, IEnumerable<MBObjectBase>> list;
            var values = Typed.TryGetValue(type, out list) ? list(manager) : null;
            if (values == null) return Enumerable.Empty<KeyValuePair<string, bool>>();
            return values.Where(o => o != null && o.StringId != null).Select(o => new KeyValuePair<string, bool>(o.StringId, o.IsReady)).ToList();
        }

        public void LoadDocument(string phase, XmlDocument document)
        {
            if (onLoad != null) onLoad(phase);
            snapshot.Clear();
            manager.LoadXml(document, Game.Current != null && Game.Current.GameType.IsDevelopment);
            snapshot.Clear();
        }

        public bool Unregister(string type, string id)
        {
            var o = Find(type, id);
            if (o == null) return false;
            manager.UnregisterObject(o);
            snapshot.Remove(type);
            return true;
        }
    }

    // CampaignObjectManager's lists with each entry's readiness. Settlements is null until InitializeOnNewGame; the other lists
    // exist and are empty until then (run 7752).
    internal sealed class CampaignLists : ICampaignLists
    {
        public IEnumerable<KeyValuePair<string, IList<KeyValuePair<string, bool>>>> Lists()
        {
            var manager = Campaign.Current == null ? null : Campaign.Current.CampaignObjectManager;
            return new List<KeyValuePair<string, IList<KeyValuePair<string, bool>>>> {
                Ids("Settlements", () => manager.Settlements), Ids("Clans", () => manager.Clans), Ids("Kingdoms", () => manager.Kingdoms),
                Ids("AliveHeroes", () => manager.AliveHeroes), Ids("DeadOrDisabledHeroes", () => manager.DeadOrDisabledHeroes),
                Ids("MobileParties", () => manager.MobileParties) };
        }

        // A list that is null, or not readable yet, is reported as unpublished.
        private static KeyValuePair<string, IList<KeyValuePair<string, bool>>> Ids<T>(string name, Func<IEnumerable<T>> list) where T : MBObjectBase
        {
            IList<KeyValuePair<string, bool>> ids = null;
            try { var values = list(); if (values != null) ids = values.Where(o => o != null).Select(o => new KeyValuePair<string, bool>(o.StringId, o.IsReady)).ToList(); }
            catch (NullReferenceException) { }
            catch (InvalidOperationException) { }
            return new KeyValuePair<string, IList<KeyValuePair<string, bool>>>(name, ids);
        }
    }
}
