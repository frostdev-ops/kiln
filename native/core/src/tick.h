// SPDX-License-Identifier: MIT
// World ticks: a detour on the engine's sim step, which every world (server, prediction, client) goes through.
//   Server world (kind 0; the dedicated server's, or the host's own in a client):
//     Pre : drain posted tasks, run Pre subscribers. No job of the world is running.
//     step: the original sim step.
//     Post: wait for the world's jobs (the server's step returns with grid/range-query jobs still running), then run
//           Post subscribers. Skipped entirely when nobody subscribed to Post.
//   Client and prediction worlds (kinds 2 and 1, client only, the game's main thread): the same Pre / step / Post,
//     with their own subscribers (subscribe_world) and task queues; their steps finish their own jobs.
// World lifecycle: a world "starts" at its first step (a new world pointer of that kind) and "stops" when the game
// destroys it (hook on WorldDestroy, feature "world.lifecycle"). Leaving a hosted game and hosting another gives the
// same kind a new world: services see a new session number and a new world() pointer.
#pragma once
#include "common.h"

namespace kiln::tick {

bool install();  // needs feature "tick"; "tick.wait_jobs" for a safe Post; "world.lifecycle" for stops
bool installed();
bool lifecycle();         // world stops are seen
bool on_tick_thread();    // the server world's thread
bool on_world_thread(uint32_t kind);
KilnResult last(KilnTickInfo* out);  // last server-world step
void write_stats_if_due();  // called from Post on the tick thread

// Client-side world kinds (1, 2) and world events. Handles carry kHandleTag so KilnApi.unsubscribe can route them.
inline constexpr KilnHandle kHandleTag = 1ull << 62;
KilnResult subscribe_world_tick(KilnOwner owner, uint32_t kind, int32_t phase, KilnTickFn fn, void* user,
                                KilnHandle* out);
KilnResult post_world(KilnOwner owner, uint32_t kind, KilnTaskFn fn, void* user);
KilnResult subscribe_world(KilnOwner owner, KilnWorldEventFn fn, void* user, KilnHandle* out);
KilnResult remove(KilnHandle handle);
KilnResult session(KilnSessionInfo* out);

}  // namespace kiln::tick
