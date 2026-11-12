// SPDX-License-Identifier: MIT
// kiln.content gamedata: the game's loaded resources (key -> data) and the section registries.
//
// Resources (docs/re/resources.md): every resource is resident in one read-only region from start. kiln.assets'
// resource_list / resource_data give the keys of a type and a resource's live bytes (an override if one is
// registered, else the game's copy). Without kiln.assets (or an older one) the same is done through the resource
// service S itself: S = *(g38_items::ItemRegistry + 8) (ItemRegistry's constructor stores it there and calls
// ResourceByKey through it, server 0x925747 / 0x925775), slots +svc.by_type / +svc.by_key / +svc.data from the
// `assets.resources` sigdb feature. Both read the same memory: the data kiln.content writes in place.
#include <windows.h>

#include <algorithm>
#include <cstdio>
#include <cstring>

#include "gamedata.h"

namespace kc::gd {

namespace {

struct DataSpan {
    const uint8_t* data;
    uint64_t size;
    uint64_t flags;
};
struct TypeList {
    uint8_t error;
    uint8_t pad[3];
    uint32_t zero;
    const uint8_t* keys;  // 32-byte ResourceIds {guid, u32 type, u32 part, u64 0}
    uint64_t count;
};
using ByKeyFn = uint32_t*(__fastcall*)(uint32_t* out, uintptr_t S, const uint8_t* key32);
using DataFn = DataSpan*(__fastcall*)(DataSpan* out, uintptr_t S, uint32_t handle);
using ByTypeFn = TypeList*(__fastcall*)(TypeList* out, uintptr_t S, uint32_t type, uint32_t* opt);

uintptr_t g_S = 0;
ByKeyFn g_by_key = nullptr;
DataFn g_data = nullptr;
ByTypeFn g_by_type = nullptr;
const KilnAssetsApi* g_assets = nullptr;
bool g_ready = false;
const char* g_via = "none";

bool assets_has_data(const KilnAssetsApi* a) {
    return a && a->header.size >= offsetof(KilnAssetsApi, resource_data) + sizeof(void*) && a->resource_data &&
           a->resource_list;
}

uint32_t call_by_key(const uint8_t* key32) {
    uint32_t out = 0;
    __try {
        g_by_key(&out, g_S, key32);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
    return out;
}
bool call_data(uint32_t h, DataSpan* sp) {
    __try {
        g_data(sp, g_S, h);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return sp->data != nullptr;
}
bool call_by_type(uint32_t type, TypeList* tl) {
    __try {
        g_by_type(tl, g_S, type, nullptr);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return tl->error == 0;
}

bool init_S(uintptr_t world, std::string* why) {
    int64_t k = 0, d = 0, t = 0;
    if (!g_api->feature(KilnStr{"assets.resources", 16}) || g_api->constant(KilnStr{"assets/svc.by_key", 17}, &k) != KILN_OK ||
        g_api->constant(KilnStr{"assets/svc.data", 15}, &d) != KILN_OK ||
        g_api->constant(KilnStr{"assets/svc.by_type", 18}, &t) != KILN_OK) {
        *why = "the resource service symbols (feature assets.resources) are not resolved on this build";
        return false;
    }
    const uintptr_t ireg = g_api->singleton(world, KilnStr{"g38_items::ItemRegistry", 23});
    uintptr_t S = 0;
    if (!ireg || !rdv(ireg + 8, S) || !S) {
        *why = "no resource service behind the item registry";
        return false;
    }
    uintptr_t fk = 0, fd = 0, ft = 0, want = 0;
    if (!rdv(S + static_cast<uintptr_t>(k), fk) || !rdv(S + static_cast<uintptr_t>(d), fd) ||
        !rdv(S + static_cast<uintptr_t>(t), ft) || !fk || !fd || !ft) {
        *why = "the resource service's slots are not readable";
        return false;
    }
    // the slots hold the functions the sigdb resolved (kiln.assets hooks their code, not the slots)
    if (g_api->symbol(KilnStr{"assets/ResourceByKey", 20}, &want) == KILN_OK && want && want != fk) {
        *why = "the resource service at ItemRegistry+8 does not hold ResourceByKey";
        return false;
    }
    g_S = S;
    g_by_key = reinterpret_cast<ByKeyFn>(fk);
    g_data = reinterpret_cast<DataFn>(fd);
    g_by_type = reinterpret_cast<ByTypeFn>(ft);
    return true;
}

}  // namespace

bool res_ready() { return g_ready; }

uintptr_t res_data(const uint8_t guid[16], uint32_t type, uint64_t* size) {
    if (!g_ready) return 0;
    if (g_assets) {
        KilnAssetKey k{};
        std::memcpy(k.guid, guid, 16);
        k.type = type;
        const void* p = nullptr;
        size_t n = 0;
        if (g_assets->resource_data(&k, &p, &n) != KILN_OK || !p) return 0;
        if (size) *size = n;
        return reinterpret_cast<uintptr_t>(p);
    }
    alignas(16) uint8_t key[32] = {};
    std::memcpy(key, guid, 16);
    std::memcpy(key + 16, &type, 4);
    const uint32_t h = call_by_key(key);
    DataSpan sp{};
    if (!h || !call_data(h, &sp)) return 0;
    if (size) *size = sp.size;
    return reinterpret_cast<uintptr_t>(sp.data);
}

bool res_list(uint32_t type, std::vector<std::array<uint8_t, 16>>* guids) {
    guids->clear();
    if (!g_ready) return false;
    if (g_assets) {
        size_t n = 0;
        g_assets->resource_list(type, nullptr, 0, &n);
        std::vector<KilnAssetKey> keys(n);
        if (n && g_assets->resource_list(type, keys.data(), keys.size(), &n) != KILN_OK) return false;
        for (const KilnAssetKey& k : keys) {
            if (k.part != 0) continue;
            std::array<uint8_t, 16> g;
            std::memcpy(g.data(), k.guid, 16);
            guids->push_back(g);
        }
        return true;
    }
    TypeList tl{};
    if (!call_by_type(type, &tl) || !tl.keys || tl.count > 1000000) return false;
    for (uint64_t i = 0; i < tl.count; ++i) {
        uint8_t k[32];
        if (!rd(reinterpret_cast<uintptr_t>(tl.keys) + i * 32, k, 32)) return false;
        uint32_t part = 0;
        std::memcpy(&part, k + 20, 4);
        if (part != 0) continue;
        std::array<uint8_t, 16> g;
        std::memcpy(g.data(), k, 16);
        guids->push_back(g);
    }
    return true;
}

// ============================================================================================= registries =======

namespace {
GRegistry g_regs[8];

bool glob(const char* p, const char* s) {
    const char *star = nullptr, *ss = nullptr;
    auto lo = [](char c) { return (c >= 'A' && c <= 'Z') ? char(c - 'A' + 'a') : c; };
    while (*s) {
        if (*p == '*') {
            star = p++;
            ss = s;
        } else if (*p == '?' || (*p && lo(*p) == lo(*s))) {
            ++p;
            ++s;
        } else if (star) {
            p = star + 1;
            s = ++ss;
        } else {
            return false;
        }
    }
    while (*p == '*') ++p;
    return *p == 0;
}

std::string hex_name(const char* prefix, uint32_t id) {
    char b[32];
    std::snprintf(b, sizeof b, "%s%08x", prefix, id);
    return b;
}

uint32_t sub_id(const uint8_t g[16], int k) { return k == 0 ? fnv1a(g, 16) : fnv1a(g, 16) ^ (static_cast<uint32_t>(k) * 0x9E3779B1u); }

}  // namespace

bool valid_section(uint32_t s) { return s >= KILN_GD_TEMPLATES && s <= KILN_GD_ITEMS; }
GRegistry& greg(uint32_t s) { return g_regs[valid_section(s) ? s : 0]; }

const char* section_name(uint32_t s) {
    switch (s) {
        case KILN_GD_TEMPLATES: return "templates";
        case KILN_GD_ARSENALS: return "arsenals";
        case KILN_GD_SEQUENCES: return "sequences";
        case KILN_GD_BUFFS: return "buffs";
        case KILN_GD_PERKS: return "perks";
        case KILN_GD_SKILLS: return "skills";
        case KILN_GD_ITEMS: return "items";
        default: return "?";
    }
}

uint32_t section_of(const std::string& n) {
    for (uint32_t s = KILN_GD_TEMPLATES; s <= KILN_GD_SKILLS; ++s)
        if (n == section_name(s)) return s;
    // items through kiln.gamedata's paths (references, configs, lists): what the content sync sends for item
    // references kiln.gamedata changed (perk slots of perk clones); kiln.content's "items" section stays the usual one
    if (n == "itemdata") return KILN_GD_ITEMS;
    return 0;
}

void GRegistry::clear() {
    entries.clear(), by_id.clear(), by_name.clear(), by_lower.clear();
    available = false;
}

void GRegistry::add(GEntry e) {
    const uint32_t i = static_cast<uint32_t>(entries.size());
    by_id.emplace(e.id, i);
    if (!e.name.empty()) {
        by_name.emplace(e.name, i);
        by_lower.emplace(lower(e.name), i);
    }
    entries.push_back(std::move(e));
}

void GRegistry::rename(uint32_t id, const std::string& name) {
    auto it = by_id.find(id);
    if (it == by_id.end()) return;
    const uint32_t i = it->second;
    GEntry& e = entries[i];
    if (e.name == name) return;
    if (auto n = by_name.find(e.name); n != by_name.end() && n->second == i) by_name.erase(n);
    if (auto n = by_lower.find(lower(e.name)); n != by_lower.end() && n->second == i) by_lower.erase(n);
    e.name = name;
    if (!name.empty()) {
        by_name.emplace(name, i);
        by_lower.emplace(lower(name), i);
    }
}

const GEntry* GRegistry::get(uint32_t id) const {
    auto it = by_id.find(id);
    return it == by_id.end() ? nullptr : &entries[it->second];
}

const GEntry* GRegistry::lookup(const std::string& key) const {
    if (key.empty()) return nullptr;
    if (auto it = by_name.find(key); it != by_name.end()) return &entries[it->second];
    if (auto it = by_lower.find(lower(key)); it != by_lower.end()) return &entries[it->second];
    uint8_t g[16];
    if (key.size() >= 36 && guid_parse(key.substr(0, 36), g)) {
        int k = 0;
        if (key.size() > 37 && key[36] == '#') k = std::atoi(key.c_str() + 37);
        else if (key.size() != 36) return nullptr;
        return get(sub_id(g, k));
    }
    char* end = nullptr;
    unsigned long long v = 0;
    if (key.size() > 2 && key[0] == '0' && (key[1] == 'x' || key[1] == 'X')) v = std::strtoull(key.c_str() + 2, &end, 16);
    else if (key[0] >= '0' && key[0] <= '9') v = std::strtoull(key.c_str(), &end, 10);
    if (end && *end == 0 && v <= 0xFFFFFFFFull) return get(static_cast<uint32_t>(v));
    return lookup_alias(section, key);
}

std::vector<const GEntry*> GRegistry::select(const std::string& sel) const {
    std::vector<const GEntry*> out;
    if (sel.find_first_of("*?") != std::string::npos) {
        for (const GEntry& e : entries)
            if (glob(sel.c_str(), e.name.c_str())) out.push_back(&e);
        return out;
    }
    uint8_t g[16];
    if (section == KILN_GD_SEQUENCES && sel.size() == 36 && guid_parse(sel, g)) {  // every sub-sequence of it
        for (int k = 0; k < 64; ++k)
            if (const GEntry* e = get(sub_id(g, k))) out.push_back(e);
            else break;
        return out;
    }
    if (const GEntry* e = lookup(sel)) out.push_back(e);
    return out;
}

void sequence_clone_entries(GRegistry& S, const uint8_t guid[16], const std::string& name, uintptr_t subs, uint32_t n, size_t stride) {
    for (uint32_t k = 0; k < n && k < 64; ++k) {
        const uint32_t id = sub_id(guid, static_cast<int>(k));
        if (S.get(id)) {
            // A later world of this process: kiln.assets still serves the clone, and build_sections listed it with the game's sequences under
            // the sub-sequence name its bytes copy from the base (the base's, so its GUID). Its own name, or a block that names it fails here
            // ("is neither a GUID nor the name of a template, sequence or perk": a joined server after a hosted game, a second hosted world).
            S.entries[S.by_id.at(id)].clone = true;
            S.rename(id, k ? name + "#" + std::to_string(k) : name);
            continue;
        }
        GEntry e;
        e.id = id, e.addr = subs + static_cast<uintptr_t>(k) * stride, e.sub = static_cast<int32_t>(k), e.clone = true;
        e.name = k ? name + "#" + std::to_string(k) : name;
        std::memcpy(e.guid, guid, 16);
        S.add(std::move(e));
    }
}

std::string entry_key(uint32_t section, const GEntry& e) {
    bool g = false;
    for (uint8_t b : e.guid) g = g || b;
    if (section == KILN_GD_ITEMS || section == KILN_GD_BUFFS || section == KILN_GD_SKILLS || !g) return hex_name("0x", e.id);
    std::string k = guid_text(e.guid);
    if (e.sub > 0) k += "#" + std::to_string(e.sub);
    return k;
}

Node root_of(uint32_t section, const GEntry& e) {
    const GRegistry& r = greg(section);
    return Node{r.type, e.addr};
}

const GEntry* lookup_alias(uint32_t section, const std::string& key) {
    if (section == KILN_GD_ARSENALS && key.rfind("template:", 0) == 0) {
        const GRegistry& T = greg(KILN_GD_TEMPLATES);
        const GEntry* t = T.lookup(key.substr(9));
        if (!t) return nullptr;
        std::vector<Resolved> rs;
        std::string err;
        if (!resolve(Root::Template, Node{T.type, t->addr}, "EnemyArsenalReference.arsenal", &rs, &err) || rs.empty()) return nullptr;
        uint8_t g[16];
        if (!rd(rs[0].node.addr, g, 16)) return nullptr;
        return greg(KILN_GD_ARSENALS).get(fnv1a(g, 16));
    }
    if (section == KILN_GD_SKILLS && key.rfind("item:", 0) == 0) {
        const Entry* it = reg(KILN_CONTENT_ITEMS).lookup(key.substr(5));
        const GRegistry& S = greg(KILN_GD_SKILLS);
        const TField* f = S.type ? S.type->field("activeSkillUnlock") : nullptr;
        if (!it || !f) return nullptr;
        for (const GEntry& e : S.entries) {
            uint32_t id = 0;
            if (rdv(e.addr + f->offset, id) && id == it->id) return &e;
        }
    }
    return nullptr;
}

void refresh_items() {
    GRegistry& I = g_regs[KILN_GD_ITEMS];
    I.clear();
    I.section = KILN_GD_ITEMS, I.label = "items", I.qtype = "keen::ItemInfo";
    I.type = type_named(I.qtype);
    const Registry& C = reg(KILN_CONTENT_ITEMS);
    for (const Entry& e : C.entries) {
        GEntry g;
        g.id = e.id, g.name = e.name, g.addr = e.addr;
        std::memcpy(g.guid, e.guid, 16);
        I.add(std::move(g));
    }
    I.available = I.type && C.available;
}

namespace {
// One resource per GUID of a type: entries = the resources (name from a field).
void build_resources(GRegistry& r, const char* name_field) {
    std::vector<std::array<uint8_t, 16>> guids;
    if (!r.type || !res_list(r.type->hash, &guids)) return;
    const TField* nf = name_field ? r.type->field(name_field) : nullptr;
    for (const auto& g : guids) {
        GEntry e;
        e.addr = res_data(g.data(), r.type->hash);
        if (!e.addr) continue;
        std::memcpy(e.guid, g.data(), 16);
        e.id = fnv1a(g.data(), 16);
        if (nf) e.name = blob_string(e.addr + nf->offset);
        if (e.name.empty()) e.name = guid_text(e.guid);
        r.add(std::move(e));
    }
    r.available = !r.entries.empty();
}

// The elements of a BlobArray field of the one resource of `coll` (buffs, skill nodes, arsenals).
bool build_collection(GRegistry& r, const char* coll, const char* array, const std::function<void(GEntry&)>& name) {
    const TType* ct = type_named(coll);
    std::vector<std::array<uint8_t, 16>> guids;
    if (!ct || !r.type || !res_list(ct->hash, &guids) || guids.empty()) return false;
    const uintptr_t base = res_data(guids[0].data(), ct->hash);
    const TField* af = ct->field(array);
    uintptr_t data = 0;
    uint32_t n = 0;
    if (!base || !af || !blob_array(base + af->offset, &data, &n)) return false;
    for (uint32_t i = 0; i < n; ++i) {
        GEntry e;
        e.addr = data + static_cast<uintptr_t>(i) * r.type->size;
        name(e);
        if (r.by_id.count(e.id)) continue;
        r.add(std::move(e));
    }
    r.available = !r.entries.empty();
    return true;
}
}  // namespace

bool build_sections(uintptr_t world, std::string* why) {
    if (!g_ready) {
        const KilnAssetsApi* a = assets_api();
        if (assets_has_data(a)) {
            g_assets = a;
            g_via = "kiln.assets";
        } else if (init_S(world, why)) {
            g_via = "the resource service";
        } else {
            return false;
        }
        g_ready = true;
    }
    struct Def {
        uint32_t s;
        const char* label;
        const char* qtype;
        Root root;
    };
    const Def defs[] = {
        {KILN_GD_TEMPLATES, "templates", "keen::ecs::TemplateResource", Root::Template},
        {KILN_GD_ARSENALS, "arsenals", "keen::enemy::EnemyArsenalResource", Root::Plain},
        {KILN_GD_SEQUENCES, "sequences", "keen::actor::ActorSubSequenceResource", Root::Plain},
        {KILN_GD_BUFFS, "buffs", "keen::BuffType", Root::Plain},
        {KILN_GD_PERKS, "perks", "keen::Perk", Root::Plain},
        {KILN_GD_SKILLS, "skills", "keen::SkillTreeNode", Root::Plain},
    };
    for (const Def& d : defs) {
        GRegistry& r = g_regs[d.s];
        r.clear();
        r.section = d.s, r.label = d.label, r.qtype = d.qtype, r.root = d.root;
        r.type = type_named(d.qtype);
        if (!r.type) LOGW("gamedata: %s off: %s is not in the game's reflection", d.label, d.qtype);
    }
    build_resources(g_regs[KILN_GD_TEMPLATES], "name");
    build_resources(g_regs[KILN_GD_PERKS], "debugName");
    {  // sequences: one entry per sub-sequence
        GRegistry& r = g_regs[KILN_GD_SEQUENCES];
        const TType* st = type_named("keen::actor::ActorSequenceResource");
        std::vector<std::array<uint8_t, 16>> guids;
        const TField* subs = st ? st->field("subSequences") : nullptr;
        const TField* nf = r.type ? r.type->field("name") : nullptr;
        if (st && r.type && subs && res_list(st->hash, &guids)) {
            for (const auto& g : guids) {
                const uintptr_t base = res_data(g.data(), st->hash);
                uintptr_t data = 0;
                uint32_t n = 0;
                if (!base || !blob_array(base + subs->offset, &data, &n)) continue;
                for (uint32_t k = 0; k < n && k < 64; ++k) {
                    GEntry e;
                    e.addr = data + static_cast<uintptr_t>(k) * r.type->size;
                    std::memcpy(e.guid, g.data(), 16);
                    e.sub = static_cast<int32_t>(k);
                    e.id = sub_id(g.data(), static_cast<int>(k));
                    if (nf) e.name = blob_string(e.addr + nf->offset);
                    if (e.name.empty() || r.by_name.count(e.name)) e.name = guid_text(e.guid) + (k ? "#" + std::to_string(k) : "");
                    r.add(std::move(e));
                }
            }
            r.available = !r.entries.empty();
        }
    }
    {
        GRegistry& r = g_regs[KILN_GD_ARSENALS];
        const TField* gf = r.type ? r.type->field("guid") : nullptr;
        build_collection(r, "keen::enemy::EnemyArsenalRegistryResource", "arsenals", [&](GEntry& e) {
            if (gf) rd(e.addr + gf->offset, e.guid, 16);
            e.id = fnv1a(e.guid, 16);
            e.name = guid_text(e.guid);
        });
    }
    {
        GRegistry& r = g_regs[KILN_GD_BUFFS];
        const TField* idf = r.type ? r.type->field("buffTypeId") : nullptr;
        build_collection(r, "keen::BuffTypeCollectionResource", "buffs", [&](GEntry& e) {
            if (idf) rdv(e.addr + idf->offset, e.id);
            e.name = hex_name("buff_", e.id);
        });
    }
    {
        GRegistry& r = g_regs[KILN_GD_SKILLS];
        const TField* idf = r.type ? r.type->field("id") : nullptr;
        build_collection(r, "keen::SkillTreeResource", "nodes", [&](GEntry& e) {
            if (idf) rdv(e.addr + idf->offset, e.id);
            e.name = hex_name("skill_", e.id);
        });
    }
    refresh_items();
    return true;
}

const char* res_via() { return g_via; }

}  // namespace kc::gd
