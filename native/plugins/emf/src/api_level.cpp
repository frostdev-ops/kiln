// SPDX-License-Identifier: MIT
// The EmfApi table, v22: a player's XP and level (player_xp_required, player_level_cap, player_level_set, and the
// EMF_ATTR_XP* / EMF_ATTR_LEVEL attributes of attribute_get / attribute_add) on kiln.progression.
//  - The curve and the cap come from kiln.progression's xp_for_level (the game's own functions, tick thread). They are
//    cached after each server step, so player_xp_required / player_level_cap answer on any thread as EMF's do.
//  - attribute_add(EMF_ATTR_XP_PENDING, n) is kiln.progression's ADD_EXPERIENCE: granted XP that the game's
//    player_level counts in the next step (level-ups, LevelUpEvent, skill points), saved with the character.
//  - player_level_set is SET_LEVEL (the game's admin "set level": level, XP 0, no pending XP, the level's requirement),
//    followed by SET_EXPERIENCE for the XP asked (cut below the requirement). Kiln applies requests in the next server
//    step, also when called from a world callback (EMF applies those at once).
#include <intrin.h>

#include <algorithm>
#include <vector>
#include <map>

#include "emf_internal.h"
#include "v26_model.h"

namespace kemf {

namespace {

std::mutex g_curve_mutex;
std::vector<uint32_t> g_curve;  // g_curve[level - 1] = XP the level needs; size = the cap
uint32_t g_curve_session = UINT32_MAX;
std::mutex g_level_mutex;
uint64_t g_level_serial = 0;
std::map<uint32_t, uint64_t> g_level_latest;

bool is_server(EmfWorld* w) { return server_requests_allowed() && w && reinterpret_cast<uintptr_t>(w) == g_api->world(KILN_WORLD_SERVER); }

// Reads the curve on the tick thread (once per session, again while it is empty).
void refresh_curve() {
    if (!S.progression || !g_api->on_tick_thread()) return;
    const uint32_t session = server_session();
    {
        std::lock_guard lock(g_curve_mutex);
        if (session != g_curve_session) {
            g_curve.clear();
            g_curve_session = session;
        }
        if (!g_curve.empty()) return;
    }
    uint32_t need = 0, cap = 0;
    if (S.progression->xp_for_level(1, &need, &cap) != KILN_OK || cap == 0 || cap > 1000) return;
    std::vector<uint32_t> curve(cap, 0);
    for (uint32_t l = 1; l <= cap; ++l) {
        uint32_t n = 0, c = 0;
        if (S.progression->xp_for_level(l, &n, &c) != KILN_OK) return;
        curve[l - 1] = n;
    }
    std::lock_guard lock(g_curve_mutex);
    g_curve = std::move(curve);
    g_curve_session = session;
}

uint32_t level_cap() {
    refresh_curve();
    std::lock_guard lock(g_curve_mutex);
    return g_curve_session == server_session() ? static_cast<uint32_t>(g_curve.size()) : 0;
}

uint32_t xp_required(uint32_t level) {
    refresh_curve();
    std::lock_guard lock(g_curve_mutex);
    return g_curve_session == server_session() && level >= 1 && level <= g_curve.size() ? g_curve[level - 1] : 0;
}

uint32_t api_player_level_cap(EmfWorld* w) { return is_server(w) && S.progression ? level_cap() : 0; }

uint32_t api_player_xp_required(EmfWorld* w, uint32_t level) {
    return is_server(w) && S.progression ? xp_required(level) : 0;
}

struct LevelSet {
    Mod* mod;
    uint32_t xp, level, session;
    EmfWorld* world;
    uint64_t serial;
};

void KILN_CALL level_set_done(const KilnProgressionResult* r, void* p) {
    std::unique_ptr<LevelSet> ls(static_cast<LevelSet*>(p));
    if (!r) return;
    std::lock_guard lock(g_level_mutex);
    auto it = g_level_latest.find(r->entity);
    if (it == g_level_latest.end() || it->second != ls->serial) return;
    g_level_latest.erase(it);
    if (!is_server(ls->world) || ls->session != server_session()) return;
    if (r->status != KILN_OK) {
        mod_logf(ls->mod, KILN_LOG_WARN, "player_level_set(%u) refused later: %s", r->entity, r->reason);
        return;
    }
    if (ls->xp == 0 || r->after.level != ls->level) return;
    KilnProgressionRequest q{};
    q.size = sizeof q;
    q.op = KILN_PROG_OP_SET_EXPERIENCE;
    q.entity = r->entity;
    q.value = v26::level_set_xp(ls->xp, r->after.experience_needed);
    if (q.value == 0) return;
    if (S.progression->submit(ls->mod && ls->mod->owner ? ls->mod->owner : g_owner, &q, nullptr) != KILN_OK)
        mod_logf(ls->mod, KILN_LOG_WARN, "player_level_set(%u): the XP towards the next level was not set", r->entity);
}

bool api_player_level_set(EmfWorld* w, uint32_t player, uint32_t level, uint32_t xp) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.progression) return unsupported(m, "player_level_set", "kiln.progression is not available"), false;
    if (!is_server(w)) return refuse(m, "player_level_set", "server world only"), false;
    const uint32_t cap = level_cap();
    const std::string why = v26::level_set_refusal(level, cap, cap != 0 || !g_api->on_tick_thread());
    if (!why.empty()) return refuse(m, "player_level_set", why.c_str()), false;
    KilnProgressionRequest q{};
    q.size = sizeof q;
    q.op = KILN_PROG_OP_SET_LEVEL;
    q.entity = player;
    q.value = level;
    q.callback = &level_set_done;
    std::lock_guard lock(g_level_mutex);
    auto* ls = new LevelSet{m, xp, level, server_session(), w, ++g_level_serial};
    q.user = ls;
    const KilnResult rc = S.progression->submit(m && m->owner ? m->owner : g_owner, &q, nullptr);
    if (rc != KILN_OK) {
        delete ls;
        refuse(m, "player_level_set",
               rc == KILN_ERR_NOT_FOUND ? "not a player in game"
               : rc == KILN_ERR_UNAVAILABLE ? "levels are off on this game build"
                                            : "the level is out of range");
        return false;
    }
    g_level_latest[player] = ls->serial;
    return true;
}

}  // namespace

bool level_attribute_get(Mod* m, EmfWorld* w, uint32_t entity, uint32_t attr, int32_t* out) {
    if (!out || !is_server(w) || !g_api->on_tick_thread()) return false;
    if (!S.progression) return unsupported(m, "attribute_get(XP / level)", "kiln.progression is not available"), false;
    KilnProgression p{};
    p.size = sizeof p;
    if (S.progression->get(entity, &p) != KILN_OK || !(p.flags & KILN_PROG_HAS_LEVEL)) return false;
    switch (attr) {
        case EMF_ATTR_LEVEL: *out = static_cast<int32_t>(p.level); break;
        case EMF_ATTR_XP: *out = static_cast<int32_t>(p.experience); break;
        case EMF_ATTR_XP_REQUIRED: *out = static_cast<int32_t>(p.experience_needed); break;
        default: *out = static_cast<int32_t>(p.experience_pending); break;
    }
    return true;
}

bool level_attribute_add(Mod* m, EmfWorld* w, uint32_t entity, uint32_t attr, int32_t delta) {
    if (!is_server(w)) return refuse(m, "attribute_add", "server world only"), false;
    if (v26::attribute_write_rule(attr) == v26::AttrWrite::ReadOnly)
        return refuse(m, "attribute_add",
                      "XP, XP required and the level are kept consistent by the game: give XP (EMF_ATTR_XP_PENDING) or "
                      "use player_level_set"),
               false;
    if (delta <= 0) return refuse(m, "attribute_add", "pending XP is only given (a positive amount)"), false;
    if (!S.progression) return unsupported(m, "attribute_add(XP)", "kiln.progression is not available"), false;
    KilnProgressionRequest q{};
    q.size = sizeof q;
    q.op = KILN_PROG_OP_ADD_EXPERIENCE;
    q.entity = entity;
    q.value = static_cast<uint32_t>(delta);
    const KilnResult rc = S.progression->submit(m && m->owner ? m->owner : g_owner, &q, nullptr);
    if (rc != KILN_OK) refuse(m, "attribute_add(XP)", rc == KILN_ERR_NOT_FOUND ? "not a player in game" : "XP is off on this game build");
    return rc == KILN_OK;
}

void level_after_step(EmfWorld*, uint8_t kind) {
    if (kind == EMF_WORLD_SERVER) refresh_curve();
}

void fill_level(EmfApi* t) {
    t->player_xp_required = &api_player_xp_required;
    t->player_level_cap = &api_player_level_cap;
    t->player_level_set = &api_player_level_set;
}

}  // namespace kemf
