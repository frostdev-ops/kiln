// SPDX-License-Identifier: MIT
// kiln.types: the game's type registry, ECS systems and ECS event streams (native/core/abi/services/kiln_types.h).
//  - Types: scan.h finds the static type table in the loaded image (no code pattern). Lookups by hash and name are
//    built once at boot.
//  - Systems: scan.h lists the system descriptors; one detour on the program runner (sigdb types/ProgramRunner) skips
//    switched-off systems and times every slice while the profiler runs.
//  - Events: the world's streams and the schema's event map, at offsets captured by sigdb (@types/EventStreams).
//    Reads walk the stream pages, posts append to the world's first poster, as the game's own code does. The event
//    hash is checked against the world's map first: an unknown hash crashes the game's distributor.
// Technique credit: Shrouded Engine Tweaks (MIT, Cacaida); docs/re/shrouded-engine-tweaks.md.
#include <windows.h>

#include <atomic>
#include <array>
#include <utility>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>

#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "scan.h"
#include "runtime.h"
#include "system_hooks.h"
#include "services/kiln_types.h"
#include "../../plugins/set/src/coexist.h"

namespace {

using namespace ktypes;

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
Image g_im;
std::vector<uint8_t> g_image_bytes;
std::vector<Range> g_image_readable;
TypeTable g_tt;
std::unordered_map<uint32_t, uint64_t> g_by_hash;
std::unordered_map<std::string_view, uint64_t> g_by_qname;
std::unordered_map<std::string_view, uint64_t> g_by_short;  // unique short names; 0 = ambiguous
std::unordered_map<std::string_view, uint64_t> g_by_short_nods;
std::unordered_map<uint64_t, uint32_t> g_index_of;
std::string g_detail;

// Systems
std::vector<System> g_sys;
std::vector<std::pair<uint64_t, uint32_t>> g_sys_by_fn;  // update fn VA -> index
std::unique_ptr<std::atomic<bool>[]> g_off;
std::unique_ptr<std::atomic<uint64_t>[]> g_calls, g_ticks;
std::unique_ptr<std::atomic<uint8_t>[]> g_kinds;
std::atomic<uint32_t> g_off_count{0};
std::atomic<bool> g_prof{false};
int64_t g_qpf = 1;
std::mutex g_sys_mu;
ProfileSession g_profile;
SystemOwners g_system_owners;
kiln::RawHook g_runner;
std::atomic<bool> g_systems_ok{false};
// When native SET may have appeared since the startup check: after a DLL load or a module-list change (3 s
// fingerprint, no wineserver call), never per tick (the check reaches wineserver once per loaded module under Wine).
kset::ModuleWatch g_modules;
bool g_external_runner = false;

// Requested update-function hooks, separate from the slice runner so callbacks get its actual ProgramContext.
constexpr size_t kMaxSystemHooks = 64;
struct SystemHook {
    uint64_t fn = 0;
    SystemHookCallbacks callbacks;
    kiln::RawHook detour;
};
std::array<std::atomic<std::shared_ptr<SystemHook>>, kMaxSystemHooks> g_hooks;
std::mutex g_hooks_mu;
KilnHandle g_next_hook = 1;
bool g_retained_updates = false;
std::atomic<bool> g_have_step{false};
thread_local bool t_system_callback = false;

// Events
int64_t g_ev_map = 0, g_ev_streams = 0, g_storage = 0;  // world.storage: Kiln's world pointer -> the ECS storage
bool g_events_ok = false;
constexpr uint64_t kStreamStride = 0x48, kStreamCount = 8, kPosters = 0x70;

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    g_api->log(g_owner, level, KilnStr{buf, n < 0 ? 0 : std::min<size_t>(static_cast<size_t>(n), sizeof buf - 1)});
}
KilnStr ks(std::string_view s) { return KilnStr{s.data(), s.size()}; }
std::string_view sv(KilnStr s) { return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view(); }

bool process_range(uint64_t address, uint64_t size, bool writing = false) {
    if (!address || size > UINTPTR_MAX - address) return false;
    const uint64_t end = address + size;
    while (address < end) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<const void*>(address), &mbi, sizeof mbi) || mbi.State != MEM_COMMIT ||
            (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
        const DWORD p = mbi.Protect & 0xff;
        const bool readable = p == PAGE_READONLY || p == PAGE_READWRITE || p == PAGE_WRITECOPY ||
                              p == PAGE_EXECUTE_READ || p == PAGE_EXECUTE_READWRITE || p == PAGE_EXECUTE_WRITECOPY;
        const bool writable = p == PAGE_READWRITE || p == PAGE_WRITECOPY || p == PAGE_EXECUTE_READWRITE || p == PAGE_EXECUTE_WRITECOPY;
        if (!readable || (writing && !writable)) return false;
        const uint64_t begin = reinterpret_cast<uint64_t>(mbi.BaseAddress);
        if (mbi.RegionSize > UINT64_MAX - begin || begin + mbi.RegionSize <= address) return false;
        address = std::min(end, begin + mbi.RegionSize);
    }
    return true;
}
struct ProcessMemory {
    bool read(uint64_t address, void* out, size_t size) const {
        if (!out || !process_range(address, size)) return false;
        SIZE_T got = 0;
        return ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<const void*>(address), out, size, &got) && got == size;
    }
    bool writable(uint64_t address, uint64_t size) const { return process_range(address, size, true); }
    bool write(uint64_t address, const void* data, size_t size) const {
        if (!writable(address, size)) return false;
        SIZE_T written = 0;
        return WriteProcessMemory(GetCurrentProcess(), reinterpret_cast<void*>(address), data, size, &written) && written == size;
    }
};
const ProcessMemory g_memory;
bool snapshot_readable(uint64_t address, uint64_t size) {
    auto it = std::upper_bound(g_image_readable.begin(), g_image_readable.end(), address,
                              [](uint64_t a, const Range& r) { return a < r.lo; });
    return it != g_image_readable.begin() && std::prev(it)->has(address, size);
}

bool image_of_process(Image& im) {
    const auto base = reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr));
    IMAGE_DOS_HEADER dos{};
    IMAGE_NT_HEADERS64 nt{};
    uint64_t nt_at, sections;
    if (!g_memory.read(base, &dos, sizeof dos) || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < sizeof dos ||
        !add_address(base, static_cast<uint64_t>(dos.e_lfanew), nt_at) || !g_memory.read(nt_at, &nt, sizeof nt) ||
        nt.Signature != IMAGE_NT_SIGNATURE || nt.OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
        nt.FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 || nt.FileHeader.SizeOfOptionalHeader < sizeof(IMAGE_OPTIONAL_HEADER64) ||
        !nt.FileHeader.NumberOfSections || nt.FileHeader.NumberOfSections > 96 ||
        nt.OptionalHeader.SizeOfImage < 0x1000 || nt.OptionalHeader.SizeOfImage > 1024 * 1024 * 1024 ||
        !add_address(nt_at, 24ull + nt.FileHeader.SizeOfOptionalHeader, sections)) return false;
    im.base = base;
    im.all = {base, base + nt.OptionalHeader.SizeOfImage};
    if (im.all.hi < base || !im.all.has(nt_at, sizeof nt) ||
        !im.all.has(sections, uint64_t(nt.FileHeader.NumberOfSections) * sizeof(IMAGE_SECTION_HEADER))) return false;
    // Copy committed readable regions once. All later metadata reads use this immutable, bounded snapshot; a
    // missing/guard page cannot fault the scanner or leave a borrowed pointer into mutable executable memory.
    g_image_bytes.assign(nt.OptionalHeader.SizeOfImage, 0);
    for (uint64_t at = base; at < im.all.hi;) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<const void*>(at), &mbi, sizeof mbi)) return false;
        const uint64_t mb = reinterpret_cast<uint64_t>(mbi.BaseAddress);
        if (mbi.RegionSize > UINT64_MAX - mb || mb + mbi.RegionSize <= at) return false;
        const uint64_t end = std::min(im.all.hi, mb + mbi.RegionSize);
        if (process_range(at, end - at) && g_memory.read(at, g_image_bytes.data() + (at - base), end - at)) {
            if (!g_image_readable.empty() && g_image_readable.back().hi == at) g_image_readable.back().hi = end;
            else g_image_readable.push_back({at, end});
        }
        at = end;
    }
    im.mem = g_image_bytes.data();
    im.readable = &snapshot_readable;
    for (unsigned i = 0; i < nt.FileHeader.NumberOfSections; ++i) {
        IMAGE_SECTION_HEADER sec{};
        const uint64_t address = sections + uint64_t(i) * sizeof sec;
        if (!im.has(address, sizeof sec)) return false;
        std::memcpy(&sec, im.at(address), sizeof sec);
        const uint64_t length = sec.Misc.VirtualSize;
        if (!length || sec.VirtualAddress > nt.OptionalHeader.SizeOfImage ||
            length > nt.OptionalHeader.SizeOfImage - sec.VirtualAddress) return false;
        const Range r{base + sec.VirtualAddress, base + sec.VirtualAddress + length};
        if (!std::memcmp(sec.Name, ".text\0\0\0", 8)) im.text = r;
        if (!std::memcmp(sec.Name, ".rdata\0\0", 8)) im.rdata = r;
    }
    return im.valid();
}

std::string_view tname(uint64_t t) { return str_at(g_im, t + td::name); }
std::string_view tqname(uint64_t t) { return str_at(g_im, t + td::qname); }
const KilnType* handle(uint64_t t) { return reinterpret_cast<const KilnType*>(t); }
uint64_t raw(const KilnType* t) { return reinterpret_cast<uint64_t>(t); }
bool known(const KilnType* t) { return t && g_index_of.count(raw(t)); }

void index_types() {
    for (uint32_t i = 0; i < g_tt.count; ++i) {
        const uint64_t t = g_im.q(g_tt.table + 8ull * i);
        g_index_of[t] = i;
        unique_insert(g_by_hash, g_im.d(t + td::hash), t);
        const std::string_view qn = tqname(t), n = tname(t);
        g_by_qname.emplace(qn, t);
        auto add = [&](std::unordered_map<std::string_view, uint64_t>& m) {
            unique_insert(m, n, t);
        };
        add(g_by_short);
        if (qn.rfind("keen::ds::", 0) != 0) add(g_by_short_nods);
    }
}

// ---- types ----
KilnResult KILN_CALL status(KilnTypesStatus* out) {
    if (!out || out->size < sizeof(KilnTypesStatus)) return KILN_ERR_INVALID_ARGUMENT;
    out->types = g_tt.error.empty() && g_tt.count != 0;
    out->systems = g_systems_ok;
    out->events = g_events_ok;

    out->type_count = g_tt.count;
    out->component_count = g_tt.component_count;
    out->event_type_count = g_tt.event_count;
    out->system_count = static_cast<uint32_t>(g_sys.size());
    out->systems_off = g_off_count.load();
    std::lock_guard lock(g_sys_mu);
    LARGE_INTEGER now{};
    if (g_profile.active) QueryPerformanceCounter(&now);
    out->profiling = g_profile.active;
    out->profile_seconds = g_profile.seconds(now.QuadPart, g_qpf);
    out->detail = g_external_runner ? ks("native SET/power proxy detected; system runner passive until exit") : ks(g_detail);
    return KILN_OK;
}
uint32_t KILN_CALL type_count() { return g_tt.error.empty() ? g_tt.count : 0; }
const KilnType* KILN_CALL type_at(uint32_t i) { return i < type_count() ? handle(g_im.q(g_tt.table + 8ull * i)) : nullptr; }
const KilnType* KILN_CALL type_by_hash(uint32_t h) {
    auto it = g_by_hash.find(h);
    return it == g_by_hash.end() ? nullptr : handle(it->second);
}
const KilnType* KILN_CALL type_by_name(KilnStr name) {
    const std::string_view n = sv(name);
    if (n.empty()) return nullptr;
    if (n.find("::") != std::string_view::npos) {
        auto it = g_by_qname.find(n);
        return it == g_by_qname.end() ? nullptr : handle(it->second);
    }
    if (auto it = g_by_qname.find(std::string("keen::ecs::") + std::string(n)); it != g_by_qname.end()) return handle(it->second);
    for (auto* m : {&g_by_short_nods, &g_by_short}) {
        auto it = m->find(n);
        if (it != m->end()) return it->second ? handle(it->second) : nullptr;
    }
    return nullptr;
}
KilnResult KILN_CALL type_info(const KilnType* type, KilnTypeInfo* out) {
    if (!out || out->size < sizeof(KilnTypeInfo)) return KILN_ERR_INVALID_ARGUMENT;
    if (!known(type)) return KILN_ERR_NOT_FOUND;
    const uint64_t t = raw(type);
    out->index = g_index_of[t];
    out->name = ks(tname(t));
    out->qualified_name = ks(tqname(t));
    out->type_size = g_im.d(t + td::size);
    if (out->type_size > 16 * 1024 * 1024 || g_im.d(t + td::count) > 65536 ||
        !scalar_layout(g_im.b(t + td::kind), out->type_size)) return KILN_ERR_NOT_FOUND;
    out->alignment = g_im.w(t + td::align);
    out->kind = g_im.b(t + td::kind);
    out->flags = g_im.b(t + td::flags);
    out->hash = g_im.d(t + td::hash);
    out->field_count = g_im.d(t + td::count);
    const uint64_t inner = g_im.q(t + td::inner);
    out->inner = g_index_of.count(inner) ? handle(inner) : nullptr;
    const uint64_t dp = g_im.q(t + td::defaults), dn = g_im.q(t + td::defaults + 8);
    out->defaults = dp && dn == out->type_size && g_im.has(dp, dn) ? g_im.at(dp) : nullptr;
    return KILN_OK;
}
KilnResult KILN_CALL type_field(const KilnType* type, uint32_t index, KilnTypeField* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!known(type)) return KILN_ERR_NOT_FOUND;
    const uint64_t e = field_entry(g_im, raw(type), index);
    if (!e) return KILN_ERR_NOT_FOUND;
    out->name = ks(str_at(g_im, e));
    const uint64_t ft = g_im.q(e + td::field_type);
    out->type = g_index_of.count(ft) ? handle(ft) : nullptr;
    out->offset = g_im.q(e + td::field_offset);
    return out->type ? KILN_OK : KILN_ERR_NOT_FOUND;
}
KilnResult KILN_CALL type_enum(const KilnType* type, uint32_t index, KilnStr* name, int64_t* value) {
    if (!known(type)) return KILN_ERR_NOT_FOUND;
    const uint64_t e = enum_entry(g_im, raw(type), index);
    if (!e) return KILN_ERR_NOT_FOUND;
    if (name) *name = ks(str_at(g_im, e));
    if (value) *value = static_cast<int64_t>(g_im.q(e + td::enum_value));
    return KILN_OK;
}
uint32_t KILN_CALL component_type_count() { return g_tt.component_count; }
const KilnType* KILN_CALL component_type_at(uint32_t i) {
    return i < g_tt.component_count ? handle(g_im.q(g_tt.components + 8ull * i)) : nullptr;
}
uint32_t KILN_CALL event_type_count() { return g_tt.event_count; }
const KilnType* KILN_CALL event_type_at(uint32_t i) {
    return i < g_tt.event_count ? handle(g_im.q(g_tt.events + 8ull * i)) : nullptr;
}

// ---- systems ----
void publish_system_switches();
void KILN_CALL noop(void*);
// The core API has no direct owner-liveness query (a post to a revoked owner fails, and a post to a live one queues a
// task), so revoked owners are probed from the tick thread at most once per second (on_pre), never per job or per tick.
bool reap_due() {
    static std::chrono::steady_clock::time_point next{};
    const auto now = std::chrono::steady_clock::now();
    if (now < next) return false;
    next = now + std::chrono::seconds(1);
    return true;
}
bool owner_revoked(KilnOwner owner) { return g_api->post(owner, &noop, nullptr) == KILN_ERR_REVOKED; }
int64_t sys_index_of_job(const void* job) {
    uint64_t prog, fn;
    if (!read_at(g_memory, reinterpret_cast<uint64_t>(job), 0x10, prog) || !prog ||
        !read_at(g_memory, prog, 0x28, fn)) return -1;
    auto it = std::lower_bound(g_sys_by_fn.begin(), g_sys_by_fn.end(), std::pair<uint64_t, uint32_t>{fn, 0});
    return it != g_sys_by_fn.end() && it->first == fn ? int64_t(it->second) : -1;
}

void runner_detour(uintptr_t world, void* job, uint64_t index, uint64_t slot) {
    if (g_off_count.load(std::memory_order_acquire)) {
        const int64_t i = sys_index_of_job(job);
        // g_off is republished whenever an owner's request changes or a revoked owner is reaped (on_pre)
        if (i >= 0 && g_off[size_t(i)].load(std::memory_order_relaxed)) return;
    }
    if (!g_prof.load(std::memory_order_acquire)) return g_runner.call<void>(world, job, index, slot);
    uint64_t generation;
    { std::lock_guard lock(g_sys_mu); generation = g_profile.generation; }
    LARGE_INTEGER a{}, b{};
    QueryPerformanceCounter(&a);
    g_runner.call<void>(world, job, index, slot);
    QueryPerformanceCounter(&b);
    const int64_t i = sys_index_of_job(job);
    if (i < 0) return;
    uint8_t kind = 255;
    read_at(g_memory, world, 8, kind);
    std::lock_guard lock(g_sys_mu);
    // A reset/start during this call must not charge an old slice to a new profile session.
    if (!g_profile.accepts(generation)) return;
    g_calls[size_t(i)].fetch_add(1, std::memory_order_relaxed);
    g_ticks[size_t(i)].fetch_add(uint64_t(std::max<int64_t>(0, b.QuadPart - a.QuadPart)), std::memory_order_relaxed);
    if (kind < 8) g_kinds[size_t(i)].fetch_or(uint8_t(1u << kind), std::memory_order_relaxed);
}

// Only the callback frame holds SEH: no C++ destructors can be skipped by a structured exception.
bool guarded_system_hook(KilnSystemHookFn fn, void* ctx, void* user) {
    const bool previous = t_system_callback;
    t_system_callback = true;
    __try { fn(ctx, user); t_system_callback = previous; return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { t_system_callback = previous; return false; }
}
void call_system_hook(size_t index, void* ctx) {
    auto hook = g_hooks[index].load(std::memory_order_acquire);
    if (!hook) return; // published before enable, never reclaimed while the process runs
    run_system_update(hook->callbacks,ctx,[&](void* context) {
        // The continuation is immutable and retained: keep parallel worlds/slices parallel.
        hook->detour.original<void (*)(void*)>()(context);
    },[](KilnSystemHookFn callback,void* context,void* user,KilnHandle handle) {
        if (guarded_system_hook(callback,context,user)) return true;
        logf(KILN_LOG_ERROR,"system hook %llu: callback faulted; disabled",static_cast<unsigned long long>(handle));
        return false;
    });
}
template<size_t I> void system_hook_trampoline(void* ctx) { call_system_hook(I, ctx); }
template<size_t... I> constexpr auto system_hook_trampolines(std::index_sequence<I...>) {
    return std::array<void (*)(void*), sizeof...(I)>{&system_hook_trampoline<I>...};
}
constexpr auto kSystemHookTrampolines = system_hook_trampolines(std::make_index_sequence<kMaxSystemHooks>{});
bool may_change_system_hooks() { return !t_system_callback && (!g_have_step.load() || g_api->on_tick_thread()); }
KilnResult KILN_CALL system_hook(KilnOwner owner, uint32_t index, KilnSystemHookFn before,
                                KilnSystemHookFn after, void* user, KilnHandle* out) {
    if (out) *out = 0;
    if (!g_systems_ok) return KILN_ERR_UNAVAILABLE;
    if (!may_change_system_hooks()) return KILN_ERR_WRONG_THREAD;
    if (!owner || (!before && !after) || !out) return KILN_ERR_INVALID_ARGUMENT;
    if (index >= g_sys.size()) return KILN_ERR_NOT_FOUND;
    if (g_api->post(owner, &noop, nullptr) == KILN_ERR_REVOKED) return KILN_ERR_REVOKED;
    std::lock_guard lock(g_hooks_mu);
    const int chosen = system_hook_slot<kMaxSystemHooks>(g_sys[index].fn,[](size_t i) {
        const auto hook=g_hooks[i].load();
        return SystemHookSlot{hook?hook->fn:0,hook && hook->callbacks.identity().handle!=0};
    });
    if (chosen<0) return chosen==-2?KILN_ERR_INVALID_ARGUMENT:KILN_ERR_UNAVAILABLE;
    const size_t slot=static_cast<size_t>(chosen);
    auto hook=g_hooks[slot].load();
    if (!hook) {
        hook=std::make_shared<SystemHook>();
        hook->fn=g_sys[index].fn;
        HMODULE pin=nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN|GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                reinterpret_cast<LPCWSTR>(&call_system_hook),&pin)) return KILN_ERR_UNAVAILABLE;
        // Publish the slot before its link can be reached: a world's thread entering the trampoline finds the hook,
        // and install() writes the continuation before the link is live.
        g_hooks[slot].store(hook,std::memory_order_release);
        if (!hook->detour.install(g_api,g_owner,static_cast<uintptr_t>(hook->fn),
                reinterpret_cast<void*>(kSystemHookTrampolines[slot]),"types/system-update")) {
            g_hooks[slot].store({});
            return KILN_ERR_UNAVAILABLE;
        }
        g_retained_updates=true;
    }
    const KilnHandle handle=g_next_hook++;
    if (!hook->callbacks.bind(owner,handle,before,after,user)) return KILN_ERR_INVALID_ARGUMENT;
    *out=handle;
    return KILN_OK;
}
KilnResult KILN_CALL system_unhook(KilnHandle handle) {
    if (t_system_callback) return KILN_ERR_WRONG_THREAD;
    std::lock_guard registry_lock(g_hooks_mu);
    for (auto& slot:g_hooks) {
        auto hook=slot.load();
        if (hook && hook->callbacks.retire(handle)) return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}
void release_system_hooks(KilnOwner owner, bool all = false) {
    std::vector<KilnHandle> handles;
    { std::lock_guard lock(g_hooks_mu);
      for (auto& slot:g_hooks) { auto h=slot.load(); if (!h) continue; const auto id=h->callbacks.identity();
          if (id.handle && (all || id.owner==owner)) handles.push_back(id.handle); } }
    for (auto handle : handles) system_unhook(handle);
}
void reap_system_hooks() {  // tick thread, when reap_due(): no allocation
    std::array<KilnHandle, kMaxSystemHooks> handles{};
    size_t n = 0;
    { std::lock_guard lock(g_hooks_mu);
      for (auto& slot : g_hooks) { auto h = slot.load();
        if (h) { const auto id=h->callbacks.identity();
          if (id.handle && owner_revoked(id.owner)) handles[n++] = id.handle; } } }
    for (size_t i = 0; i < n; ++i) system_unhook(handles[i]);
}

uint32_t KILN_CALL system_count() { return static_cast<uint32_t>(g_sys.size()); }
KilnResult KILN_CALL system_info(uint32_t i, KilnSystemInfo* out) {
    if (!out || out->size < sizeof(KilnSystemInfo)) return KILN_ERR_INVALID_ARGUMENT;
    if (i >= g_sys.size()) return KILN_ERR_NOT_FOUND;
    std::lock_guard lock(g_sys_mu);
    out->index = i;
    out->name = ks(g_sys[i].name);
    out->rva = g_sys[i].fn - g_im.base;
    out->enabled = !g_off[i].load();
    out->world_kinds = g_kinds[i].load();
    out->calls = g_calls[i].load();
    out->ms = double(g_ticks[i].load()) * 1000.0 / double(g_qpf);
    return KILN_OK;
}
int32_t KILN_CALL system_find(KilnStr name) {
    const std::string_view n = sv(name);
    auto it = std::lower_bound(g_sys.begin(), g_sys.end(), n, [](const System& s, std::string_view x) { return s.name < x; });
    if (it == g_sys.end() || it->name != n || (std::next(it) != g_sys.end() && std::next(it)->name == n)) return -1;
    return int32_t(it - g_sys.begin());
}
void publish_system_switches() { // g_sys_mu held
    uint32_t count = 0;
    for (size_t i = 0; i < g_sys.size(); ++i) {
        const bool off = g_system_owners.disabled.count(static_cast<uint32_t>(i)) != 0;
        g_off[i].store(off, std::memory_order_relaxed);
        count += off;
    }
    g_off_count.store(count, std::memory_order_release);
}
void KILN_CALL noop(void*) {}
KilnResult KILN_CALL system_set_enabled(KilnOwner owner, uint32_t i, bool enabled) {
    if (!g_systems_ok) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (i >= g_sys.size()) return KILN_ERR_NOT_FOUND;
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    if (!enabled && !can_disable(g_sys, i)) return KILN_ERR_UNAVAILABLE;
    if (g_api->post(owner, &noop, nullptr) == KILN_ERR_REVOKED) return KILN_ERR_REVOKED;
    std::lock_guard lock(g_sys_mu);
    g_system_owners.set(owner, i, enabled);
    publish_system_switches();
    logf(enabled ? KILN_LOG_INFO : KILN_LOG_WARN, "system %.*s %s request (owner %llu); %u off", int(g_sys[i].name.size()),
         g_sys[i].name.data(), enabled ? "on" : "off", static_cast<unsigned long long>(owner), g_off_count.load());
    return KILN_OK;
}
KilnResult KILN_CALL system_release_owner(KilnOwner owner) {
    if (!may_change_system_hooks()) return KILN_ERR_WRONG_THREAD;
    release_system_hooks(owner);
    std::lock_guard lock(g_sys_mu);
    g_system_owners.release(owner);
    publish_system_switches();
    return KILN_OK;
}
void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    g_have_step = true;
    const bool reap = reap_due();
    if (reap) reap_system_hooks();
    if (g_systems_ok && g_modules.due() && !kset::runtime_conflict().empty()) {
        {
        std::lock_guard lock(g_sys_mu);
        LARGE_INTEGER now{};
        QueryPerformanceCounter(&now);
        if (!displace_runner(true, g_external_runner, g_system_owners, g_profile, now.QuadPart)) return;
        g_systems_ok = false;
        g_prof = false;
        publish_system_switches();
        }
        release_system_hooks(0,true);
        // A later MinHook can retain our trampoline. Keep this now-passive adapter mapped until process exit;
        // restoring the entry or unloading its code here would invalidate the other runtime's hook chain.
        HMODULE pin = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&runner_detour), &pin);
        logf(KILN_LOG_WARN, "native SET/power proxy appeared: systems restored, runner left passive until exit");
        return;
    }
    if (!reap || !g_off_count.load(std::memory_order_acquire)) return;
    std::lock_guard lock(g_sys_mu);
    if (g_system_owners.reap([](KilnOwner owner) { return !owner_revoked(owner); })) publish_system_switches();
}
void KILN_CALL on_world(const KilnWorldEvent* event, void*) {
    if (!event || event->kind != KILN_WORLD_SERVER || event->event != KILN_WORLD_STOPPING) return;
    std::lock_guard lock(g_sys_mu);
    g_system_owners.disabled.clear();
    publish_system_switches();
}

KilnResult KILN_CALL profile(bool on) {
    if (!g_systems_ok) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_sys_mu);
    if (!g_systems_ok) return KILN_ERR_UNAVAILABLE;
    LARGE_INTEGER n{};
    QueryPerformanceCounter(&n);
    g_prof.store(false, std::memory_order_release);
    g_profile.set(on, n.QuadPart);
    if (on) {
        for (size_t i = 0; i < g_sys.size(); ++i) g_calls[i] = 0, g_ticks[i] = 0, g_kinds[i] = 0;
    }
    g_prof.store(on, std::memory_order_release);
    return KILN_OK;
}
void KILN_CALL on_unload(void*) {
    g_modules.disarm();
    release_system_hooks(0, true);
    profile(false);
    {
        std::lock_guard lock(g_sys_mu);
        g_system_owners.disabled.clear();
        publish_system_switches();
    }
    if (!g_external_runner && !g_retained_updates) g_runner.revoke();
}

// ---- events: registered inline types, bounded maps/pages/posters, copied callback data ----
uint64_t event_type(uint32_t hash) {
    for (uint32_t i = 0; i < g_tt.event_count; ++i) {
        const uint64_t t = g_im.q(g_tt.events + 8ull * i);
        if (g_im.d(t + td::hash) == hash && g_by_hash.count(hash) && g_by_hash.at(hash) == t) return t;
    }
    return 0;
}
int event_stream(uint64_t storage, uint32_t hash, uint64_t& stream) {
    uint64_t schema, map, vals, streams, count;
    if (!read_at(g_memory, storage, 0, schema) || !schema || !add_address(schema, uint64_t(g_ev_map), map)) return -2;
    const int slot = event_slot(g_memory, map, hash);
    if (slot < 0) return slot;
    uint16_t idx;
    if (!read_at(g_memory, map, 0x40, vals) || !read_at(g_memory, vals, uint64_t(slot) * 2, idx) ||
        !read_at(g_memory, storage, uint64_t(g_ev_streams), streams) || !streams ||
        !read_at(g_memory, storage, uint64_t(g_ev_streams) + kStreamCount, count) || count > 65536 || idx >= count ||
        !add_address(streams, uint64_t(idx) * kStreamStride, stream)) return -2;
    return idx;
}
KilnResult prepare_event(uintptr_t world, uint32_t hash, uint64_t& storage, uint64_t& type, uint64_t& stream) {
    if (!g_events_ok) return KILN_ERR_UNAVAILABLE;
    if (!world) return KILN_ERR_INVALID_ARGUMENT;
    bool known_world = false, correct_thread = false;
    for (uint32_t kind = 0; kind < KILN_WORLD_KINDS; ++kind) {
        if (world == g_api->world(kind)) {
            known_world = true;
            correct_thread |= g_api->on_world_thread(kind);
        }
    }
    if (!known_world) return KILN_ERR_INVALID_ARGUMENT;
    if (!correct_thread) return KILN_ERR_WRONG_THREAD;
    type = event_type(hash);
    if (!type) return KILN_ERR_NOT_FOUND;
    if (!add_address(world, uint64_t(g_storage), storage)) return KILN_ERR_INVALID_ARGUMENT;
    const int idx = event_stream(storage, hash, stream);
    if (idx == -2) return KILN_ERR_INTERNAL;
    if (idx < 0) return KILN_ERR_NOT_FOUND;
    const uint32_t size = g_im.d(type + td::size);
    uint32_t stride;
    // The stream's element stride is the reflected size (4-byte events are packed at 4), not the size rounded to 8.
    if (!size || size >= 256 || !read_at(g_memory, stream, 0x14, stride) || stride != size)
        return KILN_ERR_INVALID_ARGUMENT;
    return KILN_OK;
}
KilnResult KILN_CALL event_each(uintptr_t world, uint32_t hash, KilnEventFn fn, void* user) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    uint64_t storage, type, stream;
    const KilnResult result = prepare_event(world, hash, storage, type, stream);
    if (result != KILN_OK) return result;
    const uint32_t size = g_im.d(type + td::size);
    std::vector<std::vector<uint8_t>> copies;
    if (!copy_events(g_memory, stream, size, copies)) return KILN_ERR_INTERNAL;
    for (const auto& copy : copies) fn(copy.data(), size, user);
    return KILN_OK;
}
KilnResult KILN_CALL event_post(uintptr_t world, uint32_t hash, const void* data, uint32_t size) {
    if (!data || !size || size >= 256) return KILN_ERR_INVALID_ARGUMENT;
    uint64_t storage, type, stream;
    const KilnResult result = prepare_event(world, hash, storage, type, stream);
    if (result != KILN_OK) return result;
    uint8_t payload[255];
    if (size != g_im.d(type + td::size) || !g_memory.read(reinterpret_cast<uint64_t>(data), payload, size) ||
        !valid_payload(g_im, type, payload, size)) return KILN_ERR_INVALID_ARGUMENT;
    uint64_t poster;
    if (!read_at(g_memory, storage, uint64_t(g_ev_streams) + kPosters, poster)) return KILN_ERR_INTERNAL;
    switch (append_event(g_memory, stream, poster, hash, payload, size)) {
        case 0: return KILN_OK;
        case 1: return KILN_ERR_INVALID_ARGUMENT;
        case 2: return KILN_ERR_BUFFER_TOO_SMALL;
        default: return KILN_ERR_INTERNAL;
    }
}

const KilnTypesApi kTable = {
    {sizeof(KilnTypesApi), KILN_TYPES_VERSION}, &status, &type_count, &type_at, &type_by_hash, &type_by_name, &type_info,
    &type_field, &type_enum, &component_type_count, &component_type_at, &event_type_count, &event_type_at,
    &system_count, &system_info, &system_find, &system_set_enabled, &profile, &event_each, &event_post, &system_release_owner, &system_hook, &system_unhook,
};

bool sym(const char* n, uintptr_t* out) { return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }
bool cst(const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }
bool feat(const char* n) { return g_api->feature(KilnStr{n, std::strlen(n)}); }

void add_detail(const std::string& s) { g_detail += (g_detail.empty() ? "" : "; ") + s; }

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (!api || !info || api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->on_unload = &on_unload;
    info->id = KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_qpf = f.QuadPart ? f.QuadPart : 1;
    const auto t0 = std::chrono::steady_clock::now();
    if (!image_of_process(g_im)) {
        logf(KILN_LOG_ERROR, "types: the exe has no .text/.rdata: unavailable");
        return KILN_ERR_UNAVAILABLE;
    }
    g_tt = find_type_table(g_im);
    if (!g_tt.error.empty()) {
        add_detail("type table: " + g_tt.error);
        g_tt = TypeTable{.error = g_tt.error};
    } else {
        index_types();
    }
    if (!g_tt.component_count) add_detail("the ECS component/event lists were not found");
    g_sys = scan_systems(g_im);
    const size_t n = g_sys.size();
    g_off = std::make_unique<std::atomic<bool>[]>(n);
    g_calls = std::make_unique<std::atomic<uint64_t>[]>(n);
    g_ticks = std::make_unique<std::atomic<uint64_t>[]>(n);
    g_kinds = std::make_unique<std::atomic<uint8_t>[]>(n);
    for (size_t i = 0; i < n; ++i) g_sys_by_fn.emplace_back(g_sys[i].fn, static_cast<uint32_t>(i));
    std::sort(g_sys_by_fn.begin(), g_sys_by_fn.end());
    uintptr_t runner = 0;
    g_modules.arm();  // before the startup check, so a DLL loading in between is re-checked
    const std::string conflict = kset::runtime_conflict();
    if (!conflict.empty()) add_detail("system runner disabled: " + conflict);
    if (conflict.empty() && n && feat("types.systems") && sym("types/ProgramRunner", &runner) && g_im.text.has(runner, 40)) {
        g_runner.install(g_api, g_owner, runner, reinterpret_cast<void*>(&runner_detour), "types/ProgramRunner");
        g_systems_ok = static_cast<bool>(g_runner);
        if (!g_systems_ok) add_detail("hooking the program runner failed");
    } else {
        add_detail(n ? "the program runner was not found (sigdb types.systems)" : "no system descriptors found");
    }
    if (g_systems_ok) {
        KilnHandle tick = 0, world = 0;
        if (g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_pre, nullptr, &tick) != KILN_OK ||
            g_api->subscribe_world(g_owner, &on_world, nullptr, &world) != KILN_OK) {
            if (tick) g_api->unsubscribe(tick);
            if (world) g_api->unsubscribe(world);
            g_runner.revoke();
            g_systems_ok = false;
            add_detail("system owner/world cleanup unavailable");
        }
    }
    // The streams and the map pointer live in the world's ECS storage (world + world.storage, a core constant); the
    // event map is in the registry that storage points to.
    g_events_ok = feat("types.events") && cst("types/events.map", &g_ev_map) && cst("types/events.streams", &g_ev_streams) &&
                  cst("world.storage", &g_storage);
    g_events_ok = g_events_ok && g_tt.error.empty() && g_tt.event_count && g_ev_map >= 0 && g_ev_map < 0x1000000 &&
                  g_ev_streams >= 0 && g_ev_streams < 0x1000000 && g_storage >= 0 && g_storage < 0x1000000;
    if (g_events_ok) g_ev_streams += 0x28;  // the capture is the lea's displacement; the streams start 0x28 later
    else add_detail("event streams not found (sigdb types.events)");
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    logf(g_detail.empty() ? KILN_LOG_INFO : KILN_LOG_WARN,
         "types: %u types (%u components, %u events), %zu systems%s, events %s; %.1f ms%s%s", g_tt.count,
         g_tt.component_count, g_tt.event_count, n, g_systems_ok ? " (runner hooked)" : " (switching off)",
         g_events_ok ? "on" : "off", ms, g_detail.empty() ? "" : "; ", g_detail.c_str());
    return api->register_service(owner, KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1}, &kTable);
}
