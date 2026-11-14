/* SPDX-License-Identifier: MIT
 * kiln.assets v1: mod-supplied resources and content blobs, and overrides of the game's own, at run time. No game file
 * on disk is modified: kiln.assets answers the game's resource service (key -> handle -> data) and its content
 * reader (content hash -> container bytes) from memory. Evidence: docs/re/resources.md; guide:
 * docs/kiln/services/assets.md.
 *
 * Words:
 *  - resource: a reflected struct the game keeps resident (all of enshrouded.kfc_resources is loaded at start), keyed
 *    by KilnAssetKey {GUID, type hash, part}. Examples: keen::ItemInfo, keen::UiTextureResource,
 *    keen::RenderTextureResource, keen::ecs::TemplateResource, keen::LocaTagCollectionResource.
 *  - content blob: raw bytes streamed from enshrouded_NNN.dat on demand (texture pixels, meshes, loca tables), keyed by
 *    KilnContentHash {size, hash0, hash1, hash2}. Resources point at blobs (keen::ResourceContentReference).
 *  - GUID bytes are in the game's order, which is .NET's Guid.ToByteArray() order (the bytes of ItemInfo.objectId).
 *  - type = FNV-1a-32 of the qualified type name: keen::UiTextureResource 0xf9e86997, keen::ItemInfo 0xb5ce8765.
 *
 * Sides: icons and textures are client-only (the dedicated server never draws). Resources and resource overrides work
 * on both sides (templates and item data are loaded by the server too). Content blobs are read by both, but only the
 * client streams textures and meshes.
 *
 * ---------------------------------------------------------------------------------------------------------------
 * Part 1, item icons (stable; kiln.content's UI texture hook uses it):
 *  The game's UI texture cache is keyed by {item GUID, keen::UiTextureResource, 0}; a key it does not know is fatal
 *  ("corrupted game ui texture"). It fills that key set once from the resource service's list of every
 *  UiTextureResource. kiln.assets adds a registered icon's key to that list (and makes the cache refill when an icon
 *  is registered later), serves the icon's UiTextureResource (RGBA8, 1 mip, the PNG's size), and streams its pixels
 *  from memory. icon_state() says when the cache knows the key: a caller that aliases unknown GUIDs to another item's
 *  icon (kiln.content does, for new items) must pass the key unchanged when icon_state(guid) == KILN_ICON_SERVED, and
 *  keep aliasing otherwise (PENDING, FAILED, NONE): the cache would stop the game on a key it lacks.
 *
 *  Integration (kiln.content, client, in its UiTextureGet hook before the alias lookup):
 *      static const KilnAssetsApi* A;   // on_ready: A = api->service((KilnStr){"kiln.assets", 11}, 1);
 *      if (A && A->icon_state(guid) == KILN_ICON_SERVED) return original(cache, key, flag);
 *  Registering from a definition (any thread, e.g. when an item definition names an icon file):
 *      A->icon_register_file(owner, def_guid, (KilnStr){path, len});
 *
 *  Icons in a mod's assets/assets.json (kind "icon") are registered natively at boot, before the game builds its UI:
 *  they are SERVED from the first frame. Icons registered later (managed mods load at the first world) become SERVED
 *  within a few UI frames, once the cache has refilled (the cache has room for 128 keys beyond the game's own).
 * ---------------------------------------------------------------------------------------------------------------
 * Part 2, resources, blobs, textures, dumps (v1 as published with the asset-layer report).
 *
 * Ownership: everything registered belongs to an owner; revert_owner(owner) (also called when kiln.assets sees the
 * owner revoked) takes it off. A resource or blob the game already loaded keeps its loaded copy until the game
 * loads it again (UI textures: the next start; streamed textures and meshes: when the streamer evicts them), so
 * overrides belong in assets.json (applied at boot) or in OnLoad.
 *
 * Threads: every function may be called from any thread unless noted. Data passed in is copied.
 * Fail soft: on a build where a piece is missing, the functions that need it return KILN_ERR_UNAVAILABLE.
 */
#ifndef KILN_ASSETS_H
#define KILN_ASSETS_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_ASSETS_SERVICE "kiln.assets"
#define KILN_ASSETS_VERSION 1u

#define KILN_ASSET_TYPE_UI_TEXTURE 0xf9e86997u     /* keen::UiTextureResource */
#define KILN_ASSET_TYPE_RENDER_TEXTURE 0x0u        /* filled in by the asset-layer report (keen::RenderTextureResource) */
#define KILN_ASSET_TYPE_ITEM_INFO 0xb5ce8765u      /* keen::ItemInfo */

typedef struct KilnAssetKey {
    uint8_t guid[16]; /* game byte order (.NET Guid.ToByteArray()) */
    uint32_t type;    /* FNV-1a-32 of the qualified type name */
    uint32_t part;    /* part index, usually 0 */
} KilnAssetKey;

typedef struct KilnContentHash {
    uint32_t size; /* byte size of the blob */
    uint32_t hash0, hash1, hash2;
} KilnContentHash;

/* icon_state() */
enum {
    KILN_ICON_NONE = 0,    /* no icon registered for this GUID (or this is a dedicated server) */
    KILN_ICON_PENDING = 1, /* registered, the UI texture cache does not know the key yet: keep aliasing */
    KILN_ICON_SERVED = 2,  /* the cache knows the key and kiln.assets serves it: look it up unchanged */
    KILN_ICON_FAILED = 3,  /* registered but unusable (bad PNG, client parts off): keep aliasing */
};

/* resource_put() flags */
enum {
    KILN_ASSET_PUT_NEW = 1,      /* the key must not exist in the game (a new resource) */
    KILN_ASSET_PUT_OVERRIDE = 2, /* the key must exist in the game (replace its bytes) */
    KILN_ASSET_PUT_PIN = 4,      /* pin the key for this process (below); combines with either of the above */
    /* neither: either */
};

/* KILN_ASSET_PUT_PIN (appended in dev.26; an older kiln.assets ignores the bit and puts as usual).
 *  Who: native services only, for a resource the process must not see change once a service has built it. Today one
 *   caller uses it: kiln.playeranim, for its merged player graph. Mods never pass it: no managed call passes it and
 *   assets.json has no mode for it. kiln.assets cannot tell callers apart; the first pin of a key holds.
 *  What a pin does: the put goes through as usual, then the key is pinned until the process ends. Its item is the only
 *   one of that key and the one served from then on. A pinned key takes no further resource_put from anyone, the
 *   pinning owner included (KILN_ERR_UNAVAILABLE, nothing added, a warning in kiln.log naming the key), no
 *   resource_remove from anyone (KILN_ERR_UNAVAILABLE, the bytes stay served), and no icon_register / icon_remove of
 *   it (UI texture keys). revert_owner, and the revoke of the pinning owner, take that owner's other resources off and
 *   leave the pinned one served. A pin is refused (KILN_ERR_UNAVAILABLE, nothing added) while another owner has an
 *   item of the key: the later item of a key is the one served, so the pin could not hold.
 *  The player graph is reserved at all times: the key {GUID 21319e83-5087-4fff-8795-22997843649e,
 *   keen::anim_graph::runtime_graph::AnimationGraphResource2_0 (0xa345050e), part 0} takes only a put that carries
 *   KILN_ASSET_PUT_PIN. Every other put of it (a code call, an assets.json "resource" entry, a field override through
 *   Kiln.Api's reflected resources) is refused with KILN_ERR_UNAVAILABLE and a warning naming the key, from process
 *   start: before kiln.playeranim has merged, and also when kiln.playeranim is off or has no states to merge (then the
 *   game's own graph is served). Overriding a running player's graph ends the server; mods add player animation
 *   states in assets/player-states.json and kiln.playeranim merges them into the graph once. */

/* Where a content blob registration came from (since 0.2.0-dev.28): the provenance kiln.assets keeps for each one, and
 * the bits of KilnBlobCatalogEntry.origins. Descriptive, not a security claim: a caller of blob_add_ex/override_ex
 * says what it is. GAME is never a registration: it means "in the game's own content index". */
enum {
    KILN_BLOB_ORIGIN_GAME = 1,       /* the game's own content index lists the hash */
    KILN_BLOB_ORIGIN_MANIFEST = 2,   /* a mod's assets/assets.json (load_manifest, the boot path; icons and textures of it too) */
    KILN_BLOB_ORIGIN_MANAGED = 4,    /* Kiln's managed Assets (AddBlob, OverrideBlob) through blob_add_ex/override_ex */
    KILN_BLOB_ORIGIN_NATIVE_API = 8, /* any other caller: blob_add/blob_override, icons and textures registered by call */
};

/* ---- the content blob catalog (since 0.2.0-dev.28; capability assets.blob_catalog) ----
 * An immutable, paged snapshot of every content blob the game serves: the game's own content index and every live
 * registration (assets.json, managed, native), one entry per distinct hash, with the origins that contribute it and the
 * registration that is served now. Kiln-owned layouts, not game ones.
 *
 * blob_catalog_open(owner, mode, handle, info): builds (or shares) a snapshot of the catalog as it is now.
 *   mode KILN_BLOB_CATALOG_REQUIRE_COMPLETE: the game's index and the registrations; refused (status NOT_READY, no
 *     handle) while the game's index cannot be read yet. KILN_BLOB_CATALOG_REGISTRATIONS_ONLY: the registrations only,
 *     status INCOMPLETE with exactly the REGISTRATIONS_COMPLETE flag (it is not "all game contents").
 *   info->size must be >= 32 (set it to sizeof(KilnBlobCatalogInfo)); it is filled on every answer but a bad argument.
 *   Only status COMPLETE or INCOMPLETE returns a handle (KILN_OK). Otherwise *handle = 0 and: NOT_READY and UNAVAILABLE
 *   return KILN_ERR_UNAVAILABLE, LIMIT_EXCEEDED returns KILN_ERR_BUFFER_TOO_SMALL (never a truncated snapshot).
 *   KILN_ERR_INVALID_ARGUMENT: owner 0, an unknown mode, a NULL handle or info, info->size < 32.
 *   Budgets: 1,000,000 entries and 64 MiB per snapshot, 128 MiB for every distinct snapshot handles hold, 64 handles.
 *   A snapshot never changes; its generation is the catalog's when it was built. Handles that open at the same
 *   generation and mode share one. owner's handles close when kiln.assets reverts the owner (revoked or revert_owner).
 * blob_catalog_read(handle, first, out, entry_size, cap, written): copies entries first.. (at most cap of them, each
 *   entry_size >= 48 bytes apart; bytes past 48 are zeroed) and sets *written. first == total: 0 entries, KILN_OK;
 *   first > total, entry_size < 48, written NULL, out NULL with cap > 0: KILN_ERR_INVALID_ARGUMENT; an unknown or
 *   closed handle: KILN_ERR_NOT_FOUND. A copy in flight keeps the snapshot alive past a close.
 * blob_catalog_close(handle): KILN_ERR_NOT_FOUND when unknown or closed already.
 * Entries are sorted by (size, hash0, hash1, hash2), unsigned. Any thread. The registrations are copied under the store's
 * lock (no game call, no IO, no callback while it is held); the game's index is copied once per build through guarded
 * reads and never borrowed; builds run one at a time. The bytes of a blob: blob_read. */
enum { KILN_BLOB_CATALOG_REQUIRE_COMPLETE = 0, KILN_BLOB_CATALOG_REGISTRATIONS_ONLY = 1 };
/* KilnBlobCatalogInfo.status */
enum {
    KILN_BLOB_CATALOG_COMPLETE = 0,       /* the game's index and every registration */
    KILN_BLOB_CATALOG_INCOMPLETE = 1,     /* registrations only (the mode asked for it) */
    KILN_BLOB_CATALOG_NOT_READY = 2,      /* the game's index cannot be read yet, or was unstable while copied */
    KILN_BLOB_CATALOG_LIMIT_EXCEEDED = 3, /* a budget would be exceeded; nothing is opened */
    KILN_BLOB_CATALOG_UNAVAILABLE = 4,    /* the content hooks are off on this build, or the index layout is unsupported */
};
/* KilnBlobCatalogInfo.flags */
enum { KILN_BLOB_CATALOG_GAME_COMPLETE = 1, KILN_BLOB_CATALOG_REGISTRATIONS_COMPLETE = 2 };
/* KilnBlobCatalogInfo.reason */
enum {
    KILN_BLOB_CATALOG_REASON_NONE = 0,
    KILN_BLOB_CATALOG_REASON_GAME_NOT_READY = 1,    /* the game has not opened its content index yet */
    KILN_BLOB_CATALOG_REASON_UNREADABLE_MAP = 2,    /* a read of it faulted, or it changed while copied */
    KILN_BLOB_CATALOG_REASON_ENTRY_BUDGET = 3,      /* more than 1,000,000 entries */
    KILN_BLOB_CATALOG_REASON_MEMORY_BUDGET = 4,     /* over 64 MiB for it, 128 MiB for all held, or 64 handles open */
    KILN_BLOB_CATALOG_REASON_UNSUPPORTED_TABLE = 5, /* content hooks off, or the index is not laid out as kiln.assets reads it */
};
/* KilnBlobCatalogEntry.flags */
enum { KILN_BLOB_CATALOG_OVERRIDDEN = 1 }; /* the served bytes are a registration's in place of a game blob */

typedef struct KilnBlobCatalogInfo { /* 32 bytes, align 8 */
    uint32_t size;       /* the caller's sizeof(KilnBlobCatalogInfo), >= 32 */
    uint32_t status;     /* KILN_BLOB_CATALOG_COMPLETE ... */
    uint64_t generation; /* the catalog's generation (moves on every registration, removal, owner revert, readiness) */
    uint64_t total;      /* entries in the snapshot; 0 without one */
    uint32_t flags;      /* KILN_BLOB_CATALOG_GAME_COMPLETE | ..._REGISTRATIONS_COMPLETE */
    uint32_t reason;     /* KILN_BLOB_CATALOG_REASON_* */
} KilnBlobCatalogInfo;

typedef struct KilnBlobCatalogEntry { /* 48 bytes, align 8 */
    uint32_t size;               /* 48: the bytes of the entry kiln.assets wrote */
    uint32_t flags;              /* KILN_BLOB_CATALOG_OVERRIDDEN */
    KilnContentHash hash;
    uint32_t origins;            /* KILN_BLOB_ORIGIN_* bits: GAME when the game's index lists it, plus every live registration's */
    uint32_t selected_origin;    /* exactly one bit: whose bytes are served (GAME: the game's own) */
    KilnOwner selected_owner;    /* the served registration's owner; 0 for GAME */
    uint64_t selected_sequence;  /* its sequence; 0 for GAME */
} KilnBlobCatalogEntry;

typedef struct KilnAssetsApi {
    KilnServiceHeader header; /* { sizeof(KilnAssetsApi), KILN_ASSETS_VERSION } */

    /* ---- part 1: item icons (client) ----
     * icon_register_png: `png` = the bytes of a PNG file (8-bit RGBA/RGB/grey, any size up to 1024x1024; drawn as
     *   the item's icon at the size the UI uses, so a square image works best, 256x256 like the game's). Replaces
     *   this owner's earlier icon for that GUID. KILN_ERR_INVALID_ARGUMENT: not a PNG / too large;
     *   KILN_ERR_UNAVAILABLE: dedicated server or the client parts are off on this build.
     * icon_register_file: the same from a file (UTF-8 path, absolute or relative to <game>/kiln).
     * icon_remove: takes this owner's icon off (the key stays in the cache for the session and shows the game's
     *   fallback; kiln.content's alias applies again).
     * icon_state: KILN_ICON_*; lock-free, any thread, cheap enough for a per-lookup hook. */
    KilnResult(KILN_CALL* icon_register_png)(KilnOwner owner, const uint8_t guid[16], const void* png, size_t len);
    KilnResult(KILN_CALL* icon_register_file)(KilnOwner owner, const uint8_t guid[16], KilnStr path);
    KilnResult(KILN_CALL* icon_remove)(KilnOwner owner, const uint8_t guid[16]);
    int32_t(KILN_CALL* icon_state)(const uint8_t guid[16]);

    /* ---- part 2: resources ----
     * resource_put: serve `data` (copied) for `key`: a new resource, or the replacement of a game resource (flags).
     *   The bytes are the resource exactly as the game keeps it (its reflected struct with its BlobArray/BlobString
     *   data behind it; self-relative offsets inside the copy). New resources of a type are added to the game's
     *   list of that type. KILN_ERR_UNAVAILABLE: resources are off on this build, or the key is pinned or is the
     *   reserved player graph (KILN_ASSET_PUT_PIN above).
     * resource_remove: takes this owner's resource / override off. KILN_ERR_UNAVAILABLE: the key is pinned (nothing
     *   is taken off, from any owner).
     * resource_read: the bytes the game gets for `key` now (an override if any, else the game's own). out_len is set
     *   even when the buffer is too small (KILN_ERR_BUFFER_TOO_SMALL). KILN_ERR_NOT_FOUND: no such resource.
     * resource_list: every key of a type (the game's plus new ones), out_count set even when cap is too small. */
    KilnResult(KILN_CALL* resource_put)(KilnOwner owner, const KilnAssetKey* key, const void* data, size_t len,
                                        uint32_t flags);
    KilnResult(KILN_CALL* resource_remove)(KilnOwner owner, const KilnAssetKey* key);
    KilnResult(KILN_CALL* resource_read)(const KilnAssetKey* key, void* buffer, size_t capacity, size_t* out_len);
    KilnResult(KILN_CALL* resource_list)(uint32_t type, KilnAssetKey* out, size_t capacity, size_t* out_count);

    /* ---- part 2: content blobs ----
     * blob_add: a new blob; out_hash = the hash to put into a resource's ResourceContentReference (derived from
     *   the bytes; never collides with a game blob).
     * blob_override: serve `data` instead of the game's blob `hash` (len must equal hash->size).
     * blob_remove: takes this owner's blob / override off.
     * blob_read: the bytes of a blob (an override, a mod blob, or the game's own from its container file). */
    KilnResult(KILN_CALL* blob_add)(KilnOwner owner, const void* data, size_t len, KilnContentHash* out_hash);
    KilnResult(KILN_CALL* blob_override)(KilnOwner owner, const KilnContentHash* hash, const void* data, size_t len);
    KilnResult(KILN_CALL* blob_remove)(KilnOwner owner, const KilnContentHash* hash);
    KilnResult(KILN_CALL* blob_read)(const KilnContentHash* hash, void* buffer, size_t capacity, size_t* out_len);

    /* ---- part 2: textures (client) ----
     * texture_override_png: replace the pixels of the texture resource `key` (keen::UiTextureResource or
     *   keen::RenderTextureResource) with a PNG: the image is resized to the texture's size, its mip chain is
     *   rebuilt and it is encoded to the texture's own pixel format (RGBA8, BC1, BC3, BC4, BC5, BC7), then served as
     *   an override of the texture's content blob. */
    KilnResult(KILN_CALL* texture_override_png)(KilnOwner owner, const KilnAssetKey* key, const void* png, size_t len);

    /* ---- part 2: tools ----
     * dump: write resources / blobs to <game>/kiln/data/assets/dump/. spec: "<guid>[:<type>[:<part>]]",
     *   "type:<hash or qualified name>", "blob:<size>-<h0>-<h1>-<h2>" (hex), or "list". report: JSON summary.
     * status: JSON (hooks, counts, owners, problems).
     * load_manifest: registers the entries of <mod_dir>/assets/assets.json for `owner` (the native boot path does
     *   this for every mod in the load order; managed mods may call it for a folder of their own). */
    KilnResult(KILN_CALL* dump)(KilnStr spec, char* report, size_t capacity, size_t* out_len);
    KilnResult(KILN_CALL* status)(char* json, size_t capacity, size_t* out_len);
    void(KILN_CALL* revert_owner)(KilnOwner owner);
    KilnResult(KILN_CALL* load_manifest)(KilnOwner owner, KilnStr mod_dir, char* report, size_t capacity,
                                         size_t* out_len);

    /* resource_data: a zero-copy, read-only view of a resource's live loaded bytes, as the game sees them (a Kiln
     * override / new resource if one is registered, else the game's own resident copy). Sets *ptr and *len; the
     * pointer is valid until the next tick (resident resources outlive that; game-streamed ones may move). Do not
     * write through it. KILN_ERR_NOT_FOUND when the key is not loaded. Any thread; game memory, so the tick thread or
     * a world thread for anything the game may be paging. Added v1 (appended); check header.size before calling. */
    KilnResult(KILN_CALL* resource_data)(const KilnAssetKey* key, const void** ptr, size_t* len);

    /* resource_is_new: 1 when `key` was served in this process as a resource the game itself does not have, so a client
     * without Kiln cannot know it (it stays 1 after resource_remove: a registered template outlives its resource); 0
     * otherwise (a game resource, an override, nothing). resource_new_count: how many such keys `type` has. Lock-free,
     * any thread. Added v1 (appended); check header.size before calling. */
    int32_t(KILN_CALL* resource_is_new)(const KilnAssetKey* key);
    size_t(KILN_CALL* resource_new_count)(uint32_t type);

    /* when_resources_ready: calls fn(user) once the game's resource service is up, on the game's thread and before the
     * game has read anything through it: the moment to read a game resource and put an edited copy. At once, on this
     * thread, when it's already up. For a service that edits tables other mods share; a mod's own override belongs in
     * assets.json. KILN_ERR_UNAVAILABLE: resources are off on this build. Added v1 (appended); check header.size. */
    KilnResult(KILN_CALL* when_resources_ready)(void(KILN_CALL* fn)(void* user), void* user);

    /* ---- since 0.2.0-dev.28 (check header.size before each) ----
     * blob_add_ex / blob_override_ex: blob_add / blob_override with the registration's origin: KILN_BLOB_ORIGIN_MANAGED
     *   or KILN_BLOB_ORIGIN_NATIVE_API (anything else, MANIFEST included, is KILN_ERR_INVALID_ARGUMENT). blob_add and
     *   blob_override tag NATIVE_API; load_manifest tags MANIFEST. Which bytes are served is decided exactly as before
     *   (the latest registration of a hash wins; an owner's second registration of a hash replaces its first in place). */
    KilnResult(KILN_CALL* blob_add_ex)(KilnOwner owner, const void* data, size_t len, KilnContentHash* out_hash,
                                       uint32_t api_origin);
    KilnResult(KILN_CALL* blob_override_ex)(KilnOwner owner, const KilnContentHash* hash, const void* data, size_t len,
                                            uint32_t api_origin);
    /* The content blob catalog (above, before KilnAssetsApi). */
    KilnResult(KILN_CALL* blob_catalog_open)(KilnOwner owner, uint32_t mode, KilnHandle* handle, KilnBlobCatalogInfo* info);
    KilnResult(KILN_CALL* blob_catalog_read)(KilnHandle handle, uint64_t first, KilnBlobCatalogEntry* out,
                                             uint32_t entry_size, size_t cap, size_t* written);
    KilnResult(KILN_CALL* blob_catalog_close)(KilnHandle handle);
} KilnAssetsApi;

#ifdef __cplusplus
}
#endif
#endif
