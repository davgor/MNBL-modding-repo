#include "worldbridge/canonical_json.hpp"

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <fstream>
#include <functional>
#include <memory>
#include <stdexcept>

#pragma comment(lib, "bcrypt.lib")

namespace worldbridge::cjson {
namespace {
using json::Stream;
using json::Token;

Value read(Stream& s) {
    Value v;
    v.at = s.where();
    switch (s.token()) {
    case Token::Null: v.kind = Value::Kind::Null; break;
    case Token::True: case Token::False: v.kind = Value::Kind::Bool; v.boolean = s.token() == Token::True; break;
    case Token::Number: v.kind = Value::Kind::Number; v.text = s.text(); break;
    case Token::String: v.kind = Value::Kind::String; v.text = s.text(); break;
    case Token::BeginArray:
        v.kind = Value::Kind::Array;
        while (s.next() != Token::EndArray) v.items.push_back(read(s));
        break;
    case Token::BeginObject:
        v.kind = Value::Kind::Object;
        while (s.next() != Token::EndObject) {
            const std::string key = s.text();
            s.next();
            v.members.emplace_back(key, read(s));
        }
        break;
    default: s.fail("invalid_json", "Unexpected token");
    }
    return v;
}

std::u16string utf16(const std::string& utf8) {
    std::u16string out;
    for (std::size_t i = 0; i < utf8.size();) {
        const unsigned char c = static_cast<unsigned char>(utf8[i]);
        unsigned cp; int extra;
        if (c < 0x80) { cp = c; extra = 0; } else if (c < 0xe0) { cp = c & 31u; extra = 1; } else if (c < 0xf0) { cp = c & 15u; extra = 2; } else { cp = c & 7u; extra = 3; }
        for (int k = 1; k <= extra; ++k) cp = (cp << 6) | (static_cast<unsigned char>(utf8[i + k]) & 63u);
        i += static_cast<std::size_t>(extra) + 1;
        if (cp >= 0x10000) { cp -= 0x10000; out.push_back(static_cast<char16_t>(0xd800 + (cp >> 10))); out.push_back(static_cast<char16_t>(0xdc00 + (cp & 0x3ff))); }
        else out.push_back(static_cast<char16_t>(cp));
    }
    return out;
}

void quote(std::string& out, const std::string& text) {
    static const char* hex = "0123456789abcdef";
    out += '"';
    for (unsigned char c : text) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (c < 0x20) { out += "\\u00"; out += hex[c >> 4]; out += hex[c & 15]; }
            else out += static_cast<char>(c);
        }
    }
    out += '"';
}

void write(std::string& out, const Value& v) {
    switch (v.kind) {
    case Value::Kind::Null: out += "null"; break;
    case Value::Kind::Bool: out += v.boolean ? "true" : "false"; break;
    case Value::Kind::Number: out += ecmascript_number(std::strtod(v.text.c_str(), nullptr)); break;
    case Value::Kind::String: quote(out, v.text); break;
    case Value::Kind::Array:
        out += '[';
        for (std::size_t i = 0; i < v.items.size(); ++i) { if (i) out += ','; write(out, v.items[i]); }
        out += ']';
        break;
    case Value::Kind::Object: {
        std::vector<const std::pair<std::string, Value>*> sorted;
        for (const auto& m : v.members) sorted.push_back(&m);
        std::sort(sorted.begin(), sorted.end(), [](const auto* a, const auto* b) { return utf16(a->first) < utf16(b->first); });
        out += '{';
        for (std::size_t i = 0; i < sorted.size(); ++i) {
            if (i) out += ',';
            quote(out, sorted[i]->first);
            out += ':';
            write(out, sorted[i]->second);
        }
        out += '}';
        break;
    }
    }
}

std::string hash_bytes(const void* data, std::size_t size, const std::function<bool(BCRYPT_HASH_HANDLE)>* feed = nullptr) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) throw std::runtime_error("SHA-256 provider unavailable");
    std::unique_ptr<void, void (*)(void*)> close_algorithm(algorithm, [](void* a) { BCryptCloseAlgorithmProvider(a, 0); });
    if (BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) != 0) throw std::runtime_error("SHA-256 hash creation failed");
    std::unique_ptr<void, void (*)(void*)> close_hash(hash, [](void* h) { BCryptDestroyHash(h); });
    if (feed) { if (!(*feed)(hash)) throw std::runtime_error("SHA-256 input failed"); }
    else if (size && BCryptHashData(hash, static_cast<PUCHAR>(const_cast<void*>(data)), static_cast<ULONG>(size), 0) != 0) throw std::runtime_error("SHA-256 update failed");
    unsigned char digest[32];
    if (BCryptFinishHash(hash, digest, sizeof(digest), 0) != 0) throw std::runtime_error("SHA-256 finish failed");
    static const char* hex = "0123456789abcdef";
    std::string out;
    for (unsigned char b : digest) { out += hex[b >> 4]; out += hex[b & 15]; }
    return out;
}
}  // namespace

const Value* Value::find(const std::string& key) const {
    for (const auto& m : members) if (m.first == key) return &m.second;
    return nullptr;
}

Value& Value::set(const std::string& key, Value value) {
    for (auto& m : members) if (m.first == key) { m.second = std::move(value); return m.second; }
    members.emplace_back(key, std::move(value));
    return members.back().second;
}

Value parse(const std::filesystem::path& file, std::uint64_t max_bytes) {
    json::Limits limits;
    limits.max_bytes = max_bytes;
    Stream s(file, limits);
    s.next();
    Value v = read(s);
    s.next();  // trailing data check
    return v;
}

std::string canonical(const Value& value) {
    std::string out;
    write(out, value);
    return out;
}

// ECMAScript Number::toString for a finite double (RFC 8785 section 3.2.2.3).
std::string ecmascript_number(double value) {
    if (value == 0) return "0";  // also -0
    std::string sign = value < 0 ? "-" : "";
    char buffer[64];
    const auto r = std::to_chars(buffer, buffer + sizeof(buffer), std::fabs(value), std::chars_format::scientific);
    const std::string sci(buffer, r.ptr);  // shortest round-trip: d[.ddd]e(+|-)XX
    const auto e = sci.find('e');
    std::string digits = sci.substr(0, e);
    digits.erase(std::remove(digits.begin(), digits.end(), '.'), digits.end());
    while (digits.size() > 1 && digits.back() == '0') digits.pop_back();
    const int k = static_cast<int>(digits.size());
    const int n = std::stoi(sci.substr(e + 1)) + 1;  // value = 0.digits * 10^n
    std::string out;
    if (k <= n && n <= 21) out = digits + std::string(static_cast<std::size_t>(n - k), '0');
    else if (0 < n && n <= 21) out = digits.substr(0, static_cast<std::size_t>(n)) + "." + digits.substr(static_cast<std::size_t>(n));
    else if (-6 < n && n <= 0) out = "0." + std::string(static_cast<std::size_t>(-n), '0') + digits;
    else {
        const int exponent = n - 1;
        out = digits.substr(0, 1) + (k > 1 ? "." + digits.substr(1) : "") + "e" + (exponent < 0 ? "-" : "+") + std::to_string(std::abs(exponent));
    }
    return sign + out;
}

std::string sha256_hex(const std::string& bytes) { return hash_bytes(bytes.data(), bytes.size()); }

std::string sha256_file_hex(const std::filesystem::path& file) {
    std::ifstream in(file, std::ios::binary);
    if (!in) throw std::runtime_error("Cannot open " + json::utf8(file));
    std::vector<char> buffer(1 << 20);
    const std::function<bool(BCRYPT_HASH_HANDLE)> feed = [&](BCRYPT_HASH_HANDLE hash) {
        while (in) {
            in.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
            const auto got = static_cast<ULONG>(in.gcount());
            if (got && BCryptHashData(hash, reinterpret_cast<PUCHAR>(buffer.data()), got, 0) != 0) return false;
        }
        return in.eof();
    };
    return hash_bytes(nullptr, 0, &feed);
}

}  // namespace worldbridge::cjson
