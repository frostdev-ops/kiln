// SPDX-License-Identifier: MIT
// kiln.markers' logical store with a fake world clock (the test starts and stops worlds and runs each client step's
// dispatch itself): chapter 04 §6 M01 (local lifecycle), M02 (limits and ownership), M14 logical half (no visibility
// before the admission slice), plus the publisher rules, the error rule, world stop and the subscription rules.
#include <atomic>
#include <cmath>
#include <functional>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "check.h"
#include "store.h"
#include "text.h"

using namespace kmk;

namespace {

// "%08d" of a small counter into the first eight bytes of a 128-byte label (the rest stays 'x')
void stamp(std::string& text, int n) {
    char d[16];
    std::snprintf(d, sizeof d, "%08d", n % 100000000);
    std::memcpy(text.data(), d, 8);
}

struct Seen {
    uint32_t kind, cause, affected, audience;
    uint64_t sequence, publisher_generation, world_epoch;
    std::string owner, key;
};

struct Spec {
    KilnMarkerSpec s{};
    std::string key, text;
    explicit Spec(std::string k, std::string t = "Raid entrance", uint32_t surfaces = KILN_MARKER_COMPASS | KILN_MARKER_WORLD)
        : key(std::move(k)), text(std::move(t)) {
        s.size = sizeof s;
        s.surfaces = surfaces;
        s.position = {3670, 820, 1500};
        s.rgba = 0xffffffffu;
        sync();
    }
    void sync() {
        s.key = KilnStr{key.data(), key.size()};
        s.text = KilnStr{text.data(), text.size()};
    }
    const KilnMarkerSpec* operator&() {
        sync();
        return &s;
    }
};

struct H {
    Arena arena;
    Switches sw;
    Store store{arena, sw};
    Subscribers subs;
    std::vector<Seen> seen;
    KilnHandle handle = 0;
    std::function<void(const KilnMarkerChange&)> also;  // a callback's extra work (reentrancy)

    explicit H(bool world = true) {
        sw.local = true;
        sw.compass = true;
        sw.world = true;
        if (world) store.world_started(KILN_WORLD_CLIENT);
        CHECK_EQ(subs.subscribe(900, &H::on_change, nullptr, nullptr, this, &handle), KILN_OK);
    }
    static void KILN_CALL on_change(const KilnMarkerChange* c, void* user) {
        H* h = static_cast<H*>(user);
        h->seen.push_back({c->kind, c->cause, c->affected, c->audience, c->sequence, c->publisher_generation, c->world_epoch, c->owner, c->key});
        if (h->also) h->also(*c);
    }
    void post() {
        subs.dispatch([this] { return store.take(kRoleClient); });
    }
    KilnResult set(KilnOwner o, Spec& sp, KilnMarkerResult* r) { return store.set_local(o, &sp, r); }
    KilnMarkerResult set(KilnOwner o, Spec& sp) {
        KilnMarkerResult r{};
        r.size = sizeof r;
        CHECK_EQ(store.set_local(o, &sp, &r), KILN_OK);
        return r;
    }
    KilnMarkerResult remove(KilnOwner o, const std::string& key) {
        KilnMarkerResult r{};
        r.size = sizeof r;
        CHECK_EQ(store.remove_local(o, KilnStr{key.data(), key.size()}, &r), KILN_OK);
        return r;
    }
    KilnMarkerResult clear(KilnOwner o) {
        KilnMarkerResult r{};
        r.size = sizeof r;
        CHECK_EQ(store.clear_local(o, &r), KILN_OK);
        return r;
    }
    KilnResult bind(KilnOwner o, const std::string& ns) { return store.bind(o, KilnStr{ns.data(), ns.size()}); }
    size_t count(uint32_t kind) const {
        size_t n = 0;
        for (const Seen& s : seen) n += s.kind == kind;
        return n;
    }
};

bool reason_starts(const KilnMarkerResult& r, const char* prefix) { return std::strncmp(r.reason, prefix, std::strlen(prefix)) == 0; }

// ---- M01 ----------------------------------------------------------------------------------------------------------------

void m01_local_lifecycle() {
    kmk_test::begin();
    H h;
    CHECK_EQ(h.bind(1, "boss_mod"), KILN_OK);
    Spec a("gate"), b("gate", "Raid entrance (open)");
    KilnMarkerResult r = h.set(1, a);
    CHECK_EQ(r.status, KILN_MARKER_ACCEPTED);
    CHECK_EQ(r.committed, 1u);
    CHECK_EQ(r.affected, 1u);
    h.post();
    r = h.set(1, a);  // the same value again: accepted, nothing changed, no event
    CHECK_EQ(r.status, KILN_MARKER_ACCEPTED);
    CHECK_EQ(r.committed, 1u);
    CHECK_EQ(r.affected, 0u);
    h.post();
    r = h.set(1, b);
    CHECK_EQ(r.status, KILN_MARKER_ACCEPTED);
    CHECK_EQ(r.affected, 1u);
    h.post();
    r = h.remove(1, "gate");
    CHECK_EQ(r.status, KILN_MARKER_ACCEPTED);
    CHECK_EQ(r.affected, 1u);
    h.post();
    r = h.remove(1, "gate");
    CHECK_EQ(r.status, KILN_MARKER_NOT_FOUND);
    CHECK_EQ(r.committed, 0u);
    CHECK(reason_starts(r, "NotFound"));
    h.post();
    r = h.clear(1);  // empty scope: idempotent, no event
    CHECK_EQ(r.status, KILN_MARKER_ACCEPTED);
    CHECK_EQ(r.affected, 0u);
    h.post();
    CHECK_EQ(h.seen.size(), 3u);
    CHECK_EQ(h.count(KILN_MARKER_ADDED), 1u);
    CHECK_EQ(h.count(KILN_MARKER_UPDATED), 1u);
    CHECK_EQ(h.count(KILN_MARKER_REMOVED), 1u);
    if (h.seen.size() == 3) {
        CHECK_EQ(h.seen[0].kind, KILN_MARKER_ADDED);
        CHECK_EQ(h.seen[1].kind, KILN_MARKER_UPDATED);
        CHECK_EQ(h.seen[2].kind, KILN_MARKER_REMOVED);
        CHECK(h.seen[0].owner == "boss_mod" && h.seen[0].key == "gate");
        CHECK(h.seen[0].sequence < h.seen[1].sequence && h.seen[1].sequence < h.seen[2].sequence);
        CHECK_EQ(h.seen[0].audience, KILN_MARKER_LOCAL);
        CHECK_EQ(h.seen[0].cause, KILN_MARKER_API);
        uint64_t epoch = 0;
        h.store.world_epoch(KILN_WORLD_CLIENT, &epoch);
        CHECK(epoch != 0 && h.seen[0].world_epoch == epoch);
        CHECK(h.seen[0].publisher_generation != 0);
    }
    CHECK_EQ(h.store.local_count(), 0u);
    kmk_test::end("M01 local lifecycle");
}

// ---- M02 ----------------------------------------------------------------------------------------------------------------

void m02_limits_and_ownership() {
    kmk_test::begin();
    H h;
    const std::string ns[5] = {"ns.a", "ns.b", "ns.c", "ns.d", "ns.e"};
    for (int i = 0; i < 5; ++i) CHECK_EQ(h.bind(10 + i, ns[i]), KILN_OK);
    // fill one namespace to 128 (a dispatch every 50: at most 64 changes of one publisher wait for one)
    for (int k = 0; k < 128; ++k) {
        Spec s("k" + std::to_string(k), "same label");
        CHECK_EQ(h.set(10, s).status, KILN_MARKER_ACCEPTED);
        if (k % 50 == 49) h.post();
    }
    h.post();
    const size_t events = h.seen.size();
    const uint32_t count = h.store.local_count();
    Spec over("k128", "same label");
    KilnMarkerResult r = h.set(10, over);
    CHECK_EQ(r.status, KILN_MARKER_FULL);
    CHECK_EQ(r.committed, 0u);
    CHECK(reason_starts(r, "Full"));
    h.post();
    CHECK_EQ(h.seen.size(), events);  // no event
    CHECK_EQ(h.store.local_count(), count);
    // an update of an existing key still goes through at the namespace's limit
    Spec upd("k5", "other label");
    CHECK_EQ(h.set(10, upd).status, KILN_MARKER_ACCEPTED);
    h.post();
    // the process's 512 local markers with four namespaces, then the next one is refused
    for (int i = 1; i < 4; ++i)
        for (int k = 0; k < 128; ++k) {
            Spec s("k" + std::to_string(k), "same label");
            CHECK_EQ(h.set(10 + i, s).status, KILN_MARKER_ACCEPTED);
            if (k % 50 == 49) h.post();
        }
    h.post();
    CHECK_EQ(h.store.local_count(), 512u);
    Spec fifth("k0", "same label");
    r = h.set(14, fifth);
    CHECK_EQ(r.status, KILN_MARKER_FULL);
    CHECK(std::strstr(r.reason, "512") != nullptr);
    // two publishers, the same key: separate records
    Spec mine("k0", "a"), theirs("k0", "b");
    CHECK_EQ(h.set(10, mine).affected, 1u);
    CHECK_EQ(h.set(11, theirs).affected, 1u);
    h.post();
    CHECK_EQ(h.set(10, mine).affected, 0u);  // 10's record is still "a": the other owner did not overwrite it
    // removing one record frees one live slot; nothing was evicted to make room
    CHECK_EQ(h.remove(12, "k7").status, KILN_MARKER_ACCEPTED);
    h.post();
    CHECK_EQ(h.set(14, fifth).status, KILN_MARKER_ACCEPTED);
    h.post();
    Spec again("k8", "same label");
    CHECK_EQ(h.set(12, again).affected, 0u);  // its neighbour is still there, unchanged
    CHECK_EQ(h.set(14, over).status, KILN_MARKER_FULL);
    // interned text: the same label again costs nothing; removal gives nothing back
    const uint64_t before = h.store.text_charged("ns.a");
    CHECK(before == std::strlen("same label") + std::strlen("other label") + std::strlen("a"));
    CHECK_EQ(h.remove(10, "k1").status, KILN_MARKER_ACCEPTED);
    CHECK_EQ(h.store.text_charged("ns.a"), before);
    h.post();
    kmk_test::end("M02 limits and ownership");
}

// The text budget of one namespace: 256 KiB of new text, then FULL before the record changes.
void text_budget() {
    kmk_test::begin();
    H h;
    CHECK_EQ(h.bind(1, "chatty"), KILN_OK);
    std::string text(128, 'x');
    int accepted = 0;
    KilnMarkerResult last{};
    for (int i = 0; i < 3000; ++i) {
        stamp(text, i);
        Spec s("k", text);
        last = h.set(1, s);
        if (last.status != KILN_MARKER_ACCEPTED) break;
        ++accepted;
        h.post();
    }
    CHECK_EQ(accepted, static_cast<int>(Arena::kNamespaceBytes / 128));
    CHECK_EQ(last.status, KILN_MARKER_FULL);
    CHECK(std::strstr(last.reason, "256 KiB") != nullptr);
    // the record is still the last accepted text (no partial change), and a known label still works
    stamp(text, accepted - 1);
    Spec same("k", text);
    CHECK_EQ(h.set(1, same).affected, 0u);
    Spec old("k", std::string("00000000") + std::string(120, 'x'));
    CHECK_EQ(h.set(1, old).status, KILN_MARKER_ACCEPTED);
    // releasing and binding the namespace again does not reset its count
    CHECK_EQ(h.store.release(1), KILN_OK);
    CHECK_EQ(h.bind(2, "chatty"), KILN_OK);
    stamp(text, 99999);
    Spec fresh("k", text);
    CHECK_EQ(h.set(2, fresh).status, KILN_MARKER_FULL);
    h.post();
    kmk_test::end("text budget per namespace (256 KiB, kept after release)");
}

// ---- M14, the logical half ----------------------------------------------------------------------------------------------

void m14_logical() {
    kmk_test::begin();
    H h;
    CHECK_EQ(h.bind(1, "m14"), KILN_OK);
    Spec a("brief");
    CHECK_EQ(h.set(1, a).status, KILN_MARKER_ACCEPTED);
    CHECK_EQ(h.remove(1, "brief").status, KILN_MARKER_ACCEPTED);
    h.post();  // both, in order, in the first dispatch
    CHECK_EQ(h.seen.size(), 2u);
    if (h.seen.size() == 2) {
        CHECK_EQ(h.seen[0].kind, KILN_MARKER_ADDED);
        CHECK_EQ(h.seen[1].kind, KILN_MARKER_REMOVED);
    }
    // a clear of several records: one CLEARED with their count
    for (int k = 0; k < 5; ++k) {
        Spec s("c" + std::to_string(k));
        h.set(1, s);
    }
    h.post();
    h.seen.clear();
    KilnMarkerResult r = h.clear(1);
    CHECK_EQ(r.affected, 5u);
    h.post();
    CHECK_EQ(h.seen.size(), 1u);
    if (!h.seen.empty()) {
        CHECK_EQ(h.seen[0].kind, KILN_MARKER_CLEARED);
        CHECK_EQ(h.seen[0].affected, 5u);
        CHECK(h.seen[0].key.empty());
    }
    // a callback that writes: no deadlock, and its change arrives in the next dispatch, not this one
    h.seen.clear();
    int writes = 0;
    h.also = [&](const KilnMarkerChange& c) {
        if (c.kind != KILN_MARKER_ADDED || std::strcmp(c.key, "first") != 0) return;
        Spec s("second");
        KilnMarkerResult rr{};
        rr.size = sizeof rr;
        h.store.set_local(1, &s, &rr);
        writes += rr.status == KILN_MARKER_ACCEPTED;
    };
    Spec first("first");
    h.set(1, first);
    h.post();
    CHECK_EQ(writes, 1);
    CHECK_EQ(h.seen.size(), 1u);  // "second" waits for the next dispatch
    h.post();
    CHECK_EQ(h.seen.size(), 2u);
    if (h.seen.size() == 2) CHECK(h.seen[1].key == "second");
    h.also = nullptr;
    kmk_test::end("M14 logical half (set+remove before a step, one clear summary, writes from a callback)");
}

// ---- the queue limits ---------------------------------------------------------------------------------------------------

void pending_limits() {
    kmk_test::begin();
    H h;
    for (int i = 0; i < 6; ++i) CHECK_EQ(h.bind(20 + i, "pub" + std::to_string(i)), KILN_OK);
    for (int k = 0; k < 64; ++k) {
        Spec s("p" + std::to_string(k));
        CHECK_EQ(h.set(20, s).status, KILN_MARKER_ACCEPTED);
    }
    Spec next("p64");
    KilnMarkerResult r = h.set(20, next);
    CHECK_EQ(r.status, KILN_MARKER_FULL);
    CHECK(std::strstr(r.reason, "next client step") != nullptr);
    CHECK_EQ(h.remove(20, "p0").status, KILN_MARKER_FULL);  // a removal waits for a slot too
    CHECK_EQ(h.clear(20).status, KILN_MARKER_FULL);
    h.post();
    CHECK_EQ(h.store.pending(kRoleClient), 0u);
    CHECK_EQ(h.set(20, next).status, KILN_MARKER_ACCEPTED);  // slots back after the dispatch
    h.post();
    // 256 for all publishers together
    int ok = 0;
    for (int i = 0; i < 5; ++i)
        for (int k = 0; k < 60; ++k) {
            Spec s("q" + std::to_string(k));
            ok += h.set(21 + (i % 4), s).status == KILN_MARKER_ACCEPTED && i < 4;
        }
    CHECK_EQ(h.store.pending(kRoleClient), 240u);  // 4 x 60; the fifth round was updates of the same keys: identical
    for (int k = 0; k < 16; ++k) {
        Spec s("r" + std::to_string(k));
        CHECK_EQ(h.set(25, s).status, KILN_MARKER_ACCEPTED);
    }
    CHECK_EQ(h.store.pending(kRoleClient), 256u);
    Spec more("r16");
    r = h.set(25, more);  // pub5 has 16 waiting, under its own 64: the process's 256 refuse it
    CHECK_EQ(r.status, KILN_MARKER_FULL);
    CHECK(std::strstr(r.reason, "256 marker changes") != nullptr);
    h.post();
    CHECK_EQ(h.store.pending(kRoleClient), 0u);
    (void)ok;
    kmk_test::end("pending changes: 64 per publisher, 256 in all, freed by the dispatch");
}

// ---- world lifecycle ----------------------------------------------------------------------------------------------------

void world_stop_and_epochs() {
    kmk_test::begin();
    H h(false);
    CHECK_EQ(h.bind(1, "one"), KILN_OK);
    CHECK_EQ(h.bind(2, "two"), KILN_OK);
    CHECK_EQ(h.bind(3, "three"), KILN_OK);
    uint64_t e = 7;
    CHECK_EQ(h.store.world_epoch(KILN_WORLD_CLIENT, &e), KILN_OK);
    CHECK_EQ(e, 0u);
    Spec s("x");
    KilnMarkerResult r = h.set(1, s);  // no client world: refused, nothing deferred
    CHECK_EQ(r.status, KILN_MARKER_NOT_READY);
    CHECK_EQ(r.committed, 0u);
    CHECK_EQ(h.remove(1, "x").status, KILN_MARKER_NOT_READY);
    CHECK_EQ(h.clear(1).status, KILN_MARKER_NOT_READY);
    h.store.world_started(KILN_WORLD_CLIENT);
    uint64_t first = 0;
    h.store.world_epoch(KILN_WORLD_CLIENT, &first);
    CHECK(first != 0);
    h.store.world_started(KILN_WORLD_SERVER);
    uint64_t server = 0;
    h.store.world_epoch(KILN_WORLD_SERVER, &server);
    CHECK(server != 0 && server != first);
    CHECK_EQ(h.store.world_epoch(KILN_WORLD_PREDICTION, &e), KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(h.store.world_epoch(3, &e), KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(h.store.world_epoch(KILN_WORLD_CLIENT, nullptr), KILN_ERR_INVALID_ARGUMENT);
    Spec a("a"), b("b"), c("c");
    h.set(1, a);
    h.set(1, b);
    h.set(2, c);
    h.post();
    h.seen.clear();
    h.store.world_stopping(KILN_WORLD_CLIENT);
    h.post();  // the glue delivers on the stopping thread before the stop returns
    CHECK_EQ(h.seen.size(), 2u);  // one CLEARED per nonempty scope; "three" had nothing
    for (const Seen& x : h.seen) {
        CHECK_EQ(x.kind, KILN_MARKER_CLEARED);
        CHECK_EQ(x.cause, KILN_MARKER_WORLD_STOPPED);
        CHECK_EQ(x.world_epoch, first);
    }
    if (h.seen.size() == 2) CHECK(h.seen[0].affected + h.seen[1].affected == 3);
    CHECK_EQ(h.store.local_count(), 0u);
    h.store.world_epoch(KILN_WORLD_CLIENT, &e);
    CHECK_EQ(e, 0u);
    CHECK_EQ(h.set(1, a).status, KILN_MARKER_NOT_READY);
    // a new world: a new epoch, never an old one; the store starts empty (session only)
    h.store.world_started(KILN_WORLD_CLIENT);
    uint64_t second = 0;
    h.store.world_epoch(KILN_WORLD_CLIENT, &second);
    CHECK(second > server);
    CHECK_EQ(h.set(1, a).affected, 1u);
    // a start without a stop ends the old world's records too
    h.post();
    h.seen.clear();
    h.store.world_started(KILN_WORLD_CLIENT);
    h.post();
    CHECK_EQ(h.seen.size(), 1u);
    if (!h.seen.empty()) CHECK_EQ(h.seen[0].cause, KILN_MARKER_WORLD_STOPPED);
    kmk_test::end("world stop: one CLEARED per scope, then nothing until a new epoch");
}

// ---- publishers ---------------------------------------------------------------------------------------------------------

void publishers() {
    kmk_test::begin();
    H h;
    CHECK_EQ(h.bind(0, "x"), KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(h.bind(1, ""), KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(h.bind(1, "has space"), KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(h.bind(1, std::string(65, 'a')), KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(h.bind(1, std::string(64, 'a')), KILN_OK);
    CHECK_EQ(h.bind(1, std::string(64, 'a')), KILN_OK);  // again, same namespace
    CHECK_EQ(h.bind(1, "other"), KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(h.bind(2, "Mod.Name:v2-x_y"), KILN_OK);
    CHECK_EQ(h.bind(3, "Mod.Name:v2-x_y"), KILN_ERR_INVALID_ARGUMENT);  // a live binding holds it
    Spec s("k");
    KilnMarkerResult r{};
    r.size = sizeof r;
    CHECK_EQ(h.store.set_local(4, &s, &r), KILN_ERR_INVALID_ARGUMENT);  // never bound
    CHECK(std::strstr(r.reason, "not bound") != nullptr);
    CHECK_EQ(h.set(2, s).status, KILN_MARKER_ACCEPTED);
    h.post();
    const uint64_t gen = h.seen.back().publisher_generation;
    h.seen.clear();
    CHECK_EQ(h.store.release(2), KILN_OK);
    CHECK_EQ(h.store.release(2), KILN_OK);  // nothing left: still fine
    CHECK_EQ(h.store.release(0), KILN_ERR_INVALID_ARGUMENT);
    h.post();
    CHECK_EQ(h.seen.size(), 1u);
    if (!h.seen.empty()) {
        CHECK_EQ(h.seen[0].kind, KILN_MARKER_CLEARED);
        CHECK_EQ(h.seen[0].cause, KILN_MARKER_OWNER_RETIRED);
        CHECK_EQ(h.seen[0].affected, 1u);
    }
    CHECK_EQ(h.store.local_count(), 0u);
    CHECK_EQ(h.bind(3, "Mod.Name:v2-x_y"), KILN_OK);  // free again; a new generation
    CHECK_EQ(h.set(3, s).status, KILN_MARKER_ACCEPTED);
    h.post();
    CHECK(h.seen.back().publisher_generation > gen);
    // the ledger: 64 distinct namespaces in the process, a released one keeps its place
    H l;
    int ok = 0;
    for (int i = 0; i < 64; ++i) ok += l.bind(100 + i, "led" + std::to_string(i)) == KILN_OK;
    CHECK_EQ(ok, 64);
    CHECK_EQ(l.bind(300, "led64"), KILN_ERR_BUFFER_TOO_SMALL);
    CHECK_EQ(l.store.release(100), KILN_OK);
    CHECK_EQ(l.bind(300, "led64"), KILN_ERR_BUFFER_TOO_SMALL);  // the released namespace still counts
    CHECK_EQ(l.bind(300, "led0"), KILN_OK);                     // but it can be bound again
    kmk_test::end("publishers: namespaces, live bindings, generations, the 64-namespace ledger");
}

// ---- the error rule -----------------------------------------------------------------------------------------------------

KilnResult try_set(H& h, Spec& s, KilnMarkerResult* r) {
    std::memset(r, 0, sizeof *r);
    r->size = sizeof *r;
    return h.store.set_local(1, &s, r);
}

void validation() {
    kmk_test::begin();
    H h;
    CHECK_EQ(h.bind(1, "v"), KILN_OK);
    KilnMarkerResult r{};
    auto bad = [&](Spec& s, const char* needle) {
        const KilnResult rc = try_set(h, s, &r);
        CHECK_EQ(rc, KILN_ERR_INVALID_ARGUMENT);
        if (needle && !std::strstr(r.reason, needle)) {
            std::printf("  reason was: %s (wanted %s)\n", r.reason, needle);
            CHECK(false);
        }
    };
    { Spec s("k"); s.s.surfaces = 0; bad(s, "surfaces"); }
    { Spec s("k"); s.s.surfaces = 8; bad(s, "surfaces"); }
    { Spec s("bad key"); bad(s, "key"); }
    { Spec s(""); bad(s, "key"); }
    { Spec s(std::string(65, 'k')); bad(s, "key"); }
    { Spec s("k"); s.s.position.x = 12000.5; bad(s, "outside"); }
    { Spec s("k"); s.s.position.y = 5000.5; bad(s, "outside"); }
    { Spec s("k"); s.s.position.z = -1000.5; bad(s, "outside"); }
    { Spec s("k"); s.s.position.x = std::nan(""); bad(s, "finite"); }
    { Spec s("k"); s.s.height = 128.5f; bad(s, "height"); }
    { Spec s("k"); s.s.max_distance = -1; bad(s, "max_distance"); }
    { Spec s("k"); s.s.max_distance = 100000.5f; bad(s, "max_distance"); }
    { Spec s("k"); s.s.priority = 32768; bad(s, "priority"); }
    { Spec s("k"); s.s.priority = -32769; bad(s, "priority"); }
    { Spec s("k"); s.s.map_label_mode = 2; bad(s, "map_label_mode"); }
    { Spec s("k"); s.s.map_label_mode = 1; bad(s, "MAP"); }
    { Spec s("k", std::string(129, 'a')); bad(s, "128"); }
    { Spec s("k", "line\nbreak"); bad(s, "control"); }
    { Spec s("k", std::string("c1 \xc2\x85 next line")); bad(s, "control"); }
    { Spec s("k", std::string("\xc3\x28")); bad(s, "UTF-8"); }
    { Spec s("k", std::string("\xe0\x80\xaf")); bad(s, "overlong"); }
    { Spec s("k", std::string("\xed\xa0\x80")); bad(s, "code point"); }
    { Spec s("k", std::string("\xe2\x82")); bad(s, "cut"); }
    { Spec s("k"); s.s.icon_type = 5; bad(s, "icon_type must be 0"); }
    { Spec s("k"); s.s.size = sizeof(KilnMarkerSpec) - 8; bad(s, "size"); }
    // limits that are allowed
    { Spec s("k", std::string(128, 'a')); s.s.position = {12000, 5000, -1000}; s.s.height = -128; s.s.max_distance = 100000;
      s.s.priority = -32768; CHECK_EQ(try_set(h, s, &r), KILN_OK); CHECK_EQ(r.status, KILN_MARKER_ACCEPTED); }
    { Spec s("k2", "Zoë — 東京 🙂"); CHECK_EQ(try_set(h, s, &r), KILN_OK); CHECK_EQ(r.status, KILN_MARKER_ACCEPTED); }
    { Spec s("k3", ""); CHECK_EQ(try_set(h, s, &r), KILN_OK); CHECK_EQ(r.status, KILN_MARKER_ACCEPTED); }
    // the result itself
    { Spec s("k"); KilnMarkerResult small{}; small.size = sizeof small - 4; CHECK_EQ(h.store.set_local(1, &s, &small), KILN_ERR_INVALID_ARGUMENT);
      CHECK_EQ(h.store.set_local(1, &s, nullptr), KILN_ERR_INVALID_ARGUMENT); CHECK_EQ(h.store.set_local(1, nullptr, &r), KILN_ERR_INVALID_ARGUMENT); }
    // a larger caller struct is accepted
    { Spec s("k4"); s.s.size = sizeof(KilnMarkerSpec) + 16; CHECK_EQ(try_set(h, s, &r), KILN_OK); }
    h.post();
    kmk_test::end("schema: every bound and grammar of chapter 04 §4.5 is an invalid argument");
}

IconAnswer catalog_stub(uint32_t id, void* ctx) {
    const int state = *static_cast<int*>(ctx);
    if (state == 0) return IconAnswer::NotReady;
    if (id == 0xffe5e444u) return IconAnswer::Ok;
    if (id == 0x1095684eu) return IconAnswer::NotForMap;
    return IconAnswer::Unknown;
}

void capabilities_and_icons() {
    kmk_test::begin();
    H h;
    CHECK_EQ(h.bind(1, "caps"), KILN_OK);
    KilnMarkerResult r{};
    Spec map("m", "well", KILN_MARKER_MAP);
    map.s.icon_type = 0xffe5e444u;
    // the map row is off: unavailable, naming it
    CHECK_EQ(try_set(h, map, &r), KILN_ERR_UNAVAILABLE);
    CHECK(std::strcmp(r.reason, "markers.map") == 0);
    h.sw.map = true;
    int catalog = 0;
    h.store.set_icon_check(&catalog_stub, &catalog);
    CHECK_EQ(try_set(h, map, &r), KILN_OK);
    CHECK_EQ(r.status, KILN_MARKER_CATALOG_NOT_READY);
    CHECK_EQ(r.committed, 0u);
    catalog = 1;
    CHECK_EQ(try_set(h, map, &r), KILN_OK);
    CHECK_EQ(r.status, KILN_MARKER_ACCEPTED);
    Spec unknown("u", "x", KILN_MARKER_MAP);
    unknown.s.icon_type = 0x12345678u;
    CHECK_EQ(try_set(h, unknown, &r), KILN_ERR_INVALID_ARGUMENT);
    Spec travel("t", "x", KILN_MARKER_MAP);
    travel.s.icon_type = 0x1095684eu;
    CHECK_EQ(try_set(h, travel, &r), KILN_ERR_INVALID_ARGUMENT);
    CHECK(std::strstr(r.reason, "fast-travel") != nullptr);
    Spec nomapicon("n", "x", KILN_MARKER_MAP);
    CHECK_EQ(try_set(h, nomapicon, &r), KILN_ERR_INVALID_ARGUMENT);
    // compass and world rows; the native tooltip; anchors
    Spec both("b");
    h.sw.compass = false;
    CHECK_EQ(try_set(h, both, &r), KILN_ERR_UNAVAILABLE);
    CHECK(std::strcmp(r.reason, "markers.compass") == 0);
    h.sw.compass = true;
    h.sw.world = false;
    CHECK_EQ(try_set(h, both, &r), KILN_ERR_UNAVAILABLE);
    CHECK(std::strcmp(r.reason, "markers.world") == 0);
    h.sw.world = true;
    Spec tip("tip", "x", KILN_MARKER_MAP);
    tip.s.icon_type = 0xffe5e444u;
    tip.s.map_label_mode = KILN_MARKER_LABEL_NATIVE_TOOLTIP;
    CHECK_EQ(try_set(h, tip, &r), KILN_ERR_UNAVAILABLE);
    CHECK(std::strcmp(r.reason, "markers.map_text") == 0);
    Spec follow("f");
    follow.s.follow = 42;
    CHECK_EQ(try_set(h, follow, &r), KILN_ERR_UNAVAILABLE);
    CHECK(std::strcmp(r.reason, "markers.anchors.client") == 0);
    h.sw.local = false;
    CHECK_EQ(try_set(h, both, &r), KILN_ERR_UNAVAILABLE);
    CHECK(std::strcmp(r.reason, "markers.local") == 0);
    KilnMarkerResult rr{};
    rr.size = sizeof rr;
    CHECK_EQ(h.store.remove_local(1, KilnStr{"m", 1}, &rr), KILN_ERR_UNAVAILABLE);
    CHECK_EQ(h.store.clear_local(1, &rr), KILN_ERR_UNAVAILABLE);
    h.sw.local = true;
    // a refused request changed nothing: one record ("m")
    CHECK_EQ(h.store.local_count(), 1u);
    h.post();
    CHECK_EQ(h.seen.size(), 1u);
    kmk_test::end("capabilities name the missing row; icons by the catalog's answer (M13 store half)");
}

// ---- subscriptions ------------------------------------------------------------------------------------------------------

// issue 160: with markers.map_text on (surfaces.cpp: markers.map on and markers.map_tooltip_text resolved) a NativeTooltip
// map marker is stored with its label mode and its text in the arena, which the surfaces write into its map entry; off
// again, a new one is refused naming the row and the stored one stays as it is.
void native_tooltip_when_on() {
    kmk_test::begin();
    H h;
    CHECK_EQ(h.bind(1, "wp4"), KILN_OK);
    h.sw.map = true;
    int catalog = 1;
    h.store.set_icon_check(&catalog_stub, &catalog);
    h.sw.map_text = true;
    std::string text = "Golden chest 1";
    Spec tip("wp4.icon_chest", text, KILN_MARKER_MAP);
    tip.s.icon_type = 0xffe5e444u;
    tip.s.map_label_mode = KILN_MARKER_LABEL_NATIVE_TOOLTIP;
    KilnMarkerResult r{};
    CHECK_EQ(try_set(h, tip, &r), KILN_OK);
    CHECK(r.status == KILN_MARKER_ACCEPTED && r.affected == 1);
    std::vector<ClientRow> rows;
    h.store.snapshot_client(&rows);
    CHECK_EQ(rows.size(), size_t{1});
    if (!rows.empty()) {
        CHECK_EQ(rows[0].rec.label_mode, static_cast<uint32_t>(KILN_MARKER_LABEL_NATIVE_TOOLTIP));
        CHECK(rows[0].rec.text == text);
        CHECK(rows[0].rec.text.data() != tip.text.data());  // the arena's copy, not the caller's bytes
        const char* kept = rows[0].rec.text.data();
        tip.text.assign(tip.text.size(), 'x');  // the caller's buffer changes; the stored text does not
        h.store.snapshot_client(&rows);
        CHECK(rows[0].rec.text == text && rows[0].rec.text.data() == kept);
    }
    // the switch off again: a new NativeTooltip marker is refused naming the row, None still goes
    h.sw.map_text = false;
    Spec tip2("wp4.other", "Golden chest 2", KILN_MARKER_MAP);
    tip2.s.icon_type = 0xffe5e444u;
    tip2.s.map_label_mode = KILN_MARKER_LABEL_NATIVE_TOOLTIP;
    CHECK_EQ(try_set(h, tip2, &r), KILN_ERR_UNAVAILABLE);
    CHECK(std::strcmp(r.reason, "markers.map_text") == 0);
    tip2.s.map_label_mode = KILN_MARKER_LABEL_NONE;
    CHECK_EQ(try_set(h, tip2, &r), KILN_OK);
    CHECK_EQ(h.store.local_count(), 2u);
    h.post();
    kmk_test::end("issue 160: NativeTooltip is stored with its arena text once markers.map_text is on; refused naming the row when off");
}

struct Counter {
    int calls = 0;
    bool fault = false;
    Subscribers* subs = nullptr;
    KilnHandle self = 0;
    bool unsubscribe_inside = false;
    KilnResult inside_rc = -1;
};

void KILN_CALL count_change(const KilnMarkerChange*, void* user) {
    Counter* c = static_cast<Counter*>(user);
    ++c->calls;
    if (c->unsubscribe_inside) c->inside_rc = c->subs->unsubscribe(c->self);
    if (c->fault) throw 1;  // the host build's stand-in for a fault in a native callback
}

void KILN_CALL count_catalog(const KilnMarkerCatalogChange*, void* user) { ++static_cast<Counter*>(user)->calls; }

void subscriptions() {
    kmk_test::begin();
    H h;
    CHECK_EQ(h.bind(1, "subs"), KILN_OK);
    Counter faulty, healthy, leaver, catalog_only;
    faulty.fault = true;
    leaver.unsubscribe_inside = true;
    leaver.subs = &h.subs;
    KilnHandle hf = 0, hh = 0, hc = 0;
    CHECK_EQ(h.subs.subscribe(0, &count_change, nullptr, nullptr, nullptr, &hf), KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(h.subs.subscribe(5, nullptr, nullptr, nullptr, nullptr, &hf), KILN_ERR_INVALID_ARGUMENT);
    CHECK_EQ(h.subs.subscribe(5, &count_change, nullptr, nullptr, &faulty, &hf), KILN_OK);
    CHECK_EQ(h.subs.subscribe(6, &count_change, nullptr, nullptr, &healthy, &hh), KILN_OK);
    CHECK_EQ(h.subs.subscribe(7, &count_change, nullptr, nullptr, &leaver, &leaver.self), KILN_OK);
    CHECK_EQ(h.subs.subscribe(8, nullptr, nullptr, &count_catalog, &catalog_only, &hc), KILN_OK);
    Spec a("a"), b("b");
    h.set(1, a);
    h.set(1, b);
    h.post();
    CHECK_EQ(faulty.calls, 1);   // faulted on the first note: gone, the second note skips it
    CHECK_EQ(healthy.calls, 2);  // unaffected
    CHECK_EQ(leaver.calls, 1);   // unsubscribed itself inside the first call, which returned at once
    CHECK_EQ(leaver.inside_rc, KILN_OK);
    CHECK_EQ(catalog_only.calls, 0);
    CHECK_EQ(h.subs.unsubscribe(hf), KILN_ERR_NOT_FOUND);
    CHECK_EQ(h.subs.unsubscribe(leaver.self), KILN_ERR_NOT_FOUND);
    KilnMarkerCatalogChange cc{};
    cc.ready = 1;
    cc.count = 43;
    h.store.post_catalog(kRoleClient, cc);
    h.post();
    CHECK_EQ(catalog_only.calls, 1);
    CHECK_EQ(healthy.calls, 2);  // no change callback for a catalog note
    h.subs.remove_owner(6);
    h.set(1, a);  // identical: nothing
    Spec c2("c");
    h.set(1, c2);
    h.post();
    CHECK_EQ(healthy.calls, 2);
    CHECK_EQ(h.subs.unsubscribe(hh), KILN_ERR_NOT_FOUND);
    kmk_test::end("subscriptions: a faulting one goes alone, unsubscribe from inside returns, remove_owner");
}

// Several threads write while the client step dispatches: every accepted change arrives once, in sequence order.
void threads() {
    kmk_test::begin();
    H h;
    for (int i = 0; i < 4; ++i) CHECK_EQ(h.bind(40 + i, "t" + std::to_string(i)), KILN_OK);
    std::atomic<int> accepted{0};
    std::atomic<bool> stop{false};
    std::vector<std::thread> writers;
    for (int i = 0; i < 4; ++i)
        writers.emplace_back([&, i] {
            for (int k = 0; k < 2000; ++k) {
                Spec s("w" + std::to_string(k % 100), "v" + std::to_string(k));
                KilnMarkerResult r{};
                r.size = sizeof r;
                if (h.store.set_local(40 + i, &s, &r) == KILN_OK && r.status == KILN_MARKER_ACCEPTED && r.affected) ++accepted;
            }
        });
    std::thread poster([&] {
        while (!stop.load()) h.post();
    });
    for (auto& w : writers) w.join();
    stop = true;
    poster.join();
    h.post();
    CHECK_EQ(static_cast<int>(h.seen.size()), accepted.load());
    bool ordered = true;
    for (size_t i = 1; i < h.seen.size(); ++i) ordered &= h.seen[i].sequence > h.seen[i - 1].sequence;
    CHECK(ordered);
    CHECK_EQ(h.store.pending(kRoleClient), 0u);
    kmk_test::end("threads: writers on four threads, a dispatching thread, every accepted change once and in order");
}

}  // namespace

int main() {
    m01_local_lifecycle();
    m02_limits_and_ownership();
    text_budget();
    m14_logical();
    pending_limits();
    world_stop_and_epochs();
    publishers();
    validation();
    capabilities_and_icons();
    native_tooltip_when_on();
    subscriptions();
    threads();
    return kmk_test::finish("store_test");
}
