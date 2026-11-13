// SPDX-License-Identifier: MIT
// The EmfApi table, part 2: items (kiln.items, kiln.content), spawn/destroy (kiln.spawn), chat (kiln.chat,
// kiln.players), saves (kiln.saves), damage (kiln.combat), teleport (kiln.world), world settings
// (kiln.settings), HUD lines and notes (kiln.ui, client).
#include <intrin.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <deque>
#include <map>

#include "emf_internal.h"

namespace kemf {

void persisted_settings_after_load(uint16_t slot);

namespace {

bool in_server_cb(EmfWorld* w) {
    return w && reinterpret_cast<uintptr_t>(w) == g_api->world(KILN_WORLD_SERVER) && g_api->on_tick_thread();
}
bool is_server_world(EmfWorld* w) { return w && reinterpret_cast<uintptr_t>(w) == g_api->world(KILN_WORLD_SERVER); }
KilnOwner owner_of(const Mod* m) { return m && m->owner ? m->owner : g_owner; }

}  // namespace

// a posted task that runs `f` on the tick thread (Pre of the next step)
namespace {
struct Posted {
    std::function<void()> f;
};
void KILN_CALL posted_thunk(void* p) {
    std::unique_ptr<Posted> t(static_cast<Posted*>(p));
    t->f();
}
}  // namespace
bool post_tick(std::function<void()> f) {
    auto* t = new Posted{std::move(f)};
    if (g_api->post(g_owner, &posted_thunk, t) == KILN_OK) return true;
    delete t;
    return false;
}

namespace {

// ============================================================================================= items =========
uint32_t live_max_stack(uint32_t id, uint32_t fallback) {
    if (!S.content || !g_api->on_tick_thread()) return fallback;
    KilnContentValue v{};
    if (S.content->get(KILN_CONTENT_ITEMS, id, kstr("maxStackSize"), &v, nullptr) == KILN_OK) return static_cast<uint32_t>(v.i);
    return fallback;
}

void fill_item(const KilnItemInfo& k, EmfItemInfo* out) {
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof(EmfItemInfo));
    out->size = size;
    out->item_id = k.id;
    std::memcpy(out->guid, k.guid, 16);
    out->max_stack = static_cast<uint16_t>(live_max_stack(k.id, k.max_stack));
    copy_cstr(out->name, sizeof out->name, sv(k.name));
}

struct ItemCall {
    EmfItemCallback cb;
    const EmfItemInfo* info;
    void* user;
};

void api_for_each_item(EmfWorld*, EmfItemCallback cb, void* user) {
    if (!cb || !S.items) return;
    const uint32_t n = S.items->item_count();
    for (uint32_t i = 0; i < n; ++i) {
        KilnItemInfo k{};
        k.size = sizeof k;
        if (S.items->item_at(i, &k) != KILN_OK) continue;
        EmfItemInfo e{};
        e.size = sizeof e;
        fill_item(k, &e);
        cb(&e, user);
    }
}

uint32_t api_find_item(EmfWorld*, const char* name) {
    if (!name || !S.items) return 0;
    return S.items->find_item(kstr(name));
}

bool api_item_info(EmfWorld*, uint32_t id, EmfItemInfo* out) {
    if (!out || out->size < sizeof(EmfItemInfo) || !S.items) return false;
    KilnItemInfo k{};
    k.size = sizeof k;
    if (S.items->item_info(id, &k) != KILN_OK) return false;
    fill_item(k, out);
    return true;
}

void* api_item_data(EmfWorld* w, uint32_t id, uint64_t* size_out) {
    if (size_out) *size_out = 0;
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.content || !w || !on_thread_of(w)) {
        if (!S.content) unsupported(m, "item_data", "kiln.content is not available");
        return nullptr;
    }
    KilnContentEntry e{};
    e.size = sizeof e;
    char key[16];
    std::snprintf(key, sizeof key, "0x%08x", id);
    if (S.content->find(KILN_CONTENT_ITEMS, kstr(key), &e) != KILN_OK || !e.address) return nullptr;
    if (size_out) *size_out = type_size(fnv1a("keen::ItemInfo"));
    return reinterpret_cast<void*>(e.address);
}

bool api_set_item_max_stack(uint32_t id, uint16_t max) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.content) {
        unsupported(m, "set_item_max_stack", "kiln.content is not available");
        return false;
    }
    if (max == 0 || !g_api->on_tick_thread()) {
        if (max) refuse(m, "set_item_max_stack", "call it from a world callback (it runs on the server world's thread)", true);
        return false;
    }
    KilnContentValue v{};
    v.type = KILN_CV_INT;
    v.i = max;
    v.f = max;
    const KilnResult rc = S.content->set(owner_of(m), KILN_CONTENT_ITEMS, id, kstr("maxStackSize"), &v);
    return rc == KILN_OK;
}

// ---- put / take ----
struct ItemsReq {
    Mod* mod;
    EmfPutItemsCallback cb;
    void* user;
    std::shared_ptr<CommandRun> cmd;
};
std::atomic<int> g_items_waiting{0};

void KILN_CALL items_done(const KilnItemsResult* r, void* p) {
    std::unique_ptr<ItemsReq> q(static_cast<ItemsReq*>(p));
    g_items_waiting.fetch_sub(1);
    if (!q->cb) return;
    struct Call {
        EmfPutItemsCallback cb;
        EmfWorld* w;
        uint32_t entity, item, requested, done;
        void* user;
    } c{q->cb, world_of_kind(KILN_WORLD_SERVER), r->entity, r->item, r->requested,
        (r->flags & KILN_ITEMS_DRY_RUN) ? 0u : r->done, q->user};
    ContextScope scope(q->mod, c.w, true, q->cmd);
    guarded(q->mod, "a put/take result callback", [](void* x) {
        auto* k = static_cast<Call*>(x);
        k->cb(k->w, k->entity, k->item, k->requested, k->done, k->user);
    }, &c);
}

bool submit_items(Mod* m, bool give, EmfWorld* w, const KilnItemsRequest& req, EmfPutItemsCallback cb, void* user,
                  const char* fn) {
    if (!S.items) {
        unsupported(m, fn, "kiln.items is not available");
        return false;
    }
    if (!is_server_world(w)) return refuse(m, fn, "not the server world (host only)", true), false;
    if (!req.entity || !req.item || req.count < 1 || req.count > 65535) return refuse(m, fn, "entity 0, item 0 or a bad count"), false;
    if (g_items_waiting.load() >= 64) return refuse(m, fn, "64 requests are already waiting"), false;
    auto* q = new ItemsReq{m, cb, user, t_ctx.cmd};
    g_items_waiting.fetch_add(1);
    const KilnResult rc = give ? S.items->give(owner_of(m), &req, &items_done, q, nullptr)
                               : S.items->take(owner_of(m), &req, &items_done, q, nullptr);
    if (rc != KILN_OK) {
        g_items_waiting.fetch_sub(1);
        delete q;
        refuse(m, fn, rc == KILN_ERR_UNAVAILABLE ? "inventory transactions are off on this game build" : "refused by kiln.items");
        return false;
    }
    return true;
}

bool api_put_items(EmfWorld* w, uint32_t entity, uint32_t item, uint32_t count, EmfPutItemsCallback cb, void* user) {
    Mod* m = caller_mod(_ReturnAddress());
    KilnItemsRequest r{};
    r.size = sizeof r;
    r.flags = KILN_ITEMS_NOTIFY;
    r.entity = entity;
    r.item = item;
    r.count = count;
    return submit_items(m, true, w, r, cb, user, "put_items");
}

bool api_put_items_ex(EmfWorld* w, const EmfItemsRequest* e) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!e || e->size < offsetof(EmfItemsRequest, user) + sizeof(e->user)) return refuse(m, "put_items_ex", "bad request"), false;
    if (e->flags & ~static_cast<uint32_t>(EMF_ITEMS_RARITY)) return refuse(m, "put_items_ex", "unknown flags"), false;
    KilnItemsRequest r{};
    r.size = sizeof r;
    r.flags = KILN_ITEMS_NOTIFY;
    r.entity = e->entity;
    r.item = e->item;
    r.count = e->count;
    if (e->level) {
        r.flags |= KILN_ITEMS_LEVEL;
        r.level = std::min<uint32_t>(e->level, 255);
    }
    if (e->flags & EMF_ITEMS_RARITY) {
        if (e->rarity > 4) return refuse(m, "put_items_ex", "rarity is 0..4"), false;
        r.flags |= KILN_ITEMS_RARITY;
        r.rarity = e->rarity;
    }
    return submit_items(m, true, w, r, e->cb, e->user, "put_items_ex");
}

bool api_take_items(EmfWorld* w, const EmfItemsRequest* e) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!e || e->size < offsetof(EmfItemsRequest, user) + sizeof(e->user)) return refuse(m, "take_items", "bad request"), false;
    if (e->flags & ~static_cast<uint32_t>(EMF_ITEMS_EXACT_SLOT)) return refuse(m, "take_items", "unknown flags"), false;
    KilnItemsRequest r{};
    r.size = sizeof r;
    r.entity = e->entity;
    r.item = e->item;
    r.count = e->count;
    r.slot = e->slot;
    if (e->slot && ((e->slot >> 40) != 0 || ((e->slot >> 32) & 0xFF) > 7)) return refuse(m, "take_items", "bad slot"), false;
    if (e->flags & EMF_ITEMS_EXACT_SLOT) {
        if (!e->slot) return refuse(m, "take_items", "EMF_ITEMS_EXACT_SLOT needs a slot"), false;
        r.flags |= KILN_ITEMS_EXACT_SLOT;
    }
    return submit_items(m, false, w, r, e->cb, e->user, "take_items");
}

// ============================================================================================= spawn =========
uint32_t do_spawn(Mod* m, EmfWorld* w, const uint8_t guid[16], double x, double y, double z, const char* fn, const KilnSpawnComponent* components = nullptr, uint32_t component_count = 0) {
    if (!S.spawn) {
        unsupported(m, fn, "kiln.spawn is not available");
        return 0;
    }
    if (!in_server_cb(w) || !t_ctx.world_cb) {
        refuse(m, fn, "server world only, inside a world callback on the world passed to it");
        return 0;
    }
    if (!guid || !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return refuse(m, fn, "bad arguments"), 0;
    KilnSpawnRequest req{};
    req.size = sizeof req;
    req.components = components;
    req.component_count = component_count;
    std::memcpy(req.template_guid.b, guid, 16);
    req.position = KilnVec3{x, y, z};
    // EMF: spawned entities are not saved (except villagers); kiln.spawn's sidecar is not used for them
    req.flags = KILN_SPAWN_NO_TRACK;
    KilnTemplateInfo ti{};
    ti.size = sizeof ti;
    if (S.spawn->template_get(&req.template_guid, &ti) == KILN_OK && ti.kind != KILN_TEMPLATE_NPC)
        req.flags |= KILN_SPAWN_TRANSIENT;
    const std::string origin = "emf:" + std::string(mod_name(m));
    req.tag = kstr("emf");
    req.origin = kstr(origin);
    KilnEntity e = 0;
    const KilnResult rc = S.spawn->spawn(&req, &e);
    if (rc != KILN_OK) {
        refuse(m, fn, rc == KILN_ERR_NOT_FOUND ? "unknown template" : rc == KILN_ERR_UNAVAILABLE ? "the world is not running yet"
                                                                                                   : "the engine refused (kiln.log)");
        return 0;
    }
    return e;
}

uint32_t api_spawn(EmfWorld* w, const uint8_t guid[16], double x, double y, double z) {
    return do_spawn(caller_mod(_ReturnAddress()), w, guid, x, y, z, "spawn");
}

uint32_t api_spawn_ex(EmfWorld* w, const uint8_t guid[16], double x, double y, double z, const EmfComponentInit* comps,
                      uint32_t count) {
    Mod* m = caller_mod(_ReturnAddress());
    if (count == 0) return do_spawn(m, w, guid, x, y, z, "spawn_ex");
    if (!comps || count > KILN_SPAWN_MAX_COMPONENTS) return refuse(m, "spawn_ex", "bad component list"), 0;
    if (!S.spawn || S.spawn->header.size < offsetof(KilnSpawnApi, capabilities) + sizeof(S.spawn->capabilities) ||
        !S.spawn->capabilities || !(S.spawn->capabilities() & KILN_SPAWN_CAP_COMPONENTS))
        return unsupported(m, "spawn_ex", "kiln.spawn spawn-time components are unavailable"), 0;
    std::vector<KilnSpawnComponent> values;
    values.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        if (!comps[i].hash || !comps[i].data || !comps[i].size || comps[i].size > (1u << 20))
            return refuse(m, "spawn_ex", "invalid component bytes"), 0;
        values.push_back({comps[i].hash, 0, comps[i].data, comps[i].size});
    }
    return do_spawn(m, w, guid, x, y, z, "spawn_ex", values.data(), count);
}

bool api_destroy(EmfWorld* w, uint32_t entity) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.spawn) {
        unsupported(m, "destroy", "kiln.spawn is not available");
        return false;
    }
    if (!in_server_cb(w) || !t_ctx.world_cb) return refuse(m, "destroy", "server world only, inside a world callback"), false;
    if (entity <= 1023) return refuse(m, "destroy", "players (1..1023) are never destroyed"), false;
    return S.spawn->destroy(entity) == KILN_OK;
}

uint32_t api_give_item(EmfWorld* w, uint32_t player, uint32_t item, uint32_t count) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.spawn || !S.world) {
        unsupported(m, "give_item", "kiln.spawn / kiln.world are not available");
        return 0;
    }
    if (!in_server_cb(w) || !t_ctx.world_cb) return refuse(m, "give_item", "server world only, inside a world callback"), 0;
    if (!item || count < 1 || count > 65535) return refuse(m, "give_item", "bad item or count"), 0;
    KilnVec3 p{};
    if (S.world->position(player, &p) != KILN_OK) return refuse(m, "give_item", "the player has no position"), 0;
    p.y += 1.0;
    const std::string origin = "emf:" + std::string(mod_name(m));
    KilnEntity e = 0;
    if (S.spawn->spawn_item(item, count, p, 0, KILN_SPAWN_NO_TRACK, kstr("emf"), kstr(origin), &e) != KILN_OK)
        return refuse(m, "give_item", "the pickup could not be spawned (kiln.log)"), 0;
    return e;
}

// ============================================================================================= chat ==========
struct Rate {
    std::deque<uint64_t> sent;
};
std::mutex g_rate_mutex;
std::map<const Mod*, Rate> g_rates;

bool rate_ok(const Mod* m, size_t lines) {
    std::lock_guard lock(g_rate_mutex);
    Rate& r = g_rates[m];
    const uint64_t now = now_ms();
    while (!r.sent.empty() && now - r.sent.front() > 10000) r.sent.pop_front();
    if (r.sent.size() + lines > 30) return false;
    for (size_t i = 0; i < lines; ++i) r.sent.push_back(now);
    return true;
}

std::vector<std::string> chat_lines(std::string_view text) {
    std::vector<std::string> out;
    size_t i = 0;
    while (i <= text.size()) {
        size_t j = text.find('\n', i);
        if (j == std::string_view::npos) j = text.size();
        std::string line(text.substr(i, j - i));
        if (!line.empty() && line.back() == '\r') line.pop_back();
        for (char& c : line)
            if (static_cast<unsigned char>(c) < 0x20) c = '?';
        if (line.size() > EMF_CHAT_LINE_BYTES) {
            size_t n = EMF_CHAT_LINE_BYTES;
            while (n > 0 && (static_cast<unsigned char>(line[n]) & 0xC0) == 0x80) --n;
            line.resize(n);
        }
        if (out.size() == EMF_CHAT_MAX_LINES) {
            out.push_back("...");
            break;
        }
        out.push_back(line);
        if (j == text.size()) break;
        i = j + 1;
    }
    return out;
}

void send_lines(KilnEntity to, const std::vector<std::string>& lines) {
    for (const auto& l : lines) S.chat->send(to, kstr(l), nullptr);
}

// chat_tell and chat_tell_ex (v30). kiln.chat's server lines are notices: a client shows them as their text alone,
// without a speaker's name, also on a server with text chat off. That is what EMF_CHAT_SYSTEM asks for, so flags 0
// and EMF_CHAT_SYSTEM send the same line here (on EMF a plain chat_tell shows under the player's own name).
bool chat_tell(Mod* m, const char* fn, EmfWorld* w, uint32_t player, const char* text) {
    if (!S.chat) return unsupported(m, fn, "kiln.chat is not available"), false;
    if (!is_server_world(w)) return refuse(m, fn, "not a server world"), false;
    KilnChatPlayer p{};
    p.size = sizeof p;
    if (!player || S.chat->find_player(player, &p) != KILN_OK) return refuse(m, fn, "no such player"), false;
    auto lines = chat_lines(text ? text : "");
    for (const auto& l : lines) mod_logf(m, KILN_LOG_INFO, "[tell %u] %s", player, l.c_str());
    if (!rate_ok(m, lines.size())) return refuse(m, fn, "more than 30 lines in 10 s"), false;
    if (g_api->on_tick_thread()) {
        send_lines(player, lines);
        return true;
    }
    return post_tick([player, lines] { send_lines(player, lines); });
}

bool chat_broadcast(Mod* m, const char* fn, const char* text) {
    if (!S.chat) return unsupported(m, fn, "kiln.chat is not available"), false;
    auto lines = chat_lines(text ? text : "");
    for (const auto& l : lines) mod_logf(m, KILN_LOG_INFO, "[broadcast] %s", l.c_str());
    if (!rate_ok(m, lines.size())) return refuse(m, fn, "more than 30 lines in 10 s"), false;
    if (g_api->on_tick_thread()) {
        send_lines(0, lines);
        return true;
    }
    return post_tick([lines] { send_lines(0, lines); });
}

bool api_chat_tell(EmfWorld* w, uint32_t player, const char* text) {
    return chat_tell(caller_mod(_ReturnAddress()), "chat_tell", w, player, text);
}

bool api_chat_broadcast(const char* text) { return chat_broadcast(caller_mod(_ReturnAddress()), "chat_broadcast", text); }

bool api_chat_tell_ex(EmfWorld* w, uint32_t player, const char* text, uint32_t flags) {
    Mod* m = caller_mod(_ReturnAddress());
    if (flags & ~EMF_CHAT_SYSTEM) return refuse(m, "chat_tell_ex", "unknown flags"), false;
    return chat_tell(m, "chat_tell_ex", w, player, text);
}

bool api_chat_broadcast_ex(const char* text, uint32_t flags) {
    Mod* m = caller_mod(_ReturnAddress());
    if (flags & ~EMF_CHAT_SYSTEM) return refuse(m, "chat_broadcast_ex", "unknown flags"), false;
    return chat_broadcast(m, "chat_broadcast_ex", text);
}

bool api_player_name(EmfWorld* w, uint32_t player, char* buf, uint32_t size) {
    if (buf && size) buf[0] = '\0';
    if (!buf || !size || !is_server_world(w)) return false;
    if (S.chat) {
        KilnChatPlayer p{};
        p.size = sizeof p;
        if (S.chat->find_player(player, &p) == KILN_OK && p.name[0]) {
            copy_cstr(buf, size, p.name);
            return true;
        }
    }
    if (S.players) {
        KilnPlayer p{};
        p.size = sizeof p;
        if (S.players->get(player, &p) == KILN_OK && p.name_len) {
            copy_cstr(buf, size, std::string_view(p.name, p.name_len));
            return true;
        }
    }
    return false;
}

// ============================================================================================= saves =========
struct SaveReg {
    Mod* mod;
    EmfSaveCallback cb;
    void* user;
    bool load;
    std::atomic<bool> dead{false};
};
std::mutex g_save_mutex;
std::vector<std::unique_ptr<SaveReg>> g_save_regs;
uint64_t g_save_seq = 0;
uint16_t g_last_slot = 0;

void register_save(bool load, EmfSaveCallback cb, void* user, Mod* m) {
    if (!cb) return;
    if (!S.saves) unsupported(m, load ? "on_load" : "on_save", "kiln.saves is not available");
    auto r = std::make_unique<SaveReg>();
    r->mod = mod_of_address(reinterpret_cast<const void*>(cb));
    if (!r->mod) r->mod = m;
    r->cb = cb;
    r->user = user;
    r->load = load;
    std::lock_guard lock(g_save_mutex);
    g_save_regs.push_back(std::move(r));
}
void api_on_save(EmfSaveCallback cb, void* user) { register_save(false, cb, user, caller_mod(_ReturnAddress())); }
void api_on_load(EmfSaveCallback cb, void* user) { register_save(true, cb, user, caller_mod(_ReturnAddress())); }

bool api_request_save() {
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.saves) return unsupported(m, "request_save", "kiln.saves is not available"), false;
    if (!g_api->world(KILN_WORLD_SERVER)) return refuse(m, "request_save", "this game hosts no world"), false;
    const KilnResult rc = S.saves->request(KILN_SAVE_REASON_REQUEST);
    if (rc != KILN_OK) return refuse(m, "request_save", "the save request is off on this game build"), false;
    return true;
}

void fire_saves(bool load, uint16_t slot) {
    std::vector<SaveReg*> regs;
    {
        std::lock_guard lock(g_save_mutex);
        for (const auto& r : g_save_regs)
            if (r->load == load && !r->dead) regs.push_back(r.get());
    }
    for (SaveReg* r : regs) {
        struct Call {
            EmfSaveCallback cb;
            uint16_t slot;
            void* user;
        } c{r->cb, slot, r->user};
        ContextScope scope(r->mod, nullptr, false);
        if (!guarded(r->mod, load ? "an on_load callback" : "an on_save callback",
                     [](void* p) {
                         auto* k = static_cast<Call*>(p);
                         k->cb(k->slot, k->user);
                     },
                     &c))
            r->dead = true;
    }
}

void poll_saves() {
    if (!S.saves) return;
    for (int i = 0; i < 16; ++i) {
        KilnSaveEvent e{};
        e.size = sizeof e;
        if (S.saves->next_event(g_save_seq, &e) != KILN_OK) break;
        g_save_seq = e.seq;
        if (e.kind == KILN_SAVE_EVENT_LOADED) {
            g_last_slot = e.slot;
            logf(KILN_LOG_INFO, "world save loaded (slot 0x%04x, revision %u): on_load", e.slot, e.revision);
            persisted_settings_after_load(e.slot);
            fire_saves(true, e.slot);
        } else if (e.kind == KILN_SAVE_EVENT_WRITTEN) {
            if (e.slot) g_last_slot = e.slot;
            fire_saves(false, e.slot ? e.slot : g_last_slot);
        }
    }
}

// ============================================================================================= combat ========
// (attribute_get / attribute_add are in api_attributes.cpp)
bool damage_now(Mod* m, uint32_t attacker, uint32_t target, int32_t amount) {
    KilnDamageRequest r{};
    r.size = sizeof r;
    r.target = target;
    r.attacker = attacker;
    r.amount = amount;
    r.reaction = 2;  // EMF: always a light hit reaction
    r.aggro = attacker ? 1.0f : 0.0f;
    const KilnResult rc = S.combat->damage(owner_of(m), &r, nullptr);
    if (rc != KILN_OK) refuse(m, "damage", rc == KILN_ERR_NOT_FOUND ? "no such target" : "hits are off on this game build");
    return rc == KILN_OK;
}

bool api_damage(EmfWorld* w, uint32_t attacker, uint32_t target, int32_t amount) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.combat) return unsupported(m, "damage", "kiln.combat is not available"), false;
    if (!is_server_world(w) || amount <= 0 || !target) return refuse(m, "damage", "server world, target and amount > 0"), false;
    if (g_api->on_tick_thread()) return damage_now(m, attacker, target, amount);
    return post_tick([m, attacker, target, amount] { damage_now(m, attacker, target, amount); });
}

// ============================================================================================= teleport ======
struct TpReq {
    Mod* mod;
    EmfTeleportCallback cb;
    void* user;
    uint32_t flags;
    uint32_t entity;
    std::shared_ptr<CommandRun> cmd;
};
struct ReadyTimer {
    uint64_t due_ms;
    std::unique_ptr<TpReq> req;
    EmfTeleportResult r;
};
std::mutex g_tp_mutex;
std::vector<ReadyTimer> g_ready;

void call_tp(TpReq* q, const EmfTeleportResult& r) {
    if (!q->cb) return;
    struct Call {
        EmfTeleportCallback cb;
        EmfWorld* w;
        const EmfTeleportResult* r;
        void* user;
    } c{q->cb, world_of_kind(KILN_WORLD_SERVER), &r, q->user};
    ContextScope scope(q->mod, c.w, true, q->cmd);
    guarded(q->mod, "a teleport callback", [](void* p) {
        auto* k = static_cast<Call*>(p);
        k->cb(k->w, k->r, k->user);
    }, &c);
}

void KILN_CALL tp_done(const KilnTeleportResult* k, void* p) {
    std::unique_ptr<TpReq> q(static_cast<TpReq*>(p));
    EmfTeleportResult r{};
    r.size = sizeof r;
    r.entity = k->entity;
    r.status = k->status == KILN_TELEPORT_MOVED ? EMF_TELEPORT_MOVED : EMF_TELEPORT_FAILED;
    if (k->flags & KILN_TELEPORT_OFF_TARGET) r.flags |= EMF_TELEPORT_OFF_TARGET;
    if (k->flags & KILN_TELEPORT_RAW_TARGET) r.flags |= EMF_TELEPORT_RAW_TARGET;
    r.x = k->position.x;
    r.y = k->position.y;
    r.z = k->position.z;
    r.distance = k->distance;
    const std::string reason(sv(k->reason));
    r.reason = reason.c_str();
    const bool ready = r.status == EMF_TELEPORT_MOVED && (q->flags & EMF_TELEPORT_NOTIFY_READY);
    if (!ready) r.flags |= EMF_TELEPORT_LAST;
    call_tp(q.get(), r);
    if (ready) {
        // Kiln cannot tell yet when a player's game has loaded the destination: READY follows after 1 s for players
        // (EMF measured 0.3-3 s), at once for other entities.
        EmfTeleportResult rr = r;
        rr.status = EMF_TELEPORT_READY;
        rr.flags = (r.flags & (EMF_TELEPORT_OFF_TARGET | EMF_TELEPORT_RAW_TARGET)) | EMF_TELEPORT_LAST;
        rr.reason = "";
        if (q->entity > 1023) {
            call_tp(q.get(), rr);
            return;
        }
        std::lock_guard lock(g_tp_mutex);
        g_ready.push_back({now_ms() + 1000, std::move(q), rr});
    }
}

void poll_ready() {
    std::vector<ReadyTimer> due;
    {
        std::lock_guard lock(g_tp_mutex);
        const uint64_t now = now_ms();
        for (size_t i = 0; i < g_ready.size();) {
            if (g_ready[i].due_ms <= now) {
                due.push_back(std::move(g_ready[i]));
                g_ready.erase(g_ready.begin() + static_cast<ptrdiff_t>(i));
            } else {
                ++i;
            }
        }
    }
    for (auto& t : due) {
        t.r.reason = "";
        call_tp(t.req.get(), t.r);
    }
}

bool api_teleport(EmfWorld* w, const EmfTeleport* t) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.world) return unsupported(m, "teleport", "kiln.world is not available"), false;
    if (!is_server_world(w)) return refuse(m, "teleport", "not the server world", true), false;
    if (!t || t->size < offsetof(EmfTeleport, user) + sizeof(t->user) || !t->entity)
        return refuse(m, "teleport", "bad request"), false;
    if (t->flags & ~0x0Fu) return refuse(m, "teleport", "unknown flags"), false;
    if (!t->spawn_point && (!std::isfinite(t->x) || !std::isfinite(t->y) || !std::isfinite(t->z) || t->x < 0 ||
                            t->x > 10240 || t->z < 0 || t->z > 10240 || t->y < -1000 || t->y > 5000))
        return refuse(m, "teleport", "x and z must be 0..10240, y -1000..5000"), false;
    KilnTeleportRequest r{};
    r.size = sizeof r;
    r.entity = t->entity;
    r.target = KilnVec3{t->x, t->y, t->z};
    r.yaw_deg = t->yaw_deg;
    r.spawn_point = t->spawn_point;
    if (t->flags & EMF_TELEPORT_FIND_GROUND) r.flags |= KILN_TELEPORT_FIND_GROUND;
    if (t->flags & EMF_TELEPORT_FACE_YAW) r.flags |= KILN_TELEPORT_FACE_YAW;
    if (t->flags & EMF_TELEPORT_CHECKPOINT) r.flags |= KILN_TELEPORT_CHECKPOINT;
    auto* q = new TpReq{m, t->cb, t->user, t->flags, t->entity, t_ctx.cmd};
    r.callback = &tp_done;
    r.user = q;
    uint64_t id = 0;
    const KilnResult rc = S.world->teleport(owner_of(m), &r, &id);
    if (rc != KILN_OK) {
        delete q;
        refuse(m, "teleport", rc == KILN_ERR_UNAVAILABLE ? "teleport is off on this game build" : "bad numbers or flags");
        return false;
    }
    return true;
}

// ============================================================================================= world settings
// Strings handed to mods (EmfWorldSettingInfo.key/alias/group) must stay valid: interned, never freed.
const char* intern(const std::string& s) {
    static std::mutex mu;
    static std::map<std::string, std::unique_ptr<std::string>> pool;
    std::lock_guard lock(mu);
    auto& p = pool[s];
    if (!p) p = std::make_unique<std::string>(s);
    return p->c_str();
}

struct WsEntry {
    uint32_t index;
    std::string key, alias, group;
    const char* key_c = "";
    const char* alias_c = "";
    const char* group_c = "";
    uint32_t kind;  // KILN_SETTING_*
    uint32_t emf_type;
    double min, max;
    bool needs_client;
    double value = 0, world = 0;  // EMF units (minutes for durations)
};
struct WsChange {
    uint32_t index;  // UINT32_MAX = all (reset)
    bool reset;
    double value;
    bool persist;
    Mod* mod;
};
struct WsWatch {
    Mod* mod;
    EmfWorldSettingCallback cb;
    void* user;
    std::atomic<bool> dead{false};
};
std::mutex g_ws_mutex;
std::vector<WsEntry> g_ws;  // cache, refreshed in Pre when kiln.settings' revision moves
uint64_t g_ws_revision = UINT64_MAX;
std::vector<WsChange> g_ws_queue;
std::vector<std::unique_ptr<WsWatch>> g_ws_watches;
std::map<std::string, double> g_ws_persist;  // kept for the current slot: key -> EMF value
uint16_t g_ws_persist_slot = 0;

const std::pair<const char*, const char*> kAliases[] = {
    {"durability", "enableDurability"}, {"starving", "enableStarvingDebuff"}, {"food_buff", "foodBuffDurationFactor"},
    {"hunger_minutes", "fromHungerToStarving"}, {"tombstone", "tombstoneMode"},
    {"glider_turbulence", "enableGliderTurbulences"}, {"mining", "miningDamageFactor"},
    {"plant_growth", "plantGrowthSpeedFactor"}, {"loot", "resourceDropStackAmountFactor"},
    {"factory_speed", "factoryProductionSpeedFactor"}, {"salvage_runes", "perkUpgradeRecyclingFactor"},
    {"upgrade_cost", "perkCostFactor"}, {"xp_combat", "experienceCombatFactor"}, {"xp_mining", "experienceMiningFactor"},
    {"xp_explore", "experienceExplorationQuestsFactor"}, {"day_minutes", "dayTimeDuration"},
    {"night_minutes", "nightTimeDuration"}, {"weather", "weatherFrequency"}, {"enemy_amount", "randomSpawnerAmount"},
    {"altar_shroud_level", "flameLevelFogRemoval"},
};

double to_emf(uint32_t kind, const KilnSettingValue& v) {
    if (kind == KILN_SETTING_FLOAT) return v.number;
    if (kind == KILN_SETTING_DURATION) return static_cast<double>(v.integer) / 60e9;
    return static_cast<double>(v.integer);
}

std::string ws_file(uint16_t slot) {
    char b[16];
    std::snprintf(b, sizeof b, "%04x.txt", slot);
    return b;
}

// tick thread: (re)builds the cache. Returns the keys whose value changed since the last refresh.
std::vector<std::pair<std::string, double>> ws_refresh() {
    std::vector<std::pair<std::string, double>> changed;
    if (!S.settings) return changed;
    KilnSettingsStatus st{};
    st.size = sizeof st;
    if (S.settings->status(&st) != KILN_OK || !st.ready) return changed;
    std::lock_guard lock(g_ws_mutex);
    if (st.revision == g_ws_revision && !g_ws.empty()) return changed;
    const bool first = g_ws.empty();
    g_ws_revision = st.revision;
    const uint32_t n = S.settings->count();
    std::vector<WsEntry> fresh;
    for (uint32_t i = 0; i < n; ++i) {
        KilnSettingInfo in{};
        in.size = sizeof in;
        if (S.settings->info(i, &in) != KILN_OK || (in.flags & KILN_SETTING_F_UNKNOWN)) continue;
        WsEntry e;
        e.index = i;
        e.key = std::string(sv(in.name));
        e.group = std::string(sv(in.group));
        e.kind = in.kind;
        e.emf_type = in.kind == KILN_SETTING_BOOL ? EMF_WS_BOOL
                     : in.kind == KILN_SETTING_ENUM ? EMF_WS_ENUM
                     : in.kind == KILN_SETTING_DURATION ? EMF_WS_MINUTES
                     : in.kind == KILN_SETTING_INT ? EMF_WS_WHOLE
                                                    : EMF_WS_FLOAT;
        for (const auto& [a, k] : kAliases)
            if (iequals(k, e.key)) e.alias = a;
        const double scale = in.kind == KILN_SETTING_DURATION ? 1.0 / 60e9 : 1.0;
        e.min = in.min * scale;
        e.max = in.max * scale;
        e.needs_client = (in.flags & KILN_SETTING_F_CLIENT) != 0;
        e.key_c = intern(e.key);
        e.alias_c = intern(e.alias);
        e.group_c = intern(e.group);
        e.value = to_emf(in.kind, in.value);
        e.world = to_emf(in.kind, in.initial);
        fresh.push_back(e);
    }
    if (!first)
        for (const auto& e : fresh)
            for (const auto& o : g_ws)
                if (o.index == e.index && o.value != e.value) changed.push_back({e.key, e.value});
    g_ws = std::move(fresh);
    return changed;
}

const WsEntry* ws_find_locked(std::string_view key) {
    for (const auto& e : g_ws)
        if (iequals(e.key, key) || (!e.alias.empty() && iequals(e.alias, key))) return &e;
    return nullptr;
}

bool api_ws_get(const char* key, double* value) {
    if (!key || !value || !g_api->world(KILN_WORLD_SERVER)) return false;
    std::lock_guard lock(g_ws_mutex);
    const WsEntry* e = ws_find_locked(key);
    if (!e) return false;
    *value = e->value;
    return true;
}

bool api_ws_set(const char* key, double value, uint32_t flags) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.settings) return unsupported(m, "world_setting_set", "kiln.settings is not available"), false;
    if (!g_api->world(KILN_WORLD_SERVER)) return refuse(m, "world_setting_set", "no server world"), false;
    if (flags & ~EMF_WS_PERSIST) return refuse(m, "world_setting_set", "unknown flag bits"), false;
    std::lock_guard lock(g_ws_mutex);
    const WsEntry* e = key ? ws_find_locked(key) : nullptr;
    if (!e) return refuse(m, "world_setting_set", format("unknown setting %s", key ? key : "?").c_str()), false;
    const bool whole = e->kind != KILN_SETTING_FLOAT && e->kind != KILN_SETTING_DURATION;
    if (!std::isfinite(value) || value < e->min - 1e-9 || value > e->max + 1e-9 || (whole && value != std::floor(value))) {
        refuse(m, "world_setting_set",
               format("%s: allowed %g to %g%s", e->key.c_str(), e->min, e->max, whole ? " (whole)" : "").c_str());
        return false;
    }
    if ((flags & EMF_WS_PERSIST) && !g_ws_persist_slot)
        return refuse(m, "world_setting_set", "persist: no world save has loaded in this session yet"), false;
    g_ws_queue.push_back({e->index, false, value, (flags & EMF_WS_PERSIST) != 0, m});
    return true;
}

bool api_ws_reset(const char* key, uint32_t flags) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!S.settings) return unsupported(m, "world_setting_reset", "kiln.settings is not available"), false;
    if (!g_api->world(KILN_WORLD_SERVER)) return refuse(m, "world_setting_reset", "no server world"), false;
    if (flags & ~EMF_WS_PERSIST) return refuse(m, "world_setting_reset", "unknown flag bits"), false;
    std::lock_guard lock(g_ws_mutex);
    uint32_t index = UINT32_MAX;
    if (key && !iequals(key, "all")) {
        const WsEntry* e = ws_find_locked(key);
        if (!e) return refuse(m, "world_setting_reset", format("unknown setting %s", key).c_str()), false;
        index = e->index;
    }
    g_ws_queue.push_back({index, true, 0, (flags & EMF_WS_PERSIST) != 0, m});
    return true;
}

bool api_ws_on_changed(EmfWorldSettingCallback cb, void* user) {
    if (!cb || !S.settings) return false;
    auto w = std::make_unique<WsWatch>();
    w->mod = mod_of_address(reinterpret_cast<const void*>(cb));
    if (!w->mod) w->mod = caller_mod(_ReturnAddress());
    w->cb = cb;
    w->user = user;
    std::lock_guard lock(g_ws_mutex);
    g_ws_watches.push_back(std::move(w));
    return true;
}

bool api_ws_info(uint32_t index, EmfWorldSettingInfo* out) {
    if (!out || out->size < sizeof(EmfWorldSettingInfo) || !g_api->world(KILN_WORLD_SERVER)) return false;
    std::lock_guard lock(g_ws_mutex);
    if (index >= g_ws.size()) return false;
    const WsEntry& e = g_ws[index];
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof(EmfWorldSettingInfo));
    out->size = size;
    out->type = e.emf_type;
    out->key = e.key_c;
    out->alias = e.alias_c;
    out->group = e.group_c;
    out->min = e.min;
    out->max = e.max;
    out->value = e.value;
    out->world = e.world;
    if (e.value != e.world) out->flags |= EMF_WS_CHANGED;
    if (e.needs_client) out->flags |= EMF_WS_NEEDS_CLIENT;
    return true;
}

void save_persist_locked() {
    if (!g_ws_persist_slot) return;
    std::string text = "; EMF world settings kept for this world slot (kiln-emf); durations in minutes\n";
    for (const auto& [k, v] : g_ws_persist) text += k + "=" + format("%.10g", v) + "\n";
    write_file_atomic(P.config / L"emf" / L"world_settings" / to_wide(ws_file(g_ws_persist_slot)), text);
}

void fire_ws(const std::vector<std::pair<std::string, double>>& changed) {
    if (changed.empty()) return;
    std::vector<WsWatch*> watches;
    {
        std::lock_guard lock(g_ws_mutex);
        for (const auto& w : g_ws_watches)
            if (!w->dead) watches.push_back(w.get());
    }
    for (const auto& [k, v] : changed)
        for (WsWatch* w : watches) {
            struct Call {
                EmfWorldSettingCallback cb;
                const char* key;
                double v;
                void* user;
            } c{w->cb, k.c_str(), v, w->user};
            ContextScope scope(w->mod, world_of_kind(KILN_WORLD_SERVER), true);
            if (!guarded(w->mod, "an on_world_setting_changed callback",
                         [](void* p) {
                             auto* x = static_cast<Call*>(p);
                             x->cb(x->key, x->v, x->user);
                         },
                         &c))
                w->dead = true;
        }
}

void apply_ws_queue() {
    std::vector<WsChange> q;
    {
        std::lock_guard lock(g_ws_mutex);
        q.swap(g_ws_queue);
    }
    if (q.empty() || !S.settings) return;
    for (const WsChange& c : q) {
        if (c.reset) {
            if (S.settings->reset(owner_of(c.mod), c.index, 0) != KILN_OK) {
                char err[200] = "";
                S.settings->last_error(err, sizeof err, nullptr);
                refuse(c.mod, "world_setting_reset", err);
            }
            if (c.persist) {
                std::lock_guard lock(g_ws_mutex);
                if (c.index == UINT32_MAX) g_ws_persist.clear();
                else
                    for (const auto& e : g_ws)
                        if (e.index == c.index) g_ws_persist.erase(e.key);
                save_persist_locked();
            }
            continue;
        }
        KilnSettingValue v{};
        uint32_t kind = KILN_SETTING_FLOAT;
        std::string key;
        {
            std::lock_guard lock(g_ws_mutex);
            for (const auto& e : g_ws)
                if (e.index == c.index) kind = e.kind, key = e.key;
        }
        v.kind = kind;
        if (kind == KILN_SETTING_FLOAT) v.number = c.value;
        else if (kind == KILN_SETTING_DURATION) v.integer = static_cast<int64_t>(std::llround(c.value * 60e9));
        else v.integer = static_cast<int64_t>(std::llround(c.value));
        v.number = kind == KILN_SETTING_FLOAT ? c.value : static_cast<double>(v.integer);
        if (S.settings->set(owner_of(c.mod), c.index, &v, 0, nullptr) != KILN_OK) {
            char err[200] = "";
            S.settings->last_error(err, sizeof err, nullptr);
            refuse(c.mod, "world_setting_set", err);
            continue;
        }
        mod_logf(c.mod, KILN_LOG_INFO, "world setting %s = %g%s", key.c_str(), c.value, c.persist ? " (kept for this world)" : "");
        if (c.persist) {
            std::lock_guard lock(g_ws_mutex);
            g_ws_persist[key] = c.value;
            save_persist_locked();
        }
    }
}

}  // namespace

void persisted_settings_after_load(uint16_t slot) {
    std::map<std::string, double> kept;
    {
        std::lock_guard lock(g_ws_mutex);
        g_ws_persist_slot = slot;
        g_ws_persist.clear();
        std::string text;
        if (!slot || !read_file(P.config / L"emf" / L"world_settings" / to_wide(ws_file(slot)), &text)) return;
        size_t i = 0;
        while (i < text.size()) {
            size_t j = text.find('\n', i);
            if (j == std::string::npos) j = text.size();
            std::string line = text.substr(i, j - i);
            i = j + 1;
            if (line.empty() || line[0] == ';') continue;
            const size_t eq = line.find('=');
            if (eq == std::string::npos) continue;
            char* end = nullptr;
            const double v = std::strtod(line.c_str() + eq + 1, &end);
            g_ws_persist[line.substr(0, eq)] = v;
        }
        kept = g_ws_persist;
    }
    for (const auto& [k, v] : kept) {
        logf(KILN_LOG_INFO, "world setting %s = %g kept for slot 0x%04x: applying it", k.c_str(), v, slot);
        std::lock_guard lock(g_ws_mutex);
        if (const WsEntry* e = ws_find_locked(k)) g_ws_queue.push_back({e->index, false, v, false, nullptr});
        else logf(KILN_LOG_WARN, "kept world setting %s: unknown setting, skipped", k.c_str());
    }
}

void game_server_pre() {
    poll_saves();
    ws_refresh();
    apply_ws_queue();
    fire_ws(ws_refresh());
    poll_ready();
}

void game_on_ready() {
    // nothing to subscribe: the parts above are polled from the server world's Pre (dispatch_server_pre)
}

// ============================================================================================= HUD / notes ===
namespace {
struct HudLine {
    std::string text;
    uint32_t place, rgba;
};
std::mutex g_hud_mutex;
std::map<std::string, HudLine> g_hud;  // by id
struct Note {
    std::string text;
    uint64_t until;
};
std::deque<Note> g_notes;
KilnHandle g_frame = 0;

void update_demand() {
    if (S.ui) S.ui->set_demand((g_hud.empty() && g_notes.empty() && !windows_want_frame()) ? 0u : 1u);
}

void KILN_CALL hud_frame(const KilnUiFrame* f, void*) {
    std::lock_guard lock(g_hud_mutex);
    const uint64_t now = now_ms();
    while (!g_notes.empty() && g_notes.front().until < now) g_notes.pop_front();
    const float lh = 20.0f * (f->dpi_scale > 0 ? f->dpi_scale : 1.0f);
    float y[3] = {f->height * 0.25f, f->height - 40.0f * (f->dpi_scale > 0 ? f->dpi_scale : 1.0f), f->height - 40.0f * (f->dpi_scale > 0 ? f->dpi_scale : 1.0f)};
    // bottom places grow upwards: count lines first
    int count[3] = {0, 0, 0};
    for (const auto& [id, l] : g_hud) count[std::min<uint32_t>(l.place, 2)]++;
    y[1] -= lh * static_cast<float>(count[1] - 1);
    y[2] -= lh * static_cast<float>(count[2] - 1);
    for (const auto& [id, l] : g_hud) {
        const uint32_t p = std::min<uint32_t>(l.place, 2);
        float w = 0, h = 0;
        S.ui->text_size(kstr(l.text), 0, &w, &h);
        const float x = p == 2 ? 24.0f : f->width - w - 24.0f;
        S.ui->draw_text(x, y[p], l.rgba ? l.rgba : 0xFFFFFFE6u, 0, kstr(l.text));
        y[p] += lh;
    }
    float ny = 60.0f;
    for (const auto& n : g_notes) {
        float w = 0, h = 0;
        S.ui->text_size(kstr(n.text), 0, &w, &h);
        S.ui->draw_text((f->width - w) * 0.5f, ny, 0xFFE9A0FFu, 0, kstr(n.text));
        ny += lh;
    }
    if (g_hud.empty() && g_notes.empty() && !windows_want_frame()) S.ui->set_demand(0);
}

bool ensure_frame() {
    if (!S.ui) return false;
    if (!g_frame && S.ui->subscribe_frame(g_owner, &hud_frame, nullptr, &g_frame) != KILN_OK) g_frame = 0;
    return g_frame != 0;
}

bool valid_hud_id(std::string_view id) {
    if (id.empty() || id.size() > 48) return false;
    for (char c : id)
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'))
            return false;
    return true;
}

bool api_hud_set(const char* id, const char* text, uint32_t place, uint32_t rgba) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!id || !valid_hud_id(id) || place > EMF_HUD_BOTTOM_LEFT) return refuse(m, "hud_set", "bad id or place", true), false;
    std::lock_guard lock(g_hud_mutex);
    if (!text || !*text) {
        g_hud.erase(id);
        update_demand();
        return true;
    }
    if (!g_hud.count(id) && g_hud.size() >= 32) return refuse(m, "hud_set", "32 lines exist", true), false;
    std::string t(text);
    if (t.size() > 160) t.resize(160);
    g_hud[id] = HudLine{t, place, rgba};
    if (g_side == KILN_SIDE_CLIENT && !S.ui) unsupported(m, "hud_set", "kiln.ui is not available: the line is kept, not shown");
    if (S.ui) {
        ensure_frame();
        update_demand();
    }
    return true;
}

void api_notify(const char* fmt, ...) {
    Mod* m = caller_mod(_ReturnAddress());
    va_list ap;
    va_start(ap, fmt);
    const std::string text = vformat(fmt ? fmt : "", ap);
    va_end(ap);
    mod_logf(m, KILN_LOG_INFO, "[note] %s", text.c_str());
    if (!S.ui) return;
    std::lock_guard lock(g_hud_mutex);
    g_notes.push_back({text, now_ms() + 4000});
    while (g_notes.size() > 5) g_notes.pop_front();
    ensure_frame();
    update_demand();
}

}  // namespace

// A note of kiln-emf itself, not of a mod (the mod check's warning to the player). Any thread. It stays longer than a
// mod's note: it is one long sentence, shown once per session.
void core_note(std::string_view text) {
    logf(KILN_LOG_INFO, "[note] %.*s", static_cast<int>(text.size()), text.data());
    if (!S.ui) return;
    std::lock_guard lock(g_hud_mutex);
    g_notes.push_back({std::string(text), now_ms() + 10000});
    while (g_notes.size() > 5) g_notes.pop_front();
    ensure_frame();
    update_demand();
}

void hud_on_ready() {}

uint32_t spawn_for(Mod* m, EmfWorld* w, const uint8_t guid[16], double x, double y, double z, const char* fn) {
    return do_spawn(m, w, guid, x, y, z, fn);
}

void ui_refresh_demand() {
    std::lock_guard lock(g_hud_mutex);
    update_demand();
}

void fill_game(EmfApi* t) {
    t->for_each_item = &api_for_each_item;
    t->find_item = &api_find_item;
    t->item_info = &api_item_info;
    t->give_item = &api_give_item;
    t->spawn = &api_spawn;
    t->spawn_ex = &api_spawn_ex;
    t->destroy = &api_destroy;
    t->on_save = &api_on_save;
    t->on_load = &api_on_load;
    t->item_data = &api_item_data;
    t->set_item_max_stack = &api_set_item_max_stack;
    t->put_items = &api_put_items;
    t->put_items_ex = &api_put_items_ex;
    t->take_items = &api_take_items;
    t->notify = &api_notify;
    t->hud_set = &api_hud_set;
    t->damage = &api_damage;
    t->chat_tell = &api_chat_tell;
    t->chat_broadcast = &api_chat_broadcast;
    t->chat_tell_ex = &api_chat_tell_ex;            // v30
    t->chat_broadcast_ex = &api_chat_broadcast_ex;  // v30
    t->player_name = &api_player_name;
    t->request_save = &api_request_save;
    t->teleport = &api_teleport;
    t->world_setting_get = &api_ws_get;
    t->world_setting_set = &api_ws_set;
    t->world_setting_reset = &api_ws_reset;
    t->on_world_setting_changed = &api_ws_on_changed;
    t->world_setting_info = &api_ws_info;
}

}  // namespace kemf
