/* SPDX-License-Identifier: MIT
 * kiln.markers v1 (kiln-markers.dll): session-only markers by key on the game's map, Kiln's drawn compass strip and
 * world labels, set locally by a client mod or sent by a server mod to Kiln clients; and the internal
 * kiln.markers.bridge v1 the managed built-in receiver (Kiln.Markers) uses to carry them. The ABI root table is
 * unchanged. Contract: dossier chapter 04 (§4, §5); guide: docs/kiln/services/markers.md.
 *
 * Session only. Nothing here is saved: client records clear when the client world stops, received records when the
 * link or the server world changes, sent records when the server world stops. Restarting does not restore anything.
 *
 * Publishers. A publisher binds one stable namespace (1 to 64 bytes of [A-Za-z0-9_.:-]) to its owner with bind_owner;
 * a managed mod is bound to its mod id by Kiln.Markers. Keys follow the same grammar. A record is identified by
 * (source, publisher generation, namespace, key): a remote publisher with the same namespace cannot overwrite a local
 * record. release_owner is mandatory before a native publisher unloads (core owner_revoke does not notify this
 * service); Kiln.Markers calls it when a mod retires.
 *
 * Results (Kiln's one error rule). KILN_ERR_INVALID_ARGUMENT: a bad ABI size, identifier, UTF-8 text, coordinate,
 * icon or world kind (a KilnMarkerResult the call writes then holds the reason in words). KILN_ERR_WRONG_THREAD: a
 * synchronous capture or server operation on the wrong world thread. KILN_ERR_UNAVAILABLE: a capability is off (a
 * KilnMarkerResult the call writes then holds the capability's exact name as its reason, e.g. "markers.compass").
 * Every other answer is KILN_OK with result.status: ACCEPTED, or a refusal by the current game, store or peer state
 * (NOT_FOUND, FULL, NOT_READY, ...) with committed = 0 and the reason in words, unless a server store committed
 * before a delivery refusal (PARTIAL). Re-setting an identical value is ACCEPTED with affected = 0 and no event; a
 * clear of an empty scope likewise. Every caller sets the size of each struct it passes (input and output); a size
 * smaller than this header's is KILN_ERR_INVALID_ARGUMENT, a larger one is accepted.
 *
 * Limits (policy, not engine limits): 128 records per namespace and source, 512 local and 512 received records, 1024
 * on a client in all; at most 64 accepted changes of one publisher (256 of all) wait for the next client step's
 * dispatch, the next one is FULL; 64 distinct namespaces per process. Text is interned in a process-lifetime arena:
 * 4 MiB in all and 256 KiB of new text per namespace; the same text again costs nothing, and nothing is given back on
 * removal (FULL when exhausted). Nothing is ever evicted.
 *
 * World kinds are kiln_api.h's KILN_WORLD_* values: KILN_WORLD_SERVER (0), KILN_WORLD_CLIENT (2). KILN_WORLD_PREDICTION
 * (1) is refused (KILN_ERR_INVALID_ARGUMENT). (Chapter 04 §4.2 numbers client 1 and prediction 2; coordinator ruling 6
 * of the dev.28 wave keeps the tree's values.)
 *
 * Threads: catalog_count, catalog_at, catalog_resolve, world_epoch, release_anchor, the local mutations
 * (set_local, remove_local, clear_local), subscribe, unsubscribe, bind_owner and release_owner: any thread.
 * capture_anchor: the requested world's thread (between its steps). send, unsend, clear_sent, suppress_sent: the server
 * tick thread. Events: see subscribe.
 *
 * Capabilities (kiln.caps rows): "kiln.markers" (this table), "markers.local" (the local store; needs
 * world.lifecycle; client side), "markers.catalog" (the icon catalog; readiness is catalog_count's generation),
 * "markers.map" (guarded native map icons), "markers.compass" (Kiln's drawn compass strip), "markers.world" (drawn
 * world labels), "markers.nameplates" (a world label that follows an entity), "markers.anchors.client",
 * "markers.anchors.server", "markers.send" (server layers and the built-in receiver's link), "markers.map_text"
 * (the game's map tooltip shows a NATIVE_TOOLTIP marker's text: markers.map and the game build's tooltip check, sigdb
 * markers.map_tooltip_text; WRONG_SIDE on a dedicated server). A spec that asks for a surface whose row is off is
 * KILN_ERR_UNAVAILABLE naming that row; nothing is drawn partly. send takes NATIVE_TOOLTIP: each player whose client
 * cannot show it is refused for that spec (REQUIRES_CLIENT).
 */
#ifndef KILN_MARKERS_H
#define KILN_MARKERS_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_MARKERS_VERSION 1u
#define KILN_MARKERS_BRIDGE_VERSION 1u
#define KILN_MARKERS_SERVICE "kiln.markers"
#define KILN_MARKERS_BRIDGE_SERVICE "kiln.markers.bridge"

/* KilnMarkerSpec.surfaces and the masks of KilnMarkerVisibility (a nonzero mask of these bits only) */
enum { KILN_MARKER_MAP = 1, KILN_MARKER_COMPASS = 2, KILN_MARKER_WORLD = 4 };

/* KilnMarkerResult.status, KilnMarkerAnchorResult.status */
enum {
    KILN_MARKER_ACCEPTED = 0,          /* stored (also when identical: affected 0) */
    KILN_MARKER_NOT_FOUND = 1,         /* no record of that key, no such entity, no such icon */
    KILN_MARKER_FULL = 2,              /* a count, queue or text limit (header comment) would be exceeded */
    KILN_MARKER_REQUIRES_CLIENT = 3,   /* a recipient runs no Kiln (vanilla), has no marker channel, or cannot show what
                                          the spec asks for (the map, NATIVE_TOOLTIP) */
    KILN_MARKER_NOT_READY = 4,         /* no world of the needed kind, owner not ready, a peer's handshake pending */
    KILN_MARKER_PARTIAL = 5,           /* a server store committed; some recipients queued, some refused */
    KILN_MARKER_INVALID_ANCHOR = 6,    /* the follow handle is not a live lease of this owner and world */
    KILN_MARKER_CATALOG_NOT_READY = 7, /* the icon catalog is still warming up */
    KILN_MARKER_AMBIGUOUS = 8,         /* catalog_resolve: the alias names several icons; use the canonical key */
};

/* KilnMarkerChange.kind */
enum { KILN_MARKER_ADDED = 0, KILN_MARKER_UPDATED = 1, KILN_MARKER_REMOVED = 2, KILN_MARKER_CLEARED = 3 };

/* KilnMarkerChange.audience, KilnMarkerVisibility.audience: the record's source */
enum { KILN_MARKER_LOCAL = 0, KILN_MARKER_SERVER_BROADCAST = 1, KILN_MARKER_SERVER_TARGET = 2, KILN_MARKER_RECEIVED = 3 };

/* KilnMarkerChange.cause */
enum {
    KILN_MARKER_API = 0,           /* a call of this table */
    KILN_MARKER_OWNER_RETIRED = 1, /* release_owner */
    KILN_MARKER_WORLD_STOPPED = 2, /* the world of the records stopped */
    KILN_MARKER_PEER_LEFT = 3,     /* a recipient or the server link went */
    KILN_MARKER_SNAPSHOT = 4,      /* a received snapshot replaced the view */
    KILN_MARKER_LINK_STALE = 5,    /* the link's lease ran out */
};

/* KilnMarkerVisibility.reason[] per surface (MAP, COMPASS, WORLD) */
enum {
    KILN_MARKER_READY = 0,
    KILN_MARKER_MAP_CAPACITY = 1,
    KILN_MARKER_SURFACE_BUDGET = 2,
    KILN_MARKER_NO_CAMERA = 3,
    KILN_MARKER_BEHIND = 4,
    KILN_MARKER_OUT_OF_RANGE = 5,
    KILN_MARKER_FOLLOW_MISSING = 6,
    KILN_MARKER_ANCHOR_ENDED = 7,
    KILN_MARKER_NO_WORLD = 8,
    KILN_MARKER_CAPABILITY_OFF = 9,
    KILN_MARKER_SOURCE_STALE = 10,
    KILN_MARKER_BAD_POSITION = 11,
    KILN_MARKER_VIS_REMOVED = 12,
    KILN_MARKER_NOT_REQUESTED = 13,
};

/* KilnMarkerSpec.map_label_mode. NATIVE_TOOLTIP: the text is also the title of the game's map tooltip; it needs the MAP
 * surface and, for set_local, the markers.map_text row. */
enum { KILN_MARKER_LABEL_NONE = 0, KILN_MARKER_LABEL_NATIVE_TOOLTIP = 1 };

/* KilnMarkerIcon.flags (catalog metadata) */
enum {
    KILN_MARKER_ICON_IMAGE = 1,       /* the type has an icon image (every catalog row) */
    KILN_MARKER_ICON_SHOW_LABEL = 2,
    KILN_MARKER_ICON_SHOW_RANGE = 4,
    KILN_MARKER_ICON_ABOVE_FOG = 8,
    KILN_MARKER_ICON_CUSTOM = 16,     /* the game's custom-pin types */
    KILN_MARKER_ICON_FAST_TRAVEL = 32, /* listed with surfaces 0: refused on the map until its interaction is checked */
};

/* Opaque, owner-bound lease from capture_anchor, valid for the process and that world; 0 = a fixed position. Never
 * an entity id, never persisted or sent. */
typedef uint64_t KilnMarkerAnchor;

typedef struct KilnMarkerSpec {
    uint32_t size;           /* sizeof(KilnMarkerSpec) */
    uint32_t surfaces;       /* KILN_MARKER_MAP | _COMPASS | _WORLD, nonzero */
    KilnStr key;             /* 1 to 64 bytes of [A-Za-z0-9_.:-] */
    KilnVec3 position;       /* metres, x and z in [-1000, 12000], y in [-1000, 5000]; finite but ignored with follow */
    KilnMarkerAnchor follow; /* 0: fixed at position */
    float height;            /* metres above the point for world labels, finite, [-128, 128] (default 0) */
    float max_distance;      /* metres, finite, [0, 100000]; 0 = unlimited (default) */
    uint32_t icon_type;      /* a catalog id whose surfaces include MAP when MAP is asked for; else 0 */
    uint32_t rgba;           /* colour of drawn glyphs and text (compass, world), 0xRRGGBBAA; default 0xffffffff */
    int32_t priority;        /* [-32768, 32767], higher first when a surface has no room (default 0) */
    uint32_t map_label_mode; /* KILN_MARKER_LABEL_*, default NONE */
    KilnStr text;            /* strict UTF-8, at most 128 bytes, one line, no U+0000-001F or U+007F-009F; may be empty */
} KilnMarkerSpec;

typedef struct KilnMarkerResult {
    uint32_t size;      /* set by the caller: sizeof(KilnMarkerResult) */
    uint32_t status;    /* KILN_MARKER_ACCEPTED, ... */
    uint32_t committed; /* 1: the store holds the request (also when identical) */
    uint32_t queued;    /* server sends: recipients whose update is admitted to their link */
    uint32_t refused;   /* server sends: connected recipients that cannot take it */
    uint32_t affected;  /* records changed (0 for an identical value or an empty clear) */
    char reason[256];   /* NUL-terminated words; empty when accepted. See the header comment for errors */
} KilnMarkerResult;

typedef struct KilnMarkerAnchorResult {
    uint32_t size;          /* set by the caller */
    uint32_t status;        /* ACCEPTED, NOT_FOUND (no such entity or transform: no lease), NOT_READY (epoch mismatch) */
    KilnMarkerAnchor anchor; /* the caller's lease (release it with release_anchor); 0 unless accepted */
    uint64_t world_epoch, incarnation; /* diagnostic */
    uint32_t world_kind;    /* KILN_WORLD_SERVER or KILN_WORLD_CLIENT */
    uint32_t entity;        /* diagnostic: the entity of that world */
    KilnVec3 position;      /* where it was at capture */
    char reason[256];
} KilnMarkerAnchorResult;

typedef struct KilnMarkerIcon {
    uint32_t size;             /* set by the caller */
    uint32_t id;               /* the game's marker type id */
    uint32_t flags;            /* KILN_MARKER_ICON_* */
    uint32_t surfaces;         /* KILN_MARKER_MAP, or 0 (listed, not usable on the map) */
    uint8_t texture_guid[16];  /* the icon image, game byte order (.NET Guid.ToByteArray()) */
    char key[14];              /* canonical "type:" + eight lower-case hex digits, NUL-terminated */
    char alias[129];           /* the texture name without a leading "mapmarker_", ASCII lower-cased */
    char texture_name[129];    /* the texture's debug name, UTF-8 */
} KilnMarkerIcon;

typedef struct KilnMarkerChange {
    uint32_t size;      /* sizeof(KilnMarkerChange) of the service */
    uint32_t kind;      /* KILN_MARKER_ADDED, ... */
    uint32_t audience;  /* KILN_MARKER_LOCAL, ... */
    uint32_t recipient; /* the player a target-layer change is for; 0 otherwise */
    uint64_t world_epoch, peer_generation, publisher_generation, sequence;
    uint32_t affected;  /* records: 1, or the count a CLEARED summary stands for */
    uint32_t cause;     /* KILN_MARKER_API, ... */
    char owner[65];     /* the publisher's namespace */
    char key[65];       /* empty for CLEARED */
} KilnMarkerChange;

typedef struct KilnMarkerVisibility {
    uint32_t size, audience, recipient, initial;
    uint64_t world_epoch, peer_generation, publisher_generation, sequence;
    uint32_t requested, before, after;
    uint32_t reason[3]; /* MAP, COMPASS, WORLD; READY exactly when that bit of `after` is set */
    char owner[65], key[65];
} KilnMarkerVisibility;

typedef struct KilnMarkerCatalogChange {
    uint32_t size;
    uint32_t role;       /* 0 server, 1 client */
    uint32_t ready;
    uint32_t count;
    uint64_t generation;
    char reason[256];    /* empty, or why native map icons stay off with a ready catalog */
} KilnMarkerCatalogChange;

typedef void(KILN_CALL* KilnMarkerChangedFn)(const KilnMarkerChange* change, void* user);
typedef void(KILN_CALL* KilnMarkerVisibilityFn)(const KilnMarkerVisibility* visibility, void* user);
typedef void(KILN_CALL* KilnMarkerCatalogFn)(const KilnMarkerCatalogChange* catalog, void* user);

typedef struct KilnMarkersApi {
    KilnServiceHeader header; /* { sizeof(KilnMarkersApi), KILN_MARKERS_VERSION } */

    /* bind_owner: KILN_OK (also again with the same namespace); KILN_ERR_INVALID_ARGUMENT: owner 0, a namespace
     *   outside the grammar, this owner bound to another namespace, or the namespace bound to another live owner;
     *   KILN_ERR_BUFFER_TOO_SMALL: 64 distinct namespaces were bound in this process and this one is new.
     * release_owner: clears the owner's local and sent records (one CLEARED per nonempty scope, cause OWNER_RETIRED,
     *   dispatched at the next step), releases its anchor references, removes its subscriptions (waits for one of
     *   their callbacks running on another thread) and frees the namespace. KILN_OK also when nothing was bound. */
    KilnResult(KILN_CALL* bind_owner)(KilnOwner owner, KilnStr stable_namespace);
    KilnResult(KILN_CALL* release_owner)(KilnOwner owner);

    /* The icon catalog: 0 and 0 while it warms up. catalog_at: KILN_ERR_NOT_FOUND past the end. catalog_resolve:
     * 1 to 128 printable ASCII bytes without surrounding spaces, ASCII lower-cased, then exactly "type:" and eight hex
     * digits, or an exact alias (no prefix stripping, no fuzzy match); anything else is KILN_ERR_INVALID_ARGUMENT.
     * Status ACCEPTED (icon filled), NOT_FOUND, AMBIGUOUS (an alias of several types) or CATALOG_NOT_READY. */
    KilnResult(KILN_CALL* catalog_count)(uint32_t* count, uint64_t* generation);
    KilnResult(KILN_CALL* catalog_at)(uint32_t index, KilnMarkerIcon* out);
    KilnResult(KILN_CALL* catalog_resolve)(KilnStr name, KilnMarkerIcon* out, KilnMarkerResult* result);

    /* world_epoch: the service's epoch of that world kind's current world, 0 when there is none. A new world of any
     *   kind takes the next number; it is never reused. Any thread. */
    KilnResult(KILN_CALL* world_epoch)(uint32_t world_kind, uint64_t* epoch);
    KilnResult(KILN_CALL* capture_anchor)(KilnOwner owner, uint32_t world_kind, uint64_t expected_epoch, KilnEntity entity,
                                          KilnMarkerAnchorResult* out);
    KilnResult(KILN_CALL* release_anchor)(KilnOwner owner, KilnMarkerAnchor anchor);

    /* The local store (client): validate, reserve, intern and commit under a short lock; no game call, no callback.
     * NOT_READY without a client world. remove_local of an absent key: NOT_FOUND. clear_local: one CLEARED for a
     * nonempty scope. The owner must be bound (else KILN_ERR_INVALID_ARGUMENT). */
    KilnResult(KILN_CALL* set_local)(KilnOwner owner, const KilnMarkerSpec* spec, KilnMarkerResult* result);
    KilnResult(KILN_CALL* remove_local)(KilnOwner owner, KilnStr key, KilnMarkerResult* result);
    KilnResult(KILN_CALL* clear_local)(KilnOwner owner, KilnMarkerResult* result);

    /* Server layers (recipient 0 = the broadcast layer). */
    KilnResult(KILN_CALL* send)(KilnOwner owner, KilnEntity recipient, const KilnMarkerSpec* spec, KilnMarkerResult* result);
    KilnResult(KILN_CALL* unsend)(KilnOwner owner, KilnEntity recipient, KilnStr key, KilnMarkerResult* result);
    KilnResult(KILN_CALL* clear_sent)(KilnOwner owner, KilnEntity recipient, KilnMarkerResult* result);
    KilnResult(KILN_CALL* suppress_sent)(KilnOwner owner, KilnEntity recipient, KilnStr key, KilnMarkerResult* result);

    /* subscribe: every change of every publisher, as immutable copies valid for the call. Any callback may be NULL.
     * Logical changes arrive in accepted order at the next step of their world (client changes: the client world's
     * Post); a change a callback makes arrives in the next dispatch. When a world stops, its final notifications are
     * delivered before the stop returns, on the stopping thread: do not call game code then. No callback runs under a
     * lock of the service. A callback that faults is unsubscribed. Catalog changes: once when it becomes ready; not
     * replayed for a later subscriber (read catalog_count first).
     * unsubscribe: from outside the callback it waits for a running one; from inside it returns at once. */
    KilnResult(KILN_CALL* subscribe)(KilnOwner owner, KilnMarkerChangedFn changed, KilnMarkerVisibilityFn visibility,
                                     KilnMarkerCatalogFn catalog, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);
} KilnMarkersApi;

/* Internal bridge of the built-in receiver (Kiln.Markers). No public managed member exposes this table. All bytes are
 * copied. role 0 = server, 1 = client; route 1 = Network, 2 = hosted loopback. peer_up, peer_down and pump run on that
 * role's thread; receive is any thread and only enqueues (applied at the role's next step); attach is startup only.
 * pump calls its send callback outside locks and never re-enters author code. */
typedef struct KilnMarkerPeer {
    uint32_t size, role, peer, route, entity, max_message_bytes, flags, reserved;
    uint64_t generation;
} KilnMarkerPeer;

typedef struct KilnMarkerEnvelope {
    uint32_t size, role, peer, message;
    uint64_t peer_generation;
    const uint8_t* bytes;
    uint32_t length, reserved;
} KilnMarkerEnvelope;

typedef uint8_t(KILN_CALL* KilnMarkerSendFn)(const KilnMarkerEnvelope* envelope, void* user);

typedef struct KilnMarkersBridgeApi {
    KilnServiceHeader header; /* { sizeof(KilnMarkersBridgeApi), KILN_MARKERS_BRIDGE_VERSION } */
    KilnResult(KILN_CALL* attach)(KilnOwner owner);
    KilnResult(KILN_CALL* peer_up)(const KilnMarkerPeer* peer);
    KilnResult(KILN_CALL* peer_down)(uint32_t role, uint32_t peer, uint64_t generation);
    KilnResult(KILN_CALL* receive)(const KilnMarkerEnvelope* envelope);
    KilnResult(KILN_CALL* pump)(uint32_t role, uint32_t max_packets, KilnMarkerSendFn send, void* user, uint32_t* processed);
} KilnMarkersBridgeApi;

/* v1 layouts (64-bit): a reader of a table checks header.size, a writer of a struct checks size. */
#if defined(__cplusplus)
#define KILN_MARKERS_SIZE(type, bytes) static_assert(sizeof(type) == (bytes), #type " is " #bytes " bytes in v1")
#elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
#define KILN_MARKERS_SIZE(type, bytes) _Static_assert(sizeof(type) == (bytes), #type " is " #bytes " bytes in v1")
#else
#define KILN_MARKERS_SIZE(type, bytes)
#endif
#if UINTPTR_MAX == 0xffffffffffffffffu
KILN_MARKERS_SIZE(KilnMarkerSpec, 96);
KILN_MARKERS_SIZE(KilnMarkerResult, 280);
KILN_MARKERS_SIZE(KilnMarkerAnchorResult, 320);
KILN_MARKERS_SIZE(KilnMarkerIcon, 304);
KILN_MARKERS_SIZE(KilnMarkerChange, 192);
KILN_MARKERS_SIZE(KilnMarkerVisibility, 208);
KILN_MARKERS_SIZE(KilnMarkerCatalogChange, 280);
KILN_MARKERS_SIZE(KilnMarkersApi, 144);
KILN_MARKERS_SIZE(KilnMarkerPeer, 40);
KILN_MARKERS_SIZE(KilnMarkerEnvelope, 40);
KILN_MARKERS_SIZE(KilnMarkersBridgeApi, 48);
#endif
#undef KILN_MARKERS_SIZE

#ifdef __cplusplus
}
#endif
#endif /* KILN_MARKERS_H */
