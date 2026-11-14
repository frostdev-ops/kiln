// SPDX-License-Identifier: MIT
// kiln.toolwear: what one of the game's wear debits costs a tool (chapter 16b; wear.h has the game's side). Two places
// are acted on, and only they:
//   - WearItem, when it returns to the weapon site of durability_loss (the attacker's weapon of a HitEvent that the
//     game itself decided to debit): the delta -1 becomes -cost;
//   - the call of WearDelta after a projectile was spawned (actor_spawn_entity): R8D -1 becomes -cost.
// Everything else is forwarded untouched: armor, torches (burndown has its own caller), another caller of WearItem or
// WearDelta (another loader's, or Kiln's own taming cost), a delta that is not exactly -1, an item without a rule, a
// world that is not the authoritative server world of this process.
//
// This header is the logic, with the game behind `Ops` so that a host test drives it with made-up memory; the hooks
// themselves (the detours, the SEH guards) are toolwear.cpp. Rules of the code here, because it runs inside game
// programs, on whichever thread the scheduler gives them (two slices of one program can run at once):
//   - no allocation, no lock held across a game call, no managed code, no logging;
//   - no state shared between calls except the rule table (atomics), the change queue (a mutex around one copy) and
//     the stamp table's own mutex; what one call needs later (the spawn site's debit, seen again in WearDelta) is a
//     frame of this thread;
//   - a pointer into the game is used inside the call that got it and never kept; after the game ran, it is read again.
#pragma once
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <mutex>

#include "wear.h"

namespace kitems::toolwear {

constexpr uint32_t kSourceWeaponHit = 1, kSourceProjectileSpawn = 2;
constexpr int32_t kMaxCost = 32767;
constexpr size_t kMaxRules = 256, kQueue = 4096;

struct Slice {
    void* ptr;
    uint64_t size;
};

// The game and the item service, as the hooks need them.
struct Ops {
    void* (*job_storage)(void* job) = nullptr;
    Slice* (*read_other)(Slice* out, void* storage, uint64_t accessor, uint32_t entity) = nullptr;
    void (*iter_begin)(void* ctx, uint64_t* iter, uint32_t size) = nullptr;
    uint16_t* (*component_map_find)(void* map, const uint32_t* hash) = nullptr;
    uintptr_t (*server_world)() = nullptr;
    // {stamp, epoch} of the item entity under the stamp table's mutex; stamp 0: none can be given (then no rule is applied)
    bool (*capture)(uint32_t entity, uint32_t item, uint64_t step, uint32_t* stamp, uint64_t* epoch) = nullptr;
    uint64_t (*step)() = nullptr;
    // the index of the durability maximum in its container (the game's attribute lookup); -1: not known
    int (*max_index)(void* attributes) = nullptr;
    int64_t world_storage = 0, storage_kind = 0, registry_components = 0;
    // the spawn frame, from the approved description (wear::Facts)
    int64_t spawn_request_local = 0, spawn_durability_local = 0, spawn_request_pide = 0, spawn_iter_base = 0, spawn_itemstate_slot = 0;
    uintptr_t weapon_return = 0;
};

// One real change of a tool's durability, as the hook saw it. Plain values: nothing of the game is kept.
struct Change {
    uint32_t source;
    uint64_t epoch;  // the stamp table's epoch at capture: a record of an earlier world is dropped at the drain
    uint64_t step;
    uint32_t actor, item_entity, item_id, stamp;
    int32_t before, after, maximum, original_delta, requested_delta;
    uint32_t sequence_id, call_index;
    int64_t trigger_time;
    uint16_t trigger_type;
};

// ---- rules: item id -> cost. Written on the tick thread only, read from any thread without a lock. ---------------
// A slot is (item id << 32) | (cost + 1), 0 = never used; a rule that is taken away keeps its slot with cost part 0,
// so a reader that probes past it still finds what lies behind.
class Rules {
public:
    // The cost of a debit for `item`, or -1: no rule.
    int32_t find(uint32_t item) const {
        if (!item) return -1;
        for (size_t n = 0, i = item % kMaxRules; n < kMaxRules; ++n, i = (i + 1) % kMaxRules) {
            const uint64_t v = slots_[i].load(std::memory_order_acquire);
            if (!v) return -1;
            if (static_cast<uint32_t>(v >> 32) == item) return static_cast<int32_t>(static_cast<uint32_t>(v)) - 1;
        }
        return -1;
    }
    // Tick thread. cost -1 takes the rule away. false: the table is full (256 tools have had a rule in this process).
    bool set(uint32_t item, int32_t cost) {
        if (!item || cost < -1 || cost > kMaxCost) return false;
        const uint64_t value = (static_cast<uint64_t>(item) << 32) | static_cast<uint32_t>(cost + 1);
        for (size_t n = 0, i = item % kMaxRules; n < kMaxRules; ++n, i = (i + 1) % kMaxRules) {
            const uint64_t v = slots_[i].load(std::memory_order_acquire);
            if (!v || static_cast<uint32_t>(v >> 32) == item) {
                if (!v && cost < 0) return true;  // nothing to take away
                slots_[i].store(value, std::memory_order_release);
                return true;
            }
        }
        return false;
    }

private:
    std::atomic<uint64_t> slots_[kMaxRules] = {};
};

// ---- the change queue: producers on any thread, one drain on the tick thread --------------------------------------
class Queue {
public:
    // Never waits for the drain: full means the record is counted as dropped. The mutation it describes has happened.
    void push(const Change& c) {
        std::lock_guard lock(mu_);
        if (count_ == kQueue) {
            ++dropped_;
            return;
        }
        ring_[(head_ + count_) % kQueue] = c;
        ++count_;
    }
    // Tick thread, every Pre: says a hook's fault once (kiln.log and the capability row).
void tick();
// Tick thread: up to `max` records, oldest first.
    size_t drain(Change* out, size_t max) {
        std::lock_guard lock(mu_);
        size_t n = 0;
        while (n < max && count_ > 0) {
            out[n++] = ring_[head_];
            head_ = (head_ + 1) % kQueue;
            --count_;
        }
        return n;
    }
    // A world ended or started: its records name entity ids that mean something else from now on.
    void clear() {
        std::lock_guard lock(mu_);
        head_ = count_ = 0;
    }
    uint64_t dropped() const {
        std::lock_guard lock(mu_);
        return dropped_;
    }
    size_t size() const {
        std::lock_guard lock(mu_);
        return count_;
    }

private:
    mutable std::mutex mu_;
    Change ring_[kQueue];
    size_t head_ = 0, count_ = 0;
    uint64_t dropped_ = 0;
};

// ---- the state the hooks share -------------------------------------------------------------------------------------
struct State {
    Ops ops;
    Rules rules;
    Queue queue;
    std::atomic<bool> on{false};          // hooks act; false: every call is forwarded
    std::atomic<uint64_t> hit_seen{0}, hit_applied{0}, spawn_seen{0}, spawn_applied{0}, forwarded{0};
};

// What the spawn site decided, for the WearDelta call that follows it on this thread.
struct Frame {
    bool armed = false;
    void* durability = nullptr;
    Change change{};
    uint16_t index = 0;
    int max_index = -1;
    size_t size = 0;  // of the Durability slice the site read
    void* ctx = nullptr;
};

// ---- checks shared by both sites ------------------------------------------------------------------------------------

// The entity storage of this program invocation when it is the authoritative server world's; else null.
inline void* authority(const Ops& o, void* ctx) {
    if (!ctx) return nullptr;
    void* job = *static_cast<void**>(ctx);
    if (!job) return nullptr;
    auto* storage = static_cast<uint8_t*>(o.job_storage(job));
    if (!storage) return nullptr;
    if (storage[o.storage_kind] != 0) return nullptr;  // prediction or client storage
    const uintptr_t world = o.server_world();
    if (!world || reinterpret_cast<uintptr_t>(storage) != world + static_cast<uintptr_t>(o.world_storage)) return nullptr;
    return storage;
}

// An accessor is a pointer to its component's u16 type index: it must be the index the world's registry has for `hash`.
inline bool accessor_is(const Ops& o, void* storage, uint64_t accessor, uint32_t hash) {
    if (!accessor) return false;
    void* registry = *static_cast<void**>(storage);
    if (!registry) return false;
    const uint16_t* want = o.component_map_find(static_cast<uint8_t*>(registry) + o.registry_components, &hash);
    return want && *want == *reinterpret_cast<const uint16_t*>(accessor);
}

inline bool read(const Ops& o, void* storage, uint64_t accessor, uint32_t entity, size_t min_size, Slice* out) {
    *out = Slice{};
    o.read_other(out, storage, accessor, entity);
    return out->ptr && out->size >= min_size;
}

// ---- the weapon site -------------------------------------------------------------------------------------------------
// What the detour of WearItem does before it calls on: the delta to pass (the original's, or -cost), and when a rule
// applies, `pending` filled for after().
struct HitPending {
    bool applied = false;
    void* storage = nullptr;
    Change change{};
    uint16_t index = 0;
    int max_index = -1;
};

inline int32_t hit_before(State& s, uintptr_t caller, uint64_t durability_write, uint32_t actor, uint32_t item_entity, int32_t delta,
                          wear::WearEnv* env, HitPending* p) {
    *p = HitPending{};
    if (!s.on.load(std::memory_order_acquire) || caller != s.ops.weapon_return || delta != -1 || !env || !item_entity) return delta;
    s.hit_seen.fetch_add(1, std::memory_order_relaxed);
    const Ops& o = s.ops;
    void* storage = authority(o, env->ctx);
    if (!storage) return delta;
    // the updater's own iterator again, for its ItemState accessor (slot 5): never a lookup outside the program's declared access
    uint64_t it[wear::kHitIterator / 8] = {};
    o.iter_begin(env->ctx, it, static_cast<uint32_t>(wear::kHitIterator));
    Slice state, dur;
    if (!accessor_is(o, storage, it[5], wear::kTypeItemState) || !accessor_is(o, storage, durability_write, wear::kTypeDurability)) return delta;
    // no ItemState: the game debits such a weapon without its category filter; that stays the game's, at the game's cost
    if (!read(o, storage, it[5], item_entity, wear::kItemStateSize, &state)) return delta;
    uint32_t item = 0;
    std::memcpy(&item, static_cast<uint8_t*>(state.ptr) + wear::kItemStateItemId, 4);
    const int32_t cost = s.rules.find(item);
    if (cost < 0) return delta;
    int32_t before = 0, max = 0;
    if (!read(o, storage, durability_write, item_entity, wear::kDurabilitySize, &dur) ||
        !wear::durability_values(dur.ptr, static_cast<size_t>(dur.size), env->durability_index, o.max_index(env->attributes), &before, &max))
        return delta;
    Change c{};
    c.source = kSourceWeaponHit;
    c.step = o.step();
    if (!o.capture(item_entity, item, c.step, &c.stamp, &c.epoch) || !c.stamp) return delta;  // no identity: no rule, the game's own debit
    c.actor = actor;
    c.item_entity = item_entity;
    c.item_id = item;
    c.before = before;
    c.maximum = max;
    c.original_delta = delta;
    c.requested_delta = -cost;
    p->applied = true;
    p->storage = storage;
    p->change = c;
    p->index = env->durability_index;
    p->max_index = o.max_index(env->attributes);
    s.hit_applied.fetch_add(1, std::memory_order_relaxed);
    return -cost;
}

// After the original WearItem returned: the value is read again through the same accessor (the game may have moved
// the component), and a real change is queued. The skill roll may have saved the debit: then nothing is queued.
inline void hit_after(State& s, const HitPending& p, uint64_t durability_write, wear::WearEnv* env) {
    if (!p.applied) return;
    const Ops& o = s.ops;
    if (authority(o, env->ctx) != p.storage) return;  // not the same authoritative world any more: nothing is said
    Slice dur;
    int32_t after = 0, max = 0;
    if (!read(o, p.storage, durability_write, p.change.item_entity, wear::kDurabilitySize, &dur) ||
        !wear::durability_values(dur.ptr, static_cast<size_t>(dur.size), p.index, p.max_index, &after, &max))
        return;
    if (after == p.change.before) return;
    Change c = p.change;
    c.after = after;
    c.maximum = max;
    s.queue.push(c);
}

// ---- the spawn site ---------------------------------------------------------------------------------------------------
// The registers at the call of WearDelta after a projectile spawn, as the mid hook has them.
struct SpawnRegs {
    uintptr_t rcx, rdx, r8, r9, r12, r14, rbp, rsp;
};

// Checks everything the approved frame promises; with a rule for the used item, writes -cost into r8 and arms `f` for
// the WearDelta call that follows. Any mismatch: r8 stays, nothing is armed.
inline void spawn_before(State& s, SpawnRegs& r, Frame* f) {
    *f = Frame{};
    if (!s.on.load(std::memory_order_acquire) || static_cast<int32_t>(static_cast<uint32_t>(r.r8)) != -1) return;
    s.spawn_seen.fetch_add(1, std::memory_order_relaxed);
    const Ops& o = s.ops;
    auto* env = *reinterpret_cast<wear::WearEnv**>(r.rsp + 0x20);  // the fifth argument
    if (!env || !env->ctx || reinterpret_cast<uintptr_t>(env->ctx) != r.r12) return;
    void* storage = authority(o, env->ctx);
    if (!storage) return;
    const auto* action = *reinterpret_cast<const uint8_t**>(r.rbp + o.spawn_request_local);  // the actor's current ActionSequence
    if (!action) return;
    uint32_t item = 0, entity = 0, seq_actor = 0;
    std::memcpy(&entity, action + o.spawn_request_pide, 4);      // usedItem's item entity
    std::memcpy(&item, action + o.spawn_request_pide - 4, 4);    // usedItem's item id, just before it
    std::memcpy(&seq_actor, action + 0x10, 4);                   // triggerContext.actorEntityId
    const uint32_t actor = static_cast<uint32_t>(r.r9);
    if (!item || !entity || !actor || seq_actor != actor) return;
    const int32_t cost = s.rules.find(item);
    if (cost < 0) return;
    const uint64_t dur_acc = *reinterpret_cast<const uint64_t*>(r.rbp + o.spawn_durability_local);
    const uint64_t state_acc = *reinterpret_cast<const uint64_t*>(r.rbp + o.spawn_iter_base + o.spawn_itemstate_slot);
    if (dur_acc != r.r14 || !accessor_is(o, storage, dur_acc, wear::kTypeDurability) || !accessor_is(o, storage, state_acc, wear::kTypeItemState)) return;
    Slice dur, state;
    if (!read(o, storage, dur_acc, entity, wear::kDurabilitySize, &dur) || reinterpret_cast<uintptr_t>(dur.ptr) != r.rcx) return;
    if (!read(o, storage, state_acc, entity, wear::kItemStateSize, &state)) return;
    uint32_t live_item = 0;
    std::memcpy(&live_item, static_cast<uint8_t*>(state.ptr) + wear::kItemStateItemId, 4);
    if (live_item != item) return;  // the entity holds another item now than the action used
    int32_t before = 0, max = 0;
    const int max_index = o.max_index(env->attributes);
    if (!wear::durability_values(dur.ptr, static_cast<size_t>(dur.size), env->durability_index, max_index, &before, &max)) return;
    Change c{};
    c.source = kSourceProjectileSpawn;
    c.step = o.step();
    if (!o.capture(entity, item, c.step, &c.stamp, &c.epoch) || !c.stamp) return;
    c.actor = actor;
    c.item_entity = entity;
    c.item_id = item;
    c.before = before;
    c.maximum = max;
    c.original_delta = -1;
    c.requested_delta = -cost;
    std::memcpy(&c.sequence_id, action, 4);
    std::memcpy(&c.trigger_time, action + 8, 8);
    std::memcpy(&c.call_index, action + 0x14, 4);
    std::memcpy(&c.trigger_type, action + 0x18, 2);
    f->armed = true;
    f->durability = dur.ptr;
    f->change = c;
    f->index = env->durability_index;
    f->max_index = max_index;
    f->size = static_cast<size_t>(dur.size);
    f->ctx = env->ctx;
    r.r8 = static_cast<uint32_t>(-cost);
    s.spawn_applied.fetch_add(1, std::memory_order_relaxed);
}

// The detour of WearDelta, before it calls on. True: this is the call the spawn site armed on this thread (then call
// delta_after with the same frame once the original returned). Any other call of WearDelta is none of ours: the
// frame, if one was left armed by a call that never came, is dropped.
inline bool delta_before(Frame* f, void* durability, uint32_t notify_actor, wear::WearEnv* env) {
    if (!f->armed) return false;
    if (durability == f->durability && notify_actor == f->change.actor && env && env->ctx == f->ctx) return true;
    *f = Frame{};
    return false;
}

inline void delta_after(State& s, Frame* f, void* durability) {
    const Frame frame = *f;
    *f = Frame{};
    if (!frame.armed) return;
    if (!authority(s.ops, frame.ctx)) return;
    int32_t after = 0, max = 0;
    if (!wear::durability_values(durability, frame.size, frame.index, frame.max_index, &after, &max) || after == frame.change.before) return;
    Change c = frame.change;
    c.after = after;
    c.maximum = max;
    s.queue.push(c);
}

// ---- the service's side (toolwear.cpp) --------------------------------------------------------------------------------

struct Status {
    bool on = false;         // the hooks are in and act
    bool faulted = false;    // a hook faulted: off for this process
    char why[160] = "";      // why it is off
    uint64_t dropped = 0;    // records the full queue did not take, since the process started
    uint64_t hit_seen = 0, hit_applied = 0, spawn_seen = 0, spawn_applied = 0;
    uint32_t threads = 0;    // distinct threads a hook has run on (up to 8 are told apart)
};

}  // namespace kitems::toolwear

struct KilnApi;

namespace kitems::toolwear {

// Installs the three hooks, once wear.h's facts are resolved (items.cpp, after resolve). false: off, with `why` logged and
// the capability lowered. Nothing is hooked when the row is held by the client gate on this side.
bool install(const KilnApi* api, uint64_t owner);
// Tick thread, while a server world runs: the game's program directory (kiln.world v4) must name durability_loss and
// actor_spawn_entity with the updates the signatures found; else the hooks go off. `world` is a KilnWorldApi or null (then
// nothing is checked: the hash of the build is what approved the frames). Checked once per process.
void verify_programs(const void* world);
// A server world closed or started: queued records name entities of the world that went.
void world_reset();
// Tick thread: the rule of an item (cost 0..32767, -1 none). false: the rule table is full.
bool set_rule(uint32_t item, int32_t cost);
int32_t rule(uint32_t item);
// Tick thread, every Pre: says a hook's fault once (kiln.log and the capability row).
void tick();
// Tick thread: up to `max` records whose item entity is still the one the hook saw (same epoch, same stamp); stale ones are dropped.
size_t drain(Change* out, size_t max);
Status status();
// Publishes the kiln.toolwear table (toolwear_table.cpp), also where the hooks are off: its status then says why.
bool publish(const KilnApi* api, uint64_t owner);

}  // namespace kitems::toolwear
