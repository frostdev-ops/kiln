// SPDX-License-Identifier: MIT
// The few window functions wndproc_chain.h uses, on two fake windows, for wndproc_chain_test.cpp (a host test).
#pragma once
#include <cstdint>

using HWND = void*;
using UINT = unsigned;
using WPARAM = uintptr_t;
using LPARAM = intptr_t;
using LRESULT = intptr_t;
using LONG_PTR = intptr_t;
using BOOL = int;
#define CALLBACK
using WNDPROC = LRESULT (*)(HWND, UINT, WPARAM, LPARAM);
constexpr int GWLP_WNDPROC = -4;

namespace fakewin {
inline WNDPROC procs[2] = {};
inline int sets = 0;      // SetWindowLongPtrW calls
inline int defaults = 0;  // DefWindowProcW calls
inline HWND window(int i) { return reinterpret_cast<HWND>(static_cast<uintptr_t>(0x1000 + i)); }
inline int index(HWND h) {
    const auto v = reinterpret_cast<uintptr_t>(h);
    return v == 0x1000 ? 0 : v == 0x1001 ? 1 : -1;
}
}  // namespace fakewin

inline BOOL IsWindow(HWND h) { return fakewin::index(h) >= 0; }
inline LONG_PTR GetWindowLongPtrW(HWND h, int) { return reinterpret_cast<LONG_PTR>(fakewin::procs[fakewin::index(h)]); }
inline LONG_PTR SetWindowLongPtrW(HWND h, int, LONG_PTR v) {
    WNDPROC& slot = fakewin::procs[fakewin::index(h)];
    const WNDPROC old = slot;
    slot = reinterpret_cast<WNDPROC>(v);
    ++fakewin::sets;
    return reinterpret_cast<LONG_PTR>(old);
}
inline LRESULT CallWindowProcW(WNDPROC p, HWND h, UINT m, WPARAM w, LPARAM l) { return p(h, m, w, l); }
inline LRESULT DefWindowProcW(HWND, UINT, WPARAM, LPARAM) {
    ++fakewin::defaults;
    return 0;
}
