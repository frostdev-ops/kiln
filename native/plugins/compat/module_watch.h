// SPDX-License-Identifier: MIT
// kcompat::ModuleWatch: when a "which DLLs are loaded" check is due again. Used by kiln-set and kiln.types (native SET
// detection) and kiln-shroudtopia (Shroudtopia's own loader detection). Header-only.
#pragma once
#include <windows.h>

#include <atomic>
#include <cstdint>

namespace kcompat {
// A module-inventory check (module enumeration, paths, stats, file reads) costs several wineserver round trips per
// loaded module under Wine: never run one per frame, tick or short poll. ModuleWatch says when it is due again,
// lock-free: after a DLL has loaded (LdrRegisterDllNotification, resolved from ntdll at run time; Wine implements it),
// or, where that is unavailable, at most once per fallback interval. The notification runs under the loader lock, so it only sets a flag.
class ModuleWatch {
public:
    static constexpr uint64_t fallback_ms = 3000;
    ModuleWatch() = default;
    ModuleWatch(const ModuleWatch&) = delete;
    ModuleWatch& operator=(const ModuleWatch&) = delete;
    ~ModuleWatch() { disarm(); }
    // Call before the startup check, so a DLL that loads between the two still makes the next due() true.
    // Returns whether DLL-load notifications are active (false: the time throttle is in use).
    bool arm() {
        if (cookie_.load(std::memory_order_acquire)) return true;
        changed_.store(false, std::memory_order_relaxed);
        next_.store(GetTickCount64() + fallback_ms, std::memory_order_relaxed);
        const HMODULE nt = GetModuleHandleW(L"ntdll.dll");
        if (!nt) return false;
        const auto reg = reinterpret_cast<Register>(reinterpret_cast<void*>(GetProcAddress(nt, "LdrRegisterDllNotification")));
        const auto unreg = reinterpret_cast<Unregister>(reinterpret_cast<void*>(GetProcAddress(nt, "LdrUnregisterDllNotification")));
        if (!reg || !unreg) return false;
        void* cookie = nullptr;
        if (reg(0, &notified, this, &cookie) < 0 || !cookie) return false;
        unregister_ = unreg;
        cookie_.store(cookie, std::memory_order_release);
        return true;
    }
    // Required before this object's code can be unmapped: the loader would otherwise call into freed memory.
    void disarm() {
        if (void* cookie = cookie_.exchange(nullptr, std::memory_order_acq_rel)) unregister_(cookie);
    }
    bool notifying() const { return cookie_.load(std::memory_order_acquire) != nullptr; }
    // True at most once per change (or per fallback interval). Steady state: one relaxed atomic load.
    bool due() {
        if (notifying())
            return changed_.load(std::memory_order_relaxed) && changed_.exchange(false, std::memory_order_acq_rel);
        const uint64_t now = GetTickCount64();  // user space (KUSER_SHARED_DATA), also under Wine
        uint64_t next = next_.load(std::memory_order_relaxed);
        return now >= next && next_.compare_exchange_strong(next, now + fallback_ms, std::memory_order_acq_rel);
    }

private:
    using Notify = void(WINAPI*)(uint32_t reason, const void* data, void* context);
    using Register = int32_t(WINAPI*)(uint32_t flags, Notify fn, void* context, void** cookie);
    using Unregister = int32_t(WINAPI*)(void* cookie);
    static void WINAPI notified(uint32_t reason, const void*, void* context) {
        // LDR_DLL_NOTIFICATION_REASON_LOADED. An unload cannot introduce a conflict.
        if (reason == 1) static_cast<ModuleWatch*>(context)->changed_.store(true, std::memory_order_release);
    }
    std::atomic<bool> changed_{false};
    std::atomic<uint64_t> next_{0};
    std::atomic<void*> cookie_{nullptr};
    Unregister unregister_ = nullptr;
};
}  // namespace kcompat
