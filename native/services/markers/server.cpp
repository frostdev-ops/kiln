// SPDX-License-Identifier: MIT
#include "server.h"

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>

#include "text.h"

namespace kmk {

namespace {

void reset(KilnMarkerResult* r) {
    const uint32_t size = r->size;
    std::memset(r, 0, sizeof(KilnMarkerResult));
    r->size = size;
}

void say(KilnMarkerResult* r, const char* fmt, va_list v) { std::vsnprintf(r->reason, sizeof r->reason, fmt, v); }

KilnResult bad_arg(KilnMarkerResult* r, const char* fmt, ...) {
    va_list v;
    va_start(v, fmt);
    say(r, fmt, v);
    va_end(v);
    return KILN_ERR_INVALID_ARGUMENT;
}

KilnResult off(KilnMarkerResult* r, const char* capability) {
    std::snprintf(r->reason, sizeof r->reason, "%s", capability);
    return KILN_ERR_UNAVAILABLE;
}

KilnResult refuse(KilnMarkerResult* r, uint32_t status, const char* fmt, ...) {
    r->status = status;
    r->committed = 0;
    va_list v;
    va_start(v, fmt);
    say(r, fmt, v);
    va_end(v);
    return KILN_OK;
}

bool result_ok(const KilnMarkerResult* r) { return r && r->size >= sizeof(KilnMarkerResult); }

bool in_world(double x, double y, double z) {
    return std::isfinite(x) && std::isfinite(y) && std::isfinite(z) && x >= -1000 && x <= 12000 && z >= -1000 && z <= 12000 && y >= -1000 &&
           y <= 5000;
}

bool same(const Server::Spec1* a, const Server::Spec1* b) { return a == b || (a && b && *a == *b); }

Server::Spec1 spec_of(const Record& rec, uint64_t serial) {
    Server::Spec1 s;
    s.surfaces = rec.surfaces;
    s.x = rec.x;
    s.y = rec.y;
    s.z = rec.z;
    s.height = rec.height;
    s.max_distance = rec.max_distance;
    s.icon = rec.icon;
    s.rgba = rec.rgba;
    s.priority = rec.priority;
    s.label_mode = rec.label_mode;
    s.follow = serial;
    s.text.assign(rec.text.data(), rec.text.size());
    return s;
}

const wire::Token kZero{};

}  // namespace

Server::Server(Store& store, CatalogHolder& catalog, Inbox& inbox, LinkEnv env)
    : store_(store), catalog_(catalog), inbox_(inbox), env_(std::move(env)) {
    inbox_.set_per_peer(Inbox::kPerPeerServer);  // one flooding peer keeps only its own share (review F2)
}

void Server::log(int level, const char* fmt, ...) {
    if (!env_.log) return;
    char text[512];
    va_list v;
    va_start(v, fmt);
    std::vsnprintf(text, sizeof text, fmt, v);
    va_end(v);
    env_.log(level, text);
}

// ---- lookups ------------------------------------------------------------------------------------------------------------

Server::Link* Server::find(uint32_t peer, uint64_t generation) {
    for (auto& l : links_)
        if (l->peer == peer && l->generation == generation) return l.get();
    return nullptr;
}

Server::Link* Server::find_peer(uint32_t peer) {
    for (auto& l : links_)
        if (l->peer == peer) return l.get();
    return nullptr;
}

Server::Link* Server::find_identity(uint64_t identity) {
    for (auto& l : links_)
        if (l->identity == identity) return l.get();
    return nullptr;
}

// A player's link: the hosted loopback first (the hosting player is counted once), else its Network connection.
Server::Link* Server::target(KilnEntity entity) {
    Link* best = nullptr;
    for (auto& l : links_) {
        if (l->entity != entity || !(l->flags & kHasPlayer) || (l->flags & kRejected)) continue;
        if (l->route == kRouteLoopback) return l.get();
        if (!best) best = l.get();
    }
    return best;
}

Server::Pub* Server::pub_of(std::string_view ns, uint64_t generation) {
    for (auto& [o, p] : pubs_)
        if (p.generation == generation && p.ns == ns) return &p;
    return nullptr;
}

bool Server::shadowed(const Link& l) const {
    if (l.route != kRouteNetwork || !l.entity) return false;
    for (const auto& o : links_)
        if (o.get() != &l && o->route == kRouteLoopback && o->entity == l.entity) return true;
    return false;
}

Server::Class Server::classify(const Link& l) const {
    if (!(l.flags & kHasPlayer) || (l.flags & kRejected) || shadowed(l)) return Class::None;
    if ((l.flags & kVanilla) || !(l.flags & kHasMarkersChannel)) return Class::NoKiln;
    if (l.max_body && l.max_body < wire::kMaxBody) return Class::NoKiln;  // its channel cannot carry wire v1
    if (!(l.flags & kNetworkReady) || !l.hello || l.suspended) return Class::NotReady;
    if (l.client_body < wire::kMaxBody) return Class::NoKiln;
    if (l.stalled) return Class::NotReady;
    if (l.refused) return Class::Full;
    uint32_t before = 0;  // welcomed links with a player ahead of this one: 16 get data
    for (const auto& o : links_) {
        if (o.get() == &l) break;
        if ((o->flags & kHasPlayer) && !(o->flags & (kRejected | kVanilla)) && (o->flags & kHasMarkersChannel) && o->hello && !shadowed(*o)) ++before;
    }
    return before >= kDataRecipients ? Class::Full : Class::Ready;
}

bool Server::compatible(const Link& l, const Spec1& s) const {
    uint32_t need = s.surfaces;  // MAP 1, COMPASS 2, WORLD 4: the same bits as the HELLO capabilities
    if (s.label_mode == KILN_MARKER_LABEL_NATIVE_TOOLTIP) need |= wire::kCapMapText;
    if (s.follow) need |= wire::kCapFollowPoses;
    return (need & ~l.accepted) == 0;
}

const Server::Spec1* Server::effective_identity(const Pub& p, uint64_t identity, const std::string& key) const {
    if (auto layer = p.P.find(identity); layer != p.P.end()) {
        if (auto o = layer->second.find(key); o != layer->second.end()) return o->second.suppress ? nullptr : &o->second.spec;
    }
    auto b = p.B.find(key);
    return b == p.B.end() ? nullptr : &b->second;
}

const Server::Spec1* Server::effective(const Pub& p, const Link& l, const std::string& key) const { return effective_identity(p, l.identity, key); }

void Server::recount(Link& l) {
    l.eff = l.followed = 0;
    for (const auto& [o, p] : pubs_) {
        auto one = [&](const std::string& k) {
            if (const Spec1* s = effective(p, l, k)) {
                ++l.eff;
                if (s->follow) ++l.followed;
            }
        };
        const auto layer = p.P.find(l.identity);
        for (const auto& [k, s] : p.B)
            if (layer == p.P.end() || !layer->second.count(k)) one(k);
        if (layer != p.P.end())
            for (const auto& [k, ov] : layer->second) one(k);
    }
}

// follow_refs_ counts the stored definitions that follow each incarnation; the server holds one anchors reference per
// incarnation while its count is above 0: the reference send's resolve_follow took (pending_ref_) when the count goes
// 0 -> 1, given back when it goes 1 -> 0.
void Server::count_follow(const Spec1* before, const Spec1* after) {
    if (after && after->follow && ++follow_refs_[after->follow] == 1 && pending_ref_ == after->follow) pending_ref_ = 0;
    if (before && before->follow) {
        auto it = follow_refs_.find(before->follow);
        if (it != follow_refs_.end() && --it->second == 0) {
            follow_refs_.erase(it);
            if (env_.release_follow) env_.release_follow(before->follow);
        }
    }
}

void Server::reset_data(Link& l) {
    l.flight.reset();
    l.outbox.erase(std::remove_if(l.outbox.begin(), l.outbox.end(), [](const Packet& p) { return p.seq != 0; }), l.outbox.end());
    l.need_full = true;
    l.stalled = false;
    l.dirty.clear();
}

// The connection's P layers go: one CLEARED per publisher scope (cause), its view of the broadcast comes back.
void Server::drop_targets(Link& l, uint32_t cause) {
    for (auto& [owner, p] : pubs_) {
        auto layer = p.P.find(l.identity);
        if (layer == p.P.end()) continue;
        const uint32_t n = static_cast<uint32_t>(layer->second.size());
        for (const auto& [k, ov] : layer->second) {
            if (!ov.suppress) count_follow(&ov.spec, nullptr);
            l.dirty.insert(EKey{p.ns, p.generation, k});
        }
        p.count -= n;
        defs_ -= n;
        p.P.erase(layer);
        if (n) store_.server_change(owner, KILN_MARKER_CLEARED, KILN_MARKER_SERVER_TARGET, l.entity, l.generation, {}, n, cause, false, nullptr);
    }
    recount(l);
    if (l.dirty.size() > kPeerEffective) {
        l.dirty.clear();
        l.need_full = true;
    }
}

// ---- results ------------------------------------------------------------------------------------------------------------

void Server::finish(KilnMarkerResult* r, const Tally& t, uint32_t affected) {
    r->committed = 1;
    r->queued = t.queued;
    r->refused = t.refused;
    r->affected = affected;
    r->reason[0] = 0;
    char why[160];
    std::snprintf(why, sizeof why, "%u refused (%u without Kiln or a surface, %u not ready, %u full or stalled)", t.refused, t.requires_client, t.not_ready,
                  t.full);
    if (!t.refused) {
        r->status = KILN_MARKER_ACCEPTED;
    } else if (t.queued) {
        r->status = KILN_MARKER_PARTIAL;
        std::snprintf(r->reason, sizeof r->reason, "Partial: stored; %u recipient(s) queued, %s", t.queued, why);
    } else if (t.requires_client) {
        r->status = KILN_MARKER_REQUIRES_CLIENT;
        std::snprintf(r->reason, sizeof r->reason, "RequiresClient: stored; no recipient can show it now: %s", why);
    } else if (t.full) {
        r->status = KILN_MARKER_FULL;
        std::snprintf(r->reason, sizeof r->reason, "Full: stored; no recipient can take it now: %s", why);
    } else {
        r->status = KILN_MARKER_NOT_READY;
        std::snprintf(r->reason, sizeof r->reason, "NotReady: stored; no recipient is ready yet: %s", why);
    }
}

namespace {
// Per link of one mutation: whether any of its key changes went to its queue, or was refused (and why).
struct LinkMark {
    bool queued = false;
    int refused = 0;  // 1 requires client, 2 not ready, 3 full
};
}  // namespace

// ---- the world ----------------------------------------------------------------------------------------------------------

void Server::world_started() {
    std::lock_guard g(mu_);
    do {
        if (env_.random) env_.random(epoch_.data(), epoch_.size());
    } while (epoch_ == kZero && env_.random);
    if (epoch_ == kZero) epoch_[0] = 1;
    live_ = true;
    wire_ids_ = 0;
    for (auto& l : links_) {
        l->hello = false;  // a tuple of the last world is not this one's: the receiver says HELLO again
        l->view.clear();
        l->seq = l->acked = 0;
        reset_data(*l);
        l->outbox.clear();
        recount(*l);
    }
}

void Server::world_stopping() {
    std::lock_guard g(mu_);
    if (!live_) return;
    for (auto& [owner, p] : pubs_) {
        if (!p.B.empty())
            store_.server_change(owner, KILN_MARKER_CLEARED, KILN_MARKER_SERVER_BROADCAST, 0, 0, {}, static_cast<uint32_t>(p.B.size()),
                                 KILN_MARKER_WORLD_STOPPED, false, nullptr);
        for (const auto& [identity, layer] : p.P) {
            if (layer.empty()) continue;
            const Link* l = find_identity(identity);
            store_.server_change(owner, KILN_MARKER_CLEARED, KILN_MARKER_SERVER_TARGET, l ? l->entity : 0, l ? l->generation : 0, {},
                                 static_cast<uint32_t>(layer.size()), KILN_MARKER_WORLD_STOPPED, false, nullptr);
        }
    }
    pubs_.clear();
    defs_ = broadcast_ = broadcast_followed_ = 0;
    if (env_.release_follow)
        for (const auto& [serial, n] : follow_refs_) env_.release_follow(serial);
    follow_refs_.clear();
    for (auto& l : links_) {
        l->hello = false;
        l->view.clear();
        reset_data(*l);
        l->outbox.clear();
        l->eff = l->followed = 0;
    }
    live_ = false;
    epoch_ = {};
}

// ---- the API ------------------------------------------------------------------------------------------------------------

KilnResult Server::send(KilnOwner owner, KilnEntity recipient, const KilnMarkerSpec* spec, KilnMarkerResult* r) {
    if (!result_ok(r)) return KILN_ERR_INVALID_ARGUMENT;
    reset(r);
    if (!spec || spec->size < sizeof(KilnMarkerSpec)) return bad_arg(r, "spec.size is smaller than KilnMarkerSpec v1");
    if (!available_.load()) return off(r, "markers.send");
    if (env_.on_server_thread && !env_.on_server_thread()) {
        std::snprintf(r->reason, sizeof r->reason, "send runs on the server's tick thread");
        return KILN_ERR_WRONG_THREAD;
    }
    Record rec;
    std::string_view key;
    if (const char* why = validate_spec(*spec, &rec, &key)) return bad_arg(r, "%s", why);
    // MapLabel.NativeTooltip (issue 160): the server has no map of its own; each link takes it when its HELLO offered MapText
    // with the map (accepted_mask), and every other player is refused for this spec (compatible): nothing is drawn partly
    uint64_t serial = 0;
    if (spec->follow) {
        if (!env_.resolve_follow) return off(r, "markers.anchors.server");
        const KilnResult rc = env_.resolve_follow(owner, spec->follow, &serial);
        if (rc == KILN_ERR_UNAVAILABLE) return off(r, "markers.anchors.server");
        if (rc != KILN_OK || !serial)
            return refuse(r, KILN_MARKER_INVALID_ANCHOR, "InvalidAnchor: the follow handle is not a live server-world lease of this owner");
        pending_ref_ = serial;  // the reference just taken: kept by count_follow when a definition starts following it
    }
    // a reference no definition took (a refusal, an identical value, an incarnation followed already) goes back
    struct GiveBack {
        Server* s;
        ~GiveBack() {
            if (s->pending_ref_ && s->env_.release_follow) s->env_.release_follow(s->pending_ref_);
            s->pending_ref_ = 0;
        }
    } give_back{this};
    if (rec.surfaces & KILN_MARKER_MAP) {
        switch (CatalogHolder::check(rec.icon, &catalog_)) {
            case IconAnswer::Ok: break;
            case IconAnswer::NotReady:
                return refuse(r, KILN_MARKER_CATALOG_NOT_READY,
                              "CatalogNotReady: the icon catalog is not here yet (a dedicated server takes it from the first verified Kiln client)");
            case IconAnswer::Unknown: return bad_arg(r, "icon_type 0x%08x is not in the icon catalog", rec.icon);
            case IconAnswer::NotForMap:
                return bad_arg(r, "icon_type 0x%08x is listed without the MAP surface (fast-travel types are refused on the map)", rec.icon);
        }
    }
    std::string ns;
    uint64_t generation = 0;
    if (!store_.publisher(owner, &ns, &generation)) return bad_arg(r, "owner %llu is not bound (bind_owner)", static_cast<unsigned long long>(owner));
    const Spec1 s = spec_of(rec, serial);
    const std::string k(key);

    std::lock_guard g(mu_);
    if (!live_) return refuse(r, KILN_MARKER_NOT_READY, "NotReady: there is no server world");
    Link* to = nullptr;
    if (recipient) {
        to = target(recipient);
        if (!to) return refuse(r, KILN_MARKER_NOT_FOUND, "NotFound: player %u is not connected", recipient);
    }
    Pub* p = nullptr;
    if (auto it = pubs_.find(owner); it != pubs_.end() && it->second.generation == generation) p = &it->second;
    Pub fresh;
    if (!p) {
        fresh.owner = owner;
        fresh.ns = ns;
        fresh.generation = generation;
        p = &fresh;
    }
    const uint64_t identity = to ? to->identity : 0;
    // the layer entry before and after
    const Spec1* old_b = nullptr;
    const Override* old_p = nullptr;
    if (!to) {
        if (auto b = p->B.find(k); b != p->B.end()) old_b = &b->second;
    } else if (auto layer = p->P.find(identity); layer != p->P.end()) {
        if (auto o = layer->second.find(k); o != layer->second.end()) old_p = &o->second;
    }
    const bool existed = to ? old_p != nullptr : old_b != nullptr;
    const bool identical = to ? (old_p && !old_p->suppress && old_p->spec == s) : (old_b && *old_b == s);
    const EKey ek{p->ns, p->generation, k};

    // each link's effective value before and after
    // (decided before the commit: a stored entry is overwritten in place, so "before" may not be read after it)
    struct Delta {
        Link* l;
        const Spec1* before;
        const Spec1* after;
        bool changed, had, had_follow;
    };
    std::vector<Delta> deltas;
    for (auto& lp : links_) {
        Link& l = *lp;
        const Spec1* before = effective(*p, l, k);
        const Spec1* after = before;
        if (!to) {
            const auto layer = p->P.find(l.identity);
            if (layer == p->P.end() || !layer->second.count(k)) after = &s;
        } else if (l.identity == identity) {
            after = &s;
        }
        deltas.push_back({&l, before, after, !same(before, after), before != nullptr, before && before->follow});
    }

    if (!identical) {
        // the projected counts, before anything changes
        if (!existed) {
            if (p->count >= kPerPublisherSent)
                return refuse(r, KILN_MARKER_FULL, "Full: '%s' has %u sent markers (broadcast and per-player together; the most)", ns.c_str(), kPerPublisherSent);
            if (defs_ >= kServerDefinitions) return refuse(r, KILN_MARKER_FULL, "Full: the server holds %u sent markers (the most)", kServerDefinitions);
            if (!to && broadcast_ >= kBroadcastRecords) return refuse(r, KILN_MARKER_FULL, "Full: %u broadcast markers (the most)", kBroadcastRecords);
        }
        if (!to) {
            const int f = (s.follow ? 1 : 0) - (old_b && old_b->follow ? 1 : 0);
            if (f > 0 && broadcast_followed_ + f > kPeerFollowed)
                return refuse(r, KILN_MARKER_FULL, "Full: %u followed broadcast markers (the most)", kPeerFollowed);
        }
        const uint64_t was_follow = to ? (old_p && !old_p->suppress ? old_p->spec.follow : 0) : (old_b ? old_b->follow : 0);
        if (s.follow && s.follow != was_follow && !follow_refs_.count(s.follow) && follow_refs_.size() >= kServerFollowAnchors)
            return refuse(r, KILN_MARKER_FULL, "Full: the server follows %u entities (the most)", kServerFollowAnchors);
        for (const Delta& d : deltas) {
            const int de = (d.after ? 1 : 0) - (d.before ? 1 : 0);
            const int df = (d.after && d.after->follow ? 1 : 0) - (d.before && d.before->follow ? 1 : 0);
            if (de > 0 && d.l->eff + de > kPeerEffective)
                return refuse(r, KILN_MARKER_FULL, "Full: a recipient (peer 0x%x) would see more than %u markers", d.l->peer, kPeerEffective);
            if (df > 0 && d.l->followed + df > kPeerFollowed)
                return refuse(r, KILN_MARKER_FULL, "Full: a recipient (peer 0x%x) would see more than %u followed markers", d.l->peer, kPeerFollowed);
        }
        // the change's note: reserved before the commit (FULL when the step's queue is full)
        const uint32_t kind = existed ? KILN_MARKER_UPDATED : KILN_MARKER_ADDED;
        const char* why = nullptr;
        if (!store_.server_change(owner, kind, to ? KILN_MARKER_SERVER_TARGET : KILN_MARKER_SERVER_BROADCAST, to ? to->entity : 0,
                                  to ? to->generation : 0, k, 1, KILN_MARKER_API, true, &why))
            return refuse(r, KILN_MARKER_FULL, "Full: %s", why ? why : "the change queue is full");
        // commit
        if (p == &fresh) p = &pubs_.emplace(owner, std::move(fresh)).first->second;
        Spec1* stored = nullptr;
        if (!to) {
            Spec1& b = p->B[k];
            if (existed && b.follow) --broadcast_followed_;
            count_follow(existed ? &b : nullptr, &s);
            b = s;
            stored = &b;
            if (s.follow) ++broadcast_followed_;
            if (!existed) ++broadcast_;
        } else {
            Override& o = p->P[identity][k];
            count_follow(existed && !o.suppress ? &o.spec : nullptr, &s);
            o.suppress = false;
            o.spec = s;
            stored = &o.spec;
        }
        if (!existed) {
            ++p->count;
            ++defs_;
        }
        for (Delta& d : deltas) {
            if (d.after == &s) d.after = stored;  // the stored copy from here
            d.before = nullptr;                   // may be overwritten: only changed/had/had_follow are read below
        }
    }

    // dirty marks, link counts and the result
    std::map<Link*, LinkMark> marks;
    for (const Delta& d : deltas) {
        Link& l = *d.l;
        const bool changed = d.changed;
        if (changed) {
            l.eff = l.eff + (d.after ? 1 : 0) - (d.had ? 1 : 0);
            l.followed = l.followed + (d.after && d.after->follow ? 1 : 0) - (d.had_follow ? 1 : 0);
            l.dirty.insert(ek);
            if (l.dirty.size() > kPeerEffective) {
                l.dirty.clear();
                l.need_full = true;
            }
        }
        const Class c = classify(l);
        if (c == Class::None || !d.after) continue;
        if (!changed && !identical) continue;
        LinkMark& m = marks[&l];
        if (c == Class::Ready && compatible(l, *d.after)) {
            if (changed) {
                m.queued = true;
            } else {
                // an identical retry: only what this link does not hold yet goes again
                auto v = l.view.find(ek);
                const bool synced = v != l.view.end() && v->second.spec == *d.after && !l.dirty.count(ek) && !l.flight && !l.need_full;
                if (!synced) {
                    l.dirty.insert(ek);
                    m.queued = true;
                }
            }
        } else if (identical && (c == Class::Full || (c == Class::NotReady && l.stalled))) {
            // a refused or stalled link is tried again on an identical retry
            l.refused = 0;
            l.stalled = false;
            l.need_full = true;
            m.queued = true;
        } else {
            m.refused = c == Class::NoKiln || c == Class::Ready ? 1 : c == Class::NotReady ? 2 : 3;
        }
    }
    Tally t;
    for (const auto& [l, m] : marks) {
        if (m.refused) {
            ++t.refused;
            if (m.refused == 1) ++t.requires_client;
            else if (m.refused == 2) ++t.not_ready;
            else ++t.full;
        } else if (m.queued) {
            ++t.queued;
        }
    }
    finish(r, t, identical ? 0 : 1);
    return KILN_OK;
}

KilnResult Server::unsend(KilnOwner owner, KilnEntity recipient, KilnStr key, KilnMarkerResult* r) {
    if (!result_ok(r)) return KILN_ERR_INVALID_ARGUMENT;
    reset(r);
    if (!available_.load()) return off(r, "markers.send");
    if (env_.on_server_thread && !env_.on_server_thread()) {
        std::snprintf(r->reason, sizeof r->reason, "unsend runs on the server's tick thread");
        return KILN_ERR_WRONG_THREAD;
    }
    if (!valid_identifier(key.ptr, key.len)) return bad_arg(r, "key must be 1 to 64 bytes of [A-Za-z0-9_.:-]");
    std::string ns;
    uint64_t generation = 0;
    if (!store_.publisher(owner, &ns, &generation)) return bad_arg(r, "owner %llu is not bound (bind_owner)", static_cast<unsigned long long>(owner));
    const std::string k(key.ptr, key.len);
    std::lock_guard g(mu_);
    if (!live_) return refuse(r, KILN_MARKER_NOT_READY, "NotReady: there is no server world");
    Link* to = nullptr;
    if (recipient) {
        to = target(recipient);
        if (!to) return refuse(r, KILN_MARKER_NOT_FOUND, "NotFound: player %u is not connected", recipient);
    }
    auto pit = pubs_.find(owner);
    Pub* p = pit != pubs_.end() && pit->second.generation == generation ? &pit->second : nullptr;
    bool exists = false;
    if (p && !to) exists = p->B.count(k) != 0;
    if (p && to)
        if (auto layer = p->P.find(to->identity); layer != p->P.end()) exists = layer->second.count(k) != 0;
    if (!exists)
        return refuse(r, KILN_MARKER_NOT_FOUND, to ? "NotFound: '%s' sent no marker '%s' to player %u" : "NotFound: '%s' broadcasts no marker '%s'",
                      ns.c_str(), k.c_str(), recipient);
    const char* why = nullptr;
    if (!store_.server_change(owner, KILN_MARKER_REMOVED, to ? KILN_MARKER_SERVER_TARGET : KILN_MARKER_SERVER_BROADCAST, to ? to->entity : 0,
                              to ? to->generation : 0, k, 1, KILN_MARKER_API, true, &why))
        return refuse(r, KILN_MARKER_FULL, "Full: %s", why ? why : "the change queue is full");
    // before, then the commit, then after
    std::vector<std::pair<Link*, Spec1>> before;  // copies: the entry goes
    for (auto& lp : links_)
        if (const Spec1* b = effective(*p, *lp, k)) before.push_back({lp.get(), *b});
    if (!to) {
        auto b = p->B.find(k);
        if (b->second.follow) --broadcast_followed_;
        count_follow(&b->second, nullptr);
        p->B.erase(b);
        --broadcast_;
    } else {
        auto layer = p->P.find(to->identity);
        auto o = layer->second.find(k);
        if (!o->second.suppress) count_follow(&o->second.spec, nullptr);
        layer->second.erase(o);
        if (layer->second.empty()) p->P.erase(layer);
    }
    --p->count;
    --defs_;
    const EKey ek{p->ns, p->generation, k};
    std::map<Link*, LinkMark> marks;
    for (auto& lp : links_) {
        Link& l = *lp;
        const Spec1* was = nullptr;
        for (const auto& [bl, s] : before)
            if (bl == &l) was = &s;
        const Spec1* now = effective(*p, l, k);
        if (same(was, now)) continue;
        const bool shrank = was && !now;
        l.eff = l.eff + (now ? 1 : 0) - (was ? 1 : 0);
        l.followed = l.followed + (now && now->follow ? 1 : 0) - (was && was->follow ? 1 : 0);
        l.dirty.insert(ek);
        if (l.dirty.size() > kPeerEffective) {
            l.dirty.clear();
            l.need_full = true;
        }
        if (shrank && l.refused == wire::kAckFull) {
            l.refused = 0;  // room again: try the full view once more
            l.need_full = true;
        }
        const Class c = classify(l);
        if (c == Class::None) continue;
        LinkMark& m = marks[&l];
        if (now) {  // the broadcast shows again (an override went)
            if (c == Class::Ready && compatible(l, *now)) m.queued = true;
            else m.refused = c == Class::NoKiln || c == Class::Ready ? 1 : c == Class::NotReady ? 2 : 3;
        } else if (c == Class::Ready && (l.view.count(ek) || (was && compatible(l, *was)))) {
            m.queued = true;
        }
    }
    Tally t;
    for (const auto& [l, m] : marks) {
        if (m.refused) {
            ++t.refused;
            if (m.refused == 1) ++t.requires_client;
            else if (m.refused == 2) ++t.not_ready;
            else ++t.full;
        } else if (m.queued) {
            ++t.queued;
        }
    }
    finish(r, t, 1);
    return KILN_OK;
}

KilnResult Server::suppress_sent(KilnOwner owner, KilnEntity recipient, KilnStr key, KilnMarkerResult* r) {
    if (!result_ok(r)) return KILN_ERR_INVALID_ARGUMENT;
    reset(r);
    if (!available_.load()) return off(r, "markers.send");
    if (env_.on_server_thread && !env_.on_server_thread()) {
        std::snprintf(r->reason, sizeof r->reason, "suppress_sent runs on the server's tick thread");
        return KILN_ERR_WRONG_THREAD;
    }
    if (!valid_identifier(key.ptr, key.len)) return bad_arg(r, "key must be 1 to 64 bytes of [A-Za-z0-9_.:-]");
    if (!recipient) return bad_arg(r, "suppress_sent hides a marker from one player: recipient 0 is the broadcast (use unsend)");
    std::string ns;
    uint64_t generation = 0;
    if (!store_.publisher(owner, &ns, &generation)) return bad_arg(r, "owner %llu is not bound (bind_owner)", static_cast<unsigned long long>(owner));
    const std::string k(key.ptr, key.len);
    std::lock_guard g(mu_);
    if (!live_) return refuse(r, KILN_MARKER_NOT_READY, "NotReady: there is no server world");
    Link* to = target(recipient);
    if (!to) return refuse(r, KILN_MARKER_NOT_FOUND, "NotFound: player %u is not connected", recipient);
    auto pit = pubs_.find(owner);
    Pub* p = pit != pubs_.end() && pit->second.generation == generation ? &pit->second : nullptr;
    const Override* old = nullptr;
    if (p)
        if (auto layer = p->P.find(to->identity); layer != p->P.end())
            if (auto o = layer->second.find(k); o != layer->second.end()) old = &o->second;
    if (old && old->suppress) {  // suppressed already: nothing changes
        r->status = KILN_MARKER_ACCEPTED;
        r->committed = 1;
        return KILN_OK;
    }
    const Spec1* was = p ? effective(*p, *to, k) : nullptr;
    if (!was) return refuse(r, KILN_MARKER_NOT_FOUND, "NotFound: player %u sees no marker '%s' of '%s' to suppress", recipient, k.c_str(), ns.c_str());
    if (!old) {
        if (p->count >= kPerPublisherSent)
            return refuse(r, KILN_MARKER_FULL, "Full: '%s' has %u sent markers (broadcast and per-player together; the most)", ns.c_str(), kPerPublisherSent);
        if (defs_ >= kServerDefinitions) return refuse(r, KILN_MARKER_FULL, "Full: the server holds %u sent markers (the most)", kServerDefinitions);
    }
    const char* why = nullptr;
    if (!store_.server_change(owner, old ? KILN_MARKER_UPDATED : KILN_MARKER_ADDED, KILN_MARKER_SERVER_TARGET, to->entity, to->generation, k, 1,
                              KILN_MARKER_API, true, &why))
        return refuse(r, KILN_MARKER_FULL, "Full: %s", why ? why : "the change queue is full");
    const bool was_follow = was->follow != 0;
    const bool was_compatible = compatible(*to, *was);
    Override& o = p->P[to->identity][k];
    if (old) count_follow(&o.spec, nullptr);
    else {
        ++p->count;
        ++defs_;
    }
    o.suppress = true;
    o.spec = Spec1{};
    to->eff -= 1;
    if (was_follow) to->followed -= 1;
    const EKey ek{p->ns, p->generation, k};
    to->dirty.insert(ek);
    if (to->dirty.size() > kPeerEffective) {
        to->dirty.clear();
        to->need_full = true;
    }
    if (to->refused == wire::kAckFull) {
        to->refused = 0;
        to->need_full = true;
    }
    Tally t;
    if (classify(*to) == Class::Ready && (to->view.count(ek) || was_compatible)) t.queued = 1;
    finish(r, t, 1);
    return KILN_OK;
}

KilnResult Server::clear_sent(KilnOwner owner, KilnEntity recipient, KilnMarkerResult* r) {
    if (!result_ok(r)) return KILN_ERR_INVALID_ARGUMENT;
    reset(r);
    if (!available_.load()) return off(r, "markers.send");
    if (env_.on_server_thread && !env_.on_server_thread()) {
        std::snprintf(r->reason, sizeof r->reason, "clear_sent runs on the server's tick thread");
        return KILN_ERR_WRONG_THREAD;
    }
    std::string ns;
    uint64_t generation = 0;
    if (!store_.publisher(owner, &ns, &generation)) return bad_arg(r, "owner %llu is not bound (bind_owner)", static_cast<unsigned long long>(owner));
    std::lock_guard g(mu_);
    if (!live_) return refuse(r, KILN_MARKER_NOT_READY, "NotReady: there is no server world");
    Link* to = nullptr;
    if (recipient) {
        to = target(recipient);
        if (!to) return refuse(r, KILN_MARKER_NOT_FOUND, "NotFound: player %u is not connected", recipient);
    }
    auto pit = pubs_.find(owner);
    Pub* p = pit != pubs_.end() && pit->second.generation == generation ? &pit->second : nullptr;
    uint32_t n = 0;
    if (p && to) {
        if (auto layer = p->P.find(to->identity); layer != p->P.end()) n = static_cast<uint32_t>(layer->second.size());
    } else if (p) {
        n = p->count;
    }
    if (!n) {  // an empty scope: nothing to do, no event
        r->status = KILN_MARKER_ACCEPTED;
        r->committed = 1;
        return KILN_OK;
    }
    const char* why = nullptr;
    if (!store_.server_change(owner, KILN_MARKER_CLEARED, to ? KILN_MARKER_SERVER_TARGET : KILN_MARKER_SERVER_BROADCAST, to ? to->entity : 0,
                              to ? to->generation : 0, {}, n, KILN_MARKER_API, true, &why))
        return refuse(r, KILN_MARKER_FULL, "Full: %s", why ? why : "the change queue is full");
    // every key of the scope, for every link it can change: the value before (copied) and after
    struct Seen {
        Link* l;
        EKey k;
        std::optional<Spec1> before;
    };
    std::vector<Seen> seen;
    std::set<std::string> keys;
    if (to) {
        for (const auto& [k, ov] : p->P.find(to->identity)->second) keys.insert(k);
    } else {
        for (const auto& [k, s] : p->B) keys.insert(k);
        for (const auto& [id, layer] : p->P)
            for (const auto& [k, ov] : layer) keys.insert(k);
    }
    for (auto& lp : links_) {
        if (to && lp.get() != to) continue;
        for (const std::string& k : keys) {
            const Spec1* b = effective(*p, *lp, k);
            seen.push_back({lp.get(), EKey{p->ns, p->generation, k}, b ? std::optional<Spec1>(*b) : std::nullopt});
        }
    }
    // the commit
    if (to) {
        auto layer = p->P.find(to->identity);
        for (const auto& [k, ov] : layer->second)
            if (!ov.suppress) count_follow(&ov.spec, nullptr);
        p->P.erase(layer);
        p->count -= n;
        defs_ -= n;
    } else {
        for (const auto& [k, s] : p->B) count_follow(&s, nullptr);
        for (const auto& [id, layer] : p->P)
            for (const auto& [k, ov] : layer)
                if (!ov.suppress) count_follow(&ov.spec, nullptr);
        broadcast_ -= static_cast<uint32_t>(p->B.size());
        for (const auto& [k, s] : p->B)
            if (s.follow) --broadcast_followed_;
        defs_ -= p->count;
        pubs_.erase(pit);
        p = nullptr;
    }
    std::map<Link*, LinkMark> marks;
    for (const Seen& sv : seen) {
        Link& l = *sv.l;
        const Spec1* now = nullptr;
        if (p) now = effective(*p, l, std::get<2>(sv.k));
        const Spec1* was = sv.before ? &*sv.before : nullptr;
        if (same(was, now)) continue;
        l.eff = l.eff + (now ? 1 : 0) - (was ? 1 : 0);
        l.followed = l.followed + (now && now->follow ? 1 : 0) - (was && was->follow ? 1 : 0);
        l.dirty.insert(sv.k);
        if (l.dirty.size() > kPeerEffective) {
            l.dirty.clear();
            l.need_full = true;
        }
        if (was && !now && l.refused == wire::kAckFull) {
            l.refused = 0;
            l.need_full = true;
        }
        const Class c = classify(l);
        if (c == Class::None) continue;
        LinkMark& m = marks[&l];
        if (now) {
            if (c == Class::Ready && compatible(l, *now)) m.queued = true;
            else m.refused = c == Class::NoKiln || c == Class::Ready ? 1 : c == Class::NotReady ? 2 : 3;
        } else if (c == Class::Ready && (l.view.count(sv.k) || (was && compatible(l, *was)))) {
            m.queued = true;
        }
    }
    Tally t;
    for (const auto& [l, m] : marks) {
        if (m.refused) {
            ++t.refused;
            if (m.refused == 1) ++t.requires_client;
            else if (m.refused == 2) ++t.not_ready;
            else ++t.full;
        } else if (m.queued) {
            ++t.queued;
        }
    }
    finish(r, t, n);
    return KILN_OK;
}

void Server::release_owner(KilnOwner owner) {
    std::lock_guard g(mu_);
    auto pit = pubs_.find(owner);
    if (pit == pubs_.end()) return;
    Pub& p = pit->second;
    if (!p.B.empty())
        store_.server_change(owner, KILN_MARKER_CLEARED, KILN_MARKER_SERVER_BROADCAST, 0, 0, {}, static_cast<uint32_t>(p.B.size()),
                             KILN_MARKER_OWNER_RETIRED, false, nullptr);
    for (const auto& [identity, layer] : p.P) {
        if (layer.empty()) continue;
        const Link* l = find_identity(identity);
        store_.server_change(owner, KILN_MARKER_CLEARED, KILN_MARKER_SERVER_TARGET, l ? l->entity : 0, l ? l->generation : 0, {},
                             static_cast<uint32_t>(layer.size()), KILN_MARKER_OWNER_RETIRED, false, nullptr);
    }
    for (auto& lp : links_) {
        Link& l = *lp;
        auto one = [&](const std::string& k) {
            if (effective(p, l, k)) l.dirty.insert(EKey{p.ns, p.generation, k});
        };
        for (const auto& [k, s] : p.B) one(k);
        if (auto layer = p.P.find(l.identity); layer != p.P.end())
            for (const auto& [k, ov] : layer->second) one(k);
    }
    for (const auto& [k, s] : p.B) {
        count_follow(&s, nullptr);
        if (s.follow) --broadcast_followed_;
    }
    for (const auto& [id, layer] : p.P)
        for (const auto& [k, ov] : layer)
            if (!ov.suppress) count_follow(&ov.spec, nullptr);
    broadcast_ -= static_cast<uint32_t>(p.B.size());
    defs_ -= p.count;
    pubs_.erase(pit);
    for (auto& lp : links_) {
        recount(*lp);
        if (lp->dirty.size() > kPeerEffective) {
            lp->dirty.clear();
            lp->need_full = true;
        }
        if (lp->refused == wire::kAckFull) {
            lp->refused = 0;
            lp->need_full = true;
        }
    }
}

// ---- peers --------------------------------------------------------------------------------------------------------------

KilnResult Server::peer_up(const KilnMarkerPeer& p) {
    if (p.role != kRoleServer) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard g(mu_);
    if (Link* l = find_peer(p.peer)) {
        if (l->generation == p.generation) {  // the same connection: flags, player, limits (idempotent)
            // A new entity id on the same connection is the same player (dying and respawning, a recreated player
            // entity): a connection carries one player and a new character comes with a new connection, so the
            // per-player layer stays; Send(player) resolves the current id at admission (review question, 2026-11-22).
            const bool gained = !(l->flags & kHasPlayer) && (p.flags & kHasPlayer);
            l->route = p.route;
            l->entity = p.entity;
            l->flags = p.flags;
            l->max_body = p.max_message_bytes;
            if (gained) l->need_full = true;
            return KILN_OK;
        }
        // another generation on this peer id: the old connection ended
        drop_targets(*l, KILN_MARKER_PEER_LEFT);
        links_.erase(std::find_if(links_.begin(), links_.end(), [l](const auto& o) { return o.get() == l; }));
    }
    if (links_.size() >= kBridgePeers) return KILN_ERR_BUFFER_TOO_SMALL;
    auto l = std::make_unique<Link>();
    l->peer = p.peer;
    l->generation = p.generation;
    l->route = p.route;
    l->entity = p.entity;
    l->flags = p.flags;
    l->max_body = p.max_message_bytes;
    l->identity = ++identities_;
    recount(*l);
    links_.push_back(std::move(l));
    return KILN_OK;
}

KilnResult Server::peer_down(uint32_t peer, uint64_t generation) {
    std::lock_guard g(mu_);
    Link* l = find(peer, generation);
    if (!l) return KILN_ERR_NOT_FOUND;
    drop_targets(*l, KILN_MARKER_PEER_LEFT);
    links_.erase(std::find_if(links_.begin(), links_.end(), [l](const auto& o) { return o.get() == l; }));
    return KILN_OK;
}

// ---- inbound ------------------------------------------------------------------------------------------------------------

void Server::bad(Link& l, uint64_t now, const char* why) {
    l.bad.erase(std::remove_if(l.bad.begin(), l.bad.end(), [now](uint64_t t) { return now - t >= 10000; }), l.bad.end());
    l.bad.push_back(now);
    const bool suspend = l.bad.size() >= 3 && !l.suspended;
    if (suspend) {
        l.suspended = true;  // only a new valid HELLO is read from here; the game connection is not touched
        reset_data(l);
    }
    if (suspend || now - last_bad_log_ >= 1000 || !last_bad_log_) {
        last_bad_log_ = now ? now : 1;
        log(KILN_LOG_WARN, "markers: a bad marker message from peer 0x%x: %s%s", l.peer, why,
            suspend ? " (3 in 10 s: its marker link is suspended until it says HELLO again)" : "");
    }
}

uint32_t Server::accepted_mask(const Link& l) const {
    uint32_t m = l.caps & (wire::kCapCompass | wire::kCapWorld | wire::kCapFollowPoses);
    // MapText (issue 160): the client's markers.map_text is on; it goes into a map entry, so it needs the map as well
    if (l.map_ok) m |= l.caps & (wire::kCapMap | wire::kCapMapText);
    return m;
}

void Server::decide_catalog(Link& l) {
    l.map_ok = false;
    l.need_catalog = false;
    const BuildPair* pair = env_.pair ? env_.pair : env_.side == KILN_SIDE_CLIENT ? pair_of_client(env_.exe) : pair_of_server(env_.exe);
    wire::Digest digest{}, exe{};
    if (!pair || !wire::from_hex(pair->catalog_sha256, digest.data(), 32) || !wire::from_hex(pair->client_exe, exe.data(), 32)) return;
    if (l.exe != exe || l.digest != digest) return;  // not the verified client of this build: no map for it
    const auto c = catalog_.get();
    if (c && c->digest == digest && catalog_.map_verified()) l.map_ok = true;
    else if (!c) l.need_catalog = true;
}

wire::Header Server::header(const Link& l, uint64_t seq) const {
    wire::Header h;
    h.server_epoch = epoch_;
    h.client_nonce = l.nonce;
    h.sequence = seq;
    return h;
}

void Server::welcome(Link& l) {
    wire::Welcome w;
    w.accepted = l.accepted;
    w.max_records = static_cast<uint16_t>(wire::kSnapshotRecords);
    w.need_catalog = l.need_catalog ? 1 : 0;
    Packet p;
    p.message = wire::kWelcome;
    p.bytes = wire::encode_welcome(header(l, l.seq), w);
    enqueue(l, std::move(p));
}

void Server::on_hello(Link& l, const wire::Header& h, const wire::Hello& m, uint64_t now) {
    if (!live_) return;  // no server world: the client asks again
    if (!m.client_world_epoch) return bad(l, now, "HELLO without a client world epoch");
    const bool same_tuple = l.hello && l.nonce == h.client_nonce && l.world_epoch == m.client_world_epoch;
    if (!same_tuple) {
        if (l.world_epoch && l.world_epoch != m.client_world_epoch) {
            drop_targets(l, KILN_MARKER_PEER_LEFT);  // another client world: its per-player markers were for the last one
            l.identity = ++identities_;
            recount(l);
        }
        l.nonce = h.client_nonce;
        l.world_epoch = m.client_world_epoch;
        reset_data(l);  // a new baseline: the next transaction is a full snapshot (wire ids and revisions go on)
        l.refused = 0;
    }
    l.hello = true;
    l.suspended = false;
    l.bad.clear();
    l.caps = m.capabilities;
    l.max_records = m.max_records;
    l.client_body = m.max_body;
    l.exe = m.exe_sha256;
    l.digest = m.catalog_digest;
    decide_catalog(l);
    const uint32_t accepted = accepted_mask(l);
    if (accepted != l.accepted || l.refused) {
        l.accepted = accepted;
        l.refused = 0;
        l.need_full = true;  // the negotiated view changed: the whole view again
    }
    l.next_heartbeat = now + 2000;
    welcome(l);
}

void Server::on_catalog(Link& l, const wire::Catalog& m, uint64_t) {
    if (!l.need_catalog) return;  // not asked for
    l.need_catalog = false;
    const BuildPair* pair = env_.pair ? env_.pair : env_.side == KILN_SIDE_CLIENT ? pair_of_client(env_.exe) : pair_of_server(env_.exe);
    wire::Digest want{};
    bool ok = pair && wire::from_hex(pair->catalog_sha256, want.data(), 32) && m.digest == want;
    std::vector<IconRow> rows;
    if (ok) {
        rows.reserve(m.rows.size());
        for (const wire::CatalogRow& w : m.rows) {
            IconRow row;
            row.id = w.id;
            row.flags = w.flags;
            row.surfaces = (w.flags & KILN_MARKER_ICON_FAST_TRAVEL) ? 0u : static_cast<uint32_t>(KILN_MARKER_MAP);
            std::memcpy(row.image, w.image.data(), 16);
            row.name = w.name;
            row.alias = alias_of(row.name);
            row.key = canonical_key(row.id);
            rows.push_back(std::move(row));
        }
        std::sort(rows.begin(), rows.end(), [](const IconRow& a, const IconRow& b) { return a.id < b.id; });
        for (size_t i = 1; ok && i < rows.size(); ++i) ok = rows[i].id != rows[i - 1].id;
        ok = ok && digest_of(rows) == want;
    }
    if (ok) {
        auto c = std::make_shared<Catalog>();
        c->rows = std::move(rows);
        c->digest = want;
        if (catalog_.publish(std::move(c), true, "")) {
            catalog_published_ = true;
            log(KILN_LOG_INFO, "markers: icon catalog taken from peer 0x%x (the verified catalog of this build, %u marker types)", l.peer,
                static_cast<unsigned>(m.rows.size()));
        }
        const auto have = catalog_.get();
        l.map_ok = have && have->digest == want && catalog_.map_verified();
    } else {
        l.map_ok = false;
        log(KILN_LOG_WARN, "markers: peer 0x%x sent an icon catalog that is not this build's verified one: native map markers stay off for it", l.peer);
    }
    l.accepted = accepted_mask(l);
    l.need_full = true;
    welcome(l);
}

void Server::on_ack(Link& l, const wire::Ack& m, uint64_t now) {
    if (m.applied > l.seq) return bad(l, now, "an ACK of a transaction that was never sent");
    uint64_t done = 0;
    if (l.flight && m.applied == l.flight->seq) {
        done = l.flight->seq;
        const uint64_t seq = l.flight->seq;
        l.flight.reset();
        l.outbox.erase(std::remove_if(l.outbox.begin(), l.outbox.end(), [seq](const Packet& p) { return p.seq == seq; }), l.outbox.end());
    } else if (l.stalled && m.applied == l.stalled_seq) {
        done = l.stalled_seq;  // the stalled transaction did arrive after all
        l.stalled = false;
        if (m.status == wire::kAckAccepted) l.need_full = false;  // the view of that transaction holds
    } else {
        return;  // older than the flight: ignored, never a newer baseline
    }
    l.acked = done;
    switch (m.status) {
        case wire::kAckAccepted: break;
        case wire::kAckFull:
        case wire::kAckUnsupported:
            l.refused = m.status;  // nothing more until a HELLO, room, or an identical retry
            l.need_full = true;
            log(KILN_LOG_INFO, "markers: peer 0x%x refused transaction %llu (%s): %s", l.peer, static_cast<unsigned long long>(done),
                m.status == wire::kAckFull ? "full" : "unsupported surface or icon", m.reason.c_str());
            break;
        default: l.need_full = true; break;  // bad baseline
    }
}

void Server::on_resync(Link& l, const wire::Resync&, uint64_t now) {
    if (now - l.resync_at[1] < 1000 && l.resync_at[1]) return;  // at most 2 a second
    l.resync_at[1] = l.resync_at[0];
    l.resync_at[0] = now;
    reset_data(l);  // the obsolete flight goes; the current view goes as one snapshot
}

void Server::handle(Link& l, Inbound& m, uint64_t now) {
    const uint8_t* p = m.bytes.data();
    const size_t n = m.bytes.size();
    if (m.message >= wire::kMessages || !wire::to_server(m.message)) return bad(l, now, "a message a client may not send");
    if (l.suspended && m.message != wire::kHello) return;
    wire::Header h;
    const char* why = "";
    auto tuple = [&] { return l.hello && h.server_epoch == epoch_ && h.client_nonce == l.nonce; };
    switch (m.message) {
        case wire::kHello: {
            wire::Hello x;
            if (!wire::decode_hello(p, n, &h, &x, &why)) return bad(l, now, why);
            if (!wire::check_header(h) || h.server_epoch != kZero || h.sequence != 0) return bad(l, now, "HELLO's header is not schema 1 with no server epoch");
            return on_hello(l, h, x, now);
        }
        case wire::kCatalog: {
            wire::Catalog x;
            if (!wire::decode_catalog(p, n, &h, &x, &why)) return bad(l, now, why);
            if (!wire::check_header(h)) return bad(l, now, "CATALOG's header is not schema 1");
            if (!tuple()) return;  // an old tuple: ignored
            return on_catalog(l, x, now);
        }
        case wire::kAck: {
            wire::Ack x;
            if (!wire::decode_ack(p, n, &h, &x, &why)) return bad(l, now, why);
            if (!wire::check_header(h)) return bad(l, now, "ACK's header is not schema 1");
            if (!tuple()) return;
            return on_ack(l, x, now);
        }
        case wire::kResync: {
            wire::Resync x;
            if (!wire::decode_resync(p, n, &h, &x, &why)) return bad(l, now, why);
            if (!wire::check_header(h)) return bad(l, now, "RESYNC's header is not schema 1");
            if (!tuple()) return;
            return on_resync(l, x, now);
        }
        default: return;
    }
}

// ---- outbound -----------------------------------------------------------------------------------------------------------

void Server::enqueue(Link& l, Packet&& p) {
    p.tag = ++tags_;
    l.outbox.push_back(std::move(p));
}

void Server::start_flight(Link& l, std::vector<Packet>&& packets) {
    Link::Flight f;
    f.seq = l.seq;
    if (!packets.empty()) packets.back().last = true;
    for (Packet& p : packets) p.seq = f.seq;
    f.packets = packets;
    for (Packet& p : packets) enqueue(l, std::move(p));
    l.flight = std::move(f);
}

void Server::pose_of(const Spec1& s, ViewRow* v) const {
    if (!s.follow) {
        v->px = s.x;
        v->py = s.y;
        v->pz = s.z;
        v->reason = KILN_MARKER_READY;
        return;
    }
    // v holds the row as it was (callers update v->spec after this): an incarnation that ended stays ended; only a new
    // capture (another serial, a higher revision) can show the marker again
    if (v->revision && v->reason == KILN_MARKER_ANCHOR_ENDED && v->spec.follow == s.follow) return;
    KilnVec3 at{};
    uint32_t reason = KILN_MARKER_ANCHOR_ENDED;
    const bool live = env_.follow && env_.follow(s.follow, &at, &reason);
    if (!live) reason = KILN_MARKER_ANCHOR_ENDED;
    if (reason != KILN_MARKER_READY && reason != KILN_MARKER_FOLLOW_MISSING) reason = KILN_MARKER_ANCHOR_ENDED;
    if (reason == KILN_MARKER_READY && !in_world(at.x, at.y, at.z)) reason = KILN_MARKER_BAD_POSITION;  // the last valid position stays
    else if (reason == KILN_MARKER_READY) {
        v->px = at.x;
        v->py = at.y;
        v->pz = at.z;
    }
    if (!in_world(v->px, v->py, v->pz)) v->px = v->py = v->pz = 0;  // never a NaN or an outside point on the wire
    v->reason = reason;
}

wire::Record Server::to_wire(const EKey& k, const ViewRow& v) const {
    wire::Record r;
    r.wire_id = v.wire_id;
    r.publisher_generation = std::get<1>(k);
    r.ns = std::get<0>(k);
    r.key = std::get<2>(k);
    r.revision = v.revision;
    r.surfaces = v.spec.surfaces;
    r.priority = static_cast<int16_t>(v.spec.priority);
    r.rgba = v.spec.rgba;
    r.icon = v.spec.icon;
    r.label_mode = static_cast<uint8_t>(v.spec.label_mode);
    r.followed = v.spec.follow ? 1 : 0;
    r.source_reason = static_cast<uint16_t>(v.reason);
    r.x = v.px;
    r.y = v.py;
    r.z = v.pz;
    r.height = v.spec.height;
    r.max_distance = v.spec.max_distance;
    r.text = v.spec.text;
    return r;
}

void Server::build_snapshot(Link& l, uint64_t now) {
    std::map<EKey, const Spec1*> cur;
    for (const auto& [o, p] : pubs_) {
        auto add = [&](const std::string& k) {
            const Spec1* s = effective(p, l, k);
            if (s && compatible(l, *s)) cur.emplace(EKey{p.ns, p.generation, k}, s);  // an incompatible spec is left out whole
        };
        for (const auto& [k, s] : p.B) add(k);
        if (auto layer = p.P.find(l.identity); layer != p.P.end())
            for (const auto& [k, ov] : layer->second) add(k);
    }
    const uint32_t limit = std::min<uint32_t>(wire::kSnapshotRecords, l.max_records ? l.max_records : wire::kSnapshotRecords);
    if (cur.size() > limit) {
        l.refused = wire::kAckFull;
        log(KILN_LOG_WARN, "markers: peer 0x%x takes at most %u markers; its view has %u: nothing is sent until it has room", l.peer, limit,
            static_cast<unsigned>(cur.size()));
        return;
    }
    std::map<EKey, ViewRow> view;
    std::vector<wire::Record> recs;
    recs.reserve(cur.size());
    for (const auto& [k, s] : cur) {
        ViewRow v;
        if (auto old = l.view.find(k); old != l.view.end()) {
            v = old->second;
            pose_of(*s, &v);
            if (!(v.spec == *s)) {
                ++v.revision;  // monotonic per wire id, also when an older value shows again
                v.spec = *s;
            }
        } else {
            v.wire_id = ++wire_ids_;  // never reused in this server epoch
            v.revision = 1;
            pose_of(*s, &v);
            v.spec = *s;
        }
        v.pose_ms = now;
        recs.push_back(to_wire(k, v));
        view.emplace(k, std::move(v));
    }
    const uint64_t seq = ++l.seq, id = ++snapshots_;
    std::vector<Packet> packets;
    wire::SnapBegin b;
    b.snapshot_id = id;
    b.total_records = static_cast<uint16_t>(recs.size());
    b.total_chunks = static_cast<uint16_t>((recs.size() + wire::kChunkRecords - 1) / wire::kChunkRecords);
    packets.push_back(Packet{wire::kSnapBegin, wire::encode_snap_begin(header(l, seq), b)});
    for (uint16_t i = 0; i < b.total_chunks; ++i) {
        const size_t from = size_t{i} * wire::kChunkRecords;
        const size_t to = std::min(recs.size(), from + wire::kChunkRecords);
        const std::vector<wire::Record> part(recs.begin() + static_cast<std::ptrdiff_t>(from), recs.begin() + static_cast<std::ptrdiff_t>(to));
        packets.push_back(Packet{wire::kSnapChunk, wire::encode_snap_chunk(header(l, seq), id, i, part)});
    }
    wire::SnapEnd e;
    e.snapshot_id = id;
    e.digest = wire::snapshot_digest(recs);
    packets.push_back(Packet{wire::kSnapEnd, wire::encode_snap_end(header(l, seq), e)});
    l.view = std::move(view);
    l.dirty.clear();
    l.need_full = false;
    start_flight(l, std::move(packets));
}

void Server::build_delta(Link& l, uint64_t now) {
    const uint32_t budget = std::min<uint32_t>(wire::kDeltaOps, static_cast<uint32_t>(l.delta_ops.peek(now)));  // 128 changes a second
    if (!budget) return;
    std::vector<wire::DeltaOp> ops;
    for (auto it = l.dirty.begin(); it != l.dirty.end() && ops.size() < budget;) {
        const EKey& k = *it;
        const Spec1* cur = nullptr;
        if (const Pub* p = pub_of(std::get<0>(k), std::get<1>(k))) {
            cur = effective(*p, l, std::get<2>(k));
            if (cur && !compatible(l, *cur)) cur = nullptr;  // a key that became incompatible goes
        }
        auto v = l.view.find(k);
        if (cur && v != l.view.end()) {
            if (!(v->second.spec == *cur)) {
                pose_of(*cur, &v->second);
                ++v->second.revision;
                v->second.spec = *cur;
                v->second.pose_ms = now;
                wire::DeltaOp op;
                op.record = to_wire(k, v->second);
                ops.push_back(std::move(op));
            }
        } else if (cur) {
            ViewRow row;
            row.wire_id = ++wire_ids_;
            row.revision = 1;
            pose_of(*cur, &row);
            row.spec = *cur;
            row.pose_ms = now;
            wire::DeltaOp op;
            op.record = to_wire(k, row);
            ops.push_back(std::move(op));
            l.view.emplace(k, std::move(row));
        } else if (v != l.view.end()) {
            wire::DeltaOp op;
            op.op = wire::kRemove;
            op.remove_id = v->second.wire_id;  // retired: a later add gets a new id
            ops.push_back(std::move(op));
            l.view.erase(v);
        }
        it = l.dirty.erase(it);
    }
    if (ops.empty()) return;
    l.delta_ops.take(static_cast<double>(ops.size()), now);
    const uint64_t seq = ++l.seq;
    std::vector<Packet> packets;
    packets.push_back(Packet{wire::kDelta, wire::encode_delta(header(l, seq), ops)});
    start_flight(l, std::move(packets));
}

void Server::build_poses(Link& l, uint64_t now) {
    if (!(l.accepted & wire::kCapFollowPoses)) return;
    std::vector<wire::Pose> poses;
    for (auto& [k, v] : l.view) {
        if (!v.spec.follow || v.reason == KILN_MARKER_ANCHOR_ENDED) continue;
        if (poses.size() >= wire::kPoses) break;
        ViewRow next = v;
        pose_of(v.spec, &next);
        const double dx = next.px - v.px, dy = next.py - v.py, dz = next.pz - v.pz;
        const bool moved = dx * dx + dy * dy + dz * dz >= 0.05 * 0.05;
        const bool due = next.reason != v.reason || (moved && now - v.pose_ms >= 100) || now - v.pose_ms >= 500;
        if (!due) continue;
        v.px = next.px;
        v.py = next.py;
        v.pz = next.pz;
        v.reason = next.reason;
        v.pose_ms = now;
        poses.push_back(wire::Pose{v.wire_id, v.revision, v.px, v.py, v.pz, v.reason});
    }
    if (poses.empty()) return;
    const uint64_t seq = ++l.seq;
    std::vector<Packet> packets;
    packets.push_back(Packet{wire::kPose, wire::encode_pose(header(l, seq), poses)});
    start_flight(l, std::move(packets));
}

void Server::tick(Link& l, uint64_t now) {
    if (!live_) return;
    if (l.flight && l.flight->sent_at && now - l.flight->sent_at >= 2000) {
        if (l.flight->tries < 3) {  // the same transaction again (same snapshot id and sequence)
            ++l.flight->tries;
            l.flight->sent_at = 0;
            const uint64_t seq = l.flight->seq;
            l.outbox.erase(std::remove_if(l.outbox.begin(), l.outbox.end(), [seq](const Packet& p) { return p.seq == seq; }), l.outbox.end());
            for (const Packet& p : l.flight->packets) {
                Packet copy = p;
                enqueue(l, std::move(copy));
            }
        } else {
            l.stalled = true;
            l.stalled_seq = l.flight->seq;
            l.flight.reset();
            l.need_full = true;
            log(KILN_LOG_WARN, "markers: peer 0x%x did not acknowledge transaction %llu after 3 resends: its marker link is stalled (the game connection is not touched)",
                l.peer, static_cast<unsigned long long>(l.stalled_seq));
        }
    }
    if (l.hello && !l.suspended && now >= l.next_heartbeat) {
        l.next_heartbeat = now + 2000;
        const bool waiting = std::any_of(l.outbox.begin(), l.outbox.end(), [](const Packet& p) { return p.message == wire::kHeartbeat; });
        if (!waiting) {
            Packet p;
            p.message = wire::kHeartbeat;
            p.bytes = wire::encode_heartbeat(header(l, l.seq));
            enqueue(l, std::move(p));
        }
    }
    if (classify(l) != Class::Ready || l.flight || l.outbox.size() > 40) return;
    if (l.need_full) build_snapshot(l, now);
    else if (!l.dirty.empty()) build_delta(l, now);
    if (!l.flight) build_poses(l, now);
}

void Server::post() {
    bool published = false;
    {
        std::lock_guard g(mu_);
        const uint64_t now = env_.now_ms ? env_.now_ms() : 0;
        std::deque<Inbound> q = inbox_.take();
        std::deque<Inbound> later;
        for (Inbound& m : q) {
            Link* l = find(m.peer, m.generation);
            if (!l) continue;  // a connection that ended
            if (l->deferring || !l->controls.take(1, now)) {  // 8 a second, 16 at once: the rest waits, in order
                l->deferring = true;
                later.push_back(std::move(m));
                continue;
            }
            handle(*l, m, now);
        }
        for (auto& l : links_) l->deferring = false;
        inbox_.put_back(std::move(later));
        inbox_.take_drops();  // a lost ACK or RESYNC: the flight's timer and the client's retries cover it
        for (auto& l : links_) tick(*l, now);
        published = catalog_published_;
        catalog_published_ = false;
    }
    if (published && on_catalog_published) on_catalog_published();
}

void Server::collect(uint32_t max_packets, Bucket& process, uint64_t now, std::vector<Outgoing>* out) {
    std::lock_guard g(mu_);
    const size_t n = links_.size();
    if (!n) return;
    uint32_t total = 0;
    for (size_t i = 0; i < n && total < max_packets; ++i) {
        Link& l = *links_[(rr_ + i) % n];
        uint32_t mine = 0;
        for (const Packet& p : l.outbox) {
            if (mine >= 4 || total >= max_packets) break;
            const double size = static_cast<double>(p.bytes.size());
            if (l.bytes.peek(now) < size || process.peek(now) < size) break;  // 128 KiB/s a peer, 512 KiB/s the process
            l.bytes.take(size, now);
            process.take(size, now);
            Outgoing o;
            o.peer = l.peer;
            o.generation = l.generation;
            o.route = l.route;
            o.message = p.message;
            o.bytes = p.bytes;
            o.tag = p.tag;
            out->push_back(std::move(o));
            ++mine;
            ++total;
        }
    }
    rr_ = (rr_ + 1) % n;
}

void Server::sent(const Outgoing& o, bool ok, uint64_t now) {
    if (!ok) return;  // stays at the front for the next pump
    std::lock_guard g(mu_);
    Link* l = find(o.peer, o.generation);
    if (!l) return;
    auto it = std::find_if(l->outbox.begin(), l->outbox.end(), [&](const Packet& p) { return p.tag == o.tag; });
    if (it == l->outbox.end()) return;
    const bool last = it->last;
    const uint64_t seq = it->seq;
    l->outbox.erase(it);
    if (last && l->flight && l->flight->seq == seq) l->flight->sent_at = now ? now : 1;  // the ACK timer starts now
}

// ---- tests and logs -----------------------------------------------------------------------------------------------------

bool Server::link_info(uint32_t peer, LinkInfo* out) const {
    std::lock_guard g(mu_);
    for (const auto& l : links_) {
        if (l->peer != peer) continue;
        out->welcomed = l->hello;
        out->map_ok = l->map_ok;
        out->need_catalog = l->need_catalog;
        out->suspended = l->suspended;
        out->stalled = l->stalled;
        out->in_flight = l->flight.has_value();
        out->refused = l->refused;
        out->accepted = l->accepted;
        out->effective = l->eff;
        out->followed = l->followed;
        out->view = static_cast<uint32_t>(l->view.size());
        out->dirty = static_cast<uint32_t>(l->dirty.size());
        out->outbox = static_cast<uint32_t>(l->outbox.size());
        out->sequence = l->seq;
        out->acked = l->acked;
        out->identity = l->identity;
        return true;
    }
    return false;
}

uint32_t Server::definitions() const {
    std::lock_guard g(mu_);
    return defs_;
}

wire::Token Server::epoch() const {
    std::lock_guard g(mu_);
    return epoch_;
}

}  // namespace kmk
