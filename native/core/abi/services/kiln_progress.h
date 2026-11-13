/* SPDX-License-Identifier: MIT
 * kiln.progress v2: a small native window for work that runs before the game's own window exists, so that a start
 * never looks dead. One line of text and a progress bar. Plain Win32 on a thread of its own (kiln-core, no dependency).
 *
 * For the early plugins (native/core/src/early_plugins.cpp): the work of kiln-eml, for one, can take minutes on a big
 * install the first time. Call begin() when it starts, update() as it goes, end() when it is done:
 *  - Nothing is shown for the first moments (KILN_PROGRESS_DELAY_MS, default 2000): work that is over by then never
 *    flashes a window.
 *  - The window does not take the keyboard focus from the game (WS_EX_NOACTIVATE, shown without activation) and is
 *    gone, destroyed on its own thread, when end() returns: end() waits for that, at most a third of a second. Under
 *    gamescope (the Steam Deck's game mode) the window covers the screen with the bar in the middle, since gamescope
 *    scales the one window it shows to the whole display.
 *  - begin(), update() and end() never block the caller beyond that: update() copies a short string under a lock the
 *    window's thread holds for as long as a copy takes.
 *  - There is no window, and no error, when no desktop is there to show it (a dedicated server, a service session,
 *    Wine without a display) and with KILN_UI_DISABLE=1 (the same switch as the overlay's): begin() returns 0, and
 *    update() and end() with 0 do nothing.
 *  - Tasks the caller never ends are ended when the early step is over (the window cannot outlive it).
 *  - Several tasks may be running (two early plugins): the window shows the one that began last and still runs.
 *
 * Getting the table: api->service({"kiln.progress", 13}, 1) works in every mode the early step runs in (the core
 * answers it like kiln.profile, in safe idle too). A null table means an older kiln-core. For begin, update and end
 * check header.size >= offsetof(KilnProgressApi, step) (the v1 table's size); for the v2 calls see below. Any thread.
 * Strings are UTF-8, copied by the call (the core keeps no pointer); text longer than 255 bytes is cut. */
#ifndef KILN_PROGRESS_H
#define KILN_PROGRESS_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_PROGRESS_SERVICE "kiln.progress"
#define KILN_PROGRESS_VERSION 2u /* v2 (0.2.0-dev.27) appends `step` and `mark`: check header.size before using them */

/* kiln.progress `mark` (v2): a fact about the start that kiln-core cannot see itself. */
enum {
    KILN_PROGRESS_MARK_MENU = 1,           /* client: the game's main menu drew a frame (or, a start that goes straight into
                                              a world, the first world frame drew); kiln-ui sends it once */
    KILN_PROGRESS_MARK_SERVER_STARTED = 2, /* server: the world runs with the mods loaded (Kiln.Host's ServerStarted, the
                                              point `kilnctl run --wait` waits for) */
};

typedef struct KilnProgressApi {
    KilnServiceHeader header; /* { sizeof(KilnProgressApi), KILN_PROGRESS_VERSION } */
    /* Starts a task with a title (the window's heading: "Preparing the game data for EML mods"). Returns its id, or 0
     * when there is nothing to show with (see above): then the other calls need not be made. */
    uint64_t(KILN_CALL* begin)(KilnStr title);
    /* The line under the title and the bar: done of total (any unit; total 0 = no total known, the bar moves on its
     * own). Call as often as you like; the window follows at about ten frames a second. */
    void(KILN_CALL* update)(uint64_t task, KilnStr text, uint64_t done, uint64_t total);
    /* The task is over. The window closes when no task is left. */
    void(KILN_CALL* end)(uint64_t task);

    /* ---- v2, since 0.2.0-dev.27: what the start is doing, <game>/kiln/state/progress.json ----
     * Check header.size >= offsetof(KilnProgressApi, mark) + sizeof(void*) before using these: a core built before
     * dev.27 hands out the 32-byte v1 table (ask with min_version 1; v2 is a superset of it).
     * kiln-core is the file's one writer; services, plugins and Kiln.Host report to it through these calls. Not for
     * mods. Any thread; nothing waits for the disk (the core writes on a thread of its own). Strings are UTF-8, copied
     * (255 bytes at most each). Contract: schema/state-progress.schema.json, docs/kiln/launcher/contracts.md.
     *
     * step: the start is in step `id` of the plan (start, native-early, early-plugins, signatures, services, plugins,
     *   native, dotnet, mods, content, game). `id` must be the running step or the next one in the plan, else
     *   KILN_ERR_INVALID_ARGUMENT and nothing changes. The next step ends the running one (its tookMs) and begins.
     *   For the running step: `label` replaces the label (empty: keep it), `detail` replaces the detail (what is being
     *   worked on now, by the name a player knows; empty clears it), `done`/`total` the count (both 0: keep the count;
     *   both are the same unit: services, plugins, mods, blocks). Such a change is written at most every 250 ms (the
     *   last one is never lost). KILN_ERR_UNAVAILABLE once the file's life is over (ready, idle, failed) or before it
     *   began (a client or server whose boot never reached the writer).
     * mark: KILN_PROGRESS_MARK_*. KILN_OK, KILN_ERR_INVALID_ARGUMENT for an unknown value, KILN_ERR_UNAVAILABLE as
     *   above. Call it once: never from every frame. */
    KilnResult(KILN_CALL* step)(KilnStr id, KilnStr label, KilnStr detail, uint64_t done, uint64_t total);
    KilnResult(KILN_CALL* mark)(uint32_t what);
} KilnProgressApi;

#ifdef __cplusplus
}
#endif
#endif
