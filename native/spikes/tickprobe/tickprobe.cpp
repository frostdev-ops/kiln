// Spike: hook the ECS sim step on the dedicated server and measure it. Read-only.
// Loaded as a ShroudForge native plugin. Writes <exe dir>/kiln/tickprobe.log.
//
// Facts used (EMF ENGINE_MAP §1.1, rebased for the current build, see docs/re/):
//   SimStep(storage, bool ticked); storage = world + kWorldStorage; world kind byte at storage+0x8;
//   u32 tick number at world + kWorldTick.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <optional>
#include <string>
#include <vector>

#include "safetyhook.hpp"

namespace {

constexpr const char* kSimStepPattern = "40 55 53 56 41 55 48 8D AC 24 F8 FF FD FF B8 08 01 02 00 E8 ?? ?? ?? ??";
constexpr uintptr_t kWorldStorage = 0xE179C0;  // build 1273043: 0xE15DC0, +0x1C00 program table growth
constexpr uintptr_t kWorldTick = 0xE17E9C;     // build 1273043: 0xE1629C

FILE* g_log = nullptr;
std::wstring g_dir;

// ---- spike (b): .NET hosted through hostfxr from <exe>/kiln/dotnet (a private win-x64 runtime) ----
struct hostfxr_initialize_parameters { size_t size; const wchar_t* host_path; const wchar_t* dotnet_root; };
using hostfxr_init_fn = int (*)(const wchar_t*, const hostfxr_initialize_parameters*, void**);
using hostfxr_get_delegate_fn = int (*)(void*, int, void**);
using hostfxr_close_fn = int (*)(void*);
using load_assembly_fn = int (*)(const wchar_t*, const wchar_t*, const wchar_t*, const wchar_t*, void*, void**);
constexpr int kHdtLoadAssemblyAndGetFunctionPointer = 5;
#define UNMANAGEDCALLERSONLY_METHOD ((const wchar_t*)-1)
using managed_init_fn = int (*)(const wchar_t*);
using managed_tick_fn = int64_t (*)(int32_t);
managed_tick_fn g_managed_tick = nullptr;
std::atomic<bool> g_clr_ready{false};
std::vector<float> g_clr_samples;
double g_clr_max = 0;
SafetyHookInline g_simstep;
LARGE_INTEGER g_freq{};

struct KindStats {
    uint64_t calls = 0, ticked = 0;
    double total_ms = 0, max_ms = 0;
    uint32_t last_tick = 0;
    uint32_t prev_after = 0;     // tick counter after the previous call
    double prev_start = 0;       // QPC ms at the previous call's start
    uint32_t in_hist[5] = {};    // tick-counter advance inside one call: 0,1,2,3,4+
    uint32_t gap_hist[5] = {};   // advance between the end of one call and the start of the next
    std::vector<float> intervals;  // ms between consecutive call starts
    LARGE_INTEGER window_start{};
    std::vector<float> samples;  // ms per ticked call in this window
};
KindStats g_stats[4];
std::atomic<bool> g_first_call{true};

void log(const char* fmt, ...) {
    if (!g_log) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    fprintf(g_log, "[%02d:%02d:%02d.%03d] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list ap;
    va_start(ap, fmt);
    vfprintf(g_log, fmt, ap);
    va_end(ap);
    fputc('\n', g_log);
    fflush(g_log);
}

std::optional<uintptr_t> find_unique(const char* pattern) {
    auto base = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    auto sec = IMAGE_FIRST_SECTION(nt);
    uint8_t* text = nullptr;
    size_t text_size = 0;
    for (int i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++sec) {
        if (std::memcmp(sec->Name, ".text", 5) == 0) {
            text = base + sec->VirtualAddress;
            text_size = sec->Misc.VirtualSize;
        }
    }
    if (!text) return std::nullopt;
    std::vector<int> bytes;  // -1 = wildcard
    for (const char* p = pattern; *p;) {
        while (*p == ' ') ++p;
        if (!*p) break;
        if (p[0] == '?') {
            bytes.push_back(-1);
            p += (p[1] == '?') ? 2 : 1;
        } else {
            bytes.push_back(static_cast<int>(strtoul(p, nullptr, 16)));
            p += 2;
        }
    }
    std::optional<uintptr_t> hit;
    for (size_t i = 0; i + bytes.size() <= text_size; ++i) {
        size_t j = 0;
        for (; j < bytes.size(); ++j)
            if (bytes[j] >= 0 && text[i + j] != bytes[j]) break;
        if (j == bytes.size()) {
            if (hit) return std::nullopt;  // not unique
            hit = reinterpret_cast<uintptr_t>(text + i);
        }
    }
    return hit;
}

double now_ms() {
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return static_cast<double>(c.QuadPart) * 1000.0 / static_cast<double>(g_freq.QuadPart);
}

void report(int kind, KindStats& s, double window_ms) {
    std::vector<float> v = s.samples;
    std::sort(v.begin(), v.end());
    auto pct = [&](double q) { return v.empty() ? 0.0 : v[std::min(v.size() - 1, size_t(q * v.size()))]; };
    log("kind=%d calls=%llu ticked=%llu tick=%u rate=%.1f/s step p50=%.2fms p99=%.2fms max=%.2fms thread=%lu",
        kind, s.calls, s.ticked, s.last_tick, s.ticked * 1000.0 / window_ms, pct(0.5), pct(0.99), s.max_ms,
        GetCurrentThreadId());
    if (!s.intervals.empty()) {
        std::vector<float> iv = s.intervals;
        std::sort(iv.begin(), iv.end());
        log("kind=%d ticks-in-call [0..4+]=%u/%u/%u/%u/%u ticks-between=%u/%u/%u/%u/%u interval p50=%.2fms p99=%.2fms",
            kind, s.in_hist[0], s.in_hist[1], s.in_hist[2], s.in_hist[3], s.in_hist[4], s.gap_hist[0], s.gap_hist[1],
            s.gap_hist[2], s.gap_hist[3], s.gap_hist[4], iv[iv.size() / 2], iv[std::min(iv.size() - 1, size_t(0.99 * iv.size()))]);
    }
    std::fill(std::begin(s.in_hist), std::end(s.in_hist), 0u);
    std::fill(std::begin(s.gap_hist), std::end(s.gap_hist), 0u);
    s.intervals.clear();
    if (kind == 0 && !g_clr_samples.empty()) {
        std::vector<float> c = g_clr_samples;
        std::sort(c.begin(), c.end());
        log("kind=0 managed call p50=%.3fms p99=%.3fms max=%.3fms n=%zu", c[c.size() / 2],
            c[std::min(c.size() - 1, size_t(0.99 * c.size()))], g_clr_max, c.size());
        g_clr_samples.clear();
        g_clr_max = 0;
    }
    s.calls = s.ticked = 0;
    s.max_ms = s.total_ms = 0;
    s.samples.clear();
}

void call_managed(uint32_t tick) {
    if (!g_clr_ready.load(std::memory_order_acquire)) return;
    const double t0 = now_ms();
    g_managed_tick(static_cast<int32_t>(tick));
    const double dt = now_ms() - t0;
    g_clr_samples.push_back(static_cast<float>(dt));
    g_clr_max = std::max(g_clr_max, dt);
}

uintptr_t hk_simstep(uintptr_t storage, uintptr_t ticked, uintptr_t r8, uintptr_t r9) {
    const uint8_t kind = *reinterpret_cast<uint8_t*>(storage + 0x8);
    const uintptr_t world = storage - kWorldStorage;
    if (g_first_call.exchange(false))
        log("first SimStep: storage=%p world=%p kind=%u thread=%lu", (void*)storage, (void*)world, kind,
            GetCurrentThreadId());
    if (kind == 0 && (ticked & 0xFF)) call_managed(*reinterpret_cast<uint32_t*>(world + kWorldTick));  // "Pre"
    const uint32_t tick_before = *reinterpret_cast<uint32_t*>(world + kWorldTick);
    const double t0 = now_ms();
    const uintptr_t r = g_simstep.call<uintptr_t>(storage, ticked, r8, r9);
    const double dt = now_ms() - t0;
    const uint32_t tick_after = *reinterpret_cast<uint32_t*>(world + kWorldTick);
    if (kind < 4) {
        KindStats& s = g_stats[kind];
        if (s.prev_start != 0) {
            s.intervals.push_back(static_cast<float>(t0 - s.prev_start));
            ++s.gap_hist[std::min<uint32_t>(4, tick_before - s.prev_after)];
        }
        ++s.in_hist[std::min<uint32_t>(4, tick_after - tick_before)];
        s.prev_start = t0;
        s.prev_after = tick_after;
        if (s.window_start.QuadPart == 0) QueryPerformanceCounter(&s.window_start);
        ++s.calls;
        if (ticked & 0xFF) {
            ++s.ticked;
            s.samples.push_back(static_cast<float>(dt));
        }
        s.max_ms = std::max(s.max_ms, dt);
        s.last_tick = *reinterpret_cast<uint32_t*>(world + kWorldTick);
        LARGE_INTEGER c;
        QueryPerformanceCounter(&c);
        const double window_ms = (c.QuadPart - s.window_start.QuadPart) * 1000.0 / g_freq.QuadPart;
        if (window_ms >= 10000.0) {
            report(kind, s, window_ms);
            s.window_start = c;
        }
    }
    return r;
}

bool boot_clr() {
    const std::wstring root = g_dir + L"\\dotnet";
    WIN32_FIND_DATAW fd;
    HANDLE f = FindFirstFileW((root + L"\\host\\fxr\\*").c_str(), &fd);
    std::wstring fxr;
    if (f != INVALID_HANDLE_VALUE) {
        do {
            if (fd.cFileName[0] != L'.') fxr = root + L"\\host\\fxr\\" + fd.cFileName + L"\\hostfxr.dll";
        } while (FindNextFileW(f, &fd));
        FindClose(f);
    }
    if (fxr.empty()) { log("CLR: no runtime under kiln\\dotnet; skipping spike (b)"); return false; }
    SetEnvironmentVariableW(L"DOTNET_EnableWriteXorExecute", L"0");
    SetEnvironmentVariableW(L"DOTNET_TieredCompilation", L"1");
    HMODULE h = LoadLibraryW(fxr.c_str());
    if (!h) { log("CLR: LoadLibrary(hostfxr) failed: %lu", GetLastError()); return false; }
    auto init = reinterpret_cast<hostfxr_init_fn>(GetProcAddress(h, "hostfxr_initialize_for_runtime_config"));
    auto get_delegate = reinterpret_cast<hostfxr_get_delegate_fn>(GetProcAddress(h, "hostfxr_get_runtime_delegate"));
    auto close = reinterpret_cast<hostfxr_close_fn>(GetProcAddress(h, "hostfxr_close"));
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    hostfxr_initialize_parameters params{sizeof(params), exe, root.c_str()};
    const std::wstring app = g_dir + L"\\clrspike";
    void* handle = nullptr;
    const double t0 = now_ms();
    int rc = init((app + L"\\ClrSpike.runtimeconfig.json").c_str(), &params, &handle);
    if (rc != 0 || !handle) { log("CLR: initialize failed rc=0x%x", rc); return false; }
    load_assembly_fn load = nullptr;
    rc = get_delegate(handle, kHdtLoadAssemblyAndGetFunctionPointer, reinterpret_cast<void**>(&load));
    close(handle);
    if (rc != 0 || !load) { log("CLR: get_runtime_delegate failed rc=0x%x", rc); return false; }
    const std::wstring dll = app + L"\\ClrSpike.dll";
    managed_init_fn minit = nullptr;
    rc = load(dll.c_str(), L"Kiln.Spike.Entry, ClrSpike", L"Init", UNMANAGEDCALLERSONLY_METHOD, nullptr,
              reinterpret_cast<void**>(&minit));
    if (rc != 0 || !minit) { log("CLR: load Init failed rc=0x%x", rc); return false; }
    rc = load(dll.c_str(), L"Kiln.Spike.Entry, ClrSpike", L"Tick", UNMANAGEDCALLERSONLY_METHOD, nullptr,
              reinterpret_cast<void**>(&g_managed_tick));
    if (rc != 0 || !g_managed_tick) { log("CLR: load Tick failed rc=0x%x", rc); return false; }
    const int major = minit((g_dir + L"\\clr.log").c_str());
    log("CLR: .NET %d booted in %.0f ms", major, now_ms() - t0);
    g_clr_ready.store(true, std::memory_order_release);
    return true;
}

DWORD WINAPI init(void*) {
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring dir(exe);
    dir = dir.substr(0, dir.find_last_of(L"\\/")) + L"\\kiln";
    CreateDirectoryW(dir.c_str(), nullptr);
    g_dir = dir;
    g_log = _wfopen((dir + L"\\tickprobe.log").c_str(), L"a");
    QueryPerformanceFrequency(&g_freq);
    log("tickprobe loaded (pid %lu)", GetCurrentProcessId());
    auto addr = find_unique(kSimStepPattern);
    if (!addr) {
        log("SimStep pattern not found exactly once; doing nothing");
        return 0;
    }
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    log("SimStep at RVA 0x%llx", static_cast<unsigned long long>(*addr - base));
    g_simstep = safetyhook::create_inline(reinterpret_cast<void*>(*addr), reinterpret_cast<void*>(&hk_simstep));
    log(g_simstep ? "SimStep hooked" : "SimStep hook FAILED");
    boot_clr();
    return 0;
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(inst);
        if (HANDLE h = CreateThread(nullptr, 0, init, nullptr, 0, nullptr)) CloseHandle(h);
    }
    return TRUE;
}
