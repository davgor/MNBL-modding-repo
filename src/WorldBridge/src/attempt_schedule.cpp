#include "worldbridge/attempt_schedule.hpp"

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <charconv>
#include <memory>
#include <set>
#include <stdexcept>

namespace worldbridge::schedule {
namespace {
using cjson::Value;
using json::Error;

void be32(std::string& out, std::uint32_t v) {
    for (int shift = 24; shift >= 0; shift -= 8) out += static_cast<char>((v >> shift) & 0xff);
}

Value reasons_value(const std::vector<Reason>& reasons) {
    Value list; list.kind = Value::Kind::Array;
    for (const auto& r : reasons) {
        Value item = Value::object();
        item.set("code", Value::string(r.code));
        item.set("message", Value::string(r.message));
        list.items.push_back(item);
    }
    return list;
}

const Value& need(const Value& object, const char* key) {
    const Value* v = object.find(key);
    if (!v) throw Error("missing_field", std::string("Attempt state lacks '") + key + "'", object.at);
    return *v;
}
std::string str(const Value& v) { if (v.kind != Value::Kind::String) throw Error("wrong_type", "Expected a string", v.at); return v.text; }
std::int64_t num(const Value& v) {
    std::int64_t n = 0;
    const auto r = v.kind == Value::Kind::Number ? std::from_chars(v.text.data(), v.text.data() + v.text.size(), n) : std::from_chars_result{nullptr, std::errc::invalid_argument};
    if (r.ec != std::errc() || r.ptr != v.text.data() + v.text.size()) throw Error("wrong_type", "Expected an integer", v.at);
    return n;
}
std::string one_of(const Value& v, std::initializer_list<const char*> allowed) {
    const std::string s = str(v);
    for (const char* a : allowed) if (s == a) return s;
    throw Error("invalid_value", "Unexpected value '" + s + "'", v.at);
}
std::vector<Reason> reasons_of(const Value& list) {
    if (list.kind != Value::Kind::Array) throw Error("wrong_type", "reasons must be an array", list.at);
    std::vector<Reason> out;
    for (const auto& r : list.items) out.push_back(Reason{str(need(r, "code")), str(need(r, "message"))});
    return out;
}
const std::initializer_list<const char*> attempt_states{"pending", "running", "cancelled", "rejected", "failed", "accepted"};
const std::initializer_list<const char*> failure_names{"none", "quality", "input", "mechanical", "parser", "engine", "cancelled", "undeclared_quality"};

// Exclusive for the life of a run(): two processes can never drive the same attempt state.
class StateLock {
public:
    explicit StateLock(const fs::path& state_file) {
        const fs::path lock = state_file.wstring() + L".lock";
        handle = CreateFileW(lock.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
        if (handle == INVALID_HANDLE_VALUE)
            throw Error("state_locked", "Another process holds the attempt state (Win32 " + std::to_string(GetLastError()) + ")", json::Location{json::utf8(lock), 0, 1, 1, "$"});
    }
    ~StateLock() { if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }
    StateLock(const StateLock&) = delete;
    StateLock& operator=(const StateLock&) = delete;
private:
    HANDLE handle = INVALID_HANDLE_VALUE;
};
}  // namespace

const char* failure_name(Failure f) {
    switch (f) {
    case Failure::none: return "none";
    case Failure::quality: return "quality";
    case Failure::input: return "input";
    case Failure::mechanical: return "mechanical";
    case Failure::parser: return "parser";
    case Failure::engine: return "engine";
    case Failure::cancelled: return "cancelled";
    }
    return "unknown";
}

std::uint32_t derive_seed(std::uint32_t master, int attempt, const std::set<std::uint32_t>& used, int* counter) {
    if (attempt == 0) { if (counter) *counter = 0; return master; }
    for (std::uint32_t c = 0;; ++c) {
        std::string message = "mnbl.retry-seed/1";
        message += '\0';
        be32(message, master); be32(message, static_cast<std::uint32_t>(attempt)); be32(message, c);
        const std::string digest = cjson::sha256_hex(message);
        std::uint32_t seed = 0;
        std::from_chars(digest.data(), digest.data() + 8, seed, 16);  // first four bytes, big-endian
        if (!used.count(seed)) { if (counter) *counter = static_cast<int>(c); return seed; }
    }
}

std::string state_json(const State& s) {
    Value root = Value::object();
    root.set("schema", Value::string("mnbl.generation-attempts"));
    root.set("version", Value::integer(1));
    root.set("canonicalJson", Value::string("mnbl.canonical-json/1"));
    root.set("requestId", Value::string(s.request_id));
    root.set("configurationId", Value::string(s.configuration_id));
    root.set("campaignInstanceId", Value::string(s.campaign_instance_id));
    root.set("masterSeed", Value::integer(s.master_seed));
    root.set("maxAttempts", Value::integer(static_cast<std::uint64_t>(s.max_attempts)));
    root.set("seedDerivation", Value::string("mnbl.retry-seed/1"));
    root.set("status", Value::string(s.status));
    Value attempts; attempts.kind = Value::Kind::Array;
    for (const auto& a : s.attempts) {
        Value item = Value::object();
        item.set("index", Value::integer(static_cast<std::uint64_t>(a.index)));
        item.set("seed", Value::integer(a.seed));
        item.set("derivationCounter", Value::integer(static_cast<std::uint64_t>(a.derivation_counter)));
        item.set("runs", Value::integer(static_cast<std::uint64_t>(a.runs)));
        item.set("recipeSha256", Value::string(a.recipe_sha256));
        item.set("sourceInputId", Value::string(a.source_input_id));
        item.set("state", Value::string(a.state));
        item.set("failure", Value::string(a.failure));
        item.set("producerOutcome", Value::string(a.producer_outcome));
        item.set("reasons", reasons_value(a.reasons));
        item.set("worldContentId", Value::string(a.world_content_id));
        item.set("cities", a.cities ? Value::integer(*a.cities) : Value());
        Value history; history.kind = Value::Kind::Array;
        for (const auto& h : a.history) {
            Value run = Value::object();
            run.set("run", Value::integer(static_cast<std::uint64_t>(h.run)));
            run.set("state", Value::string(h.state));
            run.set("failure", Value::string(h.failure));
            run.set("producerOutcome", Value::string(h.producer_outcome));
            run.set("reasons", reasons_value(h.reasons));
            history.items.push_back(run);
        }
        item.set("history", history);
        attempts.items.push_back(item);
    }
    root.set("attempts", attempts);
    if (s.chosen >= 0) {
        const Attempt& a = s.attempts[static_cast<std::size_t>(s.chosen)];
        Value chosen = Value::object();
        chosen.set("attempt", Value::integer(static_cast<std::uint64_t>(a.index)));
        chosen.set("seed", Value::integer(a.seed));
        chosen.set("sourceInputId", Value::string(a.source_input_id));
        chosen.set("worldContentId", Value::string(a.world_content_id));
        root.set("chosen", chosen);
    } else root.set("chosen", Value());
    return cjson::canonical(root) + "\n";
}

// Durable replace: write and flush a sibling file, then rename it over the state with write-through.
void save_state(const fs::path& file, const State& s) {
    const std::string text = state_json(s);
    const fs::path temp = file.wstring() + L".pending";
    const json::Location at{json::utf8(temp), 0, 1, 1, "$"};
    HANDLE h = CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) throw Error("io_error", "Cannot create attempt state (Win32 " + std::to_string(GetLastError()) + ")", at);
    DWORD written = 0;
    const bool ok = WriteFile(h, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) && written == text.size() && FlushFileBuffers(h);
    CloseHandle(h);
    if (!ok) throw Error("io_error", "Cannot write attempt state", at);
    if (!MoveFileExW(temp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        throw Error("io_error", "Cannot replace attempt state (Win32 " + std::to_string(GetLastError()) + ")", at);
}

State load_state(const fs::path& file) {
    const Value root = cjson::parse(file, 4 << 20);
    if (root.kind != Value::Kind::Object || str(need(root, "schema")) != "mnbl.generation-attempts" || num(need(root, "version")) != 1)
        throw Error("unsupported_schema", "Not an mnbl.generation-attempts v1 document", root.at);
    if (str(need(root, "seedDerivation")) != "mnbl.retry-seed/1") throw Error("unsupported_version", "Unknown seed derivation", need(root, "seedDerivation").at);
    if (str(need(root, "canonicalJson")) != "mnbl.canonical-json/1") throw Error("unsupported_version", "Unknown canonical JSON encoding", need(root, "canonicalJson").at);
    State s;
    s.request_id = str(need(root, "requestId"));
    s.configuration_id = str(need(root, "configurationId"));
    s.campaign_instance_id = str(need(root, "campaignInstanceId"));
    const auto master = num(need(root, "masterSeed"));
    if (master < 0 || static_cast<std::uint64_t>(master) > identity::max_seed) throw Error("out_of_range", "masterSeed outside the producer width", need(root, "masterSeed").at);
    s.master_seed = static_cast<std::uint32_t>(master);
    s.max_attempts = static_cast<int>(num(need(root, "maxAttempts")));
    if (s.max_attempts < 1 || s.max_attempts > identity::max_attempt_ceiling) throw Error("out_of_range", "maxAttempts outside 1..16", need(root, "maxAttempts").at);
    s.status = one_of(need(root, "status"), {"in_progress", "cancelled", "accepted", "exhausted", "failed"});
    const Value& attempts = need(root, "attempts");
    if (attempts.kind != Value::Kind::Array || attempts.items.size() > static_cast<std::size_t>(s.max_attempts)) throw Error("invalid_value", "attempts must be an array within maxAttempts", attempts.at);
    for (const auto& item : attempts.items) {
        Attempt a;
        a.index = static_cast<int>(num(need(item, "index")));
        const auto seed = num(need(item, "seed"));
        if (seed < 0 || static_cast<std::uint64_t>(seed) > identity::max_seed) throw Error("out_of_range", "seed outside the producer width", need(item, "seed").at);
        a.seed = static_cast<std::uint32_t>(seed);
        a.derivation_counter = static_cast<int>(num(need(item, "derivationCounter")));
        a.runs = static_cast<int>(num(need(item, "runs")));
        a.recipe_sha256 = str(need(item, "recipeSha256"));
        a.source_input_id = str(need(item, "sourceInputId"));
        a.state = one_of(need(item, "state"), attempt_states);
        a.failure = one_of(need(item, "failure"), failure_names);
        a.producer_outcome = str(need(item, "producerOutcome"));
        a.world_content_id = str(need(item, "worldContentId"));
        a.reasons = reasons_of(need(item, "reasons"));
        const Value& cities = need(item, "cities");
        if (cities.kind != Value::Kind::Null) {
            const auto n = num(cities);
            if (n < 0) throw Error("out_of_range", "cities must be a non-negative integer or null", cities.at);
            a.cities = static_cast<std::uint64_t>(n);
        }
        const Value& history = need(item, "history");
        if (history.kind != Value::Kind::Array) throw Error("wrong_type", "history must be an array", history.at);
        for (const auto& h : history.items)
            a.history.push_back(Run{static_cast<int>(num(need(h, "run"))), one_of(need(h, "state"), attempt_states), one_of(need(h, "failure"), failure_names),
                                    str(need(h, "producerOutcome")), reasons_of(need(h, "reasons"))});
        if (a.index != static_cast<int>(s.attempts.size())) throw Error("invalid_value", "Attempt indexes must be consecutive", item.at);
        // Seeds are a pure function of the master seed: a state whose seeds do not re-derive was not written by this schedule.
        std::set<std::uint32_t> used;
        for (const auto& prior : s.attempts) used.insert(prior.seed);
        int counter = -1;
        if (derive_seed(s.master_seed, a.index, used, &counter) != a.seed || counter != a.derivation_counter)
            throw Error("invalid_value", "Attempt " + std::to_string(a.index) + " seed does not follow mnbl.retry-seed/1", need(item, "seed").at);
        if (a.runs < 0 || a.history.size() > static_cast<std::size_t>(a.runs)) throw Error("invalid_value", "Attempt run history exceeds its run count", item.at);
        s.attempts.push_back(a);
    }
    // Only the last attempt can be unfinished, accepted or failed; every earlier one was a declared quality rejection.
    for (std::size_t i = 0; i + 1 < s.attempts.size(); ++i)
        if (s.attempts[i].state != "rejected") throw Error("invalid_value", "Only the last attempt may be in state '" + s.attempts[i].state + "'", attempts.items[i].at);
    const std::string last = s.attempts.empty() ? "" : s.attempts.back().state;
    const bool consistent = s.status == "accepted" ? last == "accepted"
                            : s.status == "failed" ? last == "failed"
                            : s.status == "cancelled" ? last == "cancelled"
                            : s.status == "exhausted" ? last == "rejected" && s.attempts.size() == static_cast<std::size_t>(s.max_attempts)
                                                      : last == "" || last == "running" || last == "pending" || last == "rejected";
    if (!consistent) throw Error("invalid_value", "status '" + s.status + "' disagrees with the last attempt state '" + last + "'", root.at);
    const Value& chosen = need(root, "chosen");
    if (chosen.kind == Value::Kind::Object) {
        const auto index = num(need(chosen, "attempt"));
        if (index < 0 || index >= static_cast<std::int64_t>(s.attempts.size()) || s.attempts[static_cast<std::size_t>(index)].state != "accepted")
            throw Error("invalid_value", "chosen does not name an accepted attempt", chosen.at);
        s.chosen = static_cast<int>(index);
    } else if (chosen.kind != Value::Kind::Null) throw Error("wrong_type", "chosen must be an object or null", chosen.at);
    if ((s.status == "accepted") != (s.chosen >= 0)) throw Error("invalid_value", "status and chosen disagree", root.at);
    return s;
}

State run(const identity::GenerationRequest& request, const fs::path& state_file, const Producer& producer,
          const std::set<std::string>& retryable, const Runner& runner) {
    StateLock lock(state_file);
    const json::Location state_at{json::utf8(state_file), 0, 1, 1, "$"};
    State s;
    if (fs::exists(state_file)) {
        s = load_state(state_file);
        if (s.request_id != request.request_id || s.configuration_id != request.configuration_id || s.master_seed != request.master_seed ||
            s.max_attempts != request.max_attempts || s.campaign_instance_id != request.campaign_instance_id)
            throw Error("state_request_mismatch", "The attempt state belongs to request " + s.request_id + ", not " + request.request_id, state_at);
        // Final states are frozen: an accepted world loads its bundle; exhausted/failed never regenerate.
        if (s.status == "accepted" || s.status == "exhausted" || s.status == "failed") return s;
        s.status = "in_progress";
    } else {
        s.request_id = request.request_id;
        s.configuration_id = request.configuration_id;
        s.campaign_instance_id = request.campaign_instance_id;
        s.master_seed = request.master_seed;
        s.max_attempts = request.max_attempts;
    }
    for (;;) {
        // The attempt to run: an unfinished one (pending/running/cancelled) again with its own seed, else the next.
        if (s.attempts.empty() || s.attempts.back().state == "rejected") {
            if (static_cast<int>(s.attempts.size()) >= s.max_attempts) { s.status = "exhausted"; save_state(state_file, s); return s; }
            Attempt a;
            a.index = static_cast<int>(s.attempts.size());
            std::set<std::uint32_t> used;
            for (const auto& prior : s.attempts) used.insert(prior.seed);
            a.seed = derive_seed(s.master_seed, a.index, used, &a.derivation_counter);
            a.recipe_sha256 = cjson::sha256_hex(identity::recipe_bytes(request, a.seed));
            a.source_input_id = identity::source_input_id(producer.lock_sha256, producer.binary_sha256, a.recipe_sha256);
            s.attempts.push_back(a);
        }
        Attempt& a = s.attempts.back();
        if (a.runs > 0) a.history.push_back(Run{a.runs, a.state, a.failure, a.producer_outcome, a.reasons});  // keep the interrupted run's record
        a.state = "running"; a.runs++; a.reasons.clear(); a.failure = "none"; a.producer_outcome.clear(); a.cities.reset();
        save_state(state_file, s);
        const std::string recipe = identity::recipe_bytes(request, a.seed);
        if (cjson::sha256_hex(recipe) != a.recipe_sha256) throw Error("state_request_mismatch", "Recipe bytes differ from the persisted attempt", state_at);
        AttemptResult result;
        try {
            result = runner(a, recipe);
        } catch (const WorkCollision&) {
            throw;  // not a world outcome: the attempt stays running and resumes once the location is fresh
        } catch (const std::exception& e) {
            result = AttemptResult{Failure::mechanical, "", {{"bridge.exception", e.what()}}, ""};
        }
        a.producer_outcome = result.producer_outcome;
        a.reasons = result.reasons;
        a.cities = result.cities;
        a.failure = failure_name(result.failure);
        switch (result.failure) {
        case Failure::none:
            a.state = "accepted"; a.world_content_id = result.world_content_id;
            s.status = "accepted"; s.chosen = a.index;
            save_state(state_file, s);
            return s;
        case Failure::cancelled:
            a.state = "cancelled"; s.status = "cancelled";
            save_state(state_file, s);
            return s;
        case Failure::quality: {
            bool declared = !result.reasons.empty();
            for (const auto& r : result.reasons) declared = declared && retryable.count(r.code) != 0;
            if (declared) { a.state = "rejected"; save_state(state_file, s); continue; }
            a.failure = "undeclared_quality";
            a.state = "failed"; s.status = "failed";
            save_state(state_file, s);
            return s;
        }
        default:
            a.state = "failed"; s.status = "failed";
            save_state(state_file, s);
            return s;
        }
    }
}

}  // namespace worldbridge::schedule
