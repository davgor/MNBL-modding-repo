// Protocol contract tests: every outcome, termination of the whole process tree, no partial publication.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <chrono>
#include <fstream>
#include <iostream>
#include <map>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include "worldbridge/json.hpp"
#include "worldbridge/protocol.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;

static int checks = 0;
static void require(bool condition, const std::string& label) {
    ++checks;
    if (!condition) throw std::runtime_error("FAILED: " + label);
}

static std::string read(const fs::path& path) { std::ifstream in(path, std::ios::binary); std::stringstream s; s << in.rdbuf(); return s.str(); }
static void write(const fs::path& path, const std::string& text) { std::ofstream(path, std::ios::binary) << text; }

static std::map<std::string, std::string> flatten(const fs::path& path) {
    std::map<std::string, std::string> out;
    std::istringstream in(read(path));
    worldbridge_json::Reader(in, [&](const worldbridge_json::Path& p, const std::string& v, bool) {
        std::string key; for (const auto& part : p) key += (key.empty() ? "" : ".") + part; out[key] = v;
    }).parse();
    return out;
}

static bool process_gone(const fs::path& pid_file) {
    if (!fs::exists(pid_file)) return true;
    const DWORD pid = static_cast<DWORD>(std::stoul(read(pid_file)));
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (!process) return true;
    const bool gone = WaitForSingleObject(process, 0) == WAIT_OBJECT_0;
    CloseHandle(process);
    return gone;
}

struct Fixture {
    fs::path root, fake, lock, bad_lock;
    int counter = 0;
    void make_lock(const fs::path& path, const std::string& sha, const std::string& capabilities) {
        std::string dlls;
        for (const auto& name : pe_imports(fake)) dlls += (dlls.empty() ? "" : ",") + worldbridge_json::escape(name);
        write(path, "{\"schema\":\"mnbl.producer-lock\",\"version\":1,\"producer\":{\"sha256\":\"" + sha +
            "\",\"capabilitiesSha256\":\"" + capabilities + "\",\"helpSha256\":\"" + help_sha + "\",\"sourceCommit\":\"" + std::string(40, 'a') + "\",\"generatorSchema\":\"fwg.world\",\"schemaVersion\":2,\"splitVersion\":1,"
            "\"commands\":{\"generate\":[\"generate\",\"--request\",\"{request}\",\"--split\",\"{split}\",\"--quiet\"],"
            "\"capabilities\":[\"capabilities\",\"--output\",\"{output}\"],\"help\":[\"--help\"]}},"
            "\"runtime\":{\"ucrtbaseSha256\":\"" + ucrt_sha + "\",\"requiredDlls\":[" + dlls + "]}}");
    }
    std::string help_sha, caps_sha, ucrt_sha;
    struct Run { int exit = 0; std::map<std::string, std::string> outcome; fs::path job, publish; };
    Run produce(const std::string& scenario, const std::vector<std::string>& extra = {}, const fs::path& lock_override = {},
                const fs::path& producer_override = {}, bool create_publish = false, const std::string& request_override = {},
                const std::string& directory_suffix = {}, bool create_job = false) {
        const fs::path base = root / ("case" + std::to_string(++counter) + "-" + scenario + directory_suffix);
        fs::create_directories(base);
        if (create_job) fs::create_directories(base / "job");
        const fs::path request = base / "request.json";
        write(request, "{\"seed\":1,\"scenario\":\"" + scenario + "\"}");
        Run run; run.job = base / "job"; run.publish = base / "world";
        if (create_publish) fs::create_directories(run.publish);
        std::vector<std::string> args = {"--produce", "--lock", (lock_override.empty() ? lock : lock_override).string(),
            "--producer", (producer_override.empty() ? fake : producer_override).string(),
            "--request", request_override.empty() ? request.string() : request_override, "--job", run.job.string(), "--publish", run.publish.string()};
        args.insert(args.end(), extra.begin(), extra.end());
        run.exit = produce_main(args);
        if (fs::exists(run.job / "outcome.json")) run.outcome = flatten(run.job / "outcome.json");
        return run;
    }
};

static void classification() {
    LaunchResult r; require(classify(r, false, false) == Outcome::launch_failed, "not started -> launch_failed");
    r.started = true; require(classify(r, true, false) == Outcome::succeeded, "exit 0 + world -> succeeded");
    require(classify(r, false, false) == Outcome::output_invalid, "exit 0 without world -> output_invalid");
    r.exit_code = 2; require(classify(r, false, true) == Outcome::refused, "exit 2 + document -> refused");
    require(classify(r, false, false) == Outcome::producer_protocol_error, "exit 2 without document -> protocol error");
    r.exit_code = 1; require(classify(r, true, false) == Outcome::exit_failure, "exit 1 -> exit_failure");
    r.exit_code = 0xC0000005u; require(classify(r, true, false) == Outcome::crashed, "NTSTATUS -> crashed");
    r.cancelled = true; require(classify(r, true, true) == Outcome::cancelled, "cancel wins");
    r.cancelled = false; r.timed_out = true; require(classify(r, true, true) == Outcome::timeout, "timeout wins");
    r.timed_out = false; r.exit_code = 0xC0000135u; require(classify(r, false, false) == Outcome::launch_failed, "DLL not found -> launch_failed");
    r.exit_code = 0x80000003u; require(classify(r, false, false) == Outcome::crashed, "breakpoint exception -> crashed");
    r.exit_code = 99; r.memory_limit_hit = true; require(classify(r, false, false) == Outcome::memory_limit, "memory limit + failure -> memory_limit");
    r.exit_code = 0; require(classify(r, true, false) == Outcome::succeeded, "memory pressure without failure still succeeds");
    r.identity_mismatch = true; require(classify(r, true, false) == Outcome::pin_mismatch, "launched image identity mismatch -> pin_mismatch");
}

int main(int argc, char** argv) {
    try {
        if (argc != 3) throw std::runtime_error("Usage: process_contract_tests FAKE_PRODUCER NEW_WORK_DIRECTORY");
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
        Fixture f; f.fake = fs::absolute(argv[1]); f.root = fs::absolute(argv[2]);
        if (fs::exists(f.root)) fs::remove_all(f.root);
        fs::create_directories(f.root);
        classification();
        // The fake writes help through CRT text-mode stdout, so the captured bytes end in CRLF.
        write(f.root / "help.txt", "fake producer help\r\n"); f.help_sha = sha256_file(f.root / "help.txt");
        write(f.root / "caps.json", "{\"fake\":1}\n"); f.caps_sha = sha256_file(f.root / "caps.json");
        wchar_t system[MAX_PATH]; GetSystemDirectoryW(system, MAX_PATH); f.ucrt_sha = sha256_file(fs::path(system) / L"ucrtbase.dll");
        const std::string fake_sha = sha256_file(f.fake);
        f.lock = f.root / "lock.json"; f.make_lock(f.lock, fake_sha, f.caps_sha);
        f.bad_lock = f.root / "bad-lock.json"; f.make_lock(f.bad_lock, std::string(64, '0'), f.caps_sha);

        auto ok = f.produce("success");
        require(ok.exit == 0 && ok.outcome["outcome"] == "succeeded" && ok.outcome["strayProcessesTerminated"] == "0", "success outcome, no strays");
        require(fs::exists(ok.publish / "world.json") && !fs::exists(ok.job / "staging" / "split"), "success published atomically, staging moved");
        require(fs::exists(ok.job / "run-record.json") && read(ok.job / "outcome.json").find("elapsed") == std::string::npos, "measurements only in run record");

        auto refused = f.produce("refuse");
        require(refused.exit == 3 && refused.outcome["outcome"] == "refused" && refused.outcome["failure.field"] == "size" && refused.outcome["failure.code"] == "INVALID_INPUT", "refusal is structured");
        auto cross_field = f.produce("refuse_null_field");
        require(cross_field.outcome["outcome"] == "refused" && cross_field.outcome["failure.field"] == "null" &&
                cross_field.outcome["failure.code"] == "INVALID_INPUT", "cross-field refusal with null field stays a refusal");
        auto braces = f.produce("success", {}, {}, {}, false, {}, "-{guid}");
        require(braces.outcome["outcome"] == "succeeded", "braces in request/job paths are data, not placeholders");
        bool usage_refused = false;
        const fs::path bad_job = f.root / "bad-timeout-job";
        try { produce_main({"--produce", "--lock", f.lock.string(), "--producer", f.fake.string(), "--request", (f.root / "help.txt").string(),
                            "--job", bad_job.string(), "--publish", (f.root / "bad-timeout-world").string(), "--timeout-ms", "soon"}); }
        catch (const std::exception&) { usage_refused = true; }
        require(usage_refused && !fs::exists(bad_job), "invalid numeric option is a usage error before the job exists");
        auto existing_job = f.produce("success", {}, {}, {}, false, {}, {}, true);
        require(existing_job.exit == 3 && !fs::exists(existing_job.job / "outcome.json") && !fs::exists(existing_job.job / "fake-ran.txt"), "existing job refused, nothing written");
        const fs::path early_cancel = f.root / "already-cancelled";
        write(early_cancel, "cancel");
        auto precancelled = f.produce("hang", {"--cancel-file", early_cancel.string()});
        require(precancelled.outcome["outcome"] == "cancelled" && precancelled.outcome["launched"] == "false" && !fs::exists(precancelled.job / "fake-ran.txt"), "existing cancel file cancels before launch");
        const fs::path shadow_dir = f.root / "shadowed-producer";
        fs::create_directories(shadow_dir);
        fs::copy_file(f.fake, shadow_dir / f.fake.filename());
        write(shadow_dir / pe_imports(f.fake).front(), "not a real dll");
        auto shadowed = f.produce("success", {}, {}, shadow_dir / f.fake.filename());
        require(shadowed.outcome["outcome"] == "pin_mismatch" && shadowed.outcome["launched"] == "false" && !fs::exists(shadowed.job / "fake-ran.txt"), "unpinned DLL beside producer refused");
        auto memory = f.produce("allocate", {"--memory-limit-mb", "64"});
        require(memory.outcome["outcome"] == "memory_limit" && memory.outcome["memoryLimitReached"] == "true" && !fs::exists(memory.publish), "job memory limit is a distinct outcome");
        auto protocol = f.produce("protocol_error");
        require(protocol.outcome["outcome"] == "producer_protocol_error", "exit 2 without document");
        auto failure = f.produce("exit_failure");
        require(failure.outcome["outcome"] == "exit_failure" && failure.outcome["exitCode"] == "1", "exit 1");
        auto crash = f.produce("crash");
        require(crash.outcome["outcome"] == "crashed" && crash.outcome["exitCodeHex"] == "0xC0000005", "access violation is a crash");
        require(!fs::exists(crash.publish), "crash publishes nothing");
        auto empty = f.produce("empty_success");
        require(empty.outcome["outcome"] == "output_invalid" && !fs::exists(empty.publish), "exit 0 without world");

        auto missing = f.produce("success", {}, {}, f.root / "absent" / "fwg.exe");
        require(missing.outcome["outcome"] == "missing_executable" && missing.outcome["launched"] == "false", "missing executable");
        auto mismatch = f.produce("success", {}, f.bad_lock);
        require(mismatch.outcome["outcome"] == "pin_mismatch" && !fs::exists(mismatch.job / "fake-ran.txt") && !fs::exists(mismatch.publish), "unpinned binary never launched");
        auto relative = f.produce("success", {}, {}, {}, false, "relative-request.json");
        require(relative.outcome["outcome"] == "invalid_request", "relative request refused");
        auto occupied = f.produce("success", {}, {}, {}, true);
        require(occupied.outcome["outcome"] == "invalid_request" && !fs::exists(occupied.job / "fake-ran.txt"), "existing publish refused before launch");

        auto start = std::chrono::steady_clock::now();
        auto timeout = f.produce("hang", {"--timeout-ms", "500"});
        auto seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        require(timeout.outcome["outcome"] == "timeout" && seconds < 8 && process_gone(timeout.job / "fake-ran.txt") && !fs::exists(timeout.publish), "timeout terminates and publishes nothing");

        const fs::path cancel_file = f.root / "cancel-now";
        Fixture::Run cancelled;
        std::thread worker([&] { cancelled = f.produce("hang", {"--cancel-file", cancel_file.string()}); });
        std::this_thread::sleep_for(std::chrono::milliseconds(600));
        write(cancel_file, "cancel");
        worker.join();
        require(cancelled.outcome["outcome"] == "cancelled" && cancelled.outcome["cancelled"] == "true", "cancel file cancels");
        require(process_gone(cancelled.job / "fake-ran.txt") && !fs::exists(cancelled.publish), "cancelled producer is gone and unpublished");
        require(fs::exists(cancelled.job / "staging" / "split" / "partial.json"), "partial output stays in staging only");

        auto grandchild = f.produce("grandchild");
        require(grandchild.outcome["outcome"] == "succeeded" && std::stoul(grandchild.outcome["strayProcessesTerminated"]) >= 1, "stray descendant counted");
        require(fs::exists(grandchild.job / "grandchild.pid") && process_gone(grandchild.job / "grandchild.pid"), "descendant terminated with the job");

        auto flood = f.produce("big_output");
        require(flood.outcome["outcome"] == "succeeded" && flood.outcome["logs.stdoutTruncated"] == "true" &&
                fs::file_size(flood.job / "producer-stdout.log") == default_log_cap, "stdout log bounded at 1 MiB");

        auto env = f.produce("environment");
        const std::string vars = read(env.job / "fake-env.txt");
        require(env.outcome["outcome"] == "succeeded" && vars.find("USERPROFILE=") == std::string::npos && vars.find("APPDATA=") == std::string::npos &&
                vars.find("TEMP=" + (env.job / "staging" / "tmp").string()) != std::string::npos, "minimal environment with private temp");

        const fs::path preflight_job = f.root / "preflight";
        require(preflight_main({"--preflight", "--lock", f.lock.string(), "--producer", f.fake.string(), "--job", preflight_job.string()}) == 0 &&
                flatten(preflight_job / "preflight.json")["passed"] == "true", "preflight passes for the pinned fake");
        const fs::path stale_lock = f.root / "stale-caps-lock.json"; f.make_lock(stale_lock, fake_sha, std::string(64, 'a'));
        const fs::path stale_job = f.root / "preflight-stale";
        require(preflight_main({"--preflight", "--lock", stale_lock.string(), "--producer", f.fake.string(), "--job", stale_job.string()}) == 3 &&
                flatten(stale_job / "preflight.json")["passed"] == "false", "preflight detects a capability drift");

        std::cout << "PASS: " << checks << " producer process contract checks\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
