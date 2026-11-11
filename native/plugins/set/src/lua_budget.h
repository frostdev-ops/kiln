// SPDX-License-Identifier: MIT
#pragma once
#include <chrono>
#include "lua.h"
#include "lauxlib.h"

namespace kset::budget {
// Shared by every coroutine during one host callback. Protected calls and resumes must propagate exhaustion;
// otherwise `while true do pcall(...) end` can catch the hook error forever.
inline bool active = false, failed = false;
inline int remaining = 0;
inline std::chrono::steady_clock::time_point deadline;
inline void hook(lua_State* L, lua_Debug*) {
    if (active && (failed || (remaining -= 1000) <= 0 || std::chrono::steady_clock::now() >= deadline)) {
        failed = true;
        luaL_error(L, "script instruction/time budget exceeded");
    }
}
struct Scope {
    bool outer = !active;
    Scope() {
        if (outer) { active = true; failed = false; remaining = 1000000; deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(50); }
    }
    ~Scope() { if (outer) active = false; }
};
// Time Kiln spends inside a callback on its own one-time work (texts.inl: reading the client text catalog on the first
// lookup) is not the mod's: the deadline moves by it. Instructions are not given back.
inline void excuse(std::chrono::steady_clock::duration d) {
    if (active && !failed) deadline += d;
}
inline void check(lua_State* L) {
    if (active && (failed || std::chrono::steady_clock::now() >= deadline)) { failed = true; luaL_error(L, "script instruction/time budget exceeded"); }
}
inline int pcall(lua_State* L) {
    check(L);
    luaL_checkany(L, 1);
    int rc = lua_pcall(L, lua_gettop(L) - 1, LUA_MULTRET, 0);
    check(L);
    lua_pushboolean(L, rc == LUA_OK); lua_insert(L, 1);
    return lua_gettop(L);
}
inline int message(lua_State* L) {
    check(L);  // Lua calls message handlers with hooks suppressed after an instruction-hook error.
    lua_settop(L, 1); lua_pushvalue(L, lua_upvalueindex(1)); lua_insert(L, 1);
    lua_call(L, 1, 1);
    check(L);
    return 1;
}
inline int xpcall(lua_State* L) {
    check(L);
    luaL_checktype(L, 2, LUA_TFUNCTION);
    int n = lua_gettop(L);
    lua_pushvalue(L, 2); lua_pushcclosure(L, message, 1); lua_remove(L, 2); lua_insert(L, 1);
    int rc = lua_pcall(L, n - 2, LUA_MULTRET, 1);
    check(L);
    lua_remove(L, 1); lua_pushboolean(L, rc == LUA_OK); lua_insert(L, 1);
    return lua_gettop(L);
}
inline int resume(lua_State* L) {
    check(L);
    auto* co = lua_tothread(L, 1);
    luaL_argcheck(L, co != nullptr, 1, "thread expected");
    int args = lua_gettop(L) - 1;
    if (!lua_checkstack(co, args)) return luaL_error(L, "too many coroutine arguments");
    lua_xmove(L, co, args);
    int results = 0;
    int rc = lua_resume(co, L, args, &results);
    check(L);
    lua_pushboolean(L, rc == LUA_OK || rc == LUA_YIELD);
    if (!lua_checkstack(L, results + 1)) return luaL_error(L, "too many coroutine results");
    if (rc == LUA_OK || rc == LUA_YIELD) { lua_xmove(co, L, results); return results + 1; }
    lua_xmove(co, L, 1); return 2;
}
inline void install(lua_State* L) {
    lua_sethook(L, hook, LUA_MASKCOUNT, 1000);
    lua_pushcfunction(L, pcall); lua_setglobal(L, "pcall");
    lua_pushcfunction(L, xpcall); lua_setglobal(L, "xpcall");
    lua_getglobal(L, "coroutine"); lua_pushcfunction(L, resume); lua_setfield(L, -2, "resume"); lua_pop(L, 1);
}
} // namespace kset::budget
