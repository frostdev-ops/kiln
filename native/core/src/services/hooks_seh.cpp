// SPDX-License-Identifier: MIT
// SEH-guarded primitives for kiln.hooks. Kept in their own translation unit: a function using __try/__except must
// hold no C++ object with a destructor, so these stay free of STL. A fault in a consumer's handler, or a read/write
// of unmapped memory, returns false (with the exception code) instead of taking the server down.
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <intrin.h>

#include "services/kiln_hooks.h"

namespace kiln::hooks::seh {

// A fault inside the rest of the chain (the handler called its original and the original, or a later link, faulted)
// is not this handler's: the filter lets it travel on to the game's own handlers instead of disabling the handler and
// calling the original a second time.
static int filter(unsigned long code, volatile const bool* in_original, unsigned long* out) {
    if (in_original && *in_original) return EXCEPTION_CONTINUE_SEARCH;
    *out = code;
    return EXCEPTION_EXECUTE_HANDLER;
}

bool call_detour(KilnDetourFn fn, KilnHookCall* call, void* user, uintptr_t* out, unsigned long* code,
                 volatile const bool* in_original) {
    __try {
        *out = fn(call, user);
        return true;
    } __except (filter(GetExceptionCode(), in_original, code)) {
        return false;
    }
}

bool call_detour2(KilnDetourFn2 fn, KilnHookCall2* call, void* user, uintptr_t* out, unsigned long* code,
                  volatile const bool* in_original) {
    __try {
        *out = fn(call, user);
        return true;
    } __except (filter(GetExceptionCode(), in_original, code)) {
        return false;
    }
}

bool call_mid(KilnMidFn fn, KilnRegisters* regs, void* user, unsigned long* code) {
    __try {
        fn(regs, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool copy(void* dst, const void* src, size_t n) {
    __try {
        __movsb(static_cast<unsigned char*>(dst), static_cast<const unsigned char*>(src), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kiln::hooks::seh
