// SPDX-License-Identifier: MIT
// kiln.assets' store on the host: the real store.cpp and util.cpp, with the game faked at the calls hooks.cpp makes for them
// (game_handle, game_data, ...). Checks KILN_ASSET_PUT_PIN and the player graph's reservation (kiln_assets.h). No game, no Wine.
//   test_store            every case, in order (the store is one process-wide state: each case starts where the last one ended)
#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <string>
#include <thread>
#include <tuple>
#include <vector>

#include "assets.h"
#include "services/kiln_hooks.h"

// ---- the game side, faked at the narrowest seam: the calls of the game's own functions and the feature switches --------------------
namespace ka {
std::atomic<uintptr_t> g_S{0};
namespace {
struct GameRes {
    uint32_t handle;
    std::string bytes;
};
std::unordered_map<Key, GameRes, KeyHash> g_game;
bool g_fake_ui = false;
}  // namespace
bool g_fake_content = true;
// the game's content index as game_index_keys (hooks.cpp) answers for it
GameIndex g_fake_index = GameIndex::not_ready;
std::vector<HashK> g_fake_keys;
uint64_t g_fake_total = 0;  // > 0: the index's count when it is "too_many"
std::function<void()> g_index_copy;
std::function<void()> g_catalog_read_copy;
void catalog_read_acquired() { if (g_catalog_read_copy) g_catalog_read_copy(); }
// store.cpp's cleanup thread for revoked owners: held here while g_hold_cleanup is set (to see a store call do the revert)
std::atomic<bool> g_hold_cleanup{false};
std::atomic<int> g_cleanup_held{0};
void cleanup_worker_started() {
    if (!g_hold_cleanup.load()) return;
    ++g_cleanup_held;
    while (g_hold_cleanup.load()) std::this_thread::yield();
    --g_cleanup_held;
}
KilnOwnerRetiringFn g_owner_retiring = nullptr;
void* g_owner_retiring_user = nullptr;
KilnResult KILN_CALL observe_owner(KilnOwner, KilnOwnerRetiringFn fn, void* user, KilnHandle* handle) {
    g_owner_retiring = fn;
    g_owner_retiring_user = user;
    *handle = 1;
    return KILN_OK;
}
const void* KILN_CALL fake_service(KilnStr name, uint32_t) {
    static const KilnHooksApi hooks = [] {
        KilnHooksApi t{};
        t.header = {sizeof t, KILN_HOOKS_VERSION};
        t.subscribe_owner_retiring = &observe_owner;
        return t;
    }();
    return std::string_view(name.ptr, name.len) == KILN_HOOKS_SERVICE ? &hooks : nullptr;
}
bool resources_on() { return true; }
bool content_hooks_on() { return g_fake_content; }
GameIndex game_index_keys(std::vector<HashK>* out, size_t max_keys, uint64_t* total, std::string* why) {
    if (g_index_copy) g_index_copy();
    out->clear();
    *total = g_fake_total ? g_fake_total : g_fake_keys.size();
    *why = "fake";
    if (g_fake_index != GameIndex::ok) return g_fake_index;
    if (*total > max_keys) return GameIndex::too_many;
    *out = g_fake_keys;
    return GameIndex::ok;
}
bool content_on() { return false; }
bool ui_on() { return g_fake_ui; }
uint32_t game_handle(uintptr_t S, const Key& k) {  // as hooks.cpp: nothing before the resource system is up
    if (!S) return 0;
    const auto it = g_game.find(k);
    return it == g_game.end() ? 0 : it->second.handle;
}
bool game_data(uintptr_t S, uint32_t handle, DataSpan* out) {
    if (!S) return false;
    for (const auto& [k, r] : g_game)
        if (r.handle == handle) {
            *out = {reinterpret_cast<const uint8_t*>(r.bytes.data()), r.bytes.size(), 0};
            return true;
        }
    return false;
}
bool game_type_list(uintptr_t, uint32_t, const uint8_t** keys, uint64_t* count) {
    *keys = nullptr;
    *count = 0;
    return false;
}
bool game_blob_find(const HashK&, GameBlob*) { return false; }
std::string game_container_path(uint16_t) { return {}; }
void flush_pending_textures() {}
Rgba resize(const Rgba& s, uint32_t, uint32_t) { return s; }
}  // namespace ka

namespace {
using namespace ka;

std::vector<std::string> g_log;
void KILN_CALL log_line(KilnOwner, int32_t, KilnStr s) { g_log.emplace_back(s.ptr, s.len); }

int g_pass = 0, g_fail = 0;
void check(bool ok, const std::string& what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what.c_str());
    ++(ok ? g_pass : g_fail);
}

Key key(const char* guid, uint32_t type, uint32_t part = 0) {
    Key k{};
    if (!parse_guid(guid, k.g)) std::abort();
    k.type = type;
    k.part = part;
    return k;
}
KilnAssetKey ak(const Key& k) {
    KilnAssetKey a{};
    std::memcpy(a.guid, k.g, 16);
    a.type = k.type;
    a.part = k.part;
    return a;
}
KilnResult put(KilnOwner o, const Key& k, const std::string& bytes, uint32_t flags) {
    const KilnAssetKey a = ak(k);
    return op_resource_put(o, &a, bytes.data(), bytes.size(), flags);
}
KilnResult remove(KilnOwner o, const Key& k) {
    const KilnAssetKey a = ak(k);
    return op_resource_remove(o, &a);
}
std::string read(const Key& k) {  // what resource_read gives: an item's bytes, else the game's own
    const KilnAssetKey a = ak(k);
    std::vector<uint8_t> b(4096);
    size_t n = 0;
    const KilnResult r = op_resource_read(&a, b.data(), b.size(), &n);
    return r == KILN_OK ? std::string(b.begin(), b.begin() + n) : "<result " + std::to_string(r) + ">";
}
std::string by_handle(uint32_t h) {  // what the game's ResourceData hook serves for its handle
    ReadGuard g;
    const ResView* v = g_res.load();
    const Served* sv = v ? v->handle(h) : nullptr;
    return sv ? std::string(reinterpret_cast<const char*>(sv->data), sv->size) : "<not served>";
}
size_t items() {  // kiln.assets' item count, as its status reports it
    const std::string s = store_status_json();
    return std::strtoul(s.c_str() + s.find("\"resources\":") + 12, nullptr, 10);
}
bool logged(size_t from, const std::string& a, const std::string& b) {
    for (size_t i = from; i < g_log.size(); ++i)
        if (g_log[i].find(a) != std::string::npos && g_log[i].find(b) != std::string::npos) return true;
    return false;
}
std::string flag_name(uint32_t f) {
    if (!f) return "no flag";
    std::string s;
    if (f & KILN_ASSET_PUT_NEW) s += "|new";
    if (f & KILN_ASSET_PUT_OVERRIDE) s += "|override";
    if (f & KILN_ASSET_PUT_PIN) s += "|pin";
    return s.substr(1);
}

KilnContentHash kch(const HashK& h) { return KilnContentHash{h.size, h.h0, h.h1, h.h2}; }
std::string blob(const HashK& h) {  // what blob_read gives for a hash Kiln serves
    const KilnContentHash c = kch(h);
    std::vector<uint8_t> b(64);
    size_t n = 0;
    const KilnResult r = op_blob_read(&c, b.data(), b.size(), &n);
    return r == KILN_OK ? std::string(b.begin(), b.begin() + n) : "<result " + std::to_string(r) + ">";
}
std::vector<LedgerEntry> ledger_of(const HashK& h) {
    std::vector<LedgerEntry> r;
    for (const LedgerEntry& e : blob_ledger(nullptr))
        if (e.hash == h) r.push_back(e);
    return r;
}
// The winner as the store decided it before provenance: the last registration of the hash in registration order, where an
// owner's later registration of the same hash replaces its earlier one in place. Kept here as the reference the store must match.
struct Reference {
    struct Reg { KilnOwner owner; std::string bytes; };
    std::vector<Reg> regs;
    void add(KilnOwner o, const std::string& b) {
        for (auto& r : regs) if (r.owner == o) { r.bytes = b; return; }
        regs.push_back({o, b});
    }
    void remove(KilnOwner o) { std::erase_if(regs, [&](const Reg& r) { return r.owner == o; }); }
    std::string winner() const { return regs.empty() ? "<result 2>" : regs.back().bytes; }
};

void blob_provenance(KilnOwner manifest, KilnOwner mod, KilnOwner native) {
    const HashK game{8, 0x11111111, 0x22222222, 0x33333333};  // a game blob three registrations override
    const KilnContentHash gc = kch(game);
    Reference ref;
    uint64_t gen = catalog_generation();
    {
        OriginScope scope(KILN_BLOB_ORIGIN_MANIFEST);  // as load_manifest
        check(op_blob_override(manifest, &gc, "MANIFEST", 8) == KILN_OK, "a manifest override");
    }
    ref.add(manifest, "MANIFEST");
    check(catalog_generation() > gen, "the catalog generation moves on a registration");
    check(op_blob_override_ex(mod, &gc, "MANAGED!", 8, KILN_BLOB_ORIGIN_MANAGED) == KILN_OK, "a managed override (blob_override_ex)");
    ref.add(mod, "MANAGED!");
    check(op_blob_override(native, &gc, "NATIVE!!", 8) == KILN_OK, "a native override (legacy blob_override)");
    ref.add(native, "NATIVE!!");
    auto l = ledger_of(game);
    check(l.size() == 3 && l[0].origin == KILN_BLOB_ORIGIN_MANIFEST && l[1].origin == KILN_BLOB_ORIGIN_MANAGED &&
              l[2].origin == KILN_BLOB_ORIGIN_NATIVE_API && l[0].owner == manifest && l[1].owner == mod && l[2].owner == native &&
              l[0].is_override && l[1].is_override && l[2].is_override && l[0].sequence < l[1].sequence && l[1].sequence < l[2].sequence,
          "three registrations of the hash, each with its owner, origin, rising sequence and override flag");
    check(blob(game) == ref.winner() && blob(game) == "NATIVE!!", "the last registration is served, as before");
    // an owner registers again: in place (its place in the order stays), new sequence and origin
    const uint64_t managed_seq = l[1].sequence;
    check(op_blob_override_ex(mod, &gc, "MANAGED2", 8, KILN_BLOB_ORIGIN_NATIVE_API) == KILN_OK, "the managed owner registers again");
    ref.add(mod, "MANAGED2");
    l = ledger_of(game);
    check(l.size() == 3 && l[1].owner == mod && l[1].origin == KILN_BLOB_ORIGIN_NATIVE_API && l[1].sequence > managed_seq,
          "replaced in place: same place, the new origin, a new sequence");
    check(blob(game) == ref.winner() && blob(game) == "NATIVE!!", "the winner is unchanged by an earlier owner's re-registration");
    gen = catalog_generation();
    check(op_blob_remove(native, &gc) == KILN_OK, "the winner removes its override");
    ref.remove(native);
    check(blob(game) == ref.winner() && blob(game) == "MANAGED2" && ledger_of(game).size() == 2 && catalog_generation() > gen,
          "the next one down is served; the removed registration left the ledger; the generation moved");
    gen = catalog_generation();
    check(op_blob_remove(native, &gc) == KILN_OK && catalog_generation() == gen, "removing nothing moves nothing");
    op_revert_owner(mod);
    ref.remove(mod);
    l = ledger_of(game);
    check(blob(game) == ref.winner() && blob(game) == "MANIFEST" && l.size() == 1 && l[0].origin == KILN_BLOB_ORIGIN_MANIFEST &&
              catalog_generation() > gen,
          "revert_owner takes the owner's registration: the manifest's is served, its origin kept");
    // the external slots never take MANIFEST, GAME or a combination: nothing is added
    const size_t before = blob_ledger(nullptr).size();
    KilnContentHash out{};
    for (uint32_t bad : {0u, (uint32_t)KILN_BLOB_ORIGIN_GAME, (uint32_t)KILN_BLOB_ORIGIN_MANIFEST,
                         (uint32_t)(KILN_BLOB_ORIGIN_MANAGED | KILN_BLOB_ORIGIN_NATIVE_API), 16u})
        check(op_blob_add_ex(mod, "X", 1, &out, bad) == KILN_ERR_INVALID_ARGUMENT &&
                  op_blob_override_ex(mod, &gc, "12345678", 8, bad) == KILN_ERR_INVALID_ARGUMENT,
              "origin " + std::to_string(bad) + " refused on blob_add_ex and blob_override_ex");
    check(blob_ledger(nullptr).size() == before && blob(game) == "MANIFEST", "nothing added, the served bytes unchanged");
    // a new blob through blob_add_ex, then the same bytes from another owner: two registrations, one hash, the later served
    check(op_blob_add_ex(mod, "SAME-BYTES", 10, &out, KILN_BLOB_ORIGIN_MANAGED) == KILN_OK, "a managed blob_add_ex");
    const HashK added{out.size, out.hash0, out.hash1, out.hash2};
    KilnContentHash out2{};
    check(op_blob_add(native, "SAME-BYTES", 10, &out2) == KILN_OK && std::memcmp(&out, &out2, 16) == 0, "the same bytes again: the same hash");
    l = ledger_of(added);
    check(l.size() == 2 && l[0].origin == KILN_BLOB_ORIGIN_MANAGED && !l[0].is_override && l[1].origin == KILN_BLOB_ORIGIN_NATIVE_API &&
              l[1].owner == native && blob(added) == "SAME-BYTES",
          "two registrations of one added hash: MANAGED then NATIVE_API, neither an override");
    // the thread's scope ends with it: the default is NATIVE_API again
    check(current_origin() == KILN_BLOB_ORIGIN_NATIVE_API, "outside a scope the origin is NATIVE_API");
    op_revert_owner(native);
    op_revert_owner(mod);
    op_revert_owner(manifest);
}

// ---- the content blob catalog (0.2.0-dev.28): the ABI layouts, then the snapshot rules -----------------------------
static_assert(sizeof(KilnBlobCatalogInfo) == 32 && alignof(KilnBlobCatalogInfo) == 8, "KilnBlobCatalogInfo: 32 bytes, align 8");
static_assert(offsetof(KilnBlobCatalogInfo, generation) == 8 && offsetof(KilnBlobCatalogInfo, total) == 16 &&
              offsetof(KilnBlobCatalogInfo, flags) == 24 && offsetof(KilnBlobCatalogInfo, reason) == 28, "KilnBlobCatalogInfo offsets");
static_assert(sizeof(KilnBlobCatalogEntry) == 48 && alignof(KilnBlobCatalogEntry) == 8, "KilnBlobCatalogEntry: 48 bytes, align 8");
static_assert(offsetof(KilnBlobCatalogEntry, hash) == 8 && offsetof(KilnBlobCatalogEntry, origins) == 24 &&
              offsetof(KilnBlobCatalogEntry, selected_origin) == 28 && offsetof(KilnBlobCatalogEntry, selected_owner) == 32 &&
              offsetof(KilnBlobCatalogEntry, selected_sequence) == 40, "KilnBlobCatalogEntry offsets");

KilnBlobCatalogInfo cat_info() {
    KilnBlobCatalogInfo i{};
    i.size = sizeof i;
    return i;
}
std::vector<KilnBlobCatalogEntry> cat_all(KilnHandle h, uint64_t total) {
    std::vector<KilnBlobCatalogEntry> all(total);
    size_t n = 0;
    if (total && op_catalog_read(h, 0, all.data(), sizeof(KilnBlobCatalogEntry), all.size(), &n) != KILN_OK) all.clear();
    all.resize(n);
    return all;
}
const KilnBlobCatalogEntry* cat_find(const std::vector<KilnBlobCatalogEntry>& v, const HashK& h) {
    for (const auto& e : v)
        if (e.hash.size == h.size && e.hash.hash0 == h.h0 && e.hash.hash1 == h.h1 && e.hash.hash2 == h.h2) return &e;
    return nullptr;
}
bool cat_sorted(const std::vector<KilnBlobCatalogEntry>& v) {
    for (size_t i = 1; i < v.size(); ++i) {
        const auto& a = v[i - 1].hash;
        const auto& b = v[i].hash;
        const auto ka = std::make_tuple(a.size, a.hash0, a.hash1, a.hash2), kb = std::make_tuple(b.size, b.hash0, b.hash1, b.hash2);
        if (!(ka < kb)) return false;
    }
    return true;
}
std::vector<HashK> game_index(size_t n, uint32_t salt) {  // n distinct game hashes, unsorted (the catalog sorts)
    std::vector<HashK> v(n);
    for (size_t i = 0; i < n; ++i) v[i] = HashK{uint32_t(1000 + (i * 7919) % 50000), uint32_t(i * 2654435761u) ^ salt, uint32_t(i), 0x5eed};
    return v;
}

void blob_catalog(KilnOwner manifest, KilnOwner mod, KilnOwner native) {
    const int failed_before = g_fail;
    KilnHandle h = 99;
    KilnBlobCatalogInfo info = cat_info();
    // arguments
    check(op_catalog_open(0, 0, &h, &info) == KILN_ERR_INVALID_ARGUMENT && h == 0, "owner 0 refused");
    check(op_catalog_open(mod, 2, &h, &info) == KILN_ERR_INVALID_ARGUMENT, "an unknown mode refused");
    check(op_catalog_open(mod, 0, nullptr, &info) == KILN_ERR_INVALID_ARGUMENT && op_catalog_open(mod, 0, &h, nullptr) == KILN_ERR_INVALID_ARGUMENT,
          "a NULL handle or info refused");
    info.size = 31;
    check(op_catalog_open(mod, 0, &h, &info) == KILN_ERR_INVALID_ARGUMENT && info.size == 31 && info.status == 0, "info.size 31 refused, info untouched");
    // before the game's index is readable: the default open is NOT_READY with no handle; registrations-only works, incomplete
    const HashK g_over{64, 0xa0, 1, 1}, g_only{32, 0xa1, 2, 2};
    const KilnContentHash over = kch(g_over);
    {
        OriginScope scope(KILN_BLOB_ORIGIN_MANIFEST);
        check(op_blob_override(manifest, &over, std::string(64, 'M').data(), 64) == KILN_OK, "a manifest override of a game blob");
    }
    KilnContentHash added{};
    check(op_blob_add_ex(mod, "MANAGED-ADDITION", 16, &added, KILN_BLOB_ORIGIN_MANAGED) == KILN_OK, "a managed addition");
    KilnContentHash again{};
    check(op_blob_add(native, "MANAGED-ADDITION", 16, &again) == KILN_OK && std::memcmp(&added, &again, 16) == 0,
          "the same hash registered twice (a native owner adds the same bytes)");
    const HashK a1{added.size, added.hash0, added.hash1, added.hash2};
    g_fake_index = GameIndex::not_ready;
    info = cat_info();
    h = 99;
    check(op_catalog_open(mod, KILN_BLOB_CATALOG_REQUIRE_COMPLETE, &h, &info) == KILN_ERR_UNAVAILABLE && h == 0 &&
              info.status == KILN_BLOB_CATALOG_NOT_READY && info.reason == KILN_BLOB_CATALOG_REASON_GAME_NOT_READY && info.total == 0,
          "open before the game's index is ready: NOT_READY, game-not-ready, no handle");
    info = cat_info();
    check(op_catalog_open(mod, KILN_BLOB_CATALOG_REGISTRATIONS_ONLY, &h, &info) == KILN_OK && h != 0 &&
              info.status == KILN_BLOB_CATALOG_INCOMPLETE && info.flags == KILN_BLOB_CATALOG_REGISTRATIONS_COMPLETE && info.total == 2,
          "registrations only: INCOMPLETE, exactly REGISTRATIONS_COMPLETE, the two registered hashes");
    auto regs = cat_all(h, info.total);
    const auto* ro = cat_find(regs, g_over);
    check(regs.size() == 2 && ro && ro->origins == KILN_BLOB_ORIGIN_MANIFEST && !(ro->flags & KILN_BLOB_CATALOG_OVERRIDDEN),
          "registrations only names no GAME origin and no override (it does not read the game's index)");
    check(op_catalog_close(h) == KILN_OK && op_catalog_close(h) == KILN_ERR_NOT_FOUND, "close, then a second close is NOT_FOUND");
    // the game's index readable: base entries + manifest override + managed addition + the same hash twice
    g_fake_keys = {g_only, g_over, HashK{32, 0x01, 0, 0}, HashK{16, 0xff, 0, 0}};
    g_fake_index = GameIndex::ok;
    info = cat_info();
    check(op_catalog_open(mod, KILN_BLOB_CATALOG_REQUIRE_COMPLETE, &h, &info) == KILN_OK && h != 0 && info.status == KILN_BLOB_CATALOG_COMPLETE &&
              info.flags == (KILN_BLOB_CATALOG_GAME_COMPLETE | KILN_BLOB_CATALOG_REGISTRATIONS_COMPLETE) && info.reason == 0 && info.total == 5,
          "complete: four game blobs and one added hash");
    const KilnHandle first = h;
    const KilnBlobCatalogInfo first_info = info;
    auto all = cat_all(h, info.total);
    check(all.size() == 5 && cat_sorted(all), "five entries, sorted by (size, hash0, hash1, hash2)");
    const auto* e_over = cat_find(all, g_over);
    const auto* e_only = cat_find(all, g_only);
    const auto* e_add = cat_find(all, a1);
    check(e_over && e_over->size == 48 && e_over->origins == (KILN_BLOB_ORIGIN_GAME | KILN_BLOB_ORIGIN_MANIFEST) &&
              e_over->selected_origin == KILN_BLOB_ORIGIN_MANIFEST && e_over->selected_owner == manifest && e_over->selected_sequence != 0 &&
              e_over->flags == KILN_BLOB_CATALOG_OVERRIDDEN,
          "the overridden game blob keeps GAME, adds MANIFEST, the manifest's registration is served, OVERRIDDEN");
    check(e_only && e_only->origins == KILN_BLOB_ORIGIN_GAME && e_only->selected_origin == KILN_BLOB_ORIGIN_GAME &&
              e_only->selected_owner == 0 && e_only->selected_sequence == 0 && e_only->flags == 0,
          "a game-only blob: GAME, the game's bytes, owner 0, sequence 0");
    check(e_add && e_add->origins == (KILN_BLOB_ORIGIN_MANAGED | KILN_BLOB_ORIGIN_NATIVE_API) &&
              e_add->selected_origin == KILN_BLOB_ORIGIN_NATIVE_API && e_add->selected_owner == native && e_add->flags == 0,
          "the hash registered twice: both origins, the later (native) registration served, not GAME, not overridden");
    check(blob(a1) == "MANAGED-ADDITION" && blob(g_over) == std::string(64, 'M'), "the served bytes are the catalog's winners");
    // a second open at the same generation shares the snapshot; a registration moves the generation, the open one stays
    KilnHandle shared = 0;
    info = cat_info();
    check(op_catalog_open(native, 0, &shared, &info) == KILN_OK && shared != first && info.generation == first_info.generation &&
              info.total == first_info.total,
          "a second open at the same generation: a new handle, the same generation and total");
    const HashK later{8, 0xb0, 3, 3};
    const KilnContentHash later_c = kch(later);
    check(op_blob_override_ex(mod, &later_c, "LATER!!!", 8, KILN_BLOB_ORIGIN_MANAGED) == KILN_OK && catalog_generation() > first_info.generation,
          "a registration while the snapshot is open moves the catalog's generation");
    check(cat_all(first, first_info.total).size() == 5 && !cat_find(cat_all(first, first_info.total), later) &&
              std::memcmp(cat_all(first, 5).data(), all.data(), 5 * sizeof(KilnBlobCatalogEntry)) == 0,
          "the open snapshot is unchanged: same entries, the new hash not in it");
    info = cat_info();
    KilnHandle newer = 0;
    check(op_catalog_open(mod, 0, &newer, &info) == KILN_OK && info.generation > first_info.generation && info.total == 6 &&
              cat_find(cat_all(newer, 6), later),
          "a new open: a newer generation with the new hash");
    // paging
    std::vector<uint8_t> page(3 * 64, 0xAB);
    size_t n = 99;
    check(op_catalog_read(first, 5, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 64, 3, &n) == KILN_OK && n == 0 && page[0] == 0xAB,
          "first == total: 0 written, nothing touched");
    check(op_catalog_read(first, 6, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 64, 3, &n) == KILN_ERR_INVALID_ARGUMENT && n == 0,
          "first > total refused");
    check(op_catalog_read(first, 0, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 47, 3, &n) == KILN_ERR_INVALID_ARGUMENT,
          "an entry_size below 48 refused");
    check(op_catalog_read(first, 0, nullptr, 48, 3, &n) == KILN_ERR_INVALID_ARGUMENT && op_catalog_read(first, 0, nullptr, 48, 0, &n) == KILN_OK && n == 0 &&
              op_catalog_read(first, 0, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 48, 1, nullptr) == KILN_ERR_INVALID_ARGUMENT,
          "out NULL with cap > 0, or written NULL, refused; cap 0 reads nothing");
    check(op_catalog_read(first, 3, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 64, 3, &n) == KILN_OK && n == 2 &&
              std::memcmp(page.data(), &all[3], 48) == 0 && std::memcmp(page.data() + 64, &all[4], 48) == 0 &&
              std::all_of(page.begin() + 48, page.begin() + 64, [](uint8_t b) { return b == 0; }) &&
              std::all_of(page.begin() + 112, page.begin() + 128, [](uint8_t b) { return b == 0; }) && page[128] == 0xAB,
          "a 64-byte stride: the last two entries, their trailing 16 bytes zeroed, nothing past them written");
    check(op_catalog_read(12345, 0, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 48, 1, &n) == KILN_ERR_NOT_FOUND, "an unknown handle: NOT_FOUND");
    // the index unreadable, unsupported, too big; the content hooks off
    g_fake_index = GameIndex::unreadable;
    info = cat_info();
    op_blob_remove(mod, &later_c);  // a new generation: no snapshot to share
    check(op_catalog_open(mod, 0, &h, &info) == KILN_ERR_UNAVAILABLE && h == 0 && info.status == KILN_BLOB_CATALOG_NOT_READY &&
              info.reason == KILN_BLOB_CATALOG_REASON_UNREADABLE_MAP,
          "an index that faults or moves while copied: NOT_READY, unreadable-map, no handle (never a partial snapshot)");
    g_fake_index = GameIndex::unsupported;
    check(op_catalog_open(mod, 0, &h, &info) == KILN_ERR_UNAVAILABLE && h == 0 && info.status == KILN_BLOB_CATALOG_UNAVAILABLE &&
              info.reason == KILN_BLOB_CATALOG_REASON_UNSUPPORTED_TABLE,
          "an index laid out otherwise: UNAVAILABLE, unsupported-table");
    g_fake_index = GameIndex::ok;
    g_fake_total = 1000001;
    check(op_catalog_open(mod, 0, &h, &info) == KILN_ERR_BUFFER_TOO_SMALL && h == 0 && info.status == KILN_BLOB_CATALOG_LIMIT_EXCEEDED &&
              info.reason == KILN_BLOB_CATALOG_REASON_ENTRY_BUDGET,
          "over 1,000,000 entries: LIMIT_EXCEEDED, entry budget, no handle (never truncated)");
    g_fake_total = 0;
    g_fake_content = false;
    check(op_catalog_open(mod, 1, &h, &info) == KILN_ERR_UNAVAILABLE && h == 0 && info.status == KILN_BLOB_CATALOG_UNAVAILABLE,
          "the content hooks off: UNAVAILABLE in either mode");
    g_fake_content = true;
    // 64 handles at most
    std::vector<KilnHandle> many;
    for (;;) {
        KilnHandle x = 0;
        info = cat_info();
        if (op_catalog_open(mod, 0, &x, &info) != KILN_OK) break;
        many.push_back(x);
    }
    check(many.size() + 3 == 64 && info.status == KILN_BLOB_CATALOG_LIMIT_EXCEEDED && info.reason == KILN_BLOB_CATALOG_REASON_MEMORY_BUDGET,
          "the 65th handle is refused: LIMIT_EXCEEDED (" + std::to_string(many.size() + 3) + " open)");
    // the owner's revert closes its handles; another owner's stay
    op_revert_owner(mod);
    check(op_catalog_read(first, 0, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 48, 1, &n) == KILN_ERR_NOT_FOUND &&
              op_catalog_read(many[0], 0, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 48, 1, &n) == KILN_ERR_NOT_FOUND &&
              op_catalog_read(shared, 0, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 48, 1, &n) == KILN_OK && n == 1,
          "revert_owner closes that owner's handles only");
    check(op_catalog_close(shared) == KILN_OK, "the other owner closes its own");
    // Deterministic interleaving: an open has started copying the index when its owner is reverted.
    // The revert must also cancel the handle that has not been published yet.
    catalog_changed(kChangeReady);  // force a build rather than reuse an existing snapshot
    g_index_copy = [=] { op_revert_owner(mod); };
    info = cat_info();
    h = 99;
    const KilnResult interrupted = op_catalog_open(mod, 0, &h, &info);
    g_index_copy = {};
    check(interrupted == KILN_ERR_REVOKED && h == 0 && info.status == KILN_BLOB_CATALOG_UNAVAILABLE,
          "owner revert during open cancels the unpublished handle");
    if (h) op_catalog_close(h);
    info = cat_info();
    check(op_catalog_open(mod, 0, &h, &info) == KILN_OK && op_catalog_close(h) == KILN_OK,
          "an explicit revert permits a later open by the same live owner");
    info = cat_info();
    op_catalog_open(mod, 0, &h, &info);
    if (g_owner_retiring) g_owner_retiring(mod, g_owner_retiring_user);  // the core's raw owner_revoke notification
    check(g_owner_retiring && op_catalog_read(h, 0, reinterpret_cast<KilnBlobCatalogEntry*>(page.data()), 48, 1, &n) == KILN_ERR_NOT_FOUND,
          "raw owner revoke closes catalog handles without a managed Retiring handler");
    op_catalog_close(h);
    info = cat_info();
    h = 99;
    check(op_catalog_open(mod, 0, &h, &info) == KILN_ERR_REVOKED && h == 0,
          "a revoked owner cannot publish another handle after its cleanup callback");
    if (h) op_catalog_close(h);
    // 128 MiB for every distinct snapshot held: two of 999,000 entries fit (96 MB), a third does not
    g_fake_keys = game_index(999000, 1);
    std::vector<KilnHandle> big;
    for (int i = 0; i < 3; ++i) {
        KilnContentHash x{};
        op_blob_add(native, ("STEP" + std::to_string(i)).c_str(), 5, &x);  // a new generation each time: three distinct snapshots
        KilnHandle b = 0;
        info = cat_info();
        const KilnResult r = op_catalog_open(native, 0, &b, &info);
        if (i < 2) check(r == KILN_OK && info.total > 999000, "a large snapshot " + std::to_string(i + 1) + " opens (" + std::to_string(info.total) + " entries)");
        else check(r == KILN_ERR_BUFFER_TOO_SMALL && b == 0 && info.status == KILN_BLOB_CATALOG_LIMIT_EXCEEDED &&
                       info.reason == KILN_BLOB_CATALOG_REASON_MEMORY_BUDGET,
                   "a third distinct one would hold more than 128 MiB: LIMIT_EXCEEDED, memory budget");
        if (b) big.push_back(b);
    }
    // Hold a read's reference after its last handle closes: those bytes still count against the global budget.
    bool read_budget_refused = false;
    g_catalog_read_copy = [&] {
        op_catalog_close(big[0]);
        KilnContentHash x{};
        op_blob_add(native, "READ-IN-FLIGHT", 14, &x);
        KilnHandle next = 0;
        auto next_info = cat_info();
        read_budget_refused = op_catalog_open(native, 0, &next, &next_info) == KILN_ERR_BUFFER_TOO_SMALL && next == 0 &&
                              next_info.reason == KILN_BLOB_CATALOG_REASON_MEMORY_BUDGET;
        if (next) op_catalog_close(next);
    };
    KilnBlobCatalogEntry held_entry{};
    check(big.size() == 2 && op_catalog_read(big[0], 0, &held_entry, sizeof held_entry, 1, &n) == KILN_OK && n == 1 && read_budget_refused,
          "a closed snapshot retained by a page copy still counts against 128 MiB");
    g_catalog_read_copy = {};
    info = cat_info();
    check(op_catalog_open(native, 0, &h, &info) == KILN_OK && op_catalog_close(h) == KILN_OK,
          "the page copy releases its snapshot budget when it finishes");
    for (KilnHandle b : big) op_catalog_close(b);
    // a close (and an owner's revert) while another thread copies pages: the copy finishes on its own reference
    g_fake_keys = game_index(60000, 2);
    bool torn = false;
    int copies = 0;
    for (int round = 0; round < 40; ++round) {
        KilnHandle c = 0;
        info = cat_info();
        if (op_catalog_open(manifest, 0, &c, &info) != KILN_OK) { torn = true; break; }
        const uint64_t total = info.total;
        std::thread reader([&, c, total] {
            std::vector<KilnBlobCatalogEntry> pg(4096);
            for (uint64_t at = 0;;) {
                size_t got = 0;
                if (op_catalog_read(c, at, pg.data(), sizeof(KilnBlobCatalogEntry), pg.size(), &got) != KILN_OK) return;  // closed: fine
                for (size_t i = 0; i < got; ++i) torn |= pg[i].size != 48;
                ++copies;
                at += got;
                if (at >= total) at = 0;
            }
        });
        std::this_thread::sleep_for(std::chrono::microseconds(200 * (round % 5)));
        if (round % 2) op_catalog_close(c);
        else op_revert_owner(manifest);
        reader.join();
    }
    check(!torn && copies > 0, "40 closes and reverts during page copies: every copied entry whole (" + std::to_string(copies) + " pages)");
    g_fake_keys.clear();
    g_fake_index = GameIndex::not_ready;
    op_revert_owner(native);
    if (g_fail == failed_before) std::puts("PASS assets.catalog-snapshot");
}

// ---- a native owner the core revokes without revert_owner (kiln.hooks' owner-retiring observer) ----------------------------
bool wait_for(const std::function<bool()>& done, int ms = 10000) {
    for (int i = 0; i < ms; ++i) {
        if (done()) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return done();
}
bool settle() { return wait_for([] { return store_cleanup_idle(); }); }  // the deferred revert done, no cleanup thread left
void revoke(KilnOwner o) { g_owner_retiring(o, g_owner_retiring_user); }  // what the core's owner_revoke calls
bool in_content(const HashK& h) {  // the content snapshot the hooks serve from lists the hash
    ReadGuard g;
    const ContentSnap* cs = g_content.load();
    return cs && cs->entries.count(h);
}
size_t ledger_count(KilnOwner o) {
    size_t n = 0;
    for (const LedgerEntry& e : blob_ledger(nullptr)) n += e.owner == o;
    return n;
}
HashK hk(const KilnContentHash& c) { return HashK{c.size, c.hash0, c.hash1, c.hash2}; }
// The registrations-only catalog `reader` opens now: every entry.
std::vector<KilnBlobCatalogEntry> catalog_now(KilnOwner reader) {
    KilnHandle h = 0;
    KilnBlobCatalogInfo info = cat_info();
    if (op_catalog_open(reader, KILN_BLOB_CATALOG_REGISTRATIONS_ONLY, &h, &info) != KILN_OK) return {};
    auto v = cat_all(h, info.total);
    op_catalog_close(h);
    return v;
}
bool catalog_names(const std::vector<KilnBlobCatalogEntry>& v, KilnOwner o) {
    return std::any_of(v.begin(), v.end(), [&](const KilnBlobCatalogEntry& e) { return e.selected_owner == o; });
}

void owner_revoked_without_revert(KilnOwner other, const Key& game_key, uint32_t game_key_handle, const std::string& game_bytes,
                                  const Key& icon_key) {
    const int failed_before = g_fail;
    catalog_start();  // kiln_plugin_main subscribes the observer
    check(g_owner_retiring != nullptr && settle(), "the store's cleanup observes native owner revocation");
    g_fake_index = GameIndex::not_ready;

    // A native owner overrides a game blob another owner overrode first (the later registration is served), adds a blob, overrides a
    // game resource and registers an icon; the other owner keeps a blob of its own.
    const KilnOwner gone = 41;
    const HashK game{8, 0x77, 0x88, 0x99};
    const KilnContentHash gc = kch(game);
    KilnContentHash other_own{}, gone_new{};
    check(op_blob_override(other, &gc, "OTHER!!!", 8) == KILN_OK && op_blob_add(other, "OTHER-OWN-BLOB", 14, &other_own) == KILN_OK,
          "another owner overrides the game blob and adds one");
    check(op_blob_override(gone, &gc, "GONE!!!!", 8) == KILN_OK && blob(game) == "GONE!!!!", "the native owner's later override is served");
    check(op_blob_add(gone, "GONE-NEW-BLOB", 13, &gone_new) == KILN_OK && blob(hk(gone_new)) == "GONE-NEW-BLOB", "it adds a blob");
    check(put(gone, game_key, "GONE-RES", KILN_ASSET_PUT_OVERRIDE) == KILN_OK && by_handle(game_key_handle) == "GONE-RES",
          "it overrides a game resource");
    Rgba img;
    img.w = img.h = 1;
    img.px = {9, 8, 7, 6};
    check(op_icon_register(gone, icon_key.g, img) == KILN_OK && op_icon_state(icon_key.g) == KILN_ICON_PENDING, "it registers an icon");
    check(ledger_count(gone) == 3 && catalog_names(catalog_now(other), gone), "the ledger and a catalog snapshot list it (3 blobs)");
    const size_t items_before = items();
    const uint64_t gen = catalog_generation();

    // The core revokes it while a hook's reader is inside the snapshots: the callback returns at once, the revert waits.
    std::atomic<bool> holding{false}, release{false}, returned{false};
    std::thread reader([&] {
        ReadGuard g;
        holding = true;
        while (!release.load()) std::this_thread::yield();
    });
    wait_for([&] { return holding.load(); });
    std::thread revoker([&] {
        revoke(gone);
        returned = true;
    });
    const bool quick = wait_for([&] { return returned.load(); }, 5000);
    const bool pending = !store_cleanup_idle();
    release = true;  // (only now: a store call from here would wait for the cleanup thread, which waits for the reader)
    reader.join();
    revoker.join();
    check(quick, "the owner-retiring callback returns while a reader holds a snapshot (it does not wait)");
    check(pending, "the revert is still pending while the reader is inside (it runs on the cleanup thread, not in the callback)");
    check(settle(), "the deferred revert runs with no further store call (the cleanup thread)");

    // After the deferred step.
    check(blob(game) == "OTHER!!!" && in_content(game), "the other owner's override of the game blob is served again");
    check(!in_content(hk(gone_new)) && blob(hk(gone_new)) == "<result 2>", "the revoked owner's own blob is not served");
    check(by_handle(game_key_handle) == "<not served>" && read(game_key) == game_bytes, "the game's own resource bytes are served again");
    check(op_icon_state(icon_key.g) == KILN_ICON_NONE, "its icon is gone");
    check(ledger_count(gone) == 0 && items() == items_before - 2, "the ledger lists none of its blobs; its resource and icon texture are gone");
    check(blob(hk(other_own)) == "OTHER-OWN-BLOB" && ledger_count(other) == 2, "the other owner's blobs are untouched");
    check(catalog_generation() == gen + 1, "the catalog generation moved once (" + std::to_string(catalog_generation() - gen) + ")");
    {
        const auto v = catalog_now(other);
        const auto* e = cat_find(v, game);
        check(!catalog_names(v, gone) && !cat_find(v, hk(gone_new)) && e && e->selected_owner == other,
              "a new catalog snapshot does not list it; the game blob's winner is the other owner");
    }

    // Admission: nothing it calls registers anything any more.
    std::puts("-- 13b: registrations of the revoked owner");
    size_t mark = g_log.size();
    KilnContentHash x{};
    check(op_blob_add(gone, "LATE", 4, &x) == KILN_ERR_REVOKED && op_blob_add_ex(gone, "LATE", 4, &x, KILN_BLOB_ORIGIN_MANAGED) == KILN_ERR_REVOKED,
          "blob_add and blob_add_ex refused with KILN_ERR_REVOKED");
    check(op_blob_override(gone, &gc, "LATE!!!!", 8) == KILN_ERR_REVOKED &&
              op_blob_override_ex(gone, &gc, "LATE!!!!", 8, KILN_BLOB_ORIGIN_NATIVE_API) == KILN_ERR_REVOKED,
          "blob_override and blob_override_ex refused");
    check(put(gone, game_key, "LATE-RES", KILN_ASSET_PUT_OVERRIDE) == KILN_ERR_REVOKED && op_icon_register(gone, icon_key.g, img) == KILN_ERR_REVOKED,
          "resource_put and icon_register refused");
    check(logged(mark, "owner 41 blob", "revoked"), "the refusal is logged with the owner");
    check(ledger_count(gone) == 0 && items() == items_before - 2 && blob(game) == "OTHER!!!" && catalog_generation() == gen + 1,
          "nothing added, nothing served, the generation unchanged");

    // revert_owner after the deferred revert, and before a revocation: harmless either way.
    op_revert_owner(gone);
    check(catalog_generation() == gen + 1 && blob(game) == "OTHER!!!", "an explicit revert_owner after it is a no-op");
    revoke(gone);
    check(settle() && catalog_generation() == gen + 1, "a second revocation notice of the same owner is a no-op");
    const KilnOwner early = 42;
    check(op_blob_override(early, &gc, "EARLY!!!", 8) == KILN_OK && blob(game) == "EARLY!!!", "another native owner overrides the game blob");
    op_revert_owner(early);
    const uint64_t gen2 = catalog_generation();
    revoke(early);
    check(settle() && catalog_generation() == gen2 && blob(game) == "OTHER!!!", "a revert_owner before its revocation: the deferred step finds nothing");

    // With the cleanup thread held: the next store call, and a catalog open, take the revert on themselves.
    std::puts("-- 13c: the next store call does the revert");
    const KilnOwner late = 43;
    KilnContentHash late_new{};
    KilnHandle kept = 0;  // held open: the next open at the same generation would share its snapshot
    KilnBlobCatalogInfo kept_info = cat_info();
    check(op_blob_add(late, "LATE-OWNER-BLOB", 15, &late_new) == KILN_OK &&
              op_catalog_open(other, KILN_BLOB_CATALOG_REGISTRATIONS_ONLY, &kept, &kept_info) == KILN_OK &&
              catalog_names(cat_all(kept, kept_info.total), late),
          "a native owner adds a blob; an open snapshot at this generation lists it");
    g_hold_cleanup = true;
    revoke(late);
    const bool held = wait_for([] { return g_cleanup_held.load() == 1; });
    check(held && !store_cleanup_idle() && in_content(hk(late_new)), "revoked; the cleanup thread is held; its bytes are still served meanwhile");
    check(!catalog_names(catalog_now(other), late), "a catalog open after the mark does the revert first: the shared snapshot is not reused");
    check(!in_content(hk(late_new)) && ledger_count(late) == 0, "and its blob is no longer served");
    op_catalog_close(kept);
    g_hold_cleanup = false;
    check(settle(), "the released cleanup thread finds nothing left");
    const KilnOwner later = 44;
    KilnContentHash later_new{};
    check(op_blob_add(later, "LATER-OWNER-BLOB", 16, &later_new) == KILN_OK, "another native owner adds a blob");
    g_hold_cleanup = true;
    revoke(later);
    wait_for([] { return g_cleanup_held.load() == 1; });
    const uint64_t gen3 = catalog_generation();
    check(op_blob_remove(other, &other_own) == KILN_OK && !in_content(hk(later_new)) && catalog_generation() == gen3 + 2,
          "another owner's store call reverts it first (one generation step for the revert, one for the removal)");
    g_hold_cleanup = false;
    check(settle(), "the released cleanup thread finds nothing left");

    // A registration racing the mark: never survives the cleanup.
    std::puts("-- 13d: registrations racing the revocation");
    int survivors = 0, raced = 0, accepted_total = 0;
    for (int round = 0; round < 200; ++round) {
        const KilnOwner o = 1000 + round;
        std::vector<HashK> blobs;
        std::vector<Key> keys;
        std::atomic<bool> go{false};
        std::thread writer([&] {
            while (!go.load()) std::this_thread::yield();
            for (int i = 0; i < 400; ++i) {  // until refused (bounded: a store that never refuses fails below instead of hanging)
                const std::string bytes = "RACE-" + std::to_string(round) + "-" + std::to_string(i);
                if (i % 3 == 2) {
                    char guid[40];
                    std::snprintf(guid, sizeof guid, "77777777-0000-0000-%04d-%012d", round, i);
                    const Key k = key(guid, 0x12345678u);
                    if (put(o, k, bytes, KILN_ASSET_PUT_NEW) != KILN_OK) return;
                    keys.push_back(k);
                } else {
                    KilnContentHash h{};
                    if (op_blob_add(o, bytes.data(), bytes.size(), &h) != KILN_OK) return;
                    blobs.push_back(hk(h));
                }
            }
        });
        go = true;
        std::this_thread::sleep_for(std::chrono::microseconds(37 * (round % 11)));
        revoke(o);
        writer.join();
        if (!settle()) {
            ++survivors;
            continue;
        }
        accepted_total += static_cast<int>(blobs.size() + keys.size());
        raced += !blobs.empty() || !keys.empty();
        for (const HashK& h : blobs) survivors += in_content(h) || blob(h) != "<result 2>";
        for (const Key& k : keys) survivors += read(k) != "<result 2>";
        survivors += static_cast<int>(ledger_count(o));
    }
    check(survivors == 0 && raced > 0, "200 rounds of registrations racing the mark: none survives (" + std::to_string(accepted_total) +
                                           " accepted before the mark in " + std::to_string(raced) + " rounds, all reverted)");
    check(blob(game) == "OTHER!!!" && ledger_count(other) == 1, "the other owner's registrations untouched by all of it");
    op_revert_owner(other);
    if (g_fail == failed_before) std::puts("PASS assets.revoked-owner-cleanup");
}

const char* kGraphGuid = "21319e83-5087-4fff-8795-22997843649e";
const char* kGraphKeyText = "21319e83-5087-4fff-8795-22997843649e:keen::anim_graph::runtime_graph::AnimationGraphResource2_0:0";
constexpr uint32_t kGraphHandle = 100, kOtherGraphHandle = 101, kItemHandle = 102, kIconHandle = 103;
}  // namespace

int main() {
    KilnApi api{};
    api.log = log_line;
    api.service = fake_service;
    g_api = &api;
    g_self = 1;
    bool ok;
    const uint32_t graph_type = type_hash("keen::anim_graph::runtime_graph::AnimationGraphResource2_0", &ok);
    check(graph_type == kPlayerGraphType && graph_type == 0xa345050eu, "the graph type hash is FNV-1a of its qualified name (0xa345050e)");
    const uint32_t item_type = type_hash("keen::ItemInfo", &ok), other_type = 0x12345678u;
    const Key graph = key(kGraphGuid, graph_type);
    const Key other_graph = key("11111111-2222-3333-4444-555555555555", graph_type);  // another graph (an NPC's, say)
    const Key graph_guid_item = key(kGraphGuid, item_type);
    const Key graph_part1 = key(kGraphGuid, graph_type, 1);
    const Key item = key("22222222-3333-4444-5555-666666666666", item_type);
    const Key icon = key("33333333-4444-5555-6666-777777777777", KILN_ASSET_TYPE_UI_TEXTURE);
    const Key fresh = key("44444444-5555-6666-7777-888888888888", other_type);
    g_game[graph] = {kGraphHandle, "GAME-GRAPH"};
    g_game[other_graph] = {kOtherGraphHandle, "GAME-OTHER-GRAPH"};
    g_game[item] = {kItemHandle, "GAME-ITEM"};
    g_game[icon] = {kIconHandle, "GAME-ICON"};
    const KilnOwner manifest = 11, playeranim = 21, mod = 31;
    const std::string merged = "MERGED-GRAPH";

    // 1. Reserved before any pin: at boot (no resource system yet, where assets.json manifests are loaded) and once it is up.
    std::puts("-- 1: the graph key without the pin flag, before any pin");
    size_t mark = g_log.size();
    for (uint32_t f : {0u, (uint32_t)KILN_ASSET_PUT_OVERRIDE, (uint32_t)KILN_ASSET_PUT_NEW})
        check(put(manifest, graph, "MOD-GRAPH", f) == KILN_ERR_UNAVAILABLE, "boot: put (" + flag_name(f) + ") refused with KILN_ERR_UNAVAILABLE");
    check(items() == 0, "boot: nothing added");
    check(logged(mark, kGraphKeyText, "reserved for kiln.playeranim"), "boot: the log line names the key");
    g_S = 1;
    on_resource_system_ready(1);
    mark = g_log.size();
    for (uint32_t f : {0u, (uint32_t)KILN_ASSET_PUT_OVERRIDE, (uint32_t)KILN_ASSET_PUT_NEW})
        check(put(mod, graph, "MOD-GRAPH", f) == KILN_ERR_UNAVAILABLE, "up: put (" + flag_name(f) + ") refused with KILN_ERR_UNAVAILABLE");
    check(items() == 0, "up: nothing added");
    check(read(graph) == "GAME-GRAPH" && by_handle(kGraphHandle) == "<not served>", "up: the game's own graph is what is read and served");
    check(logged(mark, kGraphKeyText, "reserved for kiln.playeranim"), "up: the log line names the key");

    // 2. The pin: kiln.playeranim's put.
    std::puts("-- 2: the pinning put");
    check(put(playeranim, graph, merged, KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN) == KILN_OK, "put (override|pin) accepted");
    check(read(graph) == merged, "resource_read gives the pinned bytes");
    check(by_handle(kGraphHandle) == merged, "served for the game's handle (an override)");
    check(items() == 1, "one item");

    // 3. Nobody puts a pinned key again, its owner included.
    std::puts("-- 3: puts after the pin");
    for (uint32_t f : {0u, (uint32_t)KILN_ASSET_PUT_OVERRIDE, (uint32_t)KILN_ASSET_PUT_NEW, (uint32_t)KILN_ASSET_PUT_PIN,
                       (uint32_t)(KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN)}) {
        mark = g_log.size();
        check(put(mod, graph, "MOD-GRAPH", f) == KILN_ERR_UNAVAILABLE && logged(mark, kGraphKeyText, "refused"),
              "another owner's put (" + flag_name(f) + ") refused, logged with the key");
    }
    for (uint32_t f : {(uint32_t)KILN_ASSET_PUT_OVERRIDE, (uint32_t)(KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN)}) {
        mark = g_log.size();
        check(put(playeranim, graph, "MERGED-AGAIN", f) == KILN_ERR_UNAVAILABLE && logged(mark, kGraphKeyText, "pinned"),
              "the pinning owner's own put (" + flag_name(f) + ") refused, logged with the key");
    }
    check(read(graph) == merged && by_handle(kGraphHandle) == merged && items() == 1, "the pinned bytes are still read and served; one item");

    // 4. No removal.
    std::puts("-- 4: removals");
    mark = g_log.size();
    check(remove(playeranim, graph) == KILN_ERR_UNAVAILABLE, "removal by the pinning owner refused");
    check(remove(mod, graph) == KILN_ERR_UNAVAILABLE, "removal by another owner refused");
    check(logged(mark, kGraphKeyText, "removal refused"), "the log line names the key");
    check(read(graph) == merged && by_handle(kGraphHandle) == merged && items() == 1, "the pinned bytes stay");

    // 5. revert_owner of the pinning owner (also what kiln.assets does when it sees the owner revoked).
    std::puts("-- 5: revert_owner of the pinning owner");
    check(put(playeranim, item, "PA-ITEM", KILN_ASSET_PUT_OVERRIDE) == KILN_OK && put(playeranim, fresh, "PA-NEW", KILN_ASSET_PUT_NEW) == KILN_OK,
          "the owner has two other resources");
    check(read(item) == "PA-ITEM" && read(fresh) == "PA-NEW" && items() == 3, "both served");
    op_revert_owner(playeranim);
    check(read(graph) == merged && by_handle(kGraphHandle) == merged, "the pinned bytes stay served");
    check(read(item) == "GAME-ITEM" && read(fresh) == "<result 2>" && by_handle(kItemHandle) == "<not served>" && items() == 1,
          "its other resources are gone");
    check(put(mod, graph, "MOD-GRAPH", KILN_ASSET_PUT_OVERRIDE) == KILN_ERR_UNAVAILABLE && read(graph) == merged, "the pin outlives the revert");

    // 6. What is not reserved: the reservation is the one key.
    std::puts("-- 6: neighbours of the key");
    check(!player_graph_key(other_graph) && !player_graph_key(graph_guid_item) && !player_graph_key(graph_part1) && player_graph_key(graph),
          "player_graph_key matches the graph key only");
    check(put(mod, other_graph, "MOD-OTHER-GRAPH", 0) == KILN_OK && read(other_graph) == "MOD-OTHER-GRAPH" &&
              by_handle(kOtherGraphHandle) == "MOD-OTHER-GRAPH",
          "another key of the graph type: put and served");
    check(put(mod, graph_guid_item, "MOD-SAME-GUID", KILN_ASSET_PUT_NEW) == KILN_OK && read(graph_guid_item) == "MOD-SAME-GUID",
          "the graph's GUID with another type: put and served");
    check(put(mod, graph_part1, "MOD-PART-1", 0) == KILN_OK && read(graph_part1) == "MOD-PART-1", "the graph's GUID and type, part 1: put and served");
    check(remove(mod, other_graph) == KILN_OK && remove(mod, graph_guid_item) == KILN_OK && remove(mod, graph_part1) == KILN_OK &&
              read(other_graph) == "GAME-OTHER-GRAPH" && read(graph_guid_item) == "<result 2>" && read(graph_part1) == "<result 2>",
          "and removed again");
    check(read(graph) == merged && items() == 1, "the pinned graph unchanged");

    // 7. The index is built again after 64 new keys (the delta): the pinned bytes come through it.
    std::puts("-- 7: more than 64 puts after the pin");
    const ResSnap* base_before;
    {
        ReadGuard g;
        base_before = g_res.load()->base;
    }
    bool all = true;
    for (int i = 0; i < 70; ++i) {
        char guid[40];
        std::snprintf(guid, sizeof guid, "55555555-0000-0000-0000-%012d", i);
        all = put(mod, key(guid, other_type), "MOD-NEW-" + std::to_string(i), KILN_ASSET_PUT_NEW) == KILN_OK && all;
    }
    bool rebuilt, in_base;
    {
        ReadGuard g;
        const ResView* v = g_res.load();
        rebuilt = v->base != base_before && v->delta->keys.size() < 64;
        in_base = v->base->keys.count(graph) == 1;
    }
    check(all && items() == 71, "70 new keys put");
    check(rebuilt, "the base was built again");
    check(in_base && read(graph) == merged && by_handle(kGraphHandle) == merged, "the pinned bytes are in it and served");
    check(put(mod, graph, "MOD-GRAPH", KILN_ASSET_PUT_OVERRIDE) == KILN_ERR_UNAVAILABLE && read(graph) == merged, "and still take no put");

    // 8. A pin of any key: refused while another owner has an item of it (the later item would be served), then holds.
    std::puts("-- 8: a pin over another owner's item");
    check(put(mod, item, "MOD-ITEM", KILN_ASSET_PUT_OVERRIDE) == KILN_OK, "another owner overrides a game key");
    mark = g_log.size();
    check(put(playeranim, item, "PA-ITEM", KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN) == KILN_ERR_UNAVAILABLE &&
              logged(mark, "22222222-3333-4444-5555-666666666666", "pin refused"),
          "a pin of it is refused, logged with the key");
    check(read(item) == "MOD-ITEM" && by_handle(kItemHandle) == "MOD-ITEM", "the other owner's bytes are served");
    check(remove(mod, item) == KILN_OK && put(playeranim, item, "PA-ITEM", KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN) == KILN_OK,
          "once it is gone, the pin is taken");
    check(put(mod, item, "MOD-ITEM", KILN_ASSET_PUT_OVERRIDE) == KILN_ERR_UNAVAILABLE && read(item) == "PA-ITEM", "and holds");

    // 9. The icon calls are puts and removals of a UI texture key too.
    std::puts("-- 9: icons on a pinned UI texture key");
    check(put(playeranim, icon, "PA-ICON", KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN) == KILN_OK && read(icon) == "PA-ICON", "a UI texture key pinned");
    g_client = true;
    g_fake_ui = true;
    Rgba img;
    img.w = img.h = 1;
    img.px = {1, 2, 3, 4};
    check(op_icon_register(mod, icon.g, img) == KILN_ERR_UNAVAILABLE, "icon_register of it refused");
    op_icon_remove(playeranim, icon.g);
    check(read(icon) == "PA-ICON" && by_handle(kIconHandle) == "PA-ICON", "icon_remove leaves it; the pinned bytes are served");

    // 10. What a dedicated server skips of an assets.json, by what the entry is (no mark in the file needed).
    std::puts("-- 10: the manifest entries only a game client uses");
    const uint32_t model = type_hash("keen::RenderModel", &ok), tmpl = type_hash("keen::ecs::TemplateResource", &ok), clip = type_hash("keen::JointAnimation", &ok);
    check(model == 0x177394c7u && client_only_type(model) && client_only_type(type_hash("0x177394C7", &ok)), "a RenderModel, by name and by hex");
    check(client_only_type(type_hash("keen::RenderMaterialResource", &ok)) && client_only_type(type_hash("keen::RenderTextureResource", &ok)) &&
              client_only_type(type_hash("keen::UiTextureResource", &ok)) && client_only_type(KILN_ASSET_TYPE_UI_TEXTURE),
          "materials, textures and UI textures, each by its name's hash");
    check(!client_only_type(tmpl) && !client_only_type(clip) && !client_only_type(type_hash("keen::ImpactProgram", &ok)) &&
              !client_only_type(type_hash("keen::ModelHierarchyResource", &ok)) && !client_only_type(type_hash("keen::SoundResource", &ok)) &&
              !client_only_type(kPlayerGraphType) && !client_only_type(0),
          "what a server serves is not client-only");
    const std::vector<ManifestItem> entries = {
        {"blob", "cooked/blade.blob", 0, {}},                         // 0 a model's render data
        {"resource", "cooked/blade.bin", model, {"cooked/blade.blob"}},  // 1 the model that patches it in
        {"resource", "cooked/visual.bin", tmpl, {}},                  // 2 a visual template: both sides
        {"resource", "cooked/clip.bin", clip, {}},                    // 3 a clip: both sides
        {"blob", "cooked/shared.blob", 0, {}},                        // 4 patched into a model and into a template: a server needs it
        {"resource", "cooked/m2.bin", model, {"cooked\\shared.blob"}},  // 5
        {"resource", "cooked/t2.bin", tmpl, {"cooked/shared.blob"}},  // 6
        {"blob", "cooked/alone.blob", 0, {}},                         // 7 named by no resource: loaded
        {"blob", "", 0, {}},                                          // 8 a hash-only blob
        {"icon", "icons/a.png", 0, {}},                               // 9 the loader skips an icon by its kind
        {"resource", "cooked/Blade.bin", model, {"cooked/Blade.blob"}},  // 10 another spelling is another file here
    };
    check(client_only_entries(entries) == std::vector<bool>{true, true, false, false, false, true, false, false, false, false, true},
          "a model and the blob only it patches in; a template, a clip, a shared blob and a blob of its own are loaded");
    check(client_only_entries({}).empty(), "an empty manifest");
    // a blob that overrides the game's content of a fixed hash: a server mod may read that hash, so the server keeps the override even
    // when a model patches the same file in (only "side": "client" in the manifest keeps it from a server)
    const std::vector<ManifestItem> fixed = {
        {"blob", "cooked/shared.blob", 0, {}, true},
        {"resource", "cooked/blade.bin", model, {"cooked/shared.blob"}, false},
        {"blob", "cooked/own.blob", 0, {}, false},
        {"resource", "cooked/other.bin", model, {"cooked/own.blob"}, false},
    };
    check(client_only_entries(fixed) == std::vector<bool>{false, true, true, true}, "a blob with a fixed hash is kept for the server, the model and a blob only it uses are not");

    // 11. Blob provenance (0.2.0-dev.28): every registration keeps owner, origin, sequence and override; which bytes are served is
    // decided as before (the last registration of a hash; an owner's second one replaces its first in place).
    std::puts("-- 11: blob provenance and the served winner");
    blob_provenance(manifest, mod, playeranim);

    // 12. The content blob catalog (0.2.0-dev.28).
    std::puts("-- 12: the content blob catalog");
    blob_catalog(manifest, mod, playeranim);

    // 13. A native owner the core revokes without revert_owner (0.2.0-dev.28): its registrations and served bytes go, deferred.
    std::puts("-- 13: an owner revoked without revert_owner");
    const Key spare = key("66666666-7777-8888-9999-aaaaaaaaaaaa", other_type);
    g_game[spare] = {104, "GAME-SPARE"};
    owner_revoked_without_revert(40, spare, 104, "GAME-SPARE", key("88888888-9999-aaaa-bbbb-cccccccccccc", KILN_ASSET_TYPE_UI_TEXTURE));
    settle();

    std::printf("%d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
