// Bounded, streaming JSON reader shared by the source reader and the producer supervisor.
#pragma once
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <functional>
#include <istream>
#include <ostream>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

namespace worldbridge_json {
namespace fs = std::filesystem;
using Path = std::vector<std::string>;
using Scalar = std::function<void(const Path&, const std::string&, bool)>;
using Container = std::function<void(const Path&, bool)>;

class Reader {
    std::istream& input;
    Scalar scalar;
    Container container;
    std::ostream* output = nullptr;
    Path path;
    size_t depth = 0;
    unsigned long long bytes = 0;
    static constexpr size_t max_token = 1048576;
    int peek() { return input.peek(); }
    char take() {
        int c = input.get();
        if (c == EOF) throw std::runtime_error("Unexpected end of JSON");
        if (++bytes > 4ULL * 1024 * 1024 * 1024) throw std::runtime_error("Input exceeds 4 GiB contract");
        if (output) output->put(static_cast<char>(c));
        return static_cast<char>(c);
    }
    void white() { while (peek() == ' ' || peek() == '\r' || peek() == '\n' || peek() == '\t') take(); }
    void expect(char c) { white(); if (take() != c) throw std::runtime_error("Invalid JSON delimiter"); }
    static bool digit(int c) { return c >= '0' && c <= '9'; }
    static int hex(char c) {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        throw std::runtime_error("Invalid Unicode escape");
    }
    unsigned unicode() { unsigned n = 0; for (int i = 0; i < 4; ++i) n = n * 16 + hex(take()); return n; }
    static void utf8(std::string& result, unsigned cp) {
        if (cp < 0x80) result += static_cast<char>(cp);
        else if (cp < 0x800) { result += static_cast<char>(0xc0 | (cp >> 6)); result += static_cast<char>(0x80 | (cp & 63)); }
        else if (cp < 0x10000) { result += static_cast<char>(0xe0 | (cp >> 12)); result += static_cast<char>(0x80 | ((cp >> 6) & 63)); result += static_cast<char>(0x80 | (cp & 63)); }
        else { result += static_cast<char>(0xf0 | (cp >> 18)); result += static_cast<char>(0x80 | ((cp >> 12) & 63)); result += static_cast<char>(0x80 | ((cp >> 6) & 63)); result += static_cast<char>(0x80 | (cp & 63)); }
    }
    std::string string() {
        if (take() != '"') throw std::runtime_error("Expected JSON string");
        std::string value;
        while (peek() != '"') {
            unsigned char c = static_cast<unsigned char>(take());
            if (c < 32) throw std::runtime_error("Control character in string");
            if (c == '\\') {
                char escaped = take();
                switch (escaped) {
                case '"': case '\\': case '/': value += escaped; break;
                case 'b': value += '\b'; break; case 'f': value += '\f'; break;
                case 'n': value += '\n'; break; case 'r': value += '\r'; break; case 't': value += '\t'; break;
                case 'u': {
                    unsigned cp = unicode();
                    if (cp >= 0xd800 && cp <= 0xdbff) {
                        if (take() != '\\' || take() != 'u') throw std::runtime_error("Unpaired high surrogate");
                        unsigned low = unicode();
                        if (low < 0xdc00 || low > 0xdfff) throw std::runtime_error("Invalid low surrogate");
                        cp = 0x10000 + ((cp - 0xd800) << 10) + low - 0xdc00;
                    } else if (cp >= 0xdc00 && cp <= 0xdfff) throw std::runtime_error("Unpaired low surrogate");
                    utf8(value, cp); break;
                }
                default: throw std::runtime_error("Invalid escape");
                }
            } else if (c < 0x80) value += static_cast<char>(c);
            else {
                unsigned cp = 0; int extra = 0; unsigned minimum = 0;
                if (c >= 0xc2 && c <= 0xdf) { cp = c & 31; extra = 1; minimum = 0x80; }
                else if (c >= 0xe0 && c <= 0xef) { cp = c & 15; extra = 2; minimum = 0x800; }
                else if (c >= 0xf0 && c <= 0xf4) { cp = c & 7; extra = 3; minimum = 0x10000; }
                else throw std::runtime_error("Invalid UTF-8 leading byte");
                value += static_cast<char>(c);
                for (int i = 0; i < extra; ++i) {
                    unsigned char next = static_cast<unsigned char>(take());
                    if ((next & 0xc0) != 0x80) throw std::runtime_error("Invalid UTF-8 continuation");
                    cp = (cp << 6) | (next & 63); value += static_cast<char>(next);
                }
                if (cp < minimum || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff)) throw std::runtime_error("Invalid UTF-8 scalar");
            }
            if (value.size() > max_token) throw std::runtime_error("JSON string exceeds 1 MiB");
        }
        take(); return value;
    }
    std::string number() {
        std::string value;
        auto append = [&]() { if (value.size() >= 128) throw std::runtime_error("Numeric token too long"); value += take(); };
        if (peek() == '-') append();
        if (peek() == '0') append();
        else { if (!digit(peek())) throw std::runtime_error("Expected digit"); while (digit(peek())) append(); }
        if (peek() == '.') { append(); if (!digit(peek())) throw std::runtime_error("Missing fractional digits"); while (digit(peek())) append(); }
        if (peek() == 'e' || peek() == 'E') { append(); if (peek() == '+' || peek() == '-') append(); if (!digit(peek())) throw std::runtime_error("Missing exponent"); while (digit(peek())) append(); }
        char* end = nullptr;
        const double n = std::strtod(value.c_str(), &end);
        if (!std::isfinite(n) || end != value.c_str() + value.size()) throw std::runtime_error("Nonfinite or invalid number");
        return value;
    }
    void literal(const std::string& wanted) {
        for (char c : wanted) if (take() != c) throw std::runtime_error("Invalid JSON literal");
        scalar(path, wanted, false);
    }
    void object() {
        expect('{'); white(); std::set<std::string> keys; size_t key_bytes = 0;
        if (peek() == '}') { take(); return; }
        while (true) {
            white(); auto key = string(); key_bytes += key.size();
            if (keys.size() >= 65536 || key_bytes > 4 * 1048576) throw std::runtime_error("Object key memory bound exceeded");
            if (!keys.insert(key).second) throw std::runtime_error("Duplicate JSON key");
            expect(':'); path.push_back(key); value(); path.pop_back(); white();
            char c = take(); if (c == '}') return; if (c != ',') throw std::runtime_error("Invalid object separator");
        }
    }
    void array() {
        expect('['); white(); size_t index = 0;
        if (peek() == ']') { take(); return; }
        while (true) {
            path.push_back(std::to_string(index++)); value(); path.pop_back(); white();
            char c = take(); if (c == ']') return; if (c != ',') throw std::runtime_error("Invalid array separator");
        }
    }
    void value() {
        if (++depth > 64) throw std::runtime_error("JSON nesting exceeds 64");
        white(); const int c = peek();
        if (c == '{') { container(path, false); object(); } else if (c == '[') { container(path, true); array(); }
        else if (c == '"') scalar(path, string(), true);
        else if (c == 't') literal("true"); else if (c == 'f') literal("false"); else if (c == 'n') literal("null");
        else scalar(path, number(), false);
        --depth;
    }
public:
    Reader(std::istream& stream, Scalar callback, Container containers = [](const Path&, bool) {}) : input(stream), scalar(std::move(callback)), container(std::move(containers)) {}
    void parse() { value(); white(); if (peek() != EOF) throw std::runtime_error("Trailing JSON data"); }
    void split(const fs::path& destination, const std::set<std::string>& selected) {
        expect('{'); white(); std::set<std::string> keys;
        if (peek() == '}') throw std::runtime_error("Empty world");
        while (true) {
            white(); auto key = string();
            if (keys.size() >= 1024 || key.size() > 128) throw std::runtime_error("Root key bound exceeded");
            if (!keys.insert(key).second) throw std::runtime_error("Duplicate root key");
            expect(':'); std::ofstream file;
            if (selected.contains(key)) { file.open(destination / (key + ".json"), std::ios::binary); file.exceptions(std::ios::badbit | std::ios::failbit); output = &file; }
            path.push_back(key); value(); path.pop_back(); output = nullptr; white();
            char c = take(); if (c == '}') break; if (c != ',') throw std::runtime_error("Invalid root separator");
        }
        white(); if (peek() != EOF) throw std::runtime_error("Trailing data");
        for (const auto& key : selected) if (!keys.contains(key)) throw std::runtime_error("Required block missing: " + key);
    }
};


// Minimal JSON string escaping for documents this bridge writes.
inline std::string escape(const std::string& text) {
    std::string out = "\"";
    for (unsigned char c : text) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (c < 0x20) { static const char* digits = "0123456789abcdef"; out += "\\u00"; out += digits[c >> 4]; out += digits[c & 15]; }
            else out += static_cast<char>(c);
        }
    }
    return out + "\"";
}
}
