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

} KilnReplApi;

#ifdef __cplusplus
}
#endif
#endif /* KILN_REPL_H */
