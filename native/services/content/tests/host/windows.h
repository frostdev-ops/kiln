// SPDX-License-Identifier: MIT
// refs.cpp only asks whether the dedicated-server module exists. No Windows APIs run in the host tests.
#pragma once
inline void* GetModuleHandleA(const char*) { return reinterpret_cast<void*>(1); }
