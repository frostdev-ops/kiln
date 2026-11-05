/* SPDX-License-Identifier: MIT
 * kiln.players v1: who is on the server.
 *
 * Sources (evidence: docs/re/players.md, usage: docs/kiln/services/players.md)
 *  - The game server's 16 player records ("ServerPlayers", a server-world singleton): connection handle, state, name,
 *    account id, user-group permissions, player entity. Read every server-world step (Pre); entity = slot + 1.
 *  - "BasicPlayerInfos" (server-world singleton, 16 x 0xA0): the game's admin bit (+0x9D bit 0).
 *  - The game log (Kiln's mirror): SteamID64 ("[online] Added peer ... (steamid:...)"), and the whole player list on a
 *    build where the records are not found (fallback; no account id, user group or admin bit then).
 *  - Positions: the core's GetEntityTransform on the tick thread.
 *
 * Threads
 *  - status, list, get, find_*, events, last_seq, replay_log: any thread. They read a snapshot taken in the last Pre
 *    (at most one step old).
 *  - position: live on the tick thread; from any other thread it returns the snapshot's position.
 *  - raw_slot: tick thread only (reads game memory); KILN_ERR_WRONG_THREAD elsewhere.
 *  - subscribe callbacks run on the tick thread in Pre, after the scan that saw the change. A faulting callback is
 *    removed (logged); the server runs on. Native subscribers must unsubscribe before they unload: the service
 *    cannot see owner_revoke.
 *
 * Joined / left: a player has joined when their record is in game and their entity exists (the moment the game logs
 * "[server] Player '<name>' logged in with Permissions:"); they have left when the record leaves that state ("Remove
 * Entity for Player"), is reused by another connection, or is freed. Players already in game when you subscribe get
 * no JOINED: read list() first.
 *
 * KilnPlayer is frozen for v1 (arrays of it are packed at sizeof(KilnPlayer)); later versions add new functions. */
#ifndef KILN_PLAYERS_H
#define KILN_PLAYERS_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_PLAYERS_SERVICE "kiln.players"
#define KILN_PLAYERS_VERSION 1u
#define KILN_PLAYERS_NAME_MAX 128u /* bytes in KilnPlayer.name, NUL included */
#define KILN_PLAYERS_MAX_SLOTS 64u /* upper bound of status.slots; this build has 16 */

/* KilnPlayer.state */
enum {
    KILN_PLAYER_JOINING = 1, /* connected, loading (record states 1-2, or a log join without the login line yet) */
    KILN_PLAYER_IN_GAME = 2, /* logged in, the player entity exists (record state 3) */
    KILN_PLAYER_LEAVING = 3, /* being removed (record states 4-6) */
};

/* KilnPlayer.permissions: the rights of the player's user group (enshrouded_server.json "userGroups"), the bits the
 * game prints after "logged in with Permissions:". */
enum {
    KILN_PLAYER_CAN_KICK_BAN = 0x01u,
    KILN_PLAYER_CAN_ACCESS_INVENTORIES = 0x02u,
    KILN_PLAYER_CAN_EDIT_BASE = 0x04u,
    KILN_PLAYER_CAN_EDIT_WORLD = 0x08u,
    KILN_PLAYER_CAN_EXTEND_BASE = 0x10u,
    KILN_PLAYER_CAN_RECEIVE_EXP = 0x20u,
};

/* KilnPlayer.flags: which fields are known. */
enum {
    KILN_PLAYER_HAS_POSITION = 0x01u,    /* position is valid (entity has a transform) */
    KILN_PLAYER_HAS_STEAM_ID = 0x02u,    /* steam_id came from the game log */
    KILN_PLAYER_HAS_ADMIN = 0x04u,       /* is_admin was read from BasicPlayerInfos */
    KILN_PLAYER_HAS_PERMISSIONS = 0x08u, /* permissions are known (record, or the login line's list) */
    KILN_PLAYER_FROM_RECORDS = 0x10u,    /* this entry is the game's player record */
    KILN_PLAYER_FROM_LOG = 0x20u,        /* this entry was built from the game log only (fallback) */
};

typedef struct KilnPlayer {
    uint32_t size;         /* sizeof(KilnPlayer) */
    KilnEntity entity;     /* player entity id, 1..16 on this build (= slot + 1) */
    uint32_t slot;         /* the game's player slot, 0..15 */
    uint32_t state;        /* KILN_PLAYER_* state */
    uint32_t flags;        /* KILN_PLAYER_HAS_* | KILN_PLAYER_FROM_* */
    uint32_t permissions;  /* KILN_PLAYER_CAN_* */
    uint32_t user_group;   /* the game's user-group id (raw; 0 = unknown) */
    uint32_t handle;       /* the game's connection handle (slot | generation << 6; 0 from the log) */
    uint8_t is_admin;      /* the game's admin bit (gates the admin client's actions); see KILN_PLAYER_HAS_ADMIN */
    uint8_t reserved8[7];
    uint64_t account_id;   /* the game's account id of the player (record +0x48; 0 = unknown) */
    uint64_t steam_id;     /* SteamID64 (0 = unknown) */
    KilnVec3 position;     /* metres, see KILN_PLAYER_HAS_POSITION */
    uint64_t joined_step;  /* server-world step when Kiln saw the join (0 = in game before Kiln watched) */
    uint32_t name_len;     /* bytes of name, without the NUL */
    char name[KILN_PLAYERS_NAME_MAX]; /* the platform name (as the chat and the log show it), UTF-8, NUL-terminated */
    uint32_t reserved;
} KilnPlayer;

/* KilnPlayerEvent.kind */
enum {
    KILN_PLAYER_JOINED = 1,
    KILN_PLAYER_LEFT = 2,
};

typedef struct KilnPlayerEvent {
    uint64_t seq;      /* 1, 2, 3, ... (per process) */
    uint32_t kind;     /* KILN_PLAYER_JOINED / _LEFT */
    uint32_t reserved;
    uint64_t step;     /* server-world step of the scan that saw it */
    KilnPlayer player; /* JOINED: the player now; LEFT: as last seen in game */
} KilnPlayerEvent;

typedef void(KILN_CALL* KilnPlayerEventFn)(const KilnPlayerEvent* event, void* user);

/* KilnPlayersStatus.source */
enum {
    KILN_PLAYERS_SOURCE_NONE = 0,    /* nothing works on this build */
    KILN_PLAYERS_SOURCE_RECORDS = 1, /* the game's player records (full data) */
    KILN_PLAYERS_SOURCE_LOG = 2,     /* the game log only (names, steam ids, join/leave; no account/group/admin) */
};

typedef struct KilnPlayersStatus {
    uint32_t size;         /* set to sizeof(KilnPlayersStatus) before calling */
    uint32_t source;       /* KILN_PLAYERS_SOURCE_* */
    uint8_t records;       /* 1: the player records were found (layout resolved and the singleton captured) */
    uint8_t admin;         /* 1: BasicPlayerInfos was found (admin bit) */
    uint8_t positions;     /* 1: positions work (GetEntityTransform + server world) */
    uint8_t log;           /* 1: subscribed to the game-log mirror */
    uint8_t map_ok;        /* 1: the world's singleton map gives the same objects the registration hook saw */
    uint8_t layout_ok;     /* 1: the last scan found every record consistent (free, or handle names its slot) */
    uint8_t simulated;     /* 1: lab simulation (KILN_PLAYERS_SIMULATE): the players are replayed from a log file */
    uint8_t reserved8;
    uint32_t slots;        /* player slots (16 on this build) */
    uint32_t in_game;      /* players in game at the last scan */
    uint32_t connected;    /* records in use at the last scan (joining + in game + leaving) */
    uint32_t reserved;
    uint64_t last_seq;     /* newest event seq (0 = none yet) */
    uint64_t scans;        /* Pre scans done */
    uint64_t log_lines;    /* game log lines seen */
    uint64_t log_matched;  /* lines the log tracker understood */
    uintptr_t world;          /* advanced: the server world object (0 = not captured) */
    uintptr_t server_players; /* advanced: the "ServerPlayers" singleton (= the first player record) */
    uintptr_t basic_infos;    /* advanced: the "BasicPlayerInfos" singleton */
} KilnPlayersStatus;

/* raw_slot: one player record and its BasicPlayerInfos entry, as the game has them (diagnostics, layout checks). */
typedef struct KilnPlayerSlotRaw {
    uint32_t size;          /* set to sizeof(KilnPlayerSlotRaw) before calling */
    uint32_t handle;        /* record +0x00 */
    uint32_t peer;          /* record +0x04: the machine (peer) handle */
    uint32_t state;         /* record +0x0A: 0 free, 1 loading, 2 waiting for login, 3 in game, 4 removing entity,
                               5 removing, 6 failed */
    uint32_t user_group;    /* record +0x0C */
    uint32_t permissions;   /* record +0x10 */
    uint32_t entity;        /* record +0x18 (0 until the entity is spawned) */
    uint32_t name_len;      /* record +0x28 */
    uint64_t account_id;    /* record +0x48 */
    uint32_t info_entity;   /* BasicPlayerInfos[slot] +0x00 */
    uint32_t info_handle;   /* BasicPlayerInfos[slot] +0x04 */
    uint32_t info_admin;    /* BasicPlayerInfos[slot] +0x9D (the whole byte) */
    uint32_t info_nonzero;  /* bytes of the 0xA0 entry that are not zero */
} KilnPlayerSlotRaw;

/* list() flags */
enum {
    KILN_PLAYERS_LIST_IN_GAME = 0, /* default: players in game */
    KILN_PLAYERS_LIST_ALL = 1,     /* also joining and leaving connections */
};

typedef struct KilnPlayersApi {
    KilnServiceHeader header; /* { sizeof(KilnPlayersApi), KILN_PLAYERS_VERSION } */

    KilnResult(KILN_CALL* status)(KilnPlayersStatus* out);
    /* Players of the last scan, in slot order. *out_count = how many there are; when that is more than capacity the
     * first `capacity` are written and KILN_ERR_BUFFER_TOO_SMALL is returned. out may be NULL with capacity 0. */
    KilnResult(KILN_CALL* list)(uint32_t flags, KilnPlayer* out, uint32_t capacity, uint32_t* out_count);
    /* By player entity id (any state). KILN_ERR_NOT_FOUND when no player has it. */
    KilnResult(KILN_CALL* get)(KilnEntity entity, KilnPlayer* out);
    /* By name: exact match first, then ASCII case-insensitive; an in-game player wins over one joining/leaving. */
    KilnResult(KILN_CALL* find_name)(KilnStr name, KilnPlayer* out);
    KilnResult(KILN_CALL* find_steam_id)(uint64_t steam_id, KilnPlayer* out);
    /* Position in metres. Tick thread: read now; other threads: the last scan's. KILN_ERR_NOT_FOUND: not a player in
     * game or no transform; KILN_ERR_UNAVAILABLE: positions do not work on this build. */
    KilnResult(KILN_CALL* position)(KilnEntity entity, KilnVec3* out);

    /* Events with seq > after_seq, oldest first, from a ring of the last 64. *out_count = how many were written
     * (at most capacity). If after_seq is older than the ring, the oldest kept events are returned. */
    KilnResult(KILN_CALL* events)(uint64_t after_seq, KilnPlayerEvent* out, uint32_t capacity, uint32_t* out_count);
    uint64_t(KILN_CALL* last_seq)(void);
    KilnResult(KILN_CALL* subscribe)(KilnOwner owner, KilnPlayerEventFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);

    /* Tick thread: one record + BasicPlayerInfos entry, raw. KILN_ERR_INVALID_ARGUMENT for slot >= status.slots,
     * KILN_ERR_UNAVAILABLE without records. */
    KilnResult(KILN_CALL* raw_slot)(uint32_t slot, KilnPlayerSlotRaw* out);

    /* Testing: runs a fresh log tracker (the same code the service uses on the live log) over `lines` (one game log
     * message each; a message may hold several lines separated by '\n'; a leading "[I hh:mm:ss,mmm] " file prefix is
     * accepted) and returns the players it ends with (any state, slot order) and the events it emitted. No game state
     * is touched. Any thread. */
    KilnResult(KILN_CALL* replay_log)(const KilnStr* lines, uint32_t count, KilnPlayer* out, uint32_t capacity,
                                      uint32_t* out_count, KilnPlayerEvent* events, uint32_t events_capacity,
                                      uint32_t* out_events);
    /* Diagnostics, tick thread: the position path used for players (GetEntityTransform on the captured server world)
     * applied to any entity, so it can be checked on a server with no players. KILN_ERR_NOT_FOUND: no such entity. */
    KilnResult(KILN_CALL* probe_position)(KilnEntity entity, KilnVec3* out);
} KilnPlayersApi;

#ifdef __cplusplus
}
#endif
#endif
