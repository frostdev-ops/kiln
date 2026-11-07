// SPDX-License-Identifier: MIT
// kiln-set: SET's ImGui table for Draw callbacks (Shrouded Engine Tweaks api-reference "UI (ImGui)", MIT), drawn
// through kiln.ui's widget table, so mod windows share Kiln's overlay, input capture and style. Kiln's own code.
// Only inside Draw (the overlay frame); elsewhere every function raises an error, as in SET.
#include <cstring>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "lauxlib.h"
#include "lua.h"
#include "lua_api.h"
#include "services/kiln_ui.h"

namespace kset {
void bindUi(const KilnUiApi* ui);
}

namespace {
const KilnUiApi* U = nullptr;
enum class Scope { Window, Child };
std::vector<Scope> g_scopes;  // preserve nesting when recovering a callback that errored

KilnStr arg(lua_State* L, int i) {
    size_t n = 0;
    const char* s = luaL_checklstring(L, i, &n);
    return KilnStr{s, n};
}
void need(lua_State* L) {
    if (!rt::inDraw()) luaL_error(L, "ImGui works only inside Draw callbacks");
    if (!U) luaL_error(L, "the overlay is not available here (no kiln.ui: a dedicated server, or an unknown build)");
}
uint32_t rgba(lua_State* L, int i) {
    auto c = [&](int k) { double v = luaL_checknumber(L, i + k); return uint32_t(v < 0 ? 0 : v > 1 ? 255 : v * 255 + 0.5); };
    return c(0) | c(1) << 8 | c(2) << 16 | c(3) << 24;  // ImGui's packed ABGR (IM_COL32)
}

int Begin(lua_State* L) {
    need(L);
    KilnStr name = arg(L, 1);
    if (!name.len || !name.ptr[0]) return luaL_error(L, "ImGui.Begin needs a nonempty name");
    if (g_scopes.size() >= 32) return luaL_error(L, "too many nested ImGui windows");
    if (lua_isboolean(L, 2)) {  // Begin(name, open [, flags]) -> open, draw
        bool open = lua_toboolean(L, 2);
        bool draw = U->begin(name, &open, uint32_t(luaL_optinteger(L, 3, 0)));
        g_scopes.push_back(Scope::Window);
        lua_pushboolean(L, open);
        lua_pushboolean(L, draw);
        return 2;
    }
    bool draw = U->begin(name, nullptr, uint32_t(luaL_optinteger(L, 2, 0)));
    g_scopes.push_back(Scope::Window);
    lua_pushboolean(L, draw);
    return 1;
}
int End(lua_State* L) {
    need(L);
    if (g_scopes.empty() || g_scopes.back() != Scope::Window) return luaL_error(L, "ImGui.End without a matching Begin (close children first)");
    g_scopes.pop_back();
    U->end();
    return 0;
}
int BeginChild(lua_State* L) {
    need(L);
    if (g_scopes.empty()) return luaL_error(L, "ImGui.BeginChild needs a parent window");
    if (g_scopes.size() >= 32) return luaL_error(L, "too many nested ImGui windows");
    const KilnStr name = arg(L, 1);
    if (!name.len || !name.ptr[0]) return luaL_error(L, "ImGui.BeginChild needs a nonempty id");
    // SET: (id, w, h, border, windowFlags). kiln.ui always draws a child border.
    const uint32_t flags = uint32_t(luaL_optinteger(L, 5, 0));
    bool r = U->begin_child(name, float(luaL_optnumber(L, 2, 0)), float(luaL_optnumber(L, 3, 0)), flags);
    g_scopes.push_back(Scope::Child);
    lua_pushboolean(L, r);
    return 1;
}
int EndChild(lua_State* L) {
    need(L);
    if (g_scopes.empty() || g_scopes.back() != Scope::Child) return luaL_error(L, "ImGui.EndChild without a matching BeginChild");
    g_scopes.pop_back();
    U->end_child();
    return 0;
}
int CollapsingHeader(lua_State* L) {
    need(L);
    lua_pushboolean(L, U->collapsing_header(arg(L, 1), uint32_t(luaL_optinteger(L, 2, 0))));
    return 1;
}
int SetNextWindowPos(lua_State* L) {
    need(L);
    U->set_next_window_pos(float(luaL_checknumber(L, 1)), float(luaL_checknumber(L, 2)), uint32_t(luaL_optinteger(L, 3, 0)), 0, 0);
    return 0;
}
int SetNextWindowSize(lua_State* L) {
    need(L);
    U->set_next_window_size(float(luaL_checknumber(L, 1)), float(luaL_checknumber(L, 2)), uint32_t(luaL_optinteger(L, 3, 0)));
    return 0;
}
int Text(lua_State* L) {
    need(L);
    std::string s;
    for (int i = 1, n = lua_gettop(L); i <= n; ++i) s += luaL_tolstring(L, i, nullptr), lua_pop(L, 1);
    U->text(KilnStr{s.data(), s.size()});
    return 0;
}
int TextColored(lua_State* L) {
    need(L);
    U->text_colored(rgba(L, 1), arg(L, 5));
    return 0;
}
int Separator(lua_State* L) { need(L); U->separator(); return 0; }
int SameLine(lua_State* L) {
    need(L);
    U->same_line(float(luaL_optnumber(L, 1, 0)), float(luaL_optnumber(L, 2, -1)));
    return 0;
}
int Spacing(lua_State* L) { need(L); U->spacing(); return 0; }
int Button(lua_State* L) {
    need(L);
    lua_pushboolean(L, U->button(arg(L, 1), float(luaL_optnumber(L, 2, 0)), float(luaL_optnumber(L, 3, 0))));
    return 1;
}
int Checkbox(lua_State* L) {
    need(L);
    bool v = lua_toboolean(L, 2);
    bool pressed = U->checkbox(arg(L, 1), &v);
    lua_pushboolean(L, v);
    lua_pushboolean(L, pressed);
    return 2;
}
int SliderFloat(lua_State* L) {
    need(L);
    float v = float(luaL_checknumber(L, 2));
    bool used = U->slider_float(arg(L, 1), &v, float(luaL_checknumber(L, 3)), float(luaL_checknumber(L, 4)));
    lua_pushnumber(L, v);
    lua_pushboolean(L, used);
    return 2;
}
int SliderInt(lua_State* L) {
    need(L);
    int32_t v = int32_t(luaL_checkinteger(L, 2));
    bool used = U->slider_int(arg(L, 1), &v, int32_t(luaL_checkinteger(L, 3)), int32_t(luaL_checkinteger(L, 4)));
    lua_pushinteger(L, v);
    lua_pushboolean(L, used);
    return 2;
}
// InputFloat / InputInt: a text field that keeps the number while it parses (kiln.ui has no numeric input).
template <bool Int>
int InputNumber(lua_State* L) {
    need(L);
    char buf[64];
    if (Int) std::snprintf(buf, sizeof buf, "%lld", static_cast<long long>(luaL_checkinteger(L, 2)));
    else std::snprintf(buf, sizeof buf, "%g", luaL_checknumber(L, 2));
    bool changed = U->input_text(arg(L, 1), buf, sizeof buf, 0);
    char* end = nullptr;
    if (Int) {
        long long v = std::strtoll(buf, &end, 10);
        if (changed && end != buf) lua_pushinteger(L, v);
        else lua_pushvalue(L, 2), changed = false;
    } else {
        double v = std::strtod(buf, &end);
        if (changed && end != buf) lua_pushnumber(L, v);
        else lua_pushvalue(L, 2), changed = false;
    }
    lua_pushboolean(L, changed);
    return 2;
}
int InputText(lua_State* L) {
    need(L);
    size_t n = 0;
    const char* s = luaL_checklstring(L, 2, &n);
    const lua_Integer cap = luaL_optinteger(L, 3, 256);
    if (cap < 1 || cap > 65536) return luaL_error(L, "maxLen is 1 to 65536");
    std::string buf(size_t(cap) + 1, '\0');
    std::memcpy(buf.data(), s, std::min(n, size_t(cap)));
    bool changed = U->input_text(arg(L, 1), buf.data(), buf.size(), 0);
    lua_pushstring(L, buf.c_str());
    lua_pushboolean(L, changed);
    return 2;
}
int IsItemHovered(lua_State* L) { need(L); lua_pushboolean(L, U->is_item_hovered()); return 1; }
int SetTooltip(lua_State* L) { need(L); U->set_tooltip(arg(L, 1)); return 0; }

void constants(lua_State* L, const char* name, std::initializer_list<std::pair<const char*, lua_Integer>> kv) {
    lua_createtable(L, 0, int(kv.size()));
    for (auto [k, v] : kv) {
        lua_pushinteger(L, v);
        lua_setfield(L, -2, k);
    }
    lua_setglobal(L, name);
}
}  // namespace

void kset::bindUi(const KilnUiApi* ui) { U = ui; }

void rt::openImGui(lua_State* L) {
    const luaL_Reg fns[] = {{"Begin", Begin}, {"End", End}, {"BeginChild", BeginChild}, {"EndChild", EndChild},
                            {"CollapsingHeader", CollapsingHeader}, {"SetNextWindowPos", SetNextWindowPos},
                            {"SetNextWindowSize", SetNextWindowSize}, {"Text", Text}, {"TextColored", TextColored},
                            {"Separator", Separator}, {"SameLine", SameLine}, {"Spacing", Spacing}, {"Button", Button},
                            {"Checkbox", Checkbox}, {"SliderFloat", SliderFloat}, {"SliderInt", SliderInt},
                            {"InputFloat", InputNumber<false>}, {"InputInt", InputNumber<true>}, {"InputText", InputText},
                            {"IsItemHovered", IsItemHovered}, {"SetTooltip", SetTooltip}, {nullptr, nullptr}};
    lua_newtable(L);
    luaL_setfuncs(L, fns, 0);
    lua_setglobal(L, "ImGui");
    constants(L, "ImGuiCond", {{"Always", 1}, {"Once", 2}, {"FirstUseEver", 4}, {"Appearing", 8}});
    constants(L, "ImGuiWindowFlags", {{"None", 0}, {"NoTitleBar", 1}, {"NoResize", 2}, {"NoMove", 4}, {"NoScrollbar", 8},
                                      {"NoCollapse", 32}, {"AlwaysAutoResize", 64}, {"NoBackground", 128},
                                      {"NoSavedSettings", 256}, {"NoInputs", 512 | (1 << 16) | (1 << 17)}});
}
void rt::imguiMark() { g_scopes.clear(); }
void rt::imguiRecover() {
    if (!U) return;
    while (!g_scopes.empty()) {
        const Scope s = g_scopes.back();
        g_scopes.pop_back();
        if (s == Scope::Child) U->end_child(); else U->end();
    }
}
