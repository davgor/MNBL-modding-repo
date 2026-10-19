// Test double for the pinned producer. Same argv shape as fwg; behavior comes from the request file.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <new>
#include <string>
#include <vector>

namespace fs = std::filesystem;

static std::wstring option(int argc, wchar_t** argv, const wchar_t* name) {
    for (int i = 1; i + 1 < argc; ++i) if (std::wstring(argv[i]) == name) return argv[i + 1];
    return {};
}

static void write(const fs::path& path, const std::string& text) { std::ofstream(path, std::ios::binary) << text; }

int wmain(int argc, wchar_t** argv) {
    if (argc >= 2 && std::wstring(argv[1]) == L"--help") { std::fputs("fake producer help\n", stdout); return 0; }
    if (argc >= 2 && std::wstring(argv[1]) == L"capabilities") { write(option(argc, argv, L"--output"), "{\"fake\":1}\n"); return 0; }
    if (argc >= 3 && std::wstring(argv[1]) == L"grandchild-hang") {
        write(argv[2], std::to_string(GetCurrentProcessId())); Sleep(INFINITE);
    }
    if (argc < 2 || std::wstring(argv[1]) != L"generate") return 64;
    std::ifstream in(option(argc, argv, L"--request"), std::ios::binary);
    std::stringstream buffer; buffer << in.rdbuf();
    const std::string request = buffer.str();
    const auto at = request.find("\"scenario\":\"");
    const std::string scenario = at == std::string::npos ? "success" : request.substr(at + 12, request.find('"', at + 12) - at - 12);
    const fs::path split = option(argc, argv, L"--split");
    write("fake-ran.txt", std::to_string(GetCurrentProcessId()));   // working directory is the job
    auto world = [&] { fs::create_directories(split); write(split / "world.json", "{}"); };
    auto partial = [&] { fs::create_directories(split); write(split / "partial.json", "{"); };
    if (scenario == "success") { world(); return 0; }
    if (scenario == "refuse") {
        std::fputs("{\"schema\":\"fantasy-world-generator.failure\",\"schema_version\":2,\"code\":\"INVALID_INPUT\",\"message\":\"size too small\",\"field\":\"size\",\"received\":2}\n", stdout);
        return 2;
    }
    if (scenario == "refuse_null_field") {
        // Shape FWGC++ uses for cross-field refusals.
        std::fputs("{\"schema\":\"fantasy-world-generator.failure\",\"schema_version\":2,\"code\":\"INVALID_INPUT\",\"message\":\"relief exceeds radius budget\",\"field\":null}\n", stdout);
        return 2;
    }
    if (scenario == "allocate") {
        try { std::vector<std::vector<char>> blocks; for (;;) blocks.emplace_back(16 * 1048576, 'x'); }
        catch (const std::bad_alloc&) { std::fputs("out of memory\n", stderr); return 99; }
    }
    if (scenario == "protocol_error") { std::fputs("not a failure document\n", stdout); return 2; }
    if (scenario == "exit_failure") { std::fputs("fatal\n", stderr); return 1; }
    if (scenario == "empty_success") return 0;
    if (scenario == "crash") { partial(); volatile int* null = nullptr; *null = 1; return 0; }
    if (scenario == "hang") { partial(); Sleep(INFINITE); }
    if (scenario == "big_output") { std::string chunk(65536, 'x'); for (int i = 0; i < 48; ++i) std::fwrite(chunk.data(), 1, chunk.size(), stdout); world(); return 0; }
    if (scenario == "environment") {
        std::string all;
        for (auto block = GetEnvironmentStringsA(), p = block; *p; p += std::strlen(p) + 1) all += std::string(p) + "\n";
        write("fake-env.txt", all); world(); return 0;
    }
    if (scenario == "grandchild") {
        wchar_t self[MAX_PATH]; GetModuleFileNameW(nullptr, self, MAX_PATH);
        std::wstring command = L"\"" + std::wstring(self) + L"\" grandchild-hang \"" + fs::absolute("grandchild.pid").wstring() + L"\"";
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION info{};
        if (!CreateProcessW(self, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &info)) return 70;
        CloseHandle(info.hThread); CloseHandle(info.hProcess);
        for (int i = 0; i < 200 && !fs::exists("grandchild.pid"); ++i) Sleep(10);
        world(); return 0;
    }
    return 65;
}
