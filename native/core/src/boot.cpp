// SPDX-License-Identifier: MIT
// kiln_boot: called by the dinput8 proxy on the main thread, at the game's entry point, before any game code ran.
//   paths, log, kiln.json, profile (kiln.profile), image + side, build record, native mods "early", sigdb, hooks,
//   in-core services, service DLLs + plugins, native mods "boot", native "world" armed, .NET host, state/boot.json.
#include "api.h"
#include "clr.h"
#include "config.h"
#include "image.h"
#include "log.h"
#include "native_mods.h"
#include "nlohmann/json.hpp"
#include "profile.h"
#include "sigdb.h"
#include "tick.h"
#include "services/service.h"

namespace kiln {
namespace {

bool g_booted = false;
KilnBootInfo g_boot_info{};

// Copies the proxy's KilnBootInfo field by field, as far as its size goes (v1 proxies send 16 bytes).
void take_boot_info(const KilnBootInfo* info) {
    g_boot_info = KilnBootInfo{};
    g_boot_info.size = sizeof(KilnBootInfo);
    if (!info || info->size < KILN_BOOT_INFO_V1_SIZE) return;
    g_boot_info.proxy_module = info->proxy_module;
    if (KILN_BOOT_INFO_HAS(info, play_once)) g_boot_info.play_once = info->play_once;
    if (KILN_BOOT_INFO_HAS(info, chain) && info->chain) {
        g_boot_info.chain_count = info->chain_count;
        g_boot_info.chain = info->chain;
    }
    if (KILN_BOOT_INFO_HAS(info, play_once_nonce)) g_boot_info.play_once_nonce = info->play_once_nonce;
    if (KILN_BOOT_INFO_HAS(info, play_once_profile)) g_boot_info.play_once_profile = info->play_once_profile;
}

const char* play_once_name(uint32_t v) {
    switch (v) {
        case KILN_PLAY_ONCE_NO_MODS: return "no-mods";
        case KILN_PLAY_ONCE_PROFILE: return "profile";
        case KILN_PLAY_ONCE_IGNORED_EXPIRED: return "ignored-expired";
        case KILN_PLAY_ONCE_IGNORED_UNDELETABLE: return "ignored-undeletable";
        case KILN_PLAY_ONCE_IGNORED_SERVER: return "ignored-server";
        case KILN_PLAY_ONCE_IGNORED_INVALID: return "ignored-invalid";
        default: return "none";
    }
}
const char* const kChainStatus[] = {"loaded", "failed", "missing", "refused", "duplicate", "skipped"};
const char* const kChainSource[] = {"chain", "profile", "emf"};
const char* const kChainDetail[] = {nullptr, "self", "system", "kiln", "shroudforge", "not-dll", "outside", "too-many"};

const char* chain_status(uint32_t s) { return s < 6 ? kChainStatus[s] : "failed"; }
const char* chain_source(uint32_t s) { return s < 3 ? kChainSource[s] : "chain"; }
const char* chain_detail(uint32_t d) { return d < 8 ? kChainDetail[d] : nullptr; }

void log_proxy() {
    const KilnBootInfo& b = g_boot_info;
    if (b.size < KILN_BOOT_INFO_V2_SIZE)
        KLOG_I("proxy: an older Kiln proxy (KilnBootInfo %u bytes): no chain loading, no play-once", b.size);
    if (b.play_once != KILN_PLAY_ONCE_NONE)
        KLOG_I("proxy: play-once %s%s%.*s", play_once_name(b.play_once), b.play_once_nonce.len ? ", nonce " : "",
               static_cast<int>(b.play_once_nonce.len), b.play_once_nonce.ptr ? b.play_once_nonce.ptr : "");
    for (uint32_t i = 0; i < b.chain_count; ++i) {
        const KilnChainEntry& e = b.chain[i];
        const char* detail = e.status == KILN_CHAIN_REFUSED ? chain_detail(e.detail) : nullptr;
        log::writef(e.status == KILN_CHAIN_FAILED || e.status == KILN_CHAIN_MISSING ? KILN_LOG_WARN : KILN_LOG_INFO, "kiln",
                    "proxy: chain %u (%s) %.*s: %s%s%s%s", i + 1, chain_source(e.source), static_cast<int>(e.path.len),
                    e.path.ptr ? e.path.ptr : "", chain_status(e.status), detail ? " (" : "", detail ? detail : "",
                    detail ? ")" : "");
        if (e.status == KILN_CHAIN_FAILED) KLOG_W("proxy: chain %u: error %d", i + 1, e.error);
    }
}

struct BootFacts {
    double started_ms = 0;
    std::string started;  // RFC 3339 UTC
    bool image_ok = false;
    bool verified = false;
    bool hooks = false;
    std::string safe_idle;
    bool clr_tried = false, clr_started = false;
    std::string clr_error;
    std::vector<std::string> errors;
};

void write_boot_json(const BootFacts& f) {
    using nlohmann::json;
    const auto& prof = profile::state();
    json chain = json::array();
    for (uint32_t i = 0; i < g_boot_info.chain_count; ++i) {
        const KilnChainEntry& e = g_boot_info.chain[i];
        json o = {{"path", std::string(sv(e.path))}, {"source", chain_source(e.source)}, {"status", chain_status(e.status)}};
        const char* d = e.status == KILN_CHAIN_REFUSED ? chain_detail(e.detail) : nullptr;
        o["detail"] = d ? json(d) : json(nullptr);
        if (e.status == KILN_CHAIN_FAILED) o["error"] = e.error;
        chain.push_back(o);
    }
    json missing = json::array();
    for (const auto& m : prof.missing) missing.push_back({{"kind", profile::kind_name(m.kind)}, {"id", m.id}, {"path", m.path}});
    auto null_if_empty = [](const std::string& s) { return s.empty() ? json(nullptr) : json(s); };
    json services = json::array();
    svc::each([](const svc::Service& s, void* user) {
        if (s.available || std::string_view(s.name) == KILN_PROFILE_SERVICE)
            static_cast<json*>(user)->push_back(std::string(s.name) + "@" + std::to_string(s.version));
    }, &services);
    for (const auto& p : published_services()) services.push_back(p);
    json plugins = json::array();
    for (const auto& p : plugin_records())
        plugins.push_back({{"file", p.file}, {"path", p.path}, {"id", p.id}, {"version", p.version}, {"status", p.status},
                           {"error", null_if_empty(p.error)}});
    const auto native = native_mods::summary();
    json doc = {
        {"format", 1},
        {"kiln", std::string(kVersion)},
        {"abi", KILN_ABI_VERSION},
        {"pid", GetCurrentProcessId()},
        {"started", f.started},
        {"written", utc_now()},
        {"bootMs", static_cast<double>(static_cast<int64_t>((now_ms() - f.started_ms) * 10)) / 10.0},
        {"side", side_name(image().side)},
        {"exe", {{"name", u8(paths().exe.filename())}, {"sha256", image().sha256}, {"verified", f.verified}}},
        {"policy", config().policy_any ? "any" : "verified-only"},
        {"hooks", f.hooks},
        {"safeIdle", f.hooks ? json(nullptr) : json(f.safe_idle)},
        {"playOnce", {{"mode", play_once_name(g_boot_info.play_once)},
                      {"nonce", g_boot_info.play_once_nonce.len ? json(std::string(sv(g_boot_info.play_once_nonce))) : json(nullptr)},
                      {"consumed", g_boot_info.play_once == KILN_PLAY_ONCE_NO_MODS || g_boot_info.play_once == KILN_PLAY_ONCE_PROFILE}}},
        {"profile", {{"mode", profile::mode_name(prof.mode)},
                     {"id", null_if_empty(prof.id)},
                     {"name", null_if_empty(prof.name)},
                     {"generation", prof.generation},
                     {"file", null_if_empty(prof.file)},
                     {"error", null_if_empty(prof.error)},
                     {"configDir", prof.config_dir},
                     {"dataDir", prof.data_dir},
                     {"dropins", json::array()},
                     {"counts", {{"kiln", prof.mods[KILN_PROFILE_KIND_KILN].size()},
                                 {"native", prof.mods[KILN_PROFILE_KIND_NATIVE].size()},
                                 {"plugin", prof.mods[KILN_PROFILE_KIND_PLUGIN].size()},
                                 {"emf", prof.mods[KILN_PROFILE_KIND_EMF].size()}}},
                     {"missing", missing}}},
        {"paths", {{"game", u8(paths().game)}, {"kiln", u8(paths().kiln)}, {"mods", u8(paths().mods)},
                   {"logs", u8(paths().logs)}, {"data", u8(paths().data)}, {"control", u8(paths().control)},
                   {"config", u8(paths().config)}, {"state", u8(paths().state)}}},
        {"chain", chain},
        {"services", services},
        {"plugins", plugins},
        {"native", {{"loaded", native.loaded}, {"failed", native.failed}, {"pending", native.pending}}},
        {"clr", {{"started", f.clr_started}, {"error", f.clr_tried && !f.clr_started ? json(f.clr_error) : json(nullptr)}}},
        {"errors", f.errors},
    };
    for (uint32_t k = 0; k < KILN_PROFILE_KINDS; ++k)
        if (profile::dropins(k)) doc["profile"]["dropins"].push_back(profile::kind_name(k));
    if (!write_file_atomic(paths().state / L"boot.json", doc.dump(2) + "\n"))
        KLOG_W("state: cannot write %s", u8(paths().state / L"boot.json").c_str());
}

void boot() {
    init_paths();
    BootFacts f;
    f.started_ms = now_ms();
    f.started = utc_now();
    log::start();
    KLOG_I("Kiln %.*s starting (pid %lu)", static_cast<int>(kVersion.size()), kVersion.data(), GetCurrentProcessId());
    log_proxy();
    load_config();
    profile::load();
    if (profile::state().mode == KILN_PROFILE_INVALID)
        f.errors.push_back("profile " + profile::state().file + " is invalid (" + profile::state().error + "): no mods loaded");
    if (!init_image()) {
        KLOG_E("cannot parse the game image; staying idle");
        f.safe_idle = "the game image cannot be parsed";
        f.errors.push_back(f.safe_idle);
        write_boot_json(f);
        return;
    }
    // Server or client: its own definitions, build records and kiln.json section.
    const uint32_t side = image().side;
    apply_side(side);
    std::string note;
    const bool verified = build_verified(side, image().sha256, &note);
    f.verified = verified;
    // Native mods: discovered and resolved now; phase early runs before anything is hooked (also in safe idle).
    native_mods::discover(verified);
    native_mods::run_early();

    const auto definitions = paths().sigdb / to_wide(std::string(side_name(side)) + ".json");  // + <side>.d/*.json
    const bool resolved = sigdb::load_and_resolve(definitions);
    if (resolved) sigdb::write_report(paths().logs / L"sigdb-report.json");

    bool hooks = resolved && (verified || config().policy_any);
    if (!resolved) {
        KLOG_W("safe idle: no symbol definitions (%s)", to_utf8(definitions.wstring()).c_str());
        f.safe_idle = "no symbol definitions for this side (" + u8(definitions.filename()) + ")";
    } else if (!verified && !config().policy_any) {
        KLOG_W("safe idle: build %s is not verified (%s) and policy is verified-only; the game runs vanilla",
               image().sha256.c_str(), note.c_str());
        f.safe_idle = "build " + image().sha256 + " is not verified (" + note + ") and policy is verified-only";
    } else {
        KLOG_I("build %s: %s%s%s", image().sha256.c_str(), verified ? "verified" : "UNVERIFIED",
               verified ? "" : " (policy any)", side == KILN_SIDE_CLIENT ? " [client]" : "");
    }

    if (hooks) {
        if (!tick::install()) KLOG_W("tick hook unavailable");
        uintptr_t add_listener = 0;
        if (sigdb::feature("log.mirror") && sigdb::symbol("AddLogListener", &add_listener)) {
            log::install_game_listener(add_listener);
            KLOG_I("log: game log mirror registered");
        }
    }
    f.hooks = hooks;
    set_runtime_state(verified, hooks);
    if (hooks) svc::init_all();
    for (const auto& feature : sigdb::failed_features()) KLOG_W("feature %s unavailable on this build", feature.c_str());

    if (hooks) load_plugins();  // kiln/services/*.dll, then plugins (profile-aware), then on_ready
    for (const auto& p : plugin_records())
        if (std::string_view(p.status) == "failed") f.errors.push_back("plugin " + p.file + ": " + p.error);
    native_mods::run_boot();
    native_mods::arm_world(hooks);
    const auto native = native_mods::summary();
    if (native.failed) f.errors.push_back(std::to_string(native.failed) + " native mod(s) failed (state/native.json)");
    if (config().clr) {
        f.clr_tried = true;
        f.clr_started = clr::boot(api());
        if (!f.clr_started) f.clr_error = "the .NET host did not start (kiln.log, [clr] lines)";
    }
    write_boot_json(f);
    KLOG_I("boot complete; handing control to the game");
}

}  // namespace

const KilnBootInfo& boot_info() { return g_boot_info; }

}  // namespace kiln

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_boot(const KilnBootInfo* info) {
    if (kiln::g_booted) return KILN_OK;
    kiln::g_booted = true;
    kiln::take_boot_info(info);
    try {
        kiln::boot();
    } catch (const std::exception& e) {
        kiln::log::writef(KILN_LOG_ERROR, "kiln", "boot failed: %s", e.what());
        return KILN_ERR_INTERNAL;
    } catch (...) {
        kiln::log::writef(KILN_LOG_ERROR, "kiln", "boot failed");
        return KILN_ERR_INTERNAL;
    }
    return KILN_OK;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(inst);
    return TRUE;
}
