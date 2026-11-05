// SPDX-License-Identifier: MIT
// kiln.vfx internals. core.cpp is the queue, the handles and the guard's logic; it has no Windows or game dependency, so a host
// compiler can test it (tests/). The game sits behind class Game: game.cpp is the real one (signatures, hooks, SEH), sim.cpp
// a simulated one for tests and labs. Public header: native/core/abi/services/kiln_vfx.h. Evidence: docs/re/vfx.md section 4.
#pragma once
#include <cstddef>
#include <cstdint>

#include "kiln_api.h"
#include "services/kiln_vfx.h"

namespace kvx {

// ---- the game's structures (docs/re/vfx.md 4.1; sigdb/symbols/client.d/vfx.json checks the attach layout) ----

// keen::WorldTransform: position 32.32 fixed metres, orientation, scale.
struct WorldTransform {
    int64_t pos[3];
    float rot[4];
    float scale[3];
    float pad;
};
static_assert(sizeof(WorldTransform) == 56);

// What FxPlayAttached takes: entity, keen::AttachmentSlot, keen::ecs::Offset at +8, keep following at +0x30.
struct FxAttach {
    uint32_t entity;
    uint8_t slot;
    uint8_t pad0[3];
    float local[3];
    float world[3];
    float rot[4];
    uint8_t follow;
    uint8_t pad1[3];
};
static_assert(sizeof(FxAttach) == 52);

// std::optional<nanoseconds>.
struct FxTiming {
    int64_t ns;
    uint8_t has;
    uint8_t pad[7];
};
static_assert(sizeof(FxTiming) == 16);

// keen::VfxUserEffectParameterDataBlock + has-parameters + a flag FxStart fills in.
struct FxParams {
    uint32_t data[16];
    uint8_t has;
    uint8_t pad0[3];
    uint8_t ending;
    uint8_t pad1[3];
};
static_assert(sizeof(FxParams) == 72);

// ---- the VFX system's map of registered GUIDs ----

struct MapView {
    uint64_t count = 0, capacity = 0;
    const uint64_t* bits = nullptr;  // one bit per slot
    const uint64_t* keys = nullptr;  // 16 B per slot
    const uint32_t* values = nullptr;  // index into the system's VFX table, per slot
};

// VfxResolve's probe: slot = low 32 bits of the GUID & (capacity - 1), then linear. 1 found, 0 not, -1 the map is empty or not
// there yet. Touches the arrays: the real game calls it inside SEH.
int map_find(const MapView& m, const uint8_t guid[16]);

// The slot of a key, -1 if it isn't there.
int64_t map_slot(const MapView& m, const uint8_t guid[16]);

// Bit count equals the count field, capacity a power of two. false: this is not the layout the signatures promised.
bool map_valid(const MapView& m);

// ---- how long a one-shot runs: the game has no "finished" signal, so Kiln stops it (docs/re/vfx.md 3.5) ----

// Where a VfxResource holds what that takes (sigdb constants vfx/res.*).
struct ResLayout {
    int64_t kill = 8, params = 0x6c, defaults = 0x74;
};
struct VfxLengths {
    uint32_t kill_seconds = 0;
    float duration = -1.f, lifetime = -1.f;  // the "duration" and "lifetime" user parameters' defaults; < 0 = none
};
// Reads a VfxResource's bytes. The real game calls it inside SEH.
void read_lengths(const uint8_t* resource, const ResLayout& l, VfxLengths* out);
// The larger of the kill time and duration + lifetime; 10 s if the resource says nothing; 1 to 60 s.
int64_t one_shot_ns(const VfxLengths& v);
constexpr int64_t kOneShotFallbackNs = 10'000'000'000;

bool zero16(const uint8_t g[16]);
void guid_text(const uint8_t g[16], char out[40]);  // .NET Guid text form of the game's byte order

// The guard said no: one log line per GUID (per `where`), counted in status.refused. Any thread.
void guard_refused(const uint8_t guid[16], const char* where);

// The VFX builder (boot.cpp) plugs in here. `update` runs at the top of every FxController update. `refuses` says a registered VFX must not
// play after all: its GUID then counts as unknown, in the queue and in the guard. `one_shot_ns` is how long a built one-shot runs, which the
// builder knows exactly; 0 for anything else, which keeps one_shot_ns(VfxLengths)'s rule. Any of them may be null.
void core_set_build_hooks(void (*update)(), bool (*refuses)(const uint8_t guid[16]), int64_t (*one_shot_ns)(const uint8_t guid[16]));
bool core_build_refuses(const uint8_t guid[16]);

// ---- what the queue needs from the game; all called inside an FxController update, with Kiln's lock held ----

struct Frame {
    void* fxc;
    int64_t now;
};

class Game {
public:
    virtual ~Game() = default;
    virtual bool can_play() const = 0;  // signatures resolved, nothing faulted
    virtual bool has_guard() const = 0;
    virtual bool is_sim() const = 0;
    virtual MapView map(void* fxc) = 0;  // fxc may be null: the last system seen; a capacity of 0 = none
    virtual int is_registered(void* fxc, const uint8_t guid[16]) = 0;  // map_find through whatever protection it needs
    virtual bool vfx_lengths(void* fxc, const uint8_t guid[16], VfxLengths* out) = 0;  // false: not known
    virtual uint32_t play_at(const Frame& f, const uint8_t guid[16], const WorldTransform& t, const FxParams& p, const FxTiming& d) = 0;
    virtual uint32_t play_on(const Frame& f, const uint8_t guid[16], const FxAttach& a, const FxParams& p, const FxTiming& d) = 0;
    virtual void stop(const Frame& f, uint32_t fx, bool immediate) = 0;
    virtual bool lookup(const Frame& f, uint32_t fx, uint32_t* instance) = 0;  // false: the FxController dropped it
    virtual void set_param(const Frame& f, uint32_t instance, uint32_t hash, uint32_t type, const void* data, size_t size) = 0;
    virtual bool entity_position(const Frame& f, uint32_t entity, KilnVec3* out) = 0;
    virtual uint32_t create_direct(const Frame& f, const uint8_t guid[16]) = 0;  // the game's RenderCreateVfx, returns the instance
    virtual void preload(const Frame& f, const uint8_t guid[16]) = 0;
};

void core_set_game(Game* game);
Game* core_game();

// The FxController update (the hook, or the sim's tick): runs the queue. Any thread, but one at a time, as the game's update is.
void core_update(void* fxc, int64_t now);
// The client world is going away: its handles end without a game call.
void core_world_ended();

// The C table's functions (any thread).
KilnResult core_status(KilnVfxStatus* out);
KilnVfx core_play(KilnOwner owner, const KilnVfxPlay* play);
KilnResult core_stop(KilnVfx vfx, int immediate);
KilnResult core_set_param(KilnVfx vfx, uint32_t hash, uint32_t type, const void* data, size_t size);
uint32_t core_state(KilnVfx vfx);
KilnResult core_info(KilnVfx vfx, KilnVfxInfo* out);
uint32_t core_stop_owner(KilnOwner owner, int immediate);
int32_t core_is_registered(const uint8_t guid[16]);
KilnResult core_preload(const uint8_t guid[16]);
KilnResult core_bind_guid(KilnVfx vfx, const uint8_t guid[16]);
KilnResult core_status_json(char* buf, size_t cap, size_t* out_len);

// Supplied by the embedder (vfx.cpp, tests).
void vlog(int level, const char* fmt, ...);
uint32_t thread_id();
uint64_t now_ms();

int64_t m_to_fixed(double m);
double fixed_to_m(int64_t v);

}  // namespace kvx

#define KV_I(...) ::kvx::vlog(KILN_LOG_INFO, __VA_ARGS__)
#define KV_W(...) ::kvx::vlog(KILN_LOG_WARN, __VA_ARGS__)
#define KV_E(...) ::kvx::vlog(KILN_LOG_ERROR, __VA_ARGS__)
#define KV_D(...) ::kvx::vlog(KILN_LOG_DEBUG, __VA_ARGS__)
