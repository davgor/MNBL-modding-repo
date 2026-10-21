// Map epic 2 policies (MMR-248) whose consumers land in later tickets: the terrain-shaping stage (MMR-278,
// worldgen/policies/terrain-shaping.json) and the population pass (MMR-279, worldgen/policies/population.json). The keys and
// these strict readers are frozen here; the consumers read the structs. docs/biome-presentation.md lists every key.
#pragma once
#include <filesystem>
#include <map>
#include <string>
#include <vector>

namespace worldbridge::policy {
namespace fs = std::filesystem;

struct Shaping {
    std::string stage;                          // afterTravelPlacement
    struct Despike { bool enabled = true, keep_ridges = true; double max_slope_deg = 0; int ridge_neighbours = 0, sweeps = 0; } despike;
    struct Coast { bool enabled = true; double land_max_slope_deg = 0, sea_max_slope_deg = 0; int band_samples = 0; } coast;
    struct Pads { bool enabled = true, merge_touching = true; std::map<std::string, double> radius; double max_slope_deg = 0; int blend_samples = 0; } pads;
    struct Lakes { bool enabled = true; double bed_below_level = 0, shore_above_level = 0; } lakes;
    std::vector<std::string> coherence_order;   // lakes, coast, pads, despike (a permutation)
    struct LowPass { bool enabled = true; std::vector<int> kernel; std::vector<std::string> keep; } low_pass;
    struct Normals { bool smoothed_field = true; std::vector<int> kernel; } normals;
    struct Resolution { int side = 257, fine_side = 513; bool fine = false; std::string spline; } resolution;
    struct Gates {
        double interior_p95_deg = 0, interior_max_deg = 0, coast_p95_deg = 0, steep_pad_deg = 0;
        int isolated_spikes = 0, steep_pads_max = 0;
        bool travel_identical = true, no_lake_hidden = true, seams_identical = true;
    } gates;
    std::string sha256;                         // of the canonical document
};
// Strict: unknown members, wrong types and values outside their ranges are refused (json::Error).
Shaping load_shaping(const fs::path& file);

struct TroopLine {
    std::string source_clan, kind, party_template, template_culture;   // kind: mercenary | mafia | nomad | sect
    std::vector<std::string> character_templates;                      // exactly 4
    int tier = 0;
};
struct Population {
    int base_fiefs = 0, base_towns = 0, base_kingdoms = 0, base_noble_clans = 0;
    double kingdoms_per_base = 0, houses_per_base = 0, companies_per_base = 0, outlaws_per_base = 0;
    int kingdoms_min = 1, kingdom_min_houses = 0, kingdom_max_fiefs = 0, house_min_per_kingdom = 0, house_max_fiefs = 0;
    double adult_lords_per_house = 0, minor_share = 0, wanderers_per_town = 0;
    int company_members_min = 0, company_members_max = 0, outlaw_members_min = 0, outlaw_members_max = 0;
    std::vector<std::string> merge_order;
    bool merge_cross_water = true;
    int wars_per_kingdom = 0, wars_max_per_kingdom = 0;
    std::vector<std::string> war_partners, seat_priority, member_priority, kin, lord_cultures;
    int house_size_min = 0, house_size_max = 0, house_cap = 0, house_min_adults = 0, adult_age_min = 18, adult_age_max = 70;
    bool house_each_sex = true, dead_parents = true, minors = true, minors_both_parents = true;
    std::vector<std::string> captain_order, company_member_order, outlaw_leader_order, outlaw_kinds;
    int company_tier = 0, outlaw_tier = 0, outlaw_max_war_kingdoms = 0;
    std::map<std::string, TroopLine> mercenary;                  // presentation culture -> its company line
    std::map<std::string, std::vector<TroopLine>> outlaw_lines;  // presentation culture -> its outlaw lines
    std::vector<std::string> traits;                             // the personality traits, in vector order
    std::vector<std::pair<std::vector<int>, int>> trait_joint;   // installed personality vectors with their counts
    double shared_trait_share = 0;
    double rotation_daily_chance = 0;
    int wanderer_reserve = 0;   // reserve FWGC++ names per presentation culture of the towns (MMR-281, campaign.json wandererReserve)
    std::vector<std::string> wanderer_sources, backstory_unsafe;
    struct WandererTemplate { std::string id, culture; bool female = false; };
    std::vector<WandererTemplate> wanderer_templates;
    std::vector<std::string> spill_order;
    int spill_house_cap = 0;
    bool regal_to_company = false;
    std::map<std::string, int> streams;
    std::map<std::string, std::map<std::string, int>> notable_caps;   // census.notableCaps: town | village -> occupation -> cap (rosters.json)
    std::string sha256;
};
// Strict, as load_shaping; also checks the per-culture table covers every lord culture (RT-03) and the base is 133 fiefs.
Population load_population(const fs::path& file);

}  // namespace worldbridge::policy
