// SPDX-License-Identifier: MIT
// kiln.repl (native/core/abi/services/kiln_repl.h): the game's ECS replication, read only. This file: the service's
// start (features, symbols, layout), the send-step hook that gives the GameServer, the per-Pre snapshot of which peer
// stands behind each machine, the registry's component facts, and the table. Engine facts: docs/re/network.md (the
// dev.28 replication notes) and the dossier's chapter 11.
#include <windows.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <vector>

#include "kiln_hook.hpp"
#include "repl.h"
#include "services/kiln_caps.h"
#include "services/kiln_repl.h"

namespace repl {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
Offsets L;
Functions F;
std::atomic<uintptr_t> g_world{0};
std::atomic<uintptr_t> g_gs{0};

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n <= 0 || !g_api) return;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(std::min(n, static_cast<int>(sizeof buf) - 1))});
}

bool read_mem(uintptr_t address, void* out, size_t n) {
    if (address < 0x10000 || address > UINTPTR_MAX - n) return false;
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(address), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void describe(uintptr_t where, char* out, size_t cap) {
    HMODULE m = nullptr;
    char path[MAX_PATH] = {};
    if (where > 0xffff &&
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCSTR>(where), &m) &&
        m && GetModuleFileNameA(m, path, sizeof path)) {
        const char* base = std::strrchr(path, '\\');
        std::snprintf(out, cap, "%s+0x%llx", base ? base + 1 : path,
                      static_cast<unsigned long long>(where - reinterpret_cast<uintptr_t>(m)));
        return;
    }
    std::snprintf(out, cap, "0x%llx", static_cast<unsigned long long>(where));
}

namespace {

uint32_t g_side = KILN_SIDE_SERVER;
bool g_observe = false;   // repl.observe resolved and its send-step hook is in
const KilnCapsApi* g_caps = nullptr;

// ---- guards the tick thread flushes ----
std::mutex g_guards_mu;
std::vector<Guard*> g_guards;

// ---- the send step: the GameServer of this frame ----
Guard g_send_step_guard{"send-step"};
kiln::RawHook g_send_step;
using SendStepFn = void (*)(uintptr_t gs);
struct SendStepCall {
    uintptr_t gs;
};
void send_step_before(void* p) { g_gs.store(static_cast<SendStepCall*>(p)->gs, std::memory_order_relaxed); }
void d_send_step(uintptr_t gs) {
    SendStepCall c{gs};
    detour(g_send_step_guard, &c, &send_step_before, nullptr, g_send_step.original<SendStepFn>(), gs);
}

// ---- machine -> peer, snapshot each Pre ----
struct MachinePeer {
    uint32_t peer = 0;
    uint8_t flags = 0;
};
MachinePeer g_peers[KILN_REPL_MACHINES];  // tick thread

void snapshot_peers() {
    const uintptr_t gs = g_gs.load(std::memory_order_relaxed);
    for (uint32_t m = 0; m < KILN_REPL_MACHINES; ++m) {
        MachinePeer p{};
        if (gs && L.gs_machines >= 0)
            p.peer = read<uint32_t>(gs + static_cast<uintptr_t>(L.gs_machines) + m * kMachineSlotStride);
        g_peers[m] = p;
    }
}

// ---- the registry's component facts ----
std::mutex g_registry_mu;            // component_info off the tick thread
Registry* g_registry = nullptr;      // read on the tick thread, published under g_registry_mu
uintptr_t g_registry_world = 0;      // the world it was read from

struct RawRegistry {
    uintptr_t address;
    uint64_t count;
    uintptr_t descriptors, sizes, codecs;
    uint64_t replicated[10], versioned[10];
};

// Copies what one pass needs; no allocation, guarded.
bool read_registry_raw(uintptr_t world, RawRegistry* r) {
    if (!world || L.world_storage <= 0) return false;
    __try {
        r->address = *reinterpret_cast<const uintptr_t*>(world + static_cast<uintptr_t>(L.world_storage));
        if (r->address < 0x10000) return false;
        r->count = *reinterpret_cast<const uint64_t*>(r->address + kRegCount);
        r->descriptors = *reinterpret_cast<const uintptr_t*>(r->address + kRegDescriptors);
        r->sizes = *reinterpret_cast<const uintptr_t*>(r->address + kRegSizes);
        r->codecs = *reinterpret_cast<const uintptr_t*>(r->address + kRegCodecs);
        const uintptr_t rep = r->address + static_cast<uintptr_t>(L.reg_replicated >= 0 ? L.reg_replicated : kRegReplicated);
        std::memcpy(r->replicated, reinterpret_cast<const void*>(rep), sizeof r->replicated);
        std::memcpy(r->versioned, reinterpret_cast<const void*>(r->address + kRegVersioned), sizeof r->versioned);
        return r->count > 0 && r->count <= kMaxComponents && r->descriptors > 0xffff && r->sizes > 0xffff;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

struct RawComponent {
    uint32_t hash;
    uint16_t size;
    uint8_t usage;
    uint8_t custom;
    const char* name;
    uint64_t name_len;
};

bool read_component_raw(const RawRegistry& r, uint32_t i, RawComponent* c) {
    __try {
        const uintptr_t d = r.descriptors + i * kDescStride;
        c->hash = *reinterpret_cast<const uint32_t*>(d + kDescHash);
        c->usage = *reinterpret_cast<const uint8_t*>(d + kDescUsage);
        c->name = *reinterpret_cast<const char* const*>(d + kDescName);
        c->name_len = *reinterpret_cast<const uint64_t*>(d + kDescNameLen);
        c->size = *reinterpret_cast<const uint16_t*>(r.sizes + i * 2);
        c->custom = 0;
        if (r.codecs > 0xffff) {
            const uintptr_t* fns = reinterpret_cast<const uintptr_t*>(r.codecs + i * kCodecStride);
            c->custom = (fns[0] || fns[1] || fns[2] || fns[3]) ? 1 : 0;
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void read_registry(uintptr_t world) {
    RawRegistry raw{};
    if (!read_registry_raw(world, &raw)) return;
    auto* reg = new Registry;
    reg->address = raw.address;
    reg->count = static_cast<uint32_t>(raw.count);
    for (uint32_t i = 0; i < reg->count; ++i) {
        RawComponent c{};
        if (!read_component_raw(raw, i, &c)) {
            delete reg;
            return;
        }
        ComponentFacts& f = reg->at[i];
        f.hash = c.hash;
        f.size = c.size;
        f.usage = c.usage;
        const bool replicated = (raw.replicated[i >> 6] >> (i & 63)) & 1;
        const bool versioned = (raw.versioned[i >> 6] >> (i & 63)) & 1;
        f.flags = (replicated ? KILN_REPL_COMPONENT_REPLICATED : 0u) | (versioned ? KILN_REPL_COMPONENT_VERSIONED : 0u) |
                  (c.custom ? KILN_REPL_COMPONENT_CUSTOM_CODEC : 0u) | ((c.usage & 7) ? KILN_REPL_COMPONENT_CLIENT : 0u) |
                  ((c.usage & 0x38) ? KILN_REPL_COMPONENT_PREDICTION : 0u);
        char name[128] = {};
        if (c.name && c.name_len > 0 && c.name_len < sizeof name && read_mem(reinterpret_cast<uintptr_t>(c.name), name, c.name_len))
            reg->names[i].assign(name, c.name_len);
        else
            reg->names[i] = "#" + std::to_string(i);
    }
    Registry* old = nullptr;
    {
        std::lock_guard lock(g_registry_mu);
        old = g_registry;
        g_registry = reg;
        g_registry_world = world;
    }
    delete old;
    uint32_t replicated = 0, custom = 0;
    for (uint32_t i = 0; i < reg->count; ++i) {
        replicated += (reg->at[i].flags & KILN_REPL_COMPONENT_REPLICATED) != 0;
        custom += (reg->at[i].flags & KILN_REPL_COMPONENT_CUSTOM_CODEC) != 0;
    }
    logf(KILN_LOG_INFO, "repl: registry of world 0x%llx: %u components, %u with the replication bit, %u with a codec of their own",
         static_cast<unsigned long long>(world), reg->count, replicated, custom);
}

void flush_guards() {
    std::vector<Guard*> guards;
    {
        std::lock_guard lock(g_guards_mu);
        guards = g_guards;
    }
    flush_faults(guards.data(), guards.size(), [](uintptr_t where, char* out, size_t cap) { describe(where, out, cap); },
                 [](const char* line) { logf(KILN_LOG_WARN, "%s", line); });
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    const uintptr_t world = g_api->server_world();
    const uintptr_t was = g_world.exchange(world, std::memory_order_relaxed);
    if (world && (world != was || world != g_registry_world)) read_registry(world);
    flush_guards();
    if (world) snapshot_peers();
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (!ev || ev->kind != KILN_WORLD_SERVER || ev->event != KILN_WORLD_STOPPING) return;
    g_world.store(0, std::memory_order_relaxed);
    g_gs.store(0, std::memory_order_relaxed);
}

// ---- the table ----
uint32_t KILN_CALL t_capabilities() { return g_observe ? KILN_REPL_CAP_OBSERVE : 0u; }

KilnResult KILN_CALL t_stats_enable(KilnOwner, bool) { return KILN_ERR_UNAVAILABLE; }  // not built yet
KilnResult KILN_CALL t_peer_stats(KilnReplPeerStats*, uint32_t, uint32_t* count) {
    if (count) *count = 0;
    return KILN_ERR_UNAVAILABLE;
}
KilnResult KILN_CALL t_watch(KilnOwner, KilnEntity, uint32_t, uint32_t, KilnReplWatchFn, void*, KilnHandle* out) {
    if (out) *out = 0;
    return KILN_ERR_UNAVAILABLE;
}
KilnResult KILN_CALL t_remove(KilnHandle h) { return h == 0 ? KILN_ERR_INVALID_ARGUMENT : KILN_ERR_NOT_FOUND; }
KilnResult KILN_CALL t_capture(KilnOwner, uint32_t, uint32_t, KilnStr) { return KILN_ERR_UNAVAILABLE; }

KilnResult KILN_CALL t_component_info(uint32_t hash, KilnReplComponentInfo* out) {
    if (!out || out->size < sizeof(KilnReplComponentInfo)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_registry_mu);
    if (!g_registry) return KILN_ERR_UNAVAILABLE;
    for (uint32_t i = 0; i < g_registry->count; ++i) {
        const ComponentFacts& f = g_registry->at[i];
        if (f.hash != hash) continue;
        out->hash = hash;
        out->index = static_cast<uint16_t>(i);
        out->data_size = f.size;
        out->flags = f.flags;
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}

const KilnReplApi kTable = {{sizeof(KilnReplApi), KILN_REPL_VERSION}, &t_capabilities, &t_stats_enable, &t_peer_stats,
                            &t_watch, &t_remove, &t_component_info, &t_capture};

// ---- start ----
bool sym(const char* name, uintptr_t* out) { return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feature(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

// Why a capability row is off, in its own words (kiln.caps), or `fallback`.
std::string row_text(const char* name, const char* fallback) {
    if (g_caps) {
        KilnCapRow row{};
        row.size = sizeof row;
        if (g_caps->find(KilnStr{name, std::strlen(name)}, &row) == KILN_OK && row.state != KILN_CAP_AVAILABLE && row.text.len)
            return std::string(row.text.ptr, row.text.len);
    }
    return fallback;
}

void report_off(const char* name, uint32_t reason, const std::string& text) {
    if (g_caps)
        g_caps->report(g_owner, KilnStr{name, std::strlen(name)}, KILN_CAP_UNAVAILABLE, reason, KilnStr{text.data(), text.size()});
}

bool resolve_observe() {
    if (!feature("repl.observe")) return false;
    return sym("repl/SendStep", &F.send_step) && sym("repl/SendCallback", &F.send_callback) &&
           sym("repl/EntityWriter", &F.entity_writer) && sym("repl/DeltaWriter", &F.delta_writer) &&
           sym("repl/StateIndex", &F.state_index) && sym("repl/AckReceive", &F.ack_receive) &&
           sym("repl/EcsReset", &F.ecs_reset) && cst("repl/world.objects", &L.world_objects) &&
           cst("repl/obj.stride", &L.obj_stride) && cst("repl/obj.client_ctx", &L.obj_client_ctx) &&
           cst("repl/obj.pred_ctx", &L.obj_pred_ctx) && cst("repl/world.tick", &L.world_tick) &&
           cst("repl/obj.count", &L.obj_count) && cst("net/gs.machines", &L.gs_machines) && L.obj_count == KILN_REPL_MACHINES;
}

}  // namespace

void add_guard(Guard* g) {
    std::lock_guard lock(g_guards_mu);
    if (std::find(g_guards.begin(), g_guards.end(), g) == g_guards.end()) g_guards.push_back(g);
}

const Registry* registry() {
    std::lock_guard lock(g_registry_mu);
    return g_registry;
}

uint32_t peer_of(uint32_t machine, uint8_t* flags) {
    if (flags) *flags = 0;
    if (machine >= KILN_REPL_MACHINES) return 0;
    if (flags) *flags = g_peers[machine].flags;
    return g_peers[machine].peer;
}

}  // namespace repl

using namespace repl;

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, session) + sizeof(api->session))
        return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_REPL_SERVICE, sizeof(KILN_REPL_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    g_side = api->side();
    g_caps = static_cast<const KilnCapsApi*>(api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
    cst("world.storage", &L.world_storage);
    cst("world/registry.replicated", &L.reg_replicated);
    add_guard(&g_send_step_guard);

    // repl.observe: the send step's hook gives the GameServer (machine slots, per-Pre peer snapshot); the stats and
    // watch hooks install on demand.
    if (!resolve_observe()) {
        logf(KILN_LOG_INFO, "repl: observe off (%s)", row_text("repl.observe", "feature repl.observe is off on this build").c_str());
    } else if (!g_send_step.install(api, owner, F.send_step, reinterpret_cast<void*>(&d_send_step), "repl/SendStep")) {
        logf(KILN_LOG_WARN, "repl: observe off (the send step could not be hooked: %s)", g_send_step.error());
        report_off("repl.observe", KILN_CAP_REASON_FAULTED, std::string("the send step could not be hooked: ") + g_send_step.error());
    } else {
        g_observe = true;
        logf(KILN_LOG_INFO, "repl: observe on (send step hooked; per-peer stats and watches install their hooks when asked)");
    }
    // repl.client: a Kiln client's receive side.
    if (g_side == KILN_SIDE_CLIENT) {
        logf(KILN_LOG_INFO, "repl: client off (the receive side is not built in this version)");
        report_off("repl.client", KILN_CAP_REASON_SERVICE_OLDER, "kiln.repl has no client receive side in this version");
    } else {
        logf(KILN_LOG_INFO, "repl: client off (a game client's receive side; this is a server)");
    }

    logf(KILN_LOG_INFO, "repl: capabilities() = 0x%x", t_capabilities());

    KilnHandle h = 0;
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    return api->register_service(owner, KilnStr{KILN_REPL_SERVICE, sizeof(KILN_REPL_SERVICE) - 1}, &kTable);
}
