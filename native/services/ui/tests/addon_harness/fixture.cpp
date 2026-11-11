// SPDX-License-Identifier: MIT
// Deliberately inert addon-shaped DLL for negative Windows startup-gate tests (no code from a third party).
#include <windows.h>
extern "C" __declspec(dllexport) HRESULT WINAPI DirectInput8Create(HINSTANCE, DWORD, const IID&, void**, void*) { return E_FAIL; }
#ifndef FORWARDED_SHUTDOWN
extern "C" __declspec(dllexport) void shroudlink_shutdown() {}
#else
// A matching export NAME is not enough: the destination must be code owned by this module.
#pragma comment(linker, "/export:shroudlink_shutdown=KERNEL32.GetTickCount")
#endif
