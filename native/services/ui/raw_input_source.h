// SPDX-License-Identifier: MIT
// Shared by kiln.ui and its native host-feed consumer. Query only after raw input was observed: a missing registration
// lets legacy input take over without a timeout (which would duplicate a delayed raw/legacy quick tap).
#pragma once
#include <windows.h>

namespace kui::raw_input {
inline bool registered_for(USHORT usage, HWND window) {
    RAWINPUTDEVICE devices[64];
    UINT count = 64;
    const UINT n = GetRegisteredRawInputDevices(devices, &count, sizeof(RAWINPUTDEVICE));
    // An unavailable/oversized registration list is not proof that raw input stopped. Keep the existing source.
    if (n == static_cast<UINT>(-1)) return true;
    bool page = false;
    for (UINT i = 0; i < n; ++i) {
        const auto& d = devices[i];
        if (d.usUsagePage != 1) continue;
        if (d.usUsage == usage) {
            if ((d.dwFlags & RIDEV_NOLEGACY) == RIDEV_EXCLUDE) return false;
            return !d.hwndTarget || d.hwndTarget == window || (!window && IsWindow(d.hwndTarget));
        }
        if (!d.usUsage && (d.dwFlags & RIDEV_PAGEONLY))
            page = !d.hwndTarget || d.hwndTarget == window || (!window && IsWindow(d.hwndTarget));
    }
    return page;
}
}  // namespace kui::raw_input
