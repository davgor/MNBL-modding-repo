// Generation identity (worldgen/contracts/generation-request.schema.json).
//
//   RequestId          sha256 of the canonical request, including the campaign instance; names one session.
//   ConfigurationId    sha256 of the canonical request without campaignInstanceId and seedSource: equivalent
//                      configurations share it.
//   SourceInputId      per attempt: producer identity (lock file hash and pinned binary hash) + canonical recipe hash.
//   WorldContentId     sha256 of the sorted path/size/hash inventory of the exported source and compiled content.
//   CampaignInstanceId the host's UUID; keeps sessions apart, never proves different geography.
//
// Nothing here samples the clock or fresh randomness except new_request_seed(), which the host uses
// before a request is accepted (Random World).
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <string>
#include <utility>
#include <vector>
#include "worldbridge/canonical_json.hpp"

namespace worldbridge::identity {
namespace fs = std::filesystem;
using json::Error;

// The producer accepts request seeds 0..4294967295 (FWGC++ request_json.cpp; probed against the pinned
// binary by tools/Test-GenerationIdentity.ps1).
constexpr std::uint64_t max_seed = 4294967295ull;
constexpr int supported_recipe_version = 3;
constexpr int max_attempt_ceiling = 16;

struct GenerationRequest {
    std::string campaign_instance_id;   // lowercase RFC 4122 UUID text
    std::string seed_source;            // "explicit" | "random"
    std::uint32_t master_seed = 0;
    int recipe_version = 0;
    cjson::Value overrides;             // object, passed to the producer as-is (canonicalized)
    std::string producer_lock_sha256;
    std::map<std::string, std::string> policy_sha256;  // policy name -> sha256 of its file
    int max_attempts = 0;
    std::string canonical, request_id, configuration_id;
};

GenerationRequest load_request(const fs::path& file);
// Overrides are an object of scalar producer controls, none named seed or recipe_version, every number within 2^53.
void check_overrides(const cjson::Value& overrides);
// New generation only: the request must name the current producer lock and policy files exactly.
void verify_bindings(const GenerationRequest& request, const fs::path& lock_file, const std::map<std::string, fs::path>& policy_files);

// The exact UTF-8 bytes given to the producer for one attempt: canonical {"overrides","recipe_version","seed"}.
std::string recipe_bytes(const GenerationRequest& request, std::uint32_t seed);
std::string source_input_id(const std::string& lock_sha256, const std::string& producer_sha256, const std::string& recipe_sha256);

struct ContentInventory {
    std::string id;
    std::vector<std::string> lines;  // "<prefix>/<relative path>\t<size>\t<sha256>", sorted by bytes
    std::uint64_t files = 0, bytes = 0;
};
// roots: (prefix, directory) pairs, e.g. {"source", split}, {"compiled", bundle}.
ContentInventory world_content(const std::vector<std::pair<std::string, fs::path>>& roots);

// Host-side request construction (Seed / Random World). A random seed comes from the OS CSPRNG.
std::uint32_t new_request_seed();
std::string build_request(const std::string& campaign_instance_id, const std::string& seed_source, std::uint32_t seed, const cjson::Value& overrides,
                          const std::string& lock_sha256, const std::map<std::string, std::string>& policy_sha256, int max_attempts);
bool valid_uuid(const std::string& text);

}  // namespace worldbridge::identity
