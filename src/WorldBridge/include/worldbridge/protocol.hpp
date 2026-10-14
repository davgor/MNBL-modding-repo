// Host <-> worldbridge producer protocol, version 1. No engine API names belong here.
//
// The host (game module) owns cancellation intent and progress UI. worldbridge owns the producer
// child process: pin check, launch, bounded resources, logs, termination and publication.
//
//   worldbridge --produce --lock LOCK --producer EXE --request ABS_REQUEST --job NEW_DIR --publish NEW_DIR
//               [--timeout-ms N (default 600000; 0 = none)] [--cancel-file PATH] [--memory-limit-mb N]
//   worldbridge --preflight --lock LOCK --producer EXE --job NEW_DIR [--timeout-ms N]
//
// Inputs: every path is absolute. --job and --publish must not exist; --publish's parent must exist
// on the same volume as --job. The request path and the job path must be representable in the
// system ANSI code page, because the producer reads its argv that way. The producer's SHA-256 must
// equal the lock before anything is launched, and no DLL the producer imports may sit beside it.
// Cancellation: the host creates --cancel-file (any content); an existing file cancels before
// launch. Killing worldbridge also kills the producer, because its job object is kill-on-close.
//
// Events: worldbridge writes one JSON object per line to its own stdout:
//   {"protocol":1,"event":"phase","phase":"<name>"}   names: preflight, launched, terminating,
//                                                          publishing, completed
//   {"protocol":1,"event":"outcome", ...}              last line; same content as job/outcome.json
// Refusals carry "failure":{"code","message","field"}; "field" is null for cross-field refusals.
// Files in --job: outcome.json (the protocol result), run-record.json (operational measurements,
// never part of a world document), producer-stdout.log and producer-stderr.log (each capped),
// staging/split (producer output; never visible at --publish unless the outcome is "succeeded").
// Exit codes: 0 succeeded, 3 any other structured outcome, 2 a usage error detected before --job
// was created (nothing written).
#pragma once
#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

namespace worldbridge {

constexpr int protocol_version = 1;
constexpr std::uint64_t default_log_cap = 1048576;
constexpr std::uint32_t default_produce_timeout_ms = 600000;
// Producer contract this bridge reads (must match the lock).
constexpr const char* supported_generator_schema = "fwg.world";
constexpr int supported_schema_version = 2;
constexpr int supported_split_version = 1;

enum class Outcome {
    succeeded,               // exit 0, staging/split/world.json present, published atomically
    refused,                 // exit 2 with a valid fantasy-world-generator.failure v2 document
    producer_protocol_error, // exit 2 without a valid failure document
    missing_executable,      // producer path absent; nothing launched
    pin_mismatch,            // producer hash/identity differs from the lock, or an unpinned DLL sits beside it
    invalid_request,         // bad request/job/publish paths, unusable lock
    launch_failed,           // CreateProcess failed, or the loader could not start the image
    timeout,                 // bridge terminated the job after --timeout-ms
    cancelled,               // cancel file present before launch, or appeared during the run
    crashed,                 // exception-status exit (>= 0x80000000), e.g. access violation
    memory_limit,            // job memory limit reached and the producer did not succeed
    exit_failure,            // any other nonzero exit
    output_invalid,          // exit 0 but no staging/split/world.json
    publish_failed,          // staging could not be moved to --publish
    bridge_error             // the bridge itself failed after creating --job (I/O, internal)
};
const char* outcome_name(Outcome outcome);

struct ProducerLock {
    std::string sha256, capabilities_sha256, help_sha256, ucrtbase_sha256, source_commit, generator_schema;
    int schema_version = 0, split_version = 0;
    std::vector<std::string> generate_command, capabilities_command, help_command, required_dlls;
};
ProducerLock read_lock(const std::filesystem::path& lock);

// Volume serial + file index: identity of the pinned image, compared with the launched image.
struct FileIdentity { std::uint32_t volume = 0; std::uint64_t index = 0; bool valid = false; };

// One supervised child process. Arguments are an argv vector; quoting is internal.
struct Launch {
    std::filesystem::path executable, working_directory, temp_directory, stdout_log, stderr_log, cancel_file;
    std::vector<std::string> arguments;
    std::uint64_t log_cap = default_log_cap, memory_limit_bytes = 0;
    std::uint32_t timeout_ms = 0;
    FileIdentity expected_identity;
    std::function<void(const std::string&)> phase;
    int threads = -1;  // >= 0 sets FWG_THREADS for the producer (not part of the world); -1 leaves it unset
};
struct LaunchResult {
    bool started = false, timed_out = false, cancelled = false, stdout_truncated = false, stderr_truncated = false;
    bool identity_mismatch = false, memory_limit_hit = false, log_write_failed = false;
    std::uint32_t win32_error = 0, exit_code = 0, pid = 0, total_processes = 0, stray_processes_terminated = 0;
    std::uint64_t elapsed_ms = 0, peak_job_memory = 0, stdout_bytes = 0, stderr_bytes = 0;
    std::wstring command_line;
    std::vector<std::string> environment;
};
LaunchResult run_supervised(const Launch& launch);

// Pure classification of a finished launch; unit-tested without processes.
Outcome classify(const LaunchResult& result, bool world_present, bool failure_document_valid);

std::string sha256_file(const std::filesystem::path& path);
std::vector<std::string> pe_imports(const std::filesystem::path& executable);

// The process command line as UTF-8 (argv is ANSI on Windows and would corrupt non-ASCII paths).
std::vector<std::string> utf8_arguments();
// args[0] is the subcommand (--produce / --preflight); options follow.
int produce_main(const std::vector<std::string>& args);
int preflight_main(const std::vector<std::string>& args);

}
