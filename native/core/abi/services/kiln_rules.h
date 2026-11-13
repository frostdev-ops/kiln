/* SPDX-License-Identifier: MIT
 * kiln.rules v1: per-player rules the game itself enforces, changed at the place the game decides them. The managed
 * Kiln.Rules layers rule sets from several mods (scopes, owners, how values combine) and pushes the result for each
 * player here; this service only applies what it is given.
 *
 * What the hooks change (docs/re/rules.md):
 *  - flight: the gate of the game's building fly mode (player_control_action_sequence): a player with FLY_ANYWHERE may
 *    enter and keep it outside a base, without the fly-mode unlock and the building permission (still not while dead or
 *    gliding). Server world and the client's prediction world.
 *  - building zone: the server cursor's "near a building zone" test (server_cursor_no_prediction): a player with
 *    BUILD_OUTSIDE_BASES is near one everywhere. The game copies the answer into the actor state and the replicated
 *    NetworkCursor, so the hammer's menu ("Needs a Flame Altar nearby"), the cursor and the fly-mode gate all see the
 *    player as inside a base (the game's fly mode then works for this player too). Server world only.
 *  - placement: the build cursor's refusal flags (the function both cursor programs call): FLAG_BUILD_OUTSIDE_BASES
 *    clears "outside a building zone", FLAG_IGNORE_NO_BUILD_ZONES the two inhibitor-zone flags, FLAG_IGNORE_ENEMIES
 *    "enemies nearby", FLAG_IGNORE_SHROUD "inside the Shroud", FLAG_IGNORE_BASE_PERMISSIONS "no permission";
 *    build_cost 0 clears "missing materials". Server world and the client's cursor.
 *  - costs: the materials list the building programs take (blueprints, props, undo) is scaled by build_cost; what
 *    dismantling and undo give back by build_refund. Server world only (the authority).
 *  - mining: the damage a player's terraforming deals to each material (terrain and building blocks) is scaled by
 *    dig_damage. Server world only.
 *  - Shroud: the Shroud timer's drain (fog_resistance_update): SHROUD_FREEZE skips the drain while in the Shroud and
 *    the Shroud's kill; shroud_floor holds the timer at a fraction of its max. Server world only (the HUD shows the
 *    server's replicated value).
 *  - gliding: the glider's step (locomotion_execution), its gravity and its stamina: GLIDE_UNLIMITED drains no stamina,
 *    glide_sink scales the sink, GLIDE_BOOST flies like a plane: the path takes its pitch from the camera, climbing
 *    trades speed for height and diving height for speed, sprint pushes along the path (up to glide_max_speed, climbing
 *    at most glide_climb). GLIDE_STEER steers the glider's own pitch (actor_rotation's step: S up to
 *    glide_steer_limit, centred holds, W dives); with it GLIDE_BOOST only sets the speed along the nose while sprint
 *    is held (no plane path) and sink 0 no longer holds the altitude. Server world and the client's prediction world
 *    (the same values on both, or the client is corrected every step).
 *  - NoClip: the game's own switch for a character's collision step (resolve_actor_collider reads
 *    MovementCollider.isEnabled first: off, it clears the collision results and leaves the position where the velocity
 *    step put it). A player with NOCLIP flies as with CHARACTER_FLIGHT, at once and also from the ground or water, and
 *    while it is in that flight its collision step sees the switch off. Nothing is written that outlives the step: with
 *    the hook gone, the rule ended or the flight over, the next step collides again. Server world and the client's
 *    prediction world.
 *
 * Sides: on a server (and a client that hosts a world) rules are per player entity of the server world (set). On a
 * client, the prediction world and the client's own cursor act only for the local player: set_local gives its rules.
 *
 * Threads: set / set_local / clear / get / status from any thread (whole rule snapshots are copied by the hooks, which run
 * on the game's program threads). Fail soft: a symbol missing on a build turns that one hook off (status says which). */
#ifndef KILN_RULES_H
#define KILN_RULES_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_RULES_SERVICE "kiln.rules"
#define KILN_RULES_VERSION 1u

/* KilnRulesPlayer.flags */
enum {
    KILN_RULES_FLY_ANYWHERE = 0x0001u,          /* building fly mode outside bases */
    KILN_RULES_BUILD_OUTSIDE_BASES = 0x0002u,   /* near a building zone everywhere; cursor: CursorOutsideBuildingZone cleared */
    KILN_RULES_IGNORE_NO_BUILD_ZONES = 0x0004u, /* cursor: lenient and strict build-inhibitor zones cleared */
    KILN_RULES_IGNORE_ENEMIES = 0x0008u,        /* cursor: EnemiesNearby cleared */
    KILN_RULES_IGNORE_SHROUD = 0x0010u,         /* cursor: CursorInsideFogZone cleared */
    KILN_RULES_IGNORE_BASE_PERMISSIONS = 0x0020u, /* cursor: NoPermission cleared (other players' bases) */
    KILN_RULES_SHROUD_FREEZE = 0x0040u,         /* the Shroud timer does not drain, and the Shroud cannot kill */
    KILN_RULES_GLIDE_UNLIMITED = 0x0080u,       /* gliding: no stamina cost, never forced down for lack of it */
    KILN_RULES_GLIDE_BOOST = 0x0100u,           /* gliding like a plane: pitch from the view, sprint pushes (glide_* below) */
    /* since 0.2.0-dev.16 (creative mode, docs/re/creative-mode-146.md Part 3) */
    KILN_RULES_FREE_CRAFTING = 0x0200u,         /* crafting pays nothing: the recipe's payment answers "paid" inside the game's
                                                   own transaction (the operator crafting from its own inventory); client: the
                                                   crafting menu's craft buttons stay enabled without the inputs */
    KILN_RULES_KEEP_CONSUMABLES = 0x0400u,      /* fired arrows, thrown items and spell ammunition (HOOK_CONSUME), food, drinks,
                                                   potions and bandages (HOOK_CONSUME_BUFF) are not removed */
    KILN_RULES_CHARACTER_FLIGHT = 0x0800u,      /* the character flies (HOOK_FLIGHT): airborne, it takes off into the game's
                                                   BuildingFlying locomotion state; jump climbs, sneak sinks, sprint is faster;
                                                   grounded while sinking it lands; no gravity while it may fly. Server world
                                                   and the client's prediction world (the same rules on both). Kiln.Rules
                                                   grants it only where the server's rules.json allows it (off by default) */
    KILN_RULES_FLIGHT_BASE_ONLY = 0x1000u,      /* raw base-only requests fail closed: Kiln.Rules resolves real altar
                                                   bounds server-side and sends CHARACTER_FLIGHT only while inside */
    /* since 0.2.0-dev.24 */
    KILN_RULES_GLIDE_STEER = 0x2000u,           /* the glider's own pitch steered (HOOK_GLIDE_STEER, actor_rotation): S raises
                                                   the nose up to glide_steer_limit, a centred stick holds it, W dives as in
                                                   the game; GLIDE_BOOST then speeds up along the nose instead of its plane
                                                   path. Server world and the client's prediction world (Kiln.Rules sends it
                                                   only to a game that confirmed the hook) */
    /* since 0.2.0-dev.27 */
    KILN_RULES_NOCLIP = 0x4000u,                /* the character moves through terrain, blocks and props (HOOK_NOCLIP): it flies
                                                   as with CHARACTER_FLIGHT (the same hooks; it takes off at once, from the
                                                   ground and from water too, and does not land), and while it is in that
                                                   flight the game's collision step is skipped for it. Death, the glider,
                                                   a ladder or a wall end the flight, and collision with it. Server world and
                                                   the client's prediction world (Kiln.Rules sends it only to a game that
                                                   confirmed the hook, and only where the server's rules.json allows it) */
};

typedef struct KilnRulesPlayer {
    uint32_t size;        /* sizeof(KilnRulesPlayer); KILN_RULES_PLAYER_V1_SIZE (24) and the earlier 48-byte layout are still accepted */
    uint32_t flags;       /* KILN_RULES_* */
    float build_cost;     /* x what placing takes (1 = the game's; 0 = free: also clears "missing materials") */
    float build_refund;   /* x what dismantling and undo give back (1 = the game's) */
    float dig_damage;     /* x the damage a player's tools deal to terrain and blocks (1 = the game's) */
    uint32_t reserved;    /* 0 */
    /* since 0.2.0-dev.15 (size 48) */
    float shroud_floor;    /* the Shroud timer is kept at least at this fraction of its max (0 = no floor) */
    float glide_sink;      /* x the glider's sink speed (1 = the game's; 0 = holds altitude) */
    float glide_thrust;    /* GLIDE_BOOST: acceleration along the glider's heading while sprint is held, m/s^2 */
    float glide_max_speed; /* GLIDE_BOOST: the push accelerates up to this speed, m/s (speed from a dive stays) */
    float glide_lift;      /* GLIDE_BOOST: upward acceleration while sprint is held, m/s^2 */
    float glide_coast;     /* GLIDE_BOOST: without sprint, slow down along the heading at most this fast, m/s^2 (0 = the game's drag) */
    /* since 0.2.0-dev.15 (size 56; adds to the earlier 48-byte layout) */
    float glide_climb;     /* GLIDE_BOOST: the highest climb rate while the player looks up, m/s (0 = never climbs) */
    float glide_steer_limit; /* since 0.2.0-dev.24 (was reserved, 0): GLIDE_STEER: how far above level the nose may rise,
                                degrees (0..85; held there natively) */
} KilnRulesPlayer;
#define KILN_RULES_PLAYER_V1_SIZE 24u

/* KilnRulesStatus.hooks bits: which hooks are installed on this build and side */
enum {
    KILN_RULES_HOOK_FLY = 0x01u,
    KILN_RULES_HOOK_CURSOR = 0x02u,
    KILN_RULES_HOOK_COSTS = 0x04u,
    KILN_RULES_HOOK_DIG = 0x08u,
    KILN_RULES_HOOK_ZONE = 0x10u,   /* the server cursor's building-zone test (BUILD_OUTSIDE_BASES) */
    KILN_RULES_HOOK_SHROUD = 0x20u, /* the Shroud timer's drain (SHROUD_FREEZE, shroud_floor) */
    KILN_RULES_HOOK_GLIDE = 0x40u,  /* gliding (GLIDE_UNLIMITED, glide_sink, GLIDE_BOOST) */
    /* since 0.2.0-dev.16 */
    KILN_RULES_HOOK_MAP = 0x80u,        /* client: the fog of war's discover call (map_reveal) */
    KILN_RULES_HOOK_CRAFT = 0x100u,     /* the recipe payments (FREE_CRAFTING) */
    KILN_RULES_HOOK_CRAFT_UI = 0x200u,  /* client: the crafting menu's craftable amount (FREE_CRAFTING) */
    KILN_RULES_HOOK_CONSUME = 0x400u,   /* the two ammunition consume removals (KEEP_CONSUMABLES) */
    KILN_RULES_HOOK_CONSUME_BUFF = 0x800u, /* ApplyBuffEvent's consume removal: food, drinks, potions, bandages (KEEP_CONSUMABLES) */
    KILN_RULES_HOOK_FLIGHT = 0x1000u,      /* character flight: the locomotion state, gravity and the BuildingFlying mover */
    /* since 0.2.0-dev.24 */
    KILN_RULES_HOOK_GLIDE_STEER = 0x2000u, /* the glider's pitch step in actor_rotation (GLIDE_STEER) */
    /* since 0.2.0-dev.27 */
    KILN_RULES_HOOK_NOCLIP = 0x4000u,      /* the character's collision step (NOCLIP); only ever set with HOOK_FLIGHT */
};

/* map_reveal modes (client) */
enum {
    KILN_RULES_MAP_DEFAULT = 0u, /* the game's own discovery range */
    KILN_RULES_MAP_RADIUS = 1u,  /* uncover at least `radius` metres around the player each frame */
    KILN_RULES_MAP_WHOLE = 2u,   /* uncover the whole map once per world (over about a second), then the game's range */
};

typedef struct KilnRulesCreative {
    uint32_t size;          /* in: sizeof(KilnRulesCreative) */
    uint32_t map_mode;      /* KILN_RULES_MAP_* in effect */
    float map_radius;       /* metres (KILN_RULES_MAP_RADIUS) */
    uint32_t map_revealed;  /* whole-map sweeps finished this session */
    uint64_t map_calls;     /* discover calls widened or added */
    uint64_t crafts_free;   /* recipe payments answered "paid" */
    uint64_t craft_ui;      /* craftable amounts raised in the client's menu */
    uint64_t consumes_kept; /* consume removals skipped */
    uint64_t faults;        /* creative hook bodies that faulted (the game's own path ran) */
    /* since 0.2.0-dev.16 (size 80): character flight */
    uint64_t flight_takeoffs; /* steps that turned a player's state to BuildingFlying */
    uint64_t flight_landings; /* flights that ended on the ground (sinking) */
    uint64_t flight_ends;     /* flights that ended in the air (the rule or its conditions ended): the player falls */
} KilnRulesCreative;

typedef struct KilnRulesStatus {
    uint32_t size;               /* in: sizeof(KilnRulesStatus) */
    uint32_t hooks;              /* KILN_RULES_HOOK_* installed */
    uint32_t players;            /* server entities with rules set */
    uint32_t local_flags;        /* the local player's flags (client) */
    uint64_t fly_allowed;        /* fly-gate decisions turned to "allowed" */
    uint64_t cursor_cleared;     /* cursor evaluations where a refusal flag was cleared */
    uint64_t costs_scaled;       /* material lists scaled (placement) */
    uint64_t refunds_scaled;     /* material lists scaled (dismantle, undo) */
    uint64_t digs_scaled;        /* damage values scaled */
    uint64_t faults;             /* hook bodies that faulted (the game's own path ran instead) */
    char detail[160];            /* what is off and why; NUL-terminated */
    /* since 0.2.0-dev.15 (size 256) */
    uint64_t zone_forced;        /* building-zone tests turned to "near one" */
    uint64_t shroud_held;        /* Shroud-timer steps held or recovering instead of draining */
    uint64_t glide_changed;      /* glider steps changed (sink, stamina, boost) */
    uint64_t glide_steered;      /* since 0.2.0-dev.24 (was reserved, 0): glider pitch steps steered (GLIDE_STEER) */
} KilnRulesStatus;

/* since 0.2.0-dev.27: NoClip as the hook sees it (noclip below) */
typedef struct KilnRulesNoClip {
    uint32_t size;    /* in: sizeof(KilnRulesNoClip) */
    uint32_t active;  /* 1 while the entity's collision step is skipped: its last one was, at most one tick ago (0 for entity 0) */
    uint64_t skipped; /* collision steps skipped for the entity since the world started (0 for entity 0) */
    uint64_t total;   /* collision steps skipped for every entity, both worlds of this process */
    uint64_t faults;  /* hook bodies that faulted (the game's collision step ran as usual) */
} KilnRulesNoClip;

typedef struct KilnRulesApi {
    KilnServiceHeader header; /* { sizeof(KilnRulesApi), KILN_RULES_VERSION } */

    /* Server world: the rules of player entity `entity` (a player 1..1023, or up to 16 other player characters, such as
     * a lab's spawned ones: KILN_ERR_UNAVAILABLE when those are taken). NULL or all-default values clear them. */
    KilnResult(KILN_CALL* set)(KilnEntity entity, const KilnRulesPlayer* rules);
    /* Clears every player's rules (a server world closing does this too). */
    void(KILN_CALL* clear)(void);
    /* The rules last set for `entity` (defaults when none). */
    KilnResult(KILN_CALL* get)(KilnEntity entity, KilnRulesPlayer* out);
    /* Client: the local player's rules (prediction world and the client's cursor). NULL clears. */
    KilnResult(KILN_CALL* set_local)(const KilnRulesPlayer* rules);
    KilnResult(KILN_CALL* status)(KilnRulesStatus* out);
    /* since 0.2.0-dev.16 (check header.size): client only. How much of the map the fog of war uncovers around the local
     * player (KILN_RULES_MAP_*; radius in metres for MAP_RADIUS, 50..5000). What is uncovered stays uncovered for the
     * character, as the game saves it. KILN_ERR_UNAVAILABLE when the hook is off on this build or side. */
    KilnResult(KILN_CALL* map_reveal)(uint32_t mode, float radius);
    KilnResult(KILN_CALL* creative)(KilnRulesCreative* out);
    /* since 0.2.0-dev.27 (check header.size): what the NoClip hook did for `entity` (a player or one of the other player
     * characters `set` accepts; 0 = the totals only). KILN_ERR_UNAVAILABLE when the hook is off on this build or side. */
    KilnResult(KILN_CALL* noclip)(KilnEntity entity, KilnRulesNoClip* out);
} KilnRulesApi;

#ifdef __cplusplus
}
#endif
#endif
