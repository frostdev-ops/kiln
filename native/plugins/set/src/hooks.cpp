// SPDX-License-Identifier: MIT
// kiln-set: SET's native entry observation (hooks.h) on kiln.hooks. Kiln's own implementation of SET's documented
// behaviour (Shrouded Engine Tweaks api-reference "Native hooks", MIT): one kiln.hooks mid hook per target at its entry
// reads the Win64 argument positions (rcx/rdx/r8/r9 or xmm0-3, then the stack), copies the declared values into each
// active subscriber's bounded queue, and the original always runs. Lua gets the copies on the next tick (drain).
// A capture that faults disables its target (kiln.hooks' guard), the game runs on.
#include "hooks.h"

#include <windows.h>

#include <atomic>
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
    std::mutex mx;  // capture threads vs. the Lua thread
    std::vector<hk::Event> events;
    std::vector<uint8_t> payload;
    uint64_t generation = 1, delivered = 0, dropped = 0, droppedSinceDrain = 0, discarded = 0, lockWaits = 0;
    uint32_t highWater = 0;
};

struct Target {
    uintptr_t fn = 0;
    KilnHandle hook = 0;
    std::atomic<bool> faulted{false};
    bool timing = false;
    std::atomic<uint64_t> calls{0}, captureTicks{0}, timedCalls{0};
    std::vector<Sub*> subs;  // guarded by g_mx for changes; read by captures under g_mx (shared, short)
};

std::mutex g_mx;
std::map<uintptr_t, std::unique_ptr<Target>> g_targets;
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

uint64_t arg_bits(const KilnRegisters* r, int i, bool fp) {
    if (i < 4) {
        if (fp) {
            const KilnXmm* x[] = {&r->xmm0, &r->xmm1, &r->xmm2, &r->xmm3};
            uint64_t v;
            std::memcpy(&v, x[i]->bytes, 8);
            return v;
        }
        const uintptr_t g[] = {r->rcx, r->rdx, r->r8, r->r9};
        return g[i];
    }
    // At the entry [rsp] is the return address, the home space follows, then argument 5 at rsp + 0x28.
    return *reinterpret_cast<const uint64_t*>(r->rsp + 0x28 + 8 * uint64_t(i - 4));
}

void capture(Target* t, KilnRegisters* r) {
    LARGE_INTEGER a;
    QueryPerformanceCounter(&a);
    t->calls.fetch_add(1, std::memory_order_relaxed);
    std::lock_guard lock(g_mx);
    for (Sub* s : t->subs) {
        if (!s->active) continue;
        hk::Event e{};
        e.seq = g_seq.fetch_add(1) + 1;
        e.qpc = a.QuadPart;
        uint8_t local[hk::kMaxSnapshot + hk::kMaxStr];
        size_t used = 0;
        for (size_t i = 0; i < s->args.size(); ++i) {
            const hk::Arg& g = s->args[i];
            const bool fp = g.kind == hk::F32 || g.kind == hk::F64;
            hk::Value& v = e.v[i];
            if (g.kind == hk::Skip) continue;
            const uint64_t bits = arg_bits(r, int(i), fp);
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
                if (bits) { v.bits = *reinterpret_cast<const uint32_t*>(bits); v.len = 4; }
                break;
            case hk::CStr:
                if (bits) {
                    const char* p = reinterpret_cast<const char*>(bits);
                    uint32_t n = 0;
                    while (n < hk::kMaxStr && p[n]) ++n;
                    v.bits = 1;
                    v.off = uint32_t(used);
                    v.len = n;
                    std::memcpy(local + used, p, n);
                    used += n;
                }
                break;
            case hk::Snapshot:
                if (bits) {
                    const uint32_t n = std::min(g.type.size(), hk::kMaxSnapshot);
                    v.bits = 1;
                    v.off = uint32_t(used);
                    v.len = n;
                    std::memcpy(local + used, reinterpret_cast<const void*>(bits), n);
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
            s->payload.insert(s->payload.end(), local, local + used);
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

void KILN_CALL on_mid(KilnRegisters* r, void* user) {
    auto* t = static_cast<Target*>(user);
    if (t->faulted.load(std::memory_order_relaxed)) return;
    capture(t, r);
}

int hexDigit(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}
}  // namespace

void kset::bindHooks(const KilnApi* api, KilnOwner owner) {
    g_api = api;
    g_owner = owner;
    H = static_cast<const KilnHooksApi*>(api->service(KilnStr{KILN_HOOKS_SERVICE, sizeof(KILN_HOOKS_SERVICE) - 1}, 1));
    LARGE_INTEGER f, n;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&n);
    g_qpf = f.QuadPart ? f.QuadPart : 1;
    g_qpc0 = n.QuadPart;
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
uintptr_t hk::findPattern(uintptr_t, std::string_view text, std::string& err) {
    std::string norm;
    size_t bytes = 0;
    bool exact = false;
    for (size_t i = 0; i < text.size();) {
        if (isspace(static_cast<unsigned char>(text[i]))) { ++i; continue; }
        if (i + 2 > text.size() || (i + 2 < text.size() && !isspace(static_cast<unsigned char>(text[i + 2])))) {
            err = "malformed pattern: use hex byte pairs and ?? separated by spaces";
            return 0;
        }
        const char a = text[i], b = text[i + 1];
        i += 2;
        if (a == '?' && b == '?') norm += "?? ";
        else if (hexDigit(a) < 0 || hexDigit(b) < 0) {
            err = "malformed pattern: use hex byte pairs and ?? separated by spaces";
            return 0;
        } else {
            norm += a, norm += b, norm += ' ';
            exact = true;
        }
        if (++bytes > 256) {
            err = "pattern is longer than 256 bytes";
            return 0;
        }
    }
    if (!exact) {
        err = "pattern needs at least one exact byte";
        return 0;
    }
    if (!norm.empty()) norm.pop_back();
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
    std::lock_guard lock(g_mx);
    g_patterns[norm] = hits[0];
    return hits[0];
}

int hk::subscribe(uintptr_t fn, const std::string& key, const std::vector<Arg>& args, uint32_t capacity, std::string& err) {
    std::lock_guard lock(g_mx);
    if (auto it = g_reserved.find(fn); it != g_reserved.end()) {
        err = "target is reserved by Kiln (" + it->second + ")";
        note(key + ": " + err);
        return -1;
    }
    Target* t = nullptr;
    if (auto it = g_targets.find(fn); it != g_targets.end()) {
        t = it->second.get();
        if (t->faulted) {
            err = "target faulted earlier; restart the game";
            note(key + ": " + err);
            return -1;
        }
        if (t->subs.size() >= kMaxSubs) {
            err = "too many registrations on this target";
            note(key + ": " + err);
            return -1;
        }
        for (Sub* o : t->subs) {  // active declarations must agree at every non-skipped position
            if (!o->active) continue;
            for (size_t i = 0; i < std::min(o->args.size(), args.size()); ++i)
                if (o->args[i].kind != Skip && args[i].kind != Skip && o->args[i].kind != args[i].kind) {
                    err = "argument " + std::to_string(i + 1) + " disagrees with " + o->key;
                    note(key + ": " + err);
                    return -1;
                }
        }
    } else {
        if (g_targets.size() >= size_t(kMaxTargets) || !H) {
            err = H ? "too many hook targets" : "native hooks are unavailable: kiln.hooks is not running";
            note(key + ": " + err);
            return -1;
        }
        auto nt = std::make_unique<Target>();
        nt->fn = fn;
        t = nt.get();
        if (H->mid(g_owner, fn, 0, &on_mid, t, &t->hook) != KILN_OK) {
            err = "kiln.hooks could not hook the target";
            note(key + ": " + err);
            return -1;
        }
        g_targets.emplace(fn, std::move(nt));
    }
    const size_t cost = size_t(capacity) * (sizeof(Event) + 64);
    if (g_bytes + cost > kMemoryBudget) {
        err = "hook memory budget exhausted; restart the game";
        note(key + ": " + err);
        return -1;
    }
    g_bytes += cost;
    auto s = std::make_unique<Sub>();
    s->id = ++g_next_sub;
    s->key = key;
    s->args = args;
    s->capacity = capacity;
    s->fn = fn;
    s->events.reserve(std::min<uint32_t>(capacity, 64));
    t->subs.push_back(s.get());
    const int id = s->id;
    g_subs.emplace(id, std::move(s));
    return id;
}

void hk::unsubscribe(int sub) {
    std::lock_guard lock(g_mx);
    auto it = g_subs.find(sub);
    if (it == g_subs.end()) return;
    Sub* s = it->second.get();
    s->active = false;
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
        out.push_back(std::move(ti));
    }
    return out;
}
void hk::setTiming(uintptr_t fn, bool on) {
    std::lock_guard lock(g_mx);
    if (auto it = g_targets.find(fn); it != g_targets.end()) it->second->timing = on;
}
