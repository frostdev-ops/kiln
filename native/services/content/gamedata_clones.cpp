// SPDX-License-Identifier: MIT
// kiln.content gamedata: clones into new GUIDs ("add" lists of the sections, define()): templates, sequences, perks,
// buffs.
//
// A template clone (docs/re/gamedata.md "Clones"):
//  1. the base TemplateResource's bytes (the struct and its blob data; every offset inside is self-relative, so a
//     copy is valid as it is), the new name appended as the BlobString `name`;
//  2. served by kiln.assets as a NEW resource under the clone's GUID (createEntity locks the template resource by
//     GUID through the resource service, server 0x5f3aa0), so it is also in ResourcesByType: worlds created later
//     register it themselves in their template-info init (0x667c10);
//  3. registered in the running world: RegisterTemplateInfo(world + world.storage, data, &guid, false) (0x6671d0),
//     the function the init calls per template. Its TemplateInfo array is inline in the ecs storage and moves when it
//     grows, so Kiln only appends while count < capacity (13,000 on 1285601 for 12,269 game templates);
//  4. the definition's field values as layers of an internal owner (the original = the base's values; mods' layers
//     sit on top), so every path, config add and tooltip rule of the templates section works on clones too.
// A clone stays registered until the process ends (TemplateInfos cannot be removed). Clients need the same
// definition: the content sync sends every definition as the section's "add" list.
//
// A perk clone: the base keen::Perk resource copied (debugName = the clone's name, its own name / description loca
// tags when the definition has texts), served by kiln.assets as a NEW resource, and inserted into each world's
// PerkRegistry id map (FNV-1a(GUID) -> Perk*, the map PerkRegistryFind 0x93f8e0 reads; items carry the GUID in
// perkReferences[i] and the id in perkIds[i]).
//
// A buff clone: buffs are records of the one BuffTypeCollectionResource (buffMap: a static hash map buffTypeId ->
// index, buffs: the records); the game finds one with getBuffType(id, BuffRegistry) (server 0x933700), which reads the
// collection through BuffRegistry+0x20. There is no runtime map to insert into, so Kiln serves the collection from its
// own copy once a buff clone exists: the game's records mirrored at the same indices (every self-relative offset
// rebased to point at the game's blob data), the clones after them, and a rebuilt map; each world's BuffRegistry
// pointer is swapped to it (the game's collection stays valid for anything that kept a pointer). The game's records
// stay where kiln.content's layers write (buff tweaks, config adds, tooltips work as before): buffs_sync copies them
// into the mirror after every change. buffTypeId = FNV-1a(GUID), as the game pairs its BuffTypeReferences and ids.
//
// Texts (perks, buffs): "displayName" / "description" and "texts" {"<language>": {"name", "description"}} give the
// clone new loca tags (GUIDs derived from its GUID); kiln.content's loca hooks answer them on clients
// (kc_nc_set_texts, newcontent.cpp). Placeholders ("placeholder": {...}, made by Kiln.Content for a recorded clone
// whose mod is gone): the base's record made inert (no program, no configs, no VFX) under the same GUID.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cctype>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <set>
#include <unordered_set>

#include "gamedata.h"

// kiln.content's loca texts (newcontent.cpp): `json` = "text" or {"en": "...", "de": "..."}. Linked weakly: without
// it (an older newcontent.cpp) the stub below answers and clones keep the base's texts.
extern "C" void kc_nc_set_texts(uint32_t tag, const char* json, size_t len);
namespace {
std::atomic<bool> g_texts_absent{false};
}
extern "C" void kc_nc_set_texts_absent(uint32_t, const char*, size_t) { g_texts_absent.store(true); }
#pragma comment(linker, "/alternatename:kc_nc_set_texts=kc_nc_set_texts_absent")

namespace kc::gd {

namespace {

constexpr uint32_t kBuffRoom = 1024;  // buff clones per process (records in Kiln's copy of the collection)

struct Def {
    uint32_t section = 0;
    KilnOwner owner = 0;
    std::string source;
    uint8_t guid[16] = {};
    std::string name, base;
    gjson fields = gjson::object();
    gjson body;               // the definition as given (sent to clients)
    gjson texts = gjson::object();  // perks, buffs: {"<language>": {"name", "description"}}
    std::string state = "pending", problem;
    std::vector<std::string> warnings;
    uintptr_t data = 0;       // the clone's live bytes (kiln.assets' copy; buffs: its record in Kiln's collection)
    std::set<uintptr_t> worlds;
    bool orphan = false;
    bool placeholder = false; // made inert (Kiln.Content's missing-content record)
    bool rebuild = false;     // a placeholder taken over by its mod: build the record again from the base
    std::string mod;          // the owning mod ("pack <mod>" / "mod <mod>" owners; placeholders: the recorded mod)
    uint32_t slot = UINT32_MAX;  // buffs: the record index in Kiln's collection
    std::set<uintptr_t> probed;  // buffs: worlds where the game's own lookup was checked
    uint64_t version = 0;
    std::string listed;          // its entry in definitions(), as of listed_as (a fingerprint of what the entry shows)
    uint64_t listed_as = 0;
};

std::recursive_mutex g_mu;
std::vector<Def> g_defs;
// Bumped whenever a template clone goes into a world's table: kiln.content then resolves the item references that named
// it before it was there (an item's equipment.placedEntity), at once instead of at its next periodic retry.
std::atomic<uint64_t> g_template_gen{0};

// Registering many at once (a world's load): the writes, the event order and the buff collection's header are done once per turn of
// the store lock instead of once per clone (under store_mutex).
bool g_batch = false;
bool g_batch_changes = false;         // after_changes() is owed
std::vector<size_t> g_batch_buffs;    // buff clones (g_defs indices) that wait for the header
std::unordered_set<uint64_t> g_defs_layered;  // section << 32 | id: entries with layers of g_defs_owner in this world

void changed() {
    if (g_batch) g_batch_changes = true;
    else after_changes();
}

using RegisterFn = uintptr_t(__fastcall*)(uintptr_t holder, const void* data, const uint8_t* guid, uint8_t force);
RegisterFn g_register = nullptr;
int64_t g_storage = 0, g_tarr = 0, g_tcap = 0, g_tcount = 0;
bool g_templates = false;
KilnOwner g_defs_owner = 0;
std::set<std::string> g_seen_pack;  // pack definitions (guid text) seen in the current pack application

using GetBuffFn = uintptr_t(__fastcall*)(uint32_t id, uintptr_t registry);
GetBuffFn g_get_buff = nullptr;  // optional (gamedata/GetBuffType): the game's own lookup, for the self-check
int64_t g_buff_slot_sig = -1;    // gamedata/buffreg.collection when the sigdb has it (checked against the scan)

bool valid_name(const std::string& n) {
    if (n.empty() || n.size() > 128) return false;
    for (char c : n)
        if (!(std::isalnum(static_cast<unsigned char>(c)) || c == '_' || c == '-' || c == '.')) return false;
    return true;
}

const char* wname(uintptr_t world) { return world == g_api->world(KILN_WORLD_CLIENT) ? "client" : "server"; }

std::string mod_of_label(const std::string& label) {
    for (const char* p : {"mod ", "pack "})
        if (label.rfind(p, 0) == 0) return label.substr(std::strlen(p));
    return {};
}

uintptr_t call_register(uintptr_t holder, const void* data, const uint8_t* guid) {
    __try {
        return g_register(holder, data, guid, 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

uintptr_t call_get_buff(uint32_t id, uintptr_t reg) {
    __try {
        return g_get_buff(id, reg);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 1;
    }
}

// A GUID derived from another one (the loca tags of a clone): stable, v4-shaped, never a game GUID by chance.
void derive_guid(const uint8_t base[16], const char* salt, uint8_t out[16]) {
    uint64_t a = 0xcbf29ce484222325ull, b = 0x84222325cbf29ce4ull;
    auto eat = [&](uint8_t c) {
        a = (a ^ c) * 0x100000001b3ull;
        b = (b ^ (c + 0x5bu)) * 0x100000001b3ull;
    };
    for (int i = 0; i < 16; ++i) eat(base[i]);
    for (const char* p = salt; *p; ++p) eat(static_cast<uint8_t>(*p));
    std::memcpy(out, &a, 8);
    std::memcpy(out + 8, &b, 8);
    out[7] = static_cast<uint8_t>((out[7] & 0x0F) | 0x40);
}

bool has_text(const Def& d, const char* slot) {
    for (auto& [lang, t] : d.texts.items())
        if (t.is_object() && t.contains(slot)) return true;
    return false;
}

// Registers the clone's texts with kiln.content's loca hooks (process-wide; the hooks run on clients only). A slot's tag is derived from the clone's GUID
// and "kiln:<slot>": a perk's or buff's name and description, a template's own slots (an interaction's prompt), which its fields name by that tag.
void publish_texts(const Def& d) {
    std::set<std::string> slots;
    for (auto& [lang, t] : d.texts.items())
        if (t.is_object())
            for (auto& [slot, text] : t.items()) slots.insert(slot);
    for (const std::string& slot : slots) {
        gjson per = gjson::object();
        for (auto& [lang, t] : d.texts.items())
            if (t.is_object() && t.contains(slot) && t[slot].is_string()) per[lang] = t[slot];
        uint8_t tag[16];
        derive_guid(d.guid, ("kiln:" + slot).c_str(), tag);
        const std::string s = per.dump();
        kc_nc_set_texts(fnv1a(tag, 16), s.data(), s.size());
    }
    static std::atomic<bool> warned{false};
    if (g_texts_absent.load() && !warned.exchange(true))
        LOGW("gamedata: this kiln.content has no kc_nc_set_texts: perk / buff clones show their base's texts");
}

// The clone's record: its loca tags (when it has texts) and, for a placeholder, nothing that acts: no program, no
// configs, no tooltips values, no VFX, no follow-up buff. `rec` is Kiln's memory (a copy).
void dress(const Def& d, const TType* t, uint8_t* rec, size_t n) {
    for (const char* slot : {"name", "description"})
        if (has_text(d, slot))
            if (const TField* f = t->field(slot); f && f->offset + 16 <= n)
                derive_guid(d.guid, std::strcmp(slot, "name") == 0 ? "kiln:name" : "kiln:description", rec + f->offset);
    if (!d.placeholder) return;
    std::function<void(const TType*, uint32_t, int)> inert = [&](const TType* st, uint32_t base, int depth) {
        if (depth > 6) return;
        for (const TField& f : st->fields) {
            const TType* ft = f.type() ? f.type()->resolved() : nullptr;
            const uint32_t o = base + f.offset;
            if (!ft || o + ft->size > n) continue;
            if (f.name == "impact" || f.name == "onTimeout") std::memset(rec + o, 0, ft->size);
            else if (ft->kind == K_BLOBARRAY) std::memset(rec + o + 4, 0, 4);  // count 0 (the offset stays)
            else if ((f.name == "simpleAttributeBuff" || f.name == "onTimeoutIsSet" || f.name == "isSet") && ft->size == 1) rec[o] = 0;
            else if (ft->kind == K_STRUCT && f.name != "name" && f.name != "description" && f.name != "icon") inert(ft, o, depth + 1);
        }
    };
    inert(t, 0, 0);
}

// Spare room at the end of a clone's resource, for the lists its rules add: inside its own block they are always in reach, wherever kiln.assets put it.
constexpr size_t kCloneRoom = 8192;

// A resource clone's bytes: the base resource copied, the clone's name appended as the BlobString `name_field`, then kCloneRoom.
bool build_bytes(const Def& d, const GEntry& base, const TType* type, const char* name_field, std::vector<uint8_t>* out,
                 std::string* err) {
    uint64_t size = 0;
    const uintptr_t src = res_data(base.guid, type->hash, &size);
    if (!src || size < type->size || size > (64u << 20)) {
        *err = "the base's resource is not readable";
        return false;
    }
    const size_t name_at = (static_cast<size_t>(size) + 7) & ~size_t(7);
    out->assign(name_at + d.name.size() + 8 + kCloneRoom, 0);
    if (!rd(src, out->data(), static_cast<size_t>(size))) {
        *err = "the base's resource is not readable";
        return false;
    }
    store_originals(src, static_cast<size_t>(size), out->data());  // what rules changed on the base is not the clone's (see apply_fields)
    std::memcpy(out->data() + name_at, d.name.data(), d.name.size());
    if (const TField* nf = type->field(name_field)) {
        const int32_t rel = static_cast<int32_t>(name_at - nf->offset);
        const uint32_t len = static_cast<uint32_t>(d.name.size());
        std::memcpy(out->data() + nf->offset, &rel, 4);
        std::memcpy(out->data() + nf->offset + 4, &len, 4);
    }
    return true;
}

// The definition of the clone a definition is based on; nullptr when its base is the game's.
const Def* clone_base(const Def& d) {
    const GEntry* base = greg(d.section).lookup(d.base);
    if (!base || !base->clone) return nullptr;
    for (const Def& x : g_defs)
        if (&x != &d && x.section == d.section && std::memcmp(x.guid, base->guid, 16) == 0) return &x;
    return nullptr;
}

void apply_fields(Def& d, const GEntry& e) {
    PhaseTimer pt(PH_GD_FIELDS);
    // a redefinition: the old definition's layers come off first (an entry the definitions never wrote to in this world has none)
    const uint64_t key = (uint64_t(d.section) << 32) | e.id;
    if (g_defs_layered.count(key)) {
        std::vector<uintptr_t> mine;
        store_for_each([&](uintptr_t a, const Slot& s) {
            if (s.kind == GD_KIND_BASE + d.section && s.id == e.id)
                for (const Layer& l : s.layers)
                    if (l.owner == g_defs_owner) mine.push_back(a);
        });
        for (uintptr_t a : mine) {
            store_drop(g_defs_owner, a);
            forget_added(g_defs_owner, a);
        }
        // Before the rules resolve: they must see the clone without the old definition. A config it added is still in memory until the flush; a rule would change that element, and the flush would then take it away.
        std::string flushed;
        store_flush(&flushed);
    }
    // A clone starts from the game's original, not from what other mods changed. So a clone of a clone takes what the definitions before it say, oldest first, then its own.
    std::vector<const Def*> chain{&d};
    while (chain.size() < 16)
        if (const Def* b = clone_base(*chain.back())) chain.push_back(b);
        else break;
    std::vector<std::string> errs;
    const LayerModScope as_mod(d.mod);  // the definitions' owner is shared: these layers are d's mod's
    for (auto c = chain.rbegin(); c != chain.rend(); ++c)
        for (auto& [path, v] : (*c)->fields.items()) {
            if (path.empty() || path[0] == '@') continue;
            if (apply_value(g_defs_owner, 1, d.section, e, path, v, "definition " + d.name, &errs)) g_defs_layered.insert(key);
        }
    d.warnings = errs;
    for (const std::string& w : errs) LOGW("gamedata: clone %s: %s", d.name.c_str(), w.c_str());
}

// The clone's TemplateInfo in one world (its own thread). false + d.state "error" when it cannot be added.
bool ensure_info(Def& d, uintptr_t world, const std::string& base_name) {
    if (!d.worlds.count(world)) {
        uintptr_t storage = 0;
        uint64_t count = 0, cap = 0;
        const uintptr_t holder = world + static_cast<uintptr_t>(g_storage);
        if (!rdv(holder, storage) || !storage || !rdv(storage + g_tarr + g_tcount, count) || !rdv(storage + g_tarr + g_tcap, cap)) {
            d.state = "error", d.problem = "the world's template storage is not readable";
            return false;
        }
        if (count + 1 > cap) {
            d.state = "error", d.problem = "the world's template table is full (" + std::to_string(count) + " of " + std::to_string(cap) + ")";
            return false;
        }
        uintptr_t info = 0;
        {
            PhaseTimer rt(PH_GAME_REGISTER);
            info = call_register(holder, reinterpret_cast<const void*>(d.data), d.guid);
        }
        if (!info) {
            d.state = "error", d.problem = "the game did not register the template (RegisterTemplateInfo returned nothing)";
            return false;
        }
        uint64_t after = 0;
        rdv(storage + g_tarr + g_tcount, after);
        d.worlds.insert(world);
        g_template_gen.fetch_add(1, std::memory_order_relaxed);
        LOGI("gamedata: template %s (%s, clone of %s) registered in the %s world: TemplateInfo %p, %llu of %llu template infos",
             d.name.c_str(), guid_text(d.guid).c_str(), base_name.c_str(), wname(world),
             reinterpret_cast<void*>(info), static_cast<unsigned long long>(after), static_cast<unsigned long long>(cap));
    }
    return true;
}

// ---- the game's id maps (engine HashMap u32 -> u64: ActionSequenceRegistry, PerkRegistry) ----
// Kiln finds a registry's map without a fixed offset: the HashMap in the registry object whose entries are exactly the
// resources' data (FNV-1a(GUID) -> resource), and checks that before it inserts anything (docs/re/gamedata.md 5).
struct IdMap {
    uintptr_t world = 0, map = 0;
};
std::vector<IdMap> g_seq_maps, g_perk_maps;

struct MapView {
    uint64_t cap = 0;
    uint32_t count = 0;
    uint64_t* bits = nullptr;
    uint32_t* keys = nullptr;
    uint64_t* values = nullptr;
};
bool map_view(uintptr_t map, MapView* m) {
    uintptr_t b = 0, k = 0, v = 0;
    if (!rdv(map + E.hm_cap, m->cap) || !rdv(map + E.hm_count, m->count) || !rdv(map + E.hm_bits, b) ||
        !rdv(map + E.hm_keys, k) || !rdv(map + E.hm_values, v))
        return false;
    m->bits = reinterpret_cast<uint64_t*>(b), m->keys = reinterpret_cast<uint32_t*>(k), m->values = reinterpret_cast<uint64_t*>(v);
    return m->cap && (m->cap & (m->cap - 1)) == 0 && m->cap <= (1ull << 24) && m->count <= m->cap && b && k && v;
}
// Inserts (or replaces the value of) key; refuses beyond half the capacity (the game's own load factor).
bool map_put(uintptr_t map, uint32_t key, uint64_t value, const char* what, std::string* err) {
    MapView m;
    if (!map_view(map, &m)) {
        *err = std::string("the ") + what + "'s map is not readable";
        return false;
    }
    __try {
        uint64_t i = key & (m.cap - 1);
        for (uint64_t n = 0; n < m.cap; ++n) {
            if (!((m.bits[i >> 6] >> (i & 63)) & 1)) break;
            if (m.keys[i] == key) {
                reinterpret_cast<volatile uint64_t*>(m.values)[i] = value;
                return true;
            }
            i = i + 1 < m.cap ? i + 1 : 0;
        }
        if (static_cast<uint64_t>(m.count) + 1 > m.cap / 2) {
            *err = std::string("the ") + what + " is full";
            return false;
        }
        // value, key, then the bit and the count (a reader sees the slot only when it is complete)
        reinterpret_cast<volatile uint64_t*>(m.values)[i] = value;
        m.keys[i] = key;
        _InterlockedOr64(reinterpret_cast<volatile long long*>(&m.bits[i >> 6]), static_cast<long long>(1ull << (i & 63)));
        *reinterpret_cast<volatile uint32_t*>(map + E.hm_count) = m.count + 1;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *err = std::string("writing the ") + what + " faulted";
        return false;
    }
}

uintptr_t id_map_of(std::vector<IdMap>& cache, uintptr_t world, const char* singleton, const char* what,
                    const std::unordered_map<uint32_t, uintptr_t>& want, std::string* err) {
    for (const IdMap& x : cache)
        if (x.world == world) return x.map;
    const uintptr_t reg = g_api->singleton(world, KilnStr{singleton, std::strlen(singleton)});
    if (!reg) {
        *err = std::string("no ") + singleton + " in this world";
        return 0;
    }
    std::vector<uint32_t> keys;
    std::vector<uint64_t> vals;
    for (uintptr_t off = 0; off + 0x58 <= 0x400; off += 8) {
        // a candidate header's arrays must be readable at both ends before map_read walks them: clang-cl's __try does
        // not cover plain loads (only calls), so a junk pointer would fault the tick (rd() copies through a call)
        MapView m;
        if (!map_view(reg + off, &m) || m.count < want.size() / 2) continue;
        uint64_t probe = 0;
        const uintptr_t b = reinterpret_cast<uintptr_t>(m.bits), k = reinterpret_cast<uintptr_t>(m.keys), v = reinterpret_cast<uintptr_t>(m.values);
        if (!rd(b, &probe, 8) || !rd(b + ((m.cap + 63) / 64 - 1) * 8, &probe, 8) || !rd(k, &probe, 4) || !rd(k + (m.cap - 1) * 4, &probe, 4) ||
            !rd(v, &probe, 8) || !rd(v + (m.cap - 1) * 8, &probe, 8))
            continue;
        if (!map_read(reg + off, &keys, &vals) || keys.size() < want.size() / 2 || keys.empty()) continue;
        size_t match = 0;
        for (size_t i = 0; i < keys.size(); ++i)
            if (auto it = want.find(keys[i]); it != want.end() && it->second == vals[i]) ++match;
        if (match * 10 >= keys.size() * 9) {  // at least 90 % are exactly the resources
            cache.push_back(IdMap{world, reg + off});
            LOGI("gamedata: the %s world's %s: id map at +0x%llx, %zu entries (%zu checked against the resources)", wname(world),
                 what, static_cast<unsigned long long>(off), keys.size(), match);
            return reg + off;
        }
    }
    *err = std::string("the ") + what + "'s id map was not found (its entries did not match the resources)";
    return 0;
}

// ---- sequences: the world's ActionSequenceRegistry (docs/re/gamedata.md section 5) ----
uintptr_t seq_map_of(uintptr_t world, std::string* err) {
    for (const IdMap& x : g_seq_maps)
        if (x.world == world) return x.map;
    const GRegistry& S = greg(KILN_GD_SEQUENCES);
    const TType* st = type_named("keen::actor::ActorSequenceResource");
    if (!st) {
        *err = "keen::actor::ActorSequenceResource is not in the reflection";
        return 0;
    }
    std::unordered_map<uint32_t, uintptr_t> want;  // FNV(guid) -> resource data
    for (const GEntry& e : S.entries)
        if (e.sub == 0 && !e.clone) want.emplace(fnv1a(e.guid, 16), res_data(e.guid, st->hash));
    return id_map_of(g_seq_maps, world, "ActionSequenceRegistry", "sequence registry", want, err);
}

bool ensure_seq(Def& d, uintptr_t world) {
    if (d.worlds.count(world)) return true;
    std::string err;
    const uintptr_t map = seq_map_of(world, &err);
    if (!map || !map_put(map, fnv1a(d.guid, 16), d.data, "sequence registry", &err)) {
        d.state = "error", d.problem = err;
        return false;
    }
    d.worlds.insert(world);
    LOGI("gamedata: sequence %s (%s, clone of %s) registered in the %s world's sequence registry", d.name.c_str(),
         guid_text(d.guid).c_str(), d.base.c_str(), wname(world));
    return true;
}

uint32_t sub_id_of(const uint8_t g[16], int k) {
    return k == 0 ? fnv1a(g, 16) : fnv1a(g, 16) ^ (static_cast<uint32_t>(k) * 0x9E3779B1u);
}

KilnResult put_new(const KilnAssetsApi* A, const KilnAssetKey* key, const std::vector<uint8_t>& bytes) {
    PhaseTimer pt(PH_ASSET_PUT);
    return A->resource_put(g_owner, key, bytes.data(), bytes.size(), KILN_ASSET_PUT_NEW);
}

bool assets_ok(const KilnAssetsApi* A) {
    return A && A->header.size >= offsetof(KilnAssetsApi, resource_data) + sizeof(void*) && A->resource_data;
}

void register_sequence(Def& d, uintptr_t world) {
    GRegistry& S = greg(KILN_GD_SEQUENCES);
    const TType* st = type_named("keen::actor::ActorSequenceResource");
    const KilnAssetsApi* A = assets_api();
    if (!st || !S.type || !assets_ok(A)) {
        d.state = "error", d.problem = "sequence clones need kiln.assets and the sequence types";
        return;
    }
    if (GEntry* g = const_cast<GEntry*>(S.get(fnv1a(d.guid, 16))); g && !g->clone) {
        if (!d.data) {  // see register_template: a clone kiln.assets still serves from an earlier world is ours
            d.state = "error", d.problem = "the GUID is a game sequence's";
            return;
        }
        for (GEntry& e : S.entries)
            if (std::memcmp(e.guid, d.guid, 16) == 0) e.clone = true;
    }
    const GEntry* base = S.lookup(d.base);
    if (!base) {
        d.state = "error", d.problem = "unknown base sequence " + d.base;
        return;
    }
    KilnAssetKey key{};
    std::memcpy(key.guid, d.guid, 16);
    key.type = st->hash;
    if (!d.data) {
        const void* p = nullptr;
        size_t n = 0;
        if (A->resource_data(&key, &p, &n) != KILN_OK || !p) {
            uint64_t size = 0;
            uintptr_t src = 0;
            {
                PhaseTimer rt(PH_SEQ_RES);
                src = res_data(base->guid, st->hash, &size);
            }
            std::vector<uint8_t> bytes(static_cast<size_t>(size) + kCloneRoom);
            if (!src || size < st->size || size > (16u << 20) || !rd(src, bytes.data(), static_cast<size_t>(size))) {
                d.state = "error", d.problem = "the base sequence's resource is not readable";
                return;
            }
            store_originals(src, static_cast<size_t>(size), bytes.data());  // what rules changed on the base is not the clone's (see apply_fields)
            if (const TField* rf = st->field("resourceId")) std::memcpy(bytes.data() + rf->offset, d.guid, 16);
            if (put_new(A, &key, bytes) != KILN_OK ||
                A->resource_data(&key, &p, &n) != KILN_OK || !p) {
                d.state = "error", d.problem = "kiln.assets refused the new resource";
                return;
            }
            near_alloc_add(reinterpret_cast<uintptr_t>(p) + n - kCloneRoom, kCloneRoom);
        }
        d.data = reinterpret_cast<uintptr_t>(p);
    }
    if (!ensure_seq(d, world)) return;
    const TField* subs = st->field("subSequences");
    uintptr_t sd = 0;
    uint32_t sn = 0;
    if (!subs || !blob_array(d.data + subs->offset, &sd, &sn) || !sn) {
        d.state = "error", d.problem = "the clone has no sub-sequences";
        return;
    }
    for (uint32_t k = 0; k < sn && k < 64; ++k) {
        const uint32_t id = sub_id_of(d.guid, static_cast<int>(k));
        if (S.get(id)) continue;
        GEntry e;
        e.id = id, e.addr = sd + static_cast<uintptr_t>(k) * S.type->size, e.sub = static_cast<int32_t>(k), e.clone = true;
        e.name = k ? d.name + "#" + std::to_string(k) : d.name;
        std::memcpy(e.guid, d.guid, 16);
        S.add(std::move(e));
    }
    apply_fields(d, *S.get(sub_id_of(d.guid, 0)));
    changed();
    d.state = "registered";
    d.problem.clear();
}

// "base": "item:<name>": the template that item places. *ready: the item is there, so a failure won't change with time.
const GEntry* template_of_item(const std::string& item, bool* ready, std::string* err) {
    Registry& I = reg(KILN_CONTENT_ITEMS);
    const Entry* e = I.lookup(item);
    const Field* f = I.schema.find("equipment.placedEntity");
    if (!e || !f) {
        *err = "unknown item " + item;
        return nullptr;
    }
    *ready = true;
    uint8_t g[16] = {};
    if (!rd(e->addr + f->offset, g, 16)) {
        *err = "the item " + item + " is not readable";
        return nullptr;
    }
    const GEntry* t = greg(KILN_GD_TEMPLATES).get(fnv1a(g, 16));
    if (!t) *err = "the item " + item + " places no template";
    return t;
}

// "removeComponents": ["SubEntities"]: components of the base a clone does without (a creature's gear on another body). The list keeps its
// place and order with those entries left out; their data stays unused in the copy. An actor's own (what a puppet needs to live: section 5.1 of
// docs/re/npc-internals.md) and CurrentTransform are refused, as is a name the base doesn't have.
bool remove_components(const Def& d, const TType* type, std::vector<uint8_t>* bytes, std::string* err) {
    if (!d.body.contains("removeComponents")) return true;
    const gjson& names = d.body["removeComponents"];
    const TField* cf = type->field("components");
    if (!names.is_array() || !cf) {
        *err = "\"removeComponents\" must be a list of component types";
        return false;
    }
    static const std::set<std::string> kept = {"CurrentTransform", "LastTransform", "Actor", "ActorInput", "NetworkActor", "Velocity", "Faction",
                                               "AnimationGraphResourceComponent2", "ServerAnimationGraphInput", "NetworkAnimationGraphInput",
                                               "ServerAnimationGraphComponent2", "ServerAnimationGraphSlotComponent"};
    int32_t rel = 0;
    uint32_t count = 0;
    std::memcpy(&rel, bytes->data() + cf->offset, 4);
    std::memcpy(&count, bytes->data() + cf->offset + 4, 4);
    const size_t list_at = static_cast<size_t>(static_cast<int64_t>(cf->offset) + rel);
    if (list_at + count * 12ull > bytes->size()) {
        *err = "the base's component list is not readable";
        return false;
    }
    struct Var {
        std::string brief;
        uint32_t hash, size;
        int64_t data;
    };
    std::vector<Var> vars;
    bool actor = false;
    for (uint32_t i = 0; i < count; ++i) {
        const size_t at = list_at + i * 12ull;
        uint32_t h = 0, sz = 0;
        int32_t r = 0;
        std::memcpy(&h, bytes->data() + at, 4);
        std::memcpy(&r, bytes->data() + at + 4, 4);
        std::memcpy(&sz, bytes->data() + at + 8, 4);
        const TType* t = type_hash(h);
        vars.push_back({t ? t->brief : std::string(), h, sz, static_cast<int64_t>(at) + 4 + r});
        actor |= vars.back().brief == "Actor";
    }
    std::set<std::string> drop;
    for (const gjson& n : names) {
        if (!n.is_string()) {
            *err = "\"removeComponents\" must be a list of component types";
            return false;
        }
        const std::string brief = n.get<std::string>();
        if (std::none_of(vars.begin(), vars.end(), [&](const Var& v) { return v.brief == brief; })) {
            *err = "\"removeComponents\": the base has no " + brief;
            return false;
        }
        if (brief == "CurrentTransform" || (actor && kept.count(brief))) {
            *err = "\"removeComponents\": " + brief + " is what " + (actor ? "an actor" : "any entity") + " needs to live";
            return false;
        }
        drop.insert(brief);
    }
    size_t k = 0;
    for (const Var& v : vars) {
        if (drop.count(v.brief)) continue;
        const size_t at = list_at + k++ * 12ull;
        const int32_t r = static_cast<int32_t>(v.data - static_cast<int64_t>(at + 4));
        std::memcpy(bytes->data() + at, &v.hash, 4);
        std::memcpy(bytes->data() + at + 4, &r, 4);
        std::memcpy(bytes->data() + at + 8, &v.size, 4);
    }
    const uint32_t left = static_cast<uint32_t>(k);
    std::memcpy(bytes->data() + cf->offset + 4, &left, 4);
    return true;
}

// "addComponents": ["ComfortProvider"]: components of the game's types a clone has and its base hasn't, zeroed; its "fields" fill them
// in. The clone's component list moves behind the clone's name, in the same place the rest of its own data goes: the room for near
// allocations stays last. RegisterTemplateInfo reads the list as it does the game's, which is sorted by type name, so this one is too.
bool add_components(const Def& d, const TType* type, std::vector<uint8_t>* bytes, std::string* err) {
    if (!d.body.contains("addComponents")) return true;
    const gjson& names = d.body["addComponents"];
    const TField* cf = type->field("components");
    if (!names.is_array() || !cf) {
        *err = "\"addComponents\" must be a list of component types";
        return false;
    }
    struct Var {
        std::string brief;
        uint32_t hash, size;
        int64_t data;  // from the start of the clone; -1: new, after the list
    };
    int32_t rel = 0;
    uint32_t count = 0;
    std::memcpy(&rel, bytes->data() + cf->offset, 4);
    std::memcpy(&count, bytes->data() + cf->offset + 4, 4);
    const size_t head = bytes->size() - kCloneRoom;
    if (static_cast<uint64_t>(cf->offset) + rel + count * 12ull > head) {
        *err = "the base's component list is not readable";
        return false;
    }
    std::vector<Var> vars;
    for (uint32_t i = 0; i < count; ++i) {
        const size_t at = cf->offset + rel + i * 12ull;
        uint32_t h = 0, sz = 0;
        int32_t r = 0;
        std::memcpy(&h, bytes->data() + at, 4);
        std::memcpy(&r, bytes->data() + at + 4, 4);
        std::memcpy(&sz, bytes->data() + at + 8, 4);
        const TType* t = type_hash(h);
        vars.push_back({t ? t->brief : std::string(), h, sz, static_cast<int64_t>(at) + 4 + r});
    }
    size_t added = 0;
    for (const gjson& n : names) {
        if (!n.is_string()) {
            *err = "\"addComponents\" must be a list of component types";
            return false;
        }
        const std::string brief = n.get<std::string>();
        if (std::any_of(vars.begin(), vars.end(), [&](const Var& v) { return v.brief == brief; })) continue;
        const TType* t = type_named("keen::ecs::" + brief);
        const TType* b = t;
        while (b && b->name != "keen::ecs::Component") b = b->inner();
        if (!b) {
            *err = "\"" + brief + "\" is not a component type of the game";
            return false;
        }
        vars.push_back({brief, t->hash, t->size, -1});
        added += (static_cast<size_t>(t->size) + 15) & ~size_t(15);
    }
    std::stable_sort(vars.begin(), vars.end(), [](const Var& a, const Var& b) { return a.brief < b.brief; });
    const size_t list_at = (head + 15) & ~size_t(15);
    size_t data_at = (list_at + vars.size() * 12 + 15) & ~size_t(15);
    std::vector<uint8_t> out(data_at + added + kCloneRoom, 0);
    std::memcpy(out.data(), bytes->data(), head);
    for (size_t i = 0; i < vars.size(); ++i) {
        Var& v = vars[i];
        if (v.data < 0) {
            v.data = static_cast<int64_t>(data_at);
            data_at += (static_cast<size_t>(v.size) + 15) & ~size_t(15);
        }
        const size_t at = list_at + i * 12;
        const int32_t r = static_cast<int32_t>(v.data - static_cast<int64_t>(at + 4));
        std::memcpy(out.data() + at, &v.hash, 4);
        std::memcpy(out.data() + at + 4, &r, 4);
        std::memcpy(out.data() + at + 8, &v.size, 4);
    }
    const int32_t list_rel = static_cast<int32_t>(static_cast<int64_t>(list_at) - cf->offset);
    const uint32_t list_count = static_cast<uint32_t>(vars.size());
    std::memcpy(out.data() + cf->offset, &list_rel, 4);
    std::memcpy(out.data() + cf->offset + 4, &list_count, 4);
    *bytes = std::move(out);
    return true;
}

// Registers one template definition in the active world (store_mutex held, on its thread).
void register_template(Def& d, uintptr_t world) {
    GRegistry& T = greg(KILN_GD_TEMPLATES);
    if (!g_templates || !T.type) {
        d.state = "error", d.problem = "template clones are not available on this build (gamedata.template_clones)";
        return;
    }
    const KilnAssetsApi* A = assets_api();
    if (!assets_ok(A)) {
        d.state = "error", d.problem = "template clones need kiln.assets (new resources)";
        return;
    }
    const uint32_t id = fnv1a(d.guid, 16);
    if (GEntry* g = const_cast<GEntry*>(T.get(id)); g && !g->clone) {
        // kiln.assets still serves the clone Kiln built earlier in this process (an earlier world, e.g. a hosted game
        // before a join): the registry listed it with the game's resources. Ours, not the game's.
        if (!d.data) {
            d.state = "error", d.problem = "the GUID is a game template's";
            return;
        }
        g->clone = true;
    }
    if (const GEntry* n = T.lookup(d.name); n && std::memcmp(n->guid, d.guid, 16) != 0) {
        d.state = "error", d.problem = "the name " + d.name + " is taken (" + guid_text(n->guid) + ")";
        return;
    }
    const bool by_item = d.base.rfind("item:", 0) == 0;
    bool item_ready = false;
    std::string base_err = "unknown base template " + d.base;
    const GEntry* base = by_item ? template_of_item(d.base.substr(5), &item_ready, &base_err) : T.lookup(d.base);
    if (!base) {
        // the game's items come with the registries, a mod's after them: clones_tick tries again until the registry is complete
        const bool wait = by_item && !item_ready && reg(KILN_CONTENT_ITEMS).entries.empty();
        d.state = wait ? "waiting" : "error", d.problem = base_err;
        return;
    }
    KilnAssetKey key{};
    std::memcpy(key.guid, d.guid, 16);
    key.type = T.type->hash;
    if (!d.data) {
        // already served (an earlier world of this process)?
        const void* p = nullptr;
        size_t n = 0;
        if (A->resource_data(&key, &p, &n) == KILN_OK && p) {
            d.data = reinterpret_cast<uintptr_t>(p);
        } else {
            std::vector<uint8_t> bytes;
            std::string err;
            bool built = false;
            {
                PhaseTimer bt(PH_BUILD_BYTES);
                built = build_bytes(d, *base, T.type, "name", &bytes, &err) && remove_components(d, T.type, &bytes, &err) && add_components(d, T.type, &bytes, &err);
            }
            if (!built) {
                d.state = "error", d.problem = err;
                return;
            }
            if (put_new(A, &key, bytes) != KILN_OK ||
                A->resource_data(&key, &p, &n) != KILN_OK || !p) {
                d.state = "error", d.problem = "kiln.assets refused the new resource";
                return;
            }
            near_alloc_add(reinterpret_cast<uintptr_t>(p) + n - kCloneRoom, kCloneRoom);
            d.data = reinterpret_cast<uintptr_t>(p);
        }
    }
    if (!ensure_info(d, world, base->name)) return;
    if (!T.get(id)) {
        GEntry e;
        e.id = id, e.name = d.name, e.addr = d.data, e.clone = true;
        std::memcpy(e.guid, d.guid, 16);
        T.add(std::move(e));
    }
    apply_fields(d, *T.get(id));
    changed();
    d.state = "registered";
    d.problem.clear();
}

// ---- perks: a resource + the world's PerkRegistry id map --------------------------------------------------------
uintptr_t perk_map_of(uintptr_t world, std::string* err) {
    for (const IdMap& x : g_perk_maps)
        if (x.world == world) return x.map;
    const GRegistry& P = greg(KILN_GD_PERKS);
    std::unordered_map<uint32_t, uintptr_t> want;
    for (const GEntry& e : P.entries)
        if (!e.clone) want.emplace(fnv1a(e.guid, 16), e.addr);
    return id_map_of(g_perk_maps, world, "PerkRegistry", "perk registry", want, err);
}

bool ensure_perk(Def& d, uintptr_t world) {
    if (d.worlds.count(world)) return true;
    std::string err;
    const uintptr_t map = perk_map_of(world, &err);
    if (!map || !map_put(map, fnv1a(d.guid, 16), d.data, "perk registry", &err)) {
        d.state = "error", d.problem = err;
        return false;
    }
    d.worlds.insert(world);
    MapView m;
    map_view(map, &m);
    LOGI("gamedata: perk %s (%s, id 0x%08x, clone of %s%s) registered in the %s world's perk registry (%u perks)", d.name.c_str(),
         guid_text(d.guid).c_str(), fnv1a(d.guid, 16), d.base.c_str(), d.placeholder ? ", placeholder" : "", wname(world), m.count);
    return true;
}

void register_perk(Def& d, uintptr_t world) {
    GRegistry& P = greg(KILN_GD_PERKS);
    const KilnAssetsApi* A = assets_api();
    if (!P.type || !P.available || !assets_ok(A)) {
        d.state = "error", d.problem = "perk clones need kiln.assets and the perk registry";
        return;
    }
    const uint32_t id = fnv1a(d.guid, 16);
    if (GEntry* g = const_cast<GEntry*>(P.get(id)); g && !g->clone) {
        if (!d.data) {  // kiln.assets serves an earlier world's clone: the registry listed it with the game's
            d.state = "error", d.problem = "the GUID is a game perk's";
            return;
        }
        g->clone = true;
    }
    if (const GEntry* n = P.lookup(d.name); n && std::memcmp(n->guid, d.guid, 16) != 0) {
        d.state = "error", d.problem = "the name " + d.name + " is taken (" + guid_text(n->guid) + ")";
        return;
    }
    const GEntry* base = P.lookup(d.base);
    if (!base || std::memcmp(base->guid, d.guid, 16) == 0) {
        d.state = "error", d.problem = "unknown base perk " + d.base;
        return;
    }
    KilnAssetKey key{};
    std::memcpy(key.guid, d.guid, 16);
    key.type = P.type->hash;
    if (!d.data || d.rebuild) {
        const void* p = nullptr;
        size_t n = 0;
        std::vector<uint8_t> bytes;
        std::string err;
        if (!build_bytes(d, *base, P.type, "debugName", &bytes, &err)) {
            d.state = "error", d.problem = err;
            return;
        }
        dress(d, P.type, bytes.data(), bytes.size());
        if (d.data) {  // a placeholder taken over: the registry keeps the pointer, so the bytes change in place
            if (A->resource_data(&key, &p, &n) != KILN_OK || reinterpret_cast<uintptr_t>(p) != d.data || n != bytes.size()) {
                d.state = "error", d.problem = "the placeholder's resource cannot be rebuilt in place (restart the server)";
                return;
            }
            std::memcpy(reinterpret_cast<void*>(d.data), bytes.data(), bytes.size());
        } else if (A->resource_data(&key, &p, &n) == KILN_OK && p) {
            d.data = reinterpret_cast<uintptr_t>(p);  // an earlier world of this process
        } else {
            if (put_new(A, &key, bytes) != KILN_OK ||
                A->resource_data(&key, &p, &n) != KILN_OK || !p) {
                d.state = "error", d.problem = "kiln.assets refused the new resource";
                return;
            }
            near_alloc_add(reinterpret_cast<uintptr_t>(p) + n - kCloneRoom, kCloneRoom);
            d.data = reinterpret_cast<uintptr_t>(p);
        }
        d.rebuild = false;
    }
    if (!ensure_perk(d, world)) return;
    if (!P.get(id)) {
        GEntry e;
        e.id = id, e.name = d.name, e.addr = d.data, e.clone = true;
        std::memcpy(e.guid, d.guid, 16);
        P.add(std::move(e));
    }
    apply_fields(d, *P.get(id));
    changed();
    publish_texts(d);
    d.state = "registered";
    d.problem.clear();
}

// ---- buffs: Kiln's copy of the buff collection ------------------------------------------------------------------
struct BuffWorld {
    uintptr_t world = 0, registry = 0, slot = 0;
};
struct BuffMirror {
    bool tried = false, ok = false;
    std::string why;
    const TType* ct = nullptr;  // keen::BuffTypeCollectionResource
    uint32_t f_entries = 0, f_keys = 0, f_values = 0, f_buffs = 0;  // header field offsets
    uintptr_t coll = 0;         // the game's collection (resource data)
    uintptr_t orig = 0;         // its buffs array
    uint32_t n = 0, stride = 0, cap = 0, used = 0, buckets0 = 0;
    uintptr_t array = 0;        // Kiln's records: n game records, then clones
    uintptr_t header = 0;       // the collection the registries read (Kiln's; 0 = none built yet)
    uint32_t header_count = 0;  // records it lists
    std::set<uintptr_t> headers;     // every header Kiln built (a registry may still point at an older one)
    std::vector<uint32_t> relocs;    // positions of the i32 self-relative offsets inside a record
    std::vector<uint32_t> ids;       // record index -> buffTypeId
    std::vector<BuffWorld> worlds;
    uint64_t syncs = 0, words = 0;
    bool reach_warned = false;
} BM;

std::mutex g_bg_mu;
std::unordered_map<uint32_t, std::array<uint8_t, 16>> g_buff_guids;  // buffTypeId -> GUID (references, clones)

void relocs_of(const TType* t, uint32_t base, int depth, std::vector<uint32_t>* out) {
    if (!t || depth > 8) return;
    for (const TField& f : t->fields) {
        const TType* ft = f.type() ? f.type()->resolved() : nullptr;
        if (!ft) continue;
        const uint32_t o = base + f.offset;
        if (ft->kind == K_BLOBARRAY || ft->kind == K_BLOBSTRING || ft->kind == K_BLOBOPT) out->push_back(o);
        else if (ft->kind == K_BLOBVAR) out->push_back(o + 4);  // {u32 type, i32 rel from +4, u32 size}
        else if (ft->kind == K_STRUCT) relocs_of(ft, o, depth + 1, out);
        else if (ft->kind == K_ARRAY && ft->count) {
            const TType* el = ft->inner() ? ft->inner()->resolved() : nullptr;
            const uint32_t es = ft->size / ft->count;
            for (uint32_t j = 0; el && j < ft->count && j < 256; ++j) {
                if (el->kind == K_STRUCT) relocs_of(el, o + j * es, depth + 1, out);
                else if (el->kind == K_BLOBARRAY || el->kind == K_BLOBSTRING || el->kind == K_BLOBOPT) out->push_back(o + j * es);
            }
        }
    }
}

// A record copied from `from` (its bytes in rec) to `to`: every self-relative offset keeps pointing where it did.
bool rebase(uint8_t* rec, uintptr_t from, uintptr_t to) {
    for (uint32_t p : BM.relocs) {
        int32_t rel = 0;
        std::memcpy(&rel, rec + p, 4);
        if (!rel) continue;
        const int64_t target = static_cast<int64_t>(from + p) + rel;
        const int64_t nrel = target - static_cast<int64_t>(to + p);
        if (nrel < INT32_MIN || nrel > INT32_MAX) return false;
        const int32_t n32 = static_cast<int32_t>(nrel);
        std::memcpy(rec + p, &n32, 4);
    }
    return true;
}

// Writes rec over Kiln's record at dst, 8 bytes at a time, only the words that differ (the game may read it).
uint32_t put_words(uintptr_t dst, const uint8_t* rec, size_t n) {
    auto* d = reinterpret_cast<volatile uint64_t*>(dst);
    uint32_t w = 0;
    for (size_t i = 0; i < n / 8; ++i) {
        uint64_t v;
        std::memcpy(&v, rec + i * 8, 8);
        if (d[i] != v) d[i] = v, ++w;
    }
    return w;
}

bool mirror_init(std::string* err) {
    if (BM.ok) return true;
    if (BM.tried) {
        *err = BM.why;
        return false;
    }
    BM.tried = true;
    auto fail = [&](const std::string& why) {
        BM.why = "buff clones are not available: " + why;
        *err = BM.why;
        LOGW("gamedata: %s", BM.why.c_str());
        return false;
    };
    const GRegistry& B = greg(KILN_GD_BUFFS);
    BM.ct = type_named("keen::BuffTypeCollectionResource");
    const TField* fm = BM.ct ? BM.ct->field("buffMap") : nullptr;
    const TField* fb = BM.ct ? BM.ct->field("buffs") : nullptr;
    const TType* mt = fm && fm->type() ? fm->type()->resolved() : nullptr;
    const TField* me = mt ? mt->field("entries") : nullptr;
    const TField* mk = mt ? mt->field("keys") : nullptr;
    const TField* mv = mt ? mt->field("values") : nullptr;
    const TField* fid = B.type ? B.type->field("buffTypeId") : nullptr;
    if (!B.type || !fb || !me || !mk || !mv || !fid) return fail("the collection's layout is not in the reflection");
    BM.f_entries = fm->offset + me->offset, BM.f_keys = fm->offset + mk->offset, BM.f_values = fm->offset + mv->offset;
    BM.f_buffs = fb->offset;
    std::vector<std::array<uint8_t, 16>> guids;
    if (!res_list(BM.ct->hash, &guids) || guids.size() != 1) return fail("not exactly one BuffTypeCollectionResource");
    BM.coll = res_data(guids[0].data(), BM.ct->hash);
    uintptr_t ed = 0;
    if (!BM.coll || !blob_array(BM.coll + BM.f_buffs, &BM.orig, &BM.n) || !BM.n ||
        !blob_array(BM.coll + BM.f_entries, &ed, &BM.buckets0) || !BM.buckets0 || (BM.buckets0 & (BM.buckets0 - 1)))
        return fail("the buff collection is not readable");
    BM.stride = B.type->size;
    if (BM.stride % 8) return fail("a buff record is not 8-byte sized");
    relocs_of(B.type, 0, 0, &BM.relocs);
    BM.cap = BM.n + kBuffRoom;
    BM.array = reinterpret_cast<uintptr_t>(near_alloc(BM.coll, static_cast<size_t>(BM.cap) * BM.stride));
    if (!BM.array) return fail("no memory near the buff collection");
    std::vector<uint8_t> rec(BM.stride);
    BM.ids.resize(BM.n);
    for (uint32_t i = 0; i < BM.n; ++i) {
        const uintptr_t src = BM.orig + static_cast<uintptr_t>(i) * BM.stride, dst = BM.array + static_cast<uintptr_t>(i) * BM.stride;
        if (!rd(src, rec.data(), BM.stride) || !rebase(rec.data(), src, dst)) return fail("a buff record is out of reach");
        std::memcpy(reinterpret_cast<void*>(dst), rec.data(), BM.stride);
        std::memcpy(&BM.ids[i], rec.data() + fid->offset, 4);
    }
    BM.ok = true;
    LOGI("gamedata: buff clones: Kiln's copy of the buff collection at %p (%u game buffs, room for %u clones; %zu self-relative "
         "offsets per %u-byte record; the game's collection %p, %u map buckets)",
         reinterpret_cast<void*>(BM.array), BM.n, kBuffRoom, BM.relocs.size(), BM.stride, reinterpret_cast<void*>(BM.coll), BM.buckets0);
    return true;
}

// A new collection header listing n + used records, with its own map (id & (buckets - 1) -> {start, count}, keys,
// values = record indices). Kiln's memory near the game's collection; never freed (a reader may hold an old one).
bool build_header(std::string* err) {
    const uint32_t T = BM.n + BM.used;
    uint32_t B = BM.buckets0;
    while (T > B / 4 * 3 && B < (1u << 20)) B <<= 1;
    const size_t hsz = (BM.ct->size + 15) & ~size_t(15), esz = static_cast<size_t>(B) * 8, ksz = static_cast<size_t>(T) * 4;
    auto* blk = static_cast<uint8_t*>(near_alloc(BM.coll, hsz + esz + 2 * ksz));
    if (!blk) {
        *err = "no memory near the buff collection";
        return false;
    }
    std::memset(blk, 0, hsz + esz + 2 * ksz);
    uint8_t* entries = blk + hsz;
    auto* keys = reinterpret_cast<uint32_t*>(blk + hsz + esz);
    auto* values = reinterpret_cast<uint32_t*>(blk + hsz + esz + ksz);
    std::vector<uint32_t> idx(T);
    for (uint32_t i = 0; i < T; ++i) idx[i] = i;
    std::stable_sort(idx.begin(), idx.end(), [&](uint32_t a, uint32_t b) { return (BM.ids[a] & (B - 1)) < (BM.ids[b] & (B - 1)); });
    std::vector<uint32_t> start(B, 0), count(B, 0);
    for (uint32_t k = 0; k < T; ++k) {
        const uint32_t b = BM.ids[idx[k]] & (B - 1);
        if (!count[b]) start[b] = k;
        ++count[b];
        keys[k] = BM.ids[idx[k]];
        values[k] = idx[k];
    }
    for (uint32_t b = 0; b < B; ++b) {
        const uint32_t s = count[b] ? start[b] : (b ? start[b - 1] + count[b - 1] : 0);
        start[b] = s;
        std::memcpy(entries + b * 8, &s, 4);
        std::memcpy(entries + b * 8 + 4, &count[b], 4);
    }
    auto arr = [&](uint32_t field, uintptr_t data, uint32_t n) {
        const int64_t rel = static_cast<int64_t>(data) - static_cast<int64_t>(reinterpret_cast<uintptr_t>(blk) + field);
        const int32_t r32 = static_cast<int32_t>(rel);
        std::memcpy(blk + field, &r32, 4);
        std::memcpy(blk + field + 4, &n, 4);
        return rel >= INT32_MIN && rel <= INT32_MAX;
    };
    if (!arr(BM.f_entries, reinterpret_cast<uintptr_t>(entries), B) || !arr(BM.f_keys, reinterpret_cast<uintptr_t>(keys), T) ||
        !arr(BM.f_values, reinterpret_cast<uintptr_t>(values), T) || !arr(BM.f_buffs, BM.array, T)) {
        *err = "Kiln's buff collection is out of reach";
        return false;
    }
    BM.header = reinterpret_cast<uintptr_t>(blk);
    BM.header_count = T;
    BM.headers.insert(BM.header);
    return true;
}

// Points a world's BuffRegistry at Kiln's collection (its own thread). The slot is found, not assumed: the qword in the
// registry that holds the game's collection (or a header of Kiln's); the sigdb's +0x20 (getBuffType) must agree.
bool buff_world(uintptr_t world, std::string* err) {
    BuffWorld* w = nullptr;
    for (BuffWorld& x : BM.worlds)
        if (x.world == world) w = &x;
    if (!w) {
        const uintptr_t reg = g_api->singleton(world, KilnStr{"BuffRegistry", 12});
        if (!reg) {
            *err = "no BuffRegistry in this world yet";
            return false;
        }
        uintptr_t slot = 0;
        for (uintptr_t off = 0; off < 0x200 && !slot; off += 8) {
            uintptr_t v = 0;
            if (rdv(reg + off, v) && v && (v == BM.coll || BM.headers.count(v))) slot = reg + off;
        }
        if (!slot) {
            *err = "the BuffRegistry does not point at the buff collection (not loaded yet?)";
            return false;
        }
        if (g_buff_slot_sig >= 0 && slot - reg != static_cast<uintptr_t>(g_buff_slot_sig)) {
            *err = "the BuffRegistry holds the collection at +0x" + std::to_string(slot - reg) + ", getBuffType reads +" +
                   std::to_string(g_buff_slot_sig) + ": refused";
            return false;
        }
        BM.worlds.push_back(BuffWorld{world, reg, slot});
        w = &BM.worlds.back();
    }
    uintptr_t cur = 0;
    rdv(w->slot, cur);
    if (cur != BM.header) {
        __try {
            _InterlockedExchange64(reinterpret_cast<volatile long long*>(w->slot), static_cast<long long>(BM.header));
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            *err = "writing the BuffRegistry faulted";
            return false;
        }
        LOGI("gamedata: the %s world's BuffRegistry (%p +0x%llx) reads Kiln's buff collection: %u buffs (%u clones)", wname(world),
             reinterpret_cast<void*>(w->registry), static_cast<unsigned long long>(w->slot - w->registry), BM.header_count, BM.used);
    }
    return true;
}

// The game's own lookup of a clone in a world (once per world): proof that getBuffType finds Kiln's record.
void probe_buff(Def& d, uintptr_t world) {
    if (!g_get_buff || d.probed.count(world)) return;
    for (const BuffWorld& w : BM.worlds)
        if (w.world == world) {
            d.probed.insert(world);
            const uintptr_t got = call_get_buff(fnv1a(d.guid, 16), w.registry);
            const uintptr_t first = call_get_buff(BM.ids.empty() ? 0 : BM.ids[0], w.registry);
            LOGI("gamedata: the game's getBuffType(0x%08x) in the %s world = %p (%s); a game buff 0x%08x = %p (%s)", fnv1a(d.guid, 16),
                 wname(world), reinterpret_cast<void*>(got), got == d.data ? "the clone's record" : "NOT the clone's record",
                 BM.ids.empty() ? 0 : BM.ids[0], reinterpret_cast<void*>(first), first == BM.array ? "Kiln's copy" : "the game's own");
        }
}

void buff_registered(Def& d, uintptr_t world);

void register_buff(Def& d, uintptr_t world) {
    GRegistry& B = greg(KILN_GD_BUFFS);
    std::string err;
    if (!B.type || !B.available) {
        d.state = "error", d.problem = "the buff registry is not available";
        return;
    }
    if (!mirror_init(&err)) {
        d.state = "error", d.problem = err;
        return;
    }
    const uint32_t id = fnv1a(d.guid, 16);
    for (uint32_t i = 0; i < BM.n; ++i)
        if (BM.ids[i] == id) {
            char b[16];
            std::snprintf(b, sizeof b, "0x%08x", id);
            d.state = "error", d.problem = std::string("the GUID is a game buff's (id ") + b + ")";
            return;
        }
    if (const GEntry* n = B.lookup(d.name); n && n->id != id) {
        d.state = "error", d.problem = "the name " + d.name + " is taken";
        return;
    }
    const GEntry* base = B.lookup(d.base);
    if (!base || base->id == id) {
        d.state = "error", d.problem = "unknown base buff " + d.base;
        return;
    }
    if (d.slot == UINT32_MAX) {
        if (BM.used >= kBuffRoom) {
            d.state = "error", d.problem = "Kiln's buff collection is full (" + std::to_string(kBuffRoom) + " clones)";
            return;
        }
        d.slot = BM.n + BM.used++;
        BM.ids.push_back(id);
    }
    const uintptr_t dst = BM.array + static_cast<uintptr_t>(d.slot) * BM.stride;
    if (!d.data || d.rebuild) {
        std::vector<uint8_t> rec(BM.stride);
        const TField* fid = B.type->field("buffTypeId");
        if (!rd(base->addr, rec.data(), BM.stride)) {
            d.state = "error", d.problem = "the base buff's record is not readable";
            return;
        }
        store_originals(base->addr, BM.stride, rec.data());  // what rules changed on the base is not the clone's (see apply_fields)
        if (!rebase(rec.data(), base->addr, dst) || !fid) {
            d.state = "error", d.problem = "the base buff's record is not readable";
            return;
        }
        std::memcpy(rec.data() + fid->offset, &id, 4);
        dress(d, B.type, rec.data(), rec.size());
        put_words(dst, rec.data(), rec.size());
        d.data = dst;
        d.rebuild = false;
    }
    // in a batch, the collection's header is built and the world pointed at it once, at the end of the turn (buffs_attach)
    if (!g_batch) {
        if (BM.header_count != BM.n + BM.used && !build_header(&err)) {
            d.state = "error", d.problem = err;
            return;
        }
        if (!buff_world(world, &err)) {
            d.state = "waiting", d.problem = err;  // clones_tick tries again
            return;
        }
        d.worlds.insert(world);
    }
    {
        std::lock_guard lock(g_bg_mu);
        std::array<uint8_t, 16> g;
        std::memcpy(g.data(), d.guid, 16);
        g_buff_guids[id] = g;
    }
    if (!B.get(id)) {
        GEntry e;
        e.id = id, e.name = d.name, e.addr = dst, e.clone = true;
        std::memcpy(e.guid, d.guid, 16);
        B.add(std::move(e));
    }
    apply_fields(d, *B.get(id));
    changed();
    publish_texts(d);
    if (g_batch) {
        g_batch_buffs.push_back(static_cast<size_t>(&d - g_defs.data()));
        return;
    }
    buff_registered(d, world);
}

void buff_registered(Def& d, uintptr_t world) {
    if (d.state != "registered")
        LOGI("gamedata: buff %s (%s, id 0x%08x, clone of %s%s): record %u of Kiln's buff collection (%p)", d.name.c_str(),
             guid_text(d.guid).c_str(), fnv1a(d.guid, 16), d.base.c_str(), d.placeholder ? ", placeholder" : "", d.slot,
             reinterpret_cast<void*>(d.data));
    d.state = "registered";
    d.problem.clear();
    probe_buff(d, world);
}

// The end of a batch's turn: the writes and event order, then the buff collection's header with every buff clone of the turn.
void batch_finish(uintptr_t world) {
    if (g_batch_changes) {
        g_batch_changes = false;
        after_changes();
    }
    if (g_batch_buffs.empty()) return;
    std::string err;
    const bool built = BM.header_count == BM.n + BM.used || build_header(&err);
    const bool attached = built && buff_world(world, &err);
    for (size_t i : g_batch_buffs) {
        Def& d = g_defs[i];
        if (!built) d.state = "error", d.problem = err;
        else if (!attached) d.state = "waiting", d.problem = err;  // clones_tick tries again
        else {
            d.worlds.insert(world);
            buff_registered(d, world);
        }
    }
    g_batch_buffs.clear();
}

// One definition as definitions() lists it (store_mutex and g_mu held).
gjson listing(const Def& d) {
    char id[16];
    std::snprintf(id, sizeof id, "0x%08x", fnv1a(d.guid, 16));
    gjson x = {{"section", section_name(d.section)}, {"guid", guid_text(d.guid)}, {"id", id}, {"name", d.name}, {"base", d.base},
               {"owner", owner_label(d.owner)}, {"mod", d.mod}, {"source", d.source}, {"state", d.orphan ? "orphaned" : d.state},
               {"placeholder", d.placeholder}, {"worlds", d.worlds.size()}, {"version", d.version}, {"definition", d.body}};
    if (d.data) x["address"] = d.data;
    if (!d.texts.empty()) x["texts"] = d.texts;
    if (!d.problem.empty()) x["error"] = d.problem;
    if (!d.warnings.empty()) x["warnings"] = d.warnings;
    return x;
}

void register_def(Def& d, uintptr_t world) {
    PhaseTimer pt(PH_GD_REGISTER);
    switch (d.section) {
        case KILN_GD_TEMPLATES:
            register_template(d, world);
            if (!d.texts.empty()) publish_texts(d);
            break;
        case KILN_GD_SEQUENCES: register_sequence(d, world); break;
        case KILN_GD_PERKS: register_perk(d, world); break;
        case KILN_GD_BUFFS: register_buff(d, world); break;
        default: break;
    }
}

}  // namespace

// ---- buffs: the mirror follows the game's records; buff GUIDs ----
void buffs_sync() {
    if (!BM.ok || !BM.header) return;
    std::vector<uint8_t> rec(BM.stride);
    uint32_t words = 0;
    for (uint32_t i = 0; i < BM.n; ++i) {
        const uintptr_t src = BM.orig + static_cast<uintptr_t>(i) * BM.stride, dst = BM.array + static_cast<uintptr_t>(i) * BM.stride;
        if (!rd(src, rec.data(), BM.stride)) continue;
        if (!rebase(rec.data(), src, dst)) {
            if (!BM.reach_warned) LOGW("gamedata: buff record %u: a changed list is out of reach of Kiln's copy; it keeps the old one", i);
            BM.reach_warned = true;
            continue;
        }
        words += put_words(dst, rec.data(), BM.stride);
    }
    ++BM.syncs;
    BM.words += words;
    if (words) LOGD("gamedata: buff copy: %u word(s) followed the game's records", words);
}

void buff_guid_note(const uint8_t g[16]) {
    std::array<uint8_t, 16> a;
    std::memcpy(a.data(), g, 16);
    std::lock_guard lock(g_bg_mu);
    g_buff_guids.emplace(fnv1a(g, 16), a);
}

bool buff_guid(uint32_t id, uint8_t out[16]) {
    {
        std::lock_guard lock(g_bg_mu);
        if (auto it = g_buff_guids.find(id); it != g_buff_guids.end()) {
            std::memcpy(out, it->second.data(), 16);
            return true;
        }
    }
    configs_scan();  // once per world: the references in the game data
    std::lock_guard lock(g_bg_mu);
    auto it = g_buff_guids.find(id);
    if (it == g_buff_guids.end()) return false;
    std::memcpy(out, it->second.data(), 16);
    return true;
}

namespace {
// A client that hosts: the server world is kiln.content's, but the client world of the same process creates the
// replicated entities and needs the clones too (templates: its own init registers clones served before it starts;
// this covers clones defined later; perks: its own PerkRegistry; buffs: its own BuffRegistry pointer). On the client
// world's thread.
void KILN_CALL on_client_pre(const KilnTickInfo* tick, void*) {
    if (tick->step % 25 != 11) return;
    const uintptr_t cw = g_api->world(KILN_WORLD_CLIENT);
    if (!cw || !g_api->world(KILN_WORLD_SERVER)) return;
    std::lock_guard l1(store_mutex());
    std::lock_guard lock(g_mu);
    for (Def& d : g_defs)
        if (d.state == "registered" && d.data && !d.worlds.count(cw)) {
            std::string err;
            if (d.section == KILN_GD_TEMPLATES) ensure_info(d, cw, d.base);
            else if (d.section == KILN_GD_SEQUENCES) ensure_seq(d, cw);
            else if (d.section == KILN_GD_PERKS) ensure_perk(d, cw);
            else if (d.section == KILN_GD_BUFFS) {
                if (buff_world(cw, &err)) d.worlds.insert(cw), probe_buff(d, cw);
                else LOGD("gamedata: buff %s in the client world: %s", d.name.c_str(), err.c_str());
                continue;
            }
            if (d.state == "error") LOGW("gamedata: %s %s in the client world: %s", section_name(d.section), d.name.c_str(), d.problem.c_str());
            d.state = "registered";
        }
}
}  // namespace

void clones_boot() {
    g_defs_owner = g_api->owner_create(KilnStr{"gamedata:definitions", 20});
    set_owner_label(g_defs_owner, "gamedata definitions");
    uintptr_t fn = 0;
    g_templates = g_api->feature(KilnStr{"gamedata.template_clones", 24}) &&
                  g_api->symbol(KilnStr{"gamedata/RegisterTemplateInfo", 29}, &fn) == KILN_OK && fn &&
                  g_api->constant(KilnStr{"world.storage", 13}, &g_storage) == KILN_OK &&
                  g_api->constant(KilnStr{"gamedata/tmpl.array", 19}, &g_tarr) == KILN_OK &&
                  g_api->constant(KilnStr{"gamedata/tarr.cap", 17}, &g_tcap) == KILN_OK &&
                  g_api->constant(KilnStr{"gamedata/tarr.count", 19}, &g_tcount) == KILN_OK;
    g_register = reinterpret_cast<RegisterFn>(fn);
    uintptr_t gb = 0;
    if (g_api->symbol(KilnStr{"gamedata/GetBuffType", 20}, &gb) == KILN_OK && gb) g_get_buff = reinterpret_cast<GetBuffFn>(gb);
    int64_t slot = -1;
    if (g_api->constant(KilnStr{"gamedata/buffreg.collection", 27}, &slot) == KILN_OK) g_buff_slot_sig = slot;
    if (g_api->side() == KILN_SIDE_CLIENT) {
        KilnHandle h = 0;
        g_api->subscribe_world_tick(g_owner, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h);
    }
    LOGI("gamedata: template clones %s; sequence, perk and buff clones on (the game's buff lookup %s)",
         g_templates ? "on" : "OFF (gamedata.template_clones not resolved)", g_get_buff ? "resolved for the self-check" : "not resolved");
}

// Every definition, in turns of about 10 ms of the store lock: a game thread that needs it (a drop table the game loads) waits one turn
// at most, and each turn writes once.
void clones_world_ready(uintptr_t world, uint32_t) {
    size_t i = 0;
    for (bool more = true; more;) {
        std::lock_guard l1(store_mutex());
        std::lock_guard lock(g_mu);
        const auto t0 = std::chrono::steady_clock::now();
        g_batch = true;
        while (i < g_defs.size()) {
            register_def(g_defs[i++], world);
            if (std::chrono::steady_clock::now() - t0 > std::chrono::milliseconds(10)) break;
        }
        batch_finish(world);
        g_batch = false;
        more = i < g_defs.size();
    }
}

void clones_world_reset() {
    std::lock_guard lock(g_mu);
    g_defs_layered.clear();  // the store forgets every layer with the world
    for (Def& d : g_defs) {
        if (d.state == "registered" || d.state == "waiting") d.state = "pending";  // the next world registers them again
        d.worlds.clear();  // a new world may get the same address
        d.probed.clear();
    }
    g_seq_maps.clear();
    g_perk_maps.clear();
    BM.worlds.clear();
}

void clones_tick(uint64_t step) {
    if (step % 25 != 3) return;
    const uintptr_t w = active_world();
    if (!w || !g_api->on_world_thread(active_kind())) return;
    std::lock_guard l1(store_mutex());
    std::lock_guard lock(g_mu);
    for (Def& d : g_defs)
        if (d.state == "waiting") register_def(d, w);
    if (BM.ok && BM.header) {
        std::string err;
        for (BuffWorld& x : BM.worlds)
            if (x.world == w) buff_world(w, &err);  // the registry reloaded its collection? point it back
        buffs_sync();
    }
}

KilnResult clones_define(KilnOwner owner, uint32_t section, const std::string& json, bool pack, const std::string& source,
                         std::string* report) {
    PhaseTimer pt(PH_GD_DEFINE);
    const gjson j = gjson::parse(json, nullptr, false);
    gjson r = {{"ok", false}};
    auto fail = [&](const std::string& why, KilnResult rc = KILN_ERR_INVALID_ARGUMENT) {
        r["error"] = why;
        *report = r.dump();
        return rc;
    };
    if (j.is_discarded() || !j.is_object()) return fail("a definition is a JSON object");
    r["guid"] = j.value("guid", "");
    r["name"] = j.value("name", "");
    if (section != KILN_GD_TEMPLATES && section != KILN_GD_SEQUENCES && section != KILN_GD_PERKS && section != KILN_GD_BUFFS)
        return fail(std::string(section_name(section)) + ": clones of this section are not available (templates, sequences, perks, buffs)",
                    KILN_ERR_UNAVAILABLE);
    Def d;
    d.section = section, d.owner = owner, d.source = source, d.body = j;
    if (!guid_parse(j.value("guid", ""), d.guid)) return fail("\"guid\": a fixed GUID is required");
    bool zero = true;
    for (uint8_t b : d.guid) zero = zero && !b;
    if (zero) return fail("\"guid\": the zero GUID");
    d.name = j.value("name", "");
    if (!valid_name(d.name)) return fail("\"name\": letters, digits, _ - . (at most 128)");
    d.base = j.value("base", "");
    if (d.base.empty()) return fail("\"base\": the entry to clone is required");
    if (j.contains("fields")) {
        if (!j["fields"].is_object()) return fail("\"fields\": an object of paths");
        d.fields = j["fields"];
    }
    // texts (perks, buffs): every language from displayName / description, per language from "texts"
    auto lang_ok = [](const std::string& k) {
        if (k == "*") return true;
        if (k.size() < 2 || k.size() > 12) return false;
        for (char c : k)
            if (!(std::isalnum(static_cast<unsigned char>(c)) || c == '-' || c == '_')) return false;
        return true;
    };
    for (const char* k : {"displayName", "description"})
        if (j.contains(k)) {
            if (!j[k].is_string()) return fail(std::string("\"") + k + "\" must be a string");
            if (!j[k].get<std::string>().empty()) d.texts["*"][std::strcmp(k, "displayName") == 0 ? "name" : "description"] = j[k];
        }
    if (j.contains("texts")) {
        if (!j["texts"].is_object()) return fail("\"texts\" must be { \"<language>\": { \"name\", \"description\" } }");
        for (auto& [lang, t] : j["texts"].items()) {
            if (!lang_ok(lang)) return fail("texts: \"" + lang + "\" is not a language key (\"en\", \"de\", \"pt-BR\", \"*\")");
            if (t.is_string()) {
                d.texts[lang]["name"] = t;
                continue;
            }
            if (!t.is_object()) return fail("texts." + lang + " must be an object { \"name\", \"description\" }");
            for (auto& [slot, text] : t.items()) {
                // a template's slots are its own (what its loca fields name by tag); a perk or a buff has a name and a description
                const bool own = section == KILN_GD_TEMPLATES && !slot.empty() && slot.size() <= 32 &&
                                 std::all_of(slot.begin(), slot.end(), [](char c) { return std::isalnum(static_cast<unsigned char>(c)) != 0; });
                if (!own && slot != "name" && slot != "description") return fail("texts." + lang + "." + slot + ": a perk or buff has a name and a description; a template's slots are letters and digits");
                if (!text.is_string()) return fail("texts." + lang + "." + slot + " must be a string");
                if (!text.get<std::string>().empty()) d.texts[lang][slot] = text;
            }
        }
    }
    std::vector<std::string> notes;
    if (!d.texts.empty() && section == KILN_GD_SEQUENCES) {
        notes.push_back("texts are for perks, buffs and templates (ignored)");
        d.texts = gjson::object();
    }
    if (j.contains("placeholder")) {
        if (!j["placeholder"].is_object()) return fail("\"placeholder\" is Kiln's (missing content)");
        d.placeholder = true;
        d.mod = j["placeholder"].value("mod", "");
    } else {
        d.mod = mod_of_label(owner_label(owner));
    }
    std::lock_guard l1(store_mutex());
    std::lock_guard lock(g_mu);
    if (pack) g_seen_pack.insert(guid_text(d.guid));
    std::string state = d.placeholder ? "placeholder" : "defined";
    auto it = std::find_if(g_defs.begin(), g_defs.end(), [&](const Def& x) { return std::memcmp(x.guid, d.guid, 16) == 0; });
    if (it != g_defs.end()) {
        if (it->section != section) return fail("the GUID is a " + std::string(section_name(it->section)) + " clone's");
        if (it->base != d.base && it->data) return fail("a registered clone cannot change its base (" + it->base + "); use a new GUID");
        if (it->name != d.name && it->data) return fail("a registered clone cannot change its name (" + it->name + "); use a new GUID");
        if (owner != it->owner && !it->orphan && owner_label(owner) == "server sync" && owner_label(it->owner) != "server sync") {
            // the content sync (a server's definitions, or the round-trip self-check) of a clone this game defines
            // itself: the local definition stays (as ContentSync does for items)
            r["ok"] = true;
            r["state"] = "local";
            r["id"] = fnv1a(it->guid, 16);
            *report = r.dump();
            return KILN_OK;
        }
        if (d.placeholder && !it->placeholder && !it->orphan) {
            // the clone is defined (its mod is here): no placeholder needed
            r["ok"] = true;
            r["state"] = "defined";
            r["id"] = fnv1a(it->guid, 16);
            r["warning"] = "defined by " + owner_label(it->owner) + ": no placeholder needed";
            *report = r.dump();
            return KILN_OK;
        }
        if (it->placeholder != d.placeholder) it->rebuild = it->data != 0;  // inert <-> the mod's record
        if (it->placeholder && !d.placeholder) {
            state = "restored";
            LOGI("gamedata: %s %s is back (%s): it takes its GUID %s back from the placeholder", section_name(section), d.name.c_str(),
                 owner_label(owner).c_str(), guid_text(d.guid).c_str());
        } else if (!d.placeholder || it->placeholder) {
            state = it->fields == d.fields && it->texts == d.texts ? (it->owner == owner ? "unchanged" : "adopted") : "redefined";
        }
        it->owner = owner, it->source = source, it->fields = d.fields, it->body = j, it->orphan = false;
        it->texts = d.texts, it->placeholder = d.placeholder, it->mod = d.mod;
        ++it->version;
    } else {
        g_defs.push_back(std::move(d));
        it = g_defs.end() - 1;
    }
    const uintptr_t w = active_world();
    if (w && g_api->on_world_thread(active_kind()) && res_ready()) register_def(*it, w);
    const bool err = it->state == "error";
    r["ok"] = !err;
    r["state"] = err ? "error" : it->state == "waiting" ? "waiting" : state;
    r["id"] = fnv1a(it->guid, 16);
    if (err || it->state == "waiting") r["error"] = it->problem;
    for (const std::string& n : notes) it->warnings.push_back(n);
    if (!it->warnings.empty()) r["warnings"] = it->warnings;
    r["entry"] = listing(*it);  // what definitions() lists for it: the caller needs not list them all
    *report = r.dump(-1, ' ', false, gjson::error_handler_t::replace);
    return err ? KILN_ERR_INVALID_ARGUMENT : KILN_OK;
}

// Each entry's text is kept with a fingerprint of what it shows (FNV-1a) and made again only when that changed: mods and tools ask for the
// listing often, and making all of it costs a fraction of a millisecond per definition, under the store lock.
std::string clones_json() {
    std::lock_guard l1(store_mutex());  // before g_mu, as everywhere: owner_label below takes it
    std::lock_guard lock(g_mu);
    std::string out = "[";
    for (Def& d : g_defs) {
        uint64_t h = 1469598103934665603ull;
        const auto add = [&](const void* p, size_t n) {
            for (size_t i = 0; i < n; ++i) h = (h ^ static_cast<const uint8_t*>(p)[i]) * 1099511628211ull;
        };
        const auto add_s = [&](const std::string& t) {
            const size_t n = t.size();
            add(&n, sizeof n);
            add(t.data(), n);
        };
        const size_t worlds = d.worlds.size(), nw = d.warnings.size();
        add(&d.version, sizeof d.version), add(&d.orphan, 1), add(&d.placeholder, 1), add(&worlds, sizeof worlds), add(&d.data, sizeof d.data);
        add_s(d.state), add_s(d.problem), add_s(owner_label(d.owner)), add_s(d.mod), add_s(d.source);
        add(&nw, sizeof nw);
        for (const std::string& w : d.warnings) add_s(w);
        if (h != d.listed_as || d.listed.empty()) {
            d.listed = listing(d).dump(-1, ' ', false, gjson::error_handler_t::replace);
            d.listed_as = h;
        }
        if (out.size() > 1) out += ',';
        out += d.listed;
    }
    out += ']';
    return out;
}

gjson clones_status() {
    std::lock_guard lock(g_mu);
    gjson b = {{"active", BM.ok && BM.header != 0}, {"gameBuffs", BM.n}, {"clones", BM.used}, {"room", kBuffRoom},
               {"worlds", BM.worlds.size()}, {"syncs", BM.syncs}, {"wordsFollowed", BM.words}};
    if (!BM.why.empty()) b["why"] = BM.why;
    return gjson{{"buffCopy", b}, {"perkMaps", g_perk_maps.size()}, {"texts", !g_texts_absent.load()},
                 {"gameBuffLookup", g_get_buff != nullptr}};
}

// Every definition, for the content sync (the section's "add" list on the client).
gjson clones_export(uint32_t section) {
    std::lock_guard lock(g_mu);
    gjson a = gjson::array();
    for (const Def& d : g_defs)
        if (d.section == section && d.state == "registered") a.push_back(d.body);
    return a;
}

void clones_packs_begin() {
    std::lock_guard lock(g_mu);
    g_seen_pack.clear();
}

void clones_packs_end() {
    std::lock_guard l1(store_mutex());  // before g_mu, as everywhere: owner_label below takes it
    std::lock_guard lock(g_mu);
    for (Def& d : g_defs)
        if (owner_label(d.owner).rfind("pack ", 0) == 0) d.orphan = !g_seen_pack.count(guid_text(d.guid));
}

void gd_pack_add(KilnOwner owner, uint32_t section, const gjson& list, const std::string& source, ApplyResult* out) {
    if (!list.is_array()) {
        out->errors.push_back(std::string(section_name(section)) + ".add: a list of definitions");
        return;
    }
    for (const gjson& d : list) {
        std::string rep;
        if (clones_define(owner, section, d.dump(), true, source, &rep) != KILN_OK) {
            const gjson r = gjson::parse(rep, nullptr, false);
            out->errors.push_back(std::string(section_name(section)) + ".add " + d.value("name", "?") + ": " +
                                  (r.is_object() ? r.value("error", "refused") : "refused"));
        } else {
            ++out->applied;
            const gjson r = gjson::parse(rep, nullptr, false);
            if (r.is_object() && r.contains("warnings"))
                for (const gjson& w : r["warnings"]) out->warnings.push_back(w.is_string() ? w.get<std::string>() : w.dump());
        }
    }
}

}  // namespace kc::gd

namespace kc {

// A template clone kiln.gamedata defines (code, a data pack's "add" list, or a server's content state) and has not
// refused: an item may name it before the world registers it, since a world registers every clone before anything can
// place one. (Kitbash otherwise accepts only templates already loaded.)
bool gd_template_defined(const uint8_t guid[16]) {
    std::lock_guard lock(gd::g_mu);
    for (const gd::Def& d : gd::g_defs)
        if (d.section == KILN_GD_TEMPLATES && !d.orphan && d.state != "error" && std::memcmp(d.guid, guid, 16) == 0) return true;
    return false;
}

uint64_t gd_template_generation() { return gd::g_template_gen.load(std::memory_order_relaxed); }

}  // namespace kc
