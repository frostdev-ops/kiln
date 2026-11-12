// SPDX-License-Identifier: MIT
// kiln.playeranim, portable part: the game's type layouts (reflection records), from two sources with one interface:
//   - the bundled tools/animcook/types/anim-types.json (structure only, for host tests with no game);
//   - an image of the game's exe, through native/services/types/scan.h (in the process, or an exe file mapped by hand).
// It is the C++ twin of tools/kfc/kfc/refl.py's TypeDb: the same lookups, the same base-first field order. No Windows, no
// game, no Kiln API, so tests/ runs it on the host.
#pragma once
#include <cstdint>
#include <deque>
#include <memory>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace ktypes {
struct Image;
}

namespace kpa {

// Kinds: the low byte of a record's +0x4C (refl.py's K_*).
enum Kind : uint32_t {
    K_NONE = 0, K_BOOL, K_U8, K_S8, K_U16, K_S16, K_U32, K_S32, K_U64, K_S64, K_F32, K_F64,
    K_ENUM = 0x0C, K_BM8, K_BM16, K_BM32, K_BM64, K_TYPEDEF, K_STRUCT, K_STATIC_ARRAY,
    K_DS_ARRAY = 0x14, K_DS_STRING, K_DS_OPTIONAL, K_DS_VARIANT,
    K_BLOB_ARRAY = 0x18, K_BLOB_STRING, K_BLOB_OPTIONAL, K_BLOB_VARIANT, K_OBJECT_REF, K_GUID,
};
// refl.KIND_NAMES, or nullptr for a kind it has no name for.
const char* kind_name(uint32_t kind);

struct Type;

struct Field {
    std::string name;
    std::string type;       // the field type's qualified name
    bool has_type = false;  // false: the record it points at is no type (refl.py: None)
    uint64_t offset = 0;
    const Type* t = nullptr;  // TypeDb::get(type), set by finish()
};

struct EnumValue {
    std::string name;
    uint64_t value = 0;
};

struct Type {
    uint64_t va = 0;   // the record's address when read from an image (a later address wins a name, as refl.extract sorts)
    std::string name;  // qualified, "keen::JointAnimation"
    uint32_t hash = 0, size = 0, count = 0, align = 0, kind = 0;
    std::string inner;  // typedef target, enum underlying type, element, base class
    bool has_inner = false;
    std::vector<Field> fields;
    bool has_fields = false;
    std::vector<EnumValue> values;
    bool has_values = false;
    // Set by TypeDb::finish().
    const Type* inner_t = nullptr;   // get(inner) when inner is written, else null
    std::vector<const Field*> all;   // all_fields(): the base chain's fields first
    // A decoded struct's member names: all's names, a repeated name once at its first place (a dict assignment keeps
    // the first place and the last value); slot[i]: where all[i] goes.
    std::vector<std::string_view> names;
    std::vector<uint32_t> slot;
};

// Type records by qualified name and by hash; a later record of the same name (or hash) wins, as in refl.py's TypeDb.
// From the bundled dump: every record, read-only, any thread. From an image: the table is indexed, and a record is
// built the first time a lookup asks for it, with every record it reaches (its inner and field types): a lookup may add
// records, so one thread at a time (the service's lock). Records never move once built.
class TypeDb {
public:
    TypeDb();
    ~TypeDb();
    TypeDb(const TypeDb&) = delete;
    TypeDb& operator=(const TypeDb&) = delete;
    TypeDb(TypeDb&&) noexcept;
    TypeDb& operator=(TypeDb&&) noexcept;

    // By qualified name; "0x..." is a hash, as refl.TypeDb.get takes it.
    const Type* get(std::string_view key) const;
    const Type* get(uint32_t hash) const;
    const Type* by_name(std::string_view name) const;
    // Follows typedefs (at most 16 steps, as refl.py).
    const Type* resolve(const Type* t) const;
    // A struct's fields with its base chain's, base first. Empty for anything else.
    const std::vector<const Field*>& all_fields(const Type* t) const;
    size_t size() const { return types_.size(); }
    const std::deque<Type>& types() const { return types_; }

    // Adds records (last wins), then finish() resolves the references. Nothing may be added after finish().
    void add(Type t);
    void finish();

    // anim-types.json, the dump refl.extract writes. false and *why on a malformed dump.
    static bool from_json(std::string_view text, TypeDb* out, std::string* why);
    // The type table of an image (scan.h's find_type_table), indexed by name and hash; records are built on demand
    // (see above), each as refl.extract reads it. The image must stay mapped as long as the TypeDb lives. false and *why
    // when the table is not found.
    static bool from_image(const ktypes::Image& im, TypeDb* out, std::string* why);
    // The same with the table's entries given (the addresses kiln.types' type_at returns): no scan of the image.
    static bool from_table(const ktypes::Image& im, const std::vector<uint64_t>& entries, TypeDb* out, std::string* why);
    // How many records of an image's table are indexed (0 for a dump).
    size_t indexed() const;

private:
    struct Index;
    const Type* load(uint64_t va) const;  // a record of the image and every record it reaches
    void finish_one(Type& t) const;
    const Type* insert(Type t) const;

    mutable std::deque<Type> types_;
    mutable std::unordered_map<std::string, const Type*> by_name_;
    mutable std::unordered_map<uint32_t, const Type*> by_hash_;
    mutable std::unordered_map<uint64_t, const Type*> by_va_;
    std::vector<const Field*> none_;
    std::unique_ptr<Index> index_;
};

}  // namespace kpa
