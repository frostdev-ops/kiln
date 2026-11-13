// SPDX-License-Identifier: MIT
// Tracked writes (kiln.world v3): the one Kiln system program "kiln_world_writes" that changes a component of any
// entity through the game's own WriteOther, with the job's tracker, so the save and replication see the change.
// The component types come from the mods' manifests ("writes", read at boot by boot_programs.cpp) and from native
// services (write_declare): the engine fixes a program's access when the world starts. The queue, the write over a
// store and the results are write_queue.h (host-tested); this file is the engine side. Evidence: docs/re/world.md,
// "Tracked writes"; the write path itself was captured and lab-checked for kiln.items (docs/re/items.md section 12).
#include <chrono>

#include "services/kiln_types.h"
#include "world_internal.h"
#include "write_queue.h"

namespace kw {
namespace {

constexpr const char* kProgramName = "kiln_world_writes";
constexpr const char* kAttributeRegistry = "impact::AttributeRegistry";
constexpr const char* kSpawnTime = "SpawnTime";  // an entity's generation: set when it is spawned, 8 bytes (docs/re/spawn.md)

using AttrChangedFn = void (*)(void* component, void* registry);

WriteDeclarations g_types;
WriteQueue g_queue;
WriteCatalog g_catalog;                   // every component type of the last server world, for write_info
std::mutex g_scan_mutex;
std::map<uint32_t, CatalogEntry> g_scan;  // the game's writers of the last server world (its setup's scan)
std::atomic<bool> g_bits_due{false};      // the save and replication bits are read at the world's first live step
WriteWatchdog g_watchdog;                 // tick thread
std::atomic<uint64_t> g_epoch{0};         // server worlds registered so far: a request belongs to the one it was asked in
KilnHandle g_program = 0;
AttrChangedFn g_attr_changed = nullptr;
int64_t g_reg_replicated = -1, g_reg_saved = -1;
bool g_made = false;                      // the program was handed to register_program
std::atomic<bool> g_broken{false};        // an engine call faulted: no more writes this session
std::atomic<bool> g_verified{false};      // the accessors of this world were checked
std::atomic<uintptr_t> g_seen_world{0};
std::atomic<uint64_t> g_updates{0};
std::atomic<int64_t> g_last_update_ms{0}; // steady clock of the last update
thread_local bool t_in_writes = false;

std::mutex g_slots_mutex;
std::map<uint32_t, int32_t> g_slots;      // component hash -> iterator slot of its accessor
int32_t g_registry_slot = -1;
int32_t g_spawn_slot = -1;                // iterator slot of the SpawnTime read accessor
std::atomic<bool> g_generations{false};   // the program reads SpawnTime: requests carry their entity's generation
uint32_t spawn_time_hash() { return write_hash(kSpawnTime); }

int64_t now_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

void broken(const char* what, unsigned long code) {
    if (g_broken.exchange(true)) return;
    KW_E("tracked writes: %s (0x%08lx); they are off until the server restarts", what, code);
    g_types.set_program(false, true);
}

struct EngineStore final : WriteStore {
    ProgramCtx* ctx = nullptr;
    const uint64_t* iter = nullptr;
    void* storage = nullptr;

    uint64_t accessor(const WriteType& type) const {
        std::lock_guard lock(g_slots_mutex);
        const auto at = g_slots.find(type.hash);
        return at == g_slots.end() || at->second < 0 ? 0 : iter[at->second];
    }
    bool read(uint32_t entity, const WriteType& type, const uint8_t** data, uint32_t* size) override {
        *data = nullptr;
        *size = 0;
        const uint64_t acc = accessor(type);
        if (!acc) return true;
        Slice s{};
        unsigned long code = 0;
        if (!seh_read_other(&s, storage, reinterpret_cast<void*>(acc), entity, &code)) {
            broken("the game's component read faulted", code);
            return false;
        }
        if (!s.ptr || s.size == 0 || s.size > UINT32_MAX) return true;
        *data = static_cast<const uint8_t*>(s.ptr);
        *size = static_cast<uint32_t>(s.size);
        return true;
    }
    bool mark(uint32_t entity, const WriteType& type, uint8_t** data, uint32_t* size) override {
        *data = nullptr;
        *size = 0;
        const uint64_t acc = accessor(type);
        if (!acc) return true;
        Slice s{};
        unsigned long code = 0;
        if (!seh_write_other(&s, storage, ctx, acc, entity, &code)) {
            broken("the game's tracked write (WriteOther) faulted", code);
            return false;
        }
        if (!s.ptr || s.size > UINT32_MAX) return true;
        *data = const_cast<uint8_t*>(static_cast<const uint8_t*>(s.ptr));
        *size = static_cast<uint32_t>(s.size);
        return true;
    }
    bool formulas(uint8_t* component, const WriteType&) override {
        // The game checks the block's "has values" flag before it uses an attribute (actor_interaction).
        void* registry = g_registry_slot >= 0 ? reinterpret_cast<void*>(iter[g_registry_slot]) : nullptr;
        if (!g_attr_changed || !registry || !(component[6] & 1)) return true;
        unsigned long code = 0;
        if (!seh_attr_changed(reinterpret_cast<void (*)(void*, void*)>(g_attr_changed), component, registry, &code)) {
            broken("the game's attribute formulas (AttrChanged) faulted", code);
            return false;
        }
        return true;
    }
    bool change(KilnWorldChangeFn fn, uint32_t entity, uint8_t* data, uint32_t size, void* user, bool* accepted) override {
        unsigned long code = 0;
        return seh_write_change(fn, entity, data, size, user, accepted, &code);
    }
    bool generation(uint32_t entity, uint64_t* out) override {
        int32_t slot = -1;
        {
            std::lock_guard lock(g_slots_mutex);
            slot = g_spawn_slot;
        }
        if (slot < 0 || !iter[slot]) return false;
        Slice s{};
        unsigned long code = 0;
        if (!seh_read_other(&s, storage, reinterpret_cast<void*>(iter[slot]), entity, &code)) {
            broken("the game's SpawnTime read faulted", code);
            return false;
        }
        return s.ptr && s.size == 8 && seh_copy(out, s.ptr, 8);
    }
};

// Once per world: every accessor must be the type the component map says, as kiln.items checks its own.
bool verify(ProgramCtx* ctx, const uint64_t* iter) {
    const uintptr_t storage = *reinterpret_cast<const uintptr_t*>(ctx->job);
    const uintptr_t world = storage - static_cast<uintptr_t>(L.world_storage);
    const uintptr_t registry = registry_of(world);
    std::map<uint32_t, int32_t> slots;
    for (const auto& type : g_types.snapshot()) {
        if (type.state != KILN_WORLD_WRITES_READY) continue;
        const int32_t slot = program_slot(g_program, KILN_SLOT_OTHER_WRITE, kstr(type.name));
        uint16_t index = 0, held = 0;
        if (slot < 1 || !iter[slot] || !map_find_u16(registry + L.reg_components, type.hash, &index) ||
            !seh_copy(&held, reinterpret_cast<const void*>(iter[slot]), 2) || held != index) {
            KW_E("tracked writes: the accessor of %s is not what the world's component map says (slot %d, type %u, held %u); "
                 "they are off until the server restarts", type.name.c_str(), slot, index, held);
            g_broken.store(true);
            g_types.set_program(false, true);
            return false;
        }
        slots[type.hash] = slot;
    }
    const int32_t registry_slot = g_attr_changed ? program_slot(g_program, KILN_SLOT_SINGLETON, kstr(kAttributeRegistry)) : -1;
    int32_t spawn_slot = -1;
    if (g_generations.load()) {
        spawn_slot = program_slot(g_program, KILN_SLOT_OTHER_READ, kstr(kSpawnTime));
        uint16_t index = 0, held = 0;
        if (spawn_slot < 1 || !iter[spawn_slot] || !map_find_u16(registry + L.reg_components, spawn_time_hash(), &index) ||
            !seh_copy(&held, reinterpret_cast<const void*>(iter[spawn_slot]), 2) || held != index) {
            KW_E("tracked writes: the SpawnTime accessor is not what the world's component map says (slot %d, type %u, held %u); "
                 "they are off until the server restarts", spawn_slot, index, held);
            g_broken.store(true);
            g_types.set_program(false, true);
            return false;
        }
    }
    {
        std::lock_guard lock(g_slots_mutex);
        g_slots = std::move(slots);
        g_registry_slot = registry_slot;
        g_spawn_slot = spawn_slot;
    }
    g_seen_world.store(world);
    g_verified.store(true);
    KW_I("tracked writes: program %s runs in world 0x%llx, %u accessor(s) verified against the component map, attribute "
         "formulas %s, generations (SpawnTime) %s", kProgramName, static_cast<unsigned long long>(world),
         static_cast<unsigned>(g_slots.size()), registry_slot >= 0 && iter[registry_slot] ? "on" : "off", spawn_slot >= 0 ? "on" : "off");
    return true;
}

void KILN_CALL writes_update(KilnProgramCtx* c, void*) {
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    g_updates.fetch_add(1, std::memory_order_relaxed);
    g_last_update_ms.store(now_ms(), std::memory_order_relaxed);
    if (g_broken.load(std::memory_order_relaxed)) return;
    if (g_queue.waiting() == 0 && g_verified.load(std::memory_order_relaxed)) return;  // an idle step costs one lock
    alignas(16) uint64_t iter[128] = {};
    if (iter_begin(c, iter, sizeof iter) != KILN_OK) return broken("its iterator could not be made", 0);
    const uintptr_t storage = *reinterpret_cast<const uintptr_t*>(ctx->job);
    const uintptr_t world = storage - static_cast<uintptr_t>(L.world_storage);
    if ((!g_verified.load() || g_seen_world.load() != world) && !verify(ctx, iter)) return;
    EngineStore store;
    store.ctx = ctx;
    store.iter = iter;
    store.storage = *reinterpret_cast<void* const*>(ctx->job + L.job_storage);
    const uint64_t step = g_internal_runs.load(std::memory_order_relaxed);
    t_in_writes = true;
    // The checks first: they compare with what the game's programs of this step left.
    for (auto& job : g_queue.verify_due()) {
        WriteType type;
        uint32_t state = 0;
        const bool known = g_types.lookup({}, job.component, &type, &state);
        write_check(job, known ? &type : nullptr, store);
        g_queue.complete(std::move(job), step);
    }
    const uint64_t epoch = g_epoch.load();
    for (auto& job : g_queue.take(kWritePerUpdate)) {
        WriteType type;
        uint32_t state = 0;
        const bool known = g_types.lookup(job.mod, job.component, &type, &state);
        if (g_broken.load(std::memory_order_relaxed) || job.epoch != epoch) {
            job.status = KILN_WORLD_WRITE_DROPPED;
            job.reason = job.epoch != epoch ? std::string(write_world_changed()) : write_refusal(KILN_WORLD_WRITES_OFF, {}, {});
            g_queue.complete(std::move(job), step);
            continue;
        }
        if (write_run(job, known ? &type : nullptr, state, store)) g_queue.hold(std::move(job));
        else g_queue.complete(std::move(job), step);
    }
    // What still waits (more than one update's share): an entity that lacks its component now may be gone, and its id
    // may name another entity by the time the request runs. Such a request never writes.
    if (g_queue.not_run() > 0)
        g_queue.watch([&](const WriteJob& job) {
            WriteType type;
            uint32_t state = 0;
            if (!g_types.lookup({}, job.component, &type, &state) || state != KILN_WORLD_WRITES_READY) return true;  // refused when it runs
            const uint8_t* data = nullptr;
            uint32_t size = 0;
            return !store.read(job.entity, type, &data, &size) || (data && size);  // a faulting read is reported by its run
        });
    g_queue.end_update();
    t_in_writes = false;
}

void KILN_CALL owner_probe(void*) {}

// What the game's own programs do with the components: the world's program table, right after the game registered its
// programs and before Kiln's are added. Descriptors are static data of the exe; every read is guarded. Fills the
// listed types (g_types) and returns every written type's writers for the catalog.
std::map<uint32_t, CatalogEntry> scan_game_writers(uintptr_t world) {
    g_types.clear_writers();
    std::map<uint32_t, CatalogEntry> catalog;
    uint16_t count = 0;
    if (L.programs_count < 0 || L.programs_table < 0 || L.programs_stride <= 0 ||
        !seh_copy(&count, reinterpret_cast<const void*>(world + L.programs_count), 2) || count > L.programs_max) return catalog;
    uint32_t programs = 0;
    for (uint16_t i = 0; i < count; ++i) {
        const uintptr_t entry = world + L.programs_table + static_cast<uintptr_t>(i) * L.programs_stride;
        const EngineDesc* at = nullptr;
        EngineDesc desc;
        if (!seh_copy(&at, reinterpret_cast<const void*>(entry), sizeof at) || !at || !seh_copy(&desc, at, sizeof desc)) continue;
        if (desc.flags & 0x1) continue;  // a client world's program: it never runs in the server world
        char name[100] = "";
        if (!desc.name.s || desc.name.n == 0 || desc.name.n >= sizeof name || !seh_copy(name, desc.name.s, desc.name.n)) continue;
        if (std::string_view(name, desc.name.n).starts_with("kiln_")) continue;  // a Kiln service that registered by itself
        ++programs;
        const bool init = (desc.flags & KILN_PROGRAM_INIT) != 0;
        for (const NameListE* list : {&desc.write, &desc.write_opt, &desc.other_write, &desc.other_write_unser, &desc.all_write}) {
            if (!list->p || list->n == 0 || list->n > 200) continue;
            for (uint64_t k = 0; k < list->n; ++k) {
                NameRef ref{};
                char component[124] = "";
                if (!seh_copy(&ref, list->p + k, sizeof ref) || !ref.s || ref.n == 0 || ref.n >= sizeof component ||
                    !seh_copy(component, ref.s, ref.n)) continue;
                const std::string_view what(component, ref.n), who(name, desc.name.n);
                g_types.add_writer(what, who, init);
                CatalogEntry& entry = catalog[write_hash(what)];
                if (init) {
                    ++entry.init_writers;
                } else {
                    ++entry.writers;
                    entry.flags |= KILN_WORLD_WROTE_GAME_WRITES;
                    if (entry.writer_names.size() + who.size() + 2 < 200)
                        entry.writer_names += (entry.writer_names.empty() ? "" : ", ") + std::string(who);
                }
            }
        }
    }
    KW_D("tracked writes: read %u server program descriptor(s)", programs);
    return catalog;
}

void log_listed() {
    for (const auto& type : g_types.snapshot())
        if (type.state != KILN_WORLD_WRITES_OVER_LIMIT && type.state != KILN_WORLD_WRITES_UNKNOWN)
            KW_I("tracked writes: %s: %u game program(s) write it%s%s%s; %u bytes%s; save %s, replication %s", type.name.c_str(),
                 type.writers, type.writers ? " (" : "", type.writer_names.c_str(), type.writers ? ")" : "", type.size,
                 type.attribute() ? ", an attribute" : "",
                 !(type.flags & KILN_WORLD_WROTE_BITS_KNOWN) ? "not known" : (type.flags & KILN_WORLD_WROTE_SAVED) ? "yes" : "no",
                 !(type.flags & KILN_WORLD_WROTE_BITS_KNOWN) ? "not known" : (type.flags & KILN_WORLD_WROTE_REPLICATED) ? "yes" : "no");
}

// What the build says about one component type: its reflected size, whether it is an attribute, and the engine's
// per-component save and replication bits (docs/re/world.md, "Tracked writes"). Server thread, inside the world setup.
struct Facts {
    uint32_t size = 0;
    bool attribute = false, bits = false, saved = false, replicated = false, in_world = false;
};
uintptr_t components_map(uintptr_t world) {
    if (!world || L.reg_components < 0) return 0;
    uintptr_t registry = 0;
    if (!seh_copy(&registry, reinterpret_cast<const void*>(world + L.world_storage), sizeof registry) || !registry) return 0;
    return registry + L.reg_components;
}
// The reflected size and the attribute flag: kiln.types' table only (its own service, loaded once; no world is read).
Facts type_facts(uint32_t hash) {
    Facts f;
    if (const auto* types = program_types()) {
        KilnTypeInfo info{};
        info.size = sizeof info;
        const KilnType* t = types->type_by_hash ? types->type_by_hash(hash) : nullptr;
        if (t && types->type_info(t, &info) == KILN_OK) {
            f.size = info.type_size;
            for (unsigned depth = 0; depth < 16; ++depth) {
                if (sv(info.name) == "AttributeComponent") { f.attribute = depth > 0; break; }
                if (!info.inner) break;
                const KilnType* inner = info.inner;
                info = KilnTypeInfo{};
                info.size = sizeof info;
                if (types->type_info(inner, &info) != KILN_OK) break;
            }
        }
    }
    return f;
}

// `bits`: read the save and replication bits too. Not at the world's setup: the game sets the replication bits later
// (read there, every type said "no"; at the first live step 91 of 687 say yes), so they are read on the tick thread
// at the world's first live step, as every other world read of kiln.world is.
Facts facts_of(uintptr_t world, uint32_t hash, bool bits) {
    Facts f = type_facts(hash);
    const uintptr_t map = components_map(world);
    if (!map) return f;
    const uintptr_t registry = map - static_cast<uintptr_t>(L.reg_components);
    uint16_t index = 0;
    f.in_world = map_find_u16(map, hash, &index);
    uint64_t saved = 0, replicated = 0;
    f.bits = bits && f.in_world && g_reg_replicated >= 0 && g_reg_saved >= 0 &&
             seh_copy(&saved, reinterpret_cast<const void*>(registry + g_reg_saved + (index >> 6) * 8), 8) &&
             seh_copy(&replicated, reinterpret_cast<const void*>(registry + g_reg_replicated + (index >> 6) * 8), 8);
    f.saved = (saved >> (index & 63)) & 1;
    f.replicated = (replicated >> (index & 63)) & 1;
    return f;
}

uint32_t facts_flags(const Facts& f) {
    return (f.attribute ? KILN_WORLD_WROTE_ATTRIBUTE : 0u) |
           (f.bits ? KILN_WORLD_WROTE_BITS_KNOWN | (f.saved ? KILN_WORLD_WROTE_SAVED : 0u) | (f.replicated ? KILN_WORLD_WROTE_REPLICATED : 0u) : 0u);
}

// The listed types' facts.
void read_facts(uintptr_t world, bool bits) {
    for (const auto& type : g_types.snapshot()) {
        const Facts f = facts_of(world, type.hash, bits);
        g_types.set_facts(type.hash, f.size, f.attribute, f.bits, f.saved, f.replicated);
    }
}

// The catalog of every component type of this world, with the game's writers the scan found: write_info answers from
// it on any thread, never from the world itself.
void publish_catalog(uintptr_t world, std::map<uint32_t, CatalogEntry> catalog, bool bits, uint64_t epoch) {
    std::vector<uint32_t> hashes;
    if (const uintptr_t map = components_map(world)) map_each_u16(map, [&](uint32_t key, uint16_t) { hashes.push_back(key); });
    for (const auto& [hash, entry] : catalog) hashes.push_back(hash);  // a written type the map does not hold: its size still
    std::sort(hashes.begin(), hashes.end());
    hashes.erase(std::unique(hashes.begin(), hashes.end()), hashes.end());
    for (uint32_t hash : hashes) {
        const Facts f = facts_of(world, hash, bits);
        CatalogEntry& entry = catalog[hash];
        entry.size = f.size;
        entry.flags = (entry.flags & KILN_WORLD_WROTE_GAME_WRITES) | facts_flags(f);
    }
    const size_t types = catalog.size();
    g_catalog.publish(std::make_shared<const WriteCatalog::Map>(std::move(catalog)), epoch, bits);
    KW_D("tracked writes: catalog of %zu component type(s) published", types);
}

// At a world's setup: the game's writers, sizes and attributes now (the bits wait for the first live step).
void rescan(uintptr_t world) {
    read_facts(world, false);
    auto scan = scan_game_writers(world);
    {
        std::lock_guard lock(g_scan_mutex);
        g_scan = scan;
    }
    publish_catalog(world, std::move(scan), false, g_epoch.load());
    g_bits_due.store(true);
}

// The world's first live step, tick thread: the save and replication bits, for the listed types and the catalog.
void read_bits(uintptr_t world) {
    const uint64_t epoch = g_epoch.load();
    read_facts(world, true);
    std::map<uint32_t, CatalogEntry> scan;
    {
        std::lock_guard lock(g_scan_mutex);
        scan = g_scan;
    }
    if (g_epoch.load() != epoch || g_world.load() != world) return;  // another world began meanwhile: its setup publishes
    publish_catalog(world, std::move(scan), true, epoch);
    log_listed();
}

void deliver(WriteJob& job) {
    if (!job.callback) return;
    KilnWorldWriteResult r{};
    r.size = sizeof r;
    r.status = job.status;
    r.ticket = job.ticket;
    r.entity = job.entity;
    r.component = job.component;
    r.offset = job.offset;
    r.length = job.length;
    r.component_size = job.component_size;
    r.flags = job.out_flags;
    r.steps = job.steps;
    r.writers = job.writers;
    r.before = job.read ? job.before.data() : nullptr;
    r.after = job.read ? job.after.data() : nullptr;
    r.reason = kstr(job.reason);
    unsigned long code = 0;
    if (!seh_write_cb(job.callback, &r, job.user, &code))
        KW_E("tracked writes: a result callback faulted (0x%08lx) for ticket %llu", code, static_cast<unsigned long long>(job.ticket));
}

}  // namespace

bool writes_available() {
    WriteDeps d;
    d.programs = F.programs;
    d.write_other = F.write_other;
    d.read_other = F.read_other && fn.read_other;
    d.names = F.names;
    d.job_storage = L.job_storage;
    return write_deps_ok(d);
}

void writes_resolve() {
    uintptr_t address = 0;
    if (g_api->symbol(kstr("items/AttrChanged"), &address) == KILN_OK) g_attr_changed = reinterpret_cast<AttrChangedFn>(address);
    if (g_api->feature(kstr("world.component_bits"))) {
        int64_t value = -1;
        if (g_api->constant(kstr("world/registry.replicated"), &value) == KILN_OK) g_reg_replicated = value;
        value = -1;
        if (g_api->constant(kstr("world/registry.saved"), &value) == KILN_OK) g_reg_saved = value;
    }
}

std::vector<std::string> writes_declare(const std::string& who, const std::vector<std::string>& names) {
    return g_types.add(who, names);
}

// A new server world, before g_world names it: the requests and the catalog of the previous one are not its. Requests
// that have not run end; write_info answers "not ready" until this world's catalog is published.
void writes_world_changing() {
    const uint64_t epoch = g_epoch.fetch_add(1) + 1;
    g_queue.drop_stale(epoch, g_internal_runs.load());
}

void writes_refuse_list(const std::string& mod, const std::string& why) { g_types.refuse_list(mod, why); }

// A server world is about to register its programs: on the first one the names the program takes and the write program
// itself (once), then what the game's own programs write and the catalog. Server thread, inside the world setup, after
// writes_world_changing, before register_into.
void writes_before_world(uintptr_t world) {
    if (!writes_available()) return;
    if (g_types.count() == 0) {
        rescan(world);  // write_info still answers what a mod would get if it listed a type
        return;
    }
    if (!g_made) {
        g_made = true;
        const uintptr_t map = components_map(world);
        g_types.admit(map ? std::function<bool(uint32_t)>([map](uint32_t hash) { return map_find_u16(map, hash, nullptr); })
                          : std::function<bool(uint32_t)>());
        const auto names = g_types.program_names();
        std::vector<KilnStr> other;
        for (const auto& name : names) other.push_back(kstr(name));
        const KilnStr registry = kstr(kAttributeRegistry);
        const KilnStr spawn_time = kstr(kSpawnTime);
        // The generation: without SpawnTime in this world every request is "without a generation" (write_queue.h).
        g_generations.store(map && map_find_u16(map, spawn_time_hash(), nullptr));
        KilnProgramDesc d{};
        d.size = sizeof d;
        d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
        d.name = kstr(kProgramName);
        d.update = &writes_update;
        if (g_attr_changed) {
            d.singletons_read = KilnNames{&registry, 1, 0};
            d.singletons_read_optional = 1;  // without it the program still runs; attributes then get no formulas
        }
        d.other_write = KilnNames{other.data(), static_cast<uint32_t>(other.size()), 0};
        if (g_generations.load()) d.other_read = KilnNames{&spawn_time, 1, 0};
        if (register_program(g_owner, &d, &g_program, false) != KILN_OK) {
            KW_E("tracked writes: program %s could not be made; World.Write is off", kProgramName);
            g_types.set_program(false, true);
            return;
        }
        program_set_prune(g_program, [](std::string_view name) { g_types.set_unknown(name); });
    }
    rescan(world);
}

// After register_into: the names follow the program's state.
void writes_after_world() {
    if (!g_program) return;
    KilnProgramInfo info{};
    info.size = sizeof info;
    const bool active = program_info(g_program, &info) == KILN_OK && info.state == KILN_PROGRAM_ACTIVE;
    g_verified.store(false);
    g_types.set_program(active, !active || g_broken.load());
    uint32_t declared = 0, refused = 0;
    g_types.counts(&declared, &refused);
    if (active) KW_I("tracked writes: %u component type(s) can be written, %u listed name(s) refused", declared, refused);
    else KW_E("tracked writes: program %s was not registered (state %u); World.Write is off", kProgramName, info.state);
}

uint32_t writes_capability() { return writes_available() ? KILN_WORLD_CAP_TRACKED_WRITES : 0; }

KilnResult KILN_CALL write_submit(KilnOwner owner, const KilnWorldWriteRequest* r, uint64_t* out) {
    if (out) *out = 0;
    if (!r || r->size < sizeof(KilnWorldWriteRequest) || !r->entity || !r->component) return KILN_ERR_INVALID_ARGUMENT;
    if ((r->data == nullptr) == (r->change == nullptr)) return KILN_ERR_INVALID_ARGUMENT;
    if (r->data && (r->length == 0 || r->length > kWriteMaxBytes)) return KILN_ERR_INVALID_ARGUMENT;
    if (r->flags & ~static_cast<uint32_t>(KILN_WORLD_WRITE_WHOLE | KILN_WORLD_WRITE_VERIFY)) return KILN_ERR_INVALID_ARGUMENT;
    if (r->mod_id.len > 64) return KILN_ERR_INVALID_ARGUMENT;
    if (!writes_available()) return KILN_ERR_UNAVAILABLE;
    // Between two steps, on the tick thread: the entity (and its generation) is read as the next update will find it.
    if (t_in_writes || t_in_program || !g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    WriteJob job;
    job.owner = owner;
    job.entity = r->entity;
    job.component = r->component;
    job.flags = r->flags;
    job.change = r->change;
    job.callback = r->callback;
    job.user = r->user;
    job.mod = std::string(sv(r->mod_id));
    if (r->data) {
        job.offset = r->offset;
        job.length = r->length;
        job.data.resize(r->length);
        if (!seh_copy(job.data.data(), r->data, r->length)) return KILN_ERR_INVALID_ARGUMENT;
    }
    const uint64_t step = g_internal_runs.load(std::memory_order_relaxed);
    job.epoch = g_epoch.load();
    if (g_generations.load()) {
        uint32_t size = 0;
        const void* p = component(job.entity, spawn_time_hash(), &size);
        job.has_generation = p && size == 8 && seh_copy(&job.generation, p, 8);
    }
    WriteType type;
    uint32_t state = 0;
    const bool known = g_types.lookup(job.mod, job.component, &type, &state);
    if (g_broken.load()) state = KILN_WORLD_WRITES_OFF;
    if (state != KILN_WORLD_WRITES_READY) {
        // Known now: it needs no program, only its delivery. PENDING: no server world registered the program yet, so
        // no entity id can be one of its entities.
        const bool never = state == KILN_WORLD_WRITES_OFF || state == KILN_WORLD_WRITES_PENDING;
        job.status = never ? KILN_WORLD_WRITE_DROPPED : KILN_WORLD_WRITE_NOT_DECLARED;
        job.reason = write_refusal(state, known ? std::string_view(type.name) : std::string_view(), job.mod);
        if (state == KILN_WORLD_WRITES_LIST_REFUSED) job.reason += ": " + g_types.list_refusal(job.mod);
        return g_queue.reject(std::move(job), step, out);
    }
    return g_queue.submit(std::move(job), step, out);
}

KilnResult KILN_CALL write_cancel(uint64_t ticket) {
    // Between steps no update of the write program runs, so what is cancelled here is not being run right now.
    if (t_in_writes || !g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    return g_queue.cancel(ticket) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL write_info(KilnStr mod_id, uint32_t component, KilnWorldWriteInfo* out) {
    if (!out || out->size < sizeof(KilnWorldWriteInfo)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    if (!writes_available()) {
        out->state = KILN_WORLD_WRITES_OFF;
        return KILN_OK;
    }
    WriteType type;
    uint32_t state = 0;
    const bool known = g_types.lookup(sv(mod_id), component, &type, &state);
    out->state = g_broken.load() && known ? static_cast<uint32_t>(KILN_WORLD_WRITES_OFF) : state;
    // What the running world knows of the type, listed or not: from the catalog published for that world (tagged with
    // it), never from the world itself. A catalog of another world (a new one is being set up), or none: not ready.
    CatalogEntry entry;
    switch (g_catalog.find(g_epoch.load(), component, &entry)) {
        case WriteCatalog::Answer::NotReady:
            out->flags = KILN_WORLD_INFO_NOT_READY;
            return KILN_OK;
        case WriteCatalog::Answer::Absent: {
            const Facts f = type_facts(component);  // not in the world's map: what kiln.types says of it, nothing more
            out->component_size = f.size;
            out->flags = f.attribute ? static_cast<uint32_t>(KILN_WORLD_WROTE_ATTRIBUTE) : 0u;
            return KILN_OK;
        }
        case WriteCatalog::Answer::Found:
            break;
    }
    out->writers = entry.writers;
    out->init_writers = entry.init_writers;
    out->component_size = entry.size;
    out->flags = entry.flags;
    std::snprintf(out->writer_names, sizeof out->writer_names, "%s", entry.writer_names.c_str());
    return KILN_OK;
}

KilnResult KILN_CALL write_status(KilnWorldWriteStatus* out) {
    if (!out || out->size < sizeof(KilnWorldWriteStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    KilnProgramInfo info{};
    info.size = sizeof info;
    // Not made yet (it is made when the first server world registers its programs): it waits, like a pending program.
    out->program = !g_made ? static_cast<uint32_t>(KILN_PROGRAM_PENDING)
                 : g_program && program_info(g_program, &info) == KILN_OK ? info.state : static_cast<uint32_t>(KILN_PROGRAM_REFUSED);
    if (g_broken.load()) out->program = KILN_PROGRAM_FAULTED;
    out->available = writes_available();
    out->running = out->program == KILN_PROGRAM_ACTIVE && g_verified.load() && now_ms() - g_last_update_ms.load() < 2000;
    out->attributes = g_attr_changed != nullptr && program_types() != nullptr;
    out->bits = g_reg_replicated >= 0 && g_reg_saved >= 0;
    g_types.counts(&out->declared, &out->refused);
    out->pending = static_cast<uint32_t>(g_queue.waiting());
    const WriteCounters c = g_queue.counters();
    out->submitted = c.submitted;
    out->written = c.written;
    out->unchanged = c.unchanged;
    out->failed = c.failed;
    out->updates = g_updates.load();
    return KILN_OK;
}

// A native service's own list, before the first server world registers its programs (kiln_plugin_main or on_ready).
KilnResult KILN_CALL write_declare(KilnOwner, KilnStr who, const KilnStr* names, uint32_t count) {
    if (!names || count == 0 || count > kWriteMaxPerMod || who.len == 0 || who.len > 64) return KILN_ERR_INVALID_ARGUMENT;
    if (!writes_available() || g_made) return KILN_ERR_UNAVAILABLE;
    std::vector<std::string> list;
    for (uint32_t i = 0; i < count; ++i) list.emplace_back(sv(names[i]));
    const auto refused = g_types.add(std::string(sv(who)), list);
    for (const auto& name : refused) KW_W("tracked writes: %.*s lists '%s', which is not a component name", static_cast<int>(who.len), who.ptr, name.c_str());
    return refused.empty() ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;
}

// Tick thread, Pre: owners that went away, requests whose program does not run, then the results.
void writes_pre(const KilnTickInfo*) {
    static std::chrono::steady_clock::time_point next_probe{};
    const auto now = std::chrono::steady_clock::now();
    auto alive = [](KilnOwner owner) { return owner == g_owner || g_api->post(owner, &owner_probe, nullptr) != KILN_ERR_REVOKED; };
    if (g_world_live.load(std::memory_order_relaxed) && g_bits_due.exchange(false)) read_bits(g_world.load());
    if (now >= next_probe) {
        next_probe = now + std::chrono::seconds(1);
        for (KilnOwner owner : g_queue.owners())
            if (!alive(owner)) g_queue.drop_owner(owner);
    }
    // Requests that wait for a run or for their check, with no update of the program for 10 s: the world stopped.
    const bool stalled = g_watchdog.stalled(g_queue, now_ms(), g_last_update_ms.load());
    if (g_broken.load() || stalled)
        g_queue.drop_waiting(g_broken.load() ? write_refusal(KILN_WORLD_WRITES_OFF, {}, {})
                                             : std::string("the write program did not run for 10 s (no server world runs)"),
                             g_internal_runs.load());
    // A result is never delivered into an owner that unloaded since it asked, nor after a callback of this batch
    // cancelled it or unloaded its owner: both are asked again right before each callback.
    auto done = g_queue.drain(kWriteDeliverPerPre);
    write_deliver(g_queue, done, alive, [](WriteJob& job) { deliver(job); });
}

}  // namespace kw
