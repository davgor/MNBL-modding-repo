// The population pass (map epic 2, MMR-279; findings sections 16 and 17; docs/population.md). It runs inside travel::run after the
// village quota's final binding and plans, on the compiled campaign records, what campaign.json v2 carries:
//   kingdoms   FWGC++ realms merged toward round(9 F / 133) kingdoms: civilization and land border first, then presentation culture and
//              proximity (across water when needed), then a split above the policy maximum of fiefs (memberRealms, mergeSteps)
//   wars       about one starting war per kingdom with a neighbour (land border first, then nearest by sea)
//   houses     about 82 F / 133 noble houses seated on realm rulers, castellans, pretenders and magistrates, each owning 1..4 fiefs of its
//              kingdom, with FWGC++ regal people first and synthesized adult kin (spouses, siblings with a dead parent, adult children,
//              optional parented minors), 3..10 planned members and an adult of each sex
//   companies  12 F / 133 mercenary companies of 6..10, captained by remnant champions (then warlords, domain holders, synthesized)
//   outlaws    10 F / 133 outlaw clans of 4..6 from bandit and deserter bands and ruin camps, within the outlaw war guard
//   wanderers  W, the smallest integer not below the float32 0.6 x towns, rotating by civilization
//   census     every living FWGC++ hero in exactly one bucket or an instantiated notable (HeroFactory's caps applied), 0 unassigned
// F is the world's fiefs (towns + castles). Every draw is a population.json stream value, so a re-run is byte-identical.
#pragma once
#include <cstdint>
#include <map>
#include <string>
#include "worldbridge/campaign_compiler.hpp"
#include "worldbridge/policy_map2.hpp"
#include "worldbridge/projection.hpp"
#include "worldbridge/source_world.hpp"

namespace worldbridge::travel { struct Input; struct Result; }

namespace worldbridge::population {

// value(stream, version, prefix, entity, draw) = the first 8 bytes of
// sha256("mnbl.population-stream/1\n" + stream + "\n" + version + "\n" + prefix + "\n" + entity + "\n" + draw), big-endian.
std::uint64_t stream_value(const std::string& stream, int version, const std::string& prefix, const std::string& entity, int draw);
// W: the smallest integer not below the float32 product per_town x towns (War Sails' own rule, RT-04): 45 towns -> 28, 66 -> 40.
int wanderer_target(double per_town, int towns);
// Bannerlord years for a source age: the people's life-stage band (young, middle, old) maps linearly onto 20..34, 35..49 and 50..70,
// then clamps to [lo, hi]. -1 when the span or the age is unknown.
int bannerlord_age(const source::LifeSpan* span, std::int64_t age_years, const std::string& band, int lo, int hi);

// Where the settlements are: final points (world units, periodic in x with period width) and land groups (-1: none).
struct Geometry {
    double width = 0;
    std::map<std::string, projection::Point> point;
    std::map<std::string, int> group;
};
Geometry geometry_of(const travel::Input& input, const travel::Result& result);

// The pass. Refuses (json::Error) "population_unassigned" when a living FWGC++ hero ends in no bucket and "population_invalid" when a
// planned house, company or outlaw clan breaks the policy (an internal invariant), with the reason.
void plan(campaign::Compiled& compiled, const source::SourceWorld& world, const Geometry& geometry, const policy::Population& policy,
          const campaign::ContentPolicy& content, const campaign::CulturePolicy& cultures);

}  // namespace worldbridge::population
