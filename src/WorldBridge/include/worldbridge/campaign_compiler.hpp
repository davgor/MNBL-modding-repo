// Campaign semantic compiler (MMR-27): source-derived intermediate records and stable ids for the generated campaign.
// It creates no Bannerlord objects; it resolves source identities, categories and ownership joins, synthesizes only
// what the engine graph needs and the source lacks (with provenance), and leaves engine values as typed pending slots
// for MMR-28..36. Every join uses stable source UIDs; integer site and hamlet ids are used only to reach a UID.
//
//   town     <- settlements.sites[]   (uid)          kingdom and owner clan from the realm holding it
//   village  <- humans.hamlets[]      (uid)          bound to its core city's town; production and harbour by role
//   castle   <- humans.fortresses[]   (uid)          owned via the garrison city, or via the realm of its culture
//   kingdom  <- heroes.realms[]       (uid)          ruler = sovereign, or a synthesized regent
//   clan     <- one ruling house per realm (synthesized, uid "clan:<realm uid>")
//   hero     <- heroes.people[]       (uid)          occupation by role; clan, town, village or none by occupation
//   port     <- harbour villages, gathered on their town
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <string>
#include <vector>
#include "worldbridge/source_world.hpp"
#include "worldbridge/stable_ids.hpp"

namespace worldbridge::campaign {
namespace fs = std::filesystem;

struct RoleRule { std::string production, label; bool harbour = false; };
struct HeroRule { std::string occupation, without_realm; };
// affiliation: clan | settlement | none; settlement (only for affiliation settlement): town | village.
struct OccupationRule { std::string affiliation, settlement, evidence; };

struct ContentPolicy {
    std::map<std::string, std::string> settlement_kinds, fortress_kinds, hamlet_kinds;  // source kind -> game kind
    std::map<std::string, RoleRule> hamlet_roles;
    std::map<std::string, HeroRule> hero_roles;
    std::map<std::string, bool> hero_statuses;               // status -> instantiate
    std::map<std::string, OccupationRule> occupations;       // installed Occupation value -> rule and evidence
    std::map<std::string, std::string> names;                // templates, placeholders checked on load
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
    std::string id, uid, name, name_provenance, civilization, culture_uid, culture_provenance, presentation, kingdom_id, owner_clan_id, city_class;
    bool capital = false, port = false;
    std::int64_t population = 0;
    std::optional<std::int64_t> urban, rural;
    std::vector<std::string> villages, castles;
    std::vector<Slot> slots;
};
struct Village {
    std::string id, uid, name, name_provenance, bound_id, role, production, culture_uid;
    bool harbour = false;
    std::optional<std::int64_t> sea_node;
    double harbour_quality = 0;
    std::vector<Slot> slots;
};
struct Castle {
    std::string id, uid, name, name_provenance, owner_clan_id, owner_provenance, garrison_town_id, kingdom_id, culture_uid;
    std::vector<Slot> slots;
};
struct Port {
    std::string town_id;
    std::vector<std::string> village_ids;
    std::vector<std::int64_t> sea_nodes;
    double best_quality = 0;
    std::vector<Slot> slots;
};
struct Kingdom {
    std::string id, uid, name, english_name, civilization, civilization_name, parent_race_id, culture_uid, presentation, ruling_clan_id, ruler_id, capital_id;
    std::vector<std::string> towns, castles;
    std::vector<Slot> slots;
};
struct Clan {
    std::string id, uid, name, kingdom_id, leader_id, home_id, culture_uid, presentation, provenance;
    std::vector<std::string> members;
    std::vector<Slot> slots;
};
struct Hero {
    std::string id, uid, name, role, occupation, affiliation, clan_id, settlement_id, settlement_kind, kingdom_id, civilization, race_id, presentation, provenance;
    bool ruler = false;
    std::vector<Slot> slots;
};
struct RuleResult { std::string rule, evidence, status; std::int64_t minimum = 0, actual = 0; };
// A village or castle removed after compilation (for example by placement), with its reason. Its id stays reserved.
struct Exclusion { std::string id, kind, uid, reason; };

struct Compiled {
    std::string layout_seed, source_identity, world_sha256, prefix;
    std::vector<ids::Entry> id_map;
    std::vector<Kingdom> kingdoms;
    std::vector<Clan> clans;
    std::vector<Hero> heroes;
    std::vector<Town> towns;
    std::vector<Castle> castles;
    std::vector<Village> villages;
    std::vector<Port> ports;
    std::vector<std::string> not_instantiated;   // source people the policy records but does not instantiate
    std::vector<RuleResult> rules;
    std::vector<Exclusion> exclusions;
    std::map<std::string, std::int64_t> producer;  // the producer's limits and the observed counts
};

// world_sha256 is the SHA-256 of the split's world.json; with the source identity it fixes the world prefix.
// Refuses (json::Error) unknown categories, unresolved references (unknown realm, city, culture, home city, hamlet,
// sovereign), cities held by no realm or by two, unmapped civilizations, a missing producer report, producer-limit
// violations, player-adopted rows and failed minima, with the reason.
Compiled compile(const source::SourceWorld& world, const ContentPolicy& content, const CulturePolicy& cultures, const std::string& world_sha256, int id_hash_hex = 10);

// Every reference resolves to a record of the right kind, every town and castle is owned, every village bound, every
// kingdom has a capital among its towns and a ruler leading its ruling clan. Returns the problems found (empty = closed).
std::vector<std::string> closure_problems(const Compiled& compiled);

// Removes one village or castle (for example after a navigation decision), records it in exclusions and re-checks
// closure; notables of a removed village move to its town. Towns are never removed: generated towns are preserved and
// placement must find them a site. Refuses ("preserved_settlement", "broken_join") and then leaves compiled unchanged.
void exclude(Compiled& compiled, const std::string& id, const std::string& reason);

// XML attribute escaping, the localized "{=id}name" form with braces (TextObject variables) neutralized, and the two
// combined for an XML attribute. All three drop control characters, tab included.
std::string xml_escape(const std::string& text);
std::string localized(const std::string& id, const std::string& name);
std::string localized_xml(const std::string& id, const std::string& name);

// worldbridge --compile-campaign SPLIT --contract FILE --content FILE --cultures FILE --out NEW_DIRECTORY
int compile_campaign_main(int argc, char** argv);

}  // namespace worldbridge::campaign
