#include "worldbridge/json_stream.hpp"

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <unordered_set>

namespace worldbridge::json {
namespace {
constexpr std::size_t buffer_size = 1 << 20;
bool is_digit(int c) { return c >= '0' && c <= '9'; }
int hex_value(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}
void append_utf8(std::string& out, unsigned cp) {
    if (cp < 0x80) out += static_cast<char>(cp);
    else if (cp < 0x800) { out += static_cast<char>(0xc0 | (cp >> 6)); out += static_cast<char>(0x80 | (cp & 63)); }
    else if (cp < 0x10000) { out += static_cast<char>(0xe0 | (cp >> 12)); out += static_cast<char>(0x80 | ((cp >> 6) & 63)); out += static_cast<char>(0x80 | (cp & 63)); }
    else { out += static_cast<char>(0xf0 | (cp >> 18)); out += static_cast<char>(0x80 | ((cp >> 12) & 63)); out += static_cast<char>(0x80 | ((cp >> 6) & 63)); out += static_cast<char>(0x80 | (cp & 63)); }
}
// Frame states: what the parser expects next inside an open container.
enum State : int { ObjectOpen, ObjectKey, ObjectColon, ObjectAfter, ArrayOpen, ArrayValue, ArrayAfter };
}  // namespace

std::string Location::text() const {
    return file + ":" + std::to_string(line) + ":" + std::to_string(column) + " (byte " + std::to_string(offset) + ") at " + path;
}

Error::Error(std::string c, std::string m, Location w)
    : std::runtime_error(m + " [" + c + "] " + w.text()), code(std::move(c)), message(std::move(m)), where(std::move(w)) {}

Stream::Stream(const std::filesystem::path& file, Limits limits) : name(utf8(file)), limit(limits), buffer(buffer_size) {
    in.open(file, std::ios::binary);
    if (!in) throw Error("missing_file", "Cannot open JSON document", Location{name, 0, 1, 1, "$"});
}

int Stream::peek() {
    if (cursor == filled) {
        if (!in) return EOF;
        in.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
        filled = static_cast<std::size_t>(in.gcount());
        cursor = 0;
        if (filled == 0) return EOF;
    }
    return static_cast<unsigned char>(buffer[cursor]);
}

char Stream::take() {
    const int c = peek();
    if (c == EOF) fail("truncated_json", "Unexpected end of JSON");
    prev_offset = position; prev_line = line; prev_column = column;
    ++cursor;
    if (++position > limit.max_bytes) fail("limit_exceeded", "Document exceeds the byte contract");
    if (c == '\n') { ++line; column = 1; } else ++column;
    return static_cast<char>(c);
}

void Stream::white() { for (int c = peek(); c == ' ' || c == '\n' || c == '\r' || c == '\t'; c = peek()) take(); }
void Stream::mark() { token_offset = position; token_line = line; token_column = column; }

std::string Stream::path_of(std::size_t used) const {
    std::string out = "$";
    for (const auto& segment : prefix) out += segment;
    for (std::size_t i = 0; i < used && i < frames.size(); ++i) {
        const Frame& f = frames[i];
        if (f.array) { if (f.state != ArrayOpen) out += "[" + std::to_string(f.index) + "]"; }
        else if (f.state != ObjectOpen && f.state != ObjectKey) out += "." + f.key;
    }
    return out;
}

std::string Stream::path() const { return path_of(token_frames); }

Location Stream::where() const { return Location{name, token_offset, token_line, token_column, path()}; }

void Stream::fail(const std::string& code, const std::string& message) const {
    // Report the current read position for syntax errors, the token start for semantic ones.
    Location at{name, position, line, column, path_of(frames.size())};
    if (code != "truncated_json" && code != "invalid_json" && code != "limit_exceeded") at = where();
    throw Error(code, message, at);
}

void Stream::fail_previous(const std::string& code, const std::string& message) const {
    throw Error(code, message, Location{name, prev_offset, prev_line, prev_column, path_of(frames.size())});
}

void Stream::fail_at(const Mark& at, const std::string& code, const std::string& message) const {
    throw Error(code, message, Location{name, at.offset, at.line, at.column, path_of(frames.size())});
}

void Stream::read_string(std::string& out) {
    out.clear();
    if (take() != '"') fail_previous("invalid_json", "Expected a string");
    for (;;) {
        const Mark start = here();  // first byte of this character or escape sequence
        unsigned char c = static_cast<unsigned char>(take());
        if (c == '"') return;
        if (c < 32) fail_previous("invalid_json", "Control character in string");
        if (c == '\\') {
            const char e = take();
            switch (e) {
            case '"': case '\\': case '/': out += e; break;
            case 'b': out += '\b'; break; case 'f': out += '\f'; break; case 'n': out += '\n'; break;
            case 'r': out += '\r'; break; case 't': out += '\t'; break;
            case 'u': {
                auto quad = [&]() { unsigned n = 0; for (int i = 0; i < 4; ++i) { int h = hex_value(take()); if (h < 0) fail_previous("invalid_json", "Invalid Unicode escape"); n = n * 16 + static_cast<unsigned>(h); } return n; };
                unsigned cp = quad();
                if (cp >= 0xd800 && cp <= 0xdbff) {
                    if (take() != '\\' || take() != 'u') fail_at(start, "invalid_json", "Unpaired high surrogate");
                    const unsigned low = quad();
                    if (low < 0xdc00 || low > 0xdfff) fail_at(start, "invalid_json", "Invalid low surrogate");
                    cp = 0x10000 + ((cp - 0xd800) << 10) + (low - 0xdc00);
                } else if (cp >= 0xdc00 && cp <= 0xdfff) fail_at(start, "invalid_json", "Unpaired low surrogate");
                append_utf8(out, cp);
                break;
            }
            default: fail_previous("invalid_json", "Invalid escape");
            }
        } else if (c < 0x80) out += static_cast<char>(c);
        else {
            unsigned cp = 0; int extra = 0; unsigned minimum = 0;
            if (c >= 0xc2 && c <= 0xdf) { cp = c & 31u; extra = 1; minimum = 0x80; }
            else if (c >= 0xe0 && c <= 0xef) { cp = c & 15u; extra = 2; minimum = 0x800; }
            else if (c >= 0xf0 && c <= 0xf4) { cp = c & 7u; extra = 3; minimum = 0x10000; }
            else fail_previous("invalid_json", "Invalid UTF-8 leading byte");
            out += static_cast<char>(c);
            for (int i = 0; i < extra; ++i) {
                const unsigned char next = static_cast<unsigned char>(take());
                if ((next & 0xc0) != 0x80) fail_previous("invalid_json", "Invalid UTF-8 continuation");
                cp = (cp << 6) | (next & 63u); out += static_cast<char>(next);
            }
            if (cp < minimum || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff)) fail_at(start, "invalid_json", "Invalid UTF-8 scalar");
        }
        if (out.size() > limit.max_token) fail("limit_exceeded", "String exceeds the token limit");
    }
}

void Stream::read_number() {
    value.clear();
    auto append = [&]() { if (value.size() >= 128) fail("invalid_json", "Numeric token too long"); value += take(); };
    if (peek() == '-') append();
    if (peek() == '0') append();
    else { if (!is_digit(peek())) fail("invalid_json", "Expected a digit"); while (is_digit(peek())) append(); }
    if (peek() == '.') { append(); if (!is_digit(peek())) fail("invalid_json", "Missing fractional digits"); while (is_digit(peek())) append(); }
    if (peek() == 'e' || peek() == 'E') { append(); if (peek() == '+' || peek() == '-') append(); if (!is_digit(peek())) fail("invalid_json", "Missing exponent digits"); while (is_digit(peek())) append(); }
    char* end = nullptr;
    const double n = std::strtod(value.c_str(), &end);
    if (end != value.c_str() + value.size()) fail("invalid_json", "Invalid number");
    if (!std::isfinite(n)) fail("nonfinite_number", "Number is not finite");
}

void Stream::literal(const char* word) {
    for (const char* p = word; *p; ++p) if (take() != *p) fail_previous("invalid_json", "Invalid literal");
}

Token Stream::begin_value() {
    mark();
    const int c = peek();
    if (c == '{' || c == '[') {
        if (frames.size() >= limit.max_depth) fail("limit_exceeded", "Nesting exceeds the depth limit");
        token_frames = frames.size();
        take();
        frames.push_back(Frame{c == '[', 0, {}, c == '[' ? ArrayOpen : ObjectOpen});
        keysets.emplace_back();
        return current = (c == '[' ? Token::BeginArray : Token::BeginObject);
    }
    token_frames = frames.size();
    if (c == '"') { read_string(value); return current = Token::String; }
    if (c == 't') { literal("true"); value = "true"; return current = Token::True; }
    if (c == 'f') { literal("false"); value = "false"; return current = Token::False; }
    if (c == 'n') { literal("null"); value = "null"; return current = Token::Null; }
    if (c == '-' || is_digit(c)) { read_number(); return current = Token::Number; }
    if (c == EOF) fail("truncated_json", "Unexpected end of JSON");
    fail("invalid_json", "Unexpected character");
}

Token Stream::close(Token kind) {
    mark();
    take();
    frames.pop_back();
    open_key_bytes -= keysets.back().bytes;
    keysets.pop_back();
    token_frames = frames.size();
    value.clear();
    return current = kind;
}

Token Stream::next() {
    for (;;) {
        white();
        if (frames.empty()) {
            if (!started) { started = true; return begin_value(); }
            if (fragment || peek() == EOF) { mark(); token_frames = 0; return current = Token::End; }
            mark(); token_frames = 0;
            fail("trailing_data", "Trailing data after the document");
        }
        Frame& f = frames.back();
        switch (f.state) {
        case ObjectOpen:
            if (peek() == '}') return close(Token::EndObject);
            [[fallthrough]];
        case ObjectKey: {
            if (peek() != '"') { if (peek() == EOF) fail("truncated_json", "Unexpected end of JSON"); fail("invalid_json", f.state == ObjectKey ? "Expected a key after ','" : "Expected a key"); }
            mark();
            std::string key;
            read_string(key);
            auto& set = keysets.back();
            const std::size_t cost = key.size() + 48;
            set.bytes += cost; open_key_bytes += cost;
            f.key = key;
            f.state = ObjectColon;
            token_frames = frames.size();
            if (set.keys.size() >= limit.max_keys_per_object || open_key_bytes > limit.max_open_key_bytes)
                throw Error("limit_exceeded", "Object key budget exceeded", where());
            if (!set.keys.insert(key).second) fail("duplicate_key", "Duplicate key");
            value = std::move(key);
            return current = Token::Key;
        }
        case ObjectColon:
            if (take() != ':') fail_previous("invalid_json", "Expected ':'");
            white();
            f.state = ObjectAfter;
            return begin_value();
        case ObjectAfter: {
            const int c = peek();
            if (c == ',') { take(); f.state = ObjectKey; continue; }
            if (c == '}') return close(Token::EndObject);
            if (c == EOF) fail("truncated_json", "Unexpected end of JSON");
            fail("invalid_json", "Expected ',' or '}'");
        }
        case ArrayOpen:
            if (peek() == ']') return close(Token::EndArray);
            f.state = ArrayAfter;
            return begin_value();
        case ArrayValue:
            f.state = ArrayAfter;
            return begin_value();
        case ArrayAfter: {
            const int c = peek();
            if (c == ',') { take(); ++f.index; f.state = ArrayValue; continue; }
            if (c == ']') return close(Token::EndArray);
            if (c == EOF) fail("truncated_json", "Unexpected end of JSON");
            fail("invalid_json", "Expected ',' or ']'");
        }
        }
    }
}

void Stream::skip() {
    if (current != Token::BeginObject && current != Token::BeginArray) return;
    const std::size_t target = frames.size() - 1;
    while (frames.size() > target) next();
}

void Stream::seek(const Location& at, const std::vector<std::string>& path_segments) {
    in.clear();
    in.seekg(static_cast<std::streamoff>(at.offset));
    if (!in) throw Error("io_error", "Cannot seek in JSON document", at);
    cursor = filled = 0;
    position = at.offset; line = at.line; column = at.column;
    frames.clear(); keysets.clear(); open_key_bytes = 0;
    prefix = path_segments;
    started = false; fragment = true; current = Token::End; value.clear();
}

}  // namespace worldbridge::json
