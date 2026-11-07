// SPDX-License-Identifier: MIT
// A DLL with the export Enshwick mods carry (enshwick_mod_abi), so kiln-enshwick's host tests classify a DLL mod.
// It is never started: the stand-in loader only lists folders.
#include <windows.h>
#include <cstdint>

extern "C" __declspec(dllexport) uint32_t enshwick_mod_abi() { return 1; }
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
