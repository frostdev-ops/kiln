// SPDX-License-Identifier: MIT
// Resource builders (questbuild.h): knowledge maps, journal, NPC roster, template edits. Layouts: docs/re/quests.md
// sections 1 and 2, docs/re/creatures.md section 7 (reflection of svn 1287369).
#include "questbuild.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <map>

namespace kiln::qb {

namespace {

constexpr uint32_t kLineMaxBodies = 32;
constexpr size_t kWorldObj = 12, kPlayerObj = 24;
constexpr size_t kMapWorld = 0, kMapPlayer = 0x18;    // GameKnowledgeResource: the two maps
constexpr size_t kQuest = 44, kEntry = 72, kNpc = 136;
constexpr size_t kInitLine = 68, kTransLine = 64, kFareLine = 64, kOptLine = 68;
constexpr size_t kDialogRes = 0x14;  // NpcDialog.dialogResource

uint32_t rd32(const uint8_t* p) {
    uint32_t v;
    std::memcpy(&v, p, 4);
    return v;
}
void wr32(uint8_t* p, uint32_t v) { std::memcpy(p, &v, 4); }
void wrf(uint8_t* p, float v) { std::memcpy(p, &v, 4); }

// A BlobArray / BlobString header {i32 self-relative offset, u32 count} at `f`: the element position.
bool arr(const uint8_t* b, size_t len, size_t f, size_t* at, uint32_t* n) {
    if (f + 8 > len) return false;
    const int32_t rel = static_cast<int32_t>(rd32(b + f));
    *n = rd32(b + f + 4);
    if (*n == 0) {
        *at = 0;
        return true;
    }
    const int64_t a = static_cast<int64_t>(f) + rel;
    if (a < 0 || static_cast<size_t>(a) > len) return false;
    *at = static_cast<size_t>(a);
    return true;
}
bool arr(const Bytes& b, size_t f, size_t* at, uint32_t* n) { return arr(b.data(), b.size(), f, at, n); }
void set_arr(Bytes& b, size_t f, size_t at, uint32_t n) {
    wr32(b.data() + f, n ? static_cast<uint32_t>(static_cast<int64_t>(at) - static_cast<int64_t>(f)) : 0u);
    wr32(b.data() + f + 4, n);
}
// Reserves `size` zero bytes at the end, aligned; returns where.
size_t reserve(Bytes& b, size_t size, size_t align) {
    size_t o = b.size();
    o = (o + align - 1) / align * align;
    b.resize(o + size);
    return o;
}
void put_query(uint8_t* p, const Query& q) {
    wr32(p, q.id);
    wr32(p + 4, q.value);
    p[8] = q.cmp;
    p[9] = q.type;
    p[10] = q.explicit_player;
    p[11] = 0;
}

// Points the array field `f` at a copy with room for `extra` more elements (zeroed at the end of the copy). Inner
// BlobArrays / BlobStrings of the moved elements (`inner`: their offsets in the element) keep their targets.
bool grow_array(Bytes& b, size_t f, uint32_t extra, size_t stride, const std::vector<size_t>& inner, size_t* new_at,
                uint32_t* old_n, std::string* err) {
    size_t at = 0;
    uint32_t n = 0;
    if (!arr(b, f, &at, &n) || at + static_cast<uint64_t>(n) * stride > b.size()) {
        *err = "an array of the resource does not read";
        return false;
    }
    const size_t to = reserve(b, (n + static_cast<size_t>(extra)) * stride, 4);
    if (n) std::memcpy(b.data() + to, b.data() + at, n * stride);
    for (uint32_t i = 0; i < n; ++i)
        for (size_t o : inner) {
            uint8_t* field = b.data() + to + i * stride + o;
            const int32_t rel = static_cast<int32_t>(rd32(field));
            if (rel) wr32(field, static_cast<uint32_t>(rel + static_cast<int64_t>(at) - static_cast<int64_t>(to)));
        }
    set_arr(b, f, to, n + extra);
    *new_at = to;
    *old_n = n;
    return true;
}

template <class T>
void write_pod_array(Bytes& b, size_t field, const std::vector<T>& v, size_t elem, size_t align) {
    if (v.empty()) {
        set_arr(b, field, 0, 0);
        return;
    }
    const size_t at = reserve(b, v.size() * elem, align);
    std::memcpy(b.data() + at, v.data(), v.size() * elem);
    set_arr(b, field, at, static_cast<uint32_t>(v.size()));
}

char hexd(unsigned v) { return "0123456789abcdef"[v & 15]; }

// ---- SHA-1 (uuid5 only) ---------------------------------------------------------------------------------------------
void sha1(const uint8_t* data, size_t len, uint8_t out[20]) {
    uint32_t h[5] = {0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u};
    Bytes m(data, data + len);
    m.push_back(0x80);
    while (m.size() % 64 != 56) m.push_back(0);
    const uint64_t bits = static_cast<uint64_t>(len) * 8;
    for (int i = 7; i >= 0; --i) m.push_back(static_cast<uint8_t>(bits >> (i * 8)));
    auto rol = [](uint32_t v, int s) { return (v << s) | (v >> (32 - s)); };
    for (size_t o = 0; o < m.size(); o += 64) {
        uint32_t w[80];
        for (int i = 0; i < 16; ++i)
            w[i] = (uint32_t{m[o + i * 4]} << 24) | (uint32_t{m[o + i * 4 + 1]} << 16) | (uint32_t{m[o + i * 4 + 2]} << 8) |
                   m[o + i * 4 + 3];
        for (int i = 16; i < 80; ++i) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint32_t a = h[0], c1 = h[1], c = h[2], d = h[3], e = h[4];
        for (int i = 0; i < 80; ++i) {
            uint32_t f, k;
            if (i < 20) f = (c1 & c) | (~c1 & d), k = 0x5A827999u;
            else if (i < 40) f = c1 ^ c ^ d, k = 0x6ED9EBA1u;
            else if (i < 60) f = (c1 & c) | (c1 & d) | (c & d), k = 0x8F1BBCDCu;
            else f = c1 ^ c ^ d, k = 0xCA62C1D6u;
            const uint32_t t = rol(a, 5) + f + e + k + w[i];
            e = d, d = c, c = rol(c1, 30), c1 = a, a = t;
        }
        h[0] += a, h[1] += c1, h[2] += c, h[3] += d, h[4] += e;
    }
    for (int i = 0; i < 5; ++i)
        for (int j = 0; j < 4; ++j) out[i * 4 + j] = static_cast<uint8_t>(h[i] >> (24 - j * 8));
}

void swap_order(const uint8_t in[16], uint8_t out[16]) {  // network order <-> .NET order
    static constexpr int map[16] = {3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15};
    for (int i = 0; i < 16; ++i) out[i] = in[map[i]];
}

}  // namespace

bool Guid::zero() const {
    for (uint8_t x : b)
        if (x) return false;
    return true;
}
bool Guid::operator==(const Guid& o) const { return std::memcmp(b, o.b, 16) == 0; }

uint32_t fnv1a(const void* p, size_t n) {
    uint32_t h = 0x811c9dc5u;
    const auto* c = static_cast<const uint8_t*>(p);
    for (size_t i = 0; i < n; ++i) h = (h ^ c[i]) * 0x01000193u;
    return h;
}

bool parse_guid(const std::string& t, Guid* out) {
    if (t.size() != 36 || t[8] != '-' || t[13] != '-' || t[18] != '-' || t[23] != '-') return false;
    uint8_t net[16];
    int k = 0;
    for (size_t i = 0; i < 36 && k < 16;) {
        if (t[i] == '-') {
            ++i;
            continue;
        }
        auto dig = [](char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; };
        const int a = dig(t[i]), b = dig(t[i + 1]);
        if (a < 0 || b < 0) return false;
        net[k++] = static_cast<uint8_t>(a * 16 + b);
        i += 2;
    }
    if (k != 16) return false;
    swap_order(net, out->b);
    return true;
}

std::string guid_text(const Guid& g) {
    uint8_t net[16];
    swap_order(g.b, net);
    std::string s;
    for (int i = 0; i < 16; ++i) {
        if (i == 4 || i == 6 || i == 8 || i == 10) s.push_back('-');
        s.push_back(hexd(net[i] >> 4));
        s.push_back(hexd(net[i]));
    }
    return s;
}

Guid uuid5(const Guid& ns, const std::string& name) {
    Bytes in(16 + name.size());
    swap_order(ns.b, in.data());
    if (!name.empty()) std::memcpy(in.data() + 16, name.data(), name.size());
    uint8_t d[20];
    sha1(in.data(), in.size(), d);
    d[6] = static_cast<uint8_t>((d[6] & 0x0f) | 0x50);
    d[8] = static_cast<uint8_t>((d[8] & 0x3f) | 0x80);
    Guid g;
    swap_order(d, g.b);
    return g;
}

// ---- knowledge -----------------------------------------------------------------------------------------------------
namespace {
struct MapView {
    size_t buckets = 0, keys = 0, values = 0;
    uint32_t nb = 0, nk = 0, nv = 0;
};
bool map_view(const uint8_t* b, size_t len, size_t base, size_t stride, MapView* m) {
    if (!arr(b, len, base, &m->buckets, &m->nb) || !arr(b, len, base + 8, &m->keys, &m->nk) ||
        !arr(b, len, base + 16, &m->values, &m->nv))
        return false;
    if (m->nk != m->nv || !m->nb || (m->nb & (m->nb - 1))) return false;
    return m->buckets + uint64_t{m->nb} * 8 <= len && m->keys + uint64_t{m->nk} * 4 <= len &&
           m->values + uint64_t{m->nv} * stride <= len;
}

// Rebuilds one map: the objects in the game's order per bucket, the new ones behind them in their buckets.
bool rebuild_map(Bytes& b, size_t base, size_t stride, const std::vector<std::vector<uint8_t>>& add, std::string* err) {
    MapView m;
    if (!map_view(b.data(), b.size(), base, stride, &m)) {
        *err = "a knowledge map does not read";
        return false;
    }
    uint32_t nb = m.nb;
    const uint64_t total = uint64_t{m.nk} + add.size();
    while (total > nb) nb *= 2;
    struct Obj {
        uint32_t key;
        const uint8_t* value;
    };
    std::vector<std::vector<Obj>> buckets(nb);
    // the game's order: bucket by bucket; a doubled table splits buckets stably
    for (uint32_t i = 0; i < m.nb; ++i) {
        const uint32_t first = rd32(b.data() + m.buckets + i * 8), count = rd32(b.data() + m.buckets + i * 8 + 4);
        if (uint64_t{first} + count > m.nk) {
            *err = "a knowledge bucket points outside its map";
            return false;
        }
        for (uint32_t k = first; k < first + count; ++k) {
            const uint32_t key = rd32(b.data() + m.keys + k * 4);
            buckets[key & (nb - 1)].push_back(Obj{key, b.data() + m.values + k * stride});
        }
    }
    for (const auto& a : add) {
        const uint32_t key = rd32(a.data());
        buckets[key & (nb - 1)].push_back(Obj{key, a.data()});
    }
    Bytes e(uint64_t{nb} * 8), k(total * 4), v(total * stride);
    uint32_t pos = 0;
    for (uint32_t i = 0; i < nb; ++i) {
        wr32(e.data() + uint64_t{i} * 8, buckets[i].empty() ? 0u : pos);
        wr32(e.data() + uint64_t{i} * 8 + 4, static_cast<uint32_t>(buckets[i].size()));
        for (const Obj& o : buckets[i]) {
            wr32(k.data() + uint64_t{pos} * 4, o.key);
            std::memcpy(v.data() + uint64_t{pos} * stride, o.value, stride);
            ++pos;
        }
    }
    const size_t at_e = reserve(b, e.size(), 4);
    std::memcpy(b.data() + at_e, e.data(), e.size());
    set_arr(b, base, at_e, nb);
    const size_t at_k = reserve(b, k.size(), 4);
    std::memcpy(b.data() + at_k, k.data(), k.size());
    set_arr(b, base + 8, at_k, static_cast<uint32_t>(total));
    const size_t at_v = reserve(b, v.size(), 4);
    std::memcpy(b.data() + at_v, v.data(), v.size());
    set_arr(b, base + 16, at_v, static_cast<uint32_t>(total));
    return true;
}

const uint8_t* map_find(const uint8_t* b, size_t len, size_t base, size_t stride, uint32_t id) {
    MapView m;
    if (!map_view(b, len, base, stride, &m)) return nullptr;
    const uint32_t i = id & (m.nb - 1);
    const uint32_t first = rd32(b + m.buckets + uint64_t{i} * 8), count = rd32(b + m.buckets + uint64_t{i} * 8 + 4);
    for (uint32_t k = first; k < first + count && k < m.nk; ++k)
        if (rd32(b + m.keys + uint64_t{k} * 4) == id) return b + m.values + uint64_t{k} * stride;
    return nullptr;
}
}  // namespace

bool knowledge_counts(const uint8_t* res, size_t len, uint32_t* world, uint32_t* player) {
    MapView w, p;
    if (!map_view(res, len, kMapWorld, kWorldObj, &w) || !map_view(res, len, kMapPlayer, kPlayerObj, &p)) return false;
    *world = w.nk;
    *player = p.nk;
    return true;
}

const uint8_t* knowledge_find(const uint8_t* res, size_t len, bool player, uint32_t id) {
    return map_find(res, len, player ? kMapPlayer : kMapWorld, player ? kPlayerObj : kWorldObj, id);
}

bool rebuild_knowledge(const uint8_t* res, size_t len, const std::vector<KnowledgeDef>& defs, Bytes* out, std::string* err) {
    if (len < 72) {
        *err = "GameKnowledgeResource is too small";
        return false;
    }
    std::vector<std::vector<uint8_t>> world, player;
    std::map<uint32_t, const KnowledgeDef*> seen;
    for (const KnowledgeDef& d : defs) {
        if (!d.id) {
            *err = "knowledge " + d.name + ": id 0";
            return false;
        }
        auto [it, fresh] = seen.emplace(d.id, &d);
        if (!fresh) {
            char t[160];
            std::snprintf(t, sizeof t, "knowledge ids collide: %s and %s are both 0x%08x", d.name.c_str(), it->second->name.c_str(), d.id);
            *err = t;
            return false;
        }
        if ((d.world && knowledge_find(res, len, false, d.id)) || (d.player && knowledge_find(res, len, true, d.id))) {
            char t[160];
            std::snprintf(t, sizeof t, "knowledge %s: id 0x%08x is one of the game's", d.name.c_str(), d.id);
            *err = t;
            return false;
        }
        const bool twin = d.world && d.player;
        if (d.world) {
            std::vector<uint8_t> o(kWorldObj);
            wr32(o.data(), d.id);
            o[5] = twin ? 1 : 0;  // hasOptionalPlayerProgression
            o[6] = 0;             // isPlayerKnowledge
            o[7] = d.type;
            wr32(o.data() + 8, d.expected_flags);
            world.push_back(std::move(o));
        }
        if (d.player) {
            std::vector<uint8_t> o(kPlayerObj);
            wr32(o.data(), d.id);
            o[5] = twin ? 1 : 0;
            o[6] = 1;
            o[7] = d.type;
            wr32(o.data() + 8, d.expected_flags);
            wr32(o.data() + 0x10, d.experience_id);
            o[0x14] = d.skill_points;
            player.push_back(std::move(o));
        }
    }
    out->assign(res, res + len);
    return rebuild_map(*out, kMapWorld, kWorldObj, world, err) && rebuild_map(*out, kMapPlayer, kPlayerObj, player, err);
}

// ---- journal -------------------------------------------------------------------------------------------------------
bool rebuild_journal(const uint8_t* res, size_t len, const std::vector<JournalQuestDef>& quests, Bytes* out, std::string* err) {
    if (len < 32) {
        *err = "JournalRegistryResource is too small";
        return false;
    }
    out->assign(res, res + len);
    Bytes& b = *out;
    size_t at = 0;
    uint32_t n = 0;
    if (!grow_array(b, 0, static_cast<uint32_t>(quests.size()), kQuest, {0x18, 0x24}, &at, &n, err)) return false;
    for (size_t q = 0; q < quests.size(); ++q) {
        const JournalQuestDef& d = quests[q];
        const size_t entries = d.steps.empty() ? 0 : reserve(b, d.steps.size() * kEntry, 4);
        for (size_t s = 0; s < d.steps.size(); ++s) {
            const JournalStepDef& st = d.steps[s];
            uint8_t* e = b.data() + entries + s * kEntry;
            wr32(e, st.entry_id);
            wr32(e + 4, st.name);
            wr32(e + 8, st.text);
            wr32(e + 0xc, st.marker);
            put_query(e + 0x10, st.visible);
            put_query(e + 0x1c, st.done);
            std::memcpy(e + 0x30, st.voiceover.b, 16);
            wr32(e + 0x40, st.item_icon);
            e[0x44] = static_cast<uint8_t>(st.level);
            if (st.has_progress) {  // `e` is not used past this: the buffer may move
                const size_t pr = reserve(b, 12, 4);
                put_query(b.data() + pr, st.progress);
                set_arr(b, entries + s * kEntry + 0x28, pr, 1);
            }
        }
        const size_t f = at + (n + q) * kQuest;
        uint8_t* q0 = b.data() + f;
        wr32(q0, d.entry_id);
        wr32(q0 + 4, d.lore_category);
        wr32(q0 + 8, d.name);
        wr32(q0 + 0xc, d.document);
        wr32(q0 + 0x10, d.priority);
        set_arr(b, f + 0x18, entries, static_cast<uint32_t>(d.steps.size()));
        q0[0x20] = d.source;
        q0[0x21] = d.type;
        q0[0x22] = d.main;
        q0[0x23] = d.unlock_all;
    }
    return true;
}

// ---- NPC roster ----------------------------------------------------------------------------------------------------
const uint8_t* npc_find(const uint8_t* res, size_t len, uint32_t id) {
    size_t at = 0;
    uint32_t n = 0;
    if (!arr(res, len, 0, &at, &n) || at + uint64_t{n} * kNpc > len) return nullptr;
    for (uint32_t i = 0; i < n; ++i)
        if (rd32(res + at + uint64_t{i} * kNpc) == id) return res + at + uint64_t{i} * kNpc;
    return nullptr;
}

bool rebuild_npcs(const uint8_t* res, size_t len, const std::vector<NpcDef>& npcs, Bytes* out, std::string* err) {
    out->assign(res, res + len);
    Bytes& b = *out;
    size_t at = 0;
    uint32_t n = 0;
    if (!grow_array(b, 0, static_cast<uint32_t>(npcs.size()), kNpc, {0x80}, &at, &n, err)) return false;
    if (n + npcs.size() > kMaxNpcs) {
        char t[160];
        std::snprintf(t, sizeof t, "the roster holds at most %u NPCs (a 64-bit unlock mask): %u of the game's + %zu new", kMaxNpcs, n, npcs.size());
        *err = t;
        return false;
    }
    for (size_t i = 0; i < npcs.size(); ++i) {
        const NpcDef& d = npcs[i];
        if (npc_find(res, len, d.id)) {
            char t[160];
            std::snprintf(t, sizeof t, "NPC %s: NpcId 0x%08x is one of the game's", d.debug_name.c_str(), d.id);
            *err = t;
            return false;
        }
        const size_t f = at + (n + i) * kNpc;
        if (d.copy_from) {
            const uint8_t* base = npc_find(res, len, d.copy_from);
            if (!base) {
                char t[160];
                std::snprintf(t, sizeof t, "NPC %s: the base 0x%08x is not in the roster", d.debug_name.c_str(), d.copy_from);
                *err = t;
                return false;
            }
            std::memcpy(b.data() + f, base, kNpc);
            set_arr(b, f + 0x80, 0, 0);
        }
        uint8_t* e = b.data() + f;
        wr32(e, d.id);
        wr32(e + 4, d.name);
        e[8] = d.assistant ? 1 : 0;
        if (d.has_roaming) wrf(e + 0xc, d.roaming);
        put_query(e + 0x10, d.unlock);
        if (d.has_workshop) {
            wr32(e + 0x1c, d.workshop_id);
            if (d.workshop_label) wr32(e + 0x20, d.workshop_label);
        }
        wr32(e + 0x24, d.item_id);
        std::memcpy(e + 0x28, d.item_ref.b, 16);
        if (!d.debug_name.empty()) {
            const size_t s = reserve(b, d.debug_name.size(), 1);
            std::memcpy(b.data() + s, d.debug_name.data(), d.debug_name.size());
            set_arr(b, f + 0x80, s, static_cast<uint32_t>(d.debug_name.size()));
        }
    }
    return true;
}

// ---- templates -----------------------------------------------------------------------------------------------------
std::string template_name(const uint8_t* res, size_t len) {
    size_t at = 0;
    uint32_t n = 0;
    if (!arr(res, len, 0, &at, &n) || !n || at + n > len) return {};
    return std::string(reinterpret_cast<const char*>(res + at), n);
}

size_t template_component(const uint8_t* res, size_t len, uint32_t type_hash, uint32_t* size) {
    size_t at = 0;
    uint32_t n = 0;
    if (!arr(res, len, 0x10, &at, &n) || at + uint64_t{n} * 12 > len) return 0;
    for (uint32_t i = 0; i < n; ++i) {
        const size_t v = at + uint64_t{i} * 12;  // BlobVariant {u32 type, i32 rel (from +4), u32 size}
        if (rd32(res + v) != type_hash) continue;
        const int32_t rel = static_cast<int32_t>(rd32(res + v + 4));
        const int64_t c = static_cast<int64_t>(v) + 4 + rel;
        if (c <= 0 || static_cast<size_t>(c) >= len) return 0;
        if (size) *size = rd32(res + v + 8);
        return static_cast<size_t>(c);
    }
    return 0;
}

namespace {
// Writes `lines` as a new array of `stride` bytes per line at field `f`, behind the existing lines unless `replace`.
bool write_lines(Bytes& b, size_t f, const std::vector<LineDef>& lines, size_t stride, bool replace, std::string* err) {
    if (lines.empty() && !replace) return true;
    size_t at = 0;
    uint32_t n = 0;
    if (replace) {
        set_arr(b, f, 0, 0);
    }
    if (!grow_array(b, f, static_cast<uint32_t>(lines.size()), stride, {0x18, 0x2c, 0x34}, &at, &n, err)) return false;
    for (size_t i = 0; i < lines.size(); ++i) {
        const LineDef& l = lines[i];
        if (l.bodies.empty() || l.bodies.size() > kLineMaxBodies) {
            char t[120];
            std::snprintf(t, sizeof t, "a dialogue line has %zu texts (the client needs 1 to %u)", l.bodies.size(), kLineMaxBodies);
            *err = t;
            return false;
        }
        if (l.ops.size() > 8) {
            *err = "a dialogue line has more than 8 knowledge operations (the server runs at most 8 plus \"heard\")";
            return false;
        }
        const size_t pos = at + (n + i) * stride;
        // out-of-line data first: the buffer may move
        size_t ops_at = 0, bod_at = reserve(b, l.bodies.size() * 4, 4), voi_at = 0;
        std::memcpy(b.data() + bod_at, l.bodies.data(), l.bodies.size() * 4);
        if (!l.ops.empty()) {
            ops_at = reserve(b, l.ops.size() * 12, 4);
            for (size_t k = 0; k < l.ops.size(); ++k) {
                uint8_t* o = b.data() + ops_at + k * 12;
                wr32(o, l.ops[k].id);
                o[4] = l.ops[k].type;
                wr32(o + 8, l.ops[k].value);
            }
        }
        voi_at = reserve(b, l.bodies.size() * 16, 4);
        for (size_t k = 0; k < l.bodies.size(); ++k)
            if (k < l.voiceovers.size()) std::memcpy(b.data() + voi_at + k * 16, l.voiceovers[k].b, 16);
        uint8_t* e = b.data() + pos;
        wr32(e, l.id);
        e[4] = l.override_seq ? 1 : 0;
        std::memcpy(e + 8, l.sequence.b, 16);
        set_arr(b, pos + 0x18, ops_at, static_cast<uint32_t>(l.ops.size()));
        put_query(b.data() + pos + 0x20, l.requirement);
        set_arr(b, pos + 0x2c, bod_at, static_cast<uint32_t>(l.bodies.size()));
        set_arr(b, pos + 0x34, voi_at, static_cast<uint32_t>(l.bodies.size()));
        e = b.data() + pos;
        e[0x3c] = l.hint;
        e[0x3d] = l.start_action;
        e[0x3e] = l.end_action;
        if (stride > 64) wr32(e + 0x40, l.extra);
    }
    return true;
}
}  // namespace

bool edit_template(const uint8_t* res, size_t len, const TemplateEdit& t, Bytes* out, std::string* err) {
    if (len < 24) {
        *err = "the template is too small";
        return false;
    }
    out->assign(res, res + len);
    Bytes& b = *out;
    auto comp = [&](uint32_t type, const char* name, size_t need) -> size_t {
        uint32_t size = 0;
        const size_t c = template_component(b.data(), b.size(), type, &size);
        if (!c || size < need || c + need > b.size()) {
            *err = std::string("the template has no ") + name + " component";
            return 0;
        }
        return c;
    };
    if (!t.new_name.empty()) {
        const size_t s = reserve(b, t.new_name.size(), 1);
        std::memcpy(b.data() + s, t.new_name.data(), t.new_name.size());
        set_arr(b, 0, s, static_cast<uint32_t>(t.new_name.size()));
    }
    if (t.has_npc_id) {
        const size_t c = comp(kCompActiveNpc, "ActiveNpcState", 4);
        if (!c) return false;
        wr32(b.data() + c, t.npc_id);
    }
    if (t.has_loca) {
        const size_t c = comp(kCompLocaTag, "LocaTagComponent", 16);
        if (!c) return false;
        std::memcpy(b.data() + c, t.loca_tag.b, 16);
    }
    if (t.has_workshop) {
        const size_t c = comp(kCompCrafting, "Crafting", 20);
        if (!c) return false;
        if (!t.workshop_ref.zero()) std::memcpy(b.data() + c, t.workshop_ref.b, 16);
        wr32(b.data() + c + 16, t.workshop_id);
    }
    if (t.has_marker) {
        const size_t c = comp(kCompMapMarker, "MapMarker", 40);
        if (!c) return false;
        std::memcpy(b.data() + c, t.marker_tag.b, 16);
    }
    if (t.has_marker_req) {
        const size_t c = comp(kCompMarkerReq, "RuntimeMapMarkerUnlockRequirements", 36);
        if (!c) return false;
        put_query(b.data() + c, t.marker_hint);
        put_query(b.data() + c + 12, t.marker_visible);
        put_query(b.data() + c + 24, t.marker_completed);
    }
    if (t.dialog.any) {
        const size_t c = comp(kCompNpcDialog, "NpcDialog", 120);
        if (!c) return false;
        const size_t r = c + kDialogRes;
        if (t.dialog.headline) wr32(b.data() + r, t.dialog.headline);
        if (!write_lines(b, r + 0x34, t.dialog.init, kInitLine, t.dialog.replace_init, err) ||
            !write_lines(b, r + 0x3c, t.dialog.transition, kTransLine, t.dialog.replace_transition, err) ||
            !write_lines(b, r + 0x44, t.dialog.farewell, kFareLine, t.dialog.replace_farewell, err) ||
            !write_lines(b, r + 0x4c, t.dialog.options, kOptLine, t.dialog.replace_options, err))
            return false;
    }
    return true;
}

}  // namespace kiln::qb
