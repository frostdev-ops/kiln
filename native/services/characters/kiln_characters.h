/* SPDX-License-Identifier: MIT
 * kiln.characters v1: the game's CharacterId of each connected player.
 *
 * A character has a GUID of its own (keen::CharacterId): the header of characterN.ksd, block character_data, the world's
 * known_users, and GameMessageLoginRequest.characterId, which the server keeps in the player record. Evidence and limits:
 * docs/re/characters.md. Usage: docs/kiln/services/character-data.md.
 *
 * Threads: every function works from any thread and reads a snapshot of the 16 records taken at the last server-world
 * step (Pre). A snapshot entry stays after the player left, until another connection takes the slot, so a "left" event
 * can still name the character.
 */
#ifndef KILN_CHARACTERS_H
#define KILN_CHARACTERS_H
#include "kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_CHARACTERS_SERVICE "kiln.characters"
#define KILN_CHARACTERS_VERSION 1u

/* KilnCharacterInfo.flags */
enum {
    KILN_CHARACTER_LIVE = 0x01u,      /* the record holds this connection now (not only remembered) */
    KILN_CHARACTER_SIMULATED = 0x02u, /* lab simulation, see simulate() */
};

typedef struct KilnCharacterInfo {
    uint32_t size;    /* set to sizeof(KilnCharacterInfo) before calling */
    uint32_t slot;    /* the game's player slot, 0..15 */
    uint32_t handle;  /* the connection handle (slot | generation << 6); 0 for a simulated entry */
    uint32_t flags;   /* KILN_CHARACTER_* */
    uint64_t account; /* record +0x48: the account key the world's known_users uses */
    uint8_t id[16];   /* the CharacterId, in the save's byte order (.NET: new Guid(bytes)); all zero: unknown */
} KilnCharacterInfo;

typedef struct KilnCharactersStatus {
    uint32_t size;      /* set to sizeof(KilnCharactersStatus) before calling */
    uint32_t resolved;  /* 1: the record offset was found on this build */
    uint32_t records;   /* 1: kiln.players gave the records at the last scan */
    uint32_t simulated; /* 1: lab simulation on (KILN_CHARACTERS_SIMULATE) */
    uint64_t scans;     /* scans done */
    uint32_t known;     /* slots with a CharacterId in the snapshot */
    uint32_t offset;    /* the record offset of the CharacterId (0x50 on 1287369) */
} KilnCharactersStatus;

typedef struct KilnCharactersApi {
    KilnServiceHeader header; /* { sizeof(KilnCharactersApi), KILN_CHARACTERS_VERSION } */
    KilnResult(KILN_CALL* status)(KilnCharactersStatus* out);
    /* By connection handle. KILN_ERR_NOT_FOUND: no entry with this handle (never seen, or the slot changed hands). */
    KilnResult(KILN_CALL* by_handle)(uint32_t handle, KilnCharacterInfo* out);
    /* By slot: whatever the slot holds or held last. */
    KilnResult(KILN_CALL* by_slot)(uint32_t slot, KilnCharacterInfo* out);
    /* Lab only (KILN_CHARACTERS_SIMULATE=1, else KILN_ERR_UNAVAILABLE): pretends a slot holds this character, for players
     * replayed by KILN_PLAYERS_SIMULATE, which have no record. id NULL clears it. */
    KilnResult(KILN_CALL* simulate)(uint32_t slot, uint64_t account, const uint8_t* id);
} KilnCharactersApi;

#ifdef __cplusplus
}
#endif
#endif
