// SPDX-License-Identifier: MIT
// kiln-set: SET's Lua mod API. Adapted from Shrouded Engine Tweaks 0.1.1 runtime/lua_api.cpp,
// Copyright (c) 2026 Cacaida, MIT (licenses/ShroudedEngineTweaks-LICENSE.txt). Kiln's changes: the game layer is
// Kiln's (game.h on kiln.types / kiln.world / kiln.assets), Admin.* runs on Kiln's services (admin.inl), native hooks
// go through kiln.hooks (hooks.h), and the runtime is driven by Kiln's ticks (plugin.cpp).
#include "lua_api.h"

#include "game.h"
#include "hooks.h"
#include "level.h"
#include "sandbox.h"
#include "kiln_api.h"
#include "services/kiln_world.h"

#define RUNTIME_VERSION "0.1.1"

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
#include <initializer_list>
#include <iterator>
#include <map>
#include <mutex>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

// Lua is compiled as C++ (runtime/CMakeLists.txt), so its headers are included without extern "C".
#include "lauxlib.h"
#include "lua.h"
#include "lualib.h"
#include "lua_budget.h"

namespace {

std::string gModsDir;
rt::Sink gSink = [](const char*) {};
uintptr_t gWorld;  // server world, only set while Lua runs
lua_State* gL;
std::atomic<bool> gReload, gLoaded;  // gLoaded: a Lua state with the mods exists (the toolbar's Reload Mods)
std::mutex gQueueMx;
std::vector<std::string> gQueue;
std::atomic<bool> gOverlayOpen, gHasDraw;
std::atomic<uint32_t> gBuild;  // the game build (rt::setGameBuild), for GetGameBuild() and mod.json

// Lua runs on the server thread (tick) or the main thread (frame), and on the render thread (Draw). gLuaMx keeps them
// apart; gInDraw marks the render-thread side, where game memory is off limits because the game mutates it
// concurrently.
std::mutex gLuaMx;
bool gInDraw;
bool gInConsole, gHookShutdown;
std::string gCurrentMod;
std::map<std::string, uint64_t> gSystemOwners;
struct HookHandler {
    int sub, ref;
    std::string mod, name;
    std::vector<hk::Arg> args;
};
std::map<lua_Integer, HookHandler> gHooks;
lua_Integer gNextHook = 0;
std::atomic<bool> gBroken;  // an access violation stopped Lua work: the state is unusable, so no Lua runs again

constexpr const char* kRefMt = "rt.ref";
constexpr const char* kTicks = "rt.ticks";
constexpr const char* kDraws = "rt.draws";
constexpr const char* kKeys = "rt.keys";
constexpr const char* kObservers = "rt.observers";  // event type hash -> list of handlers
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
    static bool told;
    uint32_t prot = 0;
    if (!res::write(p, src, n, prot)) luaL_error(L, "resource memory couldn't be made writable");
    if (!(prot & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE)) && !std::exchange(told, true)) {
        char line[96];
        snprintf(line, sizeof line, LV_OK "Resources: the game keeps them read-only (protect 0x%x), each edit lifts that", prot);
        gSink(line);
    }
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

// Writable: an edit lasts until the game closes.
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

// Res.text(guid): the English text a LocaTag reference (item.name, ...) names, or the English name of an item or a
// template by its GUID (Kiln's catalogs, admin.inl); nil if none. Looked up only when called.
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
    const std::string d = setDisplayText(g);
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
    luaL_argcheck(L, vk != 0, 1, "unknown key (use F1-F24, A-Z, 0-9, `, Insert, Home, End, Delete, PageUp, PageDown, Pause or 0xNN)");
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
    std::vector<char> keys(32 * 1024);
    DWORD n = GetPrivateProfileStringA("bindings", nullptr, "", keys.data(), static_cast<DWORD>(keys.size()), path.c_str());
    for (const char* k = keys.data(); k < keys.data() + n && *k; k += strlen(k) + 1) {
        char combo[64];
        GetPrivateProfileStringA("bindings", k, "", combo, sizeof combo, path.c_str());
        gSaved[k] = combo;
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

std::string modOf(lua_State* L, int idx);
void discardAdmin(const std::string& mod);

// pcall with a traceback; errors go to the sink. The function and nargs arguments must be on the stack.
bool call(lua_State* L, int nargs, int nres) {
    kset::budget::Scope budget;
    int base = lua_gettop(L) - nargs;
    struct ModScope {
        std::string prior;
        ModScope(std::string mod) : prior(std::exchange(gCurrentMod, std::move(mod))) {}
        ~ModScope() { gCurrentMod = std::move(prior); }
    } scope(modOf(L, base));
    lua_pushcfunction(L, traceback);
    lua_insert(L, base);
    int rc = lua_pcall(L, nargs, nres, base);
    lua_remove(L, base);
    if (rc == LUA_OK) return true;
    discardAdmin(gCurrentMod);
    const char* error = lua_tostring(L, -1);
    gSink((LV_BAD + std::string(error ? error : "Lua error")).c_str());
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
    static const std::pair<const char*, hk::Kind> kinds[] = {
        {"skip", hk::Skip}, {"bool", hk::Bool}, {"u8", hk::U8}, {"u16", hk::U16}, {"u32", hk::U32},
        {"u64", hk::U64}, {"ptrsize", hk::U64}, {"i8", hk::I8}, {"i16", hk::I16}, {"i32", hk::I32},
        {"i64", hk::I64}, {"f32", hk::F32}, {"f64", hk::F64}, {"entity", hk::Entity},
        {"entity*", hk::EntityPtr}, {"cstr", hk::CStr}};
    for (const auto& k : kinds) if (name == k.first) return {k.second, {}};
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

// Hooks.observe{...}: hookRegister runs in a pcall so every refusal reaches the Hooks window's diagnostics. Upvalue 2
// of hookRegister is set when hk::subscribe already recorded the refusal, so it isn't counted twice.
int hookObserve(lua_State* L) {
    bool recorded = false;
    lua_pushvalue(L, lua_upvalueindex(1));
    lua_pushlightuserdata(L, &recorded);
    lua_pushcclosure(L, hookRegister, 2);
    lua_pushvalue(L, 1);
    if (lua_pcall(L, 1, 1, 0) == LUA_OK) return 1;
    const char* err = lua_tostring(L, -1);
    if (!recorded)
        hk::refuse(std::string(lua_tostring(L, lua_upvalueindex(1))) + ": " + (err ? err : "hook registration failed"));
    return lua_error(L);
}

void removeHook(lua_State* L, lua_Integer id) {
    auto it = gHooks.find(id);
    if (it == gHooks.end()) return;
    hk::unsubscribe(it->second.sub);
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
            gSink((LV_BAD + h.mod + ": hook '" + h.name + "' removed after an error").c_str());
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
        if (hk::drain(h.sub, batch)) runs.push_back({id, h, std::exchange(batch, {})});
    for (const auto& r : runs) runHook(L, r.id, r.handler, r.batch);
}

// A mod's globals: its own table, falling back to the shared one, plus what has to know the mod: Keys.hotkey and
// Keys.input, io inside its folder, and require from its folder.
void pushModEnv(lua_State* L, const std::string& name) {
    std::string dir = (std::filesystem::u8path(gModsDir) / name).string();
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
    for (const auto& f : {luaL_Reg{"observe", hookObserve}, luaL_Reg{"remove", hookRemove}}) {
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
    lua_getfield(L, LUA_REGISTRYINDEX, kRequire);
    lua_pushvalue(L, env);
    lua_pushstring(L, dir.c_str());
    lua_pushstring(L, name.c_str());
    lua_call(L, 3, 1);
    lua_setfield(L, env, "require");
    luaL_loadstring(L, "local base, env = ...; return function(chunk, name, _, custom) return base(chunk, name, 't', custom or env) end");
    lua_getglobal(L, "load"); lua_pushvalue(L, env); lua_call(L, 2, 1); lua_setfield(L, env, "load");
}

std::string modOf(lua_State* L, int idx);

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
}

// mods/<name>/mod.json, optional: {"requires": "0.1.1", "builds": [1793791052]}: the oldest Shrouded Engine Tweaks the
// mod needs, and the game builds it was made for. Read with the mods' own json.decode.
struct ModInfo {
    std::string needs;  // "requires" in mod.json
    std::vector<uint32_t> builds;
};

ModInfo readModJson(lua_State* L, const std::string& name) {
    ModInfo info;
    FILE* f = nullptr;
    f = kset::sandbox::open((std::filesystem::u8path(gModsDir) / name).string(), "mod.json", "rb");
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

// "Mod loaded: x", or failed, with the builds a mod.json named when this game build isn't one of them.
std::string modLine(const std::string& name, bool ok, const ModInfo& info) {
    bool other = !info.builds.empty() && std::find(info.builds.begin(), info.builds.end(), gBuild) == info.builds.end();
    std::string line = (ok ? (other ? LV_WARN "Mod loaded: " : LV_OK "Mod loaded: ") : LV_BAD "Mod failed: ") + name;
    if (!other) return line;
    std::string made;
    for (uint32_t b : info.builds) made += (made.empty() ? "" : ", ") + std::to_string(b);
    line += " (made for game build" + std::string(info.builds.size() > 1 ? "s " : " ") + made + ", this is " +
            std::to_string(gBuild) + (ok ? ")" : ": wrong game version?)");
    return line;
}

// Runs mods/<name>/init.lua for every mod folder, each in its own environment. A failing mod is reported and unloaded.
// What init.lua returns is kept for GetMod (true if it returns nothing).
void loadMods(lua_State* L) {
    int loaded = 0, failed = 0;
    lua_getfield(L, LUA_REGISTRYINDEX, kMods);
    int mods = lua_gettop(L);
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA((gModsDir + "\\*").c_str(), &fd);
    for (bool more = h != INVALID_HANDLE_VALUE; more; more = FindNextFileA(h, &fd) != 0) {
        std::string name = fd.cFileName, init = gModsDir + "\\" + name + "\\init.lua";
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || name[0] == '.') continue;
        if (GetFileAttributesA(init.c_str()) == INVALID_FILE_ATTRIBUTES) {
            gSink((LV_WARN "Mod skipped: " + name + " (no init.lua)").c_str());
            continue;
        }
        ModInfo info = readModJson(L, name);
        if (!info.needs.empty() && newerThanSet(info.needs)) {
            failed++;
            gSink((LV_BAD "Mod skipped: " + name + " (needs Shrouded Engine Tweaks " + info.needs +
                   ", this is " RUNTIME_VERSION ")").c_str());
            continue;
        }
        bool ok = loadText(L, (std::filesystem::u8path(gModsDir) / name).string(), "init.lua", ("@" + name + "/init.lua").c_str());
        if (!ok) {
            gSink((LV_BAD + std::string(lua_tostring(L, -1))).c_str());  // call() prints its own errors
        } else {
            pushModEnv(L, name);
            lua_setupvalue(L, -2, 1);  // a main chunk's only upvalue is _ENV
            ok = call(L, 0, 1);
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
        gSink(modLine(name, ok, info).c_str());
    }
    if (h != INVALID_HANDLE_VALUE) FindClose(h);
    lua_pop(L, 1);
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
std::string modOf(lua_State* L, int idx) {
    idx = lua_absindex(L, idx);
    lua_getfield(L, LUA_REGISTRYINDEX, kFunctionMods);
    lua_pushvalue(L, idx); lua_rawget(L, -2);
    if (const char* owner = lua_tostring(L, -1)) { std::string mod(owner); lua_pop(L, 2); return mod; }
    lua_pop(L, 2);
    lua_Debug ar;
    lua_pushvalue(L, idx);
    lua_getinfo(L, ">S", &ar);
    std::string_view s = ar.source ? ar.source : "";
    if (!s.starts_with('@') && !s.starts_with('=')) return "load";
    s.remove_prefix(1);
    return std::string(s.substr(0, s.find('/')));
}

// Drops callback i of the list on top of the stack after an error, naming its mod: "hello: tick callback removed".
void dropCallback(lua_State* L, lua_Integer i, const char* what) {
    lua_rawgeti(L, -1, i);
    gSink((LV_BAD + modOf(L, -1) + ": " + what + " removed after an error").c_str());
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
    snprintf(line, sizeof line, LV_WARN "%s: a %s callback took %.0f ms (the game waits for it)", mod.c_str(), what, ms);
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
        types.push_back(static_cast<uint32_t>(lua_tointeger(L, -2)));
        lua_pop(L, 1);
    }
    for (uint32_t h : types) {
        refl::Type t = refl::byHash(h);
        int idx = t ? ecs::eventIndex(world, h) : -1;
        if (idx < 0) {
            // Report once per type instead of skipping silently (no stream on this world, or kiln.types refused it).
            static std::vector<uint32_t> reported;
            if (std::find(reported.begin(), reported.end(), h) == reported.end() && reported.size() < 256) {
                reported.push_back(h);
                gSink((LV_WARN "Observe: " + (t ? qname(t) : std::string("unknown type")) +
                       " cannot be read on this world (error " + std::to_string(-idx) + "); its handlers will not run").c_str());
            }
            continue;
        }
        lua_rawgeti(L, -1, h);
        ecs::forEachEvent(world, idx, h, [&](const uint8_t* ev) { callHandlers(L, t, ev); });
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
    if (ok) snprintf(line, sizeof line, LV_OK "server world %p: %u entities, sim time %.1f s", reinterpret_cast<void*>(world),
                     ecs::entityCount(world), double(sim) / 1e9);
    else snprintf(line, sizeof line, LV_WARN "server world %p not ready or not recognized: Lua waits", reinterpret_cast<void*>(world));
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
    gHookShutdown = true;
    stopHooks(gL);
    runCallbacks(gL, kOnShutdown, "onShutdown");
    resetAdmin();
    for (const auto& [name, owner] : gSystemOwners) { (void)name; sys::releaseOwner(owner); }
    gSystemOwners.clear();
    { kset::budget::Scope budget; lua_close(std::exchange(gL, nullptr)); }
    gLoaded = false;
    gHookShutdown = false;
}

// onWorldLeave, without the game: the world is going away, or already gone when its leave went unseen.
void leaveWorld() {
    hk::discardPending();
    gInWorld = false;
    gSink("Left the world");
    gLeaving = true;
    runCallbacks(gL, kOnLeave, "onWorldLeave");
    gLeaving = false;
}

// A new session's first ready tick, with the game. A world whose leave went unseen is left first.
void enterWorld() {
    if (gInWorld) leaveWorld();
    hk::discardPending();
    gInWorld = true;
    gSink("Entered a world");
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

constexpr std::pair<const char*, int> kNamedKeys[] = {
    {"`", VK_OEM_3},       {"Insert", VK_INSERT}, {"Home", VK_HOME},     {"End", VK_END},
    {"Delete", VK_DELETE}, {"PageUp", VK_PRIOR},  {"PageDown", VK_NEXT}, {"Pause", VK_PAUSE}};

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
    for (const char* reg : {kTicks, kDraws, kKeys, kObservers, kMods, kBinds, kOnInit, kOnOpen, kOnClose, kOnShutdown,
                            kOnEnter, kOnLeave}) {
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
void tickMods(uintptr_t world) {
    static auto last = std::chrono::steady_clock::now();
    auto now = std::chrono::steady_clock::now();
    gDt = std::chrono::duration<double>(now - std::exchange(last, now)).count();
    if (world) checkSent(world);
    for (const std::string& line : takeQueue()) rt::exec(gL, line);
    overlayEvents();
    hk::flushLogs();
    if (!gReload) dispatchHooks(gL);
    runCallbacks(gL, kTicks, "tick");
    pollKeys(gL);
    if (world) armAdmin(world);
    if (gReload.exchange(false)) {
        gSink("Reloading mods");
        closeState();
        startState();
        if (gInWorld) runCallbacks(gL, kOnEnter, "onWorldEnter");  // set up as on entering, in either case
    }
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
        stopHooks(nullptr);
        gSink(LV_BAD "Lua: access violation, mods off until the game restarts");
    } catch (...) {
        // /EHa catches non-AV hardware faults here too. The Lua state must never be entered again.
        const bool wasBroken = gBroken.exchange(true);
        gLoaded = gHasDraw = false;
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
        if (std::exchange(gNewWorld, false)) enterWorld();
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
    resetAdmin();
    if (!gInWorld || gBroken) return;
    guarded([](uintptr_t) { leaveWorld(); }, 0, "onWorldLeave");
    gLeaving = false;
}

// The game started closing: onWorldLeave if still in a world, then onShutdown, both without the game. No mods after.
void rt::gameClosing() {
    std::lock_guard lock(gLuaMx);
    gClosing = true;
    resetAdmin();
    if (!gL || gBroken) return;
    guarded([](uintptr_t) {
        if (gInWorld) leaveWorld();
        gSink("Game closing: unloading mods");
        closeState();
    }, 0, "onShutdown");
    gLeaving = false;
}

void rt::afterTick(uintptr_t world) {
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
bool rt::modsLoaded() { return gLoaded; }

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
    resetNames();
}
bool rt::systemsAvailable() { return gSysOk; }
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
    for (const Binding& b : gBinds) rows.push_back({b.key, b.mod, b.label, b.vk ? comboName(b.mods, b.vk) : "", b.input});
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
        if (!path.empty()) WritePrivateProfileStringA("bindings", key.c_str(), vk ? combo.c_str() : nullptr, path.c_str());
    }
    gSink((LV_OK "Binding " + key + ": " + (vk ? combo : "unbound")).c_str());
}

void rt::removeDeadBindings() {
    std::lock_guard lock(gBindMx);
    std::string path = bindsPath();
    for (auto it = gSaved.begin(); it != gSaved.end();) {
        if (findBind(it->first)) {
            ++it;
            continue;
        }
        if (!path.empty()) WritePrivateProfileStringA("bindings", it->first.c_str(), nullptr, path.c_str());
        it = gSaved.erase(it);
    }
}
