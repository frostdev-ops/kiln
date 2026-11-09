#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Compile the real caller-count implementation with module-lookup stand-ins and race its publishers."""
import os
from pathlib import Path
import subprocess

here = Path(__file__).resolve().parent
out = Path(os.environ['KILN_OUT']) / 'ui-caller-test'
out.mkdir(parents=True, exist_ok=True)
source = (here.parent / 'ui.cpp').read_text()
start = source.index('struct PerCaller {')
end = source.index('void KILN_CALL t_set_menu(', start)
code = r'''
#include <algorithm>
#include <atomic>
#include <barrier>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <thread>
#include <vector>
using HMODULE = void*;
using LPCWSTR = const wchar_t*;
constexpr int GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 1;
constexpr int GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 2;
#define KILN_CALL
thread_local uintptr_t module_id = 0;
bool GetModuleHandleExW(int, LPCWSTR, HMODULE* out) { *out = reinterpret_cast<void*>(module_id); return true; }
void* GetProcAddress(HMODULE, const char*) { return reinterpret_cast<void*>(1); }
void* _ReturnAddress() { return reinterpret_cast<void*>(module_id); }
std::atomic<uint32_t> g_demand{0}, g_interactive{0};
'''
code += source[start:end]
code += r'''
int main() {
    std::barrier sync(3);
    constexpr int rounds = 100000;
    auto run = [&](uintptr_t id) {
        module_id = id;
        for (int i = 0; i < rounds; ++i) {
            sync.arrive_and_wait();
            t_set_demand(1); t_set_capture(1);
            sync.arrive_and_wait();
            sync.arrive_and_wait();
        }
    };
    std::thread a(run, 1), b(run, 2);
    int failures = 0;
    for (int i = 0; i < rounds; ++i) {
        sync.arrive_and_wait(); sync.arrive_and_wait();
        if (g_demand != 2 || g_interactive != 2) ++failures;
        // Workers are stopped at the third barrier: reset through each actual caller's API.
        for (uintptr_t id : {1, 2}) { module_id = id; t_set_demand(0); t_set_capture(0); }
        sync.arrive_and_wait();
    }
    a.join(); b.join();
    std::printf("UI caller publication: %s, %d stale totals in %d rounds\n", failures ? "FAIL" : "PASS", failures, rounds);
    return failures ? 1 : 0;
}
'''
cpp = out / 'per_caller_test.cpp'
cpp.write_text(code)
exe = out / 'per_caller_test'
subprocess.run(['g++', '-std=c++20', '-O1', '-g', '-Wall', '-Wextra', '-pthread', '-fsanitize=address,undefined', str(cpp), '-o', str(exe)], check=True)
subprocess.run([str(exe)], check=True)
