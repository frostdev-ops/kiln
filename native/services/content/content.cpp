// SPDX-License-Identifier: MIT
// kiln.content: Forge-style registries and data packs for Enshrouded's server.
//
// At boot the service resolves its symbols (sigdb/symbols/server.d/content.json) and hooks the default-inventory
// resource acquire (drop tables). The registries are the server world's, else (a game client joined to another
// machine's game) the client world's own; when that world stops, every layer comes off and the service waits for the
// next one. In Pre (of that world) it waits until the world's registries have loaded (item registry not
// loading, recipe resource present), snapshots them with layouts from the game's reflection, applies every mod's
// content/*.json in mod-folder order, then calls the ready subscribers (Kiln.Content raises ContentReadyEvent there).
// Afterwards it watches the pack files (~2 s), and reverts the layers of owners that went away (~1 s).
// See native/core/abi/services/kiln_content.h and docs/re/content.md.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <unordered_set>

#include "content.h"
#include "refs.h"
#if KC_GAMEDATA
#include "gamedata.h"
#endif
#include "kiln_boot_collector.hpp"
#include "kiln_hook.hpp"

using namespace kc;

static_assert(sizeof(KilnContentValue) == 40 && offsetof(KilnContentValue, guid) == 24);
static_assert(sizeof(KilnContentField) == 56 && offsetof(KilnContentField, enum_count) == 52);
static_assert(sizeof(KilnContentEntry) == 56 && offsetof(KilnContentEntry, guid) == 40);
static_assert(sizeof(KilnContentStack) == 32);
static_assert(sizeof(KilnContentStatus) == 208 && offsetof(KilnContentStatus, detail) == 48);

namespace kc {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;

void logf(int level, const char* fmt, ...) {
    char b[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    if (g_api && n > 0) g_api->log(g_owner, level, KilnStr{b, static_cast<size_t>(std::min(n, int(sizeof b) - 1))});
}
}  // namespace kc

namespace {

constexpr const char* kName = "kiln.content";

std::atomic<bool> g_ready{false};
std::atomic<uint64_t> g_step{0};
std::atomic<uint64_t> g_generation{0};
uint64_t g_first_step = 0;
std::chrono::steady_clock::time_point g_first_time;
uint32_t g_last_item_count = 0, g_stable = 0;
uint64_t g_wait_since = 0;
std::string g_pack_sig;
std::string g_report = "{}";
uint32_t g_pack_count = 0, g_pack_errors = 0, g_conflicts = 0;
std::mutex g_detail_mu;
char g_detail[160] = "waiting for a world";

// The world whose registries the table works on (docs: kiln_content.h "Which world"): the server world, else, in a
// client joined to another machine's game, the client world. Its thread is the only one the table accepts.
std::atomic<uint32_t> g_kind{KILN_WORLD_SERVER};
std::atomic<uintptr_t> g_world{0};  // set when the registries are ready, 0 after a reset
const char* kind_name(uint32_t k) { return k == KILN_WORLD_CLIENT ? "client" : "server"; }
}  // namespace
uintptr_t kc::active_world() { return g_world.load(); }
uint32_t kc::active_kind() { return g_kind.load(); }
namespace {

void set_detail(const char* s) {
    std::lock_guard lock(g_detail_mu);
    std::snprintf(g_detail, sizeof g_detail, "%s", s);
}

// ---- ready subscribers ----
struct ReadySub {
    KilnHandle handle;
    KilnOwner owner;
    KilnContentReadyFn fn;
    void* user;
    bool pending;
};
std::mutex g_sub_mu;
std::vector<ReadySub> g_subs;
std::atomic<uint64_t> g_next_handle{1};
uint32_t g_pending_reason = KILN_CONTENT_WORLD_LOADED;

bool call_ready(KilnContentReadyFn fn, uint64_t gen, uint32_t reason, void* user, unsigned long* code) {
    __try {
        fn(gen, reason, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void deliver_ready() {
    std::vector<ReadySub> todo;
    {
        std::lock_guard lock(g_sub_mu);
        for (ReadySub& s : g_subs)
            if (s.pending) {
                todo.push_back(s);
                s.pending = false;
            }
    }
    for (const ReadySub& s : todo) {
        unsigned long code = 0;
        if (!call_ready(s.fn, g_generation.load(), g_pending_reason, s.user, &code)) {
            LOGE("a ready callback (%s) faulted (0x%08lx); removed", owner_label(s.owner).c_str(), code);
            std::lock_guard lock(g_sub_mu);
            std::erase_if(g_subs, [&](const ReadySub& x) { return x.handle == s.handle; });
        }
    }
}

void mark_all_pending(uint32_t reason) {
    std::lock_guard lock(g_sub_mu);
    g_pending_reason = reason;
    for (ReadySub& s : g_subs) s.pending = true;
}

// ---- modified entries (for KilnContentEntry.flags) ----
std::unordered_set<uint64_t> g_modified;
bool g_modified_stale = true;
void refresh_modified() {
    if (!g_modified_stale) return;
    g_modified.clear();
    store_for_each([](uintptr_t, const Slot& s) {
        if (std::memcmp(s.current, s.original, s.n) != 0) g_modified.insert((uint64_t(s.kind) << 32) | s.id);
    });
    g_modified_stale = false;
}

// ---- packs ----
void log_report(const PacksReport& rep, double ms, bool reload) {
    uint32_t applied = 0, errors = 0;
    for (auto& [rel, r] : rep.files) {
        applied += r.applied;
        errors += static_cast<uint32_t>(r.errors.size());
        LOGI("pack %s: %u change(s), %zu error(s), %zu warning(s)", rel.c_str(), r.applied, r.errors.size(), r.warnings.size());
        for (size_t i = 0; i < r.errors.size() && i < 12; ++i) LOGW("pack %s: %s", rel.c_str(), r.errors[i].c_str());
        for (size_t i = 0; i < r.warnings.size() && i < 6; ++i) LOGI("pack %s: %s", rel.c_str(), r.warnings[i].c_str());
    }
    for (size_t i = 0; i < rep.conflicts.size() && i < 20; ++i) {
        const Conflict& c = rep.conflicts[i];
        LOGW("conflict: %s: %s sets %s, %s sets %s (the later one wins: %s)", c.what.c_str(), c.loser.c_str(),
             c.loser_value.c_str(), c.winner.c_str(), c.winner_value.c_str(), c.winner.c_str());
    }
    if (rep.conflicts.size() > 20) LOGW("conflict: ... and %zu more (see report())", rep.conflicts.size() - 20);
    g_pack_count = static_cast<uint32_t>(rep.files.size());
    g_pack_errors = errors;
    g_conflicts = static_cast<uint32_t>(rep.conflicts.size());
    const WriteStats ws = write_stats();
    LOGI("data packs %s: %zu file(s), %u change(s), %u error(s), %zu conflict(s), %u field(s) differ from the game's "
         "data, stack guard %s, in %.1f ms (generation %llu); writes since boot: %llu to writable memory, %llu to "
         "read-only resource pages (%llu VirtualProtect pairs), %llu refused",
         reload ? "re-applied" : "applied", rep.files.size(), applied, errors, rep.conflicts.size(), store_changed(),
         guard_on() ? "on" : "off", ms, static_cast<unsigned long long>(rep.generation), (unsigned long long)ws.direct,
         (unsigned long long)ws.unprotected, (unsigned long long)ws.protect_calls, (unsigned long long)ws.refused);
}

// pack_mods: the loaded mods in load order (Kiln.Content), or none = folder order.
std::mutex g_filter_mu;
bool g_have_filter = false;
std::vector<std::string> g_filter;
std::atomic<bool> g_filter_changed{false};

// A timing report in lines of a length the log keeps whole.
void log_timing(const char* what, const std::string& text) {
    size_t at = 0;
    while (at < text.size()) {
        size_t end = text.size();
        if (end - at > 900) end = text.rfind(", ", at + 900);
        if (end == std::string::npos || end <= at) end = std::min(text.size(), at + 900);
        LOGI("%s: %s", what, text.substr(at, end - at).c_str());
        at = end < text.size() && text.compare(end, 2, ", ") == 0 ? end + 2 : end;
    }
}

void apply_packs(bool reload) {
    PhaseTimer pt(PH_APPLY_PACKS);
    const auto t0 = std::chrono::steady_clock::now();
    std::vector<PackFile> files = discover_packs(&g_pack_sig);
    std::vector<std::string> skipped;
    {
        std::lock_guard lock(g_filter_mu);
        if (g_have_filter) files = order_packs(files, g_filter, &skipped);
    }
    nc_packs_begin();  // new content: pack definitions not seen again in this application are orphaned
#if KC_GAMEDATA
    gd_packs_begin();
#endif
    PacksReport rep = apply_all_packs(files);
    nc_packs_end();
#if KC_GAMEDATA
    gd_packs_end();
#endif
    rep.skipped = skipped;
    for (const std::string& m : skipped)
        LOGW("data packs of %s skipped: Kiln.Host did not load the mod (its \"mod ... not loaded\" / \"skipped\" line above and kiln/logs/mods.txt say why)", m.c_str());
    g_report = report_json(rep);
    g_generation.store(rep.generation);
    g_modified_stale = true;
    log_report(rep, std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count(), reload);
    mark_all_pending(reload ? KILN_CONTENT_PACKS_RELOADED : KILN_CONTENT_WORLD_LOADED);
}

// ---- readiness ----
void try_ready(uint64_t step, uint32_t kind, uintptr_t world) {
    if (!world) return;
    g_kind.store(kind);
    if (!g_first_step) {
        log_timing("load timing, definitions before the world", timing_report(true));
        LOGI("store lock before the world: %s", lock_report(true).c_str());
        g_first_step = step;
        g_first_time = std::chrono::steady_clock::now();
        g_wait_since = step;
    }
    const uintptr_t ireg = g_api->singleton(world, KilnStr{"g38_items::ItemRegistry", 23});
    if (!ireg) return set_detail("waiting for g38_items::ItemRegistry");
    uint8_t loading = 1, started = 0;
    rdv(ireg + E.items_loading, loading);
    rdv(ireg + E.items_started, started);
    const uint32_t n = map_count(ireg + E.items_map);
    if (loading || n == 0) {
        g_stable = 0;
        return set_detail("the item registry is loading");
    }
    if (n != g_last_item_count) {
        g_last_item_count = n;
        g_stable = 0;
        return;
    }
    if (++g_stable < 2) return;
    if (E.recipes) {
        const uintptr_t rreg = g_api->singleton(world, KilnStr{"g38_items::RecipeRegistry", 25});
        uintptr_t res = 0;
        if (!rreg || !rdv(rreg + E.recipes_resource, res) || !res || map_count(rreg + E.recipes_map) == 0) {
            if (std::chrono::steady_clock::now() - g_first_time < std::chrono::seconds(30))
                return set_detail("waiting for the recipe registry");
            LOGW("the recipe registry did not load within 30 s; recipes off");
            E.recipes = false;
        }
    }
    if (!E.reflection) {
        set_detail("the game's reflection is not available on this build");
        return;
    }
    std::string why;
    const auto t0 = std::chrono::steady_clock::now();
    bool built = false;
    {
        PhaseTimer bt(PH_BUILD_REGISTRIES);
        built = build_registries(world, &why);
    }
    if (!built) {
        set_detail(why.c_str());
        static std::string last;
        if (why != last) LOGW("not ready: %s", why.c_str());
        last = why;
        return;
    }
    const double ms_build = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    const double since = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - g_first_time).count();
    g_world.store(world);
    nc_world_ready(world, kind);  // new items / recipes into this world's maps (and the snapshot), before the packs
#if KC_GAMEDATA
    gd_world_ready(world, kind);
#endif
    LOGI("registries ready at step %llu (%.0f ms after the %s world's first step): %zu items, %zu recipes (%zu input "
         "categories), %zu random-loot items, drop tables %s; snapshot in %.1f ms",
         static_cast<unsigned long long>(step), since, kind_name(kind), reg(KILN_CONTENT_ITEMS).entries.size(),
         reg(KILN_CONTENT_RECIPES).entries.size(), g_category_guid.size(), reg(KILN_CONTENT_LOOT).entries.size(),
         reg(KILN_CONTENT_DROPS).available ? "on (filled as the game loads them)" : "off", ms_build);
    // Where the data lives (evidence for docs/re/content.md): protection, type and size of each registry's region.
    for (uint32_t k = KILN_CONTENT_ITEMS; k <= KILN_CONTENT_LOOT; ++k) {
        const Registry& r = reg(k);
        if (r.entries.empty()) continue;
        MEMORY_BASIC_INFORMATION mbi{};
        if (VirtualQuery(reinterpret_cast<void*>(r.entries.front().addr), &mbi, sizeof mbi))
            LOGI("memory: %s data at %p: protect 0x%lx, %s, region %p + %.1f MB (allocation %p)", r.label,
                 reinterpret_cast<void*>(r.entries.front().addr), mbi.Protect,
                 mbi.Type == MEM_PRIVATE ? "MEM_PRIVATE" : mbi.Type == MEM_IMAGE ? "MEM_IMAGE" : "MEM_MAPPED", mbi.BaseAddress,
                 mbi.RegionSize / 1048576.0, mbi.AllocationBase);
    }
    apply_packs(false);
    g_ready.store(true);
    set_detail("ready");
    log_timing("load timing, world load", timing_report(true));
    LOGI("store lock at world load: %s", lock_report(true).c_str());
    // <kiln>/data/content/dump-on-start: write the registries once the packs are applied (for modders).
    const std::string marker = data_dir() + "\\content\\dump-on-start";
    if (path_exists(marker)) {
        const std::string p = data_dir() + "\\content\\dump.json";
        std::string err;
        const auto t1 = std::chrono::steady_clock::now();
        if (dump_all(p, &err))
            LOGI("dump: wrote %s in %.0f ms (dump-on-start)", p.c_str(),
                 std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t1).count());
        else
            LOGE("dump: %s", err.c_str());
    }
}

// ---- owners that went away ----
void KILN_CALL noop(void*) {}
std::unordered_set<KilnOwner> g_pack_owner_set;
void probe_owners() {
    std::vector<KilnOwner> owners = store_owners();
    {
        std::lock_guard lock(g_sub_mu);
        for (const ReadySub& s : g_subs) owners.push_back(s.owner);
    }
    std::sort(owners.begin(), owners.end());
    owners.erase(std::unique(owners.begin(), owners.end()), owners.end());
    for (KilnOwner o : owners) {
        if (o == 0 || o == g_owner || owner_label(o).rfind("pack ", 0) == 0) continue;
        if (g_api->post_world(o, g_kind.load(), &noop, nullptr) != KILN_ERR_REVOKED) continue;
        uint32_t n = 0;
        {
            std::lock_guard lock(store_mutex());
            n = store_drop_owner(o);
            drops_drop_owner(o);
#if KC_GAMEDATA
            gd_owner_gone(o);
#endif
            std::string err;
            store_flush(&err);
            g_modified_stale = true;
        }
        {
            std::lock_guard lock(g_sub_mu);
            std::erase_if(g_subs, [&](const ReadySub& s) { return s.owner == o; });
        }
        if (n) LOGI("%s went away: %u field(s) put back", owner_label(o).c_str(), n);
    }
}

// ---- the world goes away ----
std::mutex g_seen_mu;
std::unordered_set<uintptr_t> g_seen;

// Takes every layer off (revert: writes the originals back; only while the world is intact) and forgets the snapshot,
// so the next world (a new hosted session, a join) is read and patched from scratch. Ready subscribers run again then.
void world_reset(bool revert, const char* why) {
    nc_world_stopping(g_world.load());
    refs_reset();
    const uintptr_t w = g_world.exchange(0);
    const bool was_ready = g_ready.exchange(false);
    uint32_t n = 0;
    std::string err;
    {
        std::lock_guard lock(store_mutex());
        n = store_reset(revert, &err);
        drops_clear_rules();
        Registry& D = reg(KILN_CONTENT_DROPS);
        D.entries.clear(), D.by_id.clear(), D.by_name.clear(), D.by_lower.clear(), D.by_addr.clear();
        g_modified.clear();
        g_modified_stale = true;
    }
#if KC_GAMEDATA
    gd_world_reset();
#endif
    {
        std::lock_guard lock(g_seen_mu);
        g_seen.clear();
    }
    g_first_step = 0, g_last_item_count = 0, g_stable = 0, g_wait_since = 0;
    set_detail("waiting for a world");
    if (was_ready || n)
        LOGI("%s world %p %s: %u changed field(s) %s; waiting for the next world%s%s", kind_name(g_kind.load()),
             reinterpret_cast<void*>(w), why, n, revert ? "put back" : "dropped (not written: the world is gone)",
             err.empty() ? "" : "; ", err.c_str());
}

void KILN_CALL on_world_event(const KilnWorldEvent* ev, void*) {
    if (!ev || ev->event != KILN_WORLD_STOPPING || ev->kind != g_kind.load()) return;
    const uintptr_t w = g_world.load();
    if (w && ev->world == w) world_reset(true, "is stopping");
    else if (!w && g_first_step) world_reset(false, "stopped before its registries were ready");
}

// The pack-file change scan stats every mod folder and pack file and reads each kiln.mod.json (file system and, under
// Wine, wineserver work): every 2 s by the clock (as 50 steps were on a dedicated server), not by step count, which
// ran it 2.5x as often on a 60 Hz hosted or joined world.
bool pack_scan_due() {
    static std::atomic<uint64_t> next{0};
    const uint64_t now = GetTickCount64();
    uint64_t due = next.load(std::memory_order_relaxed);
    return now >= due && next.compare_exchange_strong(due, now + 2000, std::memory_order_relaxed);
}

void pre(const KilnTickInfo* tick, uint32_t kind, uintptr_t world) {
    g_step.store(tick->step);
    if (g_ready.load() && (g_kind.load() != kind || g_world.load() != world)) {
        LOGW("a new %s world without a stop of the last one (no world.lifecycle?): starting over", kind_name(kind));
        world_reset(false, "was replaced");
    }
    if (!g_ready.load()) {
        if ((tick->step & 3) == 0) try_ready(tick->step, kind, world);
        if (g_ready.load()) deliver_ready();
        return;
    }
    if (tick->step % 25 == 0) probe_owners();
    if (tick->step % 250 == 0) {
        const std::string t = timing_report(true);
        if (!t.empty()) {
            LOGD("timing, the last 250 steps: %s", t.c_str());
            LOGD("store lock, the last 250 steps: %s", lock_report(true).c_str());
        }
    }
    if (g_filter_changed.exchange(false)) {
        LOGI("the set of loaded mods changed: re-applying data packs");
        apply_packs(true);
    } else if (pack_scan_due()) {
        std::string sig;
        discover_packs(&sig);
        if (sig != g_pack_sig) {
            LOGI("data pack files changed: re-applying");
            apply_packs(true);
        }
    }
#if KC_GAMEDATA
    gd_tick(tick->step);
#endif
    deliver_ready();
}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) { pre(tick, KILN_WORLD_SERVER, g_api->server_world()); }

// A client's own world: drives the service only while no server world runs here (a joined game, not hosting).
void KILN_CALL on_client_pre(const KilnTickInfo* tick, void*) {
    if (g_api->world(KILN_WORLD_SERVER)) return;
    pre(tick, KILN_WORLD_CLIENT, g_api->world(KILN_WORLD_CLIENT));
}

// ---- drop tables: the game's DefaultInventoryResource acquire ----
kiln::RawHook g_acquire_hook;

void note_table(const uint8_t guid[16], uintptr_t data) {
    {
        std::lock_guard lock(g_seen_mu);
        if (!g_seen.insert(data).second) return;
    }
    const bool job = !g_api->on_world_thread(g_kind.load());
    timing_mark_job_thread(job);
    PhaseTimer pt(PH_DROP_TABLE);
    std::lock_guard lock(store_mutex());
    timing_mark_job_thread(false);
    Registry& D = reg(KILN_CONTENT_DROPS);
    Entry e;
    std::memcpy(e.guid, guid, 16);
    e.id = fnv1a(guid, 16);
    e.addr = data;
    e.name = guid_text(guid);
    if (D.get(e.id)) return;
    D.add(e);
    if (drops_have_rules()) drops_apply_rules(D.entries.back());
    nc_drops_table(D.entries.back());  // new items' stacks (definitions with "drops")
    std::string err;
    store_flush(&err);
    g_modified_stale = true;
    if (!err.empty()) LOGW("drop table %s: %s", e.name.c_str(), err.c_str());
}

void* __fastcall hk_acquire(uint8_t* out, void* rs, const uint8_t* key) {
    uint8_t g[16];
    const bool have = key && rd(reinterpret_cast<uintptr_t>(key), g, 16);
    void* r = g_acquire_hook.call<void*>(out, rs, key);
    uintptr_t data = 0;
    if (have && g_ready.load(std::memory_order_relaxed) && rdv(reinterpret_cast<uintptr_t>(out) + 8, data) && data)
        note_table(g, data);
    return r;
}

// ============================================================================================= the table ======

bool tick() { return g_api->on_world_thread(g_kind.load()); }
std::string str(KilnStr s) { return s.ptr && s.len ? std::string(s.ptr, s.len) : std::string(); }

KilnResult common(uint32_t kind) {
    if (!valid_kind(kind)) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick()) return KILN_ERR_WRONG_THREAD;
    if (!g_ready.load() || !reg(kind).available) return KILN_ERR_UNAVAILABLE;
    return KILN_OK;
}

void fill_entry(const Registry& r, const Entry& e, KilnContentEntry* out) {
    refresh_modified();
    out->size = sizeof *out;
    out->kind = r.kind;
    out->id = e.id;
    out->flags = g_modified.count((uint64_t(r.kind) << 32) | e.id) ? KILN_CONTENT_MODIFIED : 0;
    out->name = KilnStr{e.name.data(), e.name.size()};
    out->address = e.addr;
    std::memcpy(out->guid, e.guid, 16);
}

KilnResult KILN_CALL t_status(KilnContentStatus* out) {
    PhaseTimer pt(PH_STATUS);
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->ready = g_ready.load();
    if (out->ready) {
        std::lock_guard lock(store_mutex());
        out->registries = (reg(1).available ? KILN_CONTENT_HAS_ITEMS : 0) | (reg(2).available ? KILN_CONTENT_HAS_RECIPES : 0) |
                          (reg(3).available ? KILN_CONTENT_HAS_LOOT : 0) | (reg(4).available ? KILN_CONTENT_HAS_DROPS : 0);
        out->items = static_cast<uint32_t>(reg(1).entries.size());
        out->recipes = static_cast<uint32_t>(reg(2).entries.size());
        out->loot = static_cast<uint32_t>(reg(3).entries.size());
        out->drops = static_cast<uint32_t>(reg(4).entries.size());
        // both walk the whole store: counted again only after it changed (mods ask often)
        static uint64_t gen = ~0ull;
        static uint32_t changed = 0, conflicts = 0;
        if (store_generation() != gen) {
            gen = store_generation();
            changed = store_changed();
            conflicts = static_cast<uint32_t>(store_conflicts().size());
        }
        out->changed = changed;
        out->conflicts = conflicts;
    }
    out->stack_guard = guard_on();
    out->reload_pending = g_filter_changed.load();
    out->packs = g_pack_count;
    out->pack_errors = g_pack_errors;
    out->generation = g_generation.load();
    std::lock_guard lock(g_detail_mu);
    std::snprintf(out->detail, sizeof out->detail, "%s", g_detail);
    return KILN_OK;
}

uint32_t KILN_CALL t_count(uint32_t kind) {
    if (common(kind) != KILN_OK) return 0;
    std::lock_guard lock(store_mutex());
    return static_cast<uint32_t>(reg(kind).entries.size());
}

KilnResult KILN_CALL t_entry_at(uint32_t kind, uint32_t index, KilnContentEntry* out) {
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = common(kind)) return rc;
    std::lock_guard lock(store_mutex());
    const Registry& r = reg(kind);
    if (index >= r.entries.size()) return KILN_ERR_NOT_FOUND;
    fill_entry(r, r.entries[index], out);
    return KILN_OK;
}

KilnResult KILN_CALL t_find(uint32_t kind, KilnStr key, KilnContentEntry* out) {
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = common(kind)) return rc;
    std::lock_guard lock(store_mutex());
    const Registry& r = reg(kind);
    const Entry* e = r.lookup(str(key));
    if (!e) return KILN_ERR_NOT_FOUND;
    fill_entry(r, *e, out);
    return KILN_OK;
}

uint32_t KILN_CALL t_field_count(uint32_t kind) {
    if (!valid_kind(kind) || !g_ready.load()) return 0;
    return static_cast<uint32_t>(reg(kind).schema.fields.size());
}

void fill_field(const Field& f, uint32_t index, KilnContentField* out) {
    out->size = sizeof *out;
    out->index = index;
    out->path = KilnStr{f.path.data(), f.path.size()};
    out->type_name = KilnStr{f.type_name.data(), f.type_name.size()};
    out->offset = f.offset;
    out->bytes = f.bytes;
    out->value_type = f.vt;
    out->is_signed = f.is_signed;
    out->writable = f.writable;
    out->enum_kind = f.enum_kind;
    out->reserved[0] = f.ref ? KILN_CONTENT_FIELD_REF : 0;
    out->enum_count = f.en ? static_cast<uint32_t>(f.en->values.size()) : 0;
}

KilnResult KILN_CALL t_field_at(uint32_t kind, uint32_t index, KilnContentField* out) {
    if (!out || out->size < sizeof *out || !valid_kind(kind)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_ready.load()) return KILN_ERR_UNAVAILABLE;
    const Schema& s = reg(kind).schema;
    if (index >= s.fields.size()) return KILN_ERR_NOT_FOUND;
    fill_field(s.fields[index], index, out);
    return KILN_OK;
}

KilnResult KILN_CALL t_field_find(uint32_t kind, KilnStr path, KilnContentField* out) {
    if (!out || out->size < sizeof *out || !valid_kind(kind)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_ready.load()) return KILN_ERR_UNAVAILABLE;
    const Schema& s = reg(kind).schema;
    const Field* f = s.find(str(path));
    if (!f) return KILN_ERR_NOT_FOUND;
    fill_field(*f, static_cast<uint32_t>(f - s.fields.data()), out);
    return KILN_OK;
}

KilnResult KILN_CALL t_enum_name(uint32_t kind, uint32_t field, uint32_t index, KilnStr* name, int64_t* value) {
    if (!valid_kind(kind) || !g_ready.load()) return KILN_ERR_UNAVAILABLE;
    const Schema& s = reg(kind).schema;
    if (field >= s.fields.size()) return KILN_ERR_NOT_FOUND;
    const Field& f = s.fields[field];
    if (!f.en || index >= f.en->values.size()) return KILN_ERR_NOT_FOUND;
    if (name) *name = KilnStr{f.en->values[index].first.data(), f.en->values[index].first.size()};
    if (value) *value = f.en->values[index].second;
    return KILN_OK;
}

// Resolves (kind, id, path) to an address-bearing pseudo entry + field. Drops: "[i].field" addresses stack i.
KilnResult locate(uint32_t kind, uint32_t id, const std::string& path, Entry* e, const Field** f, int32_t* sub) {
    const Registry& r = reg(kind);
    const Entry* en = r.get(id);
    if (!en) return KILN_ERR_NOT_FOUND;
    *e = *en;
    *sub = -1;
    std::string p = path;
    if (kind == KILN_CONTENT_DROPS) {
        unsigned idx = 0;
        int used = 0;
        if (std::sscanf(p.c_str(), "[%u].%n", &idx, &used) != 1 || used == 0) return KILN_ERR_INVALID_ARGUMENT;
        std::vector<DropStackRef> refs;
        walk_drop_table(en->addr, &refs);
        if (idx >= refs.size()) return KILN_ERR_NOT_FOUND;
        e->addr = refs[idx].addr;
        *sub = static_cast<int32_t>(idx);
        p = p.substr(static_cast<size_t>(used));
    }
    *f = r.schema.find(p);
    return *f ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_get(uint32_t kind, uint32_t id, KilnStr path, KilnContentValue* cur, KilnContentValue* orig) {
    if (KilnResult rc = common(kind)) return rc;
    std::lock_guard lock(store_mutex());
    Entry e;
    const Field* f = nullptr;
    int32_t sub = -1;
    if (KilnResult rc = locate(kind, id, str(path), &e, &f, &sub)) return rc;
    if (cur) *cur = current_value(e, *f);
    if (orig) *orig = original_value(e, *f);
    return KILN_OK;
}

KilnResult KILN_CALL t_set(KilnOwner owner, uint32_t kind, uint32_t id, KilnStr path, const KilnContentValue* v) {
    if (!v) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = common(kind)) return rc;
    std::lock_guard lock(store_mutex());
    Entry e;
    const Field* f = nullptr;
    int32_t sub = -1;
    const std::string p = str(path);
    if (KilnResult rc = locate(kind, id, p, &e, &f, &sub)) return rc;
    std::string why;
    const KilnResult rc = set_field(owner, next_code_seq(), kind, e, *f, *v,
                                    owner_label(owner) + ": " + reg(kind).label + " " + reg(kind).get(id)->name + "." + p, &why, sub);
    if (rc != KILN_OK) {
        LOGD("set %s.%s refused: %s", reg(kind).get(id)->name.c_str(), p.c_str(), why.c_str());
        return rc;
    }
    std::string err;
    store_flush(&err);
    if (!err.empty()) LOGW("set %s.%s: %s", reg(kind).get(id)->name.c_str(), p.c_str(), err.c_str());
    g_modified_stale = true;
    return KILN_OK;
}

KilnResult KILN_CALL t_reset(KilnOwner owner, uint32_t kind, uint32_t id, KilnStr path) {
    if (KilnResult rc = common(kind)) return rc;
    std::lock_guard lock(store_mutex());
    Entry e;
    const Field* f = nullptr;
    int32_t sub = -1;
    if (KilnResult rc = locate(kind, id, str(path), &e, &f, &sub)) return rc;
    const bool had = store_drop(owner, e.addr + f->offset);
    std::string err;
    store_flush(&err);
    g_modified_stale = true;
    return had ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_stacks(uint32_t kind, uint32_t id, uint32_t which, KilnContentStack* out, uint32_t cap,
                              uint32_t* out_count) {
    if (out_count) *out_count = 0;
    if (cap && !out) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = common(kind)) return rc;
    std::lock_guard lock(store_mutex());
    const Entry* e = reg(kind).get(id);
    if (!e) return KILN_ERR_NOT_FOUND;
    std::vector<KilnContentStack> st;
    if (kind == KILN_CONTENT_RECIPES && which <= KILN_CONTENT_OUTPUTS) {
        if (!read_recipe_stacks(*e, which, &st)) return KILN_ERR_INTERNAL;
    } else if (kind == KILN_CONTENT_DROPS && which == KILN_CONTENT_DROP_STACKS) {
        std::vector<DropStackRef> refs;
        walk_drop_table(e->addr, &refs);
        st.resize(refs.size());
        for (size_t i = 0; i < refs.size(); ++i) read_drop_stack(refs[i].addr, refs[i].group, &st[i]);
    } else {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    for (uint32_t i = 0; i < st.size() && i < cap; ++i) out[i] = st[i];
    if (out_count) *out_count = static_cast<uint32_t>(st.size());
    return st.size() > cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_set_stacks(KilnOwner owner, uint32_t kind, uint32_t id, uint32_t which, const KilnContentStack* s,
                                  uint32_t n) {
    if (n && !s) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = common(kind)) return rc;
    std::lock_guard lock(store_mutex());
    const Entry* e = reg(kind).get(id);
    if (!e) return KILN_ERR_NOT_FOUND;
    std::vector<KilnContentStack> v(s, s + n);
    std::string why;
    KilnResult rc;
    const std::string src = owner_label(owner) + ": " + reg(kind).label + " " + e->name;
    if (kind == KILN_CONTENT_RECIPES && which <= KILN_CONTENT_OUTPUTS)
        rc = set_recipe_stacks(owner, next_code_seq(), *e, which, v, src + (which ? ".outputs" : ".inputs"), &why);
    else if (kind == KILN_CONTENT_DROPS && which == KILN_CONTENT_DROP_STACKS)
        rc = set_drop_stacks(owner, next_code_seq(), *e, v, src + ".stacks", &why);
    else
        return KILN_ERR_INVALID_ARGUMENT;
    if (rc != KILN_OK) {
        LOGD("set_stacks %s refused: %s", e->name.c_str(), why.c_str());
        return rc;
    }
    std::string err;
    store_flush(&err);
    g_modified_stale = true;
    return err.empty() ? KILN_OK : KILN_ERR_INTERNAL;
}

void copy_out(const std::string& s, char* buf, size_t cap, size_t* out_len) {
    if (out_len) *out_len = s.size();
    if (!buf || !cap) return;
    const size_t n = std::min(s.size(), cap - 1);
    std::memcpy(buf, s.data(), n);
    buf[n] = 0;
}

KilnResult KILN_CALL t_apply_json(KilnOwner owner, KilnStr text, KilnStr source, char* report, size_t cap, size_t* len) {
    PhaseTimer pt(PH_APPLY_JSON);
    if (!text.ptr) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick()) return KILN_ERR_WRONG_THREAD;
    if (!g_ready.load()) return KILN_ERR_UNAVAILABLE;
    ApplyResult r;
    std::string src = str(source);
    if (src.empty()) src = owner_label(owner);
    bool ok;
    {
        std::lock_guard lock(store_mutex());
        ok = apply_pack(owner, 0, str(text), src, &r);
        g_modified_stale = true;
    }
    copy_out(apply_result_json(r), report, cap, len);
    if (!ok) return KILN_ERR_INVALID_ARGUMENT;
    const WriteStats ws = write_stats();
    LOGI("%s: %u change(s), %zu error(s) (writes since boot: %llu writable, %llu read-only pages, %llu VirtualProtect pairs)",
         src.c_str(), r.applied, r.errors.size(), (unsigned long long)ws.direct, (unsigned long long)ws.unprotected,
         (unsigned long long)ws.protect_calls);
    for (size_t i = 0; i < r.errors.size() && i < 8; ++i) LOGW("%s: %s", src.c_str(), r.errors[i].c_str());
    return KILN_OK;
}

uint32_t KILN_CALL t_revert_owner(KilnOwner owner) {
    if (!tick() || owner == 0) return 0;
    std::lock_guard lock(store_mutex());
    const uint32_t n = store_drop_owner(owner);
    drops_drop_owner(owner);
#if KC_GAMEDATA
    gd_owner_gone(owner);
#endif
    std::string err;
    store_flush(&err);
    g_modified_stale = true;
    if (n) LOGI("%s reverted: %u field(s) put back", owner_label(owner).c_str(), n);
    return n;
}

KilnResult KILN_CALL t_dump(KilnStr path, char* written, size_t cap, size_t* len) {
    if (!tick()) return KILN_ERR_WRONG_THREAD;
    if (!g_ready.load()) return KILN_ERR_UNAVAILABLE;
    std::string p = str(path);
    if (p.empty()) p = data_dir() + "\\content\\dump.json";
    const auto t0 = std::chrono::steady_clock::now();
    std::string err;
    if (!dump_all(p, &err)) {
        LOGE("dump: %s", err.c_str());
        return KILN_ERR_INTERNAL;
    }
    LOGI("dump: wrote %s in %.0f ms", p.c_str(),
         std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
    copy_out(p, written, cap, len);
    return KILN_OK;
}

KilnResult KILN_CALL t_reload_packs() {
    if (!tick()) return KILN_ERR_WRONG_THREAD;
    if (!g_ready.load()) return KILN_ERR_UNAVAILABLE;
    apply_packs(true);
    return KILN_OK;
}

KilnResult KILN_CALL t_report(char* buf, size_t cap, size_t* len) {
    if (!tick()) return KILN_ERR_WRONG_THREAD;
    copy_out(g_report, buf, cap, len);
    return KILN_OK;
}

KilnResult KILN_CALL t_subscribe_ready(KilnOwner owner, KilnContentReadyFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_sub_mu);
    const KilnHandle h = g_next_handle.fetch_add(1);
    g_subs.push_back(ReadySub{h, owner, fn, user, g_ready.load()});
    if (out) *out = h;
    return KILN_OK;
}

KilnResult KILN_CALL t_unsubscribe_ready(KilnHandle h) {
    std::lock_guard lock(g_sub_mu);
    const size_t before = g_subs.size();
    std::erase_if(g_subs, [&](const ReadySub& s) { return s.handle == h; });
    return g_subs.size() != before ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_stack_guard(int32_t mode, uint8_t* out_on) {
    if (mode >= 0) {
        if (!tick()) return KILN_ERR_WRONG_THREAD;
        std::string why;
        if (!guard_set(mode != 0, &why)) {
            if (out_on) *out_on = guard_on();
            return KILN_ERR_UNAVAILABLE;
        }
    }
    if (out_on) *out_on = guard_on();
    return E.guard ? KILN_OK : KILN_ERR_UNAVAILABLE;
}

KilnResult KILN_CALL t_pack_mods(KilnStr text) {
    const std::string s = str(text);
    std::vector<std::string> ids;
    bool have = false;
    if (!s.empty() && s != "null") {
        have = true;
        size_t i = 0;
        // a flat JSON array of strings: ["a","b"] (ids match ^[a-z0-9][a-z0-9._-]*$, no escapes needed)
        while ((i = s.find('"', i)) != std::string::npos) {
            const size_t j = s.find('"', i + 1);
            if (j == std::string::npos) return KILN_ERR_INVALID_ARGUMENT;
            ids.push_back(s.substr(i + 1, j - i - 1));
            i = j + 1;
        }
        if (s.find('[') == std::string::npos) return KILN_ERR_INVALID_ARGUMENT;
    }
    std::lock_guard lock(g_filter_mu);
    if (have == g_have_filter && ids == g_filter) return KILN_OK;
    g_have_filter = have;
    g_filter = std::move(ids);
    if (g_ready.load()) g_filter_changed.store(true);
    return KILN_OK;
}

KilnResult KILN_CALL t_export_changes(uint32_t kinds, char* buf, size_t cap, size_t* len) {
    if (len) *len = 0;
    if (!tick()) return KILN_ERR_WRONG_THREAD;
    if (!g_ready.load()) return KILN_ERR_UNAVAILABLE;
    if (kinds & ~((1u << KILN_CONTENT_ITEMS) | (1u << KILN_CONTENT_RECIPES) | (1u << KILN_CONTENT_LOOT) | KILN_CONTENT_EXPORT_GAMEDATA))
        return KILN_ERR_INVALID_ARGUMENT;
    uint32_t fields = 0;
    const std::string doc = export_changes_json(kinds, &fields);
    copy_out(doc, buf, cap, len);
    return cap > doc.size() ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
}

// ---- v3: new content ----
KilnResult KILN_CALL t_define(KilnOwner owner, uint32_t kind, KilnStr text, char* report, size_t cap, size_t* len) {
    if (len) *len = 0;
    if (!text.ptr || !text.len) return KILN_ERR_INVALID_ARGUMENT;
    std::string rep;
    const KilnResult rc = nc_define(owner, kind, str(text), false, "", &rep);
    copy_out(rep, report, cap, len);
    return rc;
}

KilnResult KILN_CALL t_definitions(char* buf, size_t cap, size_t* len) {
    if (len) *len = 0;
    const std::string doc = nc_definitions_json();
    copy_out(doc, buf, cap, len);
    return cap > doc.size() ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
}

KilnResult KILN_CALL t_label_owner(KilnOwner owner, KilnStr label) {
    if (!label.ptr || !label.len) return KILN_ERR_INVALID_ARGUMENT;
    set_owner_label(owner, str(label));
    return KILN_OK;
}

// ---- v4: kitbash references, info ----
KilnResult KILN_CALL t_set_ref(KilnOwner owner, uint32_t kind, uint32_t id, KilnStr key, KilnStr value, uint32_t flags,
                               char* report, size_t cap, size_t* len) {
    if (len) *len = 0;
    if (kind != KILN_CONTENT_ITEMS || !key.ptr || !key.len || !value.ptr) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = common(kind)) return rc;
    const nlohmann::ordered_json v = nlohmann::ordered_json::parse(str(value), nullptr, false);
    std::vector<std::string> problems;
    KilnResult rc = KILN_ERR_INVALID_ARGUMENT;
    if (v.is_discarded()) problems.push_back("the value is not JSON (a string needs quotes: \"item:Name\")");
    else {
        std::lock_guard lock(store_mutex());
        const Entry* e = reg(kind).get(id);
        if (!e) return KILN_ERR_NOT_FOUND;
        const std::string k = str(key);
        rc = apply_kitbash(owner, next_code_seq(), *e, k, v, (flags & KILN_CONTENT_REF_ALLOW_GAME) != 0,
                           owner_label(owner) + ": items " + e->name + "." + k, &problems);
        std::string err;
        store_flush(&err);
        if (!err.empty()) problems.push_back(err);
        g_modified_stale = true;
    }
    nlohmann::ordered_json r = {{"ok", rc == KILN_OK}, {"problems", problems}};
    copy_out(r.dump(), report, cap, len);
    return rc;
}

uint32_t KILN_CALL t_undefine_owner(KilnOwner owner) { return owner ? nc_undefine_owner(owner) : 0; }

KilnResult KILN_CALL t_info(char* buf, size_t cap, size_t* len) {
    if (len) *len = 0;
    std::string refs = "null";
    if (g_ready.load() && tick()) {
        std::lock_guard lock(store_mutex());
        refs = refs_info_json(reg(KILN_CONTENT_ITEMS).schema, registry_items());
    }
    const std::string doc = nc_info_json(refs);
    copy_out(doc, buf, cap, len);
    return cap > doc.size() ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
}

KilnResult KILN_CALL t_icon_alias(const uint8_t from[16], const uint8_t to[16]) {
    if (!from || !to) return KILN_ERR_INVALID_ARGUMENT;
    return nc_icon_alias(from, to) ? KILN_OK : KILN_ERR_UNAVAILABLE;
}

const KilnContentApi kTable = {
    {sizeof(KilnContentApi), KILN_CONTENT_VERSION},
    &t_status, &t_count, &t_entry_at, &t_find, &t_field_count, &t_field_at, &t_field_find, &t_enum_name,
    &t_get, &t_set, &t_reset, &t_stacks, &t_set_stacks, &t_apply_json, &t_revert_owner, &t_dump, &t_reload_packs,
    &t_report, &t_subscribe_ready, &t_unsubscribe_ready, &t_stack_guard, &t_label_owner, &t_pack_mods,
    /* v2 */ &t_export_changes,
    /* v3 */ &t_define, &t_definitions,
    /* v4 */ &t_set_ref, &t_undefine_owner, &t_info,
    /* appended to v4 */ &t_icon_alias,
};

// ============================================================================================= boot ============

template <typename F>
bool sym(const char* name, F* out) {
    uintptr_t a = 0;
    if (g_api->symbol(KilnStr{name, std::strlen(name)}, &a) != KILN_OK || !a) return false;
    *out = reinterpret_cast<F>(a);
    return true;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

void resolve() {
    E.reflection = feat("content.reflection") && sym("content/TypeByHash", &E.type_by_hash) &&
                   cst("content/type.hash", &E.type_hash) && cst("content/type.qname", &E.type_qname) &&
                   cst("content/type.inner", &E.type_inner) && cst("content/type.size", &E.type_size) &&
                   cst("content/type.count", &E.type_count) && cst("content/type.kind", &E.type_kind) &&
                   cst("content/type.fields", &E.type_fields) && cst("content/type.enum_values", &E.type_enums) &&
                   cst("content/field.stride", &E.field_stride) && cst("content/field.type", &E.field_type) &&
                   cst("content/field.offset", &E.field_offset) && cst("content/enum.stride", &E.enum_stride) &&
                   cst("content/enum.value", &E.enum_value);
    const bool hm = cst("content/hmap.bits", &E.hm_bits) && cst("content/hmap.capacity", &E.hm_cap) &&
                    cst("content/hmap.keys", &E.hm_keys) && cst("content/hmap.values", &E.hm_values) &&
                    cst("content/hmap.count", &E.hm_count);
    E.items = hm && feat("content.items") && cst("content/items.map", &E.items_map) &&
              cst("content/items.loading", &E.items_loading) && cst("content/items.started", &E.items_started);
    // Maps of the same engine type: every captured member must sit at the same distance from its map.
    int64_t rc = 0, rcap = 0, rb = 0, rk = 0, rv = 0;
    E.recipes = hm && feat("content.recipes") && cst("content/recipes.map", &E.recipes_map) &&
                cst("content/recipes.resource", &E.recipes_resource) && cst("content/recipes.count", &rc) &&
                cst("content/recipes.cap", &rcap) && cst("content/recipes.bits", &rb) && cst("content/recipes.keys", &rk) &&
                cst("content/recipes.values", &rv) && rc - E.recipes_map == E.hm_count &&
                rcap - E.recipes_map == E.hm_cap && rb - E.recipes_map == E.hm_bits && rk - E.recipes_map == E.hm_keys &&
                rv - E.recipes_map == E.hm_values;
    int64_t lc = 0, lcap = 0, lb = 0, lk = 0, lv = 0;
    E.loot = hm && feat("content.loot") && cst("content/lootgen.lootreg", &E.lootgen_lootreg) &&
             cst("content/lootreg.count", &lc) && cst("content/lootreg.cap", &lcap) && cst("content/lootreg.bits", &lb) &&
             cst("content/lootreg.keys", &lk) && cst("content/lootreg.values", &lv);
    if (E.loot) {
        E.lootreg_map = lc - E.hm_count;
        E.loot = lcap - E.lootreg_map == E.hm_cap && lb - E.lootreg_map == E.hm_bits && lk - E.lootreg_map == E.hm_keys &&
                 lv - E.lootreg_map == E.hm_values;
    }
    E.drops = feat("content.drops") && sym("content/DefaultInventoryAcquire", &E.default_inventory_acquire);
    int64_t clamp_stack = 0;
    E.guard = feat("content.stack_guard") && sym("content/StackClamp", &E.stack_clamp) &&
              cst("content/clamp.max_stack", &clamp_stack) && clamp_stack == 0x14;
    if (E.guard) {
        uint8_t b = 0;
        E.guard = rdv(E.stack_clamp, b) && b == 0x76;
    }
    if (E.drops) {
        if (!g_acquire_hook.install(g_api, g_owner, E.default_inventory_acquire, reinterpret_cast<void*>(&hk_acquire),
                                    "content/DefaultInventoryAcquire")) {
            LOGE("hooking DefaultInventoryAcquire failed; drop tables off");
            E.drops = false;
        }
    }
    LOGI("resolved: reflection %s, items %s (map +0x%llx, loading flag +0x%llx), recipes %s (map +0x%llx), random loot %s "
         "(registry +0x%llx, map +0x%llx), drop tables %s, stack guard %s",
         E.reflection ? "on" : "OFF", E.items ? "on" : "OFF", (unsigned long long)E.items_map,
         (unsigned long long)E.items_loading, E.recipes ? "on" : "OFF", (unsigned long long)E.recipes_map,
         E.loot ? "on" : "OFF", (unsigned long long)E.lootgen_lootreg, (unsigned long long)E.lootreg_map,
         E.drops ? "on" : "OFF", E.guard ? "available" : "OFF");
}

}  // namespace

// ---- the boot generation's content collector (chapter 08) ----
// kiln.content reads no definition files at boot: its definitions come from code (Kiln.Blocks) and from data packs at world ready. A boot
// generation's content documents are what Kiln.Blocks would have sent from OnLoad, prepared here before the game's entry point so the
// worlds have them from their first load (they register at registry load as any definition does):
//   {"op": "define", "kind": "items" | "recipes" | <KILN_CONTENT_*>, "definition": {...}}   kiln.content's define()
//   {"op": "clone", "section": "templates" | "sequences" | "perks" | "buffs" | <KILN_GD_*>, "definition": {...}}   kiln.gamedata's define()
// each under the mod's process owner "boot:<mod>". A refused definition refuses its mod (dropped from the generation, or the start stops when
// its content is required). destroy takes the prepared definitions out again (no world has bound before the entry point).
namespace {
struct BootPlan {
    std::vector<KilnOwner> owners;
    size_t defined = 0, cloned = 0;
};
void KILN_CALL destroy_boot(const void* p) {
    const auto* plan = static_cast<const BootPlan*>(p);
    for (const KilnOwner o : plan->owners) {
        nc_forget_unbound(o);
#if KC_GAMEDATA
        gd::clones_forget_unbound(o);
#endif
    }
    delete plan;
}
uint32_t kind_of(const nlohmann::json& k) {
    if (k.is_number_unsigned()) return k.get<uint32_t>();
    const std::string n = k.is_string() ? k.get<std::string>() : std::string();
    return n == "items" ? KILN_CONTENT_ITEMS : n == "recipes" ? KILN_CONTENT_RECIPES : 0;
}
uint32_t section_of_name(const nlohmann::json& k) {
    if (k.is_number_unsigned()) return k.get<uint32_t>();
    const std::string n = k.is_string() ? k.get<std::string>() : std::string();
    return n == "templates" ? KILN_GD_TEMPLATES : n == "sequences" ? KILN_GD_SEQUENCES : n == "perks" ? KILN_GD_PERKS : n == "buffs" ? KILN_GD_BUFFS : 0;
}
KilnResult KILN_CALL prepare_boot(void*, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out, KilnBootIssueV1* issue) {
    const kiln::boot::Input input = kiln::boot::parse(in);
    if (!input.ok) return kiln::boot::refuse(out, issue, "", "", "", "kiln.content: the boot envelope is not JSON");
    auto* plan = new BootPlan;
    for (const auto& d : input.docs("content")) {
        const auto o = input.owner.find(d.owner);
        const KilnOwner owner = o != input.owner.end() ? o->second : 0;
        auto refused = [&](const std::string& why) {
            destroy_boot(plan);
            return kiln::boot::refuse(out, issue, d.owner, "content", d.key, why);
        };
        if (!owner) return refused("kiln.content: no process owner for " + d.owner);
        if (std::find(plan->owners.begin(), plan->owners.end(), owner) == plan->owners.end()) plan->owners.push_back(owner);
        const nlohmann::json& doc = d.document;
        const std::string op = doc.is_object() ? doc.value("op", std::string()) : std::string();
        if (!doc.is_object() || !doc.contains("definition") || !doc["definition"].is_object())
            return refused("a content document is {\"op\", \"definition\": {...}}");
        const std::string text = doc["definition"].dump();
        const std::string source = "boot:" + d.owner + "/" + d.key;
        std::string report;
        if (op == "define") {
            const uint32_t kind = kind_of(doc.value("kind", nlohmann::json()));
            if (kind != KILN_CONTENT_ITEMS && kind != KILN_CONTENT_RECIPES) return refused("\"kind\": items or recipes");
            if (nc_define(owner, kind, text, false, source, &report) != KILN_OK) return refused("kiln.content refused it: " + report);
            ++plan->defined;
        } else if (op == "clone") {
#if KC_GAMEDATA
            const uint32_t section = section_of_name(doc.value("section", nlohmann::json()));
            if (gd::clones_define(owner, section, text, false, source, &report) != KILN_OK) return refused("kiln.gamedata refused it: " + report);
            ++plan->cloned;
#else
            return refused("clones are not in this build of kiln.content");
#endif
        } else {
            return refused("unknown content document \"op\": \"" + op + "\" (define, clone)");
        }
    }
    LOGI("boot generation %llu prepared: %zu definition(s), %zu clone(s)", static_cast<unsigned long long>(input.generation), plan->defined,
         plan->cloned);
    return kiln::boot::prepared(out, plan, &destroy_boot);
}
}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{kName, std::strlen(kName)};
    info->version = KilnStr{"4", 1};
    near_alloc_boot();
    resolve();
    if (E.items && E.reflection) nc_boot();
#if KC_GAMEDATA
    gd_boot();  // registers kiln.gamedata (available or not)
#endif
    // bind of a boot generation's content part (no definitions are read here either way); kiln.content without its registries takes none
    if (E.items && E.reflection && kiln::boot::collect(api, owner, "content", &prepare_boot, nullptr))
        LOGI("boot profile: the boot generation's content definitions are prepared before the game's entry point");
    if (!E.items || !E.reflection) {
        set_detail("the item registry or the game's reflection is not available on this build");
        LOGW("kiln.content unavailable on this build (items %s, reflection %s)", E.items ? "on" : "off",
             E.reflection ? "on" : "off");
        return api->register_service(owner, KilnStr{KILN_CONTENT_SERVICE, std::strlen(KILN_CONTENT_SERVICE)}, &kTable);
    }
    KilnHandle h = 0;
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    // World stops (a client's sessions; a dedicated server only with world.lifecycle) and, on a client, its own world.
    KilnHandle wh = 0, ch = 0;
    const KilnResult wrc = api->subscribe_world(owner, &on_world_event, nullptr, &wh);
    KilnResult crc = KILN_OK;
    if (api->side() == KILN_SIDE_CLIENT)
        crc = api->subscribe_world_tick(owner, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &ch);
    if (wrc != KILN_OK || crc != KILN_OK) LOGW("world events rc %d, client world tick rc %d", wrc, crc);
    if (api->side() == KILN_SIDE_CLIENT)
        LOGI("on a client: the registries of the hosted server world, else (a joined game) the client world's own");
    return api->register_service(owner, KilnStr{KILN_CONTENT_SERVICE, std::strlen(KILN_CONTENT_SERVICE)}, &kTable);
}
