/* SPDX-License-Identifier: MIT
 * kiln.questmark v1: writes a player's waypoint (keen::ecs::PlayerWaypoint {enabled +0, waypointPosition +8}, 32 B, the
 * component the game's player_waypoint_selection writes from the player's own WaypointAction) from a server program, so
 * it replicates like the game's write. Kiln.Quests points the active quest step at its marker with it; docs/re/quests.md
 * section 6, docs/kiln/services/quests.md. Whether a vanilla client draws it is client check C1.
 * Published by kiln-questmark.dll. Functions: any thread except where noted. */
#ifndef KILN_QUESTMARK_H
#define KILN_QUESTMARK_H

#include "kiln_api.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KILN_QUESTMARK_SERVICE "kiln.questmark"
#define KILN_QUESTMARK_VERSION 1u

typedef struct KilnQuestmarkApi {
    KilnServiceHeader header; /* { sizeof(KilnQuestmarkApi), KILN_QUESTMARK_VERSION } */
    /* Queues a write for the next step. Tick thread (it checks the component). KILN_ERR_NOT_FOUND: the entity has no
     * PlayerWaypoint. KILN_ERR_UNAVAILABLE: the program is not running, or the component's size is not 32 B on this
     * build. */
    KilnResult(KILN_CALL* set)(KilnEntity player, uint8_t enabled, double x, double y, double z);
    /* Requests queued so far; writes done so far. */
    KilnResult(KILN_CALL* counters)(uint64_t* requests, uint64_t* written);
    /* 1 when the program is registered and running in this world. */
    uint32_t(KILN_CALL* active)(void);
} KilnQuestmarkApi;

#ifdef __cplusplus
}
#endif
#endif
