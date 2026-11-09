// SPDX-License-Identifier: MIT
// The EmfApi table, part 3: engine events (on_event, v4) and mod ECS programs (register_program / _def, v2-v16),
// both through kiln.world's program registration.
//  - on_event: one SYSTEM program per event type ("emf_ev_<Event>", events_in = [Event]) copies the type's queued
//    events during the step; after the step (Post, tick thread) every registration gets each copy. The record stride
//    is the event type's reflected size (TypeByHash), checked against the mod's expected_size; the queue walk is the
//    one kiln.combat uses (constants combat/evq.*; EMF docs/engine_parts/re_events.md).
//  - programs: the mod's fn(program_context, user) runs inside kiln.world's SYSTEM program on a game worker thread;
//    program_singleton / program_read_other use kiln.world's iterator slots; emit_event uses the game's own emitter
//    on the job in the events-out slot (as kiln.gameplay does). kiln.world v2 resolves every events-in slot and
//    exposes tracked other-entity writes using the original program descriptor and its job tracker.
// Everything is registered in on_ready, after every emf_mod_load, before the server world sets up its programs.
#include <intrin.h>

#include <map>

#include "emf_internal.h"

namespace kemf {

namespace {

bool g_ready_done = false;  // after on_ready nothing more can be registered (the world will finalize)

// ============================================================================================= on_event ======
struct EventReg {
    Mod* mod;
    uint32_t expected;
    EmfWorldEventCallback cb;
    void* user;
    std::atomic<bool> dead{false};
    bool size_ok = true;
};
struct EventType {
    std::string name;
    KilnHandle program = 0;
    int32_t slot = -1;
    uint32_t stride = 0;     // 0 until the first update read the reflected size
    bool broken = false;     // size unknown / out of bounds / queue walk faulted
    std::vector<std::unique_ptr<EventReg>> regs;
    std::mutex mu;
    std::vector<uint8_t> buffer;  // copies of this step's events, stride bytes each
    uint64_t seen = 0;
};
std::mutex g_ev_mutex;
std::map<std::string, std::unique_ptr<EventType>> g_events;

struct Queue {
    int64_t first = -1, blocks = -1, count = -1, next = -1, shift = -1;
    bool ok = false;
} Q;

int64_t cst(const char* n) {
    int64_t v = -1;
    return g_api->constant(kstr(n), &v) == KILN_OK ? v : -1;
}

// Copies the queue's records (stride bytes each) into out. False when the walk faulted or looked wrong.
bool walk_queue(uintptr_t q, uint32_t stride, std::vector<uint8_t>* out) {
    uint32_t n = 0;
    if (!seh_read_u32(q + static_cast<uintptr_t>(Q.count), &n)) return false;
    if (n == 0) return true;
    if (n > 100000) return false;
    const uint64_t block_size = 1ull << Q.shift;
    uint64_t base = 0;
    uint16_t idx = 0;
    if (!seh_read_u64(q + static_cast<uintptr_t>(Q.blocks), &base) || !seh_read_u16(q + static_cast<uintptr_t>(Q.first), &idx))
        return false;
    uintptr_t block = idx ? static_cast<uintptr_t>(base + (idx - 1) * block_size) : 0;
    uint64_t off = 0;
    const size_t at = out->size();
    out->resize(at + static_cast<size_t>(n) * stride);
    for (uint32_t i = 0; i < n; ++i) {
        if (off + stride > block_size - 8) {
            uint16_t nx = 0;
            if (!block || !seh_read_u16(block + static_cast<uintptr_t>(Q.next), &nx)) return false;
            block = nx ? static_cast<uintptr_t>(base + (nx - 1) * block_size) : 0;
            off = 0;
        }
        if (!block) return false;
        if (!seh_copy(out->data() + at + static_cast<size_t>(i) * stride, reinterpret_cast<const void*>(block + 8 + off), stride))
            return false;
        off += stride;
    }
    return true;
}

void KILN_CALL event_each(KilnProgramCtx*, uint64_t* iter, KilnEntity, void* user) {
    auto* t = static_cast<EventType*>(user);
    if (t->broken || t->slot < 0) return;
    if (!t->stride) {
        const uint32_t real = type_size(fnv1a("keen::ecs::" + t->name));
        uint32_t expected = 0;
        for (const auto& r : t->regs)
            if (r->expected) expected = r->expected;
        if (!real && !expected) {
            logf(KILN_LOG_WARN, "event %s: its size is unknown (no reflection) and no mod gave one: not delivered", t->name.c_str());
            t->broken = true;
            return;
        }
        const uint32_t size = real ? real : expected;
        if (size == 0 || size >= 0x800) {
            logf(KILN_LOG_WARN, "event %s: %u bytes exceeds the supported event size, not delivered",
                 t->name.c_str(), size);
            t->broken = true;
            return;
        }
        for (auto& r : t->regs)
            if (r->expected && r->expected != size) {
                r->size_ok = false;
                mod_logf(r->mod, KILN_LOG_WARN,
                         "on_event(%s): the mod's struct has %u bytes, the game's event %u: not delivered to it (a game "
                         "update changed the layout?)",
                         t->name.c_str(), r->expected, size);
            }
        t->stride = size;
        logf(KILN_LOG_INFO, "event %s: %u bytes (%s), %zu registration(s)", t->name.c_str(), size,
             real ? "the game's reflection" : "the mod's struct size", t->regs.size());
    }
    const uintptr_t q = static_cast<uintptr_t>(iter[t->slot]);
    if (!q) return;
    std::lock_guard lock(t->mu);
    const size_t before = t->buffer.size();
    if (!walk_queue(q, t->stride, &t->buffer)) {
        t->buffer.resize(before);
        logf(KILN_LOG_ERROR, "event %s: reading its queue failed; delivery is switched off", t->name.c_str());
        t->broken = true;
    }
}

bool api_on_event(uint8_t kind, const char* name, uint32_t expected_size, EmfWorldEventCallback cb, void* user) {
    Mod* m = mod_of_address(reinterpret_cast<const void*>(cb));
    if (!m) m = caller_mod(_ReturnAddress());
    if (!name || !*name || !cb) return refuse(m, "on_event", "no event name or callback"), false;
    if (kind != EMF_WORLD_SERVER) {
        unsupported(m, "on_event (client world)", "kiln.world registers programs in the server world only");
        return false;
    }
    if (g_ready_done) return refuse(m, "on_event", "the worlds are set up: call it from emf_mod_load"), false;
    if (!S.world) return unsupported(m, "on_event", "kiln.world is not available"), false;
    std::lock_guard lock(g_ev_mutex);
    auto& t = g_events[name];
    if (!t) {
        if (g_events.size() > 24) {
            g_events.erase(name);
            return refuse(m, "on_event", "24 event types are already registered"), false;
        }
        t = std::make_unique<EventType>();
        t->name = name;
    }
    auto r = std::make_unique<EventReg>();
    r->mod = m;
    r->expected = expected_size;
    r->cb = cb;
    r->user = user;
    t->regs.push_back(std::move(r));
    return true;
}

// kiln-emf's own listeners (E prompts, v25): a registration without a mod, registered before events_on_ready.
bool add_internal_event(const char* name, uint32_t expected_size, EmfWorldEventCallback cb, void* user) {
    if (g_ready_done || !S.world || !name || !cb) return false;
    std::lock_guard lock(g_ev_mutex);
    auto& t = g_events[name];
    if (!t) {
        t = std::make_unique<EventType>();
        t->name = name;
    }
    auto r = std::make_unique<EventReg>();
    r->mod = nullptr;
    r->expected = expected_size;
    r->cb = cb;
    r->user = user;
    t->regs.push_back(std::move(r));
    return true;
}

// ============================================================================================= programs ======
struct Prog {
    Mod* mod;
    std::string name;
    uint8_t kind;
    EmfProgramCallback fn;
    void* user;
    std::vector<std::string> events_in, events_out, singletons, other_read, other_write;
    KilnHandle handle = 0;
    std::atomic<bool> dead{false};
};
std::mutex g_prog_mutex;
std::vector<std::unique_ptr<Prog>> g_progs;

struct CurrentProg {
    Prog* prog;
    KilnProgramCtx* ctx;
    uint64_t* iter;
};
thread_local CurrentProg t_prog{nullptr, nullptr, nullptr};

struct ProgCall {
    EmfProgramCallback fn;
    void* ctx;
    void* user;
};
void prog_thunk(void* p) {
    auto* c = static_cast<ProgCall*>(p);
    c->fn(c->ctx, c->user);
}

void KILN_CALL prog_each(KilnProgramCtx* ctx, uint64_t* iter, KilnEntity, void* user) {
    auto* p = static_cast<Prog*>(user);
    if (p->dead.load(std::memory_order_relaxed)) return;
    const CurrentProg saved = t_prog;
    t_prog = {p, ctx, iter};
    ProgCall c{p->fn, ctx, p->user};
    {
        ContextScope scope(p->mod, nullptr, false);
        const uint64_t t0 = qpc_ns();
        if (!guarded(p->mod, ("its program " + p->name).c_str(), &prog_thunk, &c)) p->dead = true;
        account(p->mod, qpc_ns() - t0);
    }
    t_prog = saved;
}

std::vector<std::string> list(const char* const* l, size_t max = 64) {
    std::vector<std::string> out;
    for (size_t i = 0; l && l[i] && i < max; ++i) out.emplace_back(l[i]);
    return out;
}

bool add_program(Mod* m, const char* name, uint8_t kind, EmfProgramCallback fn, void* user, const EmfProgramDef* def) {
    const char* fnname = def ? "register_program_def" : "register_program";
    if (!name || !*name || !fn) return refuse(m, fnname, "no name or callback"), false;
    if (kind != EMF_WORLD_SERVER) {
        unsupported(m, (std::string(fnname) + " (client world)").c_str(), "kiln.world registers programs in the server world only");
        return false;
    }
    if (g_ready_done) return refuse(m, fnname, "the worlds are set up: call it from emf_mod_load"), false;
    if (!S.world) return unsupported(m, fnname, "kiln.world is not available"), false;
    std::lock_guard lock(g_prog_mutex);
    if (g_progs.size() >= 16) return refuse(m, fnname, "16 mod programs exist"), false;
    for (const auto& p : g_progs)
        if (p->name == name) return refuse(m, fnname, "the name is taken"), false;
    auto p = std::make_unique<Prog>();
    p->mod = m;
    p->name = name;
    p->kind = kind;
    p->fn = fn;
    p->user = user;
    if (def) {
        p->events_in = list(def->events_in);
        p->events_out = list(def->events_out);
        if (def->size >= offsetof(EmfProgramDef, other_read) + sizeof(def->other_read)) {
            p->singletons = list(def->singletons, 16);
            p->other_read = list(def->other_read, 16);
        }
        if (def->size >= offsetof(EmfProgramDef, other_write) + sizeof(def->other_write)) p->other_write = list(def->other_write, 16);
        if (p->events_in.size() + p->events_out.size() > 24) return refuse(m, fnname, "more than 24 events"), false;
        if (!p->other_write.empty() && (S.world->header.size < offsetof(KilnWorldApi, write_other) + sizeof(S.world->write_other) || !S.world->write_other))
            return unsupported(m, "program_write_other", "kiln.world tracked writes are unavailable"), false;
    }
    g_progs.push_back(std::move(p));
    return true;
}

bool api_register_program(const char* name, uint8_t kind, EmfProgramCallback fn, void* user) {
    Mod* m = mod_of_address(reinterpret_cast<const void*>(fn));
    if (!m) m = caller_mod(_ReturnAddress());
    return add_program(m, name, kind, fn, user, nullptr);
}

bool api_register_program_def(const EmfProgramDef* def) {
    Mod* m = def ? mod_of_address(reinterpret_cast<const void*>(def->fn)) : nullptr;
    if (!m) m = caller_mod(_ReturnAddress());
    if (!def || def->size < offsetof(EmfProgramDef, events_out) + sizeof(def->events_out))
        return refuse(m, "register_program_def", "bad definition"), false;
    return add_program(m, def->name, def->world_kind, def->fn, def->user, def);
}

bool in_program(void* ctx, const char* fn, Mod** m) {
    *m = t_prog.prog ? t_prog.prog->mod : caller_mod(_ReturnAddress());
    if (!t_prog.prog || reinterpret_cast<void*>(t_prog.ctx) != ctx) {
        refuse(*m, fn, "called outside the program's own callback", true);
        return false;
    }
    return true;
}

void* api_program_singleton(void* ctx, const char* name) {
    Mod* m;
    if (!name || !in_program(ctx, "program_singleton", &m)) return nullptr;
    const int32_t slot = S.world->program_slot(t_prog.prog->handle, KILN_SLOT_SINGLETON, kstr(name));
    if (slot < 0) return refuse(m, "program_singleton", "not declared in EmfProgramDef.singletons", true), nullptr;
    return reinterpret_cast<void*>(t_prog.iter[slot]);
}

void* api_program_read_other(void* ctx, const char* component, uint32_t entity, uint32_t expected) {
    Mod* m;
    if (!component || !in_program(ctx, "program_read_other", &m)) return nullptr;
    const int32_t slot = S.world->program_slot(t_prog.prog->handle, KILN_SLOT_OTHER_READ, kstr(component));
    if (slot < 0) return refuse(m, "program_read_other", "not declared in EmfProgramDef.other_read", true), nullptr;
    uint32_t size = 0;
    const void* p = S.world->read_other(t_prog.ctx, t_prog.iter[slot], entity, &size);
    if (p && expected && size != expected) {
        refuse(m, "program_read_other", format("%s: the mod expects %u bytes, the game has %u", component, expected, size).c_str(), true);
        return nullptr;
    }
    return const_cast<void*>(p);
}

uint32_t api_read_events(void* ctx, const char* name, uint32_t expected, EmfEventCallback cb, void* user) {
    Mod* m;
    if (!name || !cb || !in_program(ctx, "read_events", &m)) return 0;
    const int32_t slot = S.world->program_slot(t_prog.prog->handle, KILN_SLOT_EVENT_IN, kstr(name));
    if (slot < 0) {
        refuse(m, "read_events", "not declared in events_in", true);
        return 0;
    }
    const uint32_t size = type_size(fnv1a("keen::ecs::" + std::string(name)));
    const uint32_t stride = size ? size : expected;
    if (!stride || stride >= 0x800 || (expected && size && expected != size)) {
        refuse(m, "read_events", "size mismatch or an event size outside 1..2047", true);
        return 0;
    }
    std::vector<uint8_t> buf;
    if (!Q.ok || !walk_queue(static_cast<uintptr_t>(t_prog.iter[slot]), stride, &buf)) return 0;
    const uint32_t n = static_cast<uint32_t>(buf.size() / stride);
    for (uint32_t i = 0; i < n; ++i) cb(buf.data() + static_cast<size_t>(i) * stride, user);
    return n;
}

// The game's own emitter, as kiln.gameplay uses it (gameplay/EmitEvent, JobEventBuffer, JobProgramId, ProgramTime):
// every events-out slot of the iterator holds the program's job; emitEvent(job buffer, hash, size, program id) appends
// a zeroed record and returns it; the caller stamps GameEvent-derived events with the program time.
struct Emitter {
    using EmitFn = void* (*)(void* buffer, uint32_t hash, uint32_t size, uint16_t program);
    using JobProgramIdFn = uint16_t (*)(void* job);
    using JobBufferFn = void* (*)(void* job);
    using ProgramTimeFn = int64_t* (*)(void* ctx, int64_t* out);
    EmitFn emit = nullptr;
    JobProgramIdFn program = nullptr;
    JobBufferFn buffer = nullptr;
    ProgramTimeFn time = nullptr;
    bool ok = false;
} EM;

// events that derive from keen::ecs::Event, not GameEvent: no timeStamp (EMF docs/engine_parts/re_events.md)
bool has_timestamp(std::string_view name) {
    static const char* kNo[] = {"CollisionEvent", "LoginEvent", "RevivedPlayerEvent", "AllItemsRepairedEvent",
                                "DeclinedInteraction", "TestEvent1", "TestEvent2"};
    for (const char* n : kNo)
        if (name == n) return false;
    return true;
}

struct EmitCall {
    Emitter* em;
    void* job;
    void* ctx;
    uint32_t hash, size;
    bool stamp;
    void* out;
};
void emit_thunk(void* p) {
    auto* c = static_cast<EmitCall*>(p);
    void* buf = c->em->buffer(c->job);
    const uint16_t pid = c->em->program(c->job);
    c->out = buf ? c->em->emit(buf, c->hash, c->size, pid) : nullptr;
    if (c->out && c->stamp && c->em->time) {
        int64_t now = 0;
        c->em->time(c->ctx, &now);
        std::memcpy(c->out, &now, 8);
    }
}

void* api_emit_event(void* ctx, const char* name, uint32_t expected) {
    Mod* m;
    if (!name || !in_program(ctx, "emit_event", &m)) return nullptr;
    if (!EM.ok) return unsupported(m, "emit_event", "the game's event emitter is not resolved on this build"), nullptr;
    const int32_t slot = S.world->program_slot(t_prog.prog->handle, KILN_SLOT_EVENT_OUT, kstr(name));
    if (slot < 0) return refuse(m, "emit_event", (std::string(name) + " is not declared in events_out").c_str(), true), nullptr;
    const std::string q = "keen::ecs::" + std::string(name);
    const uint32_t size = type_size(fnv1a(q));
    if (!size) return refuse(m, "emit_event", (std::string(name) + ": the game's size is unknown").c_str(), true), nullptr;
    if (expected && expected != size)
        return refuse(m, "emit_event", format("%s: the mod's struct has %u bytes, the game's event %u", name, expected, size).c_str(), true),
               nullptr;
    if (size >= 0x100) return refuse(m, "emit_event", (std::string(name) + ": events of 256 bytes or more cannot be sent").c_str(), true), nullptr;
    EmitCall c{&EM, reinterpret_cast<void*>(t_prog.iter[slot]), ctx, fnv1a(q), size, has_timestamp(name), nullptr};
    if (!c.job) return nullptr;
    unsigned long code;
    uintptr_t at;
    if (!seh_call(&emit_thunk, &c, &code, &at)) {
        refuse(m, "emit_event", format("the game's emitter faulted (0x%08lx): emit_event is switched off", code).c_str());
        EM.ok = false;
        return nullptr;
    }
    if (!c.out) refuse(m, "emit_event", "the game's event buffer is full", true);
    return c.out;
}

void* api_program_write_other(void* ctx, const char* component, uint32_t entity, uint32_t expected) {
    Mod* m;
    if (!component || !in_program(ctx, "program_write_other", &m)) return nullptr;
    if (S.world->header.size < offsetof(KilnWorldApi, write_other) + sizeof(S.world->write_other) || !S.world->write_other)
        return unsupported(m, "program_write_other", "kiln.world tracked writes are unavailable"), nullptr;
    const int32_t slot = S.world->program_slot(t_prog.prog->handle, KILN_SLOT_OTHER_WRITE, kstr(component));
    if (slot < 0) return refuse(m, "program_write_other", "not declared in EmfProgramDef.other_write", true), nullptr;
    uint32_t size = 0;
    void* data = S.world->write_other(t_prog.ctx, t_prog.iter[slot], entity, &size);
    if (data && expected && size != expected)
        return refuse(m, "program_write_other", "component size mismatch", true), nullptr;
    return data;
}

std::vector<KilnStr> kstrs(const std::vector<std::string>& v) {
    std::vector<KilnStr> o;
    for (const auto& s : v) o.push_back(kstr(s));
    return o;
}

}  // namespace

bool events_add_internal(const char* name, uint32_t expected_size, EmfWorldEventCallback cb, void* user) {
    return add_internal_event(name, expected_size, cb, user);
}

void events_on_ready() {
    g_ready_done = true;
    auto sym = [](const char* n) {
        uintptr_t v = 0;
        return g_api->symbol(kstr(n), &v) == KILN_OK ? v : uintptr_t{0};
    };
    EM.emit = reinterpret_cast<Emitter::EmitFn>(sym("gameplay/EmitEvent"));
    EM.program = reinterpret_cast<Emitter::JobProgramIdFn>(sym("gameplay/JobProgramId"));
    EM.buffer = reinterpret_cast<Emitter::JobBufferFn>(sym("gameplay/JobEventBuffer"));
    EM.time = reinterpret_cast<Emitter::ProgramTimeFn>(sym("gameplay/ProgramTime"));
    EM.ok = EM.emit && EM.program && EM.buffer;
    if (!EM.time) logf(KILN_LOG_WARN, "gameplay/ProgramTime is not resolved: emitted events get no timeStamp");
    Q.first = cst("combat/evq.first");
    Q.blocks = cst("combat/evq.blocks");
    Q.count = cst("combat/evq.count");
    Q.next = cst("combat/evq.next");
    Q.shift = cst("combat/evq.block_shift");
    Q.ok = Q.first >= 0 && Q.blocks >= 0 && Q.count >= 0 && Q.next >= 0 && Q.shift > 4 && Q.shift < 20;
    if (!S.world) return;
    std::lock_guard lock(g_ev_mutex);
    for (auto& [name, t] : g_events) {
        if (!Q.ok) {
            for (auto& r : t->regs) unsupported(r->mod, "on_event", "the event queue layout is not resolved on this build");
            continue;
        }
        const std::string pname = "emf_ev_" + name;
        const KilnStr ev[] = {kstr(t->name)};
        KilnProgramDesc d{};
        d.size = sizeof d;
        d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
        d.name = kstr(pname);
        d.each = &event_each;
        d.user = t.get();
        d.events_in = KilnNames{ev, 1, 0};
        if (S.world->register_program(g_owner, &d, &t->program) != KILN_OK) {
            for (auto& r : t->regs) refuse(r->mod, "on_event", ("kiln.world refused the program for " + name).c_str());
            continue;
        }
        t->slot = S.world->program_slot(t->program, KILN_SLOT_EVENT_IN, ev[0]);
        logf(KILN_LOG_INFO, "on_event %s: program %s registered (%zu registration(s), queue slot %d)", name.c_str(), pname.c_str(),
             t->regs.size(), t->slot);
    }
    std::lock_guard plock(g_prog_mutex);
    for (auto& p : g_progs) {
        const auto ein = kstrs(p->events_in), eout = kstrs(p->events_out), sg = kstrs(p->singletons),
                   orr = kstrs(p->other_read), owr = kstrs(p->other_write);
        KilnProgramDesc d{};
        d.size = sizeof d;
        d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
        d.name = kstr(p->name);
        d.each = &prog_each;
        d.user = p.get();
        d.events_in = KilnNames{ein.data(), static_cast<uint32_t>(ein.size()), 0};
        d.events_out = KilnNames{eout.data(), static_cast<uint32_t>(eout.size()), 0};
        d.singletons_read = KilnNames{sg.data(), static_cast<uint32_t>(sg.size()), 0};
        d.other_read = KilnNames{orr.data(), static_cast<uint32_t>(orr.size()), 0};
        d.other_write = KilnNames{owr.data(), static_cast<uint32_t>(owr.size()), 0};
        if (S.world->register_program(p->mod ? p->mod->owner : g_owner, &d, &p->handle) != KILN_OK) {
            refuse(p->mod, "register_program", ("kiln.world refused program " + p->name).c_str());
            p->dead = true;
            continue;
        }
        mod_logf(p->mod, KILN_LOG_INFO, "program %s registered through kiln.world (%zu event(s) in, %zu singleton(s), %zu other-read)",
                 p->name.c_str(), p->events_in.size(), p->singletons.size(), p->other_read.size());
    }
}

void events_after_step(EmfWorld* w, uint8_t kind) {
    if (kind != EMF_WORLD_SERVER) return;
    std::vector<EventType*> types;
    {
        std::lock_guard lock(g_ev_mutex);
        for (auto& [n, t] : g_events) types.push_back(t.get());
    }
    for (EventType* t : types) {
        std::vector<uint8_t> buf;
        {
            std::lock_guard lock(t->mu);
            if (t->buffer.empty()) continue;
            buf.swap(t->buffer);
        }
        const uint32_t stride = t->stride;
        if (!stride) continue;
        const size_t n = buf.size() / stride;
        t->seen += n;
        for (size_t i = 0; i < n; ++i)
            for (auto& r : t->regs) {
                if (r->dead || !r->size_ok) continue;
                struct Call {
                    EmfWorldEventCallback cb;
                    EmfWorld* w;
                    const void* e;
                    uint32_t size;
                    void* user;
                } c{r->cb, w, buf.data() + i * stride, stride, r->user};
                ContextScope scope(r->mod, w, true);
                const uint64_t t0 = qpc_ns();
                if (!guarded(r->mod, ("an on_event(" + t->name + ") callback").c_str(),
                             [](void* p) {
                                 auto* k = static_cast<Call*>(p);
                                 k->cb(k->w, k->e, k->size, k->user);
                             },
                             &c))
                    r->dead = true;
                account(r->mod, qpc_ns() - t0);
            }
    }
}

void events_world_stopping() {
    std::lock_guard lock(g_ev_mutex);
    for (auto& [name, t] : g_events) {
        std::lock_guard queue_lock(t->mu);
        t->buffer.clear();
    }
}

void events_shutdown() {
    const bool can_remove = S.world && S.world->header.size >= offsetof(KilnWorldApi,unregister_program)+sizeof(S.world->unregister_program)
        && S.world->unregister_program;
    bool drained = can_remove;
    { std::lock_guard lock(g_prog_mutex);
      for (auto& program : g_progs) {
          program->dead = true;
          if (program->handle && (!can_remove || S.world->unregister_program(program->handle) != KILN_OK)) drained=false;
      } }
    { std::lock_guard lock(g_ev_mutex);
      for (auto& [name,event] : g_events) {
          for (auto& registration : event->regs) registration->dead=true;
          if (event->program && (!can_remove || S.world->unregister_program(event->program) != KILN_OK)) drained=false;
      } }
    if (!drained) keep_adapter_mapped();
}

void fill_events(EmfApi* t) {
    t->register_program = &api_register_program;
    t->register_program_def = &api_register_program_def;
    t->emit_event = &api_emit_event;
    t->read_events = &api_read_events;
    t->on_event = &api_on_event;
    t->program_singleton = &api_program_singleton;
    t->program_read_other = &api_program_read_other;
    t->program_write_other = &api_program_write_other;
}

}  // namespace kemf
