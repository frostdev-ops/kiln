// SPDX-License-Identifier: MIT
// kiln.markers (services/kiln_markers.h): session-only markers on the game's map, Kiln's compass strip and world labels,
// and the internal kiln.markers.bridge the managed built-in receiver uses. Dossier chapter 04; docs/kiln/services/markers.md.
//
// This file is the glue to the core (glue.h): it publishes both tables and the capability rows, feeds the store (store.h)
// and the parts (anchors, surfaces, link) the world lifecycle and each world's Post, and delivers the queued notifications
// at each step (and on the stopping thread when a world stops).
#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <initializer_list>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_assets.h"
#include "services/kiln_caps.h"
#include "services/kiln_markers.h"

#include "arena.h"
#include "catalog.h"
#include "events.h"
#include "glue.h"
#include "store.h"

namespace {
const KilnCapsApi* g_caps = nullptr;
void log_line(int level, const char* text);
kmk::Arena g_arena;
kmk::Switches g_switches;
kmk::Store g_store(g_arena, g_switches);
kmk::Subscribers g_subs(&log_line);
kmk::CatalogHolder g_catalog;
}  // namespace

namespace kmk {

Glue g;

void logf(int32_t level, const char* fmt, ...) {
    if (!g.api) return;
    char text[768];
    va_list v;
    va_start(v, fmt);
    const int n = std::vsnprintf(text, sizeof text, fmt, v);
    va_end(v);
    if (n > 0) g.api->log(g.owner, level, KilnStr{text, static_cast<size_t>(n < static_cast<int>(sizeof text) ? n : static_cast<int>(sizeof text) - 1)});
}

void report(const char* name, uint32_t state, uint32_t reason, const char* text) {
    if (!g_caps || !g_caps->report) return;
    g_caps->report(g.owner, KilnStr{name, std::strlen(name)}, state, reason, KilnStr{text, std::strlen(text)});
}

}  // namespace kmk

namespace {

using kmk::g;
using kmk::logf;
using kmk::report;

#define KM_STR(s) KilnStr{s, sizeof(s) - 1}

void log_line(int level, const char* text) {
    if (g.api) g.api->log(g.owner, level, KilnStr{text, std::strlen(text)});
}

const KilnAssetsApi* g_assets = nullptr;
bool g_client_post = false;  // the client world's Post is ours
bool g_server_post = false;  // the server world's Post is ours

// ---- capability rows (chapter 04 §5) ------------------------------------------------------------------------------------

constexpr char kLocal[] = "markers.local";
constexpr char kCatalog[] = "markers.catalog";
constexpr char kMapText[] = "markers.map_text";

// markers.catalog: a client reads the game's resources through kiln.assets; a dedicated server has none and takes the
// catalog from a verified client over the marker link (not built yet).
std::atomic<bool> g_catalog_reader{false};
const char* g_catalog_off = "";
uint32_t g_catalog_off_reason = KILN_CAP_REASON_POLICY;

// The rows of the glue; each part reports its own (anchors_ready, surfaces_ready, link_ready).
void report_rows() {
    if (g_catalog_reader)
        report(kCatalog, KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "");
    else
        report(kCatalog, KILN_CAP_UNAVAILABLE, g_catalog_off_reason, g_catalog_off);
    // markers.map_text (issue 160): a client's row is the surfaces' (it needs markers.map and markers.map_tooltip_text)
    if (g.side != KILN_SIDE_CLIENT)
        report(kMapText, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_WRONG_SIDE,
               "the native map tooltip is a game client's; a server sends NativeTooltip markers (markers.send) and each "
               "player's Kiln shows the text where its client can (that player's markers.map_text)");
    if (g.side != KILN_SIDE_CLIENT)
        report(kLocal, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_WRONG_SIDE, "local markers are a game client's; a server sends them (markers.send)");
    else if (!g.lifecycle)
        report(kLocal, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SYMBOL_MISSING, "needs world.lifecycle (a client's markers end with its world)");
    else if (!g_client_post)
        report(kLocal, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, "the client world's step could not be subscribed");
    else
        report(kLocal, KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "");
}

// ---- results ----------------------------------------------------------------------------------------------------------

bool result_ok(const KilnMarkerResult* r) { return r && r->size >= sizeof(KilnMarkerResult); }

void clear_result(KilnMarkerResult* r) {
    const uint32_t size = r->size;
    std::memset(r, 0, sizeof(KilnMarkerResult));
    r->size = size;
}


// KILN_ERR_UNAVAILABLE with the row's name as the reason (header: "Results")
KilnResult off(KilnMarkerResult* r, const char* capability) {
    if (result_ok(r)) {
        clear_result(r);
        std::snprintf(r->reason, sizeof r->reason, "%s", capability);
    }
    return KILN_ERR_UNAVAILABLE;
}

// ---- delivery -----------------------------------------------------------------------------------------------------------

// The role's queued notes, then `extra` (the surfaces' visibility notes), as one batch.
void dispatch(uint32_t role, std::vector<kmk::Note>* extra = nullptr) {
    g_subs.dispatch([role, extra] {
        auto batch = g_store.take(role);
        if (extra) batch.insert(batch.end(), extra->begin(), extra->end());
        return batch;
    });
}

void KILN_CALL on_world(const KilnWorldEvent* e, void*) {
    if (!e || (e->kind != KILN_WORLD_SERVER && e->kind != KILN_WORLD_CLIENT)) return;
    const uint32_t role = e->kind == KILN_WORLD_CLIENT ? kmk::kRoleClient : kmk::kRoleServer;
    if (e->event == KILN_WORLD_STARTED) {
        g_store.world_started(e->kind);
        kmk::anchors_world(*e);
        kmk::link_world(*e);
    } else if (e->event == KILN_WORLD_STOPPING) {
        // anchors end first (their storage can be reused), then the link and the surfaces; records end with their world and
        // their last notes go out before the stop returns, on this (stopping) thread
        kmk::anchors_world(*e);
        kmk::link_world(*e);
        std::vector<kmk::Note> notes;
        kmk::surfaces_world(*e, &notes);
        g_store.world_stopping(e->kind);
        dispatch(role, &notes);
    }
}

// ---- the icon catalog (client) ------------------------------------------------------------------------------------------

bool read_asset(const uint8_t guid[16], uint32_t type, size_t limit, std::vector<uint8_t>* out, void*) {
    KilnAssetKey key{};
    std::memcpy(key.guid, guid, 16);
    key.type = type;
    key.part = 0;
    size_t len = 0;
    KilnResult rc = g_assets->resource_read(&key, nullptr, 0, &len);
    if (rc != KILN_ERR_BUFFER_TOO_SMALL && rc != KILN_OK) return false;
    if (len > limit) return false;
    out->resize(len);
    if (!len) return true;
    rc = g_assets->resource_read(&key, out->data(), out->size(), &len);
    return rc == KILN_OK && len == out->size();
}

void post_catalog(uint32_t ready, uint32_t count, uint64_t generation, const std::string& reason) {
    KilnMarkerCatalogChange c{};
    c.size = sizeof c;
    c.ready = ready;
    c.count = count;
    c.generation = generation;
    std::snprintf(c.reason, sizeof c.reason, "%s", reason.c_str());
    g_store.post_catalog(kmk::kRoleClient, c);
}

// when_resources_ready: on the game's thread, once its resource service is up (before the game read anything).
void KILN_CALL on_resources_ready(void*) {
    std::string why;
    auto c = kmk::read_catalog(&read_asset, nullptr, &why);
    if (!c) {
        logf(KILN_LOG_WARN, "markers: the icon catalog could not be read: %s; markers.catalog is off", why.c_str());
        g_catalog_reader = false;
        g_catalog_off_reason = KILN_CAP_REASON_FAULTED;
        report(kCatalog, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, ("the game's marker types could not be read: " + why).c_str());
        post_catalog(0, 0, 0, why);
        return;
    }
    const kmk::BuildPair* pair = kmk::pair_of_client(g.exe);
    const bool verified = pair && c->digest_hex() == pair->catalog_sha256;
    std::string reason;
    if (!verified)
        reason = pair ? "native map icons stay off: the game's marker types differ from the verified catalog of this build"
                      : "native map icons stay off: no verified catalog for this game build (a game update needs a new build pair)";
    const uint32_t count = static_cast<uint32_t>(c->rows.size()), map = c->map_rows();
    const std::string digest = c->digest_hex();
    g_catalog.publish(std::move(c), verified, reason);
    logf(KILN_LOG_INFO, "markers: icon catalog: %u marker types with an icon, %u for the map, sha256 %s (%s)", count, map, digest.c_str(),
         verified ? "the verified pair of this build" : reason.c_str());
    post_catalog(1, count, g_catalog.generation(), reason);
}

// Once a second the client's marker metadata is read again: when it differs from the published catalog (another mod
// replaced the registry, a hot reload), native map icons stay off until the game restarts (chapter 04 §4.3).
std::chrono::steady_clock::time_point g_next_check{};

void recheck_catalog() {
    const auto now = std::chrono::steady_clock::now();
    if (now < g_next_check) return;
    g_next_check = now + std::chrono::seconds(1);
    const auto c = g_catalog.get();
    if (!c || g_catalog.changed()) return;
    std::string why;
    const auto fresh = kmk::read_catalog(&read_asset, nullptr, &why);
    if (fresh && fresh->digest == c->digest) return;
    const std::string reason = fresh ? "native map icons are off until the game restarts: the game's marker types changed (sha256 " + fresh->digest_hex() + ")"
                                     : "native map icons are off until the game restarts: the game's marker types can no longer be read (" + why + ")";
    g_catalog.latch_changed(reason);
    logf(KILN_LOG_WARN, "markers: %s", reason.c_str());
}

void KILN_CALL on_client_post(const KilnTickInfo*, void*) {
    recheck_catalog();
    kmk::anchors_post(KILN_WORLD_CLIENT);
    kmk::link_client_post();
    std::vector<kmk::Note> notes;
    kmk::surfaces_client_post(&notes);
    dispatch(kmk::kRoleClient, &notes);
}

void KILN_CALL on_server_post(const KilnTickInfo*, void*) {
    kmk::anchors_post(KILN_WORLD_SERVER);
    kmk::link_server_post();
    dispatch(kmk::kRoleServer);
}

// ---- kiln.markers v1 ----------------------------------------------------------------------------------------------------

KilnResult KILN_CALL t_bind_owner(KilnOwner owner, KilnStr ns) { return g_store.bind(owner, ns); }
KilnResult KILN_CALL t_release_owner(KilnOwner owner) {
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    g_subs.remove_owner(owner);  // its callbacks first: what follows is for the others
    kmk::link_release_owner(owner);
    const KilnResult rc = g_store.release(owner);
    kmk::anchors_release_owner(owner);  // after the store dropped the records' references
    return rc;
}
KilnResult KILN_CALL t_catalog_count(uint32_t* count, uint64_t* generation) {
    if (!count || !generation) return KILN_ERR_INVALID_ARGUMENT;
    *count = 0;
    *generation = 0;
    if (!g_catalog_reader) return KILN_ERR_UNAVAILABLE;
    if (const auto c = g_catalog.get()) {
        *count = static_cast<uint32_t>(c->rows.size());
        *generation = g_catalog.generation();
    }
    return KILN_OK;
}
KilnResult KILN_CALL t_catalog_at(uint32_t index, KilnMarkerIcon* out) {
    if (!out || out->size < sizeof(KilnMarkerIcon)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_catalog_reader) return KILN_ERR_UNAVAILABLE;
    const auto c = g_catalog.get();
    if (!c || index >= c->rows.size()) return KILN_ERR_NOT_FOUND;
    kmk::fill_icon(c->rows[index], out);
    return KILN_OK;
}
KilnResult KILN_CALL t_catalog_resolve(KilnStr name, KilnMarkerIcon* out, KilnMarkerResult* r) {
    if (!result_ok(r) || !out || out->size < sizeof(KilnMarkerIcon)) return KILN_ERR_INVALID_ARGUMENT;
    clear_result(r);
    if (!g_catalog_reader) return off(r, kCatalog);
    if (!name.ptr && name.len) return KILN_ERR_INVALID_ARGUMENT;
    const std::string_view n = name.len ? std::string_view(name.ptr, name.len) : std::string_view();
    const auto c = g_catalog.get();
    const kmk::IconRow* row = nullptr;
    uint32_t matches = 0;
    if (!c) {
        // the syntax is checked first, also while warming up
        kmk::Catalog empty;
        if (kmk::resolve(empty, n, &row, &matches) == kmk::Resolved::BadSyntax) {
            std::snprintf(r->reason, sizeof r->reason, "an icon name is 1 to 128 printable ASCII characters: a canonical key (type: and eight hex digits) or an alias");
            return KILN_ERR_INVALID_ARGUMENT;
        }
        r->status = KILN_MARKER_CATALOG_NOT_READY;
        std::snprintf(r->reason, sizeof r->reason, "CatalogNotReady: the icon catalog is still warming up");
        return KILN_OK;
    }
    switch (kmk::resolve(*c, n, &row, &matches)) {
        case kmk::Resolved::Ok:
            kmk::fill_icon(*row, out);
            r->status = KILN_MARKER_ACCEPTED;
            return KILN_OK;
        case kmk::Resolved::NotFound:
            r->status = KILN_MARKER_NOT_FOUND;
            std::snprintf(r->reason, sizeof r->reason, "NotFound: no icon named '%.*s'", static_cast<int>(n.size()), n.data());
            return KILN_OK;
        case kmk::Resolved::Ambiguous:
            r->status = KILN_MARKER_AMBIGUOUS;
            std::snprintf(r->reason, sizeof r->reason, "Ambiguous: '%.*s' is the alias of %u icons; use a canonical key (type:...)",
                          static_cast<int>(n.size()), n.data(), matches);
            return KILN_OK;
        case kmk::Resolved::BadSyntax: break;
    }
    std::snprintf(r->reason, sizeof r->reason, "an icon name is 1 to 128 printable ASCII characters: a canonical key (type: and eight hex digits) or an alias");
    return KILN_ERR_INVALID_ARGUMENT;
}
KilnResult KILN_CALL t_world_epoch(uint32_t kind, uint64_t* epoch) { return g_store.world_epoch(kind, epoch); }
KilnResult KILN_CALL t_capture_anchor(KilnOwner owner, uint32_t kind, uint64_t epoch, KilnEntity entity, KilnMarkerAnchorResult* out) {
    return kmk::anchors_capture(owner, kind, epoch, entity, out);
}
KilnResult KILN_CALL t_release_anchor(KilnOwner owner, KilnMarkerAnchor anchor) { return kmk::anchors_release(owner, anchor); }
KilnResult KILN_CALL t_set_local(KilnOwner owner, const KilnMarkerSpec* spec, KilnMarkerResult* r) { return g_store.set_local(owner, spec, r); }
KilnResult KILN_CALL t_remove_local(KilnOwner owner, KilnStr key, KilnMarkerResult* r) { return g_store.remove_local(owner, key, r); }
KilnResult KILN_CALL t_clear_local(KilnOwner owner, KilnMarkerResult* r) { return g_store.clear_local(owner, r); }
KilnResult KILN_CALL t_send(KilnOwner owner, KilnEntity to, const KilnMarkerSpec* spec, KilnMarkerResult* r) { return kmk::link_send(owner, to, spec, r); }
KilnResult KILN_CALL t_unsend(KilnOwner owner, KilnEntity to, KilnStr key, KilnMarkerResult* r) { return kmk::link_unsend(owner, to, key, r); }
KilnResult KILN_CALL t_clear_sent(KilnOwner owner, KilnEntity to, KilnMarkerResult* r) { return kmk::link_clear_sent(owner, to, r); }
KilnResult KILN_CALL t_suppress_sent(KilnOwner owner, KilnEntity to, KilnStr key, KilnMarkerResult* r) {
    return kmk::link_suppress_sent(owner, to, key, r);
}
KilnResult KILN_CALL t_subscribe(KilnOwner owner, KilnMarkerChangedFn changed, KilnMarkerVisibilityFn visibility, KilnMarkerCatalogFn catalog,
                                 void* user, KilnHandle* out) {
    return g_subs.subscribe(owner, changed, visibility, catalog, user, out);
}
KilnResult KILN_CALL t_unsubscribe(KilnHandle handle) { return g_subs.unsubscribe(handle); }

const KilnMarkersApi kTable = {{sizeof(KilnMarkersApi), KILN_MARKERS_VERSION},
                               &t_bind_owner, &t_release_owner, &t_catalog_count, &t_catalog_at, &t_catalog_resolve,
                               &t_world_epoch, &t_capture_anchor, &t_release_anchor, &t_set_local, &t_remove_local,
                               &t_clear_local, &t_send, &t_unsend, &t_clear_sent, &t_suppress_sent, &t_subscribe,
                               &t_unsubscribe};


void KILN_CALL on_ready(void*) {
    g_caps = static_cast<const KilnCapsApi*>(g.api->service(KM_STR(KILN_CAPS_SERVICE), KILN_CAPS_VERSION));
    if (g_caps && g_caps->header.size < sizeof(KilnCapsApi)) g_caps = nullptr;
    g_store.set_icon_check(&kmk::CatalogHolder::check, &g_catalog);
    if (g.side != KILN_SIDE_CLIENT) {
        // the catalog comes from the first verified Kiln client over the marker link: the row is on while the link can run,
        // and the catalog warms up (count 0, Markers.Icons.IsReady false) until that client's catalog is accepted
        g_catalog_reader = g.lifecycle && g.server_post;
        g_catalog_off = "a dedicated server has no marker resources and takes the catalog over the marker link, which needs the world lifecycle and the server world's step";
        g_catalog_off_reason = KILN_CAP_REASON_SYMBOL_MISSING;
    } else {
        // kiln.assets v1 with resource_read and the appended when_resources_ready (header.size covers it)
        g_assets = static_cast<const KilnAssetsApi*>(g.api->service(KM_STR(KILN_ASSETS_SERVICE), KILN_ASSETS_VERSION));
        constexpr size_t need = offsetof(KilnAssetsApi, when_resources_ready) + sizeof(g_assets->when_resources_ready);
        if (!g_assets || g_assets->header.size < need || !g_assets->resource_read || !g_assets->when_resources_ready) {
            g_assets = nullptr;
            g_catalog_off = "needs kiln.assets (resource_read and when_resources_ready)";
            g_catalog_off_reason = KILN_CAP_REASON_SERVICE_MISSING;
        } else {
            g_catalog_reader = true;
        }
    }
    report_rows();
    kmk::anchors_ready();
    kmk::surfaces_ready();
    kmk::link_ready();
    // Only a game client reads the catalog from its own resources (g_assets is set in its branch above). A dedicated
    // server's catalog reader is the marker link: g_catalog_reader is on there with g_assets null, nothing to register.
    if (g_assets && g_catalog_reader && g_assets->when_resources_ready(&on_resources_ready, nullptr) != KILN_OK) {
        g_catalog_reader = false;
        logf(KILN_LOG_WARN, "markers: kiln.assets' resources are off on this build: markers.catalog is off");
        report(kCatalog, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SERVICE_MISSING, "kiln.assets' resources are off on this build");
    }
}

}  // namespace

// The link took a verified client's catalog on a dedicated server (glue.h): from here markers.catalog answers, and the
// server's subscribers get CatalogChanged (role 0) at the next server step.
void kmk::catalog_from_link() {
    static std::atomic<bool> s_once{false};
    const auto c = g_catalog.get();
    if (!c || s_once.exchange(true)) return;
    g_catalog_reader = true;
    report(kCatalog, KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "");
    KilnMarkerCatalogChange ch{};
    ch.size = sizeof ch;
    ch.ready = 1;
    ch.count = static_cast<uint32_t>(c->rows.size());
    ch.generation = g_catalog.generation();
    g_store.post_catalog(kmk::kRoleServer, ch);
    logf(KILN_LOG_INFO, "markers: icon catalog: %u marker types with an icon, %u for the map, sha256 %s (from a verified Kiln client)", ch.count,
         c->map_rows(), c->digest_hex().c_str());
}

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g.api = api;
    g.owner = owner;
    g.store = &g_store;
    g.subs = &g_subs;
    g.catalog = &g_catalog;
    g.sw = &g_switches;
    info->id = KM_STR(KILN_MARKERS_SERVICE);
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    g.side = api->side();
    KilnBuildInfo build{};
    build.size = sizeof build;
    if (api->build_info(&build) == KILN_OK) g.exe.assign(build.exe_sha256, strnlen(build.exe_sha256, sizeof build.exe_sha256));
    // Session only: every store is bound to a world, so without the lifecycle there is no store (chapter 04 §4.2).
    KilnHandle h = 0;
    g.lifecycle = api->feature && api->feature(KM_STR("world.lifecycle")) && api->subscribe_world(owner, &on_world, nullptr, &h) == KILN_OK;
    if (!g.lifecycle) logf(KILN_LOG_WARN, "markers: the world lifecycle is unavailable on this build: markers.local and markers.send are off");
    if (g.side == KILN_SIDE_CLIENT) {
        KilnHandle p = 0;
        g_client_post = api->subscribe_world_tick(owner, KILN_WORLD_CLIENT, KILN_TICK_POST, &on_client_post, nullptr, &p) == KILN_OK;
        g.client_post = g_client_post;
        if (!g_client_post) logf(KILN_LOG_WARN, "markers: the client world's step could not be subscribed: markers.local is off");
    }
    // the server world's Post: a dedicated server, and the server world of a hosting client
    KilnHandle sp = 0;
    g_server_post = api->subscribe_world_tick(owner, KILN_WORLD_SERVER, KILN_TICK_POST, &on_server_post, nullptr, &sp) == KILN_OK;
    g.server_post = g_server_post;
    if (!g_server_post) logf(KILN_LOG_WARN, "markers: the server world's step could not be subscribed: markers.send is off");
    g.server_post = g_server_post;
    g_switches.local = g.side == KILN_SIDE_CLIENT && g.lifecycle && g_client_post;
    kmk::anchors_start();
    KilnResult rc = api->register_service(owner, KM_STR(KILN_MARKERS_SERVICE), &kTable);
    if (rc != KILN_OK) return rc;
    rc = api->register_service(owner, KM_STR(KILN_MARKERS_BRIDGE_SERVICE), &kmk::kBridgeTable);
    if (rc != KILN_OK) logf(KILN_LOG_WARN, "markers: kiln.markers.bridge was not registered (%d): the built-in receiver cannot attach", rc);
    return KILN_OK;
}
