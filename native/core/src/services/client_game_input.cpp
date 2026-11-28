// SPDX-License-Identifier: MIT
// kiln.client.game_input's rules (client_game_input.h says what and how).
#include "client_game_input.h"

#include <array>
#include <atomic>
#include <chrono>
#include <climits>
#include <cstring>
#include <mutex>
#include <thread>

namespace kiln::game_input {
namespace {

constexpr int64_t kInf = INT64_MAX, kNegInf = INT64_MIN;
constexpr uint32_t kKnownActions = (1u << KILN_GAME_ACTION_COUNT) - 1;
constexpr int kDispatchSpins = 4096;  // tries to take the list before a dispatch skips its subscribers

int64_t steady_ns() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
        .count();
}
std::atomic<int64_t (*)()> g_clock{&steady_ns};
int64_t now_ns() { return g_clock.load(std::memory_order_relaxed)(); }

const char* const kUnbound =
    "no verified game input sites on this game build: sigdb has no client.input_observation and client.game_input (the "
    "local input writer's observation point, the input message's copy and the action bits) for it";
const char* const kInstallFailed = "hooking the game input sites failed (kiln.log says why)";
const char* const kInstallThrew = "hooking the game input sites failed at the first use: it threw";

// ---- the sample store: a sequence lock over atomic words, so readers on any thread never see a torn sample ----
constexpr size_t kWords = sizeof(KilnGameInputSample) / 8;
static_assert(sizeof(KilnGameInputSample) % 8 == 0, "the sample is whole words");
std::atomic<uint64_t> g_seq{0};
std::array<std::atomic<uint64_t>, kWords> g_words{};
std::atomic<int64_t> g_published_ns{0};

void publish(const KilnGameInputSample& s) {
    uint64_t w[kWords];
    std::memcpy(w, &s, sizeof w);
    const uint64_t seq = g_seq.load(std::memory_order_relaxed);
    g_seq.store(seq + 1, std::memory_order_relaxed);
    std::atomic_thread_fence(std::memory_order_release);
    for (size_t i = 0; i < kWords; ++i) g_words[i].store(w[i], std::memory_order_relaxed);
    g_published_ns.store(now_ns(), std::memory_order_relaxed);
    g_seq.store(seq + 2, std::memory_order_release);
}

bool load(KilnGameInputSample* out, int64_t* at_ns) {
    for (int tries = 0; tries < 64; ++tries) {
        const uint64_t a = g_seq.load(std::memory_order_acquire);
        if (a & 1) {
            std::this_thread::yield();
            continue;
        }
        uint64_t w[kWords];
        for (size_t i = 0; i < kWords; ++i) w[i] = g_words[i].load(std::memory_order_relaxed);
        const int64_t at = g_published_ns.load(std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_acquire);
        if (g_seq.load(std::memory_order_relaxed) != a) continue;
        std::memcpy(out, w, sizeof w);
        *at_ns = at;
        return true;
    }
    return false;
}

void clear_store() {
    publish(KilnGameInputSample{});
}

// ---- the gate ----
std::atomic<uint32_t> g_gate{KILN_GAME_INPUT_GATE_UNKNOWN};

// ---- subscriptions ----
enum class Kind : uint8_t { Handler, Repeat };
struct Slot {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    Kind kind = Kind::Handler;
    KilnGameInputFn fn = nullptr;
    KilnGameInputCondFn cond = nullptr;
    void* user = nullptr;
    uint32_t action = 0;
    uint32_t interval_ms = 0;
    RepeatState repeat;
    bool dead = false;  // ended from inside a callback: skipped, then dropped at the dispatch's end
};
std::mutex g_list_mu;  // the slots; held by a dispatch across its calls
std::array<Slot, kMaxSubscriptions> g_slots{};
uint32_t g_count = 0;
uint64_t g_next_handle = 0;
std::atomic<uint32_t> g_live_count{0};  // g_count, lock-free for observe's "anyone there?"
thread_local bool t_dispatching = false;

void compact_locked() {
    uint32_t n = 0;
    for (uint32_t i = 0; i < g_count; ++i)
        if (!g_slots[i].dead) g_slots[n++] = g_slots[i];
    for (uint32_t i = n; i < g_count; ++i) g_slots[i] = Slot{};
    g_count = n;
    g_live_count.store(n, std::memory_order_release);
}

void reset_repeats_locked() {
    for (uint32_t i = 0; i < g_count; ++i) g_slots[i].repeat = RepeatState{};
}

// ---- the observer's own state (main thread only) ----
struct Guard {
    uintptr_t record = 0;
    KilnEntity entity = 0;
    uint32_t session = 0, version = 0;
    uint64_t written = 0, synthetic = 0;  // the word Kiln wrote; the bits it set that the game's sample did not
};
Guard g_guard;
uint32_t g_prev_session = 0;
KilnEntity g_prev_entity = 0;
uint32_t g_prev_held = 0;
bool g_repeats_reset = true;
uint64_t g_sequence = 0;
uint32_t g_item_id = 0;  // the catalog cache: the last held item asked
ItemFacts g_item{};
std::atomic<uint32_t> g_ended{0};

// ---- binding and the lazy installer ----
std::atomic<bool> g_bound{false};
Layout g_layout{};  // written before g_bound turns true, under g_install_mu
std::mutex g_install_mu;
std::atomic<Installer> g_installer{nullptr};
std::atomic<bool> g_install_failed{false};
int64_t g_last_attempt_ns = 0;
std::atomic<const char*> g_why{kUnbound};
std::atomic<uint64_t> g_generation{1};

std::atomic<uint64_t> g_samples{0}, g_writes{0}, g_cleared{0}, g_skipped{0}, g_faults{0}, g_sub_faults{0}, g_installs{0};

void changed() { g_generation.fetch_add(1, std::memory_order_acq_rel); }

enum class Ready { Bound, Pending, Off };
Ready install_once() {
    const uint64_t seen = g_installs.load(std::memory_order_acquire);
    std::lock_guard install(g_install_mu);
    if (g_bound.load(std::memory_order_acquire)) return Ready::Bound;
    const Installer fn = g_installer.load(std::memory_order_acquire);
    if (!fn) return Ready::Off;
    if (g_installs.load(std::memory_order_relaxed) != seen) return Ready::Pending;  // refused while this one waited
    const int64_t now = now_ns();
    if (seen && now - g_last_attempt_ns < kInstallRetryNs) return Ready::Pending;
    const uint64_t attempt = seen + 1;
    Layout layout{};
    const char* why = nullptr;
    Install result = Install::Failed;
    try {  // a mod's thread: nothing the install throws leaves the call
        result = fn(&layout, &why);
    } catch (...) {
        why = kInstallThrew;
    }
    g_last_attempt_ns = now_ns();
    g_installs.store(attempt, std::memory_order_release);
    if (result == Install::Bound) {
        g_layout = layout;
        g_bound.store(true, std::memory_order_release);
        changed();
        return Ready::Bound;
    }
    if (result == Install::Again && attempt < kInstallAttempts) return Ready::Pending;
    g_install_failed.store(true, std::memory_order_release);
    g_why.store(why ? why : kInstallFailed, std::memory_order_release);
    g_installer.store(nullptr, std::memory_order_release);
    changed();
    return Ready::Off;
}

uint64_t bit(uint8_t n) { return n < 64 ? uint64_t{1} << n : 0; }

// The checks subscribe and repeat share; installs the hooks at the first use.
KilnResult admit(const Env& env, KilnOwner owner) {
    if (owner == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (env.owner_alive && !env.owner_alive(owner)) return KILN_ERR_REVOKED;
    if (!env.client_side || !env.hooks) return KILN_ERR_UNAVAILABLE;
    if (!g_bound.load(std::memory_order_acquire) && install_once() != Ready::Bound) return KILN_ERR_UNAVAILABLE;
    return KILN_OK;
}

KilnResult add_locked(const Slot& proto, KilnHandle* out) {
    if (g_count >= kMaxSubscriptions) return KILN_ERR_BUFFER_TOO_SMALL;
    Slot s = proto;
    s.handle = ++g_next_handle;
    g_slots[g_count++] = s;
    g_live_count.store(g_count, std::memory_order_release);
    *out = s.handle;
    return KILN_OK;
}
KilnResult add(const Slot& proto, KilnHandle* out) {
    if (t_dispatching) return add_locked(proto, out);  // from inside a callback: the list is this thread's already
    std::lock_guard lock(g_list_mu);
    return add_locked(proto, out);
}

// Ask every live slot, in order, for this sample; returns the press and release answers.
void dispatch_locked(KilnGameInputSample& base, uint64_t now_ms, const Game& g) {
    t_dispatching = true;
    const uint32_t n = g_count;  // one added from inside a callback is asked from the next sample on
    for (uint32_t i = 0; i < n; ++i) {
        Slot& s = g_slots[i];
        if (s.dead) continue;
        if (s.kind == Kind::Handler) {
            KilnGameInputSample io = base;
            if (!g.call(s.fn, &io, s.user)) {
                s.dead = true;
                g_sub_faults.fetch_add(1, std::memory_order_relaxed);
                continue;
            }
            base.release = io.release & kKnownActions;  // a bit in both: released
            base.press = io.press & kKnownActions & ~base.release;
            continue;
        }
        const uint32_t a = 1u << s.action;
        bool active = (base.held & a) != 0;
        if (s.cond) {
            bool r = false;
            if (!g.call_cond(s.cond, &base, s.user, &r)) {
                s.dead = true;
                g_sub_faults.fetch_add(1, std::memory_order_relaxed);
                continue;
            }
            active = r;
        }
        switch (repeat_step(s.repeat, active, now_ms, s.interval_ms)) {
            case Step::Press:
                base.press |= a;
                base.release &= ~a;
                break;
            case Step::Release:
                base.release |= a;
                base.press &= ~a;
                break;
            case Step::None:
                break;
        }
    }
    t_dispatching = false;
}

}  // namespace

bool make_layout(const Bits& b, Layout* out) {
    const uint8_t all[] = {b.mainhand,   b.mainhand_tap, b.mainhand_hold, b.mainhand_release, b.contextual,
                           b.contextual_tap, b.contextual_hold, b.block, b.evade, b.jump, b.ranged_arm, b.ranged_shoot,
                           b.ranged_shoot_tap};
    for (const uint8_t n : all)
        if (n > 63) return false;
    Layout l = *out;
    l.down[KILN_GAME_ACTION_PRIMARY_ATTACK] = bit(b.mainhand) | bit(b.ranged_shoot) | bit(b.ranged_shoot_tap);
    l.family[KILN_GAME_ACTION_PRIMARY_ATTACK] = l.down[KILN_GAME_ACTION_PRIMARY_ATTACK] | bit(b.mainhand_tap) |
                                                bit(b.mainhand_hold) | bit(b.mainhand_release);
    l.down[KILN_GAME_ACTION_SECONDARY_ATTACK] = l.family[KILN_GAME_ACTION_SECONDARY_ATTACK] = bit(b.ranged_arm);
    l.down[KILN_GAME_ACTION_BLOCK] = l.family[KILN_GAME_ACTION_BLOCK] = bit(b.block);
    l.down[KILN_GAME_ACTION_DODGE] = l.family[KILN_GAME_ACTION_DODGE] = bit(b.evade);
    l.down[KILN_GAME_ACTION_JUMP] = l.family[KILN_GAME_ACTION_JUMP] = bit(b.jump);
    l.down[KILN_GAME_ACTION_INTERACT] = bit(b.contextual);
    l.family[KILN_GAME_ACTION_INTERACT] = bit(b.contextual) | bit(b.contextual_tap) | bit(b.contextual_hold);
    *out = l;
    return true;
}

uint32_t decode(const Layout& l, uint64_t digital) {
    uint32_t held = 0;
    for (uint32_t a = 0; a < KILN_GAME_ACTION_COUNT; ++a)
        if (digital & l.down[a]) held |= 1u << a;
    return held;
}

uint64_t apply(const Layout& l, uint64_t digital, uint32_t press, uint32_t release) {
    for (uint32_t a = 0; a < KILN_GAME_ACTION_COUNT; ++a)
        if (release & (1u << a)) digital &= ~l.family[a];
    for (uint32_t a = 0; a < KILN_GAME_ACTION_COUNT; ++a)
        if (press & (1u << a)) digital = (digital & ~l.family[a]) | l.down[a];
    return digital;
}

int64_t world_time(uint32_t step, int64_t period) {
    if (step == 0) return 0;
    if (period == kInf || period == kNegInf) return period;
    return static_cast<int64_t>(static_cast<uint64_t>(period) * step);  // the game's imul: wraps as it does
}

int64_t elapsed(int64_t now, int64_t start) {
    if (now == kInf) return start == kInf ? 0 : kInf;
    if (now == kNegInf) return start == kNegInf ? 0 : kNegInf;
    if (start == kInf) return kNegInf;
    if (start == kNegInf) return kInf;
    return static_cast<int64_t>(static_cast<uint64_t>(now) - static_cast<uint64_t>(start));
}

uint32_t gate_reason(const GateFacts& f) {
    if (!f.known) return KILN_GAME_INPUT_GATE_UNKNOWN;
    if (!f.playing) return KILN_GAME_INPUT_GATE_NOT_PLAYING;
    if (f.kiln_ui) return KILN_GAME_INPUT_GATE_KILN_UI;
    if (f.hud != kHudInGame) return KILN_GAME_INPUT_GATE_GAME_MENU;
    if (!f.focused) return KILN_GAME_INPUT_GATE_UNFOCUSED;
    return KILN_GAME_INPUT_GATE_OPEN;
}

void publish_gate(const GateFacts& f) { g_gate.store(gate_reason(f), std::memory_order_release); }
uint32_t gate() { return g_gate.load(std::memory_order_acquire); }

int32_t held_kind(uint32_t held_item, const ItemFacts& item, const Ranged& ranged) {
    if (!held_item || !item.found) return KILN_GAME_HELD_NONE;
    if (item.equip_slot == kSlotMelee) return KILN_GAME_HELD_MELEE;
    if (item.equip_slot == kSlotTool) return KILN_GAME_HELD_TOOL;
    if (item.equip_slot != kSlotRanged) return KILN_GAME_HELD_NONE;
    if (ranged.present && ranged.item == held_item)  // the game's own word for this item
        return ranged.weapon_type == kWeaponBow ? KILN_GAME_HELD_BOW : KILN_GAME_HELD_NONE;
    return item.bow_name ? KILN_GAME_HELD_BOW : KILN_GAME_HELD_NONE;
}

bool bow_drawn(int32_t kind, uint32_t held_item, const Ranged& r, int64_t now) {
    if (kind != KILN_GAME_HELD_BOW || !r.present || r.item != held_item || r.weapon_type != kWeaponBow ||
        r.state != kRangedAimAndCharge)
        return false;
    return elapsed(now, r.start) >= r.charge;  // the game's own test in its ranged weapon update (0x478260)
}

Step repeat_step(RepeatState& s, bool active, uint64_t now_ms, uint32_t interval_ms) {
    if (!active) {
        s = RepeatState{};
        return Step::None;
    }
    switch (s.phase) {
        case Phase::Idle:
            s.phase = Phase::Pressed;
            s.pressed_at_ms = now_ms;
            return Step::Press;
        case Phase::Pressed:
            s.phase = Phase::Waiting;
            return Step::Release;
        case Phase::Waiting:
            if (now_ms - s.pressed_at_ms >= interval_ms) {
                s.phase = Phase::Pressed;
                s.pressed_at_ms = now_ms;
                return Step::Press;
            }
            return Step::Release;
    }
    return Step::None;
}

void observe(const Observation& o, const Game& g) {
    if (!g_bound.load(std::memory_order_acquire) || !o.record || !o.session) return;
    const Layout& l = g_layout;
    uint64_t digital = 0;
    if (!g.read(&digital, o.record + l.digital, sizeof digital)) {
        g_faults.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    // The writer visited: the record holds the game's sample now, whatever Kiln wrote at an earlier one.
    g_guard = Guard{};
    const bool same = o.session == g_prev_session && o.entity == g_prev_entity;
    const uint32_t held = decode(l, digital);
    const uint32_t pressed = same ? held & ~g_prev_held : held;
    g_prev_session = o.session;
    g_prev_entity = o.entity;
    g_prev_held = held;

    // The held item: the play state's id, the writer's own lookup key (client/play.held_item).
    uint32_t item_id = 0;
    uintptr_t play = 0;
    if (o.input && g.read(&play, o.input + l.play, sizeof play) && play) {
        if (!g.read(&item_id, play + l.held, sizeof item_id)) item_id = 0;
    }
    if (item_id != g_item_id) {
        g_item_id = item_id;
        g_item = ItemFacts{};
        if (item_id && !g.item(item_id, &g_item)) g_item = ItemFacts{};
    }
    Ranged ranged{};
    int64_t now = 0;
    if (item_id && g_item.found && g_item.equip_slot == kSlotRanged && o.world) {
        if (!g.ranged(o.world, o.entity, &ranged)) ranged = Ranged{};
        int64_t period = 0;
        if (g.read(&period, o.world + l.period, sizeof period)) now = world_time(o.version, period);
        else ranged.present = false;  // no clock: no draw state
    }
    const int32_t kind = held_kind(item_id, g_item, ranged);

    KilnGameInputSample s{};
    s.size = sizeof s;
    s.player = o.entity;
    s.client_session = o.session;
    s.step = o.version;
    s.held = held;
    s.pressed = pressed;
    s.held_kind = kind;
    s.held_item = item_id;
    s.flags = bow_drawn(kind, item_id, ranged, now) ? KILN_GAME_INPUT_BOW_DRAWN : 0u;
    s.gate = gate();
    s.digital = digital;
    s.sequence = ++g_sequence;
    publish(s);
    g_samples.fetch_add(1, std::memory_order_relaxed);

    if (!g_live_count.load(std::memory_order_acquire)) return;
    std::unique_lock lock(g_list_mu, std::try_to_lock);
    for (int spin = 0; !lock.owns_lock() && spin < kDispatchSpins; ++spin) {
        std::this_thread::yield();
        if (lock.try_lock()) break;
    }
    if (!lock.owns_lock()) {
        g_skipped.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    if (s.gate != KILN_GAME_INPUT_GATE_OPEN || !same) {
        // Closed (or a new session or player): nobody is asked, nothing is written, and every repeat starts over.
        if (!g_repeats_reset) reset_repeats_locked();
        g_repeats_reset = true;
        if (s.gate != KILN_GAME_INPUT_GATE_OPEN) return;
    }
    g_repeats_reset = false;
    const uint64_t now_ms = static_cast<uint64_t>(now_ns() / 1'000'000);
    dispatch_locked(s, now_ms, g);
    compact_locked();
    lock.unlock();
    const uint64_t written = apply(l, digital, s.press, s.release);
    if (written == digital) return;
    if (!g.write(o.record + l.digital, &written, sizeof written)) {
        g_faults.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    g_writes.fetch_add(1, std::memory_order_relaxed);
    const uint64_t synthetic = written & ~digital;
    if (synthetic) g_guard = {o.record, o.entity, o.session, o.version, written, synthetic};
}

void message(uintptr_t record, KilnEntity entity, uint32_t step, uint32_t live_session, const Game& g) {
    const Guard k = g_guard;
    if (!k.synthetic || step == k.version) return;  // nothing pressed, or the message of the sample itself
    g_guard = Guard{};                               // the step moved on: one check, then the record is the game's
    if (!g_bound.load(std::memory_order_acquire) || record != k.record || entity != k.entity || !live_session ||
        live_session != k.session)
        return;
    uint64_t now = 0;
    if (!g.read(&now, record + g_layout.digital, sizeof now)) {
        g_faults.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    if (now != k.written) return;  // someone changed the word since: not Kiln's to touch
    const uint64_t cleared = now & ~k.synthetic;
    if (!g.write(record + g_layout.digital, &cleared, sizeof cleared)) {
        g_faults.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    g_cleared.fetch_add(1, std::memory_order_relaxed);
}

void session_ended(uint32_t session) {
    uint32_t ended = g_ended.load(std::memory_order_acquire);
    while (session > ended && !g_ended.compare_exchange_weak(ended, session, std::memory_order_acq_rel)) {
    }
    g_guard = Guard{};
    g_prev_session = 0;
    g_prev_entity = 0;
    g_prev_held = 0;
    g_item_id = 0;
    g_item = ItemFacts{};
    g_gate.store(KILN_GAME_INPUT_GATE_NOT_PLAYING, std::memory_order_release);
    {
        std::lock_guard lock(g_list_mu);
        reset_repeats_locked();
        g_repeats_reset = true;
    }
    clear_store();
}

KilnResult last(const Env& env, KilnGameInputSample* out, uint32_t* out_reason) {
    if (!out || out->size < sizeof(KilnGameInputSample)) return KILN_ERR_INVALID_ARGUMENT;
    if (!env.client_side || !env.hooks) return KILN_ERR_UNAVAILABLE;
    KilnGameInputSample s{};
    s.size = sizeof s;
    uint32_t reason = KILN_GAME_INPUT_REASON_NO_SAMPLE;
    if (!g_bound.load(std::memory_order_acquire)) {
        const Ready ready = install_once();
        if (ready == Ready::Off) return KILN_ERR_UNAVAILABLE;
        if (ready == Ready::Pending) {
            *out = s;
            if (out_reason) *out_reason = reason;
            return KILN_OK;
        }
    }
    KilnGameInputSample got{};
    int64_t at = 0;
    if (load(&got, &at) && got.sequence && env.live_session && got.client_session == env.live_session &&
        env.live_session > g_ended.load(std::memory_order_acquire)) {
        const int64_t age = now_ns() - at;
        s = got;
        s.size = sizeof s;
        s.press = s.release = 0;
        s.age_ns = age > 0 ? static_cast<uint64_t>(age) : 0;
        reason = s.age_ns > KILN_GAME_INPUT_FRESH_NS ? KILN_GAME_INPUT_REASON_STALE : KILN_GAME_INPUT_REASON_NONE;
    }
    *out = s;
    if (out_reason) *out_reason = reason;
    return KILN_OK;
}

KilnResult subscribe(const Env& env, KilnOwner owner, KilnGameInputFn fn, void* user, KilnHandle* out) {
    if (out) *out = 0;
    if (!fn || !out) return KILN_ERR_INVALID_ARGUMENT;
    if (const KilnResult rc = admit(env, owner); rc != KILN_OK) return rc;
    Slot s;
    s.owner = owner;
    s.kind = Kind::Handler;
    s.fn = fn;
    s.user = user;
    return add(s, out);
}

KilnResult repeat(const Env& env, KilnOwner owner, uint32_t action, uint32_t interval_ms, KilnGameInputCondFn cond,
                  void* user, KilnHandle* out) {
    if (out) *out = 0;
    if (!out || action >= KILN_GAME_ACTION_COUNT) return KILN_ERR_INVALID_ARGUMENT;
    if (const KilnResult rc = admit(env, owner); rc != KILN_OK) return rc;
    Slot s;
    s.owner = owner;
    s.kind = Kind::Repeat;
    s.cond = cond;
    s.user = user;
    s.action = action;
    s.interval_ms = interval_ms < KILN_GAME_INPUT_MIN_REPEAT_MS ? KILN_GAME_INPUT_MIN_REPEAT_MS : interval_ms;
    return add(s, out);
}

KilnResult unsubscribe(KilnHandle handle) {
    if (!handle) return KILN_ERR_NOT_FOUND;
    if (t_dispatching) {  // from inside a callback: the list is this thread's; mark it, the dispatch drops it
        for (uint32_t i = 0; i < g_count; ++i)
            if (g_slots[i].handle == handle && !g_slots[i].dead) {
                g_slots[i].dead = true;
                return KILN_OK;
            }
        return KILN_ERR_NOT_FOUND;
    }
    std::lock_guard lock(g_list_mu);  // waits for a dispatch in flight
    for (uint32_t i = 0; i < g_count; ++i)
        if (g_slots[i].handle == handle && !g_slots[i].dead) {
            g_slots[i].dead = true;
            compact_locked();
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

void owner_gone(KilnOwner owner) {
    if (!owner) return;
    if (t_dispatching) {
        for (uint32_t i = 0; i < g_count; ++i)
            if (g_slots[i].owner == owner) g_slots[i].dead = true;
        return;
    }
    std::lock_guard lock(g_list_mu);
    for (uint32_t i = 0; i < g_count; ++i)
        if (g_slots[i].owner == owner) g_slots[i].dead = true;
    compact_locked();
}

void set_installer(Installer install) {
    std::lock_guard lock(g_install_mu);
    g_installer.store(install, std::memory_order_release);
    g_install_failed.store(false, std::memory_order_release);
    changed();
}

void unbound_reason(const char* why) {
    g_why.store(why ? why : kUnbound, std::memory_order_release);
    g_install_failed.store(false, std::memory_order_release);
    changed();
}

bool usable(const char** why, bool* installed) {
    const bool bound = g_bound.load(std::memory_order_acquire);
    if (installed) *installed = bound;
    if (bound || g_installer.load(std::memory_order_acquire)) return true;
    if (why) *why = g_why.load(std::memory_order_acquire);
    return false;
}

bool install_failed() { return g_install_failed.load(std::memory_order_acquire); }
uint64_t generation() { return g_generation.load(std::memory_order_acquire); }

Stats stats() {
    return {g_samples.load(std::memory_order_relaxed),  g_writes.load(std::memory_order_relaxed),
            g_cleared.load(std::memory_order_relaxed),  g_skipped.load(std::memory_order_relaxed),
            g_faults.load(std::memory_order_relaxed),   g_sub_faults.load(std::memory_order_relaxed),
            g_installs.load(std::memory_order_relaxed)};
}

void set_clock_for_tests(int64_t (*ns)()) { g_clock.store(ns ? ns : &steady_ns, std::memory_order_relaxed); }

void reset_for_tests() {
    std::lock_guard install(g_install_mu);
    std::lock_guard lock(g_list_mu);
    for (auto& s : g_slots) s = Slot{};
    g_count = 0;
    g_next_handle = 0;
    g_live_count.store(0, std::memory_order_release);
    g_guard = Guard{};
    g_prev_session = 0;
    g_prev_entity = 0;
    g_prev_held = 0;
    g_repeats_reset = true;
    g_sequence = 0;
    g_item_id = 0;
    g_item = ItemFacts{};
    g_ended.store(0, std::memory_order_release);
    g_gate.store(KILN_GAME_INPUT_GATE_UNKNOWN, std::memory_order_release);
    g_bound.store(false, std::memory_order_release);
    g_layout = Layout{};
    g_installer.store(nullptr, std::memory_order_release);
    g_install_failed.store(false, std::memory_order_release);
    g_last_attempt_ns = 0;
    g_why.store(kUnbound, std::memory_order_release);
    g_samples = g_writes = g_cleared = g_skipped = g_faults = g_sub_faults = g_installs = 0;
    changed();
    clear_store();
}

uint32_t subscriptions_for_tests() { return g_live_count.load(std::memory_order_acquire); }

}  // namespace kiln::game_input
