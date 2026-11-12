/* SPDX-License-Identifier: MIT
 * kiln.steamguard v1: the achievement guard (game client only). While it is on, the game's own call that reports an
 * achievement to Steam is dropped, so a character or world changed by a test or an admin command cannot unlock
 * achievements on the player's Steam account. Native side: native/services/steamguard (sigdb
 * client.d/steamguard.json, evidence in native/services/steamguard/README.md). Managed face:
 * managed/services/Kiln.Client/SteamGuard.cs (`Kiln.SteamGuard`). Capability and sigdb feature:
 * `steam.achievement_guard`.
 *
 * States. The target (the game's function) is resolved and checked at boot: `TARGET_VERIFIED`. The detour is put in
 * through kiln.hooks on the first `latch` or `hold`, never before: a game where nobody asks for the guard runs without
 * it. `HOOK_INSTALLED` means kiln.hooks reports the link in place on that target. `ACTIVE` means a latch or at least
 * one hold is on; then every achievement report is dropped (`dropped` counts it, one log line names the id). Installed
 * and not active, a report goes to the game's code as before (`passed`).
 *
 * There is no "off" call. A latch lasts until the game exits; a hold lasts until its own token is released (two holds
 * need two releases). A failure is sticky: once the target fails a check or the install fails, the guard stays "not
 * protecting" for the rest of the process and `reason` says why.
 *
 * Every doubt reads as "not protecting": a caller treats the guard as protecting only when `status` returns KILN_OK
 * with `size` covering `flags`, and TARGET_VERIFIED, HOOK_INSTALLED and ACTIVE are all set.
 *
 * On a dedicated server the service is not registered (no Steam user there); it logs that once. A joined player's
 * guard is the one in that player's own game client: a server can only ask it to latch (Kiln.Network's relay), and a
 * client without Kiln cannot be protected at all.
 *
 * Capability rows: `steam.achievement_guard` (the sigdb feature, static: unavailable when the symbol does not resolve
 * or the second sign fails, never pending), `kiln.steamguard` (this service), and `steamguard.protecting` (depends on
 * the session: pending until the first latch or hold, available while protecting, unavailable with reason faulted when
 * the install failed or another hook is on the target). */
#ifndef KILN_STEAMGUARD_H
#define KILN_STEAMGUARD_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_STEAMGUARD_SERVICE "kiln.steamguard"
#define KILN_STEAMGUARD_VERSION 1u
#define KILN_STEAMGUARD_CAPABILITY "steam.achievement_guard"

/* KilnSteamGuardStatus.flags */
enum {
    KILN_STEAMGUARD_TARGET_VERIFIED = 1u << 0, /* the symbol resolved once, its bytes and its second sign hold now */
    KILN_STEAMGUARD_HOOK_INSTALLED = 1u << 1,  /* kiln.hooks reports the guard's link in place on the target */
    KILN_STEAMGUARD_ACTIVE = 1u << 2,          /* a latch or at least one hold is on */
    KILN_STEAMGUARD_LATCHED = 1u << 3,         /* a latch was taken (on until the game exits) */
    KILN_STEAMGUARD_FAILED = 1u << 4,          /* a check or the install failed: not protecting until the game exits */
};

#define KILN_STEAMGUARD_REASON_MAX 192u

typedef struct KilnSteamGuardStatus {
    uint32_t size;     /* set by the caller: sizeof(KilnSteamGuardStatus). The service fills only that far. */
    uint32_t flags;    /* KILN_STEAMGUARD_* */
    uint32_t holds;    /* holds taken and not released yet */
    uint32_t reserved;
    uint64_t dropped;  /* achievement reports dropped while active */
    uint64_t passed;   /* achievement reports passed to the game while installed and not active */
    /* UTF-8, NUL-terminated: why the guard is not protecting, or what keeps it on. Valid only when `size` covers the
     * whole array. */
    char reason[KILN_STEAMGUARD_REASON_MAX];
} KilnSteamGuardStatus;

typedef struct KilnSteamGuardApi {
    KilnServiceHeader header; /* { sizeof(KilnSteamGuardApi), KILN_STEAMGUARD_VERSION } */
    /* The guard's state. Any thread. KILN_ERR_INVALID_ARGUMENT: out NULL or out->size smaller than `flags` needs. */
    KilnResult(KILN_CALL* status)(KilnSteamGuardStatus* out);
    /* Turns the guard on until the game exits (the first call puts the detour in). `reason` (UTF-8, not empty) goes into
     * the log. KILN_OK only when the guard protects after the call; KILN_ERR_UNAVAILABLE otherwise (target not verified,
     * install failed, foreign hook: see status.reason). Any thread. */
    KilnResult(KILN_CALL* latch)(KilnStr reason);
    /* Turns the guard on while the returned token is held (the first call puts the detour in). KILN_OK and *out_token
     * (never 0) only when the guard protects after the call; on any other result *out_token is 0 and nothing is held.
     * Any thread. */
    KilnResult(KILN_CALL* hold)(KilnStr reason, uint64_t* out_token);
    /* Releases one hold. KILN_ERR_NOT_FOUND: the token is unknown or was released already. The detour stays in. */
    KilnResult(KILN_CALL* release)(uint64_t token);
} KilnSteamGuardApi;

#ifdef __cplusplus
}
#endif
#endif /* KILN_STEAMGUARD_H */
