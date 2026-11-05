// SPDX-License-Identifier: MIT
// Structured-exception guards around calls into consumer callbacks and game code. Each frame holds no C++ object
// with a destructor, as __try requires.
#include <windows.h>

#include "world_internal.h"

namespace kw {

bool seh_each(KilnProgramEachFn fn, KilnProgramCtx* ctx, uint64_t* iter, KilnEntity e, void* user,
              unsigned long* code) {
    __try {
        fn(ctx, iter, e, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_update(KilnProgramUpdateFn fn, KilnProgramCtx* ctx, void* user, unsigned long* code) {
    __try {
        fn(ctx, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_teleport_cb(KilnTeleportFn fn, const KilnTeleportResult* r, void* user, unsigned long* code) {
    __try {
        fn(r, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_register_program(RegisterProgramFn f, uintptr_t world, const EngineDesc* d, bool* ok, unsigned long* code) {
    __try {
        *ok = f(world, d, false);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_iter_begin(void* ctx, void* iter, uint32_t size, unsigned long* code) {
    __try {
        fn.iter_begin(ctx, iter, size);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_iter_next(void* ctx, void* iter, uint32_t size, bool* more, unsigned long* code) {
    __try {
        *more = fn.iter_next(ctx, iter, size);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_get_component(uintptr_t world, uint32_t hash, uint32_t entity, Slice* out) {
    __try {
        fn.get_component(out, world, hash, entity);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        out->ptr = nullptr;
        out->size = 0;
        return false;
    }
}

bool seh_get_transform(uintptr_t world, uint32_t entity, const void** out) {
    __try {
        *out = fn.get_transform(world, entity);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = nullptr;
        return false;
    }
}

bool seh_queue_push(void* cb, void* buf, uint8_t kind, uint32_t size, uint32_t entity, void** out) {
    __try {
        *out = fn.queue_push(cb, buf, kind, size, entity);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = nullptr;
        return false;
    }
}

bool seh_read_u64(uintptr_t address, uint64_t* out) {
    __try {
        *out = *reinterpret_cast<const volatile uint64_t*>(address);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kw
