// SPDX-License-Identifier: MIT
#include "catalog.h"

#include <algorithm>
#include <cctype>
#include <map>
#include <mutex>
#include <set>

#include "services/kiln_spawn.h"

namespace kspawn::catalog {
namespace {

struct Shipped {
    const char* guid;
    const char* name;
    const char* display;
    uint32_t kind;
};
const Shipped kShipped[] = {
#include "catalog_data.inc"
};

std::mutex g_mu;
std::vector<Entry> g_sorted;                 // by lower-case name, then guid
std::map<Guid, size_t> g_by_guid;            // -> index in g_sorted
std::set<Guid> g_engine;
bool g_engine_loaded = false;

std::string lower(std::string_view s) {
    std::string r(s);
    for (char& c : r) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return r;
}

bool starts(const std::string& s, const char* p) { return s.rfind(p, 0) == 0; }

void rebuild(std::vector<Entry>&& list) {
    std::sort(list.begin(), list.end(), [](const Entry& a, const Entry& b) {
        const std::string la = lower(a.name), lb = lower(b.name);
        return la != lb ? la < lb : a.guid < b.guid;
    });
    g_sorted = std::move(list);
    g_by_guid.clear();
    for (size_t i = 0; i < g_sorted.size(); ++i) g_by_guid[g_sorted[i].guid] = i;
}

}  // namespace

uint32_t kind_of(std::string_view template_name) {  // same rules as gen_catalog.py
    const std::string n = lower(template_name);
    if (n.find("lootpickup") != std::string::npos || starts(n, "pickup")) return KILN_TEMPLATE_PICKUP;
    if (n.find("townsfolk") != std::string::npos || starts(n, "npc_") || n.find("villager") != std::string::npos)
        return KILN_TEMPLATE_NPC;
    for (const char* p : {"enemy_", "base_enemy", "animal_", "base_animal", "fakeplayer_", "player_companion", "boss_",
                          "midboss_", "enemy"})
        if (starts(n, p)) return KILN_TEMPLATE_CREATURE;
    for (const char* p : {"prop_", "base_prop", "deco_"})
        if (starts(n, p)) return KILN_TEMPLATE_PROP;
    return n.empty() ? KILN_TEMPLATE_UNKNOWN : KILN_TEMPLATE_OTHER;
}

void init() {
    std::vector<Entry> list;
    for (const Shipped& s : kShipped) {
        Entry e;
        if (!Guid::parse(s.guid, &e.guid)) continue;
        e.name = s.name;
        e.display = s.display;
        e.kind = s.kind;
        e.source = 1;
        list.push_back(std::move(e));
    }
    std::lock_guard lock(g_mu);
    rebuild(std::move(list));
}

size_t merge_engine(const std::vector<TemplateRow>& rows) {
    std::lock_guard lock(g_mu);
    std::vector<Entry> list = g_sorted;
    std::map<Guid, size_t> idx;
    for (size_t i = 0; i < list.size(); ++i) idx[list[i].guid] = i;
    for (const TemplateRow& r : rows) {
        g_engine.insert(r.guid);
        const uint32_t flags = r.flags_known ? KILN_TEMPLATE_FLAG_KNOWN | (r.do_not_save ? KILN_TEMPLATE_FLAG_DO_NOT_SAVE : 0) |
                                                   (r.lifetime ? KILN_TEMPLATE_FLAG_LIFETIME : 0) |
                                                   (r.transform ? KILN_TEMPLATE_FLAG_TRANSFORM : 0)
                                             : 0;
        auto it = idx.find(r.guid);
        if (it != idx.end()) {
            Entry& e = list[it->second];
            e.source |= 2;
            e.flags = flags;
            if (!r.name.empty()) e.name = r.name;  // the engine's own name wins
            continue;
        }
        Entry e;
        e.guid = r.guid;
        e.name = r.name;
        e.kind = kind_of(r.name);
        e.source = 2;
        e.flags = flags;
        idx[e.guid] = list.size();
        list.push_back(std::move(e));
    }
    g_engine_loaded = g_engine_loaded || !rows.empty();
    rebuild(std::move(list));
    return g_engine.size();
}

std::vector<Guid> match_names(std::string_view lower_substring) {
    std::vector<Guid> out;
    std::lock_guard lock(g_mu);
    for (const Entry& e : g_sorted)
        if (lower(e.name).find(lower_substring) != std::string::npos) out.push_back(e.guid);
    return out;
}

bool engine_loaded() {
    std::lock_guard lock(g_mu);
    return g_engine_loaded;
}

bool engine_has(const Guid& g) {
    std::lock_guard lock(g_mu);
    return g_engine.count(g) != 0;
}

bool find(std::string_view name, Entry* out) {
    if (name.empty()) return false;
    std::lock_guard lock(g_mu);
    for (const Entry& e : g_sorted)
        if (e.name == name) return *out = e, true;
    const std::string ln = lower(name);
    for (const Entry& e : g_sorted)
        if (lower(e.name) == ln) return *out = e, true;
    for (const Entry& e : g_sorted)
        if (!e.display.empty() && lower(e.display) == ln) return *out = e, true;
    Guid g;
    if (Guid::parse(name, &g)) {
        auto it = g_by_guid.find(g);
        if (it != g_by_guid.end()) return *out = g_sorted[it->second], true;
    }
    return false;
}

bool get(const Guid& g, Entry* out) {
    std::lock_guard lock(g_mu);
    auto it = g_by_guid.find(g);
    if (it == g_by_guid.end()) return false;
    *out = g_sorted[it->second];
    return true;
}

size_t count() {
    std::lock_guard lock(g_mu);
    return g_sorted.size();
}

bool at(size_t index, Entry* out) {
    std::lock_guard lock(g_mu);
    if (index >= g_sorted.size()) return false;
    *out = g_sorted[index];
    return true;
}

}  // namespace kspawn::catalog
