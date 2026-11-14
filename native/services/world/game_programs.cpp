// SPDX-License-Identifier: MIT
// kiln.world v4, the game's own programs: a directory of each world's program table (by name, any thread), the server
// world's players mask (what makes its scheduler run program slices as tasks on the game's workers) and the lab's
// parallel switch. Engine facts: the program table (world/programs.*), an entry's name {char*, u64} at +0x10 and its
// update at +0x28 (what ProgramRunner calls for every slice), the descriptor's flags at +0x180 (EngineDesc), and the
// u16 players mask at storage + world/storage.players_mask, read by StepPrograms' prologue (sigdb feature
// world.systems; chapter 06a section 2.4: bit (id - 1) mod 16 per live player entity, non-zero = parallel).
#include <algorithm>
#include <cstdio>
#include <memory>
#include <unordered_map>
#include <unordered_set>
#include <thread>

#ifdef _WIN32
#include <windows.h>
#endif

#include "services/kiln_caps.h"
#include "wraps.h"

namespace kw {
namespace {

constexpr uint32_t kKinds = KILN_WORLD_KINDS;
constexpr uint16_t kForcedBit = 0x8000;      // player 16's bit: no lab has a 16th player
constexpr uint32_t kMaxForcedSteps = 3000;   // about two minutes at 24.5 Hz: forced windows stay bounded
constexpr uint32_t kEntryName = 0x10;        // {char*, u64}; world/programs.names says the same when it resolves
constexpr uint32_t kEntryUpdate = 0x28;      // what ProgramRunner calls for every slice (types/ProgramRunner)
constexpr uint32_t kMaxName = 120;

struct Entry {
    int32_t index = 0;
    uint16_t id = 0;
    const std::string* name = nullptr;  // interned: valid for the life of the process
    uint32_t desc_flags = 0, entry_flags = 0, flags = 0;
    uintptr_t entry = 0, desc = 0, update = 0;
};

struct Directory {
    uint32_t kind = 0;
    uintptr_t world = 0;
    uint64_t generation = 0;
    uint16_t count = 0;
    std::vector<Entry> entries;
    std::unordered_map<std::string_view, int32_t> by_name;
    uint32_t game = 0, no_update = 0;
};

bool g_table_ok = false;     // the table constants resolved
bool g_threads_ok = false;   // world.systems: the players mask
int64_t g_players_mask = -1; // storage-relative
int64_t g_name_at = kEntryName;
uintptr_t g_image_lo = 0, g_image_hi = 0;
const KilnCapsApi* g_caps = nullptr;

std::mutex g_dir_mu;  // the directories and the name pool; never taken on a slice path
std::shared_ptr<const Directory> g_dir[kKinds];
std::unordered_set<std::string> g_names;
std::atomic<uint64_t> g_generation[kKinds];  // bumped by every start and stop of a world of that kind
std::atomic<uint32_t> g_world_tid[kKinds];
std::atomic<uint64_t> g_step[kKinds];

// The players mask and the lab switch (server world).
std::atomic<uint32_t> g_mask{0xFFFFu};
bool g_switch_enabled = false;
std::mutex g_switch_mu;
struct SwitchRequest {
    bool pending = false, on = false;
    uint32_t steps = 0;
} g_switch_req;
bool g_holding = false;                    // server world's Pre only
std::atomic<uint32_t> g_steps_left{0};
std::atomic<bool> g_forced{false};

int64_t cst(const char* n) {
    int64_t v = -1;
    return g_api->constant(KilnStr{n, std::strlen(n)}, &v) == KILN_OK ? v : -1;
}

bool api_has_worlds() { return g_api->size >= offsetof(KilnApi, session) + sizeof(void*); }

uintptr_t world_of(uint32_t kind) {
    if (api_has_worlds()) return g_api->world(kind);
    return kind == KILN_WORLD_SERVER ? g_api->server_world() : 0;
}

bool in_image(uintptr_t a) { return a && a >= g_image_lo && a < g_image_hi; }

const std::string* intern(std::string_view s) {  // g_dir_mu held
    return &*g_names.emplace(s).first;
}

bool ref_of(const Directory& d, int32_t index, ProgramRef* out) {
    if (index < 0 || static_cast<size_t>(index) >= d.entries.size()) return false;
    const Entry& e = d.entries[static_cast<size_t>(index)];
    out->index = e.index;
    out->name = e.name;
    out->entry = e.entry;
    out->desc = e.desc;
    out->no_update = !e.update;
    return true;
}

void report(const char* name, uint32_t state, uint32_t reason, const char* text) {
    if (!g_caps || !g_caps->report) return;
    g_caps->report(g_owner, KilnStr{name, std::strlen(name)}, state, reason, KilnStr{text, std::strlen(text)});
}

std::shared_ptr<const Directory> directory(uint32_t kind) {
    if (kind >= kKinds) return nullptr;
    std::lock_guard lock(g_dir_mu);
    return g_dir[kind];
}

// Reads the table of `world` (its thread, Pre: the table is final once the world finalized its programs).
std::shared_ptr<Directory> read_table(uint32_t kind, uintptr_t world, uint16_t count) {
    auto d = std::make_shared<Directory>();
    d->kind = kind;
    d->world = world;
    d->generation = g_generation[kind].load();
    d->count = count;
    d->entries.reserve(count);
    std::lock_guard lock(g_dir_mu);
    for (uint16_t i = 0; i < count; ++i) {
        Entry e;
        e.index = i;
        e.entry = world + static_cast<uintptr_t>(L.programs_table) + static_cast<uintptr_t>(i) * static_cast<uintptr_t>(L.programs_stride);
        uint8_t raw[0x30]{};
        if (!seh_copy(raw, reinterpret_cast<const void*>(e.entry), sizeof raw)) break;
        std::memcpy(&e.desc, raw, 8);
        std::memcpy(&e.id, raw + L.entry_id, 2);
        std::memcpy(&e.entry_flags, raw + L.entry_flags, 4);
        std::memcpy(&e.update, raw + kEntryUpdate, 8);
        e.update = wraps_resolve(e.entry, e.update);  // the game's own update, also under a wrap
        uintptr_t name_ptr = 0;
        uint64_t name_len = 0;
        std::memcpy(&name_ptr, raw + g_name_at, 8);
        std::memcpy(&name_len, raw + g_name_at + 8, 8);
        char name[kMaxName + 1]{};
        if (!name_ptr || !name_len || name_len > kMaxName || !seh_copy(name, reinterpret_cast<const void*>(name_ptr), name_len))
            std::snprintf(name, sizeof name, "#%u", static_cast<unsigned>(i));
        e.name = intern(std::string_view(name, std::strlen(name)));
        if (e.desc) seh_copy(&e.desc_flags, reinterpret_cast<const void*>(e.desc + offsetof(EngineDesc, flags)), 4);
        if (in_image(e.desc)) e.flags |= KILN_GAME_PROGRAM_GAME;
        if (!e.update) e.flags |= KILN_GAME_PROGRAM_NO_UPDATE;
        if (e.flags & KILN_GAME_PROGRAM_GAME) {
            ++d->game;
            if (!e.update) ++d->no_update;
        }
        if (!d->by_name.emplace(*e.name, e.index).second)
            KW_W("world %u: two programs named %s (#%d and #%d); the first is the one found by name", kind,
                 e.name->c_str(), d->by_name.at(*e.name), e.index);
        d->entries.push_back(e);
    }
    return d;
}

void ensure_directory(uint32_t kind, uintptr_t world) {
    uint16_t count = 0;
    if (!seh_copy(&count, reinterpret_cast<const void*>(world + static_cast<uintptr_t>(L.programs_count)), 2)) return;
    if (L.programs_max > 0 && count > L.programs_max) return;
    const auto now = directory(kind);
    if (now && now->world == world && now->generation == g_generation[kind].load() && now->count == count) return;
    auto d = read_table(kind, world, count);
    KW_I("world %u 0x%llx: program directory, %u programs (%u of the game's, %u of them with no update in this world)",
         kind, static_cast<unsigned long long>(world), static_cast<unsigned>(d->entries.size()), d->game, d->no_update);
    std::lock_guard lock(g_dir_mu);
    g_dir[kind] = std::move(d);
}

void drop_directory(uint32_t kind) {
    std::lock_guard lock(g_dir_mu);
    g_dir[kind].reset();
}

// ---- the players mask and the lab switch (server world's Pre) ----

uint16_t* mask_at(uintptr_t world) {
    return reinterpret_cast<uint16_t*>(storage_of(world) + static_cast<uintptr_t>(g_players_mask));
}

void let_go(uintptr_t world, const char* why) {
    if (!g_holding) return;
    uint16_t m = 0;
    if (world && seh_copy(&m, mask_at(world), 2)) {
        m = static_cast<uint16_t>(m & ~kForcedBit);
        seh_copy(mask_at(world), &m, 2);
    }
    g_holding = false;
    g_forced.store(false);
    g_steps_left.store(0);
    KW_I("lab parallel switch: players mask bit 15 cleared (%s); mask now 0x%04x", why, m);
}

void mask_pre(uintptr_t world) {
    if (!g_threads_ok) return;
    uint16_t m = 0;
    if (!seh_copy(&m, mask_at(world), 2)) {
        g_mask.store(0xFFFFu);
        return;
    }
    if (g_switch_enabled) {
        SwitchRequest req;
        {
            std::lock_guard lock(g_switch_mu);
            req = g_switch_req;
            g_switch_req.pending = false;
        }
        if (req.pending && !req.on) let_go(world, "switched off");
        if (req.pending && req.on) {
            if (g_holding) {
                g_steps_left.store(req.steps);
            } else if (m & kForcedBit) {
                KW_W("lab parallel switch: bit 15 of the players mask is set already (a player 16?); left alone");
            } else {
                const uint16_t before = m;
                m = static_cast<uint16_t>(m | kForcedBit);
                if (seh_copy(mask_at(world), &m, 2)) {
                    g_holding = true;
                    g_forced.store(true);
                    g_steps_left.store(req.steps);
                    KW_I("lab parallel switch: players mask 0x%04x -> 0x%04x for %u steps: the server world's parallel "
                         "path from the next step", before, m, req.steps);
                }
            }
        } else if (g_holding) {
            const uint32_t left = g_steps_left.load();
            if (left <= 1) let_go(world, "its steps ran out");
            else g_steps_left.store(left - 1);
        }
        seh_copy(&m, mask_at(world), 2);  // what the scheduler reads in this step
    }
    g_mask.store(m);
}

bool control_file(const char* leaf) {
    char dir[1024];
    size_t len = 0;
    if (g_api->path(KILN_PATH_CONTROL, dir, sizeof dir - 1, &len) != KILN_OK || len + std::strlen(leaf) + 2 > sizeof dir)
        return false;
    std::string path(dir, len);
    path += '\\';
    path += leaf;
#ifdef _WIN32
    wchar_t wide[1100];
    const int n = MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, wide, 1100);
    return n > 0 && GetFileAttributesW(wide) != INVALID_FILE_ATTRIBUTES;
#else
    return false;
#endif
}

void image_range() {
#ifdef _WIN32
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    IMAGE_DOS_HEADER dos{};
    IMAGE_NT_HEADERS64 nt{};
    if (!base || !seh_copy(&dos, reinterpret_cast<const void*>(base), sizeof dos) || dos.e_magic != IMAGE_DOS_SIGNATURE ||
        !seh_copy(&nt, reinterpret_cast<const void*>(base + static_cast<uintptr_t>(dos.e_lfanew)), sizeof nt) ||
        nt.Signature != IMAGE_NT_SIGNATURE)
        return;
    g_image_lo = base;
    g_image_hi = base + nt.OptionalHeader.SizeOfImage;
#endif
}

}  // namespace

// ---- service ----

void game_programs_resolve() {
    g_table_ok = L.programs_table >= 0 && L.programs_count >= 0 && L.programs_stride >= 0x30 && L.entry_flags >= 0 &&
                 L.entry_flags + 4 <= 0x30 && L.entry_id >= 0 && L.entry_id + 2 <= 0x30 && L.world_storage >= 0 &&
                 g_api->feature(kstr("world.programs"));
    if (const int64_t names = cst("world/programs.names"); names >= 0 && L.programs_table >= 0) {
        if (names - L.programs_table != kEntryName) {
            KW_E("program table: names at entry+0x%llx, kiln.world reads them at +0x%x; the program directory is off",
                 static_cast<unsigned long long>(names - L.programs_table), kEntryName);
            g_table_ok = false;
        }
    }
    g_players_mask = cst("world/storage.players_mask");
    g_threads_ok = g_api->feature(kstr("world.systems")) && g_players_mask > 0 && L.world_storage >= 0;
    image_range();
    const bool server = !api_has_worlds() || g_api->side() == KILN_SIDE_SERVER;
    if (control_file("systems.parallel")) {
        if (!server) KW_W("kiln/control/systems.parallel: the lab parallel switch is for dedicated servers; off");
        else if (!g_threads_ok) KW_W("kiln/control/systems.parallel: the players mask is not known on this build; the switch is off");
        else g_switch_enabled = true;
        if (g_switch_enabled) KW_W("lab parallel switch enabled (kiln/control/systems.parallel): a lab only, never a player's server");
    }
}

void game_programs_init() {
    g_caps = static_cast<const KilnCapsApi*>(g_api->service(kstr(KILN_CAPS_SERVICE), 1));
    if (g_threads_ok)
        report("systems.threads", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "the server world's players mask can be read");
    else
        report("systems.threads", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SYMBOL_MISSING,
               "sigdb feature world.systems (world/StepPrograms, world/storage.players_mask) is off on this build");
    KW_I("game programs: directory %s, threads %s (players mask at storage+0x%llx), lab parallel switch %s",
         g_table_ok ? "on" : "off", g_threads_ok ? "on" : "off", static_cast<unsigned long long>(g_players_mask),
         g_switch_enabled ? "ENABLED" : "off");
}

void game_programs_pre(uint32_t kind, const KilnTickInfo* tick) {
    if (kind >= kKinds || kind == KILN_WORLD_PREDICTION) return;
    g_world_tid[kind].store(current_thread_id(), std::memory_order_relaxed);
    if (tick) g_step[kind].store(tick->step, std::memory_order_relaxed);
    const uintptr_t world = world_of(kind);
    if (!world) return;
    if (g_table_ok) ensure_directory(kind, world);
    if (kind == KILN_WORLD_SERVER) mask_pre(world);
    if (g_table_ok) wraps_pre(kind, world);
}

void game_programs_world_event(const KilnWorldEvent* event) {
    if (!event || event->kind >= kKinds) return;
    g_generation[event->kind].fetch_add(1);
    wraps_world_event(event);
    if (event->event != KILN_WORLD_STOPPING) return;
    drop_directory(event->kind);
    if (event->kind == KILN_WORLD_SERVER && g_holding) {
        g_holding = false;
        g_forced.store(false);
        g_steps_left.store(0);
        KW_I("lab parallel switch: the server world stops; let go");
    }
    if (event->kind == KILN_WORLD_SERVER) g_mask.store(0xFFFFu);
}

uint32_t current_thread_id() {
#ifdef _WIN32
    return GetCurrentThreadId();
#else
    return static_cast<uint32_t>(std::hash<std::thread::id>{}(std::this_thread::get_id()) | 1u);
#endif
}

std::string module_name(uintptr_t address) {
#ifdef _WIN32
    HMODULE m = nullptr;
    wchar_t wide[MAX_PATH];
    if (!address || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                        reinterpret_cast<LPCWSTR>(address), &m))
        return {};
    const DWORD n = GetModuleFileNameW(m, wide, MAX_PATH);
    if (!n || n >= MAX_PATH) return {};
    const wchar_t* leaf = wide;
    for (const wchar_t* p = wide; *p; ++p)
        if (*p == L'\\' || *p == L'/') leaf = p + 1;
    char utf8[MAX_PATH * 3];
    const int len = WideCharToMultiByte(CP_UTF8, 0, leaf, -1, utf8, sizeof utf8, nullptr, nullptr);
    return len > 0 ? std::string(utf8) : std::string();
#else
    (void)address;
    return {};
#endif
}

bool game_program_lookup(uint32_t kind, std::string_view name, ProgramRef* out) {
    const auto d = directory(kind);
    if (!d) return false;
    const auto it = d->by_name.find(name);
    return it != d->by_name.end() && ref_of(*d, it->second, out);
}

bool game_program_at(uint32_t kind, int32_t index, ProgramRef* out) {
    const auto d = directory(kind);
    return d && ref_of(*d, index, out);
}

bool game_programs_has_directory(uint32_t kind) { return directory(kind) != nullptr; }

const std::string* game_program_intern(std::string_view name) {
    std::lock_guard lock(g_dir_mu);
    return intern(name);
}

uint32_t game_programs_world_tid(uint32_t kind) { return kind < kKinds ? g_world_tid[kind].load(std::memory_order_relaxed) : 0; }
uint64_t game_programs_step(uint32_t kind) { return kind < kKinds ? g_step[kind].load(std::memory_order_relaxed) : 0; }

// ---- the table ----

int32_t KILN_CALL game_program_find(uint32_t kind, KilnStr name) {
    const auto d = directory(kind);
    if (!d) return -1;
    const auto it = d->by_name.find(sv(name));
    return it == d->by_name.end() ? -1 : it->second;
}

KilnResult KILN_CALL game_program_info(uint32_t kind, int32_t index, KilnGameProgramInfo* out) {
    if (!out || out->size < sizeof(KilnGameProgramInfo)) return KILN_ERR_INVALID_ARGUMENT;
    const auto d = directory(kind);
    if (!d || index < 0 || static_cast<size_t>(index) >= d->entries.size()) return KILN_ERR_NOT_FOUND;
    const Entry& e = d->entries[static_cast<size_t>(index)];
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->world_kind = kind;
    out->index = e.index;
    out->id = e.id;
    out->name = kstr(*e.name);
    out->desc_flags = e.desc_flags;
    out->entry_flags = e.entry_flags;
    out->update_rva = in_image(e.update) ? e.update - g_image_lo : 0;
    out->flags = e.flags;
    game_program_wrap_stats(kind, e.name, out);
    return KILN_OK;
}

KilnResult KILN_CALL game_programs_status(KilnGameProgramsStatus* out) {
    if (!out || out->size < sizeof(KilnGameProgramsStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    for (uint32_t k = 0; k < kKinds; ++k) {
        const auto d = directory(k);
        if (!d) continue;
        out->directory[k] = 1;
        out->programs[k] = static_cast<uint32_t>(d->entries.size());
        out->game_programs[k] = d->game;
        out->no_update[k] = d->no_update;
    }
    out->threads = g_threads_ok;
    out->players_mask = static_cast<uint16_t>(g_mask.load());
    out->parallel_forced = g_forced.load();
    out->parallel_switch = g_switch_enabled;
    out->parallel_steps_left = g_steps_left.load();
    game_wraps_status(out);
    std::string why;
    if (!g_table_ok) why = "the program table's layout did not resolve on this build (world.programs)";
    else if (!g_threads_ok) why = "the players mask did not resolve on this build (world.systems)";
    std::snprintf(out->detail, sizeof out->detail, "%s", why.empty() ? game_wraps_off_reason() : why.c_str());
    return KILN_OK;
}

KilnResult KILN_CALL lab_parallel(uint32_t on, uint32_t steps) {
    if (!g_switch_enabled) return KILN_ERR_UNAVAILABLE;
    if (t_in_program || game_in_slice()) return KILN_ERR_WRONG_THREAD;
    if (on && (steps == 0 || steps > kMaxForcedSteps)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_switch_mu);
    g_switch_req = SwitchRequest{true, on != 0, on ? steps : 0};
    return KILN_OK;
}

// ---- tests ----
void game_programs_test_setup(uintptr_t image_lo, uintptr_t image_hi, bool table_ok, int64_t players_mask, bool switch_enabled) {
    g_image_lo = image_lo;
    g_image_hi = image_hi;
    g_table_ok = table_ok;
    g_players_mask = players_mask;
    g_threads_ok = players_mask > 0;
    g_switch_enabled = switch_enabled;
}

}  // namespace kw
