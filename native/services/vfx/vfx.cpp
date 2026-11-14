// SPDX-License-Identifier: MIT
// kiln.vfx: the plugin. core.cpp is the queue and game.cpp the game layer (native/core/abi/services/kiln_vfx.h,
// docs/kiln/services/vfx.md). Client only; kiln/config/vfx.json {"enabled": false} turns it off before anything is hooked. Labs can
// run the simulated game with {"sim": true} on a dedicated server, stepped by the server world, or {"testFailSoft": true}: the real
// game layer on an exe where no signature resolves.
#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>

#include "boot.h"
#include "game.h"
#include "kiln_boot_collector.hpp"
#include "nlohmann/json.hpp"
#include "services/kiln_caps.h"

namespace kvx {

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;

struct Config {
    bool enabled = true;
    bool sim = false;
    bool test_fail_soft = false;  // lab only: run the real game layer on a dedicated server, where no signature resolves
    double trace = 0;             // seconds: log what the game creates after each of Kiln's plays (true = 3)
};

// kiln/config/vfx.json. Missing or broken: the defaults, and a broken file says so.
Config read_config() {
    Config c;
    char dir[1024];
    size_t n = 0;
    if (g_api->path(KILN_PATH_CONFIG, dir, sizeof dir, &n) != KILN_OK || n >= sizeof dir) return c;
    const std::string path = std::string(dir, n) + "/vfx.json";
    wchar_t wide[1200];
    const int wn = MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, wide, 1200);
    if (wn <= 0) return c;
    FILE* f = _wfopen(wide, L"rb");
    if (!f) return c;
    std::string text;
    char buf[4096];
    for (size_t got; (got = std::fread(buf, 1, sizeof buf, f)) > 0;) text.append(buf, got);
    std::fclose(f);
    const auto j = nlohmann::json::parse(text, nullptr, false, true);
    if (j.is_discarded() || !j.is_object()) {
        KV_W("vfx: %s is not valid JSON: using the defaults", path.c_str());
        return c;
    }
    if (auto it = j.find("enabled"); it != j.end() && it->is_boolean()) c.enabled = it->get<bool>();
    if (auto it = j.find("sim"); it != j.end() && it->is_boolean()) c.sim = it->get<bool>();
    if (auto it = j.find("testFailSoft"); it != j.end() && it->is_boolean()) c.test_fail_soft = it->get<bool>();
    if (auto it = j.find("trace"); it != j.end()) c.trace = it->is_boolean() ? (it->get<bool>() ? 3.0 : 0.0) : it->is_number() ? it->get<double>() : 0.0;
    return c;
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev && ev->event == KILN_WORLD_STOPPING && ev->kind == KILN_WORLD_CLIENT) core_world_ended();
}

void KILN_CALL on_sim_tick(const KilnTickInfo*, void*) { sim_update(); }

// ---- the boot generation's vfx collector (chapter 08) ----
// Where nothing is built from a boot generation (a dedicated server, kiln.vfx off by its config, a client while the client gate holds
// mods.boot.vfx), the collector still answers: an empty plan, and staged VFX and materials named in a warning. Elsewhere the build that
// on_ready runs without a boot profile runs in prepare, after OnBoot, before the game's entry point.
bool g_boot_build = false;
struct BootPlan {
    bool built;
};
void KILN_CALL destroy_boot(const void* p) {
    const auto* plan = static_cast<const BootPlan*>(p);
    if (plan->built) build_forget();
    delete plan;
}
KilnResult KILN_CALL prepare_empty(void* why, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out, KilnBootIssueV1* issue) {
    const kiln::boot::Input input = kiln::boot::parse(in);
    const auto docs = input.docs("vfx");
    if (!docs.empty()) {
        issue->code = KILN_BOOT_ISSUE_WARNING | 0x10001u;
        kiln::boot::put(issue->mod_id, docs.front().owner);
        kiln::boot::put(issue->kind, std::string("vfx"));
        kiln::boot::put(issue->key, docs.front().key);
        kiln::boot::put(issue->phase, std::string("prepare"));
        kiln::boot::put(issue->reason, std::to_string(docs.size()) + " staged VFX or material definition(s) are not built here: " +
                                           static_cast<const char*>(why));
    }
    return kiln::boot::prepared(out, new BootPlan{false}, &destroy_boot);
}
KilnResult KILN_CALL prepare_build(void*, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out, KilnBootIssueV1* issue) {
    const kiln::boot::Input input = kiln::boot::parse(in);
    if (!input.ok) return kiln::boot::refuse(out, issue, "", "", "", "kiln.vfx: the boot envelope is not JSON");
    build_on_ready(g_api, g_owner, &input);
    return kiln::boot::prepared(out, new BootPlan{true}, &destroy_boot);
}
bool vfx_held(const KilnApi* api) {
    const auto* caps = static_cast<const KilnCapsApi*>(api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
    return kiln_caps_held(caps, KilnStr{"mods.boot.vfx", 13}) != 0;
}

// Before the game's entry point: mods' VFX have to be served before the game enumerates its VFX resources. A boot profile builds them in
// the boot generation's prepare instead.
void KILN_CALL on_ready(void*) {
    if (!g_boot_build) build_on_ready(g_api, g_owner);
}

static_assert(sizeof(KilnVfxPlay) == 128 && sizeof(KilnVfxInfo) == 88 && sizeof(KilnVfxStatus) == 64 && sizeof(KilnVfxApi) == 112);

const KilnVfxApi kTable = {
    {sizeof(KilnVfxApi), KILN_VFX_VERSION},
    &core_status,
    &core_play,
    &core_stop,
    &core_set_param,
    &core_state,
    &core_info,
    &core_stop_owner,
    &core_is_registered,
    &core_preload,
    &core_status_json,
    &build_define_json,
    &build_status_json,
    &core_bind_guid,
};

}  // namespace

void vlog(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n <= 0 || !g_api) return;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n < static_cast<int>(sizeof(buf)) ? n : sizeof(buf) - 1)});
}

uint32_t thread_id() { return GetCurrentThreadId(); }
uint64_t now_ms() { return GetTickCount64(); }

}  // namespace kvx

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kvx;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_VFX_SERVICE, sizeof(KILN_VFX_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    const Config cfg = read_config();
    if (!cfg.enabled) {
        KV_I("vfx: off by kiln/config/vfx.json: nothing hooked, mods play nothing");
        // the empty plan as on a dedicated server (the module stays mapped; see there)
        kiln::boot::collect(api, owner, "vfx", &prepare_empty, const_cast<char*>("kiln.vfx is off by kiln/config/vfx.json"));
        return KILN_ERR_UNAVAILABLE;
    }
    KilnHandle h = 0;
    if (cfg.sim) {
        KV_W("vfx: SIMULATED game (kiln/config/vfx.json \"sim\"): for labs and tests only, nothing reaches the real VFX system");
        core_set_game(sim_game());
        api->subscribe_tick(owner, KILN_TICK_PRE, &on_sim_tick, nullptr, &h);
    } else {
        if (api->side() != KILN_SIDE_CLIENT && !cfg.test_fail_soft) {
            KV_I("vfx: not a game client (the dedicated server has no VFX): kiln.vfx unavailable here; Kiln.Vfx relays server calls to Kiln clients");
            // A boot generation's vfx part here: the empty validated plan (chapter 08 §4.3 step 4), so a both-sides mod's staged VFX is not a
            // missing collector on a server. Registered although this function answers UNAVAILABLE: kiln-core keeps a plugin that answers so
            // mapped for the process (api.cpp load_one: no FreeLibrary) and only revokes its owner, which kiln.boot uses for log names alone;
            // prepare_empty reads nothing but its input.
            kiln::boot::collect(api, owner, "vfx", &prepare_empty, const_cast<char*>("a dedicated server renders no VFX"));
            return KILN_ERR_UNAVAILABLE;
        }
        real_game_trace(cfg.trace);
        Game* game = real_game(api, owner);
        if (!game) return KILN_ERR_UNAVAILABLE;
        core_set_game(game);
        api->subscribe_world(owner, &on_world, nullptr, &h);
    }
    const KilnResult rc = api->register_service(owner, KilnStr{KILN_VFX_SERVICE, sizeof(KILN_VFX_SERVICE) - 1}, &kTable);
    if (rc == KILN_OK) {
        core_set_build_hooks(&build_on_update, &build_refuses, &build_one_shot_ns);
        info->on_ready = &on_ready;
        // a boot profile: the build moves into the boot generation's prepare, unless the client gate holds mods.boot.vfx (then on_ready builds
        // the mods' files as before and staged VFX are left out with a warning)
        if (vfx_held(api))
            kiln::boot::collect(api, owner, "vfx", &prepare_empty,
                                const_cast<char*>("mods.boot.vfx is held until the client checks pass (08-boot.first-start-definitions)"));
        else
            g_boot_build = kiln::boot::collect(api, owner, "vfx", &prepare_build, nullptr);
        if (g_boot_build) KV_I("vfx: boot profile: mods' VFX and materials are built with the boot generation");
        Game* g = core_game();
        KV_I("vfx: kiln.vfx v1 ready (guard %s, play %s); waiting for the first FxController update", g->has_guard() ? "on" : "OFF",
             g->can_play() ? "on" : "OFF");
    }
    return rc;
}
