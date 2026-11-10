/* SPDX-License-Identifier: MIT
 * kiln.progress v1: a small native window for work that runs before the game's own window exists, so that a start
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
 * Getting the table: api->service({"kiln.progress", 13}, KILN_PROGRESS_VERSION) works in every mode the early
 * step runs in (the core answers it like kiln.profile, in safe idle too). A null table means an older kiln-core.
 * Check header.size >= sizeof(KilnProgressApi) and header.version >= KILN_PROGRESS_VERSION. Any thread.
 * Strings are UTF-8, copied by the call (the core keeps no pointer); text longer than 255 bytes is cut. */
#ifndef KILN_PROGRESS_H
#define KILN_PROGRESS_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_PROGRESS_SERVICE "kiln.progress"
#define KILN_PROGRESS_VERSION 1u

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
} KilnProgressApi;

#ifdef __cplusplus
}
#endif
#endif
