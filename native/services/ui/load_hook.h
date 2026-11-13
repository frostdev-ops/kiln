// SPDX-License-Identifier: MIT
// kiln.ui's hook on LoadLibraryExW. ui.cpp installs it when vulkan-1.dll is not loaded yet: the overlay's Vulkan hooks
// and the gamepad hook are made after the load that brings their library. The code is in a header so that
// tests/load_hook/harness.cpp runs the same lines in a Windows process (tests/test-load-hook-wine.sh).
//
// The original is called without the hook's mutex. SafetyHookInline::call() holds that mutex for the whole original
// call, so every load in the process waited for the one in progress. A load that waits for another thread (a graphics
// driver's DllMain does) while that thread loads a library never ended, and the game hung at its start in a stack
// with other engines (GitHub PR 27). The original is published before the hook is enabled, and the hook is never
// removed (kiln.ui is never unloaded), so the trampoline lives as long as the process.
#pragma once
#include <windows.h>

#include <atomic>

#include "safetyhook.hpp"

namespace kui::load_hook {

using Fn = HMODULE(WINAPI*)(LPCWSTR, HANDLE, DWORD);
// Called after every load that gave a module, on the thread that loaded. That thread may hold the loader lock (a load
// made from a DllMain), and several threads can be in it at once: it must not wait for another thread.
using Loaded = void (*)();

inline SafetyHookInline g_hook;
inline std::atomic<Fn> g_original{nullptr};
inline std::atomic<Loaded> g_loaded{nullptr};

inline HMODULE WINAPI detour(LPCWSTR name, HANDLE file, DWORD flags) {
    // Nothing runs before the original, and nothing after a load that failed: its caller reads the last error.
    const Fn original = g_original.load(std::memory_order_acquire);
    // never null here (published before enable()); the locked call is what this detour did before
    const HMODULE m = original ? original(name, file, flags) : g_hook.call<HMODULE>(name, file, flags);
    if (m) {
        if (const Loaded loaded = g_loaded.load(std::memory_order_acquire)) loaded();
    }
    return m;
}

// Hooks LoadLibraryExW (kernelbase's, else kernel32's). False: not hooked, loads go on as before.
inline bool install(Loaded loaded) {
    void* target = reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernelbase.dll"), "LoadLibraryExW"));
    if (!target) target = reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryExW"));
    if (!target) return false;
    // Created disabled, the original published, then enabled: another engine's thread loads its plugins while this
    // runs, and a load that reached the detour before the original was known came back as failed.
    g_hook = safetyhook::create_inline(target, reinterpret_cast<void*>(&detour), SafetyHookInline::StartDisabled);
    if (!g_hook) return false;
    g_loaded.store(loaded, std::memory_order_release);
    g_original.store(g_hook.original<Fn>(), std::memory_order_release);
    return static_cast<bool>(g_hook.enable());
}

}  // namespace kui::load_hook
