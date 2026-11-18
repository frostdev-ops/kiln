// SPDX-License-Identifier: MIT
// Windows test host of gate-priority-test (tests/wine.sh, headless Wine; never shipped, never built by default):
// loads the mod's DLL with LoadLibrary and runs one scenario of scenarios.h against the fake Kiln of harness.h, whose
// kiln.world compare-exchange writes run over kiln.engine's real Windows primitives (memory_win.cpp: the SEH copies and
// writes, the Interlocked words; gate.cpp: the gate and its priority raise through SetThreadPriority).
//   gate-priority-host.exe <scenario> <sigdb\types dir> <gate-priority-test.dll>
#include "scenarios.h"

namespace kiln::engine::mem {
std::uintptr_t image_base() { return harness::g_image.load(); }
}  // namespace kiln::engine::mem

int wmain(int argc, wchar_t** argv) {
    if (argc < 4) {
        std::printf("usage: gate-priority-host <scenario> <sigdb types dir> <mod dll>\n");
        return 2;
    }
    const auto narrow = [](const wchar_t* w) {
        const int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
        std::string s(n > 0 ? n - 1 : 0, '\0');
        if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
        return s;
    };
    HMODULE dll = LoadLibraryW(argv[3]);
    if (!dll) {
        std::printf("FAIL LoadLibrary %s (error %lu)\n", narrow(argv[3]).c_str(), GetLastError());
        return 1;
    }
    const auto init = reinterpret_cast<KilnNativeInitFn>(GetProcAddress(dll, "kiln_native_init"));
    if (!init) {
        std::printf("FAIL the DLL exports no kiln_native_init\n");
        return 1;
    }
    harness::check(true, "the mod's DLL loaded and exports kiln_native_init");
    return harness::run_scenario(narrow(argv[1]), narrow(argv[2]), init);
}
