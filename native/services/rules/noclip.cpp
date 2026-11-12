// SPDX-License-Identifier: MIT
// kiln.rules' NoClip (feature rules.noclip; docs/re/rules.md section 8; docs/kiln/services/rules.md): a character with
// KILN_RULES_NOCLIP moves through terrain, blocks and props.
//
// The game has the switch itself. Each step, the `velocity` program adds Velocity to the position, and then
// `resolve_actor_collider` sweeps the character's collider through the world and corrects the position. Its per-entity
// step reads MovementCollider.isEnabled first: when that is 0 it clears the collision results (no ground, no wall) and
// returns, and the position stays where the velocity step put it. Nothing else in the game reads the switch.
//
// One hook on that step, in the server world and in the player's prediction (the same rules on both sides, as for
// character flight): for a character that is in Kiln's flight and whose rules carry NoClip, isEnabled reads 0 for the
// length of the call, and is put back before the hook returns. So nothing is written that outlives a step: when the
// rule ends, the flight ends (death, the glider, a ladder), the hook is removed or its body faults, the next step
// collides as the game always does.
//
// Only a character in Kiln's flight is touched (flight.cpp owns the test): without collision there is no ground, and a
// character that cannot fly would fall through the world.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>

#include "creative.h"
#include "kiln_hook.hpp"
#include "services/kiln_caps.h"

namespace kiln_rules_noclip {
namespace {

const KilnApi* g_api = nullptr;
kiln::RawHook h_resolve;

void logf(int level, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    if (n > 0) kiln_rules_bridge::log(level, b);
}

// From the step's 6th argument (the iterator's Actor slot): its write records, 0x30 bytes each, data pointer first.
struct Layout {
    int64_t collider = 0;  // the MovementCollider record
    int64_t dynloco = 0;   // the DynamicLocomotion record (an optional component: a null data pointer without one)
    int64_t enabled = 0;   // MovementCollider.isEnabled
} L;

struct Stats {
    std::atomic<uint64_t> total{0}, faults{0};
} S;

// What the hook did per entity: players by id, the other player characters (a lab's) by rules.cpp's extra slot.
constexpr int kPlayers = 1024, kExtra = 16;
std::atomic<uint64_t> g_tick{1};
std::atomic<uint64_t> g_last[kPlayers + kExtra]{};   // g_tick at the entity's last skipped step (0: none)
std::atomic<uint64_t> g_count[kPlayers + kExtra]{};  // its skipped steps
std::atomic<uint32_t> g_who[kExtra]{};               // the entity an extra slot's numbers belong to
// An extra slot's owner, its reset and its count change together (two characters' steps run on different threads, and
// a slot can pass from one character to the next). Players' slots are theirs by id and need no lock. Never held
// while game memory is touched.
std::mutex g_extra_mu;

int slot_of(uint32_t e) {
    if (e >= 1 && e < kPlayers) return static_cast<int>(e);
    const int x = kiln_rules_bridge::extra_slot(e);
    return x < 0 || x >= kExtra ? -1 : kPlayers + x;
}

void mark(uint32_t e) {
    S.total.fetch_add(1, std::memory_order_relaxed);
    const int s = slot_of(e);
    if (s < 0) return;
    if (s < kPlayers) {
        g_count[s].fetch_add(1, std::memory_order_relaxed);
        g_last[s].store(g_tick.load(std::memory_order_relaxed), std::memory_order_relaxed);
        return;
    }
    std::lock_guard lock(g_extra_mu);
    if (g_who[s - kPlayers].exchange(e, std::memory_order_relaxed) != e)
        g_count[s].store(0, std::memory_order_relaxed);  // the slot's earlier character is gone
    g_count[s].fetch_add(1, std::memory_order_relaxed);
    g_last[s].store(g_tick.load(std::memory_order_relaxed), std::memory_order_relaxed);
}

// The MovementCollider.isEnabled byte to hold at 0 over this step, or null: the game's step runs untouched.
uint8_t* decide(void* ctx, uint8_t* slots) {
    if (!(kiln_rules_bridge::flags_seen() & KILN_RULES_NOCLIP)) return nullptr;  // nobody has NoClip: nothing is read
    auto* dyn = *reinterpret_cast<uint8_t**>(slots + L.dynloco);
    auto* collider = *reinterpret_cast<uint8_t**>(slots + L.collider);
    if (!dyn || !collider) return nullptr;
    uint32_t e = 0;
    if (!kiln_rules_flight::noclip_flight(ctx, dyn, &e)) return nullptr;
    mark(e);
    return collider + L.enabled;
}

uint8_t* decide_guarded(void* ctx, uint8_t* slots) {
    __try {
        return decide(ctx, slots);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        S.faults.fetch_add(1, std::memory_order_relaxed);
    }
    return nullptr;
}

// The game's step with the switch held off, and the switch put back however the call ends: by returning, or by an
// exception passing through (the game's own fault, or a later link of the hook chain). The exception goes on to
// whoever handles it; the byte is never left at 0 behind it.
void step_without_collision(uint8_t* enabled, void* ctx, void* iter, void* frame, void* platform, void* a5, uint8_t* slots,
                            void* a7, void* a8, void* a9) {
    // This program has the entity's MovementCollider for writing, so no other thread reads it during the call.
    const uint8_t was = *enabled;
    *enabled = 0;
#ifdef KILN_RULES_HOST_TEST
    // the host tests stand C++ exceptions in for SEH (tests/stubs/windows.h): the same cleanup, then on with it
    try {
        h_resolve.call<void>(ctx, iter, frame, platform, a5, slots, a7, a8, a9);
    } catch (...) {
        *enabled = was;
        throw;
    }
    *enabled = was;
#else
    __try {
        h_resolve.call<void>(ctx, iter, frame, platform, a5, slots, a7, a8, a9);
    } __finally {
        *enabled = was;
    }
#endif
}

// resolve_actor_collider's per-entity step: (ctx, iterator, the entity's InertialFrame, its platform's, then four
// pointers into the iterator and a null).
void __fastcall hk_resolve(void* ctx, void* iter, void* frame, void* platform, void* a5, uint8_t* slots, void* a7, void* a8,
                           void* a9) {
    uint8_t* enabled = ctx && slots ? decide_guarded(ctx, slots) : nullptr;
    if (!enabled) {
        h_resolve.call<void>(ctx, iter, frame, platform, a5, slots, a7, a8, a9);
        return;
    }
    step_without_collision(enabled, ctx, iter, frame, platform, a5, slots, a7, a8, a9);
}

template <typename F>
bool sym(const char* name, F* out) {
    uintptr_t a = 0;
    if (g_api->symbol(KilnStr{name, std::strlen(name)}, &a) != KILN_OK || !a) return false;
    *out = reinterpret_cast<F>(a);
    return true;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

// The sigdb row rules.noclip says the signatures resolved. When the hook stays out all the same, the row is lowered
// with the reason, so `Capabilities.Is("rules.noclip")` is the whole answer for a mod.
void lower(KilnOwner owner, uint32_t reason, const char* why) {
    constexpr char kRow[] = "rules.noclip";
    const auto* caps = static_cast<const KilnCapsApi*>(
        g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, KILN_CAPS_VERSION));
    if (caps && caps->report)
        caps->report(owner, KilnStr{kRow, sizeof(kRow) - 1}, KILN_CAP_UNAVAILABLE, reason, KilnStr{why, std::strlen(why)});
}

}  // namespace

void reset() {
    std::lock_guard lock(g_extra_mu);
    for (auto& v : g_last) v.store(0);
    for (auto& v : g_count) v.store(0);
    for (auto& v : g_who) v.store(0);
}

void tick() { g_tick.fetch_add(1, std::memory_order_relaxed); }

uint32_t install(const KilnApi* api, KilnOwner owner, bool flight, std::string& off) {
    g_api = api;
    auto note = [&off](const char* why) { off += off.empty() ? why : std::string(", ") + why; };
    // rules.noclip also holds the branch the step takes with the switch off and that exit's bytes (sigdb): the
    // feature is on only where "off" means "clear the results and return".
    uintptr_t step = 0;
    if (!(feat("rules.noclip") && sym("rules/ResolveActorCollider", &step) && cst("rules/resolve.collider", &L.collider) &&
          cst("rules/resolve.dynloco", &L.dynloco) && cst("rules/collider.enabled", &L.enabled))) {
        note("NoClip (rules.noclip)");
        return 0;
    }
    // Two records of one iterator, and a bool inside a small component: anything else is not the layout this was
    // written for, and the hook stays out.
    auto record = [](int64_t v) { return v >= 0 && v <= 0x400 && v % 8 == 0; };
    if (!record(L.collider) || !record(L.dynloco) || L.collider == L.dynloco || L.enabled < 0 || L.enabled > 0x80) {
        logf(KILN_LOG_ERROR, "NoClip: unexpected layout (collider record %lld, locomotion record %lld, isEnabled %lld); it stays off",
             static_cast<long long>(L.collider), static_cast<long long>(L.dynloco), static_cast<long long>(L.enabled));
        note("NoClip (unexpected layout)");
        lower(owner, KILN_CAP_REASON_FAULTED, "the collision step's layout is not the one NoClip was written for");
        return 0;
    }
    if (!flight) {
        note("NoClip (needs character flight)");
        lower(owner, KILN_CAP_REASON_POLICY,
              "character flight's hooks are off here (rules.flight, or Creative Mode runs): NoClip flies with them");
        return 0;
    }
    h_resolve.install(api, owner, step, reinterpret_cast<void*>(&hk_resolve), "rules/ResolveActorCollider");
    if (!h_resolve) {
        logf(KILN_LOG_ERROR, "NoClip: hooking the collision step failed; it stays off");
        note("NoClip (hook failed)");
        lower(owner, KILN_CAP_REASON_FAULTED, "the hook on the collision step could not be installed");
        return 0;
    }
    logf(KILN_LOG_INFO, "NoClip: collision step rva 0x%llx (skipped for a player in Kiln's flight with the rule)",
         static_cast<unsigned long long>(step - reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr))));
    return KILN_RULES_HOOK_NOCLIP;
}

KilnResult KILN_CALL state(KilnEntity entity, KilnRulesNoClip* out) {
    if (!out || out->size < 8) return KILN_ERR_INVALID_ARGUMENT;
    if (!h_resolve) return KILN_ERR_UNAVAILABLE;
    KilnRulesNoClip v{};
    v.total = S.total.load();
    v.faults = S.faults.load();
    const int s = entity ? slot_of(entity) : -1;
    if (s >= 0) {
        std::unique_lock<std::mutex> lock(g_extra_mu, std::defer_lock);
        if (s >= kPlayers) lock.lock();  // an extra slot's owner and numbers as one
        if (s < kPlayers || g_who[s - kPlayers].load() == entity) {
            const uint64_t last = g_last[s].load(), now = g_tick.load();
            v.skipped = g_count[s].load();
            v.active = last != 0 && now - last <= 1 ? 1u : 0u;
        }
    }
    const uint32_t n = out->size < sizeof v ? out->size : static_cast<uint32_t>(sizeof v);
    v.size = n;
    std::memcpy(out, &v, n);
    return KILN_OK;
}

}  // namespace kiln_rules_noclip
