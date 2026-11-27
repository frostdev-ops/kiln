// SPDX-License-Identifier: MIT
// ShroudForge's engine hooks through the real kiln.hooks registry (hooks.cpp, hooks_chain*.cpp, safetyhook), under Wine,
// no game: native/plugins/shroudforge/tests/test-host-chain.sh.
//
// "The game" is this exe's `.game` section: six places shaped like the ones the ShroudForge profile names (four 9-byte
// places, one 5-byte function entry, one 7-byte place) plus two identical functions. "The exe file" is a pristine copy of
// that section that the stubbed sigdb scans. "kfc-runtime" is a stand-in (standin_install) that asks the host the way
// ShroudForge's installer does (find_signature, then install_hook), through the adapter's real host table (src/host_hooks.cpp).
//
// What it shows: with a foreign E9 jump (Enshwick/XHL style) already on one place, the adapter still finds the place in the
// file bytes, the hook goes into Kiln's chain in front of the foreign jump, the game's function still works, the callback
// gets the registers of its kind, and the foreign relay is still reached.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "common.h"
#include "image.h"
#include "kfc_host.h"
#include "kiln_hook.hpp"
#include "log.h"
#include "nlohmann/json.hpp"
#include "registry.h"
#include "services/hooks_chain.h"
#include "services/hooks_internal.h"
#include "services/kiln_build.h"
#include "services/kiln_hooks.h"
#include "services/service.h"
#include "sf_internal.h"
#include "sigdb.h"
#include "tick.h"

using json = nlohmann::json;
namespace chain = kiln::hooks::chain;

// ---------------------------------------------------------------------------------------------------------------------
// Stubs for the rest of kiln-core (as in native/core/tests/hooks_v2_test.cpp), with a sigdb that scans "the exe file".
namespace {
std::mutex g_log_mutex;
std::vector<std::string> g_lines;
std::vector<uint8_t> g_file;   // the pristine copy of the .game section
std::filesystem::path g_state_dir;
kiln::svc::Service* g_service = nullptr;
std::map<KilnOwner, std::string> g_owner_names = {{0, "kiln"}, {7, "early:kiln-shroudforge.dll"}};
}  // namespace

namespace kiln {
ImageInfo g_image;
const ImageInfo& image() { return g_image; }
const Paths& paths() {
    static Paths p;
    p.state = g_state_dir;
    return p;
}
std::string to_utf8(std::wstring_view w) {
    if (w.empty()) return {};
    std::string s(static_cast<size_t>(WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0,
                                                          nullptr, nullptr)),
                  0);
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), static_cast<int>(s.size()), nullptr,
                        nullptr);
    return s;
}
std::wstring to_wide(std::string_view s) {
    if (s.empty()) return {};
    std::wstring w(static_cast<size_t>(MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0)),
                   0);
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), static_cast<int>(w.size()));
    return w;
}
double now_ms() {
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return 1000.0 * static_cast<double>(c.QuadPart) / static_cast<double>(f.QuadPart);
}
std::string utc_now() { return "2026-01-01T00:00:00Z"; }
bool write_file_atomic(const std::filesystem::path& file, std::string_view text) {
    std::error_code ec;
    std::filesystem::create_directories(file.parent_path(), ec);
    std::ofstream out(file, std::ios::binary | std::ios::trunc);
    out << text;
    return static_cast<bool>(out);
}
namespace log {
void writef(int level, const char*, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    std::lock_guard lock(g_log_mutex);
    g_lines.push_back(std::string(level == KILN_LOG_ERROR ? "E " : level == KILN_LOG_WARN ? "W " : "I ") + buf);
}
}  // namespace log
namespace reg {
std::string owner_name(KilnOwner o) {
    auto it = g_owner_names.find(o);
    return it == g_owner_names.end() ? "(revoked)" : it->second;
}
// kiln.hooks' owner-retiring observers (dev.28) are not part of this test: the registry is a stub here.
KilnResult add_owner_retiring(KilnOwner, OwnerRetiringFn, void*, KilnHandle*) { return KILN_ERR_UNAVAILABLE; }
KilnResult remove_owner_retiring(KilnHandle) { return KILN_ERR_NOT_FOUND; }
}  // namespace reg
namespace tick {
KilnResult subscribe_world(KilnOwner, KilnWorldEventFn, void*, KilnHandle*) { return KILN_ERR_UNAVAILABLE; }
// Typed installation timing (dev.29, typed_install_ready): no world and no Pre here.
bool typed_install_pre() { return false; }
KilnResult session(KilnSessionInfo* out) { *out = {}; out->size = sizeof *out; return KILN_OK; }
}  // namespace tick
namespace sigdb {
bool any_symbol(std::string_view, uintptr_t*) { return false; }
bool any_constant(std::string_view, int64_t*) { return false; }
// The code sigdb scans: the pristine copy, hits translated to live addresses. (The live section is what hooks patch.)
std::vector<uintptr_t> scan_code(const Pattern& p, size_t limit) {
    std::vector<uintptr_t> out;
    for (const uint8_t* h : scan(p, g_file.data(), g_file.data() + g_file.size(), limit))
        out.push_back(g_image.base + static_cast<uintptr_t>(h - g_file.data()));
    return out;
}
bool hole_value_at(const Pattern& p, uintptr_t live_match, std::string_view name, int64_t* out) {
    if (live_match < g_image.base || live_match - g_image.base + p.bytes.size() > g_file.size()) return false;
    return hole_value(p, g_file.data() + (live_match - g_image.base), name, out);
}
bool load_fragment(std::string_view, const std::filesystem::path&, const std::filesystem::path&, FragmentSummary*,
                   std::string*) {
    return false;
}
bool unload_fragment(std::string_view) { return false; }
}  // namespace sigdb
namespace caps {  // a fragment's capability rows (caps.cpp): no registry here
void fragment_rows(std::string_view) {}
void fragment_gone(std::string_view) {}
}  // namespace caps
namespace sigdb {
bool file_bytes(uintptr_t a, uint8_t* out, size_t n) {
    if (a < g_image.base || a - g_image.base + n > g_file.size()) return false;
    std::memcpy(out, g_file.data() + (a - g_image.base), n);
    return true;
}
std::string symbol_name_at(uintptr_t) { return {}; }
std::vector<LiveDiff> live_differs() { return {}; }
// Typed links (dev.28) ask sigdb for a symbol's descriptor and fingerprint: none here, ShroudForge's hooks are raw.
bool sig_at(uintptr_t, SigInfo*) { return false; }
bool fingerprint(uintptr_t, hooks::fp::Fingerprint*) { return false; }
// The resolver's descriptor status (dev.29, sig_status / validate_typed): no descriptors here.
uint64_t sig_revision() { return 1; }
bool sig(std::string_view, SigInfo*) { return false; }
bool sig_status(std::string_view, uintptr_t*, SigStatus*, std::string*) { return false; }
}  // namespace sigdb
namespace svc {
Registrar::Registrar(Service* s) { g_service = s; }
}  // namespace svc
}  // namespace kiln

// the adapter's logf (plugin.cpp is not part of this test)
namespace ksf {
void logf(int32_t level, const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    std::lock_guard lock(g_log_mutex);
    g_lines.push_back(std::string(level == KILN_LOG_ERROR ? "E " : level == KILN_LOG_WARN ? "W " : "I ") + "[sf] " + buf);
}
}  // namespace ksf

// ---------------------------------------------------------------------------------------------------------------------
// The game. Places: p0..p3 `mov rax,rcx | add rax,imm32 | ret` (original = first 9 bytes), p4 `mov [rsp+10h],rbx |
// mov rax,rcx | add rax,imm32 | ret` (original = first 5), p5 `mov rax,imm32 | add rax,rcx | ret` (original = first 7).
// dup_a / dup_b are byte-identical (an ambiguous signature). game_call(fn, x, r15, rbp, r14, r13) calls fn(x) with those
// registers set, like the real places have them.
#define GAME_FN(n) ".balign 32\n.globl game_" #n "\n.def game_" #n "; .scl 2; .type 32; .endef\ngame_" #n ":\n"
extern "C" {
uint64_t game_p0(uint64_t);
uint64_t game_p1(uint64_t);
uint64_t game_p2(uint64_t);
uint64_t game_p3(uint64_t);
uint64_t game_p4(uint64_t);
uint64_t game_p5(uint64_t);
uint64_t game_dup_a(uint64_t);
uint64_t game_dup_b(uint64_t);
uint64_t game_call(void* fn, uint64_t x, uint64_t r15, uint64_t rbp, uint64_t r14, uint64_t r13);
}
__asm__(".intel_syntax noprefix\n.section .game,\"xr\"\n"
        GAME_FN(p0) "mov rax, rcx\n add rax, 0x1000\n ret\n"
        GAME_FN(p1) "mov rax, rcx\n add rax, 0x2000\n ret\n"
        GAME_FN(p2) "mov rax, rcx\n add rax, 0x3000\n ret\n"
        GAME_FN(p3) "mov rax, rcx\n add rax, 0x4000\n ret\n"
        GAME_FN(p4) "mov [rsp+0x10], rbx\n mov rax, rcx\n add rax, 0x5000\n ret\n"
        GAME_FN(p5) "mov rax, 0x6000\n add rax, rcx\n ret\n"
        GAME_FN(dup_a) "mov rax, rcx\n add rax, 0x7000\n ret\n"
        GAME_FN(dup_b) "mov rax, rcx\n add rax, 0x7000\n ret\n"
        ".balign 32\n.globl game_call\n.def game_call; .scl 2; .type 32; .endef\ngame_call:\n"
        "mov r10, [rsp+0x28]\n mov r11, [rsp+0x30]\n"
        "push rbp\n push r15\n push r14\n push r13\n push rbx\n sub rsp, 0x20\n"
        "mov r14, r10\n mov r13, r11\n mov r15, r8\n mov rbp, r9\n mov rax, rcx\n mov rcx, rdx\n"
        "call rax\n"
        "add rsp, 0x20\n pop rbx\n pop r13\n pop r14\n pop r15\n pop rbp\n ret\n"
        ".balign 32\n int3\n"
        ".text\n");

namespace {

int g_fail = 0, g_pass = 0;
void check(bool ok, const char* what, const std::string& detail = "") {
    std::printf("%s %s%s%s\n", ok ? "PASS" : "FAIL", what, detail.empty() ? "" : ": ", detail.c_str());
    std::fflush(stdout);
    ok ? ++g_pass : ++g_fail;
}
bool log_has(const std::string& needle) {
    std::lock_guard lock(g_log_mutex);
    for (const auto& l : g_lines)
        if (l.find(needle) != std::string::npos) return true;
    return false;
}
std::string hex_str(uint64_t v) {
    char b[32];
    std::snprintf(b, sizeof b, "0x%llx", static_cast<unsigned long long>(v));
    return b;
}

struct Place {
    const char* name;
    void* fn;
    size_t original, total;  // bytes the profile names; bytes up to and including ret
    uint32_t kind;
};
const Place kPlaces[6] = {
    {"game_thread", reinterpret_cast<void*>(&game_p0), 9, 10, ksf::kHookPlain},
    {"entity_manager", reinterpret_cast<void*>(&game_p1), 9, 10, ksf::kHookPlain},
    {"world_prop_update", reinterpret_cast<void*>(&game_p2), 9, 10, ksf::kHookPlain},
    {"world_actor_placement", reinterpret_cast<void*>(&game_p3), 9, 10, ksf::kHookPlain},
    {"world_building_dispatch", reinterpret_cast<void*>(&game_p4), 5, 15, ksf::kHookContext},
    {"world_cursor", reinterpret_cast<void*>(&game_p5), 7, 11, ksf::kHookCursor},
};
constexpr uintptr_t kCursorOffset = 0x270;

uint8_t* live(const void* fn) { return const_cast<uint8_t*>(static_cast<const uint8_t*>(fn)); }
const uint8_t* pristine(const void* fn) { return g_file.data() + (live(fn) - reinterpret_cast<uint8_t*>(kiln::g_image.base)); }
std::string sig_of(const void* fn, size_t n) {
    std::string s;
    char b[4];
    for (size_t i = 0; i < n; ++i) {
        std::snprintf(b, sizeof b, "%02X ", pristine(fn)[i]);
        s += b;
    }
    s.pop_back();
    return s;
}

// ---- callbacks (stand-ins for kfc-runtime's drain, capture_entity_manager, ...) ----
struct Seen {
    std::atomic<unsigned> calls{0};
    std::atomic<uintptr_t> a{0}, b{0}, c{0};
};
Seen g_seen[6];
template <int N>
void __cdecl plain_cb(void* a, void* b) {
    g_seen[N].a = reinterpret_cast<uintptr_t>(a), g_seen[N].b = reinterpret_cast<uintptr_t>(b);
    ++g_seen[N].calls;
}
void __cdecl context_cb(void* a) {
    g_seen[4].a = reinterpret_cast<uintptr_t>(a);
    ++g_seen[4].calls;
}
void __cdecl cursor_cb(void* a, void* b, void* c) {
    g_seen[5].a = reinterpret_cast<uintptr_t>(a), g_seen[5].b = reinterpret_cast<uintptr_t>(b), g_seen[5].c = reinterpret_cast<uintptr_t>(c);
    ++g_seen[5].calls;
}
void* const kCallbacks[6] = {reinterpret_cast<void*>(&plain_cb<0>), reinterpret_cast<void*>(&plain_cb<1>),
                             reinterpret_cast<void*>(&plain_cb<2>), reinterpret_cast<void*>(&plain_cb<3>),
                             reinterpret_cast<void*>(&context_cb), reinterpret_cast<void*>(&cursor_cb)};

// ---- another module's hook: E9 at the function, a relay that counts, runs the displaced instructions, jumps back ----
std::atomic<uint32_t>* g_foreign_count = nullptr;
uint8_t* near_alloc(const void* where, size_t n) {
    const uintptr_t base = reinterpret_cast<uintptr_t>(where) & ~uintptr_t{0xFFFF};
    for (uintptr_t d = 0x10000; d < 0x70000000; d += 0x10000)
        for (int dir = 0; dir < 2; ++dir) {
            void* p = VirtualAlloc(reinterpret_cast<void*>(dir ? base + d : base - d), n, MEM_COMMIT | MEM_RESERVE,
                                   PAGE_EXECUTE_READWRITE);
            if (p) return static_cast<uint8_t*>(p);
        }
    return nullptr;
}
uint8_t* foreign_hook(const Place& p) {
    uint8_t* func = live(p.fn);
    uint8_t* relay = near_alloc(func, 0x100);
    static std::atomic<uint32_t> count{0};
    g_foreign_count = &count;
    size_t at = 0;
    relay[at++] = 0x49, relay[at++] = 0xBB;  // mov r11, imm64
    const uint64_t counter = reinterpret_cast<uint64_t>(&count);
    std::memcpy(relay + at, &counter, 8), at += 8;
    relay[at++] = 0x41, relay[at++] = 0xFF, relay[at++] = 0x03;  // inc dword [r11]
    std::memcpy(relay + at, func, 9), at += 9;                    // the displaced instructions
    relay[at] = 0xE9;
    const int32_t back = static_cast<int32_t>((func + 9) - (relay + at + 5));
    std::memcpy(relay + at + 1, &back, 4);
    DWORD old;
    VirtualProtect(func, 16, PAGE_EXECUTE_READWRITE, &old);
    func[0] = 0xE9;
    const int32_t rel = static_cast<int32_t>(relay - (func + 5));
    std::memcpy(func + 1, &rel, 4);
    std::memset(func + 5, 0x90, 4);
    FlushInstructionCache(GetCurrentProcess(), func, 16);
    return relay;
}

// ---- "kfc-runtime": the installer's use of the host interface ----
struct Placed {
    uint64_t handle = 0;
    uintptr_t target = 0;
    uint32_t found = 99;
};
Placed g_placed[6];
const ksf::KfcHost* g_host = nullptr;

void standin_install(size_t only = 6) {
    for (size_t i = 0; i < 6; ++i) {
        if (only != 6 && only != i) continue;
        const Place& p = kPlaces[i];
        const std::string sig = sig_of(p.fn, p.total);
        const std::vector<uint8_t> original(pristine(p.fn), pristine(p.fn) + p.original);
        g_placed[i].found = g_host->find_signature(sig.c_str(), original.data(), original.size(), &g_placed[i].target);
        if (g_placed[i].found == ksf::kFound)
            g_placed[i].handle = g_host->install_hook(p.name, g_placed[i].target, p.kind, kCallbacks[i],
                                                      p.kind == ksf::kHookCursor ? kCursorOffset : 0);
    }
}

void reset_seen() {
    for (auto& s : g_seen) s.calls = 0, s.a = s.b = s.c = 0;
}

// runs every place once with distinct registers and checks the game's results and the callbacks' arguments
void run_all(const char* what, bool expect_callbacks, size_t from = 0, size_t to = 6) {
    reset_seen();
    const uint64_t r15 = 0x1500, rbp = 0xbb00, r14 = 0x1400, r13 = 0x1300;
    bool results = true;
    for (size_t i = from; i < to; ++i) {
        const uint64_t x = 7 + i;
        const uint64_t got = game_call(kPlaces[i].fn, x, r15 + i, rbp + i, r14 + i, r13 + i);
        const uint64_t want = i == 5 ? 0x6000 + x : x + 0x1000 * (i + 1);
        if (got != want) results = false, std::printf("  place %zu returned %llx, want %llx\n", i, (unsigned long long)got, (unsigned long long)want);
    }
    check(results, (std::string(what) + ": the game's functions still return their results").c_str());
    bool calls = true;
    for (size_t i = from; i < to; ++i)
        if (g_seen[i].calls != (expect_callbacks ? 1u : 0u)) calls = false, std::printf("  place %zu callback ran %u times\n", i, g_seen[i].calls.load());
    check(calls, (std::string(what) + (expect_callbacks ? ": each callback ran once" : ": no callback ran")).c_str());
    if (!expect_callbacks) return;
    bool args = true;
    for (size_t i = from; i < to && i < 4; ++i)
        if (g_seen[i].a != r15 + i || g_seen[i].b != rbp + i) args = false;
    if (from <= 4 && to > 4 && g_seen[4].a != 7 + 4) args = false;                   // context: rcx
    if (from <= 5 && to > 5 && (g_seen[5].a != r14 + 5 + kCursorOffset || g_seen[5].b != r13 + 5 || g_seen[5].c != rbp + 5)) args = false;
    check(args, (std::string(what) + ": plain (r15, rbp), context (rcx), cursor (r14+offset, r13, rbp)").c_str());
}

json hook_map() {
    size_t len = 0;
    std::string text;
    if (chain::ready()) {
        const KilnHooksApi* A = static_cast<const KilnHooksApi*>(g_service->table);
        A->map_json(nullptr, 0, &len);
        text.assign(len + 1, '\0');
        A->map_json(text.data(), text.size(), &len);
        text.resize(len);
    }
    return json::parse(text, nullptr, false);
}
const json* target_of(const json& map, uintptr_t live_addr) {
    const std::string rva = hex_str(live_addr - kiln::g_image.base);
    for (const auto& t : map["targets"])
        if (t["rva"] == rva) return &t;
    return nullptr;
}

}  // namespace

// ---- a fake kiln.build and server world for the voxel writer (host_voxels.cpp) ----
const void* g_fake_build = nullptr;
uintptr_t g_server_world = 0;
uint32_t g_server_session = 1;  // kiln-core's count of server worlds since boot (KilnSessionInfo.sessions)
uintptr_t g_client_world = 0;    // a game client's own world (a joined game: this one and no server world)
uint32_t g_client_session = 0;
bool g_client_stale = false;    // the client world's pointer outlived it (no destroy hook): the core's state says none
std::vector<ksf::joined::Worlds> g_joined_calls;  // host_voxels_on_joined's callback
thread_local bool t_world_thread = false;
bool g_post_refuses = false;
std::mutex g_posts_mutex;
std::vector<std::pair<KilnTaskFn, void*>> g_posts;
KilnVoxel g_cells[16 * 16 * 16]{};  // the fake grid: 16 x 16 x 16 cells (x and z: one 16-cell tile each way)
uint32_t g_write_flags = 0;
int g_build_writes = 0;
int g_outside = 0;      // cells kiln.build was asked to write outside the grid
bool g_corrupt = false; // kiln.build's writer commits other cells than asked (no read-back), the put-back too
bool g_corrupt_first = false;  // only the first write of each pair is wrong, its put-back exact (outcome 2)
int g_pair = 0;
bool g_tick_ok = true;  // kiln.build's members answer only on the "server world's thread"
bool inside(int64_t x, int64_t y, int64_t z) { return x >= 0 && x < 16 && y >= 0 && y < 16 && z >= 0 && z < 16; }
size_t cell_at(int x, int y, int z) { return (static_cast<size_t>(z) * 16 + y) * 16 + x; }
KilnResult KILN_CALL fb_status(KilnBuildStatus* st) {
    const uint32_t size = st->size;
    std::memset(st, 0, sizeof *st);
    st->size = size, st->read = st->write = st->world = 1;
    return KILN_OK;
}
KilnResult KILN_CALL fb_grid(KilnBuildGrid* g) {
    if (!t_world_thread) return g_tick_ok = false, KILN_ERR_WRONG_THREAD;
    const uint32_t size = g->size;
    std::memset(g, 0, sizeof *g);
    g->size = size, g->cell_size = 0.5, g->block = 8;
    g->cells_max[0] = 16, g->cells_max[1] = 8192, g->cells_max[2] = 16;  // as kiln.build: y has no engine bound
    g->tile_cells[0] = g->tile_cells[1] = 16, g->tiles[0] = g->tiles[1] = 1;
    return KILN_OK;
}
KilnResult KILN_CALL fb_ready(const int32_t*, const uint32_t*) { return t_world_thread ? KILN_OK : (g_tick_ok = false, KILN_ERR_WRONG_THREAD); }
KilnResult KILN_CALL fb_read(const int32_t o[3], const uint32_t d[3], KilnVoxel* out, size_t) {
    if (!t_world_thread) return g_tick_ok = false, KILN_ERR_WRONG_THREAD;
    size_t j = 0;
    for (uint32_t z = 0; z < d[2]; ++z)
        for (uint32_t y = 0; y < d[1]; ++y)
            for (uint32_t x = 0; x < d[0]; ++x) {
                const int64_t cx = int64_t{o[0]} + x, cy = int64_t{o[1]} + y, cz = int64_t{o[2]} + z;
                out[j++] = inside(cx, cy, cz) ? g_cells[cell_at(int(cx), int(cy), int(cz))] : KilnVoxel{0};
            }
    return KILN_OK;
}
KilnResult KILN_CALL fb_write(const KilnBuildWrite* w, KilnBuildResult* r) {
    if (!t_world_thread) return g_tick_ok = false, KILN_ERR_WRONG_THREAD;
    ++g_build_writes;
    g_write_flags = w->flags;
    const bool bad = g_corrupt || (g_corrupt_first && g_pair++ % 2 == 0);
    size_t j = 0;
    for (uint32_t z = 0; z < w->dims[2]; ++z)
        for (uint32_t y = 0; y < w->dims[1]; ++y)
            for (uint32_t x = 0; x < w->dims[0]; ++x, ++j) {
                const int64_t cx = int64_t{w->origin[0]} + x, cy = int64_t{w->origin[1]} + y, cz = int64_t{w->origin[2]} + z;
                if (inside(cx, cy, cz)) g_cells[cell_at(int(cx), int(cy), int(cz))] = w->cells[j] ^ (bad ? 0x0101 : 0);
                else ++g_outside;
            }
    r->flags = KILN_BUILD_R_CHANGED | (bad ? 0 : KILN_BUILD_R_VERIFIED);
    r->blocks_written = 1;
    return KILN_OK;
}
// the "server world": runs what was posted, on its own thread, after `delay_ms`; new_world: another server world has
// begun by then (kiln-core counts it before it runs the posts of that world's first step)
void run_posts(unsigned delay_ms, bool new_world = false) {
    std::thread([delay_ms, new_world] {
        Sleep(delay_ms);
        t_world_thread = true;
        if (new_world) ++g_server_session;
        std::vector<std::pair<KilnTaskFn, void*>> todo;
        {
            std::lock_guard lock(g_posts_mutex);
            todo.swap(g_posts);
        }
        for (auto& [fn, user] : todo) fn(user);
    }).detach();
}

int main() {
    auto* base = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    auto* sec = IMAGE_FIRST_SECTION(nt);
    for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i)
        if (!std::memcmp(sec[i].Name, ".game", 5)) {
            kiln::g_image.base = reinterpret_cast<uintptr_t>(base) + sec[i].VirtualAddress;
            kiln::g_image.size = (sec[i].Misc.VirtualSize + 0xFFF) & ~size_t{0xFFF};
            kiln::g_image.text = base + sec[i].VirtualAddress;
            kiln::g_image.text_size = sec[i].Misc.VirtualSize;
            g_file.assign(kiln::g_image.text, kiln::g_image.text + sec[i].Misc.VirtualSize);
        }
    if (!kiln::g_image.base) {
        std::puts("FAIL no .game section");
        return 1;
    }
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    g_state_dir = std::filesystem::path(tmp) / L"kiln-sf-host-chain-test";
    std::printf("sf_host_chain_test: game section %p +0x%zx\n", reinterpret_cast<void*>(kiln::g_image.base), kiln::g_image.size);

    // ---- the hooks service as the core runs it, then the plugin's view of it ----
    if (!g_service || !g_service->init || !g_service->init()) {
        std::puts("FAIL kiln.hooks did not initialise");
        return 1;
    }
    g_service->available = true;
    static KilnApi api{};
    api.size = sizeof api;
    api.abi_version = KILN_ABI_VERSION;
    api.service = [](KilnStr name, uint32_t min) -> const void* {
        if (std::string_view(name.ptr, name.len) == KILN_BUILD_SERVICE) return g_fake_build;
        return std::string_view(name.ptr, name.len) == KILN_HOOKS_SERVICE && g_service->available && g_service->version >= min
                   ? g_service->table
                   : nullptr;
    };
    api.world = [](uint32_t kind) -> uintptr_t { return kind == KILN_WORLD_SERVER ? g_server_world : 0; };
    api.on_tick_thread = []() -> bool { return t_world_thread; };
    api.session = [](KilnSessionInfo* out) -> KilnResult {
        if (!out || out->size < sizeof(KilnSessionInfo)) return KILN_ERR_INVALID_ARGUMENT;
        *out = KilnSessionInfo{};
        out->size = sizeof *out;
        out->worlds[KILN_WORLD_SERVER] = g_server_world;
        out->sessions[KILN_WORLD_SERVER] = g_server_session;
        out->worlds[KILN_WORLD_CLIENT] = g_client_world;
        out->sessions[KILN_WORLD_CLIENT] = g_client_session;
        // as kiln-core's session(): a server world, else a client world alone (a joined game), else none
        out->state = g_server_world                     ? KILN_SESSION_DEDICATED
                     : g_client_world && !g_client_stale ? KILN_SESSION_REMOTE
                                                         : KILN_SESSION_NONE;
        return KILN_OK;
    };
    api.post = [](KilnOwner, KilnTaskFn fn, void* user) -> KilnResult {
        if (g_post_refuses) return KILN_ERR_UNAVAILABLE;
        std::lock_guard lock(g_posts_mutex);
        g_posts.push_back({fn, user});
        return KILN_OK;
    };
    std::puts("== the adapter's host table");
    g_host = ksf::host_table(&api, 7);
    check(g_host != nullptr && g_host->version == ksf::kHostVersion && g_host->size == sizeof(ksf::KfcHost),
          "host_table() answers when kiln.hooks v2 is there");
    check(g_host && g_host->find_signature && g_host->install_hook && g_host->remove_hook, "all three functions are set");
    {
        static KilnApi without{};
        without = api;
        without.service = [](KilnStr, uint32_t) -> const void* { return nullptr; };
        check(ksf::host_table(&without, 7) == nullptr, "no table without kiln.hooks (safe idle): the runtime then hooks the game itself");
        check(ksf::host_table(nullptr, 7) == nullptr, "no table without an API");
    }
    if (!g_host) return 1;
    ksf::host_table(&api, 7);  // the table keeps the last api: the real one

    // ---- find_signature ----
    std::puts("== find_signature scans the file's bytes");
    {
        uintptr_t at = 0;
        const std::string s0 = sig_of(kPlaces[0].fn, 10);
        const std::vector<uint8_t> o0(pristine(kPlaces[0].fn), pristine(kPlaces[0].fn) + 9);
        check(g_host->find_signature(s0.c_str(), o0.data(), o0.size(), &at) == ksf::kFound && at == reinterpret_cast<uintptr_t>(kPlaces[0].fn),
              "a unique signature is found at the function's live address");
        std::vector<uint8_t> bad = o0;
        bad[2] ^= 0x55;
        check(g_host->find_signature(s0.c_str(), bad.data(), bad.size(), &at) == ksf::kMismatch,
              "original bytes that differ from the file's: mismatch");
        check(g_host->find_signature(s0.c_str(), nullptr, 0, &at) == ksf::kFound, "no expected bytes: found");
        const std::string dup = sig_of(reinterpret_cast<void*>(&game_dup_a), 10);
        check(g_host->find_signature(dup.c_str(), nullptr, 0, &at) == ksf::kAmbiguous, "two identical places: ambiguous");
        check(g_host->find_signature("DE AD BE EF 12 34 56 78 9A", nullptr, 0, &at) == ksf::kNotFound, "no such bytes: not found");
        check(g_host->find_signature("zz yy", nullptr, 0, &at) == ksf::kNotFound, "a signature sigdb cannot read: not found");
        check(g_host->find_signature(nullptr, nullptr, 0, &at) == ksf::kUnavailable, "no signature: unavailable");
        // wildcards behave as in ShroudForge's own scanner ("?" and "??")
        std::string wild = sig_of(kPlaces[2].fn, 10);
        wild.replace(wild.find(' ', 6) + 1, 2, "?");  // one byte becomes a wildcard
        check(g_host->find_signature(wild.c_str(), nullptr, 0, &at) == ksf::kFound && at == reinterpret_cast<uintptr_t>(kPlaces[2].fn),
              "a '?' wildcard matches");
    }

    // ---- another module hooked entity_manager first (an E9 jump): the point of this change ----
    // (before Kiln hooks anything: a hook placed later would copy Kiln's own jump into its relay)
    std::puts("== a foreign E9 jump on entity_manager first, then all six places hooked through kiln.hooks");
    foreign_hook(kPlaces[1]);
    check(live(kPlaces[1].fn)[0] == 0xE9 && std::memcmp(live(kPlaces[1].fn), pristine(kPlaces[1].fn), 5) != 0,
          "entity_manager starts with another module's jump (its live bytes differ from the file's)");
    // what ShroudForge's own installer sees without a host: its signature no longer matches the live code at all
    check(std::memcmp(live(kPlaces[1].fn), pristine(kPlaces[1].fn), 10) != 0,
          "a live-code scan would not find entity_manager's signature (the old failure)");
    standin_install();
    bool all = true;
    for (size_t i = 0; i < 6; ++i)
        if (g_placed[i].found != ksf::kFound || !g_placed[i].handle) all = false;
    check(all, "every place, entity_manager included, was found in the file's bytes and hooked");
    g_foreign_count->store(0);
    run_all("hooked", true);
    check(g_foreign_count->load() == 1, "the foreign relay was still reached", std::to_string(g_foreign_count->load()));
    {
        const json m = hook_map();
        bool listed = !m.is_discarded();
        for (size_t i = 0; i < 6 && listed; ++i) {
            const json* t = target_of(m, g_placed[i].target);
            if (!t) { listed = false; break; }
            bool found = false;
            for (const auto& l : (*t)["links"])
                if (l["kind"] == "mid" && l["label"] == std::string("shroudforge/") + kPlaces[i].name &&
                    l["owner"] == "early:kiln-shroudforge.dll")
                    found = true;
            listed = listed && found;
        }
        check(listed, "hooks.json lists each as a mid link, owner early:kiln-shroudforge.dll, label shroudforge/<hook>");
        const json* t = target_of(m, g_placed[1].target);
        check(t && !(*t)["foreignBefore"].is_null(), "hooks.json records that entity_manager was chained over a foreign hook");
        const json* t0 = target_of(m, g_placed[0].target);
        check(t0 && (*t0)["foreignBefore"].is_null(), "and game_thread, nobody else's, is not marked foreign");
    }
    check(log_has("ShroudForge engine hook entity_manager at rva"), "kiln.log says where each hook went");
    check(log_has("chained over"), "kiln.log says 'chained over' for the foreign hook");
    // revoking one hook while the others keep running
    g_host->remove_hook(g_placed[1].handle);
    reset_seen();
    g_foreign_count->store(0);
    const uint64_t v = game_call(kPlaces[1].fn, 3, 1, 2, 3, 4);
    check(v == 3 + 0x2000 && g_seen[1].calls == 0 && g_foreign_count->load() == 1,
          "after revoke the callback stops, the game and the foreign relay go on");
    run_all("others after one revoke", true, 2, 6);
    for (size_t i : {0, 2, 3, 4, 5}) g_host->remove_hook(g_placed[i].handle);

    // ---- 3. calls from several threads while hooks come and go ----
    std::puts("== threads");
    {
        for (auto& p : g_placed) p = Placed{};
        std::atomic<bool> stop{false};
        std::atomic<uint64_t> wrong{0}, calls{0};
        std::vector<std::thread> threads;
        for (int t = 0; t < 4; ++t)
            threads.emplace_back([&, t] {
                while (!stop.load()) {
                    for (size_t i = 0; i < 6; ++i) {
                        const uint64_t x = 100 + t;
                        const uint64_t want = i == 5 ? 0x6000 + x : x + 0x1000 * (i + 1);
                        if (game_call(kPlaces[i].fn, x, 1, 2, 3, 4) != want) ++wrong;
                        ++calls;
                    }
                }
            });
        for (int round = 0; round < 40; ++round) {
            standin_install();
            Sleep(2);
            for (size_t i = 0; i < 6; ++i) g_host->remove_hook(g_placed[i].handle);
            for (auto& p : g_placed) p = Placed{};
        }
        stop = true;
        for (auto& t : threads) t.join();
        check(wrong == 0 && calls > 1000, "4 threads calling while hooks are placed and removed: every call right",
              std::to_string(calls.load()) + " calls, " + std::to_string(wrong.load()) + " wrong");
    }

    // ---- 4. a refused hook is reported as 0 ----
    std::puts("== refusals");
    check(g_host->install_hook("x", kiln::g_image.base - 0x100000, ksf::kHookPlain, kCallbacks[0], 0) == 0,
          "an address outside the image: 0");
    check(g_host->install_hook("x", reinterpret_cast<uintptr_t>(kPlaces[0].fn), 77, kCallbacks[0], 0) == 0, "an unknown kind: 0");
    check(g_host->install_hook("x", reinterpret_cast<uintptr_t>(kPlaces[0].fn), ksf::kHookPlain, nullptr, 0) == 0, "no callback: 0");
    g_host->remove_hook(0);  // harmless

    // ---- 5. the voxel writer (kfc_host.h voxel_write_ready / voxel_write) ----
    std::puts("== the voxel writer");
    check(g_host->voxel_write_ready && g_host->voxel_write, "the table carries the voxel writer");
    const int32_t at[3] = {2, 3, 4};
    const uint32_t box[3] = {2, 1, 2};
    const uint16_t cells[4] = {0x0000, 0xff81, 0xff82, 0x8001};
    uint32_t outcome = 9;
    check(g_host->voxel_write_ready() == 0, "no kiln.build: not ready");
    check(!g_host->voxel_write(at, box, cells, 4, &outcome) && outcome == 1, "no kiln.build: refused before any change");
    static KilnBuildApi fake{};
    fake.header.size = sizeof fake, fake.header.version = KILN_BUILD_VERSION;
    fake.status = &fb_status, fake.grid = &fb_grid, fake.ready = &fb_ready, fake.read = &fb_read, fake.write = &fb_write;
    g_fake_build = &fake;
    check(g_host->voxel_write_ready() == 0 && !g_host->voxel_write(at, box, cells, 4, &outcome) && outcome == 1,
          "no world this game hosts: not ready, refused");
    g_server_world = 0x1234;
    check(g_host->voxel_write_ready() == 1, "a hosted world: ready");
    run_posts(30);
    outcome = 9;
    check(g_host->voxel_write(at, box, cells, 4, &outcome) && outcome == 0, "written on the server world's thread: outcome 0");
    check(g_cells[cell_at(2, 3, 4)] == 0 && g_cells[cell_at(3, 3, 4)] == 0xff81 && g_cells[cell_at(2, 3, 5)] == 0xff82 &&
          g_cells[cell_at(3, 3, 5)] == 0x8001 && g_tick_ok, "the cells landed, and kiln.build was only called there");
    check(g_write_flags == (KILN_BUILD_F_IN_BASES | KILN_BUILD_F_GAME_SAVE), "the game's save rule, no base guard");
    check(!g_host->voxel_write(at, box, cells, 3, &outcome) && outcome == 1, "a count that does not match its box: refused");
    // a world that does not take the write within 2 s: cancelled and refused; when it runs later, it writes nothing
    const int writes = g_build_writes;
    const uint16_t other[4] = {1, 2, 3, 4};
    outcome = 9;
    check(!g_host->voxel_write(at, box, other, 4, &outcome) && outcome == 1, "a world that does not step: refused after the wait");
    run_posts(0);
    Sleep(200);
    check(g_build_writes == writes && g_cells[cell_at(3, 3, 4)] == 0xff81, "the cancelled write never runs");
    g_post_refuses = true;
    check(!g_host->voxel_write(at, box, other, 4, &outcome) && outcome == 1, "a post Kiln refuses: refused");
    g_post_refuses = false;
    // only the voxel grid's cells, as ShroudForge's own writer (read_registered_voxel_grid.cpp region(): no negative
    // coordinate, x and z inside the grid's tiles): kiln.build checks no grid bounds, and the engine's block writer would
    // commit blocks outside the world
    {
        const int writes_before = g_build_writes;
        const int32_t outside[][3] = {{-1, 3, 4}, {2, -1, 4}, {2, 3, -2}, {15, 3, 4}, {2, 3, 15}, {INT32_MIN, 3, 4}};
        const char* what[] = {"x below 0", "y below 0", "z below 0", "x past the grid", "z past the grid", "x at INT32_MIN"};
        for (size_t i = 0; i < std::size(outside); ++i) {
            run_posts(30);
            outcome = 9;
            const bool ok = g_host->voxel_write(outside[i], box, other, 4, &outcome);
            check(!ok && outcome == 1, (std::string("a box outside the voxel grid is refused before any change: ") + what[i]).c_str());
        }
        check(g_build_writes == writes_before && g_outside == 0, "nothing outside the grid reaches kiln.build's writer");
        const int32_t edge[3] = {14, 3, 14};
        run_posts(30);
        outcome = 9;
        check(g_host->voxel_write(edge, box, other, 4, &outcome) && outcome == 0 && g_cells[cell_at(15, 3, 15)] == 4,
              "a box that ends at the grid's edge is written");
        std::lock_guard lock(g_log_mutex);
        bool said = false;
        for (const auto& l : g_lines) said |= l.find("outside the voxel grid") != std::string::npos;
        check(said, "kiln.log says the box is outside the voxel grid");
    }
    // a write the server world runs only after another world began (a hosted game left and a new one loaded): the box
    // was meant for the world the mod saw; refused, nothing written
    {
        const int writes_before = g_build_writes;
        const uint16_t again[4] = {9, 9, 9, 9};
        run_posts(30, /*new_world=*/true);
        outcome = 9;
        check(!g_host->voxel_write(at, box, again, 4, &outcome) && outcome == 1, "a write that reaches another world is refused");
        check(g_build_writes == writes_before && g_cells[cell_at(2, 3, 4)] != 9, "and writes nothing there");
        run_posts(30);
        outcome = 9;
        check(g_host->voxel_write(at, box, again, 4, &outcome) && outcome == 0 && g_cells[cell_at(2, 3, 4)] == 9,
              "the next write, asked in the new world, is written");
    }
    // outcome 2 (written, did not read back, put back) again and again, as a mod that retries every update would get it:
    // kiln.log has the first ten, not one line a write
    {
        const uint16_t want[4] = {3, 3, 3, 3};
        g_corrupt_first = true, g_pair = 0;
        bool all = true;
        for (int i = 0; i < 12; ++i) {
            run_posts(5);
            outcome = 9;
            all &= !g_host->voxel_write(at, box, want, 4, &outcome) && outcome == 2;
        }
        g_corrupt_first = false;
        check(all, "a write that does not read back and is put back: outcome 2, each time");
        std::lock_guard lock(g_log_mutex);
        size_t lines = 0;
        for (const auto& l : g_lines) lines += l.find("the earlier cells were put back") != std::string::npos;
        check(lines == 10, "kiln.log has the first ten of them", std::to_string(lines) + " lines");
    }
    // outcome 3 (the write did not read back and neither did the put-back): as ShroudForge's own writer (it marks the
    // session's writes uncertain), no further voxel write in that world; the next world writes again
    {
        const uint16_t cells3[4] = {5, 6, 7, 8};
        g_corrupt = true;
        run_posts(30);
        outcome = 9;
        check(!g_host->voxel_write(at, box, cells3, 4, &outcome) && outcome == 3, "a put-back that does not read back: outcome 3");
        g_corrupt = false;
        const int writes_before = g_build_writes;
        const uint16_t later[4] = {1, 1, 1, 1};
        run_posts(30);
        outcome = 9;
        check(!g_host->voxel_write(at, box, later, 4, &outcome) && outcome == 1 && g_build_writes == writes_before,
              "after an uncertain outcome the world takes no further ShroudForge voxel write");
        {
            // by now this start has refused more than ten writes for other reasons: kiln.log still says each new reason,
            // and every write that changed the world and failed
            std::lock_guard lock(g_log_mutex);
            bool uncertain = false, said = false;
            for (const auto& l : g_lines) {
                uncertain |= l.find("ShroudForge voxel write failed; the outcome is uncertain") != std::string::npos;
                said |= l.find("uncertain outcome") != std::string::npos && l.find("refused") != std::string::npos;
            }
            check(uncertain, "kiln.log has the uncertain write (after more than ten refusals)");
            check(said, "kiln.log says why the next one is refused");
        }
        run_posts(30, /*new_world=*/true);
        outcome = 9;
        check(!g_host->voxel_write(at, box, later, 4, &outcome) && outcome == 1, "(a write asked in the old world that reaches the new one: refused)");
        run_posts(30);
        outcome = 9;
        check(g_host->voxel_write(at, box, later, 4, &outcome) && outcome == 0 && g_cells[cell_at(3, 3, 4)] == 1,
              "the next world takes writes again");
    }
    {
        std::lock_guard lock(g_log_mutex);
        bool said = false;
        for (const auto& l : g_lines) said |= l.find("ShroudForge voxel write refused before changing the world") != std::string::npos &&
                                              l.find("did not take the write within 2 seconds") != std::string::npos;
        check(said, "kiln.log says why a write was refused");
    }

    // a game that joined a server (a client world, no server world): refused with the joined reason, and the notice's
    // callback hears of it (joined_notice.h); ready() asks without telling anyone (ShroudForge asks it often)
    {
        const uintptr_t server = g_server_world;
        g_server_world = 0, g_client_world = 0x5678, g_client_session = 3;
        ksf::host_voxels_on_joined([](const ksf::joined::Worlds& w) { g_joined_calls.push_back(w); });
        const uint16_t want[4] = {1, 2, 3, 4};
        check(g_host->voxel_write_ready() == 0 && g_joined_calls.empty(), "a joined game: not ready, and asking tells nobody");
        outcome = 9;
        check(!g_host->voxel_write(at, box, want, 4, &outcome) && outcome == 1, "a joined game: refused before any change");
        check(g_joined_calls.size() == 1 && g_joined_calls[0].client && !g_joined_calls[0].server &&
                  g_joined_calls[0].client_session == 3,
              "the refusal reaches the joined notice with the client world's session");
        {
            std::lock_guard lock(g_log_mutex);
            bool said = false;
            for (const auto& l : g_lines) said |= l.find("ShroudForge voxel write refused before changing the world") != std::string::npos &&
                                                  l.find(ksf::joined::kReason) != std::string::npos;
            check(said, "kiln.log gives the joined reason");
        }
        g_joined_calls.clear();
        g_client_stale = true;  // back at the main menu without the core's world destroy hook: the pointer is still there
        check(!g_host->voxel_write(at, box, want, 4, &outcome) && outcome == 1 && g_joined_calls.empty(),
              "the main menu after a joined game, the client world's pointer stale: refused, no joined notice");
        g_client_stale = false;
        g_client_world = 0;
        check(!g_host->voxel_write(at, box, want, 4, &outcome) && outcome == 1 && g_joined_calls.empty(),
              "no world at all (the main menu): refused, no joined notice");
        ksf::host_voxels_on_joined(nullptr);
        g_server_world = server;
    }

    {
        std::lock_guard lock(g_log_mutex);
        size_t errors = 0;
        for (const auto& l : g_lines)
            if (l[0] == 'E' && l.find("faulted") == std::string::npos && l.find("the outcome is uncertain") == std::string::npos &&
                l.find("the earlier cells were put back") == std::string::npos)
                ++errors, std::printf("  unexpected log: %s\n", l.c_str());
        if (errors) ++g_fail;
    }
    std::printf("%d passed, %d failed\n", g_pass, g_fail);
    std::puts(g_fail ? "FAILED" : "all passed");
    return g_fail ? 1 : 0;
}
