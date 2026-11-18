// SPDX-License-Identifier: MIT
// gate-priority-test: the measurement mod of the native-Windows priority kit (docs/kiln/priority-kit.md, GitHub #58).
// Compare-exchange writes from a kiln.tasks task in a loop, through kiln.world's ecs_compare_exchange (the engine's
// write gate), logging every call's outcome and hold time, the server's step times and kiln.engine's gate counters.
//
// It changes nothing a game system reads: the target is one padding byte of a plain, untracked component of the host's
// player, a byte that no reflected field, base class or array element covers, in a type whose whole layout the
// reflection explains (every gap is alignment, nothing overlaps). Each write flips one bit of that byte under a
// one-byte mask and the next one puts it back; the component is read back to check. Off unless the run file names the
// running world.
//
// Portable: no Windows call outside current_thread_priority(), so tests/kit_test.cpp runs this code on the host over
// the fake engine and kiln.world's real write code, and tests/host_win.cpp loads the DLL under Wine.
#pragma once
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_native.h"
#include "services/kiln_types.h"

namespace gpt {

inline constexpr const char* kKitVersion = "1";

// <kiln data>/gate-priority-test/run.txt, one key=value per line ('#' starts a comment).
struct RunConfig {
    std::string world;      // the world's GUID, 32 lower-case hex digits; "" = none named (log the GUID only)
    std::string component;  // a component to use instead of the automatic pick ("" = pick)
    int offset = -1;        // the byte of `component` to flip: one of its padding bytes (-1 = the first of them)
    uint32_t rate = 50;     // change-and-restore pairs per second, 1..1000
    uint32_t minutes = 15;  // how long the writes run, 1..120
    uint32_t seconds = 0;   // seconds=: the same in seconds, 1..7200, for short checks (wins over minutes)
    uint32_t warmup_s = 30; // steps recorded before the first write, 0..600
    bool noop = false;      // mode=noop: compare-exchanges that change nothing (they still take the gate)
    std::string label;      // free text for the analysis ([A-Za-z0-9._-], at most 48)
    std::vector<std::string> warnings;  // unknown keys and values out of range (logged)
};
RunConfig parse_run(std::string_view text);

// The padding bytes of a reflected type, or why it has none we may use.
struct Padding {
    std::vector<uint32_t> bytes;  // offsets in the type, ascending
    std::string why;              // when bytes is empty: why not (a gap the reflection does not explain, ...)
};
Padding padding_of(const KilnTypesApi* types, const KilnType* type);

// Thread priority of the calling thread (Windows THREAD_PRIORITY_*; 0 elsewhere unless a test sets the hook).
int current_thread_priority();
extern int (*g_priority_hook)();

}  // namespace gpt

extern "C" KILN_EXPORT int KILN_CALL kiln_native_init(const KilnNativeContext* ctx);
