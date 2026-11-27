// SPDX-License-Identifier: MIT
// kiln.content: the layer store. Every changed field (or recipe list header) is a slot keyed by its address in game
// memory; the slot keeps the game's original bytes and one layer per owner. The newest layer (highest seq) wins;
// with no layer left the original comes back. Writes go out in batches (store_flush).
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <set>
#include <utility>

#include "content.h"
#if KC_GAMEDATA
#include "gamedata.h"
#endif

namespace kc {

namespace {
StoreMutex g_mu;
std::map<uintptr_t, Slot> g_slots;
std::vector<uintptr_t> g_dirty;  // the slots a flush writes, so that it costs what changed and not what the store holds
uint64_t g_gen = 0;              // +1 per change of any slot (store_generation)
uint64_t g_below_gen = 0;        // +1 per change of what a registry layer is computed over (store_below_generation)

// The slot at addr or the first after it, from `from` (a slot at or before addr): a few steps forward when addresses come in
// order and close together (a turn's slots: a table's fields side by side), else the map's own search.
std::map<uintptr_t, Slot>::iterator walk_to(std::map<uintptr_t, Slot>::iterator from, uintptr_t addr) {
    for (int steps = 0; from != g_slots.end() && steps < 8; ++steps, ++from)
        if (from->first >= addr) return from;
    return g_slots.lower_bound(addr);
}

void mark(uintptr_t addr, Slot& s) {
    ++g_gen;
    if (s.dirty) return;
    s.dirty = true;
    g_dirty.push_back(addr);
}
// Registry writes compare-before-write skipped (store_skipped): what is needed to name them later, one per slot.
struct SkippedRaw {
    uintptr_t addr;
    uint32_t kind, id;
    int32_t field, sub;
    std::string mod, source;
    bool revert;
};
std::vector<SkippedRaw> g_skipped;

bool excluded(const Slot& s, const Layer& l) { return s.foreign && is_registry_seq(l.seq); }

// The originals of the store's slots, mirrored under a leaf lock (review4 NS1): a registry base decoded at first use holds
// the stage runner's lock and may not take g_mu (store -> runner is the lock order), but it must take Kiln's own writes
// back out of the resident bytes it decodes (store_pristine). Kept in step with g_slots: added with a slot, dropped with it.
std::mutex g_pristine_mu;  // leaf: nothing else is locked while it is held
struct Pristine {
    uint8_t n = 0;
    uint8_t bytes[kSlotBytes] = {};
};
std::map<uintptr_t, Pristine> g_pristine;
void pristine_put(uintptr_t addr, const Slot& s) {
    std::lock_guard lock(g_pristine_mu);
    Pristine& p = g_pristine[addr];
    p.n = s.n;
    std::memcpy(p.bytes, s.original, s.n);
}
void pristine_erase(uintptr_t addr) {
    std::lock_guard lock(g_pristine_mu);
    g_pristine.erase(addr);
}

// The slots holding an evaluated layer, by the entry they belong to: a formula's layer reads its entry's other fields, so
// another layer put on or taken off one of them flushes (evaluates) the entry's evaluated slots again.
std::map<std::pair<uint32_t, uint32_t>, std::set<uintptr_t>> g_evals;
void mark_entry_evals(uint32_t kind, uint32_t id, uintptr_t except);

// The bytes the layers give, in seq order: a plain layer's bytes replace the value, an evaluated layer computes it from the
// value below (and keeps the result in its bytes, for conflicts and reports). Registry layers of a foreign slot are left out.
// below_registry: only the layers under the registry band (what a registry layer's value is computed over).
thread_local int t_fold_depth = 0;  // an evaluated layer reading another slot's value (formulas): never in a cycle
void fold(Slot& s, uint8_t* out, bool below_registry = false) {
    uint8_t v[kSlotBytes] = {};
    std::memcpy(v, s.original, s.n);
    struct Depth {
        Depth() { ++t_fold_depth; }
        ~Depth() { --t_fold_depth; }
    } depth;
    for (Layer& l : s.layers) {
        if (excluded(s, l)) continue;
        if (below_registry && l.seq >= kRegistrySeq) break;
        if (l.eval && t_fold_depth > 4) continue;  // a cycle of evaluated layers: the value below stands
        if (l.eval) {
            uint8_t r[kSlotBytes] = {};
            std::string why;
            if ((*l.eval)(v, s.n, r, &why)) std::memcpy(v, r, s.n);
            std::memcpy(l.bytes, v, s.n);
        } else {
            std::memcpy(v, l.bytes, s.n);
        }
    }
    std::memcpy(out, v, s.n);
}
bool has_registry_layer(const Slot& s) {
    for (const Layer& l : s.layers)
        if (is_registry_seq(l.seq)) return true;
    return false;
}
std::unordered_map<KilnOwner, std::string> g_labels;
std::atomic<uint64_t> g_code_seq{0};
bool g_guard = false;

// Open content transactions (store_txn_*): every layer one owner had when it began, and the slot it was in.
struct TxnSlot {
    uint32_t kind = 0, id = 0;
    int32_t field = -1, sub = -1;
    uint8_t n = 0;
    uint8_t original[kSlotBytes] = {};
    Layer layer{};
};
struct Txn {
    KilnOwner owner = 0;
    std::map<uintptr_t, TxnSlot> slots;
    std::map<uintptr_t, TxnSlot> touched;  // includes new layers later reset inside the transaction
};
std::map<uint64_t, Txn> g_txns;
uint64_t g_txn_last = 0;
constexpr size_t kMaxTxns = 64;
void txns_forget(KilnOwner owner) {
    std::erase_if(g_txns, [&](const auto& t) { return t.second.owner == owner; });
}

// ---- timing ----
int64_t now_us() {
    return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
constexpr const char* kPhaseNames[PH_COUNT] = {
    "define", "define (locked)", "clone define", "clone register", "clone fields", "clone flush", "gamedata ready", "world sync",
    "sync items", "build clone", "kitbash", "world items", "resolve item", "map insert", "registry entry", "sync recipes",
    "build recipe", "workshop list", "crafted notes", "loot", "drop tables", "flush", "write batch", "near alloc", "apply json",
    "apply packs", "build registries", "drop table hook", "store scan", "is new", "own lists", "asset put", "game register",
    "build bytes", "status", "listing", "export", "texts", "sequence resource",
    "conflicts", "pack file"};
struct PhaseStat {
    std::atomic<int64_t> us{0}, max_us{0};
    std::atomic<uint64_t> n{0};
};
PhaseStat g_phase[PH_COUNT];
thread_local Phase tl_phase = PH_COUNT;  // the innermost phase running on this thread
thread_local Phase tl_hold_phase = PH_COUNT;
thread_local int tl_depth = 0;
thread_local int64_t tl_since = 0;
thread_local bool tl_job = false;
std::mutex g_hold_mu;
int64_t g_hold_max = 0, g_wait_max = 0, g_job_wait_max = 0, g_holds_over_100ms = 0, g_hold_total = 0;
uint64_t g_holds = 0;
Phase g_hold_max_phase = PH_COUNT, g_job_wait_phase = PH_COUNT;
void note_max(std::atomic<int64_t>& m, int64_t v) {
    int64_t cur = m.load(std::memory_order_relaxed);
    while (v > cur && !m.compare_exchange_weak(cur, v, std::memory_order_relaxed)) {}
}
}  // namespace

void StoreMutex::lock() {
    if (tl_depth > 0) {
        m_.lock();
        ++tl_depth;
        return;
    }
    const int64_t t0 = now_us();
    m_.lock();
    tl_since = now_us();
    tl_hold_phase = tl_phase;
    tl_depth = 1;
    const int64_t waited = tl_since - t0;
    if (waited > 1000) {
        std::lock_guard l(g_hold_mu);
        g_wait_max = std::max(g_wait_max, waited);
        if (tl_job && waited > g_job_wait_max) g_job_wait_max = waited, g_job_wait_phase = tl_phase;
    }
}

bool StoreMutex::try_lock() {
    if (!m_.try_lock()) return false;
    if (tl_depth++ == 0) tl_since = now_us(), tl_hold_phase = tl_phase;
    return true;
}

void StoreMutex::unlock() {
    if (--tl_depth == 0) {
        const int64_t held = now_us() - tl_since;
        std::lock_guard l(g_hold_mu);
        ++g_holds;
        g_hold_total += held;
        if (held > 100000) ++g_holds_over_100ms;
        // the phase the hold started in, or, when it started outside any, the one it ended in
        if (held > g_hold_max) g_hold_max = held, g_hold_max_phase = tl_hold_phase != PH_COUNT ? tl_hold_phase : tl_phase;
    }
    m_.unlock();
}

PhaseTimer::PhaseTimer(Phase p) : p_(p), outer_(tl_phase), t0_(now_us()) {
    tl_phase = p;
    // a hold that started outside any phase is credited to the first phase inside it
    if (tl_depth > 0 && tl_hold_phase == PH_COUNT) tl_hold_phase = p;
}

PhaseTimer::~PhaseTimer() {
    const int64_t d = now_us() - t0_;
    PhaseStat& s = g_phase[p_];
    s.us.fetch_add(d, std::memory_order_relaxed);
    s.n.fetch_add(1, std::memory_order_relaxed);
    note_max(s.max_us, d);
    tl_phase = outer_;
}

void timing_mark_job_thread(bool on) { tl_job = on; }

std::string timing_report(bool reset) {
    std::string out;
    char b[512];
    for (int i = 0; i < PH_COUNT; ++i) {
        PhaseStat& s = g_phase[i];
        const uint64_t n = reset ? s.n.exchange(0) : s.n.load();
        const int64_t us = reset ? s.us.exchange(0) : s.us.load();
        const int64_t mx = reset ? s.max_us.exchange(0) : s.max_us.load();
        if (!n) continue;
        std::snprintf(b, sizeof b, "%s%s %.0f ms / %llu (max %.1f)", out.empty() ? "" : ", ", kPhaseNames[i], us / 1000.0,
                      static_cast<unsigned long long>(n), mx / 1000.0);
        out += b;
    }
    return out;
}

std::string lock_report(bool reset) {
    std::string out;
    char b[512];
    std::lock_guard l(g_hold_mu);
    std::snprintf(b, sizeof b, "%llu holds, %.0f ms in all, longest %.1f ms (%s), %lld over 100 ms; longest wait %.1f ms",
                  static_cast<unsigned long long>(g_holds), g_hold_total / 1000.0, g_hold_max / 1000.0,
                  g_hold_max_phase < PH_COUNT ? kPhaseNames[g_hold_max_phase] : "outside the timed phases",
                  static_cast<long long>(g_holds_over_100ms), g_wait_max / 1000.0);
    out += b;
    std::snprintf(b, sizeof b, ", a job thread's longest %.1f ms (%s)", g_job_wait_max / 1000.0,
                  g_job_wait_phase < PH_COUNT ? kPhaseNames[g_job_wait_phase] : "-");
    out += b;
    if (reset) g_holds = 0, g_hold_total = 0, g_hold_max = 0, g_wait_max = 0, g_job_wait_max = 0, g_holds_over_100ms = 0;
    return out;
}

StoreMutex& store_mutex() { return g_mu; }
bool store_locked_here() { return tl_depth > 0; }
uint64_t next_code_seq() { return kCodeSeq | ++g_code_seq; }

std::string owner_label(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    auto it = g_labels.find(owner);
    if (it != g_labels.end()) return it->second;
    char b[32];
    std::snprintf(b, sizeof b, "owner %llu", static_cast<unsigned long long>(owner));
    return b;
}
void set_owner_label(KilnOwner owner, const std::string& label) {
    std::lock_guard lock(g_mu);
    g_labels[owner] = label;
}

namespace {
thread_local std::string t_layer_mod;  // LayerModScope
}  // namespace

LayerModScope::LayerModScope(const std::string& mod) : prev_(std::exchange(t_layer_mod, mod)) {}
LayerModScope::~LayerModScope() { t_layer_mod = std::move(prev_); }

namespace {
bool put_layer(KilnOwner owner, uint64_t seq, uint32_t kind, uint32_t id, int32_t field, int32_t sub, uintptr_t addr, uint8_t n,
               const uint8_t* bytes, std::shared_ptr<const LayerEval> eval, std::string source, const std::string* mod = nullptr) {
    if (n == 0 || n > kSlotBytes) return false;
    std::lock_guard lock(g_mu);
    auto [it, fresh] = g_slots.try_emplace(addr);
    Slot& s = it->second;
    if (fresh) {
        s.kind = kind, s.id = id, s.field = field, s.sub = sub, s.n = n;
        if (!rd(addr, s.original, n)) {
            g_slots.erase(it);
            return false;
        }
        std::memcpy(s.current, s.original, n);
        pristine_put(addr, s);
    } else if (s.n != n) {
        return false;
    }
    for (auto& [tid, txn] : g_txns) {
        if (txn.owner != owner) continue;
        TxnSlot ts;
        ts.n = s.n;
        std::memcpy(ts.original, s.original, s.n);
        txn.touched.try_emplace(addr, std::move(ts));
    }
    auto l = std::find_if(s.layers.begin(), s.layers.end(), [&](const Layer& x) { return x.owner == owner; });
    if (l == s.layers.end()) {
        s.layers.push_back(Layer{owner, seq, {}, source, {}, nullptr});
        l = s.layers.end() - 1;
    }
    l->seq = seq;
    l->source = std::move(source);
    l->mod = mod ? *mod : t_layer_mod;
    l->eval = std::move(eval);
    if (bytes) std::memcpy(l->bytes, bytes, n);
    std::sort(s.layers.begin(), s.layers.end(), [](const Layer& a, const Layer& b) { return a.seq < b.seq; });
    if (is_registry_seq(seq)) s.registry = true;
    else s.old_changed = true, ++g_below_gen;
    mark(addr, s);
    if (l->eval) g_evals[{s.kind, s.id}].insert(addr);
    else mark_entry_evals(s.kind, s.id, addr);
    return true;
}

void mark_entry_evals(uint32_t kind, uint32_t id, uintptr_t except) {
    const auto it = g_evals.find({kind, id});
    if (it == g_evals.end()) return;
    for (uintptr_t a : it->second) {
        if (a == except) continue;
        const auto s = g_slots.find(a);
        if (s != g_slots.end()) mark(a, s->second);
    }
}
}  // namespace

bool store_put(KilnOwner owner, uint64_t seq, uint32_t kind, uint32_t id, int32_t field, int32_t sub, uintptr_t addr,
               uint8_t n, const uint8_t* bytes, const std::string& source) {
    return put_layer(owner, seq, kind, id, field, sub, addr, n, bytes, nullptr, source);
}

bool store_put_eval(KilnOwner owner, uint64_t seq, uint32_t kind, uint32_t id, int32_t field, int32_t sub, uintptr_t addr,
                    uint8_t n, LayerEval eval, const std::string& source) {
    if (!eval) return false;
    return put_layer(owner, seq, kind, id, field, sub, addr, n, nullptr, std::make_shared<const LayerEval>(std::move(eval)), source);
}

bool store_put_prepared(KilnOwner owner, uint64_t seq, uint32_t kind, uint32_t id, int32_t field, int32_t sub, uintptr_t addr,
                        uint8_t n, std::shared_ptr<const LayerEval> eval, std::string&& source, const std::string& mod) {
    if (!eval) return false;
    return put_layer(owner, seq, kind, id, field, sub, addr, n, nullptr, std::move(eval), std::move(source), &mod);
}

std::vector<uintptr_t> store_owner_slots(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    std::vector<uintptr_t> out;
    for (const auto& [addr, s] : g_slots)
        if (std::any_of(s.layers.begin(), s.layers.end(), [&](const Layer& l) { return l.owner == owner; })) out.push_back(addr);
    return out;
}

size_t store_skipped_count() {
    std::lock_guard lock(g_mu);
    return g_skipped.size();
}

std::vector<uintptr_t> store_skipped_since(size_t from) {
    std::lock_guard lock(g_mu);
    std::vector<uintptr_t> out;
    for (size_t i = from; i < g_skipped.size(); ++i) out.push_back(g_skipped[i].addr);
    return out;
}

bool store_registry_input(uintptr_t addr, uint8_t n, uint8_t* out, bool* foreign) {
    if (n == 0 || n > kSlotBytes) return false;
    std::lock_guard lock(g_mu);
    *foreign = false;
    auto it = g_slots.find(addr);
    if (it == g_slots.end()) return rd(addr, out, n);
    if (it->second.n != n) return false;
    *foreign = it->second.foreign;
    fold(it->second, out, true);
    return true;
}

uint8_t store_slot_size(uintptr_t addr) {
    std::lock_guard lock(g_mu);
    const auto it = g_slots.find(addr);
    return it == g_slots.end() ? 0 : it->second.n;
}

size_t store_put_prepared_many(KilnOwner owner, std::vector<PreparedLayer>& layers, std::vector<std::shared_ptr<const LayerEval>>* released) {
    std::lock_guard lock(g_mu);
    std::vector<size_t> order(layers.size());
    for (size_t i = 0; i < order.size(); ++i) order[i] = i;
    // in address order (the slots' map is too, so the walk stays near the last slot found); one address twice: the later
    // put replaces the earlier, as two store_put_prepared calls would
    std::stable_sort(order.begin(), order.end(), [&](size_t a, size_t b) { return layers[a].addr < layers[b].addr; });
    size_t done = 0;
    auto at = g_slots.begin();
    std::map<std::pair<uint32_t, uint32_t>, std::set<uintptr_t>>::iterator evals = g_evals.end();
    for (size_t k : order) {
        PreparedLayer& p = layers[k];
        if (p.n == 0 || p.n > kSlotBytes || !p.eval) continue;
        auto it = walk_to(at, p.addr);
        if (it == g_slots.end() || it->first != p.addr) {
            Slot fresh;
            fresh.kind = p.kind, fresh.id = p.id, fresh.field = p.field, fresh.sub = p.sub, fresh.n = p.n;
            if (!rd(p.addr, fresh.original, p.n)) continue;
            std::memcpy(fresh.current, fresh.original, p.n);
            it = g_slots.emplace_hint(it, p.addr, std::move(fresh));
            pristine_put(p.addr, it->second);
        } else if (it->second.n != p.n) {
            at = it;
            continue;
        }
        at = it;
        Slot& s = it->second;
        for (auto& [tid, txn] : g_txns) {
            if (txn.owner != owner) continue;
            TxnSlot ts;
            ts.n = s.n;
            std::memcpy(ts.original, s.original, s.n);
            txn.touched.try_emplace(p.addr, std::move(ts));
        }
        auto l = std::find_if(s.layers.begin(), s.layers.end(), [&](const Layer& x) { return x.owner == owner; });
        if (l == s.layers.end()) {
            s.layers.push_back(Layer{owner, p.seq, {}, {}, {}, nullptr});
            l = s.layers.end() - 1;
        }
        l->seq = p.seq;
        std::swap(l->source, p.source);  // the layer's old source goes back with the caller's layers
        l->mod = p.mod;
        if (released && l->eval) released->push_back(std::move(l->eval));  // its last holder may be here: freed by the caller
        l->eval = std::move(p.eval);
        if (s.layers.size() > 1) std::sort(s.layers.begin(), s.layers.end(), [](const Layer& a, const Layer& b) { return a.seq < b.seq; });
        if (is_registry_seq(p.seq)) s.registry = true;
        else s.old_changed = true, ++g_below_gen;
        if (p.gate) s.gate = true;
        mark(p.addr, s);
        if (evals == g_evals.end() || evals->first != std::make_pair(s.kind, s.id)) evals = g_evals.try_emplace({s.kind, s.id}).first;
        evals->second.insert(evals->second.end(), p.addr);  // in address order: at the set's end
        ++done;
    }
    return done;
}

uint64_t store_below_generation() {
    std::lock_guard lock(g_mu);
    return g_below_gen;
}

bool store_effective(uintptr_t addr, uint8_t n, uint8_t* out) {
    if (n == 0 || n > kSlotBytes) return false;
    std::lock_guard lock(g_mu);
    auto it = g_slots.find(addr);
    if (it == g_slots.end()) return rd(addr, out, n);
    if (it->second.n != n) return false;
    fold(it->second, out);
    return true;
}

bool store_effective_without_registry(uintptr_t addr, uint8_t n, const LayerEval* registry, uint8_t* out) {
    if (n == 0 || n > kSlotBytes) return false;
    std::lock_guard lock(g_mu);
    auto it = g_slots.find(addr);
    if (it == g_slots.end()) return rd(addr, out, n);
    Slot& s = it->second;
    if (s.n != n) return false;
    if (s.foreign) return rd(addr, out, n);  // another writer owns it for this world: its bytes are what a reader sees
    uint8_t v[kSlotBytes] = {};
    std::memcpy(v, s.original, n);
    bool applied = false;
    auto apply_registry = [&] {
        if (applied) return;
        applied = true;
        uint8_t r[kSlotBytes] = {};
        std::string why;
        if (registry && *registry && (*registry)(v, n, r, &why)) std::memcpy(v, r, n);
    };
    for (const Layer& l : s.layers) {
        if (is_registry_seq(l.seq)) continue;      // the registry's own layer is replaced by the reader's evaluation
        if (l.seq >= kRegistrySeq) apply_registry();  // code above the registry band still wins
        if (l.eval) {
            uint8_t r[kSlotBytes] = {};
            std::string why;
            if ((*l.eval)(v, n, r, &why)) std::memcpy(v, r, n);
        } else {
            std::memcpy(v, l.bytes, n);
        }
    }
    apply_registry();
    std::memcpy(out, v, n);
    return true;
}

bool store_below_registry(uintptr_t addr, uint8_t n, uint8_t* out) {
    if (n == 0 || n > kSlotBytes) return false;
    std::lock_guard lock(g_mu);
    auto it = g_slots.find(addr);
    if (it == g_slots.end()) return rd(addr, out, n);
    if (it->second.n != n) return false;
    fold(it->second, out, true);
    return true;
}

void store_set_gate(uintptr_t addr) {
    std::lock_guard lock(g_mu);
    if (auto it = g_slots.find(addr); it != g_slots.end()) it->second.gate = true;
}

bool store_slot_copy(uintptr_t addr, Slot* out) {
    std::lock_guard lock(g_mu);
    auto it = g_slots.find(addr);
    if (it == g_slots.end()) return false;
    *out = it->second;
    return true;
}

uint64_t store_slot_signature(uintptr_t addr) {
    std::lock_guard lock(g_mu);
    const auto it = g_slots.find(addr);
    if (it == g_slots.end()) return 0;
    const Slot& s = it->second;
    uint64_t h = 1469598103934665603ull;
    auto mix = [&h](const void* p, size_t n) {
        for (size_t i = 0; i < n; ++i) h = (h ^ static_cast<const uint8_t*>(p)[i]) * 1099511628211ull;
    };
    const uint8_t foreign = s.foreign ? 1 : 0;
    mix(&foreign, 1);
    mix(s.current, s.n);
    for (const Layer& l : s.layers) {
        const uint8_t band = static_cast<uint8_t>(l.seq >> 62);  // pack, registry or code
        mix(&l.owner, sizeof l.owner);
        mix(&band, 1);
        mix(l.bytes, s.n);
        mix(l.source.data(), l.source.size());
        mix("\n", 1);
        mix(l.mod.data(), l.mod.size());
        mix("\n", 1);
    }
    return h ? h : 1;
}

uint32_t store_mark_evals() {
    std::lock_guard lock(g_mu);
    uint32_t n = 0;
    for (auto& [addr, s] : g_slots)
        for (const Layer& l : s.layers)
            if (l.eval) {
                mark(addr, s);
                ++n;
                break;
            }
    return n;
}

bool store_drop(KilnOwner owner, uintptr_t addr) {
    std::lock_guard lock(g_mu);
    auto it = g_slots.find(addr);
    if (it == g_slots.end()) return false;
    auto& ls = it->second.layers;
    const size_t before = ls.size();
    for (const Layer& x : ls)
        if (x.owner == owner && !is_registry_seq(x.seq)) it->second.old_changed = true, ++g_below_gen;
    std::erase_if(ls, [&](const Layer& x) { return x.owner == owner; });
    if (ls.size() == before) return false;
    mark(addr, it->second);
    mark_entry_evals(it->second.kind, it->second.id, addr);
    return true;
}

uint32_t store_drop_many(KilnOwner owner, const std::vector<uintptr_t>& addrs, std::vector<std::shared_ptr<const LayerEval>>* released) {
    std::lock_guard lock(g_mu);
    uint32_t n = 0;
    std::vector<uintptr_t> sorted = addrs;
    std::sort(sorted.begin(), sorted.end());
    std::set<std::pair<uint32_t, uint32_t>> entries;  // their evaluated slots flushed again once each, not once per dropped slot
    auto at = g_slots.begin();
    for (uintptr_t addr : sorted) {
        auto it = walk_to(at, addr);
        if (it == g_slots.end() || it->first != addr) { at = it == g_slots.end() ? g_slots.begin() : it; continue; }
        at = it;
        Slot& s = it->second;
        const size_t before = s.layers.size();
        for (Layer& x : s.layers) {
            if (x.owner != owner) continue;
            if (!is_registry_seq(x.seq)) s.old_changed = true, ++g_below_gen;
            if (released && x.eval) released->push_back(std::move(x.eval));
        }
        std::erase_if(s.layers, [&](const Layer& x) { return x.owner == owner; });
        if (s.layers.size() == before) continue;
        mark(addr, s);
        entries.insert({s.kind, s.id});
        ++n;
    }
    // an entry's other evaluated slots (a formula reads its entry's fields): marked unless already (most were dropped here too)
    for (const auto& key : entries) {
        const auto ev = g_evals.find(key);
        if (ev == g_evals.end()) continue;
        for (uintptr_t a : ev->second) {
            if (std::binary_search(sorted.begin(), sorted.end(), a)) continue;
            const auto sl = g_slots.find(a);
            if (sl != g_slots.end()) mark(a, sl->second);
        }
    }
    return n;
}

uint32_t store_drop_owner(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    txns_forget(owner);  // what an open transaction would put back went with the owner: its rollback restores nothing
    uint32_t n = 0;
    for (auto& [addr, s] : g_slots) {
        const size_t before = s.layers.size();
        for (const Layer& x : s.layers)
            if (x.owner == owner && !is_registry_seq(x.seq)) s.old_changed = true, ++g_below_gen;
        std::erase_if(s.layers, [&](const Layer& x) { return x.owner == owner; });
        if (s.layers.size() != before) {
            mark(addr, s);
            mark_entry_evals(s.kind, s.id, addr);
            ++n;
        }
    }
    return n;
}

bool owner_has_layers(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    for (auto& [addr, s] : g_slots)
        for (const Layer& l : s.layers)
            if (l.owner == owner) return true;
    return false;
}

std::vector<KilnOwner> store_owners() {
    std::lock_guard lock(g_mu);
    std::set<KilnOwner> o;
    for (auto& [addr, s] : g_slots)
        for (const Layer& l : s.layers) o.insert(l.owner);
    return {o.begin(), o.end()};
}

uint32_t store_reset(bool revert, std::string* error) {
    std::lock_guard lock(g_mu);
    ++g_below_gen;
    uint32_t n = 0;
    for (auto& [addr, s] : g_slots) {
        if (!s.layers.empty()) ++n;
        s.layers.clear();
        mark(addr, s);
    }
    if (revert) store_flush(error);
    g_slots.clear();
    {
        std::lock_guard leaf(g_pristine_mu);
        g_pristine.clear();
    }
    g_dirty.clear();
    g_txns.clear();
    g_skipped.clear();
    g_evals.clear();
    return n;
}

bool store_has(uintptr_t addr) {
    std::lock_guard lock(g_mu);
    return g_slots.count(addr) != 0;
}

void store_originals(uintptr_t addr, size_t size, uint8_t* out) {
    std::lock_guard lock(g_mu);
    for (auto it = g_slots.lower_bound(addr); it != g_slots.end() && it->first < addr + size; ++it)
        if (it->first + it->second.n <= addr + size) std::memcpy(out + (it->first - addr), it->second.original, it->second.n);
}

void store_pristine(uintptr_t addr, size_t size, uint8_t* bytes) {
    std::lock_guard lock(g_pristine_mu);
    for (auto it = g_pristine.lower_bound(addr); it != g_pristine.end() && it->first < addr + size; ++it)
        if (it->first + it->second.n <= addr + size) std::memcpy(bytes + (it->first - addr), it->second.bytes, it->second.n);
}

bool store_original(uintptr_t addr, uint8_t n, uint8_t* out) {
    std::lock_guard lock(g_mu);
    auto it = g_slots.find(addr);
    if (it == g_slots.end() || it->second.n != n) return false;
    std::memcpy(out, it->second.original, n);
    return true;
}

size_t store_flush(std::string* error) { return store_flush_checked(error, nullptr); }

size_t store_flush_checked(std::string* error, std::vector<uintptr_t>* unwritten) {
    PhaseTimer pt(PH_FLUSH);
    std::lock_guard lock(g_mu);
    // the flush's writes; gate slots (store_set_gate) apart: the ones that close (to all zero) go first, the ones that open last
    std::vector<Write> writes, closing, opening;
    std::vector<std::map<uintptr_t, Slot>::iterator> dirty;
    dirty.reserve(g_dirty.size());
    std::sort(g_dirty.begin(), g_dirty.end());  // found in the map's order: a walk near the last slot found, not across it
    for (uintptr_t a : g_dirty)
        if (auto it = g_slots.find(a); it != g_slots.end() && it->second.dirty) dirty.push_back(it);
    g_dirty.clear();
    for (auto& d : dirty) {
        const uintptr_t addr = d->first;
        Slot& s = d->second;
        uint8_t want[kSlotBytes] = {};
        fold(s, want);
        // compare-before-write: a registry write (or a registry layer's revert) is made only over what Kiln last wrote there
        if ((s.registry || has_registry_layer(s)) && !s.foreign) {
            uint8_t now[kSlotBytes] = {};
            if (rd(addr, now, s.n) && std::memcmp(now, s.current, s.n) != 0) {
                std::string mod, source;
                bool revert = true;
                for (const Layer& l : s.layers)
                    if (is_registry_seq(l.seq)) mod = l.mod, source = l.source, revert = false;
                if (revert)  // the dropped layer is gone: name the slot only
                    mod.clear();
                g_skipped.push_back(SkippedRaw{addr, s.kind, s.id, s.field, s.sub, mod, source, revert});
                s.foreign = true, ++g_below_gen;
                std::memcpy(s.current, now, s.n);
                fold(s, want);  // the registry's layers left out: what an older path's own change in this flush writes (today's behaviour)
            }
        }
        s.registry = has_registry_layer(s);
        // a slot that stays with another writer is written only for a change of an older path's layer (today's behaviour)
        const bool skip_foreign = s.foreign && !s.old_changed;
        s.old_changed = false;
        if (!skip_foreign && std::memcmp(want, s.current, s.n) != 0) {
            Write w{addr, s.n, {}};
            std::memcpy(w.bytes, want, s.n);
            if (!s.gate) writes.push_back(w);
            else (std::all_of(want, want + s.n, [](uint8_t b) { return b == 0; }) ? closing : opening).push_back(w);
        }
    }
    uintptr_t failed = 0;
    size_t done = 0;
    if (closing.empty() && opening.empty()) {
        if (!writes.empty()) {
            PhaseTimer wt(PH_WRITE_BATCH);
            done = write_batch(writes, &failed);
        }
    } else {
        // Three batches, so a game thread that reads a gate and then what it gates never sees it open over values it was not
        // opened with. An entry whose gate did not close, or one of whose other writes did not land, is held: its later writes
        // are not made (they stay unwritten, and the caller sees them fail), so it is never left open over a half-write.
        std::set<std::pair<uint32_t, uint32_t>> held;
        auto entry_of = [](uintptr_t a) { const Slot& s = g_slots.at(a); return std::make_pair(s.kind, s.id); };
        auto run = [&](const std::vector<Write>& ws) {
            std::vector<Write> go;
            for (const Write& w : ws)
                if (!held.count(entry_of(w.addr))) go.push_back(w);
                else if (!failed) failed = w.addr;
            if (!go.empty()) {
                PhaseTimer wt(PH_WRITE_BATCH);
                uintptr_t f = 0;
                done += write_batch(go, &f);
                if (f && !failed) failed = f;
            }
            for (const Write& w : ws) {
                uint8_t now[kSlotBytes] = {};
                if (!rd(w.addr, now, w.n) || std::memcmp(now, w.bytes, w.n) != 0) held.insert(entry_of(w.addr));
            }
        };
        run(closing);
        run(writes);
        run(opening);
        writes.insert(writes.begin(), closing.begin(), closing.end());
        writes.insert(writes.end(), opening.begin(), opening.end());
    }
    if (unwritten)  // each write whose bytes are not in memory now: attributed per slot by the caller (review4 NM1)
        for (const Write& w : writes) {
            uint8_t now[kSlotBytes] = {};
            if (!rd(w.addr, now, w.n) || std::memcmp(now, w.bytes, w.n) != 0) unwritten->push_back(w.addr);
        }
    if (failed && error) {
        char b[96];
        std::snprintf(b, sizeof b, "%zu of %zu writes failed (first at %p)", writes.size() - done, writes.size(),
                      reinterpret_cast<void*>(failed));
        *error = b;
    }
    // What is in memory now, whatever happened.
    const Registry& items = reg(KILN_CONTENT_ITEMS);
    const Field* max_stack = items.schema.find("maxStackSize");
    for (auto& it : dirty) {
        Slot& s = it->second;
        uint8_t now[kSlotBytes] = {};
        if (rd(it->first, now, s.n)) std::memcpy(s.current, now, s.n);
        s.dirty = false;
        if (max_stack && s.kind == KILN_CONTENT_ITEMS && s.field >= 0 && &items.schema.fields[static_cast<size_t>(s.field)] == max_stack)
            guard_maybe(*max_stack, s.kind, s.original, s.current);
        // a slot that is clean and back to the original goes (only a written slot can have become so)
        if (s.layers.empty() && std::memcmp(s.current, s.original, s.n) == 0) {
            pristine_erase(it->first);
            g_slots.erase(it);
        }
    }
    if (!dirty.empty()) ++g_gen;
    return done;
}

uint64_t store_generation() {
    std::lock_guard lock(g_mu);
    return g_gen;
}

uint32_t store_changed() {
    std::lock_guard lock(g_mu);
    uint32_t n = 0;
    for (auto& [addr, s] : g_slots) n += std::memcmp(s.current, s.original, s.n) != 0;
    return n;
}

void store_for_each(const std::function<void(uintptr_t, const Slot&)>& fn) {
    PhaseTimer pt(PH_STORE_SCAN);
    std::lock_guard lock(g_mu);
    for (auto& [addr, s] : g_slots) fn(addr, s);
}

// ============================================================================================= transactions ===
// A content transaction: every layer of one owner set aside at its start, so that what the owner writes in it can be
// taken back whole. Rollback puts back exactly the layers of the start (bytes, order, source), takes off every layer
// written since, writes the slots, then reads each of them back from memory: a slot whose bytes are not what its
// layers say, or a layer that is not the start's, makes the rollback unverified. Under g_mu (recursive) throughout.

bool store_txn_begin(KilnOwner owner, uint64_t* id) {
    std::lock_guard lock(g_mu);
    if (owner == 0 || g_txns.size() >= kMaxTxns) return false;
    for (auto& [tid, t] : g_txns)
        if (t.owner == owner) return false;
    Txn t;
    t.owner = owner;
    for (auto& [addr, s] : g_slots)
        for (const Layer& l : s.layers) {
            if (l.owner != owner) continue;
            TxnSlot ts;
            ts.kind = s.kind, ts.id = s.id, ts.field = s.field, ts.sub = s.sub, ts.n = s.n;
            std::memcpy(ts.original, s.original, s.n);
            ts.layer = l;
            t.slots.emplace(addr, std::move(ts));
            break;
        }
    *id = ++g_txn_last;
    g_txns.emplace(*id, std::move(t));
    return true;
}

bool store_txn_commit(uint64_t id) {
    std::lock_guard lock(g_mu);
    return g_txns.erase(id) != 0;
}

TxnOutcome store_txn_rollback(uint64_t id, const std::function<void()>& settle, KilnOwner* owner_out, uint32_t* fields, std::string* why) {
    std::lock_guard lock(g_mu);
    auto it = g_txns.find(id);
    if (it == g_txns.end()) return TxnOutcome::Cancelled;
    const Txn t = std::move(it->second);
    g_txns.erase(it);
    const KilnOwner owner = t.owner;
    if (owner_out) *owner_out = owner;
    // every slot either side has a layer of the owner in, with the bytes it has when no layer is left
    struct Touched {
        uint8_t n = 0;
        uint8_t original[kSlotBytes] = {};
    };
    std::map<uintptr_t, Touched> touched;
    for (auto& [addr, s] : g_slots)
        for (const Layer& l : s.layers)
            if (l.owner == owner) {
                Touched& x = touched[addr];
                x.n = s.n;
                std::memcpy(x.original, s.original, s.n);
                break;
            }
    for (auto& [addr, ts] : t.touched) {
        Touched& x = touched[addr];
        x.n = ts.n;
        std::memcpy(x.original, ts.original, ts.n);
    }
    for (auto& [addr, ts] : t.slots) {
        Touched& x = touched[addr];
        x.n = ts.n;
        std::memcpy(x.original, ts.original, ts.n);
    }
    std::string problem;
    uint32_t wrong = 0;
    uintptr_t first = 0;
    auto bad = [&](uintptr_t addr, const char* what) {
        if (wrong++ == 0) {
            first = addr;
            problem = what;
        }
    };
    // take off what was written since the start, put back what was there
    for (auto& [addr, x] : touched) {
        auto st = t.slots.find(addr);
        auto sl = g_slots.find(addr);
        if (st == t.slots.end()) {
            if (sl == g_slots.end()) continue;
            std::erase_if(sl->second.layers, [&](const Layer& l) { return l.owner == owner; });
            mark(addr, sl->second);
            continue;
        }
        const TxnSlot& ts = st->second;
        if (sl == g_slots.end()) {  // the slot went (the owner's layer was dropped, nothing else held it): as it was first made
            Slot s;
            s.kind = ts.kind, s.id = ts.id, s.field = ts.field, s.sub = ts.sub, s.n = ts.n;
            std::memcpy(s.original, ts.original, ts.n);
            if (!rd(addr, s.current, ts.n)) {
                bad(addr, "not readable");
                continue;
            }
            sl = g_slots.emplace(addr, std::move(s)).first;
        } else if (sl->second.n != ts.n) {
            bad(addr, "a slot of another size");
            continue;
        }
        Slot& s = sl->second;
        std::erase_if(s.layers, [&](const Layer& l) { return l.owner == owner; });
        s.layers.push_back(ts.layer);
        if (!is_registry_seq(ts.layer.seq)) ++g_below_gen;
        std::stable_sort(s.layers.begin(), s.layers.end(), [](const Layer& a, const Layer& b) { return a.seq < b.seq; });
        mark(addr, s);
    }
    std::string werr;
    store_flush(&werr);
    if (settle) settle();
    // read back: the owner's layers are the start's, and memory holds what the layers say
    for (auto& [addr, x] : touched) {
        auto st = t.slots.find(addr);
        auto sl = g_slots.find(addr);
        const Layer* mine = nullptr;
        if (sl != g_slots.end())
            for (const Layer& l : sl->second.layers)
                if (l.owner == owner) mine = &l;
        if (st != t.slots.end()) {
            if (!mine || mine->seq != st->second.layer.seq || std::memcmp(mine->bytes, st->second.layer.bytes, x.n) != 0) {
                bad(addr, "the owner's layer is not the one it had");
                continue;
            }
        } else if (mine) {
            bad(addr, "a layer written in the transaction is still there");
            continue;
        }
        uint8_t folded[kSlotBytes] = {};
        if (sl != g_slots.end()) fold(sl->second, folded);
        const uint8_t* want = sl == g_slots.end() ? x.original : folded;
        uint8_t now[kSlotBytes] = {};
        if (!rd(addr, now, x.n)) bad(addr, "not readable");
        else if (std::memcmp(now, want, x.n) != 0) bad(addr, "memory does not hold what its layers say");
    }
    if (fields) *fields = static_cast<uint32_t>(touched.size());
    if (wrong == 0) return TxnOutcome::Restored;
    if (why) {
        char b[160];
        std::snprintf(b, sizeof b, "%u of %zu field(s) did not read back after the rollback (first at %p: %s)", wrong, touched.size(),
                      reinterpret_cast<void*>(first), problem.c_str());
        *why = b;
        if (!werr.empty()) *why += "; writing: " + werr;
    }
    return TxnOutcome::Unverified;
}

bool store_txn_open(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    for (auto& [tid, t] : g_txns)
        if (t.owner == owner) return true;
    return false;
}

namespace {
std::string slot_what(const Slot& s, std::string* field_out, const Field** f_out) {
#if KC_GAMEDATA
    if (s.kind >= GD_KIND_BASE) {
        *f_out = nullptr;
        return gd_slot_what(s, field_out);
    }
#endif
    const Registry& r = reg(s.kind);
    const Entry* e = r.get(s.id);
    std::string what = std::string(r.label) + " " + (e ? e->name : std::to_string(s.id));
    std::string field;
    *f_out = nullptr;
    if (s.field >= 0 && static_cast<size_t>(s.field) < r.schema.fields.size()) {
        *f_out = &r.schema.fields[static_cast<size_t>(s.field)];
        field = (*f_out)->path;
        if (s.kind == KILN_CONTENT_DROPS) field = "[" + std::to_string(s.sub) + "]." + field;
    } else {
        field = s.sub == kSubBlob ? "list" : s.sub == kSubUnlock ? "knowledgeRequirement" : s.sub == KILN_CONTENT_OUTPUTS ? "outputs" : "inputs";
    }
    *field_out = field;
    return what + "." + field;
}
std::string layer_value(const Slot& s, const Field* f, const uint8_t* bytes) {
#if KC_GAMEDATA
    if (s.kind >= GD_KIND_BASE) return gd_layer_value(s, bytes);
#endif
    if (!f) {
        uint32_t n = 0;
        std::memcpy(&n, bytes + 4, 4);
        return "list of " + std::to_string(n);
    }
    return show(*f, decode(*f, bytes));
}
}  // namespace

// The mod a layer belongs to: the one a shared owner put it for, else its owner's ("mod <id>" code, "pack <id>" data
// packs). Empty: Kiln's own, or not known. Under g_mu.
static std::string layer_mod(const Layer& l) {
    if (!l.mod.empty()) return l.mod;
    const auto it = g_labels.find(l.owner);
    if (it == g_labels.end()) return {};
    for (const char* p : {"mod ", "pack "})
        if (it->second.rfind(p, 0) == 0) return it->second.substr(std::strlen(p));
    return {};
}

// Two layers of one slot that disagree. A clone's definition and its own mod's later layers on it are not a conflict: they
// are one author's, the definition being the clone's starting value (a block writes none until a game VFX's name is known,
// then a rule writes that VFX). Between other layers, a mod's own pack and code included, a disagreement is reported.
std::vector<Conflict> store_conflicts() {
    PhaseTimer pt(PH_CONFLICTS);
    std::lock_guard lock(g_mu);
    std::vector<Conflict> out;
    for (auto& [addr, s] : g_slots) {
        if (s.layers.size() < 2) continue;
        uint8_t scratch[kSlotBytes];
        fold(s, scratch);  // evaluated layers' bytes as they are now
        size_t top = s.layers.size();
        while (top > 0 && excluded(s, s.layers[top - 1])) --top;
        if (top < 2) continue;
        const Layer& win = s.layers[top - 1];
        const std::string win_mod = layer_mod(win);
        std::string field;
        const Field* f = nullptr;
        std::string what;
        for (size_t i = 0; i + 1 < top; ++i) {
            const Layer& l = s.layers[i];
            if (excluded(s, l)) continue;
            if (l.owner == win.owner || std::memcmp(l.bytes, win.bytes, s.n) == 0) continue;
            if ((!l.mod.empty() || !win.mod.empty()) && !win_mod.empty() && layer_mod(l) == win_mod) continue;
            if (what.empty()) what = slot_what(s, &field, &f);
            out.push_back(Conflict{what, win.source, l.source, layer_value(s, f, win.bytes), layer_value(s, f, l.bytes)});
        }
    }
    return out;
}

std::vector<StoreSkipped> store_skipped() {
    std::lock_guard lock(g_mu);
    std::vector<StoreSkipped> out;
    for (const SkippedRaw& r : g_skipped) {
        Slot s;
        s.kind = r.kind, s.id = r.id, s.field = r.field, s.sub = r.sub;
        std::string field;
        const Field* f = nullptr;
        StoreSkipped x;
        x.addr = r.addr, x.kind = r.kind, x.id = r.id, x.mod = r.mod, x.source = r.source, x.revert = r.revert;
        x.what = slot_what(s, &field, &f);  // game debug and member names: diagnostics only, never a report row (P32)
        // the registry's report names the Kiln place instead (registry_live.cpp skipped_text)
        x.text = std::string("a slot Kiln wrote was written by another writer after Kiln's layer: Kiln's ") + (r.revert ? "revert" : "edit") + " not applied";
        out.push_back(std::move(x));
    }
    return out;
}

// ============================================================================================= values ===========

KilnContentValue current_value(const Entry& e, const Field& f) {
    uint8_t b[16] = {};
    rd(e.addr + f.offset, b, f.bytes);
    return decode(f, b);
}

KilnContentValue original_value(const Entry& e, const Field& f) {
    uint8_t b[16] = {};
    if (f.bytes > kSlotBytes || !store_original(e.addr + f.offset, f.bytes, b)) rd(e.addr + f.offset, b, f.bytes);
    return decode(f, b);
}

KilnResult set_field(KilnOwner owner, uint64_t seq, uint32_t kind, const Entry& e, const Field& f,
                     const KilnContentValue& v, const std::string& source, std::string* why, int32_t sub) {
    uint8_t b[8] = {};
    if (f.bytes > 8 || !encode(f, v, b, why)) return KILN_ERR_INVALID_ARGUMENT;
    const Registry& r = reg(kind);
    const int32_t index = static_cast<int32_t>(&f - r.schema.fields.data());
    if (!store_put(owner, seq, kind, e.id, index, sub, e.addr + f.offset, f.bytes, b, source)) {
        if (why) *why = "the field is not readable";
        return KILN_ERR_INTERNAL;
    }
    return KILN_OK;
}

KilnResult recipe_list_header(const Entry& e, uint32_t which, const std::vector<KilnContentStack>& stacks, uint8_t* hb,
                              std::string* why) {
    if (which > KILN_CONTENT_OUTPUTS || stacks.empty() || stacks.size() > 32) {
        *why = "a recipe list has 1..32 entries";
        return KILN_ERR_INVALID_ARGUMENT;
    }
    const Registry& items = reg(KILN_CONTENT_ITEMS);
    const bool in = which == KILN_CONTENT_INPUTS;
    const uint32_t stride = in ? RL.in_stride : RL.out_stride;
    std::vector<uint8_t> buf(stride * stacks.size(), 0);
    for (size_t i = 0; i < stacks.size(); ++i) {
        const KilnContentStack& s = stacks[i];
        uint8_t* p = buf.data() + i * stride;
        if (s.count == 0 || s.count > 65535) {
            *why = "recipe amounts are 1..65535";
            return KILN_ERR_INVALID_ARGUMENT;
        }
        if (s.item) {
            const Entry* it = items.get(s.item);
            if (!it) {
                char b[64];
                std::snprintf(b, sizeof b, "item 0x%08x is not in the item registry", s.item);
                *why = b;
                return KILN_ERR_NOT_FOUND;
            }
            std::memcpy(p + (in ? RL.in_item_ref : RL.out_item_ref), it->guid, 16);
            std::memcpy(p + (in ? RL.in_item : RL.out_item), &s.item, 4);
            std::memcpy(p + (in ? RL.in_count : RL.out_count), &s.count, 4);
        } else if (in && s.category) {
            auto c = g_category_guid.find(s.category);
            if (c == g_category_guid.end()) {
                char b[80];
                std::snprintf(b, sizeof b, "input category 0x%08x is not used by any recipe", s.category);
                *why = b;
                return KILN_ERR_NOT_FOUND;
            }
            std::memcpy(p + RL.in_cat_ref, c->second.data(), 16);
            std::memcpy(p + RL.in_cat, &s.category, 4);
            std::memcpy(p + RL.in_cat_count, &s.count, 4);
        } else {
            *why = in ? "an input needs an item or a category" : "an output needs an item";
            return KILN_ERR_INVALID_ARGUMENT;
        }
    }
    const uintptr_t header = e.addr + (in ? RL.input : RL.output);
    auto* pool = static_cast<uint8_t*>(near_alloc(header, buf.size()));
    if (!pool) {
        *why = "no memory near the recipe data";
        return KILN_ERR_INTERNAL;
    }
    std::memcpy(pool, buf.data(), buf.size());
    const intptr_t rel = reinterpret_cast<intptr_t>(pool) - static_cast<intptr_t>(header);
    if (rel > INT32_MAX || rel < INT32_MIN) {
        *why = "recipe data out of reach";
        return KILN_ERR_INTERNAL;
    }
    const int32_t off = static_cast<int32_t>(rel);
    const uint32_t n = static_cast<uint32_t>(stacks.size());
    std::memcpy(hb, &off, 4);
    std::memcpy(hb + 4, &n, 4);
    return KILN_OK;
}

KilnResult set_recipe_stacks(KilnOwner owner, uint64_t seq, const Entry& e, uint32_t which,
                             const std::vector<KilnContentStack>& stacks, const std::string& source, std::string* why) {
    uint8_t hb[8];
    if (const KilnResult r = recipe_list_header(e, which, stacks, hb, why); r != KILN_OK) return r;
    const uintptr_t header = e.addr + (which == KILN_CONTENT_INPUTS ? RL.input : RL.output);
    if (!store_put(owner, seq, KILN_CONTENT_RECIPES, e.id, -1, static_cast<int32_t>(which), header, 8, hb, source)) {
        *why = "the recipe is not readable";
        return KILN_ERR_INTERNAL;
    }
    return KILN_OK;
}

KilnResult set_drop_stacks(KilnOwner owner, uint64_t seq, const Entry& e, const std::vector<KilnContentStack>& stacks,
                           const std::string& source, std::string* why) {
    std::vector<DropStackRef> refs;
    walk_drop_table(e.addr, &refs);
    if (refs.size() != stacks.size()) {
        *why = "drop tables keep their stacks: give as many as stacks() returns";
        return KILN_ERR_INVALID_ARGUMENT;
    }
    const Schema& sc = reg(KILN_CONTENT_DROPS).schema;
    const Field* fmin = sc.find("countMin");
    const Field* fmax = sc.find("countMax");
    const Field* fch = sc.find("spawnProbability");
    const Field* fw = sc.find("weight");
    if (!fmin || !fmax || !fch || !fw) {
        *why = "drop-table fields missing";
        return KILN_ERR_UNAVAILABLE;
    }
    for (size_t i = 0; i < refs.size(); ++i) {
        KilnContentStack now{};
        read_drop_stack(refs[i].addr, refs[i].group, &now);
        const KilnContentStack& s = stacks[i];
        if (s.item != now.item) {
            *why = "drop tables keep their items (stack " + std::to_string(i) + ")";
            return KILN_ERR_INVALID_ARGUMENT;
        }
        if (s.count > s.count_max || s.count_max > 100000 || !(s.chance >= 0 && s.chance <= 1) || !(s.weight >= 0)) {
            *why = "stack " + std::to_string(i) + ": need countMin <= countMax <= 100000, chance 0..1, weight >= 0";
            return KILN_ERR_INVALID_ARGUMENT;
        }
    }
    for (size_t i = 0; i < refs.size(); ++i) {
        KilnContentStack now{};
        read_drop_stack(refs[i].addr, refs[i].group, &now);
        const KilnContentStack& s = stacks[i];
        auto put = [&](const Field* f, const void* v) {
            const int32_t idx = static_cast<int32_t>(f - sc.fields.data());
            store_put(owner, seq, KILN_CONTENT_DROPS, e.id, idx, static_cast<int32_t>(i), refs[i].addr + f->offset, 4,
                      static_cast<const uint8_t*>(v), source);
        };
        // Only fields that differ from the original get a layer (or the owner's layer is dropped).
        uint8_t ob[4];
        auto handle = [&](const Field* f, const void* v) {
            const uintptr_t a = refs[i].addr + f->offset;
            if (!store_original(a, 4, ob)) rd(a, ob, 4);
            if (std::memcmp(ob, v, 4) == 0) store_drop(owner, a);
            else put(f, v);
        };
        handle(fmin, &s.count);
        handle(fmax, &s.count_max);
        handle(fch, &s.chance);
        handle(fw, &s.weight);
    }
    return KILN_OK;
}

// ============================================================================================= stack guard =====

bool guard_on() { return g_guard; }

bool guard_set(bool on, std::string* why) {
    if (!E.guard || !E.stack_clamp) {
        if (why) *why = "the stack guard is not available on this build";
        return false;
    }
    if (on == g_guard) return true;
    const bool ok = on ? patch_code_byte(E.stack_clamp, 0x76, 0xEB) : patch_code_byte(E.stack_clamp, 0xEB, 0x76);
    if (!ok) {
        if (why) *why = "the stack clamp's code is not what kiln.content expects";
        return false;
    }
    g_guard = on;
    LOGI("stack guard %s: the game %s cut stacks down to their item's max stack size", on ? "on" : "off",
         on ? "no longer" : "again");
    return true;
}

void guard_maybe(const Field& f, uint32_t kind, const uint8_t* orig, const uint8_t* now) {
    if (g_guard || kind != KILN_CONTENT_ITEMS || !E.guard) return;
    if (decode(f, now).i > decode(f, orig).i) {
        std::string why;
        if (!guard_set(true, &why)) LOGW("stack guard: %s (reverting a raised max stack size later cuts bigger stacks)", why.c_str());
    }
}

}  // namespace kc
