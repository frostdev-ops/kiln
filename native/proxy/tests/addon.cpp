// SPDX-License-Identifier: MIT
#include <windows.h>
extern "C" __declspec(dllexport) HRESULT WINAPI DirectInput8Create(HINSTANCE, DWORD, REFIID, void**, void*) { return E_FAIL; }
#ifndef TEST_PLAIN_PROXY
extern "C" __declspec(dllexport) void shroudlink_shutdown() {}
#endif
BOOL WINAPI DllMain(HINSTANCE, DWORD, void*) { return TRUE; }
