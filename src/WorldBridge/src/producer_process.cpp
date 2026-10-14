// Producer supervision for the host/bridge protocol (see worldbridge/protocol.hpp). Windows only.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <fstream>
#include <iostream>
#include <map>
#include <memory>
#include <set>
#include <sstream>
#include <stdexcept>
#include <thread>
#include "worldbridge/json.hpp"
#include "worldbridge/protocol.hpp"

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "shell32.lib")

#ifndef WORLDBRIDGE_CMAKE_VERSION
#define WORLDBRIDGE_CMAKE_VERSION "unknown"
#endif

namespace fs = std::filesystem;
using worldbridge_json::escape;

namespace worldbridge {
namespace {

struct Handle {
    HANDLE value = nullptr;
    Handle() = default;
    explicit Handle(HANDLE h) : value(h == INVALID_HANDLE_VALUE ? nullptr : h) {}
    Handle(Handle&& other) noexcept : value(other.value) { other.value = nullptr; }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    ~Handle() { reset(); }
    void reset(HANDLE h = nullptr) { if (value) CloseHandle(value); value = h == INVALID_HANDLE_VALUE ? nullptr : h; }
    explicit operator bool() const { return value != nullptr; }
};

std::wstring wide(const std::string& text) {
    if (text.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (n <= 0) throw std::runtime_error("Invalid UTF-8 argument");
    std::wstring out(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), out.data(), n);
    return out;
}

std::string narrow(const std::wstring& text) {
    if (text.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), out.data(), n, nullptr, nullptr);
    return out;
}

// The producer receives argv through the ANSI code page; anything that needs best-fit mapping
// would reach it as a different path.
bool ansi_representable(const std::wstring& text) {
    if (text.empty()) return true;
    BOOL lossy = FALSE;
    int n = WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, &lossy);
    return n > 0 && !lossy;
}

std::string lower(std::string text) {
    std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) { return static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c); });
    return text;
}

bool is_hex(const std::string& value, size_t length) {
    return value.size() == length && value.find_first_not_of("0123456789abcdef") == std::string::npos;
}

std::string hex(std::uint32_t value) {
    std::ostringstream out; out << "0x" << std::hex << std::uppercase << value; return out.str();
}

// Windows CRT argv quoting, including embedded quotes and trailing backslashes.
std::wstring quote(const std::wstring& argument) {
    std::wstring out = L"\"";
    size_t slashes = 0;
    for (wchar_t c : argument) {
        if (c == L'\\') { ++slashes; continue; }
        if (c == L'"') { out.append(slashes * 2 + 1, L'\\'); out += c; }
        else { out.append(slashes, L'\\'); out += c; }
        slashes = 0;
    }
    out.append(slashes * 2, L'\\');
    return out + L"\"";
}

std::string hash_handle(HANDLE file) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) throw std::runtime_error("SHA-256 provider unavailable");
    std::unique_ptr<void, void (*)(void*)> close_algorithm(algorithm, [](void* a) { BCryptCloseAlgorithmProvider(a, 0); });
    if (BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) != 0) throw std::runtime_error("SHA-256 hash creation failed");
    std::unique_ptr<void, void (*)(void*)> close_hash(hash, [](void* h) { BCryptDestroyHash(h); });
    LARGE_INTEGER zero{}; SetFilePointerEx(file, zero, nullptr, FILE_BEGIN);
    std::vector<unsigned char> buffer(1 << 16);
    DWORD read = 0;
    for (;;) {
        if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) throw std::runtime_error("Read failed while hashing");
        if (read == 0) break;
        if (BCryptHashData(hash, buffer.data(), read, 0) != 0) throw std::runtime_error("SHA-256 update failed");
    }
    unsigned char digest[32];
    if (BCryptFinishHash(hash, digest, sizeof(digest), 0) != 0) throw std::runtime_error("SHA-256 finish failed");
    static const char* digits = "0123456789abcdef";
    std::string out;
    for (unsigned char b : digest) { out += digits[b >> 4]; out += digits[b & 15]; }
    return out;
}

// Opened without write/delete sharing: the hashed bytes stay the launched bytes until closed.
Handle open_locked(const fs::path& path) {
    return Handle(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
}

FileIdentity identity_of(HANDLE file) {
    BY_HANDLE_FILE_INFORMATION info{};
    FileIdentity id;
    if (GetFileInformationByHandle(file, &info)) {
        id.volume = info.dwVolumeSerialNumber;
        id.index = (static_cast<std::uint64_t>(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
        id.valid = true;
    }
    return id;
}

// Final path of an open handle with any reparse points resolved; launching by it removes junction
// retargeting between hashing and CreateProcess.
fs::path final_path(HANDLE file) {
    std::wstring buffer(32768, L'\0');
    DWORD n = GetFinalPathNameByHandleW(file, buffer.data(), static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (n == 0 || n >= buffer.size()) throw std::runtime_error("Final producer path unavailable");
    buffer.resize(n);
    if (buffer.rfind(L"\\\\?\\UNC\\", 0) == 0) return L"\\\\" + buffer.substr(8);
    if (buffer.rfind(L"\\\\?\\", 0) == 0) return buffer.substr(4);
    return buffer;
}

std::wstring volume_of(const fs::path& path) {
    wchar_t volume[MAX_PATH] = {};
    if (!GetVolumePathNameW(path.c_str(), volume, MAX_PATH)) return {};
    return volume;
}

std::string read_capped(const fs::path& path, std::uint64_t cap, bool& truncated) {
    std::ifstream in(path, std::ios::binary);
    std::string text;
    truncated = false;
    if (!in) return text;
    text.resize(static_cast<size_t>(cap + 1));
    in.read(text.data(), static_cast<std::streamsize>(cap + 1));
    text.resize(static_cast<size_t>(in.gcount()));
    truncated = text.size() > cap;
    if (truncated) text.resize(static_cast<size_t>(cap));
    return text;
}

struct FailureDocument { bool valid = false, field_null = false; std::string code, message, field; };

FailureDocument parse_failure(const fs::path& stdout_log) {
    FailureDocument doc;
    bool truncated = false;
    std::string text = read_capped(stdout_log, default_log_cap, truncated);
    if (truncated || text.find_first_not_of(" \r\n\t") == std::string::npos) return doc;
    std::map<std::string, std::pair<std::string, bool>> top;
    bool object_root = false;
    try {
        std::istringstream in(text);
        worldbridge_json::Reader reader(in, [&](const worldbridge_json::Path& p, const std::string& v, bool quoted) {
            if (p.size() == 1) top[p[0]] = {v, quoted};
        }, [&](const worldbridge_json::Path& p, bool array) { if (p.empty()) object_root = !array; });
        reader.parse();
    } catch (const std::exception&) { return doc; }
    auto field = [&](const char* name, bool quoted) -> const std::string* {
        auto it = top.find(name);
        return it != top.end() && it->second.second == quoted ? &it->second.first : nullptr;
    };
    const auto* schema = field("schema", true); const auto* version = field("schema_version", false);
    const auto* code = field("code", true); const auto* message = field("message", true);
    // FWGC++ writes "field": null for cross-field refusals.
    const auto* name = field("field", true);
    const auto* null_name = field("field", false);
    const bool field_ok = name || (null_name && *null_name == "null");
    if (object_root && schema && *schema == "fantasy-world-generator.failure" && version && *version == "2" && code && !code->empty() && message && field_ok) {
        doc.valid = true; doc.code = *code; doc.message = *message;
        if (name) doc.field = *name; else doc.field_null = true;
    }
    return doc;
}

// Template tokens are whole arguments; substituted values may contain any character.
std::vector<std::string> substitute(const std::vector<std::string>& command, const std::map<std::string, std::string>& values) {
    std::vector<std::string> out;
    for (const auto& argument : command) {
        bool replaced = false;
        for (const auto& [key, value] : values)
            if (argument == "{" + key + "}") { out.push_back(value); replaced = true; break; }
        if (!replaced) {
            if (argument.find_first_of("{}") != std::string::npos) throw std::runtime_error("Unknown command placeholder: " + argument);
            out.push_back(argument);
        }
    }
    return out;
}

void write_text(const fs::path& path, const std::string& text) {
    std::ofstream out(path, std::ios::binary);
    out.exceptions(std::ios::badbit | std::ios::failbit);
    out << text;
}

std::string bridge_identity() {
    std::ostringstream out;
    out << "{\"compiler\":\"MSVC\",\"msvcFullVersion\":" << _MSC_FULL_VER << ",\"cmake\":" << escape(WORLDBRIDGE_CMAKE_VERSION)
        << ",\"protocol\":" << protocol_version << "}";
    return out.str();
}

void emit(const std::string& line) { std::cout << line << '\n'; std::cout.flush(); }
void phase(const std::string& name) { emit("{\"protocol\":1,\"event\":\"phase\",\"phase\":" + escape(name) + "}"); }

struct Args {
    std::map<std::string, std::string> values;
    static Args parse(const std::vector<std::string>& args, const std::vector<std::string>& allowed) {
        Args out;
        for (size_t i = 1; i < args.size(); ++i) {
            const auto& key = args[i];
            if (std::find(allowed.begin(), allowed.end(), key) == allowed.end()) throw std::runtime_error("Unknown argument: " + key);
            if (i + 1 >= args.size()) throw std::runtime_error("Missing value for " + key);
            if (!out.values.emplace(key, args[++i]).second) throw std::runtime_error("Repeated argument: " + key);
        }
        return out;
    }
    std::string get(const std::string& key) const { auto it = values.find(key); return it == values.end() ? std::string() : it->second; }
    std::uint32_t number(const std::string& key, std::uint32_t fallback) const {
        auto value = get(key);
        if (value.empty()) return fallback;
        if (value.size() > 9 || value.find_first_not_of("0123456789") != std::string::npos) throw std::runtime_error("Invalid number for " + key);
        return static_cast<std::uint32_t>(std::stoul(value));
    }
};

// Imported DLLs present beside the producer would load ahead of System32 and are not pinned.
std::vector<std::string> local_dependencies(const fs::path& producer, const std::vector<std::string>& dlls) {
    std::vector<std::string> found;
    std::error_code ec;
    for (const auto& name : dlls)
        if (fs::exists(producer.parent_path() / wide(name), ec)) found.push_back(name);
    return found;
}

}  // namespace

const char* outcome_name(Outcome outcome) {
    switch (outcome) {
    case Outcome::succeeded: return "succeeded";
    case Outcome::refused: return "refused";
    case Outcome::producer_protocol_error: return "producer_protocol_error";
    case Outcome::missing_executable: return "missing_executable";
    case Outcome::pin_mismatch: return "pin_mismatch";
    case Outcome::invalid_request: return "invalid_request";
    case Outcome::launch_failed: return "launch_failed";
    case Outcome::timeout: return "timeout";
    case Outcome::cancelled: return "cancelled";
    case Outcome::crashed: return "crashed";
    case Outcome::memory_limit: return "memory_limit";
    case Outcome::exit_failure: return "exit_failure";
    case Outcome::output_invalid: return "output_invalid";
    case Outcome::publish_failed: return "publish_failed";
    case Outcome::bridge_error: return "bridge_error";
    }
    return "unknown";
}

std::vector<std::string> utf8_arguments() {
    int count = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!argv) throw std::runtime_error("Command line unavailable");
    std::vector<std::string> out;
    for (int i = 0; i < count; ++i) out.push_back(narrow(argv[i]));
    LocalFree(argv);
    return out;
}

std::string sha256_file(const fs::path& path) {
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file) throw std::runtime_error("Cannot open for hashing: " + narrow(path.wstring()));
    return hash_handle(file.value);
}

ProducerLock read_lock(const fs::path& lock_path) {
    std::ifstream in(lock_path, std::ios::binary);
    if (!in) throw std::runtime_error("Cannot open producer lock");
    ProducerLock lock;
    std::map<std::string, std::pair<std::string, bool>> scalars;
    std::map<std::string, std::vector<std::pair<std::string, bool>>> arrays;
    std::set<std::string> array_paths;
    auto join = [](const worldbridge_json::Path& p, size_t n) {
        std::string key; for (size_t i = 0; i < n; ++i) key += (i ? "." : "") + p[i]; return key;
    };
    worldbridge_json::Reader reader(in, [&](const worldbridge_json::Path& p, const std::string& v, bool quoted) {
        if (p.empty()) return;
        const std::string parent = join(p, p.size() - 1);
        if (array_paths.count(parent)) arrays[parent].push_back({v, quoted});
        else scalars[join(p, p.size())] = {v, quoted};
    }, [&](const worldbridge_json::Path& p, bool array) { if (array) array_paths.insert(join(p, p.size())); });
    reader.parse();
    auto text = [&](const std::string& key) {
        auto it = scalars.find(key);
        if (it == scalars.end() || !it->second.second) throw std::runtime_error("Lock field must be a string: " + key);
        return it->second.first;
    };
    auto integer = [&](const std::string& key) {
        auto it = scalars.find(key);
        if (it == scalars.end() || it->second.second || it->second.first.empty() || it->second.first.size() > 6 ||
            it->second.first.find_first_not_of("0123456789") != std::string::npos) throw std::runtime_error("Lock field must be an integer: " + key);
        return std::stoi(it->second.first);
    };
    auto strings = [&](const std::string& key) {
        std::vector<std::string> out;
        for (const auto& [value, quoted] : arrays[key]) { if (!quoted || value.empty()) throw std::runtime_error("Lock array must hold strings: " + key); out.push_back(value); }
        return out;
    };
    if (text("schema") != "mnbl.producer-lock" || integer("version") != 1) throw std::runtime_error("Unsupported producer lock schema");
    lock.sha256 = text("producer.sha256"); lock.capabilities_sha256 = text("producer.capabilitiesSha256");
    lock.help_sha256 = text("producer.helpSha256"); lock.ucrtbase_sha256 = text("runtime.ucrtbaseSha256");
    lock.source_commit = text("producer.sourceCommit"); lock.generator_schema = text("producer.generatorSchema");
    lock.schema_version = integer("producer.schemaVersion"); lock.split_version = integer("producer.splitVersion");
    lock.generate_command = strings("producer.commands.generate"); lock.capabilities_command = strings("producer.commands.capabilities");
    lock.help_command = strings("producer.commands.help"); lock.required_dlls = strings("runtime.requiredDlls");
    for (const auto* hash : {&lock.sha256, &lock.capabilities_sha256, &lock.help_sha256, &lock.ucrtbase_sha256})
        if (!is_hex(*hash, 64)) throw std::runtime_error("Producer lock hash is not lowercase SHA-256");
    if (!is_hex(lock.source_commit, 40)) throw std::runtime_error("Producer lock source commit is not a 40-hex commit");
    if (lock.generator_schema != supported_generator_schema || lock.schema_version != supported_schema_version || lock.split_version != supported_split_version)
        throw std::runtime_error("Producer lock contract is not the one this bridge reads");
    // Reject unknown placeholders now, not after a job exists.
    substitute(lock.generate_command, {{"request", "r"}, {"split", "s"}});
    substitute(lock.capabilities_command, {{"output", "o"}});
    substitute(lock.help_command, {});
    auto has = [](const std::vector<std::string>& v, const char* t) { return std::find(v.begin(), v.end(), t) != v.end(); };
    if (!has(lock.generate_command, "{request}") || !has(lock.generate_command, "{split}")) throw std::runtime_error("Lock generate command lacks {request}/{split}");
    if (!has(lock.capabilities_command, "{output}") || lock.help_command.empty() || lock.required_dlls.empty()) throw std::runtime_error("Lock preflight commands incomplete");
    return lock;
}

std::vector<std::string> pe_imports(const fs::path& executable) {
    std::ifstream in(executable, std::ios::binary);
    std::vector<unsigned char> image((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    auto read32 = [&](size_t offset) -> std::uint32_t {
        if (offset + 4 > image.size()) throw std::runtime_error("PE read out of bounds");
        std::uint32_t v; std::memcpy(&v, image.data() + offset, 4); return v;
    };
    auto read16 = [&](size_t offset) -> std::uint16_t {
        if (offset + 2 > image.size()) throw std::runtime_error("PE read out of bounds");
        std::uint16_t v; std::memcpy(&v, image.data() + offset, 2); return v;
    };
    if (image.size() < 64 || image[0] != 'M' || image[1] != 'Z') throw std::runtime_error("Not a PE image");
    const size_t nt = read32(0x3c);
    if (read32(nt) != 0x00004550) throw std::runtime_error("Missing PE signature");
    const size_t file_header = nt + 4, optional = file_header + 20;
    const std::uint16_t sections = read16(file_header + 2), optional_size = read16(file_header + 16);
    if (read16(optional) != 0x20b) throw std::runtime_error("Only PE32+ images are supported");
    const std::uint32_t import_rva = read32(optional + 112 + 8);  // data directory 1
    const size_t section_table = optional + optional_size;
    auto to_offset = [&](std::uint32_t rva) -> size_t {
        for (std::uint16_t i = 0; i < sections; ++i) {
            const size_t s = section_table + i * 40u;
            const std::uint32_t va = read32(s + 12), raw_size = read32(s + 16), raw = read32(s + 20), virtual_size = read32(s + 8);
            if (rva >= va && rva < va + std::max(raw_size, virtual_size)) return raw + (rva - va);
        }
        throw std::runtime_error("PE RVA outside sections");
    };
    std::vector<std::string> names;
    if (!import_rva) return names;
    for (size_t d = to_offset(import_rva);; d += 20) {
        const std::uint32_t name_rva = read32(d + 12);
        if (!name_rva && !read32(d) && !read32(d + 16)) break;
        size_t at = to_offset(name_rva); std::string name;
        while (at < image.size() && image[at] && name.size() < 256) name += static_cast<char>(image[at++]);
        names.push_back(name);
        if (names.size() > 512) throw std::runtime_error("PE import bound exceeded");
    }
    return names;
}

LaunchResult run_supervised(const Launch& launch) {
    LaunchResult result;
    auto start = std::chrono::steady_clock::now();
    auto elapsed = [&] { return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count()); };
    Handle job(CreateJobObjectW(nullptr, nullptr));
    if (!job) { result.win32_error = GetLastError(); return result; }
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    // Kill-on-close: if this bridge dies, the producer tree dies. No unhandled-exception dialogs.
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
    if (launch.memory_limit_bytes) { limits.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_JOB_MEMORY; limits.JobMemoryLimit = static_cast<SIZE_T>(launch.memory_limit_bytes); }
    if (!SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) { result.win32_error = GetLastError(); return result; }
    Handle port(CreateIoCompletionPort(INVALID_HANDLE_VALUE, nullptr, 0, 1));
    if (port) {
        JOBOBJECT_ASSOCIATE_COMPLETION_PORT association{reinterpret_cast<PVOID>(1), port.value};
        SetInformationJobObject(job.value, JobObjectAssociateCompletionPortInformation, &association, sizeof(association));
    }
    auto drain_port = [&] {
        DWORD message = 0; ULONG_PTR key = 0; LPOVERLAPPED overlapped = nullptr;
        while (port && GetQueuedCompletionStatus(port.value, &message, &key, &overlapped, 0))
            if (message == JOB_OBJECT_MSG_JOB_MEMORY_LIMIT || message == JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT) result.memory_limit_hit = true;
    };

    SECURITY_ATTRIBUTES inherit{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    HANDLE out_read = nullptr, out_write = nullptr, err_read = nullptr, err_write = nullptr;
    if (!CreatePipe(&out_read, &out_write, &inherit, 0)) { result.win32_error = GetLastError(); return result; }
    Handle o_read(out_read), o_write(out_write);
    if (!CreatePipe(&err_read, &err_write, &inherit, 0)) { result.win32_error = GetLastError(); return result; }
    Handle e_read(err_read), e_write(err_write);
    SetHandleInformation(o_read.value, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(e_read.value, HANDLE_FLAG_INHERIT, 0);
    Handle nul(CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &inherit, OPEN_EXISTING, 0, nullptr));
    if (!nul) { result.win32_error = GetLastError(); return result; }

    // Only these three handles are inherited; the job is assigned at creation; imports prefer System32.
    HANDLE inherited[3] = {nul.value, o_write.value, e_write.value};
    HANDLE jobs[1] = {job.value};
    DWORD64 mitigation = PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_PREFER_SYSTEM32_ALWAYS_ON;
    SIZE_T attribute_size = 0;
    InitializeProcThreadAttributeList(nullptr, 3, 0, &attribute_size);
    std::vector<unsigned char> attribute_storage(attribute_size);
    auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attribute_storage.data());
    if (!InitializeProcThreadAttributeList(attributes, 3, 0, &attribute_size)) { result.win32_error = GetLastError(); return result; }
    std::unique_ptr<std::remove_pointer_t<LPPROC_THREAD_ATTRIBUTE_LIST>, void (*)(LPPROC_THREAD_ATTRIBUTE_LIST)> attribute_guard(attributes, DeleteProcThreadAttributeList);
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof(inherited), nullptr, nullptr) ||
        !UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobs, sizeof(jobs), nullptr, nullptr) ||
        !UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY, &mitigation, sizeof(mitigation), nullptr, nullptr)) {
        result.win32_error = GetLastError(); return result;
    }

    // Minimal environment: system locations plus a private temp directory; nothing else is inherited.
    wchar_t system_root[MAX_PATH] = {};
    if (!GetSystemWindowsDirectoryW(system_root, MAX_PATH)) { result.win32_error = GetLastError(); return result; }
    const std::wstring root(system_root), temp = launch.temp_directory.wstring();
    std::vector<std::wstring> environment = {
        L"PATH=" + root + L"\\System32;" + root, L"SystemDrive=" + root.substr(0, 2), L"SystemRoot=" + root,
        L"TEMP=" + temp, L"TMP=" + temp, L"windir=" + root };
    if (launch.threads >= 0) environment.push_back(L"FWG_THREADS=" + std::to_wstring(launch.threads));
    std::sort(environment.begin(), environment.end(), [](const std::wstring& a, const std::wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) < 0; });
    std::wstring block;
    for (const auto& entry : environment) { block += entry; block.push_back(L'\0'); result.environment.push_back(narrow(entry)); }
    block.push_back(L'\0');

    std::wstring command = quote(launch.executable.wstring());
    for (const auto& argument : launch.arguments) command += L" " + quote(wide(argument));
    result.command_line = command;
    std::vector<wchar_t> command_buffer(command.begin(), command.end()); command_buffer.push_back(L'\0');

    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = nul.value; startup.StartupInfo.hStdOutput = o_write.value; startup.StartupInfo.hStdError = e_write.value;
    startup.lpAttributeList = attributes;
    PROCESS_INFORMATION info{};
    // Suspended until the launched image is checked against the pinned file.
    if (!CreateProcessW(launch.executable.c_str(), command_buffer.data(), nullptr, nullptr, TRUE,
            CREATE_SUSPENDED | DETACHED_PROCESS | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,  // no console host
            block.data(), launch.working_directory.c_str(), &startup.StartupInfo, &info)) {
        result.win32_error = GetLastError(); return result;
    }
    Handle process(info.hProcess), thread(info.hThread);
    o_write.reset(); e_write.reset(); nul.reset();
    if (launch.expected_identity.valid) {
        std::wstring image(32768, L'\0'); DWORD length = static_cast<DWORD>(image.size());
        FileIdentity actual;
        if (QueryFullProcessImageNameW(process.value, 0, image.data(), &length)) {
            image.resize(length);
            Handle file(CreateFileW(image.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr));
            if (file) actual = identity_of(file.value);
        }
        if (!actual.valid || actual.volume != launch.expected_identity.volume || actual.index != launch.expected_identity.index) {
            result.identity_mismatch = true;
            TerminateJobObject(job.value, ERROR_ACCESS_DENIED);
            WaitForSingleObject(process.value, 10000);
            return result;
        }
    }
    result.started = true; result.pid = info.dwProcessId;
    ResumeThread(thread.value);
    if (launch.phase) launch.phase("launched");

    std::atomic<bool> drain_failed{false};
    auto drain = [&](HANDLE pipe, const fs::path& path, std::uint64_t& total, bool& truncated) {
        std::ofstream file(path, std::ios::binary);
        if (!file) drain_failed = true;
        char buffer[4096]; DWORD read = 0;
        while (ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr) && read > 0) {
            const std::uint64_t keep = total >= launch.log_cap ? 0 : std::min<std::uint64_t>(read, launch.log_cap - total);
            if (keep && file) { file.write(buffer, static_cast<std::streamsize>(keep)); if (!file) drain_failed = true; }
            if (keep < read) truncated = true;
            total += read;
        }
    };
    std::thread out_thread(drain, o_read.value, launch.stdout_log, std::ref(result.stdout_bytes), std::ref(result.stdout_truncated));
    std::thread err_thread(drain, e_read.value, launch.stderr_log, std::ref(result.stderr_bytes), std::ref(result.stderr_truncated));

    while (WaitForSingleObject(process.value, 25) == WAIT_TIMEOUT) {
        drain_port();
        std::error_code ignored;
        const bool cancel = !launch.cancel_file.empty() && fs::exists(launch.cancel_file, ignored);
        const bool expired = launch.timeout_ms && elapsed() > launch.timeout_ms;
        if (cancel || expired) {
            result.cancelled = cancel; result.timed_out = !cancel && expired;
            if (launch.phase) launch.phase("terminating");
            TerminateJobObject(job.value, cancel ? ERROR_CANCELLED : ERROR_TIMEOUT);
            WaitForSingleObject(process.value, 10000);
            break;
        }
    }
    // Descendants left behind by a finished producer are owned too; they never outlive this call.
    // Count them from the job's PID list: ActiveProcesses lags briefly after the main process exits.
    auto others = [&]() -> std::uint32_t {
        std::vector<unsigned char> storage(sizeof(JOBOBJECT_BASIC_PROCESS_ID_LIST) + 1024 * sizeof(ULONG_PTR));
        auto list = reinterpret_cast<JOBOBJECT_BASIC_PROCESS_ID_LIST*>(storage.data());
        if (!QueryInformationJobObject(job.value, JobObjectBasicProcessIdList, list, static_cast<DWORD>(storage.size()), nullptr))
            return list->NumberOfAssignedProcesses > 1 ? list->NumberOfAssignedProcesses - 1 : 0;
        std::uint32_t count = 0;
        for (DWORD i = 0; i < list->NumberOfProcessIdsInList; ++i) if (list->ProcessIdList[i] != info.dwProcessId) ++count;
        return std::max<std::uint32_t>(count, list->NumberOfAssignedProcesses > list->NumberOfProcessIdsInList ? list->NumberOfAssignedProcesses - 1 : 0u);
    };
    for (int i = 0; i < 20 && !result.cancelled && !result.timed_out && others(); ++i) Sleep(10);  // let exiting descendants finish
    result.stray_processes_terminated = others();
    TerminateJobObject(job.value, ERROR_PROCESS_ABORTED);  // unconditional: nothing in the job outlives the producer
    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
    for (int i = 0; i < 400; ++i) {
        if (!QueryInformationJobObject(job.value, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr) || !accounting.ActiveProcesses) break;
        Sleep(25);
    }
    result.total_processes = accounting.TotalProcesses;
    DWORD code = 0; GetExitCodeProcess(process.value, &code); result.exit_code = code;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION after{};
    if (QueryInformationJobObject(job.value, JobObjectExtendedLimitInformation, &after, sizeof(after), nullptr)) result.peak_job_memory = after.PeakJobMemoryUsed;
    drain_port();
    out_thread.join(); err_thread.join();
    result.log_write_failed = drain_failed;
    result.elapsed_ms = elapsed();
    return result;
}

Outcome classify(const LaunchResult& result, bool world_present, bool failure_document_valid) {
    if (result.identity_mismatch) return Outcome::pin_mismatch;
    if (!result.started) return Outcome::launch_failed;
    if (result.cancelled) return Outcome::cancelled;
    if (result.timed_out) return Outcome::timeout;
    switch (result.exit_code) {
    case 0xC0000135u: case 0xC0000139u: case 0xC0000142u: case 0xC000007Bu: return Outcome::launch_failed;  // loader could not start the image
    default: break;
    }
    if (result.memory_limit_hit && !(result.exit_code == 0 && world_present)) return Outcome::memory_limit;
    if (result.exit_code >= 0x80000000u) return Outcome::crashed;
    if (result.exit_code == 2) return failure_document_valid ? Outcome::refused : Outcome::producer_protocol_error;
    if (result.exit_code != 0) return Outcome::exit_failure;
    return world_present ? Outcome::succeeded : Outcome::output_invalid;
}

namespace {

struct Report {
    Outcome outcome = Outcome::invalid_request;
    std::string message, job, publish, producer, producer_sha256, lock_sha256;
    FailureDocument failure;
    LaunchResult launch;
    std::uint64_t memory_limit_bytes = 0;
    bool launched = false;
};

std::string outcome_json(const Report& r) {
    std::ostringstream out;
    out << "{\"protocol\":" << protocol_version << ",\"event\":\"outcome\",\"outcome\":" << escape(outcome_name(r.outcome))
        << ",\"message\":" << escape(r.message) << ",\"job\":" << escape(r.job)
        << ",\"published\":" << (r.outcome == Outcome::succeeded ? escape(r.publish) : "null")
        << ",\"producer\":{\"path\":" << escape(r.producer) << ",\"sha256\":" << (r.producer_sha256.empty() ? "null" : escape(r.producer_sha256))
        << ",\"lockSha256\":" << (r.lock_sha256.empty() ? "null" : escape(r.lock_sha256)) << "},\"launched\":" << (r.launched ? "true" : "false");
    if (r.launched) {
        out << ",\"exitCode\":" << r.launch.exit_code << ",\"exitCodeHex\":" << escape(hex(r.launch.exit_code))
            << ",\"timedOut\":" << (r.launch.timed_out ? "true" : "false") << ",\"cancelled\":" << (r.launch.cancelled ? "true" : "false")
            << ",\"memoryLimitBytes\":" << r.memory_limit_bytes << ",\"memoryLimitReached\":" << (r.launch.memory_limit_hit ? "true" : "false")
            << ",\"strayProcessesTerminated\":" << r.launch.stray_processes_terminated
            << ",\"logs\":{\"stdout\":\"producer-stdout.log\",\"stdoutTruncated\":" << (r.launch.stdout_truncated ? "true" : "false")
            << ",\"stderr\":\"producer-stderr.log\",\"stderrTruncated\":" << (r.launch.stderr_truncated ? "true" : "false")
            << ",\"writeFailed\":" << (r.launch.log_write_failed ? "true" : "false") << "}";
    }
    if (r.failure.valid)
        out << ",\"failure\":{\"code\":" << escape(r.failure.code) << ",\"message\":" << escape(r.failure.message)
            << ",\"field\":" << (r.failure.field_null ? "null" : escape(r.failure.field)) << "}";
    out << "}";
    return out.str();
}

std::string run_record_json(const Report& r) {
    std::ostringstream out;
    out << "{\"schema\":\"mnbl.producer-run-record\",\"version\":1,\"bridge\":" << bridge_identity();
    if (r.launched) {
        out << ",\"pid\":" << r.launch.pid << ",\"elapsedMilliseconds\":" << r.launch.elapsed_ms << ",\"peakJobMemoryBytes\":" << r.launch.peak_job_memory
            << ",\"totalProcesses\":" << r.launch.total_processes << ",\"stdoutBytes\":" << r.launch.stdout_bytes << ",\"stderrBytes\":" << r.launch.stderr_bytes
            << ",\"commandLine\":" << escape(narrow(r.launch.command_line)) << ",\"environment\":[";
        for (size_t i = 0; i < r.launch.environment.size(); ++i) out << (i ? "," : "") << escape(r.launch.environment[i]);
        out << "]";
    }
    out << "}";
    return out.str();
}

int finish(Report& r, const fs::path& job, bool job_created) {
    const std::string outcome = outcome_json(r);
    if (job_created) {
        write_text(job / "outcome.json", outcome);
        write_text(job / "run-record.json", run_record_json(r));
    }
    phase("completed");
    emit(outcome);
    return r.outcome == Outcome::succeeded ? 0 : 3;
}

bool usable_new_path(const fs::path& path) {
    std::error_code ec;
    return path.is_absolute() && !fs::exists(path, ec) && !ec;
}

// Exclusive: fails if another process created the same job directory first.
bool create_job_directory(const fs::path& job) {
    std::error_code ec;
    fs::create_directories(job.parent_path(), ec);
    return CreateDirectoryW(job.c_str(), nullptr) != 0;
}

}  // namespace

int produce_main(const std::vector<std::string>& args) {
    // Inherited by the producer: no crash/critical-error dialogs can block a supervised run.
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    // Everything that can be a usage error is decided before --job exists (exit 2, nothing written).
    const auto parsed = Args::parse(args, {"--lock", "--producer", "--request", "--job", "--publish", "--timeout-ms", "--cancel-file", "--memory-limit-mb", "--threads"});
    for (const char* required : {"--lock", "--producer", "--request", "--job", "--publish"})
        if (parsed.get(required).empty()) throw std::runtime_error(std::string("Missing ") + required);
    const std::uint32_t timeout = parsed.number("--timeout-ms", default_produce_timeout_ms);
    const std::uint64_t memory_limit = static_cast<std::uint64_t>(parsed.number("--memory-limit-mb", 0)) * 1048576ULL;
    const fs::path job = wide(parsed.get("--job")), publish = wide(parsed.get("--publish")), request = wide(parsed.get("--request"));
    const fs::path producer = wide(parsed.get("--producer")), lock_path = wide(parsed.get("--lock"));
    const fs::path cancel = parsed.get("--cancel-file").empty() ? fs::path() : fs::path(wide(parsed.get("--cancel-file")));
    Report r; r.job = narrow(job.wstring()); r.publish = narrow(publish.wstring()); r.producer = narrow(producer.wstring());
    r.memory_limit_bytes = memory_limit;
    phase("preflight");
    if (!usable_new_path(job) || !create_job_directory(job)) { r.message = "--job must be an absolute path that does not exist"; return finish(r, job, false); }
    try {
        fs::create_directories(job / "staging" / "tmp");
        const fs::path split = job / "staging" / "split";
        std::error_code ec;
        if (!request.is_absolute() || !fs::is_regular_file(request, ec)) { r.message = "--request must be an absolute path to an existing file"; return finish(r, job, true); }
        if (!ansi_representable(request.wstring()) || !ansi_representable(split.wstring())) { r.message = "Request and job paths must be representable in the system ANSI code page (producer argv)"; return finish(r, job, true); }
        if (!usable_new_path(publish) || !fs::is_directory(publish.parent_path(), ec)) { r.message = "--publish must be absolute, absent, with an existing parent"; return finish(r, job, true); }
        const std::wstring job_volume = volume_of(job), publish_volume = volume_of(publish.parent_path());
        if (job_volume.empty() || _wcsicmp(job_volume.c_str(), publish_volume.c_str()) != 0) { r.message = "--publish and --job must share a volume for atomic publication"; return finish(r, job, true); }
        if (!cancel.empty() && !cancel.is_absolute()) { r.message = "--cancel-file must be absolute"; return finish(r, job, true); }
        ProducerLock lock;
        try { lock = read_lock(lock_path); } catch (const std::exception& e) { r.message = std::string("Producer lock unusable: ") + e.what(); return finish(r, job, true); }
        r.lock_sha256 = lock.sha256;
        if (!fs::is_regular_file(producer, ec)) { r.outcome = Outcome::missing_executable; r.message = "Producer executable not found"; return finish(r, job, true); }
        Handle pinned = open_locked(producer);
        if (!pinned) { r.outcome = Outcome::launch_failed; r.message = "Producer could not be opened for pin verification (Win32 " + std::to_string(GetLastError()) + ")"; return finish(r, job, true); }
        r.producer_sha256 = hash_handle(pinned.value);
        if (r.producer_sha256 != lock.sha256) { r.outcome = Outcome::pin_mismatch; r.message = "Producer SHA-256 differs from the lock; not launched"; return finish(r, job, true); }
        const auto local = local_dependencies(producer, lock.required_dlls);
        if (!local.empty()) { r.outcome = Outcome::pin_mismatch; r.message = "Unpinned DLL beside the producer: " + local.front() + "; not launched"; return finish(r, job, true); }
        if (!cancel.empty() && fs::exists(cancel, ec)) { r.outcome = Outcome::cancelled; r.message = "Cancel requested before launch"; return finish(r, job, true); }

        Launch launch;
        launch.executable = final_path(pinned.value); launch.expected_identity = identity_of(pinned.value);
        launch.working_directory = job; launch.temp_directory = job / "staging" / "tmp";
        launch.stdout_log = job / "producer-stdout.log"; launch.stderr_log = job / "producer-stderr.log"; launch.cancel_file = cancel;
        launch.timeout_ms = timeout; launch.memory_limit_bytes = memory_limit;
        launch.threads = parsed.get("--threads").empty() ? -1 : static_cast<int>(parsed.number("--threads", 0));
        launch.arguments = substitute(lock.generate_command, {{"request", narrow(request.wstring())}, {"split", narrow(split.wstring())}});
        launch.phase = phase;
        r.launch = run_supervised(launch);
        pinned.reset();
        r.launched = r.launch.started;
        const bool world = fs::is_regular_file(split / "world.json", ec);
        r.failure = r.launch.started && r.launch.exit_code == 2 ? parse_failure(launch.stdout_log) : FailureDocument{};
        r.outcome = classify(r.launch, world, r.failure.valid);
        if (r.launch.identity_mismatch) r.message = "Launched image is not the pinned file; terminated before it ran";
        else if (!r.launch.started) r.message = "Producer launch failed (Win32 " + std::to_string(r.launch.win32_error) + ")";
        if (r.outcome == Outcome::succeeded) {
            phase("publishing");
            // Scanners can briefly hold handles inside a fresh directory; retry before giving up.
            DWORD error = 0;
            for (int attempt = 0; attempt < 20; ++attempt) {
                if (MoveFileExW(split.c_str(), publish.c_str(), MOVEFILE_WRITE_THROUGH)) { error = 0; break; }
                error = GetLastError();
                if (error != ERROR_ACCESS_DENIED && error != ERROR_SHARING_VIOLATION) break;
                Sleep(100);
            }
            if (error) { r.outcome = Outcome::publish_failed; r.message = "Staging could not be published (Win32 " + std::to_string(error) + ")"; }
            else r.message = "Producer output published";
        } else if (r.message.empty()) r.message = "Producer did not produce a publishable world";
        return finish(r, job, true);
    } catch (const std::exception& error) {
        r.outcome = Outcome::bridge_error; r.message = std::string("Bridge failure: ") + error.what();
        return finish(r, job, true);
    }
}

int preflight_main(const std::vector<std::string>& args) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    const auto parsed = Args::parse(args, {"--lock", "--producer", "--job", "--timeout-ms"});
    for (const char* required : {"--lock", "--producer", "--job"})
        if (parsed.get(required).empty()) throw std::runtime_error(std::string("Missing ") + required);
    const auto timeout = parsed.number("--timeout-ms", 30000);
    const fs::path job = wide(parsed.get("--job")), producer = wide(parsed.get("--producer")), lock_path = wide(parsed.get("--lock"));
    if (!usable_new_path(job) || !create_job_directory(job)) throw std::runtime_error("--job must be an absolute path that does not exist");
    fs::create_directories(job / "tmp");
    std::vector<std::string> checks;
    bool pass = true;
    auto check = [&](const std::string& name, bool ok, const std::string& detail) {
        pass = pass && ok;
        checks.push_back("{\"name\":" + escape(name) + ",\"passed\":" + (ok ? "true" : "false") + ",\"detail\":" + escape(detail) + "}");
    };
    phase("preflight");
    ProducerLock lock;
    try { lock = read_lock(lock_path); check("lock", true, "schema mnbl.producer-lock v1; source " + lock.source_commit); }
    catch (const std::exception& e) { check("lock", false, e.what()); }
    std::error_code ec;
    const bool present = fs::is_regular_file(producer, ec);
    check("producer_present", present, narrow(producer.wstring()));
    Handle pinned = present ? open_locked(producer) : Handle();
    const std::string actual = pinned ? hash_handle(pinned.value) : std::string();
    const bool pinned_ok = pass && actual == lock.sha256;
    check("producer_pin", pinned_ok, "actual " + actual + ", lock " + lock.sha256);
    if (pinned_ok) {
        auto imports = pe_imports(producer);
        std::vector<std::string> got, want;
        for (auto& name : imports) got.push_back(lower(name));
        for (auto& name : lock.required_dlls) want.push_back(lower(name));
        std::sort(got.begin(), got.end()); std::sort(want.begin(), want.end());
        check("producer_imports_match_lock", got == want, std::to_string(got.size()) + " imports");
        const auto local = local_dependencies(producer, lock.required_dlls);
        check("no_unpinned_local_dlls", local.empty(), local.empty() ? "none beside the producer" : local.front());
        // Resolution without loading: API sets are served by the pinned ucrtbase; real DLLs must be in System32.
        wchar_t system[MAX_PATH]; GetSystemDirectoryW(system, MAX_PATH);
        for (const auto& name : imports) {
            if (lower(name).rfind("api-ms-win-", 0) == 0) { check("runtime_dependency:" + name, true, "API set; served by pinned ucrtbase.dll"); continue; }
            const fs::path candidate = fs::path(system) / wide(name);
            const bool found = fs::is_regular_file(candidate, ec);
            check("runtime_dependency:" + name, found, found ? narrow(candidate.wstring()) + " sha256 " + sha256_file(candidate) : "not present in System32");
        }
        const fs::path ucrt = fs::path(system) / L"ucrtbase.dll";
        const std::string ucrt_hash = fs::is_regular_file(ucrt, ec) ? sha256_file(ucrt) : std::string();
        check("ucrtbase_matches_lock", ucrt_hash == lock.ucrtbase_sha256, "actual " + ucrt_hash + ", lock " + lock.ucrtbase_sha256);

        const FileIdentity identity = identity_of(pinned.value);
        const fs::path launch_path = final_path(pinned.value);
        auto run = [&](const std::string& name, const std::vector<std::string>& arguments) {
            Launch launch;
            launch.executable = launch_path; launch.expected_identity = identity;
            launch.working_directory = job; launch.temp_directory = job / "tmp";
            launch.stdout_log = job / (name + "-stdout.log"); launch.stderr_log = job / (name + "-stderr.log");
            launch.timeout_ms = timeout; launch.arguments = arguments;
            return run_supervised(launch);
        };
        const fs::path capabilities = job / "capabilities.json";
        auto caps = run("capabilities", substitute(lock.capabilities_command, {{"output", narrow(capabilities.wstring())}}));
        const std::string caps_hash = fs::is_regular_file(capabilities, ec) ? sha256_file(capabilities) : std::string();
        check("capabilities_command", caps.started && caps.exit_code == 0 && caps_hash == lock.capabilities_sha256,
              "exit " + std::to_string(caps.exit_code) + ", sha256 " + caps_hash + ", lock " + lock.capabilities_sha256);
        auto help = run("help", lock.help_command);
        const std::string help_hash = help.started && !help.stdout_truncated ? sha256_file(job / "help-stdout.log") : std::string();
        check("help_command", help.started && help.exit_code == 0 && help_hash == lock.help_sha256,
              "exit " + std::to_string(help.exit_code) + ", sha256 " + help_hash + ", lock " + lock.help_sha256);
    }
    std::ostringstream out;
    out << "{\"protocol\":" << protocol_version << ",\"event\":\"preflight\",\"passed\":" << (pass ? "true" : "false")
        << ",\"bridge\":" << bridge_identity() << ",\"launchedWithoutShell\":true,\"checks\":[";
    for (size_t i = 0; i < checks.size(); ++i) out << (i ? "," : "") << checks[i];
    out << "]}";
    write_text(job / "preflight.json", out.str());
    phase("completed");
    emit(out.str());
    return pass ? 0 : 3;
}

}  // namespace worldbridge
