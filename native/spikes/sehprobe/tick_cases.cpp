// SPDX-License-Identifier: MIT
// Exercise the production detours; only core services and the original game calls are substituted.
#include "../../core/src/tick.cpp"

namespace {
unsigned originals, posts, disabled_logs;
uintptr_t worlds[KILN_WORLD_KINDS]{};
bool want_post;
__declspec(noinline) uintptr_t original(uintptr_t, uintptr_t, uintptr_t, uintptr_t) {
    ++originals;
    return 123;
}
__declspec(noinline) void original_destroy(uintptr_t) { ++originals; GetTickCount64(); }
void KILN_CALL post(const KilnTickInfo*, void*) { ++posts; }
void __cdecl bad_wait(uintptr_t) { RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr); }
}

namespace kiln {
double now_ms() { return static_cast<double>(GetTickCount64()); }
const ImageInfo& image() { static ImageInfo i; return i; }
const Paths& paths() { static Paths p; return p; }
namespace log {
void writef(int, const char*, const char* fmt, ...) {
    if (strstr(fmt, "disabled")) ++disabled_logs;
}
}
namespace sigdb {
bool feature(std::string_view) { return false; }
bool symbol(std::string_view, uintptr_t*) { return false; }
bool constant(std::string_view, int64_t*) { return false; }
}
namespace worldinfo {
uintptr_t world(uint32_t k) { return k < KILN_WORLD_KINDS ? worlds[k] : 0; }
void set_world(uint32_t k, uintptr_t w) { worlds[k] = w; }
}
namespace reg {
uint64_t version() { return 1; }
std::string owner_name(KilnOwner) { return "test"; }
bool owner_alive(KilnOwner) { return true; }
size_t drain_posts() { return 0; }
KilnResult remove(KilnHandle) { return KILN_OK; }
KilnResult add_tick(KilnOwner, int, KilnTickFn, void*, KilnHandle*) { return KILN_OK; }
KilnResult post(KilnOwner, KilnTaskFn, void*) { return KILN_OK; }
std::vector<std::shared_ptr<TickSub>> tick_subs(int phase) {
    if (!want_post || phase != KILN_TICK_POST) return {};
    auto s = std::make_shared<TickSub>();
    s->fn = &::post;
    return {s};
}
}
namespace guard {
bool task(KilnTaskFn f, void* u, unsigned long*) { f(u); return true; }
bool tick(KilnTickFn f, const KilnTickInfo* i, void* u, unsigned long*) { f(i, u); return true; }
bool copy(void* d, uintptr_t s, size_t n) { memcpy(d, reinterpret_cast<void*>(s), n); return true; }
}
}

int tick_fault_case(unsigned kind, bool post_fault) {
    using namespace kiln::tick;
    auto* mem = static_cast<unsigned char*>(VirtualAlloc(nullptr, 8192, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!mem) return 0;
    DWORD old;
    if (!VirtualProtect(mem + 4096, 4096, PAGE_NOACCESS, &old)) return 0;
    mem[0] = static_cast<unsigned char>(kind);
    g_installed = true;
    g_off_kind = 0;
    g_off_clock = post_fault ? 4 : 4096;
    want_post = true;
    g_wait_jobs = post_fault ? &bad_wait : [](uintptr_t) {};
    if (kind != KILN_WORLD_SERVER) {
        auto s = std::make_shared<Sub>();
        s->kind = kind;
        s->phase = KILN_TICK_POST;
        s->tick = post_fault ? [](const KilnTickInfo*, void*) { bad_wait(0); } : &::post;
        g_subs.push_back(s);
    }
    // the handler is called directly: the link only supplies its continuation (the fake original)
    g_hook.original = reinterpret_cast<void*>(&original);
    g_hook.handle = 1;
    const uintptr_t storage = reinterpret_cast<uintptr_t>(mem);
    const bool first = hk_sim_step(storage, 1, 0, 0) == 123;
    const bool second = hk_sim_step(storage, 1, 0, 0) == 123;
    const bool ok = first && second && originals == 2 && posts == 0 && disabled_logs == 1 && !installed() &&
                    worlds[kind] == 0;
    g_hook = {};
    VirtualFree(mem, 0, MEM_RELEASE);
    return ok;
}

int tick_destroy_fault_case() {
    using namespace kiln::tick;
    unsigned char kind = 0;
    const uintptr_t world = reinterpret_cast<uintptr_t>(&kind);
    worlds[0] = world;
    g_installed = true;
    g_wait_jobs = &bad_wait;
    g_destroy_hook.original = reinterpret_cast<void*>(&original_destroy);
    g_destroy_hook.handle = 1;
    hk_world_destroy(world);
    hk_world_destroy(world);
    const bool ok = originals == 2 && disabled_logs == 1 && worlds[0] == 0 && !installed();
    g_destroy_hook = {};
    return ok;
}
