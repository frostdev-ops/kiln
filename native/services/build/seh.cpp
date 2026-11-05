// SPDX-License-Identifier: MIT
// Structured-exception guards around every call into the engine and every read of engine memory. No frame here holds
// a C++ object with a destructor, as __try requires.
#include <windows.h>

#include "internal.h"

namespace kb {

bool seh_read(uintptr_t f, ReadRequest* req, uintptr_t wrapper, bool* ok) {
    using Fn = bool (*)(ReadRequest*, uintptr_t);
    __try {
        *ok = reinterpret_cast<Fn>(f)(req, wrapper);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *ok = false;
        return false;
    }
}

bool seh_ready(uintptr_t f, uintptr_t wrapper, float* point4, bool* ok) {
    using Fn = bool (*)(uintptr_t, float*);
    __try {
        *ok = reinterpret_cast<Fn>(f)(wrapper, point4);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *ok = false;
        return false;
    }
}

bool seh_write(Fns::WriteFn f, uintptr_t world, CellSpan* span, const uint32_t* dims, const int32_t* origin,
               unsigned long* code) {
    __try {
        f(world, span, dims, origin);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_copy(void* dst, const void* src, size_t n) {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_item(uint32_t item, uintptr_t registry, const uint8_t** out) {
    __try {
        *out = fn.item_find(item, registry);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = nullptr;
        return false;
    }
}

bool seh_spawn(uintptr_t world, const uint8_t* guid, const void* slice, uint32_t* out, unsigned long* code) {
    __try {
        *out = fn.spawn(world, guid, slice);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        *out = 0;
        return false;
    }
}

bool seh_transform(uintptr_t world, uint32_t entity, const int64_t** out) {
    __try {
        *out = fn.get_transform(world, entity);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = nullptr;
        return false;
    }
}

}  // namespace kb
