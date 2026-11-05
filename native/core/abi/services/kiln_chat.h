/* SPDX-License-Identifier: MIT
 * kiln.chat v1: the server's text chat. Receives players' lines (with the option to drop them before anyone sees
 * them) and adds server lines for everyone or for one player.
 *
 * How lines travel (docs/re/chat.md): a client sends ChatMessageClientMessage; the server's receive loop adds it to
 * the ChatMessagesSystem inbox (ChatLogAdd); the GameServer's send step sends each inbox line to every player reader
 * whose bit in the line's mask is clear (ChatMessageServerMessage), including the sender, whose own line only appears
 * through that echo. A client shows a line as "<speaker name>: <text>", where the speaker is the connection handle and
 * account stored with the line; a line with no speaker is sent but not shown. Server lines are therefore spoken by the
 * player who receives them and narrowed to that player's reader ("<their name>: [Server] text").
 * Requires "enableTextChat": true in enshrouded_server.json for clients to send and show text chat. */
#ifndef KILN_CHAT_H
#define KILN_CHAT_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_CHAT_SERVICE "kiln.chat"
#define KILN_CHAT_VERSION 1u
#define KILN_CHAT_MAX_TEXT 1024u /* bytes (UTF-8) per line; longer text is cut at a character boundary */

/* KilnChatPlayer.permissions: the user group (password) the player joined with, as the server enforces it. */
enum {
    KILN_CHAT_PERM_KICK_BAN = 1u << 0,
    KILN_CHAT_PERM_ACCESS_INVENTORIES = 1u << 1,
    KILN_CHAT_PERM_EDIT_BASE = 1u << 2,
    KILN_CHAT_PERM_EDIT_WORLD = 1u << 3,
    KILN_CHAT_PERM_EXTEND_BASE = 1u << 4,
    KILN_CHAT_PERM_RECEIVE_EXP = 1u << 5,
};

/* A connected player as the chat sees them (the server's connection record). */
typedef struct KilnChatPlayer {
    uint32_t size;          /* sizeof(KilnChatPlayer) */
    KilnEntity entity;      /* player entity (1..1023); 0 = not spawned yet / unknown */
    uint32_t connection;    /* connection handle; every chat line carries its speaker's */
    uint32_t reader;        /* chat reader slot (peer handle & 0x7f) */
    uint64_t account;       /* account id hash */
    uint32_t permissions;   /* KILN_CHAT_PERM_* */
    uint32_t state;         /* connection state byte (raw) */
    char name[64];          /* platform name the chat shows as speaker, UTF-8, NUL-terminated (cut to 63 bytes) */
} KilnChatPlayer;

/* A line a player sent. Valid only during the callback. */
typedef struct KilnChatMessage {
    uint32_t size;          /* sizeof(KilnChatMessage) */
    uint32_t type;          /* game line type; 0 = typed text (the only type delivered in v1) */
    uint64_t id;            /* running number since boot */
    KilnChatPlayer sender;  /* sender.entity == 0 when the connection record was not found */
    KilnStr text;           /* UTF-8 as the client sent it (at most KILN_CHAT_MAX_TEXT bytes) */
} KilnChatMessage;

/* Runs on the tick thread in Pre of the sim step that follows the line's arrival (the same server frame). Return
 * true to consume the line: it is not added to the chat and nobody, not even the sender, sees it. */
typedef bool(KILN_CALL* KilnChatReceiveFn)(const KilnChatMessage* message, void* user);

typedef struct KilnChatStatus {
    uint32_t size;
    uint8_t receive_hooked;     /* players' lines can be observed and consumed */
    uint8_t send_ready;         /* the game server and its chat system have been seen */
    uint8_t reserved[2];
    uint32_t players;           /* connected players (records with a connection) */
    uint32_t subscribers;
    uint64_t received;          /* typed lines received from players */
    uint64_t consumed;          /* ... of which consumed by a subscriber */
    uint64_t sent;              /* server lines added (one per recipient) */
    uint64_t inbox_count;       /* lines held in the chat system's inbox right now */
} KilnChatStatus;

/* The newest line in the chat system's inbox (diagnostics and tests). */
typedef struct KilnChatLineInfo {
    uint32_t size;
    uint32_t type;
    uint32_t connection;    /* speaker connection handle */
    uint32_t reader_mask;   /* bit r set = reader r will not be sent this line (done, or not addressed) */
    uint64_t account;
    uint32_t text_len;      /* full length; the copy is cut to the buffer */
    uint32_t reserved;
} KilnChatLineInfo;

typedef struct KilnChatApi {
    KilnServiceHeader header; /* { sizeof(KilnChatApi), KILN_CHAT_VERSION } */
    /* Players' typed lines. Any thread; delivery on the tick thread. */
    KilnResult(KILN_CALL* subscribe)(KilnOwner owner, KilnChatReceiveFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);
    /* Server line to one player (to = entity) or to every connected player (to = 0). Each recipient sees it as their
     * own line, so put a marker such as "[Server] " in the text. `delivered` (optional) = lines addressed to a
     * reader. Tick thread only. KILN_ERR_NOT_FOUND: `to` is not a connected player. */
    KilnResult(KILN_CALL* send)(KilnEntity to, KilnStr text, uint32_t* delivered);
    /* A line spoken by player `speaker`, shown to everyone (to = 0) or to one player. Tick thread only. */
    KilnResult(KILN_CALL* say)(KilnEntity speaker, KilnStr text, KilnEntity to);
    /* Connected players. Any thread (a snapshot read without locks; tick thread gives a consistent one).
     * `count` gets the total even when it exceeds `capacity`. */
    KilnResult(KILN_CALL* players)(KilnChatPlayer* out, uint32_t capacity, uint32_t* count);
    KilnResult(KILN_CALL* find_player)(KilnEntity entity, KilnChatPlayer* out);
    KilnResult(KILN_CALL* status)(KilnChatStatus* out);
    /* Tests and tools: queue `text` as if the player with connection handle `connection` (0 = nobody) had sent it.
     * It goes through subscribers and, unless consumed, into the chat like a real line. Any thread. */
    KilnResult(KILN_CALL* inject)(uint32_t connection, KilnStr text);
    /* Newest inbox line: info and a copy of its text (NUL-terminated, cut to `capacity`). Tick thread only. */
    KilnResult(KILN_CALL* last_line)(KilnChatLineInfo* info, char* text, size_t capacity);
} KilnChatApi;

#ifdef __cplusplus
}
#endif
#endif
