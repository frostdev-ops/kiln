// SPDX-License-Identifier: MIT
#include <cassert>
#include <vector>
#include "../src/lua_imgui.cpp"
#include "lualib.h"
namespace { std::vector<char> calls; uint32_t flags; }
bool rt::inDraw() { return true; }
int main() {
    KilnUiApi ui{};
    ui.begin = [](KilnStr, bool*, uint32_t) { calls.push_back('B'); return true; };
    ui.end = [] { calls.push_back('E'); };
    ui.begin_child = [](KilnStr, float, float, uint32_t f) { flags = f; calls.push_back('C'); return true; };
    ui.end_child = [] { calls.push_back('c'); };
    kset::bindUi(&ui);
    lua_State* l = luaL_newstate(); luaL_openlibs(l); rt::openImGui(l);
    auto run = [&](const char* s) { assert(luaL_loadstring(l, s) == LUA_OK); int r = lua_pcall(l, 0, 0, 0); lua_settop(l, 0); return r; };
    rt::imguiMark();
    assert(run("ImGui.Begin('parent'); ImGui.BeginChild('child',0,0,false,64); ImGui.Begin('nested')") == LUA_OK);
    assert(flags == 64);
    rt::imguiRecover();
    assert((calls == std::vector<char>{'B','C','B','E','c','E'}));
    calls.clear(); rt::imguiMark();
    assert(run("ImGui.Begin('parent'); ImGui.BeginChild('child'); ImGui.End()") != LUA_OK);
    assert((calls == std::vector<char>{'B','C'}));
    rt::imguiRecover();
    assert((calls == std::vector<char>{'B','C','c','E'}));
    calls.clear(); rt::imguiMark();
    assert(run("ImGui.BeginChild('child')") != LUA_OK && calls.empty());
    assert(run("ImGui.Begin('')") != LUA_OK && calls.empty());
    assert(run("for i=1,100 do ImGui.Begin('nested') end") != LUA_OK);
    assert(calls.size() == 32);
    rt::imguiRecover(); assert(calls.size() == 64);
    lua_close(l);
    std::puts("ImGui recovery review: PASS");
}
