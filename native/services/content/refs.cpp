// SPDX-License-Identifier: MIT
// kiln.content: reference fields of items ("kitbash": a new weapon with another weapon's model, its own perks, ...).
//
// Every reference in keen::ItemInfo is a 16-byte GUID typed by the game's reflection (keen::ObjectReference<keen::Model>,
// keen::ecs::TemplateReference, keen::PerkReference, ...). Kiln never creates resources: a reference may only point at
// something the game already loaded. The check is data-driven: a GUID is accepted for a field when some loaded item
// uses that GUID in a field of the same reflected type (a pool per type, built from every ItemInfo of the world), or,
// for template references, when the engine's template list holds it (kiln.spawn). "item:<name>" copies what another
// item has, so it is always valid.
//
// Bundles copy every field that belongs to one visual aspect from another item, scalars included (the model's offsets
// and flags must match the model). Who reads what (docs/re/content.md 8.3):
//   model     equipment.visualEntity (weapons: the template the server spawns per equipment slot: the held model),
//             equipment.visualModel + equipment.visualModels[] (armour: the worn model, per body type; the client
//             compares the GUID every frame), equipment.hideMask / blendShapeMask, equipment.equipmentEntityModelSetup.*,
//             iconModel + overridePickupModels[] (the model of a dropped item), collectionInfo.* (the collection shelf)
//   icon      the UI's icon texture (a prebaked UiTextureResource under the item's GUID: aliased to the other item's),
//             and iconImage, iconScene, iconRenderOffset.*, iconRender*Scale, fitToItemModelBoundingBox (no run-time
//             reader; copied for consistency)
//   perks     perkReferences[i] + perkIds[i] (perkId = FNV-1a(perk GUID): the PerkRegistry's key)
// Evidence and the consumers of each field: docs/re/content.md section 8.
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <unordered_map>
#include <unordered_set>

#include "refs.h"
#include "services/kiln_assets.h"
#include "services/kiln_spawn.h"
#include "services/kiln_worldtables.h"

namespace kc {

using json = nlohmann::ordered_json;

namespace {

struct G {
    uint64_t a = 0, b = 0;
    bool operator==(const G& o) const { return a == o.a && b == o.b; }
};
struct GH {
    size_t operator()(const G& g) const { return std::hash<uint64_t>()(g.a ^ (g.b * 0x9E3779B97F4A7C15ull)); }
};
G gof(const uint8_t* p) {
    G g;
    std::memcpy(&g.a, p, 8);
    std::memcpy(&g.b, p + 8, 8);
    return g;
}
bool zero16(const uint8_t* p) {
    for (int i = 0; i < 16; ++i)
        if (p[i]) return false;
    return true;
}

struct Pools {
    uint64_t key = 0;  // which item list they were built from
    std::unordered_set<uintptr_t> seen;  // the items read into them
    std::unordered_map<std::string, std::unordered_set<G, GH>> by_type;
    std::unordered_map<G, uint32_t, GH> perk_id;  // perk GUID -> perk id, as the game data pairs them
    uint32_t perk_slots = 0, perk_fnv = 0;        // slots with a perk; of those, id == FNV-1a(GUID)
    std::unordered_set<G, GH> item_guids;
};
std::mutex g_mu;
Pools g_pools;

uint64_t list_key(const std::vector<uintptr_t>& items) {
    uint64_t k = items.size() * 0x100000001B3ull;
    if (!items.empty()) k ^= items.front() ^ (items.back() << 1);
    return k;
}

// Built from the first list and grown by the items each later list adds: registering many items asks again after each one. An item
// that left the list (a redefinition's old clone, still valid memory) keeps its GUIDs in them; a world reset starts over.
const Pools& pools(const Schema& s, const std::vector<uintptr_t>& items) {
    const uint64_t key = list_key(items);
    if (g_pools.key == key && key) return g_pools;
    Pools& p = g_pools;
    p.key = key;
    std::vector<const Field*> refs;
    for (const Field& f : s.fields)
        if (f.ref) refs.push_back(&f);
    const Field* oid = s.find("objectId");
    const Field* pr[5] = {};
    const Field* pi[5] = {};
    for (int i = 0; i < 5; ++i) {
        pr[i] = s.find("perkReferences[" + std::to_string(i) + "]");
        pi[i] = s.find("perkIds[" + std::to_string(i) + "]");
    }
    uint8_t buf[2088 + 64];
    const size_t n = std::min<size_t>(s.size, sizeof buf);
    for (uintptr_t a : items) {
        if (!a || !p.seen.insert(a).second || !rd(a, buf, n)) continue;
        for (const Field* f : refs)
            if (f->offset + 16 <= n && !zero16(buf + f->offset)) p.by_type[f->type_name].insert(gof(buf + f->offset));
        if (oid) p.item_guids.insert(gof(buf + oid->offset));
        for (int i = 0; i < 5; ++i) {
            if (!pr[i] || !pi[i] || zero16(buf + pr[i]->offset)) continue;
            uint32_t id = 0;
            std::memcpy(&id, buf + pi[i]->offset, 4);
            ++p.perk_slots;
            if (id == fnv1a(buf + pr[i]->offset, 16)) ++p.perk_fnv;
            p.perk_id.emplace(gof(buf + pr[i]->offset), id);
        }
    }
    return g_pools;
}

const KilnSpawnApi* spawn_api() {
    static const KilnSpawnApi* t = nullptr;
    static bool tried = false;
    if (!tried) {
        tried = true;
        t = static_cast<const KilnSpawnApi*>(g_api->service(KilnStr{KILN_SPAWN_SERVICE, std::strlen(KILN_SPAWN_SERVICE)}, 1));
    }
    return t;
}

bool template_loaded(const uint8_t g[16]) {
    const KilnSpawnApi* sp = spawn_api();
    if (!sp || !sp->template_get) return false;
    KilnGuid kg;
    std::memcpy(kg.b, g, 16);
    KilnTemplateInfo ti{};
    ti.size = sizeof ti;
    return sp->template_get(&kg, &ti) == KILN_OK && (ti.source & 2);
}

// kiln.assets: a resource a mod serves (resource_put NEW or override) counts as loaded for its reference type.
// keen::Model references resolve to the keen::RenderModel of the same GUID (docs/re/models.md); templates to
// keen::ecs::TemplateResource; other ObjectReference<T> to T.
bool asset_served(const std::string& ref_type, const uint8_t g[16]) {
    const KilnAssetsApi* a = assets_api();
    if (!a || a->header.size < offsetof(KilnAssetsApi, resource_read) + sizeof(a->resource_read) || !a->resource_read) return false;
    uint32_t type = 0;
    if (ref_type == "keen::ObjectReference<keen::Model>") type = 0x177394c7u;  // FNV-1a("keen::RenderModel")
    else if (ref_type == "keen::ObjectReference<keen::VoxelObjectDefinition>") type = 0xe53afbafu;  // a shape's preview: keen::VoxelModelResource
    else if (ref_type == "keen::ecs::TemplateReference") type = 0x39768775u;   // FNV-1a("keen::ecs::TemplateResource")
    else if (ref_type == "keen::PerkReference") type = 0x6084515au;            // FNV-1a("keen::Perk"): kiln.gamedata's perk clones
    else if (ref_type.rfind("keen::ObjectReference<", 0) == 0 && ref_type.size() > 23)
        type = fnv1a(ref_type.substr(22, ref_type.size() - 23));
    if (!type) return false;
    KilnAssetKey k{};
    std::memcpy(k.guid, g, 16);
    k.type = type;
    size_t n = 0;
    const KilnResult r = a->resource_read(&k, nullptr, 0, &n);
    return r == KILN_OK || r == KILN_ERR_BUFFER_TOO_SMALL;
}

// A building material a mod made: its GUID is in kiln.worldtables' merged tables, and has no resource of its own.
bool world_material(const uint8_t g[16]) {
    const auto* t = static_cast<const KilnWorldTablesApi*>(g_api->service(KilnStr{KILN_WORLDTABLES_SERVICE, std::strlen(KILN_WORLDTABLES_SERVICE)}, KILN_WORLDTABLES_VERSION));
    return t && t->header.size >= sizeof(KilnWorldTablesApi) && t->material_id(g) >= 0;
}

std::string type_short(const std::string& t) {
    const size_t lt = t.find('<');
    return lt == std::string::npos ? t : t.substr(lt + 1, t.size() - lt - 2);
}

// "item:<sel>" / "item:<sel>.<path>" -> source ItemInfo + field (same path when none is given)
bool item_spec(const Schema& s, const std::string& spec, const Field& target, const ItemFind& find, uintptr_t* addr,
               const Field** src, std::string* why) {
    const std::string body = spec.substr(5);
    if (body.empty()) {
        *why = "\"item:\" needs an item name";
        return false;
    }
    if (uintptr_t a = find(body)) {
        *addr = a, *src = &target;
        return true;
    }
    for (size_t i = body.find('.'); i != std::string::npos; i = body.find('.', i + 1)) {
        const Field* f = s.find(body.substr(i + 1));
        if (!f) continue;
        if (uintptr_t a = find(body.substr(0, i))) {
            *addr = a, *src = f;
            return true;
        }
    }
    *why = "item " + body + " not found";
    return false;
}

// One reference value for `f`: true + bytes, or false + why.
bool resolve(const Schema& s, const Field& f, const std::string& spec, const ItemFind& find, const Pools& p,
             uint8_t out[16], uintptr_t* from_item, const Field** from_field, std::string* why) {
    std::memset(out, 0, 16);
    if (from_item) *from_item = 0;
    if (spec.empty() || spec == "none" || spec == "00000000-0000-0000-0000-000000000000") return true;
    if (spec.rfind("item:", 0) == 0) {
        uintptr_t a = 0;
        const Field* src = nullptr;
        if (!item_spec(s, spec, f, find, &a, &src, why)) return false;
        if (src->type_name != f.type_name || src->bytes != 16) {
            *why = spec + ": " + src->path + " is a " + src->type_name + ", " + f.path + " needs a " + f.type_name;
            return false;
        }
        if (!rd(a + src->offset, out, 16)) {
            *why = spec + ": not readable";
            return false;
        }
        if (from_item) *from_item = a, *from_field = src;
        return true;
    }
    if (spec.size() != 36 || !guid_parse(lower(spec), out)) {
        *why = f.path + ": \"" + spec + "\" is not a GUID, \"item:<name>\" or \"none\"";
        return false;
    }
    const G g = gof(out);
    if (f.type_name == "keen::ObjectReference<keen::ItemInfo>" && p.item_guids.count(g)) return true;
    if (auto it = p.by_type.find(f.type_name); it != p.by_type.end() && it->second.count(g)) return true;
    if (f.type_name == "keen::ecs::TemplateReference" && template_loaded(out)) return true;
    if (asset_served(f.type_name, out)) return true;
    if (f.path == "equipment.voxelData.placeVoxelMaterial" && world_material(out)) return true;
    *why = f.path + ": " + spec + " is not a " + type_short(f.type_name) + " the game has loaded (no loaded item uses it" +
           (f.type_name == "keen::ecs::TemplateReference" ? ", it is not a loaded template" : "") +
           ", and no mod serves it through kiln.assets)";
    return false;
}

void put(KitbashOut* out, const Field* f, const uint8_t* bytes) {
    for (RefWrite& w : out->writes)
        if (w.f == f) {
            std::memcpy(w.bytes, bytes, f->bytes);
            return;
        }
    RefWrite w;
    w.f = f;
    std::memcpy(w.bytes, bytes, f->bytes);
    out->writes.push_back(w);
}

// Copies every field of `paths` (a path ending in ".*" = the prefix) from the item at `a`.
void copy_fields(const Schema& s, uintptr_t a, std::initializer_list<const char*> paths, KitbashOut* out) {
    for (const Field& f : s.fields) {
        bool hit = false;
        for (const char* p : paths) {
            const size_t n = std::strlen(p);
            if (n >= 2 && p[n - 1] == '*') hit = hit || f.path.compare(0, n - 1, p, n - 1) == 0;
            else hit = hit || f.path == p;
        }
        if (!hit) continue;
        uint8_t b[16] = {};
        if (rd(a + f.offset, b, f.bytes)) put(out, &f, b);
    }
}

struct Alias {
    const char* key;
    const char* path;  // the reference a GUID value sets (bundles copy more from item:<name>)
};
constexpr size_t kEquipmentModelSize = 20;

const Alias kAliases[] = {
    {"model", "equipment.equipmentEntityModelSetup.model"},
    {"visual", "equipment.visualEntity"},
    {"weaponType", "weaponCategoryReference"},
    {"impact", "equipment.impactEntity"},
    {"program", "equipment.program"},
    {"animation", "equipment.equipmentEntityModelSetup.animation"},
    {"collectionModel", "collectionInfo.overrideModel"},
    {"iconModel", "iconModel"},
};

const char* alias_path(const std::string& key) {
    for (const Alias& a : kAliases)
        if (key == a.key) return a.path;
    return nullptr;
}

std::string str(const json& v) { return v.is_string() ? v.get<std::string>() : std::string(); }

void one_ref(const Schema& s, const Field& f, const json& v, const ItemFind& find, const Pools& p, KitbashOut* out) {
    if (!v.is_string() && !v.is_null()) {
        out->problems.push_back(f.path + ": a reference is a string (\"item:<name>\", a GUID or \"none\")");
        return;
    }
    uint8_t g[16];
    std::string why;
    if (!resolve(s, f, v.is_null() ? std::string("none") : v.get<std::string>(), find, p, g, nullptr, nullptr, &why)) {
        out->problems.push_back(why);
        return;
    }
    put(out, &f, g);
}

// "visualModels": [ { "mask": <keen::BlendShapeMask>, "model": <reference> } ]: a list of its own for the item's per-body models (keen::EquipmentModel, 20 bytes). The same list on the same item is
// built once: a retry of the item's references comes here again.
void visual_models(const Schema& s, const json& v, const ItemFind& find, const Pools& p, KitbashOut* out, uintptr_t self) {
    const auto header = s.special.find("equipment.visualModels");
    const Field* model = s.find("equipment.visualModel");
    if (header == s.special.end() || !model) {
        out->problems.push_back("visualModels: this build's ItemInfo has no equipment.visualModels");
        return;
    }
    if (!v.is_array() || v.size() > 64) {
        out->problems.push_back("visualModels: a list of up to 64 { \"mask\", \"model\" }");
        return;
    }
    std::vector<uint8_t> rows(v.size() * kEquipmentModelSize);
    for (size_t i = 0; i < v.size(); ++i) {
        const json& e = v[i];
        if (!e.is_object() || !e.contains("mask") || !e["mask"].is_number_unsigned() || !e.contains("model") || !e["model"].is_string()) {
            out->problems.push_back("visualModels[" + std::to_string(i) + "]: { \"mask\": a number, \"model\": a reference }");
            return;
        }
        uint8_t g[16];
        std::string why;
        if (!resolve(s, *model, e["model"].get<std::string>(), find, p, g, nullptr, nullptr, &why)) {
            out->problems.push_back("visualModels[" + std::to_string(i) + "]: " + why);
            return;
        }
        const uint32_t mask = e["mask"].get<uint32_t>();
        std::memcpy(rows.data() + i * kEquipmentModelSize, &mask, 4);
        std::memcpy(rows.data() + i * kEquipmentModelSize + 4, g, 16);
    }
    BlobWrite b;
    b.path = "equipment.visualModels";
    b.offset = header->second;
    b.count = static_cast<uint32_t>(v.size());
    if (!rows.empty() && self) {
        static std::map<std::pair<uintptr_t, std::string>, void*> built;
        void*& mem = built[{self, v.dump()}];
        if (!mem) {
            mem = near_alloc(self + header->second, rows.size());
            if (mem) std::memcpy(mem, rows.data(), rows.size());
        }
        if (!mem) {
            out->problems.push_back("visualModels: no memory near the item");
            return;
        }
        b.data = reinterpret_cast<uintptr_t>(mem);
    }
    out->blobs.push_back(b);
}

void perks(const Schema& s, const json& v, const ItemFind& find, const Pools& p, KitbashOut* out) {
    const Field* pr[5] = {};
    const Field* pi[5] = {};
    for (int i = 0; i < 5; ++i) {
        pr[i] = s.find("perkReferences[" + std::to_string(i) + "]");
        pi[i] = s.find("perkIds[" + std::to_string(i) + "]");
        if (!pr[i] || !pi[i]) {
            out->problems.push_back("perks: this build's ItemInfo has no perkReferences / perkIds");
            return;
        }
    }
    if (v.is_string()) {  // "item:<name>": all five slots of that item
        const std::string spec = v.get<std::string>();
        uintptr_t a = 0;
        const Field* src = nullptr;
        std::string why;
        if (spec.rfind("item:", 0) != 0 || !item_spec(s, spec, *pr[0], find, &a, &src, &why)) {
            out->problems.push_back("perks: " + (why.empty() ? "a list of up to 5 perks, or \"item:<name>\"" : why));
            return;
        }
        for (int i = 0; i < 5; ++i) {
            uint8_t g[16] = {}, id[4] = {};
            if (rd(a + pr[i]->offset, g, 16) && rd(a + pi[i]->offset, id, 4)) put(out, pr[i], g), put(out, pi[i], id);
        }
        return;
    }
    if (!v.is_array() || v.size() > 5) {
        out->problems.push_back("perks: a list of up to 5 perks (null keeps the base's slot), or \"item:<name>\"");
        return;
    }
    for (size_t i = 0; i < v.size(); ++i) {
        if (v[i].is_null()) continue;
        if (!v[i].is_string()) {
            out->problems.push_back("perks[" + std::to_string(i) + "]: a perk GUID, \"item:<name>\" or \"none\"");
            continue;
        }
        uint8_t g[16];
        uintptr_t from = 0;
        const Field* ff = nullptr;
        std::string why;
        if (!resolve(s, *pr[i], v[i].get<std::string>(), find, p, g, &from, &ff, &why)) {
            out->problems.push_back("perks[" + std::to_string(i) + "]: " + why);
            continue;
        }
        uint32_t id = 0;
        if (zero16(g)) id = 0;
        else if (from && ff) {  // the id of the same slot of that item
            for (int k = 0; k < 5; ++k)
                if (ff == pr[k]) rd(from + pi[k]->offset, &id, 4);
        } else if (auto it = p.perk_id.find(gof(g)); it != p.perk_id.end()) {
            id = it->second;
        } else {
            id = fnv1a(g, 16);
        }
        uint8_t ib[4];
        std::memcpy(ib, &id, 4);
        put(out, pr[i], g);
        put(out, pi[i], ib);
    }
}

}  // namespace

bool is_kitbash_key(const Schema& s, const std::string& key) {
    if (key == "icon" || key == "perks" || key == "refs" || key == "visualModels" || alias_path(key)) return true;
    const Field* f = s.find(key);
    return f && f->ref;
}

void kitbash(const Schema& s, const std::string& key, const json& value, const ItemFind& find,
             const std::vector<uintptr_t>& items, KitbashOut* out, uintptr_t self) {
    std::lock_guard lock(g_mu);
    const Pools& p = pools(s, items);
    if (key == "refs") {
        if (!value.is_object()) {
            out->problems.push_back("refs: an object { \"<reference field path>\": \"item:<name>\" | GUID | \"none\" }");
            return;
        }
        for (auto& [path, v] : value.items()) {
            const Field* f = s.find(path);
            if (!f || !f->ref) {
                out->problems.push_back("refs: " + path + " is not a reference field");
                continue;
            }
            if (path == "objectId" || f->type_name.find("LocaTag") != std::string::npos) {
                out->problems.push_back("refs: " + path + " cannot be changed (identity / texts: use \"texts\")");
                continue;
            }
            one_ref(s, *f, v, find, p, out);
        }
        return;
    }
    if (key == "perks") return perks(s, value, find, p, out);
    if (key == "visualModels") return visual_models(s, value, find, p, out, self);
    const std::string spec = str(value);
    if (key == "model" || key == "icon") {
        if (spec.rfind("item:", 0) == 0) {  // bundle
            uintptr_t a = 0;
            const Field* src = nullptr;
            std::string why;
            const Field* any = s.find(key == "model" ? "equipment.equipmentEntityModelSetup.model" : "iconScene");
            if (!any || !item_spec(s, spec, *any, find, &a, &src, &why)) {
                out->problems.push_back(key + ": " + (why.empty() ? "no such field on this build" : why));
                return;
            }
            if (key == "model") {
                copy_fields(s, a, {"equipment.visualEntity", "equipment.visualModel", "equipment.equipmentEntityModelSetup.*",
                                   "equipment.hideMask", "equipment.blendShapeMask", "iconModel", "collectionInfo.*"},
                            out);
                for (const char* bp : {"equipment.visualModels", "overridePickupModels"}) {
                    auto it = s.special.find(bp);
                    if (it == s.special.end()) continue;
                    int32_t off = 0;
                    uint32_t n = 0;
                    if (!rd(a + it->second, &off, 4) || !rd(a + it->second + 4, &n, 4) || n > 64) continue;
                    BlobWrite b;
                    b.path = bp;
                    b.offset = it->second;
                    b.data = off && n ? a + it->second + static_cast<intptr_t>(off) : 0;
                    b.count = b.data ? n : 0;
                    out->blobs.push_back(b);
                }
            } else {
                copy_fields(s, a, {"iconImage", "iconScene", "iconRenderOffset.*", "iconRenderCookingScale",
                                   "iconRenderGlobalScale", "fitToItemModelBoundingBox"},
                            out);
                if (const Field* oid = s.find("objectId"); oid && rd(a + oid->offset, out->icon_guid, 16)) out->icon = true;
            }
            return;
        }
        if (key == "icon") {  // another item's GUID: its icon texture
            uint8_t g[16];
            if (spec.size() == 36 && guid_parse(lower(spec), g) && p.item_guids.count(gof(g))) {
                std::memcpy(out->icon_guid, g, 16);
                out->icon = true;
                return;
            }
            out->problems.push_back("icon: \"item:<name>\" or the GUID of a loaded item (its icon); \"" + spec + "\" is neither");
            return;
        }
    }
    const char* path = alias_path(key);
    if (key == "model" && self) {
        // a bare model GUID: weapons and tools hold theirs in the equipment model setup (isSet), armour in visualModel
        const Field* is = s.find("equipment.equipmentEntityModelSetup.isSet");
        uint8_t set = 1;
        if (is && rd(self + is->offset, &set, 1) && !set) path = "equipment.visualModel";
    }
    const Field* f = s.find(path ? path : key);
    if (!f || !f->ref) {
        out->problems.push_back(key + ": not a reference field on this build");
        return;
    }
    one_ref(s, *f, value, find, p, out);
}

void refs_reset() {
    std::lock_guard lock(g_mu);
    g_pools = Pools{};
}

std::string refs_info_json(const Schema& s, const std::vector<uintptr_t>& items) {
    std::lock_guard lock(g_mu);
    const Pools& p = pools(s, items);
    json j = json::object();
    json t = json::object();
    for (auto& [type, set] : p.by_type) t[type] = set.size();
    j["pools"] = t;
    j["items"] = p.item_guids.size();
    j["perkSlots"] = p.perk_slots;
    j["perkIdIsFnv"] = p.perk_fnv;
    j["templates"] = spawn_api() != nullptr;
    return j.dump();
}

}  // namespace kc
