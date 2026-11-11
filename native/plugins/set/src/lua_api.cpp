// SPDX-License-Identifier: MIT
// kiln-set: SET's Lua mod API. Adapted from Shrouded Engine Tweaks 0.2.0 runtime/lua_api.cpp,
// Copyright (c) 2026 Cacaida, MIT (licenses/ShroudedEngineTweaks-LICENSE.txt). Kiln's changes: the game layer is
// Kiln's (game.h on kiln.types / kiln.world / kiln.assets), Admin.* runs on Kiln's services (admin.inl), native hooks
// go through kiln.hooks (hooks.h), and the runtime is driven by Kiln's ticks (plugin.cpp).
#include "lua_api.h"

#include "contracts.h"
#include "game.h"
#include "hooks.h"
#include "level.h"
#include "modsettings.h"
#include "inspector.h"
#include "sandbox.h"
#include "writelog.h"
#include "kiln_api.h"
#include "services/kiln_world.h"

#define RUNTIME_VERSION "0.2.0"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <bit>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <filesystem>
#include <cstring>
#include <deque>
#include <fstream>
#include <memory>
#include <unordered_map>
#include <initializer_list>
#include <iterator>
#include <map>
#include <mutex>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

// Lua is compiled as C++ (runtime/CMakeLists.txt), so its headers are included without extern "C".
#include "nlohmann/json.hpp"  // Res.text reads the catalog (texts.inl)
#include "lauxlib.h"
#include "lua.h"
#include "lualib.h"
#include "lua_budget.h"

namespace {

std::string gModsDir;  // UTF-8, like every path this file keeps as text

std::filesystem::path pathOf(const std::string& utf8) { return std::filesystem::path(std::u8string(utf8.begin(), utf8.end())); }

// A mod's folder as UTF-8 text. (path::string() would convert to the ANSI code page and throw for characters it lacks.)
std::string modFolder(const std::string& name) {
    const auto u = (pathOf(gModsDir) / pathOf(name)).u8string();
    return std::string(u.begin(), u.end());
}
// UTF-8 <-> UTF-16 for the Win32 profile (ini) API: its *A forms read paths and keys in the ANSI code page.
std::wstring wideOf(const std::string& s) { return pathOf(s).wstring(); }
std::string utf8Of(const std::wstring& w) {
    const auto u = std::filesystem::path(w).u8string();
    return std::string(u.begin(), u.end());
}
rt::Sink gSink = [](const char*) {};
uintptr_t gWorld;  // server world, only set while Lua runs
lua_State* gL;
std::atomic<bool> gReload, gLoaded;  // gLoaded: a Lua state with the mods exists (the toolbar's Reload Mods)
std::mutex gQueueMx;
std::vector<std::string> gQueue;
std::atomic<bool> gOverlayOpen, gHasDraw;
std::atomic<uint32_t> gBuild;  // the game build (rt::setGameBuild), for GetGameBuild() and mod.json
// What the last loadMods did with each mod folder (rt::modReports, for kiln-set's state/set.json).
std::mutex gReportMx;
std::vector<rt::ModReport> gReports;
void (*gOnReports)() = nullptr;

// Lua runs on the server thread (tick) or the main thread (frame), and on the render thread (Draw). gLuaMx keeps them
// apart; gInDraw marks the render-thread side, where game memory is off limits because the game mutates it
// concurrently.
std::mutex gLuaMx;
bool gInDraw;
bool gInConsole, gHookShutdown;
std::string gCurrentMod;
// Whether gCurrentMod is a mod (its code runs right now), not the console or a chunk outside any mod. A mod's resource
// writes go into the write log (wlog), so they are undone when it stops. Under gLuaMx.
bool gCurrentIsMod = false;
std::string gLastCallError;  // the error text of the last call() that failed (loadMods reports it)
std::map<std::string, uint64_t> gSystemOwners;
// Hooks.observe and Hooks.rule handles. A rule has rule >= 0, no Lua function (sub -1, ref LUA_NOREF), and the names
// of its params in index order.
struct HookHandler {
    int sub, ref;
    std::string mod, name;
    std::vector<hk::Arg> args;
    int rule = -1;
    std::vector<std::string> params;
    bool hasList = false;
};
std::map<std::string, uint32_t> gModOrder;  // the mods' load order (loadMods): rules on one target run by it
std::map<lua_Integer, HookHandler> gHooks;
lua_Integer gNextHook = 0;
std::atomic<bool> gBroken;  // an access violation stopped Lua work: the state is unusable, so no Lua runs again

constexpr const char* kRefMt = "rt.ref";
constexpr const char* kTicks = "rt.ticks";
constexpr const char* kDraws = "rt.draws";
constexpr const char* kKeys = "rt.keys";
constexpr const char* kObservers = "rt.observers";  // event type hash -> list of handlers
std::atomic<bool> gObserving{false};  // set by the first Observe; afterTick returns at once until then
constexpr const char* kFunctionMods = "rt.functionMods"; // function ownership survives load() chunk names
constexpr const char* kMods = "rt.mods";            // mod name -> what its init.lua returned (GetMod)
constexpr const char* kBinds = "rt.binds";          // "<mod>.<id>" -> Keys.hotkey or Keys.input callback
constexpr const char* kRequire = "rt.require";      // makes a mod's require (the kTextOnly prelude returns it)
constexpr const char* kIo = "rt.io";                // the real io library, only for the mods' wrappers
constexpr const char* kOnInit = "rt.onInit";        // registerForEvent lists; onUpdate and onDraw use kTicks and kDraws
constexpr const char* kOnOpen = "rt.onOverlayOpen";
constexpr const char* kOnClose = "rt.onOverlayClose";
constexpr const char* kOnShutdown = "rt.onShutdown";
constexpr const char* kOnEnter = "rt.onWorldEnter";
constexpr const char* kOnLeave = "rt.onWorldLeave";
constexpr const char* kOnChange = "rt.onSettingsChange";  // mod -> its Settings.define onChange
uintptr_t gReady;  // server world whose pre-tick Lua work ran this tick; afterTick only dispatches for it
bool gNewWorld;    // a session started in another world (not a respawn): onWorldEnter at the next ready tick
bool gLeaving;     // onWorldLeave is running: the world is going away
bool gSeenOpen;    // the overlay state the current Lua state last saw, for onOverlayOpen and onOverlayClose
double gDt;        // seconds since the previous tick (or frame, outside a world), for Tick.add and onUpdate
// In a world you host, from its first ready tick until the leave: the server tick runs the mods. Otherwise the main
// thread does (rt::frame).
std::atomic<bool> gInWorld;
std::atomic<bool> gClosing;  // the game is closing (rt::gameClosing): no game memory, and no mods after onShutdown

// A struct or array seen from Lua. Component refs hold no pointer: the address is re-resolved on every access, so an
// entity that moved chunk or lost the component can't leave a dangling pointer. Event refs point at the event in its
// stream and are only valid during the dispatch that created them (gen). Resource refs point at the resource, which
// the game keeps from startup until it exits (gen 0, needAlive); so do game option refs. Owned refs (Types.new,
// Types.copy) point at bytes stored right after the Ref in its own userdata.
struct Ref {
    uint32_t entity;
    int comp;             // component index in the world
    uint32_t off;         // byte offset of this value inside the component, event or resource
    uint32_t limit;       // component, event or resource size, for bounds checks
    const uint8_t* type;  // descriptor of this value
    uint8_t* raw;         // event data, a post scratch buffer, a resource or game options instead of a component
    uint32_t gen;         // raw refs: valid while gGen still equals this; 0 for resources and options, always valid
    bool writable;        // raw refs: the scratch buffers of Events.post and Admin.send, resources and options
    bool plain;           // game options and owned values: ordinary memory, written directly (resources use res::write)
    bool owned;           // Lua owns the bytes: views into them keep the owner alive (refIndex)
    const res::Entry* resource = nullptr; // stable key; resource_data pointers expire at the next tick
};

uint32_t gGen = 1;  // bumped after every event dispatch

std::string qname(refl::Type t) { return std::string(t.qname()); }

// The game frees its options and resources as it exits, so onShutdown then gets files only.
void needAlive(lua_State* L) {
    if (gClosing) luaL_error(L, "the game is closing: onShutdown can't reach it");
}

void needGame(lua_State* L) {
    if (gInDraw) luaL_error(L, "the game is not available in Draw callbacks: read it in Tick and keep it in a variable");
    needAlive(L);
    if (gLeaving) luaL_error(L, "the world is closing: onWorldLeave can't reach the game");
}

// Commands and events go to the world you host, so there has to be one.
void needWorld(lua_State* L) {
    if (!gInWorld) luaL_error(L, "not in a world you host");
}

// Address of [off, off + size) inside r's value, or a Lua error.
uint8_t* addr(lua_State* L, const Ref& r, uint32_t off, uint32_t size, bool write = false) {
    if (uint64_t(off) + size > r.limit) luaL_error(L, "offset %d is outside the value", int(off));
    if (r.resource) {
        needGame(L);
        uint32_t actual = 0;
        auto* p = res::data(*r.resource, &actual);
        if (!p || off > actual || size > actual - off) luaL_error(L, "resource is unavailable or smaller than its reflected view");
        return p + off;
    }
    if (r.raw) {  // not a component: scratch, an event (stale after its dispatch: gGen), a resource, options or owned
        if (!r.owned) needAlive(L);
        if (r.gen && r.gen != gGen) luaL_error(L, "event no longer valid: use it inside its handler only");
        if (write && !r.writable) luaL_error(L, "events are read-only");
        return r.raw + off;
    }
    needGame(L);
    uint32_t actual = 0;
    uint8_t* b = gWorld ? ecs::component(gWorld, r.entity, r.comp, &actual) : nullptr;
    if (b && (off > actual || size > actual - off)) luaL_error(L, "component is smaller than its reflected layout");
    if (!b) luaL_error(L, "entity %d no longer has this component", int(r.entity));
    return b + off;
}

bool isInt(uint8_t k) { return (k >= refl::U8 && k <= refl::S64) || (k >= refl::Enum && k <= refl::Bitmask64); }
bool isSigned(uint8_t k) { return k == refl::S8 || k == refl::S16 || k == refl::S32 || k == refl::S64; }

lua_Integer readInt(const uint8_t* p, uint32_t size, bool sign) {
    uint64_t v = 0;
    if (!size || size > 8) return 0;
    memcpy(&v, p, size);
    if (sign && size < 8 && (v >> (size * 8 - 1) & 1)) v |= ~0ull << (size * 8);  // sign-extend
    return static_cast<lua_Integer>(v);
}

bool fits(lua_Integer v, uint32_t size, bool sign) {
    if (!size || size > 8) return false;
    if (size == 8) return sign || v >= 0;
    int bits = int(size * 8);
    return sign ? v >= -(1ll << (bits - 1)) && v < (1ll << (bits - 1)) : v >= 0 && v < (1ll << bits);
}

using refl::enumHas;

// Single-field wrapper structs like EntityId {id} or Time {value} read and write as their scalar, so e.deadEntityId
// is a number.
using refl::unwrap;

// The user value anchors a view into an owned value to that value's userdata.
void newRef(lua_State* L, const Ref& r) {
    *static_cast<Ref*>(lua_newuserdatauv(L, sizeof(Ref), 1)) = r;
    luaL_setmetatable(L, kRefMt);
}

// A value Lua owns, of type t: its bytes follow the Ref in the userdata, which Lua never moves. Not initialized.
uint8_t* newOwned(lua_State* L, refl::Type t) {
    auto r = static_cast<Ref*>(lua_newuserdatauv(L, sizeof(Ref) + t.size(), 1));
    auto bytes = reinterpret_cast<uint8_t*>(r + 1);
    *r = {0, -1, 0, t.size(), t.p, bytes, 0, true, true, true};
    luaL_setmetatable(L, kRefMt);
    return bytes;
}

void push(lua_State* L, const Ref& r, uint32_t off, refl::Type t);

// Offset of a blob value's data inside r, checked against r's size.
uint32_t blobAt(lua_State* L, const Ref& r, uint32_t off, const res::Blob& b) {
    uint64_t at = uint64_t(off) + b.rel;
    if (at > r.limit) luaL_error(L, "blob data at offset %d + %d is outside the value", int(off), int(b.rel));
    return static_cast<uint32_t>(at);
}

// Blobs (resources only): strings as Lua strings, arrays as proxies, optionals and variants as their value or nil.
void pushBlob(lua_State* L, const Ref& r, uint32_t off, refl::Type u) {
    uint8_t k = u.prim();
    const uint32_t headerSize = k == refl::BlobOptional ? 4 : k == refl::BlobVariant ? 12 : 8;
    if (u.size() < headerSize) luaL_error(L, "invalid blob header size");
    res::Blob b = res::blob(addr(L, r, off, headerSize), k);
    if (k == refl::BlobArray) {  // the proxy reads the header again on every access
        Ref child = r;
        child.off = off;
        child.type = u.p;
        newRef(L, child);
    } else if (k == refl::BlobString) {
        uint32_t at = b.count ? blobAt(L, r, off, b) : off;
        lua_pushlstring(L, reinterpret_cast<const char*>(addr(L, r, at, b.count)), b.count);
    } else if (!b.count) {
        lua_pushnil(L);
    } else {
        refl::Type v = k == refl::BlobVariant ? refl::byHash(b.hash) : u.inner();
        if (!v) luaL_error(L, "%s holds an unknown type (hash 0x%x)", qname(u).c_str(), b.hash);
        push(L, r, blobAt(L, r, off, b), v);
    }
}

void pushGuid(lua_State* L, const uint8_t* p) {
    static constexpr uint8_t kNone[16] = {};
    if (memcmp(p, kNone, 16) == 0) lua_pushnil(L);
    else lua_pushstring(L, res::guidStr(p).c_str());
}

// Pushes the value of type t at offset off of r: scalars as Lua values, references as GUID strings, structs and
// arrays as proxies.
void push(lua_State* L, const Ref& r, uint32_t off, refl::Type t) {
    refl::Type u = unwrap(t.resolved());
    if (!u) luaL_error(L, "invalid or cyclic reflected type");
    uint8_t k = u.prim();
    if (k >= refl::BlobArray && k <= refl::BlobVariant) return pushBlob(L, r, off, u);
    const uint8_t* p = addr(L, r, off, u.size());
    if (k == refl::Bool) lua_pushboolean(L, *p != 0);
    else if (k == refl::F32) { float v; memcpy(&v, p, 4); lua_pushnumber(L, v); }
    else if (k == refl::F64) { double v; memcpy(&v, p, 8); lua_pushnumber(L, v); }
    else if (isInt(k) && u.size() > 0 && u.size() <= 8) {
        lua_Integer value = readInt(p, u.size(), isSigned(k));
        if (k == refl::Enum) {
            uint64_t mask = u.size() == 8 ? UINT64_MAX : (1ull << (u.size() * 8)) - 1;
            refl::forEnum(u, [&](std::string_view, int64_t e) { if ((uint64_t(e) & mask) == (uint64_t(value) & mask)) value = e; });
        }
        lua_pushinteger(L, value);
    }
    else if ((k == refl::ObjectReference || k == refl::Guid) && u.size() == 16) pushGuid(L, p);
    else if (k == refl::Struct || k == refl::StaticArray) {
        Ref child = r;
        child.off = off;
        child.type = u.p;
        newRef(L, child);
    } else {
        // Ds types (the options' strings and lists, the hosting passwords among them): the type name, never the data
        lua_pushfstring(L, "<%s>", qname(u).c_str());
    }
}

// Copies n bytes into r at off. Resources go through res::write, because the game keeps their pages read-only.
void put(lua_State* L, const Ref& r, uint32_t off, const void* src, uint32_t n) {
    uint8_t* p = addr(L, r, off, n, true);
    if (!r.resource && (!r.raw || r.gen || r.plain)) {
        memcpy(p, src, n);
        return;
    }
    // A mod's resource writes go into the write log first, so they are undone when the mod stops; a full log refuses
    // the write. The console's writes are not logged.
    const bool logged = gCurrentIsMod && r.resource;
    if (logged && !wlog::reserve(r.resource, p, off, n, gCurrentMod))
        luaL_error(L, "write log full: this change couldn't be made undoable");
    static bool told;
    uint32_t prot = 0;
    if (!res::write(p, src, n, prot)) luaL_error(L, "resource memory couldn't be made writable");
    if (!(prot & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE)) && !std::exchange(told, true)) {
        char line[96];
        snprintf(line, sizeof line, LV_OK "Resources: The game keeps them read-only (protect 0x%x), each edit lifts that", prot);
        gSink(line);
    }
    if (logged) wlog::wrote(r.resource, off, src, n);
}

lua_Integer enumArg(lua_State* L, int arg, refl::Type t);
void fill(lua_State* L, const Ref& r, uint32_t off, refl::Type t, int idx, uint8_t* mask = nullptr, unsigned depth = 0);

// Copies the view at stack index idx over the value of type u at off of r. Only between values of one type, and only
// fixed-size data (refl::plain): blob and Ds headers point elsewhere.
void copyValue(lua_State* L, const Ref& r, uint32_t off, refl::Type u, int idx) {
    const Ref& s = *static_cast<const Ref*>(lua_touserdata(L, idx));
    if (s.type != u.p) luaL_error(L, "can't assign a %s to a %s", qname(refl::Type{s.type}).c_str(), qname(u).c_str());
    if (!refl::plain(u)) luaL_error(L, "can't copy %s: it holds strings or arrays of variable size", qname(u).c_str());
    std::vector<uint8_t> tmp(u.size());  // the two may overlap
    memcpy(tmp.data(), addr(L, s, s.off, u.size()), u.size());
    if (auto bad = refl::badField(u, tmp.data()); !bad.name.empty()) luaL_error(L, "copied value has invalid scalar field data");
    put(L, r, off, tmp.data(), u.size());
}

// Writes the Lua value at stack index idx into the value of type t at offset off. Type- and range-checked. Enums also
// take their value names; structs take a table of fields, and structs and arrays a view of the same type.
void store(lua_State* L, const Ref& r, uint32_t off, refl::Type t, int idx) {
    kset::budget::check(L);
    refl::Type u = unwrap(t.resolved());
    uint8_t k = u.prim();
    if (k == refl::Bool) {
        luaL_checktype(L, idx, LUA_TBOOLEAN);
        uint8_t b = lua_toboolean(L, idx) ? 1 : 0;
        put(L, r, off, &b, 1);
    } else if (k == refl::F32) {
        double d = luaL_checknumber(L, idx);
        if (!std::isfinite(d) || std::abs(d) > std::numeric_limits<float>::max()) luaL_error(L, "value does not fit float32");
        auto v = static_cast<float>(d);
        put(L, r, off, &v, 4);
    } else if (k == refl::F64) {
        double v = luaL_checknumber(L, idx);
        if (!std::isfinite(v)) luaL_error(L, "float64 must be finite");
        put(L, r, off, &v, 8);
    } else if (isInt(k) && u.size() <= 8) {
        bool named = k == refl::Enum && lua_type(L, idx) == LUA_TSTRING;
        lua_Integer v = named ? enumArg(L, idx, u) : luaL_checkinteger(L, idx);
        if (!(fits(v, u.size(), isSigned(k) || k >= refl::Bitmask8) || (k == refl::Enum && fits(v, u.size(), true)))) luaL_error(L, "%I doesn't fit %s", v, qname(u).c_str());
        if (k == refl::Enum && !enumHas(u, v)) luaL_error(L, "%I is not a value of %s", v, qname(u).c_str());
        kset::budget::check(L);
        put(L, r, off, &v, u.size());
    } else if ((k == refl::ObjectReference || k == refl::Guid) && u.size() == 16) {
        uint8_t g[16] = {};  // nil clears the reference
        size_t n = 0;
        const char* s = lua_isnil(L, idx) ? nullptr : luaL_checklstring(L, idx, &n);
        if (s && !res::parseGuid({s, n}, g)) luaL_error(L, "'%s' is not a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)", s);
        put(L, r, off, g, 16);
    } else if (k >= refl::BlobArray && k <= refl::BlobVariant) {
        luaL_error(L, "can't assign to %s: its size is fixed; set its elements instead", qname(u).c_str());
    } else if (k == refl::Struct && lua_istable(L, idx)) {
        std::vector<uint8_t> bytes(u.size());
        memcpy(bytes.data(), addr(L, r, off, u.size(), true), u.size());
        Ref scratch{0, -1, 0, u.size(), u.p, bytes.data(), 0, true, true, true};
        fill(L, scratch, 0, u, idx);
        put(L, r, off, bytes.data(), u.size());
    } else if ((k == refl::Struct || k == refl::StaticArray) && luaL_testudata(L, idx, kRefMt)) {
        copyValue(L, r, off, u, idx);
    } else {
        luaL_error(L, "can't assign to %s; set its fields instead", qname(u).c_str());
    }
}

// Copies the Lua table at stack index idx into the struct of type t at offset off of r, field by field. A nested
// table fills a nested struct: {transform = {position = {x = 1}}}. A mask, if given, gets 1 for every byte written.
void fill(lua_State* L, const Ref& r, uint32_t off, refl::Type t, int idx, uint8_t* mask, unsigned depth) {
    if (depth > 32) luaL_error(L, "recursive reflected value");
    luaL_checkstack(L, 4, "reflected value is too deeply nested");
    idx = lua_absindex(L, idx);
    lua_pushnil(L);
    while (lua_next(L, idx)) {
        kset::budget::check(L);
        const char* key = lua_type(L, -2) == LUA_TSTRING ? lua_tostring(L, -2) : "?";
        refl::Field f;
        if (!refl::field(t, key, f)) luaL_error(L, "%s has no field '%s'", qname(t).c_str(), key);
        refl::Type ft = f.type.resolved();
        if (!ft || f.offset > t.size() || ft.size() > t.size() - f.offset || off > r.limit || f.offset > r.limit - off || ft.size() > r.limit - off - f.offset)
            luaL_error(L, "field lies outside its reflected value");
        if (lua_istable(L, -1) && ft.prim() == refl::Struct) {
            fill(L, r, off + f.offset, ft, -1, mask, depth + 1);
        } else {
            store(L, r, off + f.offset, f.type, lua_gettop(L));
            if (mask) memset(mask + off + f.offset, 1, ft.size());
        }
        lua_pop(L, 1);
    }
}

uint32_t arrayLen(refl::Type a) {
    uint32_t e = a.inner() ? a.inner().size() : 0;
    return e && a.size() % e == 0 ? a.size() / e : 0;
}

bool isArray(refl::Type t) { return t.prim() == refl::StaticArray || t.prim() == refl::BlobArray; }

// Where r's array elements start and how many there are. A blob array's elements are checked to lie inside r.
std::pair<uint32_t, uint32_t> elements(lua_State* L, const Ref& r) {
    refl::Type t{r.type};
    if (t.prim() == refl::StaticArray) { addr(L, r, r.off, t.size()); return {r.off, arrayLen(t)}; }
    res::Blob b = res::blob(addr(L, r, r.off, 8), refl::BlobArray);
    uint32_t at = b.count ? blobAt(L, r, r.off, b) : r.off;
    if (!t.inner() || !t.inner().size() || at + uint64_t(b.count) * t.inner().size() > r.limit) luaL_error(L, "array elements are outside the value");
    return {at, b.count};
}

// Offset and type of the key at stack index 2 inside r: an array index or a struct field name.
std::pair<uint32_t, refl::Type> member(lua_State* L, const Ref& r) {
    refl::Type t{r.type};
    if (isArray(t)) {
        lua_Integer i = luaL_checkinteger(L, 2);
        auto [start, n] = elements(L, r);
        luaL_argcheck(L, i >= 0 && i < lua_Integer(n), 2, "index out of range");
        return {start + uint32_t(i) * t.inner().size(), t.inner()};
    }
    const char* key = luaL_checkstring(L, 2);
    refl::Field f;
    if (!refl::field(t, key, f)) luaL_error(L, "%s has no field '%s'", qname(t).c_str(), key);
    if (f.offset > t.size() || f.type.size() > t.size() - f.offset || uint64_t(r.off) + f.offset > UINT32_MAX)
        luaL_error(L, "field lies outside its reflected value");
    return {r.off + f.offset, f.type};
}

const Ref& checkRef(lua_State* L) { return *static_cast<Ref*>(luaL_checkudata(L, 1, kRefMt)); }

// A view into an owned value keeps the owner alive: its user value is the owner (1 itself, or 1's own anchor).
int refIndex(lua_State* L) {
    Ref r = checkRef(L);
    auto [off, t] = member(L, r);
    push(L, r, off, t);
    if (r.owned && luaL_testudata(L, -1, kRefMt)) {
        if (lua_getiuservalue(L, 1, 1) == LUA_TNIL) {
            lua_pop(L, 1);
            lua_pushvalue(L, 1);
        }
        lua_setiuservalue(L, -2, 1);
    }
    return 1;
}

int refNewIndex(lua_State* L) {
    Ref r = checkRef(L);
    auto [off, t] = member(L, r);
    store(L, r, off, t, 3);
    return 0;
}

int refLen(lua_State* L) {
    const Ref& r = checkRef(L);
    if (!isArray(refl::Type{r.type})) return luaL_error(L, "# only works on arrays");
    lua_pushinteger(L, elements(L, r).second);
    return 1;
}

// Arrays print their elements, e.g. {60, 0, 100, ...}, the first 50 of them; structs print their type name.
int refToString(lua_State* L) {
    Ref r = checkRef(L);
    refl::Type t{r.type};
    if (!isArray(t)) {
        lua_pushfstring(L, "<%s>", qname(t).c_str());
        return 1;
    }
    auto [start, n] = elements(L, r);
    luaL_Buffer b;
    luaL_buffinit(L, &b);
    luaL_addchar(&b, '{');
    for (uint32_t i = 0; i < n && i < 50; i++) {
        if (i) luaL_addstring(&b, ", ");
        push(L, r, start + i * t.inner().size(), t.inner());
        luaL_tolstring(L, -1, nullptr);
        lua_remove(L, -2);
        luaL_addvalue(&b);
    }
    luaL_addstring(&b, n > 50 ? ", ...}" : "}");
    luaL_pushresult(&b);
    return 1;
}

int ecsGet(lua_State* L) {
    needGame(L);
    auto id = static_cast<uint32_t>(luaL_checkinteger(L, 1));
    const char* name = luaL_checkstring(L, 2);
    refl::Type t = refl::byName(name);
    if (!t || t.prim() != refl::Struct) return luaL_error(L, "unknown component type '%s'", name);
    int idx = gWorld ? ecs::compIndex(gWorld, t.hash()) : -1;
    if (idx < 0 || !ecs::component(gWorld, id, idx)) {
        lua_pushnil(L);
        return 1;
    }
    newRef(L, {id, idx, 0, t.size(), t.p, nullptr, 0, false});
    return 1;
}

// Limit: walks the whole entity map per call; mods should keep the id instead of calling this every tick.
int ecsPlayer(lua_State* L) {
    needGame(L);
    refl::Type pi = refl::byName("PlayerInput");
    int idx = gWorld && pi ? ecs::compIndex(gWorld, pi.hash()) : -1;
    uint32_t id = ecs::firstWith(gWorld, idx);
    if (id) lua_pushinteger(L, id);
    else lua_pushnil(L);
    return 1;
}

// The iterator Ecs.each returns. Upvalues: the ids, the position, then per component its descriptor and index.
int eachNext(lua_State* L) {
    lua_Integer pos = lua_tointeger(L, lua_upvalueindex(2)) + 1;
    lua_pushinteger(L, pos);
    lua_replace(L, lua_upvalueindex(2));
    if (lua_rawgeti(L, lua_upvalueindex(1), pos) == LUA_TNIL) return 1;  // nil ends the loop
    auto id = static_cast<uint32_t>(lua_tointeger(L, -1));
    int n = 0;
    for (int u = 3; lua_type(L, lua_upvalueindex(u)) != LUA_TNONE; u += 2, n++) {
        refl::Type t{static_cast<const uint8_t*>(lua_touserdata(L, lua_upvalueindex(u)))};
        newRef(L, {id, int(lua_tointeger(L, lua_upvalueindex(u + 1))), 0, t.size(), t.p, nullptr, 0, false});
    }
    return 1 + n;
}

// Ecs.each(name, ...): for id, comp, ... in Ecs.each("Health", "Faction"), over the entities that have all of them.
// Limit: one walk of the whole entity map per call; a cached archetype walk if mods need it every tick.
int ecsEach(lua_State* L) {
    needGame(L);
    int n = lua_gettop(L);
    luaL_argcheck(L, n >= 1 && n <= 8, 1, "give 1 to 8 component names");
    std::vector<int> idx;
    for (int i = 1; i <= n; i++) {
        const char* name = luaL_checkstring(L, i);
        refl::Type t = refl::byName(name);
        if (!t || t.prim() != refl::Struct) return luaL_error(L, "unknown component type '%s'", name);
        idx.push_back(gWorld ? ecs::compIndex(gWorld, t.hash()) : -1);
        lua_pushlightuserdata(L, const_cast<uint8_t*>(t.p));
        lua_replace(L, i);
    }
    std::vector<uint32_t> ids = gWorld ? ecs::allWith(gWorld, idx.data(), idx.size()) : std::vector<uint32_t>();
    lua_createtable(L, int(ids.size()), 0);
    for (size_t i = 0; i < ids.size(); i++) {
        lua_pushinteger(L, ids[i]);
        lua_rawseti(L, -2, lua_Integer(i) + 1);
    }
    lua_pushinteger(L, 0);
    for (int i = 1; i <= n; i++) {
        lua_pushvalue(L, i);
        lua_pushinteger(L, idx[i - 1]);
    }
    lua_pushcclosure(L, eachNext, 2 + 2 * n);
    return 1;
}

int ecsFields(lua_State* L) {
    const char* name = luaL_checkstring(L, 1);
    refl::Type t = refl::lookup(name);
    if (!t) return luaL_error(L, "unknown type '%s'", name);
    refl::forFields(t, [](const refl::Field& f) {
        char line[512];
        snprintf(line, sizeof line, "  +0x%-4x %.*s: %.*s", f.offset, int(f.name.size()), f.name.data(),
                 int(f.type.qname().size()), f.type.qname().data());
        gSink(line);
    });
    return 0;
}

std::string lower(std::string_view s) {
    std::string out(s);
    for (char& c : out) c = char(tolower(static_cast<unsigned char>(c)));
    return out;
}

// Prints reflected type names containing the text (case-insensitive), at most 100.
int ecsTypes(lua_State* L) {
    std::string sub = lower(luaL_checkstring(L, 1));
    size_t found = 0;
    for (size_t i = 0; i < refl::count(); i++) {
        std::string q = qname(refl::at(i));
        if (lower(q).find(sub) != std::string::npos && ++found <= 100) gSink(q.c_str());
    }
    if (found > 100) gSink(("... and " + std::to_string(found - 100) + " more").c_str());
    return 0;
}

// ---- Res: the game's resources (game.h, namespace res). Static memory, so Draw callbacks may read them too ----

void needRes(lua_State* L) {
    needGame(L);
    if (!kset::tickThread()) luaL_error(L, "resource access requires the tick thread; keep copies for Draw");
    if (!res::ready()) luaL_error(L, "resources are not available (yet): see set.log");
}

refl::Type resType(lua_State* L, int arg) {
    const char* name = luaL_checkstring(L, arg);
    refl::Type t = res::typeByName(name);
    if (!t) luaL_error(L, "unknown resource type '%s' (Res.types lists them)", name);
    return t;
}

// Writable: a mod's edit lasts until the mod stops (the write log), the console's until the game closes.
void pushRes(lua_State* L, const res::Entry& e) {
    uint32_t size = 0;
    if (!res::data(e, &size)) { lua_pushnil(L); return; }
    Ref r{0, -1, 0, size, refl::byHash(e.type).p, nullptr, 0, true};
    r.resource = &e;
    newRef(L, r);
}

// Res.get(guid [, type [, part]]): without a type, the first resource with that GUID.
int resGet(lua_State* L) {
    needRes(L);
    size_t n;
    const char* s = luaL_checklstring(L, 1, &n);
    uint8_t g[16];
    if (!res::parseGuid({s, n}, g)) return luaL_argerror(L, 1, "not a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)");
    uint32_t type = lua_isnoneornil(L, 2) ? 0 : resType(L, 2).hash();
    const res::Entry* e = res::find(g, type, static_cast<uint32_t>(luaL_optinteger(L, 3, 0)));
    if (e && refl::byHash(e->type)) pushRes(L, *e);
    else lua_pushnil(L);
    return 1;
}

// The iterator Res.each returns. Upvalues: the type hash and the position in res::ofType.
int resEachNext(lua_State* L) {
    const auto& list = res::ofType(static_cast<uint32_t>(lua_tointeger(L, lua_upvalueindex(1))));
    lua_Integer pos = lua_tointeger(L, lua_upvalueindex(2));
    if (pos >= lua_Integer(list.size())) return 0;
    lua_pushinteger(L, pos + 1);
    lua_replace(L, lua_upvalueindex(2));
    const res::Entry& e = *list[size_t(pos)];
    lua_pushstring(L, res::guidStr(e.guid).c_str());
    pushRes(L, e);
    lua_pushinteger(L, e.part);
    return 3;
}

// Res.each(type): for guid, res, part in Res.each("ItemInfo") do ... end
int resEach(lua_State* L) {
    needRes(L);
    lua_pushinteger(L, resType(L, 1).hash());
    lua_pushinteger(L, 0);
    lua_pushcclosure(L, resEachNext, 2);
    return 1;
}

// The first resource of the type whose debugName, name or English name is want (lowercase), or null.
const res::Entry* findNamed(uint32_t type, const std::string& want) {
    for (const res::Entry* e : res::ofType(type))
        if (lower(res::label(*e)) == want || lower(res::nameText(*e)) == want) return e;
    return nullptr;
}

std::string setDisplayText(const uint8_t g[16]);  // admin.inl
std::string setLocaText(const uint8_t g[16]);      // texts.inl, via admin.inl

// Res.text(guid): the English text a LocaTag reference (item.name, ...) names, or the English name of an item or a
// template by its GUID (Kiln's catalogs, admin.inl), or the text of any other loca tag (the client catalog's table,
// texts.inl: a dedicated server has no loca of its own); nil if none. Looked up only when called.
int resText(lua_State* L) {
    size_t n;
    const char* s = luaL_checklstring(L, 1, &n);
    uint8_t g[16];
    if (!res::parseGuid({s, n}, g)) return luaL_argerror(L, 1, "not a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)");
    needAlive(L);
    std::string_view t = kset::tickThread() ? res::text(g) : std::string_view{};  // the localization table (not ported yet: empty)
    if (!t.empty()) {
        lua_pushlstring(L, t.data(), t.size());
        return 1;
    }
    std::string d = setDisplayText(g);
    if (d.empty() && kset::tickThread()) d = setLocaText(g);  // any other tag: the English table (texts.inl), loaded on the first such call
    if (d.empty()) lua_pushnil(L);
    else lua_pushlstring(L, d.data(), d.size());
    return 1;
}

// Res.find(type, name): guid and resource of the first whose debugName, name or English name matches, ignoring case;
// nil if none.
int resFind(lua_State* L) {
    needRes(L);
    const res::Entry* e = findNamed(resType(L, 1).hash(), lower(luaL_checkstring(L, 2)));
    if (!e) {
        lua_pushnil(L);
        return 1;
    }
    lua_pushstring(L, res::guidStr(e->guid).c_str());
    pushRes(L, *e);
    return 2;
}

// Res.types([text]): prints the resource types whose name contains the text (case-insensitive), with their counts.
int resTypes(lua_State* L) {
    needRes(L);
    std::string sub = lower(luaL_optstring(L, 1, ""));
    for (const auto& [t, n] : res::types()) {
        std::string q = qname(t);
        if (lower(q).find(sub) != std::string::npos) gSink((q + "  " + std::to_string(n)).c_str());
    }
    return 0;
}

// ---- GameOptions: the game's settings, live (game.h, namespace opts). They last as long as the game, so Draw
// callbacks may use them too ----

int pushOptions(lua_State* L, uint8_t* p, refl::Type t, const char* what) {
    needAlive(L);
    if (!p || !t) return luaL_error(L, "%s not found yet: see set.log", what);
    newRef(L, {0, -1, 0, t.size(), t.p, p, 0, true, true});
    return 1;
}

int optsMachine(lua_State* L) { return pushOptions(L, opts::machine(), opts::machineType(), "game options"); }
int optsUser(lua_State* L) { return pushOptions(L, opts::user(), opts::userType(), "user settings"); }

// GameOptions.dump([prefix]): prints "machine.graphics.vsync = false" lines, those whose path starts with the prefix;
// returns how many. Hosting passwords and banned accounts are never printed.
int optsDump(lua_State* L) {
    std::string_view prefix = luaL_optstring(L, 1, "");
    needAlive(L);
    if (!opts::ready()) return luaL_error(L, "game options not found yet: see set.log");
    lua_pushinteger(L, lua_Integer(opts::dump(prefix, [](const std::string& s) { gSink(s.c_str()); })));
    return 1;
}

// ---- Systems: the game's ECS systems, switched off by name (game.h, namespace sys). Flags only, no game memory, so
// Draw callbacks may use them too ----

std::atomic<bool> gSysOk;  // the program runner is hooked, after sys::scan: the overlay reads sys:: only once it is

// Systems.list([search]): the names holding search (case-insensitive), sorted.
int systemsList(lua_State* L) {
    std::string sub = lower(luaL_optstring(L, 1, ""));
    lua_newtable(L);
    lua_Integer i = 0;
    for (const sys::System& s : sys::all()) {
        if (s.name.find(sub) == std::string_view::npos) continue;
        lua_pushlstring(L, s.name.data(), s.name.size());
        lua_rawseti(L, -2, ++i);
    }
    return 1;
}

const sys::System& systemArg(lua_State* L) {
    const char* name = luaL_checkstring(L, 1);
    if (!gSysOk) luaL_error(L, "Systems is unavailable in this game build: see set.log");
    const sys::System* s = sys::find(name);
    if (!s) luaL_error(L, "unknown system '%s' (Systems.list searches)", name);
    return *s;
}

// Systems.enable(name, on): false makes the game skip that system in every world until it's enabled again.
int systemsEnable(lua_State* L) {
    const sys::System& s = systemArg(L);
    luaL_checktype(L, 2, LUA_TBOOLEAN);
    const auto owner = static_cast<uint64_t>(lua_tointeger(L, lua_upvalueindex(1)));
    if (!owner || sys::setOn(s, lua_toboolean(L, 2), owner) != 0)
        return luaL_error(L, "system switching refused: only explicitly permitted gameplay systems may be disabled");
    return 0;
}

int systemsEnabled(lua_State* L) {
    lua_pushboolean(L, sys::isOn(systemArg(L)));
    return 1;
}

// Systems.profile(on): true starts the profiler over from zero, false stops it.
int systemsProfile(lua_State* L) {
    luaL_checktype(L, 1, LUA_TBOOLEAN);
    if (!gSysOk) return luaL_error(L, "Systems is unavailable in this game build: see set.log");
    sys::setProfiling(lua_toboolean(L, 1));
    return 0;
}

// Systems.stats(): {{name, ms, calls, world}, ...} since the profiler started, most ms first, and the seconds measured.
int systemsStats(lua_State* L) {
    lua_newtable(L);
    lua_Integer i = 0;
    for (const sys::Stat& s : sys::stats()) {
        lua_createtable(L, 0, 4);
        lua_pushlstring(L, s.sys->name.data(), s.sys->name.size());
        lua_setfield(L, -2, "name");
        lua_pushnumber(L, s.ms);
        lua_setfield(L, -2, "ms");
        lua_pushinteger(L, lua_Integer(s.calls));
        lua_setfield(L, -2, "calls");
        lua_pushstring(L, sys::worldName(s.kinds));
        lua_setfield(L, -2, "world");
        lua_rawseti(L, -2, ++i);
    }
    lua_pushnumber(L, sys::profiledSecs());
    return 2;
}

// ---- Types: the reflected types, and values Lua owns. No game memory, so Draw callbacks may use them too ----

refl::Type typeArg(lua_State* L, int arg) {
    const char* name = luaL_checkstring(L, arg);
    refl::Type t = refl::lookup(name).resolved();
    if (!t) luaL_error(L, "unknown type '%s' (Ecs.types searches; a name several types share needs the full one)", name);
    return t;
}

// Types.fields(name): {{name = "maxStackSize", type = "keen::uint16", offset = 20}, ...}, base classes first.
int typesFields(lua_State* L) {
    refl::Type t = typeArg(L, 1);
    lua_newtable(L);
    lua_Integer i = 0;
    refl::forFields(t, [&](const refl::Field& f) {
        lua_createtable(L, 0, 3);
        lua_pushlstring(L, f.name.data(), f.name.size());
        lua_setfield(L, -2, "name");
        lua_pushstring(L, qname(f.type).c_str());
        lua_setfield(L, -2, "type");
        lua_pushinteger(L, f.offset);
        lua_setfield(L, -2, "offset");
        lua_rawseti(L, -2, ++i);
    });
    return 1;
}

// Types.enum(name): {None = 0, Fxaa = 1, ...}
int typesEnum(lua_State* L) {
    refl::Type t = typeArg(L, 1);
    if (t.prim() != refl::Enum) return luaL_error(L, "%s is not an enum", qname(t).c_str());
    lua_newtable(L);
    refl::forEnum(t, [&](std::string_view name, int64_t v) {
        lua_pushinteger(L, v);
        lua_setfield(L, -2, std::string(name).c_str());
    });
    return 1;
}

void needPlain(lua_State* L, refl::Type t) {
    if (t.prim() != refl::Struct && t.prim() != refl::StaticArray)
        luaL_error(L, "%s is not a struct or array", qname(t).c_str());
    if (!refl::plain(t))
        luaL_error(L, "%s holds strings or arrays of variable size: only fixed-size values can be made", qname(t).c_str());
}

// Types.new(name [, values]): a struct or array Lua owns, holding the game's default value (zeros if the type has
// none), then the fields in values.
int typesNew(lua_State* L) {
    refl::Type t = typeArg(L, 1);
    needPlain(L, t);
    if (!lua_isnoneornil(L, 2)) luaL_checktype(L, 2, LUA_TTABLE);
    uint8_t* b = newOwned(L, t);
    if (const uint8_t* d = t.defaults()) memcpy(b, d, t.size());
    else memset(b, 0, t.size());
    if (lua_istable(L, 2)) fill(L, *static_cast<const Ref*>(lua_touserdata(L, -1)), 0, t, 2);
    return 1;
}

// Types.copy(view): a copy Lua owns of a struct or array, e.g. to put back later.
int typesCopy(lua_State* L) {
    const Ref& s = checkRef(L);
    refl::Type t{s.type};
    needPlain(L, t);
    const uint8_t* src = addr(L, s, s.off, t.size());
    memcpy(newOwned(L, t), src, t.size());
    return 1;
}

int ownedCallback(lua_State* L) {
    const int n = lua_gettop(L);
    lua_pushvalue(L, lua_upvalueindex(1)); lua_insert(L, 1);
    lua_call(L, n, LUA_MULTRET);
    return lua_gettop(L);
}
void markFunction(lua_State* L, int index, const std::string& mod = gCurrentMod) {
    index = lua_absindex(L, index);
    // Each registration has its own identity: two mods may legitimately register the same exported function.
    lua_pushvalue(L, index); lua_pushcclosure(L, ownedCallback, 1); lua_replace(L, index);
    lua_getfield(L, LUA_REGISTRYINDEX, kFunctionMods);
    lua_pushvalue(L, index); lua_pushlstring(L, mod.data(), mod.size()); lua_rawset(L, -3);
    lua_pop(L, 1);
}

// Appends fn (argument 1) to a callback list in the registry: Tick.add and Draw.add.
void addCallback(lua_State* L, const char* reg) {
    luaL_checktype(L, 1, LUA_TFUNCTION);
    markFunction(L, 1);
    lua_getfield(L, LUA_REGISTRYINDEX, reg);
    lua_pushvalue(L, 1);
    lua_rawseti(L, -2, lua_Integer(lua_rawlen(L, -2)) + 1);
}

int tickAdd(lua_State* L) {
    addCallback(L, kTicks);
    return 0;
}

int drawAdd(lua_State* L) {
    addCallback(L, kDraws);
    gHasDraw = true;
    return 0;
}

// The lifecycle event names and the callback lists behind them.
constexpr std::pair<const char*, const char*> kLifecycle[] = {
    {"onInit", kOnInit},         {"onUpdate", kTicks},          {"onDraw", kDraws},
    {"onOverlayOpen", kOnOpen}, {"onOverlayClose", kOnClose}, {"onShutdown", kOnShutdown},
    {"onWorldEnter", kOnEnter}, {"onWorldLeave", kOnLeave}};

// registerForEvent(name, fn): onUpdate is Tick.add and onDraw is Draw.add.
int registerForEvent(lua_State* L) {
    const char* name = luaL_checkstring(L, 1);
    luaL_checktype(L, 2, LUA_TFUNCTION);
    auto ev = std::find_if(std::begin(kLifecycle), std::end(kLifecycle), [&](const auto& e) { return strcmp(e.first, name) == 0; });
    if (ev == std::end(kLifecycle))
        return luaL_argerror(L, 1, "unknown event (onInit, onUpdate, onDraw, onOverlayOpen, onOverlayClose, onShutdown, "
                                   "onWorldEnter, onWorldLeave)");
    lua_remove(L, 1);
    addCallback(L, ev->second);
    if (ev->second == kDraws) gHasDraw = true;
    return 0;
}

int overlayIsOpen(lua_State* L) {
    lua_pushboolean(L, gOverlayOpen);
    return 1;
}

int keysOn(lua_State* L) {
    int vk = rt::keyFromName(luaL_checkstring(L, 1));
    luaL_argcheck(L, vk != 0, 1, "unknown key (use F1-F24, A-Z, 0-9, a name like Space, Enter, Left, Num0 or Semicolon, or 0xNN)");
    luaL_checktype(L, 2, LUA_TFUNCTION);
    markFunction(L, 2);
    lua_getfield(L, LUA_REGISTRYINDEX, kKeys);
    lua_pushvalue(L, 2);
    lua_rawseti(L, -2, vk);
    return 0;
}

// A hotkey or input a mod registered. Its key combination comes from bindings.ini, set in the overlay's Bindings
// window, which reads and edits these from the render thread: gBindMx guards gBinds and gSaved.
struct Binding {
    std::string key, mod, label;  // key = "<mod>.<id>"
    bool input;
    int mods, vk;  // rt::KeyMod bits and the key; vk 0 = unbound
    bool held;     // the combination was held at the last poll
};
std::mutex gBindMx;
std::vector<Binding> gBinds;                // registered by the current Lua state
std::map<std::string, std::string> gSaved;  // bindings.ini: key -> combination text

constexpr const char* kModNames[] = {"Ctrl", "Shift", "Alt"};  // rt::ModCtrl, ModShift, ModAlt

Binding* findBind(const std::string& key) {
    for (Binding& b : gBinds)
        if (b.key == key) return &b;
    return nullptr;
}

std::string comboName(int mods, int vk) {
    std::string s;
    for (int i = 0; i < 3; i++)
        if (mods & (1 << i)) (s += kModNames[i]) += '+';
    return s + rt::keyName(vk);
}

// "Ctrl+Shift+F6" -> modifiers and key; false if any part is unknown.
bool parseCombo(const std::string& s, int& mods, int& vk) {
    mods = vk = 0;
    size_t i = 0;
    for (size_t plus; (plus = s.find('+', i)) != std::string::npos; i = plus + 1) {
        int bit = 0;
        for (int m = 0; m < 3; m++)
            if (_stricmp(s.substr(i, plus - i).c_str(), kModNames[m]) == 0) bit = 1 << m;
        if (!bit) return false;
        mods |= bit;
    }
    vk = rt::keyFromName(s.substr(i).c_str());
    return vk != 0;
}

// shrouded_engine_tweaks\bindings.ini, next to the mods folder. Empty without a mods folder.
std::string bindsPath() {
    size_t slash = gModsDir.find_last_of('\\');
    return slash == std::string::npos ? "" : gModsDir.substr(0, slash) + "\\bindings.ini";
}

// For every new Lua state, so reload() also picks up edits made by hand.
void loadBindings() {
    std::lock_guard lock(gBindMx);
    gBinds.clear();
    gSaved.clear();
    std::string path = bindsPath();
    if (path.empty()) return;
    const std::wstring file = wideOf(path);
    std::vector<wchar_t> keys(32 * 1024);
    DWORD n = GetPrivateProfileStringW(L"bindings", nullptr, L"", keys.data(), static_cast<DWORD>(keys.size()), file.c_str());
    for (const wchar_t* k = keys.data(); k < keys.data() + n && *k; k += wcslen(k) + 1) {
        wchar_t combo[64];
        GetPrivateProfileStringW(L"bindings", k, L"", combo, 64, file.c_str());
        gSaved[utf8Of(k)] = utf8Of(combo);
    }
}

// Keys.hotkey(id, label, fn) and Keys.input(id, label, fn), one closure per mod: upvalue 1 is the mod's name, 2 whether
// it registers an input. Registering an id again replaces it.
int keysBind(lua_State* L) {
    std::string mod = lua_tostring(L, lua_upvalueindex(1)), id = luaL_checkstring(L, 1);
    bool input = lua_toboolean(L, lua_upvalueindex(2));
    const char* label = luaL_checkstring(L, 2);
    luaL_checktype(L, 3, LUA_TFUNCTION);
    markFunction(L, 3, mod);
    bool okId = !id.empty() && std::all_of(id.begin(), id.end(), [](char c) {
        return std::isalnum(static_cast<unsigned char>(c)) || c == '_';
    });
    luaL_argcheck(L, okId, 1, "use letters, digits and _ only");
    std::string key = mod + "." + id;
    lua_getfield(L, LUA_REGISTRYINDEX, kBinds);
    lua_pushvalue(L, 3);
    lua_setfield(L, -2, key.c_str());
    std::lock_guard lock(gBindMx);
    Binding* b = findBind(key);
    if (!b) b = &gBinds.emplace_back(Binding{key, mod, "", false, 0, 0, false});
    b->label = label;
    b->input = input;
    auto saved = gSaved.find(key);
    if (saved == gSaved.end() || !parseCombo(saved->second, b->mods, b->vk)) b->mods = b->vk = 0;
    return 0;
}

int luaPrint(lua_State* L) {
    int n = lua_gettop(L);
    luaL_Buffer b;
    luaL_buffinit(L, &b);
    for (int i = 1; i <= n; i++) {
        if (i > 1) luaL_addchar(&b, '\t');
        luaL_tolstring(L, i, nullptr);
        luaL_addvalue(&b);
    }
    luaL_pushresult(&b);
    gSink(lua_tostring(L, -1));
    return 0;
}

int luaReload(lua_State*) {
    gReload = true;  // applied after this tick's Lua work, outside the running state
    return 0;
}

int traceback(lua_State* L) {
    luaL_traceback(L, L, lua_tostring(L, 1), 1);
    return 1;
}

std::string modOf(lua_State* L, int idx, bool* isMod = nullptr);
void discardAdmin(const std::string& mod);

// pcall with a traceback; errors go to the sink. The function and nargs arguments must be on the stack.
bool call(lua_State* L, int nargs, int nres) {
    kset::budget::Scope budget;
    int base = lua_gettop(L) - nargs;
    struct ModScope {
        std::string prior;
        bool priorIsMod;
        ModScope(std::string mod, bool isMod)
            : prior(std::exchange(gCurrentMod, std::move(mod))), priorIsMod(std::exchange(gCurrentIsMod, isMod)) {}
        ~ModScope() { gCurrentMod = std::move(prior); gCurrentIsMod = priorIsMod; }
    };
    bool isMod = false;
    std::string owner = modOf(L, base, &isMod);
    ModScope scope(std::move(owner), isMod);
    lua_pushcfunction(L, traceback);
    lua_insert(L, base);
    int rc = lua_pcall(L, nargs, nres, base);
    lua_remove(L, base);
    if (rc == LUA_OK) return true;
    discardAdmin(gCurrentMod);
    const char* error = lua_tostring(L, -1);
    gLastCallError = error ? error : "Lua error";
    gSink((LV_BAD + gLastCallError).c_str());
    lua_pop(L, 1);
    return false;
}

void clearFields(lua_State* L, const char* table, std::initializer_list<const char*> names) {
    lua_getglobal(L, table);
    for (const char* n : names) {
        lua_pushnil(L);
        lua_setfield(L, -2, n);
    }
    lua_pop(L, 1);
}

// Pushes the chunk of a mod's Lua file, or an error message: text only, named chunk ("@hello/init.lua") so error lines
// start with the mod's own path. A UTF-8 BOM and a first line starting with # are skipped, as luaL_loadfilex does.
bool loadText(lua_State* L, const std::string& root, const std::string& path, const char* chunk) {
    FILE* f = kset::sandbox::open(root, path, "rb");
    std::string text;
    char buffer[4096];
    bool ok = f != nullptr;
    if (f) {
        for (size_t n; (n = fread(buffer, 1, sizeof buffer, f));) {
            if (text.size() + n > (8u << 20)) { ok = false; break; }
            text.append(buffer, n);
        }
        ok = ok && !ferror(f);
        fclose(f);
    }
    if (!ok) { lua_pushfstring(L, "cannot open %s", chunk + 1); return false; }
    if (text.starts_with("\xEF\xBB\xBF")) text.erase(0, 3);
    if (text.starts_with('#')) text.erase(0, std::min(text.find('\n'), text.size()));
    return luaL_loadbufferx(L, text.data(), text.size(), chunk, "t") == LUA_OK;
}
int luaLoadText(lua_State* L) {
    if (!loadText(L, luaL_checkstring(L, 1), luaL_checkstring(L, 2), luaL_checkstring(L, 3))) {
        luaL_pushfail(L); lua_insert(L, -2); return 2;
    }
    if (!lua_isnoneornil(L, 4)) { lua_pushvalue(L, 4); lua_setupvalue(L, -2, 1); }
    return 1;
}
// Crafted bytecode can corrupt memory and escape the sandbox, so load and require accept source text only. The global
// require searches a copy of package.path: a mod that changes package.path can't point it at other files.
// Returns the factory for a mod's own require: names resolve inside its folder (dots become \, so ".." can't
// survive) and run in its environment, cached per mod. Its chunks are named like "@hello/util.lua" (luaLoadText).
const char* kTextOnly = R"lua(
    local loadtext = ...
    local load, error, type, pcall = load, error, type, pcall
    _G.load = function(chunk, name, _, ...) return load(chunk, name, "t", ...) end
    return function(env, dir, mod)
        local cache, loading = {}, {}
        return function(name)
            if type(name) ~= "string" or not name:match("^[%w_%-]+[%.%w_%-]*$") or name:find("..", 1, true) or name:sub(-1) == "." then
                error("require name must stay inside the mod's folder", 2)
            end
            if cache[name] == nil then
                if loading[name] then error("require cycle: '" .. name .. "' is still loading", 2) end
                local path, chunk = name:gsub("%.", "/"), "@" .. mod .. "/" .. name:gsub("%.", "/")
                local fn, err = loadtext(dir, path .. ".lua", chunk .. ".lua", env)
                if not fn and err:find("^cannot open") then fn, err = loadtext(dir, path .. "/init.lua", chunk .. "/init.lua", env) end
                if not fn then error(err, 2) end
                loading[name] = true
                local ok, r = pcall(fn, name)
                loading[name] = nil
                if not ok then error(r, 0) end
                cache[name] = r == nil and true or r
            end
            return cache[name]
        end
    end
)lua";

// Mods are third-party code, so the state gets no file, process or native-code access:
// no debug, dofile/loadfile, os beyond time functions, and io only inside a mod's own folder (pushModEnv). The global
// require only finds Lua files in the mods folder; mods get their own (kTextOnly).
void openSafeLibs(lua_State* L) {
    const luaL_Reg libs[] = {{LUA_GNAME, luaopen_base},        {LUA_LOADLIBNAME, luaopen_package},
                             {LUA_COLIBNAME, luaopen_coroutine}, {LUA_TABLIBNAME, luaopen_table},
                             {LUA_STRLIBNAME, luaopen_string},   {LUA_MATHLIBNAME, luaopen_math},
                             {LUA_UTF8LIBNAME, luaopen_utf8},    {LUA_OSLIBNAME, luaopen_os}};
    for (const luaL_Reg& l : libs) {
        luaL_requiref(L, l.name, l.func, 1);
        lua_pop(L, 1);
    }
    clearFields(L, "os", {"execute", "remove", "rename", "tmpname", "exit", "getenv", "setlocale"});
    clearFields(L, "package", {"loadlib"});
    lua_getglobal(L, "package");
    lua_pushstring(L, gModsDir.empty() ? "" : (gModsDir + "\\?.lua;" + gModsDir + "\\?\\init.lua").c_str());
    lua_setfield(L, -2, "path");
    lua_pushstring(L, "");
    lua_setfield(L, -2, "cpath");
    lua_getfield(L, -1, "searchers");  // keep 1 (preload) and 2 (Lua files); drop the two C-library searchers
    for (int i = 4; i >= 3; i--) {
        lua_pushnil(L);
        lua_rawseti(L, -2, i);
    }
    lua_pop(L, 2);
    kset::budget::install(L);
    luaL_loadstring(L, kTextOnly);  // after package.path is set: it keeps a copy
    lua_pushcfunction(L, luaLoadText);
    lua_call(L, 1, 1);
    lua_setfield(L, LUA_REGISTRYINDEX, kRequire);
    lua_pushcfunction(L, luaopen_io);  // called directly: luaL_requiref would put it in package.loaded for require("io")
    lua_call(L, 0, 1);
    lua_setfield(L, LUA_REGISTRYINDEX, kIo);
    luaL_getmetatable(L, LUA_FILEHANDLE); lua_pushboolean(L, 0); lua_setfield(L, -2, "__metatable"); lua_pop(L, 1);
    clearFields(L, "_G", {"dofile", "loadfile"});
    clearFields(L, "string", {"dump"});
    clearFields(L, "_G", {"package", "require"});
}

// ---- Settings.define (adapted from SET 0.2.0 runtime/lua_api.cpp; values live in modsettings.cpp) ----

// True for the runtime's own files in a mod's folder (settings.json and its .tmp): the mod's io reads them, but
// doesn't write them. Windows drops trailing dots and spaces, so "settings.json." is the same file.
bool setOwned(std::string_view path) {
    if (path.find_first_of("/\\") != std::string_view::npos) return false;  // in a subfolder
    while (!path.empty() && (path.back() == '.' || path.back() == ' ')) path.remove_suffix(1);
    std::string l = lower(path);
    return l == "settings.json" || l == "settings.json.tmp";
}

constexpr const char* kSettingTypes[] = {"bool", "number", "int", "choice", "text", nullptr};  // mset::Type order
constexpr const char* kSettingWant[] = {"a boolean", "a finite number", "an integer", "one of its values", "a string"};

// A setting's value at idx, checked against f's type, into num and text (a choice gives its index). False if it has
// the wrong type; the range is mset::assign's.
bool settingFrom(lua_State* L, int idx, const mset::Field& f, double& num, std::string& text) {
    int t = lua_type(L, idx);
    if (f.type == mset::Bool) {
        num = lua_toboolean(L, idx);
        return t == LUA_TBOOLEAN;
    }
    if (f.type == mset::Number || f.type == mset::Int) {
        int isInt = 1;
        num = f.type == mset::Int ? static_cast<double>(lua_tointegerx(L, idx, &isInt)) : lua_tonumber(L, idx);
        return t == LUA_TNUMBER && isInt && std::isfinite(num);
    }
    if (t != LUA_TSTRING) return false;
    size_t n;
    const char* p = lua_tolstring(L, idx, &n);
    text.assign(p, n);
    if (f.type == mset::Text) return true;
    auto it = std::find(f.values.begin(), f.values.end(), text);
    num = static_cast<double>(it - f.values.begin());
    return it != f.values.end();
}

void pushSetting(lua_State* L, const mset::Field& f) {
    switch (f.type) {
    case mset::Bool: lua_pushboolean(L, f.num != 0); break;
    case mset::Number: lua_pushnumber(L, f.num); break;
    case mset::Int: lua_pushinteger(L, static_cast<lua_Integer>(f.num)); break;
    case mset::Choice: lua_pushstring(L, f.values[static_cast<size_t>(f.num)].c_str()); break;
    default: lua_pushlstring(L, f.text.data(), f.text.size());
    }
}

// Field name of the spec table at t: a number, or def if it's missing.
double specNum(lua_State* L, int t, const char* name, double def, const std::string& key) {
    int type = lua_getfield(L, t, name);
    double v = type == LUA_TNIL ? def : lua_tonumber(L, -1);
    if (type != LUA_TNIL && (type != LUA_TNUMBER || v != v))
        luaL_error(L, "Settings.define: '%s': %s must be a number", key.c_str(), name);
    lua_pop(L, 1);
    return v;
}

// The range or the values of the spec table at t, for its type.
void settingLimits(lua_State* L, int t, mset::Field& f) {
    if (f.type == mset::Number || f.type == mset::Int) {
        constexpr double kExact = 9007199254740992.0;  // 2^53: integers a double holds exactly
        f.lo = specNum(L, t, "min", f.type == mset::Int ? -kExact : -HUGE_VAL, f.key);
        f.hi = specNum(L, t, "max", f.type == mset::Int ? kExact : HUGE_VAL, f.key);
        if (f.type == mset::Int) f.lo = std::max(std::ceil(f.lo), -kExact), f.hi = std::min(std::floor(f.hi), kExact);
        if (f.lo > f.hi) luaL_error(L, "Settings.define: '%s': min is above max", f.key.c_str());
    } else if (f.type == mset::Text) {
        double max = specNum(L, t, "max", 256, f.key);
        if (max < 1) luaL_error(L, "Settings.define: '%s': max (the length) must be 1 or more", f.key.c_str());
        f.maxLen = static_cast<size_t>(std::min(max, 65536.0));
    } else if (f.type == mset::Choice) {
        lua_getfield(L, t, "values");
        for (lua_Integer i = 1; lua_istable(L, -1) && lua_rawgeti(L, -1, i) == LUA_TSTRING; i++, lua_pop(L, 1))
            f.values.push_back(lua_tostring(L, -1));
        if (f.values.empty()) luaL_error(L, "Settings.define: '%s': values must list the choices (strings)", f.key.c_str());
        lua_pop(L, 2);
    }
}

// One entry of Settings.define: {key =, type =, default =, label =, min =, max =, values =}. Without a default: false,
// 0 (within the range), the first choice, "".
mset::Field settingSpec(lua_State* L, int t) {
    mset::Field f;
    if (lua_getfield(L, t, "key") != LUA_TSTRING) luaL_error(L, "Settings.define: every entry needs a key (a string)");
    f.key = lua_tostring(L, -1);
    bool okKey = !f.key.empty() && std::all_of(f.key.begin(), f.key.end(), [](char c) {
        return std::isalnum(static_cast<unsigned char>(c)) || c == '_';
    });
    if (!okKey) luaL_error(L, "Settings.define: key '%s': use letters, digits and _ only", f.key.c_str());
    int type = -1;
    if (lua_getfield(L, t, "type") == LUA_TSTRING)
        for (int i = 0; kSettingTypes[i]; i++)
            if (strcmp(lua_tostring(L, -1), kSettingTypes[i]) == 0) type = i;
    if (type < 0) luaL_error(L, "Settings.define: '%s': type must be bool, number, int, choice or text", f.key.c_str());
    f.type = static_cast<mset::Type>(type);
    f.label = lua_getfield(L, t, "label") == LUA_TSTRING ? lua_tostring(L, -1) : f.key;
    lua_pop(L, 3);
    settingLimits(L, t, f);
    double num = 0;
    std::string text;
    if (lua_getfield(L, t, "default") != LUA_TNIL && !settingFrom(L, -1, f, num, text))
        luaL_error(L, "Settings.define: '%s': default must be %s", f.key.c_str(), kSettingWant[f.type]);
    lua_pop(L, 1);
    mset::assign(f, num, text);
    return f;
}

// mods\<mod>\settings.json into page, read with the mods' json.decode. A file that isn't a JSON object is renamed to
// settings.json.bad; a value of the wrong type keeps its default; keys the mod doesn't define are kept, as JSON text.
void readSettings(lua_State* L, mset::Page& page) {
    FILE* f = kset::sandbox::open(page.path, "settings.json", "rb");
    if (!f) return;
    std::string text;
    char buf[4096];
    for (size_t n; (n = fread(buf, 1, sizeof buf, f)) > 0 && text.size() + n <= (1u << 20);) text.append(buf, n);
    fclose(f);
    if (text.starts_with("\xEF\xBB\xBF")) text.erase(0, 3);  // Notepad's UTF-8 BOM
    int top = lua_gettop(L);
    std::string why = "not a JSON object";
    size_t at = text.find_first_not_of(" \t\r\n");
    if (at != std::string::npos && text[at] == '{') {
        lua_getglobal(L, "json");
        lua_getfield(L, -1, "decode");
        lua_pushlstring(L, text.data(), text.size());
        if (lua_pcall(L, 1, 1, 0) != LUA_OK) why = lua_type(L, -1) == LUA_TSTRING ? lua_tostring(L, -1) : "not JSON";
        else if (!lua_istable(L, -1)) why = "not a JSON object";
        else why.clear();
    }
    if (!why.empty()) {
        lua_settop(L, top);
        std::error_code ec;
        std::filesystem::rename(pathOf(page.path) / "settings.json", pathOf(page.path) / "settings.json.bad", ec);
        gSink((LV_WARN "Settings: " + page.mod + "\\settings.json couldn't be read (" + why +
               "): Renamed to settings.json.bad, using the defaults").c_str());
        return;
    }
    for (lua_pushnil(L); lua_next(L, -2); lua_pop(L, 1)) {
        if (lua_type(L, -2) != LUA_TSTRING) continue;  // an object's keys are strings
        std::string key = lua_tostring(L, -2);
        auto it = std::find_if(page.fields.begin(), page.fields.end(), [&](const mset::Field& x) { return x.key == key; });
        if (it == page.fields.end()) {
            lua_getglobal(L, "json");
            lua_getfield(L, -1, "encode");
            lua_pushvalue(L, -3);
            if (lua_pcall(L, 1, 1, 0) == LUA_OK && lua_type(L, -1) == LUA_TSTRING) page.unknown[key] = lua_tostring(L, -1);
            lua_pop(L, 2);
            continue;
        }
        double num = it->num;
        std::string s;
        if (!settingFrom(L, -1, *it, num, s) || !mset::assign(*it, num, s))
            gSink((LV_WARN "Settings: " + page.mod + ": '" + key + "' in settings.json isn't " +
                   kSettingWant[it->type] + ", using the default").c_str());
    }
    lua_settop(L, top);
}

int settingGet(lua_State* L) {
    mset::Field f;
    const char* key = luaL_checkstring(L, 2);
    if (!mset::get(lua_tostring(L, lua_upvalueindex(1)), key, f)) return luaL_error(L, "Settings: no setting '%s'", key);
    pushSetting(L, f);
    return 1;
}

// s.key = value: checked and clamped, saved within a second. No onChange: that's for edits in the overlay.
int settingSet(lua_State* L) {
    std::string mod = lua_tostring(L, lua_upvalueindex(1)), key = luaL_checkstring(L, 2), text;
    mset::Field f;
    if (!mset::get(mod, key, f)) return luaL_error(L, "Settings: no setting '%s'", key.c_str());
    double num = f.num;
    if (!settingFrom(L, 3, f, num, text) || !mset::set(mod, key, num, text, false))
        return luaL_error(L, "Settings: '%s' takes %s", key.c_str(), kSettingWant[f.type]);
    return 0;
}

// Settings.define{entries..., onChange = fn}, one closure per mod (upvalue 1: its name): the mod's typed settings,
// with the values saved in its settings.json, once per mod. Returns the table they're read and written through.
int settingsDefine(lua_State* L) {
    std::string mod = lua_tostring(L, lua_upvalueindex(1));
    luaL_checktype(L, 1, LUA_TTABLE);
    if (mset::has(mod)) return luaL_error(L, "Settings.define: once per mod");
    mset::Page page{mod, modFolder(mod)};
    for (lua_Integer i = 1; lua_rawgeti(L, 1, i) != LUA_TNIL; i++, lua_pop(L, 1)) {
        if (!lua_istable(L, -1)) luaL_error(L, "Settings.define: entry %d isn't a table", static_cast<int>(i));
        mset::Field f = settingSpec(L, lua_gettop(L));
        for (const mset::Field& g : page.fields)
            if (g.key == f.key) luaL_error(L, "Settings.define: '%s' is defined twice", f.key.c_str());
        page.fields.push_back(std::move(f));
    }
    lua_pop(L, 1);
    if (page.fields.empty()) return luaL_error(L, "Settings.define: no settings given");
    if (lua_getfield(L, 1, "onChange") != LUA_TNIL) {
        luaL_checktype(L, -1, LUA_TFUNCTION);
        markFunction(L, -1, mod);
        lua_getfield(L, LUA_REGISTRYINDEX, kOnChange);
        lua_pushvalue(L, -2);
        lua_setfield(L, -2, mod.c_str());
        lua_pop(L, 1);
    }
    lua_pop(L, 1);
    readSettings(L, page);
    mset::define(std::move(page));
    lua_newtable(L);  // stays empty, so every read and write goes through the metatable
    lua_newtable(L);
    for (const auto& f : {luaL_Reg{"__index", settingGet}, luaL_Reg{"__newindex", settingSet}}) {
        lua_pushstring(L, mod.c_str());
        lua_pushcclosure(L, f.func, 1);
        lua_setfield(L, -2, f.name);
    }
    lua_pushboolean(L, 0);
    lua_setfield(L, -2, "__metatable");
    lua_setmetatable(L, -2);
    return 1;
}

// onChange(key, value) for each setting edited in the Mod Settings window since the last tick,
// run as the mod that defined it, never from the render thread.
void settingsEdits(lua_State* L) {
    auto edits = mset::takeEdits();
    if (edits.empty()) return;
    lua_getfield(L, LUA_REGISTRYINDEX, kOnChange);
    for (const auto& [mod, key] : edits) {
        mset::Field f;
        if (lua_getfield(L, -1, mod.c_str()) != LUA_TFUNCTION || !mset::get(mod, key, f)) {
            lua_pop(L, 1);
            continue;
        }
        lua_pushstring(L, key.c_str());
        pushSetting(L, f);
        call(L, 2, 0);
    }
    lua_pop(L, 1);
}

int closeModFile(lua_State* L) {
    auto* p = static_cast<luaL_Stream*>(luaL_checkudata(L, 1, LUA_FILEHANDLE));
    int result = fclose(p->f);
    return luaL_fileresult(L, result == 0, nullptr);
}
int ioLineNext(lua_State* L) {
    lua_settop(L, 0);
    lua_pushvalue(L, lua_upvalueindex(1));
    auto* p = static_cast<luaL_Stream*>(luaL_checkudata(L, 1, LUA_FILEHANDLE));
    if (!p->closef) return 0;
    lua_getfield(L, 1, "read"); lua_pushvalue(L, 1);
    int n = int(lua_rawlen(L, lua_upvalueindex(2)));
    for (int i = 1; i <= n; ++i) lua_rawgeti(L, lua_upvalueindex(2), i);
    lua_call(L, n + 1, LUA_MULTRET);
    if (lua_isnil(L, 2)) {
        if (lua_gettop(L) > 2) return luaL_error(L, "%s", lua_tostring(L, 3));
        p->closef = nullptr; fclose(p->f); return 0;
    }
    return lua_gettop(L) - 1;
}
// Per-mod io.open/io.lines return Lua's standard file userdata, backed by the checked native handle.
int modIo(lua_State* L) {
    size_t n;
    const char* path = luaL_checklstring(L, 1, &n);
    if (!kset::sandbox::relative({path, n})) return luaL_argerror(L, 1, "the path must stay inside the mod's folder");
    bool lines = lua_toboolean(L, lua_upvalueindex(2));
    const char* mode = lines ? "r" : luaL_optstring(L, 2, "r");
    // settings.json is Settings.define's: opening it to write fails the way io.open fails, nil and a message (SET 0.2.0).
    if (strpbrk(mode, "wa+") && setOwned({path, n})) {
        lua_pushnil(L);
        lua_pushstring(L, "settings.json belongs to Shrouded Engine Tweaks: use Settings.define to save settings");
        return 2;
    }
    FILE* file = kset::sandbox::open(lua_tostring(L, lua_upvalueindex(1)), {path, n}, mode);
    if (!file) {
        if (lines) return luaL_error(L, "cannot open mod file");
        return luaL_fileresult(L, 0, path);
    }
    auto* stream = static_cast<luaL_Stream*>(lua_newuserdatauv(L, sizeof(luaL_Stream), 0));
    stream->f = file; stream->closef = closeModFile;
    luaL_setmetatable(L, LUA_FILEHANDLE);
    if (!lines) return 1;
    int fidx = lua_gettop(L);
    lua_pushvalue(L, fidx); lua_createtable(L, fidx - 2, 0);
    for (int i = 2; i < fidx; ++i) { lua_pushvalue(L, i); lua_rawseti(L, -2, i - 1); }
    lua_pushcclosure(L, ioLineNext, 2);
    lua_pushnil(L); lua_pushnil(L); lua_pushvalue(L, fidx);
    return 4;
}

// One declared argument position: a kind name ("u32", "entity*", "cstr"...), or "<Type>*" for a copy of a plain
// reflected type.
hk::Arg hookArg(lua_State* L, int idx) {
    size_t len;
    const char* p = luaL_checklstring(L, idx, &len);
    std::string name(p, len);
    hk::Kind kind;
    if (hk::kindOf(name, kind)) return {kind, {}};
    if (!name.empty() && name.back() == '*') {
        name.pop_back();
        auto type = refl::lookup(name).resolved();
        if (type) { needPlain(L, type); return {hk::Snapshot, type}; }
    }
    luaL_error(L, "unknown hook argument type '%s'", name.c_str());
    return {};
}

std::vector<hk::Arg> hookArgs(lua_State* L) {
    lua_getfield(L, 1, "args");
    luaL_checktype(L, -1, LUA_TTABLE);
    size_t n = lua_rawlen(L, -1);
    if (n > hk::kMaxArgs) luaL_error(L, "hooks accept at most eight argument positions");
    std::vector<hk::Arg> args;
    for (size_t i = 1; i <= n; ++i) {
        lua_rawgeti(L, -1, lua_Integer(i));
        args.push_back(hookArg(L, -1));
        lua_pop(L, 1);
    }
    lua_pushnil(L);
    while (lua_next(L, -2)) {
        if (!lua_isinteger(L, -2) || lua_tointeger(L, -2) < 1 || lua_tointeger(L, -2) > lua_Integer(n))
            luaL_error(L, "hook args must be a contiguous array");
        lua_pop(L, 1);
    }
    lua_pop(L, 1);
    return args;
}

std::string hookText(lua_State* L, const char* field, size_t limit) {
    lua_getfield(L, 1, field);
    size_t len;
    const char* p = luaL_checklstring(L, -1, &len);
    if (!len || len > limit || memchr(p, 0, len)) luaL_error(L, "invalid hook %s", field);
    std::string text(p, len);
    lua_pop(L, 1);
    return text;
}

int hookRegister(lua_State* L) {
    if (gInDraw || gInConsole || gHookShutdown || gClosing || gBroken)
        return luaL_error(L, "Hooks.observe is unavailable in Draw, the console, or shutdown");
    luaL_checktype(L, 1, LUA_TTABLE);
    std::string mod = lua_tostring(L, lua_upvalueindex(1)), name = hookText(L, "name", 128);
    if (name.find_first_of("/\\") != std::string::npos) return luaL_error(L, "hook names cannot contain slashes");
    std::string pattern = hookText(L, "pattern", 4096), err;
    auto args = hookArgs(L);
    lua_getfield(L, 1, "queue");
    lua_Integer cap = lua_isnil(L, -1) ? 256 : luaL_checkinteger(L, -1);
    lua_pop(L, 1);
    if (cap < 1 || cap > hk::kMaxQueue) return luaL_error(L, "hook queue must be between 1 and 4096");
    lua_getfield(L, 1, "fn");
    luaL_checktype(L, -1, LUA_TFUNCTION);
    markFunction(L, -1, mod);
    auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    uintptr_t fn = hk::findPattern(base, pattern, err);
    if (!fn) return luaL_error(L, "%s", err.c_str());
    if (gNextHook == LUA_MAXINTEGER) return luaL_error(L, "hook handle limit reached; reload mods");
    int ref = luaL_ref(L, LUA_REGISTRYINDEX);
    lua_Integer handle = ++gNextHook;
    auto [it, inserted] = gHooks.emplace(handle, HookHandler{-1, ref, mod, name, std::move(args)});
    (void)inserted;
    int id = hk::subscribe(fn, mod + '/' + name, it->second.args, uint32_t(cap), err);
    if (id < 0) {
        *static_cast<bool*>(lua_touserdata(L, lua_upvalueindex(2))) = true;
        luaL_unref(L, LUA_REGISTRYINDEX, ref);
        gHooks.erase(it);
        return luaL_error(L, "%s", err.c_str());
    }
    it->second.sub = id;
    lua_pushinteger(L, handle);
    return 1;
}

// Hooks.observe{...} and Hooks.rule{...}: fn runs in a pcall so every refusal reaches the Hooks window's diagnostics.
// Upvalue 2 of fn is set when the hooks layer already recorded the refusal, so it isn't counted twice.
int registerGuarded(lua_State* L, lua_CFunction fn) {
    bool recorded = false;
    lua_pushvalue(L, lua_upvalueindex(1));
    lua_pushlightuserdata(L, &recorded);
    lua_pushcclosure(L, fn, 2);
    lua_pushvalue(L, 1);
    if (lua_pcall(L, 1, 1, 0) == LUA_OK) return 1;
    const char* err = lua_tostring(L, -1);
    if (!recorded)
        hk::refuse(std::string(lua_tostring(L, lua_upvalueindex(1))) + ": " + (err ? err : "hook registration failed"));
    return lua_error(L);
}

// ---- Hooks.rule, Hooks.param, Hooks.list (from SET 0.2.0 runtime/lua_api.cpp) ----
int hookObserve(lua_State* L) { return registerGuarded(L, hookRegister); }

// Do not silently drop malformed conditions or id/build entries. rawlen alone does not prove a contiguous array:
// a table can have holes and unrelated keys while its key count happens to equal rawlen.
size_t ruleArray(lua_State* L, int idx, const char* what) {
    idx = lua_absindex(L, idx);
    luaL_checktype(L, idx, LUA_TTABLE);
    const size_t n = lua_rawlen(L, idx);
    size_t count = 0;
    for (lua_pushnil(L); lua_next(L, idx); lua_pop(L, 1)) {
        if (!lua_isinteger(L, -2) || lua_tointeger(L, -2) < 1 || lua_Unsigned(lua_tointeger(L, -2)) > n)
            luaL_error(L, "rule %s must be a contiguous array", what);
        ++count;
    }
    if (count != n) luaL_error(L, "rule %s must be a contiguous array", what);
    return n;
}

double ruleNumber(lua_State* L, int idx) {
    const double v = luaL_checknumber(L, idx);
    if (!std::isfinite(v)) luaL_error(L, "rule numbers must be finite");
    return v;
}

// Up to 64 entity ids from the table at idx.
std::vector<uint32_t> ruleIds(lua_State* L, int idx) {
    idx = lua_absindex(L, idx);
    lua_Integer n = lua_Integer(ruleArray(L, idx, "id list"));
    std::vector<uint32_t> ids;
    if (n > rules::kMaxList) luaL_error(L, "a rule's id list holds at most 64 ids");
    for (lua_Integer i = 1; i <= n; ++i) {
        lua_rawgeti(L, idx, i);
        lua_Integer v = lua_isinteger(L, -1) ? lua_tointeger(L, -1) : -1;
        if (v < 0 || v > 0xffffffffLL) luaL_error(L, "rule ids are entity ids (whole numbers)");
        ids.push_back(uint32_t(v));
        lua_pop(L, 1);
    }
    return ids;
}

struct RuleParse {
    hk::RuleDecl d;
    std::vector<std::string> params;  // names, in index order
    bool hasList = false;
    const contracts::Entry* entry = nullptr;  // target = "<catalog name>"
};

// params = {name = number}: a "$name" value reads the param's live value (Hooks.param). Indexes follow the sorted
// names, so the same rule after a reload has the same shape and reuses its slot.
void ruleParams(lua_State* L, RuleParse& p) {
    int type = lua_getfield(L, 1, "params");
    if (type != LUA_TNIL && type != LUA_TTABLE) luaL_error(L, "rule params must be a table of names to numbers");
    std::map<std::string, double> named;
    if (type == LUA_TTABLE) {
        lua_pushnil(L);
        while (lua_next(L, -2)) {
            if (lua_type(L, -2) != LUA_TSTRING || lua_type(L, -1) != LUA_TNUMBER)
                luaL_error(L, "rule params must be a table of names to numbers");
            named[lua_tostring(L, -2)] = ruleNumber(L, -1);
            lua_pop(L, 1);
        }
    }
    lua_pop(L, 1);
    if (named.size() > rules::kMaxParams) luaL_error(L, "a rule takes at most 8 params");
    for (const auto& [name, v] : named) {
        p.params.push_back(name);
        p.d.params.push_back(v);
    }
}

// A number, true/false, "player" (the host player's entity) or "$name" (a param).
rules::Operand ruleOperand(lua_State* L, int idx, const RuleParse& p) {
    rules::Operand o;
    if (lua_type(L, idx) == LUA_TNUMBER) { o.value = ruleNumber(L, idx); return o; }
    if (lua_type(L, idx) == LUA_TBOOLEAN) { o.value = lua_toboolean(L, idx); return o; }
    const char* s = lua_type(L, idx) == LUA_TSTRING ? lua_tostring(L, idx) : "";
    if (!strcmp(s, "player")) { o.src = rules::Player; return o; }
    for (size_t i = 0; *s == '$' && i < p.params.size(); ++i)
        if (p.params[i] == s + 1) { o.src = rules::Param; o.param = uint8_t(i); return o; }
    luaL_error(L, *s == '$' ? "rule param '%s' isn't in params" : "rule values are numbers, booleans, \"player\" or \"$param\"", s + 1);
    return o;
}

// A field the catalog lists for argument a (1-based) of its entry.
rules::Loc catalogField(lua_State* L, const contracts::Entry& e, lua_Integer a, const char* name) {
    for (const auto& f : e.fields)
        if (f.arg == a - 1 && !strcmp(f.name, name)) return contracts::loc(f);
    luaL_error(L, "catalog target '%s' has no field '%s' on argument %d", e.name, name, int(a));
    return {};
}

// {arg = n [, field = "path"]} at t: the value it names. An entity* argument without a field is the id it points at;
// on a catalog target, a field is one the catalog lists.
rules::Loc ruleLoc(lua_State* L, int t, const RuleParse& p) {
    const auto& args = p.d.args;
    lua_getfield(L, t, "arg");
    int ok = 0;
    lua_Integer a = lua_tointegerx(L, -1, &ok);
    if (!ok || a < 1 || a > lua_Integer(args.size())) luaL_error(L, "rule entries need arg = a declared position (1..%d)", int(args.size()));
    lua_getfield(L, t, "field");
    if (p.entry && !lua_isnil(L, -1)) {
        rules::Loc l = catalogField(L, *p.entry, a, luaL_checkstring(L, -1));
        lua_pop(L, 2);
        return l;
    }
    const hk::Arg& arg = args[size_t(a - 1)];
    rules::Loc l;
    std::string err;
    bool found = lua_isnil(L, -1) && arg.kind != hk::EntityPtr
                     ? hk::argLoc(arg, uint8_t(a - 1), l)
                     : hk::fieldLoc(arg, uint8_t(a - 1), lua_isnil(L, -1) ? "" : luaL_checkstring(L, -1), l, err);
    if (!found) luaL_error(L, "%s", err.empty() ? ("argument " + std::to_string(a) + " is a pointer: name a field").c_str() : err.c_str());
    lua_pop(L, 2);
    return l;
}

// The one key of names set in the entry at t, its value pushed; its index in names.
int ruleKey(lua_State* L, int t, std::initializer_list<const char*> names, const char* what) {
    int found = -1, i = 0;
    for (const char* n : names) {
        if (lua_getfield(L, t, n) != LUA_TNIL) {
            if (found >= 0) luaL_error(L, "each %s entry takes exactly one operation", what);
            found = i;
        }
        lua_pop(L, 1);
        ++i;
    }
    if (found < 0) luaL_error(L, "a %s entry has no operation", what);
    lua_getfield(L, t, names.begin()[found]);
    return found;
}

// when or set: a list of entry tables. Returns the list's stack index, or 0 if it's absent.
int ruleList(lua_State* L, const char* field) {
    int type = lua_getfield(L, 1, field);
    if (type == LUA_TNIL) { lua_pop(L, 1); return 0; }
    if (type != LUA_TTABLE) luaL_error(L, "rule %s must be a list of tables", field);
    ruleArray(L, -1, field);
    return lua_gettop(L);
}

void ruleConds(lua_State* L, RuleParse& p) {
    int list = ruleList(L, "when");
    for (lua_Integer i = 1; list && lua_rawgeti(L, list, i) != LUA_TNIL; ++i) {
        int e = lua_gettop(L);
        luaL_checktype(L, e, LUA_TTABLE);
        if (p.d.shape.conds == rules::kMaxConds) luaL_error(L, "a rule takes at most 8 conditions");
        rules::Cond c;
        c.at = ruleLoc(L, e, p);
        c.cmp = rules::Cmp(ruleKey(L, e, {"eq", "ne", "lt", "le", "gt", "ge", "in"}, "when"));
        if (c.cmp == rules::In) {
            if (p.hasList) luaL_error(L, "a rule takes one in list");
            p.hasList = true;
            p.d.list = ruleIds(L, -1);
        } else {
            c.v = ruleOperand(L, -1, p);
        }
        p.d.shape.cond[p.d.shape.conds++] = c;
        lua_settop(L, e - 1);
    }
    if (list) lua_settop(L, list - 1);
}

// block = value: a matching call doesn't run and returns value (true on a void function).
void ruleBlock(lua_State* L, RuleParse& p) {
    if (lua_getfield(L, 1, "block") != LUA_TNIL) {
        p.d.shape.block = true;
        p.d.shape.ret = ruleOperand(L, -1, p);
    }
    lua_pop(L, 1);
}

void ruleChanges(lua_State* L, RuleParse& p) {
    int list = ruleList(L, "set");
    for (lua_Integer i = 1; list && lua_rawgeti(L, list, i) != LUA_TNIL; ++i) {
        int e = lua_gettop(L);
        luaL_checktype(L, e, LUA_TTABLE);
        if (p.d.shape.changes == rules::kMaxChanges) luaL_error(L, "a rule takes at most 8 changes");
        rules::Change c;
        c.at = ruleLoc(L, e, p);
        c.op = rules::Op(ruleKey(L, e, {"set", "add", "mul", "min", "max"}, "set"));
        c.v = ruleOperand(L, -1, p);
        p.d.shape.change[p.d.shape.changes++] = c;
        lua_settop(L, e - 1);
    }
    if (list) lua_settop(L, list - 1);
}

// after = {{<set|add|mul|min|max> = value}, ...}: changes to the return value, once the original returned.
void ruleAfter(lua_State* L, RuleParse& p) {
    auto& s = p.d.shape;
    int list = ruleList(L, "after");
    for (lua_Integer i = 1; list && lua_rawgeti(L, list, i) != LUA_TNIL; ++i) {
        int e = lua_gettop(L);
        luaL_checktype(L, e, LUA_TTABLE);
        if (s.afters == rules::kMaxChanges) luaL_error(L, "a rule takes at most 8 after changes");
        rules::Change c;
        c.at = s.retAt;
        c.op = rules::Op(ruleKey(L, e, {"set", "add", "mul", "min", "max"}, "after"));
        c.v = ruleOperand(L, -1, p);
        s.after[s.afters++] = c;
        lua_settop(L, e - 1);
    }
    if (list) lua_settop(L, list - 1);
    if (s.block && (s.changes || s.afters)) luaL_error(L, "a rule that blocks changes nothing: the original doesn't run");
    if (!s.changes && !s.block && !s.afters)
        luaL_error(L, "a rule needs set = {{arg = n, <set|add|mul|min|max> = value}, ...}, after = {...} or block = value");
}

// builds = {...}: the game builds the pattern and argument declarations were verified on. Required.
bool ruleBuildListed(lua_State* L) {
    if (lua_getfield(L, 1, "builds") != LUA_TTABLE || !lua_rawlen(L, -1))
        luaL_error(L, "a rule on a pattern needs builds = {...}: the game builds it was verified on");
    ruleArray(L, -1, "builds");
    bool listed = false;
    for (lua_Integer i = 1; lua_rawgeti(L, -1, i) != LUA_TNIL; ++i, lua_pop(L, 1)) {
        if (!lua_isinteger(L, -1) || lua_tointeger(L, -1) < 0 || lua_tointeger(L, -1) > UINT32_MAX)
            luaL_error(L, "rule builds must be whole-number game build stamps");
        listed |= lua_tointeger(L, -1) == lua_Integer(gBuild.load());
    }
    lua_pop(L, 2);
    return listed;
}

// target = "<catalog name>": the catalog declares the arguments and the fields a rule may read or change.
void ruleTarget(lua_State* L, RuleParse& p, contracts::Status& st) {
    std::string target = hookText(L, "target", 128);
    for (const char* k : {"pattern", "args", "builds"}) {
        bool set = lua_getfield(L, 1, k) != LUA_TNIL;
        lua_pop(L, 1);
        if (set) luaL_error(L, "a rule on a catalog target takes no %s: the catalog has it", k);
    }
    if (!contracts::find(target, st) || !st.entry) luaL_error(L, "no catalog target '%s'", target.c_str());
    p.entry = st.entry;
    p.d.block = st.entry->caps & contracts::Block;
    p.d.after = st.entry->caps & contracts::After;
    p.d.shape.retAt = contracts::retLoc(*st.entry);
    p.d.args = contracts::args(*st.entry);
    p.d.thunk = st.entry->thunk;
    for (const auto& f : st.entry->fields) (f.readOnly ? p.d.reads : p.d.fields).push_back(contracts::loc(f));
}

// The catalog entry's capabilities: changing an argument needs Args, changing a field Fields, blocking Block, changing
// the return value After.
void ruleCaps(lua_State* L, const RuleParse& p) {
    if (p.d.shape.block && !(p.entry->caps & contracts::Block))
        luaL_error(L, "catalog target '%s' doesn't let rules block it", p.entry->name);
    if (p.d.shape.afters && !(p.entry->caps & contracts::After))
        luaL_error(L, "catalog target '%s' doesn't let rules change its return value", p.entry->name);
    for (int i = 0; i < p.d.shape.changes; ++i) {
        bool field = p.d.shape.change[i].at.field;
        if (!(p.entry->caps & (field ? contracts::Fields : contracts::Args)))
            luaL_error(L, "catalog target '%s' doesn't let rules change %s", p.entry->name, field ? "fields" : "arguments");
    }
}

// Where the rule goes: the catalog target's function, or the pattern's on a listed build. 0 if the rule stays off on
// this game (a yellow line says why).
uintptr_t ruleFn(lua_State* L, const RuleParse& p, const contracts::Status& st, const std::string& mod,
                 const std::string& name) {
    std::string off, err, why;
    // Kiln: what kiln.hooks can't do here keeps the rule off the way an unchecked target does (nil, a yellow line).
    if (p.entry) ruleCaps(L, p);
    if (!hk::rulesAvailable(why)) {
        off = "Native hooks are unavailable (" + why + ")";
    } else if (p.entry && p.d.shape.afters && !hk::afterAvailable(p.d.thunk, why)) {
        off = "Return changes are unavailable on Kiln (" + why + ")";
    } else if (p.entry) {
        if (st.fn) return st.fn;
        off = "Catalog target '" + st.name + "' is off (" + st.why + ")";
    } else if (ruleBuildListed(L)) {
        uintptr_t fn = hk::findPattern(reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)), hookText(L, "pattern", 4096), err);
        if (!fn) luaL_error(L, "%s", err.c_str());
        return fn;
    } else {
        off = "Not verified on game build " + std::to_string(gBuild.load());
    }
    gSink((LV_WARN + mod + ": Rule '" + name + "' off: " + off).c_str());
    return 0;
}

int hookRuleRegister(lua_State* L) {
    if (gInDraw || gInConsole || gHookShutdown || gClosing || gBroken)
        return luaL_error(L, "Hooks.rule is unavailable in Draw, the console, or shutdown");
    luaL_checktype(L, 1, LUA_TTABLE);
    std::string mod = lua_tostring(L, lua_upvalueindex(1)), name = hookText(L, "name", 128), err, conflict;
    if (name.find_first_of("/\\") != std::string::npos) return luaL_error(L, "hook names cannot contain slashes");
    RuleParse p;
    contracts::Status st;
    p.d.key = mod + '/' + name;
    bool catalog = lua_getfield(L, 1, "target") != LUA_TNIL;
    lua_pop(L, 1);
    if (catalog) ruleTarget(L, p, st);
    else { hookText(L, "pattern", 4096); p.d.args = hookArgs(L); }
    auto order = gModOrder.find(mod);
    p.d.order = order == gModOrder.end() ? UINT32_MAX : order->second;
    ruleParams(L, p);
    ruleConds(L, p);
    ruleBlock(L, p);
    ruleChanges(L, p);
    ruleAfter(L, p);
    bool* recorded = static_cast<bool*>(lua_touserdata(L, lua_upvalueindex(2)));
    if (!hk::validRule(p.d, err)) {
        *recorded = true;  // validRule recorded it
        return luaL_error(L, "%s", err.c_str());
    }
    uintptr_t fn = ruleFn(L, p, st, mod, name);
    if (!fn) { lua_pushnil(L); return 1; }
    if (gNextHook == LUA_MAXINTEGER) return luaL_error(L, "hook handle limit reached; reload mods");
    int id = hk::addRule(fn, p.d, err, conflict);
    if (id < 0) {
        *recorded = true;
        return luaL_error(L, "%s", err.c_str());
    }
    if (!conflict.empty())
        gSink((LV_WARN + mod + ": Rule '" + name +
               (p.d.shape.block ? "' and " + conflict + " both block this call; the first in mod order wins"
                                : "' changes a value " + conflict + " changes too; both apply, in mod order")).c_str());
    lua_Integer handle = ++gNextHook;
    gHooks.emplace(handle, HookHandler{-1, LUA_NOREF, mod, name, p.d.args, id, p.params, p.hasList});
    lua_pushinteger(L, handle);
    return 1;
}

int hookRule(lua_State* L) { return registerGuarded(L, hookRuleRegister); }

// The calling mod's active rule with the handle at 1.
HookHandler& ownRule(lua_State* L) {
    auto it = gHooks.find(luaL_checkinteger(L, 1));
    if (it == gHooks.end() || it->second.rule < 0) luaL_error(L, "not an active rule");
    if (it->second.mod != lua_tostring(L, lua_upvalueindex(1))) luaL_error(L, "cannot change another mod's rule");
    return it->second;
}

// Hooks.param(id, name, value): the next calls use the new value.
int hookParam(lua_State* L) {
    HookHandler& h = ownRule(L);
    const char* name = luaL_checkstring(L, 2);
    double v = ruleNumber(L, 3);
    auto at = std::find(h.params.begin(), h.params.end(), name);
    if (at == h.params.end()) return luaL_error(L, "rule has no param '%s'", name);
    hk::setParam(h.rule, int(at - h.params.begin()), v);
    return 0;
}

// Hooks.list(id, {ids}): replaces the ids of the rule's in condition.
int hookList(lua_State* L) {
    HookHandler& h = ownRule(L);
    if (!h.hasList) return luaL_error(L, "rule has no in condition");
    hk::setList(h.rule, ruleIds(L, 2));
    return 0;
}

void removeHook(lua_State* L, lua_Integer id) {
    auto it = gHooks.find(id);
    if (it == gHooks.end()) return;
    if (it->second.rule >= 0) hk::removeRule(it->second.rule);
    else hk::unsubscribe(it->second.sub);
    luaL_unref(L, LUA_REGISTRYINDEX, it->second.ref);
    gHooks.erase(it);
}

int hookRemove(lua_State* L) {
    lua_Integer id = luaL_checkinteger(L, 1);
    auto it = gHooks.find(id);
    if (it != gHooks.end() && it->second.mod != lua_tostring(L, lua_upvalueindex(1)))
        return luaL_error(L, "cannot remove another mod's hook");
    removeHook(L, id);
    return 0;
}

void removeModHooks(lua_State* L, const std::string& mod) {
    for (auto it = gHooks.begin(); it != gHooks.end();) {
        auto cur = it++;
        if (cur->second.mod == mod) removeHook(L, cur->first);
    }
}

void stopHooks(lua_State* L) {
    hk::unsubscribeAll();
    if (L) for (const auto& [id, h] : gHooks) { (void)id; luaL_unref(L, LUA_REGISTRYINDEX, h.ref); }
    gHooks.clear();
}

void pushHookValue(lua_State* L, const hk::Arg& arg, const hk::Value& v, const hk::Batch& batch) {
    switch (arg.kind) {
    case hk::Skip: lua_pushnil(L); break;
    case hk::Bool: lua_pushboolean(L, v.bits != 0); break;
    case hk::F32: lua_pushnumber(L, std::bit_cast<float>(uint32_t(v.bits))); break;
    case hk::F64: lua_pushnumber(L, std::bit_cast<double>(v.bits)); break;
    case hk::Snapshot:
        if (!v.bits) lua_pushnil(L);
        else memcpy(newOwned(L, arg.type), batch.payload.data() + v.off, v.len);
        break;
    case hk::CStr:
        if (!v.bits) lua_pushnil(L);
        else lua_pushlstring(L, v.len ? reinterpret_cast<const char*>(batch.payload.data() + v.off) : "", v.len);
        break;
    case hk::EntityPtr:
        if (!v.len) { lua_pushnil(L); break; }
        [[fallthrough]];
    default: lua_pushinteger(L, std::bit_cast<int64_t>(v.bits)); break;
    }
}

void pushHookEvent(lua_State* L, const HookHandler& h, const hk::Batch& batch, const hk::Event& e) {
    lua_createtable(L, 0, 4);
    lua_pushinteger(L, lua_Integer(e.seq)); lua_setfield(L, -2, "seq");
    lua_pushnumber(L, hk::seconds(e.qpc)); lua_setfield(L, -2, "time");
    lua_pushinteger(L, lua_Integer(e.dropped)); lua_setfield(L, -2, "dropped");
    lua_createtable(L, int(h.args.size()), 0);
    for (size_t i = 0; i < h.args.size(); ++i) {
        pushHookValue(L, h.args[i], e.v[i], batch);
        lua_rawseti(L, -2, lua_Integer(i + 1));
    }
    lua_setfield(L, -2, "args");
}

void runHook(lua_State* L, lua_Integer id, const HookHandler& h, const hk::Batch& batch) {
    for (const auto& e : batch.events) {
        if (!gHooks.contains(id) || gReload) break;
        int top = lua_gettop(L);
        lua_rawgeti(L, LUA_REGISTRYINDEX, h.ref);
        pushHookEvent(L, h, batch, e);
        // Allocating the event can run Lua finalizers that remove a subscription.
        if (!gHooks.contains(id) || !hk::delivered(h.sub, batch.generation)) { lua_settop(L, top); break; }
        if (!call(L, 1, 0)) {
            gSink((LV_BAD + h.mod + ": Hook '" + h.name + "' removed after an error").c_str());
            removeHook(L, id);
        }
        lua_settop(L, top);
    }
}

void dispatchHooks(lua_State* L) {
    struct Run { lua_Integer id; HookHandler handler; hk::Batch batch; };
    std::vector<Run> runs;
    hk::Batch batch;
    // Seal every batch before any handler runs, including calls caused by another handler.
    for (const auto& [id, h] : gHooks)
        if (h.rule < 0 && hk::drain(h.sub, batch)) runs.push_back({id, h, std::exchange(batch, {})});
    for (const auto& r : runs) runHook(L, r.id, r.handler, r.batch);
}

// A mod's globals: its own table, falling back to the shared one, plus what has to know the mod: Keys.hotkey and
// Keys.input, io inside its folder, and require from its folder.
void pushModEnv(lua_State* L, const std::string& name) {
    std::string dir = modFolder(name);
    lua_newtable(L);
    int env = lua_gettop(L);
    lua_newtable(L);
    lua_newtable(L);
    lua_pushglobaltable(L);
    lua_pushnil(L);
    while (lua_next(L, -2)) { lua_pushvalue(L, -2); lua_insert(L, -2); lua_rawset(L, -5); }
    lua_pop(L, 1);
    lua_setfield(L, -2, "__index");
    lua_pushboolean(L, 0); lua_setfield(L, -2, "__metatable");
    lua_setmetatable(L, env);
    lua_pushvalue(L, env); lua_setfield(L, env, "_G");
    lua_newtable(L);
    lua_pushcfunction(L, keysOn);
    lua_setfield(L, -2, "on");
    for (bool input : {false, true}) {
        lua_pushstring(L, name.c_str());
        lua_pushboolean(L, input);
        lua_pushcclosure(L, keysBind, 2);
        lua_pushvalue(L, -1);
        lua_setfield(L, env, input ? "registerInput" : "registerHotkey");
        lua_setfield(L, -2, input ? "input" : "hotkey");
    }
    lua_setfield(L, env, "Keys");
    lua_newtable(L);
    for (const auto& f : {luaL_Reg{"observe", hookObserve}, luaL_Reg{"rule", hookRule}, luaL_Reg{"param", hookParam},
                          luaL_Reg{"list", hookList}, luaL_Reg{"remove", hookRemove}}) {
        lua_pushstring(L, name.c_str());
        lua_pushcclosure(L, f.func, 1);
        lua_setfield(L, -2, f.name);
    }
    lua_setfield(L, env, "Hooks");
    uint64_t owner = sys::createOwner("kiln-set/" + name);
    gSystemOwners[name] = owner;
    lua_getglobal(L, "Systems");
    lua_newtable(L);
    for (const char* fn : {"list", "enabled", "profile", "stats"}) { lua_getfield(L, -2, fn); lua_setfield(L, -2, fn); }
    lua_pushinteger(L, static_cast<lua_Integer>(owner)); lua_pushcclosure(L, systemsEnable, 1); lua_setfield(L, -2, "enable");
    lua_setfield(L, env, "Systems"); lua_pop(L, 1);
    lua_newtable(L);
    lua_getfield(L, LUA_REGISTRYINDEX, kIo);
    for (const char* fn : {"open", "lines"}) {
        lua_pushstring(L, dir.c_str());
        lua_pushboolean(L, std::strcmp(fn, "lines") == 0);
        lua_pushcclosure(L, modIo, 2);
        lua_setfield(L, -3, fn);
    }
    lua_pop(L, 1);
    lua_setfield(L, env, "io");
    lua_newtable(L);
    lua_pushstring(L, name.c_str());
    lua_pushcclosure(L, settingsDefine, 1);
    lua_setfield(L, -2, "define");
    lua_setfield(L, env, "Settings");
    lua_getfield(L, LUA_REGISTRYINDEX, kRequire);
    lua_pushvalue(L, env);
    lua_pushstring(L, dir.c_str());
    lua_pushstring(L, name.c_str());
    lua_call(L, 3, 1);
    lua_setfield(L, env, "require");
    luaL_loadstring(L, "local base, env = ...; return function(chunk, name, _, custom) return base(chunk, name, 't', custom or env) end");
    lua_getglobal(L, "load"); lua_pushvalue(L, env); lua_call(L, 2, 1); lua_setfield(L, env, "load");
}

// In the table at idx, every function of mod becomes false (a list, like a dropped callback) or is removed (a map).
void dropModFns(lua_State* L, int idx, const std::string& mod, bool map) {
    idx = lua_absindex(L, idx);
    lua_pushnil(L);
    while (lua_next(L, idx)) {
        bool mine = lua_isfunction(L, -1) && modOf(L, -1) == mod;
        lua_pop(L, 1);
        if (!mine) continue;
        lua_pushvalue(L, -1);  // the key, kept for lua_next
        if (map) lua_pushnil(L);
        else lua_pushboolean(L, 0);
        lua_rawset(L, idx);  // assigning to a field that exists is allowed while traversing
    }
}

// A mod whose init.lua failed is gone completely: what it registered before the error stops too (callbacks, events,
// keys, hotkeys, native hooks), so it can't half-run.
void unloadMod(lua_State* L, const std::string& mod) {
    for (const char* reg : {kTicks, kDraws, kOnInit, kOnOpen, kOnClose, kOnShutdown, kOnEnter, kOnLeave, kKeys, kBinds}) {
        lua_getfield(L, LUA_REGISTRYINDEX, reg);
        dropModFns(L, -1, mod, reg == kKeys || reg == kBinds);
        lua_pop(L, 1);
    }
    lua_getfield(L, LUA_REGISTRYINDEX, kObservers);  // event type hash -> handler list
    lua_pushnil(L);
    while (lua_next(L, -2)) {
        if (lua_istable(L, -1)) dropModFns(L, -1, mod, false);
        lua_pop(L, 1);
    }
    lua_pop(L, 1);
    {
        std::lock_guard lock(gBindMx);
        std::erase_if(gBinds, [&](const Binding& b) { return b.mod == mod; });
    }
    removeModHooks(L, mod);
    discardAdmin(mod);
    if (auto it = gSystemOwners.find(mod); it != gSystemOwners.end()) { sys::releaseOwner(it->second); gSystemOwners.erase(it); }
    lua_getfield(L, LUA_REGISTRYINDEX, kOnChange);
    lua_pushnil(L);
    lua_setfield(L, -2, mod.c_str());
    lua_pop(L, 1);
    wlog::leave(mod, gSink);
    mset::drop(mod, gSink);
}

// mods/<name>/mod.json, optional: {"requires": "0.1.1", "builds": [1793791052]}: the oldest Shrouded Engine Tweaks the
// mod needs, and the game builds it was made for. Read with the mods' own json.decode.
struct ModInfo {
    std::string needs;  // "requires" in mod.json
    std::vector<uint32_t> builds;
    std::string title, version;  // optional "name" and "version" (Kiln's mod manager shows them)
};

ModInfo readModJson(lua_State* L, const std::string& name) {
    ModInfo info;
    FILE* f = nullptr;
    f = kset::sandbox::open(modFolder(name), "mod.json", "rb");
    if (!f) return info;
    std::string text;
    char buf[4096];
    for (size_t n; (n = fread(buf, 1, sizeof buf, f)) > 0 && text.size() + n <= (1u << 20);) text.append(buf, n);
    fclose(f);
    if (text.starts_with("\xEF\xBB\xBF")) text.erase(0, 3);  // Notepad's UTF-8 BOM
    int top = lua_gettop(L);
    lua_getglobal(L, "json");
    lua_getfield(L, -1, "decode");
    lua_pushlstring(L, text.data(), text.size());
    if (lua_pcall(L, 1, 1, 0) != LUA_OK || !lua_istable(L, -1)) {
        std::string why = lua_type(L, -1) == LUA_TSTRING ? lua_tostring(L, -1) : "not a JSON object";
        gSink((LV_WARN + name + ": mod.json not read (" + why + "), loading without it").c_str());
        lua_settop(L, top);
        return info;
    }
    if (lua_getfield(L, -1, "requires") == LUA_TSTRING) info.needs = lua_tostring(L, -1);
    lua_pop(L, 1);
    if (lua_getfield(L, -1, "name") == LUA_TSTRING) info.title = lua_tostring(L, -1);
    lua_pop(L, 1);
    if (lua_getfield(L, -1, "version") == LUA_TSTRING) info.version = lua_tostring(L, -1);
    lua_pop(L, 1);
    if (lua_getfield(L, -1, "builds") == LUA_TTABLE) {
        for (lua_Integer i = 1; lua_rawgeti(L, -1, i) == LUA_TNUMBER; i++, lua_pop(L, 1))
            info.builds.push_back(static_cast<uint32_t>(lua_tointeger(L, -1)));
    }
    lua_settop(L, top);
    return info;
}

// True if version want ("0.2.0") is newer than this Shrouded Engine Tweaks. Missing parts count as 0.
bool newerThanSet(const std::string& want) {
    int a[3] = {}, b[3] = {};
    sscanf_s(want.c_str(), "%d.%d.%d", &a[0], &a[1], &a[2]);
    sscanf_s(RUNTIME_VERSION, "%d.%d.%d", &b[0], &b[1], &b[2]);
    return std::lexicographical_compare(b, b + 3, a, a + 3);
}

// "Mod Loaded: x", or failed, with the builds a mod.json named when this game build isn't one of them.
std::string modLine(const std::string& name, bool ok, const ModInfo& info) {
    bool other = !info.builds.empty() && std::find(info.builds.begin(), info.builds.end(), gBuild) == info.builds.end();
    std::string line = (ok ? (other ? LV_WARN "Mod Loaded: " : LV_OK "Mod Loaded: ") : LV_BAD "Mod Failed: ") + name;
    if (!other) return line;
    std::string made;
    for (uint32_t b : info.builds) made += (made.empty() ? "" : ", ") + std::to_string(b);
    line += " (made for game build" + std::string(info.builds.size() > 1 ? "s " : " ") + made + ", this is " +
            std::to_string(gBuild) + (ok ? ")" : ": Wrong game version?)");
    return line;
}

// Runs mods/<name>/init.lua for every mod folder, each in its own environment. A failing mod is reported and unloaded.
// What init.lua returns is kept for GetMod (true if it returns nothing).
void loadMods(lua_State* L) {
    int loaded = 0, failed = 0;
    gModOrder.clear();
    std::vector<rt::ModReport> reports;
    auto report = [&](const std::string& name, const char* status, std::string reason, const ModInfo& info) {
        reports.push_back({name, status, std::move(reason), info.title, info.version, gModsDir + "\\" + name});
    };
    lua_getfield(L, LUA_REGISTRYINDEX, kMods);
    int mods = lua_gettop(L);
    // The mod folders by name, case-insensitive as NTFS lists them. std::filesystem reads the folder with the wide Win32
    // calls: FindFirstFileA would read gModsDir in the ANSI code page and find no mod under a Cyrillic or CJK game folder.
    std::vector<std::string> folders;
    {
        std::error_code ec;
        for (std::filesystem::directory_iterator it(pathOf(gModsDir), ec), end; !ec && it != end; it.increment(ec)) {
            std::error_code dir_ec;
            if (!it->is_directory(dir_ec)) continue;
            const auto u = it->path().filename().u8string();
            std::string folder(u.begin(), u.end());
            if (!folder.empty() && folder[0] != '.') folders.push_back(std::move(folder));
        }
    }
    const auto folded = [](std::string v) {
        for (char& c : v) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c + 32);
        return v;
    };
    std::sort(folders.begin(), folders.end(), [&](const std::string& x, const std::string& y) {
        const std::string fx = folded(x), fy = folded(y);
        return fx != fy ? fx < fy : x < y;
    });
    for (const std::string& name : folders) {
        std::error_code init_ec;
        if (!std::filesystem::exists(pathOf(gModsDir) / pathOf(name) / "init.lua", init_ec)) {
            gSink((LV_WARN "Mod Skipped: " + name + " (no init.lua)").c_str());
            report(name, "skipped", "the folder has no init.lua", ModInfo{});
            continue;
        }
        gModOrder.emplace(name, uint32_t(gModOrder.size()));
        ModInfo info = readModJson(L, name);
        if (!info.needs.empty() && newerThanSet(info.needs)) {
            failed++;
            gSink((LV_BAD "Mod Skipped: " + name + " (needs Shrouded Engine Tweaks " + info.needs +
                   ", this is " RUNTIME_VERSION ")").c_str());
            report(name, "failed", "needs Shrouded Engine Tweaks " + info.needs + ", this is " RUNTIME_VERSION, info);
            continue;
        }
        bool ok = loadText(L, modFolder(name), "init.lua", ("@" + name + "/init.lua").c_str());
        std::string why;
        if (!ok) {
            const char* e = lua_tostring(L, -1);
            why = e ? e : "init.lua could not be read";
            gSink((LV_BAD + why).c_str());  // call() prints its own errors
        } else {
            pushModEnv(L, name);
            lua_setupvalue(L, -2, 1);  // a main chunk's only upvalue is _ENV
            ok = call(L, 0, 1);
            if (!ok) why = gLastCallError.empty() ? "init.lua raised an error" : gLastCallError;
        }
        if (ok) {
            if (lua_isnil(L, -1)) lua_pushboolean(L, 1);
            lua_setfield(L, mods, name.c_str());
            loaded++;
        } else {
            failed++;
            unloadMod(L, name);
        }
        lua_settop(L, mods);
        if (const auto nl = why.find('\n'); nl != std::string::npos) why.resize(nl);  // the error, without the traceback
        const std::string line = modLine(name, ok, info);
        gSink(line.c_str());
        // the note about game builds (modLine's parentheses) is the reason a loaded mod may misbehave
        const auto paren = line.find(" (made for game build");
        report(name, ok ? "loaded" : "failed",
               ok ? (paren == std::string::npos ? std::string() : line.substr(paren + 2, line.size() - paren - 3)) : why, info);
    }
    lua_pop(L, 1);
    {
        std::lock_guard lock(gReportMx);
        gReports = std::move(reports);
    }
    if (gOnReports) gOnReports();
    gSink(((failed ? LV_WARN "Mods: " : LV_OK "Mods: ") + std::to_string(loaded) + " loaded, " + std::to_string(failed) + " failed").c_str());
}

// GetMod(name): what that mod's init.lua returned, true if it returned nothing, nil if it isn't loaded.
int getMod(lua_State* L) {
    lua_getfield(L, LUA_REGISTRYINDEX, kMods);
    lua_getfield(L, -1, luaL_checkstring(L, 1));
    return 1;
}

// The mod the function at idx comes from, for error lines: its chunk name up to the first / ("@hello/init.lua" is
// hello), "console" for a typed line, "load" for a string chunk.
std::string modOf(lua_State* L, int idx, bool* isMod) {
    if (isMod) *isMod = false;
    if (!lua_isfunction(L, idx)) return "load";
    idx = lua_absindex(L, idx);
    lua_getfield(L, LUA_REGISTRYINDEX, kFunctionMods);
    lua_pushvalue(L, idx); lua_rawget(L, -2);
    if (const char* owner = lua_tostring(L, -1)) {
        std::string mod(owner);
        lua_pop(L, 2);
        if (isMod) *isMod = mod != "console" && mod != "load";  // what the console registered is not a mod's
        return mod;
    }
    lua_pop(L, 2);
    lua_Debug ar;
    lua_pushvalue(L, idx);
    lua_getinfo(L, ">S", &ar);
    std::string_view s = ar.source ? ar.source : "";
    if (isMod) *isMod = s.starts_with('@');
    if (!s.starts_with('@') && !s.starts_with('=')) return "load";
    s.remove_prefix(1);
    return std::string(s.substr(0, s.find('/')));
}

// Drops callback i of the list on top of the stack after an error, naming its mod: "hello: tick callback removed".
void dropCallback(lua_State* L, lua_Integer i, const char* what) {
    lua_rawgeti(L, -1, i);
    gSink((LV_BAD + modOf(L, -1) + ": " + char(toupper(static_cast<unsigned char>(*what))) + (what + 1) + " removed after an error").c_str());
    lua_pop(L, 1);
    lua_pushboolean(L, 0);
    lua_rawseti(L, -2, i);
}

// A Tick or Draw callback slower than this holds up the game (the server tick or frame, or the mods' Draw).
constexpr double kSlowMs = 50;

// One yellow line per mod and kind every 10 s, naming the mod whose callback i (in the list on top) was slow.
void warnSlow(lua_State* L, lua_Integer i, const char* what, double ms) {
    static std::map<std::string, std::chrono::steady_clock::time_point> told;  // under gLuaMx, like all Lua work
    lua_rawgeti(L, -1, i);
    std::string mod = modOf(L, -1);
    lua_pop(L, 1);
    auto now = std::chrono::steady_clock::now();
    auto [it, fresh] = told.try_emplace(mod + "." + what, now);
    if (!fresh && now - it->second < std::chrono::seconds(10)) return;
    it->second = now;
    char line[256];
    snprintf(line, sizeof line, LV_WARN "%s: A %s callback took %.0f ms (the game waits for it)", mod.c_str(), what, ms);
    gSink(line);
}

// Runs every callback in a list: Tick.add, Draw.add or a registerForEvent event (what names it in the error line).
// A callback that errors is dropped so it can't repeat the error every tick or frame. Tick callbacks get dt, and Draw
// callbacks get ImGui's stacks restored, so a Begin without End can't break the frame.
void runCallbacks(lua_State* L, const char* reg, const char* what) {
    bool draw = reg == kDraws, tick = reg == kTicks;
    lua_getfield(L, LUA_REGISTRYINDEX, reg);
    for (lua_Integer i = 1, count = lua_Integer(lua_rawlen(L, -1)); i <= count; i++) {
        lua_rawgeti(L, -1, i);
        if (!lua_isfunction(L, -1)) {
            lua_pop(L, 1);
            continue;
        }
        if (draw) rt::imguiMark();
        if (tick) lua_pushnumber(L, gDt);
        auto start = std::chrono::steady_clock::now();
        if (!call(L, tick ? 1 : 0, 0)) {
            if (draw) rt::imguiRecover();
            dropCallback(L, i, (std::string(what) + " callback").c_str());
            continue;
        }
        if (draw) rt::imguiRecover();
        double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
        if ((tick || draw) && ms > kSlowMs) warnSlow(L, i, what, ms);
    }
    lua_pop(L, 1);
}

bool isEvent(refl::Type t) {
    for (int i = 0; t && t.prim() == refl::Struct && i < 32; i++, t = t.inner()) {
        if (t.qname() == "keen::ecs::Event") return true;
    }
    return false;
}

refl::Type eventType(lua_State* L, int arg) {
    const char* name = luaL_checkstring(L, arg);
    refl::Type t = refl::byName(name);
    if (!t || !isEvent(t)) luaL_error(L, "'%s' is not an event type", name);
    return t;
}

// Observe(name, fn): fn(event) for every event of that type, after each server tick.
int observe(lua_State* L) {
    refl::Type t = eventType(L, 1);
    luaL_checktype(L, 2, LUA_TFUNCTION);
    markFunction(L, 2);
    gObserving = true;
    lua_getfield(L, LUA_REGISTRYINDEX, kObservers);
    if (lua_rawgeti(L, -1, t.hash()) == LUA_TNIL) {  // handler list for this type
        lua_pop(L, 1);
        lua_newtable(L);
        lua_pushvalue(L, -1);
        lua_rawseti(L, -3, t.hash());
    }
    lua_pushvalue(L, 2);
    lua_rawseti(L, -2, lua_Integer(lua_rawlen(L, -2)) + 1);
    return 0;
}

// Events.post(name, fields): fields are checked in a scratch copy first, so a bad table posts nothing.
// GameEvent.timeStamp is set to now. Returns false if the game's event buffer is full.
int eventsPost(lua_State* L) {
    refl::Type t = eventType(L, 1);
    luaL_checktype(L, 2, LUA_TTABLE);
    if (t.size() >= 0x100) return luaL_error(L, "%s is too big to post", qname(t).c_str());
    needGame(L);
    needWorld(L);
    if (!gWorld) return luaL_error(L, "no world to post %s to", qname(t).c_str());
    switch (int idx = ecs::eventIndex(gWorld, t.hash()); idx < 0 ? KilnResult(-idx) : KILN_OK) {
        case KILN_OK: break;
        case KILN_ERR_NOT_FOUND: return luaL_error(L, "this world has no %s stream", qname(t).c_str());
        case KILN_ERR_WRONG_THREAD: return luaL_error(L, "Events.post: not on the world's tick thread");
        case KILN_ERR_UNAVAILABLE: return luaL_error(L, "events are unavailable on this game build");
        case KILN_ERR_INVALID_ARGUMENT:
            return luaL_error(L, "%s: refused (not a known world, or its stream does not match the reflected size)", qname(t).c_str());
        default: return luaL_error(L, "%s: events failed (error %d)", qname(t).c_str(), -idx);
    }
    uint8_t scratch[0x100] = {};
    Ref r{0, -1, 0, t.size(), t.p, scratch, gGen, true};
    refl::Field ts;
    if (refl::field(t, "timeStamp", ts) && ts.type.size() == 8 && ts.offset <= t.size() && 8 <= t.size() - ts.offset) {
        int64_t now = ecs::simTime(gWorld);
        memcpy(scratch + ts.offset, &now, 8);
    }
    fill(L, r, 0, t, 2);
    lua_pushboolean(L, ecs::postEvent(gWorld, t.hash(), scratch, t.size()));
    return 1;
}

lua_Integer enumArg(lua_State* L, int arg, refl::Type t) {
    if (lua_type(L, arg) == LUA_TNUMBER) {
        lua_Integer v = luaL_checkinteger(L, arg);
        luaL_argcheck(L, t.fieldCount() && refl::enumHas(t, v), arg, lua_pushfstring(L, "not a value of %s", qname(t).c_str()));
        return v;
    }
    std::string want = lower(luaL_checkstring(L, arg));
    lua_Integer v = 0;
    bool found = false;
    refl::forEnum(t, [&](std::string_view n, int64_t e) {
        if (lower(n) == want) { v = e; found = true; }
    });
    if (!found) luaL_argerror(L, arg, lua_pushfstring(L, "not a value of %s", qname(t).c_str()));
    return v;
}

#include "admin.inl"

// Calls the handler list on top of the stack with a read-only proxy of ev. A handler that errors is dropped.
void callHandlers(lua_State* L, refl::Type t, const uint8_t* ev) {
    for (lua_Integer i = 1, count = lua_Integer(lua_rawlen(L, -1)); i <= count; i++) {
        lua_rawgeti(L, -1, i);
        if (!lua_isfunction(L, -1)) {
            lua_pop(L, 1);
            continue;
        }
        newRef(L, {0, -1, 0, t.size(), t.p, const_cast<uint8_t*>(ev), gGen, false});
        if (!call(L, 1, 0)) dropCallback(L, i, "event handler");
    }
}

// Runs Observe handlers for this tick's events. Types are collected first, so a handler may call Observe.
void dispatchEvents(lua_State* L, uintptr_t world) {
    std::vector<uint32_t> types;
    lua_getfield(L, LUA_REGISTRYINDEX, kObservers);
    lua_pushnil(L);
    while (lua_next(L, -2)) {
        // Unloads and errors leave false in place of a handler: a list without a function left is not read.
        bool live = false;
        if (lua_istable(L, -1))
            for (lua_Integer i = 1, n = lua_Integer(lua_rawlen(L, -1)); i <= n && !live; i++) {
                live = lua_rawgeti(L, -1, i) == LUA_TFUNCTION;
                lua_pop(L, 1);
            }
        if (live) types.push_back(static_cast<uint32_t>(lua_tointeger(L, -2)));
        lua_pop(L, 1);
    }
    if (types.empty()) gObserving = false;  // every handler is gone: afterTick returns at once until the next Observe
    for (uint32_t h : types) {
        refl::Type t = refl::byHash(h);
        lua_rawgeti(L, -1, h);
        int idx = t ? ecs::forEachEvent(world, h, [&](const uint8_t* ev) { callHandlers(L, t, ev); }) : -1;
        if (idx < 0) {
            // Report once per type instead of skipping silently (no stream on this world, or kiln.types refused it).
            static std::vector<uint32_t> reported;
            if (std::find(reported.begin(), reported.end(), h) == reported.end() && reported.size() < 256) {
                reported.push_back(h);
                gSink((LV_WARN "Observe: " + (t ? qname(t) : std::string("unknown type")) +
                       " cannot be read on this world (error " + std::to_string(-idx) + "); its handlers will not run").c_str());
            }
        }
        lua_pop(L, 1);
    }
    lua_pop(L, 1);
    gGen++;  // proxies handed out during this dispatch are invalid from now on
}

// The game's own window, not the runtime console: there F-keys are line editing (F6 types ^Z).
bool gameFocused() {
    HWND fg = GetForegroundWindow();
    DWORD pid = 0;
    GetWindowThreadProcessId(fg, &pid);
    return pid == GetCurrentProcessId() && fg != GetConsoleWindow();
}

bool keyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

// A binding is active while its key and exactly its modifiers are held. A hotkey fires when it becomes active; an
// input gets true then and false when it stops, which losing focus also does. The callbacks run after gBindMx is
// released, since they may register bindings.
void pollBinds(lua_State* L, bool focus) {
    std::vector<std::pair<std::string, int>> fire;  // key, and -1 for a hotkey or the input's new state
    int mods = rt::heldMods();
    {
        std::lock_guard lock(gBindMx);
        for (Binding& b : gBinds) {
            bool now = focus && b.vk && keyDown(b.vk) && mods == b.mods;
            if (now == b.held) continue;
            b.held = now;
            if (b.input || now) fire.emplace_back(b.key, b.input ? int(now) : -1);
        }
    }
    if (fire.empty()) return;
    lua_getfield(L, LUA_REGISTRYINDEX, kBinds);
    for (const auto& [key, arg] : fire) {
        lua_getfield(L, -1, key.c_str());
        if (arg >= 0) lua_pushboolean(L, arg);
        call(L, arg >= 0 ? 1 : 0, 0);
    }
    lua_pop(L, 1);
}

// Calls Keys.on handlers once per key press (down edge), and the bound hotkeys and inputs, only while the game window
// has focus.
void pollKeys(lua_State* L) {
    static bool down[256];
    bool focus = gameFocused() && !gOverlayOpen;
    lua_getfield(L, LUA_REGISTRYINDEX, kKeys);
    lua_pushnil(L);
    while (lua_next(L, -2)) {
        int vk = int(lua_tointeger(L, -2)) & 0xff;
        bool now = keyDown(vk);
        bool press = now && !down[vk] && focus;
        down[vk] = now;
        if (press) {
            lua_pushvalue(L, -1);
            call(L, 0, 0);
        }
        lua_pop(L, 1);
    }
    lua_pop(L, 1);
    pollBinds(L, focus);
}

// True once a server world looks like the layout we know: Health is registered and entities exist. Checked every
// tick until it passes, because the first ticks of a world can come before it is populated.
bool worldReady(uintptr_t world) {
    static uintptr_t seen;
    static bool ok, warned;
    static ULONGLONG last;
    static int64_t lastSim;
    ULONGLONG now = GetTickCount64();
    int64_t sim = ecs::simTime(world);
    // A new session: another world, or ticks resuming after a pause (main menu, then a load). A reloaded world
    // often gets the same address, so the pause matters. A death and respawn pauses too, but there the session's
    // sim time goes on; a load starts it again.
    if (world != seen || now - last > 2000) {
        gNewWorld |= world != seen || sim < lastSim;
        seen = world;
        ok = warned = false;
        resetAdmin();
    }
    last = now;
    lastSim = sim;
    if (ok) return true;
    refl::Type hp = refl::byName("Health");
    ok = hp && ecs::compIndex(world, hp.hash()) >= 0 && ecs::entityCount(world) > 0;
    char line[160];
    if (ok) snprintf(line, sizeof line, LV_OK "Server World: %p: %u Entities, Sim Time: %.1fs.", reinterpret_cast<void*>(world),
                     ecs::entityCount(world), double(sim) / 1e9);
    else snprintf(line, sizeof line, LV_WARN "Server World: %p Not recognized or ready yet: Waiting", reinterpret_cast<void*>(world));
    if (ok || !std::exchange(warned, true)) gSink(line);
    return ok;
}

std::vector<std::string> takeQueue() {
    std::lock_guard lock(gQueueMx);
    return std::exchange(gQueue, {});
}

void startState() {
    gL = rt::open();
    gSeenOpen = false;  // an overlay that is already open gets onOverlayOpen at the next tick
    loadBindings();
    loadMods(gL);
    gLoaded = true;
    runCallbacks(gL, kOnInit, "onInit");
}

// onShutdown runs first, so mods can still save their files.
void closeState() {
    setinspect::clear();
    gHookShutdown = true;
    stopHooks(gL);
    runCallbacks(gL, kOnShutdown, "onShutdown");
    mset::dropAll(gSink);  // after onShutdown, so what a mod sets there is saved too
    resetAdmin();
    for (const auto& [name, owner] : gSystemOwners) { (void)name; sys::releaseOwner(owner); }
    gSystemOwners.clear();
    { kset::budget::Scope budget; lua_close(std::exchange(gL, nullptr)); }
    gLoaded = false;
    gHookShutdown = false;
}

// onWorldLeave, without the game: the world is going away, or already gone when its leave went unseen.
void leaveWorld() {
    setinspect::clear();
    hk::discardPending();
    rules::setPlayer(0);
    gInWorld = false;
    gSink(LV_LOG "Left the world");
    gLeaving = true;
    runCallbacks(gL, kOnLeave, "onWorldLeave");
    gLeaving = false;
}

// A new session's first ready tick, with the game. A world whose leave went unseen is left first.
void enterWorld() {
    if (gInWorld) leaveWorld();
    hk::discardPending();
    gInWorld = true;
    gSink(LV_LOG "Entered a world");
    runCallbacks(gL, kOnEnter, "onWorldEnter");
}

// onOverlayOpen or onOverlayClose when the overlay changed since the last tick. On the tick, not the render thread, so
// handlers can reach the game in a world.
void overlayEvents() {
    bool open = gOverlayOpen;
    if (open != std::exchange(gSeenOpen, open))
        runCallbacks(gL, open ? kOnOpen : kOnClose, open ? "onOverlayOpen" : "onOverlayClose");
}

void global(lua_State* L, const char* name, const luaL_Reg* fns) {
    lua_newtable(L);
    luaL_setfuncs(L, fns, 0);
    lua_setglobal(L, name);
}

// Lua deliberately disables instruction hooks inside GC finalizers. Mods cannot install Lua finalizers;
// otherwise a single __gc infinite loop can bypass every callback budget, including during lua_close.
int safeSetmetatable(lua_State* L) {
    luaL_checktype(L, 1, LUA_TTABLE);
    if (!lua_isnil(L, 2)) luaL_checktype(L, 2, LUA_TTABLE);
    if (lua_getmetatable(L, 1)) {
        lua_getfield(L, -1, "__metatable"); bool locked = !lua_isnil(L, -1); lua_pop(L, 2);
        if (locked) return luaL_error(L, "cannot change a protected metatable");
    }
    if (!lua_isnil(L, 2)) {
        lua_getfield(L, 2, "__gc"); bool finalizer = !lua_isnil(L, -1); lua_pop(L, 1);
        if (finalizer) return luaL_error(L, "Lua finalizers are disabled in the sandbox");
    }
    lua_settop(L, 2); lua_setmetatable(L, 1); lua_pushvalue(L, 1); return 1;
}
int safeRawset(lua_State* L) {
    luaL_checktype(L, 1, LUA_TTABLE);
    if (lua_getmetatable(L, 1)) {
        lua_getfield(L, -1, "__newindex");
        bool frozen = lua_iscfunction(L, -1);
        lua_pop(L, 2);
        if (frozen) return luaL_error(L, "shared API tables cannot be changed");
    }
    luaL_checkany(L, 2); luaL_checkany(L, 3);
    lua_settop(L, 3); lua_rawset(L, 1); lua_pushvalue(L, 1); return 1;
}
int frozenWrite(lua_State* L) {
    return luaL_error(L, "%s is shared by every mod and can't be changed", lua_tostring(L, lua_upvalueindex(1)));
}

// pairs() on a frozen table lists the real one. Upvalues: next, the real table.
int frozenNext(lua_State* L) {
    lua_settop(L, 2);
    lua_pushvalue(L, lua_upvalueindex(1));
    lua_pushvalue(L, 2);
    return lua_next(L, -2) ? 2 : 0;
}
int frozenPairs(lua_State* L) {
    lua_pushvalue(L, lua_upvalueindex(2));
    lua_pushcclosure(L, frozenNext, 1);
    lua_pushnil(L); lua_pushnil(L);
    return 3;
}

// Puts the global table `name` behind a read-only proxy. Every mod shares it, so one mod's assignment (Ecs.player =
// nil) would change it for all of them. rawset and pairs cannot expose or change the backing table.
void freeze(lua_State* L, const char* name) {
    lua_getglobal(L, name);
    int real = lua_gettop(L);
    lua_newtable(L);
    lua_createtable(L, 0, 4);
    lua_pushvalue(L, real);
    lua_setfield(L, -2, "__index");
    lua_pushstring(L, name);
    lua_pushcclosure(L, frozenWrite, 1);
    lua_setfield(L, -2, "__newindex");
    lua_getglobal(L, "next");
    lua_pushvalue(L, real);
    lua_pushcclosure(L, frozenPairs, 2);
    lua_setfield(L, -2, "__pairs");
    lua_pushboolean(L, 0);
    lua_setfield(L, -2, "__metatable");  // getmetatable gives false, setmetatable fails
    lua_setmetatable(L, -2);
    lua_setglobal(L, name);
    lua_pop(L, 1);
}

int getVersion(lua_State* L) {
    lua_pushliteral(L, RUNTIME_VERSION);
    return 1;
}

// GetGameBuild(): the running exe's build (its link time stamp), as Status shows it, so a mod can pick per build.
int getGameBuild(lua_State* L) {
    lua_pushinteger(L, gBuild);
    return 1;
}

// Features(): {id = {compatible, available, reason, label}}, a new table on every call.
int luaFeatures(lua_State* L) {
    auto all = rt::features();
    lua_createtable(L, 0, static_cast<int>(all.size()));
    for (const rt::Feature& f : all) {
        lua_createtable(L, 0, 4);
        lua_pushboolean(L, f.compatible);
        lua_setfield(L, -2, "compatible");
        lua_pushboolean(L, f.available);
        lua_setfield(L, -2, "available");
        lua_pushstring(L, f.reason.c_str());
        lua_setfield(L, -2, "reason");
        lua_pushstring(L, f.label.c_str());
        lua_setfield(L, -2, "label");
        lua_setfield(L, -2, f.id.c_str());
    }
    return 1;
}

// The overlay's state comes from the plugin (rt::setFeature); everything else is read from Kiln's services when asked.
std::mutex gFeatMx;
bool gOverlaySet = false, gOverlayOk = false;
std::string gOverlayWhy;

// Key names, both ways. Punctuation is named after the US layout, in words: bindings.ini values lose surrounding
// quotes, and a name may not contain '+', which splits combos. (SET 0.2.0's table.)
constexpr std::pair<const char*, int> kNamedKeys[] = {
    {"`", VK_OEM_3},         {"Insert", VK_INSERT},    {"Home", VK_HOME},          {"End", VK_END},
    {"Delete", VK_DELETE},   {"PageUp", VK_PRIOR},     {"PageDown", VK_NEXT},      {"Pause", VK_PAUSE},
    {"Space", VK_SPACE},     {"Tab", VK_TAB},          {"Enter", VK_RETURN},       {"Backspace", VK_BACK},
    {"CapsLock", VK_CAPITAL}, {"Left", VK_LEFT},       {"Up", VK_UP},              {"Right", VK_RIGHT},
    {"Down", VK_DOWN},       {"PrintScreen", VK_SNAPSHOT}, {"ScrollLock", VK_SCROLL}, {"NumLock", VK_NUMLOCK},
    {"Num0", VK_NUMPAD0},    {"Num1", VK_NUMPAD1},     {"Num2", VK_NUMPAD2},       {"Num3", VK_NUMPAD3},
    {"Num4", VK_NUMPAD4},    {"Num5", VK_NUMPAD5},     {"Num6", VK_NUMPAD6},       {"Num7", VK_NUMPAD7},
    {"Num8", VK_NUMPAD8},    {"Num9", VK_NUMPAD9},     {"NumMultiply", VK_MULTIPLY}, {"NumAdd", VK_ADD},
    {"NumSubtract", VK_SUBTRACT}, {"NumDecimal", VK_DECIMAL}, {"NumDivide", VK_DIVIDE}, {"Semicolon", VK_OEM_1},
    {"Equals", VK_OEM_PLUS}, {"Comma", VK_OEM_COMMA},  {"Minus", VK_OEM_MINUS},    {"Period", VK_OEM_PERIOD},
    {"Slash", VK_OEM_2},     {"LeftBracket", VK_OEM_4}, {"Backslash", VK_OEM_5},   {"RightBracket", VK_OEM_6},
    {"Quote", VK_OEM_7},     {"<>", VK_OEM_102}};

}  // namespace

void rt::configure(const std::string& modsDir, Sink sink) {
    gModsDir = modsDir;
    gSink = sink;
}

lua_State* rt::open() {
    lua_State* L = luaL_newstate();
    openSafeLibs(L);
    luaL_newmetatable(L, kRefMt);
    const luaL_Reg meta[] = {{"__index", refIndex}, {"__newindex", refNewIndex}, {"__len", refLen},
                             {"__tostring", refToString}, {nullptr, nullptr}};
    luaL_setfuncs(L, meta, 0);
    lua_pushboolean(L, 0); lua_setfield(L, -2, "__metatable");
    lua_pop(L, 1);
    const luaL_Reg ecsFns[] = {{"get", ecsGet},       {"player", ecsPlayer}, {"each", ecsEach},
                               {"fields", ecsFields}, {"types", ecsTypes},   {nullptr, nullptr}};
    global(L, "Ecs", ecsFns);
    const luaL_Reg resFns[] = {{"get", resGet},     {"each", resEach}, {"find", resFind},
                               {"types", resTypes}, {"text", resText}, {nullptr, nullptr}};
    global(L, "Res", resFns);
    const luaL_Reg optsFns[] = {{"machine", optsMachine}, {"user", optsUser}, {"dump", optsDump}, {nullptr, nullptr}};
    global(L, "GameOptions", optsFns);
    const luaL_Reg sysFns[] = {{"list", systemsList},       {"enable", systemsEnable}, {"enabled", systemsEnabled},
                               {"profile", systemsProfile}, {"stats", systemsStats},   {nullptr, nullptr}};
    global(L, "Systems", sysFns);
    const luaL_Reg typesFns[] = {{"fields", typesFields}, {"enum", typesEnum}, {"new", typesNew},
                                 {"copy", typesCopy},     {nullptr, nullptr}};
    global(L, "Types", typesFns);
    const luaL_Reg tickFns[] = {{"add", tickAdd}, {nullptr, nullptr}}, keysFns[] = {{"on", keysOn}, {nullptr, nullptr}},
                   eventsFns[] = {{"post", eventsPost}, {nullptr, nullptr}},
                   drawFns[] = {{"add", drawAdd}, {nullptr, nullptr}},
                   overlayFns[] = {{"isOpen", overlayIsOpen}, {nullptr, nullptr}};
    global(L, "Tick", tickFns);
    global(L, "Keys", keysFns);
    global(L, "Events", eventsFns);
    global(L, "Draw", drawFns);
    global(L, "Overlay", overlayFns);
    const luaL_Reg adminFns[] = {{"send", adminSend},         {"setTime", adminSetTime},
                                 {"teleport", adminTeleport}, {"setLevel", adminSetLevel},
                                 {"setFlameLevel", adminSetFlameLevel}, {"settings", adminSettings},
                                 {"changeSettings", adminChangeSettings}, {"spawn", adminSpawn}, {"give", adminGive},
                                 {nullptr, nullptr}};
    global(L, "Admin", adminFns);
    rt::openImGui(L);
    rt::openJson(L);
    lua_register(L, "Observe", observe);
    lua_register(L, "print", luaPrint);
    lua_register(L, "reload", luaReload);
    lua_register(L, "GetMod", getMod);
    lua_register(L, "registerForEvent", registerForEvent);
    lua_register(L, "GetVersion", getVersion);
    lua_register(L, "GetGameBuild", getGameBuild);
    lua_register(L, "Features", luaFeatures);
    gObserving = false;  // a fresh observer table
    for (const char* reg : {kTicks, kDraws, kKeys, kObservers, kMods, kBinds, kOnInit, kOnOpen, kOnClose, kOnShutdown,
                            kOnEnter, kOnLeave, kOnChange}) {
        lua_newtable(L);
        lua_setfield(L, LUA_REGISTRYINDEX, reg);
    }
    lua_register(L, "setmetatable", safeSetmetatable);
    lua_register(L, "rawset", safeRawset);
    lua_pushliteral(L, "");
    if (lua_getmetatable(L, -1)) { lua_pushboolean(L, 0); lua_setfield(L, -2, "__metatable"); lua_pop(L, 1); }
    lua_pop(L, 1);
    for (const char* name : {"string", "table", "math", "coroutine", "utf8", "os", "Ecs", "Res", "GameOptions", "Systems", "Types", "Tick", "Keys", "Events", "Draw", "Overlay", "Admin",
                             "ImGui", "ImGuiCond", "ImGuiWindowFlags", "json"})
        freeze(L, name);
    lua_newtable(L);
    lua_newtable(L); lua_pushliteral(L, "k"); lua_setfield(L, -2, "__mode"); lua_setmetatable(L, -2);
    lua_setfield(L, LUA_REGISTRYINDEX, kFunctionMods);
    gHasDraw = false;
    return L;
}

void rt::setWorld(uintptr_t world) { gWorld = world; }

void rt::exec(lua_State* L, const std::string& line) {
    struct ConsoleScope {
        bool prior = std::exchange(gInConsole, true);
        ~ConsoleScope() { gInConsole = prior; }
    } scope;
    int top = lua_gettop(L);
    std::string expr = "return " + line;  // expressions print their value, like the standalone lua REPL
    if (luaL_loadbufferx(L, expr.data(), expr.size(), "=console", "t") != LUA_OK) {
        lua_pop(L, 1);
        if (luaL_loadbufferx(L, line.data(), line.size(), "=console", "t") != LUA_OK) {
            gSink((LV_BAD + std::string(lua_tostring(L, -1))).c_str());
            lua_settop(L, top);
            return;
        }
    }
    if (call(L, 0, LUA_MULTRET) && lua_gettop(L) > top) {
        lua_getglobal(L, "print");
        lua_insert(L, top + 1);
        call(L, lua_gettop(L) - top - 1, 0);
    }
    lua_settop(L, top);
}

namespace {

// One tick of the loaded mods: queued lines, overlay events, hook handlers, Tick callbacks, keys, admin commands,
// reload(). world is 0 on the main thread, outside a world. dt is timed here, so it stays true when the other thread
// takes over.
// The host player's entity, for rules comparing with "player": the last one is kept while it still has PlayerInput, so
// the entity list is asked only when it changes. 0 outside a world. Skipped while no rule is registered.
void updatePlayer(uintptr_t world) {
    static uint32_t last;
    bool any = false;
    for (const auto& [id, h] : gHooks) { (void)id; any |= h.rule >= 0; }
    if (!any || !world) { rules::setPlayer(last = 0); return; }
    refl::Type pi = refl::byName("PlayerInput");
    int idx = pi ? ecs::compIndex(world, pi.hash()) : -1;
    if (idx < 0) last = 0;
    else if (!last || !ecs::component(world, last, idx)) last = ecs::firstWith(world, idx);
    rules::setPlayer(last);
}

void tickMods(uintptr_t world) {
    static auto last = std::chrono::steady_clock::now();
    updatePlayer(world);
    auto now = std::chrono::steady_clock::now();
    gDt = std::chrono::duration<double>(now - std::exchange(last, now)).count();
    if (world) checkSent(world);
    for (const std::string& line : takeQueue()) rt::exec(gL, line);
    overlayEvents();
    settingsEdits(gL);
    hk::flushLogs();
    if (!gReload) dispatchHooks(gL);
    runCallbacks(gL, kTicks, "tick");
    pollKeys(gL);
    if (world) armAdmin(world);
    if (gReload.exchange(false)) {
        gSink("Reloading mods");
        closeState();
        wlog::leaveAll(gSink);  // after onShutdown, so a mod's own restore comes first
        startState();
        if (gInWorld) runCallbacks(gL, kOnEnter, "onWorldEnter");  // set up as on entering, in either case
    }
    mset::save(false, gSink);
    setinspect::tick(world);
}

// guarded's SEH half: __try can't share a function with C++ objects to destroy or a C++ try.
bool noAccessViolation(void (*fn)(uintptr_t), uintptr_t arg) {
    __try {
        fn(arg);
        return true;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        return false;
    }
}

// Runs Lua work under gLuaMx, which the caller holds. An access violation (a game update can move what a mod reads)
// is caught here, inside the lock, so the caller still unlocks: caught outside it, /EHsc would skip the unlock and
// freeze the next thread that waits for Lua. The Lua state is unusable afterwards, since Lua's error handling still
// points into the skipped frames, so no Lua runs again.
void guarded(void (*fn)(uintptr_t), uintptr_t arg, const char* what) {
    try {
        if (noAccessViolation(fn, arg)) return;
        gBroken = true;
        gLoaded = gHasDraw = false;
        setinspect::clear();
        stopHooks(nullptr);
        gCurrentMod.clear();
        gCurrentIsMod = false;
        if (!gClosing) wlog::leaveAll(gSink);  // C++ only: no Lua runs again
        mset::dropAll(gSink);
        gSink(LV_BAD "Lua: Access violation, mods off until the game restarts");
    } catch (...) {
        // /EHa catches non-AV hardware faults here too. The Lua state must never be entered again.
        const bool wasBroken = gBroken.exchange(true);
        gLoaded = gHasDraw = false;
        setinspect::clear();
        if (wasBroken) return;  // do not retry cleanup that faulted in the AV path above
        stopHooks(nullptr);
        gSink((LV_BAD "Internal error in " + std::string(what) + "; mods off until the game restarts").c_str());
    }
}

}  // namespace

void rt::tick(uintptr_t world) {
    std::lock_guard lock(gLuaMx);
    gReady = 0;
    if (gBroken || gClosing) return;
    guarded([](uintptr_t w) {
        if (!worldReady(w)) return;
        gReady = gWorld = w;
        if (!gL) startState();  // without the main thread's frame hook, mods load in the first world
        if (std::exchange(gNewWorld, false)) { ecs::forgetEvents(); enterWorld(); }
        if (gInWorld) tickMods(w);  // after a leave, the world ticks on while it shuts down: no mods there
    }, world, "the Lua tick");
    gWorld = 0;
    gLeaving = false;
}

// The main thread, every frame (the options update): mods load here once the game is ready, and run here while no
// world you host runs them.
void rt::frame() {
    if (gInWorld || gClosing || gBroken) return;
    std::lock_guard lock(gLuaMx);
    if (gInWorld || gClosing || gBroken) return;  // a world started, or the game began closing, while this waited
    guarded([](uintptr_t) {
        if (!gL) startState();
        tickMods(0);
    }, 0, "the Lua frame");
}

// The player left the world: onWorldLeave runs now, without the game, and the main thread runs the mods again.
void rt::worldLeft() {
    std::lock_guard lock(gLuaMx);
    ecs::forgetEvents();
    setinspect::clear();
    resetAdmin();
    if (!gInWorld || gBroken) return;
    guarded([](uintptr_t) { leaveWorld(); }, 0, "onWorldLeave");
    gLeaving = false;
}

// The game started closing: onWorldLeave if still in a world, then onShutdown, both without the game. No mods after.
// gameAlive: the game runs on without the mods (kiln-set steps aside for SET's own runtime), so what the mods changed
// in the game's resources goes back. When the game itself closes, nothing is restored: its data is going away.
void rt::gameClosing(bool gameAlive) {
    std::lock_guard lock(gLuaMx);
    gClosing = true;
    ecs::forgetEvents();
    setinspect::clear();
    resetAdmin();
    rules::setPlayer(0);
    if (gL && !gBroken)
        guarded([](uintptr_t) {
            if (gInWorld) leaveWorld();
            gSink("Game Closing: Unloading mods");
            closeState();
        }, 0, "onShutdown");
    gLeaving = false;
    if (gameAlive && !gBroken) guarded([](uintptr_t) { wlog::leaveAll(gSink); }, 0, "the write log");
    else wlog::forget();
}

void rt::afterTick(uintptr_t world) {
    if (!gObserving) return;  // no mod has called Observe: no per-tick work at all
    std::lock_guard lock(gLuaMx);
    if (!gL || gBroken || !gInWorld || world != gReady) return;
    gWorld = world;
    guarded([](uintptr_t w) { dispatchEvents(gL, w); }, world, "event dispatch");
    gWorld = 0;
}

void rt::queue(std::string line) {
    std::lock_guard lock(gQueueMx);
    gQueue.push_back(std::move(line));
}

// Draw callbacks, on the render thread inside the overlay's ImGui frame. Waits for the mods' tick or frame.
void rt::draw() {
    gLuaMx.lock();
    drawLocked();
}

// The overlay's way in: takes the Lua lock only if no tick or frame holds it, so a present never waits for a mod.
bool rt::tryLockDraw() { return gLuaMx.try_lock(); }

// Draw callbacks under the lock tryLockDraw (or draw) took. Releases it.
void rt::drawLocked() {
    std::lock_guard lock(gLuaMx, std::adopt_lock);
    if (!gL || gBroken) return;
    gInDraw = true;
    guarded([](uintptr_t) { runCallbacks(gL, kDraws, "draw"); }, 0, "Draw");
    gInDraw = false;
    if (gBroken) rt::imguiRecover();  // the callback the access violation stopped may have left windows open
}

// overlay_selftest only: a helper thread holds the Lua lock, as a long tick would, and Draw counts as registered.
void rt::testHoldLua(bool on) {
    static std::thread holder;
    static std::atomic<bool> hold, held;
    if (on == holder.joinable()) return;
    if (!on) {
        hold = false;
        holder.join();
        gHasDraw = false;
        return;
    }
    gHasDraw = hold = true;
    held = false;
    holder = std::thread([] {
        std::lock_guard lock(gLuaMx);
        held = true;
        while (hold) Sleep(1);
    });
    while (!held) Sleep(1);
}

bool rt::inDraw() { return gInDraw; }
bool rt::hasDraw() { return gHasDraw; }
void rt::setOverlayOpen(bool open) { gOverlayOpen = open; }
void rt::requestReload() { gReload = true; }
void rt::setGameBuild(uint32_t build) { gBuild = build; }
uint32_t rt::gameBuild() { return gBuild; }
bool rt::modsLoaded() { return gLoaded; }

std::vector<rt::ModReport> rt::modReports() {
    std::lock_guard lock(gReportMx);
    return gReports;
}

void rt::onModsReported(void (*fn)()) { gOnReports = fn; }

void rt::bindKiln(const KilnApi* api, KilnOwner owner) {
    gKApi = api;
    gKOwner = owner;
    auto get = [&](const char* n, uint32_t v) { return api->service(KilnStr{n, strlen(n)}, v); };
    gKTime = static_cast<const KilnTimeApi*>(get(KILN_TIME_SERVICE, 1));
    gKWorld = static_cast<const KilnWorldApi*>(get(KILN_WORLD_SERVICE, 1));
    gKSpawn = static_cast<const KilnSpawnApi*>(get(KILN_SPAWN_SERVICE, 1));
    gKItems = static_cast<const KilnItemsApi*>(get(KILN_ITEMS_SERVICE, 1));
    gKSettings = static_cast<const KilnSettingsApi*>(get(KILN_SETTINGS_SERVICE, 1));
    gKProgression = static_cast<const KilnProgressionApi*>(get(KILN_PROGRESSION_SERVICE, KILN_PROGRESSION_VERSION));
    gKKnowledge = static_cast<const KilnKnowledgeApi*>(get(KILN_KNOWLEDGE_SERVICE, KILN_KNOWLEDGE_VERSION));
    resetNames();
    settexts::reset();
}
bool rt::systemsAvailable() { return gSysOk; }

void rt::setFeature(const char* id, bool compatible, std::string reason, bool ready) {
    if (strcmp(id, "overlay") != 0) return;  // the only state the plugin owns; unknown ids are ignored
    std::lock_guard lock(gFeatMx);
    gOverlaySet = true;
    gOverlayOk = compatible && ready;
    gOverlayWhy = std::move(reason);
}

// SET's ids, in its Compatibility window's order, with Kiln's state behind them; then Kiln's own ids for what a SET
// mod may rely on and Kiln doesn't have. An id is never renamed or dropped once shipped.
std::vector<rt::Feature> rt::features() {
    const uint32_t build = gBuild;
    const bool tested = testedBuild(build);
    const bool world = gInWorld;
    std::vector<Feature> out;
    auto add = [&](const char* id, const char* label, bool compatible, bool available, std::string reason) {
        out.push_back({id, label, compatible && available ? std::string() : std::move(reason), compatible, compatible && available});
    };
    // usable only in a world you host
    auto inWorld = [&](const char* id, const char* label, bool compatible, const char* missing) {
        add(id, label, compatible, world, compatible ? "Not in a world you host" : missing);
    };
    out.push_back({"game_build", "Game Build " + std::to_string(build),
                   tested ? "" : "Not tested with this version: Each feature checks itself", tested, tested});
    const bool assets = kset::has("assets");
    add("resources", "Resources (Res)", assets, res::ready(), assets ? "Not loaded yet" : "kiln.assets is not running");
    add("english_names", "English Names (Res.text)", true, true, "");
    add("game_options", "Game Options (GameOptions)", false, false, "Not available on Kiln");
    add("field_of_view", "Live Field of View", false, false, "Needs GameOptions: Not available on Kiln");
    add("systems", "Systems", gSysOk, true, "kiln.types can't switch systems on this game build");
    inWorld("events", "Events (Observe, Events.post)", kset::has("types"), "kiln.types is not running");
    inWorld("admin", "Admin Commands (Admin.*)", gKTime || gKWorld || gKSpawn || gKProgression || gKKnowledge,
            "Kiln's admin services are not running");
    inWorld("admin_give", "Admin.give", gKItems != nullptr, "kiln.items is not running");
    inWorld("admin_settings", "Game Settings (Admin.settings)", gKSettings != nullptr, "kiln.settings is not running");
    {
        std::lock_guard lock(gFeatMx);
        add("overlay", "Overlay (Draw, ImGui)", gOverlaySet && gOverlayOk, true, gOverlaySet ? gOverlayWhy : "Not checked yet");
    }
    auto checked = contracts::checked();
    for (const contracts::Entry& e : contracts::catalog()) {
        auto s = std::find_if(checked.begin(), checked.end(), [&](const contracts::Status& c) { return c.name == e.name; });
        bool ok = s != checked.end() && s->fn;
        out.push_back({e.name, std::string("Rule Target ") + e.name, ok ? "" : s != checked.end() ? s->why : "Not checked yet",
                       ok, ok});
    }
    // Kiln's own ids.
    std::string why;
    const bool hooks = hk::rulesAvailable(why);
    add("hooks", "Native Hooks (Hooks.*)", hooks, true, "kiln.hooks is not running");
    add("admin_send", "Admin.send", false, false, "Not available on Kiln: Use the Admin helpers");
    {
        std::lock_guard lock(gFeatMx);
        add("mod_settings_page", "Mod Settings Page", gOverlaySet && gOverlayOk, true,
            gOverlaySet ? gOverlayWhy : "Not checked yet");
    }
    return out;
}
void rt::setSystemsAvailable(bool ok) { gSysOk = ok; }

// Key names shared by Keys.on and the overlay key setting: F1-F24, A-Z, 0-9, a few named keys, or a 0xNN code.
int rt::keyFromName(const char* n) {
    for (const auto& [name, vk] : kNamedKeys)
        if (_stricmp(n, name) == 0) return vk;
    char* end = nullptr;
    if (n[0] == '0' && (n[1] == 'x' || n[1] == 'X')) {
        long v = strtol(n + 2, &end, 16);
        return n[2] && !*end && v > 0 && v < 256 ? int(v) : 0;
    }
    if ((n[0] == 'F' || n[0] == 'f') && n[1]) {
        long k = strtol(n + 1, &end, 10);
        return !*end && k >= 1 && k <= 24 ? VK_F1 + int(k) - 1 : 0;
    }
    char c = char(toupper(static_cast<unsigned char>(n[0])));
    return n[0] && !n[1] && ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) ? c : 0;
}

std::string rt::keyName(int vk) {
    for (const auto& [name, k] : kNamedKeys)
        if (k == vk) return name;
    if (vk >= VK_F1 && vk <= VK_F24) return "F" + std::to_string(vk - VK_F1 + 1);
    if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return std::string(1, char(vk));
    char hex[8];
    snprintf(hex, sizeof hex, "0x%02X", vk & 0xff);
    return hex;
}

int rt::heldMods() {
    return (keyDown(VK_CONTROL) ? ModCtrl : 0) | (keyDown(VK_SHIFT) ? ModShift : 0) | (keyDown(VK_MENU) ? ModAlt : 0);
}

std::vector<rt::BindInfo> rt::bindings(int& dead) {
    std::lock_guard lock(gBindMx);
    std::vector<BindInfo> rows;
    for (const Binding& b : gBinds)
        rows.push_back({b.key, b.mod, b.label, b.vk ? comboName(b.mods, b.vk) : "", b.input, b.vk});
    std::stable_sort(rows.begin(), rows.end(), [](const BindInfo& a, const BindInfo& b) { return a.mod < b.mod; });
    dead = static_cast<int>(std::count_if(gSaved.begin(), gSaved.end(), [](const auto& s) { return !findBind(s.first); }));
    return rows;
}

// Applies at once and writes bindings.ini, keeping the file's other lines. Logged, so set.log shows what was bound.
void rt::setBinding(const std::string& key, int mods, int vk) {
    std::string combo = vk ? comboName(mods, vk) : "";
    {
        std::lock_guard lock(gBindMx);
        if (Binding* b = findBind(key)) {
            b->mods = vk ? mods : 0;
            b->vk = vk;
        }
        if (vk) gSaved[key] = combo;
        else gSaved.erase(key);
        std::string path = bindsPath();
        if (!path.empty()) {
            const std::wstring wkey = wideOf(key), wcombo = wideOf(combo);
            WritePrivateProfileStringW(L"bindings", wkey.c_str(), vk ? wcombo.c_str() : nullptr, wideOf(path).c_str());
        }
    }
    gSink((LV_OK "Binding " + key + ": " + (vk ? combo : "Unbound")).c_str());
}

void rt::removeDeadBindings() {
    std::lock_guard lock(gBindMx);
    std::string path = bindsPath();
    for (auto it = gSaved.begin(); it != gSaved.end();) {
        if (findBind(it->first)) {
            ++it;
            continue;
        }
        if (!path.empty()) WritePrivateProfileStringW(L"bindings", wideOf(it->first).c_str(), nullptr, wideOf(path).c_str());
        it = gSaved.erase(it);
    }
}
