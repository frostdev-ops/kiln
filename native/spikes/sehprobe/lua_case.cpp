// SPDX-License-Identifier: MIT
// Actual Lua /EHsc frames must let an AV reach kiln-set's /EHa guard, rather than convert it to a Lua error.
#include <windows.h>
#include "lauxlib.h"
#include "lua.h"

__declspec(noinline) static int bad_binding(lua_State*) {
    static volatile uintptr_t address = 0x10;
    return *reinterpret_cast<const int*>(address);
}
static bool guarded_pcall(lua_State* L) {
    __try {
        lua_pcall(L, 0, 0, 0);
        return false;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        return true;
    }
}
int lua_fault_case() {
    lua_State* L = luaL_newstate();
    if (!L) return 0;
    lua_pushcfunction(L, [](lua_State* s) { return luaL_error(s, "ordinary Lua error"); });
    if (lua_pcall(L, 0, 0, 0) == LUA_OK) return 0;
    lua_settop(L, 0);
    lua_pushcfunction(L, &bad_binding);
    // Do not re-enter/close a faulted Lua state: its error chain points into abandoned /EHsc frames.
    return guarded_pcall(L);
}
