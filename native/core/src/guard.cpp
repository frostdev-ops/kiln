// SPDX-License-Identifier: MIT
// Each function holds no C++ objects with destructors, as __try requires. A caught fault also writes a crash report
// (crash.cpp) before the caller disables the consumer.
#include "guard.h"

#include <intrin.h>

#include "crash.h"

namespace kiln::guard {

bool tick(KilnTickFn fn, const KilnTickInfo* info, void* user, unsigned long* code) {
    crash::Capture cap{};
    __try {
        fn(info, user);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, code)) {
        crash::report(cap, "tick subscriber", reinterpret_cast<const void*>(fn));
        return false;
    }
}

bool task(KilnTaskFn fn, void* user, unsigned long* code) {
    crash::Capture cap{};
    __try {
        fn(user);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, code)) {
        crash::report(cap, "posted task", reinterpret_cast<const void*>(fn));
        return false;
    }
}

bool log_line(KilnLogLineFn fn, int32_t level, KilnStr text, void* user, unsigned long* code) {
    crash::Capture cap{};
    __try {
        fn(level, text, user);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, code)) {
        crash::report(cap, "game-log subscriber", reinterpret_cast<const void*>(fn));
        return false;
    }
}

bool plugin_main(KilnPluginMainFn fn, const KilnApi* api, KilnOwner owner, KilnPluginInfo* info, KilnResult* result,
                 unsigned long* code) {
    crash::Capture cap{};
    __try {
        *result = fn(api, owner, info);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, code)) {
        crash::report(cap, "plugin entry point", reinterpret_cast<const void*>(fn));
        return false;
    }
}

bool copy(void* dst, uintptr_t src, size_t n) {
    if (!src) return false;
    __try {
        __movsb(static_cast<unsigned char*>(dst), reinterpret_cast<const unsigned char*>(src), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kiln::guard

