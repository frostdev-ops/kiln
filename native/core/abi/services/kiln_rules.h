/* SPDX-License-Identifier: MIT
 * kiln.rules v1: per-player rules the game itself enforces, changed at the place the game decides them. The managed
 * Kiln.Rules layers rule sets from several mods (scopes, owners, how values combine) and pushes the result for each
 * player here; this service only applies what it is given.
 *
 * What the hooks change (docs/re/rules.md):
 *  - flight: the gate of the game's building fly mode (player_control_action_sequence): a player with FLY_ANYWHERE may
 *    enter and keep it outside a base, without the fly-mode unlock and the building permission (still not while dead or
 *    gliding). Server world and the client's prediction world.
 *  - placement: the build cursor's refusal flags (the function both cursor programs call): FLAG_BUILD_OUTSIDE_BASES
 *    clears "outside a building zone", FLAG_IGNORE_NO_BUILD_ZONES the two inhibitor-zone flags, FLAG_IGNORE_ENEMIES
 *    "enemies nearby", FLAG_IGNORE_SHROUD "inside the Shroud", FLAG_IGNORE_BASE_PERMISSIONS "no permission";
 *    build_cost 0 clears "missing materials". Server world and the client's cursor.
 *  - costs: the materials list the building programs take (blueprints, props, undo) is scaled by build_cost; what
 *    dismantling and undo give back by build_refund. Server world only (the authority).
 *  - mining: the damage a player's terraforming deals to each material (terrain and building blocks) is scaled by
 *    dig_damage. Server world only.
 *
 * Sides: on a server (and a client that hosts a world) rules are per player entity of the server world (set). On a
 * client, the prediction world and the client's own cursor act only for the local player: set_local gives its rules.
 *
 * Threads: set / set_local / clear / get / status from any thread (values are read atomically by the hooks, which run
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
    KILN_RULES_BUILD_OUTSIDE_BASES = 0x0002u,   /* cursor: CursorOutsideBuildingZone cleared */
    KILN_RULES_IGNORE_NO_BUILD_ZONES = 0x0004u, /* cursor: lenient and strict build-inhibitor zones cleared */
    KILN_RULES_IGNORE_ENEMIES = 0x0008u,        /* cursor: EnemiesNearby cleared */
    KILN_RULES_IGNORE_SHROUD = 0x0010u,         /* cursor: CursorInsideFogZone cleared */
    KILN_RULES_IGNORE_BASE_PERMISSIONS = 0x0020u, /* cursor: NoPermission cleared (other players' bases) */
};

typedef struct KilnRulesPlayer {
    uint32_t size;        /* sizeof(KilnRulesPlayer) */
    uint32_t flags;       /* KILN_RULES_* */
    float build_cost;     /* x what placing takes (1 = the game's; 0 = free: also clears "missing materials") */
    float build_refund;   /* x what dismantling and undo give back (1 = the game's) */
    float dig_damage;     /* x the damage a player's tools deal to terrain and blocks (1 = the game's) */
    uint32_t reserved;    /* 0 */
} KilnRulesPlayer;

/* KilnRulesStatus.hooks bits: which hooks are installed on this build and side */
enum {
    KILN_RULES_HOOK_FLY = 0x01u,
    KILN_RULES_HOOK_CURSOR = 0x02u,
    KILN_RULES_HOOK_COSTS = 0x04u,
    KILN_RULES_HOOK_DIG = 0x08u,
};

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
} KilnRulesStatus;

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
} KilnRulesApi;

#ifdef __cplusplus
}
#endif
#endif
