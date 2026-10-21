// Map epic 2 policy tests (MMR-248): map2_policy_tests POLICIES_DIR WORK_DIR
// The shared biome table (terrain-materials.json v2) and its agreement with playability.json cells.landFaceGroups, the
// population and terrain-shaping policies, the hero-role buckets and the one format capability: each loads with its frozen
// values, and each strict reader refuses unknown members and values outside the rules.
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <sstream>
#include <string>
#include "worldbridge/capabilities.hpp"
#include "worldbridge/policy_map2.hpp"
#include "worldbridge/travel_graph.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;

namespace {
int checks = 0, failures = 0;
fs::path policies, work;

void check(bool ok, const std::string& what) {
    ++checks;
    if (ok) std::cout << "ok    " << what << "\n";
    else { ++failures; std::cout << "FAIL  " << what << "\n"; }
}
void refused(const std::string& what, const std::string& code, const std::function<void()>& body) {
    try { body(); check(false, what + ": accepted, expected " + code); }
    catch (const json::Error& e) { check(e.code == code, what + " -> " + e.code + (e.code == code ? "" : " (wanted " + code + "): " + e.message)); }
    catch (const std::exception& e) { check(false, what + ": unexpected " + std::string(e.what())); }
}
std::string slurp(const fs::path& p) { std::ifstream in(p, std::ios::binary); std::ostringstream s; s << in.rdbuf(); return s.str(); }
void spit(const fs::path& p, const std::string& text) { fs::create_directories(p.parent_path()); std::ofstream out(p, std::ios::binary | std::ios::trunc); out << text; }
// A copy of a policy with one text replaced (the text must occur), written under the work directory.
fs::path mutate(const std::string& file, const std::string& name, const std::string& from, const std::string& to) {
    std::string text = slurp(policies / file);
    const auto at = text.find(from);
    if (at == std::string::npos) { check(false, name + ": text not found"); return policies / file; }
    text.replace(at, from.size(), to);
    const auto out = work / "policies" / (name + "-" + file);
    spit(out, text);
    return out;
}

void biome_table() {
    const auto m = terrain::load_materials(policies / "terrain-materials.json");
    auto row = [&](std::int64_t id) { return m.presentation.at(id); };
    check(m.layer_set.size() == 15 && m.layer_set[0].name == "grass" && m.layer_set[14].name == "permanent_snow" && m.layer_set[6].diffuse == "desert_floor_a" &&
          m.layer_set[4].diffuse == "ground_forest_b_d" && m.layer_set[12].diffuse == "cliff_b_d" && m.layer_set[10].diffuse == "ground_pebbles_a_d" &&
          std::none_of(m.layer_set.begin(), m.layer_set.end(), [](const auto& l) { return l.diffuse.find("main") != std::string::npos; }) &&
          std::all_of(m.layer_set.begin(), m.layer_set.end(), [](const auto& l) { return l.scale >= 15 && l.scale <= 20 && l.tint_mode == 0; }),
          "layerSet: 15 slots, grass base, permanent snow on top; full-colour textures (no pale *_mainmap, MMR-249), scales 15-20, no tint");
    check(m.alpha.rule == "overInverse" && m.alpha.smooth_blend == 1 && !m.alpha.slope_transparency && m.flora.biomes.size() == 3 && m.flora.biomes.at("forest").layer == "forest_floor",
          "alpha: the over-compositing inverse, smooth blend 1, no slope transparency; map trees on forest, boreal forest and rainforest");
    check(row(2).terrain_type == "Desert" && row(3).terrain_type == "Plain" && row(4).terrain_type == "Forest" && row(5).terrain_type == "Steppe" && row(13).terrain_type == "Swamp" &&
          row(16).terrain_type == "Steppe" && row(17).terrain_type == "Snow" && row(0).water && row(0).terrain_type.empty() && row(8).water,
          "the shared table: desert Desert, grassland Plain, forest Forest, exposed rock and cold tundra Steppe, marsh Swamp, land ice Snow, water no face");
    check(row(17).snow == "permanent" && row(16).snow == "seasonal" && m.permanent_white.snow_months == 12 && m.permanent_white.terrain_type == "Snow" &&
          row(17).layers.at("permanent_snow") == 1.0, "permanent white: land ice and 12-month snow, all permanent_snow, the Snow face");
    check(row(2).battle_class == "desert" && row(4).battle_class == "woodland" && row(16).battle_class == "cold" && row(2).colour_grade == 20 && row(3).colour_grade_wet == 60 &&
          row(0).colour_grade == -1, "battle classes and colour grades per biome");
    bool no_dune = true;
    for (const auto& t : m.mutation.land_valid) no_dune = no_dune && t != "Dune";
    check(no_dune && m.mutation.schools.size() == 8 && m.mutation.schools.at("fire") == "Desert" && m.mutation.hidden_looks.at("blood") == "infernal" &&
          m.mutation.generated_variants == "visual" && m.mutation.battle_class == "faceClass" && m.mutation.core_overrides.at("water").at("land_ice") == "Snow",
          "mutation remap: 8 visible schools within the land-valid set (no Dune), hidden schools reuse a look, generated variants visual only");

    const auto playability = travel::load_policy(policies / "playability.json");
    bool agrees = true;
    try { travel::check_face_table(playability, m); } catch (const std::exception&) { agrees = false; }
    check(agrees, "playability cells.landFaceGroups agrees with the shared biome table");
    const auto other = terrain::load_materials(mutate("terrain-materials.json", "face_disagrees", "\"terrainType\": \"Desert\"", "\"terrainType\": \"Plain\""));
    refused("a face table that differs from playability", "face_table_mismatch", [&] { travel::check_face_table(playability, other); });

    auto refuse_materials = [&](const std::string& name, const std::string& from, const std::string& to, const std::string& code) {
        const auto file = mutate("terrain-materials.json", name, from, to);
        refused("material policy " + name, code, [&] { terrain::load_materials(file); });
    };
    refuse_materials("mutation_dune", "\"landValid\": [\"Plain\",", "\"landValid\": [\"Dune\", \"Plain\",", "face_group_mismatch");
    refuse_materials("school_outside_set", "\"fire\": \"Desert\"", "\"fire\": \"Mountain\"", "face_group_mismatch");
    refuse_materials("unknown_layer", "{\"sand\": 0.6, \"arid_soil\": 0.4}", "{\"dunes\": 0.6, \"arid_soil\": 0.4}", "unmapped_category");
    refuse_materials("mix_not_one", "{\"sand\": 0.6, \"arid_soil\": 0.4}", "{\"sand\": 0.6, \"arid_soil\": 0.3}", "out_of_range");
    refuse_materials("water_with_face", "\"water\": true, \"terrainType\": null", "\"water\": true, \"terrainType\": \"Plain\"", "out_of_range");
    refuse_materials("land_dune_face", "\"terrainType\": \"Desert\"", "\"terrainType\": \"Dune\"", "face_group_mismatch");
    refuse_materials("seasonal_ice", "\"battleClass\": \"cold\", \"colourGrade\": 120, \"snow\": \"permanent\"}", "\"battleClass\": \"cold\", \"colourGrade\": 120, \"snow\": \"seasonal\"}", "unmapped_category");
    refuse_materials("battle_class", "\"battleClass\": \"desert\"", "\"battleClass\": \"dunes\"", "unmapped_category");
    refuse_materials("grade", "\"colourGrade\": 20", "\"colourGrade\": 21", "out_of_range");
    refuse_materials("layer_member", "\"slot\": 0, \"name\": \"grass\",", "\"slot\": 0, \"extra\": 1, \"name\": \"grass\",", "unknown_field");
    refuse_materials("layer_gap", "\"slot\": 1, \"name\": \"steppe\"", "\"slot\": 2, \"name\": \"steppe\"", "out_of_range");
    refuse_materials("variants_retype", "\"generatedVariants\": \"visual\"", "\"generatedVariants\": \"retype\"", "unmapped_category");
}

void shaping() {
    const auto s = policy::load_shaping(policies / "terrain-shaping.json");
    check(s.stage == "afterTravelPlacement" && s.gates.interior_p95_deg == 45 && s.gates.interior_max_deg == 60 && s.gates.coast_p95_deg == 50 && s.gates.steep_pads_max == 10 &&
          s.resolution.side == 257 && s.resolution.fine && s.resolution.fine_side == 513 && s.coherence_order.size() == 4 && s.pads.radius.at("town") > s.pads.radius.at("village"),
          "terrain shaping: after placement, the section 15 gates, the 513-sample surface (MMR-249 go, MMR-278)");
    auto refuse = [&](const std::string& name, const std::string& from, const std::string& to, const std::string& code) {
        const auto file = mutate("terrain-shaping.json", name, from, to);
        refused("shaping policy " + name, code, [&] { policy::load_shaping(file); });
    };
    refuse("unknown_member", "\"version\": 1,", "\"version\": 1, \"extra\": 1,", "unknown_field");
    refuse("stage", "\"afterTravelPlacement\"", "\"beforeTravel\"", "unmapped_category");
    refuse("kernel", "\"kernel\": [1, 4, 1]", "\"kernel\": [1, 4]", "out_of_range");
    refuse("gate_order", "\"interiorSlopeP95Deg\": 45", "\"interiorSlopeP95Deg\": 70", "out_of_range");
}

void population() {
    const auto p = policy::load_population(policies / "population.json");
    int lords = 0;
    for (const auto& [v, n] : p.trait_joint) lords += n;
    check(p.base_fiefs == 133 && p.kingdoms_per_base == 9 && p.houses_per_base == 82 && p.companies_per_base == 12 && p.outlaws_per_base == 10 && p.wanderers_per_town == 0.6 &&
          p.kingdom_max_fiefs == 17, "population: one 133-fief base, War Sails ratios 9/82/12/10, 0.6 wanderers per town, the largest kingdom's 17 fiefs");
    check(p.mercenary.size() == 7 && p.mercenary.at("battania").source_clan == "wolfskins" && p.mercenary.at("khuzait").source_clan == "karakhuzaits" &&
          p.mercenary.at("nord").party_template == "kingdom_hero_party_mercenary_sturgia_template" && p.outlaw_lines.at("empire").size() == 3 && p.outlaw_lines.at("nord").size() == 1,
          "troop templates: a company line and outlaw lines for every presentation culture (RT-03)");
    check(lords == 443 && p.traits.size() == 5 && p.shared_trait_share > 0 && p.house_min_adults == 2 && p.house_each_sex && p.wanderer_templates.size() == 78,
          "lord traits from the 443 installed lords, houses with an adult of each sex, 78 wanderer templates");
    auto refuse = [&](const std::string& name, const std::string& from, const std::string& to, const std::string& code) {
        const auto file = mutate("population.json", name, from, to);
        refused("population policy " + name, code, [&] { policy::load_population(file); });
    };
    refuse("unknown_member", "\"version\": 1,", "\"version\": 1, \"extra\": 1,", "unknown_field");
    refuse("base_120", "\"fiefs\": 133,", "\"fiefs\": 120,", "out_of_range");
    refuse("lord_template", "\"partyTemplate\": \"kingdom_hero_party_mercenary_aserai_template\"", "\"partyTemplate\": \"kingdom_hero_party_aserai_template\"", "out_of_range");
    refuse("one_sex_houses", "\"eachSex\": true", "\"eachSex\": false", "out_of_range");
    refuse("regal_spill", "\"regalToCompany\": false", "\"regalToCompany\": true", "out_of_range");
    refuse("merge_order", "\"order\": [\n   \"civilizationLand\",\n   \"presentationProximity\",", "\"order\": [\n   \"presentationProximity\",\n   \"civilizationLand\",", "out_of_range");
}

void content() {
    const auto c = campaign::load_content(policies / "campaign-content.json");
    check(c.hero_roles.at("sovereign").bucket == "house" && c.hero_roles.at("warlord").bucket == "company" && c.hero_roles.at("champion").fallback_bucket == "wanderer" &&
          c.hero_roles.at("council").seat_buckets.at("magistrate") == "house" && c.hero_roles.at("reeve").bucket == "notable" && c.hero_roles.at("dread").bucket == "none" &&
          !c.cleaner.numerals && c.cleaner.strip_age_tag, "hero roles carry their population bucket; the name cleaner never uses numerals");
    const auto file = mutate("campaign-content.json", "bad_bucket", "\"bucket\": \"house\"", "\"bucket\": \"vassal\"");
    refused("content policy with an unknown bucket", "unmapped_category", [&] { campaign::load_content(file); });
    const auto cleaner = mutate("campaign-content.json", "cleaner_member", "\"numerals\": false,", "\"numerals\": false, \"extra\": 1,");
    refused("content policy cleaner with an unknown member", "unknown_field", [&] { campaign::load_content(cleaner); });
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 3) { std::cerr << "usage: map2_policy_tests POLICIES_DIR WORK_DIR\n"; return 2; }
    policies = argv[1]; work = argv[2];
    fs::create_directories(work);
    // mutated copies resolve the playability calibration relative to themselves, as the originals do
    spit(work / "calibration" / "naval-transitions" / "naval-transitions.json", slurp(policies / ".." / "calibration" / "naval-transitions" / "naval-transitions.json"));
    try {
        check(std::string(capabilities::kFormatsV2) == "R11", "one capability id, R11, covers cells v2, weights v2 and campaign v2");
        biome_table();
        shaping();
        population();
        content();
    } catch (const std::exception& e) { check(false, std::string("unexpected exception: ") + e.what()); }
    std::cout << checks - failures << "/" << checks << " checks passed\n";
    return failures == 0 ? 0 : 1;
}
