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
    /* neither: either */
};

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
     *   list of that type.
     * resource_remove: takes this owner's resource / override off.
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
} KilnAssetsApi;

#ifdef __cplusplus
}
#endif
#endif
