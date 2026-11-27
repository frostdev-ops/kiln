/* SPDX-License-Identifier: MIT
 * kiln.labbot v1 (since 0.2.0-dev.32): bot players on dedicated LAB servers, for tests that need a real player in the
 * world (an owner for pets, a tamer, a rider) and no game client.
 *
 * What a bot is: a player the dedicated server itself hosts. The server's own session publishes its machines and players
 * to the GameServer in a double-buffered state; kiln.labbot adds the bot's machine and player to every published
 * buffer, and then sends, inside the GameServer's receive step, what a client would: the Login message (a name and a
 * CharacterId), the character upload of a new character, SystemsReady and GameplayReady, and acknowledgements of the
 * character saves the server sends back. The game's own code does the rest: the player record, the spawn position
 * search, the player entity from the game's player template, user-group rights, saves, logout and removal. So a bot is
 * a real player of the game (Players, CharacterData, chat commands, kiln.net's player records all see it), with these
 * differences from a joined client: it has no network connection (what the server sends it goes nowhere), it never
 * acknowledges replication, and it moves only when something moves it (kiln.world teleport). Engine facts:
 * docs/re/labbot.md. Lab scripts: tools/labbot (docs: tools/lab/README.md).
 *
 * Lab servers only: every member but status answers KILN_ERR_UNAVAILABLE unless kiln.json's policy is "any" AND
 * kiln.net's lab transport is open (labPort in kiln/config/network.json), the same rule as kiln.repl's loopback harness.
 * Never on a public server: no Steam account, ticket or password is used or needed, so nothing here may run where real
 * players join.
 *
 * Identity: a bot's CharacterId and account id are derived from its name (KilnLabBotJoin.character_id and .account
 * zero), so a bot that joins under the same name in a later server session is the same character on that world
 * (per-character data such as pets carries over), and two names never share one. Within one server session a character
 * logs in once: the game does not spawn it again after its logout until the server restarts, so join refuses it.
 *
 * Threads: join and leave on the tick thread (they queue; the bot moves inside the next GameServer receive step).
 * status, info and list from any thread (a snapshot of the last step). */
#ifndef KILN_LABBOT_H
#define KILN_LABBOT_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_LABBOT_SERVICE "kiln.labbot"
#define KILN_LABBOT_VERSION 1u
#define KILN_LABBOT_MAX 4u /* bots at once */

/* KilnLabBotInfo.phase */
enum {
    KILN_LABBOT_FREE = 0,
    KILN_LABBOT_CONNECTING = 1, /* listed in the session's published state; the login follows */
    KILN_LABBOT_LOGGING_IN = 2, /* the login made the player record; the character upload follows */
    KILN_LABBOT_LOADING = 3,    /* the spawn position search; then SystemsReady and GameplayReady */
    KILN_LABBOT_SPAWNING = 4,   /* the record is in game; the GameServer spawns the player entity */
    KILN_LABBOT_IN_GAME = 5,    /* the player entity exists: entity != 0 */
    KILN_LABBOT_LEAVING = 6,    /* logout: the final save, then the game removes the entity and the record */
    KILN_LABBOT_LEFT = 7,       /* gone (reason says how); the id stays readable until its slot is reused */
    KILN_LABBOT_FAILED = 8,     /* the join failed (reason says where) */
};

/* KilnLabBotJoin.group: which user group of enshrouded_server.json the bot is in (its rights, as a password would pick) */
enum {
    KILN_LABBOT_GROUP_ADMIN = 0,  /* the group with the most rights (canKickBan first): Kiln's Admin commands work */
    KILN_LABBOT_GROUP_LEAST = 1,  /* the group with the fewest rights */
    KILN_LABBOT_GROUP_INDEX = 2,  /* group_index: the n-th entry of userGroups */
};

/* KilnLabBotJoin.flags */
enum {
    KILN_LABBOT_JOIN_KILN_CLIENT = 0x01u, /* the bot counts as a client with Kiln for kiln.net's gate (its Gameplay Ready
                                             is not held for lack of a Kiln frame); a lab peer bound to its slot
                                             (kiln_net.h KILN_NET_SELFTEST_BIND) then speaks Kiln for it */
};

/* leave flags */
enum {
    KILN_LABBOT_LEAVE_DROP = 0x01u, /* as a lost connection: no logout, the game resets the record (no final save) */
};

typedef struct KilnLabBotJoin {
    uint32_t size;            /* sizeof(KilnLabBotJoin) */
    uint32_t flags;           /* KILN_LABBOT_JOIN_* */
    KilnStr name;             /* 1..32 bytes of [A-Za-z0-9 _-.]: the platform name the game, chat and logs show */
    uint8_t character_id[16]; /* zero: derived from name */
    uint64_t account;         /* zero: derived from name */
    uint32_t group;           /* KILN_LABBOT_GROUP_* */
    uint32_t group_index;     /* KILN_LABBOT_GROUP_INDEX: index into the server's userGroups */
} KilnLabBotJoin;

typedef struct KilnLabBotInfo {
    uint32_t size;            /* set by the caller: sizeof(KilnLabBotInfo) */
    uint32_t id;              /* 1.. (never 0); a slot's next bot gets a new id */
    uint32_t phase;           /* KILN_LABBOT_* */
    uint32_t slot;            /* player record slot 0..15 (the entity is slot + 1 on this build) */
    uint32_t machine;         /* the bot's machine handle (what kiln.net calls its peer) */
    uint32_t player;          /* its player (connection) handle: kiln.chat's inject takes it */
    KilnEntity entity;        /* the player entity; 0 until in game */
    uint32_t record_state;    /* the game's record state: 0 free, 1 loading, 2 loaded, 3 in game, 4/5 removing, 6 failed */
    uint32_t permissions;     /* the record's user-group rights (KILN_CHAT_PERM_* bits) */
    uint32_t group;           /* the user-group id the bot joined with */
    uint32_t saves_acked;     /* character saves the bot acknowledged */
    uint64_t account;
    uint8_t character_id[16];
    uint64_t joined_step;     /* server step of the join */
    uint64_t in_game_step;    /* step the entity appeared (0: not yet) */
    char name[40];            /* NUL-terminated */
    char reason[200];         /* the last event; FAILED and LEFT: why */
} KilnLabBotInfo;

typedef struct KilnLabBotStatus {
    uint32_t size;            /* set by the caller: sizeof(KilnLabBotStatus) */
    uint32_t available;       /* 1: bots may join here */
    uint32_t bots;            /* bots not FREE/LEFT/FAILED */
    uint32_t groups;          /* user groups the server has */
    uint64_t publishes;       /* published session states the bots were added to */
    char why[200];            /* available 0: why not */
} KilnLabBotStatus;

typedef struct KilnLabBotApi {
    KilnServiceHeader header; /* { sizeof(KilnLabBotApi), KILN_LABBOT_VERSION } */
    /* Any thread. KILN_OK also when bots are unavailable (out->available 0, out->why). */
    KilnResult(KILN_CALL* status)(KilnLabBotStatus* out);
    /* Starts a bot's join; *out_id names it. Tick thread. KILN_ERR_INVALID_ARGUMENT: a bad name, flags or group, a
     * name a live bot has, or a character or account that logged in on this server before (kiln.log says which);
     * KILN_ERR_NOT_FOUND: KILN_LABBOT_MAX bots are live, no free player slot or machine, or no
     * such user group; KILN_ERR_UNAVAILABLE: not a lab server, the symbols did not resolve, or no server world runs. The join then goes on by itself; info() shows where it is. */
    KilnResult(KILN_CALL* join)(KilnOwner owner, const KilnLabBotJoin* request, uint32_t* out_id);
    /* Ends the bot's game (KILN_LABBOT_LEAVE_*): a logout unless DROP. Tick thread. KILN_ERR_NOT_FOUND: no such live bot. */
    KilnResult(KILN_CALL* leave)(uint32_t id, uint32_t flags);
    /* Any thread. KILN_ERR_NOT_FOUND: no bot of that id is remembered. */
    KilnResult(KILN_CALL* info)(uint32_t id, KilnLabBotInfo* out);
    /* Every remembered bot (live ones and the last LEFT/FAILED of each slot), newest first; *count = how many there
     * are, also beyond cap. Any thread. */
    KilnResult(KILN_CALL* list)(KilnLabBotInfo* out, uint32_t cap, uint32_t* count);
} KilnLabBotApi;

#ifdef __cplusplus
}
#endif
#ifdef __cplusplus
static_assert(sizeof(KilnLabBotJoin) == 56 && offsetof(KilnLabBotJoin, name) == 8 &&
                  offsetof(KilnLabBotJoin, character_id) == 24 && offsetof(KilnLabBotJoin, account) == 40 &&
                  offsetof(KilnLabBotJoin, group) == 48,
              "KilnLabBotJoin layout");
static_assert(sizeof(KilnLabBotInfo) == 328 && offsetof(KilnLabBotInfo, account) == 48 &&
                  offsetof(KilnLabBotInfo, character_id) == 56 && offsetof(KilnLabBotInfo, joined_step) == 72 &&
                  offsetof(KilnLabBotInfo, name) == 88 && offsetof(KilnLabBotInfo, reason) == 128,
              "KilnLabBotInfo layout");
static_assert(sizeof(KilnLabBotStatus) == 224 && offsetof(KilnLabBotStatus, publishes) == 16 &&
                  offsetof(KilnLabBotStatus, why) == 24,
              "KilnLabBotStatus layout");
static_assert(sizeof(KilnLabBotApi) == 48, "KilnLabBotApi is 48 bytes");
#else
_Static_assert(sizeof(KilnLabBotJoin) == 56, "KilnLabBotJoin layout");
_Static_assert(sizeof(KilnLabBotInfo) == 328, "KilnLabBotInfo layout");
_Static_assert(sizeof(KilnLabBotStatus) == 224, "KilnLabBotStatus layout");
_Static_assert(sizeof(KilnLabBotApi) == 48, "KilnLabBotApi is 48 bytes");
#endif
#endif
