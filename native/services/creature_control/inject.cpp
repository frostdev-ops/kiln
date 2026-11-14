// SPDX-License-Identifier: MIT
// kiln.creature_control: the native-only failure injection of 19d §5.2 (last paragraph) and §6 gate 3. Not part of the
// table and not reachable from a mod's API: an export of kiln-creature-control.dll that answers only when the lab switch
// file kiln/data/kiln.creature_control/inject existed when the service started. A lab test finds it with
// GetProcAddress. Tick thread.
//   what 1: request queue capacity (value; 1024 is the normal)
//   what 2: notice queue capacity per owner (value)
//   what 3: component transition bound in nanoseconds (value; 0 = the normal 0.5 s)
//   what 4: a restore that never verifies (value 0/1): RestoreFailed, quarantine and retirement follow
//   what 5: two-phase withdrawal now (value ignored): as when the broker's program stops
#include <windows.h>

#include <string>

#include "internal.h"

namespace kcc {

namespace {
bool g_inject = false;
}

void inject_init() {
    wchar_t dir[1024] = {};
    char utf8[1024] = {};
    size_t n = 0;
    if (!g_api->path || g_api->path(KILN_PATH_DATA, utf8, sizeof utf8 - 1, &n) != KILN_OK || n == 0 || n >= sizeof utf8) return;
    utf8[n] = 0;
    const int w = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, dir, 1024);
    if (w <= 0) return;
    std::wstring file(dir);
    file += L"\\kiln.creature_control\\inject";
    g_inject = GetFileAttributesW(file.c_str()) != INVALID_FILE_ATTRIBUTES;
    if (g_inject) logf(KILN_LOG_WARN, "failure injection is ON (lab switch kiln/data/kiln.creature_control/inject)");
}

}  // namespace kcc

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_creature_control_inject(uint32_t what, uint64_t value) {
    using namespace kcc;
    if (!g_inject || !g_broker) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    switch (what) {
        case 1: g_broker->set_request_capacity(static_cast<uint32_t>(value)); break;
        case 2: g_broker->set_notice_capacity(static_cast<uint32_t>(value)); break;
        case 3: g_broker->set_transition_timeout(value); break;
        case 4: g_broker->set_fail_restore(value != 0); break;
        case 5: g_broker->withdraw("injected withdrawal (lab)"); break;
        default: return KILN_ERR_INVALID_ARGUMENT;
    }
    logf(KILN_LOG_INFO, "inject %u = %llu", what, static_cast<unsigned long long>(value));
    return KILN_OK;
}
