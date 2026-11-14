// SPDX-License-Identifier: MIT
#include "config.h"

#include <fstream>

#include "log.h"
#include "nlohmann/json.hpp"

namespace kiln {
namespace {
Config g_config;
nlohmann::json g_doc;  // kiln.json as read, for apply_side

int parse_level(const std::string& s) {
    if (s == "error") return KILN_LOG_ERROR;
    if (s == "warn" || s == "warning") return KILN_LOG_WARN;
    if (s == "debug") return KILN_LOG_DEBUG;
    if (s == "trace") return KILN_LOG_TRACE;
    return KILN_LOG_INFO;
}
// "native": {"policy": ...}; an unknown value keeps the current one (logged).
void read_native(const nlohmann::json& sec, const char* where) {
    if (!sec.contains("native") || !sec["native"].is_object()) return;
    const auto& n = sec["native"];
    if (!n.contains("policy")) return;
    const std::string p = n["policy"].is_string() ? n["policy"].get<std::string>() : "";
    if (p == "always") g_config.native_policy = NativePolicy::Always;
    else if (p == "verified-only") g_config.native_policy = NativePolicy::VerifiedOnly;
    else if (p == "never") g_config.native_policy = NativePolicy::Never;
    else KLOG_W("config: %s native.policy '%s' is not always, verified-only or never; ignored", where, p.c_str());
}
// "preview": true | "all" | [names]. Present in a section, it replaces what an earlier section said.
void read_preview(const nlohmann::json& sec, const char* where) {
    if (!sec.contains("preview")) return;
    const auto& p = sec["preview"];
    g_config.preview_all = false;
    g_config.preview.clear();
    if (p.is_boolean()) {
        g_config.preview_all = p.get<bool>();
    } else if (p.is_string() && p.get<std::string>() == "all") {
        g_config.preview_all = true;
    } else if (p.is_array()) {
        for (const auto& n : p) {
            if (n.is_string()) g_config.preview.push_back(n.get<std::string>());
            else KLOG_W("config: %s \"preview\" holds an entry that is not a capability name; ignored", where);
        }
    } else {
        KLOG_W("config: %s \"preview\" is not true, false or a list of capability names; ignored", where);
    }
}
}  // namespace

const Config& config() { return g_config; }

const char* native_policy_name(NativePolicy p) {
    return p == NativePolicy::Never ? "never" : p == NativePolicy::VerifiedOnly ? "verified-only" : "always";
}

void load_config() {
    const auto file = paths().kiln / L"kiln.json";
    std::ifstream in(file);
    if (!in) {
        KLOG_I("config: no kiln.json, defaults (policy verified-only)");
        return;
    }
    try {
        const auto doc = nlohmann::json::parse(in, nullptr, true, true);
        g_doc = doc;
        g_config.policy_any = doc.value("policy", "verified-only") == "any";
        g_config.clr = doc.value("clr", true);
        g_config.plugins = doc.value("plugins", true);
        g_config.log_level = parse_level(doc.value("log_level", "info"));
        log::set_level(g_config.log_level);
        read_native(doc, "kiln.json");
        read_preview(doc, "kiln.json");
        KLOG_I("config: policy=%s clr=%d plugins=%d native.policy=%s", g_config.policy_any ? "any" : "verified-only",
               g_config.clr, g_config.plugins, native_policy_name(g_config.native_policy));
    } catch (const std::exception& e) {
        KLOG_E("config: kiln.json unreadable (%s); using defaults", e.what());
    }
}

void apply_side(uint32_t side) {
    const char* name = side == KILN_SIDE_CLIENT ? "client" : "server";
    if (!g_doc.is_object() || !g_doc.contains(name) || !g_doc[name].is_object()) return;
    try {
        const auto& sec = g_doc[name];
        // Apply the gate override even if another side option has the wrong type. In particular, preview:false
        // must not inherit a top-level preview:true because e.g. clr could not be read.
        read_preview(sec, name);
        if (sec.contains("policy")) g_config.policy_any = sec.value("policy", "verified-only") == "any";
        g_config.clr = sec.value("clr", g_config.clr);
        g_config.plugins = sec.value("plugins", g_config.plugins);
        if (sec.contains("log_level")) {
            g_config.log_level = parse_level(sec.value("log_level", "info"));
            log::set_level(g_config.log_level);
        }
        read_native(sec, name);
        KLOG_I("config: \"%s\" section: policy=%s clr=%d plugins=%d native.policy=%s", name,
               g_config.policy_any ? "any" : "verified-only", g_config.clr, g_config.plugins,
               native_policy_name(g_config.native_policy));
    } catch (const std::exception& e) {
        KLOG_E("config: kiln.json \"%s\" section partly unreadable (%s); readable values stay", name, e.what());
    }
}

bool build_verified(uint32_t side, const std::string& sha256, std::string* note) {
    const auto file = paths().sigdb / L"builds" / to_wide(side == KILN_SIDE_CLIENT ? "client" : "server") /
                      to_wide(sha256 + ".json");
    std::ifstream in(file);
    if (!in) {
        if (note) *note = "no build record";
        return false;
    }
    try {
        const auto doc = nlohmann::json::parse(in, nullptr, true, true);
        if (doc.value("sha256", "") != sha256) {
            if (note) *note = "build record sha256 mismatch";
            return false;
        }
        if (doc.contains("side") && doc.value("side", "") != (side == KILN_SIDE_CLIENT ? "client" : "server")) {
            if (note) *note = "build record is for the other side";
            return false;
        }
        const bool ok = doc.value("verified", false);
        if (note) *note = ok ? doc.value("note", "verified") : "build record not verified";
        return ok;
    } catch (const std::exception& e) {
        if (note) *note = std::string("build record unreadable: ") + e.what();
        return false;
    }
}

}  // namespace kiln
