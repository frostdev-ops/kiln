// Minimal Windows clock shim for the production hook adapter's Linux regression harness.
#pragma once
#include <chrono>
struct LARGE_INTEGER { long long QuadPart; };
inline int QueryPerformanceCounter(LARGE_INTEGER* p) {
    p->QuadPart = std::chrono::steady_clock::now().time_since_epoch().count(); return 1;
}
inline int QueryPerformanceFrequency(LARGE_INTEGER* p) { p->QuadPart = 1000000000; return 1; }
