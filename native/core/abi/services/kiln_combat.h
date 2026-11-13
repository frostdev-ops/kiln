/* SPDX-License-Identifier: MIT
 * kiln.combat v1: health, damage, healing, death on the server world, through the engine's own hit pipeline.
 *   1. Health: read current/max, set, heal (silent attribute writes; the game copies Health into the replicated
 *      NetworkHealth component itself, every step).
 *   2. Hits: damage / kill an entity as a real hit (HitCandidate -> impact_hit_conclusion -> applyHitToEntity): health
 *      change, killing blow, HitEvent (hit reaction, aggro, damage numbers on clients) and kill credit for the attacker.
 *   3. Invulnerability per entity (god mode): incoming damage hits are dropped, other health losses restored.
 *   4. Events for a listener (the managed Kiln.Combat): hits about to hurt or heal an entity (writable, cancelable),
 *      applied damage, deaths with the killer, player respawns.
 *   5. (later in v1) knockback on held hits, fall damage (held one step, writable), natural regeneration (held one
 *      step) and changeAttributeValue health gains as HEAL events with a cause.
 *   6. (v2) Attributes: get, set and add the current value of health, stamina, mana, body heat and oxygen of any
 *      entity that carries them (creatures and players alike), by the game's own attribute id, with the pair's minimum
 *      and maximum. The ids are read from the game's attribute registry at run time (docs/re/combat.md "Attributes").
 * Evidence and engine notes: docs/re/combat.md. Usage: docs/kiln/services/combat.md.
 *
 * Threads: everything except status(), set_listener() and (v2) attribute_ids() is tick-thread only (Pre or Post, or a
 * posted task); elsewhere KILN_ERR_WRONG_THREAD. Managed mods are always called there.
 *
 * Amounts are the engine's integer health points (a player starts with about 100-200; the HUD shows these numbers).
 * The table only grows at the end; check header.size before using a field added later. */
#ifndef KILN_COMBAT_H
#define KILN_COMBAT_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_COMBAT_SERVICE "kiln.combat"
#define KILN_COMBAT_VERSION 2u /* v2 appends the attribute functions (after `status`): check header.size before using them */

typedef struct KilnHealth {
    uint32_t size;         /* set to sizeof(KilnHealth) before calling */
    int32_t current;       /* health points (Health attribute 0x8EB84995) */
    int32_t max;           /* computed maximum (attribute 0xDC8358BE) */
    uint8_t dead;          /* the Actor's dead state (state 7, the game's state test) as the game's death check leaves
                              it at its next run: 1 while health < 1 (dead or dying), 0 once health >= 1; 0 without an Actor */
    uint8_t has_actor;     /* the entity has an Actor component (creatures, players, NPCs) */
    uint8_t invulnerable;  /* Kiln god mode is on for it */
    uint8_t reserved;
} KilnHealth;

/* KilnDamageRequest.flags */
enum {
    KILN_DAMAGE_FORCE_INTERRUPT = 0x01u, /* the hit interrupts the target's current action (HitEventFlags 0x800000) */
    KILN_DAMAGE_SILENT = 0x02u,          /* no HitEvent: health changes, but no reaction, damage number or kill credit */
    KILN_DAMAGE_AS_GAME_HIT = 0x04u,     /* enter the pending list like a hit the engine produced: it is held and shown
                                            to the listener as KILN_COMBAT_HURT (when hits are held) before it applies */
    KILN_DAMAGE_KILL = 0x08u,            /* amount = the target's current health (a guaranteed killing blow) */
    KILN_DAMAGE_NO_CREDIT = 0x10u,       /* clear the target's credit first (ExperienceSource.lastHitPlayerId): the game
                                            gives a kill's XP to the last player who hit the target, whoever lands the
                                            killing blow. With attacker 0 the kill then credits nobody */
};

typedef struct KilnDamageRequest {
    uint32_t size;        /* sizeof(KilnDamageRequest) */
    KilnEntity target;
    KilnEntity attacker;  /* root attacker for kill credit / XP (a player, a creature) or 0 */
    KilnEntity source;    /* the weapon/projectile/attacker entity the hit comes from; 0 = attacker */
    int32_t amount;       /* > 0 damage, < 0 heals through the hit path (WasHealing). Ignored with KILN_DAMAGE_KILL */
    uint16_t reaction;    /* impact::HitReactionType: 0 none, 1 very light, 2 light, 3 medium, 4 heavy, 5 boss,
                             6 long stun, 7 trap stun, 9 throw into air */
    uint8_t push_back;    /* push-back force 0..255 */
    uint8_t reserved0;
    uint32_t flags;       /* KILN_DAMAGE_* */
    float aggro;          /* threat for enemy AI (the game's hits use ~1.0) */
} KilnDamageRequest;

/* KilnHitStatus.state */
enum {
    KILN_HIT_QUEUED = 0,   /* waits for the next impact_hit_conclusion */
    KILN_HIT_HELD = 1,     /* AS_GAME_HIT: held for the listener */
    KILN_HIT_APPLIED = 2,  /* applyHitToEntity ran for the target: before/after are valid */
    KILN_HIT_DROPPED = 3,  /* the engine skipped it (target gone, no CombatFlags.handleHits, a stronger hit of the same
                              impact, invulnerable) or the listener canceled it */
};

typedef struct KilnHitStatus {
    uint32_t size;       /* set to sizeof(KilnHitStatus) before calling */
    uint32_t state;      /* KILN_HIT_* */
    int32_t health_before, health_after;
    uint8_t killing_blow; /* health went from > 0 to <= 0 */
    uint8_t reserved[3];
    uint32_t steps;      /* sim steps from the request to the state */
} KilnHitStatus;

/* ---- events (to the listener) ---- */
enum {
    KILN_COMBAT_HURT = 1,      /* a hit is about to damage `target` by `amount`; writable: amount (>= 0), canceled */
    KILN_COMBAT_HEAL = 2,      /* a hit is about to heal `target` by `amount`; writable: amount (>= 0), canceled */
    KILN_COMBAT_DAMAGED = 3,   /* a hit changed `target`'s health (health_before -> health_after); observe only */
    KILN_COMBAT_DIED = 4,      /* `target` died: the game's death program marked it dead (Actor dead state) in the last
                                  step; attacker/source = the killing blow's, when it was a hit. Observe only */
    KILN_COMBAT_RESPAWNED = 5, /* a dead player is alive again (respawn or revive) */
    KILN_COMBAT_DROPS = 6,     /* a loot container (`part`: an entity of a LootContainer_* template with DestroyOnLoot, or
                                  the corpse itself once it has an Inventory) appeared within 4 m of `target`'s corpse within 30 s of its death;
                                  writable: canceled (the container is destroyed). Its items: kiln.items read() */
    /* --- added later in v1 (events carry the fields after `step`: check KilnCombatEvent.size) --- */
    KILN_COMBAT_KNOCKBACK = 7, /* a held hit with push-back or a hit reaction is about to apply (same id as its HURT
                                  event, after it in the array). Writable: push_back (strength 0..255), direction (unit
                                  vector, normalised), reaction; canceled = push_back 0 and reaction 0 (the hit still
                                  applies). Asking for it turns the one-step hit hold on, like HURT */
    KILN_COMBAT_FALL = 8,      /* fall damage is about to apply (fall_damage_infliction; held one step). target, amount
                                  (damage, writable >= 0), health_before/max, lethal, fall_distance, fall_fraction;
                                  canceled = no damage (and no landing stagger). God-mode entities arrive canceled */
};

/* KilnCombatEvent.cause for KILN_COMBAT_HEAL */
enum {
    KILN_HEAL_HIT = 0,          /* a healing hit (spells, potions through the hit pipeline); held one step */
    KILN_HEAL_KILN = 1,         /* heal() / Combat.Heal */
    KILN_HEAL_REGENERATION = 2, /* the game's health_recharge (natural regeneration, regen buffs); held one step:
                                   canceled = the points are not added, amount writable */
    KILN_HEAL_ATTRIBUTE = 3,    /* changeAttributeValue (impact native: potions, food, skills adding Health directly);
                                   reported the Pre after it applied: canceled = Kiln takes it back, a changed amount is
                                   corrected (never below 1 when the entity was alive) */
};
#define KILN_COMBAT_MASK(kind) (1u << (kind))

/* KilnCombatEvent.origin */
enum {
    KILN_ORIGIN_GAME = 0,  /* the engine's own hit (weapons, spells, AI, traps) */
    KILN_ORIGIN_KILN = 1,  /* a damage() request */
    KILN_ORIGIN_OTHER = 2, /* no hit (fall damage, set_health, scripts): DIED only */
};

typedef struct KilnCombatEvent {
    uint32_t size;          /* sizeof(KilnCombatEvent) */
    uint32_t kind;          /* KILN_COMBAT_* */
    uint64_t id;            /* unique per event (HURT/HEAL: the held hit) */
    KilnEntity target;      /* the victim / patient (the hit's root target: a creature, not its weakspot) */
    KilnEntity part;        /* the entity the hit landed on (a weakspot child), = target if none */
    KilnEntity attacker;    /* root source (player for player hits); 0 unknown */
    KilnEntity source;      /* the direct source (weapon, projectile, attacker) */
    int32_t amount;         /* HURT/HEAL: magnitude (writable); DAMAGED: health lost (> 0) or gained (< 0) */
    int32_t health_before;  /* -1 unknown */
    int32_t health_after;   /* DAMAGED/RESPAWNED; -1 unknown */
    int32_t health_max;     /* -1 unknown */
    uint32_t hit_flags;     /* engine HitEventFlags: 1 blocked, 2 parried, 8 crit, 0x20 backstab, 0x40 weakspot,
                               0x2000 killing blow, 0x4000 healing, ... (docs/re/combat.md) */
    uint16_t damage_types;  /* engine damage type mask */
    uint16_t reaction;      /* hit reaction type */
    uint8_t attack_category, weapon_category, origin, lethal; /* lethal: HURT: amount >= current health */
    uint8_t canceled;       /* HURT/HEAL: set to drop the hit */
    uint8_t is_player;      /* target is a player entity */
    uint8_t reserved[2];
    uint32_t impact_id;     /* the engine's impact instance id (dedupe key) */
    KilnVec3 position;      /* hit point (HURT/HEAL/DAMAGED) or the entity's position (DIED/RESPAWNED), metres */
    uint64_t step;          /* sim step the event refers to */
    /* --- added later in v1 --- */
    KilnVec3 direction;     /* KNOCKBACK: push direction (writable; also the hit direction other HURT handlers see) */
    float fall_distance;    /* FALL: metres fallen (DynamicFallDamage.detectedFallDistance) */
    float fall_fraction;    /* FALL: the game's damage as a fraction of max health */
    uint8_t push_back;      /* KNOCKBACK: push-back strength 0..255 (writable) */
    uint8_t cause;          /* HEAL: KILN_HEAL_* */
    uint8_t reserved2[6];
} KilnCombatEvent;

/* Called on the tick thread in Pre, once per step, with the events gathered since the last call (count may be 0; the
 * array is valid during the call only). Returns the KILN_COMBAT_MASK bits of the kinds it wants from now on. Asking
 * for HURT or HEAL makes kiln.combat hold every entity hit the engine produces for one step (about 41 ms on a
 * dedicated server) so the listener can change or cancel it before it applies; without them hits are untouched. */
typedef uint32_t(KILN_CALL* KilnCombatListenerFn)(KilnCombatEvent* events, uint32_t count, void* user);

typedef struct KilnCombatStatus {
    uint32_t size;        /* set to sizeof(KilnCombatStatus) before calling */
    uint8_t health;       /* 1: health read/write works on this build */
    uint8_t hits;         /* 1: damage/kill and the hit hooks work */
    uint8_t deaths;       /* 1: death events work */
    uint8_t holding;      /* 1: hits are held for the listener now */
    uint32_t interest;    /* listener mask */
    uint64_t hits_seen;   /* applyHitToEntity calls */
    uint64_t hits_held, hits_released, hits_canceled, hits_modified;
    uint64_t kiln_hits;   /* damage() requests applied */
    uint64_t god_blocked; /* hits dropped by invulnerability */
    uint64_t god_restored;/* other health losses undone by invulnerability */
    uint64_t deaths_seen;  /* DIED events (deaths Kiln reported) */
    uint64_t events_delivered;
    uint32_t invulnerable;/* entities in god mode */
    uint32_t reserved;
    uint64_t game_died_events; /* the game's EntityDiedEvents read */
    uint64_t hits_deferred;    /* damage() hits moved to a later step (the engine applies at most 4 impacts per target
                                  per step) */
    uint64_t drops_seen, drops_canceled;
    uint64_t repeat_hits;      /* held hits not reported: a repeat of an impact on the same target the engine skips */
    /* --- added later in v1 (check size) --- */
    uint8_t falls;             /* 1: fall damage is intercepted on this build */
    uint8_t regen;             /* 1: natural regeneration is intercepted */
    uint8_t attr_heals;        /* 1: changeAttributeValue health gains are seen */
    uint8_t reserved3[5];
    uint64_t falls_seen, falls_canceled, falls_modified;
    uint64_t regen_seen, regen_canceled;
    uint64_t attr_heals_seen, attr_heals_corrected;
    uint64_t knockbacks_seen, knockbacks_modified;
} KilnCombatStatus;

/* ---- v2: attributes ----
 * An attribute is a number the game keeps in an ECS component of the entity (Health, Stamina, Mana, BodyHeat, Oxygen:
 * one component each, 40 or 32 bytes). Every attribute has an id (a 32-bit hash the game's attribute registry maps to a
 * component and a slot). A "pair" is a current value with its minimum and maximum, which the game computes from other
 * attributes (level, gear, buffs) and uses to clamp the current value after every write. Kiln resolves the ids of the
 * five pairs below from the game's attribute resource at run time (kiln.types gives the layout); the table is the same
 * on every entity. Values are the engine's integers (a player's stamina and mana are in the hundreds). */
enum {
    KILN_ATTR_NONE = 0,
    KILN_ATTR_HEALTH = 1,    /* component keen::ecs::Health. Writes go through set_health (god mode keeps 1) */
    KILN_ATTR_STAMINA = 2,   /* keen::ecs::Stamina (creatures and players) */
    KILN_ATTR_MANA = 3,      /* keen::ecs::Mana (players, and a few creatures such as Player_Vukah_Melee) */
    KILN_ATTR_BODY_HEAT = 4, /* keen::ecs::BodyHeat (players) */
    KILN_ATTR_OXYGEN = 5,    /* keen::ecs::Oxygen (players and most creatures) */
};

typedef struct KilnAttributeIds {
    uint32_t size;        /* set to sizeof(KilnAttributeIds) before calling */
    uint32_t kind;        /* in: KILN_ATTR_* */
    uint32_t current_id;  /* the game's attribute ids; min_id and max_id are 0 when the pair has none */
    uint32_t min_id;
    uint32_t max_id;
    uint32_t component;   /* FNV-1a-32 of the component's qualified name (keen::ecs::Stamina = 0x92410664) */
    uint8_t game_clamp;   /* 1: the game's own formula clamps the current value to [min, max] after every change (read from
                             the attribute resource); Kiln clamps as well, so a write never depends on it */
    uint8_t reserved[3];
} KilnAttributeIds;

typedef struct KilnAttributeValue {
    uint32_t size;        /* set to sizeof(KilnAttributeValue) before calling */
    uint32_t id;          /* the attribute id asked for */
    uint32_t kind;        /* KILN_ATTR_* of the pair the id belongs to */
    uint32_t current_id;  /* that pair's ids; max_id is 0 when it has none */
    uint32_t max_id;
    int32_t value;        /* the value of `id` (after the write, for set and add) */
    int32_t current;      /* the pair's current value */
    int32_t min;          /* valid when has_min */
    int32_t max;          /* valid when has_max */
    uint8_t has_min;
    uint8_t has_max;
    uint8_t is_current;   /* `id` is the pair's current value: the one set and add accept */
    uint8_t reserved;
} KilnAttributeValue;

typedef struct KilnCombatApi {
    KilnServiceHeader header; /* { sizeof(KilnCombatApi), KILN_COMBAT_VERSION } */

    /* NOT_FOUND: no such entity or it has no Health component. */
    KilnResult(KILN_CALL* health)(KilnEntity entity, KilnHealth* out);
    /* Sets current health (clamped to 0..max) with the attribute system's own add (formulas re-evaluated). 0 kills on
     * the next step without a killer. *out_applied (optional) = the new value. */
    KilnResult(KILN_CALL* set_health)(KilnEntity entity, int32_t value, int32_t* out_applied);
    /* Adds `amount` (> 0) up to max, silently (no HitEvent). *out_healed = health actually added. */
    KilnResult(KILN_CALL* heal)(KilnEntity entity, int32_t amount, int32_t* out_healed);
    /* Queues a hit; it applies in the next impact_hit_conclusion (this step when called in Pre). The engine applies at
     * most 4 different impacts to one target per step (its per-target map holds 4); kiln.combat keeps further hits on
     * a target for the following steps instead of letting the engine drop them. *out_id identifies it for
     * hit_status. NOT_FOUND: target missing; UNAVAILABLE: hits off on this build or no world yet. */
    KilnResult(KILN_CALL* damage)(KilnOwner owner, const KilnDamageRequest* req, uint64_t* out_id);
    KilnResult(KILN_CALL* hit_status)(uint64_t id, KilnHitStatus* out);
    /* God mode on/off for one entity (players too): damage hits on it are dropped before they apply, and a health
     * loss from anything else (fall damage, set_health by others, drowning) is restored in the next Pre, before the
     * game's death check runs. Several owners may hold it; it ends when the last one releases it. */
    KilnResult(KILN_CALL* set_invulnerable)(KilnOwner owner, KilnEntity entity, bool on);
    bool(KILN_CALL* invulnerable)(KilnEntity entity);
    /* Drops everything `owner` holds: its god-mode entries and its queued hits (call when the owner goes away). */
    KilnResult(KILN_CALL* release_owner)(KilnOwner owner);
    /* One listener (the managed Kiln.Combat); a second call replaces it, fn NULL removes it. Any thread. */
    KilnResult(KILN_CALL* set_listener)(KilnCombatListenerFn fn, void* user);
    KilnResult(KILN_CALL* status)(KilnCombatStatus* out); /* any thread */

    /* ---- v2: attributes (appended; check header.size >= offsetof(KilnCombatApi, attribute_add) + sizeof(void*)) ----
     * Server world, tick thread (Pre or Post) except attribute_ids (any thread). Results:
     *   INVALID_ARGUMENT  a bad `out`, a kind or an attribute id Kiln does not know (an id of the five pairs' components
     *                     only), or set/add on an id that is not a pair's current value: the minimum, the maximum and the
     *                     computed values are recomputed by the game, so a write would not last
     *   NOT_FOUND         no such entity, or it has no such component (a Vukah has no Mana) or it is not set up yet
     *   UNAVAILABLE       attributes are off on this build (the attribute resource, kiln.types, kiln.assets or the
     *                     registry was not found; logged at boot), or no server world yet
     *   WRONG_THREAD      not on the tick thread */
    /* The ids of a pair, as resolved from the game. UNAVAILABLE until the first server world runs. */
    KilnResult(KILN_CALL* attribute_ids)(uint32_t kind, KilnAttributeIds* out);
    /* Reads attribute `attribute_id` of `entity`, with its pair's current value and bounds. Any id of the five
     * components is readable (current, minimum, maximum, recharge, base...). Works on non-players. */
    KilnResult(KILN_CALL* attribute_get)(KilnEntity entity, uint32_t attribute_id, KilnAttributeValue* out);
    /* Sets a pair's current value, clamped to [min, max] (min: the pair's minimum attribute, else 0), through the
     * game's attribute code (its formulas re-run; the game copies players' values to the replicated
     * NetworkPlayerAttributes and creatures' to NetworkStamina/NetworkHealth itself). `out` (optional) = the pair
     * after the write. Health: Combat.SetHealth semantics (silent, god mode keeps 1). */
    KilnResult(KILN_CALL* attribute_set)(KilnEntity entity, uint32_t attribute_id, int32_t value, KilnAttributeValue* out);
    /* Adds `delta` (negative to spend) to a pair's current value, clamped like attribute_set. */
    KilnResult(KILN_CALL* attribute_add)(KilnEntity entity, uint32_t attribute_id, int32_t delta, KilnAttributeValue* out);

    /* ---- since 0.2.0-dev.27 (appended; check header.size >= offsetof(KilnCombatApi, hold_filter) + sizeof(void*)) ----
     * The entities the listener's handlers watch (managed event filters): while hits are held for the listener, only a
     * hit whose target, root target, source or root source is one of `entities` is held; every other hit applies at once,
     * untouched, as when nobody listens. count 0 (the default): every hit is held. The set replaces the previous one and
     * counts from the next hit conclusion. At most 4096 entities (INVALID_ARGUMENT above, or entities NULL with count >
     * 0). Any thread. */
    KilnResult(KILN_CALL* hold_filter)(const KilnEntity* entities, uint32_t count);
} KilnCombatApi;

#ifdef __cplusplus
}
#endif
#endif
