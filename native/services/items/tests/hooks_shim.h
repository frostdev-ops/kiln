// SPDX-License-Identifier: MIT
// A stand-in for kiln.hooks v2 in host tests (no core, no game, runs under plain Wine). raw_detour is a safetyhook inline
// hook on the target that is never removed, *original is its trampoline, revoke does nothing, caller() answers 0.
// fail_nth makes the Nth raw_detour call fail (a hook that cannot be placed). The real registry is tested by
// native/core/tests/test-hooks-v2.sh; this only lets kiln.items' own hook code run against the kiln::RawHook API.
#pragma once
#include <windows.h>

#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "safetyhook.hpp"
#include "services/kiln_hooks.h"

namespace hooks_shim {

inline int calls = 0;       // raw_detour calls so far
inline int fail_nth = 0;    // 0 = never fail
inline int links = 0;       // links placed

inline KilnResult KILN_CALL raw_detour(KilnOwner, uintptr_t target, const KilnHookOrder*, void* fn, void** original,
                                       KilnHandle* out) {
    if (!fn || !target) return KILN_ERR_INVALID_ARGUMENT;
    if (++calls == fail_nth) return KILN_ERR_UNAVAILABLE;
    auto* hook = new SafetyHookInline(safetyhook::create_inline(reinterpret_cast<void*>(target), fn));  // never freed
    if (!*hook) return KILN_ERR_UNAVAILABLE;
    if (original) *original = hook->original<void*>();
    if (out) *out = static_cast<KilnHandle>(++links);
    return KILN_OK;
}
inline uintptr_t KILN_CALL caller(KilnHandle) { return 0; }
inline KilnResult KILN_CALL revoke(KilnHandle) { return KILN_OK; }

inline const void* KILN_CALL service(KilnStr, uint32_t) {
    static KilnHooksApi table = [] {
        KilnHooksApi t{};
        t.header = {sizeof t, KILN_HOOKS_VERSION};
        t.raw_detour = &raw_detour;
        t.caller = &caller;
        t.revoke = &revoke;
        return t;
    }();
    return &table;
}

inline const KilnApi* api() {
    static KilnApi a = [] {
        KilnApi x{};
        x.size = sizeof x;
        x.service = &service;
        return x;
    }();
    return &a;
}

}  // namespace hooks_shim
