/* SPDX-License-Identifier: MIT
 * kiln.build v1: change the world the way the game itself saves and replicates it, so vanilla clients see the result.
 *   1. Voxels: read cells (terrain and building blocks are the same 0.5 m voxel grid), write exact cells through the
 *      engine's own block writer and store commit (the sink every building and terraforming kernel of the game ends
 *      in: saved with the world, replicated to every client), and shape edits built on that (dig, add a material,
 *      flatten, smooth, paint, replace in a box, sphere or cylinder).
 *   2. Props: place entities with the components the game's own building helper gives a placed prop (CurrentTransform,
 *      DisableRandomLootOnDestroy, UsedItem = the item dismantling gives back), list and remove them.
 * Schematics (.kschem), the paste journal, PlaceOnce and the commands live in the managed layer (Kiln.Build).
 * Evidence and engine notes: docs/re/build.md. Usage: docs/kiln/services/build.md.
 *
 * Threads: every function that touches the world runs on the tick thread (Pre/Post or a posted task) and returns
 * KILN_ERR_WRONG_THREAD elsewhere; status and the cell math helpers are thread-safe. Managed mods always call from
 * the tick thread.
 *
 * Cells. A KilnVoxel is the engine's own u16: low byte = material (0 = air), high byte = fill 0..255 (255 = solid;
 * partial values place the surface inside the cell). Arrays of cells are x fastest, then y, then z:
 * index = (z * dims[1] + y) * dims[0] + x. Cell (i, j, k) covers world metres
 * [origin + (i, j, k) * cell_size, origin + (i + 1, j + 1, k + 1) * cell_size) (KilnBuildGrid). The engine commits
 * whole 8x8x8 blocks: a write of any box is widened to whole blocks by reading the cells around it first (they are
 * written back unchanged).
 *
 * Persistence. The game itself saves voxel edits only inside player-base build zones (its save writes one terrain blob
 * per base zone; everything else regrows at the next restart). Kiln keeps the chunks its writes touch in its own save
 * regions, which it appends to the game's region list, so the game's save writes them too (see keep/release).
 *
 * Ownership. Voxels have no owner in the engine. Every Kiln edit names an actor (the player it acts for, 0 = Kiln
 * itself) and an origin (mod id) for events and journals; neither is written into the world. Kiln's edits never pass
 * the game's building programs, so kiln.gameplay's protection zones do not apply to them unless the caller sets
 * KILN_BUILD_F_RESPECT_ZONES (then the actor is checked like a player building there). To protect player bases from
 * mistakes, an edit that touches a player base's build zone (the game's BuildZoneSystem base list) is refused unless
 * KILN_BUILD_F_IN_BASES is set, and props
 * that would found a base (templates with BaseIdComponent: flame altars) are refused unless KILN_BUILD_F_ALLOW_ALTARS.
 * The table only grows at the end; check header.size before using a field added later. */
#ifndef KILN_BUILD_H
#define KILN_BUILD_H
#include "../kiln_api.h"
#include "kiln_spawn.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_BUILD_SERVICE "kiln.build"
#define KILN_BUILD_VERSION 1u

typedef uint16_t KilnVoxel;
#define KILN_VOXEL_MATERIAL(v) ((uint8_t)((v) & 0xFFu))
#define KILN_VOXEL_FILL(v) ((uint8_t)(((v) >> 8) & 0xFFu))
#define KILN_VOXEL_MAKE(material, fill) ((KilnVoxel)((((unsigned)(fill) & 0xFFu) << 8) | ((unsigned)(material) & 0xFFu)))
#define KILN_VOXEL_AIR ((KilnVoxel)0)

/* Write / shape / prop flags */
enum {
    KILN_BUILD_F_RESPECT_ZONES = 1u << 0, /* refuse when a kiln.gameplay zone denies `actor` building/terraforming there */
    KILN_BUILD_F_IN_BASES = 1u << 1,      /* allow touching a flame altar's build zone (player bases) */
    KILN_BUILD_F_ALLOW_ALTARS = 1u << 2,  /* props: allow templates with BaseIdComponent (they found a base) */
    KILN_BUILD_F_UNLOADED = 1u << 3,      /* write even where the voxel tiles are not streamed in (not recommended) */
    KILN_BUILD_F_DRY_RUN = 1u << 4,       /* compute and report, write nothing */
    KILN_BUILD_F_TRANSIENT = 1u << 5,     /* do not keep the edit across restarts (the game's own rule outside bases) */
};

/* KilnBuildResult.flags */
enum {
    KILN_BUILD_R_CHANGED = 1u << 0,   /* at least one cell differed and was written */
    KILN_BUILD_R_VERIFIED = 1u << 1,  /* the cells read back equal to what was written */
    KILN_BUILD_R_ZONE = 1u << 2,      /* refused: a protection zone (zone = its id) */
    KILN_BUILD_R_BASE = 1u << 3,      /* refused: the box touches a player base (no KILN_BUILD_F_IN_BASES) */
    KILN_BUILD_R_NOT_LOADED = 1u << 4,/* refused: voxel tiles of the box are not streamed in */
    KILN_BUILD_R_DRY_RUN = 1u << 5,
    KILN_BUILD_R_KEPT = 1u << 6,      /* the touched chunks are in Kiln's save regions (the game saves them) */
};

typedef struct KilnBuildGrid {
    uint32_t size;          /* in: sizeof(KilnBuildGrid) */
    uint32_t level;         /* the engine's edit level (6 on svn 1285601) */
    double cell_size;       /* metres per cell edge (0.5) */
    KilnVec3 origin;        /* world position of cell (0, 0, 0)'s low corner */
    int32_t cells_min[3];   /* valid cells: [cells_min, cells_max) per axis (y has no engine bound; Kiln uses 0..8192) */
    int32_t cells_max[3];
    uint32_t tile_cells[2]; /* streaming tile size in cells (x, z) */
    uint32_t tiles[2];      /* tile counts (x, z) */
    uint32_t block;         /* commit granularity in cells (8) */
    uint32_t reserved;
} KilnBuildGrid;

typedef struct KilnBuildResult {
    uint32_t size;            /* in: sizeof(KilnBuildResult) */
    uint32_t flags;           /* KILN_BUILD_R_* */
    uint64_t cells_changed;   /* cells whose value changed */
    uint32_t blocks_written;  /* 8x8x8 blocks committed */
    uint32_t zone;            /* KILN_BUILD_R_ZONE: the denying zone */
    int32_t box_origin[3];    /* the cell box the edit covered (before widening to blocks) */
    uint32_t box_dims[3];
    double milliseconds;      /* time inside the engine's writer */
} KilnBuildResult;

typedef struct KilnBuildWrite {
    uint32_t size;          /* in: sizeof(KilnBuildWrite) */
    uint32_t flags;         /* KILN_BUILD_F_* */
    int32_t origin[3];      /* cell coordinates of the box's low corner */
    uint32_t dims[3];       /* box size in cells (each 1..4096; at most 2^22 cells) */
    const KilnVoxel* cells; /* dims[0]*dims[1]*dims[2] cells, x fastest */
    const uint8_t* mask;    /* optional, same layout: 0 = keep the world's cell there */
    KilnEntity actor;       /* who the edit is for (0 = Kiln); zone checks with RESPECT_ZONES */
    uint32_t reserved;
    KilnStr origin_mod;     /* mod id, for events */
} KilnBuildWrite;

/* Shape edits */
enum {
    KILN_BUILD_OP_DIG = 1,     /* remove: cells inside become air (fill falls off smoothly at the boundary) */
    KILN_BUILD_OP_ADD = 2,     /* add `material`: cells inside get fill 255, the boundary partial */
    KILN_BUILD_OP_FLATTEN = 3, /* inside the shape's x/z footprint: solid (material, or the column's own) up to
                                  level_y, air above; the shape's y extent limits how far it digs/fills */
    KILN_BUILD_OP_SMOOTH = 4,  /* blur the fill field inside (3x3x3, `strength` 0..1 per pass, `passes` 1..8) */
    KILN_BUILD_OP_PAINT = 5,   /* change the material of non-air cells inside, keep their fill */
    KILN_BUILD_OP_REPLACE = 6, /* cells of material `from_material` inside become `material` (fill kept) */
};
enum {
    KILN_BUILD_SHAPE_BOX = 1,      /* center + half extents */
    KILN_BUILD_SHAPE_SPHERE = 2,   /* center + radius (half.x) */
    KILN_BUILD_SHAPE_CYLINDER = 3, /* vertical: center + radius (half.x) + half height (half.y) */
};

typedef struct KilnBuildShape {
    uint32_t size;         /* in: sizeof(KilnBuildShape) */
    uint32_t flags;        /* KILN_BUILD_F_* */
    uint32_t op;           /* KILN_BUILD_OP_* */
    uint32_t shape;        /* KILN_BUILD_SHAPE_* */
    KilnVec3 center;       /* metres */
    KilnVec3 half;         /* metres: box half extents; sphere radius in x; cylinder radius in x, half height in y */
    double level_y;        /* FLATTEN: surface height (metres); NaN = center.y */
    float strength;        /* SMOOTH: 0..1 */
    uint32_t passes;       /* SMOOTH: 1..8 */
    uint8_t material;      /* ADD / PAINT / REPLACE / FLATTEN fill material (0 = FLATTEN keeps each column's) */
    uint8_t from_material; /* REPLACE */
    uint8_t reserved[2];
    KilnEntity actor;
    KilnStr origin_mod;
} KilnBuildShape;

/* Props */
typedef struct KilnBuildProp {
    uint32_t size;          /* in: sizeof(KilnBuildProp) */
    uint32_t flags;         /* KILN_BUILD_F_* */
    KilnGuid template_guid; /* used when not all zero */
    KilnStr template_name;  /* else: a template name (kiln.spawn lookup), or an item debug name whose placedEntity is
                               the template (e.g. "Prop_Storage_24_T1_Box" or the item that places it) */
    KilnVec3 position;      /* metres, exact */
    float rotation[4];      /* quaternion x, y, z, w (all zero = identity) */
    uint32_t item_id;       /* UsedItem: what dismantling gives back. 0 = the item that places the template, when
                               known; ~0u = none */
    KilnEntity actor;
    KilnStr origin_mod;
} KilnBuildProp;

typedef struct KilnBuildPropInfo {
    uint32_t size;          /* in: sizeof(KilnBuildPropInfo) */
    KilnEntity entity;
    KilnGuid template_guid;
    KilnVec3 position;
    float rotation[4];
    float scale[3];
    uint32_t item_id;       /* its UsedItem, 0 = none */
    uint32_t kind;          /* KILN_TEMPLATE_* (kiln.spawn's) */
    uint32_t flags;         /* KILN_BUILD_PROP_* */
    char name[128];         /* template name */
} KilnBuildPropInfo;

/* KilnBuildPropInfo.flags */
enum {
    KILN_BUILD_PROP_PLACED = 1u << 0,   /* has UsedItem: a building prop (placed by a player or by Kiln) */
    KILN_BUILD_PROP_ALTAR = 1u << 1,    /* has BaseIdComponent (flame altar) */
    KILN_BUILD_PROP_SAVED = 1u << 2,    /* its template is saved by the game (no DoNotSave) */
};

/* props_in_box filters */
enum {
    KILN_BUILD_PROPS_PLACED = 1u << 0,  /* only building props (UsedItem) */
    KILN_BUILD_PROPS_SAVED = 1u << 1,   /* only templates the game saves */
    KILN_BUILD_PROPS_ALL = 1u << 2,     /* every entity with a transform (creatures and players excluded) */
};

/* Player bases: the game's own base build zones (BuildZoneSystem, rebuilt by the server every step) */
typedef struct KilnBuildBase {
    uint32_t size;      /* in: sizeof(KilnBuildBase) */
    KilnEntity altar;   /* the flame altar that owns the zone */
    KilnVec3 center;    /* centre of the build zone (metres) */
    KilnVec3 half;      /* half its size */
    uint32_t abandoned;
    uint32_t reserved;
} KilnBuildBase;

typedef struct KilnBuildStatus {
    uint32_t size;          /* in: sizeof(KilnBuildStatus) */
    uint8_t read;           /* 1: voxel reads work on this build */
    uint8_t write;          /* 1: voxel writes work */
    uint8_t props;          /* 1: prop placement works */
    uint8_t world;          /* 1: the server world runs and the voxel world was found */
    uint64_t reads, writes, cells_read, cells_written, blocks_written, props_placed, props_removed, refused;
    char detail[160];       /* why something is off; NUL-terminated */
    uint8_t persist;        /* 1: Kiln's save regions are hooked into the game's save */
    uint8_t reserved[7];
    uint64_t kept_chunks;   /* 16 m chunks in Kiln's save regions */
    uint64_t saves_seen;    /* world saves whose region list Kiln extended */
    uint64_t boxes_added;   /* region boxes appended over all saves */
} KilnBuildStatus;

typedef struct KilnBuildApi {
    KilnServiceHeader header; /* { sizeof(KilnBuildApi), KILN_BUILD_VERSION } */

    /* any thread */
    KilnResult(KILN_CALL* status)(KilnBuildStatus* out);

    /* ---- voxels (tick) ---- */
    /* Grid geometry. UNAVAILABLE before the world runs. */
    KilnResult(KILN_CALL* grid)(KilnBuildGrid* out);
    /* KILN_OK when every streaming tile under the box is loaded. Otherwise asks the game for the missing tiles (a
     * dedicated server streams only around players and bases; asked tiles arrive within a few seconds) and returns
     * UNAVAILABLE: call again in a later step. write/shape refuse (KILN_BUILD_R_NOT_LOADED) and ask the same way. */
    KilnResult(KILN_CALL* ready)(const int32_t origin[3], const uint32_t dims[3]);
    /* Reads cells (capacity in cells). NOT_FOUND-free: cells outside the grid read as the engine returns them (below
     * y = 0 solid). UNAVAILABLE when a tile is not loaded (the cells are still filled). */
    KilnResult(KILN_CALL* read)(const int32_t origin[3], const uint32_t dims[3], KilnVoxel* out, size_t capacity);
    /* Writes cells through the engine's writer; *result reports what changed and whether it read back equal. */
    KilnResult(KILN_CALL* write)(const KilnBuildWrite* write, KilnBuildResult* result);
    /* The cell box a shape edit touches (for journals: read it before calling shape). */
    KilnResult(KILN_CALL* shape_box)(const KilnBuildShape* shape, int32_t origin[3], uint32_t dims[3]);
    /* Runs a shape edit (read box, compute, write). */
    KilnResult(KILN_CALL* shape)(const KilnBuildShape* shape, KilnBuildResult* result);

    /* ---- props (tick) ---- */
    KilnResult(KILN_CALL* prop_place)(const KilnBuildProp* prop, KilnEntity* out);
    /* Destroys a prop (any non-player entity that is not a creature or villager). */
    KilnResult(KILN_CALL* prop_remove)(KilnEntity entity);
    /* Entities whose position lies in [min, max] (metres), filtered (KILN_BUILD_PROPS_*), nearest to the box centre
     * first. *count = all matches; up to capacity written. */
    KilnResult(KILN_CALL* props_in_box)(KilnVec3 min, KilnVec3 max, uint32_t filter, KilnBuildPropInfo* out,
                                        size_t capacity, size_t* count);
    KilnResult(KILN_CALL* prop_info)(KilnEntity entity, KilnBuildPropInfo* out);

    /* ---- bases (tick) ---- */
    KilnResult(KILN_CALL* bases)(KilnBuildBase* out, size_t capacity, size_t* count);

    /* ---- items (thread-safe once the world ran) ---- */
    /* The voxel material an item places (building material items: "Stone Block" ...); 0 = none / unknown. */
    uint32_t(KILN_CALL* item_material)(uint32_t item_id);
    /* The template an item places (prop items); NOT_FOUND when it places none. */
    KilnResult(KILN_CALL* item_template)(uint32_t item_id, KilnGuid* out);

    /* ---- save regions (tick) ----
     * The game saves voxel edits only inside player-base build zones; everything else reverts at the next restart.
     * Kiln adds the 16 m chunks its own writes touch (unless KILN_BUILD_F_TRANSIENT) to the region list the game's save
     * writes, so they are saved like a base. keep() adds a box by hand (then the players' own digging there is kept
     * too); release() forgets the chunks inside a box (they revert at the next restart unless a base covers them). */
    KilnResult(KILN_CALL* keep)(const int32_t origin[3], const uint32_t dims[3]);
    KilnResult(KILN_CALL* release)(const int32_t origin[3], const uint32_t dims[3], uint64_t* released);
} KilnBuildApi;

#ifdef __cplusplus
}
#endif
#endif
