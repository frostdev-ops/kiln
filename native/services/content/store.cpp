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

void mark(uintptr_t addr, Slot& s) {
    ++g_gen;
    if (s.dirty) return;
    s.dirty = true;
    g_dirty.push_back(addr);
}
std::unordered_map<KilnOwner, std::string> g_labels;
std::atomic<uint64_t> g_code_seq{0};
bool g_guard = false;

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

bool store_put(KilnOwner owner, uint64_t seq, uint32_t kind, uint32_t id, int32_t field, int32_t sub, uintptr_t addr,
               uint8_t n, const uint8_t* bytes, const std::string& source) {
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
    } else if (s.n != n) {
        return false;
    }
    auto l = std::find_if(s.layers.begin(), s.layers.end(), [&](const Layer& x) { return x.owner == owner; });
    if (l == s.layers.end()) {
        s.layers.push_back(Layer{owner, seq, {}, source});
        l = s.layers.end() - 1;
    }
    l->seq = seq;
    l->source = source;
    l->mod = t_layer_mod;
    std::memcpy(l->bytes, bytes, n);
    std::sort(s.layers.begin(), s.layers.end(), [](const Layer& a, const Layer& b) { return a.seq < b.seq; });
    mark(addr, s);
    return true;
}

bool store_drop(KilnOwner owner, uintptr_t addr) {
    std::lock_guard lock(g_mu);
    auto it = g_slots.find(addr);
    if (it == g_slots.end()) return false;
    auto& ls = it->second.layers;
    const size_t before = ls.size();
    std::erase_if(ls, [&](const Layer& x) { return x.owner == owner; });
    if (ls.size() == before) return false;
    mark(addr, it->second);
    return true;
}

uint32_t store_drop_owner(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    uint32_t n = 0;
    for (auto& [addr, s] : g_slots) {
        const size_t before = s.layers.size();
        std::erase_if(s.layers, [&](const Layer& x) { return x.owner == owner; });
        if (s.layers.size() != before) {
            mark(addr, s);
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
    uint32_t n = 0;
    for (auto& [addr, s] : g_slots) {
        if (!s.layers.empty()) ++n;
        s.layers.clear();
        mark(addr, s);
    }
    if (revert) store_flush(error);
    g_slots.clear();
    g_dirty.clear();
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

bool store_original(uintptr_t addr, uint8_t n, uint8_t* out) {
    std::lock_guard lock(g_mu);
    auto it = g_slots.find(addr);
    if (it == g_slots.end() || it->second.n != n) return false;
    std::memcpy(out, it->second.original, n);
    return true;
}

size_t store_flush(std::string* error) {
    PhaseTimer pt(PH_FLUSH);
    std::lock_guard lock(g_mu);
    std::vector<Write> writes;
    std::vector<uintptr_t> addrs;
    std::vector<std::map<uintptr_t, Slot>::iterator> dirty;
    dirty.reserve(g_dirty.size());
    for (uintptr_t a : g_dirty)
        if (auto it = g_slots.find(a); it != g_slots.end() && it->second.dirty) dirty.push_back(it);
    g_dirty.clear();
    for (auto& d : dirty) {
        const uintptr_t addr = d->first;
        Slot& s = d->second;
        const uint8_t* want = s.layers.empty() ? s.original : s.layers.back().bytes;
        if (std::memcmp(want, s.current, s.n) != 0) {
            Write w{addr, s.n, {}};
            std::memcpy(w.bytes, want, s.n);
            writes.push_back(w);
            addrs.push_back(addr);
        }
    }
    uintptr_t failed = 0;
    size_t done = 0;
    if (!writes.empty()) {
        PhaseTimer wt(PH_WRITE_BATCH);
        done = write_batch(writes, &failed);
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
        if (s.layers.empty() && std::memcmp(s.current, s.original, s.n) == 0) g_slots.erase(it);
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
        field = s.sub == kSubBlob ? "list" : s.sub == KILN_CONTENT_OUTPUTS ? "outputs" : "inputs";
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
        const Layer& win = s.layers.back();
        const std::string win_mod = layer_mod(win);
        std::string field;
        const Field* f = nullptr;
        std::string what;
        for (size_t i = 0; i + 1 < s.layers.size(); ++i) {
            const Layer& l = s.layers[i];
            if (l.owner == win.owner || std::memcmp(l.bytes, win.bytes, s.n) == 0) continue;
            if ((!l.mod.empty() || !win.mod.empty()) && !win_mod.empty() && layer_mod(l) == win_mod) continue;
            if (what.empty()) what = slot_what(s, &field, &f);
            out.push_back(Conflict{what, win.source, l.source, layer_value(s, f, win.bytes), layer_value(s, f, l.bytes)});
        }
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

KilnResult set_recipe_stacks(KilnOwner owner, uint64_t seq, const Entry& e, uint32_t which,
                             const std::vector<KilnContentStack>& stacks, const std::string& source, std::string* why) {
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
    uint8_t hb[8];
    const int32_t off = static_cast<int32_t>(rel);
    const uint32_t n = static_cast<uint32_t>(stacks.size());
    std::memcpy(hb, &off, 4);
    std::memcpy(hb + 4, &n, 4);
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
