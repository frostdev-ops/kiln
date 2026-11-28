/* SPDX-License-Identifier: MIT
 * kiln.client.game_input v1 (since 0.2.0-dev.31.30, mod API 0.2.28): the local player's game actions as the game client
 * resolved them, and a way to press or release them for one input sample. Managed face: Kiln.GameInput (Kiln.Client).
 * docs/kiln/services/client.md "Game input"; the game side: docs/re/game-input.md.
 *
 * Where it reads and writes. The game client's local input writer fills the local player's keen::ecs::ClientPlayerInput
 * once per client world step on the main thread, after its own key binding, controller mapping and menu contexts: its
 * 64-bit digitalInput (+0x3a8, keen::ecs::PlayerDigitalInput, one bit per keen::ecs::PlayerInputType) is what the
 * simulation and the outbound input message then use. Kiln observes the writer right after that field is written (the
 * same kiln.hooks link kiln.client's building input uses, sigdb client.input_observation) and before the input message
 * copies it. Each visit is one sample: Kiln decodes the actions below, publishes them for `last`, and, while the gates
 * below are open, asks the subscribers, which may press or release actions for that sample; Kiln writes the result into
 * the same field. So rebinding, controllers and the game's own menu handling come for free, and nothing polls the
 * keyboard.
 *
 * The gates (a sample whose gate is not OPEN asks no subscriber and changes nothing): a world is in play (kiln.ui's
 * screen state PLAY), no game screen but the in-game HUD is open (menus, inventory, map, chat, dialogs: kiln.ui's
 * hud_screen is not 1), neither Kiln's menu nor a Kiln window has the input (kiln.ui's status: menu_open, capturing),
 * and the game window is the foreground window. They are read at each client world Pre (one step before the sample).
 * Without kiln.ui's screen state (kiln-ui.dll missing, or its feature ui.screens off on the build) the gate is UNKNOWN
 * and subscribers are never asked.
 *
 * Stale presses. When the game skips its writer for a step (blocked input), its input message still sends the record as
 * last written. Kiln clears the bits it pressed itself in the last sample before that message copies the record, when
 * the record, the local entity, the client session and the whole digitalInput word are still as Kiln left them and the
 * message's step is not the sample's (a mid link in the input message, sigdb client.game_input). Always on; nothing for
 * a subscriber to do.
 *
 * The actions (KILN_GAME_ACTION_*; bit (1u << action) in held, pressed, press and release). Each is a set of the game's
 * own PlayerInputType bits: "down" are the bits that mean its input is held, "family" every bit the game derives from
 * that input (tap, hold, release). IsHeld: any "down" bit set. Press: clear the family, then set "down". Release: clear
 * the family. PRIMARY_ATTACK down MainhandAction (0), RangedCombatShoot (51), RangedCombatShoot_Tap (52), family adds
 * MainhandAction_Tap/_Hold/_Release (1-3); SECONDARY_ATTACK RangedCombatArm (50); BLOCK Block (30); DODGE Evade (31);
 * JUMP Jump (32); INTERACT ContextualAction (5), family adds ContextualAction_Tap/_Hold (6, 7). The bit numbers are the
 * build's (sigdb client.game_input constants). Append only.
 *
 * Held item (held_kind, held_item). The game's held item is the one its writer looks up for the step (the play state's
 * held item id). Its kind comes from kiln.items' catalog: equipment slot MeleeWeapon (1) is MELEE, Tool (2) is TOOL,
 * RangeWeapon (5) is BOW when the game's ranged weapon state (keen::ecs::NetworkRangedWeapon of the local player in
 * the client world) holds that item as a bow (weaponType 0), and otherwise when the item's name has "Bow" in it while
 * that state holds another item; staffs and wands, and everything else, are NONE.
 * BOW_DRAWN: held_kind BOW, that state holds the item, its currentState is AimAndCharge (2), and the client world's time
 * (step x period, as the game's own getter) minus stateStartTime has reached weaponParameters.chargeDuration, the
 * effective duration the game copied at the draw's start (skills and kiln.combat's action speed included).
 *
 * Threads. last, subscribe, repeat, unsubscribe: any thread. Subscribers and repeat conditions run on the game's main
 * thread inside the input writer: decide at once, no waiting, no game calls, no Kiln calls that wait. They are asked in
 * the order they subscribed; Press after Release of the same action (or the other way round) keeps the later one. A
 * structured exception in fn or cond removes that subscription (logged). unsubscribe returns once a call in flight has
 * ended (from inside a callback it marks it and returns at once). A revoked owner's subscriptions end.
 *
 * Lazy hooks: nothing is hooked at the start. The first last, subscribe or repeat of the process installs the two mid
 * links (the observation, shared with building input, and the message guard); a game without a mod that uses game input
 * never has them. An install refused because some thread could not be checked is tried again by a later call (three
 * attempts in all).
 *
 * The capability row client.game_input: the game client, kiln.client.game_input started, sigdb client.input_observation
 * and client.game_input resolved on this build, and no failed install. Off: every member answers KILN_ERR_UNAVAILABLE
 * (on a dedicated server always).
 */
#ifndef KILN_CLIENT_GAME_INPUT_H
#define KILN_CLIENT_GAME_INPUT_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_CLIENT_GAME_INPUT_SERVICE "kiln.client.game_input"
#define KILN_CLIENT_GAME_INPUT_VERSION 1u

/* A sample older than this (at last) is STALE: about 15 client steps at 60 Hz. */
#define KILN_GAME_INPUT_FRESH_NS 250000000ull
/* repeat's interval is at least this (smaller values are raised to it). */
#define KILN_GAME_INPUT_MIN_REPEAT_MS 50u

/* Kiln.GameAction. Append only. */
enum {
    KILN_GAME_ACTION_PRIMARY_ATTACK = 0,
    KILN_GAME_ACTION_SECONDARY_ATTACK = 1,
    KILN_GAME_ACTION_BLOCK = 2,
    KILN_GAME_ACTION_DODGE = 3,
    KILN_GAME_ACTION_JUMP = 4,
    KILN_GAME_ACTION_INTERACT = 5,
    KILN_GAME_ACTION_COUNT = 6,
};

/* KilnGameInputSample.held_kind: Kiln.ActionKind's numbers (kiln.combat's action speed uses the same), or NONE. */
enum {
    KILN_GAME_HELD_NONE = -1,
    KILN_GAME_HELD_MELEE = 0,
    KILN_GAME_HELD_BOW = 1,
    KILN_GAME_HELD_TOOL = 2,
};

/* KilnGameInputSample.flags */
enum {
    KILN_GAME_INPUT_BOW_DRAWN = 1u, /* a bow draw has reached its effective charge duration (above) */
};

/* KilnGameInputSample.gate: why subscribers are (not) asked at this sample */
enum {
    KILN_GAME_INPUT_GATE_OPEN = 0,
    KILN_GAME_INPUT_GATE_NOT_PLAYING = 1, /* no world in play (main menu, loading, the game's UI not in play) */
    KILN_GAME_INPUT_GATE_GAME_MENU = 2,   /* a game screen other than the in-game HUD has the input (menus, map, chat) */
    KILN_GAME_INPUT_GATE_KILN_UI = 3,     /* Kiln's menu or a Kiln window has the input */
    KILN_GAME_INPUT_GATE_UNFOCUSED = 4,   /* the game window is not the foreground window */
    KILN_GAME_INPUT_GATE_UNKNOWN = 5,     /* kiln.ui's screen state is not available here: never open */
};

/* last()'s *out_reason. NONE: a fresh sample of the live client session. */
enum {
    KILN_GAME_INPUT_REASON_NONE = 0,
    KILN_GAME_INPUT_REASON_NO_SAMPLE = 1, /* nothing in this client session yet (out zeroed but size) */
    KILN_GAME_INPUT_REASON_STALE = 2,     /* out holds the newest sample, older than KILN_GAME_INPUT_FRESH_NS */
};

typedef struct KilnGameInputSample {
    uint32_t size;           /* set by the caller of last: sizeof(KilnGameInputSample) */
    KilnEntity player;       /* the local player's entity */
    uint32_t client_session; /* KilnClientState.client_session of the sample */
    uint32_t step;           /* the game's input version: the client world's step it stamps the input with */
    uint32_t held;           /* actions held as the game sampled them (before any subscriber) */
    uint32_t pressed;        /* actions held now and not at the previous sample of the session */
    uint32_t press;          /* a subscriber's answer: actions to press for this sample (0 in last) */
    uint32_t release;        /* a subscriber's answer: actions to release for this sample (0 in last) */
    int32_t held_kind;       /* KILN_GAME_HELD_* */
    uint32_t held_item;      /* the game's held item (keen::ItemId), 0 = none */
    uint32_t flags;          /* KILN_GAME_INPUT_* */
    uint32_t gate;           /* KILN_GAME_INPUT_GATE_* at this sample */
    uint64_t digital;        /* the game's digitalInput word as it sampled it */
    uint64_t sequence;       /* samples since the game started; never 0 for a sample */
    uint64_t age_ns;         /* last only: the sample's age, monotonic nanoseconds */
} KilnGameInputSample;       /* 72 bytes */

/* A subscriber: may change sample->press and sample->release (other fields are ignored on return); a bit left in both
 * is released. */
typedef void(KILN_CALL* KilnGameInputFn)(KilnGameInputSample* sample, void* user);
/* A repeat's condition: true while it repeats. */
typedef bool(KILN_CALL* KilnGameInputCondFn)(const KilnGameInputSample* sample, void* user);

typedef struct KilnClientGameInputApi {
    KilnServiceHeader header; /* { sizeof(KilnClientGameInputApi), KILN_CLIENT_GAME_INPUT_VERSION }; append-only */
    /* The newest sample into *out (out->size >= sizeof(KilnGameInputSample)); *out_reason (may be NULL) says whether it
     * is fresh. The first call of the process installs the hooks and answers NO_SAMPLE. INVALID_ARGUMENT: out NULL or
     * short; UNAVAILABLE: the row client.game_input is off. */
    KilnResult(KILN_CALL* last)(KilnGameInputSample* out, uint32_t* out_reason);
    /* fn(sample, user) at every sample whose gate is OPEN, until unsubscribe or the owner is revoked. INVALID_ARGUMENT:
     * owner 0 or no fn; REVOKED: the owner is revoked; UNAVAILABLE: the row is off or the hooks could not go in (yet);
     * BUFFER_TOO_SMALL: 64 subscriptions (subscribe and repeat together) already. */
    KilnResult(KILN_CALL* subscribe)(KilnOwner owner, KilnGameInputFn fn, void* user, KilnHandle* out);
    /* Presses `action` for one sample and releases it at the following ones, again every interval_ms (raised to
     * KILN_GAME_INPUT_MIN_REPEAT_MS), while cond(sample, user) is true (cond NULL: while the game samples the action as
     * held) and the gate is OPEN. While cond is false, or the gate is closed, it writes nothing and starts again from a
     * press when cond turns true; a new client session starts it again too. Errors as subscribe; INVALID_ARGUMENT also for
     * an action of KILN_GAME_ACTION_COUNT or more. Repeats are asked after the subscribers that came before them. */
    KilnResult(KILN_CALL* repeat)(KilnOwner owner, uint32_t action, uint32_t interval_ms, KilnGameInputCondFn cond,
                                  void* user, KilnHandle* out);
    /* Ends a subscribe or repeat. NOT_FOUND: not one (or already ended). */
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);
} KilnClientGameInputApi;

#ifdef __cplusplus
}
#endif
#ifdef __cplusplus
static_assert(sizeof(KilnGameInputSample) == 72, "KilnGameInputSample is 72 bytes");
static_assert(offsetof(KilnGameInputSample, held_kind) == 32 && offsetof(KilnGameInputSample, digital) == 48 &&
                  offsetof(KilnGameInputSample, sequence) == 56 && offsetof(KilnGameInputSample, age_ns) == 64,
              "KilnGameInputSample layout");
static_assert(sizeof(KilnClientGameInputApi) == 40, "KilnClientGameInputApi is 40 bytes");
#else
_Static_assert(sizeof(KilnGameInputSample) == 72, "KilnGameInputSample is 72 bytes");
_Static_assert(offsetof(KilnGameInputSample, held_kind) == 32 && offsetof(KilnGameInputSample, digital) == 48 &&
                   offsetof(KilnGameInputSample, sequence) == 56 && offsetof(KilnGameInputSample, age_ns) == 64,
               "KilnGameInputSample layout");
_Static_assert(sizeof(KilnClientGameInputApi) == 40, "KilnClientGameInputApi is 40 bytes");
#endif
#endif
