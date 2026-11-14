/* SPDX-License-Identifier: MIT
 * kiln.net v1: Kiln's frame transport between a client and the server, in-band in the game's own connection.
 *
 * Transport (docs/re/network.md): the game's session layer gives every connection ("machine") one message socket per
 * game channel. Game channel 12 is configured reliable + large and is read by no vanilla code, so Kiln uses it: on
 * the server a hook right after the GameServer syncs its sockets drains channel 12 of every machine before the game's
 * own loop runs; on the client a hook on the GameClient receive step drains it and flushes Kiln's queued frames. No
 * extra port; the game session authenticates and encrypts the connection.
 *
 * This service moves opaque frames (at most `max_frame` bytes each). The protocol on top (handshake, mod sync,
 * channels, fragmentation, per-channel limits) is Kiln.Network's (managed/services/Kiln.Network, docs/kiln/network.md).
 *
 * Peers
 *  - Server side: a peer is a connected machine, identified by the game's machine (peer) handle (1..0x7FFFFFFF). The
 *    player of a peer comes from the player record whose +0x04 names it (KilnNetPeerInfo.entity/name/account).
 *  - Client side: the server is KILN_NET_PEER_SERVER.
 *  - Lab peers (tests only): with "labPort" in kiln/config/network.json or KILN_NET_LAB_PORT, the server also accepts
 *    frames as UDP datagrams on 127.0.0.1:<port>; each source address is a peer KILN_NET_PEER_LAB | n
 *    (tools/nettest speaks the same frames). Never enable it on a public server.
 * Threads
 *  - Frames and peer events are delivered on the tick thread in Pre (server: the same server frame as the receive).
 *  - send: tick thread on the server (the frame goes straight into the game's socket); any thread on the client
 *    (queued, sent from the GameClient receive step on the main thread).
 *  - status / peers / peer_info: any thread (snapshot).
 * Native subscribers must unsubscribe before they unload: the service cannot see owner_revoke.
 *
 * Clients without Kiln (docs/kiln/network.md "Clients without Kiln", docs/re/network.md "Vanilla clients"). A vanilla
 * client crashes on the first entity of a template it doesn't have, so nothing of that kind may reach it:
 *  - Proof: a Kiln client sends KILN_NET_KNOCK frames while it loads, before its Gameplay Ready. Vanilla code never
 *    writes channel 12, so any frame from a peer proves it has Kiln. Knocks are not delivered to subscribers.
 *  - Gate (gate_set): the game starts replicating to a machine when its Gameplay Ready arrives. With the gate on, an
 *    unproven peer's Gameplay Ready is held: no player entity, no world data. It goes on by itself when proof arrives;
 *    otherwise Kiln.Network decides: kick (refuse) or gate_release (let in).
 *  - Withholding: a peer gets no entities of templates only Kiln has, or their children, until withhold_set clears it:
 *    never for a vanilla client (it sees the world without them), at its content sync's ack for a Kiln client, so
 *    definitions that come over the sync are in place first. What was kept back then goes out. A clear covers the
 *    Kiln-only templates there are at that moment: entities of a template added later (a hot load) are kept from that
 *    peer until withhold_set clears it again, at the ack of a content state that has it. Ids inside other data (an item
 *    in a stack) are not covered.
 *  - Client guard: a Kiln client that still gets a create of a template it lacks (a server mod it doesn't have) drops it
 *    and its children, with one log line per template, instead of faulting in the game's create path. */
#ifndef KILN_NET_H
#define KILN_NET_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_NET_SERVICE "kiln.net"
#define KILN_NET_VERSION 1u

typedef uint32_t KilnPeer;
#define KILN_NET_PEER_NONE 0u
#define KILN_NET_PEER_SERVER 0xFFFFFFFFu /* on a client: the server it is connected to */
#define KILN_NET_PEER_LAB 0x80000000u    /* bit set: a lab loopback peer (tests) */

/* KilnNetPeerInfo.transport */
enum {
    KILN_NET_TRANSPORT_GAME = 1, /* the game connection, channel 12 */
    KILN_NET_TRANSPORT_LAB = 2,  /* loopback UDP (lab tests) */
};

/* KilnNetStatus.roles */
enum {
    KILN_NET_ROLE_SERVER = 1u, /* this process serves clients (dedicated server, or a hosted world) */
    KILN_NET_ROLE_CLIENT = 2u, /* this process is a client of a server */
};

/* Peer events (KilnNetPeerFn) */
enum {
    KILN_NET_PEER_UP = 1,   /* the peer's transport exists (it may or may not speak Kiln) */
    KILN_NET_PEER_DOWN = 2, /* gone: disconnected, its machine slot was reused, or (lab) forgotten */
};

/* KilnNetPeerInfo.flags */
enum {
    KILN_NET_PEER_HAS_PLAYER = 0x01u, /* entity/slot/name/account come from the game's player record */
    KILN_NET_PEER_LOGGED_IN = 0x02u,  /* the machine slot's logged-in bit is set */
    KILN_NET_PEER_PROVED = 0x04u,     /* a frame came from this peer: it has Kiln */
    KILN_NET_PEER_HELD = 0x08u,       /* its Gameplay Ready waits in the gate: the server sends it no world data */
    KILN_NET_PEER_WITHHELD = 0x10u,   /* entities of Kiln-only templates are withheld from its player */
    /* since 0.2.0-dev.28: */
    KILN_NET_PEER_REFUSED = 0x20u,    /* kick() or the gate's time limit refused it: until it leaves, its Gameplay Ready is
                                         never processed (it gets no world data if it was not in the world yet) */
    KILN_NET_PEER_ENDING = 0x40u,     /* refused; the login failure could not be sent, so the game's own session kick was
                                         (feature net.disconnect): the game ends the connection about a second later */
    KILN_NET_PEER_BLOCKED = 0x80u,    /* refused before it was in the world, and nothing could end it (no login failure and
                                         no session kick on this build, or both faulted): it stays connected, blocked, with
                                         no world data, until it leaves */
};

/* The proof frame, Kiln.Network's frame header with type 10. Four bytes: the game pads frames to a multiple of 4. */
#define KILN_NET_KNOCK_INIT {'K', 'N', 1, 10}

/* gate_set() modes */
enum {
    KILN_NET_GATE_OFF = 0,  /* the default: every Gameplay Ready goes straight through (withholding still applies) */
    KILN_NET_GATE_HOLD = 1, /* an unproven peer's Gameplay Ready is held */
    KILN_NET_GATE_HOLD_ALL = 2, /* since 0.2.0-dev.28: every peer's Gameplay Ready is held, a proved one's too; proof does
                                   not end it, only gate_release, a kick, a lower mode or the 30 s limit (an older
                                   kiln.net answers KILN_ERR_INVALID_ARGUMENT) */
};

/* KilnNetGateInfo.caps */
enum {
    KILN_NET_GATE_CAN_HOLD = 1u,     /* feature net.gate: Gameplay Ready is hooked on this build */
    KILN_NET_GATE_CAN_WITHHOLD = 2u, /* feature net.withhold + kiln.assets: entities can be withheld on this build */
    KILN_NET_GATE_CAN_GUARD = 4u,    /* feature net.client_guard: this client drops creates of templates it lacks */
    KILN_NET_GATE_CAN_HOLD_ALL = 8u, /* since 0.2.0-dev.28: gate_set takes KILN_NET_GATE_HOLD_ALL (with CAN_HOLD) */
    KILN_NET_GATE_CAN_DISCONNECT = 16u, /* since 0.2.0-dev.28: feature net.disconnect: kick() ends a peer the login failure
                                           cannot reach through the game's own session kick (dedicated server only) */
};

typedef struct KilnNetGateInfo {
    uint32_t size;              /* set to sizeof(KilnNetGateInfo) before calling */
    uint32_t caps;              /* KILN_NET_GATE_CAN_* */
    uint32_t mode;              /* KILN_NET_GATE_* */
    uint32_t held;              /* peers held right now */
    uint32_t kiln_templates;    /* templates served that the game does not have */
    uint32_t withheld_slots;    /* bit per player slot: entities are withheld from it */
    uint64_t withheld_entities; /* times an entity was kept out of a player's replication so far */
    uint32_t missing_templates; /* client: templates of this connection the game doesn't have */
    uint32_t newer_slots;       /* bit per player slot: cleared, with entities kept back for templates added since */
    uint64_t dropped_creates;   /* client: creates dropped on this connection, children included */
} KilnNetGateInfo;

typedef struct KilnNetPeerInfo {
    uint32_t size;          /* sizeof(KilnNetPeerInfo) */
    KilnPeer peer;
    uint32_t transport;     /* KILN_NET_TRANSPORT_* */
    uint32_t flags;         /* KILN_NET_PEER_* */
    KilnEntity entity;      /* the player's entity (slot + 1); 0 = no player record yet */
    uint32_t slot;          /* player slot 0..15; 0xFFFFFFFF = none */
    uint32_t connection;    /* the game's player connection handle (record +0x00); 0 = none */
    uint32_t record_state;  /* the record's raw state byte (1 loading, 2 waiting, 3 in game, 4-6 leaving) */
    uint64_t account;       /* the game's account id (record +0x48); 0 = unknown */
    uint64_t up_ms;         /* GetTickCount64 when the peer came up */
    uint64_t rx_frames, rx_bytes, tx_frames, tx_bytes;
    uint64_t dropped;       /* frames dropped for this peer (rate limit, queue full, send failure) */
    char name[64];          /* platform name from the player record, UTF-8, NUL-terminated (cut to 63 bytes) */
} KilnNetPeerInfo;

typedef struct KilnNetStatus {
    uint32_t size;          /* set to sizeof(KilnNetStatus) before calling */
    uint32_t roles;         /* KILN_NET_ROLE_* hooked in this process */
    uint8_t server_ready;   /* server half: the GameServer has been seen (hook fired at least once) */
    uint8_t client_ready;   /* client half: a connection to a server exists right now */
    uint8_t lab;            /* the lab UDP transport is open */
    uint8_t reserved;
    uint32_t channel;       /* game channel used (12) */
    uint32_t max_frame;     /* bytes per frame */
    uint32_t peers;         /* peers up right now */
    uint32_t lab_port;      /* 0 = lab transport off */
    uint32_t subscribers;
    uint64_t rx_frames, rx_bytes, tx_frames, tx_bytes;
    uint64_t dropped;       /* inbound frames dropped (queue full, rate limit, oversize) */
    uint64_t send_failures; /* outbound frames the game refused (no socket, budget, too big) after retries */
    uint64_t drains;        /* receive hook calls */
    uint64_t game_frames_seen; /* frames read from the game socket (both directions' receive side) */
} KilnNetStatus;

/* One frame from `from`; `data` is valid only during the call. Tick thread. */
typedef void(KILN_CALL* KilnNetFrameFn)(KilnPeer from, const uint8_t* data, uint32_t len, void* user);
/* KILN_NET_PEER_UP / _DOWN. Tick thread. */
typedef void(KILN_CALL* KilnNetPeerFn)(KilnPeer peer, uint32_t event, void* user);

typedef struct KilnNetApi {
    KilnServiceHeader header; /* { sizeof(KilnNetApi), KILN_NET_VERSION } */
    KilnResult(KILN_CALL* status)(KilnNetStatus* out);
    /* Every frame and peer event (either callback may be NULL). Peers already up are reported as UP in the next Pre. */
    KilnResult(KILN_CALL* subscribe)(KilnOwner owner, KilnNetFrameFn on_frame, KilnNetPeerFn on_peer, void* user,
                                     KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);
    /* One frame (1..max_frame bytes). KILN_ERR_NOT_FOUND: no such peer; KILN_ERR_BUFFER_TOO_SMALL: frame too big;
     * KILN_ERR_WRONG_THREAD: server side off the tick thread. A frame the game cannot take this frame (bandwidth
     * budget) is retried in later frames; frames still unsent after ~2 s are dropped and counted. */
    KilnResult(KILN_CALL* send)(KilnPeer to, const uint8_t* data, uint32_t len);
    /* Peers up right now; `count` gets the total even when it exceeds `capacity`. */
    KilnResult(KILN_CALL* peers)(KilnNetPeerInfo* out, uint32_t capacity, uint32_t* count);
    KilnResult(KILN_CALL* peer_info)(KilnPeer peer, KilnNetPeerInfo* out);
    /* Server side: refuse a game peer with the game's own login-failure message (reason = keen::GameMessageLoginErrors:
     * 0 Authentication, 1 Version, 2 Full, 3 Shutdown, 4 SaveDataIssue, 5 Generic). Lab peers are forgotten (DOWN).
     * Tick thread. Since 0.2.0-dev.28 the peer is refused for good (KILN_NET_PEER_REFUSED) even when nothing can reach
     * it; when the login failure is not on this build or faults, the game's own session kick ends it instead
     * (KILN_NET_GATE_CAN_DISCONNECT, KILN_NET_PEER_ENDING) and the call answers KILN_OK; when neither works it answers
     * KILN_ERR_UNAVAILABLE or KILN_ERR_INTERNAL and the peer is KILN_NET_PEER_BLOCKED. */
    KilnResult(KILN_CALL* kick)(KilnPeer peer, uint32_t reason);
    /* The peer of a connected player's entity (server side). Any thread (snapshot). */
    KilnResult(KILN_CALL* peer_of_entity)(KilnEntity entity, KilnPeer* out);

    /* ---- clients without Kiln; added v1 (appended), check header.size before calling ---- */
    /* KILN_NET_GATE_OFF lets held peers go on. Any thread. KILN_ERR_UNAVAILABLE: no server role or no gate here. */
    KilnResult(KILN_CALL* gate_set)(uint32_t mode);
    KilnResult(KILN_CALL* gate_info)(KilnNetGateInfo* out);
    /* Lets a held peer go on: its Gameplay Ready is processed in the next receive step. Any thread. KILN_ERR_NOT_FOUND:
     * it isn't held. kick drops the hold instead. */
    KilnResult(KILN_CALL* gate_release)(KilnPeer peer);
    /* 1 when this server-world entity is withheld from peers without proof, 0 when not, -1 when it can't be read.
     * Tick thread. */
    int32_t(KILN_CALL* entity_withheld)(KilnEntity entity);
    /* 1 when this template GUID (game byte order) is one only Kiln has, 0 when not, -1 without kiln.assets. Any thread. */
    int32_t(KILN_CALL* template_kiln_only)(const uint8_t guid[16]);
    /* Whether Kiln-only entities are kept from this peer (`on`: a default for a new peer). Clearing sends what was kept
     * back and takes the Kiln-only templates there are now: entities of one added later wait for the next clear. Clearing
     * a cleared peer again does that again. Any thread. */
    KilnResult(KILN_CALL* withhold_set)(KilnPeer peer, uint32_t on);
    /* Lab transport only (else KILN_ERR_UNAVAILABLE), tick thread: drives the game's own code on the server world
     * (tools/nettest t_guard). `op` is a KILN_NET_SELFTEST_*. */
    KilnResult(KILN_CALL* lab_selftest)(uint32_t op, uint64_t a, uint64_t b, uint64_t* out);
} KilnNetApi;

/* lab_selftest ops */
enum {
    KILN_NET_SELFTEST_CREATE = 1, /* a = u8[16] template GUID, b = entity | parent << 32: the game's create reader on this
                                     world; out = its result's first word (0 = dropped) */
    KILN_NET_SELFTEST_SLOT = 2,   /* a = slot: out = the first byte of that player's replication object */
    KILN_NET_SELFTEST_MASK = 3,   /* a = slots forced to withhold on top of the peers' (0 = none), clearing queues re-sends */
    KILN_NET_SELFTEST_QUEUE = 4,  /* a = slot, b = entity: the game's relevance step on that slot with this entity new; out = heap size */
    KILN_NET_SELFTEST_BIND = 5,   /* a = lab peer, b = a free slot (0xFFFFFFFF: none): that peer stands for the slot's player, so
                                     withholding and withhold_set act on that slot as for a joined player */
    KILN_NET_SELFTEST_HEAP = 6,   /* a = slot: out = the size of that player's replication heap */
    KILN_NET_SELFTEST_BIND_MACHINE = 7, /* a = lab peer, b = machine index (0xFFFFFFFF: none): that peer stands for the
                                           loopback harness's fake player on that machine (kiln_repl.h), one peer per
                                           machine; a = 0 only asks. out = the lab peer bound to machine b now (0 = none) */
    KILN_NET_SELFTEST_KICK_FAIL = 8, /* since 0.2.0-dev.28: a = 0 off, 1 kick()'s login failure fails (game and lab peers;
                                        the gate's time limit too), 2 the session kick fails as well (net.disconnect off) */
    KILN_NET_SELFTEST_SESSION_KICK = 9, /* since 0.2.0-dev.28: a = a machine handle of index 0 (the server's own machine):
                                           the game's session kick of it, which the session refuses (host, or not a
                                           machine) and logs; out = 1 when it was queued */
};

#ifdef __cplusplus
}
#endif
#endif
