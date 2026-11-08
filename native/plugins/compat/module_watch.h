// SPDX-License-Identifier: MIT
// kcompat::ModuleWatch: when a "which DLLs are loaded" check is due again. Used by kiln-set and kiln.types (native SET
// detection) and kiln-shroudtopia (Shroudtopia's own loader detection). Header-only.
#pragma once
#include <windows.h>
#include <winternl.h>

#include <atomic>
#include <cstdint>

namespace kcompat {
// A module-inventory check (module enumeration, paths, stats, file reads) costs several wineserver round trips per
// loaded module under Wine: never run one per frame, tick or short poll. ModuleWatch says when it is due again,
// lock-free for its callers:
//  - after a DLL load notification (LdrRegisterDllNotification, resolved from ntdll at run time). The callback runs
//    under the loader lock, so it only sets a lock-free flag;
//  - after the loaded-module list changed without one (e.g. Wine's no-resolve LdrLoadDll path does not notify): every
//    safety_ms one caller fingerprints the PEB loader list (entry count + a hash of each entry's base and name
//    buffer), a memory walk with no wineserver call, under the loader lock taken try-only (LdrLockLoaderLock flag 2,
//    never blocking: a busy lock retries in retry_ms). The baseline is taken at arm(), before the startup check, so
//    any change after it re-checks;
//  - where the loader-lock functions are missing, at most once per safety_ms unconditionally (the full check).
class ModuleWatch {
public:
    static constexpr uint64_t safety_ms = 3000, retry_ms = 250;
    static constexpr uint64_t fallback_ms = safety_ms;  // the unconditional interval without a fingerprint
    ModuleWatch() = default;
    ModuleWatch(const ModuleWatch&) = delete;
    ModuleWatch& operator=(const ModuleWatch&) = delete;
    ~ModuleWatch() { disarm(); }
    // arm/disarm are serialized by the plugin lifecycle; due and the callback may run on other threads.
    // Call before the startup check, so a DLL that loads between the two still makes the next due() true.
    // Returns whether DLL-load notifications are active (the fingerprint safety net works either way).
    bool arm() {
        if (cookie_.load(std::memory_order_acquire)) return true;
        changed_.store(false, std::memory_order_relaxed);
        next_.store(GetTickCount64() + safety_ms, std::memory_order_relaxed);
        const HMODULE nt = GetModuleHandleW(L"ntdll.dll");
        if (!nt) return false;
        lock_ = reinterpret_cast<Lock>(reinterpret_cast<void*>(GetProcAddress(nt, "LdrLockLoaderLock")));
        unlock_ = reinterpret_cast<Unlock>(reinterpret_cast<void*>(GetProcAddress(nt, "LdrUnlockLoaderLock")));
        uint64_t fp = 0;
        // Several tries: the startup path must not fall back to full checks just because the lock was busy once.
        bool printed = false;
        for (int i = 0; i < 50 && !printed; ++i) {
            const int r = fingerprint(&fp);
            if (r < 0) break;
            printed = r > 0;
            if (!printed) Sleep(1);
        }
        print_.store(printed ? fp : 0, std::memory_order_relaxed);
        printed_.store(printed, std::memory_order_release);
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
    // Whether the cheap fingerprint safety net is in use (false: the unconditional full check every safety_ms).
    bool fingerprinting() const { return printed_.load(std::memory_order_acquire); }
    // Consume BEFORE checking the inventory: a load during the check must leave another check pending.
    // One caller claims each change/interval. GetTickCount64 reads shared user-space data, including under Wine.
    bool due() {
        if (changed_.load(std::memory_order_relaxed) && changed_.exchange(false, std::memory_order_acq_rel)) {
            // Re-baseline now, before the caller's check: a later silent load still changes the fingerprint, and
            // the load just notified does not cost a second full check at the next safety round.
            uint64_t fp = 0;
            if (fingerprinting() && fingerprint(&fp) > 0) print_.store(fp, std::memory_order_relaxed);
            next_.store(GetTickCount64() + safety_ms, std::memory_order_relaxed);
            return true;
        }
        const uint64_t now = GetTickCount64();  // user space (KUSER_SHARED_DATA), also under Wine
        uint64_t next = next_.load(std::memory_order_relaxed);
        if (now < next || !next_.compare_exchange_strong(next, now + safety_ms, std::memory_order_acq_rel)) return false;
        if (!fingerprinting()) return true;  // no fingerprint: the throttled full check
        uint64_t fp = 0;
        const int r = fingerprint(&fp);
        if (r == 0) {  // loader lock busy: skip this round, never block the game thread
            next_.store(now + retry_ms, std::memory_order_relaxed);
            return false;
        }
        if (r < 0) return true;
        return print_.exchange(fp, std::memory_order_acq_rel) != fp;
    }
    // One fingerprint of the loaded-module list: 1 = *out set, 0 = the loader lock was busy, -1 = unavailable.
    int fingerprint(uint64_t* out) const {
        if (!lock_ || !unlock_) return -1;
        ULONG disposition = 0;
        ULONG_PTR cookie = 0;
        if (lock_(2 /* LDR_LOCK_LOADER_LOCK_FLAG_TRY_ONLY */, &disposition, &cookie) < 0) return -1;
        if (disposition != 1 /* LDR_LOCK_LOADER_LOCK_DISPOSITION_LOCK_ACQUIRED */) return 0;
        int result = -1;
        const PEB* peb = NtCurrentTeb()->ProcessEnvironmentBlock;
        if (peb && peb->Ldr) {
            const LIST_ENTRY* head = &peb->Ldr->InMemoryOrderModuleList;
            uint64_t h = 1469598103934665603ull, n = 0;
            auto mix = [&h](uint64_t v) {
                for (int i = 0; i < 8; ++i, v >>= 8) h = (h ^ (v & 0xff)) * 1099511628211ull;
            };
            const LIST_ENTRY* e = head->Flink;
            for (; e && e != head && n < 65536; e = e->Flink, ++n) {
                const auto* m = CONTAINING_RECORD(e, LDR_DATA_TABLE_ENTRY, InMemoryOrderLinks);
                mix(reinterpret_cast<uintptr_t>(m->DllBase));
                mix(reinterpret_cast<uintptr_t>(m->FullDllName.Buffer));
                mix(m->FullDllName.Length);
            }
            if (e == head) {
                mix(n);
                *out = h;
                result = 1;
            }
        }
        unlock_(0, cookie);
        return result;
    }

private:
    using Notify = void(WINAPI*)(uint32_t reason, const void* data, void* context);
    using Register = int32_t(WINAPI*)(uint32_t flags, Notify fn, void* context, void** cookie);
    using Unregister = int32_t(WINAPI*)(void* cookie);
    using Lock = int32_t(WINAPI*)(ULONG flags, ULONG* disposition, ULONG_PTR* cookie);
    using Unlock = int32_t(WINAPI*)(ULONG flags, ULONG_PTR cookie);
    static_assert(std::atomic<bool>::is_always_lock_free, "the loader callback must never acquire a lock");
    static void WINAPI notified(uint32_t reason, const void*, void* context) {
        // LDR_DLL_NOTIFICATION_REASON_LOADED. An unload cannot introduce a conflict.
        if (reason == 1) static_cast<ModuleWatch*>(context)->changed_.store(true, std::memory_order_release);
    }
    std::atomic<bool> changed_{false};
    std::atomic<uint64_t> next_{0};
    std::atomic<void*> cookie_{nullptr};
    std::atomic<bool> printed_{false};
    std::atomic<uint64_t> print_{0};
    Unregister unregister_ = nullptr;
    Lock lock_ = nullptr;
    Unlock unlock_ = nullptr;
};
}  // namespace kcompat
