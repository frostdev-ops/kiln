/* SPDX-License-Identifier: MIT
 * kiln.gamedata v1: the game's ability and creature data, read and changed at run time, reversibly (lives in
 * kiln-content.dll next to kiln.content and shares its layer store, data packs and content sync).
 *
 * Sections (KILN_GD_*), each a registry of entries found in the game's loaded resources:
 *  - TEMPLATES: keen::ecs::TemplateResource (creatures, projectiles, explosions, props: 12,269), id = FNV-1a-32 of the
 *    template GUID, name = the template's name ("Enemy_Vukah_Melee"). Paths start with a component's type name:
 *    "Enemy01BehaviorState.aggroSetup.viewRange", "LifeTime.lifeTime", "ImpactConfiguration.configValues.NoiseRange".
 *  - ARSENALS: the enemy AI arsenals (keen::enemy::EnemyArsenalResource in the one EnemyArsenalRegistryResource), id =
 *    FNV of the arsenal GUID, name = the GUID; also found as "template:<template name>". "attacks[1].description.maxRange".
 *  - SEQUENCES: actor sub-sequences (keen::actor::ActorSubSequenceResource of every ActorSequenceResource), name = the
 *    sub-sequence's name, key also the sequence GUID ("<guid>#k" for sub k > 0). "events[SpawnEntityEvent#0].time".
 *  - BUFFS: keen::BuffType of the BuffTypeCollectionResource, id = buffTypeId ("0x...").
 *  - PERKS: keen::Perk resources, name = debugName.
 *  - SKILLS: keen::SkillTreeNode of the SkillTreeResource, id = the node id; also "item:<ability item>".
 *  - ITEMS: kiln.content's items, for the paths kiln.content's flat schema has not (config values, blob arrays):
 *    "impactValues.ManaCost", "uiValues[0].value", "sequences[0].triggeredBy".
 *
 * Paths are the game's reflection: dotted field names, "[i]" for static / blob arrays, "[Type#k]" for the k-th
 * element of a variant array of that type ("[Type]" = #0), "[*]" for every element, and inside keen::impact::Configurations
 * a config by alias or id ("impactValues.ManaCost", "configValues.0xa19409b2", ".scaleFactor" for scaled ones). A config
 * a struct does not have yet is added (its array is re-pointed at a longer copy). Values are kiln.content's
 * (KilnContentValue; keen::Time in seconds) or JSON (set_json: numbers, bools, enum / flag names, GUIDs or names of
 * templates / sequences for references, objects for structs, {"mul": 1.5} ops on the game's original).
 *
 * Layers, owners, conflicts, revert and data packs work exactly as in kiln.content (kiln_content.h); data-pack
 * sections "templates", "arsenals", "sequences", "buffs", "perks", "skills"; item paths in "items". Other services add
 * their own pack sections with register_section (e.g. "creatures").
 *
 * Threads: status, configs, definitions, register_section: any thread; everything else on the thread of kiln.content's
 * world (KILN_ERR_WRONG_THREAD otherwise). Fail soft: KILN_ERR_UNAVAILABLE when the resources or a section are not
 * available on this build. Docs: docs/kiln/services/gamedata.md; evidence docs/re/gamedata.md.
 */
#ifndef KILN_GAMEDATA_H
#define KILN_GAMEDATA_H
#include "../kiln_api.h"
#include "kiln_content.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_GAMEDATA_SERVICE "kiln.gamedata"
#define KILN_GAMEDATA_VERSION 1u

enum {
    KILN_GD_TEMPLATES = 1,
    KILN_GD_ARSENALS = 2,
    KILN_GD_SEQUENCES = 3,
    KILN_GD_BUFFS = 4,
    KILN_GD_PERKS = 5,
    KILN_GD_SKILLS = 6,
    KILN_GD_ITEMS = 7,
};

/* KilnGdEntry.flags */
enum { KILN_GD_MODIFIED = 0x1u, KILN_GD_CLONE = 0x2u };

typedef struct KilnGdEntry {
    uint32_t size;      /* in: sizeof(KilnGdEntry) */
    uint32_t section;
    uint32_t id;
    uint32_t flags;     /* KILN_GD_* */
    KilnStr name;       /* Kiln's copy, valid for the life of the process */
    uint64_t address;   /* the entry's struct in game memory (diagnostics) */
    uint8_t guid[16];   /* templates, sequences, perks, arsenals: the resource / arsenal GUID; else 0 */
} KilnGdEntry;

/* A pack section of another service. apply: the section's JSON value of one pack document (or Content.Apply call),
 * as owner `owner` ("pack:<mod>", "mod:<mod>", "sync"), from `source`; report: JSON {"applied", "errors", "warnings"}.
 * revert: take off what (owner, source) applied; source empty = every source of owner; owner empty = everything (the
 * world reset). Both run on kiln.content's world thread, inside the pack application. */
typedef KilnResult(KILN_CALL* KilnGdSectionFn)(void* user, KilnStr section, KilnStr json, KilnStr owner, KilnStr source,
                                               uint64_t generation, uint32_t reload, char* report, size_t cap,
                                               size_t* out_len);
typedef void(KILN_CALL* KilnGdRevertFn)(void* user, KilnStr section, KilnStr owner, KilnStr source);

typedef struct KilnGamedataApi {
    KilnServiceHeader header; /* { sizeof(KilnGamedataApi), KILN_GAMEDATA_VERSION } */

    /* JSON {ready, sections: {name: {available, count, changed}}, configs, externals, definitions, detail}. Any thread. */
    KilnResult(KILN_CALL* status)(char* json, size_t cap, size_t* out_len);

    uint32_t(KILN_CALL* count)(uint32_t section);
    KilnResult(KILN_CALL* entry_at)(uint32_t section, uint32_t index, KilnGdEntry* out);
    /* key: name (exact, then ignoring case), "0x" id, GUID ("<guid>#k" sub-sequences), "template:<name>" (arsenals),
     * "item:<name>" (skills). */
    KilnResult(KILN_CALL* find)(uint32_t section, KilnStr key, KilnGdEntry* out);

    /* A leaf's current value and the game's original. KILN_ERR_INVALID_ARGUMENT: the path is not a single leaf. */
    KilnResult(KILN_CALL* get)(uint32_t section, uint32_t id, KilnStr path, KilnContentValue* current,
                               KilnContentValue* original);
    /* Any node as JSON: {"path", "type", "value", "original" (leaves), "writable", "address", "config"}. */
    KilnResult(KILN_CALL* get_json)(uint32_t section, uint32_t id, KilnStr path, char* buf, size_t cap, size_t* out_len);
    KilnResult(KILN_CALL* set)(KilnOwner owner, uint32_t section, uint32_t id, KilnStr path, const KilnContentValue* v);
    /* value_json: any JSON value for the path (fans out over [*] and objects). report: {"applied", "errors"}. */
    KilnResult(KILN_CALL* set_json)(KilnOwner owner, uint32_t section, uint32_t id, KilnStr path, KilnStr value_json,
                                    char* report, size_t cap, size_t* out_len);
    /* Takes owner's layers of that path (and below) off. KILN_ERR_NOT_FOUND: owner had none. */
    KilnResult(KILN_CALL* reset)(KilnOwner owner, uint32_t section, uint32_t id, KilnStr path);
    /* Every leaf below `prefix` (empty: the whole entry) as a JSON array [{"path", "type", "value", "writable"}];
     * at most `limit` (0: 20000). */
    KilnResult(KILN_CALL* fields)(uint32_t section, uint32_t id, KilnStr prefix, uint32_t limit, char* buf, size_t cap,
                                  size_t* out_len);
    /* Writes entries as JSON (key empty: the whole section, one entry per line) to path (empty:
     * <kiln>/data/content/gamedata-<section>.json). written = the path. */
    KilnResult(KILN_CALL* dump)(uint32_t section, KilnStr key, KilnStr path, char* written, size_t cap, size_t* out_len);
    /* The config vocabulary: [{"id": "0xaf236ece", "names": ["ManaCost"], "programs": n, "type": "..."}]. */
    KilnResult(KILN_CALL* configs)(char* buf, size_t cap, size_t* out_len);

    KilnResult(KILN_CALL* register_section)(KilnOwner owner, KilnStr name, KilnGdSectionFn apply, KilnGdRevertFn revert,
                                            void* user);
    KilnResult(KILN_CALL* unregister_section)(KilnStr name);

    /* Clones (data-pack "add" lists): a JSON object {"guid", "name", "base", "fields": {...}}; report as
     * kiln.content's define(). KILN_ERR_UNAVAILABLE: clones of this section are not supported on this build. */
    KilnResult(KILN_CALL* define)(KilnOwner owner, uint32_t section, KilnStr json, char* report, size_t cap,
                                  size_t* out_len);
    KilnResult(KILN_CALL* definitions)(char* buf, size_t cap, size_t* out_len);
} KilnGamedataApi;

#ifdef __cplusplus
}
#endif
#endif
