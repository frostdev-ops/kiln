// SPDX-License-Identifier: MIT
// kiln.hooks typed links (dev.28) under Wine, no game: the real service (hooks.cpp), registry engine
// (hooks_chain.cpp), static machine code (hooks_chain_asm.cpp: kiln_hk_typed, kiln_hk_call_frame) and SEH helpers,
// with the core's logging, sigdb and owner registry replaced by stubs (the same stubs as hooks_v2_test.cpp).
//
// The "game" is the exe's own `.game` section: the synthetic function set of chapter 06b §9.9 (a struct returned
// through a hidden buffer, a double return, eight stack arguments, narrow integers, mixed integer and float
// positions), one that throws, one that faults, and a hand-assembled one that never writes its home space.
//
//   native/core/tests/test-hooks-typed.sh         (builds hooks_typed_test.exe and runs it under Wine)
#include <windows.h>

#include <intrin.h>

#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
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
#include "log.h"
#include "registry.h"
#include "services/hooks_chain.h"
#include "services/hooks_internal.h"
#include "services/kiln_hooks.h"
#include "services/service.h"
#include "sigdb.h"
#include "tick.h"

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
                                                  {6, "test"},
                                                  {7, "shroudtopia:modx"},
                                                  {8, "emf:mody"}};
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
std::atomic<int> g_sym_calls{0};
std::string symbol_name_at(uintptr_t) {
    ++g_sym_calls;
    return {};
}
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
void ran(char c) { g_order += c; }
std::string take_order() {
    std::string s;
    s.swap(g_order);
    return s;
}

std::atomic<uint64_t> g_real{0};       // real-function executions
std::atomic<uintptr_t> g_real_align{0};  // rsp & 15 at the real function's entry (8 = the standard alignment)

KilnHookOrder ord(int prio, const char* label) {
    KilnHookOrder o{};
    o.size = sizeof o;
    o.priority = prio;
    o.label = KilnStr{label, std::strlen(label)};
    return o;
}
KilnSigArg arg(const char* name, uint32_t kind, uint32_t size = 0) {
    return KilnSigArg{KilnStr{name, std::strlen(name)}, kind, size};
}
KilnSig sig_of(const KilnSigArg* args, uint32_t argc, uint32_t ret_kind, uint32_t ret_size = 0, uint8_t sret = 0) {
    KilnSig s{};
    s.size = sizeof s;
    s.argc = argc;
    s.args = args;
    s.ret_kind = ret_kind;
    s.ret_size = ret_size;
    s.sret = sret;
    return s;
}

float f32_at(const uint8_t* x) {
    float v;
    std::memcpy(&v, x, 4);
    return v;
}
double f64_at(const uint8_t* x) {
    double v;
    std::memcpy(&v, x, 8);
    return v;
}
void put_f32(uint8_t* x, float v) { std::memcpy(x, &v, 4); }
void put_f64(uint8_t* x, double v) { std::memcpy(x, &v, 8); }
float stack_f32(const KilnCallFrame* f, int k) {
    float v;
    std::memcpy(&v, &f->stack[k], 4);
    return v;
}

struct Big {  // 24 bytes: returned through a hidden buffer
    int64_t x, y;
    int32_t z;
};

}  // namespace

// ---------------------------------------------------------------------------------------------------------------------
// The game: functions in the .game section (the image range the stub image() reports).
#pragma code_seg(".game")
extern "C" {
__declspec(noinline) float tg_mix6(int32_t a, float b, double c, int64_t d, float e, int64_t f) {
    g_real.fetch_add(1);
    g_real_align.store(reinterpret_cast<uintptr_t>(_AddressOfReturnAddress()) & 15);
    return static_cast<float>(a) + b * 2.0f + static_cast<float>(c) + static_cast<float>(d) * 0.5f + e * 3.0f +
           static_cast<float>(f);
}
__declspec(noinline) Big tg_sret(int64_t a, double b, int64_t c, int64_t d, int64_t e) {
    g_real.fetch_add(1);
    Big r;
    r.x = a + d;
    r.y = c + e;
    r.z = static_cast<int32_t>(b * 2.0);
    return r;
}
__declspec(noinline) double tg_f64(double a, double b) {
    g_real.fetch_add(1);
    return a * 10.0 + b;
}
__declspec(noinline) uint64_t tg_sum12(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f,
                                       uint64_t g, uint64_t h, uint64_t i, uint64_t j, uint64_t k, uint64_t l) {
    g_real.fetch_add(1);
    return a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f + 7 * g + 8 * h + 9 * i + 10 * j + 11 * k + 12 * l;
}
__declspec(noinline) int64_t tg_narrow(uint8_t a, uint16_t b, int8_t c, bool d, uint8_t e, int16_t f) {
    g_real.fetch_add(1);
    return static_cast<int64_t>(a) + 3 * static_cast<int64_t>(b) + 5 * static_cast<int64_t>(c) + (d ? 1000 : 0) +
           7 * static_cast<int64_t>(e) + 11 * static_cast<int64_t>(f);
}
__declspec(noinline) int tg_throw(int x) {
    g_real.fetch_add(1);
    if (x) throw 42;
    return x + 7;
}
__declspec(noinline) int tg_av(int x) {
    g_real.fetch_add(1);
    if (x) *static_cast<volatile int*>(nullptr) = x;
    return x + 7;
}
__declspec(noinline) uint64_t tg_hot(uint64_t x, double y) {
    g_real.fetch_add(1, std::memory_order_relaxed);
    return x * 3 + 1 + static_cast<uint64_t>(y);
}
#define TG_PLAIN(n)                                                                                                    \
    __declspec(noinline) uint64_t tg_p##n(uint64_t x) {                                                                \
        g_real.fetch_add(1);                                                                                           \
        return x * 3 + n;                                                                                              \
    }
TG_PLAIN(1)
TG_PLAIN(2)
TG_PLAIN(3)
TG_PLAIN(4)
TG_PLAIN(5)
TG_PLAIN(6)
TG_PLAIN(7)
int tg_asm_home(int);
uint64_t kiln_home_call(void* fn, int x);
}
#pragma code_seg()

// tg_asm_home: `int f(int x)` = x + 1, hand-assembled so that it never writes its home space.
// kiln_home_call(fn, x): fills its outgoing home space with a pattern, calls fn(x), and returns 0xBADBAD when any of
// the four slots changed (somebody between the caller and a function that keeps its hands off wrote there), else
// fn's result.
__asm__(".intel_syntax noprefix\n.section .game,\"xr\"\n"
        ".globl tg_asm_home\n.def tg_asm_home; .scl 2; .type 32; .endef\ntg_asm_home:\n"
        "  push rbx\n  sub rsp, 0x20\n  mov eax, ecx\n  add eax, 1\n  add rsp, 0x20\n  pop rbx\n  ret\n"
        "  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n"
        "  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n  int3\n"
        ".text\n"
        ".globl kiln_home_call\n.def kiln_home_call; .scl 2; .type 32; .endef\n"
        ".seh_proc kiln_home_call\n"
        "kiln_home_call:\n"
        "  push rbx\n"
        "  .seh_pushreg rbx\n"
        "  sub rsp, 0x30\n"
        "  .seh_stackalloc 0x30\n"
        "  .seh_endprologue\n"
        "  mov rax, rcx\n"
        "  mov ecx, edx\n"
        "  mov rbx, 0x1122334455667788\n"
        "  mov [rsp+0x00], rbx\n"
        "  mov [rsp+0x08], rbx\n"
        "  mov [rsp+0x10], rbx\n"
        "  mov [rsp+0x18], rbx\n"
        "  call rax\n"
        "  mov eax, eax\n"
        "  cmp [rsp+0x00], rbx\n"
        "  jne .Lhome_bad\n"
        "  cmp [rsp+0x08], rbx\n"
        "  jne .Lhome_bad\n"
        "  cmp [rsp+0x10], rbx\n"
        "  jne .Lhome_bad\n"
        "  cmp [rsp+0x18], rbx\n"
        "  je .Lhome_ok\n"
        ".Lhome_bad:\n"
        "  mov eax, 0xBADBAD\n"
        ".Lhome_ok:\n"
        "  add rsp, 0x30\n"
        "  pop rbx\n"
        "  ret\n"
        ".seh_endproc\n"
        ".att_syntax prefix\n");

namespace {

// Calls through volatile pointers: the compiler may not fold or inline the "game".
float (*volatile p_mix6)(int32_t, float, double, int64_t, float, int64_t) = tg_mix6;
Big (*volatile p_sret)(int64_t, double, int64_t, int64_t, int64_t) = tg_sret;
double (*volatile p_f64)(double, double) = tg_f64;
uint64_t (*volatile p_sum12)(uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t,
                             uint64_t, uint64_t, uint64_t) = tg_sum12;
int64_t (*volatile p_narrow)(uint8_t, uint16_t, int8_t, bool, uint8_t, int16_t) = tg_narrow;
int (*volatile p_throw)(int) = tg_throw;
int (*volatile p_av)(int) = tg_av;
uint64_t (*volatile p_hot)(uint64_t, double) = tg_hot;
#define P_PLAIN(n) uint64_t (*volatile p_p##n)(uint64_t) = tg_p##n;
P_PLAIN(1) P_PLAIN(2) P_PLAIN(3) P_PLAIN(4) P_PLAIN(5) P_PLAIN(6) P_PLAIN(7)

template <class F>
uintptr_t T(F* f) {
    return reinterpret_cast<uintptr_t>(f);
}

// SEH wrapper: no C++ objects here.
int call_av(int x, DWORD* code) {
    __try {
        return p_av(x);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
void test_call_with_frame() {
    std::puts("-- call_with_frame: direct calls of unhooked functions (the Invoke path)");
    alignas(16) static uint8_t odd[sizeof(KilnCallFrame) + 16];  // an unaligned frame, as a caller's own may be
    {
        KilnCallFrame& f = *new (odd + 1) KilnCallFrame{};
        uint64_t st[2] = {};
        f.gpr[0] = static_cast<uint32_t>(7);
        put_f32(f.xmm[1], 1.5f);
        put_f64(f.xmm[2], 2.25);
        f.gpr[3] = 40;
        float e = 0.5f;
        std::memcpy(&st[0], &e, 4);
        st[1] = 100;
        f.stack = st;
        g_real = 0;
        g_real_align = 0;
        const KilnResult r = A->call_with_frame(T(&tg_mix6), &f, 2);
        const float want = p_mix6(7, 1.5f, 2.25, 40, 0.5f, 100);
        check(r == KILN_OK && f32_at(f.xmm0r) == want && g_real == 2,
              "mixed integer and float positions with two stack arguments; float return in xmm0r",
              std::to_string(f32_at(f.xmm0r)) + " vs " + std::to_string(want));
        check(g_real_align == 8, "the called function sees the standard stack alignment (rsp = 8 mod 16)");
    }
    {
        KilnCallFrame f{};
        Big out{};
        uint64_t st[2] = {4, 5};
        f.gpr[0] = T(&out);  // the hidden buffer: position 0
        f.gpr[1] = 1;        // a
        put_f64(f.xmm[2], 3.5);  // b
        f.gpr[3] = 3;        // c
        f.stack = st;        // d, e
        const KilnResult r = A->call_with_frame(T(&tg_sret), &f, 2);
        const Big want = p_sret(1, 3.5, 3, 4, 5);
        check(r == KILN_OK && out.x == want.x && out.y == want.y && out.z == want.z && f.rax == T(&out),
              "a struct return through the hidden buffer: declared arguments shift by one, rax is the buffer");
    }
    {
        KilnCallFrame f{};
        put_f64(f.xmm[0], 1.25);
        put_f64(f.xmm[1], 0.5);
        A->call_with_frame(T(&tg_f64), &f, 0);
        check(f64_at(f.xmm0r) == p_f64(1.25, 0.5), "a double return with no stack arguments");
    }
    {
        KilnCallFrame f{};
        uint64_t st[8] = {5, 6, 7, 8, 9, 10, 11, 12};
        f.gpr[0] = 1, f.gpr[1] = 2, f.gpr[2] = 3, f.gpr[3] = 4;
        f.stack = st;
        A->call_with_frame(T(&tg_sum12), &f, 8);
        check(f.rax == p_sum12(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12), "eight stack arguments");
    }
    {
        // narrow integers with garbage in the upper bytes, as the game's callers leave them
        KilnCallFrame f{};
        uint64_t st[2] = {0xABCDEF0000000000ull | 200u, 0x7777777777770000ull | static_cast<uint16_t>(-300)};
        f.gpr[0] = 0xFFFFFFFFFFFFFF00ull | 250u;
        f.gpr[1] = 0x1234567812340000ull | 60000u;
        f.gpr[2] = 0x5555555555555500ull | static_cast<uint8_t>(-100);
        f.gpr[3] = 0xAAAAAAAAAAAAAA00ull | 1u;
        f.stack = st;
        A->call_with_frame(T(&tg_narrow), &f, 2);
        const int64_t want = p_narrow(250, 60000, -100, true, 200, -300);
        check(static_cast<int64_t>(f.rax) == want, "narrow integers: the upper bytes of a position are not part of the value",
              std::to_string(static_cast<int64_t>(f.rax)) + " vs " + std::to_string(want));
    }
    {
        KilnCallFrame f{};
        check(A->call_with_frame(0, &f, 0) == KILN_ERR_INVALID_ARGUMENT &&
                  A->call_with_frame(T(&tg_f64), nullptr, 0) == KILN_ERR_INVALID_ARGUMENT &&
                  A->call_with_frame(T(&tg_sum12), &f, 17) == KILN_ERR_INVALID_ARGUMENT &&
                  A->call_with_frame(T(&tg_sum12), &f, 8) == KILN_ERR_INVALID_ARGUMENT,
              "call_with_frame refuses a null function, a null frame, more than 16 stack arguments and a missing stack");
    }
}

// ---- the mixed function through the thunk ----
struct Mix6Seen {
    int32_t a;
    float b;
    double c;
    int64_t d;
    float e;
    int64_t f;
    uintptr_t align;
    uint64_t slot;
    int calls;
} g_mix{};
uintptr_t KILN_CALL mix6_handler(KilnCallFrame* f, void* user) {
    g_mix.a = static_cast<int32_t>(f->gpr[0]);
    g_mix.b = f32_at(f->xmm[1]);
    g_mix.c = f64_at(f->xmm[2]);
    g_mix.d = static_cast<int64_t>(f->gpr[3]);
    g_mix.e = stack_f32(f, 0);
    g_mix.f = static_cast<int64_t>(f->stack[1]);
    g_mix.align = reinterpret_cast<uintptr_t>(_AddressOfReturnAddress()) & 15;
    g_mix.slot = f->slot;
    ++g_mix.calls;
    if (user) {  // change every kind of argument before the original runs
        put_f32(f->xmm[1], g_mix.b + 1.0f);
        put_f64(f->xmm[2], g_mix.c + 2.0);
        f->gpr[0] = static_cast<uint32_t>(g_mix.a + 3);
        const float e2 = g_mix.e + 4.0f;
        std::memcpy(&f->stack[0], &e2, 4);
        f->stack[1] = static_cast<uint64_t>(g_mix.f + 5);
    }
    return KILN_TYPED_CONTINUE;
}

void test_mixed_through_thunk() {
    std::puts("-- a typed link on the mixed function: arguments seen and changed");
    const KilnSigArg args[] = {arg("a", KILN_SIG_S32), arg("b", KILN_SIG_F32), arg("c", KILN_SIG_F64),
                               arg("d", KILN_SIG_S64), arg("e", KILN_SIG_F32), arg("f", KILN_SIG_S64)};
    const KilnSig sig = sig_of(args, 6, KILN_SIG_F32);
    const float direct = p_mix6(7, 1.5f, 2.25, 40, 0.5f, 100);
    const float changed = p_mix6(7 + 3, 1.5f + 1.0f, 2.25 + 2.0, 40, 0.5f + 4.0f, 100 + 5);
    KilnHandle h = 0;
    const auto o = ord(0, "t/mix6");
    check(A->detour_typed(6, T(&tg_mix6), &o, &sig, &mix6_handler, nullptr, &h) == KILN_OK && h, "detour_typed installed");
    chain::LinkInfo li;
    check(chain::link_info(h, &li) && li.kind == chain::Kind::Typed && li.live, "the link is of the typed kind");
    g_mix = {};
    g_real = 0;
    g_real_align = 0;
    const float r = p_mix6(7, 1.5f, 2.25, 40, 0.5f, 100);
    check(g_mix.calls == 1 && g_mix.a == 7 && g_mix.b == 1.5f && g_mix.c == 2.25 && g_mix.d == 40 && g_mix.e == 0.5f &&
              g_mix.f == 100,
          "the handler saw rcx, xmm1, xmm2, r9 and both stack arguments");
    check(r == direct && g_real == 1, "falling through runs the original once; its float return reaches the caller",
          std::to_string(r) + " vs " + std::to_string(direct));
    check(g_mix.align == 8 && g_real_align == 8, "standard stack alignment at the handler and at the original");
    check(g_mix.slot == h, "frame.slot is the running link's handle");
    check(A->caller(h) != 0, "caller() works for a typed link");
    A->revoke(h);
    KilnHandle h2 = 0;
    const size_t slots = chain::link_count();
    check(A->detour_typed(6, T(&tg_mix6), &o, &sig, &mix6_handler, reinterpret_cast<void*>(1), &h2) == KILN_OK,
          "a second typed link (other user data) installed");
    g_mix = {};
    const float r2 = p_mix6(7, 1.5f, 2.25, 40, 0.5f, 100);
    check(r2 == changed && g_mix.calls == 1, "changed register, xmm and stack arguments reach the original",
          std::to_string(r2) + " vs " + std::to_string(changed));
    // the revoked first link comes back in its old slot
    KilnHandle h3 = 0;
    check(A->detour_typed(6, T(&tg_mix6), &o, &sig, &mix6_handler, nullptr, &h3) == KILN_OK && h3 != h &&
              chain::link_count() == slots + 1,
          "re-installing a revoked typed link takes its slot again (a new handle)");
    A->revoke_owner(6);
    g_mix = {};
    check(p_mix6(7, 1.5f, 2.25, 40, 0.5f, 100) == direct && g_mix.calls == 0, "revoke_owner removes typed links");
}

// ---- struct return ----
int g_sret_calls = 0;
uintptr_t KILN_CALL sret_after(KilnCallFrame* f, void*) {  // an "After" step: change the struct the original wrote
    ++g_sret_calls;
    A->call_original_frame(f);
    auto* out = reinterpret_cast<Big*>(f->gpr[0]);
    out->z += 1000;
    return KILN_TYPED_CONTINUE;
}
uintptr_t KILN_CALL sret_skip(KilnCallFrame* f, void*) {  // replace the function: fill the caller's buffer
    ++g_sret_calls;
    auto* out = reinterpret_cast<Big*>(f->gpr[0]);
    out->x = static_cast<int64_t>(f->gpr[1]) * 100;  // a
    out->y = static_cast<int64_t>(f->stack[1]);       // e
    out->z = static_cast<int32_t>(f64_at(f->xmm[2])); // b
    f->rax = f->gpr[0];
    return KILN_TYPED_SKIP;
}

void test_sret() {
    std::puts("-- struct return through the hidden buffer");
    const KilnSigArg args[] = {arg("a", KILN_SIG_S64), arg("b", KILN_SIG_F64), arg("c", KILN_SIG_S64),
                               arg("d", KILN_SIG_S64), arg("e", KILN_SIG_S64)};
    const KilnSig sig = sig_of(args, 5, KILN_SIG_STRUCT, sizeof(Big), 1);
    const Big want = p_sret(1, 3.5, 3, 4, 5);
    KilnHandle h = 0;
    const auto o = ord(0, "t/sret-after");
    A->detour_typed(6, T(&tg_sret), &o, &sig, &sret_after, nullptr, &h);
    g_real = 0;
    g_sret_calls = 0;
    const Big r = p_sret(1, 3.5, 3, 4, 5);
    check(g_sret_calls == 1 && g_real == 1 && r.x == want.x && r.y == want.y && r.z == want.z + 1000,
          "a struct return changed after the original reaches the caller; six positions were forwarded");
    A->revoke(h);
    const auto o2 = ord(0, "t/sret-skip");
    A->detour_typed(6, T(&tg_sret), &o2, &sig, &sret_skip, nullptr, &h);
    g_real = 0;
    const Big r2 = p_sret(1, 3.5, 3, 4, 5);
    check(g_real == 0 && r2.x == 100 && r2.y == 5 && r2.z == 3, "a handler that skips fills the caller's buffer itself");
    A->revoke_owner(6);
}

// ---- the call state machine ----
uintptr_t KILN_CALL sm_handler(KilnCallFrame* f, void* user) {
    const char tag = static_cast<char>(reinterpret_cast<uintptr_t>(user));
    ran(tag);
    switch (tag) {
        case 'S':  // skip with a result
            f->rax = 4242;
            return KILN_TYPED_SKIP;
        case 'F':  // fall through after changing the argument
            f->gpr[0] += 10;
            return KILN_TYPED_CONTINUE;
        case 'T': {  // call the original twice: the second call does nothing
            const KilnResult r1 = A->call_original_frame(f);
            const uint64_t first = f->rax;
            f->gpr[0] += 100;  // would change the result if the original ran again
            const KilnResult r2 = A->call_original_frame(f);
            if (r1 != KILN_OK || r2 != KILN_OK || f->rax != first) f->rax = 0xDEAD;
            return KILN_TYPED_CONTINUE;
        }
        case 'R':  // change the return after the original
            A->call_original_frame(f);
            f->rax += 1;
            return KILN_TYPED_CONTINUE;
        case 'K':  // call the original, then ask to skip: the original's result stands, nothing runs twice
            A->call_original_frame(f);
            return KILN_TYPED_SKIP;
        case 'L':  // fault after the original ran (having overwritten the result first)
            A->call_original_frame(f);
            f->rax = 999;
            *static_cast<volatile int*>(nullptr) = 1;
            return KILN_TYPED_CONTINUE;
        case 'E':  // fault before the original, after changing the argument and asking for nothing
            f->gpr[0] += 1;
            *static_cast<volatile int*>(nullptr) = 1;
            return KILN_TYPED_SKIP;
        default:  // a plain observer
            return KILN_TYPED_CONTINUE;
    }
}
void* tag(char c) { return reinterpret_cast<void*>(static_cast<uintptr_t>(c)); }

void test_state_machine() {
    std::puts("-- the call state machine");
    const KilnSigArg args[] = {arg("x", KILN_SIG_U64)};
    const KilnSig sig = sig_of(args, 1, KILN_SIG_U64);
    {  // two handlers, the first skips
        const auto o1 = ord(10, "t/p1-skip"), o2 = ord(5, "t/p1-second");
        A->detour_typed(6, T(&tg_p1), &o1, &sig, &sm_handler, tag('S'), nullptr);
        A->detour_typed(6, T(&tg_p1), &o2, &sig, &sm_handler, tag('o'), nullptr);
        take_order();
        g_real = 0;
        const uint64_t r = p_p1(5);
        check(r == 4242 && g_real == 0 && take_order() == "S",
              "two handlers, the first skips: the second never runs, the original does not run", std::to_string(r));
        A->revoke_owner(6);
        check(p_p1(5) == 16, "after the revoke the function is itself again");
    }
    {  // fall-through
        const auto o1 = ord(10, "t/p2-fall"), o2 = ord(5, "t/p2-second");
        A->detour_typed(6, T(&tg_p2), &o1, &sig, &sm_handler, tag('F'), nullptr);
        A->detour_typed(6, T(&tg_p2), &o2, &sig, &sm_handler, tag('F'), nullptr);
        take_order();
        g_real = 0;
        const uint64_t r = p_p2(5);
        check(r == (5 + 20) * 3 + 2 && g_real == 1 && take_order() == "FF",
              "falling through runs the rest of the chain and the original once, with the changed arguments",
              std::to_string(r));
        A->revoke_owner(6);
    }
    {  // CallOriginal is once-only
        const auto o1 = ord(10, "t/p3-twice");
        A->detour_typed(6, T(&tg_p3), &o1, &sig, &sm_handler, tag('T'), nullptr);
        g_real = 0;
        const uint64_t r = p_p3(5);
        check(r == 5 * 3 + 3 && g_real == 1, "a second call_original_frame does nothing and keeps the stored result",
              std::to_string(r));
        A->revoke_owner(6);
    }
    {  // a scalar return changed after the original; and "skip" after the original changes nothing
        const auto o1 = ord(10, "t/p4-ret"), o2 = ord(20, "t/p4-skip-late");
        A->detour_typed(6, T(&tg_p4), &o1, &sig, &sm_handler, tag('R'), nullptr);
        A->detour_typed(6, T(&tg_p4), &o2, &sig, &sm_handler, tag('K'), nullptr);
        take_order();
        g_real = 0;
        const uint64_t r = p_p4(5);
        check(r == 5 * 3 + 4 + 1 && g_real == 1 && take_order() == "KR",
              "a return changed after the original reaches the caller through an outer link", std::to_string(r));
        A->revoke_owner(6);
    }
    {  // a fault after CallOriginal
        const size_t before = log_count("faulted");
        const auto o1 = ord(10, "t/p5-late-fault");
        KilnHandle h = 0;
        A->detour_typed(6, T(&tg_p5), &o1, &sig, &sm_handler, tag('L'), &h);
        take_order();
        g_real = 0;
        const uint64_t r = p_p5(5);
        check(r == 5 * 3 + 5 && g_real == 1 && take_order() == "L",
              "a handler that faults after the original: the function ran once, the caller gets its result",
              std::to_string(r) + " real=" + std::to_string(g_real.load()));
        check(log_count("faulted") == before + 1 && log_has("typed link \"t/p5-late-fault\""), "the fault is logged once");
        take_order();
        check(p_p5(6) == 6 * 3 + 5 && take_order().empty(), "the faulted link is disabled afterwards");
        check(A->revoke(h) == KILN_ERR_NOT_FOUND, "its handle is no longer live");
        A->revoke_owner(6);
    }
    {  // a fault before CallOriginal
        const auto o1 = ord(10, "t/p6-early-fault"), o2 = ord(5, "t/p6-second");
        A->detour_typed(6, T(&tg_p6), &o1, &sig, &sm_handler, tag('E'), nullptr);
        A->detour_typed(6, T(&tg_p6), &o2, &sig, &sm_handler, tag('o'), nullptr);
        take_order();
        g_real = 0;
        const uint64_t r = p_p6(5);
        check(r == (5 + 1) * 3 + 6 && g_real == 1 && take_order() == "Eo",
              "a handler that faults before the original: the chain goes on once with the arguments as they are",
              std::to_string(r));
        A->revoke_owner(6);
    }
    {  // misuse
        KilnCallFrame f{};
        check(A->call_original_frame(nullptr) == KILN_ERR_INVALID_ARGUMENT &&
                  A->call_original_frame(&f) == KILN_ERR_INVALID_ARGUMENT,
              "call_original_frame refuses a frame that is not a handler's");
        uint64_t not_state[4] = {};
        f.reserved = reinterpret_cast<uint64_t>(&not_state);
        check(A->call_original_frame(&f) == KILN_ERR_INVALID_ARGUMENT, "...and a frame whose cursor is not the chain's");
    }
}

// ---- malformed descriptors ----
void test_bad_sigs() {
    std::puts("-- malformed descriptors are refused");
    const auto o = ord(0, "t/bad");
    const KilnSigArg one[] = {arg("x", KILN_SIG_U64)};
    const uintptr_t t = T(&tg_p7);
    auto refused = [&](KilnSig s) {
        return A->detour_typed(6, t, &o, &s, &sm_handler, nullptr, nullptr) == KILN_ERR_INVALID_ARGUMENT;
    };
    KilnSig small = sig_of(one, 1, KILN_SIG_U64);
    small.size = sizeof(KilnSig) - 8;
    std::vector<KilnSigArg> many(KILN_SIG_MAX_ARGS + 1, arg("x", KILN_SIG_U64));
    const KilnSigArg bad_kind[] = {arg("x", KILN_SIG_STRUCT)}, void_arg[] = {arg("x", KILN_SIG_VOID)},
                     byval0[] = {arg("x", KILN_SIG_BYVAL, 0)};
    check(A->detour_typed(6, t, &o, nullptr, &sm_handler, nullptr, nullptr) == KILN_ERR_INVALID_ARGUMENT, "no descriptor");
    check(refused(small), "a descriptor smaller than KilnSig");
    check(refused(sig_of(nullptr, 1, KILN_SIG_U64)), "arguments declared but not given");
    check(refused(sig_of(many.data(), KILN_SIG_MAX_ARGS + 1, KILN_SIG_U64)), "more positions than a typed link forwards");
    check(refused(sig_of(many.data(), KILN_SIG_MAX_ARGS, KILN_SIG_STRUCT, 24, 1)), "...the hidden buffer counts");
    check(refused(sig_of(bad_kind, 1, KILN_SIG_U64)) && refused(sig_of(void_arg, 1, KILN_SIG_U64)), "an argument kind that is return-only");
    check(refused(sig_of(byval0, 1, KILN_SIG_U64)), "a by-value struct without a size");
    check(refused(sig_of(one, 1, KILN_SIG_STRUCT + 1)), "an unknown return kind");
    check(refused(sig_of(one, 1, KILN_SIG_U64, 8, 1)), "sret with a scalar return kind");
    check(refused(sig_of(one, 1, KILN_SIG_STRUCT, 0, 1)), "sret without a size");
    check(refused(sig_of(one, 1, KILN_SIG_STRUCT, 12, 0)), "a 12-byte struct returned without sret");
    const KilnSig ok = sig_of(one, 1, KILN_SIG_U64);
    check(A->detour_typed(6, t, &o, &ok, nullptr, nullptr, nullptr) == KILN_ERR_INVALID_ARGUMENT, "no handler");
    check(A->detour_typed(6, 0x1000, &o, &ok, &sm_handler, nullptr, nullptr) == KILN_ERR_INVALID_ARGUMENT,
          "a target outside the image");
    check(chain::chain_of(t).empty() && p_p7(1) == 10, "none of them hooked anything");
    const KilnSig max = sig_of(many.data(), KILN_SIG_MAX_ARGS, KILN_SIG_U64);
    KilnHandle h = 0;
    check(A->detour_typed(6, t, &o, &max, &sm_handler, tag('o'), &h) == KILN_OK, "the largest descriptor is accepted");
    A->revoke_owner(6);
}

// ---- eight stack arguments, next to raw and structured links ----
uint64_t (*g_sum12_orig)(uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t,
                         uint64_t, uint64_t, uint64_t) = nullptr;
uint64_t sum12_raw(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f, uint64_t g, uint64_t h,
                   uint64_t i, uint64_t j, uint64_t k, uint64_t l) {
    ran('r');
    return g_sum12_orig(a, b + 1, c, d, e, f, g, h, i, j, k, l + 1) + 1;  // b: +2, l: +12, +1
}
uintptr_t KILN_CALL sum12_structured(KilnHookCall2* c, void*) {
    ran('s');
    c->args[11] += 10;  // l: +120
    return A->call_original2(c) + 2;
}
uintptr_t KILN_CALL sum12_typed(KilnCallFrame* f, void* user) {
    const char t = static_cast<char>(reinterpret_cast<uintptr_t>(user));
    ran(t);
    if (t == 'A') {
        f->gpr[0] += 1;      // a: +1
        f->stack[7] += 100;  // l: +1200
        A->call_original_frame(f);
        f->rax += 4;
    } else {
        f->stack[0] += 1;    // e: +5
    }
    return KILN_TYPED_CONTINUE;
}

void test_mixed_chain() {
    std::puts("-- a typed link next to a raw and a structured link on one target (12 arguments)");
    std::vector<KilnSigArg> args(12, arg("v", KILN_SIG_U64));
    const KilnSig sig = sig_of(args.data(), 12, KILN_SIG_U64);
    const uintptr_t t = T(&tg_sum12);
    const auto oa = ord(40, "t/sum12-typed-a"), orw = ord(30, "t/sum12-raw"), os = ord(20, "t/sum12-structured"),
               ob = ord(10, "t/sum12-typed-b");
    KilnHandle hs = 0;
    check(A->detour_typed(6, t, &oa, &sig, &sum12_typed, tag('A'), nullptr) == KILN_OK &&
              A->raw_detour(6, t, &orw, reinterpret_cast<void*>(&sum12_raw), reinterpret_cast<void**>(&g_sum12_orig),
                            nullptr) == KILN_OK &&
              A->detour2(6, t, &os, 12, &sum12_structured, nullptr, &hs) == KILN_OK &&
              A->detour_typed(6, t, &ob, &sig, &sum12_typed, tag('B'), nullptr) == KILN_OK,
          "typed, raw, structured and typed links installed on one target");
    take_order();
    g_real = 0;
    const uint64_t base = 1 + 2 * 2 + 3 * 3 + 4 * 4 + 5 * 5 + 6 * 6 + 7 * 7 + 8 * 8 + 9 * 9 + 10 * 10 + 11 * 11 + 12 * 12;
    const uint64_t r = p_sum12(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
    const uint64_t want = base + 1 + 1200 + 4 + 2 + 12 + 1 + 120 + 2 + 5;
    check(r == want && g_real == 1 && take_order() == "ArsB",
          "all twelve arguments cross every kind of link in priority order; each link's changes and returns add up",
          std::to_string(r) + " vs " + std::to_string(want));
    A->revoke(hs);
    take_order();
    const uint64_t r2 = p_sum12(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
    check(r2 == want - 120 - 2 && take_order() == "ArB", "revoking the structured link leaves the others");
    A->revoke_owner(6);
    check(p_sum12(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12) == base, "after the revoke the function is itself again");
}

// ---- narrow integers and a double return through the thunk ----
int64_t g_narrow_seen = 0;
uintptr_t KILN_CALL narrow_handler(KilnCallFrame* f, void*) {
    // mask by type: the upper bytes of every position are the caller's garbage
    const auto a = static_cast<uint8_t>(f->gpr[0]);
    const auto b = static_cast<uint16_t>(f->gpr[1]);
    const auto c = static_cast<int8_t>(f->gpr[2]);
    const bool d = static_cast<uint8_t>(f->gpr[3]) != 0;
    const auto e = static_cast<uint8_t>(f->stack[0]);
    const auto g = static_cast<int16_t>(f->stack[1]);
    g_narrow_seen = static_cast<int64_t>(a) + 3 * b + 5 * c + (d ? 1000 : 0) + 7 * e + 11 * g;
    return KILN_TYPED_CONTINUE;
}
uintptr_t KILN_CALL f64_handler(KilnCallFrame* f, void*) {
    put_f64(f->xmm[0], f64_at(f->xmm[0]) + 1.0);
    A->call_original_frame(f);
    put_f64(f->xmm0r, f64_at(f->xmm0r) * 2.0);
    return KILN_TYPED_CONTINUE;
}

void test_narrow_and_f64() {
    std::puts("-- narrow integers and a double return through the thunk");
    const KilnSigArg nargs[] = {arg("a", KILN_SIG_U8),   arg("b", KILN_SIG_U16), arg("c", KILN_SIG_S8),
                                arg("d", KILN_SIG_BOOL), arg("e", KILN_SIG_U8),  arg("f", KILN_SIG_S16)};
    const KilnSig nsig = sig_of(nargs, 6, KILN_SIG_S64);
    const auto o = ord(0, "t/narrow");
    A->detour_typed(6, T(&tg_narrow), &o, &nsig, &narrow_handler, nullptr, nullptr);
    g_narrow_seen = 0;
    const int64_t r = p_narrow(250, 60000, -100, true, 200, -300);
    const int64_t want = 250 + 3 * 60000 + 5 * -100 + 1000 + 7 * 200 + 11 * -300;
    check(r == want && g_narrow_seen == want, "u8, u16, s8, bool and two narrow stack arguments, masked by type",
          std::to_string(g_narrow_seen) + " vs " + std::to_string(want));
    const KilnSigArg fargs[] = {arg("a", KILN_SIG_F64), arg("b", KILN_SIG_F64)};
    const KilnSig fsig = sig_of(fargs, 2, KILN_SIG_F64);
    const auto of = ord(0, "t/f64");
    A->detour_typed(6, T(&tg_f64), &of, &fsig, &f64_handler, nullptr, nullptr);
    const double rf = p_f64(1.25, 0.5);
    check(rf == ((1.25 + 1.0) * 10.0 + 0.5) * 2.0, "a double argument changed before and the double return changed after",
          std::to_string(rf));
    A->revoke_owner(6);
}

// ---- exceptions and faults of the original ----
uintptr_t KILN_CALL pass_handler(KilnCallFrame* f, void* user) {
    ran(static_cast<char>(reinterpret_cast<uintptr_t>(user)));
    A->call_original_frame(f);
    return KILN_TYPED_CONTINUE;
}

void test_exceptions() {
    std::puts("-- exceptions and faults of the original cross the thunk");
    const KilnSigArg args[] = {arg("x", KILN_SIG_S32)};
    const KilnSig sig = sig_of(args, 1, KILN_SIG_S32);
    {
        const auto o1 = ord(20, "t/throw-1"), o2 = ord(10, "t/throw-2");
        KilnHandle h1 = 0, h2 = 0;
        A->detour_typed(6, T(&tg_throw), &o1, &sig, &pass_handler, tag('1'), &h1);
        A->detour_typed(6, T(&tg_throw), &o2, &sig, &sm_handler, tag('o'), &h2);  // falls through: the other path
        take_order();
        int caught = 0;
        try {
            p_throw(1);
        } catch (int v) {
            caught = v;
        }
        check(caught == 42 && take_order() == "1o",
              "a C++ exception thrown by the original unwinds through kiln_hk_call_frame and kiln_hk_typed (both paths)");
        check(p_throw(0) == 7, "the chain still works afterwards");
        check(A->revoke(h1) == KILN_OK && A->revoke(h2) == KILN_OK, "both links were still live (the exception was not theirs)");
    }
    {
        const auto o = ord(0, "t/av");
        KilnHandle h = 0;
        A->detour_typed(6, T(&tg_av), &o, &sig, &pass_handler, tag('a'), &h);
        const size_t before = log_count("faulted");
        take_order();
        DWORD code = 0;
        const int r = call_av(1, &code);
        check(r == -1 && code == EXCEPTION_ACCESS_VIOLATION && take_order() == "a",
              "an access violation inside the original reaches the caller's handler");
        check(log_count("faulted") == before && call_av(0, &code) == 7 && take_order() == "a",
              "...and the typed link is still active (the fault was not blamed on it)");
        A->revoke_owner(6);
    }
}

// ---- the caller's home space ----
uintptr_t KILN_CALL home_handler(KilnCallFrame* f, void* user) {
    if (user) A->call_original_frame(f);
    return KILN_TYPED_CONTINUE;
}

void test_home_space() {
    std::puts("-- the caller's home space is never written");
    const KilnSigArg args[] = {arg("x", KILN_SIG_S32)};
    const KilnSig sig = sig_of(args, 1, KILN_SIG_S32);
    check(kiln_home_call(reinterpret_cast<void*>(&tg_asm_home), 41) == 42, "unhooked: the checker itself is sound");
    const auto o1 = ord(10, "t/home-call"), o2 = ord(5, "t/home-fall");
    A->detour_typed(6, T(&tg_asm_home), &o1, &sig, &home_handler, tag('c'), nullptr);
    A->detour_typed(6, T(&tg_asm_home), &o2, &sig, &home_handler, nullptr, nullptr);
    const uint64_t r = kiln_home_call(reinterpret_cast<void*>(&tg_asm_home), 41);
    check(r == 42, "two typed links (one calls the original, one falls through): the four home slots are untouched",
          std::to_string(r));
    A->revoke_owner(6);
}

// ---- threads ----
std::atomic<uint64_t> g_hot_handler{0}, g_hot_churn{0}, g_hot_bad{0};
uintptr_t KILN_CALL hot_handler(KilnCallFrame* f, void*) {
    g_hot_handler.fetch_add(1, std::memory_order_relaxed);
    f->gpr[0] += 1;
    put_f64(f->xmm[1], f64_at(f->xmm[1]) + 1.0);
    A->call_original_frame(f);
    f->rax += 5;
    return KILN_TYPED_CONTINUE;
}
uintptr_t KILN_CALL churn_handler(KilnCallFrame*, void*) {
    g_hot_churn.fetch_add(1, std::memory_order_relaxed);
    return KILN_TYPED_CONTINUE;  // falls through
}

void test_threads() {
    std::puts("-- threads: 8 callers, a ninth installs and revokes links");
    const KilnSigArg args[] = {arg("x", KILN_SIG_U64), arg("y", KILN_SIG_F64)};
    const KilnSig sig = sig_of(args, 2, KILN_SIG_U64);
    const uintptr_t t = T(&tg_hot);
    const auto o = ord(10, "t/hot"), oc = ord(20, "t/hot-churn"), oc2 = ord(0, "t/hot-churn-back");
    A->detour_typed(6, t, &o, &sig, &hot_handler, nullptr, nullptr);
    constexpr uint64_t kCalls = 1000000;
    constexpr int kThreads = 8;
    g_real = 0;
    g_hot_handler = 0, g_hot_churn = 0, g_hot_bad = 0;
    const size_t faults_before = log_count("faulted");
    std::atomic<bool> go{false}, stop{false};
    std::atomic<uint64_t> churns{0};
    std::vector<std::thread> callers;
    for (int i = 0; i < kThreads; ++i)
        callers.emplace_back([&, i] {
            while (!go.load()) std::this_thread::yield();
            uint64_t bad = 0;
            for (uint64_t n = 0; n < kCalls; ++n) {
                const uint64_t x = n + static_cast<uint64_t>(i) * 7;
                const double y = static_cast<double>(n & 1023);
                // hot_handler: x + 1, y + 1, return + 5; the churn links change nothing
                const uint64_t want = (x + 1) * 3 + 1 + static_cast<uint64_t>(y + 1.0) + 5;
                if (p_hot(x, y) != want) ++bad;
            }
            g_hot_bad.fetch_add(bad);
        });
    std::thread churner([&] {
        while (!go.load()) std::this_thread::yield();
        while (!stop.load()) {
            KilnHandle h1 = 0, h2 = 0;
            if (A->detour_typed(7, t, &oc, &sig, &churn_handler, nullptr, &h1) != KILN_OK) g_hot_bad.fetch_add(1);
            if (A->detour_typed(7, t, &oc2, &sig, &churn_handler, nullptr, &h2) != KILN_OK) g_hot_bad.fetch_add(1);
            std::this_thread::yield();
            if (churns.load() & 1) {
                A->revoke(h1);
                A->revoke(h2);
            } else {
                A->revoke_owner(7);
            }
            churns.fetch_add(1);
        }
    });
    const size_t slots_before = chain::link_count();
    go = true;
    for (auto& c : callers) c.join();
    stop = true;
    churner.join();
    const uint64_t total = kCalls * kThreads;
    check(g_hot_bad == 0, "every call returned the right value", std::to_string(g_hot_bad.load()) + " bad");
    check(g_real == total && g_hot_handler == total, "counts equal: the handler and the original ran once per call",
          std::to_string(g_real.load()) + " / " + std::to_string(g_hot_handler.load()) + " of " + std::to_string(total));
    check(churns > 100 && g_hot_churn > 0, "links were installed and revoked while the callers ran",
          std::to_string(churns.load()) + " rounds, " + std::to_string(g_hot_churn.load()) + " churn-handler calls");
    check(log_count("faulted") == faults_before, "no fault");
    check(chain::link_count() <= slots_before + 2, "the churn revived its two slots (no slot leak)",
          std::to_string(chain::link_count() - slots_before));
    A->revoke_owner(6);
    A->revoke_owner(7);
    check(p_hot(1, 0.0) == 4, "after the revoke the function is itself again");
}

}  // namespace

int main(int, char**) {
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
    g_state_dir = std::filesystem::path(tmp) / (L"kiln-hooks-typed-test-" + std::to_wstring(GetCurrentProcessId()));
    std::printf("hooks_typed_test: game section %p +0x%zx\n", reinterpret_cast<void*>(kiln::g_image.base), kiln::g_image.size);
    if (!g_service || !g_service->init || !g_service->init()) {
        std::puts("FAIL kiln.hooks service did not initialise");
        return 1;
    }
    A = static_cast<const KilnHooksApi*>(g_service->table);
    check(A->header.size == sizeof(KilnHooksApi) && A->detour_typed && A->call_original_frame && A->call_with_frame &&
              A->fingerprint,
          "the service table carries the four typed entries", "size " + std::to_string(A->header.size));

    test_call_with_frame();
    test_mixed_through_thunk();
    test_sret();
    test_state_machine();
    test_bad_sigs();
    test_mixed_chain();
    test_narrow_and_f64();
    test_exceptions();
    test_home_space();
    test_threads();

    {
        std::lock_guard lock(g_log_mutex);
        size_t errors = 0;
        for (const auto& l : g_lines)
            if (l[0] == 'E' && l.find("faulted") == std::string::npos) ++errors, std::printf("  unexpected log: %s\n", l.c_str());
        if (errors) ++g_fail;
    }
    std::printf("%d passed, %d failed\n", g_pass, g_fail);
    std::puts(g_fail ? "FAILED" : "all passed");
    std::error_code ec;
    std::filesystem::remove_all(g_state_dir, ec);
    return g_fail ? 1 : 0;
}
