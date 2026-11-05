// SPDX-License-Identifier: MIT
// kiln.settings: guarded access to game memory and the game's reflection of keen::ecs::GameSettings. Layout values
// come from sigdb/symbols/server.d/settings.json (see docs/re/settings.md). World singletons come from the core
// (api->singleton).
#pragma once
#include <windows.h>

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace kset {

struct Layout {
    // GameServer instance (SendServerInfo's argument)
    int64_t inst_settings = -1, inst_resend = -1, inst_world = -1, inst_preset = -1;
    // reflection type metadata / field record / enum value record
    int64_t type_hash = -1, type_qname = -1, type_inner = -1, type_size = -1, type_count = -1, type_kind = -1,
            type_fields = -1, type_enum_values = -1;
    int64_t field_stride = -1, field_type = -1, field_offset = -1, enum_stride = -1, enum_value = -1;
    // g38_daytime::Daytime
    bool daytime = false;
    int64_t dt_day = 0, dt_night = 0, dt_changes = 0, dt_tod = 0;
};

// Structured-exception guarded copies. False when any byte faulted.
bool read(uintptr_t address, void* out, size_t size);
bool write(uintptr_t address, const void* in, size_t size);
template <class T>
bool rd(uintptr_t address, T& out) {
    return address && read(address, &out, sizeof out);
}
// UTF-8 text at (ptr, len), at most 256 bytes; empty on fault.
std::string text(uintptr_t ptr, uint64_t len);

uint32_t fnv1a(std::string_view s);

// keen reflection primitive kinds (type + 0x4C)
enum : uint32_t {
    KIND_BOOL = 0x1, KIND_U8 = 0x2, KIND_S8 = 0x3, KIND_U16 = 0x4, KIND_S16 = 0x5, KIND_U32 = 0x6, KIND_S32 = 0x7,
    KIND_U64 = 0x8, KIND_S64 = 0x9, KIND_F32 = 0xA, KIND_ENUM = 0xC, KIND_TYPEDEF = 0x11, KIND_STRUCT = 0x12,
};

struct Option {
    std::string name;
    int64_t value;
};

// One reflected field, reduced to what kiln.settings can read and write.
struct Field {
    std::string name;
    uint32_t offset = 0;
    uint32_t width = 0;      // bytes
    uint32_t kind = 0;       // KILN_SETTING_* (0 = unsupported type)
    bool is_signed = false;  // INT / ENUM storage
    std::string type_name;   // qualified name of the field's type
    std::vector<Option> options;
};

struct Reflected {
    uint32_t size = 0;
    std::vector<Field> fields;
};

using TypeByHashFn = uintptr_t(__fastcall*)(uint32_t hash);
// TypeByHash under a guard. 0 when it faulted or the type is unknown.
uintptr_t type_by_hash(TypeByHashFn fn, uint32_t hash);
// Reads the reflected layout of a struct type. error = why not.
bool reflect(const Layout& L, uintptr_t type, std::string_view expect_qname, Reflected& out, std::string& error);

}  // namespace kset
