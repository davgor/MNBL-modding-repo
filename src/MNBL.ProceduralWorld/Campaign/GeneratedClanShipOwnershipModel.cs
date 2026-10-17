using System;
using System.Collections.Generic;
using NavalDLC.ComponentInterfaces;
using TaleWorlds.CampaignSystem;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-35: the selling half of the generated world's fleet rule (docs/naval-content.md). ShipTradeCampaignBehavior sells a clan's ships
    // when it holds more than ClanShipOwnershipModel.GetIdealShipNumberForClan (buying is GeneratedShipDistributionModel's to refuse). This
    // model, added after War Sails' own (CampaignGameStarter.AddModel chains it as BaseModel), gives a landlocked clan an ideal fleet of zero,
    // so a ship it still holds is sold, and delegates every other clan to the installed model unchanged. The landlocked clans come from
    // NavalContentFactory (its plan, saved with the world package).
    internal sealed class GeneratedClanShipOwnershipModel : ClanShipOwnershipModel
    {
        private readonly HashSet<string> landlocked;

        public GeneratedClanShipOwnershipModel(IEnumerable<string> landlockedClans)
        {
            landlocked = new HashSet<string>(landlockedClans ?? new string[0], StringComparer.Ordinal);
        }

        // The startup audit adds a clan temporarily to check that War Sails reads this model.
        internal ICollection<string> Landlocked { get { return landlocked; } }
        internal ClanShipOwnershipModel Installed { get { return BaseModel; } }

        public override int GetIdealShipNumberForClan(Clan clan)
        {
            if (clan != null && landlocked.Contains(clan.StringId)) return 0;
            return BaseModel == null ? 0 : BaseModel.GetIdealShipNumberForClan(clan);
        }
    }
}
