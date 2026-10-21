// The shared name cleaner (map epic 2; MMR-279 adds it for houses, companies and outlaw clans, MMR-286 applies it to every
// other display name). Policy: worldgen/policies/campaign-content.json names.cleaner. Three operations:
//   - strip the FWGC++ "(Age N)" tag, anywhere in a name and in any case ("Selweg (Age 1)" -> "Selweg");
//   - build names in a culture's FWGC++ naming pattern (civilizations.entities[].heritage.culture.household.naming_pattern)
//     from lexicon roots (heritage.genome.lexicon) and given names;
//   - make names unique per kind by a lexicon suffix on the last word ("House Torbarg" -> "House Torbargkor"), never by numerals.
// MMR-286 adds place names: a village, castle or nameless town is named FWGC++-style, a compound of two lexicon roots of its
// civilization (a root of the place's kind concepts, campaign-content.json names.places, after a root of the whole lexicon:
// "old" + "field" -> "Eldfen"), unique among all settlements without numerals.
// Everything is a pure function of its inputs, so a re-run gives the same names.
#pragma once
#include <cstdint>
#include <set>
#include <string>
#include <utility>
#include <vector>

namespace worldbridge::names {

struct Policy { bool strip_age_tag = true, numerals = false, keep_source_name = true; std::string dedupe = "lexiconSuffix"; };

// One civilization's naming heritage. lexicon: concept -> root, sorted by concept (the source object's member order is not relied on).
struct Heritage {
    std::string pattern;   // given_plus_house | given_plus_clan | given_plus_father | given_plus_workshop | given_plus_cohort | given_plus_vessel
    std::vector<std::pair<std::string, std::string>> lexicon;
};

// "(Age N)" removed wherever it stands, whitespace collapsed and trimmed: "Dunmar (age 2)mark" -> "Dunmarmark".
std::string strip_age_tag(const std::string& name);
bool has_age_tag(const std::string& name);
// A word holding a digit ("Fields 2", "2nd") or a roman ordinal word (II..XX) counts as a numeral.
bool has_numeral(const std::string& name);
// First letter upper-cased (ASCII), the rest unchanged.
std::string capitalized(const std::string& word);
// Roots joined into one capitalized word: {"kor", "brand"} -> "Korbrand".
std::string compound(const std::vector<std::string>& roots);
// The lexicon root of a concept ("field" -> "fen"), or empty.
std::string root_of(const Heritage& heritage, const std::string& concept_name);
// A stable 64-bit salt of a text (FNV-1a), for the rotations below when no policy stream applies.
std::uint64_t salt_of(const std::string& text);

// A family name in the naming pattern from a root (a founder's given name or a lexicon compound):
// house "House R", clan "Clan R", father "Line of R", workshop "Workshop of R", cohort "R Cohort", vessel "Crew of the R".
// An unknown or empty pattern uses the house form.
std::string house_name(const std::string& pattern, const std::string& root);
// A synthesized person's name in the pattern: given + family part. house, clan, workshop, cohort: "G R"; father: "G Psson" /
// "G Psdottir" (P the parent's given name); vessel: "G of the R".
std::string person_name(const std::string& pattern, const std::string& given, const std::string& root, const std::string& parent_given, bool female);

// Unique display names of one kind (for example every clan: houses, mercenary companies and outlaw clans together).
class Unique {
public:
    explicit Unique(Policy policy) : policy_(std::move(policy)) {}
    // The cleaned wanted name if free, else the first free name that appends one lexicon root (then two) to its last word, roots
    // tried from the salt's rotation of the lexicon. An empty lexicon uses a small neutral root list. Refuses (std::runtime_error)
    // only when every candidate is taken.
    std::string take(const std::string& wanted, const Heritage& heritage, std::uint64_t salt);
    // A new place name (MMR-286): compound({a, k}) for k a root of the kind's concepts (in the given order, rotated by the salt) and a
    // any other lexicon root (rotated by the salt), the first free one that is no numeral; then three roots {a, b, k}. Concepts the
    // lexicon lacks are skipped; with none left (or an empty lexicon) every root, or the neutral list, is the kind's. Refuses
    // (std::runtime_error) only when every candidate is taken.
    std::string coin(const Heritage& heritage, const std::vector<std::string>& concepts, std::uint64_t salt);
    void reserve(const std::string& name) { used_.insert(name); }
    bool taken(const std::string& name) const { return used_.count(name) != 0; }
private:
    Policy policy_;
    std::set<std::string> used_;
};

}  // namespace worldbridge::names
