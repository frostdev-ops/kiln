// SPDX-License-Identifier: MIT
// ECS program registration (EMF docs/engine_parts/re_custom_programs.md §2, §5-§7, re-verified on svn 1285601):
// kiln.world hooks the game's RegisterAllPrograms (the last program-registration call of the server world setup),
// calls the original, then registers every accepted Kiln program with the game's own RegisterProgram. Names are
// resolved by the engine later, at finalize, by hash; kiln.world validates every name against the world's maps first,
// because one unknown component or event aborts the engine's access resolution for that program half-way.
#include <algorithm>
#include <array>
#include <utility>

#include "safetyhook.hpp"
#include "world_internal.h"

namespace kw {
namespace {

constexpr int kMaxPrograms = 32;
constexpr uint32_t kMaxIterBytes = 4096;
constexpr uint32_t kAllowedFlags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_INIT | KILN_PROGRAM_HOST_ONLY |
                                   KILN_PROGRAM_EXCLUSIVE | KILN_PROGRAM_CHUNKED | KILN_PROGRAM_PINNED |
                                   KILN_PROGRAM_BARRIER;
constexpr uint32_t kEngineSkipped = 0x20;  // entry flag set at finalize: a required singleton is missing

enum ListId {
    L_TPL, L_TPL_OPT, L_READ, L_READ_OPT, L_WRITE, L_WRITE_OPT, L_SREAD, L_SWRITE,
    L_OTPL, L_OREAD, L_OWRITE, L_WITH, L_WITHOUT, L_ANY, L_EIN, L_EOUT, L_COUNT
};
enum MapKind { M_DYN, M_TPL, M_SINGLETON, M_EVENT };
constexpr MapKind kListMap[L_COUNT] = {M_TPL, M_TPL, M_DYN, M_DYN, M_DYN, M_DYN, M_SINGLETON, M_SINGLETON,
                                       M_TPL, M_DYN, M_DYN, M_TPL, M_TPL, M_TPL, M_EVENT, M_EVENT};
constexpr const char* kListName[L_COUNT] = {"tpl", "tpl_optional", "read", "read_optional", "write",
                                            "write_optional", "singletons_read", "singletons_write", "other_tpl",
                                            "other_read", "other_write", "with", "without", "any_of", "events_in",
                                            "events_out"};

struct List {
    std::vector<std::string> names;
    std::vector<NameRef> refs;
    std::vector<int32_t> slot;   // qword index into the iterator per name (-1 = none)
    std::vector<uint16_t> index; // engine type index (dyn/tpl lists) after validation
};

struct Program {
    KilnOwner owner = 0;
    std::string name;
    uint32_t flags = 0;
    KilnProgramEachFn each = nullptr;
    KilnProgramUpdateFn update = nullptr;
    void* user = nullptr;
    bool internal = false;
    List lists[L_COUNT];
    uint64_t sread_opt = 0, swrite_opt = 0;
    EngineDesc desc{};
    uint32_t T = 0, R = 0, W = 0, S = 0, OT = 0, O = 0, EI = 0, EO = 0, iter_size = 0;
    int slot = -1;  // trampoline index
    std::atomic<int> state{KILN_PROGRAM_PENDING};
    std::atomic<uint64_t> updates{0}, entities{0};
    std::atomic<uint32_t> engine_id{0};
    std::atomic<uintptr_t> entry{0};
    std::atomic<bool> fault_logged{false};
};

std::mutex g_mutex;
std::vector<Program*> g_programs;  // never freed: the engine keeps pointers into each descriptor
Program* g_by_slot[kMaxPrograms] = {};
SafetyHookInline g_hook;
std::atomic<bool> g_registered_once{false};
std::atomic<uintptr_t> g_finalize_checked{0};
thread_local Program* t_prog = nullptr;  // the Kiln program whose update runs on this thread

void internal_update(ProgramCtx* ctx);

void fault(Program* p, const char* what, unsigned long code) {
    p->state.store(KILN_PROGRAM_FAULTED);
    if (!p->fault_logged.exchange(true))
        KW_E("program %s: %s (0x%08lx); the program is disabled", p->name.c_str(), what, code);
}

// The engine's partial-iteration check (slice runner, after the update returns): ctx.index must be 0 or the slice's
// entity count. After a fault mid-iteration we finish the slice on paper so the engine does not abort.
void repair(ProgramCtx* ctx) {
    if (ctx->index == 0) return;
    uint64_t count = 0;
    if (seh_read_u64(reinterpret_cast<uintptr_t>(ctx->job) + static_cast<uintptr_t>(L.job_entity_count), &count))
        ctx->index = static_cast<uint32_t>(count);
}

// Counts the engine computed for this program's iterator must equal ours, or our slots would be wrong.
bool counts_match(Program* p, ProgramCtx* ctx) {
    const uint8_t* j = ctx->job;
    const uint32_t t = j[L.job_tpl_count];
    const uint32_t r = *reinterpret_cast<const uint16_t*>(j + L.job_read_count);
    const uint32_t w = *reinterpret_cast<const uint16_t*>(j + L.job_write_count);
    const auto* singletons = *reinterpret_cast<const uint8_t* const*>(j + L.job_singletons);
    const uint32_t s = singletons ? singletons[L.singletons_count] : 0;
    const uint32_t ot = j[L.job_other_tpl_count];
    const uint32_t o = j[L.job_other_count];
    const uint64_t ei = *reinterpret_cast<const uint64_t*>(j + L.job_events_in_count);
    const uint32_t eo = *reinterpret_cast<const uint32_t*>(j + L.job_events_out_count);
    if (t == p->T && r == p->R && w == p->W && s == p->S && ot == p->OT && o == p->O && ei == p->EI && eo == p->EO)
        return true;
    p->state.store(KILN_PROGRAM_FAULTED);
    if (!p->fault_logged.exchange(true))
        KW_E("program %s: the engine's iterator layout differs from kiln.world's (engine T%u R%u W%u S%u OT%u O%u "
             "EI%llu EO%u, kiln T%u R%u W%u S%u OT%u O%u EI%u EO%u); disabled",
             p->name.c_str(), t, r, w, s, ot, o, static_cast<unsigned long long>(ei), eo, p->T, p->R, p->W, p->S,
             p->OT, p->O, p->EI, p->EO);
    return false;
}

void run_each(Program* p, ProgramCtx* ctx) {
    alignas(16) uint64_t it[kMaxIterBytes / 8 + 2];
    auto* kctx = reinterpret_cast<KilnProgramCtx*>(ctx);
    unsigned long code = 0;
    if (!seh_iter_begin(ctx, it, p->iter_size, &code)) {
        fault(p, "iter_begin faulted", code);
        return;
    }
    if (p->flags & KILN_PROGRAM_SYSTEM) {
        if (!seh_each(p->each, kctx, it, 0, p->user, &code)) fault(p, "update faulted", code);
        return;
    }
    bool faulted = false;
    for (;;) {
        bool more = false;
        if (!seh_iter_next(ctx, it, p->iter_size, &more, &code)) {
            fault(p, "iter_next faulted", code);
            repair(ctx);
            return;
        }
        if (!more) break;
        p->entities.fetch_add(1, std::memory_order_relaxed);
        if (faulted) continue;
        uint32_t e = 0;
        fn.current_entity(ctx, &e);
        if (!seh_each(p->each, kctx, it, e, p->user, &code)) {
            faulted = true;
            fault(p, "update faulted", code);
        }
    }
}

void dispatch(int i, void* raw) {
    Program* p = g_by_slot[i];
    auto* ctx = static_cast<ProgramCtx*>(raw);
    if (!p || p->state.load(std::memory_order_relaxed) != KILN_PROGRAM_ACTIVE) return;  // iterate none: allowed
    p->updates.fetch_add(1, std::memory_order_relaxed);
    if (!counts_match(p, ctx)) return;
    t_in_program = true;
    t_prog = p;
    if (p->internal) {
        internal_update(ctx);
    } else if (p->each) {
        run_each(p, ctx);
    } else {
        unsigned long code = 0;
        if (!seh_update(p->update, reinterpret_cast<KilnProgramCtx*>(ctx), p->user, &code)) {
            fault(p, "update faulted", code);
            repair(ctx);
        }
    }
    t_prog = nullptr;
    t_in_program = false;
}

template <int I>
void trampoline(void* ctx) {
    dispatch(I, ctx);
}
template <int... I>
constexpr std::array<void (*)(void*), sizeof...(I)> make_trampolines(std::integer_sequence<int, I...>) {
    return {&trampoline<I>...};
}
constexpr auto kTrampolines = make_trampolines(std::make_integer_sequence<int, kMaxPrograms>{});

// ---- layout ----

std::string qualified(const std::string& n) { return "keen::ecs::" + n; }

// Assigns slots to a group of lists: sorted by key across the lists (ties: list order), consecutive from `base`.
template <class Key>
uint32_t assign(Program& p, std::initializer_list<int> ids, uint32_t base, uint32_t stride, bool sort, Key key) {
    struct Item {
        int list;
        size_t i;
    };
    std::vector<Item> items;
    for (int id : ids)
        for (size_t i = 0; i < p.lists[id].names.size(); ++i) items.push_back({id, i});
    if (sort) std::stable_sort(items.begin(), items.end(), [&](const Item& a, const Item& b) {
        return key(a.list, a.i) < key(b.list, b.i);
    });
    for (size_t k = 0; k < items.size(); ++k) p.lists[items[k].list].slot[items[k].i] = static_cast<int32_t>(base + k * stride);
    return static_cast<uint32_t>(items.size());
}

// Slot layout of the engine iterator (re_custom_programs.md §6.1; checked on 1285601 against the game's teleport
// program, which iterates [ctx][R:NpcCommon][W x8 by name][S x3][O:PlayerSpawnPoint] in 0x1B0 bytes).
template <class Key>
void layout(Program& p, Key key) {
    for (auto& l : p.lists) l.slot.assign(l.names.size(), -1);
    uint32_t at = 1;
    p.T = assign(p, {L_TPL, L_TPL_OPT}, at, 1, true, key);
    at += p.T;
    p.R = assign(p, {L_READ, L_READ_OPT}, at, 1, true, key);
    at += p.R;
    p.W = assign(p, {L_WRITE, L_WRITE_OPT}, at, 6, true, key);
    at += 6 * p.W;
    p.S = assign(p, {L_SREAD, L_SWRITE}, at, 1, false, key);
    at += p.S;
    p.OT = assign(p, {L_OTPL}, at, 1, true, key);
    at += p.OT;
    const uint32_t oread = assign(p, {L_OREAD}, at, 1, true, key);
    at += oread;
    const uint32_t owrite = assign(p, {L_OWRITE}, at, 1, true, key);
    at += owrite;
    p.O = oread + owrite;
    p.EI = assign(p, {L_EIN}, at, 1, false, key);
    if (p.EI != 1)
        for (auto& s : p.lists[L_EIN].slot) s = -1;  // queue order of several events is not verified
    at += p.EI;
    p.EO = assign(p, {L_EOUT}, at, 1, false, key);
    at += p.EO;
    p.iter_size = at * 8;
}

std::vector<std::vector<int32_t>> slots_of(const Program& p) {
    std::vector<std::vector<int32_t>> v;
    for (auto& l : p.lists) v.push_back(l.slot);
    return v;
}

bool valid_name(std::string_view n, bool allow_scope) {
    if (n.empty() || n.size() > 120) return false;
    for (char c : n) {
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' ||
                        (allow_scope && c == ':');
        if (!ok) return false;
    }
    return allow_scope || n.find("::") == std::string_view::npos;
}

Program* by_handle(KilnHandle h) {
    std::lock_guard lock(g_mutex);
    if (h == 0 || h > g_programs.size()) return nullptr;
    return g_programs[h - 1];
}

// ---- validation and registration (server thread, inside the world setup) ----

uintptr_t map_base(uintptr_t registry, MapKind k) {
    switch (k) {
        case M_DYN: return L.reg_components >= 0 ? registry + L.reg_components : 0;
        case M_TPL: return L.reg_templates >= 0 ? registry + L.reg_templates : 0;
        case M_SINGLETON: return F.singletons ? registry + L.reg_singletons : 0;
        case M_EVENT: return F.events ? registry + L.reg_events : 0;
    }
    return 0;
}

bool validate(Program& p, uintptr_t world, std::string& why) {
    const uintptr_t registry = registry_of(world);
    for (int id = 0; id < L_COUNT; ++id) {
        List& l = p.lists[id];
        l.index.assign(l.names.size(), 0);
        if (l.names.empty()) continue;
        const MapKind mk = kListMap[id];
        const uintptr_t map = map_base(registry, mk);
        if (!map) {
            why = std::string(kListName[id]) + ": cannot check these names on this build";
            return false;
        }
        for (size_t i = 0; i < l.names.size(); ++i) {
            const std::string& n = l.names[i];
            const uint32_t key = mk == M_SINGLETON ? fnv1a(n) : fnv1a(n, fnv1a("keen::ecs::"));
            uint16_t v = 0;
            if (map_find_u16(map, key, &v)) {
                l.index[i] = v;
                continue;
            }
            if (mk == M_SINGLETON) {
                const uint64_t opt = id == L_SREAD ? p.sread_opt : p.swrite_opt;
                if (!(opt >> i & 1))
                    KW_W("program %s: singleton '%s' is not registered yet; if it is still missing when the world "
                         "starts, the engine skips the program",
                         p.name.c_str(), n.c_str());
                continue;
            }
            why = std::string(kListName[id]) + ": unknown " +
                  (mk == M_EVENT ? "event" : mk == M_TPL ? "template component" : "component") + " '" + n + "'";
            return false;
        }
    }
    // The engine orders TPL/READ/WRITE/OTHER slots by type index; we laid them out by name. They must agree.
    const auto provisional = slots_of(p);
    layout(p, [&](int list, size_t i) { return static_cast<uint32_t>(p.lists[list].index[i]); });
    if (slots_of(p) != provisional) {
        why = "the engine's type order differs from name order for these components";
        layout(p, [&](int list, size_t i) { return qualified(p.lists[list].names[i]); });
        return false;
    }
    return true;
}

void register_into(uintptr_t world) {
    std::lock_guard lock(g_mutex);
    const auto count_at = reinterpret_cast<const uint16_t*>(world + L.programs_count);
    for (Program* p : g_programs) {
        const int st = p->state.load();
        if (st == KILN_PROGRAM_REFUSED || st == KILN_PROGRAM_FAULTED) continue;
        std::string why;
        if (!validate(*p, world, why)) {
            p->state.store(KILN_PROGRAM_REFUSED);
            KW_E("program %s refused: %s", p->name.c_str(), why.c_str());
            continue;
        }
        const uint16_t before = *count_at;
        if (before >= L.programs_max) {
            p->state.store(KILN_PROGRAM_REFUSED);
            KW_E("program %s refused: the world's program table is full (%u/%lld)", p->name.c_str(), before,
                 static_cast<long long>(L.programs_max));
            continue;
        }
        bool ok = false;
        unsigned long code = 0;
        if (!seh_register_program(fn.register_program, world, &p->desc, &ok, &code)) {
            p->state.store(KILN_PROGRAM_REFUSED);
            KW_E("program %s: RegisterProgram faulted (0x%08lx)", p->name.c_str(), code);
            continue;
        }
        const uint16_t after = *count_at;
        const uintptr_t entry = world + L.programs_table + static_cast<uintptr_t>(after - 1) * L.programs_stride;
        if (!ok || after != before + 1 || *reinterpret_cast<const EngineDesc* const*>(entry) != &p->desc) {
            p->state.store(KILN_PROGRAM_REFUSED);
            KW_E("program %s: the engine refused it (duplicate name?) (ok=%d, count %u -> %u)", p->name.c_str(), ok,
                 before, after);
            continue;
        }
        p->entry.store(entry);
        p->engine_id.store(*reinterpret_cast<const uint16_t*>(entry + L.entry_id));
        p->state.store(KILN_PROGRAM_ACTIVE);
        KW_I("program %s registered as #%u (T%u R%u W%u S%u OT%u O%u EI%u EO%u, iterator %u bytes, flags 0x%x)",
             p->name.c_str(), p->engine_id.load(), p->T, p->R, p->W, p->S, p->OT, p->O, p->EI, p->EO, p->iter_size,
             p->flags);
    }
    KW_I("world 0x%llx: %u Kiln programs; the program table holds %u of %lld",
         static_cast<unsigned long long>(world), static_cast<unsigned>(g_programs.size()), *count_at,
         static_cast<long long>(L.programs_max));
}

void on_world(uintptr_t world) {
    const uintptr_t storage = storage_of(world);
    const uint8_t kind = *reinterpret_cast<const uint8_t*>(storage + L.storage_kind);
    if (kind != 0) {  // the client and prediction worlds of a client: expected, Kiln's programs run in the server world
        KW_I("RegisterAllPrograms for a world of kind %u: not the server world; no Kiln programs there", kind);
        return;
    }
    if (*reinterpret_cast<const uint8_t*>(world) != 0 ||
        (L.may_finalize >= 0 && *reinterpret_cast<const uint8_t*>(world + L.may_finalize) != 0)) {
        KW_E("server world 0x%llx is already finalizing its programs; no Kiln programs registered",
             static_cast<unsigned long long>(world));
        return;
    }
    g_world.store(world);
    g_registered_once.store(true);
    register_into(world);
}

void __fastcall hk_register_all(uintptr_t world) {
    g_hook.call<void>(world);
    try {
        on_world(world);
    } catch (const std::exception& e) {
        KW_E("program registration failed: %s", e.what());
    } catch (...) {
        KW_E("program registration failed");
    }
}

void internal_update(ProgramCtx* ctx) {
    const uintptr_t storage = *reinterpret_cast<const uintptr_t*>(ctx->job);
    const uintptr_t world = storage - static_cast<uintptr_t>(L.world_storage);
    if (world != g_world.load(std::memory_order_relaxed)) {
        static std::atomic<bool> warned{false};
        if (!warned.exchange(true))
            KW_W("own program runs in world 0x%llx, expected 0x%llx; following it", static_cast<unsigned long long>(world),
                 static_cast<unsigned long long>(g_world.load()));
        g_world.store(world);
    }
    g_internal_runs.fetch_add(1, std::memory_order_release);
    teleport_in_program(reinterpret_cast<KilnProgramCtx*>(ctx));
}

}  // namespace

bool programs_init() {
    if (!F.programs) return false;
    g_hook = safetyhook::create_inline(reinterpret_cast<void*>(fn.register_all), reinterpret_cast<void*>(&hk_register_all));
    if (!g_hook) {
        KW_E("hooking RegisterAllPrograms at %p failed", reinterpret_cast<void*>(fn.register_all));
        return false;
    }
    return true;
}

// First Pre after the world finalized: report programs the engine skips (missing required singletons).
void programs_after_first_step(uintptr_t world) {
    if (g_finalize_checked.exchange(world) == world) return;
    std::lock_guard lock(g_mutex);
    for (Program* p : g_programs) {
        const uintptr_t entry = p->entry.load();
        if (p->state.load() != KILN_PROGRAM_ACTIVE || !entry) continue;
        const uint32_t flags = *reinterpret_cast<const uint32_t*>(entry + L.entry_flags);
        if (flags & kEngineSkipped) {
            p->state.store(KILN_PROGRAM_SKIPPED);
            KW_E("program %s: the engine skips it (a required singleton is missing; engine flags 0x%x)",
                 p->name.c_str(), flags);
        }
    }
}

KilnResult register_program(KilnOwner owner, const KilnProgramDesc* d, KilnHandle* out, bool internal) {
    if (out) *out = 0;
    if (!F.programs) return KILN_ERR_UNAVAILABLE;
    if (!d || d->size < sizeof(KilnProgramDesc)) return KILN_ERR_INVALID_ARGUMENT;
    const std::string name(sv(d->name));
    auto refuse = [&](const char* why) {
        KW_E("program '%s' refused: %s", name.c_str(), why);
        return KILN_ERR_INVALID_ARGUMENT;
    };
    if (g_registered_once.load()) {
        KW_E("program '%s' refused: the server world has registered its programs already (register from "
             "kiln_plugin_main or on_ready)", name.c_str());
        return KILN_ERR_UNAVAILABLE;
    }
    if (!valid_name(name, false) || name.size() > 63) return refuse("name must be 1-63 characters [A-Za-z0-9_]");
    if (d->flags & ~kAllowedFlags) return refuse("unsupported flags");
    if (!internal && (!d->each) == (!d->update)) return refuse("set exactly one of each / update");

    auto* p = new Program();  // never freed (the engine keeps &p->desc)
    p->owner = owner;
    p->name = name;
    p->flags = d->flags;
    p->each = d->each;
    p->update = d->update;
    p->user = d->user;
    p->internal = internal;
    p->sread_opt = d->singletons_read_optional;
    p->swrite_opt = d->singletons_write_optional;
    const KilnNames* src[L_COUNT] = {&d->tpl, &d->tpl_optional, &d->read, &d->read_optional, &d->write,
                                     &d->write_optional, &d->singletons_read, &d->singletons_write, &d->other_tpl,
                                     &d->other_read, &d->other_write, &d->with, &d->without, &d->any_of,
                                     &d->events_in, &d->events_out};
    for (int id = 0; id < L_COUNT; ++id) {
        const KilnNames& n = *src[id];
        if (n.count && !n.items) {
            delete p;
            return refuse("a list has a count but no items");
        }
        if (n.count > 160) {
            delete p;
            return refuse("a list is too long");
        }
        List& l = p->lists[id];
        l.names.reserve(n.count);
        for (uint32_t i = 0; i < n.count; ++i) {
            std::string s(sv(n.items[i]));
            if (!valid_name(s, kListMap[id] == M_SINGLETON)) {
                delete p;
                KW_E("program '%s' refused: bad name '%s' in %s", name.c_str(), s.c_str(), kListName[id]);
                return KILN_ERR_INVALID_ARGUMENT;
            }
            l.names.push_back(std::move(s));
        }
    }
    // Duplicates inside a slot group would give two slots for one bit.
    const std::vector<std::vector<int>> groups = {{L_TPL, L_TPL_OPT}, {L_READ, L_READ_OPT, L_WRITE, L_WRITE_OPT},
                                                  {L_SREAD}, {L_SWRITE}, {L_OTPL}, {L_OREAD}, {L_OWRITE},
                                                  {L_EIN}, {L_EOUT}, {L_WITH, L_WITHOUT, L_ANY}};
    for (const auto& g : groups) {
        std::vector<std::string> all;
        for (int id : g) all.insert(all.end(), p->lists[id].names.begin(), p->lists[id].names.end());
        std::sort(all.begin(), all.end());
        if (std::adjacent_find(all.begin(), all.end()) != all.end()) {
            delete p;
            return refuse("a name appears twice in one access group (read/write lists, a singleton list, ...)");
        }
    }
    const size_t tpl = p->lists[L_TPL].names.size() + p->lists[L_TPL_OPT].names.size();
    const size_t rw = p->lists[L_READ].names.size() + p->lists[L_READ_OPT].names.size() +
                      p->lists[L_WRITE].names.size() + p->lists[L_WRITE_OPT].names.size();
    const size_t singles = p->lists[L_SREAD].names.size() + p->lists[L_SWRITE].names.size();
    const size_t filters = p->lists[L_WITH].names.size() + p->lists[L_ANY].names.size();
    if (tpl > 8 || rw > 42 || p->lists[L_SREAD].names.size() > 64 || p->lists[L_SWRITE].names.size() > 64 ||
        p->lists[L_OTPL].names.size() > 9 ||
        p->lists[L_OREAD].names.size() + p->lists[L_OWRITE].names.size() > 92 ||
        p->lists[L_EIN].names.size() + p->lists[L_EOUT].names.size() > 24) {
        delete p;
        return refuse("over an engine limit (tpl 8, read+write 42, singletons 64 per list, other_tpl 9, other 92, "
                      "events 24)");
    }
    (void)singles;
    const bool system = d->flags & KILN_PROGRAM_SYSTEM;
    if (system && (tpl || rw || filters || p->lists[L_WITHOUT].names.size())) {
        delete p;
        return refuse("a SYSTEM program iterates no entities: no tpl/read/write/with/without/any_of lists");
    }
    if (!system && !tpl && !rw && !filters) {
        delete p;
        return refuse("an entity query without required components matches every entity; add a list or use SYSTEM");
    }
    layout(*p, [&](int list, size_t i) { return qualified(p->lists[list].names[i]); });
    if (p->iter_size > kMaxIterBytes) {
        delete p;
        return refuse("iterator over 4096 bytes");
    }
    for (auto& l : p->lists) {
        l.refs.clear();
        for (auto& s : l.names) l.refs.push_back({s.data(), s.size()});
    }
    auto list = [&](int id) { return NameListE{p->lists[id].refs.data(), p->lists[id].refs.size()}; };
    EngineDesc& e = p->desc;
    e.name = {p->name.data(), p->name.size()};
    e.tpl = list(L_TPL);
    e.tpl_opt = list(L_TPL_OPT);
    e.read = list(L_READ);
    e.read_opt = list(L_READ_OPT);
    e.write = list(L_WRITE);
    e.write_opt = list(L_WRITE_OPT);
    e.sys_read = list(L_SREAD);
    e.sys_read_opt = p->sread_opt;
    e.sys_write = list(L_SWRITE);
    e.sys_write_opt = p->swrite_opt;
    e.other_tpl = list(L_OTPL);
    e.other_read = list(L_OREAD);
    e.other_write = list(L_OWRITE);
    e.with = list(L_WITH);
    e.without = list(L_WITHOUT);
    e.any_of = list(L_ANY);
    e.events_in = list(L_EIN);
    e.events_out = list(L_EOUT);
    e.flags = p->flags;
    e.interval = 1;  // 0 divides by zero in the stage scheduler

    std::lock_guard lock(g_mutex);
    if (g_programs.size() >= kMaxPrograms) {
        delete p;
        KW_E("program '%s' refused: kiln.world holds %d programs at most", name.c_str(), kMaxPrograms);
        return KILN_ERR_UNAVAILABLE;
    }
    for (Program* q : g_programs)
        if (q->name == name) {
            delete p;
            KW_E("program '%s' refused: the name is taken", name.c_str());
            return KILN_ERR_INVALID_ARGUMENT;
        }
    p->slot = static_cast<int>(g_programs.size());
    e.update = kTrampolines[p->slot];
    g_by_slot[p->slot] = p;
    g_programs.push_back(p);
    if (out) *out = static_cast<KilnHandle>(g_programs.size());
    KW_I("program %s accepted (%s, flags 0x%x, iterator %u bytes)", name.c_str(),
         internal ? "internal" : (p->each ? "each" : "raw"), p->flags, p->iter_size);
    return KILN_OK;
}

int32_t program_slot(KilnHandle h, int32_t kind, KilnStr name) {
    Program* p = by_handle(h);
    if (!p) return -1;
    const std::string_view n = sv(name);
    int ids[2] = {-1, -1};
    switch (kind) {
        case KILN_SLOT_TPL: ids[0] = L_TPL, ids[1] = L_TPL_OPT; break;
        case KILN_SLOT_READ: ids[0] = L_READ, ids[1] = L_READ_OPT; break;
        case KILN_SLOT_WRITE: ids[0] = L_WRITE, ids[1] = L_WRITE_OPT; break;
        case KILN_SLOT_SINGLETON: ids[0] = L_SREAD, ids[1] = L_SWRITE; break;
        case KILN_SLOT_OTHER_TPL: ids[0] = L_OTPL; break;
        case KILN_SLOT_OTHER_READ: ids[0] = L_OREAD; break;
        case KILN_SLOT_OTHER_WRITE: ids[0] = L_OWRITE; break;
        case KILN_SLOT_EVENT_IN: ids[0] = L_EIN; break;
        case KILN_SLOT_EVENT_OUT: ids[0] = L_EOUT; break;
        default: return -1;
    }
    std::lock_guard lock(g_mutex);
    for (int id : ids) {
        if (id < 0) continue;
        const List& l = p->lists[id];
        for (size_t i = 0; i < l.names.size(); ++i)
            if (l.names[i] == n) return l.slot[i];
    }
    return -1;
}

KilnResult program_info(KilnHandle h, KilnProgramInfo* out) {
    Program* p = by_handle(h);
    if (!p) return KILN_ERR_NOT_FOUND;
    if (!out || out->size < sizeof(KilnProgramInfo)) return KILN_ERR_INVALID_ARGUMENT;
    out->state = static_cast<uint32_t>(p->state.load());
    out->engine_id = p->engine_id.load();
    out->iter_size = p->iter_size;
    out->updates = p->updates.load();
    out->entities = p->entities.load();
    return KILN_OK;
}

uint32_t programs_registered() {
    std::lock_guard lock(g_mutex);
    return static_cast<uint32_t>(g_programs.size());
}

uint32_t engine_program_count(uintptr_t world) {
    if (!world || L.programs_count < 0) return 0;
    return *reinterpret_cast<const uint16_t*>(world + L.programs_count);
}

// ---- helpers inside an update ----

KilnResult iter_begin(KilnProgramCtx* c, uint64_t* iter, uint32_t size) {
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || !iter || !t_in_program) return KILN_ERR_WRONG_THREAD;
    // The engine ignores the size: the buffer must hold the whole iterator.
    if (t_prog && size < t_prog->iter_size) return KILN_ERR_BUFFER_TOO_SMALL;
    fn.iter_begin(ctx, iter, size);
    return KILN_OK;
}

bool iter_next(KilnProgramCtx* c, uint64_t* iter, uint32_t size) {
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || !iter || !t_in_program) return false;
    return fn.iter_next(ctx, iter, size);
}

KilnEntity current_entity(KilnProgramCtx* c) {
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || !t_in_program) return 0;
    uint32_t e = 0;
    fn.current_entity(ctx, &e);
    return e;
}

void* command_buffer(KilnProgramCtx* c) {
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || !t_in_program || !F.queue) return nullptr;
    return *reinterpret_cast<void* const*>(ctx->job + L.job_cmdbuf);
}

std::atomic<bool> g_queue_broken{false};

void* queue_push(KilnProgramCtx* c, uint8_t kind, uint32_t size, KilnEntity entity) {
    void* cb = command_buffer(c);
    if (!cb || size < 16 || size > 0x8000 || kind == 0x11) return nullptr;  // 0x11 is fatal in the queue
    if (g_queue_broken.load(std::memory_order_relaxed)) return nullptr;
    void* r = nullptr;
    if (!seh_queue_push(cb, static_cast<uint8_t*>(cb) + L.cmdbuf_queue, kind, size, entity, &r)) {
        if (!g_queue_broken.exchange(true))
            KW_E("QueuePush faulted (kind %u, %u bytes, entity %u); command-buffer pushes are off from now on",
                 kind, size, entity);
        return nullptr;
    }
    return r;
}

KilnResult queue_add(KilnProgramCtx* c, KilnEntity entity, uint32_t hash, const void* data, uint32_t size) {
    if (!entity || !hash || !data || size == 0 || size > 4096) return KILN_ERR_INVALID_ARGUMENT;
    if (!command_buffer(c)) return t_in_program ? KILN_ERR_UNAVAILABLE : KILN_ERR_WRONG_THREAD;
    const uint32_t total = static_cast<uint32_t>(L.rec_data) + size;
    auto* r = static_cast<uint8_t*>(queue_push(c, 3, total, entity));
    if (!r) return KILN_ERR_BUFFER_TOO_SMALL;  // full: retry next step
    std::memcpy(r + L.rec_hashes, &hash, 4);
    const uint16_t sz = static_cast<uint16_t>(size);
    std::memcpy(r + L.rec_sizes, &sz, 2);
    std::memcpy(r + L.rec_data, data, size);
    return KILN_OK;
}

KilnResult queue_remove(KilnProgramCtx* c, KilnEntity entity, uint32_t hash) {
    if (!entity || !hash) return KILN_ERR_INVALID_ARGUMENT;
    if (!command_buffer(c)) return t_in_program ? KILN_ERR_UNAVAILABLE : KILN_ERR_WRONG_THREAD;
    auto* r = static_cast<uint8_t*>(queue_push(c, 4, static_cast<uint32_t>(L.tp_remove_size), entity));
    if (!r) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(r + L.rec_hashes, &hash, 4);
    return KILN_OK;
}

void mark_written(KilnProgramCtx* c, const uint64_t* w) {
    if (!c || !w || !t_in_program || !F.write || !w[5]) return;
    fn.mark_written(reinterpret_cast<void*>(w[5]), reinterpret_cast<void*>(w[1]), reinterpret_cast<void*>(w[2]),
                    reinterpret_cast<void*>(w[3]), static_cast<uint16_t>(w[4]), static_cast<uint8_t>(w[4] >> 16));
}

const void* read_other(KilnProgramCtx* c, uint64_t accessor, KilnEntity entity, uint32_t* out_size) {
    if (out_size) *out_size = 0;
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || !t_in_program || !F.read_other || !accessor || !entity) return nullptr;
    void* storage = *reinterpret_cast<void* const*>(ctx->job + L.job_storage);
    Slice s{};
    fn.read_other(&s, storage, reinterpret_cast<void*>(accessor), entity);
    if (out_size) *out_size = static_cast<uint32_t>(s.size);
    return s.ptr;
}

}  // namespace kw
