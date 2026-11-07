// SPDX-License-Identifier: MIT
// kiln-set: SET's Admin.* on Kiln's services, included by lua_api.cpp in place of SET's admin-command injector.
// SET writes the game's own admin commands into the host's input; Kiln has typed services for the same jobs, which
// also work on a dedicated server: kiln.time (setTime), kiln.world (teleport), kiln.spawn (spawn), kiln.items (give),
// kiln.settings (settings, changeSettings). Calls queue and run at the next server tick, as in SET.
// setLevel / setFlameLevel / send have no native Kiln service yet and raise an error saying so.
#include "services/kiln_items.h"
#include "services/kiln_settings.h"
#include "services/kiln_spawn.h"
#include "services/kiln_time.h"

const KilnApi* gKApi = nullptr;
KilnOwner gKOwner = 0;
const KilnTimeApi* gKTime = nullptr;
const KilnWorldApi* gKWorld = nullptr;
const KilnSpawnApi* gKSpawn = nullptr;
const KilnItemsApi* gKItems = nullptr;
const KilnSettingsApi* gKSettings = nullptr;

// Queued admin work: run on the next server tick (armAdmin), one closure per call.
std::mutex gAdminMx;
struct AdminWork { std::string mod; std::function<void()> fn; };
std::vector<AdminWork> gAdminQueue;
void discardAdmin(const std::string& mod) {
    std::lock_guard lock(gAdminMx);
    std::erase_if(gAdminQueue, [&](const AdminWork& work) { return work.mod == mod; });
}

void queueAdmin(lua_State* L, std::function<void()> fn) {
    needGame(L);
    needWorld(L);
    std::lock_guard lock(gAdminMx);
    if (gAdminQueue.size() >= 1024) luaL_error(L, "admin command queue is full");
    gAdminQueue.push_back({gCurrentMod, std::move(fn)});
}

void adminLog(const std::string& s) { gSink(s.c_str()); }

int adminSend(lua_State* L) {
    return luaL_error(L, "Admin.send is not available on Kiln: use Admin.setTime, teleport, spawn, give or changeSettings");
}

int adminSetTime(lua_State* L) {
    double h = luaL_checknumber(L, 1);
    if (!(h >= 0 && h < 24)) return luaL_error(L, "time of day is 0 to below 24 hours, not %f", h);
    if (!gKTime) return luaL_error(L, "Admin.setTime is unavailable: kiln.time is not running");
    queueAdmin(L, [h] {
        const KilnResult r = gKTime->set_time_of_day(h, KILN_TIME_SET_GAME);
        if (r != KILN_OK) adminLog(LV_WARN "admin command adminSetDaytime was not run by the game (kiln.time " + std::to_string(r) + ")");
    });
    return 0;
}

int adminTeleport(lua_State* L) {
    const double x = luaL_checknumber(L, 1), y = luaL_checknumber(L, 2), z = luaL_checknumber(L, 3);
    if (!lua_isnoneornil(L, 4)) luaL_checktype(L, 4, LUA_TBOOLEAN);
    const bool exact = lua_toboolean(L, 4);
    if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return luaL_error(L, "teleport coordinates must be finite");
    if (!gKWorld) return luaL_error(L, "Admin.teleport is unavailable: kiln.world is not running");
    queueAdmin(L, [x, y, z, exact] {
        const uint32_t p = ecs::player();
        if (!p) return adminLog(LV_WARN "admin command adminTeleport was not run by the game (no player)");
        KilnTeleportRequest req{};
        req.size = sizeof req;
        req.entity = p;
        req.target = KilnVec3{x, y, z};
        req.flags = exact ? 0u : KILN_TELEPORT_FIND_GROUND;
        uint64_t id = 0;
        if (gKWorld->teleport(gKOwner, &req, &id) != KILN_OK)
            adminLog(LV_WARN "admin command adminTeleport was not run by the game");
    });
    return 0;
}

int adminSetLevel(lua_State* L) {
    return luaL_error(L, "Admin.setLevel is not available on Kiln yet");
}

int adminSetFlameLevel(lua_State* L) {
    return luaL_error(L, "Admin.setFlameLevel is not available on Kiln yet");
}

// A template by GUID or name (kiln.spawn resolves names, display names and GUID text).
int adminSpawn(lua_State* L) {
    std::string tpl = luaL_checkstring(L, 1);
    const lua_Integer count = luaL_optinteger(L, 2, 1), level = luaL_optinteger(L, 3, 0);
    if (count < 1 || count > 255) return luaL_error(L, "count is 1 to 255, not %d", int(count));
    if (level < 0 || level > 65535) return luaL_error(L, "level is 1 to 65535, not %d", int(level));
    needGame(L);
    if (!gKSpawn || !gKWorld) return luaL_error(L, "Admin.spawn is unavailable: kiln.spawn is not running");
    KilnTemplateInfo info{};
    info.size = sizeof info;
    if (gKSpawn->template_find(KilnStr{tpl.data(), tpl.size()}, &info) != KILN_OK)
        return luaL_error(L, "no TemplateResource named '%s'", tpl.c_str());
    queueAdmin(L, [info, count, level] {
        const uint32_t p = ecs::player();
        KilnVec3 at{};
        if (!p || gKWorld->position(p, &at) != KILN_OK) return adminLog(LV_WARN "admin command adminSpawnCommand was not run by the game (no player)");
        for (lua_Integer i = 0; i < count; ++i) {
            KilnSpawnRequest req{};
            req.size = sizeof req;
            req.template_guid = info.guid;
            req.position = KilnVec3{at.x + 5.0, at.y, at.z};  // 5 m from the player, as SET
            req.level = static_cast<int32_t>(level);
            req.tag = KilnStr{"set", 3};
            req.origin = KilnStr{"kiln-set", 8};
            KilnEntity e = 0;
            if (gKSpawn->spawn(&req, &e) != KILN_OK) return adminLog(LV_WARN "admin command adminSpawnCommand was not run by the game");
        }
    });
    return 0;
}

int adminGive(lua_State* L) {
    std::string item = luaL_checkstring(L, 1);
    const lua_Integer count = luaL_optinteger(L, 2, 1), level = luaL_optinteger(L, 3, 0);
    if (count < 1 || count > 999) return luaL_error(L, "count is 1 to 999, not %d", int(count));
    if (level < 0 || level > 255) return luaL_error(L, "level is 1 to 255, not %d", int(level));
    int rarity = -1;
    if (!lua_isnoneornil(L, 4)) {
        static const char* kRarity[] = {"common", "uncommon", "rare", "epic", "legendary"};
        if (lua_type(L, 4) == LUA_TSTRING) {
            std::string r = lower(lua_tostring(L, 4));
            for (int i = 0; i < 5; ++i) if (r == kRarity[i]) rarity = i;
        } else {
            const auto value = luaL_checkinteger(L, 4);
            if (value < 0 || value > 4) return luaL_error(L, "rarity is Common..Legendary or 0 to 4");
            rarity = static_cast<int>(value);
        }
        if (rarity < 0 || rarity > 4) return luaL_error(L, "rarity is Common..Legendary or 0 to 4");
    }
    needGame(L);
    if (!gKItems) return luaL_error(L, "Admin.give is unavailable: kiln.items is not running");
    // By GUID, debugName or English name, as SET: GUID text and debug names resolve here; English names through Res.
    KilnItemId id = 0;
    uint8_t g[16];
    if (res::parseGuid(item, g)) id = gKItems->item_id_of_guid(g);
    if (!id) id = gKItems->find_item(KilnStr{item.data(), item.size()});
    if (!id) {
        refl::Type t = refl::byName("keen::ItemInfo");
        if (t && res::ready()) {
            const std::string want = lower(item);
            for (const res::Entry* e : res::ofType(t.hash()))
                if (lower(res::label(*e)) == want) id = gKItems->item_id_of_guid(e->guid);
        }
    }
    if (!id) return luaL_error(L, "no ItemInfo named '%s'", item.c_str());
    queueAdmin(L, [id, count, level, rarity] {
        const uint32_t p = ecs::player();
        if (!p) return adminLog(LV_WARN "admin command adminInventoryCreateAction was not run by the game (no player)");
        KilnItemsRequest req{};
        req.size = sizeof req;
        req.entity = p;
        req.item = id;
        req.count = static_cast<uint32_t>(count);
        if (level) req.flags |= KILN_ITEMS_LEVEL, req.level = static_cast<uint32_t>(level);
        if (rarity >= 0) req.flags |= KILN_ITEMS_RARITY, req.rarity = static_cast<uint8_t>(rarity);
        uint64_t ticket = 0;
        if (gKItems->give(gKOwner, &req, nullptr, nullptr, &ticket) != KILN_OK)
            adminLog(LV_WARN "admin command adminInventoryCreateAction was not run by the game");
    });
    return 0;
}

// Admin.settings(): the world's settings as a table, by their enshrouded_server.json names (SET's field names).
int adminSettings(lua_State* L) {
    needGame(L);
    needWorld(L);
    if (!gKSettings) return luaL_error(L, "Admin.settings is unavailable: kiln.settings is not running");
    lua_newtable(L);
    for (uint32_t i = 0, n = gKSettings->count(); i < n; ++i) {
        KilnSettingInfo s{};
        s.size = sizeof s;
        if (gKSettings->info(i, &s) != KILN_OK) continue;
        lua_pushlstring(L, s.name.ptr, s.name.len);
        if (s.kind == KILN_SETTING_FLOAT) lua_pushnumber(L, s.value.number);
        else if (s.kind == KILN_SETTING_BOOL) lua_pushboolean(L, s.value.integer != 0);
        else lua_pushinteger(L, s.value.integer);
        lua_settable(L, -3);
    }
    return 1;
}

// Admin.changeSettings{...}: only the named settings change; checked against kiln.settings' ranges. Enums take the
// option's name or its value, as SET.
int adminChangeSettings(lua_State* L) {
    luaL_checktype(L, 1, LUA_TTABLE);
    if (!gKSettings) return luaL_error(L, "Admin.changeSettings is unavailable: kiln.settings is not running");
    std::vector<std::pair<uint32_t, KilnSettingValue>> changes;
    lua_pushnil(L);
    while (lua_next(L, 1)) {
        if (lua_type(L, -2) != LUA_TSTRING) return luaL_error(L, "setting names are strings");
        size_t n = 0;
        const char* name = lua_tolstring(L, -2, &n);
        uint32_t idx = 0;
        KilnSettingInfo s{};
        s.size = sizeof s;
        if (gKSettings->find(KilnStr{name, n}, &idx) != KILN_OK || gKSettings->info(idx, &s) != KILN_OK)
            return luaL_error(L, "unknown setting '%s'", name);
        KilnSettingValue v{};
        v.kind = s.kind;
        if (s.kind == KILN_SETTING_BOOL) {
            luaL_checktype(L, -1, LUA_TBOOLEAN);
            v.integer = lua_toboolean(L, -1);
        } else if (s.kind == KILN_SETTING_ENUM && lua_type(L, -1) == LUA_TSTRING) {
            const std::string want = lower(lua_tostring(L, -1));
            bool found = false;
            for (uint32_t o = 0; o < s.option_count && !found; ++o) {
                KilnStr on{};
                int64_t ov = 0;
                if (gKSettings->option(idx, o, &on, &ov) == KILN_OK && lower(std::string(on.ptr, on.len)) == want) v.integer = ov, found = true;
            }
            if (!found) return luaL_error(L, "%s has no option '%s'", name, want.c_str());
        } else if (s.kind == KILN_SETTING_FLOAT) {
            v.number = luaL_checknumber(L, -1);
            if (!(v.number >= s.min && v.number <= s.max)) return luaL_error(L, "%s is %f to %f", name, s.min, s.max);
        } else {
            if (!lua_isinteger(L, -1)) return luaL_error(L, "%s takes a whole number", name);
            v.integer = lua_tointeger(L, -1);
            if (!(double(v.integer) >= s.min && double(v.integer) <= s.max)) return luaL_error(L, "%s is %f to %f", name, s.min, s.max);
        }
        v.number = s.kind == KILN_SETTING_FLOAT ? v.number : double(v.integer);
        changes.emplace_back(idx, v);
        lua_pop(L, 1);
    }
    queueAdmin(L, [changes] {
        for (const auto& [idx, v] : changes) {
            KilnSettingValue before{};
            if (gKSettings->set(gKOwner, idx, &v, 0, &before) != KILN_OK)
                adminLog(LV_WARN "admin command adminChangeGameSettingsAction was not run by the game");
        }
    });
    return 0;
}

void resetAdmin() {
    std::lock_guard lock(gAdminMx);
    gAdminQueue.clear();
}
void checkSent(uintptr_t) {}
void armAdmin(uintptr_t) {
    std::vector<AdminWork> q;
    {
        std::lock_guard lock(gAdminMx);
        q.swap(gAdminQueue);
    }
    for (auto& work : q) work.fn();
}
