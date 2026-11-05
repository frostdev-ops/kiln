// SPDX-License-Identifier: MIT
// kiln.content: the layer store. Every changed field (or recipe list header) is a slot keyed by its address in game
// memory; the slot keeps the game's original bytes and one layer per owner. The newest layer (highest seq) wins;
// with no layer left the original comes back. Writes go out in batches (store_flush).
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <set>

#include "content.h"
#if KC_GAMEDATA
#include "gamedata.h"
#endif

namespace kc {

namespace {
std::recursive_mutex g_mu;
std::map<uintptr_t, Slot> g_slots;
std::unordered_map<KilnOwner, std::string> g_labels;
std::atomic<uint64_t> g_code_seq{0};
bool g_guard = false;
}  // namespace

std::recursive_mutex& store_mutex() { return g_mu; }
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
    std::memcpy(l->bytes, bytes, n);
    std::sort(s.layers.begin(), s.layers.end(), [](const Layer& a, const Layer& b) { return a.seq < b.seq; });
    s.dirty = true;
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
    it->second.dirty = true;
    return true;
}

uint32_t store_drop_owner(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    uint32_t n = 0;
    for (auto& [addr, s] : g_slots) {
        const size_t before = s.layers.size();
        std::erase_if(s.layers, [&](const Layer& x) { return x.owner == owner; });
        if (s.layers.size() != before) {
            s.dirty = true;
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
        s.dirty = true;
    }
    if (revert) store_flush(error);
    g_slots.clear();
    return n;
}

bool store_has(uintptr_t addr) {
    std::lock_guard lock(g_mu);
    return g_slots.count(addr) != 0;
}

bool store_original(uintptr_t addr, uint8_t n, uint8_t* out) {
    std::lock_guard lock(g_mu);
    auto it = g_slots.find(addr);
    if (it == g_slots.end() || it->second.n != n) return false;
    std::memcpy(out, it->second.original, n);
    return true;
}

size_t store_flush(std::string* error) {
    std::lock_guard lock(g_mu);
    std::vector<Write> writes;
    std::vector<uintptr_t> addrs;
    for (auto& [addr, s] : g_slots) {
        if (!s.dirty) continue;
        const uint8_t* want = s.layers.empty() ? s.original : s.layers.back().bytes;
        if (std::memcmp(want, s.current, s.n) != 0) {
            Write w{addr, s.n, {}};
            std::memcpy(w.bytes, want, s.n);
            writes.push_back(w);
            addrs.push_back(addr);
        }
    }
    uintptr_t failed = 0;
    const size_t done = writes.empty() ? 0 : write_batch(writes, &failed);
    if (failed && error) {
        char b[96];
        std::snprintf(b, sizeof b, "%zu of %zu writes failed (first at %p)", writes.size() - done, writes.size(),
                      reinterpret_cast<void*>(failed));
        *error = b;
    }
    // What is in memory now, whatever happened.
    const Registry& items = reg(KILN_CONTENT_ITEMS);
    const Field* max_stack = items.schema.find("maxStackSize");
    for (auto it = g_slots.begin(); it != g_slots.end();) {
        Slot& s = it->second;
        if (s.dirty) {
            uint8_t now[kSlotBytes] = {};
            if (rd(it->first, now, s.n)) std::memcpy(s.current, now, s.n);
            s.dirty = false;
            if (max_stack && s.kind == KILN_CONTENT_ITEMS && s.field >= 0 &&
                &items.schema.fields[static_cast<size_t>(s.field)] == max_stack)
                guard_maybe(*max_stack, s.kind, s.original, s.current);
        }
        if (s.layers.empty() && std::memcmp(s.current, s.original, s.n) == 0) it = g_slots.erase(it);
        else ++it;
    }
    return done;
}

uint32_t store_changed() {
    std::lock_guard lock(g_mu);
    uint32_t n = 0;
    for (auto& [addr, s] : g_slots) n += std::memcmp(s.current, s.original, s.n) != 0;
    return n;
}

void store_for_each(const std::function<void(uintptr_t, const Slot&)>& fn) {
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

std::vector<Conflict> store_conflicts() {
    std::lock_guard lock(g_mu);
    std::vector<Conflict> out;
    for (auto& [addr, s] : g_slots) {
        if (s.layers.size() < 2) continue;
        const Layer& win = s.layers.back();
        std::string field;
        const Field* f = nullptr;
        std::string what;
        for (size_t i = 0; i + 1 < s.layers.size(); ++i) {
            const Layer& l = s.layers[i];
            if (l.owner == win.owner || std::memcmp(l.bytes, win.bytes, s.n) == 0) continue;
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
