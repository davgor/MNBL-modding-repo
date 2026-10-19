// mnbl.canonical-json/1: RFC 8785 (JSON Canonicalization Scheme) over a small bounded JSON tree.
// Object members are sorted by UTF-16 code units, numbers use the ECMAScript Number-to-String form
// of their IEEE-754 double, strings escape only '"', '\\' and control characters, and no
// insignificant whitespace is written. Equivalent configurations therefore hash identically.
#pragma once
#include <cstdint>
#include <filesystem>
#include <string>
#include <utility>
#include <vector>
#include "worldbridge/json_stream.hpp"

namespace worldbridge::cjson {

struct Value {
    enum class Kind { Null, Bool, Number, String, Array, Object };
    Kind kind = Kind::Null;
    bool boolean = false;
    std::string text;  // number token or decoded string
    std::vector<Value> items;
    std::vector<std::pair<std::string, Value>> members;  // in document order; canonical() sorts
    json::Location at;

    const Value* find(const std::string& key) const;
    static Value string(std::string s) { Value v; v.kind = Kind::String; v.text = std::move(s); return v; }
    static Value integer(std::uint64_t n) { Value v; v.kind = Kind::Number; v.text = std::to_string(n); return v; }
    static Value object() { Value v; v.kind = Kind::Object; return v; }
    Value& set(const std::string& key, Value value);  // replaces or appends
};

// Parses a whole document (duplicate keys, non-finite numbers and trailing data refused by the tokenizer).
Value parse(const std::filesystem::path& file, std::uint64_t max_bytes = 1u << 20);
std::string canonical(const Value& value);
std::string ecmascript_number(double value);
std::string sha256_hex(const std::string& bytes);
std::string sha256_file_hex(const std::filesystem::path& file);

}  // namespace worldbridge::cjson
