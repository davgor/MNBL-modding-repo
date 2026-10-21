#include "worldbridge/name_cleaner.hpp"
#include <algorithm>
#include <cctype>
#include <stdexcept>

namespace worldbridge::names {
namespace {
bool space(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r'; }
bool digit(char c) { return c >= '0' && c <= '9'; }
char lower(char c) { return static_cast<char>(std::tolower(static_cast<unsigned char>(c))); }

// Collapses runs of whitespace to one space and trims both ends.
std::string squeeze(const std::string& text) {
    std::string out;
    for (char c : text) {
        if (space(c)) { if (!out.empty() && out.back() != ' ') out += ' '; }
        else out += c;
    }
    while (!out.empty() && out.back() == ' ') out.pop_back();
    return out;
}
// The length of an "(Age N)" tag starting at `at` (case-insensitive, inner spaces allowed), or 0.
std::size_t tag_at(const std::string& s, std::size_t at) {
    if (s[at] != '(') return 0;
    std::size_t i = at + 1;
    while (i < s.size() && space(s[i])) ++i;
    static const char word[] = "age";
    for (int k = 0; k < 3; ++k, ++i) if (i >= s.size() || lower(s[i]) != word[k]) return 0;
    if (i >= s.size() || !space(s[i])) return 0;
    while (i < s.size() && space(s[i])) ++i;
    const std::size_t digits = i;
    while (i < s.size() && digit(s[i])) ++i;
    if (i == digits) return 0;
    while (i < s.size() && space(s[i])) ++i;
    if (i >= s.size() || s[i] != ')') return 0;
    return i + 1 - at;
}
const std::vector<std::string>& neutral_roots() {
    static const std::vector<std::string> roots{"dun", "fen", "hal", "kor", "mar", "tor", "ven", "sel"};
    return roots;
}
}  // namespace

std::string strip_age_tag(const std::string& name) {
    std::string out;
    for (std::size_t i = 0; i < name.size();) {
        // The tag goes with the whitespace before it: "Selweg (Age 1)." -> "Selweg.", "Dunmar (age 2)mark" -> "Dunmarmark".
        if (const auto n = tag_at(name, i)) { while (!out.empty() && space(out.back())) out.pop_back(); i += n; continue; }
        out += name[i++];
    }
    return squeeze(out);
}

bool has_age_tag(const std::string& name) {
    for (std::size_t i = 0; i < name.size(); ++i) if (tag_at(name, i)) return true;
    return false;
}

bool has_numeral(const std::string& name) {
    static const std::set<std::string> roman{"ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x", "xi", "xii", "xiii", "xiv", "xv", "xvi", "xvii", "xviii", "xix", "xx"};
    std::string word;
    auto check = [&]() {
        bool numeral = false;
        for (char c : word) if (digit(c)) numeral = true;
        std::string l; for (char c : word) l += lower(c);
        const bool result = numeral || (roman.count(l) && word != l);   // "V" or "IV" as a word; a lower-case "v" is not a numeral
        word.clear();
        return result;
    };
    for (char c : name) {
        if (space(c) || c == ',' || c == '-' || c == '(' || c == ')') { if (!word.empty() && check()) return true; }
        else word += c;
    }
    return !word.empty() && check();
}

std::string capitalized(const std::string& word) {
    std::string out = word;
    if (!out.empty()) out[0] = static_cast<char>(std::toupper(static_cast<unsigned char>(out[0])));
    return out;
}

std::string compound(const std::vector<std::string>& roots) {
    std::string out;
    for (const auto& r : roots) out += r;
    std::string l; for (char c : out) l += lower(c);
    return capitalized(l);
}

std::string root_of(const Heritage& heritage, const std::string& concept_name) {
    for (const auto& entry : heritage.lexicon) if (entry.first == concept_name) return entry.second;
    return "";
}

std::uint64_t salt_of(const std::string& text) {
    std::uint64_t h = 14695981039346656037ull;
    for (const unsigned char c : text) { h ^= c; h *= 1099511628211ull; }
    return h;
}

std::string house_name(const std::string& pattern, const std::string& root) {
    const std::string r = strip_age_tag(root);
    if (pattern == "given_plus_clan") return "Clan " + r;
    if (pattern == "given_plus_father") return "Line of " + r;
    if (pattern == "given_plus_workshop") return "Workshop of " + r;
    if (pattern == "given_plus_cohort") return r + " Cohort";
    if (pattern == "given_plus_vessel") return "Crew of the " + r;
    return "House " + r;
}

std::string person_name(const std::string& pattern, const std::string& given, const std::string& root, const std::string& parent_given, bool female) {
    const std::string g = strip_age_tag(given), r = strip_age_tag(root), p = strip_age_tag(parent_given);
    if (pattern == "given_plus_father" && !p.empty()) return g + " " + p + (female ? "sdottir" : "sson");
    if (pattern == "given_plus_vessel" && !r.empty()) return g + " of the " + r;
    return r.empty() ? g : g + " " + r;
}

std::string Unique::take(const std::string& wanted, const Heritage& heritage, std::uint64_t salt) {
    const std::string base = policy_.strip_age_tag ? strip_age_tag(wanted) : squeeze(wanted);
    if (used_.insert(base).second) return base;
    std::vector<std::string> roots;
    for (const auto& entry : heritage.lexicon) if (!entry.second.empty()) roots.push_back(entry.second);
    if (roots.empty()) roots = neutral_roots();
    const std::size_t n = roots.size(), start = static_cast<std::size_t>(salt % n);
    auto suffixed = [&](const std::string& suffix) {
        std::string l; for (char c : suffix) l += lower(c);
        return base + l;   // appended to the last word
    };
    for (std::size_t i = 0; i < n; ++i) {
        const auto candidate = suffixed(roots[(start + i) % n]);
        if (used_.insert(candidate).second) return candidate;
    }
    for (std::size_t i = 0; i < n; ++i)
        for (std::size_t j = 0; j < n; ++j) {
            const auto candidate = suffixed(roots[(start + i) % n] + roots[(start + j) % n]);
            if (used_.insert(candidate).second) return candidate;
        }
    throw std::runtime_error("No unique name left for '" + base + "'");
}

std::string Unique::coin(const Heritage& heritage, const std::vector<std::string>& concepts, std::uint64_t salt) {
    std::vector<std::string> all, kind;
    for (const auto& entry : heritage.lexicon) if (!entry.second.empty() && std::find(all.begin(), all.end(), entry.second) == all.end()) all.push_back(entry.second);
    if (all.empty()) all = neutral_roots();
    for (const auto& c : concepts) { const auto r = root_of(heritage, c); if (!r.empty() && std::find(kind.begin(), kind.end(), r) == kind.end()) kind.push_back(r); }
    if (kind.empty()) kind = all;
    const std::size_t n = all.size(), m = kind.size(), a0 = static_cast<std::size_t>(salt % n), k0 = static_cast<std::size_t>((salt >> 32) % m);
    auto free = [&](const std::string& candidate) { return !has_numeral(candidate) && used_.insert(candidate).second; };
    for (std::size_t i = 0; i < m; ++i)
        for (std::size_t j = 0; j < n; ++j) {
            const auto& k = kind[(k0 + i) % m]; const auto& a = all[(a0 + j) % n];
            if (a == k) continue;
            const auto candidate = compound({a, k});
            if (free(candidate)) return candidate;
        }
    for (std::size_t i = 0; i < m; ++i)
        for (std::size_t j = 0; j < n; ++j)
            for (std::size_t l = 0; l < n; ++l) {
                const auto candidate = compound({all[(a0 + j) % n], all[(a0 + l + 1) % n], kind[(k0 + i) % m]});
                if (free(candidate)) return candidate;
            }
    throw std::runtime_error("No unique place name left");
}

}  // namespace worldbridge::names
