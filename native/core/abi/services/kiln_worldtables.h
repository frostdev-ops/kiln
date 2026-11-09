/* SPDX-License-Identifier: MIT
 * kiln.worldtables v1: the game's shared building-material tables, merged from every enabled mod at boot.
 * Guide: docs/kiln/services/worldtables.md. Evidence: docs/re/building.md.
 *
 * Several game resources are one table for the whole game (materialGuids, the terraforming registry, the material
 * pools, the blueprint items, and on the client the render tables). Two mods can't each override them, so this service
 * reads the game's resident bytes when its resource service comes up, applies every mod's entries, and serves the
 * results through kiln.assets, before anything in the game reads them.
 *
 * Ids. A building material is a byte in a cell, 128..255. Live materials (enabled mods) take the free ids in sorted GUID
 * order; kept materials (absent mods, remembered per world) come after them, also sorted. So a client that doesn't know a
 * server's kept materials still agrees on every live id, and a kept id is always past the render tables' count. A save
 * keeps a GUID per id and remaps on load, so ids may move between sessions.
 *
 * Threads: any thread unless noted. Strings stay valid for the life of the process.
 * Fail soft: a game build that doesn't resolve leaves the game's tables untouched: state() is KILN_WT_OFF.
 */
#ifndef KILN_WORLDTABLES_H
#define KILN_WORLDTABLES_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_WORLDTABLES_SERVICE "kiln.worldtables"
#define KILN_WORLDTABLES_VERSION 1u

/* state() */
enum {
    KILN_WT_OFF = 0,       /* turned off, or this game build doesn't resolve: the game's tables are untouched */
    KILN_WT_WAITING = 1,   /* the game's resource service isn't up yet: no ids yet */
    KILN_WT_UNTOUCHED = 2, /* nothing to merge: the game's tables are served as they are */
    KILN_WT_MERGED = 3,    /* the merged tables are served */
};

/* KilnWtMaterial.state */
enum {
    KILN_WT_LIVE = 0, /* an enabled mod's material */
    KILN_WT_KEPT = 1, /* an absent mod's material, kept so a world's cells of it stay */
};

typedef struct KilnWtMaterial {
    uint32_t size;      /* set by the caller: sizeof(KilnWtMaterial) */
    uint32_t state;     /* KILN_WT_LIVE, KILN_WT_KEPT */
    uint8_t guid[16];   /* the material's GUID, game byte order. Follows from mod id and block name: UUIDv5(<mod>:material:<name>), a terrain one's <mod>:terrain:<name> */
    uint32_t cell_id;   /* the id in a cell: 128..255 a building material (index into the render tables: cell_id - 128), 1..127 a terrain material */
    uint32_t reused;    /* 1: a slot the game has marked deprecated; its table entries are this material's now */
    uint32_t item_id;   /* the block item's ItemId, 0 for a kept material */
    uint32_t reserved;
    KilnStr mod;        /* the mod id, as last seen for a kept material */
    KilnStr block;      /* the block's name */
    KilnStr mod_dir;    /* the mod's folder, absolute; empty for a kept material */
    KilnStr definition; /* the block's JSON as written, so a table writer reads its own fields ("surface"); "{}" when kept */
} KilnWtMaterial;

/* A game material a mod changes (MaterialTweakBlock), for a writer of per-material tables (a retexture). */
/* A crafting prop a mod made (PropBlock.Provides): a place in the game's table of props a recipe can ask to stand near. */
typedef struct KilnWtCraftingProp {
    uint32_t size;      /* set by the caller: sizeof(KilnWtCraftingProp) */
    uint32_t state;     /* KILN_WT_LIVE */
    uint8_t guid[16];   /* UUIDv5(<mod>:craftingprop:<block name or its "was">) */
    uint32_t prop_id;   /* keen::CraftingPropId: FNV-1a over the GUID's 16 bytes */
    uint32_t index;     /* its bit in keen::CraftingPropMask: 51..63 (the game's own are 0..50) */
    uint32_t item_id;   /* the ItemId of the item that places it */
    uint32_t name_tag;  /* the loca tag in its table entry: the name of that item */
    KilnStr mod;
    KilnStr block;
} KilnWtCraftingProp;

typedef struct KilnWtTweak {
    uint32_t size;      /* set by the caller: sizeof(KilnWtTweak) */
    uint32_t cell_id;   /* the game material's id in a cell: 1..127 terrain, 128..255 building */
    KilnStr mod;
    KilnStr block;
    KilnStr mod_dir;    /* the mod's folder, absolute */
    KilnStr definition; /* the block's JSON as written ("surface", "strength") */
} KilnWtTweak;

struct KilnWorldTablesApi;

/* A table writer runs once on the game's thread, after the ids are known and before any of the game's reads: read the
 * materials with material_count / material_at and resource_put the tables you own through kiln.assets. A writer that
 * can't do its table logs why and leaves it: the service's own tables stay merged. */
typedef void(KILN_CALL* KilnWtWriterFn)(const struct KilnWorldTablesApi* api, void* user);

typedef struct KilnWorldTablesApi {
    KilnServiceHeader header; /* { sizeof(KilnWorldTablesApi), KILN_WORLDTABLES_VERSION } */

    int32_t(KILN_CALL* state)(void); /* KILN_WT_* */

    /* What client and server have to agree on: 16 hex digits over the live materials' ids and GUIDs. The same text for
     * every install with no live material, including one with the service off. */
    KilnStr(KILN_CALL* table_hash)(void);

    /* Every material in the tables: the building materials, then the terrain ones (cell_id < 128), live ones first in each. 0 before the merge. */
    uint32_t(KILN_CALL* material_count)(void);
    KilnResult(KILN_CALL* material_at)(uint32_t index, KilnWtMaterial* out);
    /* The cell id of a material's GUID, or -1: not in the tables. */
    int32_t(KILN_CALL* material_id)(const uint8_t guid[16]);
    /* The cell id of the first id after the game's own table entries, or -1 before the merge. */
    int32_t(KILN_CALL* first_new_id)(void);

    /* Adds a table writer. In on_ready: the merge runs after every service's on_ready. KILN_ERR_UNAVAILABLE: too late. */
    KilnResult(KILN_CALL* add_writer)(KilnOwner owner, KilnWtWriterFn fn, void* user);

    /* A definition from code: {"format": 1, "mod": "<id>", "dir": "<folder>", "materialTweaks": {...}, "buildingMaterials": {...}, "blueprints": {...},
     * "craftingProps": {"<name>": {"key": <name or was>, "item": <GUID of the placing item>, "name": <text>}}},
     * the same blocks as a JSON file. Validates it. A block the boot merge already has unchanged is "registered"; any
     * other is kept for the next start ("pending"), as the game reads these tables once. report: JSON, with "errors" by
     * field and per block "status" and "id". KILN_ERR_INVALID_ARGUMENT: a block is wrong. */
    KilnResult(KILN_CALL* define_json)(KilnStr json, char* report, size_t capacity, size_t* out_len);

    /* A mod's code ran: blocks kept from code for names other than these (a JSON array) are dropped from the next start. */
    KilnResult(KILN_CALL* prune_pending)(KilnStr mod, KilnStr defined_names_json);

    /* JSON: state, hash, ids, tweaks, what was refused and why. */
    KilnResult(KILN_CALL* status_json)(char* json, size_t capacity, size_t* out_len);

    /* Stops keeping an absent mod's material in every world: from the next start, a world's cells of it are lost when it
     * loads. KILN_ERR_NOT_FOUND: no world keeps it. */
    KilnResult(KILN_CALL* forget)(const uint8_t guid[16]);

    /* The tweaks of game materials whose material resolved, in load order (a later one wins a field both set). 0 before the merge and when off.
     * Added v1 (appended); check header.size before calling. */
    uint32_t(KILN_CALL* tweak_count)(void);
    KilnResult(KILN_CALL* tweak_at)(uint32_t index, KilnWtTweak* out);
    /* A game material's cell id from what a mod writes: a GameMaterials name, "Cell<id>", or the material's GUID. -1: not a game material, or before the
     * merge. On the game's thread or a writer's. Added v1 (appended). */
    int32_t(KILN_CALL* game_material_id)(KilnStr text);

    /* The mods' crafting props in the game's table, in the order of their places: 0 before the merge and when off. A recipe's required prop is
     * keen::RecipeInfo.requiredProps (the id) and a bit of requiredPropsMask (the index). Added v1 (appended); check header.size before calling. */
    uint32_t(KILN_CALL* crafting_prop_count)(void);
    KilnResult(KILN_CALL* crafting_prop_at)(uint32_t index, KilnWtCraftingProp* out);
    /* The place (bit) of a crafting prop's GUID, or -1: not in the table. Added v1 (appended). */
    int32_t(KILN_CALL* crafting_prop_index)(const uint8_t guid[16]);
} KilnWorldTablesApi;

#ifdef __cplusplus
}
#endif
#endif
