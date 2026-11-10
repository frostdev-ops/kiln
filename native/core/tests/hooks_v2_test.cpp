// SPDX-License-Identifier: MIT
// kiln.hooks v2 under Wine, no game: the real service (hooks.cpp), registry engine (hooks_chain.cpp), static machine
// code (hooks_chain_asm.cpp) and SEH helpers, with the core's logging, sigdb and owner registry replaced by stubs.
//
// The "game" is the exe's own `.game` section: compiled functions with the signatures the tests need, and a few
// hand-assembled ones whose instruction boundaries are known (the stand-in foreign hooker and the mid hooks need
// them). The exe's other code (the handlers, the foreign hooker) plays "another module".
//
//   native/core/tests/test-hooks-v2.sh            (builds hooks_v2_test.exe, runs it twice: caller-table slot < 64
//                                                  and >= 64 [TlsExpansionSlots])
#include <windows.h>

#include <intrin.h>

#include <atomic>
#include <cmath>
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

#include "kiln_hook.hpp"
#include "common.h"
#include "image.h"
#include "log.h"
#include "nlohmann/json.hpp"
#include "registry.h"
#include "services/hooks_chain.h"
#include "services/hooks_internal.h"
#include "services/kiln_hooks.h"
#include "services/service.h"
#include "sigdb.h"
#include "tick.h"

using json = nlohmann::json;
namespace chain = kiln::hooks::chain;

// ---------------------------------------------------------------------------------------------------------------------
// Stubs for the rest of kiln-core
namespace {
std::mutex g_log_mutex;
std::vector<std::string> g_lines;
std::map<uintptr_t, std::vector<uint8_t>> g_file;  // "the exe file's" first bytes of functions a foreign hooker patched
std::vector<kiln::sigdb::LiveDiff> g_live_diffs;
std::filesystem::path g_state_dir;
kiln::svc::Service* g_service = nullptr;
std::map<KilnOwner, std::string> g_owner_names = {{0, "kiln"},
                                                  {1, "service:kiln-chat.dll"},
                                                  {2, "plugin:kiln-emf.dll"},
                                                  {3, "emf:coolmod"},
                                                  {4, "native:alpha"},
                                                  {5, "native:beta"},
                                                  {6, "test"}};
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
}  // namespace reg
namespace tick {
KilnResult subscribe_world(KilnOwner, KilnWorldEventFn, void*, KilnHandle*) { return KILN_ERR_UNAVAILABLE; }
}  // namespace tick
namespace sigdb {
bool any_symbol(std::string_view, uintptr_t*) { return false; }
bool any_constant(std::string_view, int64_t*) { return false; }
std::vector<uintptr_t> scan_code(const Pattern&, size_t) { return {}; }
bool hole_value_at(const Pattern&, uintptr_t, std::string_view, int64_t*) { return false; }
bool load_fragment(std::string_view, const std::filesystem::path&, const std::filesystem::path&, FragmentSummary*,
                   std::string*) {
    return false;
}
bool unload_fragment(std::string_view) { return false; }
bool file_bytes(uintptr_t a, uint8_t* out, size_t n) {
    for (auto& [at, bytes] : g_file)
        if (a >= at && a + n <= at + bytes.size()) {
            std::memcpy(out, bytes.data() + (a - at), n);
            return true;
        }
    std::memcpy(out, reinterpret_cast<const void*>(a), n);  // "the file" equals the live code unless a hooker patched it
    return true;
}
std::string symbol_name_at(uintptr_t) { return {}; }
std::vector<LiveDiff> live_differs() { return g_live_diffs; }
}  // namespace sigdb
namespace svc {
Registrar::Registrar(Service* s) { g_service = s; }
}  // namespace svc
}  // namespace kiln

// ---------------------------------------------------------------------------------------------------------------------
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
size_t log_count(const std::string& needle) {
    std::lock_guard lock(g_log_mutex);
    size_t n = 0;
    for (const auto& l : g_lines)
        if (l.find(needle) != std::string::npos) ++n;
    return n;
}

const KilnHooksApi* A = nullptr;
std::string g_order;  // what ran, in order (single-threaded tests)
std::mutex g_order_mutex;
void ran(char c) {
    std::lock_guard lock(g_order_mutex);
    g_order += c;
}
std::string take_order() {
    std::lock_guard lock(g_order_mutex);
    std::string s;
    s.swap(g_order);
    return s;
}

std::atomic<uint64_t> g_real{0};  // real-function executions
std::atomic<bool> g_probe{false};
std::atomic<uintptr_t> g_probe_ret{0};

KilnHookOrder ord(int prio, const char* label, const char* before = nullptr, const char* after = nullptr) {
    KilnHookOrder o{};
    o.size = sizeof o;
    o.priority = prio;
    o.label = KilnStr{label, std::strlen(label)};
    if (before) o.before = KilnStr{before, std::strlen(before)};
    if (after) o.after = KilnStr{after, std::strlen(after)};
    return o;
}

}  // namespace

// ---------------------------------------------------------------------------------------------------------------------
// The game: functions in the .game section (the image range the stub image() reports).
#pragma code_seg(".game")
extern "C" {
__declspec(noinline) uint64_t game_sum9(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f,
                                        uint64_t g, uint64_t h, uint64_t i) {
    g_real.fetch_add(1);
    if (g_probe.load()) g_probe_ret.store(reinterpret_cast<uintptr_t>(_ReturnAddress()));
    return a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f + 7 * g + 8 * h + 9 * i;
}
__declspec(noinline) double game_fmix(double a, float b, int c, double d, float e) {
    g_real.fetch_add(1);
    return a * 1000.0 + static_cast<double>(b) * 100.0 + c * 10.0 + d + static_cast<double>(e) / 100.0;
}
__declspec(noinline) uint64_t game_v1(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f) {
    g_real.fetch_add(1);
    return a + 10 * b + 100 * c + 1000 * d + 10000 * e + 100000 * f;
}
#define GAME_ORD(n)                                                                                                    \
    __declspec(noinline) uint64_t game_ord##n(uint64_t x) {                                                            \
        g_real.fetch_add(1);                                                                                           \
        return x * 3 + n;                                                                                              \
    }
GAME_ORD(1)
GAME_ORD(2)
GAME_ORD(3)
GAME_ORD(4)
__declspec(noinline) uint64_t game_hdr(uint64_t a, int b) {
    g_real.fetch_add(1);
    return a * 10 + static_cast<uint64_t>(b);
}
#define GAME_LATE(n)                                                                                                   \
    __declspec(noinline) uint64_t game_late##n(uint64_t x) {                                                           \
        g_real.fetch_add(1);                                                                                           \
        return x * 3 + n;                                                                                              \
    }
GAME_LATE(1)
GAME_LATE(2)
GAME_LATE(3)
__declspec(noinline) uint64_t game_chaos(uint64_t x) {
    g_real.fetch_add(1);
    return x * 3 + 7;
}
__declspec(noinline) uint64_t game_stress(uint64_t x) {
    g_real.fetch_add(1);
    return x * 3 + 1;
}
__declspec(noinline) uint64_t game_nested(uint64_t x) {  // calls another hooked target
    g_real.fetch_add(1);
    return game_stress(x) + 1;
}
__declspec(noinline) int game_throw(int x) {
    g_real.fetch_add(1);
    if (x) throw 42;
    return x + 7;
}
__declspec(noinline) int game_av(int x) {
    g_real.fetch_add(1);
    if (x) *static_cast<volatile int*>(nullptr) = x;
    return x + 7;
}
__declspec(noinline) uint64_t game_fault(uint64_t x) {
    g_real.fetch_add(1);
    return x + 1;
}
}
#pragma code_seg()

// Hand-assembled game functions with known instruction boundaries (1, 5, 7, 10, 14), `int f(int x)` = x + 1:
//   push rbx; sub rsp,20h; mov eax,ecx; add eax,1; add rsp,20h; pop rbx; ret
#define GAME_ASM(name)                                                                                                 \
    ".globl " #name "\n.def " #name "; .scl 2; .type 32; .endef\n" #name ":\n"                                         \
    "  push rbx\n  sub rsp, 0x20\n  mov eax, ecx\n  add eax, 1\n  add rsp, 0x20\n  pop rbx\n  ret\n"                    \
    "  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n"    \
    "  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n"
extern "C" {
int game_asm_flags(int);
extern char game_asm_flags_hook[];
int game_asm_regs(int);
extern char game_asm_regs_hook[];
int game_asm_fb(int);
int game_asm_fo(int);
int game_asm_mid(int);
int game_asm_mid2(int);
int game_asm_mid3(int);
int game_asm_mix(int);
}
__asm__(".intel_syntax noprefix\n.section .game,\"xr\"\n" GAME_ASM(game_asm_fb) GAME_ASM(game_asm_fo)
            GAME_ASM(game_asm_mid) GAME_ASM(game_asm_mid2) GAME_ASM(game_asm_mid3) GAME_ASM(game_asm_mix) ".text\n");

// flags: `cmp ecx,5` then (hook point) `mov eax,0` (does not touch the flags), then `setz al`: 1 when x == 5. A hook at the
// hook point sees live flags.
__asm__(".intel_syntax noprefix\n.section .game,\"xr\"\n"
        ".globl game_asm_flags\n.def game_asm_flags; .scl 2; .type 32; .endef\ngame_asm_flags:\n"
        "  cmp ecx, 5\n"
        ".globl game_asm_flags_hook\ngame_asm_flags_hook:\n"
        "  mov eax, 0\n  setz al\n  ret\n"
        "  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n"
        // regs: load distinct values, hook point (mov eax,1), then check each (r13 is rewritten by the handler)
        ".globl game_asm_regs\n.def game_asm_regs; .scl 2; .type 32; .endef\ngame_asm_regs:\n"
        "  push rbx\n  push rsi\n  push rdi\n  push r12\n  push r13\n  push r14\n  push r15\n  sub rsp, 0x20\n"
        "  mov rbx, 0x1111\n  mov rsi, 0x2222\n  mov rdi, 0x3333\n  mov r12, 0x4444\n  mov r13, 0x5555\n"
        "  mov r14, 0x6666\n  mov r15, 0x7777\n  mov r10, 0x8888\n  mov r11, 0x9999\n  mov r8, 0xaaaa\n  mov r9, 0xbbbb\n"
        "  mov rdx, 0xcccc\n"
        ".globl game_asm_regs_hook\ngame_asm_regs_hook:\n"
        "  mov eax, 1\n"
        "  cmp rbx, 0x1111\n  jne 1f\n  cmp rsi, 0x2222\n  jne 1f\n  cmp rdi, 0x3333\n  jne 1f\n"
        "  cmp r12, 0x4444\n  jne 1f\n  cmp r13, 0x5556\n  jne 1f\n  cmp r14, 0x6666\n  jne 1f\n"
        "  cmp r15, 0x7777\n  jne 1f\n  cmp r10, 0x8888\n  jne 1f\n  cmp r11, 0x9999\n  jne 1f\n"
        "  cmp r8, 0xaaaa\n  jne 1f\n  cmp r9, 0xbbbb\n  jne 1f\n  cmp rdx, 0xcccc\n  jne 1f\n"
        "  jmp 2f\n"
        "1:\n  xor eax, eax\n"
        "2:\n  add rsp, 0x20\n  pop r15\n  pop r14\n  pop r13\n  pop r12\n  pop rdi\n  pop rsi\n  pop rbx\n  ret\n"
        "  int3\n  int3\n  int3\n  int3\n"
        ".text\n");

namespace {

// ---------------------------------------------------------------------------------------------------------------------
// Handlers
using Fn9 = uint64_t (*)(uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t);

template <int N>
struct L9 {  // raw link on a 9-argument function; link 2 also bumps arg 0 and (stack) arg 8
    static inline Fn9 orig = nullptr;
    static inline KilnHandle h = 0;
    static inline uintptr_t seen = 0;
    static inline uintptr_t align = 0;
    static uint64_t fn(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f, uint64_t g, uint64_t hh,
                       uint64_t i) {
        seen = A->caller(h);  // before anything else is called
        align = reinterpret_cast<uintptr_t>(_AddressOfReturnAddress()) & 15;
        ran(static_cast<char>('0' + N));
        if (N == 2) a += 1, i += 100;
        return orig(a, b, c, d, e, f, g, hh, i);
    }
};

uint64_t expect9(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f, uint64_t g, uint64_t h,
                 uint64_t i) {
    return a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f + 7 * g + 8 * h + 9 * i;
}

using FnF = double (*)(double, float, int, double, float);
template <int N>
struct LF {
    static inline FnF orig = nullptr;
    static inline KilnHandle h = 0;
    static double fn(double a, float b, int c, double d, float e) {
        ran(static_cast<char>('0' + N));
        if (N == 1) a += 1.0;
        if (N == 2) e *= 2.0f, d += 0.25;
        const double r = orig(a, b, c, d, e);
        return N == 3 ? r + 0.5 : r;
    }
};
double expectF(double a, float b, int c, double d, float e) {
    return a * 1000.0 + static_cast<double>(b) * 100.0 + c * 10.0 + d + static_cast<double>(e) / 100.0;
}

// Structured (detour2) on the 9-argument function.
std::atomic<uintptr_t> g_s2_caller{0};
uintptr_t KILN_CALL s2_fn(KilnHookCall2* c, void* user) {
    g_s2_caller = c->caller;
    ran(static_cast<char>(reinterpret_cast<uintptr_t>(user)));
    c->args[8] += 1000;  // a stack argument
    c->args[2] += 1;
    return A->call_original2(c) + 1;
}

// Ordering links: each records its letter and forwards.
template <int N>
struct LO {
    static inline uint64_t (*orig)(uint64_t) = nullptr;
    static uint64_t fn(uint64_t x) {
        ran(static_cast<char>('a' + N));
        return orig(x);
    }
};
template <int N>
KilnHandle add_ord(uintptr_t target, KilnOwner owner, int prio, const char* before = nullptr, const char* after = nullptr) {
    KilnHandle h = 0;
    const std::string label = std::string("ord") + static_cast<char>('a' + N);
    const KilnHookOrder o = ord(prio, label.c_str(), before, after);
    // the label pointer only has to live for the call
    const KilnResult r = A->raw_detour(owner, target, &o, reinterpret_cast<void*>(&LO<N>::fn),
                                       reinterpret_cast<void**>(&LO<N>::orig), &h);
    return r == KILN_OK ? h : 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// Stand-in "other engine": Enshwick-style E9 hook (hookchain spike). The relay is allocated near the function.
uint8_t* near_alloc(const void* where, size_t n) {
    const uintptr_t base = reinterpret_cast<uintptr_t>(where) & ~uintptr_t{0xFFFF};
    for (uintptr_t d = 0x10000; d < 0x70000000; d += 0x10000)
        for (int dir = 0; dir < 2; ++dir) {
            const uintptr_t a = dir ? base + d : base - d;
            void* p = VirtualAlloc(reinterpret_cast<void*>(a), n, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (p) return static_cast<uint8_t*>(p);
        }
    return nullptr;
}
void abs_jmp(uint8_t* at, const void* to) {  // FF 25 00000000 <abs64>
    at[0] = 0xFF, at[1] = 0x25;
    std::memset(at + 2, 0, 4);
    const uint64_t a = reinterpret_cast<uint64_t>(to);
    std::memcpy(at + 6, &a, 8);
}
void rel_jmp(uint8_t* at, const void* to) {  // E9 rel32
    at[0] = 0xE9;
    const int32_t rel = static_cast<int32_t>(static_cast<const uint8_t*>(to) - (at + 5));
    std::memcpy(at + 1, &rel, 4);
}

uint8_t* g_fb_tramp = nullptr;
uint8_t* g_fo_tramp = nullptr;
int foreign_detour_fb(int x) {
    ran('F');
    return reinterpret_cast<int (*)(int)>(g_fb_tramp)(x);
}
int foreign_detour_fo(int x) {
    ran('F');
    return reinterpret_cast<int (*)(int)>(g_fo_tramp)(x);
}

// Hooks `func` the way Enshwick does (5-byte E9 to a nearby relay; its trampoline relocates an E9 already there).
// Returns the relay so the caller can inspect it.
uint8_t* foreign_hook(uint8_t* func, void* detour, uint8_t** tramp_out) {
    uint8_t* arena = near_alloc(func, 0x1000);
    if (!arena) return nullptr;
    uint8_t* relay = arena + 0x100;
    uint8_t* tramp = arena + 0x200;
    abs_jmp(relay, detour);
    std::memcpy(tramp, func, 5);
    if (func[0] == 0xE9) {
        int32_t rel;
        std::memcpy(&rel, func + 1, 4);
        rel_jmp(tramp, func + 5 + rel);
    }
    abs_jmp(tramp + 5, func + 5);
    *tramp_out = tramp;
    DWORD old;
    VirtualProtect(func, 16, PAGE_EXECUTE_READWRITE, &old);
    rel_jmp(func, relay);
    FlushInstructionCache(GetCurrentProcess(), func, 16);
    return relay;
}

void snapshot_file(const void* func) {
    g_file[reinterpret_cast<uintptr_t>(func)] =
        std::vector<uint8_t>(static_cast<const uint8_t*>(func), static_cast<const uint8_t*>(func) + 16);
}

std::string exe_name_lower() {
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::string s = kiln::to_utf8(std::wstring_view(path, n));
    s = s.substr(s.find_last_of("\\/") + 1);
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

// ---------------------------------------------------------------------------------------------------------------------
// call sites (never tail calls: a real return address into each)
std::atomic<uint64_t> g_sink{0};
Fn9 volatile g_p9 = game_sum9;
__declspec(noinline) uint64_t callsite_a(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f,
                                         uint64_t g, uint64_t h, uint64_t i) {
    const uint64_t r = g_p9(a, b, c, d, e, f, g, h, i);
    g_sink.fetch_add(1);
    return r;
}
__declspec(noinline) uint64_t callsite_b(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f,
                                         uint64_t g, uint64_t h, uint64_t i) {
    const uint64_t r = g_p9(a, b, c, d, e, f, g, h, i);
    g_sink.fetch_add(2);
    return r;
}

// SEH wrapper: no C++ objects here.
int call_av(int x, DWORD* code) {
    __try {
        return game_av(x);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
void test_raw_chain_9args() {
    std::puts("-- raw links: 9 arguments through a chain of 3, caller() in each link");
    // probe the real return address of each call site before anything is hooked
    g_probe = true;
    callsite_a(1, 1, 1, 1, 1, 1, 1, 1, 1);
    const uintptr_t ret_a = g_probe_ret;
    callsite_b(1, 1, 1, 1, 1, 1, 1, 1, 1);
    const uintptr_t ret_b = g_probe_ret;
    g_probe = false;
    check(ret_a && ret_b && ret_a != ret_b, "probe: two distinct call sites", "a/b differ");

    const uintptr_t target = reinterpret_cast<uintptr_t>(&game_sum9);
    const auto o1 = ord(30, "t/sum9-1"), o2 = ord(20, "t/sum9-2"), o3 = ord(10, "t/sum9-3");
    check(A->raw_detour(6, target, &o1, reinterpret_cast<void*>(&L9<1>::fn), reinterpret_cast<void**>(&L9<1>::orig),
                        &L9<1>::h) == KILN_OK && L9<1>::orig,
          "raw link 1 installed, original set before the first call");
    check(A->raw_detour(6, target, &o2, reinterpret_cast<void*>(&L9<2>::fn), reinterpret_cast<void**>(&L9<2>::orig),
                        &L9<2>::h) == KILN_OK,
          "raw link 2 installed");
    check(A->raw_detour(6, target, &o3, reinterpret_cast<void*>(&L9<3>::fn), reinterpret_cast<void**>(&L9<3>::orig),
                        &L9<3>::h) == KILN_OK,
          "raw link 3 installed");
    take_order();
    g_real = 0;
    const uint64_t r = callsite_a(1, 2, 3, 4, 5, 6, 7, 8, 9);
    const uint64_t want = expect9(1 + 1, 2, 3, 4, 5, 6, 7, 8, 9 + 100);
    check(r == want, "all nine arguments reach the real function (link 2's changes included)",
          std::to_string(r) + " vs " + std::to_string(want));
    check(take_order() == "123" && g_real == 1, "links ran in priority order, the real function once");
    {
        char b[200];
        std::snprintf(b, sizeof b, "want %p, got %p %p %p", reinterpret_cast<void*>(ret_a), reinterpret_cast<void*>(L9<1>::seen),
                      reinterpret_cast<void*>(L9<2>::seen), reinterpret_cast<void*>(L9<3>::seen));
        check(L9<1>::seen == ret_a && L9<2>::seen == ret_a && L9<3>::seen == ret_a,
              "caller() is the game's call site in every link", b);
    }
    check(L9<1>::align == 8 && L9<2>::align == 8 && L9<3>::align == 8,
          "stack alignment at the handlers is the standard one (rsp = 8 mod 16)");
    const uint64_t r2 = callsite_b(9, 8, 7, 6, 5, 4, 3, 2, 1);
    check(r2 == expect9(9 + 1, 8, 7, 6, 5, 4, 3, 2, 1 + 100) && L9<1>::seen == ret_b && L9<3>::seen == ret_b,
          "the other call site is reported correctly");
    take_order();
}

void test_floats() {
    std::puts("-- raw links: float/double arguments and a double return");
    const uintptr_t target = reinterpret_cast<uintptr_t>(&game_fmix);
    const auto o1 = ord(3, "t/fmix-1"), o2 = ord(2, "t/fmix-2"), o3 = ord(1, "t/fmix-3");
    A->raw_detour(6, target, &o1, reinterpret_cast<void*>(&LF<1>::fn), reinterpret_cast<void**>(&LF<1>::orig), &LF<1>::h);
    A->raw_detour(6, target, &o2, reinterpret_cast<void*>(&LF<2>::fn), reinterpret_cast<void**>(&LF<2>::orig), &LF<2>::h);
    A->raw_detour(6, target, &o3, reinterpret_cast<void*>(&LF<3>::fn), reinterpret_cast<void**>(&LF<3>::orig), &LF<3>::h);
    take_order();
    volatile double a = 2.5, d = 3.75;
    volatile float b = 1.5f, e = 4.0f;
    const double r = game_fmix(a, b, 7, d, e);
    const double want = expectF(a + 1.0, b, 7, d + 0.25, e * 2.0f) + 0.5;
    check(std::fabs(r - want) < 1e-9, "xmm0-3 and the stack float survive the chain; the float return passes back",
          std::to_string(r) + " vs " + std::to_string(want));
    check(take_order() == "123", "float links ran in order");
}

void test_structured() {
    std::puts("-- detour2: 9 integer arguments (stack arguments included) with the caller");
    const uintptr_t target = reinterpret_cast<uintptr_t>(&game_sum9);
    // a structured link in front of the raw ones (priority 40) and one behind them (priority 0)
    const auto front = ord(40, "t/sum9-s-front"), back = ord(0, "t/sum9-s-back");
    KilnHandle hf = 0, hb = 0;
    check(A->detour2(6, target, &front, 9, &s2_fn, reinterpret_cast<void*>('S'), &hf) == KILN_OK, "detour2 installed (front)");
    check(A->detour2(6, target, &back, 9, &s2_fn, reinterpret_cast<void*>('s'), &hb) == KILN_OK, "detour2 installed (back)");
    check(A->detour2(6, target, &back, 13, &s2_fn, nullptr, nullptr) == KILN_ERR_INVALID_ARGUMENT,
          "detour2 refuses more than 12 arguments");
    take_order();
    g_probe = false;
    const uint64_t r = callsite_a(1, 2, 3, 4, 5, 6, 7, 8, 9);
    // chain: S (front) -> 1 -> 2 -> 3 -> s (back) -> real. Structured links add +1000 to arg 8, +1 to arg 2, +1 return.
    const uint64_t want = expect9(1 + 1, 2, 3 + 2, 4, 5, 6, 7, 8, 9 + 100 + 2000) + 2;
    check(r == want, "structured links in a mixed chain change stack arguments and the return",
          std::to_string(r) + " vs " + std::to_string(want));
    check(take_order() == "S123s", "structured links ran at their priority among the raw ones");
    check(g_s2_caller != 0 && g_s2_caller == L9<1>::seen, "KilnHookCall2.caller is the game's call site");
    A->revoke(hf);
    A->revoke(hb);
    take_order();
    const uint64_t r2 = callsite_a(1, 2, 3, 4, 5, 6, 7, 8, 9);
    check(r2 == expect9(1 + 1, 2, 3, 4, 5, 6, 7, 8, 9 + 100) && take_order() == "123", "revoking them leaves the raw chain");
}

void test_ordering() {
    std::puts("-- ordering: priority, registration order, before/after owners, an unsatisfiable constraint");
    // owners: 1 = service:kiln-chat.dll ("kiln.chat"), 2 = plugin:kiln-emf.dll ("kiln.emf"), 3 = emf:coolmod,
    //         4 = native:alpha, 5 = native:beta
    take_order();
    {
        const uintptr_t t = reinterpret_cast<uintptr_t>(&game_ord1);
        add_ord<0>(t, 4, 0);
        add_ord<1>(t, 4, 10);
        add_ord<2>(t, 5, 5);
        add_ord<3>(t, 5, 5);  // same priority as c: registration order
        game_ord1(1);
        check(take_order() == "bcda", "priority first (higher runs first), then registration order", "bcda");
    }
    {
        const uintptr_t t = reinterpret_cast<uintptr_t>(&game_ord2);
        add_ord<0>(t, 1, 0);                          // a: kiln.chat
        add_ord<1>(t, 2, 100, nullptr, "kiln.chat");  // b: kiln-emf, must run after kiln.chat despite priority 100
        add_ord<2>(t, 3, 50);                         // c
        game_ord2(1);
        const std::string got = take_order();
        check(got == "cab", "after=\"kiln.chat\" beats priority: kiln-emf's link follows kiln.chat's", got);
    }
    {
        const uintptr_t t = reinterpret_cast<uintptr_t>(&game_ord3);
        add_ord<0>(t, 4, 0, "native:beta");   // a before beta's link
        add_ord<1>(t, 5, 99);                 // b: beta, high priority
        add_ord<2>(t, 6, 50);                 // c
        game_ord3(1);
        const std::string got = take_order();
        check(got == "acb" || got == "cab", "before=\"native:beta\": alpha's link precedes beta's despite priority", got);
        check(got.find('a') < got.find('b'), "a before b");
    }
    {
        const uintptr_t t = reinterpret_cast<uintptr_t>(&game_ord4);
        add_ord<0>(t, 4, 0, "native:beta");   // a before b
        add_ord<1>(t, 5, 0, "native:alpha");  // b before a: contradicts a's constraint, dropped
        game_ord4(1);
        const std::string got = take_order();
        check(got == "ab", "a contradictory constraint is dropped; the earlier one holds", got);
        check(log_has("contradicts an earlier constraint"), "the dropped constraint is logged");
    }
}

void test_mid() {
    std::puts("-- mid links: at a function entry, in the middle of a function, beside raw links, xmm preserved");
    auto* fe = reinterpret_cast<int (*)(int)>(&game_asm_mid);
    static KilnHandle h1 = 0;
    static std::atomic<uintptr_t> top_of_stack{0}, mid_caller{0};
    static void* walk[32] = {};
    static USHORT walk_n = 0;
    static auto cb1 = +[](KilnRegisters* r, void*) {
        mid_caller = A->caller(h1);
        ran('M');
        r->rcx += 10;
        top_of_stack = *reinterpret_cast<uintptr_t*>(r->rsp);  // the hooked function's return address
        walk_n = RtlCaptureStackBackTrace(0, 32, walk, nullptr);  // unwinds through kiln_hk_mid's realigned frame
    };
    const auto o = ord(0, "t/mid-entry");
    check(A->mid2(6, reinterpret_cast<uintptr_t>(&game_asm_mid), &o, cb1, nullptr, &h1) == KILN_OK, "mid2 installed at an entry");
    take_order();
    check(fe(41) == 52 && take_order() == "M", "a mid hook at a function entry changes an argument register");
    check(top_of_stack != 0 && top_of_stack == mid_caller,
          "ctx.rsp is the hooked code's rsp: [rsp] is the return address caller() reports");
    {
        bool found = false;
        for (USHORT i = 0; i < walk_n; ++i) found = found || reinterpret_cast<uintptr_t>(walk[i]) == top_of_stack;
        check(found, "a stack walk from a mid handler unwinds through kiln_hk_mid to the game's caller",
              std::to_string(walk_n) + " frames");
    }

    // beside a raw link on the same target (priority decides: raw first)
    static int (*orig3)(int) = nullptr;
    static auto raw3 = +[](int x) -> int {
        ran('R');
        return orig3(x + 1);
    };
    const auto oraw = ord(5, "t/mid3-raw"), omid = ord(0, "t/mid3-mid");
    check(A->raw_detour(6, reinterpret_cast<uintptr_t>(&game_asm_mid3), &oraw, reinterpret_cast<void*>(raw3),
                        reinterpret_cast<void**>(&orig3), nullptr) == KILN_OK,
          "a raw link on the target");
    static auto cb3 = +[](KilnRegisters* r, void*) {
        ran('m');
        r->rcx += 100;
    };
    check(A->mid2(6, reinterpret_cast<uintptr_t>(&game_asm_mid3), &omid, cb3, nullptr, nullptr) == KILN_OK,
          "a mid link on the same target (supported: they share one physical detour)");
    take_order();
    const int v = game_asm_mid3(0);
    check(v == 102 && take_order() == "Rm", "raw then mid on one target: 0 +1 (raw) +100 (mid) +1 (function)", std::to_string(v));

    // the middle of a function: offset 5 (mov eax,ecx), stack not at the usual alignment, first call of a new thread
    static auto cb2 = +[](KilnRegisters* r, void*) {
        ran('x');
        r->rcx += 1;
    };
    uint8_t* mid_at = reinterpret_cast<uint8_t*>(&game_asm_mid2) + 5;
    check(A->mid(6, reinterpret_cast<uintptr_t>(mid_at), 0, cb2, nullptr, nullptr) == KILN_OK, "v1 mid installed mid-function");
    take_order();
    int got = 0;
    std::string order_in_thread;
    std::thread t([&] {
        got = reinterpret_cast<int (*)(int)>(&game_asm_mid2)(41);  // this thread's first hooked call: kiln_hk_slow at a mid-function point
    });
    t.join();
    check(got == 43 && take_order() == "x", "mid hook in the middle of a function, first call of a fresh thread", std::to_string(got));

    // xmm registers across a mid link: the handler clobbers them, the context restores them
    static auto clobber = +[](KilnRegisters*, void*) {
        ran('c');
        volatile double x = 1.25;
        for (int i = 0; i < 4; ++i) x = x * 1.5 + std::sqrt(x);
    };
    const auto oc = ord(1000, "t/fmix-clobber");
    check(A->mid2(6, reinterpret_cast<uintptr_t>(&game_fmix), &oc, clobber, nullptr, nullptr) == KILN_OK, "mid link on the float function");
    take_order();
    volatile double a = 2.5, d = 3.75;
    volatile float b = 1.5f, e = 4.0f;
    const double r = game_fmix(a, b, 7, d, e);
    const double want = expectF(a + 1.0, b, 7, d + 0.25, e * 2.0f) + 0.5;
    check(std::fabs(r - want) < 1e-9 && take_order() == "c123", "xmm0-3 and flags survive a mid link", std::to_string(r));
}

void test_mid_registers_and_flags() {
    std::puts("-- mid links keep every register and the flags (also on a thread's first call)");
    static auto flags_cb = +[](KilnRegisters*, void*) {
        ran('f');
        volatile int x = 0;  // plain code that is sure to write the flags
        for (int i = 0; i < 3; ++i) x = x + 1 - 7 * (x > 5);
    };
    const auto of = ord(0, "t/flags");
    check(A->mid2(6, reinterpret_cast<uintptr_t>(&game_asm_flags_hook), &of, flags_cb, nullptr, nullptr) == KILN_OK,
          "mid link between a cmp and its consumer");
    take_order();
    int a = -1, b = -1;
    std::thread t1([&] { a = game_asm_flags(5); });  // first hooked call of the thread: slow path with live flags
    t1.join();
    std::thread t2([&] { b = game_asm_flags(4); });
    t2.join();
    check(a == 1 && b == 0, "flags survive the entry stub's slow path and the context capture", std::to_string(a) + "," + std::to_string(b));
    take_order();

    static auto regs_cb = +[](KilnRegisters* r, void*) {
        ran('r');
        volatile double x = 1.0;
        for (int i = 0; i < 5; ++i) x = x * 1.1 + std::sqrt(x);  // clobbers xmm0-5
        r->r13 = 0x5556;                                           // a write-back
    };
    const auto orr = ord(0, "t/regs");
    check(A->mid2(6, reinterpret_cast<uintptr_t>(&game_asm_regs_hook), &orr, regs_cb, nullptr, nullptr) == KILN_OK, "mid link in a register-heavy function");
    int rr = -1;
    std::thread t3([&] { rr = game_asm_regs(0); });
    t3.join();
    check(rr == 1 && take_order() == "r", "rbx/rsi/rdi/r8-r15/rdx intact, and a handler's write to r13 takes effect");
    A->revoke_owner(6);
}

void test_v1_compat() {
    std::puts("-- v1: detour (4 args, now forwards every argument), mid, stats, revoke, v1 + raw on one target");
    static int calls = 0;
    static auto v1fn = +[](KilnHookCall* c, void*) -> uintptr_t {
        ++calls;
        ran('V');
        c->args[0] += 1;
        return A->call_original(c) + 1000000;
    };
    const uintptr_t target = reinterpret_cast<uintptr_t>(&game_v1);
    KilnHandle hv = 0;
    check(A->detour(6, target, 5, v1fn, nullptr, &hv) == KILN_OK && hv, "v1 detour installed");
    static uint64_t (*orig_r)(uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t) = nullptr;
    static auto rawfn = +[](uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f) -> uint64_t {
        ran('R');
        return orig_r(a, b, c + 1, d, e, f);
    };
    const auto o = ord(1, "t/v1-raw");
    check(A->raw_detour(6, target, &o, reinterpret_cast<void*>(rawfn), reinterpret_cast<void**>(&orig_r), nullptr) == KILN_OK,
          "a raw link on the same target");
    take_order();
    const uint64_t r = game_v1(1, 2, 3, 4, 5, 6);
    check(r == (1 + 1) + 10 * 2 + 100 * (3 + 1) + 1000 * 4 + 10000 * 5 + 100000 * 6 + 1000000,
          "v1 + raw share the physical detour; arguments past the 4th reach the original", std::to_string(r));
    check(take_order() == "VR", "v1 (priority 5) ran before the raw link (priority 1)");
    KilnHooksStats st{};
    st.size = sizeof st;
    const bool stats_ok = A->stats(&st) == KILN_OK;
    check(stats_ok && st.handlers > 0 && st.raw_links > 0 && st.detour_calls > 0 && st.targets > 0,
          "stats: v2 fields filled", "raw=" + std::to_string(st.raw_links) + " targets=" + std::to_string(st.targets));
    struct V1Stats {  // the v1 layout (a v1 caller passes this size)
        uint32_t size, a, b, c, d;
        uint64_t e, f;
    } old{};
    old.size = sizeof old;
    check(A->stats(reinterpret_cast<KilnHooksStats*>(&old)) == KILN_OK && old.c == st.handlers,
          "stats: a v1-sized struct still works");
    check(A->revoke(hv) == KILN_OK && A->revoke(hv) == KILN_ERR_NOT_FOUND, "revoke a v1 link, twice");
    take_order();
    game_v1(1, 2, 3, 4, 5, 6);
    check(take_order() == "R", "the revoked v1 link is gone");
    // v1 patch handles still revoke through the same function
    static uint8_t scratch[8] = {1, 2, 3, 4, 5, 6, 7, 8};
    const uint8_t patch[2] = {9, 9};
    KilnHandle hp = 0;
    check(A->patch(6, reinterpret_cast<uintptr_t>(scratch), patch, 2, &hp) == KILN_OK && scratch[0] == 9, "patch applied");
    check(A->revoke(hp) == KILN_OK && scratch[0] == 1 && scratch[1] == 2, "revoke() of a patch handle restores the bytes");
}

void test_continuation_after_revoke() {
    std::puts("-- a revoked link's continuation stays safe");
    const uintptr_t t = reinterpret_cast<uintptr_t>(&game_fault);
    static uint64_t (*orig_a)(uint64_t) = nullptr;
    static uint64_t (*orig_b)(uint64_t) = nullptr;
    static auto fa = +[](uint64_t x) -> uint64_t { ran('a'); return orig_a(x); };
    static auto fb = +[](uint64_t x) -> uint64_t { ran('b'); return orig_b(x + 100); };
    KilnHandle ha = 0, hb = 0;
    const auto oa = ord(10, "t/fault-a"), ob = ord(5, "t/fault-b");
    A->raw_detour(6, t, &oa, reinterpret_cast<void*>(fa), reinterpret_cast<void**>(&orig_a), &ha);
    A->raw_detour(6, t, &ob, reinterpret_cast<void*>(fb), reinterpret_cast<void**>(&orig_b), &hb);
    take_order();
    check(game_fault(1) == 102 && take_order() == "ab", "baseline");
    A->revoke(ha);
    take_order();
    // a handler that still holds `orig_a` (a's continuation) calls it after the revoke: it forwards to b, then the function
    check(orig_a(1) == 102 && take_order() == "b", "the revoked link's continuation forwards to the next live link");
    A->revoke(hb);
    take_order();
    check(orig_a(1) == 2 && take_order().empty(), "...and to the real function once nothing is left");
    check(game_fault(1) == 2, "an empty chain just calls the real function");
    A->revoke_owner(6);  // owner 6 owns most test links: no crash, idempotent
    A->revoke_owner(6);
    check(game_fault(7) == 8, "revoke_owner is idempotent and leaves the original reachable");
}

void test_faults_and_exceptions() {
    std::puts("-- faults and exceptions");
    // structured handler faults before calling the original: disabled, the original still runs exactly once
    {
        const uintptr_t t = reinterpret_cast<uintptr_t>(&game_stress);
        static auto bad = +[](KilnHookCall2* c, void*) -> uintptr_t {
            ran('B');
            *static_cast<volatile int*>(nullptr) = 1;
            return A->call_original2(c);
        };
        static auto good = +[](KilnHookCall2* c, void*) -> uintptr_t {
            ran('G');
            return A->call_original2(c);
        };
        KilnHandle hb = 0;
        const auto ob = ord(10, "t/stress-bad"), og = ord(5, "t/stress-good");
        A->detour2(6, t, &ob, 1, bad, nullptr, &hb);
        A->detour2(6, t, &og, 1, good, nullptr, nullptr);
        take_order();
        g_real = 0;
        const uint64_t r = game_stress(5);
        check(r == 16 && g_real == 1 && take_order() == "BG", "a faulting structured handler is disabled; the chain continues, once",
              std::to_string(r));
        check(log_has("faulted") && log_has("was disabled"), "the fault is logged");
        take_order();
        check(game_stress(5) == 16 && take_order() == "G", "the disabled link is skipped afterwards");
        A->revoke_owner(6);
    }
    // a structured handler that faults AFTER calling the original: the original is not run a second time
    {
        const uintptr_t t = reinterpret_cast<uintptr_t>(&game_nested);
        static auto late = +[](KilnHookCall2* c, void*) -> uintptr_t {
            const uintptr_t r = A->call_original2(c);
            ran('L');
            *static_cast<volatile int*>(nullptr) = 1;
            return r + 1;
        };
        KilnHandle hl = 0;
        const auto ol = ord(10, "t/nested-late");
        A->detour2(6, t, &ol, 1, late, nullptr, &hl);
        take_order();
        g_real = 0;
        const uint64_t r = game_nested(3);
        // game_nested runs once and calls game_stress once: 2 real executions; value = stress(3) + 1 = 11
        check(r == 11 && g_real == 2 && take_order() == "L", "a handler that faults after calling the original does not run it twice",
              std::to_string(r) + " real=" + std::to_string(g_real.load()));
        A->revoke_owner(6);
    }
    // exception thrown by the game function, through raw + structured + raw links
    {
        const uintptr_t t = reinterpret_cast<uintptr_t>(&game_throw);
        static int (*oa)(int) = nullptr;
        static int (*ob)(int) = nullptr;
        static auto ra = +[](int x) -> int { ran('1'); return oa(x); };
        static auto rb = +[](int x) -> int { ran('3'); return ob(x); };
        static auto sf = +[](KilnHookCall2* c, void*) -> uintptr_t { ran('2'); return A->call_original2(c); };
        KilnHandle hs = 0;
        const auto o1 = ord(30, "t/throw-1"), o2 = ord(20, "t/throw-2"), o3 = ord(10, "t/throw-3");
        A->raw_detour(6, t, &o1, reinterpret_cast<void*>(ra), reinterpret_cast<void**>(&oa), nullptr);
        A->detour2(6, t, &o2, 1, sf, nullptr, &hs);
        A->raw_detour(6, t, &o3, reinterpret_cast<void*>(rb), reinterpret_cast<void**>(&ob), nullptr);
        take_order();
        int caught = 0;
        try {
            game_throw(1);
        } catch (int v) {
            caught = v;
        }
        check(caught == 42 && take_order() == "123", "a C++ exception thrown by the game unwinds through raw and structured links");
        check(game_throw(0) == 7, "the chain still works afterwards");
        take_order();
        std::string info;
        KilnHandle probe = hs;
        check(A->revoke(probe) == KILN_OK, "the structured link was still live (the exception was not blamed on it)");
        A->revoke_owner(6);
    }
    // a hardware fault in the game function itself is not the handler's: it reaches the caller's SEH handler
    {
        const uintptr_t t = reinterpret_cast<uintptr_t>(&game_av);
        static auto sf = +[](KilnHookCall2* c, void*) -> uintptr_t { ran('s'); return A->call_original2(c); };
        KilnHandle hs = 0;
        const auto o = ord(1, "t/av-s");
        A->detour2(6, t, &o, 1, sf, nullptr, &hs);
        take_order();
        DWORD code = 0;
        const int r = call_av(1, &code);
        check(r == -1 && code == EXCEPTION_ACCESS_VIOLATION && take_order() == "s",
              "an access violation inside the game function reaches the caller's handler", "code=" + std::to_string(code));
        check(call_av(0, &code) == 7, "...and the structured link is still active (not disabled)");
        A->revoke_owner(6);
    }
}

void test_foreign() {
    std::puts("-- foreign hooks: before Kiln (chained, recorded) and over Kiln (detected, never unhooked)");
    // before: another engine's E9 jmp is on the function when Kiln hooks it
    {
        auto* f = reinterpret_cast<uint8_t*>(&game_asm_fb);
        snapshot_file(f);
        foreign_hook(f, reinterpret_cast<void*>(&foreign_detour_fb), &g_fb_tramp);
        {   // what sigdb saw at resolve time: the live bytes of a symbol that differ from the file's
            kiln::sigdb::LiveDiff d;
            d.symbol = "fake/Fb";
            d.address = reinterpret_cast<uintptr_t>(f);
            d.at = d.address;
            d.bytes_ok = true;
            std::memcpy(d.bytes, f, 16);
            d.by = "jmp to test";
            g_live_diffs.push_back(d);
        }
        take_order();
        check(reinterpret_cast<int (*)(int)>(f)(41) == 42 && take_order() == "F", "the other engine's hook alone");
        static int (*orig)(int) = nullptr;
        static auto fn = +[](int x) -> int { ran('K'); return orig(x); };
        const auto o = ord(0, "t/fb");
        KilnHandle h = 0;
        check(A->raw_detour(6, reinterpret_cast<uintptr_t>(f), &o, reinterpret_cast<void*>(fn), reinterpret_cast<void**>(&orig), &h) == KILN_OK,
              "Kiln hooks a function that starts with a foreign jmp");
        check(reinterpret_cast<int (*)(int)>(f)(41) == 42 && take_order() == "KF", "both run: Kiln, then the other engine, then the original once");
        check(log_has("t/fb chained over " + exe_name_lower()) || log_has("chained over " + exe_name_lower()) ||
                  log_has("chained over hooks_v2_test.exe"),
              "kiln.log: \"<label> chained over <module>+0x...\"");
        const auto ch = chain::chain_of(reinterpret_cast<uintptr_t>(f));
        check(ch.size() == 1, "one link on the target");
        bool found = false;
        for (const auto& ti : chain::targets())
            if (ti.address == reinterpret_cast<uintptr_t>(f)) {
                found = ti.has_before && lower(ti.before.module) == exe_name_lower() && !ti.before.own && ti.links.size() == 1 &&
                        ti.links[0].kind == chain::Kind::Raw && ti.links[0].label == "t/fb" && !ti.has_over;
            }
        check(found, "targets(): the snapshot carries the links and the foreign hook it was chained over");
    }
    // over: Kiln hooks first, then another engine hooks over it (its decoder relocates Kiln's jmp)
    {
        auto* f = reinterpret_cast<uint8_t*>(&game_asm_fo);
        static int (*orig)(int) = nullptr;
        static auto fn = +[](int x) -> int { ran('K'); return orig(x); };
        const auto o = ord(0, "t/fo");
        A->raw_detour(6, reinterpret_cast<uintptr_t>(f), &o, reinterpret_cast<void*>(fn), reinterpret_cast<void**>(&orig), nullptr);
        take_order();
        check(reinterpret_cast<int (*)(int)>(f)(41) == 42 && take_order() == "K", "Kiln's hook alone");
        check(chain::check_foreign() == 0, "check_foreign: nothing hooked over yet");
        uint8_t own[16];
        std::memcpy(own, f, 16);
        foreign_hook(f, reinterpret_cast<void*>(&foreign_detour_fo), &g_fo_tramp);
        check(reinterpret_cast<int (*)(int)>(f)(41) == 42 && take_order() == "FK", "both run: the other engine, then Kiln, then the original once");
        const size_t over = chain::check_foreign();
        check(over == 1, "check_foreign: one target hooked over", std::to_string(over));
        check(log_has("hooked over Kiln's t/fo") || log_has("hooked over Kiln's"), "kiln.log: \"<module> hooked over Kiln's <label>\"");
        KilnHooksStats st{};
        st.size = sizeof st;
        A->stats(&st);
        check(st.foreign_over == 1 && st.foreign_before == 1, "stats count foreign-before and foreign-over", "before=" + std::to_string(st.foreign_before) + " over=" + std::to_string(st.foreign_over));
        // the hook map records both and the sigdb live-differs list
        size_t len = 0;
        check(A->map_json(nullptr, 0, &len) == KILN_ERR_BUFFER_TOO_SMALL && len > 100, "map_json reports the needed length");
        std::string buf(len + 1, '\0');
        check(A->map_json(buf.data(), buf.size(), &len) == KILN_OK, "map_json fills the buffer");
        json j = json::parse(buf.data(), buf.data() + len, nullptr, false);
        check(j.is_object() && j.value("format", 0) == 1 && j["targets"].is_array(), "map: format 1 with targets");
        bool saw_before = false, saw_over = false;
        for (auto& t : j["targets"]) {
            if (!t["foreignBefore"].is_null()) {
                saw_before = true;
                check(lower(t["foreignBefore"].value("module", "")) == exe_name_lower() && !t["foreignBefore"].value("offset", "").empty(),
                      "map: foreignBefore names the foreign module and offset", t["foreignBefore"].dump());
            }
            if (!t["foreignOver"].is_null()) {
                saw_over = true;
                check(lower(t["foreignOver"].value("module", "")) == exe_name_lower(), "map: foreignOver names the module",
                      t["foreignOver"].dump());
            }
            check(t.contains("rva") && t.contains("symbol") && t.contains("label") && t["links"].is_array(), "map: target has rva/symbol/label/links");
            for (auto& l : t["links"])
                if (!(l.contains("owner") && l.contains("kind") && l.contains("priority") && l.contains("label"))) check(false, "map: link fields");
        }
        check(saw_before && saw_over, "map: both foreign directions are listed");
        check(j["foreign"].is_array() && !j["foreign"].empty() && j["foreign"][0]["symbol"] == "fake/Fb" &&
                  lower(j["foreign"][0].value("module", "")) == exe_name_lower(),
              "map: sigdb's live-differs symbols appear under \"foreign\" with module attribution", j["foreign"].dump());
        // the foreign engine removes its jump: Kiln notices
        std::memcpy(f, own, 16);
        FlushInstructionCache(GetCurrentProcess(), f, 16);
        check(chain::check_foreign() == 0 && log_has("no longer hooked over"), "the foreign hook goes away: check_foreign clears the record");
    }
}

void test_analyze() {
    std::puts("-- foreign hook attribution: a jump into a trampoline outside any module");
    // like safetyhook's mid-hook trampoline: some code, then `call [rip+slot]` to the handler in a module
    uint8_t* arena = static_cast<uint8_t*>(VirtualAlloc(nullptr, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    uint8_t* tr = arena + 0x40;
    const uint8_t pre[] = {0x50, 0x53, 0x51, 0x52, 0x9C};  // push rax; push rbx; push rcx; push rdx; pushfq
    std::memcpy(tr, pre, sizeof pre);
    tr[5] = 0xFF, tr[6] = 0x15;
    const int32_t disp = static_cast<int32_t>((arena + 0x200) - (tr + 11));
    std::memcpy(tr + 7, &disp, 4);
    tr[11] = 0x9D, tr[12] = 0xC3;
    const uint64_t handler = reinterpret_cast<uint64_t>(&foreign_detour_fb);
    std::memcpy(arena + 0x200, &handler, 8);
    uint8_t site[16] = {};
    uint8_t* at = arena + 0x800;  // where the foreign E9 would sit
    rel_jmp(at, tr);
    std::memcpy(site, at, 16);
    const chain::Dest d = chain::analyze(reinterpret_cast<uintptr_t>(at), site);
    check(d.present && lower(d.module) == exe_name_lower() && d.address == handler,
          "an E9 into a trampoline that calls a handler through [rip+slot] is attributed to the handler's module", d.module);
    // plain cases
    uint8_t relay[16];
    abs_jmp(relay, reinterpret_cast<void*>(&foreign_detour_fb));
    uint8_t* at2 = arena + 0x900;
    std::memcpy(at2, relay, 14);
    uint8_t site2[16] = {};
    std::memcpy(site2, at2, 14);
    const chain::Dest d2 = chain::analyze(reinterpret_cast<uintptr_t>(at2), site2);
    check(d2.present && lower(d2.module) == exe_name_lower() && d2.offset != 0, "a 14-byte absolute jmp is followed to its module+offset");
    uint8_t plain[16] = {0x90, 0x90, 0x90, 0x90, 0x90};
    const chain::Dest d3 = chain::analyze(reinterpret_cast<uintptr_t>(at2), plain);
    check(!d3.present && d3.in_place, "bytes without a jump are reported as patched in place");
    VirtualFree(arena, 0, MEM_RELEASE);
}

void test_map_file_and_extension() {
    std::puts("-- hooks.json on disk, map extensions");
    static auto ext = +[](void* j, const char* phase) {
        auto& doc = *static_cast<nlohmann::ordered_json*>(j);
        doc["overlaps"] = nlohmann::ordered_json::array();
        doc["extPhase"] = phase;
    };
    kiln::hooks::add_map_extension(ext);
    kiln::hooks::boot_complete();
    std::ifstream in(g_state_dir / "hooks.json");
    std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    json j = json::parse(text, nullptr, false);
    check(j.is_object() && j.value("phase", "") == "boot" && j.contains("overlaps") && j.value("extPhase", "") == "boot",
          "boot_complete writes state/hooks.json; extensions add their sections");
    check(log_has("map state/hooks.json"), "boot_complete logs its summary");
}

void test_threads() {
    std::puts("-- 4 threads call in a loop while links are added and revoked");
    const uintptr_t t = reinterpret_cast<uintptr_t>(&game_stress);
    static std::atomic<uint64_t> hits[6];
    static uint64_t (*orig[6])(uint64_t) = {};
    // handlers per index: count, forward
    static auto h0 = +[](uint64_t x) -> uint64_t { hits[0]++; return orig[0](x); };
    static auto h1 = +[](uint64_t x) -> uint64_t { hits[1]++; return orig[1](x); };
    static auto h2 = +[](uint64_t x) -> uint64_t { hits[2]++; return orig[2](x); };
    static auto h3 = +[](uint64_t x) -> uint64_t { hits[3]++; return orig[3](x); };
    static auto h4 = +[](uint64_t x) -> uint64_t { hits[4]++; return orig[4](x); };
    static auto h5 = +[](uint64_t x) -> uint64_t { hits[5]++; return orig[5](x); };
    void* fns[6] = {reinterpret_cast<void*>(h0), reinterpret_cast<void*>(h1), reinterpret_cast<void*>(h2),
                    reinterpret_cast<void*>(h3), reinterpret_cast<void*>(h4), reinterpret_cast<void*>(h5)};
    KilnHandle hd[6] = {};
    auto add = [&](int i, int prio) {
        const auto o = ord(prio, "t/stress");
        return A->raw_detour(6, t, &o, fns[i], reinterpret_cast<void**>(&orig[i]), &hd[i]) == KILN_OK;
    };
    check(add(0, 30) && add(1, 20) && add(2, 10), "three links installed");
    g_real = 0;
    std::atomic<bool> stop{false};
    std::atomic<uint64_t> calls{0}, bad{0};
    std::vector<std::thread> th;
    for (int k = 0; k < 4; ++k)
        th.emplace_back([&, k] {
            uint64_t i = static_cast<uint64_t>(k) * 1000003;
            while (!stop.load()) {
                const uint64_t r = game_stress(i);
                if (r != i * 3 + 1) ++bad;
                ++calls;
                ++i;
            }
        });
    auto nap = [] { std::this_thread::sleep_for(std::chrono::milliseconds(60)); };
    static std::atomic<uint64_t> s_hits{0}, m_hits{0};
    static auto s_fn = +[](KilnHookCall2* c, void*) -> uintptr_t { s_hits++; return A->call_original2(c); };
    static auto m_fn = +[](KilnRegisters*, void*) { m_hits++; };
    KilnHandle hs = 0, hm = 0;
    nap();
    A->revoke(hd[1]);  // the middle one
    nap();
    add(3, 25);        // in front of one, behind another
    {
        const auto os = ord(22, "t/stress-s"), om = ord(18, "t/stress-m");
        A->detour2(6, t, &os, 1, s_fn, nullptr, &hs);
        A->mid2(6, t, &om, m_fn, nullptr, &hm);
    }
    nap();
    A->revoke(hs);
    nap();
    A->revoke(hd[0]);  // the head
    nap();
    add(4, 5);         // the tail
    add(5, 50);        // a new head
    nap();
    A->revoke(hd[2]);
    A->revoke(hd[5]);
    nap();
    check(m_hits > 0 && s_hits > 0, "structured and mid links in the churn were reached", "s=" + std::to_string(s_hits.load()) + " m=" + std::to_string(m_hits.load()));
    A->revoke_owner(6);  // everything, at once
    nap();
    stop = true;
    for (auto& x : th) x.join();
    check(bad == 0, "every call returned the right value through every chain state", "bad=" + std::to_string(bad.load()));
    check(g_real == calls, "no lost calls: the real function ran once per call", std::to_string(g_real.load()) + " vs " + std::to_string(calls.load()));
    check(calls > 1000, "the loops really ran", std::to_string(calls.load()));
    uint64_t total_hits = 0;
    for (auto& x : hits) total_hits += x;
    check(total_hits > 0, "links were reached while they were live");
    check(game_stress(2) == 7, "the target still works with an empty chain");
}

// Random adds and revokes of every kind, with random priorities and constraints, while 4 threads call.
template <int N>
struct CL {
    static inline uint64_t (*orig)(uint64_t) = nullptr;
    static inline std::atomic<uint64_t> hits{0};
    static uint64_t fn(uint64_t x) {
        hits++;
        return orig(x);
    }
};
constexpr int kPool = 16;
template <int... I>
void add_chaos_raw(int pick, uintptr_t target, KilnOwner owner, const KilnHookOrder& o, KilnHandle* h, std::integer_sequence<int, I...>) {
    ((pick == I ? (void)A->raw_detour(owner, target, &o, reinterpret_cast<void*>(&CL<I>::fn), reinterpret_cast<void**>(&CL<I>::orig), h) : (void)0), ...);
}
struct Rec {
    KilnHandle h;
    KilnOwner owner;
    int tmpl;  // the CL<> template a raw link uses (its `original` variable belongs to that one link), else -1
};

void test_chaos() {
    std::puts("-- random link churn (raw, structured, mid; priorities, constraints, revoke_owner) under 4 calling threads");
    const uintptr_t t = reinterpret_cast<uintptr_t>(&game_chaos);
    static std::atomic<uint64_t> shits{0}, mhits{0};
    static auto s_fn = +[](KilnHookCall2* c, void*) -> uintptr_t { shits++; return A->call_original2(c); };
    static auto m_fn = +[](KilnRegisters*, void*) { mhits++; };
    std::atomic<bool> stop{false};
    std::atomic<uint64_t> calls{0}, bad{0};
    g_real = 0;
    std::vector<std::thread> th;
    for (int k = 0; k < 4; ++k)
        th.emplace_back([&, k] {
            uint64_t i = static_cast<uint64_t>(k) * 104729;
            while (!stop.load()) {
                if (game_chaos(i) != i * 3 + 7) ++bad;
                ++calls;
                ++i;
            }
        });
    uint32_t rng = 12345;
    auto rnd = [&](uint32_t n) { rng = rng * 1664525u + 1013904223u; return (rng >> 8) % n; };
    const char* names[] = {"service:kiln-chat.dll", "plugin:kiln-emf.dll", "emf:coolmod", "native:alpha"};
    const char* tokens[] = {"kiln.chat", "kiln.emf", "coolmod", "native:alpha", "nobody"};
    std::vector<Rec> live;
    unsigned used = 0;  // bit i: CL<i> belongs to a live link
    const auto t0 = std::chrono::steady_clock::now();
    int adds = 0, revokes = 0;
    while (std::chrono::steady_clock::now() - t0 < std::chrono::milliseconds(2500)) {
        const uint32_t act = rnd(10);
        if (act < 6 || live.size() < 3) {
            const KilnOwner owner = 1 + rnd(4);
            const char* before = rnd(4) == 0 ? tokens[rnd(5)] : nullptr;
            const char* after = rnd(4) == 0 ? tokens[rnd(5)] : nullptr;
            const KilnHookOrder o = ord(static_cast<int>(rnd(7)) - 3, "t/chaos", before, after);
            KilnHandle h = 0;
            uint32_t kind = rnd(3);
            int tmpl = -1;
            if (kind == 0) {
                for (int k = 0; k < kPool && tmpl < 0; ++k)
                    if (!(used & (1u << k))) tmpl = k;
                if (tmpl < 0) kind = 1;
            }
            KilnResult rc = KILN_ERR_INTERNAL;
            if (kind == 0) {
                add_chaos_raw(tmpl, t, owner, o, &h, std::make_integer_sequence<int, kPool>{});
                rc = h ? KILN_OK : KILN_ERR_INTERNAL;
                if (rc == KILN_OK) used |= 1u << tmpl;
            } else if (kind == 1) {
                rc = A->detour2(owner, t, &o, 1, s_fn, nullptr, &h);
            } else {
                rc = A->mid2(owner, t, &o, m_fn, nullptr, &h);
            }
            if (rc == KILN_OK && h) live.push_back({h, owner, kind == 0 ? tmpl : -1}), ++adds;
        } else if (act < 9) {
            const size_t i = rnd(static_cast<uint32_t>(live.size()));
            A->revoke(live[i].h);
            if (live[i].tmpl >= 0) used &= ~(1u << live[i].tmpl);
            live.erase(live.begin() + static_cast<long>(i));
            ++revokes;
        } else {
            const KilnOwner o = 1 + rnd(4);
            A->revoke_owner(o);
            for (size_t i = 0; i < live.size();)
                if (live[i].owner == o) {
                    if (live[i].tmpl >= 0) used &= ~(1u << live[i].tmpl);
                    live.erase(live.begin() + static_cast<long>(i));
                } else {
                    ++i;
                }
            ++revokes;
        }
        if (rnd(3) == 0) std::this_thread::sleep_for(std::chrono::microseconds(200));
    }
    stop = true;
    for (auto& x : th) x.join();
    check(bad == 0, "every call returned the right value through every chain state", "bad=" + std::to_string(bad.load()));
    check(g_real == calls, "no lost calls: the real function ran once per call", std::to_string(g_real.load()) + " vs " + std::to_string(calls.load()));
    check(adds > 100 && revokes > 20 && calls > 1000, "the churn really ran", "adds=" + std::to_string(adds) + " revokes=" + std::to_string(revokes) + " calls=" + std::to_string(calls.load()));
    // quiescent: priority order with no constraints, via the raw links that are left
    for (KilnOwner o = 1; o <= 4; ++o) A->revoke_owner(o);
    check(chain::chain_of(t).empty() && game_chaos(5) == 22, "revoke_owner of everything empties the chain");
    take_order();
    static uint64_t (*qo[4])(uint64_t) = {};
    static auto q0 = +[](uint64_t x) -> uint64_t { ran('0'); return qo[0](x); };
    static auto q1 = +[](uint64_t x) -> uint64_t { ran('1'); return qo[1](x); };
    static auto q2 = +[](uint64_t x) -> uint64_t { ran('2'); return qo[2](x); };
    static auto q3 = +[](uint64_t x) -> uint64_t { ran('3'); return qo[3](x); };
    void* qf[4] = {reinterpret_cast<void*>(q0), reinterpret_cast<void*>(q1), reinterpret_cast<void*>(q2), reinterpret_cast<void*>(q3)};
    const int prios[4] = {1, 5, 5, -2};
    for (int i = 0; i < 4; ++i) {
        const KilnHookOrder o = ord(prios[i], "t/chaos-q");
        A->raw_detour(5, t, &o, qf[i], reinterpret_cast<void**>(&qo[i]), nullptr);
    }
    game_chaos(1);
    check(take_order() == "1203", "after the churn a fresh chain still orders by priority, then registration");
    A->revoke_owner(5);
}

void test_first_install_under_load() {
    std::puts("-- the first link on a target goes in while 4 threads call it");
    std::atomic<bool> stop{false};
    std::atomic<uint64_t> calls{0}, bad{0};
    std::vector<std::thread> th;
    for (int k = 0; k < 4; ++k)
        th.emplace_back([&, k] {
            uint64_t i = static_cast<uint64_t>(k) * 7919;
            while (!stop.load()) {
                if (game_late1(i) != i * 3 + 1) ++bad;
                if (game_late2(i) != i * 3 + 2) ++bad;
                if (game_late3(i) != i * 3 + 3) ++bad;
                calls += 3;
                ++i;
            }
        });
    auto nap = [] { std::this_thread::sleep_for(std::chrono::milliseconds(80)); };
    static std::atomic<uint64_t> hit1{0}, hit3{0};
    static uint64_t (*o1)(uint64_t) = nullptr;
    static auto f1 = +[](uint64_t x) -> uint64_t { hit1++; return o1(x); };
    static auto s2 = +[](KilnHookCall2* c, void*) -> uintptr_t { return A->call_original2(c); };
    static auto m3 = +[](KilnRegisters*, void*) { hit3++; };
    nap();
    const auto a = ord(0, "t/late1"), b = ord(0, "t/late2"), c = ord(0, "t/late3");
    bool ok = A->raw_detour(6, reinterpret_cast<uintptr_t>(&game_late1), &a, reinterpret_cast<void*>(f1), reinterpret_cast<void**>(&o1), nullptr) == KILN_OK;
    nap();
    ok = A->detour2(6, reinterpret_cast<uintptr_t>(&game_late2), &b, 1, s2, nullptr, nullptr) == KILN_OK && ok;
    nap();
    ok = A->mid2(6, reinterpret_cast<uintptr_t>(&game_late3), &c, m3, nullptr, nullptr) == KILN_OK && ok;
    nap();
    stop = true;
    for (auto& t : th) t.join();
    check(ok && bad == 0, "three first installs under load: every call still returned the right value", "bad=" + std::to_string(bad.load()));
    check(hit1 > 0 && hit3 > 0 && calls > 1000, "the new links were reached afterwards", "calls=" + std::to_string(calls.load()));
    A->revoke_owner(6);
}

// ---- the client header
const KilnHooksApi* g_fake_table = nullptr;
const void* KILN_CALL fake_service(KilnStr name, uint32_t min_version) {
    if (kiln::sv(name) != "kiln.hooks") return nullptr;
    return g_fake_table && g_fake_table->header.version >= min_version ? g_fake_table : nullptr;
}
kiln::RawHook g_hdr;
kiln::RawHook g_hdr_moved;
uintptr_t g_hdr_caller = 0;
uint64_t hdr_fn(uint64_t a, int b) {
    g_hdr_caller = g_hdr.caller();
    ran('H');
    return g_hdr.call<uint64_t>(a + 1, b);
}
struct SameLayoutCtx {  // a safetyhook::Context stand-in: same size and field order
    KilnRegisters r;
};
void hdr_mid(SameLayoutCtx& c) {
    ran('m');
    c.r.rcx += 5;
}

void test_client_header() {
    std::puts("-- kiln_hook.hpp: RawHook and MidHook2 against the v2 table");
    KilnApi api{};
    api.size = sizeof api;
    api.abi_version = KILN_ABI_VERSION;
    api.service = &fake_service;
    g_fake_table = A;
    const uintptr_t t = reinterpret_cast<uintptr_t>(&game_hdr);
    check(g_hdr.install(&api, 6, t, reinterpret_cast<void*>(&hdr_fn), "hdr/game_hdr", 3) && g_hdr && g_hdr.handle(), "RawHook::install in place");
    take_order();
    g_probe = false;
    volatile uint64_t av = 4;
    volatile int bv = 2;
    const uint64_t r = game_hdr(av, bv);
    check(r == 5 * 10 + 2 && take_order() == "H", "call<R>(args...) continues the chain and the real function");
    check(g_hdr_caller != 0, "caller() inside the handler is the game's call site (non-zero)");
    check(g_hdr.original<uint64_t (*)(uint64_t, int)>() != nullptr, "original<T>() gives the continuation");
    kiln::MidHook2 mh = kiln::MidHook2::create(&api, 6, t, &hdr_mid, "hdr/mid", 0);
    check(static_cast<bool>(mh), "MidHook2::create takes a handler of any same-layout context type");
    take_order();
    const uint64_t r2 = game_hdr(av, bv);  // mid (prio 0) after the raw link (prio 3): rcx 4 -> raw +1 -> mid +5 = 10 -> 102
    check(r2 == (4 + 1 + 5) * 10 + 2 && take_order() == "Hm", "raw link then mid link on one target through the header", std::to_string(r2));
    mh.revoke();
    check(!mh && take_order().empty(), "MidHook2::revoke");
    g_hdr_moved = std::move(g_hdr);
    check(g_hdr_moved && !g_hdr, "RawHook moves");
    g_hdr_moved.revoke();
    check(!g_hdr_moved, "RawHook::revoke");
    uint64_t (*stale)(uint64_t, int) = nullptr;
    (void)stale;
    take_order();
    check(game_hdr(av, bv) == 4 * 10 + 2 && take_order().empty(), "after revoke the chain is empty again");
    // re-installing / assigning replaces the link instead of stacking a second one (safetyhook's assignment semantics)
    check(g_hdr.install(&api, 6, t, reinterpret_cast<void*>(&hdr_fn), "hdr/game_hdr", 3), "RawHook::install again");
    const KilnHandle first = g_hdr.handle();
    check(g_hdr.install(&api, 6, t, reinterpret_cast<void*>(&hdr_fn), "hdr/game_hdr", 3) && g_hdr.handle() != first,
          "RawHook::install on a live hook");
    take_order();
    check(game_hdr(av, bv) == 5 * 10 + 2 && take_order() == "H" && chain::chain_of(t).size() == 1,
          "install() on a live hook replaced the link (one link, the handler runs once)");
    g_hdr = kiln::RawHook::create(&api, 6, t, reinterpret_cast<void*>(&hdr_fn), "hdr/game_hdr", 3);
    take_order();
    check(g_hdr && game_hdr(av, bv) == 5 * 10 + 2 && take_order() == "H" && chain::chain_of(t).size() == 1,
          "g = RawHook::create(...) over a live hook revokes the old link");
    g_hdr = {};
    take_order();
    check(!g_hdr && g_hdr.original<uint64_t (*)(uint64_t, int)>() != nullptr && chain::chain_of(t).empty() &&
              game_hdr(av, bv) == 4 * 10 + 2 && take_order().empty(),
          "g = {} revokes the link and keeps original() valid for a handler still running");
    kiln::MidHook2 m1 = kiln::MidHook2::create(&api, 6, t, &hdr_mid, "hdr/mid", 0);
    m1 = kiln::MidHook2::create(&api, 6, t, &hdr_mid, "hdr/mid", 0);
    check(m1 && chain::chain_of(t).size() == 1, "MidHook2 assignment revokes the old link");
    m1 = {};
    check(!m1 && chain::chain_of(t).empty(), "MidHook2 = {} revokes");
    // a core that only has v1
    KilnHooksApi v1 = *A;
    v1.header.version = 1;
    v1.header.size = offsetof(KilnHooksApi, raw_detour);
    g_fake_table = &v1;
    kiln::RawHook none;
    check(!none.install(&api, 6, t, reinterpret_cast<void*>(&hdr_fn), "x") && !none && none.error() != nullptr,
          "a v1-only kiln.hooks makes install() fail with an error text", none.error() ? none.error() : "");
    g_fake_table = A;
}

void test_thread_tables() {
    std::puts("-- per-thread caller tables are recycled");
    const size_t before = chain::thread_tables();
    for (int round = 0; round < 3; ++round) {
        std::vector<std::thread> th;
        for (int k = 0; k < 8; ++k) th.emplace_back([] { game_stress(1); });
        for (auto& t : th) t.join();
    }
    const size_t after = chain::thread_tables();
    check(after <= before + 8 + 2, "24 short-lived threads reuse the tables of finished ones", "before=" + std::to_string(before) + " after=" + std::to_string(after));
    // a thread's first hooked call (table lookup, recycling: OpenThread on finished threads) keeps its last-error value
    DWORD err = 0;
    std::thread([&] {
        SetLastError(0xBEEF);
        game_stress(1);
        err = GetLastError();
    }).join();
    check(err == 0xBEEF, "the first hooked call of a thread preserves GetLastError()", std::to_string(err));
}

}  // namespace

// ---------------------------------------------------------------------------------------------------------------------
int main(int argc, char** argv) {
    // The game's section: the range the hooks service treats as the image.
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
        }
    if (!kiln::g_image.base) {
        std::puts("FAIL no .game section");
        return 1;
    }
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    g_state_dir = std::filesystem::path(tmp) / L"kiln-hooks-v2-test";
    if (argc > 1 && !std::strcmp(argv[1], "tls64"))  // push the caller-table slot into TlsExpansionSlots
        for (int i = 0; i < 70; ++i) TlsAlloc();
    std::printf("hooks_v2_test: game section %p +0x%zx, tls %s\n", reinterpret_cast<void*>(kiln::g_image.base),
                kiln::g_image.size, argc > 1 ? argv[1] : "normal");

    if (!g_service || !g_service->init || !g_service->init()) {
        std::puts("FAIL kiln.hooks service did not initialise");
        return 1;
    }
    g_service->available = true;
    check(kiln::hooks::ensure_ready() && chain::ready(), "ensure_ready() is idempotent (core code that hooks before the services start)");
    A = static_cast<const KilnHooksApi*>(g_service->table);
    check(A->header.version == KILN_HOOKS_VERSION && A->header.size == sizeof(KilnHooksApi), "service table is v2",
          "size " + std::to_string(A->header.size));
    check(A->raw_detour && A->caller && A->detour2 && A->call_original2 && A->mid2 && A->map_json, "every v2 entry is set");

    test_raw_chain_9args();
    test_floats();
    test_structured();
    test_ordering();
    test_mid();
    test_mid_registers_and_flags();
    test_v1_compat();
    test_continuation_after_revoke();
    test_faults_and_exceptions();
    test_foreign();
    test_analyze();
    test_map_file_and_extension();
    test_threads();
    test_chaos();
    test_first_install_under_load();
    test_client_header();
    test_thread_tables();

    {
        std::lock_guard lock(g_log_mutex);
        size_t errors = 0;
        for (const auto& l : g_lines)
            if (l[0] == 'E' && l.find("faulted") == std::string::npos) ++errors, std::printf("  unexpected log: %s\n", l.c_str());
        if (errors) ++g_fail;
    }
    std::printf("%d passed, %d failed\n", g_pass, g_fail);
    std::puts(g_fail ? "FAILED" : "all passed");
    return g_fail ? 1 : 0;
}
