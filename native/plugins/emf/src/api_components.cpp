// SPDX-License-Identifier: MIT
// The EmfApi table, v23 change_component and v25 E prompts (spawn_offer, offer, offer_remove, offer_active).
//  - change_component: EMF's policy and four-moment queue (v26_model.h ComponentQueue). kiln-emf's SYSTEM program
//    "emf_components" (kiln.world, server world) pushes the engine's add / remove records with kiln.world's
//    queue_add / queue_remove; the checks and the answers run after each server step on the tick thread.
//  - Offers: the three run-time components the game adds to its own interactables (InteractionOfferRuntime,
//    ClientInteractionOffer, CheckPlayerInRange), added through the same queue; the accept is the game's
//    InteractionAcceptedEvent, read by an internal on_event listener. None of them is saved.
//  - spawn_offer spawns the template (EMF's spawn rules) and then offers on it, so the prompt appears a step or two after
//    the spawn (EMF adds the components with the spawn). Not removed in dev.20: the offer components go through the same
//    queue as every other change (kiln.gameplay's component_change, which Kiln.Gameplay Offers uses too, is also
//    after the spawn). kiln.spawn can take spawn-time components (KilnSpawnRequest.components, lab-checked), which would
//    remove the delay, but then a template that carries its own offer must be told apart before the spawn (offer()
//    refuses those today) and the registry needs a way to adopt components that arrived with the entity.
#include <intrin.h>

#include "emf_internal.h"
#include "v26_model.h"

namespace kemf {

namespace {

v26::ComponentQueue g_queue;
std::unique_ptr<v26::OfferService> g_offers;
KilnHandle g_program = 0;
uint32_t g_player_state = 0;  // hash of PlayerState
std::atomic<bool> g_stopping{false};
std::string g_off;  // why changing components is off ("" = on)

EmfWorld* server_world() { return reinterpret_cast<EmfWorld*>(g_api->world(KILN_WORLD_SERVER)); }
bool is_server(EmfWorld* w) { return !g_stopping && w && reinterpret_cast<uintptr_t>(w) == g_api->world(KILN_WORLD_SERVER); }
std::string owner_key(const Mod* m) { return m ? m->id : std::string("?"); }

bool has_component(uint32_t entity, uint32_t hash) {
    if (!S.world) return false;
    uint32_t size = 0;
    return S.world->component(entity, hash, &size) != nullptr;
}

v26::ComponentEnv env() {
    v26::ComponentEnv e;
    e.exists = [](uint32_t id) { return S.world && S.world->exists(id); };
    e.has = &has_component;
    e.is_player = [](uint32_t id) { return (id >= 1 && id <= 16) || (g_player_state && has_component(id, g_player_state)); };
    return e;
}

void KILN_CALL program_each(KilnProgramCtx* ctx, uint64_t*, KilnEntity, void*) {
    if (!g_queue.pending()) return;
    g_queue.run(server_world(), server_session(), env(), [ctx](uint32_t entity, uint32_t hash, bool add, const std::vector<uint8_t>& data) {
        const KilnResult rc = add ? S.world->queue_add(ctx, entity, hash, data.empty() ? nullptr : data.data(),
                                                       static_cast<uint32_t>(data.size()))
                                  : S.world->queue_remove(ctx, entity, hash);
        return rc == KILN_OK;
    });
}

void answer_all(std::vector<v26::ComponentAnswer>& ans) {
    for (auto& a : ans)
        if (a.done) a.done(a.world, a.status, a.why);
    ans.clear();
}

// change_component's checks that need no world, then the queue.
bool request_change(v26::ComponentRequest& r, std::string* why) {
    if (g_stopping) {
        if (why) *why = "the server world is stopping";
        return false;
    }
    if (!g_off.empty()) {
        if (why) *why = "changing components is off: " + g_off;
        return false;
    }
    if (r.entity >= 1 && r.entity <= 16 && !r.internal) {
        if (why) *why = "never a player";
        return false;
    }
    return g_queue.request(r, why);
}

// ============================================================================================= v23 ===========
struct ComponentCb {
    Mod* mod;
    EmfComponentCallback cb;
    void* user;
    uint32_t entity, hash;
};

bool api_change_component(EmfWorld* w, const EmfComponentChange* c) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!c || c->size < v26::kComponentChangeV23Size)
        return refuse(m, "change_component", "no request, or its size field is too small", true), false;
    if (!S.world) return unsupported(m, "change_component", "kiln.world is not available"), false;
    if (c->op != EMF_COMPONENT_ADD && c->op != EMF_COMPONENT_REMOVE)
        return refuse(m, "change_component", "op must be EMF_COMPONENT_ADD or EMF_COMPONENT_REMOVE"), false;
    if (!is_server(w)) return refuse(m, "change_component", "the server world only (a world this game hosts)"), false;
    v26::ComponentRequest r;
    r.world = w;
    r.session = server_session();
    r.entity = c->entity;
    r.hash = c->hash;
    r.add = c->op == EMF_COMPONENT_ADD;
    if (r.add && c->data) {
        if (!c->data_size || c->data_size > 4096)
            return refuse(m, "change_component", "data given, so data_size must be the component's size"), false;
        const auto* p = static_cast<const uint8_t*>(c->data);
        r.data.assign(p, p + c->data_size);
    }
    r.flags = c->flags;
    r.owner = owner_key(m);
    if (c->cb) {
        auto k = std::make_shared<ComponentCb>(ComponentCb{m, c->cb, c->user, c->entity, c->hash});
        r.done = [k](EmfWorld* world, uint32_t status, const std::string& why) {
            struct Call {
                ComponentCb* k;
                EmfWorld* w;
                uint32_t status;
                const char* why;
            } call{k.get(), world, status, why.c_str()};
            ContextScope scope(k->mod, world, world != nullptr);
            guarded(k->mod, "a change_component callback",
                    [](void* p) {
                        auto* x = static_cast<Call*>(p);
                        x->k->cb(x->w, x->k->entity, x->k->hash, x->status, x->why, x->k->user);
                    },
                    &call);
        };
    }
    std::string why;
    if (request_change(r, &why)) return true;
    refuse(m, "change_component", (v26::component_name(c->hash) + " on " + std::to_string(c->entity) + ": " + why).c_str());
    return false;
}

// ============================================================================================= v25 ===========
bool offers_on(Mod* m, EmfWorld* w, const char* fn) {
    if (!g_offers) return unsupported(m, fn, g_off.empty() ? "kiln.world is not available" : g_off.c_str()), false;
    if (!is_server(w)) return refuse(m, fn, "the server world only (a world this game hosts)"), false;
    if (!g_api->on_tick_thread() || !t_ctx.world_cb || t_ctx.world != w) return refuse(m, fn, "from a world callback on the server world"), false;
    return true;
}

v26::OfferCallback offer_callback(Mod* m, EmfOfferCallback cb, void* user) {
    if (!cb) return nullptr;
    auto broken = std::make_shared<std::atomic<bool>>(false);
    return [m, cb, user, broken](EmfWorld* w, uint32_t player, uint32_t entity) {
        if (broken->load()) return;
        struct Call {
            EmfOfferCallback cb;
            EmfWorld* w;
            uint32_t player, entity;
            void* user;
        } c{cb, w, player, entity, user};
        ContextScope scope(m, w, true);
        if (!guarded(m, "an offer callback",
                     [](void* p) {
                         auto* x = static_cast<Call*>(p);
                         x->cb(x->w, x->player, x->entity, x->user);
                     },
                     &c))
            broken->store(true);
    };
}

bool do_offer(Mod* m, EmfWorld* w, uint32_t entity, const EmfOffer* o, EmfOfferCallback cb, void* user, const char* fn) {
    v26::OfferSpec s;
    if (const std::string bad = v26::offer_spec_from(o, &s); !bad.empty()) return refuse(m, fn, bad.c_str()), false;
    if ((entity >= 1 && entity <= 16) || (g_player_state && has_component(entity, g_player_state)))
        return refuse(m, fn, "never a player"), false;
    KilnVec3 pos{};
    if (!S.world->exists(entity) || S.world->position(entity, &pos) != KILN_OK)
        return refuse(m, fn, "no such entity (or it has no position)"), false;
    std::string why;
    if (g_offers->offer(w, server_session(), entity, s, owner_key(m), offer_callback(m, cb, user), &why)) return true;
    refuse(m, fn, (std::to_string(entity) + ": " + why).c_str());
    return false;
}

uint32_t api_spawn_offer(EmfWorld* w, const uint8_t guid[16], double x, double y, double z, const EmfOffer* o,
                         EmfOfferCallback cb, void* user) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!offers_on(m, w, "spawn_offer") || !guid) return 0;
    v26::OfferSpec s;
    if (const std::string bad = v26::offer_spec_from(o, &s); !bad.empty()) return refuse(m, "spawn_offer", bad.c_str()), 0;
    const uint32_t id = spawn_for(m, w, guid, x, y, z, "spawn_offer");
    if (!id) return 0;
    std::string why;
    if (!g_offers->offer(w, server_session(), id, s, owner_key(m), offer_callback(m, cb, user), &why))
        refuse(m, "spawn_offer", ("spawned " + std::to_string(id) + ", the offer was refused: " + why).c_str());
    return id;
}

bool api_offer(EmfWorld* w, uint32_t entity, const EmfOffer* o, EmfOfferCallback cb, void* user) {
    Mod* m = caller_mod(_ReturnAddress());
    return offers_on(m, w, "offer") && do_offer(m, w, entity, o, cb, user, "offer");
}

bool api_offer_remove(EmfWorld* w, uint32_t entity) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!offers_on(m, w, "offer_remove")) return false;
    std::string why;
    if (g_offers->remove(w, server_session(), entity, owner_key(m), &why)) return true;
    refuse(m, "offer_remove", (std::to_string(entity) + ": " + why).c_str());
    return false;
}

bool api_offer_active(EmfWorld* w, uint32_t entity) {
    Mod* m = caller_mod(_ReturnAddress());
    return is_server(w) && g_offers && entity && g_offers->active(server_session(), entity, owner_key(m));
}

void on_accept_event(EmfWorld* w, const void* event, uint32_t size, void*) {
    if (g_offers) g_offers->on_accept(w, server_session(), event, size);
}

}  // namespace

void components_on_ready() {
    if (!S.world) {
        g_off = "kiln.world is not available";
        return;
    }
    g_player_state = S.world->component_hash(kstr("PlayerState"));
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
    d.name = kstr("emf_components");
    d.each = &program_each;
    if (S.world->register_program(g_owner, &d, &g_program) != KILN_OK) {
        g_off = "kiln.world refused the emf_components program";
        g_queue.enabled = false;
        logf(KILN_LOG_WARN, "change_component and offers are off: %s", g_off.c_str());
        return;
    }
    v26::OfferDeps deps;
    deps.has = [](uint32_t entity, uint32_t hash) {
        if (hash == 0) {
            KilnVec3 p{};
            return S.world->exists(entity) && S.world->position(entity, &p) == KILN_OK;
        }
        return has_component(entity, hash);
    };
    deps.read_offer = [](uint32_t entity) -> std::optional<std::pair<uint32_t, uint32_t>> {
        uint32_t size = 0;
        const auto* p = static_cast<const uint8_t*>(S.world->component(entity, v26::kInteractionOfferRuntime, &size));
        uint32_t seq = 0, verb = 0;
        if (!p || size < 20 || !seh_read_u32(reinterpret_cast<uintptr_t>(p + 8), &seq) ||
            !seh_read_u32(reinterpret_cast<uintptr_t>(p + 16), &verb))
            return std::nullopt;
        return std::make_pair(seq, verb);
    };
    deps.change = [](v26::ComponentRequest& r, std::string* why) { return request_change(r, why); };
    deps.log = [](const std::string& line) { logf(KILN_LOG_INFO, "%s", line.c_str()); };
    g_offers = std::make_unique<v26::OfferService>(std::move(deps));
    if (!events_add_internal("InteractionAcceptedEvent", v26::kInteractionAcceptedEventSize, &on_accept_event, nullptr))
        logf(KILN_LOG_WARN, "offers: the InteractionAcceptedEvent listener was not registered: accepts are not delivered");
    logf(KILN_LOG_INFO, "change_component (v23) and offers (v25): program emf_components registered");
}

void components_after_step(EmfWorld* w, uint8_t kind) {
    if (kind != EMF_WORLD_SERVER || !S.world) return;
    std::vector<v26::ComponentAnswer> ans;
    const uint32_t session = server_session();
    g_queue.after_step(w, session, env(), &ans);
    answer_all(ans);
    if (g_offers) g_offers->after_step(w, session);
}

bool server_requests_allowed() { return !g_stopping; }
void components_world_started() { g_stopping = false; }

void components_world_stopping() {
    g_stopping = true;
    std::vector<v26::ComponentAnswer> ans;
    g_queue.end_all("the world ended (a new world, or the menu)", &ans);
    // Retain the service object: already extracted internal completion closures refer to it.
    if (g_offers) g_offers->after_step(nullptr, server_session() + 1);
    answer_all(ans);
}

void fill_components(EmfApi* t) {
    t->change_component = &api_change_component;
    t->spawn_offer = &api_spawn_offer;
    t->offer = &api_offer;
    t->offer_remove = &api_offer_remove;
    t->offer_active = &api_offer_active;
}

}  // namespace kemf
