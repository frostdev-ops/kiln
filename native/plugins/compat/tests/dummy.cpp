// SPDX-License-Identifier: MIT
// An empty DLL for compathost: stands in for another loader's DLL (shroudtopia.dll) or a mod DLL without an entry.
#include <windows.h>
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
#ifdef FAKE_FLAME
// Stands in for Flame's loader: its file names the entry point it calls without exporting it.
extern "C" __declspec(dllexport) const char compat_fake_loader_marker[] = "FlameStartRuntime";
#endif
#ifdef FAKE_SHROUD
extern "C" __declspec(dllexport) const char compat_fake_shroud_marker[] = "Shroudtopia";
#endif
