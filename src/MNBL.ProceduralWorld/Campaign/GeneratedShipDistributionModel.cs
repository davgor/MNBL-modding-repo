using System;
using System.Collections.Generic;
using NavalDLC.ComponentInterfaces;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-35: the half of the fleet rule that stops a landlocked clan getting ships (docs/naval-content.md). ShipTradeCampaignBehavior buys
    // a ship for a clan with a fixed chance, for a party ShipDistributionModel.CanPartyTakeShip accepts, from any town that sells, with no
    // reachability check (the ideal fleet of ClanShipOwnershipModel only decides selling). This model, chained over War Sails' own (BaseModel),
    // refuses ships, and fleet transfers, to the parties of a clan whose home has no port town on an open basin in its land group, and
    // delegates everything else unchanged. Its landlocked set is the same one GeneratedClanShipOwnershipModel holds.
    internal sealed class GeneratedShipDistributionModel : ShipDistributionModel
    {
        private readonly ICollection<string> landlocked;

        public GeneratedShipDistributionModel(ICollection<string> landlockedClans) { landlocked = landlockedClans; }

        internal ShipDistributionModel Installed { get { return BaseModel; } }

        private bool Landlocked(MobileParty party) { return party != null && party.ActualClan != null && landlocked.Contains(party.ActualClan.StringId); }

        public override float GetScoreForPartyShipComposition(MobileParty party, MBReadOnlyList<Ship> ships)
        {
            return BaseModel == null ? 0f : BaseModel.GetScoreForPartyShipComposition(party, ships);
        }

        public override bool CanPartyTakeShip(PartyBase party, Ship ship)
        {
            if (party != null && Landlocked(party.MobileParty)) return false;
            return BaseModel != null && BaseModel.CanPartyTakeShip(party, ship);
        }

        public override bool CanSendShipToParty(Ship ship, MobileParty party)
        {
            if (Landlocked(party)) return false;
            return BaseModel != null && BaseModel.CanSendShipToParty(ship, party);
        }
    }
}
