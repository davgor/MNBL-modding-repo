#include "worldbridge/world_bundle.hpp"
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <algorithm>
#include <cctype>
#include <chrono>
#include <exception>
#include <fstream>
#include <iostream>
#include <set>
#include <sstream>
#include <string_view>
#include "worldbridge/projected_world.hpp"
#include "worldbridge/projection.hpp"
#include "worldbridge/source_world.hpp"
#include "worldbridge/world_identity.hpp"
#pragma comment(lib, "bcrypt.lib")

#ifndef WORLDBRIDGE_VERSION
#define WORLDBRIDGE_VERSION "unknown"
#endif

namespace worldbridge::bundle {

bool safe_bundle_path(const std::string& p) {
    static const std::set<std::string> roots{"source", "compiled", "policies"};
    static const std::set<std::string> devices{"con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
                                               "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"};
    const auto slash = p.find('/');
    if (p.size() > kMaxBundlePath || slash == std::string::npos || !roots.count(p.substr(0, slash)) || p.back() == '/') return false;
    std::stringstream parts(p);
    for (std::string part; std::getline(parts, part, '/');) {
        if (part.empty() || part == "." || part == ".." || part.back() == '.' || part.back() == ' ') return false;
        for (const unsigned char c : part)
            if (c < 0x20 || std::string_view("<>:\"|?*\\").find(static_cast<char>(c)) != std::string_view::npos) return false;
        std::string stem = part.substr(0, part.find('.'));
        for (char& c : stem) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
        if (devices.count(stem)) return false;
    }
    return true;
}

const std::vector<std::string>& required_files() {
    static const std::vector<std::string> files{
        "compiled/campaign/campaign.json", "compiled/campaign/id-map.json", "compiled/compatibility.json", "compiled/terrain/height-q16.bin",
        "compiled/terrain/heightfield.bin", "compiled/terrain/masks.bin", "compiled/terrain/projection.json", "compiled/terrain/weights.bin",
        "compiled/travel/cells.bin", "compiled/travel/ports.json", "compiled/travel/travel-graph.json", "compiled/validation.json", "source/world.json"};
    return files;
}

const std::vector<std::string>& provenance_members() {
    static const std::vector<std::string> members{"source.recipe", "source.producer", "compiler", "engine.runtimeLockSha256"};
    return members;
}

namespace {
using cjson::Value;
using json::Error;

json::Location nowhere() { return json::Location{"", 0, 1, 1, "$"}; }
[[noreturn]] void refuse(const std::string& code, const std::string& message) { throw Error(code, message, nowhere()); }

Value str(const std::string& s) { return Value::string(s); }
Value integer(long long v) { Value out; out.kind = Value::Kind::Number; out.text = std::to_string(v); return out; }
Value num(double v) { Value out; out.kind = Value::Kind::Number; out.text = cjson::ecmascript_number(v); return out; }
Value array() { Value out; out.kind = Value::Kind::Array; return out; }
Value null() { return Value(); }

std::string random_hex() {
    unsigned char bytes[8];
    if (BCryptGenRandom(nullptr, bytes, sizeof(bytes), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) throw std::runtime_error("OS random generator unavailable");
    static const char* digits = "0123456789abcdef";
    std::string out;
    for (unsigned char b : bytes) { out += digits[b >> 4]; out += digits[b & 15]; }
    return out;
}

fs::path native(const std::string& bundle_path) { return fs::path(std::u8string(bundle_path.begin(), bundle_path.end())); }

// Written once, flushed to disk before the next step: a crash after this point never leaves a torn file on its path.
void write_flushed(const fs::path& file, const std::string& bytes) {
    fs::create_directories(file.parent_path());
    HANDLE h = CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create " + json::utf8(file));
    DWORD written = 0;
    bool ok = bytes.empty() || (WriteFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) && written == bytes.size());
    ok = ok && FlushFileBuffers(h);
    CloseHandle(h);
    if (!ok) throw std::runtime_error("Cannot write " + json::utf8(file));
}

std::string read_all(const fs::path& file) {
    std::ifstream in(file, std::ios::binary);
    if (!in) throw std::runtime_error("Cannot read " + json::utf8(file));
    std::ostringstream s; s << in.rdbuf();
    return s.str();
}

void check_path(const std::string& p) {
    if (!safe_bundle_path(p)) refuse("invalid_path", "Unsafe bundle path (outside source/, compiled/ and policies/, longer than " + std::to_string(kMaxBundlePath) + " bytes, or with a segment Windows cannot hold): " + p);
}

std::string exe_sha256() {
    wchar_t buffer[32768];
    const DWORD n = GetModuleFileNameW(nullptr, buffer, 32768);
    if (n == 0 || n >= 32768) refuse("missing_file", "Cannot locate the worldbridge binary to record its hash");
    return cjson::sha256_file_hex(fs::path(std::wstring(buffer, n)));
}

// The manifest without its provenance members (provenance_members), canonical: what must be equal for reuse.
std::map<std::string, std::string> comparable(const Value& manifest) {
    Value m = manifest;
    for (const auto& member : provenance_members()) {
        const auto dot = member.find('.');
        auto erase = [](Value& object, const std::string& key) {
            object.members.erase(std::remove_if(object.members.begin(), object.members.end(), [&](const auto& kv) { return kv.first == key; }), object.members.end());
        };
        if (dot == std::string::npos) { erase(m, member); continue; }
        for (auto& [key, value] : m.members)
            if (key == member.substr(0, dot) && value.kind == Value::Kind::Object) erase(value, member.substr(dot + 1));
    }
    std::map<std::string, std::string> out;
    for (const auto& [key, value] : m.members) out[key] = cjson::canonical(value);
    return out;
}

// Every bundle file must fit below MAX_PATH under root/<WorldContentId>, so a host without long-path support can read it.
void check_root_budget(const fs::path& root, const std::map<std::string, std::string>& files) {
    const std::size_t base = fs::absolute(root).lexically_normal().wstring().size() + 1 + 64;   // root\<id>
    for (const auto& [path, bytes] : files) {
        const std::wstring rel = fs::path(native(path)).make_preferred().wstring();
        if (base + 1 + rel.size() > kMaxFilePath)
            refuse("path_too_long", "Bundle root " + json::utf8(fs::absolute(root)) + " is too deep: " + path + " would be " + std::to_string(base + 1 + rel.size()) +
                   " characters (at most " + std::to_string(kMaxFilePath) + "); use a root of at most " + std::to_string(kMaxFilePath - 66 - kMaxBundlePath) + " characters");
        for (auto slash = rel.find(L'\\'); slash != std::wstring::npos; slash = rel.find(L'\\', slash + 1))
            if (base + 1 + slash > kMaxDirectoryPath) refuse("path_too_long", "Bundle root " + json::utf8(fs::absolute(root)) + " is too deep for the directories of " + path);
    }
}
}  // namespace

Published publish(const Content& content, const fs::path& root, const StepHook& hook) {
    for (const auto& [path, bytes] : content.files) check_path(path);
    if (content.description.kind != Value::Kind::Object) refuse("wrong_type", "The bundle description must be an object");
    for (const char* reserved : {"schema", "version", "worldContentId", "worldContentAlgorithm", "inventory", "totals"})
        if (content.description.find(reserved)) refuse("invalid_value", std::string("The description may not set '") + reserved + "'");
    check_root_budget(root, content.files);
    fs::create_directories(root);
    const fs::path staging = root / (std::string(kStagingPrefix) + random_hex());
    if (!fs::create_directory(staging)) refuse("work_collision", "Staging directory already exists: " + json::utf8(staging));
    auto step = [&](const std::string& name, const fs::path& where) { if (hook) hook(name, where); };
    try {
        step("staging", staging);
        for (const char* r : {"source", "compiled", "policies"}) fs::create_directories(staging / r);
        for (const auto& [path, bytes] : content.files) {
            write_flushed(staging / native(path), bytes);
            step("file:" + path, staging);
        }
        const auto inventory = identity::world_content({{"source", staging / "source"}, {"compiled", staging / "compiled"}, {"policies", staging / "policies"}});
        step("content-id", staging);
        // The whole inventory is re-read from disk and must equal what was meant to be written.
        Value rows = array();
        std::uint64_t total = 0;
        for (const auto& [path, bytes] : content.files) {
            const fs::path file = staging / native(path);
            if (!fs::is_regular_file(file) || fs::file_size(file) != bytes.size() || cjson::sha256_file_hex(file) != cjson::sha256_hex(bytes))
                refuse("staging_corrupt", "Staged file differs from its content: " + path);
            Value row = Value::object();
            row.set("path", str(path)); row.set("bytes", integer(static_cast<long long>(bytes.size()))); row.set("sha256", str(cjson::sha256_hex(bytes)));
            rows.items.push_back(row);
            total += bytes.size();
        }
        if (inventory.files != content.files.size()) refuse("staging_corrupt", "The staged inventory holds files the content does not name");
        step("verified", staging);
        Value manifest = content.description;
        manifest.set("schema", str("mnbl.world-bundle")); manifest.set("version", integer(1));
        manifest.set("worldContentId", str(inventory.id));
        manifest.set("worldContentAlgorithm", str("mnbl.world-content-id/1: sha256 of 'mnbl.world-content-id/1\\n' + the sorted lines '<root>/<relative path>\\t<bytes>\\t<sha256>\\n' over source/, compiled/ and policies/ (worldbridge identity::world_content; paths use '/', sorted by UTF-8 bytes)"));
        manifest.set("inventory", rows);
        Value totals = Value::object(); totals.set("files", integer(static_cast<long long>(content.files.size()))); totals.set("bytes", integer(static_cast<long long>(total)));
        manifest.set("totals", totals);
        const std::string manifest_text = cjson::canonical(manifest);
        write_flushed(staging / "manifest.json", manifest_text);
        step("manifest", staging);
        Value ready = Value::object();
        ready.set("schema", str("mnbl.data-ready")); ready.set("version", integer(1));
        ready.set("worldContentId", str(inventory.id)); ready.set("manifestSha256", str(cjson::sha256_hex(manifest_text)));
        write_flushed(staging / "DATA_READY", cjson::canonical(ready));
        step("ready", staging);
        // The complete candidate must pass the same validation a consumer runs, or nothing is published.
        const auto candidate = validate(staging, false);
        if (!candidate.valid) {
            std::string all;
            for (const auto& p : candidate.problems) all += (all.empty() ? "" : "; ") + p;
            refuse("staging_invalid", "The staged bundle does not validate: " + all);
        }
        step("validated", staging);
        Published out{inventory.id, cjson::sha256_hex(manifest_text), root / inventory.id, false};
        // Same WorldContentId: the same content. A valid bundle with the same description (provenance aside) is reused as it
        // is; a damaged one, or one that describes the content differently, is refused and never touched.
        auto reuse = [&]() {
            const auto existing = validate(out.directory);
            if (!existing.valid) refuse("bundle_conflict", "An invalid bundle already holds " + json::utf8(out.directory) + ": " + existing.problems.front());
            const auto mine = comparable(manifest), theirs = comparable(existing.manifest);
            std::string differ;
            for (const auto& [key, value] : mine) if (!theirs.count(key) || theirs.at(key) != value) differ += (differ.empty() ? "" : ", ") + key;
            for (const auto& [key, value] : theirs) if (!mine.count(key)) differ += (differ.empty() ? "" : ", ") + key;
            if (!differ.empty())
                refuse("bundle_conflict", "The published bundle " + json::utf8(out.directory) + " describes this content differently (" + differ +
                       "); bundles are never rewritten, so publish under another root or retire that bundle explicitly");
            std::error_code ec;
            fs::remove_all(staging, ec);
            out.existed = true;
            out.manifest_sha256 = existing.manifest_sha256;
            return out;
        };
        if (fs::exists(out.directory)) return reuse();
        step("rename", staging);
        // Atomic on one volume. A transient denial (a scanner holding a handle) is retried; a concurrent publisher that won
        // the race leaves a bundle that is reused like any existing one.
        DWORD error = 0;
        for (int attempt = 0; attempt < 6; ++attempt) {
            if (MoveFileExW(staging.c_str(), out.directory.c_str(), MOVEFILE_WRITE_THROUGH)) { error = 0; break; }
            error = GetLastError();
            if (fs::exists(out.directory)) return reuse();
            if (error != ERROR_ACCESS_DENIED && error != ERROR_SHARING_VIOLATION) break;
            Sleep(50u << attempt);
        }
        if (error != 0) throw std::runtime_error("Cannot publish " + json::utf8(staging) + " as " + json::utf8(out.directory) + " (error " + std::to_string(error) + ")");
        step("published", out.directory);
        return out;
    } catch (const SimulatedCrash&) {
        throw;   // a killed process cleans nothing up
    } catch (...) {
        std::error_code ec;
        fs::remove_all(staging, ec);
        throw;
    }
}

namespace {
// Thrown by build when the travel graph rejects the world: nothing is published.
struct Rejected : std::runtime_error {
    std::vector<travel::Reason> reasons;
    explicit Rejected(std::vector<travel::Reason> r) : std::runtime_error("The world is rejected by the travel gate"), reasons(std::move(r)) {}
};
}  // namespace

Built build(const Sources& s) {
    const auto world = source::read_source(s.split, source::load_compatibility(s.contract));
    const auto run = travel::run(world, s.split / "world.json", s.policies);
    if (run.result.status != "ok") throw Rejected(run.result.reasons);
    Built out;
    out.travel = run.result;
    auto& files = out.content.files;
    // source/: the producer split byte for byte (links refused, as WorldContentId refuses them).
    for (const auto& entry : fs::recursive_directory_iterator(s.split)) {
        if (entry.is_symlink()) refuse("unsafe_path", "The split contains a link: " + json::utf8(entry.path()));
        if (!entry.is_regular_file()) continue;
        const auto rel = entry.path().lexically_relative(s.split).generic_u8string();
        files["source/" + std::string(rel.begin(), rel.end())] = read_all(entry.path());
    }
    for (const auto& [path, bytes] : run.compiled) files["compiled/" + path] = bytes;
    // policies/: every input the compilers read, byte for byte.
    const auto proj_policy = projection::load_policy(s.policies.projection);
    const auto playability = travel::load_policy(s.policies.playability);
    const fs::path projection_calibration = s.policies.projection.parent_path() / fs::path(std::u8string(proj_policy.calibration_file.begin(), proj_policy.calibration_file.end()));
    std::map<std::string, fs::path> policy_files{
        {"projection", s.policies.projection}, {"terrain-materials", s.policies.materials}, {"terrain-import", s.policies.import},
        {"campaign-content", s.policies.content}, {"culture-presentations", s.policies.cultures}, {"playability", s.policies.playability},
        {"source-compatibility", s.contract}, {"calibration/axis-sea-datum", projection_calibration}};
    // The playability calibration sits where its policy names it.
    {
        const Value doc = cjson::parse(s.policies.playability);
        const Value* cal = doc.find("calibration");
        const Value* file = cal ? cal->find("file") : nullptr;
        if (!file) refuse("missing_field", "The playability policy names no calibration");
        policy_files["calibration/naval-transitions"] = s.policies.playability.parent_path() / fs::path(std::u8string(file->text.begin(), file->text.end()));
    }
    Value policies = Value::object();
    for (const auto& [name, file] : policy_files) {
        const std::string bytes = read_all(file);
        files["policies/" + name + ".json"] = bytes;
        policies.set(name, str(cjson::sha256_hex(bytes)));
    }
    // compiled/validation.json: the static checks the bundle passed, before any runtime validation.
    Value report = Value::object();
    report.set("schema", str("mnbl.bundle-validation")); report.set("version", integer(1));
    report.set("travelStatus", str(run.result.status));
    std::size_t valid_ports = 0; for (const auto& p : run.result.ports) valid_ports += p.valid;
    Value counts = Value::object();
    counts.set("landComponents", integer(static_cast<long long>(run.result.lands.size()))); counts.set("groups", integer(static_cast<long long>(run.result.groups.size())));
    counts.set("basins", integer(static_cast<long long>(run.result.basins.size()))); counts.set("validPorts", integer(static_cast<long long>(valid_ports)));
    counts.set("exclusions", integer(static_cast<long long>(run.result.exclusions.size()))); counts.set("repairs", integer(static_cast<long long>(run.result.repairs.size())));
    counts.set("hostChecks", integer(static_cast<long long>(run.result.host_checks.size())));
    report.set("counts", counts);
    Value pending = Value::object();
    for (const auto& [owner, n] : run.pending_slots) pending.set(owner, integer(static_cast<long long>(n)));
    report.set("pendingSlots", pending);
    Value checks = array();
    for (const char* c : {"source contract: the split reads under worldgen/contracts/source-compatibility.json", "terrain: compiled under the import calibration (MMR-26)",
                          "campaign: every reference resolves and the records are closed after the travel exclusions (MMR-27/28)",
                          "travel: status ok; every town player-safe; repairs within their bounds (MMR-28)",
                          "engine validation: pending (host checks in compiled/travel/travel-graph.json, MMR-38)"})
        checks.items.push_back(str(c));
    report.set("checks", checks);
    files["compiled/validation.json"] = cjson::canonical(report);

    // The manifest description (identity and inventory are added at publication).
    Value d = Value::object();
    d.set("publication", str("DATA_READY: a source/compiler bundle sealed after static checks. Activation is forbidden until the host validates runtime content and navigation; only U03 writes an activation receipt (worldgen/contracts/activation-receipt.schema.json), outside the bundle, after W02-W05 and C07. Publication is not campaign commit."));
    Value source = Value::object();
    source.set("sourceIdentity", str(run.identity.source_identity)); source.set("worldSha256", str(run.identity.world_sha256));
    const Value lock = cjson::parse(s.producer_lock);
    const Value* producer = lock.find("producer");
    if (!producer || !producer->find("sha256") || !producer->find("sourceCommit")) refuse("missing_field", "The producer lock lacks producer.sha256 or sourceCommit");
    Value prod = Value::object();
    prod.set("lockSha256", str(cjson::sha256_file_hex(s.producer_lock))); prod.set("binarySha256", str(producer->find("sha256")->text));
    prod.set("sourceCommit", str(producer->find("sourceCommit")->text)); prod.set("generatorVersion", str(world.metadata.generator_version));
    source.set("producer", prod);
    if (s.recipe) {
        const Value recipe = cjson::parse(*s.recipe);
        Value r = Value::object();
        r.set("sha256", str(cjson::sha256_file_hex(*s.recipe)));
        const Value* seed = recipe.find("seed");
        r.set("seed", seed ? *seed : null());
        source.set("recipe", r);
    } else {
        source.set("recipe", null());
    }
    d.set("source", source);
    Value compiler = Value::object();
    compiler.set("bridgeVersion", str(WORLDBRIDGE_VERSION)); compiler.set("binarySha256", str(exe_sha256()));
    Value stages = array();
    for (const char* st : {"MMR-25 projection", "MMR-26 terrain", "MMR-27 campaign", "MMR-28 travel"}) stages.items.push_back(str(st));
    compiler.set("stages", stages);
    d.set("compiler", compiler);
    d.set("policies", policies);
    const Value runtime = cjson::parse(s.runtime_lock, 1u << 22);
    Value engine = Value::object();
    for (const char* key : {"gameVersion", "navalVersion"}) {
        const Value* v = runtime.find(key);
        if (!v) refuse("missing_field", std::string("The runtime lock lacks ") + key);
        engine.set(key, str(v->text));
    }
    engine.set("runtimeLockSha256", str(cjson::sha256_file_hex(s.runtime_lock)));
    engine.set("editor", null());
    engine.set("note", str("No editor dependency (MMR-21). The host refuses a bundle whose game or War Sails version differs from the installed one."));
    d.set("engine", engine);
    Value caps = array();
    for (const auto& [id, name] : std::vector<std::pair<const char*, const char*>>{
             {"R02", "runtime terrain rendering, heights and picking on generated terrain"}, {"R03", "generated land and War Sails routes"},
             {"R04", "generated campaign objects registered before initialization"}, {"R05", "save identity read before generated references deserialize"},
             {"R08", "generated wind, storm area and snow/rain bound to native consumers"}, {"R09", "generated terrain through land and naval missions"},
             {"R10", "periodic east/west topology"}}) {
        Value c = Value::object(); c.set("id", str(id)); c.set("name", str(name)); caps.items.push_back(c);
    }
    d.set("requiredCapabilities", caps);
    // compiled/compatibility.json: what a host must match, inside the content so the WorldContentId covers it (a game or
    // War Sails update gives a new bundle, never a reused one with stale versions). The manifest repeats it for inspection.
    Value compat = Value::object(), versions = Value::object();
    compat.set("schema", str("mnbl.bundle-compatibility")); compat.set("version", integer(1));
    versions.set("gameVersion", *engine.find("gameVersion")); versions.set("navalVersion", *engine.find("navalVersion"));
    compat.set("engine", versions);
    compat.set("requiredCapabilities", caps);
    files["compiled/compatibility.json"] = cjson::canonical(compat);
    Value coords = Value::object();
    const projection::Projection proj(proj_policy, projection::frame_of(world));
    coords.set("projectionSha256", str(run.identity.projection_sha256));
    coords.set("axes", str("+X east, +Y south, +Z up (MMR-16 calibration); logical x wraps every logicalWidth"));
    coords.set("logicalWidth", num(proj.width())); coords.set("logicalHeight", num(proj.height()));
    coords.set("strip", num(proj.strip())); coords.set("poleMargin", num(proj.pole_margin()));
    coords.set("verticalScale", num(proj_policy.vertical_scale)); coords.set("seaDatum", num(0));
    Value grid = Value::object();
    grid.set("side", integer(run.identity.side)); grid.set("columnOffset", integer(run.identity.column_offset)); grid.set("rowOffset", integer(run.identity.row_offset));
    coords.set("terrain", grid);
    d.set("coordinates", coords);
    Value validation = Value::object();
    validation.set("reportPath", str("compiled/validation.json")); validation.set("reportSha256", str(cjson::sha256_hex(files["compiled/validation.json"])));
    validation.set("travelStatus", str(run.result.status));
    d.set("validation", validation);
    Value retention = Value::object();
    retention.set("kind", str("world-data-bundle"));
    retention.set("references", str("Activation receipts and saved campaigns name this worldContentId; the bundle never lists them. Campaign instance binding lives in the activation receipt, so identical content can back separate saves."));
    retention.set("deletableWhen", str("no activation receipt or save names this worldContentId (cleanup: MMR-47; abandoned staging: MMR-62)"));
    d.set("retention", retention);
    d.set("runtimeDerived", str("Resolved game-policy slots (C02-C04) and engine caches belong to a separate RuntimeDerivedBundle bound by its own hash; it never rewrites this bundle or its worldContentId."));
    d.set("migration", str("No silent rewriting at load: a newer bundle schema needs an explicit migration tool."));
    out.content.description = d;
    return out;
}

int bundle_main(int argc, char** argv) {
    const char* usage = "Usage: worldbridge --bundle SPLIT --contract FILE --projection-policy FILE --materials FILE --import FILE --content FILE --cultures FILE "
                        "--playability FILE --producer-lock FILE --runtime-lock FILE [--recipe FILE] --root DIR --diagnostics DIR";
    std::map<std::string, std::string> o;
    static const std::set<std::string> known{"--contract", "--projection-policy", "--materials", "--import", "--content", "--cultures", "--playability",
                                             "--producer-lock", "--runtime-lock", "--recipe", "--root", "--diagnostics"};
    for (int i = 3; i < argc; i += 2) {
        const std::string key = argv[i];
        if (!known.count(key) || i + 1 >= argc) throw std::runtime_error(usage);
        if (!o.emplace(key, argv[i + 1]).second) throw std::runtime_error("Repeated option " + key);
    }
    for (const auto& key : known)
        if (key != "--recipe" && !o.count(key)) throw std::runtime_error(usage);
    const auto started = std::chrono::system_clock::now();
    const auto clock = std::chrono::steady_clock::now();
    Sources s;
    s.split = argv[2]; s.contract = o["--contract"]; s.producer_lock = o["--producer-lock"]; s.runtime_lock = o["--runtime-lock"];
    s.policies = {o["--projection-policy"], o["--materials"], o["--import"], o["--content"], o["--cultures"], o["--playability"]};
    if (o.count("--recipe")) s.recipe = fs::path(o["--recipe"]);
    Value report = Value::object();
    int code = 0;
    std::exception_ptr failure;
    try {
        const auto built = build(s);
        const auto compiled = std::chrono::steady_clock::now();
        const auto published = publish(built.content, o["--root"]);
        report.set("status", str("published"));
        report.set("worldContentId", str(published.world_content_id)); report.set("manifestSha256", str(published.manifest_sha256));
        report.set("directory", str(json::utf8(fs::absolute(published.directory))));
        Value existed; existed.kind = Value::Kind::Bool; existed.boolean = published.existed;
        report.set("existed", existed);
        report.set("compileSeconds", num(std::chrono::duration<double>(compiled - clock).count()));
    } catch (const Rejected& r) {
        report.set("status", str("rejected"));
        Value reasons = array();
        for (const auto& reason : r.reasons) { Value v = Value::object(); v.set("code", str(reason.code)); v.set("message", str(reason.message)); reasons.items.push_back(v); }
        report.set("reasons", reasons);
        code = 3;
    } catch (const std::exception& e) {
        // A refusal or failure is recorded in the diagnostics too, then reported as usual (exit 2).
        failure = std::current_exception();
        report.set("status", str("failed"));
        const auto* refusal = dynamic_cast<const json::Error*>(&e);
        report.set("error", str(refusal ? refusal->code + ": " + refusal->message : std::string(e.what())));
    }
    // Volatile run data stays outside the bundle and its identity.
    report.set("wallSeconds", num(std::chrono::duration<double>(std::chrono::steady_clock::now() - clock).count()));
    report.set("startedUnixSeconds", integer(std::chrono::duration_cast<std::chrono::seconds>(started.time_since_epoch()).count()));
    Value inputs = Value::object();
    inputs.set("split", str(json::utf8(fs::absolute(s.split))));
    for (const auto& [key, value] : o) inputs.set(key.substr(2), str(json::utf8(fs::absolute(value))));
    report.set("inputs", inputs);
    const fs::path diag = fs::path(o["--diagnostics"]);
    fs::create_directories(diag);
    const std::string text = cjson::canonical(report);
    write_flushed(diag / ("bundle-" + random_hex() + ".json"), text);
    if (failure) std::rethrow_exception(failure);
    std::cout << text << std::endl;
    return code;
}

}  // namespace worldbridge::bundle
