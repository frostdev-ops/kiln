// SPDX-License-Identifier: MIT
// kiln.playeranim, portable part: a reflected resource ("blob") as a value tree, and back, byte for byte as
// tools/kfc/kfc/blob.py (Decoder) and blobwrite.py (Encoder) do it:
//   - the root struct first; out-of-line data appended behind it depth first in field order, each block aligned to its
//     element's alignment, offsets self-relative, no padding at the end;
//   - a BlobArray of u8 with more than 64 elements is one byte string; a variant keeps its type and its struct's size.
// Enums and bitmasks stay numbers in the tree, with the names the Python maps them to, so that comparisons and the
// bytes are the Python's. Every read is bounds-checked: bytes that do not decode throw PyError("DecodeError", ...),
// never a fault. Also here: the Python text rules the reports need (repr, UTF-8 with replacement, GUID text).
#pragma once
#include <array>
#include <cstdint>
#include <exception>
#include <string>
#include <string_view>
#include <vector>

#include "typedb.h"

namespace kpa {

using Bytes = std::vector<uint8_t>;

// An exception the Python reference raises: `type` is its class name (DecodeError, EncodeError, KeyError, TypeError,
// IndexError, error for struct.error, MergeOff), `text` its str(). The merge reports "<type>: <text>" as
// playergraph.merge does (MergeOff: the text alone).
struct PyError : std::exception {
    std::string type, text;
    PyError(std::string type_, std::string text_) : type(std::move(type_)), text(std::move(text_)) {}
    const char* what() const noexcept override { return text.c_str(); }
};

// ---- small shared rules ----

uint32_t fnv1a(const void* data, size_t n);
inline uint32_t fnv1a(std::string_view s) { return fnv1a(s.data(), s.size()); }

// keen::Guid: 16 bytes in memory order ({u32, u16, u16, u8[8]}, little-endian groups), text lower-case 8-4-4-4-12.
struct Guid {
    std::array<uint8_t, 16> b{};
    bool zero() const;
    std::string text() const;
    // The 36-character hyphenated form, any case (uuid.UUID accepts more; the merge only ever parses this form).
    static bool parse(std::string_view s, Guid* out);
    bool operator==(const Guid&) const = default;
};
inline uint32_t fnv_guid(const Guid& g) { return fnv1a(g.b.data(), 16); }

// Python's bytes.decode("utf-8", "replace"): every maximal invalid subsequence becomes U+FFFD.
std::string utf8_replace(const uint8_t* p, size_t n);
// Code points of WTF-8 text (UTF-8 that may hold surrogates, as Python strings from JSON escapes can); a byte that is no
// part of a sequence counts as one.
std::vector<uint32_t> code_points(std::string_view s);
void append_utf8(std::string* out, uint32_t cp);  // surrogates as three bytes (WTF-8)
// Python's repr() of a str: quotes, escapes, and printable non-ASCII kept (see blob.cpp for the printable rule).
std::string py_repr(std::string_view s);
// "0x%08x", the Python's _h().
std::string hex8(uint64_t v);
// repr(float): the shortest digits that read back; fixed notation from 1e-4 up to 1e16, else an exponent.
std::string py_float_repr(double x);
// struct.pack("<f", x): x rounded to float32 (nearest, ties to even); false where CPython raises OverflowError (a finite
// value that rounds to infinity).
bool to_f32(double x, float* out);

// ---- the value tree ----

enum class VK : uint8_t {
    None,     // None: an empty variant or optional, a field whose type the reflection lacks
    Bool,     // u: 0 or 1
    Int,      // u: the number, sign-extended when `sign`
    Float,    // f (a float32 field holds a float32 value)
    Enum,     // u: the number as read (sign-extended for a signed underlying type); t: its type; name: names[]
    Bits,     // u: the number as read; t: the bitmask's type
    Str,      // s: text (a BlobString; a typedef whose target the reflection lacks: hex digits)
    Guid,     // g
    List,     // items
    Struct,   // names[i]: items[i]; a dict in the Python, so order does not matter to equality
    Variant,  // s: the type's qualified name ($type), items[0]: the value
    Unknown,  // a variant of a type the reflection lacks: u its hash, size its $size
    Hex,      // s: the bytes of a BlobArray<u8> of more than 64 elements ({"bytes": ...})
    Raw,      // s: the bytes of a kind the codec does not decode ({"$raw": ..., "$kind": ...}); u: the kind
};

struct Value {
    VK k = VK::None;
    bool sign = false;
    int32_t name = -1;  // Enum: index of the first value entry that matches (the Python's name), -1 when none
    uint64_t u = 0, size = 0;
    double f = 0;
    const Type* t = nullptr;
    std::string s;
    Guid g;
    std::vector<Value> items;
    // Struct: member names. They point into the TypeDb the value was decoded with, or at string literals: a tree must
    // not outlive its TypeDb, and set() takes only keys that live as long.
    std::vector<std::string_view> names;

    static Value none() { return {}; }
    static Value boolean(bool b);
    static Value integer(uint64_t v);
    static Value real(double v);
    static Value guid(const Guid& g);
    static Value text(std::string v);
    static Value list(std::vector<Value> v = {});
    static Value strukt();
    static Value hk(uint64_t v);  // {"value": v}, a HashKey32 as the Python writes it

    // dict.get / [] / assignment / pop on a Struct.
    const Value* get(std::string_view key) const;
    Value* get(std::string_view key);
    const Value& at(std::string_view key) const;  // PyError("KeyError", "'key'") when missing (or not a Struct)
    Value& at(std::string_view key);
    void set(std::string_view key, Value v);
    bool erase(std::string_view key);
    // The Int under member "value" ({"value": id}): KeyError/TypeError as the Python would raise them.
    uint64_t id() const;
    // An Enum's name in the Python's tree ("" when it is a number there).
    std::string_view enum_name() const;
    bool is(std::string_view enum_name_) const { return k == VK::Enum && name >= 0 && enum_name() == enum_name_; }
};

// Python ==, for values of the trees below (dict equality ignores order; NaN is unequal to itself; True == 1 == 1.0).
bool equal(const Value& a, const Value& b);

class Decoder {
public:
    explicit Decoder(const TypeDb& db, uint64_t max_array = uint64_t{1} << 20) : db_(db), max_array_(max_array) {}
    // decode(type, buf, off): DecodeError "unknown type ..." when `t` is null.
    Value decode(const Type* t, const uint8_t* b, size_t n, uint64_t off = 0, const std::string& type_name = "") const;
    Value decode(const Type* t, const Bytes& b) const { return decode(t, b.data(), b.size()); }

private:
    Value value(const Type* t, const uint8_t* b, size_t n, int64_t o, int depth) const;
    const TypeDb& db_;
    uint64_t max_array_;
};

class Encoder {
public:
    explicit Encoder(const TypeDb& db) : db_(db) {}
    Bytes encode(const Type* t, const Value& v, const std::string& type_name = "") const;

private:
    void write(Bytes& b, const Type* t, const Value& v, uint64_t o) const;
    uint64_t reserve(Bytes& b, uint64_t size, uint64_t align) const;
    const TypeDb& db_;
};

}  // namespace kpa
