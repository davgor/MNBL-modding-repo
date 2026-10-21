// Campaign semantic compiler (MMR-27): source-derived intermediate records and stable ids for the generated campaign.
// It creates no Bannerlord objects; it resolves source identities, categories and ownership joins, synthesizes only
// what the engine graph needs and the source lacks (with provenance), and leaves engine values as typed pending slots
// for MMR-28..36. Every join uses stable source UIDs; integer site and hamlet ids are used only to reach a UID.
//
//   town     <- settlements.sites[]   (uid)          kingdom and owner house from the realm holding it
//   village  <- humans.hamlets[]      (uid)          bound to its core city's town; production and harbour by role (the travel
//                                                     compiler's village quota, MMR-252, then binds exactly 3 per town and 2 per castle)
//   castle   <- humans.fortresses[]   (uid)          owned via the garrison city, or via the realm of its culture
//   kingdom  <- heroes.realms[]       (uid)          ruler = sovereign, or a synthesized regent
//   house    <- one provisional house per realm (synthesized, uid "house:<realm uid>", id kind clan)
//   hero     <- heroes.people[]       (uid)          occupation by role; house, town, village or none by occupation
//   port     <- harbour villages, gathered on their town
// Names (MMR-286): every display name goes through the shared name cleaner (name_cleaner.hpp): Age tags stripped, unique per kind by a
// lexicon suffix, never a numeral, the source name kept as sourceName; villages, castles and nameless towns are coined from lexicon roots,
// regents named after FWGC++ residents of their civilization, ruins (ruins[]) cleaned for the map's markers.
// The population pass (population.cpp, MMR-279) then merges the realms into kingdoms and plans the houses, mercenary companies,
// outlaw clans and wanderers of campaign.json v2 on the final village binding.
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <string>
#include <vector>
#include "worldbridge/name_cleaner.hpp"
#include "worldbridge/source_world.hpp"
#include "worldbridge/stable_ids.hpp"

namespace worldbridge::campaign {
namespace fs = std::filesystem;

// concepts (MMR-286): the lexicon concepts a village of the role is named from (name_cleaner.hpp Unique::coin).
struct RoleRule { std::string production; std::vector<std::string> concepts; bool harbour = false; };
// bucket (map epic 2, MMR-248; MMR-279 places each living hero): house | company | wanderer | notable | none; fallback_bucket: where the
// role goes when its bucket has no place (empty: none); seat_buckets: a bucket per council seat that overrides bucket.
struct HeroRule { std::string occupation, without_realm, bucket, fallback_bucket; std::map<std::string, std::string> seat_buckets; };
// The shared name cleaner's policy (map epic 2, MMR-248 keys; name_cleaner.hpp, MMR-279; MMR-286 applies it to every display name).
using NameCleaner = names::Policy;
// affiliation: clan | settlement | none; settlement (only for affiliation settlement): town | village.
struct OccupationRule { std::string affiliation, settlement, evidence; };

struct ContentPolicy {
    std::map<std::string, std::string> settlement_kinds, fortress_kinds, hamlet_kinds;  // source kind -> game kind
    std::map<std::string, RoleRule> hamlet_roles;
    std::map<std::string, HeroRule> hero_roles;
    std::map<std::string, bool> hero_statuses;               // status -> instantiate
    std::map<std::string, OccupationRule> occupations;       // installed Occupation value -> rule and evidence
    std::map<std::string, std::string> names;                // templates (rulingClan), placeholders checked on load
    std::map<std::string, std::vector<std::string>> places;  // names.places: town (a nameless source site), castle -> lexicon concepts
    NameCleaner cleaner;
    struct Minimum { std::string rule, evidence; std::int64_t minimum = 0; };
    std::vector<Minimum> minima;
    std::string sha256;
};
ContentPolicy load_content(const fs::path& file);

struct CulturePolicy {
    std::vector<std::string> official;
    std::map<std::string, std::string> presentation;  // civilization -> official culture id
    std::string sha256;
};
CulturePolicy load_cultures(const fs::path& file);

// A value the engine needs that a later ticket resolves; "resolved" when this compiler already decided it.
struct Slot { std::string name, status, owner, value; };

struct Town {
    std::string id, uid, name, name_provenance, source_name, civilization, culture_uid, culture_provenance, presentation, kingdom_id, owner_clan_id, city_class;
    bool capital = false, port = false;
    std::int64_t population = 0;
    std::optional<std::int64_t> urban, rural;
    std::vector<std::string> villages, castles;
    std::vector<Slot> slots;
};
// bound_kind: town | castle. source_town: the town whose rural population gives the hearth share (the compiler town; the
// garrison town for a synthesized village of a castle). origin: source | reassigned (bound elsewhere than its compiler town) |
// synthesized (MMR-252 quota); tier: the clearance tier a synthesized village was placed in. pinned: a port's harbour village.
struct Village {
    std::string id, uid, name, name_provenance, bound_id, role, production, culture_uid, bound_kind = "town", source_town, origin = "source", tier;
    bool harbour = false, pinned = false;
    std::optional<std::int64_t> sea_node;
    double harbour_quality = 0;
    std::vector<Slot> slots;
};
struct Castle {
    std::string id, uid, name, name_provenance, owner_clan_id, owner_provenance, garrison_town_id, kingdom_id, culture_uid;
    std::vector<std::string> villages;   // bound villages (MMR-252 quota)
    std::vector<Slot> slots;
};
struct Port {
    std::string town_id;
    std::vector<std::string> village_ids;
    std::vector<std::int64_t> sea_nodes;
    double best_quality = 0;
    bool forced = false;   // a forced port (MMR-251 town repair, written by the travel compiler): MMR-252 gives it a harbour village
    std::vector<Slot> slots;
};
// step: seed (the kingdom's first realm) | civilizationLand | presentationProximity | splitAboveMax (population.json kingdomMerge).
struct MergeStep { std::string realm, step; bool crosses_water = false; };
struct Kingdom {
    std::string id, uid, name, source_name, english_name, civilization, civilization_name, parent_race_id, culture_uid, presentation, ruling_house_id, ruler_id, capital_id;
    std::vector<std::string> towns, castles;
    std::vector<std::string> member_realms;   // FWGC++ realm uids, highest-ranked first (MMR-279)
    std::vector<MergeStep> merge_steps;
    std::vector<Slot> slots;
};
// A noble house (a Bannerlord clan of a kingdom). origin: kingdomRuler | realmRuler | castellan | pretender | magistrate | forcedSeat.
// seat: the fief the house is founded on (its home); fiefs: the towns and castles it owns (MMR-279: 1..houses.maxFiefs).
struct House {
    std::string id, uid, name, kingdom_id, leader_id, home_id, seat_id, culture_uid, presentation, provenance, origin, source_name;
    std::vector<std::string> fiefs, members;
    std::vector<Slot> slots;
};
// bucket (MMR-279): house | company | outlaw | wanderer | notable. clan_id: the house, company or outlaw clan of a clan member.
// age: Bannerlord years (adults within population.json houses.age). alive false: a dead parent (kin link only). synthesized: made by
// the population pass (kin, castellans, captains, soldiers, outlaws) or a regent; given: the source given name (house roots).
struct Hero {
    std::string id, uid, name, source_name, role, occupation, affiliation, clan_id, settlement_id, settlement_kind, kingdom_id, civilization, race_id, presentation, provenance;
    std::string bucket, given, epithet, spouse_id, father_id, mother_id;
    bool ruler = false, female = false, alive = true, synthesized = false;
    int age = 0;
    std::vector<Slot> slots;
};
// A mercenary company or an outlaw clan (minor clans, MMR-279). kind: mercenary for companies; mafia | nomad | sect for outlaw clans.
// wars: the kingdoms of the outlaw clan's culture (the war guard). source: the FWGC++ record it comes from, or "synthesized".
struct MinorClan {
    std::string id, uid, name, name_provenance, source_name, home_id, leader_id, presentation, culture_uid, kind, party_template, source;
    std::vector<std::string> members, character_templates, wars;
    int tier = 0;
    std::vector<Slot> slots;
};
struct Wanderer { std::string hero, presentation, epithet; bool female = false; };
// A runtime wanderer's name (campaign.json wandererReserve, MMR-281): an FWGC++ resident of a civilization presented through the
// culture, with the resident's sex, a name no hero carries, and an archetypal FWGC++ epithet (empty: the template's).
struct ReserveName { std::string name, epithet, presentation, civilization; bool female = false; };
struct War { std::string a, b, partner; };   // partner: landBorder | nearestBySea
// The population census (campaign.json census; MMR-279). wanderers is W (the smallest integer not below the float32 0.6 x towns);
// engine_children the children the engine's random child generator adds (ceil(adults / 2) less the planned minors, per house).
struct Census {
    std::int64_t fiefs = 0, towns = 0, castles = 0, kingdoms = 0, houses = 0, adult_lords = 0, minors = 0, dead_parents = 0, engine_children = 0;
    std::int64_t companies = 0, company_members = 0, outlaws = 0, outlaw_members = 0, wanderers = 0, wanderers_planned = 0, notables = 0, notables_bounded = 0;
    std::int64_t source_heroes = 0, unassigned = 0, moved_notables = 0;
    std::map<std::string, std::int64_t> buckets, synthesized, targets, engine_children_by_house;
    struct Overflow { std::string hero, from, to, reason; };
    std::vector<Overflow> overflow;
    std::vector<std::string> notes;
};
// An FWGC++ ruin (ruins[]; MMR-286): its cleaned display name, unique among the ruins, and the source name. The map's ruin markers and
// the ruins encyclopedia page show name (MapDressing RuinNames).
struct Ruin { std::string uid, name, source_name; };
// What names made after the compile need (MMR-286; not written): each civilization's naming heritage and each culture's civilization.
struct Naming {
    std::map<std::string, names::Heritage> heritage;
    std::map<std::string, std::string> culture_civilization;
    // The heritage of a culture's civilization, else of the fallback civilization, else an empty one (the cleaner's neutral roots).
    const names::Heritage& of(const std::string& culture_uid, const std::string& fallback_civilization) const;
};
struct RuleResult { std::string rule, evidence, status; std::int64_t minimum = 0, actual = 0; };
// A village or castle removed after compilation (for example by placement), with its reason. Its id stays reserved.
struct Exclusion { std::string id, kind, uid, reason; };
// The village quota's record (MMR-252, campaign.json villageQuota; travel-graph.md "Village quota"). exceptions: villages closer than
// the clearance to a fortification (islet exceptions); moved: notables of dropped villages (hero, from village, to village).
struct VillageQuota {
    int town = 3, castle = 2, kept = 0, reassigned = 0, dropped = 0, synthesized = 0, protected_kept = 0, clearance_moves = 0, passes = 0;
    bool stable = false;
    double spacing = 0;
    struct Exception { std::string village, fortification, reason; double distance = 0; };
    struct Moved { std::string hero, from, to; };
    std::vector<Exception> exceptions;
    std::vector<Moved> moved;
    std::map<std::string, std::int64_t> tiers;   // synthesized villages per tier
};
struct Compiled {
    std::string layout_seed, source_identity, world_sha256, prefix;
    std::vector<ids::Entry> id_map;
    std::vector<Kingdom> kingdoms;
    std::vector<House> houses;
    std::vector<Hero> heroes;
    std::vector<Town> towns;
    std::vector<Castle> castles;
    std::vector<Village> villages;
    std::vector<Port> ports;
    std::vector<Ruin> ruins;
    Naming naming;
    std::vector<std::string> not_instantiated;   // source people the policy records but does not instantiate
    std::vector<RuleResult> rules;
    std::vector<Exclusion> exclusions;
    std::map<std::string, std::int64_t> producer;  // the producer's limits and the observed counts
    std::optional<VillageQuota> quota;             // set by the travel compiler (written only after the quota ran)
    // The population pass (MMR-279; written only after it ran, with the heroes' bucket, sex, age and kin links).
    std::vector<MinorClan> companies, outlaws;
    std::vector<Wanderer> wanderers;
    std::vector<ReserveName> wanderer_reserve;   // MMR-281: the names the runtime wanderer tick gives the wanderers it makes
    std::vector<War> wars;
    std::optional<Census> census;
};

// world_sha256 is the SHA-256 of the split's world.json; with the source identity it fixes the world prefix.
// Refuses (json::Error) unknown categories, unresolved references (unknown realm, city, culture, home city, hamlet,
// sovereign), cities held by no realm or by two, unmapped civilizations, a missing producer report, producer-limit
// violations, player-adopted rows and failed minima, with the reason.
Compiled compile(const source::SourceWorld& world, const ContentPolicy& content, const CulturePolicy& cultures, const std::string& world_sha256, int id_hash_hex = 10);

// Every reference resolves to a record of the right kind, every town and castle is owned by a house of its kingdom, every village bound
// to a town or castle that lists it (and its source town a town), every kingdom has a capital among its towns and a ruler leading its
// ruling house, every clan member's clan lists it, and kin links name heroes. Returns the problems found (empty = closed).
std::vector<std::string> closure_problems(const Compiled& compiled);

// Removes one village or castle (for example after a navigation decision), records it in exclusions and re-checks
// closure; notables of a removed village move to its source town, and only a port this removal empties is erased (RT-12: a
// forced port without villages stays). A castle with bound villages cannot be removed (closure). Towns are never removed:
// generated towns are preserved and placement must find them a site. Refuses ("preserved_settlement", "broken_join") and then
// leaves compiled unchanged.
void exclude(Compiled& compiled, const std::string& id, const std::string& reason);

// XML attribute escaping, the localized "{=id}name" form with braces (TextObject variables) neutralized, and the two
// combined for an XML attribute. All three drop control characters, tab included.
std::string xml_escape(const std::string& text);
std::string localized(const std::string& id, const std::string& name);
std::string localized_xml(const std::string& id, const std::string& name);

// The canonical id-map.json and campaign.json of a compiled world (after any exclusions), exactly as
// --compile-campaign writes them.
std::string id_map_document(const Compiled& compiled);
std::string records_document(const Compiled& compiled, const ContentPolicy& content, const CulturePolicy& cultures);

// worldbridge --compile-campaign SPLIT --contract FILE --content FILE --cultures FILE --out NEW_DIRECTORY
int compile_campaign_main(int argc, char** argv);

}  // namespace worldbridge::campaign
