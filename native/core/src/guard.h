// SPDX-License-Identifier: MIT
// Calls into consumer code under a structured-exception guard, so a faulting plugin callback is disabled instead of
// taking the server down. Returns false (and the exception code) when the call faulted.
#pragma once
#include "common.h"

namespace kiln::guard {

bool tick(KilnTickFn fn, const KilnTickInfo* info, void* user, unsigned long* code);
bool task(KilnTaskFn fn, void* user, unsigned long* code);
bool log_line(KilnLogLineFn fn, int32_t level, KilnStr text, void* user, unsigned long* code);
bool plugin_main(KilnPluginMainFn fn, const KilnApi* api, KilnOwner owner, KilnPluginInfo* info, KilnResult* result,
                 unsigned long* code);

// memcpy from game memory that may be unmapped: false instead of a crash.
bool copy(void* dst, uintptr_t src, size_t n);

}  // namespace kiln::guard
