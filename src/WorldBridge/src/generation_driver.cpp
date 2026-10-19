// worldbridge --generate and --new-request: the host-facing generation flow over the supervised
// producer (MMR-22), the bounded source reader (MMR-23) and the attempt schedule.
#include "worldbridge/attempt_schedule.hpp"
#include "worldbridge/protocol.hpp"
#include "worldbridge/source_world.hpp"
#include "worldbridge/travel_graph.hpp"

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <cctype>
#include <charconv>
#include <fstream>
#include <utility>
#include <iostream>
#include <map>
#include <optional>
#include <sstream>
#include <stdexcept>

namespace worldbridge::schedule {
namespace {
using cjson::Value;

std::map<std::string, std::string> options(const std::vector<std::string>& args, const std::set<std::string>& allowed, const char* usage) {
    std::map<std::string, std::string> out;
    if (args.size() % 2 != 1) throw std::runtime_error(usage);
    for (std::size_t i = 1; i + 1 < args.size(); i += 2) {
        if (!allowed.count(args[i])) throw std::runtime_error("Unknown option " + args[i] + "\n" + usage);
        if (!out.emplace(args[i], args[i + 1]).second) throw std::runtime_error("Repeated option " + args[i]);
    }
    return out;
}

// Whole-string unsigned decimal within [low, high]; anything else is a usage error before any state changes.
std::uint64_t decimal(const std::string& text, std::uint64_t low, std::uint64_t high, const std::string& what) {
    std::uint64_t n = 0;
    const auto r = std::from_chars(text.data(), text.data() + text.size(), n);
    if (text.empty() || r.ec != std::errc() || r.ptr != text.data() + text.size() || n < low || n > high || (text.size() > 1 && text[0] == '0'))
        throw std::runtime_error(what + " must be a decimal integer in " + std::to_string(low) + ".." + std::to_string(high) + ", got '" + text + "'");
    return n;
}

fs::path path_arg(const std::string& utf8) { return fs::absolute(json::from_utf8(utf8)); }

std::map<std::string, fs::path> policy_list(const std::string& text) {
    std::map<std::string, fs::path> out;
    std::stringstream items(text);
    std::string item;
    while (std::getline(items, item, ',')) {
        const auto eq = item.find('=');
        if (eq == std::string::npos || eq == 0) throw std::runtime_error("--policies takes name=path[,name=path...]");
        if (!out.emplace(item.substr(0, eq), path_arg(item.substr(eq + 1))).second) throw std::runtime_error("Repeated policy " + item.substr(0, eq));
    }
    return out;
}

const Value& field(const Value& root, const char* key, const std::string& what) {
    const Value* v = root.find(key);
    if (!v) throw json::Error("missing_field", what + " lacks " + key, root.at);
    return *v;
}
std::uint64_t field_number(const Value& root, const char* key, const std::string& what) {
    const Value& v = field(root, key, what);
    std::uint64_t n = 0;
    if (v.kind != Value::Kind::Number || std::from_chars(v.text.data(), v.text.data() + v.text.size(), n).ptr != v.text.data() + v.text.size())
        throw json::Error("wrong_type", what + " " + key + " must be a non-negative integer", v.at);
    return n;
}

struct QualityPolicy { std::uint64_t min_cities = 0, min_harbour_hamlets = 0, min_castles = 0; std::set<std::string> retryable; };

QualityPolicy quality_policy(const fs::path& file) {
    const Value root = cjson::parse(file);
    if (field(root, "schema", "world-quality").text != "mnbl.world-quality" || field_number(root, "version", "world-quality") != 1)
        throw json::Error("unsupported_schema", "Not an mnbl.world-quality v1 policy", root.at);
    QualityPolicy q;
    q.min_cities = field_number(root, "minCities", "world-quality");
    q.min_harbour_hamlets = field_number(root, "minHarbourHamlets", "world-quality");
    q.min_castles = field_number(root, "minCastles", "world-quality");
    const Value& codes = field(root, "retryableQualityCodes", "world-quality");
    if (codes.kind != Value::Kind::Array) throw json::Error("wrong_type", "world-quality retryableQualityCodes must be an array", codes.at);
    for (const auto& code : codes.items) {
        if (code.kind != Value::Kind::String || code.text.rfind("quality.", 0) != 0) throw json::Error("invalid_value", "Retryable codes must be 'quality.*' strings", code.at);
        q.retryable.insert(code.text);
    }
    return q;
}

// worldgen/contracts/producer-seed-width.json: the seed width probed against one pinned producer binary.
void verify_seed_width(const fs::path& file, const std::string& producer_sha256) {
    const Value root = cjson::parse(file);
    if (field(root, "schema", "seed width").text != "mnbl.producer-seed-width" || field_number(root, "version", "seed width") != 1)
        throw json::Error("unsupported_schema", "Not an mnbl.producer-seed-width v1 record", root.at);
    if (field(root, "producerSha256", "seed width").text != producer_sha256)
        throw json::Error("seed_width_unverified", "The seed width was probed for another producer binary", field(root, "producerSha256", "seed width").at);
    if (field_number(root, "maxSeed", "seed width") != identity::max_seed || field(root, "verified", "seed width").kind != Value::Kind::Bool || !field(root, "verified", "seed width").boolean)
        throw json::Error("seed_width_unverified", "The probed seed width does not match the request contract", root.at);
}

std::string lower(std::string s) { for (auto& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c))); return s; }

// A parser refusal without machine-specific absolute paths: the file relative to the world directory. The reader
// reports canonical paths (fs::canonical, which also expands 8.3 names), so the world is matched in that form and as
// given, case-insensitively; a file outside both is reduced to its name. The state never depends on where --work is.
std::string located(const json::Error& e, const fs::path& world) {
    std::string file = e.where.file;
    std::error_code ec;
    const fs::path canonical = fs::canonical(world, ec);
    std::vector<std::string> bases{lower(json::utf8(world)) + "/"};
    if (!ec) bases.push_back(lower(json::utf8(canonical)) + "/");
    bool inside = false;
    for (const auto& base : bases)
        if (lower(file).rfind(base, 0) == 0) { file = file.substr(base.size()); inside = true; break; }
    if (!inside) file = json::utf8(json::from_utf8(file).filename());
    return e.message + " at " + file + ":" + std::to_string(e.where.line) + ":" + std::to_string(e.where.column) + " (byte " + std::to_string(e.where.offset) + ") " + e.where.path;
}

// The pinned producer takes its request and output paths as narrow argv (MMR-22 supervisor): a work location the
// system ANSI code page cannot spell is a usage error, refused before any state exists rather than frozen as a failure.
bool ansi_representable(const fs::path& path) {
    const std::wstring w = path.wstring();
    BOOL lossy = FALSE;
    const int n = WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, &lossy);
    return n > 0 && !lossy;
}

// The bound product policy's attempt ceiling.
std::uint64_t max_quality_attempts(const fs::path& product_policy) {
    const Value product = cjson::parse(product_policy);
    if (field(product, "schema", "product-policy").text != "mnbl.product-policy" || field_number(product, "version", "product-policy") != 1)
        throw json::Error("unsupported_schema", "Not an mnbl.product-policy v1 policy", product.at);
    return field_number(product, "maxQualityAttempts", "product-policy");
}

const char* const required_policies[] = {"product-policy", "world-quality", "source-compatibility"};
// Optional static travel gate (MMR-28): a request that binds "playability" binds all six and each attempt's world is
// compiled in memory; a travel rejection is a quality reason, retried only when world-quality declares its code.
const char* const travel_policies[] = {"projection", "terrain-materials", "terrain-import", "campaign-content", "culture-presentations", "playability"};

std::string summary(const State& s) {
    Value v = Value::object();
    v.set("status", Value::string(s.status));
    v.set("requestId", Value::string(s.request_id));
    v.set("attempts", Value::integer(s.attempts.size()));
    if (s.chosen >= 0) {
        const auto& a = s.attempts[static_cast<std::size_t>(s.chosen)];
        v.set("seed", Value::integer(a.seed));
        v.set("sourceInputId", Value::string(a.source_input_id));
        v.set("worldContentId", Value::string(a.world_content_id));
    }
    Value reasons; reasons.kind = Value::Kind::Array;
    for (const auto& a : s.attempts)
        for (const auto& r : a.reasons) { Value item = Value::object(); item.set("attempt", Value::integer(static_cast<std::uint64_t>(a.index))); item.set("code", Value::string(r.code)); item.set("message", Value::string(r.message)); reasons.items.push_back(item); }
    v.set("reasons", reasons);
    return cjson::canonical(v);
}

int exit_code(const State& s) { return s.status == "accepted" ? 0 : s.status == "cancelled" ? 4 : 3; }

fs::path attempt_dir(const fs::path& work, const std::string& request_id, int index, int run) {
    return work / request_id.substr(0, 16) / ("attempt-" + std::to_string(index) + "-run-" + std::to_string(run));
}
}  // namespace

int generate_main(const std::vector<std::string>& args) {
    const char* usage = "Usage: worldbridge --generate --request REQ --state STATE --work DIR --lock LOCK --producer EXE --seed-width SEED_WIDTH "
                        "--policies product-policy=P,world-quality=Q,source-compatibility=C[,projection=..,terrain-materials=..,terrain-import=..,campaign-content=..,culture-presentations=..,playability=..] "
                        "[--threads N] [--cancel-file F] [--timeout-ms N]";
    auto o = options(args, {"--request", "--state", "--work", "--lock", "--producer", "--seed-width", "--policies", "--threads", "--cancel-file", "--timeout-ms"}, usage);
    for (const char* k : {"--request", "--state", "--work", "--lock", "--producer", "--seed-width", "--policies"}) if (!o.count(k)) throw std::runtime_error(usage);
    // Everything that can be a usage error is decided before the state is touched.
    std::vector<std::string> extra;
    if (o.count("--threads")) { extra.push_back("--threads"); extra.push_back(std::to_string(decimal(o["--threads"], 0, 1024, "--threads"))); }
    if (o.count("--timeout-ms")) { extra.push_back("--timeout-ms"); extra.push_back(std::to_string(decimal(o["--timeout-ms"], 1, 86400000, "--timeout-ms"))); }
    if (o.count("--cancel-file")) { extra.push_back("--cancel-file"); extra.push_back(json::utf8(path_arg(o["--cancel-file"]))); }
    const auto request = identity::load_request(path_arg(o["--request"]));
    const fs::path state_file = path_arg(o["--state"]), work = path_arg(o["--work"]);
    const fs::path lock = path_arg(o["--lock"]), producer = path_arg(o["--producer"]);
    const auto policies = policy_list(o["--policies"]);
    for (const char* name : required_policies)
        if (!request.policy_sha256.count(name) || !policies.count(name))
            throw json::Error("missing_field", std::string("The request and --policies must both bind '") + name + "'", json::Location{json::utf8(path_arg(o["--request"])), 0, 1, 1, "$.policies"});
    std::optional<travel::Policies> travel_gate;
    if (policies.count("playability")) {
        for (const char* name : travel_policies)
            if (!request.policy_sha256.count(name) || !policies.count(name))
                throw json::Error("missing_field", std::string("A request that binds 'playability' must bind '") + name + "' too", json::Location{json::utf8(path_arg(o["--request"])), 0, 1, 1, "$.policies"});
        travel_gate = travel::Policies{policies.at("projection"), policies.at("terrain-materials"), policies.at("terrain-import"), policies.at("campaign-content"), policies.at("culture-presentations"), policies.at("playability")};
    }
    if (fs::exists(state_file)) {
        const State existing = load_state(state_file);
        if (existing.request_id == request.request_id && (existing.status == "accepted" || existing.status == "exhausted" || existing.status == "failed")) {
            // Frozen: an existing campaign loads its bundle (verified, never regenerated), even under a newer producer.
            if (existing.status == "accepted") {
                const auto& a = existing.attempts[static_cast<std::size_t>(existing.chosen)];
                const fs::path world = attempt_dir(work, existing.request_id, a.index, a.runs) / "world";
                if (!fs::is_directory(world) || identity::world_content({{"source", world}}).id != a.world_content_id)
                    throw json::Error("frozen_world_mismatch", "The accepted world is missing or differs from its WorldContentId", json::Location{json::utf8(world), 0, 1, 1, "$"});
            }
            std::cout << summary(existing) << std::endl;
            return exit_code(existing);
        }
    }
    // A new or resumed generation is bound to exactly the producer and policies the request accepted.
    identity::verify_bindings(request, lock, policies);
    const auto locked = read_lock(lock);
    verify_seed_width(path_arg(o["--seed-width"]), locked.sha256);
    const Producer identity_of{cjson::sha256_file_hex(lock), locked.sha256};
    const auto contract = source::load_compatibility(policies.at("source-compatibility"));
    const auto quality = quality_policy(policies.at("world-quality"));
    const auto ceiling = max_quality_attempts(policies.at("product-policy"));
    if (static_cast<std::uint64_t>(request.max_attempts) > ceiling)
        throw json::Error("policy_violation", "maxAttempts " + std::to_string(request.max_attempts) + " exceeds the product policy's maxQualityAttempts " + std::to_string(ceiling),
                          json::Location{json::utf8(path_arg(o["--request"])), 0, 1, 1, "$.maxAttempts"});
    if (!ansi_representable(attempt_dir(work, request.request_id, 0, 0)))
        throw json::Error("unrepresentable_path", "--work cannot be spelled in the system ANSI code page, which the producer's argv requires", json::Location{json::utf8(work), 0, 1, 1, "$"});
    fs::create_directories(work);

    const Runner runner = [&](const Attempt& a, const std::string& recipe) -> AttemptResult {
        // Request-scoped and never reused: an existing directory means another state used this location.
        const fs::path dir = attempt_dir(work, request.request_id, a.index, a.runs);
        fs::create_directories(dir.parent_path());
        std::error_code ec;
        if (!fs::create_directory(dir, ec) || ec) throw WorkCollision("Attempt directory already exists or cannot be created: " + json::utf8(dir));
        { std::ofstream out(dir / "request.json", std::ios::binary); out << recipe; if (!out) throw std::runtime_error("Cannot write the attempt recipe"); }
        const fs::path job = dir / "job";
        std::vector<std::string> produce = {"--produce", "--lock", json::utf8(lock), "--producer", json::utf8(producer), "--request", json::utf8(dir / "request.json"),
                                            "--job", json::utf8(job), "--publish", json::utf8(dir / "world")};
        produce.insert(produce.end(), extra.begin(), extra.end());
        int code = 0;
        {
            // Supervisor events go to the attempt's log; stdout carries only the generation summary.
            std::ofstream events(dir / "supervisor-events.log", std::ios::binary);
            auto* previous = std::cout.rdbuf(events.rdbuf());
            try { code = produce_main(produce); } catch (...) { std::cout.rdbuf(previous); throw; }
            std::cout.rdbuf(previous);
        }
        AttemptResult result;
        if ((code != 0 && code != 3) || !fs::is_regular_file(job / "outcome.json")) throw std::runtime_error("The supervisor wrote no outcome for this attempt (exit " + std::to_string(code) + ")");
        const Value outcome = cjson::parse(job / "outcome.json");
        const Value* job_field = outcome.find("job");
        if (!job_field || job_field->kind != Value::Kind::String || json::from_utf8(job_field->text) != job) throw std::runtime_error("The outcome does not belong to this attempt's job");
        result.producer_outcome = outcome.find("outcome") ? outcome.find("outcome")->text : "";
        if ((code == 0) != (result.producer_outcome == "succeeded")) throw std::runtime_error("The supervisor exit code " + std::to_string(code) + " contradicts its outcome '" + result.producer_outcome + "'");
        const std::string message = outcome.find("message") ? outcome.find("message")->text : "";
        if (result.producer_outcome == "cancelled") { result.failure = Failure::cancelled; result.reasons.push_back({"producer.cancelled", message}); return result; }
        if (result.producer_outcome == "refused") {
            const Value* f = outcome.find("failure");
            const Value* fcode = f ? f->find("code") : nullptr;
            const Value* fmessage = f ? f->find("message") : nullptr;
            result.failure = Failure::input;
            result.reasons.push_back({"producer.refused", (fcode ? fcode->text + ": " : std::string()) + (fmessage ? fmessage->text : message)});
            return result;
        }
        if (result.producer_outcome != "succeeded") { result.failure = Failure::mechanical; result.reasons.push_back({"producer." + result.producer_outcome, message}); return result; }
        try {
            const auto world = source::read_source(dir / "world", contract);
            std::size_t harbours = 0;
            for (const auto& h : world.hamlets) harbours += h.harbour.has_value();
            if (world.cities.size() < quality.min_cities) result.reasons.push_back({"quality.too_few_cities", std::to_string(world.cities.size()) + " cities, need " + std::to_string(quality.min_cities)});
            if (harbours < quality.min_harbour_hamlets) result.reasons.push_back({"quality.too_few_harbours", std::to_string(harbours) + " harbour hamlets, need " + std::to_string(quality.min_harbour_hamlets)});
            if (world.castles.size() < quality.min_castles) result.reasons.push_back({"quality.too_few_castles", std::to_string(world.castles.size()) + " castles, need " + std::to_string(quality.min_castles)});
            if (!result.reasons.empty()) { result.failure = Failure::quality; return result; }
            if (travel_gate) {
                try {
                    const auto travel = travel::run(world, dir / "world" / "world.json", *travel_gate);
                    // Every attempt keeps its travel outputs as evidence; a rejection names its UIDs there and in the reason.
                    const fs::path out_dir = dir / "travel";
                    fs::create_directories(out_dir);
                    for (const auto& [name, bytes] : {std::pair<const char*, const std::string*>{"travel-graph.json", &travel.graph}, {"ports.json", &travel.ports},
                                                      {"cells.bin", &travel.cells}, {"campaign.json", &travel.campaign}}) {
                        std::ofstream out(out_dir / name, std::ios::binary | std::ios::trunc);
                        out.write(bytes->data(), static_cast<std::streamsize>(bytes->size()));
                        if (!out) throw std::runtime_error("Cannot write the attempt's travel outputs");
                    }
                    if (travel.result.status != "ok") {
                        for (const auto& r : travel.result.reasons) {
                            std::string described = r.message;
                            if (!r.uids.empty()) {
                                described += " [uids:";
                                for (std::size_t i = 0; i < r.uids.size() && i < 20; ++i) described += " " + r.uids[i];
                                if (r.uids.size() > 20) described += " (+" + std::to_string(r.uids.size() - 20) + " more in travel/travel-graph.json)";
                                described += "]";
                            }
                            result.reasons.push_back({r.code, described});
                        }
                        result.failure = Failure::quality;
                        return result;
                    }
                } catch (const json::Error& e) {
                    // A compiler refusal is not a world-quality outcome: the generation fails at once.
                    result.failure = Failure::parser;
                    result.reasons.push_back({"compiler." + e.code, e.message});
                    return result;
                }
            }
            result.world_content_id = identity::world_content({{"source", dir / "world"}}).id;
        } catch (const json::Error& e) {
            result.failure = Failure::parser;
            result.reasons.push_back({"source." + e.code, located(e, dir / "world")});
        }
        return result;
    };
    const State s = run(request, state_file, identity_of, quality.retryable, runner);
    std::cout << summary(s) << std::endl;
    return exit_code(s);
}

int new_request_main(const std::vector<std::string>& args) {
    const char* usage = "Usage: worldbridge --new-request --out FILE --instance UUID (--seed N [--seed-source explicit|random] | --random 1) --overrides JSON_FILE --lock LOCK "
                        "--policies product-policy=P,world-quality=Q,source-compatibility=C --max-attempts N";
    auto o = options(args, {"--out", "--instance", "--seed", "--seed-source", "--random", "--overrides", "--lock", "--policies", "--max-attempts"}, usage);
    for (const char* k : {"--out", "--instance", "--overrides", "--lock", "--policies", "--max-attempts"}) if (!o.count(k)) throw std::runtime_error(usage);
    if (o.count("--seed") == o.count("--random")) throw std::runtime_error("Give exactly one of --seed and --random\n" + std::string(usage));
    if (o.count("--random") && o["--random"] != "1") throw std::runtime_error("--random takes the value 1");
    if (!identity::valid_uuid(o["--instance"])) throw std::runtime_error("--instance must be a lowercase RFC 4122 UUID (campaignInstanceId)");
    // MMR-44: --seed-source random records a seed the host itself drew from the OS CSPRNG before acceptance (the setup screen's Random
    // World shows the seed before generating); it is provenance only (ConfigurationId excludes seedSource).
    if (o.count("--seed-source") && (o.count("--random") || (o["--seed-source"] != "explicit" && o["--seed-source"] != "random")))
        throw std::runtime_error("--seed-source takes explicit or random, and only with --seed\n" + std::string(usage));
    const bool random = o.count("--random") != 0;
    const std::string seed_source = random ? "random" : (o.count("--seed-source") ? o["--seed-source"] : std::string("explicit"));
    const std::uint64_t seed = random ? identity::new_request_seed() : decimal(o["--seed"], 0, identity::max_seed, "--seed");
    const int attempts = static_cast<int>(decimal(o["--max-attempts"], 1, identity::max_attempt_ceiling, "--max-attempts"));
    // Only requests --generate can run: the three bound policies, within the product policy's attempt ceiling.
    const auto policies = policy_list(o["--policies"]);
    for (const char* name : required_policies)
        if (!policies.count(name)) throw std::runtime_error(std::string("--policies must bind '") + name + "'");
    const auto ceiling = max_quality_attempts(policies.at("product-policy"));
    if (static_cast<std::uint64_t>(attempts) > ceiling)
        throw std::runtime_error("--max-attempts " + std::to_string(attempts) + " exceeds the product policy's maxQualityAttempts " + std::to_string(ceiling));
    std::map<std::string, std::string> hashes;
    for (const auto& [name, path] : policies) hashes[name] = cjson::sha256_file_hex(path);
    const std::string text = identity::build_request(o["--instance"], seed_source, static_cast<std::uint32_t>(seed), cjson::parse(path_arg(o["--overrides"])),
                                                     cjson::sha256_file_hex(path_arg(o["--lock"])), hashes, attempts);
    // An accepted request is never overwritten, and only a request that validates is ever published at --out.
    const fs::path out_path = path_arg(o["--out"]), pending = out_path.wstring() + L".pending";
    if (fs::exists(out_path)) throw std::runtime_error("--out already exists; an accepted request is never replaced");
    { std::ofstream out(pending, std::ios::binary | std::ios::trunc); out << text; if (!out) throw std::runtime_error("Cannot write the request"); }
    try { identity::load_request(pending); } catch (...) { std::error_code ec; fs::remove(pending, ec); throw; }
    fs::rename(pending, out_path);
    std::cout << text << std::endl;
    return 0;
}

}  // namespace worldbridge::schedule
