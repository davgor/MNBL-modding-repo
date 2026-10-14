#include "worldbridge/world_identity.hpp"

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <set>
#include <stdexcept>

#pragma comment(lib, "bcrypt.lib")

namespace worldbridge::identity {
namespace {
using cjson::Value;

[[noreturn]] void refuse(const std::string& code, const std::string& message, const json::Location& at) { throw Error(code, message, at); }

bool lower_hex(const std::string& text, std::size_t length) {
    if (text.size() != length) return false;
    for (char c : text) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
    return true;
}

std::uint64_t unsigned_integer(const Value& v, const char* what, std::uint64_t maximum) {
    if (v.kind != Value::Kind::Number) refuse("wrong_type", std::string(what) + " must be an integer", v.at);
    std::uint64_t n = 0;
    const auto r = std::from_chars(v.text.data(), v.text.data() + v.text.size(), n);
    if (r.ec != std::errc() || r.ptr != v.text.data() + v.text.size()) refuse("wrong_type", std::string(what) + " must be a non-negative integer", v.at);
    if (n > maximum) refuse("out_of_range", std::string(what) + " " + v.text + " exceeds " + std::to_string(maximum), v.at);
    return n;
}

const Value& member(const Value& object, const char* key) {
    const Value* v = object.find(key);
    if (!v) refuse("missing_field", std::string("Request lacks '") + key + "'", object.at);
    return *v;
}

std::string text(const Value& v, const char* what) {
    if (v.kind != Value::Kind::String) refuse("wrong_type", std::string(what) + " must be a string", v.at);
    return v.text;
}
// Exact test of |x| > 2^53 on a JSON number token, whatever its spelling (9007199254740993, 9007199254740993.0,
// 9.007199254740993e15): 2^53 + 1 rounds to 2^53 as a double, so a floating comparison cannot see it.
bool beyond_2_53(const std::string& token) {
    std::size_t i = token[0] == '-' ? 1 : 0;
    std::string digits;
    for (; i < token.size() && token[i] >= '0' && token[i] <= '9'; ++i) digits += token[i];
    long long point = static_cast<long long>(digits.size());  // value = 0.<digits> x 10^point
    if (i < token.size() && token[i] == '.')
        for (++i; i < token.size() && token[i] >= '0' && token[i] <= '9'; ++i) digits += token[i];
    if (i < token.size() && (token[i] == 'e' || token[i] == 'E')) {
        long long exponent = std::strtoll(token.c_str() + i + 1, nullptr, 10);
        point += std::max(-100000LL, std::min(100000LL, exponent));
    }
    const auto first = digits.find_first_not_of('0');
    if (first == std::string::npos) return false;
    digits.erase(0, first);
    point -= static_cast<long long>(first);
    static const std::string limit = "9007199254740992";  // 2^53, 16 digits
    if (point != static_cast<long long>(limit.size())) return point > static_cast<long long>(limit.size());
    std::string head = digits.substr(0, limit.size());
    head.append(limit.size() - head.size(), '0');
    if (head != limit) return head > limit;
    return digits.find_first_not_of('0', limit.size()) != std::string::npos;
}
}  // namespace

void check_overrides(const cjson::Value& overrides) {
    if (overrides.kind != Value::Kind::Object) refuse("wrong_type", "overrides must be an object", overrides.at);
    for (const auto& [name, value] : overrides.members) {
        if (name.empty() || name == "seed" || name == "recipe_version") refuse("invalid_value", "Override name '" + name + "' is not a control", value.at);
        if (value.kind == Value::Kind::Array || value.kind == Value::Kind::Object || value.kind == Value::Kind::Null) refuse("wrong_type", "Override '" + name + "' must be a scalar", value.at);
        // RFC 8785 numbers are IEEE doubles: beyond 2^53 a number cannot be canonicalized without changing its value.
        if (value.kind == Value::Kind::Number && beyond_2_53(value.text))
            refuse("out_of_range", "Override '" + name + "' exceeds 2^53 in magnitude and cannot be represented exactly", value.at);
    }
}

bool valid_uuid(const std::string& t) {
    if (t.size() != 36) return false;
    for (std::size_t i = 0; i < t.size(); ++i) {
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        if (dash ? t[i] != '-' : !((t[i] >= '0' && t[i] <= '9') || (t[i] >= 'a' && t[i] <= 'f'))) return false;
    }
    return t[14] >= '1' && t[14] <= '8' && (t[19] == '8' || t[19] == '9' || t[19] == 'a' || t[19] == 'b');  // RFC 4122/9562 version and variant
}

GenerationRequest load_request(const fs::path& file) {
    const Value root = cjson::parse(file, 64 * 1024);
    if (root.kind != Value::Kind::Object) refuse("wrong_type", "Request must be an object", root.at);
    static const std::set<std::string> allowed{"schema", "version", "campaignInstanceId", "seedSource", "masterSeed", "recipeVersion", "overrides",
                                               "producerLockSha256", "policies", "maxAttempts"};
    for (const auto& [key, value] : root.members)
        if (!allowed.count(key)) refuse("unexpected_member", "Unknown request member '" + key + "'", value.at);
    if (text(member(root, "schema"), "schema") != "mnbl.generation-request") refuse("unsupported_schema", "Not an mnbl.generation-request", member(root, "schema").at);
    if (unsigned_integer(member(root, "version"), "version", 1000) != 1) refuse("unsupported_version", "Request version must be 1", member(root, "version").at);
    GenerationRequest r;
    r.campaign_instance_id = text(member(root, "campaignInstanceId"), "campaignInstanceId");
    if (!valid_uuid(r.campaign_instance_id)) refuse("invalid_value", "campaignInstanceId must be a lowercase RFC 4122 UUID", member(root, "campaignInstanceId").at);
    r.seed_source = text(member(root, "seedSource"), "seedSource");
    if (r.seed_source != "explicit" && r.seed_source != "random") refuse("invalid_value", "seedSource must be 'explicit' or 'random'", member(root, "seedSource").at);
    r.master_seed = static_cast<std::uint32_t>(unsigned_integer(member(root, "masterSeed"), "masterSeed", max_seed));
    r.recipe_version = static_cast<int>(unsigned_integer(member(root, "recipeVersion"), "recipeVersion", 1000));
    if (r.recipe_version != supported_recipe_version) refuse("unsupported_version", "recipeVersion must be 3", member(root, "recipeVersion").at);
    r.overrides = member(root, "overrides");
    check_overrides(r.overrides);
    r.producer_lock_sha256 = text(member(root, "producerLockSha256"), "producerLockSha256");
    if (!lower_hex(r.producer_lock_sha256, 64)) refuse("invalid_value", "producerLockSha256 must be 64 lowercase hex digits", member(root, "producerLockSha256").at);
    const Value& policies = member(root, "policies");
    if (policies.kind != Value::Kind::Object || policies.members.empty()) refuse("wrong_type", "policies must be a non-empty object of name -> sha256", policies.at);
    for (const auto& [name, value] : policies.members) {
        const std::string hash = text(value, "policy hash");
        if (!lower_hex(hash, 64)) refuse("invalid_value", "Policy '" + name + "' hash must be 64 lowercase hex digits", value.at);
        r.policy_sha256[name] = hash;
    }
    r.max_attempts = static_cast<int>(unsigned_integer(member(root, "maxAttempts"), "maxAttempts", max_attempt_ceiling));
    if (r.max_attempts < 1) refuse("out_of_range", "maxAttempts must be at least 1", member(root, "maxAttempts").at);

    r.canonical = cjson::canonical(root);
    r.request_id = cjson::sha256_hex("mnbl.request-id/1 mnbl.canonical-json/1\n" + r.canonical);
    Value configuration = Value::object();
    for (const auto& [key, value] : root.members)
        if (key != "campaignInstanceId" && key != "seedSource") configuration.members.emplace_back(key, value);
    r.configuration_id = cjson::sha256_hex("mnbl.configuration-id/1 mnbl.canonical-json/1\n" + cjson::canonical(configuration));
    return r;
}

void verify_bindings(const GenerationRequest& r, const fs::path& lock_file, const std::map<std::string, fs::path>& policy_files) {
    const json::Location at{json::utf8(lock_file), 0, 1, 1, "$"};
    const std::string lock = cjson::sha256_file_hex(lock_file);
    if (lock != r.producer_lock_sha256) refuse("producer_changed", "The producer lock is " + lock + ", the request was accepted for " + r.producer_lock_sha256, at);
    for (const auto& [name, hash] : r.policy_sha256) {
        const auto file = policy_files.find(name);
        if (file == policy_files.end()) refuse("policy_changed", "No policy file supplied for '" + name + "'", at);
        const std::string actual = cjson::sha256_file_hex(file->second);
        if (actual != hash) refuse("policy_changed", "Policy '" + name + "' is " + actual + ", the request was accepted for " + hash, json::Location{json::utf8(file->second), 0, 1, 1, "$"});
    }
    for (const auto& [name, path] : policy_files)
        if (!r.policy_sha256.count(name)) refuse("policy_changed", "Policy '" + name + "' is not bound by the request", json::Location{json::utf8(path), 0, 1, 1, "$"});
}

std::string recipe_bytes(const GenerationRequest& r, std::uint32_t seed) {
    Value recipe = Value::object();
    recipe.set("overrides", r.overrides);
    recipe.set("recipe_version", Value::integer(static_cast<std::uint64_t>(r.recipe_version)));
    recipe.set("seed", Value::integer(seed));
    return cjson::canonical(recipe);
}

std::string source_input_id(const std::string& lock_sha256, const std::string& producer_sha256, const std::string& recipe_sha256) {
    return cjson::sha256_hex("mnbl.source-input-id/1\nproducer-lock " + lock_sha256 + "\nproducer " + producer_sha256 + "\nrecipe " + recipe_sha256 + "\n");
}

ContentInventory world_content(const std::vector<std::pair<std::string, fs::path>>& roots) {
    ContentInventory inventory;
    for (const auto& [prefix, root] : roots) {
        if (!fs::is_directory(root)) throw Error("missing_file", "Content root '" + prefix + "' is not a directory", json::Location{json::utf8(root), 0, 1, 1, "$"});
        for (const auto& entry : fs::recursive_directory_iterator(root)) {
            // Exported content is plain files and directories; links of any kind (symlinks, junctions, hard links) are refused.
            const DWORD attributes = GetFileAttributesW(entry.path().c_str());
            std::error_code ec;
            if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT) || entry.is_symlink() ||
                (entry.is_regular_file() && fs::hard_link_count(entry.path(), ec) != 1))
                throw Error("unsafe_path", "Content contains a link or unreadable entry: " + prefix + "/" + json::utf8(entry.path().lexically_relative(root)),
                            json::Location{json::utf8(entry.path()), 0, 1, 1, "$"});
            if (!entry.is_regular_file()) continue;  // directories (including empty ones) carry no content of their own
            const std::string relative = json::utf8(entry.path().lexically_relative(root));
            const auto size = entry.file_size();
            inventory.lines.push_back(prefix + "/" + relative + "\t" + std::to_string(size) + "\t" + cjson::sha256_file_hex(entry.path()));
            inventory.files++;
            inventory.bytes += size;
        }
    }
    std::sort(inventory.lines.begin(), inventory.lines.end());
    std::string text = "mnbl.world-content-id/1\n";
    for (const auto& line : inventory.lines) text += line + "\n";
    inventory.id = cjson::sha256_hex(text);
    return inventory;
}

std::uint32_t new_request_seed() {
    std::uint32_t seed = 0;
    if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&seed), sizeof(seed), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) throw std::runtime_error("OS random generator unavailable");
    return seed;
}

std::string build_request(const std::string& instance, const std::string& seed_source, std::uint32_t seed, const cjson::Value& overrides,
                          const std::string& lock_sha256, const std::map<std::string, std::string>& policies, int max_attempts) {
    check_overrides(overrides);  // before canonicalization, which would round a number beyond 2^53
    if (!valid_uuid(instance)) throw Error("invalid_value", "campaignInstanceId must be a lowercase RFC 4122 UUID", overrides.at);
    Value r = Value::object();
    r.set("schema", Value::string("mnbl.generation-request"));
    r.set("version", Value::integer(1));
    r.set("campaignInstanceId", Value::string(instance));
    r.set("seedSource", Value::string(seed_source));
    r.set("masterSeed", Value::integer(seed));
    r.set("recipeVersion", Value::integer(supported_recipe_version));
    r.set("overrides", overrides);
    r.set("producerLockSha256", Value::string(lock_sha256));
    Value p = Value::object();
    for (const auto& [name, hash] : policies) p.set(name, Value::string(hash));
    r.set("policies", p);
    r.set("maxAttempts", Value::integer(static_cast<std::uint64_t>(max_attempts)));
    return cjson::canonical(r);
}

}  // namespace worldbridge::identity
