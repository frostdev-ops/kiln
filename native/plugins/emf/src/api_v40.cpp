// SPDX-License-Identifier: MIT
// The EmfApi table, v28 to v40 (EMF 0.6.3 to 0.6.8): the 45 functions EMF appended after net_peer.
//
// kiln-emf serves the whole v40 table (1088 bytes) so that a mod built against any EMF header up to 0.6.8 starts:
// emf::init in EMF's SDK refuses a host whose table is smaller than the mod's own, whatever the mod then calls.
//
// Carried: chat_tell_ex and chat_broadcast_ex (api_game.cpp: kiln.chat's server lines are already lines without a
// speaker) and world_to_screen (kiln.render's camera, below).
//
// Not carried yet (42 functions): each answers as EMF's own core answers where a feature is off on a game build, so a
// mod meets a state it already has to handle: the documented failure value, out buffers cleared, `why` filled where
// the request has one, a request whose `size` is too small refused as EMF refuses it, and no callback ever called.
// One log line per mod and function says so, and the status file lists the call (state/emf.json "unsupported").
// A function leaves this file when Kiln carries it: docs/kiln/emf-parity.md has the list.
#include <intrin.h>

#include <cstdio>

#include "emf_internal.h"
#include "services/kiln_render.h"
#include "v26_model.h"

namespace kemf {

namespace {

constexpr const char* kNotYet = "not available on Kiln yet";

// In the API function itself (never in a helper): the return address names the calling mod.
#define KEMF_NOT_YET(fn) unsupported(caller_mod(_ReturnAddress()), fn, kNotYet)

void clear(char* buf, uint32_t size) {
    if (buf && size) buf[0] = 0;
}

template <size_t N>
void say_why(char (&why)[N]) {
    std::snprintf(why, N, "%s", kNotYet);
}

// ---------------------------------------------------------------------------------------------- v28 -------------
bool api_on_player(EmfPlayerCallback, void*) {
    KEMF_NOT_YET("on_player");
    return false;
}

// ---------------------------------------------------------------------------------------------- v29 -------------
uint32_t api_item_name(EmfWorld*, uint32_t, uint32_t, char* buf, uint32_t size) {
    KEMF_NOT_YET("item_name");
    clear(buf, size);
    return 0;  // "0 when there is none: use the debug name then"
}

uint32_t api_game_language(char* code_out, uint32_t size) {
    KEMF_NOT_YET("game_language");
    clear(code_out, size);
    return 0;  // "0 on the dedicated server or before the game's text is known"
}

// ---------------------------------------------------------------------------------------------- v30 -------------
bool api_player_character_name(EmfWorld*, uint32_t, char* buf, uint32_t size) {
    KEMF_NOT_YET("player_character_name");
    clear(buf, size);
    return false;  // "false (buf = "") when it is not known"
}

// ---------------------------------------------------------------------------------------------- v31 -------------
bool api_on_hit(EmfHitCallback, void*) {
    KEMF_NOT_YET("on_hit");
    return false;
}

bool api_on_hit_before(EmfHitBeforeCallback, void*) {
    KEMF_NOT_YET("on_hit_before");
    return false;  // "false when the hook is not available on this game build"
}

uint32_t api_hit_rule_add(const EmfHitRule*) {
    KEMF_NOT_YET("hit_rule_add");
    return 0;  // "0 when refused: ... the hook not available on this game build"
}

bool api_hit_rule_remove(uint32_t) {
    KEMF_NOT_YET("hit_rule_remove");
    return false;  // no rule has that id
}

// ---------------------------------------------------------------------------------------------- v32 -------------
uint32_t api_buff_types(EmfWorld*, EmfBuffType*, uint32_t) {
    KEMF_NOT_YET("buff_types");
    return 0;  // "0 when the world's buff registry cannot be read"
}

bool api_buff_type(EmfWorld*, uint32_t, EmfBuffType*) {
    KEMF_NOT_YET("buff_type");
    return false;
}

bool api_buff_text(uint32_t, uint32_t, char* buf, uint32_t cap) {
    KEMF_NOT_YET("buff_text");
    clear(buf, cap);
    return false;
}

uint32_t api_buffs_of(EmfWorld*, uint32_t, EmfBuff*, uint32_t) {
    KEMF_NOT_YET("buffs_of");
    return 0;
}

bool api_buff_apply(EmfWorld*, uint32_t, uint32_t, int64_t, uint32_t, uint32_t, EmfBuffCallback, void*) {
    KEMF_NOT_YET("buff_apply");
    return false;  // refused at once: `done` is not called
}

bool api_buff_remove(EmfWorld*, uint32_t, uint32_t, EmfBuffCallback, void*) {
    KEMF_NOT_YET("buff_remove");
    return false;
}

bool api_on_buff(EmfBuffCallback, void*) {
    KEMF_NOT_YET("on_buff");
    return false;
}

// ---------------------------------------------------------------------------------------------- v33 -------------
bool api_marker_set(const EmfMarker*) {
    KEMF_NOT_YET("marker_set");
    return false;
}

bool api_marker_remove(const char*) {
    KEMF_NOT_YET("marker_remove");
    return false;  // the mod has no such marker
}

uint32_t api_marker_clear() {
    KEMF_NOT_YET("marker_clear");
    return 0;
}

uint32_t api_marker_types(EmfMapMarkerType*, uint32_t) {
    KEMF_NOT_YET("marker_types");
    return 0;
}

// kiln.render, looked up at the first call. A function-local static: concurrent first callers wait for the one lookup
// (world_to_screen may be called from any thread), and the pointer never changes afterwards. Services are published
// before any mod loads, so the first call cannot come too early.
const KilnRenderApi* render_api() {
    static const KilnRenderApi* const render = []() -> const KilnRenderApi* {
        auto* r = static_cast<const KilnRenderApi*>(g_api->service(kstr(KILN_RENDER_SERVICE), 1));
        const bool usable = r && r->header.size >= offsetof(KilnRenderApi, world_to_screen) + sizeof(void*) && r->world_to_screen;
        return usable ? r : nullptr;
    }();
    return render;
}

// A world point in display pixels with the newest camera, through kiln.render (the client; any thread). False when it
// is behind the camera, when no frame is known yet, or without kiln.render (a dedicated server, an older Kiln core);
// px and py are written only for a point on screen.
bool api_world_to_screen(double x, double y, double z, float* px, float* py) {
    const KilnRenderApi* render = render_api();
    if (!render) {
        unsupported(caller_mod(_ReturnAddress()), "world_to_screen",
                    g_side == KILN_SIDE_SERVER ? "a dedicated server draws nothing" : "kiln.render is not available");
        return false;
    }
    const double world[3] = {x, y, z};
    float out[3] = {};
    if (!render->world_to_screen(world, 0.f, 0.f, out)) return false;
    if (px) *px = out[0];
    if (py) *py = out[1];
    return true;
}

uint32_t api_marker_send(uint32_t, const EmfMarker*, uint32_t) {
    KEMF_NOT_YET("marker_send");
    return 0;  // it went to no player
}

uint32_t api_marker_unsend(uint32_t, const char*) {
    KEMF_NOT_YET("marker_unsend");
    return 0;
}

// ---------------------------------------------------------------------------------------------- v34 -------------
bool api_map_cursor(double*, double*) {
    KEMF_NOT_YET("map_cursor");
    return false;
}

bool api_on_map_click(EmfMapClickCallback, void*) {
    KEMF_NOT_YET("on_map_click");
    return false;
}

bool api_map_action(const char*, const char*, uint32_t, EmfMapActionCallback, void*) {
    KEMF_NOT_YET("map_action");
    return false;
}

bool api_ground_at(EmfWorld*, double, double, EmfGroundCallback, void*) {
    KEMF_NOT_YET("ground_at");
    return false;  // refused at once: `cb` is not called
}

// ---------------------------------------------------------------------------------------------- v35 -------------
bool api_player_view(EmfPlayerView*) {
    KEMF_NOT_YET("player_view");
    return false;
}

bool api_fog_at(double, double, float*) {
    KEMF_NOT_YET("fog_at");
    return false;
}

uint32_t api_map_markers(EmfGameMarker*, uint32_t) {
    KEMF_NOT_YET("map_markers");
    return 0;
}

bool api_canvas_set(const char*, const EmfCanvas*) {
    KEMF_NOT_YET("canvas_set");
    return false;
}

bool api_canvas_draw(const char*, const EmfCanvasCmd*, uint32_t) {
    KEMF_NOT_YET("canvas_draw");
    return false;  // an unknown canvas
}

// ---------------------------------------------------------------------------------------------- v36 -------------
bool api_remove_building(EmfWorld*, EmfRemoveBuilding* req) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!req || req->size < v26::kRemoveBuildingV36Size) {
        refuse(m, "remove_building", "no request, or its size field is not sizeof(EmfRemoveBuilding)", true);
        return false;
    }
    unsupported(m, "remove_building", kNotYet);
    req->refunded = 0;
    say_why(req->why);
    return false;
}

// ---------------------------------------------------------------------------------------------- v37 -------------
uint32_t api_path_find(EmfWorld*, const EmfPathQuery*, EmfPathCallback, void*) {
    KEMF_NOT_YET("path_find");
    return 0;  // refused: no callback comes
}

bool api_path_cancel(uint32_t) {
    KEMF_NOT_YET("path_cancel");
    return false;
}

// ---------------------------------------------------------------------------------------------- v38 -------------
uint32_t api_door_state(EmfWorld*, uint32_t) {
    KEMF_NOT_YET("door_state");
    return EMF_DOOR_NONE;
}

bool api_door_set(EmfWorld*, uint32_t, uint32_t, uint32_t) {
    KEMF_NOT_YET("door_set");
    return false;
}

bool api_door_lock(EmfWorld*, uint32_t, uint32_t) {
    KEMF_NOT_YET("door_lock");
    return false;
}

// ---------------------------------------------------------------------------------------------- v39 -------------
uint32_t api_enemy_order(EmfWorld*, EmfEnemyOrder* order) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!order || order->size < v26::kEnemyOrderV39Size) {
        refuse(m, "enemy_order", "no order, or its size field is not sizeof(EmfEnemyOrder)", true);
        return 0;
    }
    unsupported(m, "enemy_order", kNotYet);
    say_why(order->why);
    return 0;
}

bool api_enemy_order_cancel(uint32_t) {
    KEMF_NOT_YET("enemy_order_cancel");
    return false;
}

uint32_t api_enemy_target(EmfWorld*, uint32_t) {
    KEMF_NOT_YET("enemy_target");
    return 0;
}

// ---------------------------------------------------------------------------------------------- v40 -------------
uint32_t api_enemy_move(EmfWorld*, EmfMoveOrder* order, EmfMoveCallback, void*) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!order || order->size < v26::kMoveOrderV40Size) {
        refuse(m, "enemy_move", "no order, or its size field is not sizeof(EmfMoveOrder)", true);
        return 0;
    }
    unsupported(m, "enemy_move", kNotYet);
    say_why(order->why);
    return 0;  // refused: `cb` is not called
}

bool api_enemy_move_cancel(uint32_t) {
    KEMF_NOT_YET("enemy_move_cancel");
    return false;
}

uint32_t api_enemy_moving(EmfWorld*, uint32_t) {
    KEMF_NOT_YET("enemy_moving");
    return 0;
}

}  // namespace

// chat_tell_ex and chat_broadcast_ex are filled by fill_game (api_game.cpp).
void fill_v40(EmfApi* t) {
    t->on_player = &api_on_player;
    t->item_name = &api_item_name;
    t->game_language = &api_game_language;
    t->player_character_name = &api_player_character_name;
    t->on_hit = &api_on_hit;
    t->on_hit_before = &api_on_hit_before;
    t->hit_rule_add = &api_hit_rule_add;
    t->hit_rule_remove = &api_hit_rule_remove;
    t->buff_types = &api_buff_types;
    t->buff_type = &api_buff_type;
    t->buff_text = &api_buff_text;
    t->buffs_of = &api_buffs_of;
    t->buff_apply = &api_buff_apply;
    t->buff_remove = &api_buff_remove;
    t->on_buff = &api_on_buff;
    t->marker_set = &api_marker_set;
    t->marker_remove = &api_marker_remove;
    t->marker_clear = &api_marker_clear;
    t->marker_types = &api_marker_types;
    t->world_to_screen = &api_world_to_screen;
    t->marker_send = &api_marker_send;
    t->marker_unsend = &api_marker_unsend;
    t->map_cursor = &api_map_cursor;
    t->on_map_click = &api_on_map_click;
    t->map_action = &api_map_action;
    t->ground_at = &api_ground_at;
    t->player_view = &api_player_view;
    t->fog_at = &api_fog_at;
    t->map_markers = &api_map_markers;
    t->canvas_set = &api_canvas_set;
    t->canvas_draw = &api_canvas_draw;
    t->remove_building = &api_remove_building;
    t->path_find = &api_path_find;
    t->path_cancel = &api_path_cancel;
    t->door_state = &api_door_state;
    t->door_set = &api_door_set;
    t->door_lock = &api_door_lock;
    t->enemy_order = &api_enemy_order;
    t->enemy_order_cancel = &api_enemy_order_cancel;
    t->enemy_target = &api_enemy_target;
    t->enemy_move = &api_enemy_move;
    t->enemy_move_cancel = &api_enemy_move_cancel;
    t->enemy_moving = &api_enemy_moving;
}

}  // namespace kemf
