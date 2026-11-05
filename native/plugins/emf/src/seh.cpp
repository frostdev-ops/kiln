// SPDX-License-Identifier: MIT
// Structured-exception guards. Every function that holds a __try keeps no C++ object with a destructor in its frame.
#include <intrin.h>

#include <cstdio>

#include "emf_internal.h"

namespace kemf {

namespace {
int filter(EXCEPTION_POINTERS* ep, unsigned long* code, uintptr_t* address) {
    *code = ep->ExceptionRecord->ExceptionCode;
    *address = reinterpret_cast<uintptr_t>(ep->ExceptionRecord->ExceptionAddress);
    // A stack overflow cannot be survived reliably: let it go (the game's own handler, a crash dump).
    if (*code == EXCEPTION_STACK_OVERFLOW) return EXCEPTION_CONTINUE_SEARCH;
    return EXCEPTION_EXECUTE_HANDLER;
}
}  // namespace

bool seh_call(ThunkFn fn, void* ctx, unsigned long* code, uintptr_t* address) {
    *code = 0;
    *address = 0;
    __try {
        fn(ctx);
        return true;
    } __except (filter(GetExceptionInformation(), code, address)) {
        return false;
    }
}

bool seh_copy(void* dst, const void* src, size_t n) {
    if (!dst || !src) return false;
    __try {
        __movsb(static_cast<unsigned char*>(dst), static_cast<const unsigned char*>(src), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_read_u64(uintptr_t a, uint64_t* out) { return a && seh_copy(out, reinterpret_cast<const void*>(a), 8); }
bool seh_read_u32(uintptr_t a, uint32_t* out) { return a && seh_copy(out, reinterpret_cast<const void*>(a), 4); }
bool seh_read_u16(uintptr_t a, uint16_t* out) { return a && seh_copy(out, reinterpret_cast<const void*>(a), 2); }
bool seh_read_u8(uintptr_t a, uint8_t* out) { return a && seh_copy(out, reinterpret_cast<const void*>(a), 1); }

bool guarded(Mod* m, const char* what, ThunkFn fn, void* ctx) {
    unsigned long code = 0;
    uintptr_t address = 0;
    if (seh_call(fn, ctx, &code, &address)) return true;
    if (m) m->faults.fetch_add(1, std::memory_order_relaxed);
    char where[160] = "";
    HMODULE h = nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(address), &h) &&
        h) {
        wchar_t name[MAX_PATH] = L"";
        GetModuleFileNameW(h, name, MAX_PATH);
        const std::wstring w(name);
        const size_t slash = w.find_last_of(L"\\/");
        std::snprintf(where, sizeof where, "%s+0x%llx", to_utf8(w.substr(slash == std::wstring::npos ? 0 : slash + 1)).c_str(),
                      static_cast<unsigned long long>(address - reinterpret_cast<uintptr_t>(h)));
    } else {
        std::snprintf(where, sizeof where, "0x%llx", static_cast<unsigned long long>(address));
    }
    mod_logf(m, KILN_LOG_ERROR, "%s faulted (exception 0x%08lx at %s%s); it is switched off, the game runs on", what, code,
             where, code == 0xE06D7363 ? ", an uncaught C++ exception" : "");
    mark_status_dirty();
    return false;
}

}  // namespace kemf
