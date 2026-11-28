// SPDX-License-Identifier: MIT
// kiln.client.game_input: the local player's game actions at the game's own input writer (abi/services/
// kiln_client_game_input.h, docs/kiln/services/client.md "Game input"). This part has no game in it: the action bits,
// the decode and the write, the gates' rule, the held item's kind and the bow's draw, the sample store, the subscriber
// and repeat lists and their dispatch, the stale press guard, and the lazy installer's state. The hooks and the service
// table are client_game_input_service.cpp's; host tests: native/core/tests/client_game_input_test.cpp.
//
// The game side (docs/re/game-input.md, client 26430625): the local input writer 0x3af340 fills the local player's
// keen::ecs::ClientPlayerInput once per client world step on the main thread; kiln.client's observation (client_input.h,
// client/InputObserved) sees it right after digitalInput and the camera are written, and hands the visit here
// (observe). The input message 0x3a8970 copies the record next; its link (client/InputMessageCopy) calls message() right
// before that copy.
//
// Threads. observe() and message() run on the game's main thread (the client frame) and session_ended() there too; they
// allocate nothing and wait for no mod thread longer than a bounded spin (a dispatch that cannot get the list skips its
// subscribers for that sample: stats().skipped). publish_gate() comes from the client world's Pre (the main thread).
// last, subscribe, repeat, unsubscribe and owner_gone: any thread.
#pragma once
#include <cstddef>
#include <cstdint>

#include "services/kiln_client_game_input.h"

namespace kiln::game_input {

constexpr uint32_t kMaxSubscriptions = 64;
constexpr uint8_t kSlotMelee = 1;   // keen::EquipmentSlotType MeleeWeapon
constexpr uint8_t kSlotTool = 2;    // Tool
constexpr uint8_t kSlotRanged = 5;  // RangeWeapon (bows and staffs; wands are MidRangeWeapon 4)
constexpr uint8_t kHudInGame = 1;   // keen::HudScreenType InGame
constexpr uint8_t kRangedAimAndCharge = 2;  // keen::ranged_weapon::State AimAndCharge
constexpr uint8_t kWeaponBow = 0;           // keen::ranged_weapon::WeaponType Bow

// The build's PlayerInputType bit numbers (sigdb client.game_input constants), 0..63 each.
struct Bits {
    uint8_t mainhand, mainhand_tap, mainhand_hold, mainhand_release;
    uint8_t contextual, contextual_tap, contextual_hold;
    uint8_t block, evade, jump;
    uint8_t ranged_arm, ranged_shoot, ranged_shoot_tap;
};

// Where the bound build keeps what observe reads, and the action masks.
struct Layout {
    uint64_t down[KILN_GAME_ACTION_COUNT];    // bits that mean the action's input is held
    uint64_t family[KILN_GAME_ACTION_COUNT];  // every bit the game derives from that input (down included)
    uint32_t digital;  // ClientPlayerInput.data.digitalInput (client/cpi.digital)
    uint32_t play;     // the input object's play state pointer (client/input.play)
    uint32_t held;     // the play state's held item id (client/play.held_item)
    uint32_t step;     // the client world's step counter (client/input.step)
    uint32_t period;   // the client world's step period, keen::Time (client/world.period)
};
// The masks from the bit numbers (docs: kiln_client_game_input.h, "The actions"). False: a number above 63.
bool make_layout(const Bits& bits, Layout* out);

uint32_t decode(const Layout& l, uint64_t digital);  // the actions held in a digitalInput word
// The word with `release`'s families cleared, then `press`'s families cleared and their down bits set.
uint64_t apply(const Layout& l, uint64_t digital, uint32_t press, uint32_t release);

// keen::Time as the game computes it: INT64_MAX and INT64_MIN are +- infinity. The client world's time (0x9bd9a0):
// step 0 gives 0, an infinite period itself, else step * period. elapsed (0x25c1e0): now - start with the infinities.
int64_t world_time(uint32_t step, int64_t period);
int64_t elapsed(int64_t now, int64_t start);

// The facts the gates are made of (client_game_input_service.cpp reads them from kiln.ui and the window at each
// client world Pre).
struct GateFacts {
    bool known;    // kiln.ui's screen state is there (kiln-ui.dll and its feature ui.screens)
    bool playing;  // in a world, the game's UI state PLAY
    uint8_t hud;   // keen::HudScreenType
    bool kiln_ui;  // Kiln's menu shows or a Kiln window captures input
    bool focused;  // the game window is the foreground window
};
uint32_t gate_reason(const GateFacts& f);  // KILN_GAME_INPUT_GATE_*
void publish_gate(const GateFacts& f);     // the gate the next samples use
uint32_t gate();                           // the gate now

// keen::ecs::NetworkRangedWeapon of the local player, the fields Kiln reads.
struct Ranged {
    bool present;
    uint8_t state;        // currentState
    uint8_t weapon_type;  // weaponParameters.weaponType
    int64_t charge;       // weaponParameters.chargeDuration (keen::Time)
    int64_t start;        // stateStartTime
    uint32_t item;        // weaponInstance.itemId
};
// One item of kiln.items' catalog.
struct ItemFacts {
    bool found;
    uint8_t equip_slot;  // keen::EquipmentSlotType
    bool bow_name;       // its name has "Bow" in it
};
int32_t held_kind(uint32_t held_item, const ItemFacts& item, const Ranged& ranged);  // KILN_GAME_HELD_*
bool bow_drawn(int32_t kind, uint32_t held_item, const Ranged& ranged, int64_t now);

// Repeat timing: one repeat's state across samples.
enum class Phase : uint8_t { Idle, Pressed, Waiting };
enum class Step : uint8_t { None, Press, Release };
struct RepeatState {
    Phase phase = Phase::Idle;
    uint64_t pressed_at_ms = 0;
};
// One sample: inactive (cond false or gate closed) writes nothing and starts over; else a press, then releases until
// interval_ms after that press, then a press again.
Step repeat_step(RepeatState& s, bool active, uint64_t now_ms, uint32_t interval_ms);

// The game's memory and the calls observe needs (the service gives guarded ones; tests give fakes).
struct Game {
    bool (*read)(void* dst, uintptr_t src, size_t n);
    bool (*write)(uintptr_t dst, const void* src, size_t n);
    bool (*ranged)(uintptr_t world, KilnEntity entity, Ranged* out);  // false: none (or it could not be read)
    bool (*item)(uint32_t id, ItemFacts* out);                        // false: kiln.items could not say
    // A subscriber's or a condition's call under a structured-exception guard: false when it faulted.
    bool (*call)(KilnGameInputFn fn, KilnGameInputSample* sample, void* user);
    bool (*call_cond)(KilnGameInputCondFn fn, const KilnGameInputSample* sample, void* user, bool* out);
};

// One visit of kiln.client's observation to the local player's record.
struct Observation {
    uintptr_t record;  // the ClientPlayerInput
    uintptr_t input;   // the client game's input object (the writer's rbp)
    uintptr_t world;   // the client world
    KilnEntity entity;
    uint32_t session;  // the client session
    uint32_t version;  // the step the game stamps this input with
};
void observe(const Observation& o, const Game& g);
// The input message's link, before it copies the record: r13 = record, edi = entity, r14d = the message's step.
void message(uintptr_t record, KilnEntity entity, uint32_t step, uint32_t live_session, const Game& g);
// The client world's STOPPING: no sample of that session is given again; repeats and the guard start over.
void session_ended(uint32_t session);

// The facts the members check (client_game_input_service.cpp).
struct Env {
    bool client_side;
    bool hooks;
    uint32_t live_session;  // the live client session, 0 = none
    bool (*owner_alive)(KilnOwner);  // nullptr: every owner is
};
KilnResult last(const Env& env, KilnGameInputSample* out, uint32_t* out_reason);
KilnResult subscribe(const Env& env, KilnOwner owner, KilnGameInputFn fn, void* user, KilnHandle* out);
KilnResult repeat(const Env& env, KilnOwner owner, uint32_t action, uint32_t interval_ms, KilnGameInputCondFn cond,
                  void* user, KilnHandle* out);
KilnResult unsubscribe(KilnHandle handle);
void owner_gone(KilnOwner owner);  // a revoked owner: its subscriptions end

// The lazy installer, as kiln.client.crosshair's (client_crosshair.h): the service gives one at its start; the first
// last, subscribe or repeat that passes its own checks calls it, once at a time. Again (nothing written, threads that
// could not be checked) lets a later call try again, at least kInstallRetryNs later and up to kInstallAttempts in all;
// meanwhile last answers NO_SAMPLE and subscribe/repeat UNAVAILABLE. Anything else, or the last attempt, is final.
enum class Install { Bound, Again, Failed };
constexpr uint32_t kInstallAttempts = 3;
constexpr int64_t kInstallRetryNs = 1'000'000'000;
using Installer = Install (*)(Layout* layout, const char** why);
void set_installer(Installer install);  // nullptr: none; clears install_failed()
void unbound_reason(const char* why);   // usable()'s reason while there is neither installer nor binding (static string)
bool usable(const char** why, bool* installed);
bool install_failed();
uint64_t generation();  // grows at every change of usable()'s answer (kiln.caps' row client.game_input)

struct Stats {
    uint64_t samples;     // samples published
    uint64_t writes;      // samples whose digitalInput Kiln changed
    uint64_t cleared;     // stale presses the guard cleared
    uint64_t skipped;     // dispatches skipped: the list was busy
    uint64_t faults;      // guarded reads or writes that faulted
    uint64_t sub_faults;  // subscribers or conditions that faulted (removed)
    uint64_t installs;    // installer calls that returned
};
Stats stats();

// Host tests.
void set_clock_for_tests(int64_t (*ns)());  // nullptr: the steady clock
void reset_for_tests();  // no samples, gate UNKNOWN, no subscriptions, unbound (default reason), no installer, zero stats
uint32_t subscriptions_for_tests();

}  // namespace kiln::game_input
