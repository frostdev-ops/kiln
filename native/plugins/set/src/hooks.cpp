// SPDX-License-Identifier: MIT
// kiln-set: SET's native hooks (hooks.h) on kiln.hooks. Kiln's own implementation of SET's documented behaviour
// (Shrouded Engine Tweaks api-reference "Native hooks", MIT): one kiln.hooks mid hook per target at its entry reads
// the Win64 argument positions (rcx/rdx/r8/r9 or xmm0-3, then the stack). It first applies the target's rules
// (rules.cpp; the rule bookkeeping below is adapted from SET 0.2.0 runtime/hooks.cpp, Copyright (c) 2026 Cacaida, MIT,
// licenses/ShroudedEngineTweaks-LICENSE.txt): changed arguments go back into the registers or the caller's stack
// slots, a blocked call returns to the game's caller from the mid hook (rax or xmm0, trampoline_rsp = rsp). Then it
// copies the declared values into each active subscriber's bounded queue. Lua gets the copies on the next tick (drain).
// A return change needs code after the original: a kiln.hooks raw link with the target's own signature (AfterThunk),
// which only catalog targets have. SET does all of this in its own assembly stub; Kiln has no such stub.
// A capture that faults disables its target's observers (kiln.hooks' guard), the game runs on.
#include "hooks.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <array>
#include <bit>
#include <cctype>
#include <cstring>
#include <deque>
#include <map>
#include <memory>
#include <mutex>

#include "kiln_api.h"
#include "level.h"
#include "services/kiln_hooks.h"

namespace kset {
void bindHooks(const KilnApi* api, KilnOwner owner);
}

namespace {
const KilnApi* g_api = nullptr;
const KilnHooksApi* H = nullptr;
KilnOwner g_owner = 0;
hk::Log g_log = nullptr;
int64_t g_qpc0 = 0, g_qpf = 1;
std::atomic<uint64_t> g_seq{0};
std::atomic<uint64_t> g_problems{0};

struct Sub {
    int id = -1;
    std::string key;
    std::vector<hk::Arg> args;
    uint32_t capacity = 256;
    uintptr_t fn = 0;
    bool active = true;
    size_t cost = 0;
    std::mutex mx;  // capture threads vs. the Lua thread
    std::vector<hk::Event> events;
    std::vector<uint8_t> payload;
    uint64_t generation = 1, delivered = 0, dropped = 0, droppedSinceDrain = 0, discarded = 0, lockWaits = 0;
    uint32_t highWater = 0;
};

// A retained rule registration. The calling threads read only rule, floatMask and declMask, which never change once
// the slot is published in the target's order word; the rest belongs to the control side, under g_mx.
struct RuleSlot {
    std::unique_ptr<rules::Rule> rule;
    std::string key;
    std::array<hk::Arg, hk::kMaxArgs> args{};
    uint32_t count = 0;
    uint8_t floatMask = 0, declMask = 0;  // positions read from XMM; positions a rule reads at all
    uint32_t order = 0;
    uint64_t seq = 0, loggedFaults = 0;
    bool active = false, faulted = false;
};

struct Target {
    uintptr_t fn = 0;
    int index = 0;  // in g_target_list: rule ids are index * kMaxRules + slot
    KilnHandle hook = 0;
    std::atomic<bool> faulted{false};
    bool timing = false;
    std::atomic<uint64_t> calls{0}, captureTicks{0}, timedCalls{0};
    std::atomic<unsigned> live{0};  // active subscribers: a target with rules only never takes g_mx in a call
    std::vector<Sub*> subs;  // guarded by g_mx for changes; read by captures under g_mx (shared, short)
    std::array<RuleSlot, rules::kMaxRules> rslots;
    int ruleCount = 0;
    std::atomic<uint64_t> order{0};  // the active rules, in order (rules::packOrder)
    std::atomic<bool> ruleFaultPending{false};
    std::atomic<uint64_t> ruleCalls{0};
    std::atomic<bool> afterLink{false};  // a return-change link (AfterThunk) is installed on this target
    KilnHandle afterHook = 0;
};

// What the entry hook left for the return-change link of the same call, on the same thread.
struct Pending {
    Target* target = nullptr;
    uint64_t word = 0;
    uint16_t mask = 0;
};
thread_local Pending t_pending;

std::mutex g_mx;
std::map<uintptr_t, std::unique_ptr<Target>> g_targets;  // never erased: hooks and rule storage stay until exit
std::vector<Target*> g_target_list;
uint64_t g_rule_seq = 0;
std::map<int, std::unique_ptr<Sub>> g_subs;
std::map<std::string, uintptr_t> g_patterns;  // pattern text -> target (our own patch changes the entry bytes)
std::map<uintptr_t, std::string> g_reserved;
std::deque<std::string> g_diag, g_pending_log;
int g_next_sub = 0;
size_t g_bytes = 0;

void note(const std::string& s) {
    g_problems.fetch_add(1);
    g_diag.push_back(s);
    while (g_diag.size() > 32) g_diag.pop_front();
    g_pending_log.push_back(s);
}

bool read_memory(uintptr_t p, void* out, size_t n) {
    return p && n <= UINTPTR_MAX - p && H && H->read && H->read(p, out, n) == KILN_OK;
}

bool arg_bits(const KilnRegisters* r, int i, bool fp, uint64_t& out) {
    if (i < 4) {
        if (fp) {
            const KilnXmm* x[] = {&r->xmm0, &r->xmm1, &r->xmm2, &r->xmm3};
            uint64_t v;
            std::memcpy(&v, x[i]->bytes, 8);
            out = v;
            return true;
        }
        const uintptr_t g[] = {r->rcx, r->rdx, r->r8, r->r9};
        out = g[i];
        return true;
    }
    // At the entry [rsp] is the return address, the home space follows, then argument 5 at rsp + 0x28.
    const uintptr_t off = 0x28 + 8 * uintptr_t(i - 4);
    return r->rsp <= UINTPTR_MAX - off && read_memory(r->rsp + off, &out, sizeof out);
}

void capture(Target* t, KilnRegisters* r) {
    LARGE_INTEGER a;
    QueryPerformanceCounter(&a);
    std::lock_guard lock(g_mx);
    for (Sub* s : t->subs) {
        if (!s->active) continue;
        hk::Event e{};
        e.seq = g_seq.fetch_add(1) + 1;
        e.qpc = a.QuadPart;
        // Each of the eight arguments can independently carry a snapshot or string.
        std::array<uint8_t, hk::kMaxArgs * hk::kMaxSnapshot> local;
        size_t used = 0;
        for (size_t i = 0; i < s->args.size(); ++i) {
            const hk::Arg& g = s->args[i];
            const bool fp = g.kind == hk::F32 || g.kind == hk::F64;
            hk::Value& v = e.v[i];
            if (g.kind == hk::Skip) continue;
            uint64_t bits = 0;
            auto fault = [&] {
                t->faulted = true;
                note(s->key + ": invalid argument read; target disabled");
            };
            if (!arg_bits(r, int(i), fp, bits)) { fault(); return; }
            switch (g.kind) {
            case hk::Bool: v.bits = bits & 0xff; break;
            case hk::U8: v.bits = bits & 0xff; break;
            case hk::U16: v.bits = bits & 0xffff; break;
            case hk::U32: case hk::Entity: v.bits = bits & 0xffffffffu; break;
            case hk::I8: v.bits = uint64_t(int64_t(int8_t(bits))); break;
            case hk::I16: v.bits = uint64_t(int64_t(int16_t(bits))); break;
            case hk::I32: v.bits = uint64_t(int64_t(int32_t(bits))); break;
            case hk::F32: v.bits = bits & 0xffffffffu; break;
            case hk::EntityPtr:
                if (bits) {
                    uint32_t id = 0;
                    if (!read_memory(bits, &id, sizeof id)) { fault(); return; }
                    v.bits = id; v.len = 4;
                }
                break;
            case hk::CStr:
                if (bits) {
                    uint32_t n = 0;
                    for (; n < hk::kMaxStr; ++n) {
                        uint8_t c = 0;
                        if (bits > UINTPTR_MAX - n || !read_memory(bits + n, &c, 1)) { fault(); return; }
                        if (!c) break;
                        local[used + n] = c;
                    }
                    v.bits = 1;
                    v.off = uint32_t(used);
                    v.len = n;
                    used += n;
                }
                break;
            case hk::Snapshot:
                if (bits) {
                    const uint32_t n = std::min(g.type.size(), hk::kMaxSnapshot);
                    v.bits = 1;
                    v.off = uint32_t(used);
                    v.len = n;
                    if (!read_memory(bits, local.data() + used, n)) { fault(); return; }
                    used += n;
                }
                break;
            default: v.bits = bits; break;
            }
        }
        if (!s->mx.try_lock()) {
            s->lockWaits++;
            s->mx.lock();
        }
        if (s->events.size() >= s->capacity) {
            s->dropped++;
            s->droppedSinceDrain++;
        } else {
            for (size_t i = 0; i < s->args.size(); ++i)
                if ((s->args[i].kind == hk::CStr || s->args[i].kind == hk::Snapshot) && e.v[i].bits) e.v[i].off += uint32_t(s->payload.size());
            s->payload.insert(s->payload.end(), local.data(), local.data() + used);
            s->events.push_back(e);
            s->highWater = std::max<uint32_t>(s->highWater, uint32_t(s->events.size()));
        }
        s->mx.unlock();
    }
    if (t->timing) {
        LARGE_INTEGER b;
        QueryPerformanceCounter(&b);
        t->captureTicks.fetch_add(uint64_t(b.QuadPart - a.QuadPart), std::memory_order_relaxed);
        t->timedCalls.fetch_add(1, std::memory_order_relaxed);
    }
}

#ifdef _WIN32
int memoryFault(DWORD code) {
    return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_IN_PAGE_ERROR || code == STATUS_GUARD_PAGE_VIOLATION
               ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH;
}
// A plain copy: unlike kiln.hooks' write, it never lifts page protection, so a rule can't write read-only memory.
bool write_memory(uintptr_t at, const void* src, size_t n) {
    __try {
        std::memcpy(reinterpret_cast<void*>(at), src, n);
        return true;
    } __except (memoryFault(GetExceptionCode())) {
        return false;
    }
}
bool on_stack(uintptr_t at, size_t n) {
    const auto* tib = reinterpret_cast<const NT_TIB*>(NtCurrentTeb());
    return at >= reinterpret_cast<uintptr_t>(tib->StackLimit) && n <= reinterpret_cast<uintptr_t>(tib->StackBase) &&
           at <= reinterpret_cast<uintptr_t>(tib->StackBase) - n;
}
#else
}  // namespace
// Host tests supply these (fake faults, a fake stack range). Unset: plain copies, and nothing is on the stack.
namespace kset::hooktest {
bool (*write)(uintptr_t at, const void* src, size_t n) = nullptr;
bool (*on_stack)(uintptr_t at, size_t n) = nullptr;
}
namespace {
bool write_memory(uintptr_t at, const void* src, size_t n) {
    if (kset::hooktest::write) return kset::hooktest::write(at, src, n);
    std::memcpy(reinterpret_cast<void*>(at), src, n);
    return true;
}
bool on_stack(uintptr_t at, size_t n) { return kset::hooktest::on_stack && kset::hooktest::on_stack(at, n); }
#endif

// A changed argument goes where the original reads it: the register the mid hook restores, or the caller's stack slot.
bool write_arg(KilnRegisters* r, int i, bool fp, uint64_t bits) {
    if (i >= 4) {
        const uintptr_t off = 0x28 + 8 * uintptr_t(i - 4);
        return r->rsp <= UINTPTR_MAX - off && write_memory(r->rsp + off, &bits, sizeof bits);
    }
    if (fp) {
        KilnXmm* x[] = {&r->xmm0, &r->xmm1, &r->xmm2, &r->xmm3};
        std::memcpy(x[i]->bytes, &bits, 8);
        return true;
    }
    uintptr_t* g[] = {&r->rcx, &r->rdx, &r->r8, &r->r9};
    *g[i] = uintptr_t(bits);
    return true;
}

// Stage, then commit: the rules run on a copy of the arguments, which is written back only if no rule faulted.
void apply_rules(Target& t, KilnRegisters* r, uint64_t word, rules::Result& res) {
    uint8_t slots[rules::kMaxRules];
    rules::Rule* rs[rules::kMaxRules];
    const int n = rules::unpackOrder(word, slots);
    uint8_t fp = 0, used = 0;
    for (int i = 0; i < n; ++i) {
        const auto& s = t.rslots[slots[i]];
        rs[i] = s.rule.get();
        fp |= s.floatMask;
        used |= s.declMask;
    }
    rules::Frame in{}, out;
    for (int p = 0; p < hk::kMaxArgs; ++p)
        if ((used >> p & 1) && !arg_bits(r, p, fp >> p & 1, in.a[p])) return;  // an unreadable stack slot: the call is left alone
    t.ruleCalls.fetch_add(1, std::memory_order_relaxed);
    if (rules::apply(rs, n, in, out, &res) >= 0) {
        if (!t.ruleFaultPending.exchange(true)) g_problems.fetch_add(1);
        return;
    }
    if (res.blocked) return;
    for (int p = 0; p < hk::kMaxArgs; ++p)
        if (out.a[p] != in.a[p]) write_arg(r, p, fp >> p & 1, out.a[p]);
}

// The return value of a call whose entry marked return changes. The rules are the ones the entry saw; their storage is
// never freed, so a rule removed meanwhile still finishes this call.
uint64_t finish_after(const Pending& p, uint64_t bits) {
    uint8_t slots[rules::kMaxRules];
    rules::Rule* rs[rules::kMaxRules];
    const int n = rules::unpackOrder(p.word, slots);
    for (int i = 0; i < n; ++i) rs[i] = p.target->rslots[slots[i]].rule.get();
    return rules::after(rs, n, p.mask, bits);
}

// On every call of a hooked function: the rules first, so observers see the arguments the original gets, and blocked
// calls too.
void KILN_CALL on_mid(KilnRegisters* r, void* user) {
    auto* t = static_cast<Target*>(user);
    t->calls.fetch_add(1, std::memory_order_relaxed);
    if (t->afterLink.load(std::memory_order_relaxed)) t_pending = {};
    const uint64_t word = t->order.load(std::memory_order_acquire);
    rules::Result res;
    if (word >> 60) apply_rules(*t, r, word, res);
    if (t->live.load(std::memory_order_acquire) && !t->faulted.load(std::memory_order_relaxed)) capture(t, r);
    if (res.blocked) {
        // The call doesn't run. At the entry [rsp] is the game's return address: resume there (kiln_hooks.h).
        if (res.fp) {
            std::memset(r->xmm0.bytes, 0, sizeof r->xmm0.bytes);
            std::memcpy(r->xmm0.bytes, &res.ret, 8);
        } else {
            r->rax = uintptr_t(res.ret);
        }
        r->trampoline_rsp = r->rsp;
        return;
    }
    if (res.after && t->afterLink.load(std::memory_order_relaxed)) t_pending = {t, word, res.after};
}

// ---- return-change links: one handler per catalog target that allows it, with that function's own signature ----
// combat_xp (contracts.cpp): (u32 level, optional level*, f32 xp modifier, u32 players, context) -> u32 in rax. The
// integer positions and stack slots 5 to 8 pass through as the caller left them; position 3 is read from xmm2.
using CombatXpFn = uint64_t(KILN_CALL*)(uint64_t, uint64_t, float, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t);
void* g_xp_original = nullptr;
std::atomic<Target*> g_xp_target{nullptr};
uint64_t KILN_CALL combat_xp_after(uint64_t a, uint64_t b, float c, uint64_t d, uint64_t e, uint64_t f, uint64_t g, uint64_t h) {
    const Pending p = std::exchange(t_pending, {});
    uint64_t ret = reinterpret_cast<CombatXpFn>(g_xp_original)(a, b, c, d, e, f, g, h);
    if (p.mask && p.target && p.target == g_xp_target.load(std::memory_order_relaxed)) ret = finish_after(p, ret);
    return ret;
}

bool raw_links() {
    return H && H->header.size >= offsetof(KilnHooksApi, raw_detour) + sizeof(H->raw_detour) && H->raw_detour;
}

int hexDigit(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}
}  // namespace

namespace {
// The active, unfaulted rules sorted by mod order, then registration, as the target's order word. One atomic store, so
// a call sees either the old order or the new one.
void publish_order(Target& t) {
    uint8_t slots[rules::kMaxRules];
    int n = 0;
    for (int i = 0; i < t.ruleCount; ++i)
        if (t.rslots[size_t(i)].active && !t.rslots[size_t(i)].faulted) slots[n++] = uint8_t(i);
    std::sort(slots, slots + n, [&](uint8_t a, uint8_t b) {
        const auto &x = t.rslots[a], &y = t.rslots[b];
        return x.order != y.order ? x.order < y.order : x.seq < y.seq;
    });
    t.order.store(rules::packOrder(slots, n), std::memory_order_release);
}

// Under g_mx. A rule that faulted in a call switched itself off there; here it is recorded and leaves the order.
void collect_rule_faults() {
    for (Target* t : g_target_list) {
        if (!t->ruleFaultPending.exchange(false)) continue;
        for (int i = 0; i < t->ruleCount; ++i) {
            auto& r = t->rslots[size_t(i)];
            const uint64_t faults = r.rule->faults;
            if (faults == r.loggedFaults) continue;
            r.loggedFaults = faults;
            r.faulted = true;
            g_diag.push_back("Rule " + r.key + " turned off after an invalid field read or write; restart to retry it");
            while (g_diag.size() > 32) g_diag.pop_front();
            g_pending_log.push_back(g_diag.back());
        }
        publish_order(*t);
    }
}

void relive(Target& t) {
    unsigned n = 0;
    for (const Sub* s : t.subs) n += s->active;
    t.live.store(n, std::memory_order_release);
}

int fail(const std::string& key, std::string& err, std::string message) {
    err = std::move(message);
    note(key + ": " + err);
    return -1;
}

bool same_arg(const hk::Arg& a, const hk::Arg& b) {
    return a.kind == b.kind && (a.kind != hk::Snapshot || a.type.p == b.type.p);
}

// Every non-skipped position agrees with the target's active observers and rules.
bool agrees(Target& t, const std::string& key, const std::vector<hk::Arg>& args, std::string& err) {
    auto check = [&](const hk::Arg* other, size_t n, const std::string& theirs) {
        for (size_t i = 0; i < args.size() && i < n; ++i)
            if (args[i].kind != hk::Skip && other[i].kind != hk::Skip && !same_arg(args[i], other[i])) {
                fail(key, err, "argument " + std::to_string(i + 1) + " disagrees with " + theirs);
                return false;
            }
        return true;
    };
    for (Sub* o : t.subs)
        if (o->active && !check(o->args.data(), o->args.size(), o->key)) return false;
    for (int i = 0; i < t.ruleCount; ++i) {
        const auto& r = t.rslots[size_t(i)];
        if (r.active && !check(r.args.data(), r.count, r.key)) return false;
    }
    return true;
}

bool valid_args(const std::vector<hk::Arg>& args, uint32_t capacity, std::string& err, size_t* payload = nullptr) {
    if (args.size() > size_t(hk::kMaxArgs) || !capacity || capacity > hk::kMaxQueue) {
        err = "hooks need a target, 0..8 arguments and a queue capacity of 1..4096";
        return false;
    }
    size_t bytes = 0;
    for (const auto& a : args) {
        if (a.kind > hk::CStr || (a.kind == hk::Snapshot && (!a.type || !a.type.size() ||
            a.type.size() > hk::kMaxSnapshot || (a.type.prim() != refl::Struct && a.type.prim() != refl::StaticArray) ||
            !refl::plain(a.type)))) {
            err = "hook snapshots require a plain fixed-size struct or array of at most 1024 bytes";
            return false;
        }
        bytes += a.kind == hk::Snapshot ? a.type.size() : a.kind == hk::CStr ? hk::kMaxStr : 0;
    }
    if (payload) *payload = bytes;
    return true;
}

// Refusals shared by observers and rules: Kiln's own hooks, and entries too close to another hooked one.
bool admissible(uintptr_t fn, const std::string& key, std::string& err) {
    if (auto it = g_reserved.find(fn); it != g_reserved.end()) {
        fail(key, err, "target is reserved by Kiln (" + it->second + ")");
        return false;
    }
    auto overlaps = [fn](uintptr_t other) { return (fn > other ? fn - other : other - fn) < 16; };
    for (const auto& [at, what] : g_reserved) if (overlaps(at)) {
        err = "target overlaps a reserved Kiln hook (" + what + ")";
        return false;
    }
    for (const auto& [at, unused] : g_targets) if (at != fn && overlaps(at)) {
        err = "target overlaps an existing entry hook";
        return false;
    }
    return true;
}

// The target for fn, hooked now if it is new. nullptr with err set.
Target* target_for(uintptr_t fn, const std::string& key, std::string& err) {
    if (auto it = g_targets.find(fn); it != g_targets.end()) return it->second.get();
    if (g_targets.size() >= size_t(hk::kMaxTargets) || !H) {
        fail(key, err, H ? "too many hook targets" : "native hooks are unavailable: kiln.hooks is not running");
        return nullptr;
    }
    auto nt = std::make_unique<Target>();
    nt->fn = fn;
    nt->index = int(g_target_list.size());
    Target* t = nt.get();
    if (H->mid(g_owner, fn, 0, &on_mid, t, &t->hook) != KILN_OK) {
        fail(key, err, "kiln.hooks could not hook the target");
        return nullptr;
    }
    g_targets.emplace(fn, std::move(nt));
    g_target_list.push_back(t);
    return t;
}
}  // namespace

void kset::bindHooks(const KilnApi* api, KilnOwner owner) {
    g_api = api;
    g_owner = owner;
    H = static_cast<const KilnHooksApi*>(api->service(KilnStr{KILN_HOOKS_SERVICE, sizeof(KILN_HOOKS_SERVICE) - 1}, 1));
    if (H && H->header.size < sizeof(KilnHooksApi)) H = nullptr;
    LARGE_INTEGER f, n;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&n);
    g_qpf = f.QuadPart ? f.QuadPart : 1;
    g_qpc0 = n.QuadPart;
    rules::setEnv({&read_memory, &write_memory, &on_stack});
    // Targets Kiln itself hooks for the runtime: refused, like SET's own reserved targets.
    for (const char* s : {"SimStep", "types/ProgramRunner"}) {
        uintptr_t a = 0;
        if (api->symbol(KilnStr{s, std::strlen(s)}, &a) == KILN_OK && a) g_reserved[a] = s;
    }
}

void hk::setLog(Log log) { g_log = log; }
void hk::flushLogs() {
    std::deque<std::string> q;
    {
        std::lock_guard lock(g_mx);
        collect_rule_faults();
        q.swap(g_pending_log);
    }
    for (const auto& s : q)
        if (g_log) g_log((LV_WARN "Hooks: " + s).c_str());
}
std::vector<std::string> hk::diagnostics() {
    std::lock_guard lock(g_mx);
    return {g_diag.begin(), g_diag.end()};
}
uint64_t hk::problems() { return g_problems.load(); }
double hk::seconds(int64_t qpc) { return double(qpc - g_qpc0) / double(g_qpf); }
size_t hk::retainedBytes() {
    std::lock_guard lock(g_mx);
    return g_bytes;
}
void hk::reserve(uintptr_t fn, const char* what) {
    std::lock_guard lock(g_mx);
    if (fn) g_reserved[fn] = what;
}
void hk::restoreCode(uintptr_t, uint8_t*, size_t) {}
void hk::refuse(const std::string& message) {
    std::lock_guard lock(g_mx);
    note(message);
}

// Pattern rules as SET documents them: whitespace-separated hex pairs and ?? wildcards, at most 256 bytes, at least
// one exact byte, exactly one match in .text. kiln.hooks scans; a pattern that matched before keeps its target, since
// the mid hook itself changes the entry bytes.
namespace {
// The pattern as normalized text ("48 89 ?? 57") and as bytes (-1 for ??). False with err set for a bad one.
bool parse_pattern(std::string_view text, std::string& norm, std::vector<int>& bytes, std::string& err) {
    norm.clear();
    bytes.clear();
    bool exact = false;
    for (size_t i = 0; i < text.size();) {
        if (isspace(static_cast<unsigned char>(text[i]))) { ++i; continue; }
        if (i + 2 > text.size() || (i + 2 < text.size() && !isspace(static_cast<unsigned char>(text[i + 2])))) {
            err = "malformed pattern: use hex byte pairs and ?? separated by spaces";
            return false;
        }
        const char a = text[i], b = text[i + 1];
        i += 2;
        if (a == '?' && b == '?') {
            norm += "?? ";
            bytes.push_back(-1);
        } else if (hexDigit(a) < 0 || hexDigit(b) < 0) {
            err = "malformed pattern: use hex byte pairs and ?? separated by spaces";
            return false;
        } else {
            norm += a, norm += b, norm += ' ';
            bytes.push_back(hexDigit(a) * 16 + hexDigit(b));
            exact = true;
        }
        if (bytes.size() > 256) {
            err = "pattern is longer than 256 bytes";
            return false;
        }
    }
    if (!exact) {
        err = "pattern needs at least one exact byte";
        return false;
    }
    if (!norm.empty()) norm.pop_back();
    return true;
}
}  // namespace

bool hk::matchAt(uintptr_t imageBase, uintptr_t at, std::string_view text, std::string& err) {
    std::string norm;
    std::vector<int> bytes;
    err.clear();
    if (!parse_pattern(text, norm, bytes, err)) return false;
    if (!H) {
        err = "native hooks are unavailable: kiln.hooks is not running";
        return false;
    }
    const uintptr_t base = H->image_base ? H->image_base() : imageBase;
    const size_t size = H->image_size ? H->image_size() : 0;
    uint8_t live[256];
    if (!base || at < base || at - base > size || bytes.size() > size - (at - base) || !read_memory(at, live, bytes.size())) {
        err = "outside the game image";
        return false;
    }
    for (size_t i = 0; i < bytes.size(); ++i)
        if (bytes[i] >= 0 && live[i] != uint8_t(bytes[i])) return false;
    return true;
}

uintptr_t hk::findPattern(uintptr_t, std::string_view text, std::string& err) {
    std::string norm;
    std::vector<int> bytes;
    if (!parse_pattern(text, norm, bytes, err)) return 0;
    {
        std::lock_guard lock(g_mx);
        if (auto it = g_patterns.find(norm); it != g_patterns.end()) return it->second;
    }
    if (!H) {
        err = "native hooks are unavailable: kiln.hooks is not running";
        return 0;
    }
    uintptr_t hits[2] = {};
    uint32_t n = 0;
    const KilnResult r = H->scan(KilnStr{norm.data(), norm.size()}, hits, 2, &n);
    if (r == KILN_ERR_NOT_FOUND || n == 0) {
        err = "pattern not found in the game's code";
        return 0;
    }
    if (n > 1) {
        err = "pattern is ambiguous: " + std::to_string(n) + " matches";
        return 0;
    }
    if (r != KILN_OK || !hits[0]) {
        err = "native hook scan failed";
        return 0;
    }
    std::lock_guard lock(g_mx);
    g_patterns[norm] = hits[0];
    return hits[0];
}

int hk::subscribe(uintptr_t fn, const std::string& key, const std::vector<Arg>& args, uint32_t capacity, std::string& err) {
    std::lock_guard lock(g_mx);
    size_t payload = 0;
    if (!fn) {
        err = "hooks need a target, 0..8 arguments and a queue capacity of 1..4096";
        return -1;
    }
    if (!valid_args(args, capacity, err, &payload)) return -1;
    // Include the batch being delivered while captures fill the next queue.
    const size_t cost = 2 * size_t(capacity) * (sizeof(Event) + payload);
    if (!admissible(fn, key, err)) return -1;
    Target* t = nullptr;
    Sub* reuse = nullptr;
    if (auto it = g_targets.find(fn); it != g_targets.end()) {
        t = it->second.get();
        if (t->faulted) return fail(key, err, "target faulted earlier; restart the game");
        for (Sub* old : t->subs)
            if (!old->active && (!reuse || old->key == key)) reuse = old;
        if (!reuse && t->subs.size() >= kMaxSubs) return fail(key, err, "too many registrations on this target");
        if (!agrees(*t, key, args, err)) return -1;
    }
    if (cost > kMemoryBudget - (g_bytes - (reuse ? reuse->cost : 0))) return fail(key, err, "hook memory budget exhausted");
    if (!t && !(t = target_for(fn, key, err))) return -1;
    if (reuse) {
        g_bytes -= reuse->cost;
        reuse->cost = cost;
        g_bytes += cost;
        reuse->key = key;
        reuse->args = args;
        reuse->capacity = capacity;
        // Release capacity from a previous, larger declaration before accounting the new one.
        std::vector<Event>().swap(reuse->events);
        std::vector<uint8_t>().swap(reuse->payload);
        reuse->events.reserve(capacity);
        reuse->payload.reserve(size_t(capacity) * payload);
        reuse->active = true;
        ++reuse->generation;
        relive(*t);
        return reuse->id;
    }
    g_bytes += cost;
    auto s = std::make_unique<Sub>();
    s->id = ++g_next_sub;
    s->key = key;
    s->args = args;
    s->capacity = capacity;
    s->cost = cost;
    s->fn = fn;
    s->events.reserve(capacity);
    s->payload.reserve(size_t(capacity) * payload);
    t->subs.push_back(s.get());
    const int id = s->id;
    g_subs.emplace(id, std::move(s));
    relive(*t);
    return id;
}

// ---- rules: the control side (adapted from SET 0.2.0 runtime/hooks.cpp) ----
namespace {

bool num_loc(uint8_t prim, uint32_t size, rules::Loc& l) {
    using namespace refl;
    if (prim == Bool) { l.num = rules::Boolean; l.width = 1; return true; }
    if (prim >= U8 && prim <= S64) {
        l.num = (prim - U8) % 2 ? rules::Signed : rules::Unsigned;
        l.width = uint8_t(1 << ((prim - U8) / 2));
        return true;
    }
    if (prim == F32 || prim == F64) { l.num = rules::Float; l.width = prim == F32 ? 4 : 8; return true; }
    if (prim >= Enum && prim <= Bitmask64 && (size == 1 || size == 2 || size == 4 || size == 8)) {
        l.num = rules::Unsigned;
        l.width = uint8_t(size);
        return true;
    }
    return false;
}

// A rule's value at l is the one its argument declaration describes.
bool declared(const rules::Loc& l, const std::vector<hk::Arg>& args, std::string& err) {
    if (l.arg >= args.size() || args[l.arg].kind == hk::Skip) {
        err = "rule uses argument " + std::to_string(l.arg + 1) + ", which isn't declared";
        return false;
    }
    rules::Loc want;
    bool ok;
    if (l.field) {
        const auto& a = args[l.arg];
        ok = (a.kind == hk::EntityPtr && l.offset == 0 && l.width == 4 && l.num == rules::Unsigned) ||
             (a.kind == hk::Snapshot && uint64_t(l.offset) + l.width <= a.type.size());
    } else {
        ok = hk::argLoc(args[l.arg], l.arg, want) && want == l;
    }
    if (!ok) err = "rule value doesn't match argument " + std::to_string(l.arg + 1) + "'s declaration";
    return ok;
}

bool listed(const rules::Loc& l, const std::vector<rules::Loc>& fields) {
    return std::find(fields.begin(), fields.end(), l) != fields.end();
}

bool check_rule(const hk::RuleDecl& d, std::string& err) {
    if (!valid_args(d.args, 1, err)) return false;
    const auto& s = d.shape;
    auto no = [&](const char* why) { err = why; return false; };
    if (s.conds > rules::kMaxConds || s.changes > rules::kMaxChanges || d.params.size() > size_t(rules::kMaxParams) ||
        d.list.size() > size_t(rules::kMaxList))
        return no("rules hold at most 8 conditions, 8 changes, 8 params and 64 ids");
    auto operand = [&](const rules::Operand& o) {
        return o.src != rules::Param || o.param < d.params.size() ? true : no("rule uses an unset param");
    };
    if (s.block) {
        if (!d.block) return no("blocking a call needs a catalog target that allows it");
        if (s.changes || s.afters) return no("a rule that blocks changes nothing: the original doesn't run");
        if (!operand(s.ret)) return false;
    } else if (!s.changes && !s.afters) {
        return no("a rule needs at least one change, a block or an after");
    }
    if (s.afters > rules::kMaxChanges) return no("rules hold at most 8 after changes");
    if (s.afters && !d.after) return no("changing a return value needs a catalog target that allows it");
    for (int i = 0; i < s.afters; ++i) {
        if (!(s.after[i].at == s.retAt)) return no("after changes take the return kind");
        if (!operand(s.after[i].v)) return false;
    }
    for (int i = 0; i < s.conds; ++i)
        if ((!listed(s.cond[i].at, d.fields) && !listed(s.cond[i].at, d.reads) && !declared(s.cond[i].at, d.args, err)) ||
            (s.cond[i].cmp != rules::In && !operand(s.cond[i].v)))
            return false;
    for (int i = 0; i < s.changes; ++i) {
        const auto& at = s.change[i].at;
        if (at.field && !listed(at, d.fields))
            return no(d.fields.empty() ? "changing a field needs a catalog target" : "the catalog doesn't let rules change that field");
        if ((!at.field && !declared(at, d.args, err)) || !operand(s.change[i].v)) return false;
    }
    return true;
}

std::string mod_of(const std::string& key) { return key.substr(0, key.find('/')); }

// Another mod's active rule on t that changes a value slot n changes too.
std::string conflict_with(const Target& t, int n) {
    const auto& me = t.rslots[size_t(n)];
    for (int i = 0; i < t.ruleCount; ++i) {
        const auto& r = t.rslots[size_t(i)];
        if (i != n && r.active && mod_of(r.key) != mod_of(me.key) && rules::writesSame(r.rule->shape, me.rule->shape))
            return r.key;
    }
    return {};
}

bool key_active(const std::string& key) {
    for (const Target* t : g_target_list) {
        for (const Sub* s : t->subs) if (s->active && s->key == key) return true;
        for (int j = 0; j < t->ruleCount; ++j) if (t->rslots[size_t(j)].active && t->rslots[size_t(j)].key == key) return true;
    }
    return false;
}

// A retained slot with the same key, arguments and shape is reused; otherwise a new one is made. -1 with err set.
int rule_slot(Target& t, const hk::RuleDecl& d, std::string& err) {
    for (int i = 0; i < t.ruleCount; ++i) {
        auto& r = t.rslots[size_t(i)];
        if (r.active || r.key != d.key || r.count != d.args.size() || !(r.rule->shape == d.shape) ||
            !std::equal(d.args.begin(), d.args.end(), r.args.begin(), same_arg))
            continue;
        if (r.faulted) return fail(d.key, err, "this rule faulted; restart the game before trying it again");
        return i;
    }
    if (t.ruleCount == rules::kMaxRules) return fail(d.key, err, "hook target has 15 retained rules; restart to reclaim them");
    if (sizeof(rules::Rule) > hk::kMemoryBudget - g_bytes) return fail(d.key, err, "rule storage would exceed the 64 MiB budget");
    auto& r = t.rslots[size_t(t.ruleCount)];
    r.rule = std::make_unique<rules::Rule>();
    r.rule->shape = d.shape;
    r.key = d.key;
    r.count = uint32_t(d.args.size());
    std::copy(d.args.begin(), d.args.end(), r.args.begin());
    for (size_t p = 0; p < d.args.size(); ++p) {
        if (d.args[p].kind == hk::F32 || d.args[p].kind == hk::F64) r.floatMask |= uint8_t(1 << p);
        if (d.args[p].kind != hk::Skip) r.declMask |= uint8_t(1 << p);
    }
    for (const auto& f : d.fields) r.declMask |= uint8_t(1 << f.arg);  // a catalog field's pointer may be a skip
    for (const auto& f : d.reads) r.declMask |= uint8_t(1 << f.arg);
    g_bytes += sizeof(rules::Rule);
    return t.ruleCount++;
}

RuleSlot* rule_by_id(int id, Target** target = nullptr) {
    if (id < 0 || size_t(id / rules::kMaxRules) >= g_target_list.size()) return nullptr;
    Target& t = *g_target_list[size_t(id / rules::kMaxRules)];
    const int i = id % rules::kMaxRules;
    if (target) *target = &t;
    return i < t.ruleCount ? &t.rslots[size_t(i)] : nullptr;
}

// The return-change link for a target, installed once and kept. Below the entry hook in the chain (lower priority),
// so the entry hook has already left this call's pending changes for it.
bool install_after(Target& t, hk::AfterThunk thunk, const std::string& key, std::string& err) {
    if (t.afterLink.load()) return true;
    std::string why;
    if (!hk::afterAvailable(thunk, why)) return fail(key, err, "changing a return value isn't available: " + why) >= 0;
    Target* other = g_xp_target.load();
    if (other && other != &t) return fail(key, err, "the return-change link is already on another function") >= 0;
    static const char label[] = "kiln-set/combat_xp after";
    KilnHookOrder order{};
    order.size = sizeof order;
    order.priority = -1000;
    order.label = KilnStr{label, sizeof label - 1};
    g_xp_target.store(&t);
    if (H->raw_detour(g_owner, t.fn, &order, reinterpret_cast<void*>(&combat_xp_after), &g_xp_original, &t.afterHook) != KILN_OK) {
        g_xp_target.store(nullptr);
        return fail(key, err, "kiln.hooks could not install the return-change link") >= 0;
    }
    t.afterLink.store(true, std::memory_order_release);
    return true;
}

}  // namespace

bool hk::kindOf(std::string_view name, Kind& out) {
    static const std::pair<const char*, Kind> kinds[] = {
        {"skip", Skip}, {"bool", Bool}, {"u8", U8}, {"u16", U16}, {"u32", U32}, {"u64", U64}, {"ptrsize", U64},
        {"i8", I8}, {"i16", I16}, {"i32", I32}, {"i64", I64}, {"f32", F32}, {"f64", F64}, {"entity", Entity},
        {"entity*", EntityPtr}, {"cstr", CStr}};
    for (const auto& k : kinds)
        if (name == k.first) { out = k.second; return true; }
    return false;
}

bool hk::argLoc(const Arg& a, uint8_t pos, rules::Loc& out) {
    out = {};
    out.arg = pos;
    switch (a.kind) {
    case Bool: return num_loc(refl::Bool, 1, out);
    case U8: case U16: case U32: case U64: return num_loc(uint8_t(refl::U8 + 2 * (a.kind - U8)), 0, out);
    case I8: case I16: case I32: case I64: return num_loc(uint8_t(refl::S8 + 2 * (a.kind - I8)), 0, out);
    case F32: return num_loc(refl::F32, 4, out);
    case F64: return num_loc(refl::F64, 8, out);
    case Entity: return num_loc(refl::U32, 4, out);
    default: return false;
    }
}

bool hk::fieldLoc(const Arg& a, uint8_t pos, std::string_view path, rules::Loc& out, std::string& err) {
    out = {};
    out.arg = pos;
    out.field = true;
    if (a.kind == EntityPtr && path.empty()) return num_loc(refl::U32, 4, out);
    if (a.kind != Snapshot) { err = "argument " + std::to_string(pos + 1) + " doesn't point at a reflected type"; return false; }
    refl::Type leaf;
    if (!refl::path(a.type, path, out.offset, leaf)) {
        err = std::string(a.type.qname()) + " has no field '" + std::string(path) + "'";
        return false;
    }
    if (num_loc(leaf.prim(), leaf.size(), out)) return true;
    err = "field '" + std::string(path) + "' isn't a number";
    return false;
}

bool hk::validRule(const RuleDecl& d, std::string& err) {
    std::lock_guard lock(g_mx);
    err.clear();
    if (check_rule(d, err)) return true;
    note(d.key + ": " + err);
    return false;
}

bool hk::rulesAvailable(std::string& why) {
    why = H ? "" : "kiln.hooks is not running";
    return H != nullptr;
}

bool hk::afterAvailable(AfterThunk thunk, std::string& why) {
    why.clear();
    if (!H) why = "kiln.hooks is not running";
    else if (thunk == NoThunk) why = "Kiln has no return-change link for this function";
    else if (!raw_links()) why = "it needs kiln.hooks 2 (raw links)";
    return why.empty();
}

int hk::addRule(uintptr_t fn, const RuleDecl& d, std::string& err, std::string& conflict) {
    std::lock_guard lock(g_mx);
    err.clear();
    conflict.clear();
    collect_rule_faults();
    if (!fn || d.key.empty()) return fail(d.key, err, "hook target and registration key must not be empty");
    if (!check_rule(d, err)) { note(d.key + ": " + err); return -1; }
    if (!admissible(fn, d.key, err)) return -1;
    if (key_active(d.key)) return fail(d.key, err, "hook name is already active: " + d.key);
    if (auto it = g_targets.find(fn); it != g_targets.end() && !agrees(*it->second, d.key, d.args, err)) return -1;
    Target* t = target_for(fn, d.key, err);
    if (!t) return -1;
    if (d.shape.afters && !install_after(*t, d.thunk, d.key, err)) return -1;
    const int n = rule_slot(*t, d, err);
    if (n < 0) return -1;
    auto& r = t->rslots[size_t(n)];
    for (int i = 0; i < rules::kMaxParams; ++i) rules::setParam(*r.rule, i, size_t(i) < d.params.size() ? d.params[size_t(i)] : 0);
    rules::setList(*r.rule, d.list.data(), uint32_t(d.list.size()));
    r.order = d.order;
    r.seq = ++g_rule_seq;
    r.active = true;
    r.rule->active = true;
    conflict = conflict_with(*t, n);
    publish_order(*t);
    return t->index * rules::kMaxRules + n;
}

void hk::removeRule(int id) {
    std::lock_guard lock(g_mx);
    Target* t = nullptr;
    RuleSlot* r = rule_by_id(id, &t);
    if (!r || !r->active) return;
    r->active = false;
    r->rule->active = false;
    publish_order(*t);
}

bool hk::setParam(int id, int i, double v) {
    std::lock_guard lock(g_mx);
    RuleSlot* r = rule_by_id(id);
    if (!r || !r->active || i < 0 || i >= rules::kMaxParams) return false;
    rules::setParam(*r->rule, i, v);
    return true;
}

bool hk::setList(int id, const std::vector<uint32_t>& ids) {
    std::lock_guard lock(g_mx);
    RuleSlot* r = rule_by_id(id);
    if (!r || !r->active || ids.size() > size_t(rules::kMaxList)) return false;
    rules::setList(*r->rule, ids.data(), uint32_t(ids.size()));
    return true;
}

void hk::unsubscribe(int sub) {
    std::lock_guard lock(g_mx);
    auto it = g_subs.find(sub);
    if (it == g_subs.end()) return;
    Sub* s = it->second.get();
    s->active = false;
    if (auto t = g_targets.find(s->fn); t != g_targets.end()) relive(*t->second);
    std::lock_guard q(s->mx);
    s->discarded += s->events.size();
    s->events.clear();
    s->payload.clear();
    s->generation++;
}
void hk::unsubscribeAll() {
    std::vector<int> ids;
    {
        std::lock_guard lock(g_mx);
        for (auto& [id, s] : g_subs) ids.push_back(id);
        for (Target* t : g_target_list) {
            for (int i = 0; i < t->ruleCount; ++i) {
                t->rslots[size_t(i)].active = false;
                t->rslots[size_t(i)].rule->active = false;
            }
            publish_order(*t);
        }
    }
    for (int id : ids) unsubscribe(id);
}
void hk::discardPending() {
    std::lock_guard lock(g_mx);
    for (auto& [id, s] : g_subs) {
        std::lock_guard q(s->mx);
        s->discarded += s->events.size();
        s->events.clear();
        s->payload.clear();
        s->generation++;
    }
}
bool hk::drain(int sub, Batch& out) {
    std::lock_guard lock(g_mx);
    auto it = g_subs.find(sub);
    if (it == g_subs.end() || !it->second->active) return false;
    Sub* s = it->second.get();
    std::lock_guard q(s->mx);
    if (s->events.empty()) return false;
    out.generation = s->generation;
    out.events.swap(s->events);
    out.payload.swap(s->payload);
    s->events.clear();
    s->payload.clear();
    out.events.front().dropped = std::exchange(s->droppedSinceDrain, 0);
    s->delivered += out.events.size();
    return true;
}
bool hk::delivered(int sub, uint64_t generation) {
    std::lock_guard lock(g_mx);
    auto it = g_subs.find(sub);
    return it != g_subs.end() && it->second->active && it->second->generation == generation;
}
std::vector<hk::TargetInfo> hk::info() {
    std::lock_guard lock(g_mx);
    std::vector<TargetInfo> out;
    for (auto& [fn, t] : g_targets) {
        TargetInfo ti{fn, t->faulted.load(), t->timing, t->calls.load(), t->captureTicks.load(), t->timedCalls.load(), 0, 0, {}};
        for (Sub* s : t->subs)
            ti.subs.push_back({s->key, s->active, s->delivered, s->dropped, s->discarded, s->lockWaits, s->highWater, s->capacity});
        ti.ruleCalls = t->ruleCalls.load();
        for (int j = 0; j < t->ruleCount; ++j) {
            const auto& r = t->rslots[size_t(j)];
            ti.rules.push_back({r.key, r.active ? conflict_with(*t, j) : "", r.active, r.faulted, r.rule->hits.load(),
                                r.rule->faults.load(), r.rule->refused.load()});
        }
        out.push_back(std::move(ti));
    }
    return out;
}
void hk::setTiming(uintptr_t fn, bool on) {
    std::lock_guard lock(g_mx);
    if (auto it = g_targets.find(fn); it != g_targets.end()) it->second->timing = on;
}
