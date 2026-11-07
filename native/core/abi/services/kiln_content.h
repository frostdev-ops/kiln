/* SPDX-License-Identifier: MIT
 * kiln.content v2: the game's content registries (Forge's registries + data packs) on the server: read and change
 * items, recipes, random-loot items and default-inventory drop tables at run time, reversibly.
 *
 * Registries (KILN_CONTENT_*):
 *  - ITEMS:   every ItemInfo of the server's item registry (g38_items::ItemRegistry), id = ItemId (FNV-1a-32 of the
 *             item GUID), name = the game's debug name ("Material_T1_Wood_Logs").
 *  - RECIPES: every RecipeInfo of g38_items::RecipeRegistry, id = RecipeId, name = the recipe's debug name.
 *  - LOOT:    every LootableItemInfo of the random-loot registry (containers, world pickups), id = the item's ItemId,
 *             name = the item's debug name.
 *  - DROPS:   default-inventory tables (enemy drops, fixed chests: keen::ecs::DefaultInventoryResource). The game
 *             loads them on demand, so this registry fills as the game uses them; id = FNV-1a-32 of the table GUID,
 *             name = the GUID ("xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"). Data-pack rules for drop tables apply the
 *             moment the game first uses a table (before that roll).
 *
 * Fields: the layout of each registry's struct comes from the game's own reflection (keen::ItemInfo, keen::RecipeInfo,
 * keen::LootableItemInfo, keen::ecs::ItemStackInfoResource), flattened into dotted paths: "maxStackSize",
 * "damageSetup.dmgMod", "fuelSetup.burnTime", "perkIds[2]". Every scalar field (bool, integers, floats, enums, flag
 * sets, keen::Time) can be read; writable fields can be changed. Identity fields (ids, GUIDs) are read-only;
 * references (KILN_CONTENT_FIELD_REF, v4) are not values but can be pointed at other loaded resources with set_ref()
 * (new items freely, game items with KILN_CONTENT_REF_ALLOW_GAME). keen::Time values are seconds. Recipe
 * inputs/outputs and drop-table stacks go through stacks()/set_stacks().
 *
 * Changes are layered per owner: the game's original value is kept; each owner's change is a layer; the newest layer
 * wins (data packs in mod load order, then changes made by code in call order); revert_owner() removes an owner's
 * layers and puts back whatever is left (another owner's value or the original). Nothing is saved: the game data is
 * rebuilt from its files on every start, and kiln.content re-applies data packs at world load (mods re-apply their
 * code changes in the ready callback). See docs/kiln/services/content.md.
 *
 * Data packs: <game>/kiln/mods/<mod>/content/<file>.json, applied in mod load order (pack_mods; without it folder
 * order, skipping folders with a .disabled file) when the registries have loaded, re-applied when a file changes
 * (checked every ~2 s) or the set of loaded mods changes. Format: docs/kiln/services/content.md.
 *
 * Which world: the table works on the registries of the **server world** (a dedicated server, or a client while it
 * hosts), else, in a client joined to another machine's game, on the **client world**'s own registries
 * (g38_items::ItemRegistry / RecipeRegistry of the client world; random loot and drop tables are server-only, so they
 * stay unavailable there). When that world stops (KILN_WORLD_STOPPING), every layer is taken off while the world is
 * still intact and the service waits for the next world (ready subscribers run again at its load).
 *
 * Threads: every function except status() runs on the thread of that world only (KILN_ERR_WRONG_THREAD): the tick
 * thread for the server world, the game's main thread for the client world. Ready callbacks run there (Pre). Fail
 * soft: missing symbols or an unexpected layout make a registry (or the service) unavailable; nothing is written.
 *
 * Versions: v1 = up to pack_mods; v2 appends export_changes; v3 appends define /
 * definitions (new items and recipes); v4 appends set_ref / undefine_owner / info (kitbash references,
 * per-language texts, loot / drops / knowledge of new items, missing-content placeholders) and the field flag in
 * KilnContentField.reserved[0]; icon_alias is appended to v4 (check header.size). Check header.version (or header.size)
 * before calling an appended function. */
#ifndef KILN_CONTENT_H
#define KILN_CONTENT_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_CONTENT_SERVICE "kiln.content"
#define KILN_CONTENT_VERSION 4u

enum {
    KILN_CONTENT_ITEMS = 1,
    KILN_CONTENT_RECIPES = 2,
    KILN_CONTENT_LOOT = 3,
    KILN_CONTENT_DROPS = 4,
};

/* KilnContentValue.type / KilnContentField.value_type */
enum {
    KILN_CV_NONE = 0,
    KILN_CV_BOOL = 1,
    KILN_CV_INT = 2,    /* integers, enums, flag sets: `i` (u64 fields: the bit pattern) */
    KILN_CV_FLOAT = 3,  /* f32 / f64: `f` */
    KILN_CV_TIME = 4,   /* keen::Time: `f` in seconds (`i` = nanoseconds) */
    KILN_CV_GUID = 5,   /* read-only: `guid` */
};

typedef struct KilnContentValue {
    uint32_t type;      /* KILN_CV_* */
    uint32_t reserved;
    int64_t i;          /* BOOL (0/1), INT, TIME (ns) */
    double f;           /* FLOAT, TIME (s); INT and BOOL also as a double */
    uint8_t guid[16];   /* GUID */
} KilnContentValue;

/* KilnContentField.enum_kind */
enum { KILN_CONTENT_PLAIN = 0, KILN_CONTENT_ENUM = 1, KILN_CONTENT_FLAGS = 2 };

typedef struct KilnContentField {
    uint32_t size;        /* in: sizeof(KilnContentField) */
    uint32_t index;
    KilnStr path;         /* "damageSetup.dmgMod"; Kiln's copy, valid for the life of the process */
    KilnStr type_name;    /* the game's type, e.g. "keen::float32", "keen::ItemRarity" */
    uint32_t offset;      /* from the start of the struct */
    uint8_t bytes;        /* 1, 2, 4, 8 or 16 */
    uint8_t value_type;   /* KILN_CV_* */
    uint8_t is_signed;
    uint8_t writable;
    uint8_t enum_kind;    /* KILN_CONTENT_PLAIN / ENUM / FLAGS (names: enum_name) */
    uint8_t reserved[3];  /* v4: reserved[0] = KILN_CONTENT_FIELD_* flags */
    uint32_t enum_count;
} KilnContentField;

/* KilnContentField.reserved[0] (v4) */
enum { KILN_CONTENT_FIELD_REF = 0x1u /* a reference (16-byte GUID, value_type GUID): settable through set_ref */ };

/* set_ref() flags (v4) */
enum { KILN_CONTENT_REF_ALLOW_GAME = 0x1u /* allow changing a reference of a game item (opt-in; new items need none) */ };

/* KilnContentEntry.flags */
enum { KILN_CONTENT_MODIFIED = 0x1u /* at least one field differs from the game's original */ };

typedef struct KilnContentEntry {
    uint32_t size;        /* in: sizeof(KilnContentEntry) */
    uint32_t kind;
    uint32_t id;
    uint32_t flags;
    KilnStr name;         /* Kiln's copy, valid for the life of the process */
    uint64_t address;     /* the struct in game memory (diagnostics only) */
    uint8_t guid[16];     /* items: the item GUID; recipes: the recipe GUID; drops: the table GUID; loot: the item GUID */
} KilnContentEntry;

/* export_changes() kinds (v4): also the gamedata sections (templates, arsenals, sequences, buffs, perks, skills) and
 * item config paths, when the build has them (kiln.gamedata) */
#define KILN_CONTENT_EXPORT_GAMEDATA (1u << 16)

/* stacks(): which list */
enum {
    KILN_CONTENT_INPUTS = 0,   /* recipes: ingredients */
    KILN_CONTENT_OUTPUTS = 1,  /* recipes: results */
    KILN_CONTENT_DROP_STACKS = 2, /* drops: every stack of the table's group tree, depth first */
};

typedef struct KilnContentStack {
    uint32_t item;        /* ItemId (0 for a category input) */
    uint32_t category;    /* recipe inputs: RecipeInputCategoryId when the input is "any item of a category" */
    uint32_t count;       /* recipes: amount; drops: countMin */
    uint32_t count_max;   /* drops: countMax (recipes: = count) */
    float chance;         /* drops: spawnProbability (recipes: 1) */
    float weight;         /* drops: weight in a selector group (recipes: 0) */
    uint32_t group;       /* drops: index of the group in the tree, depth first (read-only); recipes: 0 */
    uint32_t reserved;
} KilnContentStack;

/* KilnContentStatus.registries bits: which registries work on this build */
enum { KILN_CONTENT_HAS_ITEMS = 1, KILN_CONTENT_HAS_RECIPES = 2, KILN_CONTENT_HAS_LOOT = 4, KILN_CONTENT_HAS_DROPS = 8 };

typedef struct KilnContentStatus {
    uint32_t size;          /* in: sizeof(KilnContentStatus) */
    uint8_t ready;          /* registries loaded and data packs applied */
    uint8_t registries;     /* KILN_CONTENT_HAS_* */
    uint8_t stack_guard;    /* 1: stacks above an item's max are kept (not cut down by the game) */
    uint8_t reload_pending; /* 1: a data-pack re-application is queued for the next Pre (pack_mods changed) */
    uint32_t items, recipes, loot, drops;
    uint32_t packs;         /* data-pack files applied */
    uint32_t pack_errors;   /* problems in the last application (see report()) */
    uint32_t conflicts;     /* fields set by more than one owner to different values */
    uint32_t changed;       /* fields currently differing from the game's original */
    uint64_t generation;    /* +1 per application (world load, pack reload) */
    char detail[160];
} KilnContentStatus;

/* reason of a ready callback */
enum { KILN_CONTENT_WORLD_LOADED = 0, KILN_CONTENT_PACKS_RELOADED = 1 };
typedef void(KILN_CALL* KilnContentReadyFn)(uint64_t generation, uint32_t reason, void* user);

typedef struct KilnContentApi {
    KilnServiceHeader header; /* { sizeof(KilnContentApi), KILN_CONTENT_VERSION } */

    KilnResult(KILN_CALL* status)(KilnContentStatus* out); /* any thread */

    /* Registries. count() is 0 until ready. find: debug name (exact, then case-insensitive), "0x1234abcd" / decimal
     * id, or (drops) the table GUID. */
    uint32_t(KILN_CALL* count)(uint32_t kind);
    KilnResult(KILN_CALL* entry_at)(uint32_t kind, uint32_t index, KilnContentEntry* out);
    KilnResult(KILN_CALL* find)(uint32_t kind, KilnStr key, KilnContentEntry* out);

    /* Field schema of a registry (from the game's reflection). */
    uint32_t(KILN_CALL* field_count)(uint32_t kind);
    KilnResult(KILN_CALL* field_at)(uint32_t kind, uint32_t index, KilnContentField* out);
    KilnResult(KILN_CALL* field_find)(uint32_t kind, KilnStr path, KilnContentField* out);
    KilnResult(KILN_CALL* enum_name)(uint32_t kind, uint32_t field, uint32_t index, KilnStr* name, int64_t* value);

    /* Values. get: current (and, optional, the game's original). set: a layer of `owner`; INT/FLOAT/TIME/BOOL values
     * are converted to the field's type (rounded, range-checked: KILN_ERR_INVALID_ARGUMENT when out of range).
     * reset: removes `owner`'s layer of that field. */
    KilnResult(KILN_CALL* get)(uint32_t kind, uint32_t id, KilnStr path, KilnContentValue* current,
                               KilnContentValue* original);
    KilnResult(KILN_CALL* set)(KilnOwner owner, uint32_t kind, uint32_t id, KilnStr path, const KilnContentValue* value);
    KilnResult(KILN_CALL* reset)(KilnOwner owner, uint32_t kind, uint32_t id, KilnStr path);

    /* Recipe inputs / outputs, drop-table stacks. *out_count = the list's length (more than cap: the first cap are
     * written, KILN_ERR_BUFFER_TOO_SMALL). set_stacks: recipes: replaces the whole list (1..32 entries; items must
     * exist; a category input keeps its category); drops: changes count/count_max/chance/weight of the existing stacks
     * (same length and order as stacks() returned; items cannot change). */
    KilnResult(KILN_CALL* stacks)(uint32_t kind, uint32_t id, uint32_t which, KilnContentStack* out, uint32_t cap,
                                  uint32_t* out_count);
    KilnResult(KILN_CALL* set_stacks)(KilnOwner owner, uint32_t kind, uint32_t id, uint32_t which,
                                      const KilnContentStack* stacks, uint32_t count);

    /* A data-pack document (the content/<file>.json format) applied as `owner`'s layers. `source` names it in reports.
     * report (optional): JSON {"applied":n,"errors":[...],"conflicts":[...]} (NUL-terminated, cut at cap).
     * KILN_ERR_INVALID_ARGUMENT: not JSON / not a pack (nothing applied). */
    KilnResult(KILN_CALL* apply_json)(KilnOwner owner, KilnStr json, KilnStr source, char* report, size_t cap,
                                      size_t* out_len);
    /* Removes every layer of `owner` (fields, stacks, drop rules); returns how many fields were put back. */
    uint32_t(KILN_CALL* revert_owner)(KilnOwner owner);

    /* Writes every registry (schema + entries with their non-zero fields, stacks, originals of changed fields) as
     * JSON to `path` (empty: <game>/kiln/data/content/dump.json). *written = the path written (NUL-terminated). */
    KilnResult(KILN_CALL* dump)(KilnStr path, char* written, size_t cap, size_t* out_len);
    /* Re-reads every mod folder's content/<file>.json now (also happens when a file changes). */
    KilnResult(KILN_CALL* reload_packs)(void);
    /* JSON report of the last data-pack application: packs, errors, conflicts. NUL-terminated, cut at cap. */
    KilnResult(KILN_CALL* report)(char* buffer, size_t cap, size_t* out_len);

    /* fn runs after every application (world load, pack reload), on the tick thread (Pre), after the data packs:
     * code changes made there are layered on top. Subscribing after the world loaded: fn runs in the next Pre (reason
     * WORLD_LOADED). Removed when `owner` is revoked (checked every ~1 s) or by unsubscribe_ready. */
    KilnResult(KILN_CALL* subscribe_ready)(KilnOwner owner, KilnContentReadyFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe_ready)(KilnHandle handle);

    /* The stack guard: while on, the game no longer cuts a stack down to its item's max stack size (its
     * "[inventory] Deleted %k items from stack" check). Switched on automatically when an item's max stack size is
     * raised, so reverting a raise does not delete items; it stays on until the server restarts. mode: -1 query,
     * 0 off, 1 on. KILN_ERR_UNAVAILABLE: not on this build. */
    KilnResult(KILN_CALL* stack_guard)(int32_t mode, uint8_t* out_on);

    /* Names `owner` in reports and conflict lines (e.g. "mod bigstacks"). Any thread. */
    KilnResult(KILN_CALL* label_owner)(KilnOwner owner, KilnStr label);

    /* Which mods' data packs apply, in which order: a JSON array of mod ids in load order (Kiln.Content passes the
     * loaded mods from Kiln.Host's mod list, so a pack follows its mod: unload reverts it, a reload re-applies it).
     * "null" (the default) = every mod folder without a .disabled file, in folder order. A change after the world
     * loaded re-applies the packs at the next Pre. Any thread. */
    KilnResult(KILN_CALL* pack_mods)(KilnStr json);

    /* ---- v2 ---- */

    /* The effective changes as a data-pack document with absolute values (Kiln.Network's content sync sends it to
     * clients): every field whose current value differs from the game's original, and the whole current list of every
     * changed recipe input / output list. `kinds`: bit mask of (1u << KILN_CONTENT_*); 0 = items and recipes. Entries
     * are keyed by id ("0x2b0cf1d5") with their debug name in "@name"; values are raw numbers (enums and flag sets as
     * integers, keen::Time in seconds). Drop tables are not exported (their changes are rules applied on the server).
     * Document: {"format":1,"@fields":n,"items":{...},"recipes":{...},"loot":{...}}. *out_len = its length
     * (NUL-terminated, cut at cap: KILN_ERR_BUFFER_TOO_SMALL). Tick thread (of the table's world). */
    KilnResult(KILN_CALL* export_changes)(uint32_t kinds, char* buffer, size_t cap, size_t* out_len);

    /* ---- v3: new items and recipes (Forge's DeferredRegister) ---- */

    /* Defines a new item (kind KILN_CONTENT_ITEMS) or recipe (KILN_CONTENT_RECIPES) as `owner`'s, from a JSON object
     * (the same object as an entry of a data pack's "items": {"add": [...]} / "recipes": {"add": [...]} list):
     *   item:   {"guid": fixed GUID, "name": debug name, "base": item to clone (name | "0x" id | GUID),
     *            "displayName", "description", "lore" (texts; shown by the client), "fields": {"<path>": value}}
     *   recipe: {"guid", "name", "base": recipe to copy and/or "station": a recipe whose station to use | "0x" workshop
     *            id | "hand", "inputs": [{"item"|"category", "count"}], "outputs": [{"item", "count"}],
     *            "duration": s, "unlock": "always" (default: no knowledge needed) | "base", "displayName", "fields"}
     * id = FNV-1a over the GUID's 16 bytes (the game's own ItemId / RecipeId), so every process that defines the same
     * GUID agrees. The definition is inserted into the registries of every world that runs here (the server world,
     * and a client's own world) as soon as they have loaded, before the save is read; defined after that, it is in
     * at the next Pre (on the active world's thread: at once). Defining a GUID again by the same owner (or after its
     * owner went away: a reload) updates / adopts it. When the owner goes away the item stays registered until the
     * process ends (saved stacks are never cut); recipes leave their station's list. report (optional): JSON
     * {"ok", "guid", "id", "name", "state": defined | unchanged | redefined | adopted, "error" | "warning"}.
     * KILN_ERR_INVALID_ARGUMENT: a bad definition (report says why); KILN_ERR_UNAVAILABLE: not on this build. Any
     * thread. */
    KilnResult(KILN_CALL* define)(KilnOwner owner, uint32_t kind, KilnStr json, char* report, size_t cap,
                                  size_t* out_len);
    /* Every definition as a JSON array: {kind, guid, id, name, base, owner, source, state: pending | registered |
     * orphaned | error, worlds: ["server", "client"], version, error?, listed? (recipes: on its station)}. Any thread. */
    KilnResult(KILN_CALL* definitions)(char* buffer, size_t cap, size_t* out_len);

    /* ---- v4: kitbash references, texts per language, distribution, placeholders ---- */

    /* Points reference fields of a registered item at resources the game already loaded ("kitbash"), as `owner`'s
     * layers (reverted like any field: revert_owner, reset). kind: KILN_CONTENT_ITEMS. key: "model" (a bundle:
     * visual entity, models, model setup, hide mask, collection model), "visual", "perks", "weaponType", "impact",
     * "program", "animation", "collectionModel", "iconModel", "refs" ({"<path>": value}), or the path of a reference
     * field ("equipment.visualEntity"). value_json: a JSON value: "item:<name>" (that item's value of the same field /
     * bundle), "item:<name>.<path>", a GUID that some loaded item uses in a field of the same reference type (a template
     * the engine loaded, for template references), "none"; perks: a list of up to 5 (null = keep the slot) or
     * "item:<name>". Items defined with define() need no flag; game items need KILN_CONTENT_REF_ALLOW_GAME.
     * report (optional): JSON {"ok", "problems": [...]}. KILN_ERR_INVALID_ARGUMENT: nothing applied (problems say why).
     * Tick thread (of the table's world). The same keys work in data-pack item rules ("@refs": true / "options":
     * {"refs": true} for game items) and in definitions. */
    KilnResult(KILN_CALL* set_ref)(KilnOwner owner, uint32_t kind, uint32_t id, KilnStr key, KilnStr value_json,
                                   uint32_t flags, char* report, size_t cap, size_t* out_len);
    /* Sets every definition of `owner` aside as if the owner had gone away (items stay registered until the process
     * ends, recipes leave their station; defining them again adopts them). Returns how many. Any thread. */
    uint32_t(KILN_CALL* undefine_owner)(KilnOwner owner);
    /* JSON: {"languages": [game language keys by keen::LanguageId], "textLanguage": bool (the hooks answer per
     * language), "refs": {pools per reference type}, "placeholders": [...], "distribution": {...}}. Any thread. */
    KilnResult(KILN_CALL* info)(char* buffer, size_t cap, size_t* out_len);

    /* ---- appended to v4 (check header.size) ---- */

    /* icon_alias: the UI looks the texture key `from` up as `to` for as long as kiln.assets does not serve an icon
     * under `from` (icon_state != KILN_ICON_SERVED), the way a new item's icon falls back to its base item's. For a
     * UiTexture GUID of a mod's own (a status's or a perk's PNG icon registered with kiln.assets) named in game data
     * before the UI cache knows it: a key the cache lacks stops the game. Stays for the session. Client; harmless on a
     * server. Returns KILN_ERR_UNAVAILABLE when the UI texture hook is off; in that case callers must keep the
     * original texture until the new one is SERVED. Any thread. */
    KilnResult(KILN_CALL* icon_alias)(const uint8_t from[16], const uint8_t to[16]);
} KilnContentApi;

#ifdef __cplusplus
}
#endif
#endif
