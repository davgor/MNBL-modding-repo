#include "worldbridge/world_bundle.hpp"
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <algorithm>
#include <cctype>
#include <set>
#include "worldbridge/world_identity.hpp"

// The validation rules here are the ones the C# host (CampaignBootstrap/WorldDataBundle.cs) and tools/summarize_bundle.py
// apply; tools/test_bundle_parity.py runs one set of damaged bundles through all three (docs/world-bundle.md).
namespace worldbridge::bundle {
namespace {
using cjson::Value;
const std::uint64_t kMaxDocument = 64ull << 20;

const Value* member(const Value& object, const char* key) { return object.kind == Value::Kind::Object ? object.find(key) : nullptr; }
std::string text_of(const Value& object, const char* key) {
    const Value* v = member(object, key);
    return v && v->kind == Value::Kind::String ? v->text : std::string();
}
bool is_one(const Value& object, const char* key) { const Value* v = member(object, key); return v && v->kind == Value::Kind::Number && v->text == "1"; }
bool is_sha256(const std::string& s) { return s.size() == 64 && std::all_of(s.begin(), s.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }); }
// A non-negative integer token (no sign, fraction or exponent) that fits 64 bits.
bool count_of(const Value* v, std::uint64_t& out) {
    if (!v || v->kind != Value::Kind::Number || v->text.empty() || v->text.size() > 19 || !std::all_of(v->text.begin(), v->text.end(), [](char c) { return c >= '0' && c <= '9'; })) return false;
    out = std::stoull(v->text);
    return true;
}
fs::path native(const std::string& bundle_path) { return fs::path(std::u8string(bundle_path.begin(), bundle_path.end())); }
std::string relative_name(const fs::path& path, const fs::path& top) { const auto s = path.lexically_relative(top).generic_u8string(); return std::string(s.begin(), s.end()); }
DWORD attributes(const fs::path& path) { return GetFileAttributesW(path.c_str()); }
}  // namespace

Report validate(const fs::path& given, bool name_must_match) {
    Report r;
    auto problem = [&](const std::string& p) { r.problems.push_back(p); };
    try {
        fs::path directory = fs::absolute(given).lexically_normal();
        if (directory.filename().empty()) directory = directory.parent_path();   // a trailing separator names the same directory
        if (!fs::is_directory(directory)) { problem("not a bundle directory: " + json::utf8(given)); return r; }
        if (attributes(directory) & FILE_ATTRIBUTE_REPARSE_POINT) { problem("the bundle directory is a link"); return r; }
        const fs::path manifest_file = directory / "manifest.json", ready_file = directory / "DATA_READY";
        if (!fs::is_regular_file(manifest_file)) { problem("manifest.json is missing"); return r; }
        try { r.manifest = cjson::parse(manifest_file, kMaxDocument); }   // duplicate keys are refused here
        catch (const json::Error& e) { problem("manifest.json is not valid JSON: " + e.code + ": " + e.message); return r; }
        r.manifest_sha256 = cjson::sha256_file_hex(manifest_file);
        const Value& m = r.manifest;
        if (text_of(m, "schema") != "mnbl.world-bundle" || !is_one(m, "version")) { problem("manifest is not mnbl.world-bundle version 1"); return r; }
        r.world_content_id = text_of(m, "worldContentId");
        if (!is_sha256(r.world_content_id)) problem("manifest worldContentId is not a SHA-256");
        // DATA_READY: written last, it names this manifest and this content.
        if (!fs::is_regular_file(ready_file)) problem("DATA_READY is missing: the bundle was never sealed");
        else {
            Value ready;
            try { ready = cjson::parse(ready_file); }
            catch (const json::Error& e) { problem("DATA_READY is not valid JSON: " + e.code + ": " + e.message); }
            if (text_of(ready, "schema") != "mnbl.data-ready" || !is_one(ready, "version")) problem("DATA_READY is not mnbl.data-ready version 1");
            if (text_of(ready, "manifestSha256") != r.manifest_sha256) problem("DATA_READY names a different manifest");
            if (text_of(ready, "worldContentId") != r.world_content_id) problem("DATA_READY names a different worldContentId");
        }
        if (name_must_match && json::utf8(directory.filename()) != r.world_content_id) problem("the directory name is not the worldContentId");

        // Inventory: well-formed rows with safe paths, strictly sorted by UTF-8 bytes, and totals that add up.
        const Value* inventory = member(m, "inventory");
        if (!inventory || inventory->kind != Value::Kind::Array) { problem("manifest inventory is missing"); return r; }
        std::map<std::string, std::pair<std::uint64_t, std::string>> listed;
        std::string previous;
        std::uint64_t listed_bytes = 0;
        for (const auto& row : inventory->items) {
            const std::string path = text_of(row, "path"), sha = text_of(row, "sha256");
            std::uint64_t bytes = 0;
            if (path.empty() || !is_sha256(sha) || !count_of(member(row, "bytes"), bytes)) { problem("an inventory row is malformed"); continue; }
            if (!safe_bundle_path(path)) { problem("unsafe inventory path " + path); continue; }
            if (!previous.empty() && !(previous < path)) problem("inventory is not sorted and unique at " + path);
            previous = path;
            listed[path] = {bytes, sha};
            listed_bytes += bytes;
        }
        std::uint64_t total_files = 0, total_bytes = 0;
        const Value* totals = member(m, "totals");
        if (!totals || !count_of(member(*totals, "files"), total_files) || !count_of(member(*totals, "bytes"), total_bytes) ||
            total_files != inventory->items.size() || total_bytes != listed_bytes)
            problem("manifest totals do not match the inventory");

        // The files on disk are exactly the inventory (names compared exactly), with no links anywhere in the bundle.
        std::set<std::string> on_disk;
        for (auto it = fs::recursive_directory_iterator(directory); it != fs::recursive_directory_iterator(); ++it) {
            const DWORD a = attributes(it->path());
            const std::string rel = relative_name(it->path(), directory);
            if (a == INVALID_FILE_ATTRIBUTES) { problem("unreadable entry " + rel); continue; }
            if (a & FILE_ATTRIBUTE_REPARSE_POINT) {
                problem(std::string(a & FILE_ATTRIBUTE_DIRECTORY ? "linked directory " : "linked file ") + rel);
                if (a & FILE_ATTRIBUTE_DIRECTORY) it.disable_recursion_pending();
                continue;
            }
            if (!(a & FILE_ATTRIBUTE_DIRECTORY)) {
                std::error_code ec;
                if (fs::hard_link_count(it->path(), ec) != 1) problem("hard-linked file " + rel);
                on_disk.insert(rel);
            }
        }
        for (const auto& [path, expect] : listed) {
            if (!on_disk.count(path)) { problem("missing file " + path); continue; }
            const fs::path file = directory / native(path);
            if (fs::file_size(file) != expect.first) problem("size differs: " + path);
            else if (cjson::sha256_file_hex(file) != expect.second) problem("hash differs: " + path);
        }
        for (const auto& rel : on_disk) {
            if (rel == "manifest.json" || rel == "DATA_READY" || listed.count(rel)) continue;
            std::string lower = rel;
            std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            problem("unlisted file " + rel + (lower.find("receipt") != std::string::npos || rel.find("ACTIVATED") != std::string::npos ? " (activation receipts never live in a bundle)" : ""));
        }
        for (const char* root : {"source", "compiled", "policies"})
            if (!fs::is_directory(directory / root)) problem(std::string("missing root ") + root + "/");
        for (const auto& file : required_files())
            if (!listed.count(file)) problem("missing required file " + file);

        // WorldContentId from the verified rows: the same lines identity::world_content hashes.
        if (r.problems.empty()) {
            std::string text = "mnbl.world-content-id/1\n";
            for (const auto& [path, row] : listed) text += path + "\t" + std::to_string(row.first) + "\t" + row.second + "\n";
            if (cjson::sha256_hex(text) != r.world_content_id) problem("worldContentId does not match the files");
        }

        // Hashes the manifest names inside the bundle: every policy and the static validation report.
        const Value* policies = member(m, "policies");
        if (!policies || policies->kind != Value::Kind::Object) problem("manifest policies are missing");
        else for (const auto& [name, sha] : policies->members) {
            const std::string path = "policies/" + name + ".json";
            if (!safe_bundle_path(path) || !listed.count(path) || sha.kind != Value::Kind::String || listed.at(path).second != sha.text)
                problem("policy " + name + " does not match its manifest hash");
        }
        const Value* validation = member(m, "validation");
        if (!validation || text_of(*validation, "travelStatus") != "ok") problem("the static validation did not pass");
        else {
            const std::string path = text_of(*validation, "reportPath");
            if (!safe_bundle_path(path) || !listed.count(path) || listed.at(path).second != text_of(*validation, "reportSha256"))
                problem("the validation report does not match its manifest hash");
        }
        // Compatibility: the manifest repeats compiled/compatibility.json, which the WorldContentId covers.
        const Value* engine = member(m, "engine");
        const Value* capabilities = member(m, "requiredCapabilities");
        if (!engine || text_of(*engine, "gameVersion").empty() || text_of(*engine, "navalVersion").empty() || !capabilities || capabilities->kind != Value::Kind::Array)
            problem("manifest engine versions or requiredCapabilities are missing");
        else if (r.problems.empty()) {
            for (const auto& c : capabilities->items) {
                const std::string id = text_of(c, "id");
                if (id.size() != 3 || id[0] != 'R' || !std::isdigit(static_cast<unsigned char>(id[1])) || !std::isdigit(static_cast<unsigned char>(id[2])) || text_of(c, "name").empty())
                    problem("a required capability is malformed");
            }
            const Value compat = cjson::parse(directory / "compiled/compatibility.json", kMaxDocument);
            const Value* versions = member(compat, "engine");
            const Value* required = member(compat, "requiredCapabilities");
            if (text_of(compat, "schema") != "mnbl.bundle-compatibility" || !versions || !required || text_of(*versions, "gameVersion") != text_of(*engine, "gameVersion") ||
                text_of(*versions, "navalVersion") != text_of(*engine, "navalVersion") || cjson::canonical(*required) != cjson::canonical(*capabilities))
                problem("manifest engine or requiredCapabilities differ from compiled/compatibility.json");
        }
        // Cross-file identity: the travel outputs name the compiled files beside them.
        if (r.problems.empty()) {
            const Value graph = cjson::parse(directory / "compiled/travel/travel-graph.json", kMaxDocument);
            const Value ports = cjson::parse(directory / "compiled/travel/ports.json", kMaxDocument);
            if (text_of(graph, "status") != "ok") problem("the travel graph is not ok: a rejected world was bundled");
            const Value* in = member(graph, "inputs");
            auto expect = [&](const char* key, const char* file) {
                if (!in || text_of(*in, key) != listed.at(file).second) problem(std::string("travel-graph inputs.") + key + " does not match " + file);
            };
            expect("terrainMasksSha256", "compiled/terrain/masks.bin");
            expect("terrainHeightfieldSha256", "compiled/terrain/heightfield.bin");
            expect("idMapSha256", "compiled/campaign/id-map.json");
            expect("projectionSha256", "compiled/terrain/projection.json");
            const Value* g = member(graph, "grid");
            if (!g || text_of(*g, "cellsSha256") != listed.at("compiled/travel/cells.bin").second) problem("travel-graph cellsSha256 does not match cells.bin");
            const Value* pin = member(ports, "inputs");
            if (!pin || !in || cjson::canonical(*pin) != cjson::canonical(*in)) problem("ports.json inputs differ from travel-graph.json inputs");
        }
    } catch (const json::Error& e) {
        problem(e.code + ": " + e.message);
    } catch (const std::exception& e) {
        problem(e.what());
    }
    r.valid = r.problems.empty();
    return r;
}

std::vector<fs::path> list_ready(const fs::path& root) {
    std::vector<fs::path> out;
    if (!fs::is_directory(root)) return out;
    for (const auto& entry : fs::directory_iterator(root)) {
        if (!entry.is_directory()) continue;
        const std::string name = json::utf8(entry.path().filename());
        if (name.size() != 64 || !std::all_of(name.begin(), name.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); })) continue;   // staging and anything else
        if (fs::is_regular_file(entry.path() / "DATA_READY")) out.push_back(entry.path());
    }
    std::sort(out.begin(), out.end());
    return out;
}

}  // namespace worldbridge::bundle
