// SPDX-License-Identifier: MIT
// kiln.hooks v2 registry engine: one physical detour per target for every kind of Kiln handler.
//
// This header is internal to kiln-core (the service in hooks.cpp is its only client). hooks_chain.cpp holds the engine,
// hooks_chain_asm.cpp the static machine code it jumps through; the test native/core/tests/hooks_v2_test.cpp builds
// both with a few stubs and runs them under Wine.
//
// Shape of a target (see docs/kiln/services/hooks.md for the user-facing description):
//
//   game call -> [inline hook at the target] -> entry stub -> head cell -> link 1 entry -> ... -> real function
//
//  - The entry stub records the game's return address in a per-thread table (caller()), preserving every register
//    and the flags, then jumps through the head cell.
//  - Every link owns a "next cell" holding the entry of the link after it (or the safetyhook trampoline of the real
//    function when it is the last live one). A raw link's continuation is a 6-byte `jmp [next cell]`; a structured
//    link reaches its continuation from C++; a mid link captures a register context and continues through the cell.
//  - Adding, ordering and revoking links only rewrites 8-byte cells (atomic on x64). Links, stubs and cells are never
//    freed: a revoked link's continuation keeps forwarding to the next live link, so stale callers stay safe.
#pragma once
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include "services/kiln_hooks.h"

namespace kiln::hooks::chain {

enum class Kind : uint8_t { Raw, Structured, V1, Mid };
const char* kind_name(Kind k);  // "raw" | "structured" | "v1" | "mid"

constexpr size_t kMaxTargets = 512;      // distinct hooked addresses (one caller-table slot each)
constexpr size_t kMaxLinks = 1u << 16;   // links ever created (stubs are never freed)

struct Order {
    int32_t priority = 0;
    std::string label, before, after;
};

// Where a foreign hook goes (module + offset), when it could be followed.
struct Dest {
    bool present = false;       // a jump/patch was found
    bool in_place = false;      // bytes changed but no jump decoded
    uintptr_t address = 0;      // final destination reached (0 when none)
    std::string module;         // file name of the module containing it ("" = none/unknown)
    uintptr_t offset = 0;       // address - module base
    bool own = false;           // the module is one of Kiln's ("kiln-*.dll")
};

// Everything the engine takes from its host. Production wires these to sigdb and the owner registry; the test fakes
// them. All may be null (the feature just reports nothing).
struct Env {
    // Copies n bytes of the exe *file's* code (relocated like the live image) at live address `a`. False when the file
    // view is unusable or `a` is outside .text.
    bool (*file_bytes)(uintptr_t a, uint8_t* out, size_t n) = nullptr;
    std::string (*symbol_at)(uintptr_t a) = nullptr;     // sigdb symbol name of an address ("" = none)
    std::string (*owner_name)(KilnOwner o) = nullptr;     // registry name of an owner
    uintptr_t image_lo = 0, image_hi = 0;                 // the exe's range: jumps ending outside it are "foreign"
    void (*changed)() = nullptr;                          // links changed (mutex held: only arm a timer)
};

bool init(const Env& env);   // false: no thread-local slot (the engine stays unavailable)
bool ready();

KilnResult install_raw(KilnOwner owner, uintptr_t target, const Order& order, void* fn, void** original,
                       KilnHandle* out);
KilnResult install_structured(KilnOwner owner, uintptr_t target, const Order& order, uint32_t arg_count,
                              KilnDetourFn2 fn, void* user, KilnHandle* out);
KilnResult install_v1(KilnOwner owner, uintptr_t target, int32_t priority, KilnDetourFn fn, void* user,
                      KilnHandle* out);
KilnResult install_mid(KilnOwner owner, uintptr_t target, const Order& order, KilnMidFn fn, void* user,
                       KilnHandle* out);

uintptr_t call_original2(KilnHookCall2* call);
uintptr_t call_original_v1(KilnHookCall* call);
uintptr_t caller_of(KilnHandle h);

// True when `h` is a link handle (revoke() of a patch handle is the caller's business).
bool is_link_handle(KilnHandle h);
KilnResult revoke(KilnHandle h);
size_t revoke_owner(KilnOwner owner);  // returns the number of links removed

struct Stats {
    uint32_t targets = 0, detour_targets = 0, mid_targets = 0, links = 0, raw_links = 0;
    uint32_t foreign_before = 0, foreign_over = 0;   // another module's hook (not Kiln's own kiln-*.dll)
    uint32_t own_before = 0, own_over = 0;           // a Kiln module's own safetyhook hook on a registry target
    uint64_t detour_calls = 0, mid_calls = 0;
};
Stats stats();

// ---- foreign hooks ----
// Follows a jump written over `at`: `bytes` are the 16 bytes found there. Pure analysis; reads memory it follows
// through guarded copies.
Dest analyze(uintptr_t at, const uint8_t* bytes);
// Takes the links that faulted and were disabled out of their chains (they already forward; this saves the hop).
void sweep();
// The first-world check has run: hooks.json says so ("worldChecked").
void note_world_checked();
// Re-reads every target's first bytes; records (and logs) the ones another module hooked over. Never unhooks.
// Returns the number of targets currently hooked over.
size_t check_foreign();

// A sigdb symbol whose live bytes differed from the exe file's before Kiln hooked (hooks.json "foreign").
struct ForeignSym {
    std::string symbol;
    uintptr_t rva = 0;
    Dest dest;
};

// ---- what is hooked ----
// A copy of one target's state for the map writer and for core code that reasons about overlaps (WP-G1).
struct TargetInfo {
    struct LinkRow {
        std::string owner, label;
        Kind kind = Kind::Raw;
        int32_t priority = 0;
    };
    uintptr_t address = 0, rva = 0;
    std::string symbol;               // sigdb name of the address ("" = none)
    std::string label;                // symbol, else the first link's label, else "rva 0x..."
    std::vector<LinkRow> links;       // live links in chain order (the first one runs first)
    bool has_before = false, has_over = false;
    Dest before;                      // a foreign hook was on the function when Kiln hooked it
    Dest over;                        // a foreign hook was found on top of Kiln's detour at the last check
};
std::vector<TargetInfo> targets();

// ---- the hook map ----
// Extensions append sections to the document (WP-G1: "overlaps", "rules"). Called with the registry mutex NOT held,
// in registration order, after the base sections are built.
using MapExtension = void (*)(void* json /* nlohmann::json* */, const char* phase);
void add_map_extension(MapExtension fn);
std::string map_text(const std::vector<ForeignSym>& foreign, const char* phase);

// For tests: number of links ever created, per-thread caller tables allocated, and a link's continuation / entry
// addresses.
size_t link_count();
size_t thread_tables();
struct LinkInfo {
    bool live = false;
    Kind kind = Kind::Raw;
    uintptr_t entry = 0, cont = 0;
    uintptr_t target = 0;
    std::string owner, label;
    int32_t priority = 0;
};
bool link_info(KilnHandle h, LinkInfo* out);
// The live links of a target in chain order (handles), for tests and the map.
std::vector<KilnHandle> chain_of(uintptr_t target);

}  // namespace kiln::hooks::chain

// Called from the static machine code (hooks_chain_asm.cpp); defined in hooks_chain.cpp.
extern "C" {
void* kiln_hk_alloc_block(void);
uintptr_t kiln_hk_structured_dispatch(const uintptr_t* regs, const uintptr_t* stack, void* link);
void kiln_hk_mid_dispatch(void* regs);
// The machine code itself (hooks_chain_asm.cpp).
void kiln_hk_slow(void);
void kiln_hk_structured(void);
void kiln_hk_mid(void);
}
