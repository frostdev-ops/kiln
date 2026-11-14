// SPDX-License-Identifier: MIT
// kiln.repl's lab gate (no game; built with the service's own repl.cpp and loopback.cpp, run under plain Wine by
// tests/run.sh): the loopback harness's functions answer only with kiln.json's policy "any" AND kiln.net's lab port,
// on the tick thread; otherwise KILN_ERR_UNAVAILABLE (KILN_ERR_WRONG_THREAD off the tick thread), and repl.loopback is
// reported off with the reason. The service starts here against a stand-in KilnApi: its kiln.json is a file this test
// writes, kiln.net's lab transport is a stand-in status.
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <fstream>
#include <map>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_caps.h"
#include "services/kiln_net.h"
#include "services/kiln_repl.h"

extern "C" KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info);

namespace {

int g_checks = 0, g_failed = 0;
void check(bool ok, const char* what, const char* where) {
    ++g_checks;
    if (!ok) {
        ++g_failed;
        std::printf("FAIL [%s] %s\n", where, what);
    }
}
const char* g_case = "";
#define CHECK(x) check((x), #x, g_case)

std::string g_dir;           // <tmp>\repl-gate-test: the stand-in <game>\kiln
uint32_t g_lab_port = 0;     // kiln.net's lab transport in this case
bool g_tick_thread = true;
const KilnReplApi* g_table = nullptr;
std::vector<std::string> g_log;
struct Report {
    uint32_t state, reason;
    std::string text;
};
std::map<std::string, Report> g_reports;

void KILN_CALL api_log(KilnOwner, int32_t, KilnStr text) { g_log.emplace_back(text.ptr, text.len); }
bool KILN_CALL api_feature(KilnStr name) {
    const std::string n(name.ptr, name.len);
    return n == "repl.observe" || n == "repl.loopback";
}
KilnResult KILN_CALL api_path(int32_t kind, char* buffer, size_t capacity, size_t* out_len) {
    if (kind != KILN_PATH_KILN) return KILN_ERR_INVALID_ARGUMENT;
    if (out_len) *out_len = g_dir.size();
    if (capacity < g_dir.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, g_dir.data(), g_dir.size());
    return KILN_OK;
}
void dummy_game_function() {}
KilnResult KILN_CALL api_symbol(KilnStr, uintptr_t* out) {
    *out = reinterpret_cast<uintptr_t>(&dummy_game_function);
    return KILN_OK;
}
KilnResult KILN_CALL api_constant(KilnStr name, int64_t* out) {
    static const std::map<std::string, int64_t> values = {
        {"world.storage", 0xe179c0},          {"repl/world.objects", 0x15fd2a8},  {"repl/obj.stride", 0x125850},
        {"repl/obj.client_ctx", 0x8},         {"repl/obj.pred_ctx", 0x92c18},     {"repl/world.slot_mask", 0x15d3c90},
        {"repl/world.tick", 0xe17e9c},        {"repl/ctx.heap", 0x8f0},           {"repl/obj.count", 17},
        {"net/gs.machines", 0x16a0},          {"spawn/registry.template_map", 0x745810},
        {"spawn/registry.template_map_cap", 0x745820}, {"spawn/registry.template_map_values", 0x745850}};
    auto it = values.find(std::string(name.ptr, name.len));
    if (it == values.end()) return KILN_ERR_NOT_FOUND;
    *out = it->second;
    return KILN_OK;
}

// kiln.caps: what the service reports
KilnResult KILN_CALL caps_find(KilnStr, KilnCapRow*) { return KILN_ERR_NOT_FOUND; }
KilnResult KILN_CALL caps_report(KilnOwner, KilnStr name, uint32_t state, uint32_t reason, KilnStr text) {
    g_reports[std::string(name.ptr, name.len)] = Report{state, reason, std::string(text.ptr, text.len)};
    return KILN_OK;
}
// kiln.net: the lab transport's status
KilnResult KILN_CALL net_status(KilnNetStatus* out) {
    out->lab = g_lab_port ? 1 : 0;
    out->lab_port = g_lab_port;
    return KILN_OK;
}
KilnResult KILN_CALL net_selftest(uint32_t, uint64_t, uint64_t, uint64_t*) { return KILN_ERR_INVALID_ARGUMENT; }

const void* KILN_CALL api_service(KilnStr name, uint32_t) {
    static KilnCapsApi caps = [] {
        KilnCapsApi t{};
        t.header = {sizeof t, KILN_CAPS_VERSION};
        t.find = &caps_find;
        t.report = &caps_report;
        return t;
    }();
    static KilnNetApi net = [] {
        KilnNetApi t{};
        t.header = {sizeof t, KILN_NET_VERSION};
        t.status = &net_status;
        t.lab_selftest = &net_selftest;
        return t;
    }();
    const std::string n(name.ptr, name.len);
    if (n == KILN_CAPS_SERVICE) return &caps;
    if (n == KILN_NET_SERVICE) return &net;
    return nullptr;  // no kiln.hooks: the send step stays unhooked (observe off), which this test does not look at
}
KilnResult KILN_CALL api_register(KilnOwner, KilnStr, const void* table) {
    g_table = static_cast<const KilnReplApi*>(table);
    return KILN_OK;
}
KilnResult KILN_CALL api_subscribe_tick(KilnOwner, int32_t, KilnTickFn, void*, KilnHandle* out) {
    *out = 1;
    return KILN_OK;
}
KilnResult KILN_CALL api_subscribe_world(KilnOwner, KilnWorldEventFn, void*, KilnHandle* out) {
    *out = 2;
    return KILN_OK;
}
bool KILN_CALL api_on_tick_thread() { return g_tick_thread; }
uintptr_t KILN_CALL api_server_world() { return 0; }
uint32_t KILN_CALL api_side() { return KILN_SIDE_SERVER; }

KilnApi make_api() {
    KilnApi a{};
    a.size = sizeof a;
    a.abi_version = KILN_ABI_VERSION;
    a.log = &api_log;
    a.feature = &api_feature;
    a.path = &api_path;
    a.symbol = &api_symbol;
    a.constant = &api_constant;
    a.service = &api_service;
    a.register_service = &api_register;
    a.subscribe_tick = &api_subscribe_tick;
    a.subscribe_world = &api_subscribe_world;
    a.on_tick_thread = &api_on_tick_thread;
    a.server_world = &api_server_world;
    a.side = &api_side;
    return a;
}

void write_kiln_json(const char* text) {
    std::ofstream(g_dir + "\\kiln.json", std::ios::trunc) << text;
}

bool has_line(const char* part) {
    for (const auto& l : g_log)
        if (l.find(part) != std::string::npos) return true;
    return false;
}

// Every lab function refuses with `rc`.
void refused(KilnResult rc) {
    KilnReplFake fakes[4];
    uint32_t added = 99;
    CHECK(g_table->lab_add_fakes(4, fakes, 4, &added) == rc && added == 0);
    CHECK(g_table->lab_force_relevant(16, 5) == rc);
    CHECK(g_table->lab_remove_fakes() == rc);
    KilnReplLoopbackStats s{};
    s.size = sizeof s;
    CHECK(g_table->lab_stats(&s) == rc);
    uint32_t len = 0, tick = 0;
    CHECK(g_table->lab_decoded(16, 5, 0x1234, nullptr, 0, &len, &tick) == rc);
}

}  // namespace

int main() {
    char tmp[MAX_PATH];
    GetTempPathA(sizeof tmp, tmp);
    g_dir = std::string(tmp) + "repl-gate-test";
    CreateDirectoryA(g_dir.c_str(), nullptr);
    static const KilnApi api = make_api();
    KilnPluginInfo info{};
    info.size = sizeof info;
    write_kiln_json("{\"policy\": \"any\"}");
    g_lab_port = 28130;
    const KilnResult main_rc = kiln_plugin_main(&api, 7, &info);
    g_case = "start";
    CHECK(main_rc == KILN_OK && g_table && info.on_ready);
    if (!g_table || !info.on_ready) return 1;
    CHECK(g_table->header.size >= offsetof(KilnReplApi, lab_decoded) + sizeof(g_table->lab_decoded));

    // policy "any" and a lab port: the harness answers (no world here, so it stops at that)
    g_case = "any + lab port";
    g_log.clear();
    g_reports.clear();
    info.on_ready(info.user);
    CHECK((g_table->capabilities() & KILN_REPL_CAP_LOOPBACK) != 0);
    CHECK(has_line("repl: loopback on (lab port 28130"));
    CHECK(g_reports.find("repl.loopback") == g_reports.end());
    KilnReplLoopbackStats s{};
    s.size = sizeof s;
    CHECK(g_table->lab_stats(&s) == KILN_OK && s.fakes == 0);
    CHECK(g_table->lab_force_relevant(16, 5) == KILN_ERR_INVALID_ARGUMENT);  // past the gate: 16 is not a fake
    CHECK(g_table->lab_remove_fakes() == KILN_OK);
    KilnReplFake fakes[4];
    uint32_t added = 0;
    CHECK(g_table->lab_add_fakes(0, fakes, 4, &added) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(g_table->lab_add_fakes(4, fakes, 4, &added) == KILN_ERR_UNAVAILABLE && added == 0);  // no server world
    // ... off the tick thread
    g_tick_thread = false;
    refused(KILN_ERR_WRONG_THREAD);
    g_tick_thread = true;

    // without policy "any" (a verified build's default): refused, the row says why
    g_case = "verified-only + lab port";
    write_kiln_json("{\"policy\": \"verified-only\"}");
    g_log.clear();
    g_reports.clear();
    info.on_ready(info.user);
    CHECK((g_table->capabilities() & KILN_REPL_CAP_LOOPBACK) == 0);
    refused(KILN_ERR_UNAVAILABLE);
    CHECK(g_reports.count("repl.loopback") && g_reports["repl.loopback"].state == KILN_CAP_UNAVAILABLE &&
          g_reports["repl.loopback"].reason == KILN_CAP_REASON_POLICY &&
          g_reports["repl.loopback"].text.find("policy \"any\"") != std::string::npos &&
          g_reports["repl.loopback"].text.find("labPort") == std::string::npos);
    CHECK(has_line("repl: loopback off (lab servers only"));

    // no kiln.json at all: the core's default is verified-only
    g_case = "no kiln.json";
    DeleteFileA((g_dir + "\\kiln.json").c_str());
    info.on_ready(info.user);
    CHECK((g_table->capabilities() & KILN_REPL_CAP_LOOPBACK) == 0);
    refused(KILN_ERR_UNAVAILABLE);

    // the server section replaces the top level, both ways (the core's rule)
    g_case = "server section verified-only";
    write_kiln_json("{\"policy\": \"any\", \"server\": {\"policy\": \"verified-only\"}}");
    info.on_ready(info.user);
    refused(KILN_ERR_UNAVAILABLE);
    g_case = "server section any";
    write_kiln_json("{\"policy\": \"verified-only\", \"server\": {\"policy\": \"any\"}} // a comment, as kiln.json allows");
    info.on_ready(info.user);
    CHECK((g_table->capabilities() & KILN_REPL_CAP_LOOPBACK) != 0);
    CHECK(g_table->lab_stats(&s) == KILN_OK);

    // policy "any" without a lab port (labPort is not in kiln/config/network.json): refused, the row says why
    g_case = "any, no lab port";
    write_kiln_json("{\"policy\": \"any\"}");
    g_lab_port = 0;
    g_reports.clear();
    info.on_ready(info.user);
    CHECK((g_table->capabilities() & KILN_REPL_CAP_LOOPBACK) == 0);
    refused(KILN_ERR_UNAVAILABLE);
    CHECK(g_reports.count("repl.loopback") && g_reports["repl.loopback"].reason == KILN_CAP_REASON_POLICY &&
          g_reports["repl.loopback"].text.find("labPort") != std::string::npos &&
          g_reports["repl.loopback"].text.find("policy \"any\"") == std::string::npos);

    // neither
    g_case = "neither";
    write_kiln_json("{\"policy\": \"verified-only\"}");
    g_reports.clear();
    info.on_ready(info.user);
    refused(KILN_ERR_UNAVAILABLE);
    CHECK(g_reports.count("repl.loopback") && g_reports["repl.loopback"].text.find("policy \"any\" and kiln.net's lab port") != std::string::npos);

    DeleteFileA((g_dir + "\\kiln.json").c_str());
    RemoveDirectoryA(g_dir.c_str());
    std::printf("repl lab gate tests: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
