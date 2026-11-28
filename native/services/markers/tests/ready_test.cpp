// SPDX-License-Identifier: MIT
// kiln.markers' glue (markers.cpp) through the core's start sequence on a fake core: kiln_plugin_main, then on_ready (the
// core calls it under a fault guard and only logs "on_ready faulted"), once per process side: a dedicated server (with
// kiln.assets loaded, as kiln-assets.dll is on a server) and a game client, joined or hosting (the same side). on_ready must
// return on each, and only a game client registers for the game's resources (when_resources_ready): a dedicated server's
// icon catalog comes over the marker link. The glue's state is the DLL's, once per process, so each case runs in a child
// process; a child that faults (the sanitizer's SEGV report above, or a signal) fails its case. The parts with no host build
// (anchors.cpp: game hooks; surfaces.cpp: render and UI) are the stand-ins below; the link (link.cpp, the server role, the
// receiver, the bridge) is the real one.
#include <sys/wait.h>
#include <unistd.h>

#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>

#include "check.h"
#include "glue.h"
#include "kiln_api.h"
#include "services/kiln_assets.h"
#include "services/kiln_caps.h"
#include "services/kiln_markers.h"

extern "C" KilnResult kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info);

// ---- stand-ins for the parts that hook the game or draw (glue.h) ---------------------------------------------------------
namespace kmk {
void anchors_start() {}
void anchors_ready() {}
void anchors_world(const KilnWorldEvent&) {}
void anchors_post(uint32_t) {}
KilnResult anchors_capture(KilnOwner, uint32_t, uint64_t, KilnEntity, KilnMarkerAnchorResult*) { return KILN_ERR_UNAVAILABLE; }
KilnResult anchors_release(KilnOwner, KilnMarkerAnchor) { return KILN_ERR_UNAVAILABLE; }
void anchors_release_owner(KilnOwner) {}
bool anchors_follow(uint64_t, KilnVec3*, uint32_t*) { return false; }
uint32_t anchors_retain(KilnOwner, uint32_t, KilnMarkerAnchor, uint64_t*, char*, size_t) { return KILN_MARKER_INVALID_ANCHOR; }
void anchors_drop(uint64_t) {}
void surfaces_ready() {}
void surfaces_world(const KilnWorldEvent&, std::vector<Note>*) {}
void surfaces_client_post(std::vector<Note>*) {}
}  // namespace kmk

namespace {

// ---- the fake core ---------------------------------------------------------------------------------------------------------
struct Row {
    uint32_t state = 0, reason = 0;
    std::string text;
};

struct Fake {
    uint32_t side = KILN_SIDE_SERVER;
    bool assets = true;       // kiln-assets.dll is loaded
    bool server_step = true;  // the server world's Post can be subscribed
    int resources_waits = 0;  // when_resources_ready calls
    std::map<std::string, Row> rows;
    std::vector<std::string> registered;
    const KilnMarkersApi* markers = nullptr;
} F;

std::string s(KilnStr v) { return v.ptr ? std::string(v.ptr, v.len) : std::string(); }

KilnResult KILN_CALL c_report(KilnOwner, KilnStr name, uint32_t state, uint32_t reason, KilnStr text) {
    F.rows[s(name)] = Row{state, reason, s(text)};
    return KILN_OK;
}
KilnResult KILN_CALL a_read(const KilnAssetKey*, void*, size_t, size_t* len) {
    if (len) *len = 0;
    return KILN_ERR_NOT_FOUND;
}
KilnResult KILN_CALL a_when_ready(void(KILN_CALL*)(void*), void*) {
    ++F.resources_waits;  // the real one calls back later, on the game's thread
    return KILN_OK;
}

KilnCapsApi g_caps_table{};
KilnAssetsApi g_assets_table{};

void KILN_CALL k_log(KilnOwner, int32_t, KilnStr) {}
KilnResult KILN_CALL k_build_info(KilnBuildInfo* out) {
    out->side = F.side;
    std::memset(out->exe_sha256, '0', 64);
    out->exe_sha256[64] = 0;
    return KILN_OK;
}
bool KILN_CALL k_feature(KilnStr name) { return s(name) == "world.lifecycle"; }
const void* KILN_CALL k_service(KilnStr name, uint32_t) {
    const std::string n = s(name);
    if (n == KILN_CAPS_SERVICE) return &g_caps_table;
    if (n == KILN_ASSETS_SERVICE) return F.assets ? &g_assets_table : nullptr;
    return nullptr;
}
KilnResult KILN_CALL k_register(KilnOwner, KilnStr name, const void* table) {
    F.registered.push_back(s(name));
    if (s(name) == KILN_MARKERS_SERVICE) F.markers = static_cast<const KilnMarkersApi*>(table);
    return KILN_OK;
}
uint32_t KILN_CALL k_side() { return F.side; }
bool KILN_CALL k_on_world_thread(uint32_t) { return true; }
KilnResult KILN_CALL k_world_tick(KilnOwner, uint32_t kind, int32_t, KilnTickFn, void*, KilnHandle* out) {
    if (kind == KILN_WORLD_SERVER && !F.server_step) return KILN_ERR_UNAVAILABLE;
    if (out) *out = 1;
    return KILN_OK;
}
KilnResult KILN_CALL k_subscribe_world(KilnOwner, KilnWorldEventFn, void*, KilnHandle* out) {
    if (out) *out = 2;
    return KILN_OK;
}

KilnApi make_api() {
    g_caps_table.header = {sizeof(KilnCapsApi), KILN_CAPS_VERSION};
    g_caps_table.report = &c_report;
    g_assets_table.header = {sizeof(KilnAssetsApi), KILN_ASSETS_VERSION};
    g_assets_table.resource_read = &a_read;
    g_assets_table.when_resources_ready = &a_when_ready;
    KilnApi api{};
    api.size = sizeof(KilnApi);
    api.abi_version = KILN_ABI_VERSION;
    api.log = &k_log;
    api.build_info = &k_build_info;
    api.feature = &k_feature;
    api.service = &k_service;
    api.register_service = &k_register;
    api.side = &k_side;
    api.on_world_thread = &k_on_world_thread;
    api.subscribe_world_tick = &k_world_tick;
    api.subscribe_world = &k_subscribe_world;
    return api;
}

bool row_is(const char* name, uint32_t state, uint32_t reason) {
    const auto it = F.rows.find(name);
    if (it == F.rows.end()) {
        std::printf("  no row %s\n", name);
        return false;
    }
    if (it->second.state != state || it->second.reason != reason)
        std::printf("  row %s: state %u reason %u (%s)\n", name, it->second.state, it->second.reason, it->second.text.c_str());
    return it->second.state == state && it->second.reason == reason;
}

// Runs the core's sequence in this (child) process, then `checks`. Writes 'R' to `fd` once on_ready returned.
template <class Checks>
void start_and_check(int fd, Checks checks) {
    static KilnApi api = make_api();
    KilnPluginInfo info{};
    info.size = sizeof info;
    CHECK_EQ(kiln_plugin_main(&api, 7, &info), KILN_OK);
    CHECK(info.on_ready != nullptr);
    if (info.on_ready) info.on_ready(info.user);
    const char r = 'R';
    if (write(fd, &r, 1) != 1) CHECK(false);
    CHECK(F.markers != nullptr);
    if (F.markers) checks();
}

// One case in a child process: the case passes when the child got through on_ready and saw no failed check.
template <class Setup, class Checks>
void run_case(const char* name, Setup setup, Checks checks) {
    kmk_test::begin();
    int p[2];
    if (pipe(p) != 0) {
        CHECK(false);
        kmk_test::end(name);
        return;
    }
    std::fflush(stdout);
    const pid_t pid = fork();
    if (pid == 0) {
        close(p[0]);
        setup();
        start_and_check(p[1], checks);
        std::fflush(stdout);
        _exit(kmk_test::g_case_failures ? 1 : 0);  // this case's checks only (the counters came over from the parent)
    }
    close(p[1]);
    char r = 0;
    const bool returned = read(p[0], &r, 1) == 1 && r == 'R';
    close(p[0]);
    int status = 0;
    waitpid(pid, &status, 0);
    if (!returned) std::printf("  on_ready did not return: it faulted (the sanitizer's report above, or a signal)\n");
    CHECK(returned);
    CHECK(WIFEXITED(status) && WEXITSTATUS(status) == 0);
    kmk_test::end(name);
}

void catalog_warms_up() {
    uint32_t count = 99;
    uint64_t generation = 99;
    CHECK_EQ(F.markers->catalog_count(&count, &generation), KILN_OK);
    CHECK_EQ(count, 0u);
    CHECK_EQ(generation, 0u);
}

}  // namespace

int main() {
    // The lab's dedicated server (every dev.31 lab since 04-markers): kiln.assets loaded, the world lifecycle and the server
    // world's step on. markers.catalog is on and warms up until a verified Kiln client sends its catalog over the link.
    run_case(
        "a dedicated server's on_ready returns and never asks kiln.assets for resources",
        [] { F.side = KILN_SIDE_SERVER; },
        [] {
            CHECK_EQ(F.resources_waits, 0);
            CHECK(row_is("markers.catalog", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE));
            CHECK(row_is("markers.send", KILN_CAP_PENDING, KILN_CAP_REASON_SESSION));
            CHECK(row_is("markers.local", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_WRONG_SIDE));
            // issue 160: the native map tooltip is a client's; a server sends NativeTooltip markers through markers.send
            CHECK(row_is("markers.map_text", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_WRONG_SIDE));
            CHECK(F.rows["markers.map_text"].text.find("markers.send") != std::string::npos);
            CHECK_EQ(F.registered.size(), 2u);
            catalog_warms_up();
        });
    run_case(
        "a dedicated server without kiln.assets: the same",
        [] {
            F.side = KILN_SIDE_SERVER;
            F.assets = false;
        },
        [] {
            CHECK_EQ(F.resources_waits, 0);
            CHECK(row_is("markers.catalog", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE));
            catalog_warms_up();
        });
    run_case(
        "a dedicated server whose server step could not be subscribed: markers.catalog and markers.send are off",
        [] {
            F.side = KILN_SIDE_SERVER;
            F.server_step = false;
        },
        [] {
            CHECK_EQ(F.resources_waits, 0);
            CHECK(row_is("markers.catalog", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SYMBOL_MISSING));
            CHECK(row_is("markers.send", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED));
            uint32_t count = 0;
            uint64_t generation = 0;
            CHECK_EQ(F.markers->catalog_count(&count, &generation), KILN_ERR_UNAVAILABLE);
        });
    // A game client, joined or hosting (a hosting client is KILN_SIDE_CLIENT with its server world's step as well): it reads
    // its own resources, so it registers for them exactly once.
    run_case(
        "a game client (joined or hosting) registers for the game's resources once",
        [] { F.side = KILN_SIDE_CLIENT; },
        [] {
            CHECK_EQ(F.resources_waits, 1);
            CHECK(row_is("markers.catalog", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE));
            CHECK(row_is("markers.local", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE));
            // issue 160: a client's markers.map_text is the surfaces' (with markers.map; a stand-in here), not the glue's
            CHECK(F.rows.count("markers.map_text") == 0);
            catalog_warms_up();
        });
    run_case(
        "a game client without kiln.assets: markers.catalog is off, nothing is registered",
        [] {
            F.side = KILN_SIDE_CLIENT;
            F.assets = false;
        },
        [] {
            CHECK_EQ(F.resources_waits, 0);
            CHECK(row_is("markers.catalog", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SERVICE_MISSING));
        });
    return kmk_test::finish("ready_test");
}
