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

bool seh_copy(void* out, const void* source, size_t size) {
    __try { std::memcpy(out,source,size); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// ---- tracked writes (writes.cpp) ----
bool seh_read_other(Slice* out, void* storage, void* accessor, uint32_t entity, unsigned long* code) {
    __try {
        fn.read_other(out, storage, accessor, entity);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        out->ptr = nullptr;
        out->size = 0;
        return false;
    }
}

bool seh_write_other(Slice* out, void* storage, ProgramCtx* ctx, uint64_t accessor, uint32_t entity, unsigned long* code) {
    __try {
        fn.write_other(out, storage, fn.job_tracker(ctx->job), accessor, entity, fn.job_write_flag(ctx));
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        out->ptr = nullptr;
        out->size = 0;
        return false;
    }
}

bool seh_attr_changed(void (*f)(void*, void*), void* component, void* registry, unsigned long* code) {
    __try {
        f(component, registry);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_write_change(KilnWorldChangeFn f, uint32_t entity, void* data, uint32_t size, void* user, bool* accepted,
                      unsigned long* code) {
    __try {
        *accepted = f(entity, data, size, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        *accepted = false;
        return false;
    }
}

bool seh_write_cb(KilnWorldWriteFn f, const KilnWorldWriteResult* result, void* user, unsigned long* code) {
    __try {
        f(result, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kw
