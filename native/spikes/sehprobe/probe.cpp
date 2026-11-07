// SPDX-License-Identifier: MIT
// SEH probe: does a __try/__except catch a hardware fault (access violation) that happens in code inlined directly
// inside the __try block, under the flags this file is compiled with? Built twice by CMakeLists.txt: once with
// /EHsc (synchronous C++ EH) and once with /EHa (clang: -fasync-exceptions). run.sh runs both under Wine.
// Each case runs in a child process (the exe re-launches itself with the case name), so an uncaught fault only
// kills that child: the parent prints CAUGHT / CRASH per case. The exit code counts uncaught a- and b-cases; the
// x-cases (catch(...) vs. a fault, C++ exceptions) are informational: for x-catchall CAUGHT means catch(...) swallows
// hardware faults.
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <cstdint>
#include <stdexcept>

#ifndef PROBE_MODE
#define PROBE_MODE "?"
#endif

// An address that is never mapped (low 64 KiB are reserved on Windows and Wine), hidden from the optimiser.
static const unsigned char* bad_ptr() {
    static volatile uintptr_t a = 0x10;
    return reinterpret_cast<const unsigned char*>(a);
}
static volatile int g_sink;

// (a1) inline 16-byte memcpy in __try: the vk.cpp read16 shape.
static int a_memcpy() {
    unsigned char out[16];
    const unsigned char* at = bad_ptr();
    __try {
        memcpy(out, at, 16);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 1;
    }
    g_sink = out[0];
    return 0;
}
// (a2) inline pointer dereference (load) in __try.
static int a_load() {
    const int* p = reinterpret_cast<const int*>(bad_ptr());
    int v = 0;
    __try {
        v = *p;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 1;
    }
    g_sink = v;
    return 0;
}
// (a3) inline store in __try (code-patch shape).
static int a_store() {
    int* p = reinterpret_cast<int*>(const_cast<unsigned char*>(bad_ptr()));
    __try {
        *p = 42;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 1;
    }
    return 0;
}
// (a4) pointer chase (game-structure walk shape): two dependent loads.
static int a_chase() {
    const uintptr_t* p = reinterpret_cast<const uintptr_t*>(bad_ptr());
    uintptr_t v = 0;
    __try {
        v = *reinterpret_cast<const uintptr_t*>(*p + 8);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 1;
    }
    g_sink = static_cast<int>(v);
    return 0;
}
// (b) the same memcpy in a noinline callee.
__declspec(noinline) static void copy_out(void* out, const void* at, size_t n) { memcpy(out, at, n); }
static int b_callee() {
    unsigned char out[16];
    __try {
        copy_out(out, bad_ptr(), 16);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 1;
    }
    g_sink = out[0];
    return 0;
}
// (b2) noinline callee doing a load.
__declspec(noinline) static int load_int(const int* p) { return *p; }
static int b_load() {
    int v = 0;
    __try {
        v = load_int(reinterpret_cast<const int*>(bad_ptr()));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 1;
    }
    g_sink = v;
    return 0;
}

// (b4) as b2, but the noinline callee is static and only called directly (not address-taken): clang may still move
// its load into the caller, after the call and outside the __try's call-site range (seen with /EHsc: CRASH).
__declspec(noinline) static int load_int_static(const int* p) { return *p; }
static int b_load_static() {
    int v = 0;
    __try {
        v = load_int_static(reinterpret_cast<const int*>(bad_ptr()));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 1;
    }
    g_sink = v;
    return 0;
}
// (x1) catch(...) around a faulting noinline call: does catch(...) swallow a hardware fault? (MSVC: only under /EHa.)
static int x_catchall() {
    int v = 0;
    try {
        v = load_int(reinterpret_cast<const int*>(bad_ptr()));
    } catch (...) {
        return 1;
    }
    g_sink = v;
    return 0;
}
// (x1b) catch(...) around a fault in a function called through a pointer the optimiser cannot see through: here the
// call is a real call site inside the try (a Lua-binding shape). CAUGHT = catch(...) swallows the access violation.
static int (*volatile g_load_fn)(const int*) = load_int;
static int x_catchall_indirect() {
    int v = 0;
    try {
        v = g_load_fn(reinterpret_cast<const int*>(bad_ptr()));
    } catch (...) {
        return 1;
    }
    g_sink = v;
    return 0;
}
// (b3) __try around a fault in a function called through an opaque pointer (Kiln's guard.cpp shape).
static int b_indirect() {
    int v = 0;
    __try {
        v = g_load_fn(reinterpret_cast<const int*>(bad_ptr()));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 1;
    }
    g_sink = v;
    return 0;
}
// (x2) a plain C++ throw/catch still works (exit 0 = caught by the typed handler).
__declspec(noinline) static void thrower(int v) { if (v) throw std::runtime_error("x"); }
static int x_cxx() {
    try {
        thrower(g_sink + 1);
    } catch (const std::runtime_error&) {
        return 1;
    }
    return 0;
}
// (x3) a C++ exception crossing a __try/__except frame (in a callee) still reaches the outer catch.
__declspec(noinline) static void seh_frame_thrower() {
    __try {
        thrower(g_sink + 1);
    } __except (GetExceptionCode() == 0xE06D7363 ? EXCEPTION_CONTINUE_SEARCH : EXCEPTION_EXECUTE_HANDLER) {
    }
}
static int x_cxx_through_seh() {
    try {
        seh_frame_thrower();
    } catch (const std::runtime_error&) {
        return 1;
    }
    return 0;
}

struct Case { const char* name; int (*fn)(); };
static const Case kCases[] = {
    {"a-inline-memcpy16", a_memcpy},
    {"a-inline-load", a_load},
    {"a-inline-store", a_store},
    {"a-inline-chase", a_chase},
    {"b-noinline-memcpy16", b_callee},
    {"b-noinline-load", b_load},
    {"b-noinline-static-load", b_load_static},
    {"b-indirect-call", b_indirect},
    {"x-catchall-indirect", x_catchall_indirect},
    {"x-catchall-swallows-av", x_catchall},
    {"x-cxx-throw", x_cxx},
    {"x-cxx-through-seh", x_cxx_through_seh},
};

int main(int argc, char** argv) {
    if (argc == 3 && strcmp(argv[1], "--case") == 0) {
        for (const Case& c : kCases)
            if (strcmp(c.name, argv[2]) == 0) return c.fn() == 1 ? 0 : 2;  // 0 caught, 2 no fault?!
        return 3;
    }
    char self[MAX_PATH];
    GetModuleFileNameA(nullptr, self, MAX_PATH);
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    int bad = 0;
    for (const Case& c : kCases) {
        char cmd[MAX_PATH + 64];
        snprintf(cmd, sizeof cmd, "\"%s\" --case %s", self, c.name);
        STARTUPINFOA si{sizeof si};
        PROCESS_INFORMATION pi{};
        DWORD code = 0xFFFFFFFF;
        if (CreateProcessA(nullptr, cmd, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) {
            if (WaitForSingleObject(pi.hProcess, 20000) != WAIT_OBJECT_0) TerminateProcess(pi.hProcess, 0xDEAD);
            WaitForSingleObject(pi.hProcess, 5000);
            GetExitCodeProcess(pi.hProcess, &code);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
        const char* verdict = code == 0 ? "CAUGHT" : code == 2 ? "NOFAULT" : "CRASH";
        printf("[%s] %-22s %s (exit 0x%08lx)\n", PROBE_MODE, c.name, verdict, code);
        if (code != 0 && c.name[0] != 'x') bad++;  // x-cases are informational
    }
    fflush(stdout);
    return bad;
}
