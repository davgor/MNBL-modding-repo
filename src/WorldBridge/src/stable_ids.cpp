#include "worldbridge/stable_ids.hpp"
#include "worldbridge/canonical_json.hpp"

namespace worldbridge::ids {
namespace {
json::Location nowhere() { return json::Location{"", 0, 1, 1, "$"}; }
}  // namespace

std::string world_prefix(const std::string& source_identity, const std::string& world_sha256) {
    if (source_identity.empty()) throw json::Error("missing_field", "A world prefix needs the source identity", nowhere());
    bool hex = world_sha256.size() == 64;
    for (char ch : world_sha256) hex = hex && ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
    if (!hex) throw json::Error("wrong_type", "A world prefix needs the world.json SHA-256 (64 lowercase hex digits)", nowhere());
    return "mnbl_" + cjson::sha256_hex("mnbl.world-prefix/2\n" + source_identity + "\n" + world_sha256).substr(0, 8);
}

std::string kind_code(const std::string& kind) {
    static const std::map<std::string, std::string> codes{{"town", "t"}, {"village", "v"}, {"castle", "c"}, {"kingdom", "k"}, {"clan", "l"}, {"hero", "h"}};
    const auto it = codes.find(kind);
    if (it == codes.end()) throw json::Error("unmapped_category", "No id kind '" + kind + "'", nowhere());
    return it->second;
}

Registry::Registry(std::string prefix, int hash_hex) : prefix_(std::move(prefix)), hex_(hash_hex) {
    if (hex_ < 1 || hex_ > 64) throw json::Error("out_of_range", "Id hash length must be 1..64 hex digits", nowhere());
    for (char ch : prefix_)
        if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_')) throw json::Error("out_of_range", "Id prefix must use [a-z0-9_]", nowhere());
}

const std::string& Registry::assign(const std::string& kind, const std::string& uid) {
    if (uid.empty()) throw json::Error("missing_field", "A " + kind + " has an empty source uid", nowhere());
    const auto key = std::make_pair(kind, uid);
    if (by_source_.count(key)) throw json::Error("duplicate_id", "Source " + kind + " '" + uid + "' appears twice", nowhere());
    const std::string id = prefix_ + "_" + kind_code(kind) + "_" + cjson::sha256_hex("mnbl.stable-id/1\n" + kind + "\n" + uid).substr(0, static_cast<std::size_t>(hex_));
    const auto taken = by_id_.find(id);
    if (taken != by_id_.end())
        throw json::Error("id_collision", "Id " + id + " of " + kind + " '" + uid + "' is already " + taken->second.first + " '" + taken->second.second + "'", nowhere());
    by_id_.emplace(id, key);
    return by_source_.emplace(key, id).first->second;
}

const std::string& Registry::at(const std::string& kind, const std::string& uid) const {
    const auto it = by_source_.find(std::make_pair(kind, uid));
    if (it == by_source_.end()) throw json::Error("broken_join", "No " + kind + " with source uid '" + uid + "'", nowhere());
    return it->second;
}

bool Registry::has(const std::string& kind, const std::string& uid) const { return by_source_.count(std::make_pair(kind, uid)) != 0; }

std::vector<Entry> Registry::entries() const {
    std::vector<Entry> out;
    for (const auto& [id, source] : by_id_) out.push_back({id, source.first, source.second});
    return out;
}

}  // namespace worldbridge::ids
