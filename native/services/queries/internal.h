// SPDX-License-Identifier: MIT
// What the two halves of kiln.queries share (queries.cpp: the ray, entities and the service; path.cpp: paths).
#ifndef KILN_QUERIES_INTERNAL_H
#define KILN_QUERIES_INTERNAL_H
#include <atomic>
#include <cstddef>

#include "kiln_api.h"
#include "services/kiln_caps.h"
#include "services/kiln_queries.h"
#include "services/kiln_world.h"

#include "grid.h"

namespace kq {

extern const KilnApi* g_api;
extern KilnOwner g_owner;
extern const KilnWorldApi* g_world;
extern const KilnCapsApi* g_caps;
extern GridLayout g_layout;          // the voxel grid's layout, when g_grid_ok
extern std::atomic<bool> g_grid_ok;  // the grid layout resolved and nothing showed it wrong

void logf(int32_t level, const char* fmt, ...);

// kiln.world's in_program (appended to its table since 0.2.0-dev.28): this thread runs a Kiln world program's update,
// a wrap stage on a game program's slice or a kiln.types system_hook callback. The world is mid-step there, also on its
// own thread, where engine jobs may use the realm's voxel scratch: a ray and a path submit refuse KILN_ERR_WRONG_THREAD,
// reason "InsideProgram". A kiln.world without the member (an older one, or none yet) answers false: the calls behave
// as before it existed.
inline bool in_program() {
    const KilnWorldApi* w = g_world;
    return w && w->header.size >= offsetof(KilnWorldApi, in_program) + sizeof w->in_program && w->in_program &&
           w->in_program() != 0;
}
inline constexpr char kInsideProgram[] = "InsideProgram";

struct Clock {
    uint32_t session = 0;
    uint64_t step = 0;
    bool world = false;
};
Clock now();
bool world_ready(const Clock& c);  // tick thread

// path.cpp
void path_resolve();                       // kiln_plugin_main: sigdb
void path_ready();                         // on_ready: the world program
void path_world_closed(uint32_t session);  // any thread
KilnResult KILN_CALL t_path_submit(KilnOwner owner, const KilnQueryPathRequest* request, KilnQueryPathAdmission* out);
KilnResult KILN_CALL t_path_cancel(KilnOwner owner, uint64_t request);
KilnResult KILN_CALL t_path_poll(KilnOwner owner, KilnQueryPathUpdate* update, KilnQueryPathPoint* points, uint32_t capacity, uint32_t* needed);
KilnResult KILN_CALL t_path_retire(KilnOwner owner);

}  // namespace kq
#endif
