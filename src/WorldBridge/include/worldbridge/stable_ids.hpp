// Stable Bannerlord identifiers (MMR-27): source UID plus object kind -> a deterministic, bounded, unique game id.
//
//   id = <world prefix> "_" <kind code> "_" hex(sha256("mnbl.stable-id/1\n" kind "\n" uid))[0:10]
//   world prefix = "mnbl_" hex(sha256("mnbl.world-prefix/2\n" sourceIdentity "\n" worldSha256))[0:8]
//
// sourceIdentity is the source identity document (source::write_identity: schema, stage, generator version, grid side
// and seeds) and worldSha256 the SHA-256 of the split's world.json, so two different worlds get different prefixes
// even when they share a seed (sizes 33..1025 of seed 42 all do). The 8-hex prefix separates worlds; it is not a proof
// of uniqueness, and campaign scoping proper is MMR-30. Ids use only [a-z0-9_], are at most 26 characters, and never
// depend on display names. A second (kind, uid) that maps to an id already taken is refused ("id_collision"), never
// renamed silently; the same (kind, uid) twice is refused too.
#pragma once
#include <map>
#include <string>
#include <utility>
#include <vector>
#include "worldbridge/json_stream.hpp"

namespace worldbridge::ids {

std::string world_prefix(const std::string& source_identity, const std::string& world_sha256);
// The short code of an object kind: town t, village v, castle c, kingdom k, clan l, hero h.
std::string kind_code(const std::string& kind);

struct Entry { std::string id, kind, uid; };

class Registry {
public:
    explicit Registry(std::string prefix, int hash_hex = 10);
    // Assigns (once) the id of a source UID of a kind; refuses a repeated (kind, uid) and any collision.
    const std::string& assign(const std::string& kind, const std::string& uid);
    // The id already assigned to (kind, uid); refuses an unknown pair ("broken_join").
    const std::string& at(const std::string& kind, const std::string& uid) const;
    bool has(const std::string& kind, const std::string& uid) const;
    std::vector<Entry> entries() const;  // sorted by id
    const std::string& prefix() const { return prefix_; }

private:
    std::string prefix_;
    int hex_;
    std::map<std::pair<std::string, std::string>, std::string> by_source_;
    std::map<std::string, std::pair<std::string, std::string>> by_id_;
};

}  // namespace worldbridge::ids
