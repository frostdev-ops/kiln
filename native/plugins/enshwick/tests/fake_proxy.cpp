// SPDX-License-Identifier: MIT
// Stand-ins for what kiln-enshwick looks for in the process (host tests, preloaded by compathost --load):
//   FAKE_STUB  powrprof.dll carrying the texts of Enshwick's stub (its thread name, the loader path); starts nothing
//   FAKE_OTHER powrprof.dll of another tool (SET's folder name)
//   FAKE_FORWARD powrprof.dll of a proxy loader that only forwards the system DLL's exports (issue 23): no tool's marker
//   FAKE_CLAIM takes the stub's per-process claim, Local\enshwick-stub-<pid>, as a stub that already started does
#include <windows.h>

#include <cwchar>

#if defined(FAKE_STUB)
extern "C" __declspec(dllexport) const char kMarker[] = "enshwick-loader-stub mods\\enshwick\\enshwick.dll";
#elif defined(FAKE_OTHER)
// A path reference alone is not Enshwick's stub identity (another tool can mention/install its loader).
extern "C" __declspec(dllexport) const char kMarker[] = "shrouded_engine_tweaks mods\\enshwick\\enshwick.dll";
#elif defined(FAKE_FORWARD)
extern "C" __declspec(dllexport) const char kMarker[] = "Forwarding exports to %s\\powrprof.dll";
#endif

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
#if defined(FAKE_CLAIM)
    if (reason == DLL_PROCESS_ATTACH) {
        wchar_t name[64];
        swprintf(name, 64, L"Local\\enshwick-stub-%lu", GetCurrentProcessId());
        CreateMutexW(nullptr, FALSE, name);  // held for the process life
    }
#else
    (void)reason;
#endif
    return TRUE;
}
