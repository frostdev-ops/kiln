// SPDX-License-Identifier: MIT
// Boot-time resources for mods' quests and NPCs in the game's UI (docs/kiln/services/npcs.md): reads every enabled mod's
// quests/*.json, compiles what goes to the game (questpack.cpp) and registers the rebuilt GameKnowledgeResource,
// JournalRegistryResource, NpcCollection and templates with kiln.assets, before the game reads them.
//
// When: right after the game's resource service is up (a post-hook on ResourceInit: nothing can have read a resource
// yet; on a client whose service was up before Kiln loaded, in on_ready). Every reader in the game goes through the
// resource service's slots (key -> handle -> data), which kiln.assets answers: the registries keep the handle and ask
// for the data when their world loads (docs/re/quests.md, "R4").
#include <windows.h>

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <sstream>
#include <unordered_map>

#include "knowledge_internal.h"
#include "questpack.h"
#include "safetyhook.hpp"
#include "services/kiln_assets.h"
#include "services/kiln_content.h"
#include "services/kiln_profile.h"

namespace fs = std::filesystem;

namespace kiln::knowledge {

namespace {

std::mutex g_mu;
bool g_built = false;
std::string g_manifest = "{\"mods\":[],\"errors\":[]}";
std::unordered_map<std::string, uint32_t> g_names;  // "<mod>:<name>" (lower case) -> knowledge id
safetyhook::InlineHook g_init_hook;
const KilnAssetsApi* A = nullptr;

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}
std::string read_file(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    std::stringstream ss;
    ss << f.rdbuf();
    return ss.str();
}
std::string path_of(int32_t which) {
    char buf[1024];
    size_t n = 0;
    if (g_api->path(which, buf, sizeof buf, &n) != KILN_OK) return {};
    return std::string(buf, n);
}

bool res_read(const qb::Guid& g, uint32_t type, const uint8_t** p, size_t* n) {
    KilnAssetKey k{};
    std::memcpy(k.guid, g.b, 16);
    k.type = type;
    const void* data = nullptr;
    size_t len = 0;
    if (A->resource_data(&k, &data, &len) != KILN_OK || !data) return false;
    *p = static_cast<const uint8_t*>(data);
    *n = len;
    return true;
}

// Template names -> GUIDs, built once when a name is first asked for (every TemplateResource's name field).
std::unordered_map<std::string, qb::Guid> g_template_names;
bool template_by_name(const std::string& name, qb::Guid* out) {
    if (g_template_names.empty()) {
        size_t count = 0;
        A->resource_list(qb::kTypeTemplate, nullptr, 0, &count);
        std::vector<KilnAssetKey> keys(count);
        if (count && A->resource_list(qb::kTypeTemplate, keys.data(), keys.size(), &count) == KILN_OK) {
            for (const KilnAssetKey& k : keys) {
                const void* d = nullptr;
                size_t l = 0;
                if (A->resource_data(&k, &d, &l) != KILN_OK || !d) continue;
                qb::Guid g;
                std::memcpy(g.b, k.guid, 16);
                g_template_names.emplace(qb::template_name(static_cast<const uint8_t*>(d), l), g);
            }
        }
    }
    auto it = g_template_names.find(name);
    if (it == g_template_names.end()) return false;
    *out = it->second;
    return true;
}

// Every enabled mod of the load order with a quests/ folder, as kiln.assets finds its assets.json.
void collect_mods(std::vector<qb::ModInput>* mods, bool client) {
    auto one = [&](const std::string& dir) {
        std::error_code ec;
        const fs::path base(widen(dir));
        const fs::path qdir = base / L"quests";
        const qb::ojson mf = qb::ojson::parse(read_file(base / L"kiln.mod.json"), nullptr, false, true);
        if (mf.is_discarded() || !mf.is_object() || !mf.contains("id")) {
            if (fs::is_directory(qdir, ec)) KW("native: %s has quests/ but no readable kiln.mod.json: skipped", dir.c_str());
            return;
        }
        // what the mod defined in code at an earlier start (Kiln.Quests/Native/NativeExport.cs)
        const fs::path code = fs::path(widen(path_of(KILN_PATH_DATA))) / L"kiln-quests" / L"native" / widen(mf["id"].get<std::string>() + ".json");
        // the NPC blocks it defined (Kiln.Blocks, Npc/NpcCompiler.cs), kept beside it so that a redeploy of the mod keeps them; the mod's own files win
        const fs::path npc_blocks = fs::path(code).replace_filename(widen(mf["id"].get<std::string>() + ".blocks.json"));
        if (!fs::is_directory(qdir, ec) && !fs::exists(code, ec) && !fs::exists(npc_blocks, ec)) return;
        const std::string side = mf.value("side", "both");
        if ((side == "server" && client) || (side == "client" && !client)) return;
        qb::ModInput m;
        m.id = mf["id"].get<std::string>();
        for (const auto& existing : *mods)
            if (existing.id == m.id) return;
        std::vector<fs::path> files, generated;
        for (auto it = fs::directory_iterator(qdir, ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->path().extension() == L".json") files.push_back(it->path());
        for (auto it = fs::directory_iterator(qdir / L"generated", ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->path().extension() == L".json") generated.push_back(it->path());
        std::sort(files.begin(), files.end());
        std::sort(generated.begin(), generated.end());
        files.insert(files.end(), generated.begin(), generated.end());
        ec.clear();
        if (fs::exists(npc_blocks, ec)) files.push_back(npc_blocks);
        if (fs::exists(code, ec)) files.push_back(code);
        for (const fs::path& f : files) {
            qb::ojson doc = qb::ojson::parse(read_file(f), nullptr, false, true);
            if (doc.is_discarded() || !doc.is_object()) {
                KW("native: %s/%s is not valid JSON: skipped", m.id.c_str(), f.filename().string().c_str());
                continue;
            }
            m.documents.push_back(std::move(doc));
            m.files.push_back(f.filename().string());
        }
        const qb::ojson audio = qb::ojson::parse(read_file(base / L"assets" / L"cooked" / L"audio.json"), nullptr, false, true);
        if (!audio.is_discarded()) m.audio = audio;
        const qb::ojson cook = qb::ojson::parse(read_file(base / L"assets" / L"cook.json"), nullptr, false, true);
        if (!cook.is_discarded() && cook.is_object()) m.guid_namespace = cook.value("guidNamespace", "");
        mods->push_back(std::move(m));
    };
    const auto* prof = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{"kiln.profile", 12}, 1));
    bool scan_dropins = true;
    if (prof && prof->header.size >= sizeof(KilnProfileApi)) {
        const uint32_t n = prof->count(KILN_PROFILE_KIND_KILN);
        for (uint32_t i = 0; i < n; ++i) {
            KilnProfileMod m{sizeof m};
            if (prof->get(KILN_PROFILE_KIND_KILN, i, &m) == KILN_OK && m.enabled) one(std::string(m.path.ptr, m.path.len));
        }
        if (prof->mode() == KILN_PROFILE_ACTIVE || prof->mode() == KILN_PROFILE_ONCE)
            scan_dropins = (prof->dropins() & (1u << KILN_PROFILE_KIND_KILN)) != 0;
    }
    if (scan_dropins) {
        const std::string root = path_of(KILN_PATH_MODS);
        std::error_code ec;
        std::vector<fs::path> dirs;
        for (auto it = fs::directory_iterator(widen(root), ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->is_directory(ec) && !fs::exists(it->path() / L".disabled", ec)) dirs.push_back(it->path());
        std::sort(dirs.begin(), dirs.end());
        for (const fs::path& d : dirs) {
            const std::u8string u = d.u8string();
            one(std::string(u.begin(), u.end()));
        }
    }
}

void put_resource(const char* what, const qb::Guid& g, uint32_t type, const qb::Bytes& data, uint32_t flags) {
    if (data.empty()) return;
    KilnAssetKey k{};
    std::memcpy(k.guid, g.b, 16);
    k.type = type;
    const KilnResult r = A->resource_put(g_owner, &k, data.data(), data.size(), flags);
    if (r != KILN_OK) KW("native: %s (%s, %zu bytes): kiln.assets refused it (%d)", what, qb::guid_text(g).c_str(), data.size(), r);
}

void build_locked() {
    if (g_built) return;
    g_built = true;
    KilnBuildInfo bi{sizeof bi};
    g_api->build_info(&bi);
    const bool client = bi.side == KILN_SIDE_CLIENT;
    std::vector<qb::ModInput> mods;
    collect_mods(&mods, client);
    if (mods.empty()) return;
    qb::GameData game;
    game.read = &res_read;
    game.template_by_name = &template_by_name;
    qb::PackOut p;
    try {
        p = qb::compile_pack(mods, game);
    } catch (const std::exception& e) {
        KW("native: the build failed: %s", e.what());
        return;
    }
    for (const std::string& e : p.errors) KW("native: %s", e.c_str());
    for (const std::string& w : p.warnings) KW("native: %s", w.c_str());
    g_manifest = p.manifest.dump();
    g_template_names.clear();
    if (!p.any) return;
    qb::Guid gk, gj, gn;
    qb::parse_guid(qb::kGuidKnowledge, &gk);
    qb::parse_guid(qb::kGuidJournal, &gj);
    qb::parse_guid(qb::kGuidNpcs, &gn);
    put_resource("GameKnowledgeResource", gk, qb::kTypeKnowledge, p.res_knowledge, KILN_ASSET_PUT_OVERRIDE);
    put_resource("JournalRegistryResource", gj, qb::kTypeJournal, p.res_journal, KILN_ASSET_PUT_OVERRIDE);
    put_resource("NpcCollection", gn, qb::kTypeNpcs, p.res_npcs, KILN_ASSET_PUT_OVERRIDE);
    for (const qb::TemplateOut& t : p.templates)
        put_resource(t.name.c_str(), t.guid, qb::kTypeTemplate, t.data, t.is_new ? KILN_ASSET_PUT_NEW : KILN_ASSET_PUT_OVERRIDE);
    // names for Knowledge.Resolve: "<mod>:<name>"
    for (const auto& m : p.manifest["mods"]) {
        if (!m.value("ok", false)) continue;
        const std::string mod = m.value("id", "");
        for (const auto& [name, idv] : m["knowledge"].items()) {
            std::string key = mod + ":" + name;
            std::transform(key.begin(), key.end(), key.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            g_names[key] = static_cast<uint32_t>(std::stoul(idv.get<std::string>(), nullptr, 16));
        }
    }
    // texts: answered by kiln.content's loca hooks (client only: the server never shows text)
    int texts = 0;
    if (client) {
        using SetTexts = void (*)(uint32_t, const char*, size_t);
        HMODULE h = GetModuleHandleW(L"kiln-content.dll");
        auto set = h ? reinterpret_cast<SetTexts>(GetProcAddress(h, "kc_nc_set_texts")) : nullptr;
        if (set) {
            for (const qb::TextOut& t : p.texts) {
                set(t.tag, t.json.data(), t.json.size());
                ++texts;
            }
        } else {
            KW("native: kiln.content has no kc_nc_set_texts: native quests and dialogue show no texts");
        }
    }
    // items and recipes a mod defines inline (summon tools, vendor stock)
    if (!p.content_items.empty() || !p.content_recipes.empty()) {
        const auto* C = static_cast<const KilnContentApi*>(g_api->service(KilnStr{"kiln.content", 12}, 4));
        if (!C) {
            KW("native: kiln.content v4 is not available: summon items and vendor recipes are not defined");
        } else {
            auto define = [&](uint32_t kind, const std::string& json) {
                char report[512] = {};
                size_t n = 0;
                const KilnResult r = C->define(g_owner, kind, KilnStr{json.data(), json.size()}, report, sizeof report, &n);
                if (r != KILN_OK) KW("native: kiln.content refused a definition: %s", report);
            };
            for (const std::string& j : p.content_items) define(KILN_CONTENT_ITEMS, j);
            for (const std::string& j : p.content_recipes) define(KILN_CONTENT_RECIPES, j);
        }
    }
    KI("native: %zu mod(s): %zu new knowledge object(s), %zu quest(s), %zu NPC(s) in the roster, %zu template(s), %d text(s); "
       "GameKnowledgeResource %zu bytes served by kiln.assets",
       mods.size(), p.knowledge.size(), p.quests.size(), p.npcs.size(), p.templates.size(), texts, p.res_knowledge.size());
}

void build_once() {
    std::lock_guard lock(g_mu);
    if (!A) A = static_cast<const KilnAssetsApi*>(g_api->service(kstr(KILN_ASSETS_SERVICE), 1));
    if (!A || A->header.size < sizeof(KilnAssetsApi) || !A->resource_data || !A->resource_put) {
        if (!g_built) KW("native: kiln.assets is not available: mods' native quests and NPCs are off");
        g_built = true;
        return;
    }
    build_locked();
}

char __fastcall hk_resource_init(uintptr_t S, void* cfg) {
    const char ok = g_init_hook.call<char>(S, cfg);
    if (ok) build_once();
    return ok;
}

}  // namespace

void native_pack_install() {
    uintptr_t a = 0;
    if (g_api->symbol(kstr("assets/ResourceInit"), &a) != KILN_OK || !a) {
        KW("native: assets/ResourceInit is not resolved: mods' native quests and NPCs are off");
        return;
    }
    auto h = safetyhook::InlineHook::create(reinterpret_cast<void*>(a), reinterpret_cast<void*>(&hk_resource_init));
    if (!h) {
        KW("native: hook on ResourceInit failed: mods' native quests and NPCs are off");
        return;
    }
    g_init_hook = std::move(*h);
}

void native_pack_ready() {
    // A client whose resource service was up before Kiln loaded: the hook will not fire.
    std::lock_guard lock(g_mu);
    if (g_built) return;
    if (!A) A = static_cast<const KilnAssetsApi*>(g_api->service(kstr(KILN_ASSETS_SERVICE), 1));
    if (!A || A->header.size < sizeof(KilnAssetsApi) || !A->resource_data) return;
    qb::Guid gk;
    qb::parse_guid(qb::kGuidKnowledge, &gk);
    const uint8_t* p = nullptr;
    size_t n = 0;
    if (res_read(gk, qb::kTypeKnowledge, &p, &n)) build_locked();
}

std::string native_pack_manifest() {
    std::lock_guard lock(g_mu);
    return g_manifest;
}

bool native_pack_resolve(const std::string& lower_name, uint32_t* id) {
    std::lock_guard lock(g_mu);
    auto it = g_names.find(lower_name);
    if (it == g_names.end()) return false;
    *id = it->second;
    return true;
}

}  // namespace kiln::knowledge
