// Pull-style streaming JSON tokenizer with exact error locations (file, byte, line, column, JSON path).
// Reads through a fixed 1 MiB buffer. Memory is bounded by the limits below (token size, nesting depth and
// the budget for keys of all currently open objects, kept for duplicate detection), not by file size.
#pragma once
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <vector>

namespace worldbridge::json {

// Paths as UTF-8 text and back, independent of the system code page (Windows paths may hold any character).
inline std::string utf8(const std::filesystem::path& p) { const auto s = p.generic_u8string(); return std::string(s.begin(), s.end()); }
inline std::filesystem::path from_utf8(const std::string& s) { return std::filesystem::path(std::u8string(s.begin(), s.end())); }

struct Location {
    std::string file;
    std::uint64_t offset = 0, line = 1, column = 1;
    std::string path;  // JSONPath-style, e.g. $.layers.height[3][7]
    std::string text() const;
};

// Every refusal carries a stable code and the location it was detected at.
class Error : public std::runtime_error {
public:
    Error(std::string code, std::string message, Location where);
    const std::string code;
    const std::string message;
    const Location where;
};

enum class Token { BeginObject, EndObject, BeginArray, EndArray, Key, String, Number, True, False, Null, End };

struct Limits {
    std::size_t max_token = 1048576;          // longest string or key, bytes
    std::size_t max_depth = 64;
    std::uint64_t max_bytes = 4ULL << 30;     // whole-file contract
    std::size_t max_keys_per_object = 65536;
    std::size_t max_open_key_bytes = 16u << 20;  // key text + 48 bytes per key, summed over all open objects
};

class Stream {
public:
    explicit Stream(const std::filesystem::path& file, Limits limits = {});
    // Advances to the next token. Scalars and keys are available through text() until the next call.
    Token next();
    Token token() const { return current; }
    const std::string& text() const { return value; }
    // Skips the value whose first token was just returned (no-op for scalars).
    void skip();
    // Location of the start of the current token, with the JSON path of the value it belongs to.
    Location where() const;
    std::string path() const;
    std::size_t depth() const { return frames.size(); }
    // Re-positions at a byte offset recorded earlier from where(); line/column and path are restored
    // from the caller-supplied location so later errors stay exact.
    void seek(const Location& at, const std::vector<std::string>& path_segments);
    [[noreturn]] void fail(const std::string& code, const std::string& message) const;
    // Syntax error at the byte take() just consumed (the offending byte itself).
    [[noreturn]] void fail_previous(const std::string& code, const std::string& message) const;
    struct Mark { std::uint64_t offset, line, column; };
    Mark here() const { return Mark{position, line, column}; }
    [[noreturn]] void fail_at(const Mark& at, const std::string& code, const std::string& message) const;
    std::uint64_t bytes_read() const { return position; }
    const std::string& file_name() const { return name; }

private:
    struct Frame { bool array; std::size_t index; std::string key; int state; };
    struct FrameKeys { std::unordered_set<std::string> keys; std::size_t bytes = 0; };
    std::ifstream in;
    std::string name;
    Limits limit;
    std::vector<char> buffer;
    std::size_t cursor = 0, filled = 0;
    std::uint64_t position = 0, line = 1, column = 1;
    std::uint64_t token_offset = 0, token_line = 1, token_column = 1;
    std::uint64_t prev_offset = 0, prev_line = 1, prev_column = 1;  // position of the last consumed byte
    std::size_t open_key_bytes = 0;
    std::size_t token_frames = 0;           // frames that make up the current token's path
    std::vector<Frame> frames;
    std::vector<FrameKeys> keysets;         // duplicate-key detection per open object
    std::vector<std::string> prefix;        // path segments of a fragment opened with seek()
    Token current = Token::End;
    std::string value;
    bool started = false, fragment = false;

    int peek();
    char take();
    void white();
    void mark();
    void read_string(std::string& out);
    void read_number();
    void literal(const char* word);
    Token begin_value();
    Token close(Token kind);
    std::string path_of(std::size_t frames_used) const;
};

}  // namespace worldbridge::json
