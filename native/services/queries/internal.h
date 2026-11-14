// SPDX-License-Identifier: MIT
// What the two halves of kiln.queries share (queries.cpp: the ray, entities and the service; path.cpp: paths).
#ifndef KILN_QUERIES_INTERNAL_H
#define KILN_QUERIES_INTERNAL_H
#include <atomic>

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
