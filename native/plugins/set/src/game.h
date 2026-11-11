// SPDX-License-Identifier: MIT
// kiln-set: the game-side interface SET's Lua layer (lua_api.cpp) is written against, implemented on Kiln's services:
// reflection and systems on kiln.types, entities on kiln.world, events on kiln.types' streams, resources on
// kiln.assets. Declarations adapted from Shrouded Engine Tweaks runtime/game.h, Copyright (c) 2026 Cacaida, MIT
// (licenses/ShroudedEngineTweaks-LICENSE.txt); the implementations in game.cpp are Kiln's own except where marked.
#pragma once
#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

struct KilnApi;

namespace refl {
enum Prim : uint8_t {
    Bool = 1, U8, S8, U16, S16, U32, S32, U64, S64, F32, F64,
    Enum, Bitmask8, Bitmask16, Bitmask32, Bitmask64, Typedef, Struct, StaticArray,
    DsArray, DsString, DsOptional, DsVariant, BlobArray, BlobString, BlobOptional, BlobVariant, ObjectReference, Guid,
};
// View over one of the game's type descriptors (a kiln.types handle). Layout: kiln_types.h / docs/re.
struct Type {
    const uint8_t* p = nullptr;
    explicit operator bool() const { return p != nullptr; }
    std::string_view name() const { return str(0x00); }
    std::string_view qname() const { return str(0x20); }
    Type inner() const;
    uint32_t size() const { return u32(0x40); }
    uint32_t fieldCount() const { return u32(0x48); }
    uint8_t prim() const;
    uint32_t hash() const { return u32(0x50); }
    Type resolved() const;
    const uint8_t* defaults() const;
private:
    std::string_view str(size_t off) const;
    uint32_t u32(size_t off) const;
};
struct Field {
    std::string_view name;
    Type type;
    uint32_t offset;
};
size_t count();
Type at(size_t i);
Type byHash(uint32_t qualifiedHash);
Type byName(std::string_view name);
Type lookup(std::string_view name);
bool plain(Type t);
bool field(Type t, std::string_view name, Field& out);
void forFields(Type t, const std::function<void(const Field&)>& fn);
void forEnum(Type t, const std::function<void(std::string_view name, int64_t value)>& fn);
Type unwrap(Type t);
// A value inside t by path ("dataStorage[0]", "transform.position.x"): its offset and its type, a wrapper unwrapped.
// Static arrays only; false if any step doesn't exist. (From SET 0.2.0 runtime/game.cpp, MIT.)
bool path(Type t, std::string_view path, uint32_t& off, Type& leaf);
uint32_t fnv(std::string_view s);
bool enumHas(Type t, int64_t v);
std::string text(const uint8_t* p, Type u);
Field badField(Type t, const uint8_t* p);
}  // namespace refl

namespace sys {
struct System {
    std::string_view name;
    uintptr_t fn;
    uint32_t index;  // kiln.types index
};
const std::vector<System>& all();
const System* find(std::string_view name);
bool isOn(const System& s);
int setOn(const System& s, bool on, uint64_t owner);
uint64_t createOwner(std::string_view name);
void releaseOwner(uint64_t owner);
size_t offCount();
void setProfiling(bool on);
bool profiling();
struct Stat {
    const System* sys;
    uint64_t calls;
    double ms;
    uint8_t kinds;
};
std::vector<Stat> stats();
double profiledSecs();
inline const char* worldName(uint8_t kinds) { return !(kinds & 4) ? "server" : (kinds & 3) ? "both" : "client"; }
}  // namespace sys

// Game options (client only). Not ported yet: ready() stays false and GameOptions.* says so.
namespace opts {
bool ready();
uint8_t* machine();
uint8_t* user();
refl::Type machineType();
refl::Type userType();
size_t dump(std::string_view prefix, const std::function<void(const std::string&)>& fn);
}  // namespace opts

namespace ecs {
int compIndex(uintptr_t world, uint32_t typeHash);
uint8_t* component(uintptr_t world, uint32_t entity, int idx, uint32_t* size = nullptr);
uint32_t firstWith(uintptr_t world, int idx);
std::vector<uint32_t> allWith(uintptr_t world, const int* idx, size_t n);
uint32_t entityCount(uintptr_t world);
bool exists(uint32_t entity);
std::vector<refl::Type> componentTypes();
std::vector<uint32_t> findBounded(uint32_t hash, uint32_t limit, uint32_t& total); // tick thread; allocation capped at 65536
bool position(uint32_t entity, double* xyz); // metres; tick thread
// Stream lookups are cached per (world, type): a probe runs only on a miss. Tick thread, under the Lua lock.
int eventIndex(uintptr_t world, uint32_t typeHash);  // >= 0: has a stream; < 0: -KilnResult
// One event_each; 0 or -KilnResult (a cached failure returns without calling kiln.types).
int forEachEvent(uintptr_t world, uint32_t typeHash, const std::function<void(const uint8_t* ev)>& fn);
void forgetEvents();     // world left or closing: the next lookup starts over
uint64_t eventProbes();  // probes run so far (tests)
// Posting copies the payload at once (kiln.types event_post), so the caller fills a buffer first.
bool postEvent(uintptr_t world, uint32_t typeHash, const uint8_t* data, uint32_t size);
int64_t simTime(uintptr_t world);
uint32_t player();  // a player entity in the server world (the host in a hosted game, the first player on a server)
}  // namespace ecs

namespace res {
struct Entry {
    uint8_t guid[16];
    uint32_t type;
    uint32_t part;
    uint32_t off, size;  // off unused on Kiln; size = the resource's bytes
};
struct Blob {
    uint32_t rel, count, hash;
};
bool ready();
// ready()'s last answer without scanning or waiting, for threads that must not scan (the renderer, the main menu, where
// a scan could also cache an incomplete list): Unchecked until a scan on the tick thread.
enum class Scan : uint8_t { Unchecked, Found, None };
Scan scanned();
bool write(void* dst, const void* src, size_t n, uint32_t& prot);
size_t count();
uint8_t* data(const Entry& e, uint32_t* size = nullptr);
const Entry* find(const uint8_t guid[16], uint32_t type, uint32_t part = 0);
const std::vector<const Entry*>& ofType(uint32_t type);
std::vector<std::pair<refl::Type, size_t>> types();
refl::Type typeByName(std::string_view name);
std::string_view label(const Entry& e);
Blob blob(const uint8_t* p, uint8_t prim);
bool inside(const Entry& e, const uint8_t* p, uint64_t n);
bool parseGuid(std::string_view s, uint8_t out[16]);
std::string guidStr(const uint8_t g[16]);
bool isText(refl::Type t);
std::string_view text(const uint8_t guid[16]);
std::string_view nameText(const Entry& e);
}  // namespace res

// Game builds (the exe's link time stamp, GetGameBuild()) this runtime was checked on: the three client builds SET
// 0.2.0 lists (runtime/game.h kTestedBuilds), and the dedicated server build Kiln's lab runs kiln-set on.
inline constexpr uint32_t kTestedBuilds[] = {1792148057, 1792507660, 1793791052, 1793791038};
inline bool testedBuild(uint32_t build) {
    for (uint32_t b : kTestedBuilds)
        if (b == build) return true;
    return false;
}

namespace kset {
// Binds the services (on_ready). Returns what is missing, in words ("" when all is there).
std::string bind(const KilnApi* api);
bool tickThread();
bool systemsReady();  // kiln.types switches and profiles systems on this build
// Which Kiln services answered at bind: "types", "world", "assets", "players", "time" (Features()).
bool has(std::string_view service);
}  // namespace kset
