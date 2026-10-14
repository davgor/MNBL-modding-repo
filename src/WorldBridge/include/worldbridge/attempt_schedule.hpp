// Bounded deterministic generation attempts (worldgen/contracts/generation-attempts.schema.json).
//
// Seeds, mnbl.retry-seed/1: attempt 0 uses the persisted master seed. Attempt k >= 1 takes, for
// counter c = 0, 1, ..., SHA-256("mnbl.retry-seed/1" 0x00 BE32(master) BE32(k) BE32(c)) and reads its
// first four bytes as a big-endian uint32, keeping the first value no earlier attempt used.
//
// Only declared world-quality failures are retried. Producer refusals (input), mechanical producer or
// bridge failures, source parser errors, compiler and engine limitations fail immediately. Every
// attempt, reason and seed is persisted after each transition; a cancelled attempt resumes with the
// same seed; an accepted, exhausted or failed state is final and is never regenerated.
#pragma once
#include <cstdint>
#include <filesystem>
#include <functional>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>
#include "worldbridge/world_identity.hpp"

namespace worldbridge::schedule {
namespace fs = std::filesystem;

enum class Failure { none, quality, input, mechanical, parser, engine, cancelled };
const char* failure_name(Failure failure);

struct Reason { std::string code, message; };

// Thrown by a runner whose work location is not fresh (e.g. a deleted state reusing a work directory). Nothing is
// classified and nothing in that location is read: the attempt stays "running", and the next invocation reruns it
// as a new run (a new location). Any other exception from a runner is a mechanical (bridge) failure.
struct WorkCollision : std::runtime_error { using std::runtime_error::runtime_error; };

// One earlier run of an attempt (a cancelled or interrupted run before a resume).
struct Run { int run = 0; std::string state, failure, producer_outcome; std::vector<Reason> reasons; };

struct AttemptResult {
    Failure failure = Failure::none;
    std::string producer_outcome;  // the supervisor's outcome name, when the producer ran
    std::vector<Reason> reasons;
    std::string world_content_id;  // set when accepted
};

struct Attempt {
    int index = 0, derivation_counter = 0, runs = 0;
    std::uint32_t seed = 0;
    std::string recipe_sha256, source_input_id;
    std::string state = "pending";  // pending | running | cancelled | rejected | failed | accepted
    std::string failure = "none", producer_outcome;
    std::vector<Reason> reasons;
    std::string world_content_id;
    std::vector<Run> history;  // earlier runs of this attempt, oldest first
};

struct State {
    std::string request_id, configuration_id, campaign_instance_id;
    std::uint32_t master_seed = 0;
    int max_attempts = 0;
    std::string status = "in_progress";  // in_progress | cancelled | accepted | exhausted | failed
    std::vector<Attempt> attempts;
    int chosen = -1;
};

std::uint32_t derive_seed(std::uint32_t master, int attempt, const std::set<std::uint32_t>& used, int* counter);

// Runs one attempt: given the attempt (seed, index, run number) and the exact recipe bytes.
using Runner = std::function<AttemptResult(const Attempt& attempt, const std::string& recipe_bytes)>;

struct Producer { std::string lock_sha256, binary_sha256; };

// Creates or resumes the state at state_file and runs attempts until a final status or cancellation.
State run(const identity::GenerationRequest& request, const fs::path& state_file, const Producer& producer,
          const std::set<std::string>& retryable_quality_codes, const Runner& runner);

State load_state(const fs::path& file);
std::string state_json(const State& state);  // canonical bytes
void save_state(const fs::path& file, const State& state);

// CLI entry points (src/generation_driver.cpp, linked into worldbridge.exe).
int generate_main(const std::vector<std::string>& args);
int new_request_main(const std::vector<std::string>& args);

}  // namespace worldbridge::schedule
