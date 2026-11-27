// SPDX-License-Identifier: MIT
// kiln.content registry views: kiln.registry.view bound to the game (view_service.h): the type list from kiln.types, the
// resources from kiln.assets, the members through the game's reflection (rbytes) under kiln.content's store lock, the
// registries through the stage's runner, the alias table from <game>/kiln/regmap/views.json. Registered by bind_service.
#include <windows.h>

#include <filesystem>
#include <fstream>
#include <mutex>
#include <sstream>

#include "../content.h"
#include "model_bytes.h"
#include "services/kiln_registry.h"
#include "services/kiln_types.h"
#include "stage.h"
#include "view_service.h"

namespace kc::registry::views {

namespace {

const KilnAssetsApi* assets() {
    const auto* a = g_api ? static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, 1)) : nullptr;
    return a && a->header.size >= offsetof(KilnAssetsApi, resource_data) + sizeof(void*) && a->resource_data && a->resource_list ? a : nullptr;
}
const KilnTypesApi* types() {
    const auto* t = g_api ? static_cast<const KilnTypesApi*>(g_api->service(KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1}, 1)) : nullptr;
    return t && t->type_count && t->type_at && t->type_info ? t : nullptr;
}
std::string kiln_dir() {
    char b[1024];
    size_t n = 0;
    if (!g_api || g_api->path(KILN_PATH_KILN, b, sizeof b, &n) != KILN_OK) return {};
    return std::string(b, n);
}
std::string text(KilnStr s) { return s.ptr ? std::string(s.ptr, s.len) : std::string(); }

bool list_keys(uint32_t type, std::vector<KilnAssetKey>* out) {
    out->clear();
    const KilnAssetsApi* a = assets();
    if (!a) return false;
    size_t n = 0;
    if (a->resource_list(type, nullptr, 0, &n) != KILN_OK && n == 0) return true;
    std::vector<KilnAssetKey> keys(n);
    size_t got = 0;
    if (n && a->resource_list(type, keys.data(), keys.size(), &got) != KILN_OK) return false;
    keys.resize(std::min(got, keys.size()));
    for (const KilnAssetKey& k : keys)
        if (k.part == 0) out->push_back(k);
    return true;
}

ServiceEnv make_env() {
    ServiceEnv e;
    e.ready = [](std::string* why) {
        const auto p = stage::runner().phase();
        if (p == stage::Phase::Start || p == stage::Phase::WorldLoad) return assets() != nullptr || (*why = "kiln.assets does not serve the game's resources here", false);
        return *why = "the game's resources are not loaded yet (views open once Kiln's registry stage has run)", false;
    };
    e.resource_types = [](std::string* why) {
        std::vector<std::pair<std::string, uint32_t>> out;
        const KilnTypesApi* t = types();
        const KilnAssetsApi* a = assets();
        if (!t || !a) return *why = "the game's type list is not available here (kiln.types)", out;
        const uint32_t n = t->type_count();
        for (uint32_t i = 0; i < n; ++i) {
            KilnTypeInfo info{};
            info.size = sizeof info;
            const KilnType* type = t->type_at(i);
            if (!type || t->type_info(type, &info) != KILN_OK || info.kind != KILN_TYPE_STRUCT) continue;
            size_t count = 0;
            a->resource_list(info.hash, nullptr, 0, &count);
            if (count) out.emplace_back(text(info.qualified_name), info.hash);
        }
        return out;
    };
    e.describe = &describe_type;
    e.list = &list_keys;
    e.member = [](const KilnAssetKey& key, const std::string& type, const std::string& path, json* out, std::string* why) {
        const KilnAssetsApi* a = assets();
        if (!a) return *why = "kiln.assets is not available", false;
        std::lock_guard lock(store_mutex());  // never a half-written slot of a pack application
        const void* p = nullptr;
        size_t n = 0;
        if (a->resource_data(&key, &p, &n) != KILN_OK || !p) return *why = "the resource is not resident", false;
        rbytes::Node root;
        if (!rbytes::root(p, n, type, &root, why)) return false;
        return rbytes::member(root, path, out, why);
    };
    e.regmap = [](map::RegMap* out) {
        KilnBuildInfo bi{};
        bi.size = sizeof bi;
        std::string side = "server", exe;
        if (g_api && g_api->build_info && g_api->build_info(&bi) == KILN_OK) side = bi.side == KILN_SIDE_CLIENT ? "client" : "server", exe = bi.exe_sha256;
        std::vector<std::string> errs;
        return map::load_dir(kiln_dir() + "\\regmap", side, exe, out, &errs);
    };
    e.aliases = [](Aliases* out, std::vector<std::string>* errs) {
        const std::string path = kiln_dir() + "\\regmap\\views.json";
        const std::filesystem::path p(std::u8string(path.begin(), path.end()));
        std::ifstream f(p, std::ios::binary);
        if (!f) return *out = Aliases{}, true;  // no table: every name is the derived one
        std::stringstream ss;
        ss << f.rdbuf();
        const json doc = json::parse(ss.str(), nullptr, false);
        return load_aliases(doc, out, errs);
    };
    e.registry_read = [](const std::string& r, const std::string& id, const std::string& f, std::string* out) {
        return stage::runner().read(KILN_REGISTRY_VIEW_RESOLVED, r, id, f, out);
    };
    e.registry_select = [](const std::string& r, const std::string& sel, std::string* out) {
        return stage::runner().select(KILN_REGISTRY_VIEW_RESOLVED, r, sel, out);
    };
    e.registry_mapping = [](const std::string& q, std::string* state, std::string* reason) {
        stage::Runner::FieldInfo fi;
        if (!stage::runner().field_info(q, &fi)) return false;
        static const char* names[] = {"mapped", "unmapped", "unproven", "removed"};
        *state = fi.mapping < 4 ? names[fi.mapping] : "unmapped";
        *reason = fi.reason;
        return true;
    };
    e.log = [](int level, const std::string& line) {
        if (level > 0) LOGW("%s", line.c_str());
        else LOGI("%s", line.c_str());
    };
    return e;
}

}  // namespace

void bind_view_service() {
    if (!g_api) return;
    static Service& service = *new Service(make_env());  // never destroyed: the table lives as long as the process
    const KilnResult rc = g_api->register_service(g_owner, KilnStr{KILN_REGISTRY_VIEW_SERVICE, sizeof(KILN_REGISTRY_VIEW_SERVICE) - 1},
                                                  view_table(&service));
    if (rc != KILN_OK) LOGW("registry: kiln.registry.view rc %d", rc);
}

}  // namespace kc::registry::views
