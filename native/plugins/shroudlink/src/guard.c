/* SPDX-License-Identifier: MIT
 * Kiln integration boundary: a broken native mod's load/entry must not unwind out of the loader worker. */
#include <windows.h>

unsigned long kiln_sl_guard(void (*run)(void*), void* arg) {
    __try { run(arg); return 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return GetExceptionCode(); }
}
HMODULE kiln_sl_load(const wchar_t* path) {
    return LoadLibraryExW(path, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
}
/* Linked only by the regression test (otherwise removed by the linker). */
void kiln_sl_test_fault(void) { RaiseException(0xE0000042, 0, 0, NULL); }
