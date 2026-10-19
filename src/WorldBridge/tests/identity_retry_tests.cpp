// Identity and retry tests: identity_retry_tests WORK_DIR
// Canonical JSON (RFC 8785 vectors), request validation and bindings, identity separation, seed
// derivation (vectors computed independently with Python hashlib), and the attempt schedule driven by
// scripted runners: quality retries, ceiling, exhaustion, immediate failure classes, cancel/resume,
// frozen final states and byte-identical persistence.
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <map>
#include <sstream>
#include <string>
#include <vector>
#include "worldbridge/attempt_schedule.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;
using schedule::Failure;

namespace {
int checks = 0, failures = 0;
fs::path work;

void check(bool ok, const std::string& what) {
    ++checks;
    if (ok) std::cout << "ok    " << what << "\n";
    else { ++failures; std::cout << "FAIL  " << what << "\n"; }
}
std::string slurp(const fs::path& p) { std::ifstream in(p, std::ios::binary); std::ostringstream s; s << in.rdbuf(); return s.str(); }
void spit(const fs::path& p, const std::string& text) { std::ofstream out(p, std::ios::binary | std::ios::trunc); out << text; }

std::string canon_text(const std::string& name, const std::string& json_text) {
    const fs::path f = work / ("cjson-" + name + ".json");
    spit(f, json_text);
    return cjson::canonical(cjson::parse(f));
}

void canonical_json() {
    check(canon_text("rfc_numbers", "[333333333.33333329,1E30,4.50,2e-3,0.000000000000000000000000001]") == "[333333333.3333333,1e+30,4.5,0.002,1e-27]",
          "RFC 8785 number vector: [333333333.3333333,1e+30,4.5,0.002,1e-27]");
    check(canon_text("numbers", "[0,-0,1e21,1e20,0.000001,1e-7,4294967295,-1.5,129.0,2.5e2]") == "[0,0,1e+21,100000000000000000000,0.000001,1e-7,4294967295,-1.5,129,250]",
          "ECMAScript number forms: -0, exponent thresholds 1e21 and 1e-7, integral doubles");
    // RFC 8785 3.2.3 sorting example: members sorted by UTF-16 code units (U+1F600 before U+FB33).
    const std::string sorted = canon_text("utf16_sort", "{\"\\u20ac\":1,\"\\r\":2,\"\\ufb33\":3,\"1\":4,\"\\ud83d\\ude00\":5,\"\\u0080\":6,\"\\u00f6\":7}");
    const std::string want = std::string("{\"\\r\":2,\"1\":4,\"\xc2\x80\":6,\"\xc3\xb6\":7,\"\xe2\x82\xac\":1,\"\xf0\x9f\x98\x80\":5,\"\xef\xac\xb3\":3}");
    check(sorted == want, "members sorted by UTF-16 code units (RFC 8785 example order)");
    check(canon_text("escapes", "[\"\\u0001\\\"\\\\\\/\\u00e9\\u2028\"]") == std::string("[\"\\u0001\\\"\\\\/\xc3\xa9\xe2\x80\xa8\"]"), "strings escape only quote, backslash and control characters");
    const std::string a = canon_text("equivalent_a", "{ \"size\" : 129,\n \"phase\":16 }");
    const std::string b = canon_text("equivalent_b", "{\"phase\":16.0,\"size\":1.29e2}");
    check(a == b && cjson::sha256_hex(a) == cjson::sha256_hex(b) && a == "{\"phase\":16,\"size\":129}", "equivalent configurations (spacing, member order, number spelling) give identical bytes and hash");
    try { canon_text("duplicate", "{\"a\":1,\"a\":2}"); check(false, "duplicate key accepted"); }
    catch (const json::Error& e) { check(e.code == "duplicate_key", "duplicate keys refused before identity is computed"); }
}

struct Fixture { fs::path lock, product, quality; std::string lock_sha, product_sha, quality_sha; };
Fixture fixture;

std::string request_text(const std::string& instance, const std::string& source, std::uint32_t seed, const std::string& overrides = "{\"size\":33,\"phase\":16}", int attempts = 3) {
    const fs::path o = work / "overrides.json";
    spit(o, overrides);
    return identity::build_request(instance, source, seed, cjson::parse(o), fixture.lock_sha, {{"product-policy", fixture.product_sha}, {"world-quality", fixture.quality_sha}}, attempts);
}

identity::GenerationRequest request(const std::string& name, const std::string& text) {
    const fs::path f = work / ("request-" + name + ".json");
    spit(f, text);
    return identity::load_request(f);
}

const std::string A = "3f2b8c1e-4d5a-4b6c-8d7e-9f0a1b2c3d4e";
const std::string B = "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d";

void requests_and_identity() {
    const auto r = request("valid", request_text(A, "explicit", 42));
    check(r.master_seed == 42 && r.max_attempts == 3 && r.recipe_version == 3 && r.request_id.size() == 64, "valid request accepted");
    check(identity::recipe_bytes(r, 42) == "{\"overrides\":{\"phase\":16,\"size\":33},\"recipe_version\":3,\"seed\":42}", "recipe bytes are the canonical producer request");
    auto refuse = [&](const std::string& name, const std::string& text, const std::string& code) {
        try { request(name, text); check(false, "request " + name + " accepted"); }
        catch (const json::Error& e) { check(e.code == code, "request " + name + " -> " + e.code + " at " + e.where.path); }
    };
    std::string base = request_text(A, "explicit", 42);
    auto with = [&](const std::string& from, const std::string& to) { std::string t = base; t.replace(t.find(from), from.size(), to); return t; };
    refuse("uppercase_uuid", with(A, "3F2B8C1E-4D5A-4B6C-8D7E-9F0A1B2C3D4E"), "invalid_value");
    refuse("seed_too_wide", with("\"masterSeed\":42", "\"masterSeed\":4294967296"), "out_of_range");
    refuse("seed_negative", with("\"masterSeed\":42", "\"masterSeed\":-1"), "wrong_type");
    refuse("recipe_version", with("\"recipeVersion\":3", "\"recipeVersion\":2"), "unsupported_version");
    refuse("unknown_member", with("\"maxAttempts\":3", "\"maxAttempts\":3,\"clock\":\"now\""), "unexpected_member");
    refuse("override_named_seed", with("\"phase\":16", "\"phase\":16,\"seed\":7"), "invalid_value");
    refuse("zero_attempts", with("\"maxAttempts\":3", "\"maxAttempts\":0"), "out_of_range");
    refuse("too_many_attempts", with("\"maxAttempts\":3", "\"maxAttempts\":17"), "out_of_range");
    refuse("bad_lock_hash", with(fixture.lock_sha, "abc"), "invalid_value");
    refuse("seed_source", with("\"seedSource\":\"explicit\"", "\"seedSource\":\"clock\""), "invalid_value");
    refuse("duplicate_member", with("\"maxAttempts\":3", "\"maxAttempts\":3,\"maxAttempts\":2"), "duplicate_key");

    // Bindings: a new generation must name the current lock and policy files.
    std::map<std::string, fs::path> policies{{"product-policy", fixture.product}, {"world-quality", fixture.quality}};
    try { identity::verify_bindings(r, fixture.lock, policies); check(true, "bindings verified for the current lock and policies"); }
    catch (const std::exception& e) { check(false, std::string("bindings refused: ") + e.what()); }
    const fs::path other_lock = work / "lock-newer.json";
    spit(other_lock, slurp(fixture.lock) + " ");
    try { identity::verify_bindings(r, other_lock, policies); check(false, "changed producer lock accepted"); }
    catch (const json::Error& e) { check(e.code == "producer_changed", "a different producer lock is refused for a new generation (producer_changed)"); }
    auto changed = policies; changed["world-quality"] = other_lock;
    try { identity::verify_bindings(r, fixture.lock, changed); check(false, "changed policy accepted"); }
    catch (const json::Error& e) { check(e.code == "policy_changed", "a changed policy file is refused (policy_changed)"); }
    auto extra = policies; extra["unbound"] = fixture.product;
    try { identity::verify_bindings(r, fixture.lock, extra); check(false, "unbound policy accepted"); }
    catch (const json::Error& e) { check(e.code == "policy_changed", "a policy the request does not bind is refused"); }

    // Identity separation.
    const auto replay = request("replay", request_text(B, "explicit", 42));
    const auto random_world = request("random", request_text(A, "random", 2675596451u));
    const auto same_seed_random = request("same_seed_random", request_text(B, "random", 42));
    const std::string recipe_r = cjson::sha256_hex(identity::recipe_bytes(r, r.master_seed));
    const std::string recipe_replay = cjson::sha256_hex(identity::recipe_bytes(replay, replay.master_seed));
    const std::string sid_r = identity::source_input_id(fixture.lock_sha, "binary", recipe_r);
    const std::string sid_replay = identity::source_input_id(fixture.lock_sha, "binary", recipe_replay);
    const std::string sid_random = identity::source_input_id(fixture.lock_sha, "binary", cjson::sha256_hex(identity::recipe_bytes(random_world, random_world.master_seed)));
    check(r.request_id != replay.request_id && r.configuration_id == replay.configuration_id && sid_r == sid_replay,
          "same seed, new campaign instance: distinct RequestId, same ConfigurationId and SourceInputId (replay is not a duplicate)");
    check(same_seed_random.configuration_id == r.configuration_id, "seedSource does not change ConfigurationId (same seed, same configuration)");
    check(random_world.configuration_id != r.configuration_id && sid_random != sid_r, "Random World seed gives a distinct configuration and SourceInputId");
    check(identity::source_input_id(fixture.lock_sha, "other-binary", recipe_r) != sid_r && identity::source_input_id("other-lock", "binary", recipe_r) != sid_r,
          "SourceInputId changes with producer identity (lock and binary)");
    check(r.request_id == cjson::sha256_hex("mnbl.request-id/1 mnbl.canonical-json/1\n" + r.canonical) && r.request_id != cjson::sha256_hex(r.canonical),
          "RequestId is domain-separated: SHA-256 of 'mnbl.request-id/1 mnbl.canonical-json/1\\n' + canonical request bytes");
    refuse("override_beyond_2_53", with("\"phase\":16", "\"phase\":9007199254740993"), "out_of_range");
    refuse("override_negative_beyond_2_53", with("\"phase\":16", "\"phase\":-9007199254740993"), "out_of_range");
    try { request("override_2_53", with("\"phase\":16", "\"phase\":9007199254740992")); check(true, "an override integer of exactly 2^53 is accepted"); }
    catch (const json::Error& e) { check(false, "2^53 override refused: " + e.code); }
    // Every spelling of a number is judged by its exact decimal value.
    for (const std::string& spelling : {"9007199254740993.0", "9007199254740993e0", "9.007199254740993e15", "-9007199254740992.5", "1e16", "90071992547409930e-1"})
        refuse("override_spelled_" + spelling, with("\"phase\":16", "\"phase\":" + spelling), "out_of_range");
    bool spellings_ok = true;
    for (const std::string& spelling : {"9007199254740992.0", "9.007199254740992e15", "-9007199254740992", "0.5", "1e2", "0.000001", "90071992547409920e-2"}) {
        try { request("override_ok", with("\"phase\":16", "\"phase\":" + spelling)); } catch (const json::Error&) { spellings_ok = false; std::cout << "      refused " << spelling << "\n"; }
    }
    check(spellings_ok, "numbers within 2^53 in any spelling are accepted (2^53 as 9007199254740992.0 and 9.007199254740992e15, 0.5, 1e2, 1e-6)");
    { const fs::path o = work / "overrides-wide.json";
      spit(o, "{\"phase\":9007199254740993}");
      try { identity::build_request(A, "explicit", 42, cjson::parse(o), fixture.lock_sha, {{"product-policy", fixture.product_sha}}, 3); check(false, "build_request rounded an override beyond 2^53"); }
      catch (const json::Error& e) { check(e.code == "out_of_range", "build_request validates overrides before canonicalizing (2^53 + 1 is refused, not rounded)"); } }
}

void seeds() {
    struct Vector { std::uint32_t master; std::vector<std::uint32_t> seeds; };
    const std::vector<Vector> vectors = {{42, {42, 2675596451u, 3430524811u, 2419810229u}}, {0, {0, 3233179271u, 3045640099u, 945399983u}},
                                         {4294967295u, {4294967295u, 3600581904u, 683733645u, 3160612746u}}};
    for (const auto& v : vectors) {
        std::set<std::uint32_t> used; bool ok = true;
        for (int k = 0; k < 4; ++k) { int c = -1; const auto s = schedule::derive_seed(v.master, k, used, &c); ok = ok && s == v.seeds[static_cast<std::size_t>(k)] && c == 0; used.insert(s); }
        check(ok, "mnbl.retry-seed/1 vectors for master " + std::to_string(v.master) + " match an independent implementation");
    }
    int counter = -1;
    const auto resolved = schedule::derive_seed(42, 1, {42, 2675596451u}, &counter);
    check(resolved == 1808827064u && counter == 1, "a repeated derived seed resolves deterministically to the next counter (1808827064, counter 1)");
}

void content_identity() {
    const fs::path a = work / "content-a", b = work / "content-b";
    fs::remove_all(a); fs::remove_all(b);
    fs::create_directories(a / "plans/city"); fs::create_directories(b / "plans/city");
    spit(a / "world.json", "{}"); spit(a / "plans/city/x.json", "[1]");
    spit(b / "plans/city/x.json", "[1]"); spit(b / "world.json", "{}");
    const auto ia = identity::world_content({{"source", a}}), ib = identity::world_content({{"source", b}});
    check(ia.id == ib.id && ia.files == 2 && ia.lines.front().rfind("source/plans/city/x.json\t3\t", 0) == 0, "WorldContentId comes from the sorted path/size/hash inventory, not creation order");
    spit(b / "world.json", "{ }");
    check(identity::world_content({{"source", b}}).id != ia.id, "any content byte change changes WorldContentId");
    check(identity::world_content({{"source", a}, {"compiled", b}}).id != ia.id, "compiled content joins the inventory under its own prefix");
}

// Scripted runner: one entry per call, in order.
struct Script {
    std::vector<schedule::AttemptResult> results;
    std::vector<std::pair<int, std::uint32_t>> calls;  // (attempt index, seed)
    schedule::Runner runner() {
        return [this](const schedule::Attempt& a, const std::string& recipe) {
            calls.emplace_back(a.index, a.seed);
            if (recipe.find("\"seed\":" + std::to_string(a.seed) + "}") == std::string::npos) throw std::runtime_error("recipe does not carry the attempt seed");
            if (calls.size() > results.size()) throw std::runtime_error("runner called more often than scripted");
            return results[calls.size() - 1];
        };
    }
};
schedule::AttemptResult quality(const std::string& code = "quality.too_few_harbours") { return {Failure::quality, "succeeded", {{code, "scripted"}}, ""}; }
schedule::AttemptResult accepted(const std::string& id = "content-1") { return {Failure::none, "succeeded", {}, id}; }
schedule::AttemptResult failed(Failure f, const std::string& code) { return {f, f == Failure::parser ? "succeeded" : "crashed", {{code, "scripted"}}, ""}; }
const std::set<std::string> retryable{"quality.too_few_cities", "quality.too_few_harbours", "quality.too_few_castles"};
const schedule::Producer producer{"lock-sha", "binary-sha"};

schedule::State run_script(const std::string& name, const identity::GenerationRequest& r, Script& script, bool fresh = true) {
    const fs::path state = work / ("state-" + name + ".json");
    if (fresh) fs::remove(state);
    return schedule::run(r, state, producer, retryable, script.runner());
}

void attempts() {
    const auto r = request("schedule", request_text(A, "explicit", 42));
    { Script s{{quality(), quality(), accepted()}};
      const auto st = run_script("retry", r, s);
      check(st.status == "accepted" && st.attempts.size() == 3 && st.chosen == 2 && st.attempts[0].seed == 42 && st.attempts[1].seed == 2675596451u && st.attempts[2].seed == 3430524811u &&
            st.attempts[0].state == "rejected" && st.attempts[1].state == "rejected" && st.attempts[2].world_content_id == "content-1",
            "declared quality failures retry with derived seeds 42 -> 2675596451 -> 3430524811; the third is accepted"); }
    { Script s{{quality("quality.too_few_cities"), quality(), quality("quality.too_few_castles")}};
      const auto st = run_script("exhausted", r, s);
      check(st.status == "exhausted" && st.attempts.size() == 3 && st.chosen == -1 && s.calls.size() == 3 &&
            st.attempts[0].reasons[0].code == "quality.too_few_cities" && st.attempts[2].reasons[0].code == "quality.too_few_castles",
            "the ceiling (3) ends in 'exhausted' with every attempt's reasons kept and nothing substituted"); }
    { const auto r1 = request("one_attempt", request_text(A, "explicit", 42, "{\"size\":33,\"phase\":16}", 1));
      Script s{{quality()}};
      const auto st = run_script("ceiling1", r1, s);
      check(st.status == "exhausted" && st.attempts.size() == 1 && s.calls.size() == 1, "a configured ceiling of 1 allows exactly one attempt"); }
    for (const auto& [failure, code] : std::vector<std::pair<Failure, std::string>>{{Failure::mechanical, "producer.crashed"}, {Failure::input, "producer.refused"},
                                                                                    {Failure::parser, "source.truncated_json"}, {Failure::engine, "engine.limit"}}) {
        Script s{{failed(failure, code), accepted()}};
        const auto st = run_script(std::string("immediate_") + schedule::failure_name(failure), r, s);
        check(st.status == "failed" && st.attempts.size() == 1 && s.calls.size() == 1 && st.attempts[0].failure == schedule::failure_name(failure),
              std::string(schedule::failure_name(failure)) + " failure ends the generation immediately (no retry)");
    }
    { Script s{{quality("quality.unlisted"), accepted()}};
      const auto st = run_script("undeclared", r, s);
      check(st.status == "failed" && st.attempts[0].failure == "undeclared_quality" && s.calls.size() == 1, "a quality code not declared retryable is not retried"); }

    // Cancel during attempt 1, then resume: the same seed runs again and the outcome equals an uninterrupted run.
    Script straight{{quality(), accepted("content-x")}};
    const auto uninterrupted = run_script("uninterrupted", r, straight);
    Script first{{quality(), {Failure::cancelled, "cancelled", {{"producer.cancelled", "scripted"}}, ""}}};
    const auto cancelled = run_script("cancel_resume", r, first);
    check(cancelled.status == "cancelled" && cancelled.attempts.size() == 2 && cancelled.attempts[1].state == "cancelled" && cancelled.attempts[1].runs == 1, "cancellation is persisted with its attempt");
    Script second{{accepted("content-x")}};
    const auto resumed = run_script("cancel_resume", r, second, false);
    check(resumed.status == "accepted" && second.calls.size() == 1 && second.calls[0].first == 1 && second.calls[0].second == cancelled.attempts[1].seed && resumed.attempts[1].runs == 2,
          "resume reruns the cancelled attempt with its own seed (no new attempt, no new seed)");
    check(resumed.attempts.size() == uninterrupted.attempts.size() && resumed.attempts[1].seed == uninterrupted.attempts[1].seed &&
          resumed.attempts[1].source_input_id == uninterrupted.attempts[1].source_input_id && resumed.attempts[1].world_content_id == uninterrupted.attempts[1].world_content_id,
          "cancelled+resumed and uninterrupted runs agree on attempt sequence, chosen seed and content identity");

    // Final states are frozen.
    Script none{{}};
    const auto frozen = run_script("cancel_resume", r, none, false);
    check(none.calls.empty() && frozen.status == "accepted" && schedule::state_json(frozen) == schedule::state_json(resumed), "an accepted state is loaded, never regenerated");
    Script none2{{}};
    const auto still_exhausted = run_script("exhausted", r, none2, false);
    check(none2.calls.empty() && still_exhausted.status == "exhausted", "an exhausted state is final");
    const auto other = request("other_instance", request_text(B, "explicit", 42));
    try { Script s{{accepted()}}; run_script("cancel_resume", other, s, false); check(false, "state reused by another request"); }
    catch (const json::Error& e) { check(e.code == "state_request_mismatch", "a state cannot be resumed by a different request (campaign sessions cannot overwrite each other)"); }

    // Determinism and same-seed replay.
    Script d1{{quality(), accepted("content-y")}}, d2{{quality(), accepted("content-y")}};
    const auto s1 = run_script("deterministic_1", r, d1), s2 = run_script("deterministic_2", r, d2);
    check(slurp(work / "state-deterministic_1.json") == slurp(work / "state-deterministic_2.json") && schedule::state_json(s1) == schedule::state_json(s2),
          "separate runs of the same request persist byte-identical attempt state (no clock, no randomness)");
    Script rp{{quality(), accepted("content-y")}};
    const auto replayed = run_script("replay_instance", other, rp);
    check(replayed.status == "accepted" && replayed.attempts[1].source_input_id == s1.attempts[1].source_input_id && replayed.attempts[1].seed == s1.attempts[1].seed &&
          replayed.request_id != s1.request_id && replayed.campaign_instance_id != s1.campaign_instance_id,
          "same-seed replay under a new campaign instance runs and is accepted with the same SourceInputId (never rejected as a duplicate)");
    const auto loaded = schedule::load_state(work / "state-deterministic_1.json");
    check(schedule::state_json(loaded) == slurp(work / "state-deterministic_1.json"), "persisted state round-trips byte for byte");

    // Run history: the interrupted run's record is kept when an attempt reruns.
    check(resumed.attempts[1].history.size() == 1 && resumed.attempts[1].history[0].run == 1 && resumed.attempts[1].history[0].state == "cancelled" &&
          resumed.attempts[1].history[0].reasons.size() == 1 && resumed.attempts[1].history[0].reasons[0].code == "producer.cancelled" && resumed.attempts[0].history.empty(),
          "resume keeps the cancelled run (run 1, producer.cancelled) in the attempt's history");

    // A hard kill leaves the attempt 'running'; a work-location collision is not a world outcome and leaves it the same way.
    { Script s{{quality()}};
      bool collided = false;
      const auto base_runner = s.runner();
      int calls = 0;
      const schedule::Runner killing = [&](const schedule::Attempt& a, const std::string& recipe) -> schedule::AttemptResult {
          if (++calls == 2) throw schedule::WorkCollision("scripted: location not fresh");
          return base_runner(a, recipe);
      };
      fs::remove(work / "state-killed.json");
      try { schedule::run(r, work / "state-killed.json", producer, retryable, killing); } catch (const schedule::WorkCollision&) { collided = true; }
      const auto after = schedule::load_state(work / "state-killed.json");
      check(collided && after.status == "in_progress" && after.attempts.size() == 2 && after.attempts[1].state == "running" && after.attempts[1].runs == 1,
            "a work-location collision propagates and leaves the attempt 'running' (as a hard kill would), nothing classified");
      Script rest{{accepted("content-k")}};
      const auto finished = run_script("killed", r, rest, false);
      check(finished.status == "accepted" && rest.calls.size() == 1 && rest.calls[0].second == after.attempts[1].seed && finished.attempts[1].runs == 2 &&
            finished.attempts[1].history.size() == 1 && finished.attempts[1].history[0].state == "running",
            "a killed attempt resumes with its own seed and records the interrupted run as 'running'"); }

    // Any other runner exception is a mechanical (bridge) failure, never retried.
    { const schedule::Runner throwing = [](const schedule::Attempt&, const std::string&) -> schedule::AttemptResult { throw std::runtime_error("scripted bridge fault"); };
      fs::remove(work / "state-throwing.json");
      const auto st = schedule::run(r, work / "state-throwing.json", producer, retryable, throwing);
      check(st.status == "failed" && st.attempts[0].failure == "mechanical" && st.attempts[0].reasons[0].code == "bridge.exception", "a runner exception is a mechanical failure (bridge.exception)"); }

    // Exclusive state: a second driver of the same state file is refused while the first runs.
    { std::string nested_code;
      const schedule::Runner nesting = [&](const schedule::Attempt&, const std::string&) -> schedule::AttemptResult {
          Script inner{{accepted()}};
          try { schedule::run(r, work / "state-locked.json", producer, retryable, inner.runner()); nested_code = "ran"; }
          catch (const json::Error& e) { nested_code = e.code; }
          return accepted("content-l");
      };
      fs::remove(work / "state-locked.json");
      const auto st = schedule::run(r, work / "state-locked.json", producer, retryable, nesting);
      check(nested_code == "state_locked" && st.status == "accepted" && !fs::exists(work / "state-locked.json.lock"),
            "a concurrent run on the same state is refused (state_locked); the lock disappears with its holder"); }

    // A persisted state that disagrees with the request it is resumed for is refused, even with a matching RequestId.
    { const std::string good = slurp(work / "state-cancel_resume.json");
      auto tampered = [&](const std::string& name, const std::string& from, const std::string& to) {
          std::string t = good;
          const auto at = t.find(from);
          if (at == std::string::npos) { check(false, "tamper anchor missing: " + from); return std::string(); }
          t.replace(at, from.size(), to);
          spit(work / ("state-tampered-" + name + ".json"), t);
          return name;
      };
      auto refused_load = [&](const std::string& name, const std::string& code, const std::string& what) {
          try { schedule::load_state(work / ("state-tampered-" + name + ".json")); check(false, what + " loaded"); }
          catch (const json::Error& e) { check(e.code == code, what + " -> " + e.code + " at " + e.where.path); }
      };
      tampered("status", "\"status\":\"accepted\"", "\"status\":\"finished\""); refused_load("status", "invalid_value", "an unknown status");
      tampered("seed_width", "\"masterSeed\":42", "\"masterSeed\":4294967296"); refused_load("seed_width", "out_of_range", "a master seed beyond 32 bits");
      tampered("seed", "\"seed\":42,", "\"seed\":43,"); refused_load("seed", "invalid_value", "an attempt seed that does not re-derive");
      tampered("chosen", "\"chosen\":{\"attempt\":1", "\"chosen\":{\"attempt\":0"); refused_load("chosen", "invalid_value", "a chosen attempt that was not accepted");
      tampered("no_chosen", "\"status\":\"accepted\"", "\"status\":\"in_progress\""); refused_load("no_chosen", "invalid_value", "status and chosen disagreeing");
      tampered("attempt_state", "\"state\":\"rejected\"", "\"state\":\"failed\""); refused_load("attempt_state", "invalid_value", "an earlier attempt that is not a rejection");
      tampered("max", "\"maxAttempts\":3", "\"maxAttempts\":2");
      try { Script s{{}}; schedule::run(r, work / "state-tampered-max.json", producer, retryable, s.runner()); check(false, "state with another maxAttempts resumed"); }
      catch (const json::Error& e) { check(e.code == "state_request_mismatch", "a state whose maxAttempts differs from the request is refused (state_request_mismatch)"); } }
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 2) { std::cerr << "usage: identity_retry_tests WORK_DIR\n"; return 2; }
    work = argv[1];
    fs::remove_all(work); fs::create_directories(work);
    try {
        fixture.lock = work / "producer.lock.json"; spit(fixture.lock, "{\"schema\":\"mnbl.producer-lock\"}\n");
        fixture.product = work / "product-policy.json"; spit(fixture.product, "{\"maxQualityAttempts\":3}\n");
        fixture.quality = work / "world-quality.json"; spit(fixture.quality, "{\"minCities\":2}\n");
        fixture.lock_sha = cjson::sha256_file_hex(fixture.lock);
        fixture.product_sha = cjson::sha256_file_hex(fixture.product);
        fixture.quality_sha = cjson::sha256_file_hex(fixture.quality);
        canonical_json();
        requests_and_identity();
        seeds();
        content_identity();
        attempts();
    } catch (const std::exception& e) {
        std::cout << "FAIL  unexpected: " << e.what() << "\n";
        ++failures;
    }
    std::cout << (failures ? "FAILED " : "PASSED ") << (checks - failures) << "/" << checks << " identity and retry checks\n";
    return failures ? 1 : 0;
}
