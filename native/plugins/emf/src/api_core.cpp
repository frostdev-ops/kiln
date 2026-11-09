// SPDX-License-Identifier: MIT
// The EmfApi table, part 1: log, worlds, components, entities, ticks (on_tick / run_on_world), session, build, mod
// info. EMF's threading rule ("after the world's step, on its thread, no game jobs running") is Kiln's Post phase:
// the server world's Post runs after WaitWorldJobs; the client and prediction worlds finish their jobs inside the step.
#include <intrin.h>

#include <algorithm>
#include <cstdio>

#include "emf_internal.h"
#include "services/kiln_types.h"
#include "nlohmann/json.hpp"

namespace kemf {

namespace {

EmfApi g_table{};

// ---------------------------------------------------------------------------------------------- layout --------
struct Layout {
    bool loaded = false;
    int64_t storage = -1, kind = -1, clock = -1, clock_period = -1;
    // entity map (keen hash map inside the storage); see docs/re/world.md "Entity map"
    int64_t ent = -1, ent_cap = -1, ent_values = -1, ent_bits = -1, ent_keys = -1;
    bool map_ok = false;
    uintptr_t get_component = 0, get_template = 0, type_by_hash = 0;
    int64_t type_size = -1;
};
Layout L;
std::once_flag g_layout_once;

int64_t cst(const char* name) {
    int64_t v = -1;
    return g_api->constant(kstr(name), &v) == KILN_OK ? v : -1;
}
uintptr_t sym(const char* name) {
    uintptr_t v = 0;
    return g_api->symbol(kstr(name), &v) == KILN_OK ? v : 0;
}

void load_layout() {
    L.storage = cst("world.storage");
    L.kind = cst("storage.kind");
    L.clock = cst("storage.clock");
    L.clock_period = cst("storage.clock_period");
    L.get_component = sym("GetComponent");
    L.get_template = sym("GetTemplateComponent");
    L.type_by_hash = sym("settings/TypeByHash");
    if (!L.type_by_hash) L.type_by_hash = sym("content/TypeByHash");
    L.type_size = cst("settings/type.size");
    if (L.type_size < 0) L.type_size = 64;  // keen reflection record: u32 size at +0x40 (docs/re/settings.md)
    const int64_t ent = cst("world/storage.entities"), cap = cst("world/storage.entities.cap"),
                  val = cst("world/storage.entities.values");
    const int64_t s_cap = cst("world/registry.singletons.cap"), s_bits = cst("world/registry.singletons.bits"),
                  s_keys = cst("world/registry.singletons.keys"), s_val = cst("world/registry.singletons.values");
    // The same derivation as kiln.world: the entity map gives capacity/values, the singleton map (same hash-map
    // type) gives bits/keys, and both must agree on values.
    if (L.storage >= 0 && ent >= 0 && cap > ent && val > ent && s_cap >= 0 && s_bits >= 0 && s_keys >= 0 && s_val >= 0) {
        const int64_t m_cap = cap - ent, m_val = val - ent;
        const int64_t reg = s_cap - m_cap;
        if (s_val - reg == m_val) {
            L.ent = ent;
            L.ent_cap = m_cap;
            L.ent_values = m_val;
            L.ent_bits = s_bits - reg;
            L.ent_keys = s_keys - reg;
            L.map_ok = L.ent_bits >= 0 && L.ent_keys >= 0;
        }
    }
    L.loaded = true;
    logf(KILN_LOG_INFO, "layout: storage 0x%llx, entity map %s, GetComponent %s, reflection %s",
         static_cast<unsigned long long>(L.storage), L.map_ok ? "ok" : "not confirmed (for_each_entity is off)",
         L.get_component ? "ok" : "missing", L.type_by_hash ? "ok" : "missing");
}
const Layout& layout() {
    std::call_once(g_layout_once, load_layout);
    return L;
}

// ---------------------------------------------------------------------------------------------- ticks ---------
struct TickReg {
    Mod* mod;
    EmfWorldCallback cb;
    void* user;
    std::atomic<bool> dead{false};
};
struct RunReg {
    Mod* mod;
    uint8_t kind;
    EmfWorldCallback cb;
    void* user;
};
std::mutex g_tick_mutex;
std::vector<std::unique_ptr<TickReg>> g_ticks;
std::vector<RunReg> g_runs;

struct WorldCall {
    EmfWorldCallback cb;
    EmfWorld* w;
    void* user;
};
void world_thunk(void* p) {
    auto* c = static_cast<WorldCall*>(p);
    c->cb(c->w, c->user);
}

// ---------------------------------------------------------------------------------------------- API ------------
void api_log(const char* mod, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    const std::string text = vformat(fmt ? fmt : "", ap);
    va_end(ap);
    if (Mod* m = mod ? find_mod(mod) : nullptr) {
        mod_log(m, KILN_LOG_INFO, text);
        return;
    }
    Mod* c = caller_mod(_ReturnAddress());
    mod_log(c, KILN_LOG_INFO, std::string("[") + (mod ? mod : "?") + "] " + text);
}

EmfWorld* api_server_world() { return world_of_kind(KILN_WORLD_SERVER); }
EmfWorld* api_client_world() { return world_of_kind(KILN_WORLD_CLIENT); }
uint8_t api_world_kind(EmfWorld* w) { return world_kind_of(w); }

uint32_t api_world_tick(EmfWorld* w) {
    const Layout& l = layout();
    if (!w || l.storage < 0 || l.clock < 0) return 0;
    uint32_t v = 0;
    return seh_read_u32(reinterpret_cast<uintptr_t>(w) + static_cast<uintptr_t>(l.storage + l.clock), &v) ? v : 0;
}

int64_t api_ecs_time_ns(EmfWorld* w) {
    const Layout& l = layout();
    if (!w || l.storage < 0 || l.clock < 0 || l.clock_period < 0) return 0;
    uint32_t tick = 0;
    uint64_t period = 0;
    const uintptr_t s = reinterpret_cast<uintptr_t>(w) + static_cast<uintptr_t>(l.storage);
    if (!seh_read_u32(s + static_cast<uintptr_t>(l.clock), &tick) ||
        !seh_read_u64(s + static_cast<uintptr_t>(l.clock_period), &period))
        return 0;
    return static_cast<int64_t>(period) * static_cast<int64_t>(tick);
}

void* api_singleton(EmfWorld* w, const char* name) {
    if (!w || !name) return nullptr;
    return reinterpret_cast<void*>(g_api->singleton(reinterpret_cast<uintptr_t>(w), kstr(name)));
}

uint32_t api_local_player() {
    if (!S.client) return 0;
    KilnClientState st{};
    st.size = sizeof st;
    return S.client->state(&st) == KILN_OK ? st.local_player : 0;
}

void* api_get_component(EmfWorld* w, uint32_t hash, uint32_t entity, uint64_t* size_out) {
    if (size_out) *size_out = 0;
    return const_cast<void*>(component_of(w, hash, entity, size_out));
}

void api_for_each_entity(EmfWorld* w, EmfEntityCallback cb, void* user) {
    Mod* caller = caller_mod(_ReturnAddress());
    if (!w || !cb) return;
    const Layout& l = layout();
    if (!l.map_ok) {
        unsupported(caller, "for_each_entity", "the entity map is not confirmed on this build");
        return;
    }
    if (!on_thread_of(w)) {
        refuse(caller, "for_each_entity", "not on the world's thread (call it inside a world callback)", true);
        return;
    }
    // The layout comes from this side's own sigdb (client.d composes server.d/world.json: the same storage code), so it
    // holds for the client and prediction worlds too.
    const uintptr_t map = reinterpret_cast<uintptr_t>(w) + static_cast<uintptr_t>(l.storage + l.ent);
    uint64_t cap = 0, bits = 0, keys = 0;
    if (!seh_read_u64(map + static_cast<uintptr_t>(l.ent_cap), &cap) ||
        !seh_read_u64(map + static_cast<uintptr_t>(l.ent_bits), &bits) ||
        !seh_read_u64(map + static_cast<uintptr_t>(l.ent_keys), &keys) || cap == 0 || (cap & (cap - 1)) ||
        cap > (1u << 24) || !bits || !keys)
        return;
    std::vector<uint64_t> words(static_cast<size_t>((cap + 63) / 64));
    std::vector<uint32_t> ids(static_cast<size_t>(cap));
    if (!seh_copy(words.data(), reinterpret_cast<const void*>(bits), words.size() * 8) ||
        !seh_copy(ids.data(), reinterpret_cast<const void*>(keys), ids.size() * 4))
        return;
    std::vector<uint32_t> snapshot;
    snapshot.reserve(4096);
    for (uint64_t i = 0; i < cap; ++i)
        if ((words[i >> 6] >> (i & 63)) & 1) snapshot.push_back(ids[i]);
    for (uint32_t id : snapshot) cb(id, user);  // a fault here is the caller's callback: its own guard catches it
}

void api_on_tick(EmfWorldCallback cb, void* user) {
    if (!cb) return;
    auto r = std::make_unique<TickReg>();
    r->mod = mod_of_address(reinterpret_cast<const void*>(cb));
    if (!r->mod) r->mod = caller_mod(_ReturnAddress());
    r->cb = cb;
    r->user = user;
    std::lock_guard lock(g_tick_mutex);
    g_ticks.push_back(std::move(r));
}

void api_run_on_world(uint8_t kind, EmfWorldCallback cb, void* user) {
    if (!cb) return;
    Mod* m = mod_of_address(reinterpret_cast<const void*>(cb));
    if (!m) m = caller_mod(_ReturnAddress());
    if (kind > EMF_WORLD_CLIENT) {
        refuse(m, "run_on_world", "unknown world kind");
        return;
    }
    std::lock_guard lock(g_tick_mutex);
    g_runs.push_back({m, kind, cb, user});
}

uint32_t api_server_session() { return server_session(); }

// game build: EMF's "known" = Kiln verified this exe; "number" = the build's svn number from Kiln's build record
bool api_game_build(EmfGameBuild* out) {
    if (!out || out->size < sizeof(EmfGameBuild)) return false;
    static std::once_flag once;
    static EmfGameBuild b{};
    std::call_once(once, [] {
        b.size = sizeof b;
        KilnBuildInfo bi{};
        bi.size = sizeof bi;
        if (g_api->build_info(&bi) == KILN_OK) {
            std::memcpy(b.sha256, bi.exe_sha256, 65);
            b.known = bi.verified ? 1 : 0;
            b.server = bi.side == KILN_SIDE_SERVER ? 1 : 0;
            std::string text;
            const auto rec = P.kiln / L"sigdb" / L"builds" / (bi.side == KILN_SIDE_SERVER ? L"server" : L"client") /
                             to_wide(std::string(bi.exe_sha256) + ".json");
            if (read_file(rec, &text)) {
                try {
                    const auto j = nlohmann::json::parse(text);
                    if (j.contains("svn") && j["svn"].is_number_integer()) b.number = j["svn"].get<uint32_t>();
                } catch (...) {
                }
            }
        }
        const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        b.module_base = base;
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + static_cast<uintptr_t>(dos->e_lfanew));
        const auto* sec = IMAGE_FIRST_SECTION(nt);
        for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i)
            if (std::memcmp(sec[i].Name, ".text", 5) == 0) {
                b.text_rva = sec[i].VirtualAddress;
                b.text_size = sec[i].Misc.VirtualSize;
            }
    });
    const uint32_t size = out->size;
    *out = b;
    out->size = size;
    return true;
}

void fill_mod_info(const Mod* m, EmfModInfo* out) {
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof(EmfModInfo));
    out->size = size;
    out->status = m->status;
    copy_cstr(out->id, sizeof out->id, m->id);
    copy_cstr(out->name, sizeof out->name, m->name);
    copy_cstr(out->version, sizeof out->version, m->version);
    copy_cstr(out->source, sizeof out->source, m->source);
    copy_cstr(out->main, sizeof out->main, m->main);
}

bool api_mod_info(const char* id, EmfModInfo* out) {
    if (!id || !out || out->size < sizeof(EmfModInfo)) return false;
    const Mod* m = find_mod(id);
    if (!m) return false;
    fill_mod_info(m, out);
    return true;
}

bool api_mod_info_at(uint32_t index, EmfModInfo* out) {
    if (!out || out->size < sizeof(EmfModInfo)) return false;
    std::vector<const Mod*> sorted;
    for (const auto& m : g_mods) sorted.push_back(m.get());
    std::sort(sorted.begin(), sorted.end(), [](const Mod* a, const Mod* b) { return lower(a->name) < lower(b->name); });
    if (index >= sorted.size()) return false;
    fill_mod_info(sorted[index], out);
    return true;
}

void* api_get_template_component(EmfWorld* w, uint32_t hash, uint32_t entity, uint64_t* size_out) {
    return template_component_of(w, hash, entity, size_out);
}

// Hooks are owned by kiln-emf, whose DLL (and loaded EMF modules) remain mapped until process exit.
struct ProgramHook {
    Mod* mod;
    EmfProgramCallback before, after;
    void* user;
    std::atomic<bool> dead{false};
};
std::mutex g_program_hooks_mutex;
std::vector<std::unique_ptr<ProgramHook>> g_program_hooks;
void run_program_hook(void* ctx, void* raw, bool after) {
    auto* hook = static_cast<ProgramHook*>(raw);
    if (hook->dead.load()) return;
    auto fn = after ? hook->after : hook->before;
    if (!fn) return;
    struct Call { EmfProgramCallback fn; void* ctx; void* user; } call{fn, ctx, hook->user};
    ContextScope scope(hook->mod, nullptr, false);
    const uint64_t start = qpc_ns();
    if (!guarded(hook->mod, "a hook_program callback", [](void* raw) {
        auto* c = static_cast<Call*>(raw); c->fn(c->ctx, c->user);
    }, &call)) hook->dead = true;
    account(hook->mod, qpc_ns() - start);
}
void KILN_CALL program_before(void* ctx, void* user) { run_program_hook(ctx, user, false); }
void KILN_CALL program_after(void* ctx, void* user) { run_program_hook(ctx, user, true); }
bool api_hook_program(const char* name, EmfProgramCallback before, EmfProgramCallback after, void* user) {
    Mod* mod = caller_mod(_ReturnAddress());
    if (!name || !*name || (!before && !after)) return refuse(mod, "hook_program", "name and a callback required"), false;
    const auto* types = static_cast<const KilnTypesApi*>(g_api->service(kstr(KILN_TYPES_SERVICE), KILN_TYPES_VERSION));
    if (!types || types->header.size < sizeof(KilnTypesApi) || !types->system_hook)
        return unsupported(mod, "hook_program", "kiln.types system hooks are unavailable"), false;
    const int32_t index = types->system_find(kstr(name));
    if (index < 0) return refuse(mod, "hook_program", "unknown or ambiguous system name"), false;
    auto hook = std::make_unique<ProgramHook>();
    hook->mod = mod; hook->before = before; hook->after = after; hook->user = user;
    KilnHandle handle = 0;
    const auto result = types->system_hook(g_owner, static_cast<uint32_t>(index), &program_before, &program_after, hook.get(), &handle);
    if (result != KILN_OK) return refuse(mod, "hook_program", "system hook refused (duplicate function or unavailable)"), false;
    std::lock_guard lock(g_program_hooks_mutex);
    g_program_hooks.push_back(std::move(hook));
    return true;
}

}  // namespace

// Owner retirement must drain worker callbacks before user records or adapter code can disappear.
void keep_adapter_mapped() {
    HMODULE pin = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(&keep_adapter_mapped), &pin);
}
void program_hooks_shutdown() {
    const auto* types = static_cast<const KilnTypesApi*>(g_api->service(kstr(KILN_TYPES_SERVICE),1));
    if (!types || types->header.size < sizeof(KilnTypesApi) || !types->system_release_owner ||
        types->system_release_owner(g_owner) != KILN_OK) { keep_adapter_mapped(); return; }
    std::lock_guard lock(g_program_hooks_mutex);
    g_program_hooks.clear();
}

// ---------------------------------------------------------------------------------------------- helpers -------
EmfWorld* world_of_kind(uint32_t kind) { return reinterpret_cast<EmfWorld*>(g_api->world(kind)); }

uint8_t world_kind_of(EmfWorld* w) {
    if (!w) return 0xFF;
    for (uint32_t k = 0; k < KILN_WORLD_KINDS; ++k)
        if (g_api->world(k) == reinterpret_cast<uintptr_t>(w)) return static_cast<uint8_t>(k);
    const Layout& l = layout();
    uint8_t v = 0xFF;
    if (l.storage >= 0 && l.kind >= 0 &&
        seh_read_u8(reinterpret_cast<uintptr_t>(w) + static_cast<uintptr_t>(l.storage + l.kind), &v) && v <= 2)
        return v;
    return 0xFF;
}

bool on_thread_of(EmfWorld* w) {
    const uint8_t k = world_kind_of(w);
    return k <= EMF_WORLD_CLIENT && g_api->on_world_thread(k);
}

uint32_t server_session() {
    KilnSessionInfo s{};
    s.size = sizeof s;
    return g_api->session(&s) == KILN_OK ? s.sessions[KILN_WORLD_SERVER] : 0;
}

namespace {
struct Slice {
    const void* ptr;
    uint64_t size;
};
using GetComponentFn = void (*)(Slice* out, uintptr_t world, uint32_t hash, uint32_t entity);
struct GcCall {
    GetComponentFn fn;
    Slice* out;
    uintptr_t w;
    uint32_t hash, entity;
};
void gc_thunk(void* p) {
    auto* c = static_cast<GcCall*>(p);
    c->fn(c->out, c->w, c->hash, c->entity);
}
using TypeByHashFn = uintptr_t(__fastcall*)(uint32_t);
struct TbhCall {
    TypeByHashFn fn;
    uint32_t hash;
    uintptr_t out;
};
void tbh_thunk(void* p) {
    auto* c = static_cast<TbhCall*>(p);
    c->out = c->fn(c->hash);
}
}  // namespace

const void* component_of(EmfWorld* w, uint32_t hash, uint32_t entity, uint64_t* size) {
    if (size) *size = 0;
    if (!w || !hash || !entity) return nullptr;
    const uintptr_t world = reinterpret_cast<uintptr_t>(w);
    if (S.world && world == g_api->world(KILN_WORLD_SERVER)) {
        uint32_t sz = 0;
        const void* p = S.world->component(entity, hash, &sz);
        if (p && size) *size = sz;
        return p;
    }
    // client / prediction worlds: the game's GetComponent on that world's thread
    const Layout& l = layout();
    if (!l.get_component || !on_thread_of(w)) return nullptr;
    Slice s{};
    GcCall c{reinterpret_cast<GetComponentFn>(l.get_component), &s, world, hash, entity};
    unsigned long code;
    uintptr_t at;
    if (!seh_call(&gc_thunk, &c, &code, &at) || !s.ptr) return nullptr;
    if (size) *size = s.size;
    return s.ptr;
}

namespace {
using GetTemplateFn = void* (*)(uintptr_t world, uint32_t hash, uint32_t entity);
struct GtCall {
    GetTemplateFn fn;
    uintptr_t w;
    uint32_t hash, entity;
    void* out;
};
void gt_thunk(void* p) {
    auto* c = static_cast<GtCall*>(p);
    c->out = c->fn(c->w, c->hash, c->entity);
}
}  // namespace

// The entity's template data (read-only game memory) through the game's GetTemplateComponent; *size = the
// component type's reflected size (EMF reports the game's own size so mods can compare it with their struct).
void* template_component_of(EmfWorld* w, uint32_t hash, uint32_t entity, uint64_t* size) {
    if (size) *size = 0;
    const Layout& l = layout();
    if (!w || !hash || !entity || !l.get_template || !on_thread_of(w)) return nullptr;
    GtCall c{reinterpret_cast<GetTemplateFn>(l.get_template), reinterpret_cast<uintptr_t>(w), hash, entity, nullptr};
    unsigned long code;
    uintptr_t at;
    if (!seh_call(&gt_thunk, &c, &code, &at) || !c.out) return nullptr;
    if (size) *size = type_size(hash);
    return c.out;
}

uint32_t type_size(uint32_t qualified_hash) {
    const Layout& l = layout();
    if (!l.type_by_hash) return 0;
    TbhCall c{reinterpret_cast<TypeByHashFn>(l.type_by_hash), qualified_hash, 0};
    unsigned long code;
    uintptr_t at;
    if (!seh_call(&tbh_thunk, &c, &code, &at) || !c.out) return 0;
    uint32_t v = 0;
    return seh_read_u32(c.out + static_cast<uintptr_t>(l.type_size), &v) ? v : 0;
}

void core_after_step(EmfWorld* w, uint8_t kind) {
    // run_on_world first (EMF: "once, after the next step of a world of kind")
    std::vector<RunReg> runs;
    std::vector<TickReg*> ticks;
    {
        std::lock_guard lock(g_tick_mutex);
        for (size_t i = 0; i < g_runs.size();) {
            if (g_runs[i].kind == kind) {
                runs.push_back(g_runs[i]);
                g_runs.erase(g_runs.begin() + static_cast<ptrdiff_t>(i));
            } else {
                ++i;
            }
        }
        ticks.reserve(g_ticks.size());
        for (const auto& t : g_ticks)
            if (!t->dead.load(std::memory_order_relaxed)) ticks.push_back(t.get());
    }
    for (const RunReg& r : runs) {
        WorldCall c{r.cb, w, r.user};
        ContextScope scope(r.mod, w, true);
        const uint64_t t0 = qpc_ns();
        guarded(r.mod, "a run_on_world callback", &world_thunk, &c);
        account(r.mod, qpc_ns() - t0);
    }
    for (TickReg* t : ticks) {
        WorldCall c{t->cb, w, t->user};
        ContextScope scope(t->mod, w, true);
        const uint64_t t0 = qpc_ns();
        if (!guarded(t->mod, "an on_tick callback", &world_thunk, &c)) t->dead = true;
        account(t->mod, qpc_ns() - t0);
    }
}

void dispatch_after_step(EmfWorld* w, uint8_t kind) {
    if (!w) return;
    events_after_step(w, kind);
    components_after_step(w, kind);  // v23/v25: answers of change_component, offer upkeep
    windows_after_step(w, kind);     // v24: window events
    level_after_step(w, kind);       // v22: the XP curve cache
    commands_after_step(w, kind);
    core_after_step(w, kind);
    settings_after_step(w, kind);
}

const EmfApi* api_table() {
    if (g_table.version == 0) {
        g_table.version = kEmfApiVersion;
        g_table.size = sizeof(EmfApi);
        fill_core(&g_table);
        fill_settings(&g_table);
        fill_game(&g_table);
        fill_attributes(&g_table);  // v18
        fill_events(&g_table);
        fill_hooks(&g_table);
        fill_commands(&g_table);
        fill_level(&g_table);       // v22
        fill_components(&g_table);  // v23, v25
        fill_windows(&g_table);     // v24, v26
        // every slot must be filled: a NULL would crash a mod that checks only api->size
        const auto* slots = reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(&g_table) + 8);
        const size_t n = (sizeof(EmfApi) - 8) / sizeof(void*);
        for (size_t i = 0; i < n; ++i)
            if (!slots[i]) logf(KILN_LOG_ERROR, "internal: EmfApi slot %zu is empty", i);
    }
    return &g_table;
}

void fill_core(EmfApi* t) {
    t->log = &api_log;
    t->server_world = &api_server_world;
    t->client_world = &api_client_world;
    t->world_kind = &api_world_kind;
    t->world_tick = &api_world_tick;
    t->ecs_time_ns = &api_ecs_time_ns;
    t->singleton = &api_singleton;
    t->local_player = &api_local_player;
    t->get_component = &api_get_component;
    t->for_each_entity = &api_for_each_entity;
    t->on_tick = &api_on_tick;
    t->run_on_world = &api_run_on_world;
    t->hook_program = &api_hook_program;
    t->get_template_component = &api_get_template_component;
    t->server_session = &api_server_session;
    t->mod_info = &api_mod_info;
    t->mod_info_at = &api_mod_info_at;
    t->game_build = &api_game_build;
}

}  // namespace kemf
