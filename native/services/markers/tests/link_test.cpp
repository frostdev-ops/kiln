// SPDX-License-Identifier: MIT
// kiln.markers' link with a fake bridge and a fake clock (dossier chapter 04 §6): the server layers and the server role
// (server.h) against built-in receivers (receiver.h) in other fake processes, joined by a fake network that can drop,
// duplicate and delay packets. M09 (target and broadcast layers), M10 (partial counts, retry, late join, reconnect), M11
// (loss, duplicates, late ACKs, stale tuples, gaps, resync, flood, bad messages), M13 server half (the catalog from a
// verified client, refusals), the hosted loopback (route 2, counted once), follow poses, the count limits and the error
// rule. No game: the catalog is synthetic (its own digest in a synthetic build pair).
#include <algorithm>
#include <cmath>
#include <cstring>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <tuple>
#include <vector>

#include "bridge.h"
#include "catalog.h"
#include "check.h"
#include "receiver.h"
#include "server.h"
#include "store.h"
#include "wire.h"

using namespace kmk;

namespace {

uint64_t g_now = 100000;
uint64_t clock_fn() { return g_now; }
uint64_t g_rand = 0;
void rand_fn(uint8_t* out, size_t n) {
    ++g_rand;
    for (size_t i = 0; i < n; ++i) out[i] = static_cast<uint8_t>(g_rand * 31 + i * 7 + 1);
}
bool g_verbose = false;

// follow: serial -> (position, reason); resolve: a lease handle is its serial while g_follow_on
std::map<uint64_t, std::pair<KilnVec3, uint32_t>> g_follow;
bool g_follow_on = false;
std::map<uint64_t, int> g_refs;  // the anchors references the server holds per serial (resolve_follow +1, release_follow -1)

// ---- a synthetic catalog and build pair -----------------------------------------------------------------------------------

std::shared_ptr<Catalog> synthetic_catalog(bool variant = false) {
    auto c = std::make_shared<Catalog>();
    const struct {
        uint32_t id, flags;
        const char* name;
    } rows[] = {{0x00000101u, 9, "mapmarker_camp"},       {0x00000202u, 25, "icon_cave"},  {0x00000303u, 41, "mapmarker_travel"},
                {0x00000404u, 9, "mapmarker_well_plant"}, {0x00000505u, 13, "icon_boss"}, {0x00000606u, 9, variant ? "mapmarker_other" : "mapmarker_tower"}};
    for (const auto& r : rows) {
        IconRow row;
        row.id = r.id;
        row.flags = r.flags;
        row.surfaces = (r.flags & KILN_MARKER_ICON_FAST_TRAVEL) ? 0u : static_cast<uint32_t>(KILN_MARKER_MAP);
        for (int i = 0; i < 16; ++i) row.image[i] = static_cast<uint8_t>(r.id + i);
        row.name = r.name;
        row.alias = alias_of(row.name);
        row.key = canonical_key(row.id);
        c->rows.push_back(row);
    }
    c->digest = digest_of(c->rows);
    return c;
}
const std::string kServerExe(64, '1'), kClientExe(64, '2');
std::string g_digest_hex;
BuildPair g_pair{kServerExe.c_str(), kClientExe.c_str(), nullptr};
constexpr uint32_t kCamp = 0x101, kTravel = 0x303, kUnknownIcon = 0x999;

// ---- a fake process -----------------------------------------------------------------------------------------------------

struct Proc {
    Arena arena;
    Switches sw;
    Store store{arena, sw};
    CatalogHolder catalog;
    Inbox inbox[2];
    std::unique_ptr<Server> server;
    std::unique_ptr<Receiver> receiver;
    std::unique_ptr<Bridge> bridge;
    std::vector<KilnMarkerChange> changes[2];
    std::vector<KilnMarkerCatalogChange> catalogs[2];
    int published = 0;
    bool on_thread = true;

    Proc(uint32_t side, const std::string& exe, bool client_surfaces) {
        store.set_clock(&clock_fn);
        LinkEnv env;
        env.now_ms = &clock_fn;
        env.random = &rand_fn;
        env.on_server_thread = [this] { return on_thread; };
        env.follow = [](uint64_t serial, KilnVec3* at, uint32_t* reason) {
            auto it = g_follow.find(serial);
            if (it == g_follow.end()) return false;
            *at = it->second.first;
            *reason = it->second.second;
            return true;
        };
        env.resolve_follow = [](KilnOwner, KilnMarkerAnchor lease, uint64_t* serial) -> KilnResult {
            if (!g_follow_on) return KILN_ERR_UNAVAILABLE;
            if (!g_follow.count(lease)) return KILN_ERR_NOT_FOUND;
            *serial = lease;
            ++g_refs[lease];
            return KILN_OK;
        };
        env.release_follow = [](uint64_t serial) { --g_refs[serial]; };
        env.log = [](int, const char* t) {
            if (g_verbose) std::printf("    log: %s\n", t);
        };
        env.exe = exe;
        env.side = side;
        env.pair = &g_pair;
        server = std::make_unique<Server>(store, catalog, inbox[0], env);
        receiver = std::make_unique<Receiver>(store, catalog, sw, inbox[1], env);
        bridge = std::make_unique<Bridge>(server.get(), receiver.get(), &inbox[0], &inbox[1]);
        bridge->attach(77);
        server->set_available(true);
        server->on_catalog_published = [this] {
            ++published;
            KilnMarkerCatalogChange ch{};
            ch.size = sizeof ch;
            ch.ready = 1;
            ch.count = static_cast<uint32_t>(catalog.get()->rows.size());
            ch.generation = catalog.generation();
            store.post_catalog(kRoleServer, ch);
        };
        if (client_surfaces) {
            sw.compass = true;
            sw.world = true;
            sw.map = true;
        }
    }
    void take() {
        for (uint32_t role = 0; role < 2; ++role)
            for (const Note& n : store.take(role)) {
                if (n.kind == Note::Change) changes[role].push_back(n.change);
                if (n.kind == Note::Catalog) catalogs[role].push_back(n.catalog);
            }
    }
    size_t count(uint32_t role, uint32_t kind, uint32_t audience = 99) const {
        size_t c = 0;
        for (const auto& ch : changes[role]) c += ch.kind == kind && (audience == 99 || ch.audience == audience);
        return c;
    }
    std::vector<ClientRow> received() const {
        std::vector<ClientRow> rows, out;
        store.snapshot_client(&rows);
        for (auto& r : rows)
            if (r.source == KILN_MARKER_RECEIVED) out.push_back(r);
        return out;
    }
    const ClientRow* row(const std::string& key) const {
        static std::vector<ClientRow> keep;
        keep = received();
        for (const ClientRow& r : keep)
            if (r.key == key) return &r;
        return nullptr;
    }
};

KilnMarkerPeer peer(uint32_t role, uint32_t id, uint32_t route, uint32_t entity, uint32_t flags, uint64_t generation) {
    KilnMarkerPeer p{};
    p.size = sizeof p;
    p.role = role;
    p.peer = id;
    p.route = route;
    p.entity = entity;
    p.max_message_bytes = 16384;
    p.flags = flags;
    p.generation = generation;
    return p;
}

KilnResult up(Bridge& b, KilnMarkerPeer p) { return b.peer_up(&p); }

constexpr uint32_t kKiln = kHasPlayer | kNetworkReady | kHasMarkersChannel;
constexpr uint32_t kVanillaPeer = kHasPlayer | kVanilla;

// ---- a fake network -----------------------------------------------------------------------------------------------------

struct Conn {
    Proc* client = nullptr;  // null: a vanilla game client (no Kiln, no receiver)
    uint32_t peer = 0, entity = 0;
    uint64_t sgen = 0, cgen = 0;
    bool up = true;
    size_t frames = 0;  // server -> this peer
};

struct Net {
    Proc* srv = nullptr;
    std::deque<Conn> conns;
    // 0: drop, 1: deliver, 2: deliver twice, 3: deliver after hold_ms
    std::function<int(bool to_client, Conn& c, uint32_t message, const std::vector<uint8_t>& bytes)> filter;
    uint64_t hold_ms = 0;
    struct Held {
        uint64_t at;
        bool to_client;
        Conn* c;
        uint32_t message;
        std::vector<uint8_t> bytes;
    };
    std::vector<Held> held;
    std::map<std::pair<uint32_t, uint64_t>, int> sent_seq;  // (message, sequence) -> transmissions server -> client

    void deliver(bool to_client, Conn& c, uint32_t message, const std::vector<uint8_t>& bytes) {
        if (to_client) {
            if (c.client) c.client->inbox[1].push(kServerPeer, c.cgen, message, bytes.data(), bytes.size());
        } else {
            srv->inbox[0].push(c.peer, c.sgen, message, bytes.data(), bytes.size());
        }
    }
    void route(bool to_client, Conn& c, uint32_t message, std::vector<uint8_t> bytes) {
        const int f = filter ? filter(to_client, c, message, bytes) : 1;
        if (f == 0) return;
        if (f == 3) {
            held.push_back({g_now + hold_ms, to_client, &c, message, std::move(bytes)});
            return;
        }
        deliver(to_client, c, message, bytes);
        if (f == 2) deliver(to_client, c, message, bytes);
    }
    static uint8_t KILN_CALL server_send(const KilnMarkerEnvelope* e, void* user) {
        Net* n = static_cast<Net*>(user);
        for (Conn& c : n->conns)
            if (c.up && c.peer == e->peer && c.sgen == e->peer_generation) {
                ++c.frames;
                wire::Header h;
                const char* why = nullptr;
                if (wire::decode_header(e->bytes, e->length, &h, &why)) ++n->sent_seq[{e->message, h.sequence}];
                n->route(true, c, e->message, std::vector<uint8_t>(e->bytes, e->bytes + e->length));
                return 1;
            }
        return 0;
    }
    struct ClientCtx {
        Net* net;
        Conn* conn;
    };
    static uint8_t KILN_CALL client_send(const KilnMarkerEnvelope* e, void* user) {
        auto* x = static_cast<ClientCtx*>(user);
        if (!x->conn->up) return 0;
        x->net->route(false, *x->conn, e->message, std::vector<uint8_t>(e->bytes, e->bytes + e->length));
        return 1;
    }
    Conn& join(Proc* client, uint32_t peer_id, uint32_t entity, uint32_t flags = kKiln) {
        static uint64_t gens = 1000;
        Conn c;
        c.client = client;
        c.peer = peer_id;
        c.entity = entity;
        c.sgen = ++gens;
        c.cgen = ++gens;
        conns.push_back(c);
        Conn& r = conns.back();
        CHECK_EQ(up(*srv->bridge, peer(0, peer_id, kRouteNetwork, entity, flags, r.sgen)), KILN_OK);
        if (client) CHECK_EQ(up(*client->bridge, peer(1, kServerPeer, kRouteNetwork, 0, kNetworkReady | kHasMarkersChannel, r.cgen)), KILN_OK);
        return r;
    }
    void leave(Conn& c) {
        c.up = false;
        CHECK_EQ(srv->bridge->peer_down(0, c.peer, c.sgen), KILN_OK);
        if (c.client) CHECK_EQ(c.client->bridge->peer_down(1, kServerPeer, c.cgen), KILN_OK);
    }
    void step(int n = 1, uint64_t ms = 50) {
        for (int i = 0; i < n; ++i) {
            g_now += ms;
            for (auto it = held.begin(); it != held.end();) {
                if (it->at <= g_now) {
                    deliver(it->to_client, *it->c, it->message, it->bytes);
                    it = held.erase(it);
                } else {
                    ++it;
                }
            }
            for (Conn& c : conns) {
                if (!c.up || !c.client) continue;
                c.client->receiver->post();
                c.client->take();
                ClientCtx ctx{this, &c};
                uint32_t done = 0;
                c.client->bridge->pump(1, 16, &client_send, &ctx, &done, g_now);
            }
            srv->server->post();
            srv->take();
            uint32_t done = 0;
            srv->bridge->pump(0, 16, &server_send, this, &done, g_now);
        }
    }
    void settle(int steps = 40) { step(steps); }
};

struct Spec {
    KilnMarkerSpec s{};
    std::string key, text;
    explicit Spec(std::string k, std::string t = "Raid entrance", uint32_t surfaces = KILN_MARKER_COMPASS | KILN_MARKER_WORLD, uint32_t icon = 0)
        : key(std::move(k)), text(std::move(t)) {
        s.size = sizeof s;
        s.surfaces = surfaces;
        s.position = {3670, 820, 1500};
        s.rgba = 0xffffffffu;
        s.icon_type = icon;
    }
    const KilnMarkerSpec* operator&() {
        s.key = KilnStr{key.data(), key.size()};
        s.text = KilnStr{text.data(), text.size()};
        return &s;
    }
};

KilnMarkerResult res() {
    KilnMarkerResult r{};
    r.size = sizeof r;
    return r;
}

KilnStr ks(const std::string& s) { return KilnStr{s.data(), s.size()}; }

// A server process with its world and a bound author (owner 5, "author"), and its fake network.
struct World {
    Proc srv{KILN_SIDE_SERVER, kServerExe, false};
    std::vector<std::unique_ptr<Proc>> clients;
    Net net;
    explicit World(bool server_catalog = true) {
        net.srv = &srv;
        if (server_catalog) srv.catalog.publish(synthetic_catalog(), true, "");
        srv.store.world_started(KILN_WORLD_SERVER);
        srv.server->world_started();
        CHECK_EQ(srv.store.bind(5, KilnStr{"author", 6}), KILN_OK);
    }
    Proc& client(bool catalog = true, bool surfaces = true) {
        clients.push_back(std::make_unique<Proc>(KILN_SIDE_CLIENT, kClientExe, surfaces));
        Proc& c = *clients.back();
        if (catalog) c.catalog.publish(synthetic_catalog(), true, "");
        c.store.world_started(KILN_WORLD_CLIENT);
        c.receiver->world_started();
        return c;
    }
    KilnMarkerResult send(KilnEntity to, Spec& sp, KilnResult want = KILN_OK) {
        KilnMarkerResult r = res();
        CHECK_EQ(srv.server->send(5, to, &sp, &r), want);
        return r;
    }
    KilnMarkerResult unsend(KilnEntity to, const std::string& k) {
        KilnMarkerResult r = res();
        CHECK_EQ(srv.server->unsend(5, to, ks(k), &r), KILN_OK);
        return r;
    }
    KilnMarkerResult suppress(KilnEntity to, const std::string& k) {
        KilnMarkerResult r = res();
        CHECK_EQ(srv.server->suppress_sent(5, to, ks(k), &r), KILN_OK);
        return r;
    }
    KilnMarkerResult clear(KilnEntity to) {
        KilnMarkerResult r = res();
        CHECK_EQ(srv.server->clear_sent(5, to, &r), KILN_OK);
        return r;
    }
};

std::vector<uint32_t> kinds(const Proc& p, uint32_t role, uint32_t audience = 99) {
    std::vector<uint32_t> k;
    for (const auto& c : p.changes[role])
        if (audience == 99 || c.audience == audience) k.push_back(c.kind);
    return k;
}

bool reason_starts(const KilnMarkerResult& r, const char* prefix) { return std::strncmp(r.reason, prefix, std::strlen(prefix)) == 0; }

// ---- cases ----------------------------------------------------------------------------------------------------------------

void handshake_and_catalog() {  // M13 server half, M08 protocol: HELLO, CATALOG, WELCOME, an empty snapshot
    kmk_test::begin();
    World w(false);  // a dedicated server: no catalog of its own
    Proc& p = w.client();
    Spec map("gate", "Raid entrance", KILN_MARKER_MAP | KILN_MARKER_COMPASS, kCamp);
    KilnMarkerResult r = w.send(0, map);
    CHECK_EQ(r.status, KILN_MARKER_CATALOG_NOT_READY);  // warming up: nothing stored
    CHECK_EQ(r.committed, 0u);
    Spec plate("plate", "Smith", KILN_MARKER_WORLD);  // a world-only spec needs no catalog
    r = w.send(0, plate);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.committed == 1);
    w.net.join(&p, 0x81, 101);
    w.net.settle();
    Server::LinkInfo li;
    CHECK(w.srv.server->link_info(0x81, &li));
    CHECK(li.welcomed && li.map_ok && !li.need_catalog);
    CHECK_EQ(li.accepted, wire::kCapMap | wire::kCapCompass | wire::kCapWorld | wire::kCapFollowPoses);
    CHECK_EQ(w.srv.published, 1);
    CHECK(w.srv.catalog.get() && w.srv.catalog.get()->rows.size() == 6 && w.srv.catalog.map_verified());
    CHECK_EQ(w.srv.catalogs[0].size(), size_t{1});
    CHECK(!w.srv.catalogs[0].empty() && w.srv.catalogs[0][0].ready == 1 && w.srv.catalogs[0][0].count == 6 && w.srv.catalogs[0][0].role == 0);
    const auto info = p.receiver->info();
    CHECK(info.phase == Receiver::Phase::Linked && info.applied > 0 && info.view == 1);
    CHECK_EQ(p.count(1, KILN_MARKER_ADDED, KILN_MARKER_RECEIVED), size_t{1});
    // now the catalog is here: a map marker goes, and reaches the client
    r = w.send(0, map);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.committed == 1 && r.queued == 1 && r.refused == 0);
    w.net.settle();
    CHECK(p.row("gate") && p.row("gate")->rec.icon == kCamp);
    CHECK_EQ(p.count(1, KILN_MARKER_ADDED, KILN_MARKER_RECEIVED), size_t{2});
    // icons the catalog does not offer for the map
    Spec bad_icon("x", "t", KILN_MARKER_MAP, kUnknownIcon), travel("y", "t", KILN_MARKER_MAP, kTravel);
    r = w.send(0, bad_icon, KILN_ERR_INVALID_ARGUMENT);
    r = w.send(0, travel, KILN_ERR_INVALID_ARGUMENT);
    CHECK(std::strstr(r.reason, "fast-travel") != nullptr);
    kmk_test::end("link.m13_catalog_from_a_verified_client");
}

void wrong_catalog() {  // M13: a client whose catalog is not the build pair's never turns the map on
    kmk_test::begin();
    World w(false);
    Proc& p = w.client(false);
    p.catalog.publish(synthetic_catalog(true), true, "");  // another digest
    w.net.join(&p, 0x81, 101);
    w.net.settle();
    Server::LinkInfo li;
    CHECK(w.srv.server->link_info(0x81, &li));
    CHECK(li.welcomed && !li.map_ok && !li.need_catalog);
    CHECK_EQ(li.accepted & wire::kCapMap, 0u);
    CHECK_EQ(w.srv.published, 0);
    CHECK(!w.srv.catalog.get());
    // a client that sends a CATALOG whose digest field lies: refused, the map stays off for it
    World w2(false);
    Proc& q = w2.client();
    w2.net.filter = [](bool to_client, Conn&, uint32_t m, const std::vector<uint8_t>& b) {
        if (!to_client && m == wire::kCatalog) {
            auto& bytes = const_cast<std::vector<uint8_t>&>(b);
            bytes[wire::kHeaderBytes + 5] ^= 1;  // a row's id byte: the rows no longer give the digest
        }
        return 1;
    };
    w2.net.join(&q, 0x82, 102);
    w2.net.settle();
    CHECK(w2.srv.server->link_info(0x82, &li));
    CHECK(li.welcomed && !li.map_ok);
    CHECK(!w2.srv.catalog.get());
    Spec map("gate", "t", KILN_MARKER_MAP, kCamp);
    KilnMarkerResult r = w2.send(0, map);
    CHECK_EQ(r.status, KILN_MARKER_CATALOG_NOT_READY);
    kmk_test::end("link.m13_wrong_catalog_never_reaches_the_map");
}

void m09_layers() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    Proc& q = w.client();
    w.net.join(&p, 0x81, 101);
    w.net.join(&q, 0x82, 102);
    w.net.settle();
    Spec A("k", "A"), B("k", "B");
    auto text = [](Proc& c) -> std::string {
        const ClientRow* r = c.row("k");
        return r ? std::string(r->rec.text) : std::string("-");
    };
    KilnMarkerResult r = w.send(0, A);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.committed == 1 && r.queued == 2 && r.refused == 0 && r.affected == 1);
    w.net.settle();
    CHECK(text(p) == "A" && text(q) == "A");
    r = w.send(101, B);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 1);
    w.net.settle();
    CHECK(text(p) == "B" && text(q) == "A");
    r = w.unsend(101, "k");
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 1);
    w.net.settle();
    CHECK(text(p) == "A" && text(q) == "A");
    r = w.suppress(101, "k");
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.committed == 1);
    w.net.settle();
    CHECK(text(p) == "-" && text(q) == "A");
    r = w.suppress(101, "k");  // again: nothing changes
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.affected == 0);
    r = w.clear(101);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.affected == 1);
    w.net.settle();
    CHECK(text(p) == "A" && text(q) == "A");
    r = w.clear(0);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.affected == 1);
    w.net.settle();
    CHECK(text(p) == "-" && text(q) == "-");
    // the server's logical events: one per committed mutation
    const auto sk = kinds(w.srv, 0);
    const std::vector<uint32_t> want_s = {KILN_MARKER_ADDED, KILN_MARKER_ADDED, KILN_MARKER_REMOVED, KILN_MARKER_ADDED, KILN_MARKER_CLEARED, KILN_MARKER_CLEARED};
    CHECK(sk == want_s);
    if (w.srv.changes[0].size() == 6) {
        CHECK_EQ(w.srv.changes[0][0].audience, uint32_t{KILN_MARKER_SERVER_BROADCAST});
        CHECK(w.srv.changes[0][1].audience == KILN_MARKER_SERVER_TARGET && w.srv.changes[0][1].recipient == 101);
        CHECK_EQ(w.srv.changes[0][4].affected, 1u);
        CHECK(w.srv.changes[0][5].audience == KILN_MARKER_SERVER_BROADCAST && w.srv.changes[0][5].affected == 1);
        CHECK(std::string(w.srv.changes[0][0].owner) == "author" && std::string(w.srv.changes[0][0].key) == "k");
    }
    // the receivers' events: per changed effective key
    const std::vector<uint32_t> want_p = {KILN_MARKER_ADDED, KILN_MARKER_UPDATED, KILN_MARKER_UPDATED, KILN_MARKER_REMOVED, KILN_MARKER_ADDED,
                                          KILN_MARKER_REMOVED};
    CHECK(kinds(p, 1) == want_p);
    const std::vector<uint32_t> want_q = {KILN_MARKER_ADDED, KILN_MARKER_REMOVED};
    CHECK(kinds(q, 1) == want_q);
    for (const auto& c : p.changes[1]) CHECK(c.audience == KILN_MARKER_RECEIVED && std::string(c.owner) == "author");
    kmk_test::end("link.m09_target_and_broadcast_layers");
}

void updates_in_place() {  // a new value for a stored key (broadcast and per-player): one Updated per receiver
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    Proc& q = w.client();
    w.net.join(&p, 0x81, 101);
    w.net.join(&q, 0x82, 102);
    w.net.settle();
    Spec a1("k", "A1"), a2("k", "A2"), b1("k", "B1"), b2("k", "B2");
    w.send(0, a1);
    w.net.settle();
    KilnMarkerResult r = w.send(0, a2);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 2 && r.affected == 1);
    w.net.settle();
    CHECK(p.row("k") && std::string(p.row("k")->rec.text) == "A2" && q.row("k") && std::string(q.row("k")->rec.text) == "A2");
    w.send(101, b1);
    w.net.settle();
    r = w.send(101, b2);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 1);
    w.net.settle();
    CHECK(p.row("k") && std::string(p.row("k")->rec.text) == "B2" && std::string(q.row("k")->rec.text) == "A2");
    const std::vector<uint32_t> want_p = {KILN_MARKER_ADDED, KILN_MARKER_UPDATED, KILN_MARKER_UPDATED, KILN_MARKER_UPDATED};
    const std::vector<uint32_t> want_q = {KILN_MARKER_ADDED, KILN_MARKER_UPDATED};
    CHECK(kinds(p, 1) == want_p);
    CHECK(kinds(q, 1) == want_q);
    const std::vector<uint32_t> want_s = {KILN_MARKER_ADDED, KILN_MARKER_UPDATED, KILN_MARKER_ADDED, KILN_MARKER_UPDATED};
    CHECK(kinds(w.srv, 0) == want_s);
    kmk_test::end("link.updates_of_a_stored_key");
}

void m10_partial_and_retry() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    Conn& cp = w.net.join(&p, 0x81, 101);
    Conn& cq = w.net.join(nullptr, 0x82, 102, kVanillaPeer);  // a vanilla player
    w.net.settle();
    Spec A("k", "A");
    KilnMarkerResult r = w.send(0, A);
    CHECK_EQ(r.committed, 1u);
    CHECK_EQ(r.status, KILN_MARKER_PARTIAL);
    CHECK(r.queued == 1 && r.refused == 1 && reason_starts(r, "Partial"));
    w.net.step();  // the server's notes go out at its next step
    CHECK_EQ(w.srv.count(0, KILN_MARKER_ADDED), size_t{1});
    w.net.settle();
    CHECK_EQ(p.count(1, KILN_MARKER_ADDED), size_t{1});
    // the identical retry: no new event, nothing new for p (it has it), q still refused
    r = w.send(0, A);
    CHECK(r.committed == 1 && r.affected == 0 && r.queued == 0 && r.refused == 1 && r.status == KILN_MARKER_REQUIRES_CLIENT);
    w.net.settle();
    CHECK_EQ(w.srv.count(0, KILN_MARKER_ADDED), size_t{1});
    CHECK_EQ(p.count(1, KILN_MARKER_ADDED), size_t{1});
    CHECK_EQ(p.changes[1].size(), size_t{1});
    // a late Kiln player gets the broadcast in its snapshot
    Proc& late = w.client();
    w.net.join(&late, 0x83, 103);
    w.net.settle();
    CHECK_EQ(late.count(1, KILN_MARKER_ADDED), size_t{1});
    CHECK(late.row("k") && std::string(late.row("k")->rec.text) == "A");
    // a per-player override does not outlive its connection; the broadcast does
    Spec B("only_p", "B");
    r = w.send(101, B);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 1);
    w.net.settle();
    CHECK(p.row("only_p") != nullptr);
    const size_t target_clears = w.srv.count(0, KILN_MARKER_CLEARED, KILN_MARKER_SERVER_TARGET);
    w.net.leave(cp);
    CHECK_EQ(w.srv.count(0, KILN_MARKER_CLEARED, KILN_MARKER_SERVER_TARGET), target_clears);  // queued, delivered at the next step
    w.net.step();
    CHECK_EQ(w.srv.count(0, KILN_MARKER_CLEARED, KILN_MARKER_SERVER_TARGET), target_clears + 1);
    CHECK(p.received().empty());  // the client's received records went with the link
    w.net.join(&p, 0x81, 101);  // the same player again, a new connection
    w.net.settle();
    CHECK(p.row("k") != nullptr && p.row("only_p") == nullptr);
    // a targeted send to a player who is not connected is refused before the commit
    Spec C("c", "C");
    r = w.send(999, C);
    CHECK(r.status == KILN_MARKER_NOT_FOUND && r.committed == 0);
    // no frame ever went to the vanilla player
    CHECK_EQ(cq.frames, size_t{0});
    kmk_test::end("link.m10_partial_counts_retry_late_join_reconnect");
}

void m11_loss_and_resync() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    Conn& c = w.net.join(&p, 0x81, 101);
    w.net.settle();
    // (a) a snapshot of 40 records (two chunks) whose first chunk is lost once: the same transaction again, never half
    std::vector<std::unique_ptr<Spec>> specs;
    for (int i = 0; i < 40; ++i) {
        specs.push_back(std::make_unique<Spec>("k" + std::to_string(100 + i), "text " + std::to_string(i)));
        w.send(0, *specs.back());
    }
    w.net.leave(c);
    Conn& c2 = w.net.join(&p, 0x81, 101);
    int dropped = 0;
    w.net.filter = [&](bool to_client, Conn&, uint32_t m, const std::vector<uint8_t>& b) {
        if (to_client && m == wire::kSnapChunk && !dropped) {
            wire::Header h;
            wire::SnapChunk sc;
            const char* why = nullptr;
            if (wire::decode_snap_chunk(b.data(), b.size(), &h, &sc, &why) && sc.index == 0 && !sc.records.empty()) {
                ++dropped;
                return 0;
            }
        }
        return 1;
    };
    for (int i = 0; i < 120 && p.received().size() < 40; ++i) {
        w.net.step();
        const size_t n = p.received().size();
        CHECK(n == 0 || n == 40);  // nothing in between: a snapshot is published whole or not at all
    }
    CHECK_EQ(dropped, 1);
    CHECK_EQ(p.received().size(), size_t{40});
    w.net.settle();
    CHECK_EQ(p.count(1, KILN_MARKER_ADDED), size_t{40});
    // (b) a duplicated SNAP_END and duplicated chunks: no duplicate events
    const size_t added = p.count(1, KILN_MARKER_ADDED);
    w.net.filter = [](bool to_client, Conn&, uint32_t m, const std::vector<uint8_t>&) {
        return to_client && (m == wire::kSnapEnd || m == wire::kSnapChunk || m == wire::kDelta) ? 2 : 1;
    };
    Spec extra("extra", "x");
    w.send(0, extra);
    w.net.settle();
    CHECK_EQ(p.count(1, KILN_MARKER_ADDED), added + 1);
    // (c) ACKs held past 2 s: the transaction is sent again (same sequence), applied once
    w.net.filter = [](bool to_client, Conn&, uint32_t m, const std::vector<uint8_t>&) { return !to_client && m == wire::kAck ? 3 : 1; };
    w.net.hold_ms = 2600;
    Spec late("late", "y");
    w.send(0, late);
    w.net.sent_seq.clear();
    w.net.step(100);
    w.net.filter = nullptr;
    w.net.settle();
    CHECK_EQ(p.count(1, KILN_MARKER_ADDED), added + 2);
    int resent = 0;
    for (const auto& [k, n] : w.net.sent_seq)
        if (k.first == wire::kDelta && n > 1) ++resent;
    CHECK(resent >= 1);
    Server::LinkInfo li;
    CHECK(w.srv.server->link_info(0x81, &li) && !li.stalled && !li.in_flight);
    // (d) everything to the client lost for a while: 3 resends, then stalled; heartbeats bring a resync and a full
    // snapshot of the CURRENT state (a key removed meanwhile is not replayed)
    // (the heartbeats pass, so the lease holds; the client's RESYNCs are lost too)
    w.net.filter = [](bool to_client, Conn&, uint32_t m, const std::vector<uint8_t>&) {
        if (to_client) return m == wire::kHeartbeat || m == wire::kWelcome ? 1 : 0;
        return m == wire::kResync ? 0 : 1;
    };
    Spec gone("gone", "z");
    w.send(0, gone);
    w.net.sent_seq.clear();
    w.net.step(200);  // 10 s
    CHECK(w.srv.server->link_info(0x81, &li) && li.stalled);
    int tries = 0;
    for (const auto& [k, n] : w.net.sent_seq)
        if (k.first == wire::kDelta) tries = std::max(tries, n);
    CHECK_EQ(tries, 4);  // the first send and 3 resends
    w.unsend(0, "gone");
    Spec fresh("fresh", "f");
    w.send(0, fresh);
    // the link's lease ran out on the client meanwhile: it cleared and says HELLO again
    w.net.filter = nullptr;
    w.net.settle(80);
    CHECK(w.srv.server->link_info(0x81, &li) && !li.stalled);
    CHECK(p.row("gone") == nullptr && p.row("fresh") != nullptr && p.row("late") != nullptr);
    CHECK_EQ(p.received().size(), size_t{43});
    // (e) a packet of an old tuple (another nonce) is ignored: no event, not a bad message
    const size_t events = p.changes[1].size();
    wire::Header h;
    h.server_epoch = w.srv.server->epoch();
    h.client_nonce.fill(0x55);
    h.sequence = 999;
    auto bytes = wire::encode_heartbeat(h);
    p.inbox[1].push(kServerPeer, c2.cgen, wire::kHeartbeat, bytes.data(), bytes.size());
    w.net.step(3);
    CHECK_EQ(p.changes[1].size(), events);
    CHECK(!p.receiver->info().suspended);
    // (f) a gap (a DELTA two ahead) asks for RESYNC; the server answers with a full snapshot (no duplicate events)
    const auto ri = p.receiver->info();
    h.client_nonce = ri.nonce;
    h.server_epoch = ri.server_epoch;
    h.sequence = ri.applied + 2;
    wire::DeltaOp op;
    op.op = wire::kRemove;
    op.remove_id = 1;
    bytes = wire::encode_delta(h, {op});
    size_t resyncs = 0;
    w.net.filter = [&](bool to_client, Conn&, uint32_t m, const std::vector<uint8_t>&) {
        if (!to_client && m == wire::kResync) ++resyncs;
        return 1;
    };
    p.inbox[1].push(kServerPeer, c2.cgen, wire::kDelta, bytes.data(), bytes.size());
    w.net.settle();
    CHECK(resyncs >= 1);
    CHECK_EQ(p.changes[1].size(), events);
    CHECK_EQ(p.received().size(), size_t{43});
    w.net.filter = nullptr;
    kmk_test::end("link.m11_loss_duplicates_late_acks_stall_resync");
}

void m11_bad_messages() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    Proc& q = w.client();
    Conn& cp = w.net.join(&p, 0x81, 101);
    w.net.join(&q, 0x82, 102);
    w.net.settle();
    // three messages the server may not get, from p, in 10 s: p's marker link is suspended; q goes on
    wire::Header h;
    auto bytes = wire::encode_heartbeat(h);
    for (int i = 0; i < 3; ++i) w.srv.inbox[0].push(0x81, cp.sgen, wire::kHeartbeat, bytes.data(), bytes.size());
    w.net.step();
    Server::LinkInfo li;
    CHECK(w.srv.server->link_info(0x81, &li) && li.suspended);
    Spec A("k", "A");
    KilnMarkerResult r = w.send(0, A);
    CHECK(r.queued == 1 && r.refused == 1 && r.status == KILN_MARKER_PARTIAL);
    w.net.step(5);
    CHECK(q.row("k") != nullptr && p.row("k") == nullptr);
    // p's receiver says HELLO again once its lease runs out; the server takes it back
    w.net.settle(260);
    CHECK(w.srv.server->link_info(0x81, &li) && !li.suspended && li.welcomed);
    CHECK(p.row("k") != nullptr);
    // the receiver: three client-to-server messages from the server suspend its link only
    for (int i = 0; i < 3; ++i) p.inbox[1].push(kServerPeer, cp.cgen, wire::kAck, bytes.data(), bytes.size());
    w.net.step();
    CHECK(p.receiver->info().suspended);
    // a flood: the inbound queue holds 128 envelopes, the rest are dropped (and asked for again), never applied in part
    size_t taken = 0;
    for (int i = 0; i < 200; ++i) taken += q.inbox[1].push(kServerPeer, 1, wire::kHeartbeat, bytes.data(), bytes.size());
    CHECK_EQ(taken, Inbox::kEnvelopes);
    w.net.settle();
    CHECK(q.row("k") != nullptr && !q.receiver->info().suspended);
    kmk_test::end("link.m11_bad_messages_suspend_the_marker_link_only");
}

void receiver_limits() {  // the receiver's own limits against a server that does not keep them (crafted frames)
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    Conn& c = w.net.join(&p, 0x81, 101);
    w.net.settle();
    const auto ri = p.receiver->info();
    wire::Header h;
    h.server_epoch = ri.server_epoch;
    h.client_nonce = ri.nonce;
    std::vector<wire::Ack> acks;
    w.net.filter = [&](bool to_client, Conn&, uint32_t m, const std::vector<uint8_t>& b) {
        if (!to_client && m == wire::kAck) {
            wire::Header ah;
            wire::Ack a;
            const char* why = nullptr;
            if (wire::decode_ack(b.data(), b.size(), &ah, &a, &why)) acks.push_back(a);
        }
        return to_client ? 0 : 1;  // the real server stays quiet
    };
    // 129 records of one namespace in one snapshot: refused whole (Full), nothing applied
    std::vector<wire::Record> recs;
    for (int i = 0; i < 129; ++i) {
        wire::Record r;
        r.wire_id = 5000 + i;
        r.publisher_generation = 1;
        r.ns = "flood";
        char k[16];
        std::snprintf(k, sizeof k, "k%03d", i);
        r.key = k;
        r.revision = 1;
        r.surfaces = KILN_MARKER_WORLD;
        r.x = 1;
        r.y = 2;
        r.z = 3;
        recs.push_back(r);
    }
    h.sequence = ri.applied + 1;
    wire::SnapBegin b{77, 129, 5};
    auto push = [&](uint32_t m, const std::vector<uint8_t>& bytes) { p.inbox[1].push(kServerPeer, c.cgen, m, bytes.data(), bytes.size()); };
    push(wire::kSnapBegin, wire::encode_snap_begin(h, b));
    for (uint16_t i = 0; i < 5; ++i) {
        std::vector<wire::Record> part(recs.begin() + i * 32, recs.begin() + std::min<size_t>(129, (i + 1) * 32));
        push(wire::kSnapChunk, wire::encode_snap_chunk(h, 77, i, part));
    }
    push(wire::kSnapEnd, wire::encode_snap_end(h, wire::SnapEnd{77, wire::snapshot_digest(recs)}));
    w.net.step(4);
    CHECK(!acks.empty() && acks.back().status == wire::kAckFull);
    CHECK_EQ(p.received().size(), size_t{0});  // nothing applied
    // a snapshot whose digest does not match: never applied, RESYNC asked
    acks.clear();
    recs.resize(3);
    b = wire::SnapBegin{78, 3, 1};
    h.sequence = ri.applied + 2;
    push(wire::kSnapBegin, wire::encode_snap_begin(h, b));
    push(wire::kSnapChunk, wire::encode_snap_chunk(h, 78, 0, recs));
    wire::Digest wrong{};
    push(wire::kSnapEnd, wire::encode_snap_end(h, wire::SnapEnd{78, wrong}));
    w.net.step(4);
    CHECK(acks.empty());
    CHECK_EQ(p.received().size(), size_t{0});
    // an unsupported surface (the tooltip was not accepted): ACK unsupported, prior view kept
    recs.resize(1);
    recs[0].surfaces = KILN_MARKER_MAP;
    recs[0].icon = kCamp;
    recs[0].label_mode = KILN_MARKER_LABEL_NATIVE_TOOLTIP;
    h.sequence = ri.applied + 3;
    b = wire::SnapBegin{79, 1, 1};
    push(wire::kSnapBegin, wire::encode_snap_begin(h, b));
    push(wire::kSnapChunk, wire::encode_snap_chunk(h, 79, 0, recs));
    push(wire::kSnapEnd, wire::encode_snap_end(h, wire::SnapEnd{79, wire::snapshot_digest(recs)}));
    w.net.step(4);
    CHECK(!acks.empty() && acks.back().status == wire::kAckUnsupported);
    CHECK_EQ(p.received().size(), size_t{0});
    w.net.filter = nullptr;
    kmk_test::end("link.m11_receiver_limits_and_refusals");
}

// issue 160: MapLabel.NativeTooltip from a server. A player's Kiln offers MapText in its HELLO when its markers.map_text
// is on (receiver.cpp local_caps); the server accepts it with the map (a verified catalog) and sends NativeTooltip
// markers to those links only. Any other player is refused (RequiresClient): nothing is drawn partly, and the mod can
// send that player the same marker without the tooltip (a per-player value).
void native_tooltip_per_player() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();  // shows the native tooltip
    p.sw.map_text = true;
    Proc& q = w.client();  // its client cannot (markers.map_text off: an older Kiln, or the feature unresolved)
    Proc& u = w.client(false);  // offers MapText but its catalog is not the build pair's: no map, so no tooltip
    u.sw.map_text = true;
    u.catalog.publish(synthetic_catalog(true), true, "");
    w.net.join(&p, 0x81, 101);
    w.net.join(&q, 0x82, 102);
    w.net.join(&u, 0x83, 103);
    w.net.settle();
    Server::LinkInfo li;
    CHECK(w.srv.server->link_info(0x81, &li));
    CHECK_EQ(li.accepted, wire::kCapMap | wire::kCapCompass | wire::kCapWorld | wire::kCapMapText | wire::kCapFollowPoses);
    CHECK(w.srv.server->link_info(0x82, &li));
    CHECK_EQ(li.accepted & wire::kCapMapText, 0u);
    CHECK(w.srv.server->link_info(0x83, &li));
    CHECK_EQ(li.accepted & (wire::kCapMap | wire::kCapMapText), 0u);
    Spec chest("chest1", "Golden chest 1", KILN_MARKER_MAP, kCamp);
    chest.s.map_label_mode = KILN_MARKER_LABEL_NATIVE_TOOLTIP;
    KilnMarkerResult r = w.send(0, chest);
    CHECK(r.committed == 1 && r.status == KILN_MARKER_PARTIAL && r.queued == 1 && r.refused == 2 && reason_starts(r, "Partial"));
    w.net.settle();
    const ClientRow* got = p.row("chest1");
    CHECK(got && got->rec.label_mode == KILN_MARKER_LABEL_NATIVE_TOOLTIP && got->rec.text == "Golden chest 1" && got->rec.icon == kCamp);
    CHECK(q.row("chest1") == nullptr && u.row("chest1") == nullptr);
    // the mod's fallback for q: the same key without the tooltip, for that player only
    Spec plain("chest1", "Golden chest 1", KILN_MARKER_MAP, kCamp);
    r = w.send(102, plain);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 1);
    w.net.settle();
    CHECK(q.row("chest1") && q.row("chest1")->rec.label_mode == KILN_MARKER_LABEL_NONE);
    CHECK(p.row("chest1") && p.row("chest1")->rec.label_mode == KILN_MARKER_LABEL_NATIVE_TOOLTIP);
    // a compass-only marker never needed the tooltip: everyone with Kiln takes it
    Spec far("far", "Far", KILN_MARKER_COMPASS);
    r = w.send(0, far);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 3 && r.refused == 0);
    kmk_test::end("link.native_tooltip_goes_to_players_whose_kiln_shows_it");
}

void hosted_loopback() {  // route 2: the hosting player's own receiver, counted once with its Network row
    kmk_test::begin();
    World w;
    Proc& host = w.srv;
    host.sw.compass = host.sw.world = host.sw.map = true;
    host.store.world_started(KILN_WORLD_CLIENT);
    host.receiver->world_started();
    // the hosting player (entity 100): its route-2 endpoint and the Network row of its own machine (slot 0)
    CHECK_EQ(up(*host.bridge, peer(0, kLoopbackServerPeer, kRouteLoopback, 100, kKiln, 9)), KILN_OK);
    CHECK_EQ(up(*host.bridge, peer(1, kServerPeer, kRouteLoopback, 0, kNetworkReady | kHasMarkersChannel, 9)), KILN_OK);
    Conn& own = w.net.join(nullptr, 0x80, 100);  // must get nothing
    Proc& p = w.client();
    w.net.join(&p, 0x81, 101);
    for (int i = 0; i < 40; ++i) {
        w.net.step();
        host.receiver->post();
        host.take();
        uint32_t done = 0;
        host.bridge->pump(1, 16, nullptr, nullptr, &done, g_now);  // route 2 never reaches a send callback
    }
    Spec A("k", "A");
    KilnMarkerResult r = w.send(0, A);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 2 && r.refused == 0);  // the host once, p once
    for (int i = 0; i < 40; ++i) {
        w.net.step();
        host.receiver->post();
        host.take();
        uint32_t done = 0;
        host.bridge->pump(1, 16, nullptr, nullptr, &done, g_now);
    }
    CHECK(host.row("k") != nullptr && p.row("k") != nullptr);
    CHECK_EQ(host.count(1, KILN_MARKER_ADDED, KILN_MARKER_RECEIVED), size_t{1});
    CHECK_EQ(own.frames, size_t{0});
    // the host is targeted through its loopback
    Spec B("mine", "B");
    r = w.send(100, B);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 1);
    kmk_test::end("link.hosted_loopback_counted_once");
}

void follow_poses() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    w.net.join(&p, 0x81, 101);
    w.net.settle();
    Spec f("boss", "Boss", KILN_MARKER_WORLD);
    f.s.follow = 77;
    KilnMarkerResult r = w.send(0, f, KILN_ERR_UNAVAILABLE);  // no anchors on the server yet
    CHECK(std::string(r.reason) == "markers.anchors.server");
    g_follow_on = true;
    g_follow[77] = {{100, 50, 100}, KILN_MARKER_READY};
    r = w.send(0, f);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 1);
    w.net.settle(10);
    const ClientRow* row = p.row("boss");
    CHECK(row && row->followed && row->rec.x == 100);
    const size_t events = p.changes[1].size();
    // it moves 0.5 m every 50 ms: poses, no logical change
    for (int i = 0; i < 40; ++i) {
        g_follow[77].first.x += 0.5;
        w.net.step();
    }
    w.net.step(4);
    row = p.row("boss");
    CHECK(row && std::fabs(row->rec.x - g_follow[77].first.x) < 2.6);  // within one or two pose periods
    CHECK(row && row->pose_prev_ms < row->pose_last_ms && row->pose_last.x == row->rec.x);
    CHECK_EQ(p.changes[1].size(), events);
    // standing still: keepalives every 500 ms keep it fresh; without poses for 750 ms it reads SOURCE_STALE
    w.net.step(30);
    row = p.row("boss");
    CHECK(row && row->source_reason == KILN_MARKER_READY);
    g_now += 800;
    row = p.row("boss");
    CHECK(row && row->source_reason == KILN_MARKER_SOURCE_STALE);
    w.net.step(15);
    row = p.row("boss");
    CHECK(row && row->source_reason == KILN_MARKER_READY);
    // the entity is destroyed: ANCHOR_ENDED, kept (no Removed); the same serial never comes back
    g_follow[77].second = KILN_MARKER_ANCHOR_ENDED;
    w.net.step(10);
    row = p.row("boss");
    CHECK(row && row->source_reason == KILN_MARKER_ANCHOR_ENDED);
    g_follow[77].second = KILN_MARKER_READY;
    w.net.step(20);
    row = p.row("boss");
    CHECK(row && row->source_reason == KILN_MARKER_ANCHOR_ENDED);
    CHECK_EQ(p.changes[1].size(), events);
    // a new capture (serial 78) sent again: one Updated, READY again
    g_follow[78] = {{300, 60, 300}, KILN_MARKER_READY};
    f.s.follow = 78;
    r = w.send(0, f);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.affected == 1);
    w.net.settle(10);
    row = p.row("boss");
    CHECK(row && row->source_reason == KILN_MARKER_READY && row->rec.x == 300);
    CHECK_EQ(p.changes[1].size(), events + 1);
    CHECK_EQ(p.count(1, KILN_MARKER_UPDATED), size_t{1});
    g_follow_on = false;
    g_follow.clear();
    kmk_test::end("link.follow_poses_stale_and_ended");
}

void limits() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    w.net.join(&p, 0x81, 101);
    w.net.settle();
    // 128 per publisher
    for (int i = 0; i < 128; ++i) {
        Spec s("a" + std::to_string(i), "t");
        KilnMarkerResult r = w.send(0, s);
        CHECK_EQ(r.status, KILN_MARKER_ACCEPTED);
        if (i % 30 == 0) w.net.step();  // the step takes the queued notes (64 per publisher may wait)
    }
    w.net.step();
    Spec over("a128", "t");
    KilnMarkerResult r = w.send(0, over);
    CHECK(r.status == KILN_MARKER_FULL && r.committed == 0);
    // 512 in the broadcast: three more publishers fill it, a fifth is refused
    for (KilnOwner o = 6; o <= 9; ++o) {
        const std::string ns = "pub" + std::to_string(o);
        CHECK_EQ(w.srv.store.bind(o, KilnStr{ns.data(), ns.size()}), KILN_OK);
    }
    for (KilnOwner o = 6; o <= 8; ++o)
        for (int i = 0; i < 128; ++i) {
            Spec s("b" + std::to_string(i), "t");
            KilnMarkerResult rr = res();
            CHECK_EQ(w.srv.server->send(o, 0, &s, &rr), KILN_OK);
            CHECK_EQ(rr.status, KILN_MARKER_ACCEPTED);
            if (i % 30 == 0) w.net.step();
        }
    w.net.step();
    Spec fifth("c", "t");
    KilnMarkerResult rr = res();
    CHECK_EQ(w.srv.server->send(9, 0, &fifth, &rr), KILN_OK);
    CHECK(rr.status == KILN_MARKER_FULL && rr.committed == 0 && reason_starts(rr, "Full: 512 broadcast"));
    // a recipient sees at most 512: a per-player marker on top is refused for that player
    rr = res();
    CHECK_EQ(w.srv.server->send(9, 101, &fifth, &rr), KILN_OK);
    CHECK(rr.status == KILN_MARKER_FULL && rr.committed == 0);
    const size_t before = w.srv.changes[0].size();
    w.net.step();
    CHECK_EQ(w.srv.changes[0].size(), before);  // refusals made no event
    w.net.settle(200);
    CHECK_EQ(p.received().size(), size_t{512});
    // release_owner: one CLEARED for its scope, its records leave every receiver
    w.srv.server->release_owner(6);
    w.net.step();
    CHECK_EQ(w.srv.count(0, KILN_MARKER_CLEARED), size_t{1});
    CHECK(!w.srv.changes[0].empty() && w.srv.changes[0].back().cause == KILN_MARKER_OWNER_RETIRED && w.srv.changes[0].back().affected == 128);
    w.net.settle(80);
    CHECK_EQ(p.received().size(), size_t{384});
    kmk_test::end("link.limits_and_release_owner");
}

void error_rule() {
    kmk_test::begin();
    World w;
    Spec ok("k", "t");
    KilnMarkerResult r = res();
    w.srv.server->set_available(false);
    CHECK_EQ(w.srv.server->send(5, 0, &ok, &r), KILN_ERR_UNAVAILABLE);
    CHECK(std::string(r.reason) == "markers.send");
    w.srv.server->set_available(true);
    w.srv.on_thread = false;
    r = res();
    CHECK_EQ(w.srv.server->send(5, 0, &ok, &r), KILN_ERR_WRONG_THREAD);
    r = res();
    CHECK_EQ(w.srv.server->unsend(5, 0, ks("k"), &r), KILN_ERR_WRONG_THREAD);
    w.srv.on_thread = true;
    // issue 160: NativeTooltip is not refused by the server's own rows (it has no map); each link decides (native_tooltip_per_player)
    Spec tooltip("tip", "t", KILN_MARKER_MAP, kCamp);
    tooltip.s.map_label_mode = KILN_MARKER_LABEL_NATIVE_TOOLTIP;
    r = res();
    CHECK_EQ(w.srv.server->send(5, 0, &tooltip, &r), KILN_OK);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.committed == 1);
    r = w.unsend(0, "tip");
    CHECK(r.committed == 1);
    Spec bad("bad key", "t");
    r = res();
    CHECK_EQ(w.srv.server->send(5, 0, &bad, &r), KILN_ERR_INVALID_ARGUMENT);
    r = res();
    CHECK_EQ(w.srv.server->send(42, 0, &ok, &r), KILN_ERR_INVALID_ARGUMENT);  // not bound
    r = res();
    CHECK_EQ(w.srv.server->suppress_sent(5, 0, ks("k"), &r), KILN_ERR_INVALID_ARGUMENT);
    r = w.unsend(0, "k");
    CHECK(r.status == KILN_MARKER_NOT_FOUND && r.committed == 0);
    r = w.clear(0);  // an empty scope: Ok, nothing changed
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.affected == 0);
    // broadcast without recipients: Ok, for later joins
    r = w.send(0, ok);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.committed == 1 && r.queued == 0 && r.refused == 0);
    // no server world: NotReady
    w.srv.server->world_stopping();
    w.srv.store.world_stopping(KILN_WORLD_SERVER);
    w.srv.take();
    CHECK(!w.srv.changes[0].empty() && w.srv.changes[0].back().kind == KILN_MARKER_CLEARED && w.srv.changes[0].back().cause == KILN_MARKER_WORLD_STOPPED);
    r = res();
    CHECK_EQ(w.srv.server->send(5, 0, &ok, &r), KILN_OK);
    CHECK_EQ(r.status, KILN_MARKER_NOT_READY);
    kmk_test::end("link.error_rule_and_world_stop");
}

void client_world_stop() {  // M12: a client leaving its world: one CLEARED per namespace; a new world starts empty
    kmk_test::begin();
    World w;
    CHECK_EQ(w.srv.store.bind(6, KilnStr{"other", 5}), KILN_OK);
    Proc& p = w.client();
    Conn& c = w.net.join(&p, 0x81, 101);
    w.net.settle();
    Spec a("a", "A"), b("b", "B");
    w.send(0, a);
    KilnMarkerResult r = res();
    CHECK_EQ(w.srv.server->send(6, 0, &b, &r), KILN_OK);
    w.net.settle();
    CHECK_EQ(p.received().size(), size_t{2});
    p.receiver->world_stopping();
    p.store.world_stopping(KILN_WORLD_CLIENT);
    p.take();
    CHECK_EQ(p.count(1, KILN_MARKER_CLEARED), size_t{2});
    for (const auto& ch : p.changes[1])
        if (ch.kind == KILN_MARKER_CLEARED) CHECK(ch.cause == KILN_MARKER_WORLD_STOPPED && ch.affected == 1);
    CHECK(p.received().empty());
    // rejoin: a new client world gets the server's current broadcast again
    p.store.world_started(KILN_WORLD_CLIENT);
    p.receiver->world_started();
    w.net.settle();
    CHECK_EQ(p.received().size(), size_t{2});
    (void)c;
    kmk_test::end("link.m12_client_world_stop_and_rejoin");
}

void bridge_rules() {
    kmk_test::begin();
    Proc host(KILN_SIDE_SERVER, kServerExe, false);
    // receive copies the bytes
    std::vector<uint8_t> b = {1, 2, 3};
    KilnMarkerEnvelope e{};
    e.size = sizeof e;
    e.role = 0;
    e.peer = 5;
    e.message = 8;
    e.peer_generation = 1;
    e.bytes = b.data();
    e.length = 3;
    CHECK_EQ(host.bridge->receive(&e), KILN_OK);
    b[0] = 9;
    auto q = host.inbox[0].take();
    CHECK(q.size() == 1 && q[0].bytes[0] == 1 && q[0].peer == 5 && q[0].message == 8);
    e.size = 8;
    CHECK_EQ(host.bridge->receive(&e), KILN_ERR_INVALID_ARGUMENT);
    // peer_up is idempotent per generation; peer_down needs the exact generation
    CHECK_EQ(up(*host.bridge, peer(0, 7, kRouteNetwork, 0, kNetworkReady, 3)), KILN_OK);
    CHECK_EQ(up(*host.bridge, peer(0, 7, kRouteNetwork, 11, kKiln, 3)), KILN_OK);
    CHECK_EQ(host.bridge->peer_down(0, 7, 4), KILN_ERR_NOT_FOUND);
    CHECK_EQ(host.bridge->peer_down(0, 7, 3), KILN_OK);
    KilnMarkerPeer bad = peer(0, 7, 3, 0, 0, 3);
    CHECK_EQ(host.bridge->peer_up(&bad), KILN_ERR_INVALID_ARGUMENT);
    // 32 registered peers at most
    for (uint32_t i = 0; i < 32; ++i) CHECK_EQ(up(*host.bridge, peer(0, 100 + i, kRouteNetwork, 0, 0, 1)), KILN_OK);
    CHECK_EQ(up(*host.bridge, peer(0, 200, kRouteNetwork, 0, 0, 1)), KILN_ERR_BUFFER_TOO_SMALL);
    // an unattached bridge answers unavailable
    Inbox i0, i1;
    Bridge none(nullptr, nullptr, &i0, &i1);
    e.size = sizeof e;
    CHECK_EQ(none.receive(&e), KILN_ERR_UNAVAILABLE);
    kmk_test::end("link.bridge_table_rules");
}

}  // namespace

// The server keeps exactly one anchors reference per followed incarnation while a stored definition follows it: refusals,
// identical values and a second definition of the same incarnation give the call's reference back; the last definition
// that goes gives the kept one back (S3 + S7 seam).
void follow_references() {
    kmk_test::begin();
    World w;
    g_follow_on = true;
    g_follow[77] = {{100, 50, 100}, KILN_MARKER_READY};
    g_follow[78] = {{120, 50, 100}, KILN_MARKER_READY};
    g_refs.clear();
    Spec a("ra", "A", KILN_MARKER_WORLD);
    a.s.follow = 77;
    w.send(0, a);
    CHECK_EQ(g_refs[77], 1);
    w.send(0, a);  // identical
    CHECK_EQ(g_refs[77], 1);
    Spec b("rb", "B", KILN_MARKER_WORLD);
    b.s.follow = 77;
    w.send(0, b);  // a second definition of the same incarnation
    CHECK_EQ(g_refs[77], 1);
    Spec fixed("ra", "A fixed", KILN_MARKER_WORLD);
    w.send(0, fixed);  // ra stops following; rb still does
    CHECK_EQ(g_refs[77], 1);
    w.unsend(0, "rb");
    CHECK_EQ(g_refs[77], 0);
    Spec c("rc", "C", KILN_MARKER_WORLD);
    c.s.follow = 78;
    w.send(0, c);
    CHECK_EQ(g_refs[78], 1);
    Spec bad("rd", "D", KILN_MARKER_WORLD | KILN_MARKER_MAP);  // map without an icon: refused before commit
    bad.s.follow = 78;
    w.send(0, bad, KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(g_refs[78], 1);
    w.clear(0);
    CHECK_EQ(g_refs[78], 0);
    g_follow_on = false;
    kmk_test::end("link.follow_references: one anchors reference per followed incarnation, given back by the last definition");
}

// Review F1: received text is charged per link generation, and a record over its namespace's budget is left at its last
// value without refusing the transaction: other mods keep flowing, and a new server starts with a fresh budget.
std::string padded(const std::string& head) {
    std::string t = head;
    t.resize(128, 'x');  // the longest legal text
    return t;
}

void received_text_budget() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    Conn& c1 = w.net.join(&p, 0x81, 101);
    w.net.settle();
    CHECK_EQ(w.srv.store.bind(6, KilnStr{"other", 5}), KILN_OK);
    Spec o("o1", "Other mod's marker");
    KilnMarkerResult r = res();
    CHECK_EQ(w.srv.server->send(6, 0, &o, &r), KILN_OK);
    w.net.settle();
    CHECK(p.row("o1") != nullptr);
    // one label whose text keeps changing (a countdown): every value is new text for the client
    int delivered = 0;
    std::string last;
    for (int i = 0; i < 2600; ++i) {
        Spec clock("clock", padded("Event ends in " + std::to_string(i) + " "));
        w.send(0, clock);
        w.net.step(3);
        const ClientRow* row = p.row("clock");
        if (row && std::string(row->rec.text) == clock.text) {
            ++delivered;
            last = clock.text;
        }
    }
    CHECK(delivered >= 2000 && delivered < 2600);  // the namespace's 256 KiB of received text holds per link
    const ClientRow* row = p.row("clock");
    CHECK(row && std::string(row->rec.text) == last);  // kept at its last value, not removed
    Server::LinkInfo li;
    CHECK(w.srv.server->link_info(0x81, &li));
    CHECK_EQ(li.refused, 0u);  // the link is not refused
    CHECK(!li.stalled);
    Spec o2("o2", "Other mod's second marker");
    r = res();
    CHECK_EQ(w.srv.server->send(6, 0, &o2, &r), KILN_OK);
    CHECK(r.queued == 1 && r.refused == 0);
    w.net.settle();
    CHECK(p.row("o2") != nullptr);  // another mod keeps flowing
    // the player leaves and joins another server in the same process: a fresh budget
    w.net.leave(c1);
    p.receiver->world_stopping();
    p.store.world_stopping(KILN_WORLD_CLIENT);
    p.store.world_started(KILN_WORLD_CLIENT);
    p.receiver->world_started();
    World w2;
    Spec hello("welcome", "Welcome!");
    w2.send(0, hello);
    w2.net.join(&p, 0x91, 201);
    w2.net.settle();
    CHECK(p.row("welcome") != nullptr);
    kmk_test::end("link.received_text_budget: per link, one record over budget keeps its last value, others and a new server flow");
}

// Review F2: one Kiln peer that floods the server role's inbox (200 well-formed frames a second, Kiln.Network's per-peer
// allowance) must not stall another player's link: its frames beyond its own share are dropped, the others' ACKs get in.
void inbox_flood() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    w.net.join(&p, 0x81, 101);
    w.net.settle();
    Spec a("a", "before the flood");
    w.send(0, a);
    w.net.settle();
    CHECK(p.row("a") != nullptr);
    Conn& bad = w.net.join(nullptr, 0x82, 102);
    std::vector<uint8_t> junk(60, 0xAB);
    int c_sent_at = -1, c_arrived_at = -1;
    for (int i = 0; i < 300; ++i) {  // 15 s
        for (int k = 0; k < 10; ++k) w.srv.inbox[0].push(bad.peer, bad.sgen, wire::kAck, junk.data(), junk.size());
        if (i == 40) {
            Spec b("b", "during the flood");
            w.send(0, b);
        }
        if (i == 150) {
            Spec c("c", "later in the flood");
            w.send(0, c);
            c_sent_at = i;
        }
        w.net.step(1);
        if (c_sent_at >= 0 && c_arrived_at < 0 && p.row("c")) c_arrived_at = i;
    }
    Server::LinkInfo li;
    CHECK(w.srv.server->link_info(0x81, &li));
    CHECK(!li.stalled);
    CHECK(p.row("b") != nullptr);
    CHECK(c_arrived_at >= 0 && c_arrived_at - c_sent_at <= 40);  // within 2 s, during the flood
    CHECK(w.srv.inbox[0].size() <= Inbox::kPerPeerServer + 2);   // the flooder holds only its own share of the queue
    kmk_test::end("link.inbox_flood: one flooding peer keeps only its own share of the server inbox; others' acks get in");
}

// Review test gap 2: a cheap random-input run of the wire decoders and of both roles' handling of mutated real frames
// (bit flips, truncation, garbage tails, wrong message ids), under ASan/UBSan. Afterwards the link must recover and the
// client's limits must hold.
void fuzz_wire_and_roles() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    Conn& c = w.net.join(&p, 0x81, 101);
    std::vector<std::pair<uint32_t, std::vector<uint8_t>>> to_client, to_server;
    w.net.filter = [&](bool down, Conn&, uint32_t m, const std::vector<uint8_t>& b) {
        auto& v = down ? to_client : to_server;
        if (v.size() < 600) v.push_back({m, b});
        return 1;
    };
    w.net.settle();
    g_follow_on = true;
    g_follow[91] = {{110, 50, 100}, KILN_MARKER_READY};
    for (int i = 0; i < 6; ++i) {
        Spec s("f" + std::to_string(i), "Fuzz seed " + std::to_string(i), i % 2 ? KILN_MARKER_WORLD : KILN_MARKER_COMPASS);
        if (i == 3) s.s.follow = 91;
        w.send(i == 4 ? 101 : 0, s);
        w.net.step(4);
        g_follow[91].first.x += 1;
    }
    w.unsend(0, "f1");
    w.net.settle(20);
    w.net.filter = nullptr;
    CHECK(to_client.size() > 10 && to_server.size() > 3);
    uint64_t rng = 0x9e3779b97f4a7c15ull;
    auto next = [&] {
        rng ^= rng << 13;
        rng ^= rng >> 7;
        rng ^= rng << 17;
        return rng;
    };
    auto mutate = [&](std::vector<uint8_t> b) {
        switch (next() % 5) {
            case 0: for (int k = 0, n = 1 + int(next() % 4); k < n && !b.empty(); ++k) b[next() % b.size()] ^= uint8_t(1u << (next() % 8)); break;
            case 1: if (!b.empty()) b.resize(next() % b.size()); break;
            case 2: for (int k = 0, n = int(next() % 64); k < n; ++k) b.push_back(uint8_t(next())); break;
            case 3: if (b.size() > 44) for (size_t k = 44; k < b.size(); ++k) if (next() % 7 == 0) b[k] = uint8_t(next()); break;
            default: for (auto& x : b) if (next() % 13 == 0) x = uint8_t(next()); break;
        }
        return b;
    };
    wire::Header h;
    const char* why;
    for (int i = 0; i < 20000; ++i) {
        const bool down = next() % 4 != 0;
        const auto& pick = down ? to_client[next() % to_client.size()] : to_server[next() % to_server.size()];
        const std::vector<uint8_t> b = mutate(pick.second);
        const uint32_t m = next() % 9 == 0 ? uint32_t(next() % 14) : pick.first;
        // every decoder on every buffer: none may read out of bounds or accept a malformed length
        { wire::Hello x; wire::decode_hello(b.data(), b.size(), &h, &x, &why); }
        { wire::Welcome x; wire::decode_welcome(b.data(), b.size(), &h, &x, &why); }
        { wire::Catalog x; wire::decode_catalog(b.data(), b.size(), &h, &x, &why); }
        { wire::SnapBegin x; wire::decode_snap_begin(b.data(), b.size(), &h, &x, &why); }
        { wire::SnapChunk x; wire::decode_snap_chunk(b.data(), b.size(), &h, &x, &why); }
        { wire::SnapEnd x; wire::decode_snap_end(b.data(), b.size(), &h, &x, &why); }
        { std::vector<wire::DeltaOp> x; wire::decode_delta(b.data(), b.size(), &h, &x, &why); }
        { std::vector<wire::Pose> x; wire::decode_pose(b.data(), b.size(), &h, &x, &why); }
        { wire::Ack x; wire::decode_ack(b.data(), b.size(), &h, &x, &why); }
        { wire::Resync x; wire::decode_resync(b.data(), b.size(), &h, &x, &why); }
        wire::decode_heartbeat(b.data(), b.size(), &h, &why);
        if (down) p.inbox[1].push(kServerPeer, c.cgen, m, b.data(), b.size());
        else w.srv.inbox[0].push(c.peer, c.sgen, m, b.data(), b.size());
        if (i % 16 == 0) w.net.step(1);
        CHECK(p.store.received_count() <= 512);
    }
    // quiet again: the suspension and lease run out, the link comes back with a full snapshot
    w.net.step(400);
    Spec after("after_fuzz", "After the fuzz");
    w.send(0, after);
    w.net.settle(60);
    CHECK(p.row("after_fuzz") != nullptr);
    g_follow_on = false;
    kmk_test::end("link.fuzz: 20000 mutated frames through every decoder and both roles, then the link recovers");
}

// Review question (server.cpp peer_up): a player's entity id going to 0 and coming back as another id on the SAME
// connection (dying and respawning, a recreated player entity) keeps that player's per-player markers: a connection
// carries one player, a new character means a new connection.
void respawn_keeps_target_layer() {
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    Conn& c = w.net.join(&p, 0x81, 101);
    w.net.settle();
    Spec a("k", "For everyone");
    w.send(0, a);
    Spec b("k", "Only for you");
    w.send(101, b);
    w.net.settle();
    const ClientRow* row = p.row("k");
    CHECK(row && std::string(row->rec.text) == "Only for you");
    CHECK_EQ(up(*w.srv.bridge, peer(0, 0x81, kRouteNetwork, 0, kKiln & ~kHasPlayer, c.sgen)), KILN_OK);  // dead: no entity
    w.net.step(10);
    CHECK_EQ(up(*w.srv.bridge, peer(0, 0x81, kRouteNetwork, 105, kKiln, c.sgen)), KILN_OK);  // respawned as 105
    w.net.settle();
    row = p.row("k");
    CHECK(row && std::string(row->rec.text) == "Only for you");
    Spec t("t2", "Sent to the new id");
    KilnMarkerResult r = w.send(105, t);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.queued == 1);
    w.net.settle();
    CHECK(p.row("t2") != nullptr);
    kmk_test::end("link.respawn_keeps_target_layer: a player's per-player markers survive an entity id change on one connection");
}

void hello_backs_off() {  // a server that never answers (an older Kiln refusing this client's HELLO, a link gone silent)
    kmk_test::begin();
    World w;
    Proc& p = w.client();
    std::vector<uint64_t> hellos;  // when the client said HELLO
    bool answer = false;
    w.net.filter = [&](bool to_client, Conn&, uint32_t message, const std::vector<uint8_t>&) {
        if (!to_client && message == wire::kHello) hellos.push_back(g_now);
        return answer || to_client ? 1 : 0;  // the server never hears it
    };
    Conn& c = w.net.join(&p, 0x81, 101);
    w.net.step(12000);  // 10 minutes
    if (g_verbose) std::printf("    %zu HELLOs in 10 minutes\n", hellos.size());
    CHECK(hellos.size() >= 9);   // the first five a second apart, then 5, 10, 20, 40 s
    CHECK(hellos.size() <= 20);  // then one a minute: not one every 5 s for ever (124 in 10 minutes)
    CHECK(hellos.size() >= 7 && hellos[1] - hellos[0] <= 1050 && hellos[5] - hellos[4] <= 1050);  // one a second at first
    CHECK(hellos.size() >= 7 && hellos[6] - hellos[5] >= 4950 && hellos[6] - hellos[5] <= 5050);  // then after 5 s
    CHECK(hellos.size() >= 2 && hellos[hellos.size() - 1] - hellos[hellos.size() - 2] >= 59950 &&
          hellos[hellos.size() - 1] - hellos[hellos.size() - 2] <= 60050);  // capped at a minute
    CHECK(p.receiver->info().phase == Receiver::Phase::Hello);
    // the server answers again: the link comes up within a minute
    answer = true;
    w.net.step(1240);
    Server::LinkInfo li;
    CHECK(w.srv.server->link_info(0x81, &li) && li.welcomed);
    CHECK(p.receiver->info().phase == Receiver::Phase::Linked);
    // a new connection starts over at once, a second apart
    w.net.leave(c);
    answer = false;
    hellos.clear();
    w.net.join(&p, 0x81, 101);
    w.net.step(30);
    CHECK(hellos.size() >= 2 && hellos[1] - hellos[0] <= 1050);
    kmk_test::end("link.hello_backs_off_while_the_server_does_not_answer");
}

int main(int argc, char** argv) {
    g_verbose = argc > 1 && std::strcmp(argv[1], "-v") == 0;
    g_digest_hex = synthetic_catalog()->digest_hex();
    g_pair.catalog_sha256 = g_digest_hex.c_str();
    handshake_and_catalog();
    wrong_catalog();
    m09_layers();
    updates_in_place();
    m10_partial_and_retry();
    m11_loss_and_resync();
    m11_bad_messages();
    receiver_limits();
    hosted_loopback();
    follow_poses();
    follow_references();
    received_text_budget();
    inbox_flood();
    fuzz_wire_and_roles();
    respawn_keeps_target_layer();
    limits();
    error_rule();
    native_tooltip_per_player();
    client_world_stop();
    bridge_rules();
    hello_backs_off();
    return kmk_test::finish("link_test");
}
