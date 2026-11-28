// SPDX-License-Identifier: MIT
// kiln.markers' admission (chapter 04 §4.5 "Selection", §4.6 reason precedence): which surfaces of which rows are
// admitted at one client Post, and why the others are not. Pure: the surfaces' glue fills a View and the candidates
// (positions already resolved: a followed row's current position from the anchors, a received row's latest pose).
//
// Per requested surface the first reason that applies wins: NO_WORLD, CAPABILITY_OFF, ANCHOR_ENDED, FOLLOW_MISSING or
// SOURCE_STALE, BAD_POSITION, NO_CAMERA (World and Compass always need a camera and a viewport; Map only with a finite
// max_distance), OUT_OF_RANGE, BEHIND (World only), then the surface's room (MAP_CAPACITY when the game's list has no
// room, SURFACE_BUDGET when Kiln's own budget is spent), else READY. Eligible rows are ordered exactly: priority
// descending, squared 3D camera distance ascending (infinite without a camera), source (local before received),
// namespace (byte order), publisher generation ascending, key (byte order). No insertion-order tie.
//
// That order decides which rows a surface takes. The compass and world surfaces draw them in it; the map writes the rows
// it took in slot order instead: source, namespace, generation, key (no priority, no distance). The game's marker layer
// keeps each list slot's type and icon until a list count changes (docs/re/map.md, "The tooltip's name"), so a row that
// changed slots as the camera or a followed marker moved would show another row's icon (and, with
// MapLabel.NativeTooltip, a tooltip that disagrees with the icon under the cursor).
#ifndef KILN_MARKERS_SELECT_H
#define KILN_MARKERS_SELECT_H
#include <cstdint>
#include <string_view>
#include <vector>

#include "services/kiln_markers.h"

namespace kmk {

constexpr uint32_t kMapBudget = 128;     // mod rows added to the game's map list per step
constexpr uint32_t kWorldBudget = 128;   // drawn world labels per frame
constexpr uint32_t kCompassBudget = 64;  // drawn compass entries per frame

enum Surface : uint32_t { kSurfMap = 0, kSurfCompass = 1, kSurfWorld = 2 };
constexpr uint32_t kSurfaceBit[3] = {KILN_MARKER_MAP, KILN_MARKER_COMPASS, KILN_MARKER_WORLD};

// What admission knows about this client at one Post.
struct View {
    bool world_active = false;       // a client world (its epoch is nonzero)
    bool cap[3] = {false, false, false};  // markers.map, markers.compass, markers.world are on now
    bool camera = false;             // a fresh camera (kiln.render)
    double cam[3] = {0, 0, 0};       // its position, metres
    double fwd[3] = {0, 0, 1};       // its unit view direction
    double near_plane = 0.1;         // metres
    bool viewport = false;           // a nonzero swapchain extent
    uint32_t map_room = 0;           // free rows in the game's map list now (capacity - count); 0 without a usable list
};

struct Candidate {
    uint32_t requested = 0;          // KILN_MARKER_MAP | _COMPASS | _WORLD
    uint32_t source_rank = 0;        // 0 local, 1 received
    int32_t priority = 0;
    std::string_view ns, key;
    uint64_t publisher_generation = 0;
    uint32_t source_state = KILN_MARKER_READY;  // READY, ANCHOR_ENDED, FOLLOW_MISSING, SOURCE_STALE or BAD_POSITION
    double x = 0, y = 0, z = 0;      // the point now (metres)
    float height = 0;                // a world label's height above the point
    float max_distance = 0;          // 0 = unlimited
};

struct Admitted {
    uint32_t after = 0;              // the admitted surfaces
    uint32_t reason[3] = {KILN_MARKER_NOT_REQUESTED, KILN_MARKER_NOT_REQUESTED, KILN_MARKER_NOT_REQUESTED};
    double d2 = 0;                   // squared camera distance (infinity without a camera)
};

// A point inside the world (chapter 04 §4.2 bounds): x and z in [-1000, 12000], y in [-1000, 5000], finite.
bool position_valid(double x, double y, double z);

// Fills out (one per candidate, same order). order[s] receives the indexes admitted on surface s: best first for the drawn
// surfaces, in slot order for the map (the order its rows go into the game's list).
void admit(const View& view, const std::vector<Candidate>& rows, std::vector<Admitted>* out, std::vector<uint32_t> order[3]);

// The camera's heading in radians from its view direction: atan2(forward.x, forward.z), north (+Z) = 0, east (+X) =
// pi/2. When the horizontal part is shorter than 1e-8 the previous heading is kept (initially 0).
double heading_of(const double fwd[3], double previous);
// Forward = the camera's rotation applied to +Z (quaternion x, y, z, w); normalized first. False for a zero quaternion.
bool forward_of(const float q[4], double out[3]);

}  // namespace kmk
#endif
