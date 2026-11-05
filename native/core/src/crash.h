// SPDX-License-Identifier: MIT
// Native crash reports: kiln/crash-reports/crash-<time>-native-fault.txt for faults the SEH guards catch (guard.cpp),
// in the layout of the managed reports (managed/Kiln.Host/CrashReports.cs): Kiln and game identity, the mod list
// (kiln/logs/mods.txt, kept by Kiln.Host), the exception with a stack unwound from the faulting context, and the
// last lines of kiln.log. The server keeps running: the guard has already disabled the faulting consumer.
#pragma once
#include "common.h"

namespace kiln::crash {

constexpr int kMaxFrames = 32;

// Plain data (no destructor), so a guard function can keep it on its stack next to __try.
struct Capture {
    DWORD code;
    uint32_t nparams;
    uintptr_t address;  // faulting instruction
    uintptr_t info[2];  // access violation: [0] 0 read / 1 write / 8 execute, [1] the address
    uintptr_t rsp;
    uint32_t frames;
    uintptr_t stack[kMaxFrames];
};

// Use as the filter: __except (crash::filter(GetExceptionInformation(), &cap, &code)). Records the exception and
// unwinds the stack from the faulting context while it is intact; returns EXCEPTION_EXECUTE_HANDLER.
int filter(EXCEPTION_POINTERS* ep, Capture* cap, unsigned long* code);

// Call from the __except block. `where`: what was being called ("tick subscriber"); `callback`: the consumer's
// function (its module names the culprit). At most a few reports per process.
void report(const Capture& cap, const char* where, const void* callback);

}  // namespace kiln::crash
