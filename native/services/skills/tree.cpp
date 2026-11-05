// SPDX-License-Identifier: MIT
// kiln.skills: node definitions and the expanded keen::SkillTreeResource.
//
// Layout (the game's reflection; docs/re/abilities.md section 4):
//   SkillTreeResource {nodes BlobArray<SkillTreeNode 140 B> +0, links BlobArray<SkillTreeNodeLink 16 B> +8}
//   BlobArray = {i32 offset relative to the field itself, u32 count}; a node has five: uiValues +0x5c,
//   configValues.simple +0x64, configValues.scaled +0x6c, incomingLinks +0x74, outgoingLinks +0x7c. Config lists are
//   BlobArray<BlobVariant<ImpactConfig>>: 12-byte headers {u32 type hash, i32 offset relative to header+4, u32 size}
//   with plain payloads (no relative fields in any ImpactConfig). Links are plain.
//   Every link sits in its source's outgoingLinks and its target's incomingLinks (all 312 game links); the game's
//   Reachable() walks both lists, so links work in both directions for unlocking.
// The expanded resource is the game's bytes unchanged, then (appended) a copy of the node array with the new nodes
// behind the game's, new sub-arrays, and a copy of the link array with the new links; the two headers point at the
// copies. The game's nodes keep their sub-arrays where they were (offsets rebased); a game node that gets a new link
// points its list at an appended copy.
#include "tree.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <functional>
#include <map>
#include <set>
#include <unordered_map>

namespace kiln::skills {

uint32_t fnv1a(const void* p, size_t n, uint32_t h) {
    const auto* b = static_cast<const uint8_t*>(p);
    for (size_t i = 0; i < n; ++i) h = (h ^ b[i]) * 0x1000193u;
    return h;
}

std::string hex32(uint32_t v) {
    char b[16];
    std::snprintf(b, sizeof b, "0x%08x", v);
    return b;
}

namespace {
int hexv(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}
}  // namespace

bool guid_parse(const std::string& s, uint8_t out[16]) {
    // xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx -> .NET ToByteArray: Data1 LE, Data2 LE, Data3 LE, Data4 as is
    if (s.size() != 36 || s[8] != '-' || s[13] != '-' || s[18] != '-' || s[23] != '-') return false;
    uint8_t raw[16];
    int k = 0;
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '-') continue;
        const int hi = hexv(s[i]), lo = i + 1 < s.size() ? hexv(s[i + 1]) : -1;
        if (hi < 0 || lo < 0 || k >= 16) return false;
        raw[k++] = static_cast<uint8_t>(hi * 16 + lo);
        ++i;
    }
    if (k != 16) return false;
    const int order[16] = {3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15};
    for (int i = 0; i < 16; ++i) out[i] = raw[order[i]];
    return true;
}

std::string guid_str(const uint8_t g[16]) {
    char b[40];
    std::snprintf(b, sizeof b, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[3], g[2], g[1],
                  g[0], g[5], g[4], g[7], g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
    return b;
}

uint32_t text_tag(const uint8_t guid[16], int slot) {
    uint8_t g[16];
    std::memcpy(g, guid, 16);
    g[8] ^= 0x6b;  // 'k'
    g[15] ^= static_cast<uint8_t>(0x51 + slot);
    uint32_t id = fnv1a(g, 16);
    return id ? id : 1;
}

void icon_guid(const uint8_t guid[16], uint8_t out[16]) {
    std::memcpy(out, guid, 16);
    out[8] ^= 0x69;  // 'i'
    out[15] ^= 0x2a;
}

namespace {

uint32_t link_id(const uint8_t guid[16], int k, int salt) {
    uint8_t g[16];
    std::memcpy(g, guid, 16);
    g[8] ^= 0x6c;  // 'l'
    g[14] ^= static_cast<uint8_t>(salt);
    g[15] ^= static_cast<uint8_t>(k + 1);
    return fnv1a(g, 16);
}

uint32_t rd32(const uint8_t* p) {
    uint32_t v;
    std::memcpy(&v, p, 4);
    return v;
}
int32_t rds32(const uint8_t* p) {
    int32_t v;
    std::memcpy(&v, p, 4);
    return v;
}
void wr32(uint8_t* p, uint32_t v) { std::memcpy(p, &v, 4); }
void wrs32(uint8_t* p, int32_t v) { std::memcpy(p, &v, 4); }

bool parse_ref_hex(const std::string& s, uint32_t* out) {
    if (s.size() < 3 || s[0] != '0' || (s[1] != 'x' && s[1] != 'X')) return false;
    uint32_t v = 0;
    for (size_t i = 2; i < s.size(); ++i) {
        const int h = hexv(s[i]);
        if (h < 0 || i > 9) return false;
        v = v * 16 + static_cast<uint32_t>(h);
    }
    *out = v;
    return true;
}

int type_of(const std::string& s) {
    if (s == "Attribute") return kAttribute;
    if (s == "ActiveSkill" || s == "Active") return kActive;
    if (s == "PassiveSkill" || s == "Passive") return kPassive;
    return -2;
}
int class_of(const std::string& s) {
    if (s == "Warrior") return 0;
    if (s == "Mage") return 1;
    if (s == "Ranger") return 2;
    if (s == "Neutral" || s == "None") return 3;
    return -2;
}

bool str_list(const json& j, const char* key, std::vector<std::string>* out, std::string* why) {
    if (!j.contains(key)) return true;
    const json& v = j[key];
    if (v.is_string()) {
        out->push_back(v.get<std::string>());
        return true;
    }
    if (!v.is_array()) return *why = std::string("\"") + key + "\" must be a node or a list of nodes", false;
    for (const json& e : v) {
        if (!e.is_string()) return *why = std::string("\"") + key + "\": every entry must be a node (\"0x<id>\", a GUID or a name)", false;
        out->push_back(e.get<std::string>());
    }
    return true;
}

bool id_map(const json& j, const char* key, std::vector<std::pair<uint32_t, double>>* out, std::string* why) {
    if (!j.contains(key)) return true;
    if (!j[key].is_object()) return *why = std::string("\"") + key + "\" must be {\"0x<configId>\": number}", false;
    for (auto& [k, v] : j[key].items()) {
        uint32_t id = 0;
        if (!parse_ref_hex(k, &id)) {
            uint8_t g[16];
            if (!guid_parse(k, g)) return *why = std::string(key) + ": \"" + k + "\" is not a 0x config id", false;
            id = fnv1a(g, 16);
        }
        if (!v.is_number() && !v.is_boolean()) return *why = std::string(key) + "." + k + " must be a number", false;
        out->emplace_back(id, v.is_boolean() ? (v.get<bool>() ? 1.0 : 0.0) : v.get<double>());
    }
    return true;
}

bool pair_of(const json& v, float* a, float* b) {
    if (v.is_array() && v.size() == 2 && v[0].is_number() && v[1].is_number()) {
        *a = v[0].get<float>();
        *b = v[1].get<float>();
        return true;
    }
    if (v.is_object() && v.contains("x") && v.contains("y") && v["x"].is_number() && v["y"].is_number()) {
        *a = v["x"].get<float>();
        *b = v["y"].get<float>();
        return true;
    }
    return false;
}

}  // namespace

bool parse_node(const json& j, NodeDef* d, std::string* why) {
    if (!j.is_object()) return *why = "a node definition is a JSON object", false;
    auto str = [&](const char* k) { return j.contains(k) && j[k].is_string() ? j[k].get<std::string>() : std::string(); };
    if (!guid_parse(str("guid"), d->guid)) return *why = "\"guid\" must be a fixed GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)", false;
    d->id = fnv1a(d->guid, 16);
    if (d->id == 0) return *why = "this GUID hashes to node id 0", false;
    d->name = str("name");
    if (d->name.empty() || d->name.size() > 96) return *why = "\"name\" (a debug name, 1..96 characters) is required", false;
    if (j.contains("type")) {
        d->type = type_of(str("type"));
        if (d->type < 0) return *why = "\"type\" must be PassiveSkill, Attribute or ActiveSkill (new roots are not allowed)", false;
    }
    if (j.contains("class")) {
        d->cls = class_of(str("class"));
        if (d->cls < 0) return *why = "\"class\" must be Warrior, Mage, Ranger or Neutral", false;
    }
    if (j.contains("cost")) {
        if (!j["cost"].is_number_integer() || j["cost"].get<int>() < 0 || j["cost"].get<int>() > 1000)
            return *why = "\"cost\" must be a whole number 0..1000 (skill points; per level for nodes with levels)", false;
        d->cost = j["cost"].get<int>();
    }
    if (j.contains("maxLevel")) {
        if (!j["maxLevel"].is_number_integer() || j["maxLevel"].get<int>() < 0 || j["maxLevel"].get<int>() > 7)
            return *why = "\"maxLevel\" must be 0 (one unlock) or 1..7 (the game keeps a level in 3 bits)", false;
        d->max_level = j["maxLevel"].get<int>();
    }
    if (j.contains("position")) {
        if (!pair_of(j["position"], &d->x, &d->y)) return *why = "\"position\" must be [x, y] (the game's tree units)", false;
        d->has_pos = true;
    }
    d->near_ref = str("near");
    if (j.contains("offset")) {
        if (!pair_of(j["offset"], &d->dx, &d->dy)) return *why = "\"offset\" must be [dx, dy]", false;
        d->has_offset = true;
    }
    if (!str_list(j, "from", &d->from, why) || !str_list(j, "to", &d->to, why)) return false;
    if (j.contains("bidirectional")) d->bidirectional = j["bidirectional"].is_boolean() && j["bidirectional"].get<bool>();
    d->like = str("like");
    if (j.contains("program")) {
        if (!guid_parse(str("program"), d->program)) return *why = "\"program\" must be an ImpactProgram GUID", false;
        d->has_program = true;
    }
    if (!id_map(j, "configs", &d->configs, why) || !id_map(j, "scales", &d->scales, why)) return false;
    if (j.contains("icon")) {
        if (!guid_parse(str("icon"), d->icon)) return *why = "\"icon\" must be a UiTexture GUID (\"iconFile\" takes a PNG)", false;
        d->has_icon = true;
    }
    d->icon_file = str("iconFile");
    if (j.contains("texts")) {
        if (!j["texts"].is_object()) return *why = "\"texts\" must be {\"en\": {\"name\", \"description\", \"perLevel\"}, ...}", false;
        d->texts = j["texts"];
    } else if (j.contains("displayName") || j.contains("description")) {
        json t = json::object();
        if (j.contains("displayName")) t["name"] = j["displayName"];
        if (j.contains("description")) t["description"] = j["description"];
        if (j.contains("perLevel")) t["perLevel"] = j["perLevel"];
        d->texts = json{{"*", t}};
    }
    if (j.contains("unlocksAbility")) {
        if (!guid_parse(str("unlocksAbility"), d->ability))
            return *why = "\"unlocksAbility\" must be the ability item's GUID (its ItemId is FNV-1a of it)", false;
        d->has_ability = true;
    }
    if (d->from.empty() && d->to.empty())
        return *why = "a node needs a link: \"from\" (the nodes that lead to it) or \"to\"", false;
    return true;
}

namespace {

struct VNode {
    uint32_t id;
    size_t off;  // node's offset in the game's resource
};

struct Parsed {
    bool ok = false;
    size_t nodes_off = 0, links_off = 0;
    uint32_t nc = 0, lc = 0;
    std::string why;
};

Parsed parse_header(const uint8_t* p, size_t len) {
    Parsed r;
    if (len < 16) return r.why = "shorter than its header", r;
    const int64_t no = 0 + static_cast<int64_t>(rds32(p + 0)), lo = 8 + static_cast<int64_t>(rds32(p + 8));
    r.nc = rd32(p + 4);
    r.lc = rd32(p + 12);
    if (r.nc == 0 || r.nc > 4096 || r.lc > 16384) return r.why = "node / link counts out of range", r;
    if (no < 16 || lo < 16 || static_cast<uint64_t>(no) + r.nc * kNode > len || static_cast<uint64_t>(lo) + r.lc * kLink > len)
        return r.why = "arrays outside the resource", r;
    r.nodes_off = static_cast<size_t>(no);
    r.links_off = static_cast<size_t>(lo);
    r.ok = true;
    return r;
}

// Absolute target of a BlobArray at field offset `f` (0 when null).
int64_t target(const uint8_t* p, size_t f) {
    const int32_t rel = rds32(p + f);
    return rel ? static_cast<int64_t>(f) + rel : 0;
}

struct Out {
    std::vector<uint8_t> b;
    size_t append(size_t n, size_t align = 4) {
        size_t at = (b.size() + align - 1) & ~(align - 1);
        b.resize(at + n, 0);
        return at;
    }
    void set_array(size_t field, size_t at, uint32_t count) {
        wrs32(b.data() + field, static_cast<int32_t>(static_cast<int64_t>(at) - static_cast<int64_t>(field)));
        wr32(b.data() + field + 4, count);
    }
};

int ui_scale(uint8_t fmt) {
    switch (fmt) {
    case 1: return 100;   // Percentage
    case 2: return 1000;  // Duration (s -> ms)
    case 4: return 100;   // Float
    default: return 1;    // Normal, DurationMilliseconds, Numberless
    }
}

}  // namespace

Built build(const uint8_t* v, size_t len, std::vector<Def*>& defs, bool force_copy, bool texts_ok, bool shadow_ok) {
    Built R;
    const Parsed P = parse_header(v, len);
    if (!P.ok) {
        R.problems.push_back("the game's SkillTreeResource did not parse: " + P.why);
        for (Def* d : defs)
            if (d->state != kOrphaned) d->state = kError, d->error = "the game's skill tree did not parse";
        return R;
    }
    R.ok = true;
    R.vanilla_nodes = P.nc;
    R.vanilla_links = P.lc;
    std::unordered_map<uint32_t, uint32_t> vindex;  // id -> index
    std::set<uint32_t> link_ids;
    uint32_t level_max = 0;
    for (uint32_t i = 0; i < P.nc; ++i) {
        const uint8_t* n = v + P.nodes_off + i * kNode;
        vindex[rd32(n + F::id)] = i;
        if (n[F::max_level]) level_max = std::max<uint32_t>(level_max, n[F::level_index]);
    }
    for (uint32_t i = 0; i < P.lc; ++i) link_ids.insert(rd32(v + P.links_off + i * kLink));
    R.level_max = level_max;

    // ---- pick the definitions that go in: valid, refs resolvable, in id order, within the caps ----
    std::vector<Def*> live;
    for (Def* d : defs) {
        if (d->state == kOrphaned) continue;
        d->state = kDefined;
        d->error.clear();
        d->index = d->level_index = -1;
        d->shadow = false;
        live.push_back(d);
    }
    std::sort(live.begin(), live.end(), [](const Def* a, const Def* b) { return a->node.id < b->node.id; });
    // duplicate GUIDs: the first owner keeps it (same order everywhere: by id, then by owner id string)
    std::map<uint32_t, Def*> by_id;
    std::map<std::string, Def*> by_name;
    for (Def* d : live) {
        if (vindex.count(d->node.id)) {
            d->state = kError, d->error = "the GUID's id " + hex32(d->node.id) + " is a game node";
            continue;
        }
        auto [it, fresh] = by_id.emplace(d->node.id, d);
        if (!fresh) {
            d->state = kError, d->error = "GUID " + guid_str(d->node.guid) + " is defined twice (first: " + it->second->owner_id + ")";
            continue;
        }
        by_name.emplace(d->node.name, d);
    }
    auto resolve = [&](const std::string& ref, uint32_t* id) -> bool {
        uint32_t h = 0;
        uint8_t g[16];
        if (parse_ref_hex(ref, &h)) {
            *id = h;
        } else if (guid_parse(ref, g)) {
            *id = fnv1a(g, 16);
        } else if (auto it = by_name.find(ref); it != by_name.end() && it->second->state != kError) {
            *id = it->second->node.id;
        } else {
            return false;
        }
        return vindex.count(*id) || (by_id.count(*id) && by_id[*id]->state != kError);
    };
    // resolve until nothing changes (a node may refer to another Kiln node that turns out to be an error)
    for (bool changed = true; changed;) {
        changed = false;
        for (Def* d : live) {
            if (d->state == kError) continue;
            uint32_t id;
            std::string bad;
            for (const auto& r : d->node.from)
                if (!resolve(r, &id)) bad = r;
            for (const auto& r : d->node.to)
                if (!resolve(r, &id)) bad = r;
            if (!d->node.like.empty() && !resolve(d->node.like, &id)) bad = d->node.like;
            if (!d->node.near_ref.empty() && !resolve(d->node.near_ref, &id)) bad = d->node.near_ref;
            if (!bad.empty()) {
                d->state = kError, d->error = "unknown node \"" + bad + "\" (a game node's 0x id, a node GUID or a Kiln node's name)";
                changed = true;
            }
        }
    }
    // ---- like nodes: copy their effect ----
    auto vnode = [&](uint32_t id) -> const uint8_t* {
        auto it = vindex.find(id);
        return it == vindex.end() ? nullptr : v + P.nodes_off + it->second * kNode;
    };
    // a Kiln node's "like" may name another Kiln node: resolve to its own like chain (to a game node)
    std::function<const uint8_t*(Def*, int)> like_of = [&](Def* d, int depth) -> const uint8_t* {
        if (d->node.like.empty() || depth > 8) return nullptr;
        uint32_t id = 0;
        if (!resolve(d->node.like, &id)) return nullptr;
        if (const uint8_t* n = vnode(id)) return n;
        auto it = by_id.find(id);
        return it == by_id.end() ? nullptr : like_of(it->second, depth + 1);
    };
    // Native slots (13 nodes, 9 with levels on svn 1287369): the same answer in every process with the same definitions, and as stable
    // as a pure function of them can be, because a node that moves costs its players a migration (GameShadow.cs). Mods with few nodes go
    // first, then by mod id, then by node id: adding a mod displaces only nodes of mods with more nodes than it has, and a small mod
    // stays whole. Nothing is kept between runs: client and server have to agree without talking. A node gets a slot only once it hangs
    // on a game node or a native node: the game's own Reachable and connectivity check follow only native nodes' lists, so a node linked
    // only to shadow nodes could never be unlocked (and would fail the check at login). Going in link order leaves no slot unused.
    // The rest are shadow nodes (index >= 256) when the shadow hooks are on, else they are refused.
    std::map<std::string, size_t> mod_size;
    std::vector<Def*> order;
    for (Def* d : live)
        if (d->state != kError) {
            ++mod_size[d->owner_id];
            order.push_back(d);
        }
    std::stable_sort(order.begin(), order.end(), [&](const Def* a, const Def* b) {
        const size_t na = mod_size[a->owner_id], nb = mod_size[b->owner_id];
        if (na != nb) return na < nb;
        if (a->owner_id != b->owner_id) return a->owner_id < b->owner_id;
        return a->node.id < b->node.id;
    });
    std::map<uint32_t, std::vector<uint32_t>> adj;
    for (Def* d : order) {
        uint32_t id;
        for (const auto* refs : {&d->node.from, &d->node.to})
            for (const auto& r : *refs)
                if (resolve(r, &id)) adj[d->node.id].push_back(id), adj[id].push_back(d->node.id);
    }
    std::vector<Def*> native, shadow;
    std::set<uint32_t> native_ids;
    uint32_t next_level = level_max + 1;
    const auto max_level_of = [&](Def* d) {
        const uint8_t* like = like_of(d, 0);
        return d->node.max_level >= 0 ? d->node.max_level : (like ? like[F::max_level] : 0);
    };
    const auto fits = [&](Def* d) { return P.nc + native.size() < 256 && (max_level_of(d) == 0 || next_level < 100); };
    const auto take = [&](Def* d) {
        if (max_level_of(d) > 0) d->level_index = static_cast<int>(next_level++);
        native.push_back(d);
        native_ids.insert(d->node.id);
    };
    for (bool changed = true; changed;) {
        changed = false;
        for (Def* d : order) {  // from the start after each take: what hangs on it comes before the next mod's nodes
            if (native_ids.count(d->node.id) || !fits(d)) continue;
            bool anchored = false;
            for (uint32_t p : adj[d->node.id]) anchored |= vindex.count(p) || native_ids.count(p);
            if (!anchored) continue;
            take(d);
            changed = true;
            break;
        }
    }
    std::vector<Def*> rest;
    for (Def* d : order)
        if (!native_ids.count(d->node.id)) rest.push_back(d);
    // nothing is short of a slot: what hangs on no game node at all stays native, as a mod defined it
    bool all_fit = !rest.empty();
    {
        size_t levels = 0;
        for (Def* d : rest) levels += max_level_of(d) > 0 ? 1 : 0;
        all_fit = all_fit && P.nc + native.size() + rest.size() <= 256 && next_level + levels <= 100;
    }
    if (all_fit || !shadow_ok) {
        std::vector<Def*> still;
        for (Def* d : rest)
            if (fits(d)) take(d);
            else still.push_back(d);
        rest = std::move(still);
    }
    for (Def* d : rest) {
        if (shadow_ok && P.nc + native.size() + shadow.size() < kMaxNodes) {
            d->shadow = true;
            shadow.push_back(d);
        } else {
            d->state = kError;
            d->error = P.nc + native.size() >= 256
                ? "the game's skill tree is full: 256 nodes (PlayerSkillsUi has one mask bit per tree index; the game uses " +
                      std::to_string(P.nc) + ") and the shadow table is off on this build. Use a Kiln tree (Kiln.Skills)"
                : "no level slot left: PlayerSkillsUi keeps 100 level bytes (one per levelIndex; the game uses 0.." +
                      std::to_string(level_max) + ") and the shadow table is off on this build. Use maxLevel 0 or a Kiln tree";
        }
    }
    std::sort(shadow.begin(), shadow.end(), [](const Def* a, const Def* b) { return a->node.id < b->node.id; });
    std::vector<Def*> in = native;
    in.insert(in.end(), shadow.begin(), shadow.end());
    R.free_levels = next_level <= 100 ? 100 - next_level : 0;
    R.shadow_nodes = static_cast<uint32_t>(shadow.size());
    const uint32_t total = P.nc + static_cast<uint32_t>(in.size());
    for (size_t i = 0; i < in.size(); ++i) in[i]->index = static_cast<int>(P.nc + i), in[i]->state = kInTree;
    std::set<uint32_t> shadow_ids;
    for (Def* d : shadow) shadow_ids.insert(d->node.id);

    // ---- links ----
    // A link that touches a shadow node goes into the global link list (the game's screen draws every link) and into
    // the shadow node's own lists, never into a native node's lists: the game's own walkers (Reachable, the
    // connectivity check, UiRebuild) only follow native nodes' lists, so they never meet an index past 255.
    struct NewLink {
        uint32_t id, src, dst;
        bool bidir;
    };
    std::vector<NewLink> links;
    std::map<uint32_t, std::vector<uint32_t>> add_in, add_out;  // node id -> new link ids
    std::map<uint32_t, ShadowNode> sn;
    for (Def* d : shadow) {
        ShadowNode& x = sn[d->node.id];
        x.id = d->node.id;
        x.cost = static_cast<uint16_t>(d->node.cost);
        const uint8_t* like = like_of(d, 0);
        x.max_level = static_cast<uint8_t>(d->node.max_level >= 0 ? d->node.max_level : (like ? like[F::max_level] : 0));
        x.index = static_cast<uint32_t>(d->index);
    }
    auto is_root = [&](uint32_t id) {
        const uint8_t* n = vnode(id);
        return n && n[F::type] == kRoot;
    };
    for (Def* d : in) {
        int k = 0;
        auto mk = [&](uint32_t src, uint32_t dst) {
            uint32_t id = 0;
            for (int salt = 0; salt < 64; ++salt) {
                id = link_id(d->node.guid, k, salt);
                if (id && !link_ids.count(id)) break;
            }
            link_ids.insert(id);
            links.push_back({id, src, dst, d->node.bidirectional});
            const bool touches_shadow = shadow_ids.count(src) || shadow_ids.count(dst);
            if (!touches_shadow || shadow_ids.count(src)) add_out[src].push_back(id);
            if (!touches_shadow || shadow_ids.count(dst)) add_in[dst].push_back(id);
            if (shadow_ids.count(dst)) sn[dst].from.emplace_back(src, is_root(src));
            if (shadow_ids.count(src)) sn[src].to.push_back(dst);
            ++k;
        };
        uint32_t id;
        for (const auto& r : d->node.from)
            if (resolve(r, &id)) mk(id, d->node.id);
        for (const auto& r : d->node.to)
            if (resolve(r, &id)) mk(d->node.id, id);
    }
    for (auto& [id, x] : sn) R.shadow.push_back(std::move(x));
    R.added_nodes = static_cast<uint32_t>(in.size());
    R.added_links = static_cast<uint32_t>(links.size());
    R.nodes = total;
    R.links = P.lc + R.added_links;
    if (in.empty() && !force_copy) {
        R.bytes.assign(v, v + len);
        return R;
    }

    // ---- write ----
    Out o;
    o.b.assign(v, v + len);
    const size_t A = o.append(total * kNode);
    // the game's nodes, sub-arrays rebased to their old data
    for (uint32_t i = 0; i < P.nc; ++i) {
        const size_t so = P.nodes_off + i * kNode, no = A + i * kNode;
        std::memcpy(o.b.data() + no, v + so, kNode);
        for (size_t f : F::kBlobArrays) {
            const int32_t rel = rds32(v + so + f);
            if (rel) wrs32(o.b.data() + no + f, static_cast<int32_t>(static_cast<int64_t>(so + f) + rel - static_cast<int64_t>(no + f)));
        }
    }
    auto node_at = [&](uint32_t idx) { return A + idx * kNode; };
    std::unordered_map<uint32_t, uint32_t> index_of = vindex;
    for (Def* d : in) index_of[d->node.id] = static_cast<uint32_t>(d->index);
    // a list = old ids + new ids (re-pointed only when it grows)
    auto extend = [&](size_t node_off, size_t field, const uint8_t* src_node, const std::vector<uint32_t>& extra) {
        uint32_t old_n = 0;
        std::vector<uint32_t> ids;
        if (src_node) {
            old_n = rd32(src_node + field + 4);
            const int64_t t = target(src_node, field);
            if (t && old_n) {
                const size_t abs = static_cast<size_t>((src_node - v) + t);
                for (uint32_t k = 0; k < old_n; ++k) ids.push_back(rd32(v + abs + k * 4));
            }
        }
        ids.insert(ids.end(), extra.begin(), extra.end());
        if (ids.empty()) {
            wr32(o.b.data() + node_off + field, 0);
            wr32(o.b.data() + node_off + field + 4, 0);
            return;
        }
        const size_t at = o.append(ids.size() * 4);
        for (size_t k = 0; k < ids.size(); ++k) wr32(o.b.data() + at + k * 4, ids[k]);
        o.set_array(node_off + field, at, static_cast<uint32_t>(ids.size()));
    };
    for (auto& [id, extra] : add_in)
        if (auto it = vindex.find(id); it != vindex.end()) extend(node_at(it->second), F::in, vnode(id), extra);
    for (auto& [id, extra] : add_out)
        if (auto it = vindex.find(id); it != vindex.end()) extend(node_at(it->second), F::out, vnode(id), extra);

    // copies of a like node's config list with overrides; returns false when an override has no config
    auto copy_configs = [&](size_t node_off, size_t field, const uint8_t* like, const std::vector<std::pair<uint32_t, double>>& vals,
                            const std::vector<std::pair<uint32_t, double>>& scales, std::set<uint32_t>* used,
                            std::vector<std::pair<uint32_t, int64_t>>* ui_changes) {
        const uint32_t cnt = like ? rd32(like + field + 4) : 0;
        const int64_t t = like ? target(like, field) : 0;
        if (!cnt || !t) {
            wr32(o.b.data() + node_off + field, 0);
            wr32(o.b.data() + node_off + field + 4, 0);
            return;
        }
        const size_t habs = static_cast<size_t>((like - v) + t);
        bool touched = false;
        for (uint32_t k = 0; k < cnt; ++k) {
            const uint8_t* h = v + habs + k * 12;
            const size_t pay = static_cast<size_t>(static_cast<int64_t>(habs + k * 12 + 4) + rds32(h + 4));
            const uint32_t cid = rd32(v + pay + 0x24);
            for (auto& [id, _] : vals) touched |= id == cid;
            for (auto& [id, _] : scales) touched |= id == cid;
        }
        if (!touched) {  // share the like node's list
            o.set_array(node_off + field, habs, cnt);
            return;
        }
        const size_t nh = o.append(cnt * 12);
        for (uint32_t k = 0; k < cnt; ++k) {
            const uint8_t* h = v + habs + k * 12;
            const uint32_t type = rd32(h), size = rd32(h + 8);
            const size_t pay = static_cast<size_t>(static_cast<int64_t>(habs + k * 12 + 4) + rds32(h + 4));
            const size_t np = o.append(size);
            std::memcpy(o.b.data() + np, v + pay, size);
            const size_t hk = nh + k * 12;
            wr32(o.b.data() + hk, type);
            wrs32(o.b.data() + hk + 4, static_cast<int32_t>(static_cast<int64_t>(np) - static_cast<int64_t>(hk + 4)));
            wr32(o.b.data() + hk + 8, size);
            uint8_t* p = o.b.data() + np;
            const uint32_t cid = rd32(p + 0x24);
            for (auto& [id, val] : vals) {
                if (id != cid || size < 0x50) continue;
                switch (type) {
                case 0x1836a3c6u: { float f = static_cast<float>(val); std::memcpy(p + 0x4c, &f, 4); break; }      // Float
                case 0x7319886bu: wrs32(p + 0x4c, static_cast<int32_t>(std::lround(val))); break;                // Sint32
                case 0xdfd03b39u: wr32(p + 0x4c, static_cast<uint32_t>(std::max(0L, std::lround(val)))); break;  // Uint32
                case 0xee96ae04u: p[0x4c] = val != 0; break;                                                     // Bool
                default: continue;
                }
                used->insert(id);
                // the tooltip value paired with this config (uiValues.locaId == FNV-1a(locaTag))
                static const uint8_t zero[16] = {};
                if (std::memcmp(p, zero, 16) != 0)
                    ui_changes->emplace_back(fnv1a(p, 16), static_cast<int64_t>(std::llround(val * ui_scale(p[0x11]))));
            }
            for (auto& [id, val] : scales) {
                if (id != cid || size < 0x50) continue;
                if (type == 0xcb35b8e4u || type == 0xfe3d9e05u) {  // ScaledFloat / ScaledSint32: scaleFactor +0x4c
                    float f = static_cast<float>(val);
                    std::memcpy(p + 0x4c, &f, 4);
                    used->insert(id);
                }
            }
        }
        o.set_array(node_off + field, nh, cnt);
    };

    // positions: absolute, or near a node (default: the first "from" node) plus an offset; Kiln nodes may sit near
    // each other, so resolve them in a pass of their own
    std::unordered_map<uint32_t, std::pair<float, float>> pos;
    for (uint32_t i = 0; i < P.nc; ++i) {
        const uint8_t* nn = v + P.nodes_off + i * kNode;
        float px, py;
        std::memcpy(&px, nn + F::pos, 4);
        std::memcpy(&py, nn + F::pos + 4, 4);
        pos[rd32(nn)] = {px, py};
    }
    std::function<std::pair<float, float>(Def*, int)> pos_of = [&](Def* d, int depth) -> std::pair<float, float> {
        if (auto it = pos.find(d->node.id); it != pos.end()) return it->second;
        const NodeDef& nd = d->node;
        std::pair<float, float> r{nd.x, nd.y};
        if (!nd.has_pos) {
            uint32_t near_id = 0;
            const std::string& ref = !nd.near_ref.empty() ? nd.near_ref : (!nd.from.empty() ? nd.from[0] : nd.to[0]);
            std::pair<float, float> b{0, 0};
            if (resolve(ref, &near_id)) {
                if (auto it = pos.find(near_id); it != pos.end()) b = it->second;
                else if (auto k = by_id.find(near_id); k != by_id.end() && depth < 16) b = pos_of(k->second, depth + 1);
            }
            r = {b.first + nd.dx, b.second + nd.dy};
        }
        pos[d->node.id] = r;
        return r;
    };
    for (Def* d : in) pos_of(d, 0);

    // the new nodes
    for (Def* d : in) {
        const NodeDef& nd = d->node;
        const size_t no = node_at(static_cast<uint32_t>(d->index));
        uint8_t* n = o.b.data() + no;
        const uint8_t* like = like_of(d, 0);
        wr32(n + F::id, nd.id);
        const auto [x, y] = pos[nd.id];
        std::memcpy(n + F::pos, &x, 4);
        std::memcpy(n + F::pos + 4, &y, 4);
        // texts: our own tags when texts are given (the service hands them to the game's loca), else the like node's
        const bool own_texts = texts_ok && nd.texts.is_object() && !nd.texts.empty();
        for (int s = 0; s < 3; ++s) {
            const size_t f = s == 0 ? F::name : s == 1 ? F::desc : F::perlvl;
            const uint32_t tag = own_texts ? text_tag(nd.guid, s) : (like ? rd32(like + f) : 0);
            wr32(n + f, tag);
            d->tag[s] = tag;
        }
        if (nd.has_ability) {
            std::memcpy(n + F::ability_ref, nd.ability, 16);
            wr32(n + F::ability_id, fnv1a(nd.ability, 16));
        }
        if (nd.has_icon) {
            std::memcpy(n + F::icon_in, nd.icon, 16);
            std::memcpy(n + F::icon_act, nd.icon, 16);
        } else if (like) {
            std::memcpy(n + F::icon_in, like + F::icon_in, 16);
            std::memcpy(n + F::icon_act, like + F::icon_act, 16);
        }
        if (nd.has_program) std::memcpy(n + F::impact, nd.program, 16);
        else if (like) std::memcpy(n + F::impact, like + F::impact, 16);
        std::set<uint32_t> used;
        std::vector<std::pair<uint32_t, int64_t>> ui_changes;
        copy_configs(no, F::cfg_simple, like, nd.configs, nd.scales, &used, &ui_changes);
        n = o.b.data() + no;  // the buffer may have moved
        copy_configs(no, F::cfg_scaled, like, nd.configs, nd.scales, &used, &ui_changes);
        n = o.b.data() + no;
        for (auto& [id, _] : nd.configs)
            if (!used.count(id)) R.problems.push_back(nd.name + ": config " + hex32(id) + " is not a number config of its like node (not set)");
        for (auto& [id, _] : nd.scales)
            if (!used.count(id)) R.problems.push_back(nd.name + ": scaled config " + hex32(id) + " is not in its like node (not set)");
        // tooltip values: the like node's, with the changed configs' values
        if (like && rd32(like + F::ui + 4)) {
            const uint32_t cnt = rd32(like + F::ui + 4);
            const size_t src = static_cast<size_t>((like - v) + target(like, F::ui));
            if (ui_changes.empty()) {
                o.set_array(no + F::ui, src, cnt);
            } else {
                const size_t at = o.append(cnt * 40);
                std::memcpy(o.b.data() + at, v + src, cnt * 40);
                for (uint32_t k = 0; k < cnt; ++k)
                    for (auto& [loca, val] : ui_changes)
                        if (rd32(o.b.data() + at + k * 40) == loca) wrs32(o.b.data() + at + k * 40 + 8, static_cast<int32_t>(val));
                o.set_array(no + F::ui, at, cnt);
            }
        } else {
            wr32(o.b.data() + no + F::ui, 0);
            wr32(o.b.data() + no + F::ui + 4, 0);
        }
        extend(no, F::in, nullptr, add_in[nd.id]);
        extend(no, F::out, nullptr, add_out[nd.id]);
        n = o.b.data() + no;
        const uint16_t cost = static_cast<uint16_t>(nd.cost);
        std::memcpy(n + F::cost, &cost, 2);
        n[F::type] = static_cast<uint8_t>(nd.type >= 0 ? nd.type : (like && like[F::type] ? like[F::type] : kPassive));
        n[F::cls] = static_cast<uint8_t>(nd.cls >= 0 ? nd.cls : (like ? like[F::cls] : 3));
        // shadow nodes keep their max level (the screen shows it) with levelIndex 0: the game never reads their level
        n[F::max_level] = static_cast<uint8_t>(d->level_index >= 0 || d->shadow ? (nd.max_level >= 0 ? nd.max_level : (like ? like[F::max_level] : 0)) : 0);
        n[F::level_index] = static_cast<uint8_t>(d->level_index >= 0 ? d->level_index : 0);
    }
    // new links: the game's copied, then ours
    const size_t L = o.append((P.lc + links.size()) * kLink);
    std::memcpy(o.b.data() + L, v + P.links_off, P.lc * kLink);
    for (size_t k = 0; k < links.size(); ++k) {
        uint8_t* l = o.b.data() + L + (P.lc + k) * kLink;
        wr32(l + 0, links[k].id);
        wr32(l + 4, links[k].src);
        wr32(l + 8, links[k].dst);
        l[12] = links[k].bidir ? 1 : 0;
    }
    o.set_array(0, A, total);
    o.set_array(8, L, static_cast<uint32_t>(P.lc + links.size()));
    R.bytes = std::move(o.b);
    return R;
}

// ------------------------------------------------------------------------------------------------ checks -------
json summary(const uint8_t* p, size_t len) {
    json j = json::object();
    const Parsed P = parse_header(p, len);
    if (!P.ok) {
        j["error"] = P.why;
        return j;
    }
    j["nodes"] = P.nc;
    j["links"] = P.lc;
    uint32_t lm = 0;
    for (uint32_t i = 0; i < P.nc; ++i) {
        const uint8_t* n = p + P.nodes_off + i * kNode;
        if (n[F::max_level]) lm = std::max<uint32_t>(lm, n[F::level_index]);
    }
    j["levelMax"] = lm;
    return j;
}

json verify(const uint8_t* v, size_t vlen, const uint8_t* n, size_t nlen) {
    json out = json::object();
    json problems = json::array();
    auto bad = [&](const std::string& s) {
        if (problems.size() < 40) problems.push_back(s);
    };
    const Parsed A = parse_header(v, vlen), B = parse_header(n, nlen);
    if (!A.ok || !B.ok) {
        out["ok"] = false;
        out["problems"] = json::array({std::string("header: ") + (A.ok ? B.why : A.why)});
        return out;
    }
    if (nlen < vlen || std::memcmp(v, n + 0, 0) != 0) bad("the expanded resource is shorter than the game's");
    // the game's bytes must be unchanged (except the two headers)
    if (nlen >= vlen && std::memcmp(v + 16, n + 16, vlen - 16) != 0) bad("the game's bytes changed");
    if (B.nc < A.nc) bad("fewer nodes than the game's");
    if (B.lc < A.lc) bad("fewer links than the game's");
    std::set<uint32_t> blinks;
    for (uint32_t i = 0; i < B.lc; ++i) blinks.insert(rd32(n + B.links_off + i * kLink));
    if (A.lc <= B.lc && std::memcmp(v + A.links_off, n + B.links_off, A.lc * kLink) != 0) bad("a game link differs");
    auto deref = [](const uint8_t* base, size_t blen, size_t node_off, size_t f, size_t elem, std::vector<uint8_t>* outv) -> bool {
        const int32_t rel = rds32(base + node_off + f);
        const uint32_t cnt = rd32(base + node_off + f + 4);
        outv->clear();
        if (!rel || !cnt) return true;
        const int64_t t = static_cast<int64_t>(node_off + f) + rel;
        if (t < 0 || static_cast<uint64_t>(t) + cnt * elem > blen) return false;
        outv->assign(base + t, base + t + cnt * elem);
        return true;
    };
    // variant lists: compare headers' type/size and payload bytes
    auto variants = [&](const uint8_t* base, size_t blen, size_t node_off, size_t f, std::vector<uint8_t>* flat) -> bool {
        std::vector<uint8_t> hdr;
        if (!deref(base, blen, node_off, f, 12, &hdr)) return false;
        flat->clear();
        const int64_t t = static_cast<int64_t>(node_off + f) + rds32(base + node_off + f);
        for (size_t k = 0; k * 12 < hdr.size(); ++k) {
            const uint32_t type = rd32(hdr.data() + k * 12), size = rd32(hdr.data() + k * 12 + 8);
            const int64_t pay = t + static_cast<int64_t>(k * 12 + 4) + rds32(hdr.data() + k * 12 + 4);
            if (pay < 0 || static_cast<uint64_t>(pay) + size > blen) return false;
            flat->insert(flat->end(), reinterpret_cast<const uint8_t*>(&type), reinterpret_cast<const uint8_t*>(&type) + 4);
            flat->insert(flat->end(), base + pay, base + pay + size);
        }
        return true;
    };
    for (uint32_t i = 0; i < A.nc && i < B.nc; ++i) {
        const size_t ao = A.nodes_off + i * kNode, bo = B.nodes_off + i * kNode;
        // fixed fields (everything but the five BlobArray headers)
        uint8_t fa[kNode], fb[kNode];
        std::memcpy(fa, v + ao, kNode);
        std::memcpy(fb, n + bo, kNode);
        for (size_t f : F::kBlobArrays) std::memset(fa + f, 0, 8), std::memset(fb + f, 0, 8);
        if (std::memcmp(fa, fb, kNode) != 0) bad("node " + std::to_string(i) + " (" + hex32(rd32(v + ao)) + "): fields differ");
        std::vector<uint8_t> x, y;
        if (!deref(v, vlen, ao, F::ui, 40, &x) || !deref(n, nlen, bo, F::ui, 40, &y) || x != y)
            bad("node " + hex32(rd32(v + ao)) + ": uiValues differ");
        for (size_t f : {F::cfg_simple, F::cfg_scaled})
            if (!variants(v, vlen, ao, f, &x) || !variants(n, nlen, bo, f, &y) || x != y)
                bad("node " + hex32(rd32(v + ao)) + ": configValues differ");
        for (size_t f : {F::in, F::out}) {
            if (!deref(v, vlen, ao, f, 4, &x) || !deref(n, nlen, bo, f, 4, &y) || y.size() < x.size() ||
                std::memcmp(x.data(), y.data(), x.size()) != 0)
                bad("node " + hex32(rd32(v + ao)) + ": a link list lost game links");
            for (size_t k = 0; k * 4 < y.size(); ++k)
                if (!blinks.count(rd32(y.data() + k * 4))) bad("node " + hex32(rd32(v + ao)) + ": a link id is not in the links");
        }
    }
    std::set<uint32_t> ids;
    for (uint32_t i = 0; i < B.nc; ++i) {
        const size_t bo = B.nodes_off + i * kNode;
        const uint32_t id = rd32(n + bo);
        if (!ids.insert(id).second) bad("node id " + hex32(id) + " twice");
        if (i < A.nc) continue;
        std::vector<uint8_t> y;
        if (!deref(n, nlen, bo, F::ui, 40, &y)) bad("added node " + hex32(id) + ": uiValues out of range");
        if (!variants(n, nlen, bo, F::cfg_simple, &y) || !variants(n, nlen, bo, F::cfg_scaled, &y))
            bad("added node " + hex32(id) + ": configValues out of range");
        for (size_t f : {F::in, F::out}) {
            if (!deref(n, nlen, bo, f, 4, &y)) bad("added node " + hex32(id) + ": link list out of range");
            for (size_t k = 0; k * 4 < y.size(); ++k)
                if (!blinks.count(rd32(y.data() + k * 4))) bad("added node " + hex32(id) + ": link id not in the links");
        }
        if (n[bo + F::type] == kRoot) bad("added node " + hex32(id) + " is a root");
    }
    // every added link: both ends exist, and it is in its source's out list and its target's in list
    for (uint32_t i = A.lc; i < B.lc; ++i) {
        const uint8_t* l = n + B.links_off + i * kLink;
        if (!ids.count(rd32(l + 4)) || !ids.count(rd32(l + 8))) bad("added link " + hex32(rd32(l)) + ": an end is not a node");
    }
    out["ok"] = problems.empty();
    out["nodes"] = B.nc;
    out["links"] = B.lc;
    out["added"] = B.nc - A.nc;
    out["addedLinks"] = B.lc - A.lc;
    out["problems"] = problems;
    return out;
}

}  // namespace kiln::skills
