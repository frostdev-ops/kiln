/* SPDX-License-Identifier: MIT
 * kiln.registry.view v1 (since 0.2.0-dev.32): one read-only view over every registry and every other resource type the
 * game has, under Kiln names. kiln.registry v1 and kiln.registry.live v1 are unchanged; this is a separate table.
 *
 * Views (docs/kiln/modding/registries/views.md):
 *  - stable views are the registries (items, recipes, loot, drops, balance and every later one, from Kiln's registry
 *    schemas): their fields read through the registry's resolved view (kiln.registry read with KILN_REGISTRY_VIEW_RESOLVED),
 *    mapped and proven per game build as the registry is; they are written through kiln.registry / kiln.registry.live only.
 *  - advanced views are every other resource type of the running build (quests, NPC dialogs, materials, UI data, ...): Kiln
 *    names derived from the game's reflection on the first build Kiln maps ("npcDialog", "rewardItems[].count") and kept by
 *    Kiln's alias table when a game update renames a type or a member. Tier Advanced (D5): verified per game build (a field
 *    is readable when its member resolves on this build's reflection), not lab-proven; read-only in v1. A field a registry
 *    covers names that registry field ("coveredBy"), the stable way to the same data.
 * Ids are Kiln ids ("game:<stable name>", the debug name in lower case with the registries' clash suffixes; an entry without
 * a name "game:<view in lower case>_<first 8 hex digits of its GUID>"). Fields are Kiln paths ("xpReward", "rewardItems",
 * "rewardItems[2].count"; a list's element by index). No member of this table takes or gives a game type, member, offset or
 * address.
 *
 * Values (JSON): numbers, true/false, enum and flag values by Kiln name, ids (another view's entry when one has the GUID, else
 * the GUID text; null for none), text, lists (a list of structs: objects with flat dotted Kiln keys).
 *
 * Conventions: strings in are UTF-8 KilnStr (ptr may be NULL only when len is 0), at most 1024 bytes (a selector 4096); text
 * out is copied into buf, NUL-terminated, cut at cap, *out_len (may be NULL) = the whole length without the NUL; buf may be
 * NULL only when cap is 0. KILN_OK: buf holds the JSON below; KILN_ERR_BUFFER_TOO_SMALL: call again with cap >= *out_len + 1.
 * A refusal returns its code AND copies the reason (plain text, Kiln words) into buf the same way:
 *  - KILN_ERR_UNAVAILABLE: not now or not here (before the registry stage ran, when the game's resources are resident; a field
 *    a kind of member the view cannot read; a field this entry has no value of; a stable field not mapped on this build);
 *  - KILN_ERR_NOT_FOUND: no such view, entry or field on this game build;
 *  - KILN_ERR_INVALID_ARGUMENT: a string over its limit or not UTF-8 sized, an empty selector, a malformed index.
 * Refusals are per entry and per field: one entry without a member never hides the others (entry_json lists both). kiln.log
 * gets each distinct refusal once.
 *
 * Threads: any thread. Advanced reads decode the resident resource bytes under kiln.content's store lock, so they never see
 * a half-written value and wait for a pack application in progress; keep them off hot paths (a read walks the reflection).
 * The catalog is built once per process at the first call after the stage; an advanced view's entries are listed when first
 * needed and again when the number of resources of its type changes.
 */
#ifndef KILN_REGISTRY_VIEW_H
#define KILN_REGISTRY_VIEW_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_REGISTRY_VIEW_SERVICE "kiln.registry.view"
#define KILN_REGISTRY_VIEW_VERSION 1u

typedef struct KilnRegistryViewApi {
    KilnServiceHeader header; /* { sizeof(KilnRegistryViewApi), KILN_REGISTRY_VIEW_VERSION } */
    /* Every view: {"format": 1, "views": [{"name", "tier": "stable" | "advanced", "fields": n, "registry"?, "entries"?
     * (advanced, once listed), "truncated"?}], "advanced"?: why there are no advanced views here}. */
    KilnResult(KILN_CALL* views_json)(char* buf, size_t cap, size_t* out_len);
    /* One view's fields: {"view", "tier", "fields": [{"path", "type": "int" | "float" | "bool" | "enum" | "flags" | "time" |
     * "id" | "text" | "list" | "struct" | "unread", "element"?, "values"?, "ref"?: the view its ids name, "readOnly",
     * "state": "readable" | "unread" | "removed" (advanced) or "mapped" | "unmapped" | "unproven" | "removed" (stable),
     * "reason"?, "coveredBy"?: "<registry>.<field>", "exact"?: one to one}], "truncated"?}. */
    KilnResult(KILN_CALL* fields_json)(KilnStr view, char* buf, size_t cap, size_t* out_len);
    /* One field of one entry, as JSON. */
    KilnResult(KILN_CALL* read)(KilnStr view, KilnStr id, KilnStr field, char* buf, size_t cap, size_t* out_len);
    /* The ids a selector names, as a JSON array in the view's order: an id, a case-insensitive glob over ids ("game:quest_*",
     * "*"), or an entry's debug name. */
    KilnResult(KILN_CALL* select)(KilnStr view, KilnStr selector, char* buf, size_t cap, size_t* out_len);
    /* Every field of one entry: {"view", "id", "tier", "fields": {"<path>": value}, "refused": {"<path>": "<reason>"}}
     * (a list's element fields come inside the list's value). */
    KilnResult(KILN_CALL* entry_json)(KilnStr view, KilnStr id, char* buf, size_t cap, size_t* out_len);
} KilnRegistryViewApi;

#ifdef __cplusplus
}
#define KILN_REG_VIEW_ASSERT static_assert
#else
#define KILN_REG_VIEW_ASSERT _Static_assert
#endif
KILN_REG_VIEW_ASSERT(sizeof(KilnRegistryViewApi) == 48 && offsetof(KilnRegistryViewApi, views_json) == 8 &&
    offsetof(KilnRegistryViewApi, fields_json) == 16 && offsetof(KilnRegistryViewApi, read) == 24 &&
    offsetof(KilnRegistryViewApi, select) == 32 && offsetof(KilnRegistryViewApi, entry_json) == 40, "KilnRegistryViewApi v1");
#undef KILN_REG_VIEW_ASSERT
#endif
