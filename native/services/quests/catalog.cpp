// SPDX-License-Identifier: MIT
// kiln.knowledge's catalog: names for knowledge ids and queries, the NPC roster and the journal, read from the game's
// own data at the world's first step (tick thread):
//  - NpcCollection through the NpcRegistry singleton (the array npc_unlock_state_server walks; its unlock bit i is
//    NPC i): "npc:<debugName>[.unlock]" -> the NPC's unlockRequirement.
//  - JournalRegistryResource through g38_journal::JournalRegistry (the quest array trigger_quest_rewards walks):
//    "quest:0x<entryId>" -> the quest's "rewarded" player knowledge, ".step<n>.visible|.done" -> the step's queries.
//  - GameKnowledgeQueryResourceDb through kiln.assets (the resource by GUID): "query:<name>" -> the named query.
//  - "line:<template>/<init|option|transition|farewell><k>" through kiln.gamedata's template reflection, on demand.
// Texts (quest names, dialogue) are loca tags: client data, not on a dedicated server.
#include <algorithm>
#include <bit>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "knowledge_internal.h"
#include "nlohmann/json.hpp"
#include "services/kiln_assets.h"
#include "services/kiln_gamedata.h"

namespace kiln::knowledge {
namespace {

std::mutex g_cat_mu;
bool g_ready = false;
int g_attempts = 0;
std::vector<CatNpc> g_npcs;
std::vector<CatQuest> g_quests;
std::vector<CatQuery> g_queries;
std::unordered_map<uint32_t, size_t> g_query_by_id;
std::unordered_map<std::string, KilnKnowledgeName> g_names;  // lower-case name -> meaning
std::unordered_map<uint32_t, std::string> g_know_names, g_query_names;
std::unordered_map<std::string, uint32_t> g_line_cache;     // lower-case "line:..." -> id (0: not found)
uintptr_t g_npcreg = 0;
int64_t o_npcs = -1, o_npc_version = -1, o_npc_state = -1, o_journal = -1;

// GameKnowledgeQueryResourceDb aacd27f0-25ad-42b6-9c8d-91dca8fcafd8 (keen::GameKnowledgeQueryResourceDb 0xe613a80a)
constexpr uint8_t kQueryDbGuid[16] = {0xf0, 0x27, 0xcd, 0xaa, 0xad, 0x25, 0xb6, 0x42,
                                      0x9c, 0x8d, 0x91, 0xdc, 0xa8, 0xfc, 0xaf, 0xd8};
constexpr uint32_t kQueryDbType = 0xe613a80au;

std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
std::string hex8(uint32_t v) {
    char b[16];
    std::snprintf(b, sizeof b, "0x%08x", v);
    return b;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(kstr(name), out) == KILN_OK; }

bool read_string(uintptr_t field, std::string* out) {
    uintptr_t base = 0;
    uint32_t n = 0;
    if (!blob(field, &base, &n) || n > 4096) return false;
    out->assign(n, '\0');
    return n == 0 || (base && copy_guarded(out->data(), reinterpret_cast<const void*>(base), n));
}
KilnKnowledgeQuery read_query(uintptr_t at) {
    KilnKnowledgeQuery q{};
    if (copy_guarded(&q, reinterpret_cast<const void*>(at), 11)) q.reserved = 0;
    else q = KilnKnowledgeQuery{};
    return q;
}
KilnKnowledgeName make_name(uint32_t cat, uint32_t index, const KilnKnowledgeQuery& q) {
    KilnKnowledgeName n{};
    n.size = sizeof n;
    n.kind = q.type == 0 ? KILN_KNOW_NAME_QUERY : KILN_KNOW_NAME_KNOWLEDGE;
    n.id = q.id;
    n.catalog = cat;
    n.index = index;
    n.query = q;
    return n;
}
void add_name(const std::string& name, const KilnKnowledgeName& n) {
    g_names.emplace(lower(name), n);
    auto& rev = n.kind == KILN_KNOW_NAME_QUERY ? g_query_names : g_know_names;
    rev.emplace(n.id, name);
}

void build_npcs(uintptr_t npcreg) {
    uintptr_t field = 0, base = 0;
    uint32_t n = 0;
    if (!npcreg || o_npcs < 0 || !rd(npcreg + o_npcs, &field) || !field || !blob(field, &base, &n) || !base || n > 4096)
        return;
    for (uint32_t i = 0; i < n; ++i) {
        const uintptr_t e = base + i * 136u;
        CatNpc c;
        c.npc.size = sizeof c.npc;
        uint8_t raw[0x28] = {};
        if (!copy_guarded(raw, reinterpret_cast<const void*>(e), sizeof raw)) break;
        std::memcpy(&c.npc.npc_id, raw + 0, 4);
        std::memcpy(&c.npc.name, raw + 4, 4);
        c.npc.crafting_assistant = raw[8];
        c.npc.unlock = read_query(e + 0x10);
        std::memcpy(&c.npc.workshop, raw + 0x1c, 4);
        std::memcpy(&c.npc.item, raw + 0x24, 4);
        c.npc.unlocked = 0xff;
        read_string(e + 0x80, &c.name);
        if (c.name.empty()) c.name = hex8(c.npc.npc_id);
        if (c.npc.unlock.id) {
            const KilnKnowledgeName kn = make_name(KILN_KNOW_CAT_NPCS, i, c.npc.unlock);
            add_name("npc:" + c.name + ".unlock", kn);
            g_names.emplace(lower("npc:" + c.name), kn);
        }
        g_npcs.push_back(std::move(c));
    }
    g_npcreg = npcreg;
}

void build_journal(uintptr_t journal) {
    uintptr_t field = 0, base = 0;
    uint32_t n = 0;
    if (!journal || o_journal < 0 || !rd(journal + o_journal, &field) || !field || !blob(field, &base, &n) || !base ||
        n > 4096)
        return;
    for (uint32_t i = 0; i < n; ++i) {
        const uintptr_t e = base + i * 44u;
        uint8_t raw[44] = {};
        if (!copy_guarded(raw, reinterpret_cast<const void*>(e), sizeof raw)) break;
        CatQuest q;
        KilnJournalQuest& j = q.quest;
        j.size = sizeof j;
        std::memcpy(&j.entry_id, raw + 0, 4);
        std::memcpy(&j.name, raw + 8, 4);
        std::memcpy(&j.priority, raw + 0x10, 4);
        j.tutorial = raw[0x14];
        j.source = raw[0x20];
        j.type = raw[0x21];
        j.main = raw[0x22];
        j.all_players = raw[0x23];
        uintptr_t eb = 0, rb = 0;
        uint32_t en = 0, rn = 0;
        if (blob(e + 0x18, &eb, &en) && eb && en < 256) {
            for (uint32_t k = 0; k < en; ++k) {
                const uintptr_t s = eb + k * 72u;
                uint8_t sr[72] = {};
                if (!copy_guarded(sr, reinterpret_cast<const void*>(s), sizeof sr)) break;
                KilnJournalStep st{};
                st.size = sizeof st;
                std::memcpy(&st.entry_id, sr + 0, 4);
                std::memcpy(&st.name, sr + 4, 4);
                std::memcpy(&st.text, sr + 8, 4);
                std::memcpy(&st.map_marker, sr + 0xc, 4);
                st.visible = read_query(s + 0x10);
                st.done = read_query(s + 0x1c);
                uintptr_t pb = 0;
                uint32_t pn = 0;
                if (blob(s + 0x28, &pb, &pn)) st.progress_steps = pn;
                std::memcpy(&st.item_icon, sr + 0x40, 4);
                st.recommended_level = sr[0x44];
                q.steps.push_back(st);
            }
        }
        j.steps = static_cast<uint32_t>(q.steps.size());
        if (blob(e + 0x24, &rb, &rn) && rb && rn < 256) {
            j.rewards = rn;
            for (uint32_t k = 0; k < rn && k < 4; ++k) {
                uint8_t rr[24] = {};
                if (!copy_guarded(rr, reinterpret_cast<const void*>(rb + k * 24u), sizeof rr)) break;
                std::memcpy(&j.reward_item[k], rr + 0x10, 4);
                std::memcpy(&j.reward_xp[k], rr + 0x14, 4);
            }
        }
        const std::string qn = "quest:" + hex8(j.entry_id);
        add_name(qn, make_name(KILN_KNOW_CAT_QUESTS, i,
                               KilnKnowledgeQuery{j.entry_id, 1, 2 /*EQUALS*/, 1 /*BOOL*/, 1 /*player*/, 0}));
        for (size_t k = 0; k < q.steps.size(); ++k) {
            const KilnJournalStep& st = q.steps[k];
            char suffix[48];
            if (st.visible.id) {
                std::snprintf(suffix, sizeof suffix, ".step%zu.visible", k + 1);
                const KilnKnowledgeName kn = make_name(KILN_KNOW_CAT_QUESTS, i, st.visible);
                g_names.emplace(lower(qn + suffix), kn);
                if (kn.kind == KILN_KNOW_NAME_KNOWLEDGE) g_know_names.emplace(kn.id, qn + suffix);
            }
            if (st.done.id) {
                std::snprintf(suffix, sizeof suffix, ".step%zu.done", k + 1);
                const KilnKnowledgeName kn = make_name(KILN_KNOW_CAT_QUESTS, i, st.done);
                g_names.emplace(lower(qn + suffix), kn);
                if (kn.kind == KILN_KNOW_NAME_KNOWLEDGE) g_know_names.emplace(kn.id, qn + suffix);
            }
        }
        g_quests.push_back(std::move(q));
    }
}

void build_queries() {
    const auto* A = static_cast<const KilnAssetsApi*>(g_api->service(kstr(KILN_ASSETS_SERVICE), 1));
    if (!A || A->header.size < sizeof(KilnAssetsApi) || !A->resource_data) {
        KW("catalog: kiln.assets (resource_data) is not available: no query names");
        return;
    }
    KilnAssetKey key{};
    std::memcpy(key.guid, kQueryDbGuid, 16);
    key.type = kQueryDbType;
    const void* p = nullptr;
    size_t len = 0;
    if (A->resource_data(&key, &p, &len) != KILN_OK || !p || len < 44) {
        KW("catalog: GameKnowledgeQueryResourceDb aacd27f0-... is not loaded: no query names");
        return;
    }
    const uintptr_t res = reinterpret_cast<uintptr_t>(p);
    uintptr_t base = 0;
    uint32_t n = 0;
    if (!blob(res + 0x10, &base, &n) || !base || n > 100000) return;
    for (uint32_t i = 0; i < n; ++i) {
        const uintptr_t e = base + i * 48u;
        uint8_t raw[48] = {};
        if (e + 48 > res + len || !copy_guarded(raw, reinterpret_cast<const void*>(e), sizeof raw)) break;
        CatQuery q;
        std::memcpy(&q.id, raw + 8, 4);
        std::memcpy(&q.index, raw + 0xc, 4);
        q.type = raw[0x10];
        q.player = raw[0x11];
        std::memcpy(&q.knowledge, raw + 0x20, 4);
        read_string(e, &q.name);
        if (!q.id) continue;
        g_query_by_id.emplace(q.id, g_queries.size());
        if (!q.name.empty()) {
            KilnKnowledgeName kn{};
            kn.size = sizeof kn;
            kn.kind = KILN_KNOW_NAME_QUERY;
            kn.id = q.id;
            kn.catalog = KILN_KNOW_CAT_QUERIES;
            kn.index = static_cast<uint32_t>(g_queries.size());
            kn.query = KilnKnowledgeQuery{q.id, 0, 2 /*EQUALS*/, 0 /*EXTERN*/, 0, 0};
            add_name("query:" + q.name, kn);
        }
        g_queries.push_back(std::move(q));
    }
}

}  // namespace

void catalog_build(uintptr_t npcreg, uintptr_t journal) {
    std::lock_guard lock(g_cat_mu);
    if (g_ready) return;
    if (o_npcs < 0) {
        cst("quests/npcreg.npcs", &o_npcs);
        cst("quests/npcreg.version", &o_npc_version);
        cst("quests/npcreg.state", &o_npc_state);
        cst("quests/journal.quests", &o_journal);
    }
    g_npcs.clear();
    g_quests.clear();
    g_queries.clear();
    g_query_by_id.clear();
    g_names.clear();
    g_know_names.clear();
    g_query_names.clear();
    build_npcs(npcreg);
    build_journal(journal);
    build_queries();
    ++g_attempts;
    // ready when every source answered, or after a few tries with what there is (the server runs on either way)
    const bool complete = !g_npcs.empty() && !g_quests.empty() && !g_queries.empty();
    if (!complete && g_attempts < 4) return;
    g_ready = true;
    size_t unlocks = 0, steps = 0;
    for (const CatNpc& n : g_npcs) unlocks += n.npc.unlock.id != 0;
    for (const CatQuest& q : g_quests) steps += q.steps.size();
    uint64_t mask = 0;
    uint16_t ver = 0;
    int unlocked = -1;
    if (npc_unlock_mask(&mask, &ver)) unlocked = std::popcount(mask & (g_npcs.size() >= 64 ? ~0ull : ((1ull << g_npcs.size()) - 1)));
    KI("catalog: %zu NPCs (%zu with an unlock requirement; NpcRegistry: %d unlocked, version %u), %zu quests (%zu steps), "
       "%zu named queries, %zu names",
       g_npcs.size(), unlocks, unlocked, ver, g_quests.size(), steps, g_queries.size(), g_names.size());
}

void catalog_clear() {
    std::lock_guard lock(g_cat_mu);
    g_ready = false;
    g_attempts = 0;
    g_npcs.clear();
    g_quests.clear();
    g_queries.clear();
    g_query_by_id.clear();
    g_names.clear();
    g_know_names.clear();
    g_query_names.clear();
    g_line_cache.clear();
    g_npcreg = 0;
}

bool catalog_ready() {
    std::lock_guard lock(g_cat_mu);
    return g_ready;
}

uint32_t catalog_count(uint32_t cat) {
    std::lock_guard lock(g_cat_mu);
    switch (cat) {
    case KILN_KNOW_CAT_NPCS: return static_cast<uint32_t>(g_npcs.size());
    case KILN_KNOW_CAT_QUESTS: return static_cast<uint32_t>(g_quests.size());
    case KILN_KNOW_CAT_QUERIES: return static_cast<uint32_t>(g_queries.size());
    default: return 0;
    }
}

// The vectors are only rebuilt by catalog_build / catalog_clear on the tick thread; callers are on the tick thread.
const CatNpc* catalog_npc(uint32_t index) {
    std::lock_guard lock(g_cat_mu);
    return index < g_npcs.size() ? &g_npcs[index] : nullptr;
}
const CatQuest* catalog_quest(uint32_t index) {
    std::lock_guard lock(g_cat_mu);
    return index < g_quests.size() ? &g_quests[index] : nullptr;
}
const CatQuery* catalog_query_by_id(uint32_t id) {
    std::lock_guard lock(g_cat_mu);
    auto it = g_query_by_id.find(id);
    return it == g_query_by_id.end() ? nullptr : &g_queries[it->second];
}
const CatQuery* catalog_query_at(uint32_t index) {
    std::lock_guard lock(g_cat_mu);
    return index < g_queries.size() ? &g_queries[index] : nullptr;
}

bool catalog_resolve(const std::string& lower_name, KilnKnowledgeName* out) {
    std::lock_guard lock(g_cat_mu);
    auto it = g_names.find(lower_name);
    if (it == g_names.end()) return false;
    *out = it->second;
    return true;
}

std::string catalog_name_of(uint32_t id, uint32_t kind) {
    std::lock_guard lock(g_cat_mu);
    const auto& m = kind == KILN_KNOW_NAME_QUERY ? g_query_names : g_know_names;
    auto it = m.find(id);
    return it == m.end() ? std::string() : it->second;
}

std::string catalog_entry_name(uint32_t cat, uint32_t index) {
    std::lock_guard lock(g_cat_mu);
    switch (cat) {
    case KILN_KNOW_CAT_NPCS: return index < g_npcs.size() ? "npc:" + g_npcs[index].name : std::string();
    case KILN_KNOW_CAT_QUESTS: return index < g_quests.size() ? "quest:" + hex8(g_quests[index].quest.entry_id) : std::string();
    case KILN_KNOW_CAT_QUERIES: return index < g_queries.size() ? "query:" + g_queries[index].name : std::string();
    default: return {};
    }
}

bool npc_unlock_mask(uint64_t* mask, uint16_t* version) {
    if (!g_npcreg || o_npc_state < 0 || o_npc_version < 0) return false;
    return rd(g_npcreg + o_npc_state + 8, mask) && rd(g_npcreg + o_npc_version, version);
}

// "line:<template name>/<init|option|transition|farewell><k>" (k from 0, in the template's NpcDialog order)
bool catalog_resolve_line(const std::string& name, uint32_t* out_id) {
    const std::string key = lower(name);
    {
        std::lock_guard lock(g_cat_mu);
        if (auto it = g_line_cache.find(key); it != g_line_cache.end()) {
            *out_id = it->second;
            return it->second != 0;
        }
    }
    const size_t slash = name.find('/', 5);
    if (slash == std::string::npos) return false;
    const std::string tmpl = name.substr(5, slash - 5);
    std::string rest = lower(name.substr(slash + 1));
    const char* list = nullptr;
    size_t pre = 0;
    for (auto [p, l] : {std::pair{"option", "dialogOptions"}, std::pair{"init", "initLines"},
                        std::pair{"transition", "transitionLines"}, std::pair{"farewell", "farewellLines"}})
        if (rest.starts_with(p)) {
            list = l;
            pre = std::strlen(p);
            break;
        }
    if (!list || pre >= rest.size()) return false;
    char* end = nullptr;
    const unsigned long k = std::strtoul(rest.c_str() + pre, &end, 10);
    if (!end || *end) return false;
    const auto* GD = static_cast<const KilnGamedataApi*>(g_api->service(kstr(KILN_GAMEDATA_SERVICE), 1));
    uint32_t id = 0;
    if (GD && GD->header.size >= sizeof(KilnGamedataApi)) {
        KilnGdEntry e{};
        e.size = sizeof e;
        if (GD->find(KILN_GD_TEMPLATES, KilnStr{tmpl.data(), tmpl.size()}, &e) == KILN_OK) {
            const std::string prefix = std::string("NpcDialog.dialogResource.") + list + "[" + std::to_string(k) + "]";
            std::string buf(1 << 16, '\0');
            size_t len = 0;
            KilnResult rc = GD->fields(KILN_GD_TEMPLATES, e.id, KilnStr{prefix.data(), prefix.size()}, 200, buf.data(),
                                       buf.size(), &len);
            if (rc == KILN_ERR_BUFFER_TOO_SMALL && len > buf.size() && len < (16u << 20)) {
                buf.assign(len + 1, '\0');
                rc = GD->fields(KILN_GD_TEMPLATES, e.id, KilnStr{prefix.data(), prefix.size()}, 200, buf.data(),
                                buf.size(), &len);
            }
            if (rc == KILN_OK) {
                try {
                    const auto j = nlohmann::json::parse(buf.substr(0, len));
                    for (const auto& f : j) {
                        const std::string path = f.value("path", "");
                        if (path.find(prefix + ".optionId") != 0) continue;
                        const auto& v = f["value"];
                        if (v.is_number_unsigned()) id = static_cast<uint32_t>(v.get<uint64_t>());
                        else if (v.is_number_integer()) id = static_cast<uint32_t>(v.get<int64_t>());
                        else if (v.is_string()) id = static_cast<uint32_t>(std::strtoull(v.get<std::string>().c_str(), nullptr, 0));
                        if (id) break;
                    }
                } catch (...) {
                    id = 0;
                }
            }
        }
    }
    std::lock_guard lock(g_cat_mu);
    g_line_cache[key] = id;
    *out_id = id;
    return id != 0;
}

// Where the registry's GameKnowledgeResource comes from: the resource service's resident copy of cb1eeb8b-... (what
// kiln.assets serves and overrides) or something else. Decides how mod knowledge objects can be added (docs).
std::string knowledge_resource_origin(uintptr_t res) {
    const auto* A = static_cast<const KilnAssetsApi*>(g_api->service(kstr(KILN_ASSETS_SERVICE), 1));
    if (!A || A->header.size < sizeof(KilnAssetsApi) || !A->resource_data) return "kiln.assets not available";
    // GameKnowledgeResource cb1eeb8b-f58a-4785-bf0f-6dd5437e9a5b (keen::GameKnowledgeResource 0xbc47511c)
    static constexpr uint8_t guid[16] = {0x8b, 0xeb, 0x1e, 0xcb, 0x8a, 0xf5, 0x85, 0x47,
                                         0xbf, 0x0f, 0x6d, 0xd5, 0x43, 0x7e, 0x9a, 0x5b};
    KilnAssetKey key{};
    std::memcpy(key.guid, guid, 16);
    key.type = 0xbc47511cu;
    const void* p = nullptr;
    size_t len = 0;
    if (A->resource_data(&key, &p, &len) != KILN_OK || !p) return "the resource service has no GameKnowledgeResource cb1eeb8b";
    char b[160];
    std::snprintf(b, sizeof b, "%s the resource service's copy of cb1eeb8b (%p, %zu bytes)",
                  reinterpret_cast<uintptr_t>(p) == res ? "is" : "is NOT", p, len);
    return b;
}

}  // namespace kiln::knowledge
