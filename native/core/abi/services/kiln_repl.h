/* SPDX-License-Identifier: MIT
 * kiln.repl v1 (since 0.2.0-dev.28): the game's ECS replication, read only. What the server sends each peer (per
 * machine and channel counters, the component bytes of one entity as each peer is sent them), what a Kiln client
 * receives, and on lab servers a loopback harness that runs the game's own replication for fake players.
 * Engine facts: docs/re/network.md (the dev.28 replication notes); the dossier's chapter 11.
 *
 * Machines and peers
 *  - The game replicates per machine (0..16, the GameServer's machine slots). An event or a stats row names the peer by
 *    its full handle (generation | machine), the handle in that machine's slot when the frame ran: a reused slot is a
 *    new peer. In a hosted world machine 0 is the local client; a dedicated server's machine 0 is the server itself and
 *    gets no replication.
 *  - A loopback fake (lab only, below) has no handle: it shows as KILN_REPL_FAKE_PEER | machine, unless a kiln.net lab
 *    peer was bound to its machine (kiln_net.h, KILN_NET_SELFTEST_BIND_MACHINE): then it shows as that lab peer.
 * Channels: 4 = client channel, 5 = prediction channel (the game's wire channel numbers).
 * Threads: the game's replication jobs run on the world's worker threads, in parallel per (machine, channel), after the
 * sim step; kiln.repl's hooks there only copy into memory of their own (machine, channel). Everything a caller gets
 * arrives on the tick thread (server) / main thread (client) in the next Pre.
 * Faults: kiln.hooks does not guard raw detours, so kiln.repl guards its own code in each of its detours: the game's
 * function runs exactly once and its result is untouched; a fault in kiln.repl's part disables that hook for the
 * session (it passes through) and logs "repl: observer <name> faulted at <where>; disabled" once.
 *
 * The table only grows at its end; readers check header.size. */
#ifndef KILN_REPL_H
#define KILN_REPL_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_REPL_SERVICE "kiln.repl"
#define KILN_REPL_VERSION 1u

/* capabilities(): what this table can do in this process (the bits are frozen). A bit is set when its sigdb feature
 * resolved and its code is there; the capability rows (repl.observe, repl.client, repl.loopback) say why one is not. */
enum {
    KILN_REPL_CAP_OBSERVE = 1u << 0,  /* sigdb feature repl.observe (server side, also a hosted world) */
    KILN_REPL_CAP_CLIENT = 1u << 1,   /* repl.client (a Kiln client's receive side) */
    KILN_REPL_CAP_LOOPBACK = 1u << 2, /* repl.loopback: the lab harness; only with policy "any" and a kiln.net lab port */
    KILN_REPL_CAP_FILTER = 1u << 3,   /* reserved (snapshot filters) */
    KILN_REPL_CAP_TOUCH = 1u << 4,    /* reserved */
    KILN_REPL_CAP_MESSAGES = 1u << 5, /* reserved */
    KILN_REPL_CAP_SCHEMA = 1u << 6,   /* reserved */
};

#define KILN_REPL_ALL_MACHINES 0xFFFFFFFFu
#define KILN_REPL_FAKE_PEER 0x80000000u /* a loopback fake without a bound lab peer: KILN_REPL_FAKE_PEER | machine */
#define KILN_REPL_MACHINES 17u          /* machine indices 0..16 */

enum { KILN_REPL_SEND = 1, KILN_REPL_RECEIVE = 2 };

/* KilnReplPeerStats.flags */
enum {
    KILN_REPL_PEER_FAKE = 1u,  /* a loopback fake machine (lab) */
    KILN_REPL_PEER_BOUND = 2u, /* ... with a kiln.net lab peer bound to it: `peer` is that lab peer */
};

typedef struct KilnReplPeerStats { /* per machine and channel, since the first enable */
    uint32_t size;                 /* set by the caller: sizeof(KilnReplPeerStats) */
    uint32_t peer;                 /* full peer handle (generation | machine); see "Machines and peers" */
    uint8_t channel;               /* 4 client, 5 prediction */
    uint8_t flags;                 /* KILN_REPL_PEER_* */
    uint8_t pad[6];
    uint64_t frames, messages, bytes;
    uint64_t refused;              /* messages the session refused at allocation (batch count - batch sent) */
    uint64_t creates, updates, destroy_lists, destroyed_ids;
    uint64_t unchanged;            /* writer skips with nothing changed */
    uint64_t too_big;              /* writer skips because the message was over the channel's maximum */
    uint64_t acks, resets, dropped; /* ack messages, reset requests, records the rings dropped */
} KilnReplPeerStats;

/* One component of a watched entity, as a peer is sent it (send) or as this client decoded it (receive). */
typedef struct KilnReplComponent {
    uint16_t index;                /* the component's index in the world's registry */
    uint16_t size;                 /* bytes at `data` */
    uint32_t hash;                 /* component hash (kiln_world.h component_hash) */
    uint8_t custom_codec;          /* 1: the game packs it with its own codec; a client sees it quantized */
    uint8_t pad[7];
    const uint8_t* data;
} KilnReplComponent;

typedef struct KilnReplWatchEvent { /* tick thread (send) / main thread (receive), next Pre; valid during the call */
    uint32_t size;
    uint32_t peer;
    uint8_t direction, channel, kind /* 2 create, 3 update, 4 destroyed */, gap /* 1: records were dropped before it */;
    uint32_t entity, tick, seq, baseline_tick, payload_bytes;
    uint16_t count, pad;
    uint32_t pad2;
    const KilnReplComponent* components;
} KilnReplWatchEvent;

typedef void(KILN_CALL* KilnReplWatchFn)(const KilnReplWatchEvent* e, void* user);

/* KilnReplComponentInfo.flags */
enum {
    KILN_REPL_COMPONENT_REPLICATED = 1u,  /* a tracked write of it queues the entity for replication */
    KILN_REPL_COMPONENT_VERSIONED = 2u,   /* a version byte travels with it */
    KILN_REPL_COMPONENT_CUSTOM_CODEC = 4u, /* the game packs it with its own codec */
    KILN_REPL_COMPONENT_CLIENT = 8u,      /* may be on the client channel */
    KILN_REPL_COMPONENT_PREDICTION = 16u, /* may be on the prediction channel */
};

typedef struct KilnReplComponentInfo {
    uint32_t size;                 /* set by the caller: sizeof(KilnReplComponentInfo) */
    uint32_t hash;
    uint16_t index;                /* registry index */
    uint16_t data_size;            /* bytes of one component */
    uint32_t flags;                /* KILN_REPL_COMPONENT_* */
} KilnReplComponentInfo;

/* ---- lab: the loopback harness (KILN_REPL_CAP_LOOPBACK) ----
 * The game's own replication runs for "fake players" on free machine indices of the server world: the game activates a
 * replication object per fake, runs relevance, snapshot, writer, delta and send-callback jobs for it on its workers
 * every frame, and the fake's messages are captured at the send callback instead of a socket. In the next Pre the
 * server exe's own copy of the client decoder decodes them (against the template's state or the harness's earlier
 * decoded state, as a client does), each decoded state is compared component by component with the snapshot the
 * server coded, and the fake acknowledges every message through the game's own ack handler, so baselines advance as
 * with a real client. Nothing is relevant to a fake by itself (it has no player entity): lab_force_relevant names the
 * entities. What it does not give: a client world (merge, apply), a player entity, a session (bandwidth, receive).
 * Every lab function answers KILN_ERR_UNAVAILABLE unless kiln.json's policy is "any" AND kiln.net's lab transport is
 * open (labPort), and KILN_ERR_WRONG_THREAD off the tick thread. */
typedef struct KilnReplFake {
    uint32_t machine;              /* machine index (16, 15, ... the highest free ones) */
    uint32_t slot;                 /* player slot the game replicates it as (15, 14, ...) */
} KilnReplFake;

typedef struct KilnReplLoopbackStats { /* since the fakes were added (counters start over at an add from none) */
    uint32_t size;                 /* set by the caller: sizeof(KilnReplLoopbackStats) */
    uint32_t fakes;                /* bit per machine: the fakes active now */
    uint64_t messages;             /* messages captured from the fakes' batches (every type) */
    uint64_t decoded;              /* entity messages decoded */
    uint64_t components_equal;     /* generic components equal to the server's snapshot */
    uint64_t components_differ;    /* generic components that differ, or a decoded component set that differs */
    uint64_t custom_equal, custom_differ; /* components with the game's own codec (lossy: a difference is expected) */
    uint64_t decode_faults;        /* the decoder or the template state faulted or returned nothing */
    uint64_t no_baseline;          /* an update whose baseline the harness did not have (or an unknown template) */
    uint64_t acked;                /* message seqs acknowledged back */
    uint64_t dropped;              /* messages or snapshots that did not fit the capture buffers */
    uint64_t destroy_lists;        /* destroy-list messages captured */
    uint32_t active_objects;       /* bit per machine: the game's replication object is active now (also real players) */
    uint32_t slot_mask;            /* the world's mask of player slots being replicated, now */
} KilnReplLoopbackStats;

typedef struct KilnReplApi {
    KilnServiceHeader header;      /* { sizeof(KilnReplApi), KILN_REPL_VERSION } */
    uint32_t(KILN_CALL* capabilities)(void); /* KILN_REPL_CAP_*; any thread */
    /* Reference-counted per owner: the first enable installs the stats hooks and zeroes the counters, the last disable
     * (or owner revoke) removes them. Tick thread (server) / main thread (client). */
    KilnResult(KILN_CALL* stats_enable)(KilnOwner owner, bool on);
    KilnResult(KILN_CALL* peer_stats)(KilnReplPeerStats* out, uint32_t cap, uint32_t* count); /* KILN_ERR_BUFFER_TOO_SMALL */
    KilnResult(KILN_CALL* watch)(KilnOwner owner, KilnEntity e, uint32_t peer_or_all, uint32_t direction,
                                 KilnReplWatchFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* remove)(KilnHandle h); /* any handle this service returned */
    /* The registry's facts about a component, from the live world (cached at its first Pre). Any thread.
     * KILN_ERR_NOT_FOUND: no component of that hash; KILN_ERR_UNAVAILABLE: no world seen yet. */
    KilnResult(KILN_CALL* component_info)(uint32_t hash, KilnReplComponentInfo* out);
    KilnResult(KILN_CALL* capture)(KilnOwner owner, uint32_t peer, uint32_t seconds, KilnStr path);

    /* ---- lab: the loopback harness; since 0.2.0-dev.28; check header.size ---- */
    /* Adds `count` (1..16) fakes on the highest free machine indices and free player slots; `out` gets up to `cap` of
     * them, *added how many. They replicate from the next frame on. KILN_ERR_INVALID_ARGUMENT: a bad count, or fakes are
     * active already (remove them first); KILN_ERR_NOT_FOUND: not that many free machines or slots; KILN_ERR_UNAVAILABLE:
     * no server world yet (or no send step seen), or the harness is off. */
    KilnResult(KILN_CALL* lab_add_fakes)(uint32_t count, KilnReplFake* out, uint32_t cap, uint32_t* added);
    /* Makes `entity` relevant to fake `machine` on both channels, from the next frame on (at most 64 per fake).
     * KILN_ERR_INVALID_ARGUMENT: not a fake machine, or entity 0. */
    KilnResult(KILN_CALL* lab_force_relevant)(uint32_t machine, KilnEntity entity);
    /* Stops the fakes and lets the game free their replication objects (the world's slot bits go). Their counters stay
     * for lab_stats. KILN_OK also when there were none. */
    KilnResult(KILN_CALL* lab_remove_fakes)(void);
    KilnResult(KILN_CALL* lab_stats)(KilnReplLoopbackStats* out);
    /* The newest state the harness decoded for `entity` on fake `machine`'s client channel: the bytes of component
     * `hash` (*len = their count; copied up to `cap`; out may be NULL to ask the size) and that message's tick.
     * KILN_ERR_NOT_FOUND: nothing decoded for it, or that state has no such component; KILN_ERR_BUFFER_TOO_SMALL. */
    KilnResult(KILN_CALL* lab_decoded)(uint32_t machine, KilnEntity entity, uint32_t hash, uint8_t* out, uint32_t cap,
                                       uint32_t* len, uint32_t* tick);
} KilnReplApi;

#ifdef __cplusplus
}
#endif
#endif /* KILN_REPL_H */
