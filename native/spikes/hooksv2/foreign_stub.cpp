// SPDX-License-Identifier: MIT
// foreign-stub.dll: a stand-in for "another engine" in lab tests of kiln.hooks v2 (docs/kiln/services/hooks.md). It is
// not part of Kiln; it only exists to put a foreign hook next to Kiln's.
//
// Chain it from <game>/kiln/chain.ini ([chain] section: `chain\foreign-stub.dll`, the file in <game>/kiln/chain/). It
// reads foreign-stub.ini from its own folder:
//
//   before=0x64ed50,0x7bdb20     RVAs hooked in DllMain, i.e. before Kiln resolves anything: Kiln must chain over them
//   over=0x7bdb20                RVAs hooked once <game>/kiln/state/hooks.json exists (Kiln's boot is over): hooked
//                                over Kiln's own detours, which the first-world check must notice
//
// Every hook is a safetyhook mid hook that only counts calls (so it never needs the function's signature). The log goes to
// foreign-stub.log next to the DLL, with the call counts when a hooked function was called (every 5 s).
#include <windows.h>

#include <array>
#include <atomic>
#include <cstdio>
#include <fstream>
#include <mutex>
#include <string>
#include <vector>

#include "safetyhook.hpp"

namespace {

constexpr int kMax = 16;
struct One {
    uintptr_t rva = 0;
    std::atomic<uint64_t> calls{0};
    safetyhook::MidHook hook;
    const char* when = "";
};
One g_hooks[kMax];
int g_count = 0;
std::mutex g_mutex;
std::wstring g_dir;

void log_line(const std::string& s) {
    std::ofstream out(g_dir + L"\\foreign-stub.log", std::ios::app);
    out << s << "\n";
}

template <int N>
void on_call(safetyhook::Context&) {
    g_hooks[N].calls.fetch_add(1, std::memory_order_relaxed);
}
template <int... I>
constexpr auto make_table(std::integer_sequence<int, I...>) {
    return std::array<safetyhook::MidHookFn, sizeof...(I)>{&on_call<I>...};
}
const auto g_fns = make_table(std::make_integer_sequence<int, kMax>{});

void hook_rva(uintptr_t rva, const char* when) {
    std::lock_guard lock(g_mutex);
    if (g_count >= kMax) return;
    One& h = g_hooks[g_count];
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    auto r = safetyhook::MidHook::create(reinterpret_cast<void*>(base + rva), g_fns[g_count]);
    char b[160];
    if (!r) {
        std::snprintf(b, sizeof b, "%s: could not hook rva 0x%llx (error %d)", when, static_cast<unsigned long long>(rva),
                      static_cast<int>(r.error().type));
        log_line(b);
        return;
    }
    h.hook = std::move(*r);
    h.rva = rva;
    h.when = when;
    ++g_count;
    std::snprintf(b, sizeof b, "%s: hooked rva 0x%llx", when, static_cast<unsigned long long>(rva));
    log_line(b);
}

std::vector<uintptr_t> parse(const std::string& v) {
    std::vector<uintptr_t> out;
    size_t i = 0;
    while (i < v.size()) {
        size_t j = v.find(',', i);
        if (j == std::string::npos) j = v.size();
        const std::string t = v.substr(i, j - i);
        if (!t.empty()) out.push_back(static_cast<uintptr_t>(std::strtoull(t.c_str(), nullptr, 0)));
        i = j + 1;
    }
    return out;
}

std::vector<uintptr_t> g_over;

DWORD WINAPI worker(void*) {
    const std::wstring marker = g_dir + L"\\..\\state\\hooks.json";
    for (int i = 0; i < 1200; ++i) {  // up to 60 s
        if (GetFileAttributesW(marker.c_str()) != INVALID_FILE_ATTRIBUTES) break;
        Sleep(50);
    }
    for (uintptr_t rva : g_over) hook_rva(rva, "over");
    for (;;) {
        Sleep(5000);
        std::lock_guard lock(g_mutex);
        for (int i = 0; i < g_count; ++i) {
            char b[120];
            std::snprintf(b, sizeof b, "calls: rva 0x%llx (%s) %llu", static_cast<unsigned long long>(g_hooks[i].rva),
                          g_hooks[i].when, static_cast<unsigned long long>(g_hooks[i].calls.load()));
            log_line(b);
        }
    }
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, void*) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(inst);
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(inst, path, MAX_PATH);
    g_dir.assign(path, n);
    g_dir.erase(g_dir.find_last_of(L"\\/"));
    std::ifstream ini(g_dir + L"\\foreign-stub.ini");
    std::string line, before;
    while (std::getline(ini, line)) {
        if (line.rfind("before=", 0) == 0) before = line.substr(7);
        if (line.rfind("over=", 0) == 0) g_over = parse(line.substr(5));
    }
    log_line("loaded");
    for (uintptr_t rva : parse(before)) hook_rva(rva, "before");
    if (!g_over.empty()) CreateThread(nullptr, 0, &worker, nullptr, 0, nullptr);
    return TRUE;
}
