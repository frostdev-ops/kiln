// SPDX-License-Identifier: MIT
// refs.cpp only asks whether the dedicated-server module exists; newcontent.cpp's game texts ask which thread calls
// (text_test.cpp sets host_thread_id to play the main thread or another one). No other Windows API runs in the host tests.
#pragma once
inline void* GetModuleHandleA(const char*) { return reinterpret_cast<void*>(1); }
inline unsigned long host_thread_id = 1;
inline unsigned long GetCurrentThreadId() { return host_thread_id; }
