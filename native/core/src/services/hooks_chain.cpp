// SPDX-License-Identifier: MIT
// kiln.hooks v2 registry engine (design: hooks_chain.h). One safetyhook inline detour per target address; every Kiln
// handler on that address is a link in a chain that is only ever walked through 8-byte cells.
//
// Generated code (all of it preserves every register and the flags except where noted, and never calls out except
// through kiln_hk_slow, which has unwind info):
//   entry stub  (per target)  push rcx; rcx = this thread's caller table (TEB TlsSlots, no call); record [rsp];
//                             pop; jmp [head cell]. First call of a thread: call kiln_hk_slow once (flags saved).
//   adapter     (per structured link)  r11 = Link; jmp kiln_hk_structured
//   mid stub    (per mid link)         push Link; push [next cell]; jmp kiln_hk_mid
//   continuation(per raw/structured link)  jmp [next cell]
//
// Locking: g_mutex serialises every mutation (install, revoke, rebuild, map). Dispatch never takes it: it reads cells,
// Link fields that never change after publication, and the atomic `live` flag.
#include "hooks_chain.h"

#include <windows.h>

#include <intrin.h>

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "../log.h"
#include "nlohmann/json.hpp"
#include "safetyhook.hpp"

namespace kiln::hooks {
namespace seh {
bool call_detour(KilnDetourFn fn, KilnHookCall* call, void* user, uintptr_t* out, unsigned long* code,
                 volatile const bool* in_original);
bool call_detour2(KilnDetourFn2 fn, KilnHookCall2* call, void* user, uintptr_t* out, unsigned long* code,
                  volatile const bool* in_original);
bool call_mid(KilnMidFn fn, KilnRegisters* regs, void* user, unsigned long* code);
bool copy(void* dst, const void* src, size_t n);
}  // namespace seh
}  // namespace kiln::hooks

namespace kiln::hooks::chain {
namespace {

using json = nlohmann::ordered_json;

constexpr uint32_t kFrameMagic = 0x4B484B31;  // "KHK1"
constexpr size_t kCodeCap = 4u << 20, kDataCap = 2u << 20, kCommitStep = 64u << 10;
constexpr size_t kFootprint = 16;  // bytes of the target compared/recorded for foreign detection

// A Link is created once and never freed: its stubs, cells and this record outlive the handler's owner, so a stale
// caller (a revoked link's continuation, a dispatch racing a revoke) always lands on valid memory.
struct Target;
struct Link {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    std::string owner_name, owner_key;       // owner_key = normalised for before/after matching
    std::string label, before, after;
    int32_t priority = 0;
    uint64_t seq = 0;
    Kind kind = Kind::Raw;
    std::atomic<bool> live{true};
    Target* target = nullptr;
    uint32_t slot = 0;
    void* fn = nullptr;
    void* user = nullptr;
    uint32_t arg_count = 0;
    uint8_t* entry = nullptr;                // what the previous link / head cell jumps to
    uint8_t* cont = nullptr;                 // continuation stub (raw, structured, v1); null for mid
    std::atomic<uintptr_t>* next = nullptr;  // cell: entry of the following live link, or the trampoline
    std::vector<std::string> warned;         // dropped constraints already reported
};

struct Target {
    uintptr_t addr = 0;
    uint32_t slot = 0;
    safetyhook::InlineHook hook;             // process lifetime: never reset
    uint8_t* entry_stub = nullptr;
    std::atomic<uintptr_t>* head = nullptr;
    uintptr_t trampoline = 0;
    std::vector<Link*> order;                // every link ever added, live or dead, in chain order
    std::string label_cache;
    uint8_t own[kFootprint] = {};
    size_t own_len = 5;
    bool before_present = false;
    Dest before;
    bool over_present = false;
    Dest over;
    std::atomic<bool> dirty{false};          // a link faulted and was disabled: take it out of the chain at the next sweep()
};

// Per-thread record of the game's return address for each target slot.
// caller[] comes first: the entry stub stores at [table + 8 * slot].
struct Block {
    uintptr_t caller[kMaxTargets] = {};
    DWORD tid = 0;
};

struct Arena {
    uint8_t* base = nullptr;
    uint8_t* code = nullptr;
    uint8_t* data = nullptr;
    size_t code_used = 0, code_committed = 0, data_used = 0, data_committed = 0;
};

struct State {
    Env env;
    bool ready = false;
    DWORD tls = TLS_OUT_OF_INDEXES;
    Arena arena;
    std::atomic<uintptr_t>* cell_slow = nullptr;
    std::atomic<uintptr_t>* cell_structured = nullptr;
    std::atomic<uintptr_t>* cell_mid = nullptr;

    std::mutex mutex;
    std::vector<Target*> targets;
    std::map<uintptr_t, Target*> by_addr;
    uint64_t next_seq = 1;
    uint32_t next_link = 0;  // index into links

    std::atomic<Link*> links[kMaxLinks];
    std::vector<MapExtension> extensions;

    std::atomic<uint64_t> detour_calls{0}, mid_calls{0};
    std::atomic<bool> world_checked{false};

    // thread tables: plain Win32 so the slow path can never throw
    SRWLOCK block_lock = SRWLOCK_INIT;
    Block* blocks[4096] = {};
    size_t block_count = 0;
    Block overflow;
};

State& S() {
    static State& s = *new State;  // process lifetime
    return s;
}

// ---------------------------------------------------------------------------------------------------------------------
// Arena: one reservation, code at the bottom, data (cells) above it, both RIP-relative reachable.

bool arena_init() {
    Arena& a = S().arena;
    if (a.base) return true;
    a.base = static_cast<uint8_t*>(VirtualAlloc(nullptr, kCodeCap + kDataCap, MEM_RESERVE, PAGE_NOACCESS));
    if (!a.base) return false;
    a.code = a.base;
    a.data = a.base + kCodeCap;
    return true;
}

uint8_t* alloc_code(size_t n) {
    Arena& a = S().arena;
    n = (n + 15) & ~size_t{15};
    if (a.code_used + n > kCodeCap) return nullptr;
    while (a.code_used + n > a.code_committed) {
        if (!VirtualAlloc(a.code + a.code_committed, kCommitStep, MEM_COMMIT, PAGE_EXECUTE_READWRITE)) return nullptr;
        a.code_committed += kCommitStep;
    }
    uint8_t* p = a.code + a.code_used;
    a.code_used += n;
    return p;
}

std::atomic<uintptr_t>* alloc_cells(size_t count, uintptr_t init = 0) {
    Arena& a = S().arena;
    const size_t n = count * 8;
    if (a.data_used + n > kDataCap) return nullptr;
    while (a.data_used + n > a.data_committed) {
        if (!VirtualAlloc(a.data + a.data_committed, kCommitStep, MEM_COMMIT, PAGE_READWRITE)) return nullptr;
        a.data_committed += kCommitStep;
    }
    auto* p = reinterpret_cast<std::atomic<uintptr_t>*>(a.data + a.data_used);
    a.data_used += n;
    for (size_t i = 0; i < count; ++i) new (&p[i]) std::atomic<uintptr_t>(init);
    return p;
}

// ---- a tiny emitter: only the instruction forms the stubs need ----
struct Em {
    uint8_t* p;
    uint8_t* start;
    explicit Em(uint8_t* at) : p(at), start(at) {}
    void b(uint8_t x) { *p++ = x; }
    void d32(int32_t v) { std::memcpy(p, &v, 4); p += 4; }
    void q(uint64_t v) { std::memcpy(p, &v, 8); p += 8; }
    // opcode bytes already written; the disp32 that follows is the last field of the instruction.
    void rip(const void* cell) {
        const intptr_t d = reinterpret_cast<const uint8_t*>(cell) - (p + 4);
        d32(static_cast<int32_t>(d));
    }
    void jmp_cell(const void* cell) { b(0xFF), b(0x25), rip(cell); }    // jmp  [rip+cell]
    void call_cell(const void* cell) { b(0xFF), b(0x15), rip(cell); }   // call [rip+cell]
    void push_cell(const void* cell) { b(0xFF), b(0x35), rip(cell); }   // push [rip+cell]
    size_t size() const { return static_cast<size_t>(p - start); }
};

void flush(const void* p, size_t n) { FlushInstructionCache(GetCurrentProcess(), p, n); }

// ---- thread tables ----
Block* current_block() {
    State& s = S();
    if (s.tls < 64) return reinterpret_cast<Block*>(__readgsqword(0x1480 + 8 * s.tls));
    const auto* ex = reinterpret_cast<const uintptr_t*>(__readgsqword(0x1780));
    return ex ? reinterpret_cast<Block*>(ex[s.tls - 64]) : nullptr;
}

bool thread_dead(DWORD tid) {
    HANDLE h = OpenThread(SYNCHRONIZE, FALSE, tid);
    if (!h) return GetLastError() == ERROR_INVALID_PARAMETER;
    const bool dead = WaitForSingleObject(h, 0) == WAIT_OBJECT_0;
    CloseHandle(h);
    return dead;
}

}  // namespace
}  // namespace kiln::hooks::chain

// The first call of a thread through any entry stub (kiln_hk_slow). Never throws, never fails: the shared overflow
// block takes over when the table is full.
extern "C" void* kiln_hk_alloc_block(void) {
    using namespace kiln::hooks::chain;
    State& s = S();
    const DWORD saved_error = GetLastError();  // runs inside a game call: OpenThread/HeapAlloc must not leak into it
    const DWORD me = GetCurrentThreadId();
    AcquireSRWLockExclusive(&s.block_lock);
    Block* b = nullptr;
    for (size_t i = 0; i < s.block_count && !b; ++i)
        if (s.blocks[i]->tid == me) b = s.blocks[i];
    for (size_t i = 0; i < s.block_count && !b; ++i)
        if (s.blocks[i]->tid && thread_dead(s.blocks[i]->tid)) b = s.blocks[i];  // recycle a finished thread's table
    if (!b && s.block_count < 4096) {
        b = static_cast<Block*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Block)));
        if (b) s.blocks[s.block_count++] = b;
    }
    if (b) {
        std::memset(b->caller, 0, sizeof b->caller);
        b->tid = me;
    } else {
        b = &s.overflow;
    }
    ReleaseSRWLockExclusive(&s.block_lock);
    TlsSetValue(s.tls, b);
    SetLastError(saved_error);
    return b;
}

namespace kiln::hooks::chain {
namespace {

// ---------------------------------------------------------------------------------------------------------------------
// Owners and constraint matching

std::string norm_owner(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    // "service:kiln-chat.dll" -> "kiln.chat"; "emf:coolmod" -> "coolmod"
    const size_t colon = s.find(':');
    if (colon != std::string::npos && colon < 12) s.erase(0, colon + 1);
    if (s.size() > 4 && s.compare(s.size() - 4, 4, ".dll") == 0) s.erase(s.size() - 4);
    for (char& c : s)
        if (c == '-' || c == '_') c = '.';
    return s;
}

std::vector<std::string> split_owners(const std::string& list) {
    std::vector<std::string> out;
    size_t i = 0;
    while (i <= list.size()) {
        size_t j = list.find(',', i);
        if (j == std::string::npos) j = list.size();
        std::string t = list.substr(i, j - i);
        while (!t.empty() && std::isspace(static_cast<unsigned char>(t.front()))) t.erase(t.begin());
        while (!t.empty() && std::isspace(static_cast<unsigned char>(t.back()))) t.pop_back();
        if (!t.empty()) out.push_back(norm_owner(t));
        i = j + 1;
    }
    return out;
}

std::string hex(uintptr_t v) {
    char b[32];
    std::snprintf(b, sizeof b, "0x%llx", static_cast<unsigned long long>(v));
    return b;
}

uintptr_t rva_of(uintptr_t a) { return a - S().env.image_lo; }

std::string target_label(Target& t);

// ---------------------------------------------------------------------------------------------------------------------
// Ordering: constraints (a stable topological sort, earlier-registered constraints win a cycle), then priority
// (higher first), then registration order.

std::vector<Link*> order_links(std::vector<Link*> live, Target& t) {
    std::sort(live.begin(), live.end(), [](const Link* a, const Link* b) { return a->seq < b->seq; });
    const size_t n = live.size();
    std::vector<std::vector<size_t>> out(n);
    std::vector<size_t> indeg(n, 0);
    auto reaches = [&](size_t from, size_t to) {
        std::vector<size_t> stack{from};
        std::vector<char> seen(n, 0);
        while (!stack.empty()) {
            const size_t x = stack.back();
            stack.pop_back();
            if (x == to) return true;
            if (seen[x]) continue;
            seen[x] = 1;
            for (size_t y : out[x]) stack.push_back(y);
        }
        return false;
    };
    auto add_edge = [&](size_t a, size_t b, Link* who, const char* what, const std::string& tok) {
        if (a == b || live[a]->owner_key == live[b]->owner_key) return;
        if (std::find(out[a].begin(), out[a].end(), b) != out[a].end()) return;
        if (reaches(b, a)) {  // would close a cycle: the later constraint loses
            const std::string key = std::string(what) + ":" + tok;
            if (std::find(who->warned.begin(), who->warned.end(), key) == who->warned.end()) {
                who->warned.push_back(key);
                KLOG_W("hooks: ordering on %s: link \"%s\" of %s asked to run %s %s, which contradicts an earlier "
                       "constraint; dropped",
                       target_label(t).c_str(), who->label.c_str(), who->owner_name.c_str(), what, tok.c_str());
            }
            return;
        }
        out[a].push_back(b);
        ++indeg[b];
    };
    for (size_t i = 0; i < n; ++i) {
        for (const std::string& tok : split_owners(live[i]->before))
            for (size_t j = 0; j < n; ++j)
                if (live[j]->owner_key == tok) add_edge(i, j, live[i], "before", tok);
        for (const std::string& tok : split_owners(live[i]->after))
            for (size_t j = 0; j < n; ++j)
                if (live[j]->owner_key == tok) add_edge(j, i, live[i], "after", tok);
    }
    std::vector<Link*> sorted;
    std::vector<char> done(n, 0);
    for (size_t step = 0; step < n; ++step) {
        size_t best = n;
        for (size_t i = 0; i < n; ++i) {
            if (done[i] || indeg[i]) continue;
            if (best == n || live[i]->priority > live[best]->priority) best = i;  // seq order breaks ties (ascending i)
        }
        if (best == n) break;  // cannot happen: edges that close a cycle were dropped
        done[best] = 1;
        sorted.push_back(live[best]);
        for (size_t y : out[best]) --indeg[y];
    }
    return sorted;
}

// g_mutex held. Recomputes a target's chain and retargets the cells. Safe against concurrent callers: every cell
// always names a valid entry (or the trampoline); a new link is wired before it becomes reachable.
void rebuild(Target& t) {
    std::vector<Link*> live;
    for (Link* l : t.order)
        if (l->live.load(std::memory_order_acquire)) live.push_back(l);
    std::vector<Link*> sorted = order_links(live, t);

    // Dead links keep their place (after the live link that preceded them) so their continuations forward sensibly.
    std::map<Link*, std::vector<Link*>> hanging;
    std::vector<Link*> lead;
    Link* last_live = nullptr;
    for (Link* l : t.order) {
        if (l->live.load(std::memory_order_acquire)) {
            last_live = l;
        } else if (last_live) {
            hanging[last_live].push_back(l);
        } else {
            lead.push_back(l);
        }
    }
    std::vector<Link*> merged = lead;
    for (Link* l : sorted) {
        merged.push_back(l);
        auto it = hanging.find(l);
        if (it != hanging.end()) merged.insert(merged.end(), it->second.begin(), it->second.end());
    }

    // The entry each position leads to: the next live link at or after it, else the real function.
    auto entry_of = [](Link* l) { return reinterpret_cast<uintptr_t>(l->entry); };
    std::vector<uintptr_t> next(merged.size());
    uintptr_t following = t.trampoline;
    for (size_t i = merged.size(); i-- > 0;) {
        next[i] = following;
        if (merged[i]->live.load(std::memory_order_acquire)) following = entry_of(merged[i]);
    }
    // New links first (unreachable until a cell names them), then existing cells tail to head, head last. A new link's
    // cell already holds the trampoline (install()); the tail-to-head pass sets it before any earlier cell names it.
    for (size_t i = 0; i < merged.size(); ++i)
        if (merged[i]->next->load(std::memory_order_relaxed) == 0) merged[i]->next->store(next[i], std::memory_order_release);
    for (size_t i = merged.size(); i-- > 0;)
        if (merged[i]->next->load(std::memory_order_relaxed) != next[i])
            merged[i]->next->store(next[i], std::memory_order_release);
    if (t.head->load(std::memory_order_relaxed) != following) t.head->store(following, std::memory_order_release);
    t.order = std::move(merged);
}

void notify_changed() {
    if (S().env.changed) S().env.changed();
}

// ---------------------------------------------------------------------------------------------------------------------
// Foreign hooks

bool read_guarded(uintptr_t a, void* out, size_t n) { return seh::copy(out, reinterpret_cast<const void*>(a), n); }

// The destination of the jump instruction in `b` (found at `at`), or 0.
uintptr_t first_hop(uintptr_t at, const uint8_t* b) {
    int32_t d = 0;
    if (b[0] == 0xE9 || b[0] == 0xE8) return std::memcpy(&d, b + 1, 4), at + 5 + d;
    if (b[0] == 0xEB) return at + 2 + static_cast<int8_t>(b[1]);
    if (b[0] == 0xFF && (b[1] == 0x25 || b[1] == 0x15)) {
        std::memcpy(&d, b + 2, 4);
        uint64_t t = 0;
        if (!read_guarded(at + 6 + d, &t, 8)) return 0;
        return static_cast<uintptr_t>(t);
    }
    if (b[0] == 0x48 && b[1] == 0xB8 && b[10] == 0xFF && b[11] == 0xE0) {  // mov rax, imm64; jmp rax
        uint64_t t = 0;
        std::memcpy(&t, b + 2, 8);
        return static_cast<uintptr_t>(t);
    }
    if (b[0] == 0x49 && b[1] == 0xBB && b[10] == 0x41 && b[11] == 0xFF && b[12] == 0xE3) {  // mov r11, imm64; jmp r11
        uint64_t t = 0;
        std::memcpy(&t, b + 2, 8);
        return static_cast<uintptr_t>(t);
    }
    return 0;
}

bool module_of(uintptr_t a, Dest& d) {
    HMODULE mod = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(a), &mod) ||
        !mod)
        return false;
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(mod, path, MAX_PATH);
    if (!n) return false;
    std::wstring w(path, n);
    const size_t slash = w.find_last_of(L"\\/");
    if (slash != std::wstring::npos) w.erase(0, slash + 1);
    d.module = to_utf8(w);
    d.offset = a - reinterpret_cast<uintptr_t>(mod);
    std::string low = d.module;
    for (char& c : low) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    // Kiln's modules are kiln-core.dll and kiln-<service|plugin>.dll; a third-party "KilnTweaks.dll" is not one of them.
    d.own = low.size() > 9 && low.rfind("kiln-", 0) == 0 && low.compare(low.size() - 4, 4, ".dll") == 0;
    return true;
}

bool outside_exe(uintptr_t a) { return a < S().env.image_lo || a >= S().env.image_hi; }

}  // namespace

Dest analyze(uintptr_t at, const uint8_t* bytes) {
    Dest d;
    uintptr_t t = first_hop(at, bytes);
    if (!t) {
        d.in_place = true;
        return d;
    }
    d.present = true;
    for (int hop = 0; hop < 5; ++hop) {
        if (outside_exe(t) && module_of(t, d)) {
            d.address = t;
            return d;
        }
        uint8_t nb[16];
        if (!read_guarded(t, nb, sizeof nb)) break;
        const uintptr_t next = first_hop(t, nb);
        if (!next) break;
        t = next;
    }
    // A relay or trampoline outside any module (saves registers, then calls the hook). The module it belongs to is the
    // first one named by an absolute `mov r64, imm64` in its first 64 bytes, or by an indirect `call/jmp [rip+disp32]`
    // in its first 512 (safetyhook's mid-hook trampoline calls its handler that way).
    uint8_t stub[512];
    size_t got = 0;
    if (outside_exe(t))
        for (size_t n : {sizeof stub, size_t{128}, size_t{64}})
            if (read_guarded(t, stub, n)) {
                got = n;
                break;
            }
    for (size_t i = 0; i + 10 <= got && i + 10 <= 64; ++i) {
        if ((stub[i] & 0xF0) != 0x40 || stub[i + 1] < 0xB8 || stub[i + 1] > 0xBF) continue;
        uint64_t imm = 0;
        std::memcpy(&imm, stub + i + 2, 8);
        Dest m;
        if (module_of(static_cast<uintptr_t>(imm), m)) {
            m.present = true;
            m.address = static_cast<uintptr_t>(imm);
            return m;
        }
    }
    for (size_t i = 0; i + 6 <= got; ++i) {
        if (stub[i] != 0xFF || (stub[i + 1] != 0x15 && stub[i + 1] != 0x25)) continue;
        int32_t disp = 0;
        std::memcpy(&disp, stub + i + 2, 4);
        uint64_t slot = 0;
        if (!read_guarded(t + i + 6 + disp, &slot, 8) || !outside_exe(static_cast<uintptr_t>(slot))) continue;
        Dest m;
        if (module_of(static_cast<uintptr_t>(slot), m)) {
            m.present = true;
            m.address = static_cast<uintptr_t>(slot);
            return m;
        }
    }
    d.address = t;
    return d;
}

namespace {

std::string dest_text(const Dest& d) {
    if (!d.module.empty()) return d.module + "+" + hex(d.offset);
    if (d.address) return "an unnamed jump target " + hex(d.address);
    return "patched bytes";
}

json dest_json(const Dest& d) {
    json j;
    j["module"] = d.module;
    j["offset"] = hex(d.offset);
    if (d.address) j["address"] = hex(d.address);
    if (d.in_place) j["inPlace"] = true;
    j["own"] = d.own;
    return j;
}

std::string target_label(Target& t) {
    if (!t.label_cache.empty()) return t.label_cache;
    std::string name;
    if (S().env.symbol_at) name = S().env.symbol_at(t.addr);
    if (name.empty())
        for (Link* l : t.order)
            if (!l->label.empty()) {
                name = l->label;
                break;
            }
    if (name.empty()) name = "rva " + hex(rva_of(t.addr));
    t.label_cache = name;
    return name;
}

// ---------------------------------------------------------------------------------------------------------------------
// Targets

// g_mutex held. Finds or creates the target: entry stub, head cell, the one safetyhook inline detour.
Target* target_for(uintptr_t addr) {
    State& s = S();
    if (auto it = s.by_addr.find(addr); it != s.by_addr.end()) return it->second;
    if (s.targets.size() >= kMaxTargets) {
        KLOG_W("hooks: no free target slot (%zu in use)", kMaxTargets);
        return nullptr;
    }
    auto t = std::make_unique<Target>();
    t->addr = addr;
    t->slot = static_cast<uint32_t>(s.targets.size());
    t->head = alloc_cells(1);
    t->entry_stub = alloc_code(96);
    if (!t->head || !t->entry_stub) {
        KLOG_E("hooks: out of stub memory");
        return nullptr;
    }
    // Foreign hook already there? Compare the live bytes with the exe file's (the file is never patched).
    uint8_t live[kFootprint] = {}, file[kFootprint] = {};
    const bool have_live = read_guarded(addr, live, sizeof live);
    if (have_live && s.env.file_bytes && s.env.file_bytes(addr, file, sizeof file) &&
        std::memcmp(live, file, sizeof live) != 0) {
        t->before = analyze(addr, live);
        t->before_present = true;
    }

    // Entry stub.
    {
        Em e(t->entry_stub);
        e.b(0x51);  // push rcx
        std::vector<uint8_t*> to_slow;
        auto jrcxz = [&] { e.b(0xE3); to_slow.push_back(e.p); e.b(0); };
        if (s.tls < 64) {
            e.b(0x65), e.b(0x48), e.b(0x8B), e.b(0x0C), e.b(0x25), e.d32(static_cast<int32_t>(0x1480 + 8 * s.tls));
            jrcxz();  // mov rcx, gs:[TlsSlots[idx]]
        } else {
            e.b(0x65), e.b(0x48), e.b(0x8B), e.b(0x0C), e.b(0x25), e.d32(0x1780);
            jrcxz();  // mov rcx, gs:[TlsExpansionSlots]
            e.b(0x48), e.b(0x8B), e.b(0x89), e.d32(static_cast<int32_t>(8 * (s.tls - 64)));
            jrcxz();  // mov rcx, [rcx + 8*(idx-64)]
        }
        uint8_t* fast = e.p;
        e.b(0x41), e.b(0x52);                                              // push r10
        e.b(0x4C), e.b(0x8B), e.b(0x54), e.b(0x24), e.b(0x10);             // mov r10, [rsp+16]  (return address)
        e.b(0x4C), e.b(0x89), e.b(0x91), e.d32(static_cast<int32_t>(8 * t->slot));  // mov [rcx+slot*8], r10
        e.b(0x41), e.b(0x5A);                                              // pop r10
        e.b(0x59);                                                         // pop rcx
        e.jmp_cell(t->head);
        uint8_t* slow = e.p;
        e.b(0x9C);                                                         // pushfq (the C++ side clobbers flags)
        e.call_cell(s.cell_slow);                                          // rcx = this thread's table
        e.b(0x9D);                                                         // popfq
        e.b(0xEB), e.b(static_cast<uint8_t>(fast - (e.p + 1)));            // jmp fast
        for (uint8_t* j : to_slow) *j = static_cast<uint8_t>(slow - (j + 1));
        flush(t->entry_stub, e.size());
    }

    // The one physical detour. Created disabled so the head cell can name the trampoline before a thread can arrive.
    auto hook = safetyhook::InlineHook::create(reinterpret_cast<void*>(addr), t->entry_stub,
                                               safetyhook::InlineHook::StartDisabled);
    if (!hook) {
        KLOG_E("hooks: safetyhook could not detour rva %s (error %d)", hex(rva_of(addr)).c_str(),
               static_cast<int>(hook.error().type));
        return nullptr;
    }
    t->hook = std::move(*hook);
    t->trampoline = t->hook.target_address() ? t->hook.trampoline().address() : 0;
    t->head->store(t->trampoline, std::memory_order_release);
    if (!t->hook.enable()) {
        KLOG_E("hooks: safetyhook could not enable the detour on rva %s", hex(rva_of(addr)).c_str());
        return nullptr;
    }
    read_guarded(addr, t->own, sizeof t->own);
    t->own_len = t->own[0] == 0xE9 ? 5 : 14;

    Target* raw = t.release();
    s.targets.push_back(raw);
    s.by_addr[addr] = raw;
    return raw;
}

Link* lookup(KilnHandle h) {
    const uint64_t hi = h >> 32;
    if (hi == 0 || hi > kMaxLinks) return nullptr;
    Link* l = S().links[hi - 1].load(std::memory_order_acquire);
    return l && l->handle == h ? l : nullptr;
}

KilnResult install(Kind kind, KilnOwner owner, uintptr_t addr, const Order& order, void* fn, void* user,
                   uint32_t arg_count, void** original, KilnHandle* out) {
    State& s = S();
    if (!fn || !addr) return KILN_ERR_INVALID_ARGUMENT;
    if (!s.ready) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(s.mutex);
    if (s.next_link >= kMaxLinks) {
        KLOG_W("hooks: link table full (%zu)", kMaxLinks);
        return KILN_ERR_UNAVAILABLE;
    }
    const bool had_target = s.by_addr.count(addr) != 0;
    Target* t = target_for(addr);
    if (!t) return KILN_ERR_UNAVAILABLE;
    auto l = std::make_unique<Link>();
    l->owner = owner;
    l->owner_name = s.env.owner_name ? s.env.owner_name(owner) : std::string("owner ") + std::to_string(owner);
    l->owner_key = norm_owner(l->owner_name);
    if (!order.label.empty()) {
        l->label = order.label;
    } else {
        const std::string sym = s.env.symbol_at ? s.env.symbol_at(addr) : std::string();
        l->label = sym.empty() ? l->owner_name + "@" + hex(rva_of(addr)) : sym;
    }
    l->before = order.before;
    l->after = order.after;
    l->priority = order.priority;
    l->seq = s.next_seq++;
    l->kind = kind;
    l->target = t;
    l->slot = t->slot;
    l->fn = fn;
    l->user = user;
    l->arg_count = arg_count;
    const uint32_t index = s.next_link;
    l->handle = (static_cast<uint64_t>(index + 1) << 32) | static_cast<uint32_t>(l->seq);
    auto* cells = alloc_cells(2);  // [0] = next, [1] = self (the mid stub pushes it)
    uint8_t* stub = alloc_code(48);
    if (!cells || !stub) {
        KLOG_E("hooks: out of stub memory");
        return KILN_ERR_UNAVAILABLE;
    }
    l->next = &cells[0];
    // dev.21 integration fix: never 0, even before rebuild() places the link. `*original` is written below before the
    // link is reachable, and a thread still inside a revoked link that shared that variable (a RawHook re-installed in
    // place) jumps through this cell at once. The real function is a safe target until rebuild() names the next link.
    cells[0].store(t->trampoline, std::memory_order_relaxed);
    cells[1].store(reinterpret_cast<uintptr_t>(l.get()), std::memory_order_relaxed);
    Em e(stub);
    if (kind == Kind::Raw) {
        l->entry = static_cast<uint8_t*>(fn);
        e.jmp_cell(l->next);  // continuation
        l->cont = stub;
    } else if (kind == Kind::Mid) {
        l->entry = stub;
        e.push_cell(&cells[1]);
        e.push_cell(l->next);
        e.jmp_cell(s.cell_mid);
    } else {  // structured, v1
        l->entry = stub;  // adapter
        e.b(0x49), e.b(0xBB), e.q(reinterpret_cast<uint64_t>(l.get()));  // mov r11, Link
        e.jmp_cell(s.cell_structured);
        uint8_t* cont = stub + ((e.size() + 15) & ~size_t{15});
        Em c(cont);
        c.jmp_cell(l->next);
        l->cont = cont;
    }
    flush(stub, 48);
    if (original && kind == Kind::Raw) *original = l->cont;  // before the link can be reached

    Link* link = l.release();
    t->order.push_back(link);
    t->label_cache.clear();
    s.links[index].store(link, std::memory_order_release);
    ++s.next_link;
    rebuild(*t);
    if (out) *out = link->handle;
    size_t live = 0;
    for (Link* x : t->order) live += x->live.load() ? 1 : 0;
    KLOG_I("hooks: %s link \"%s\" on rva %s prio %d (handle %llu, owner %s, %zu on target%s)", kind_name(kind),
           link->label.c_str(), hex(rva_of(addr)).c_str(), link->priority, static_cast<unsigned long long>(link->handle),
           link->owner_name.c_str(), live, had_target ? "" : ", new detour");
    if (!had_target && t->before_present) {
        if (!t->before.module.empty() || t->before.address)
            KLOG_I("hooks: %s chained over %s", target_label(*t).c_str(), dest_text(t->before).c_str());
        else
            KLOG_I("hooks: %s: the bytes at rva %s differ from the exe file's (patched in place by someone else)",
                   target_label(*t).c_str(), hex(rva_of(addr)).c_str());
    }
    notify_changed();
    return KILN_OK;
}

}  // namespace

const char* kind_name(Kind k) {
    switch (k) {
        case Kind::Raw: return "raw";
        case Kind::Structured: return "structured";
        case Kind::V1: return "v1";
        case Kind::Mid: return "mid";
    }
    return "?";
}

bool init(const Env& env) {
    State& s = S();
    if (s.ready) return true;
    s.env = env;
    s.tls = TlsAlloc();
    if (s.tls == TLS_OUT_OF_INDEXES) {
        KLOG_E("hooks: no thread-local slot for the caller table; the v2 registry is unavailable");
        return false;
    }
    if (!arena_init()) {
        KLOG_E("hooks: cannot reserve stub memory");
        return false;
    }
    s.cell_slow = alloc_cells(1, reinterpret_cast<uintptr_t>(&kiln_hk_slow));
    s.cell_structured = alloc_cells(1, reinterpret_cast<uintptr_t>(&kiln_hk_structured));
    s.cell_mid = alloc_cells(1, reinterpret_cast<uintptr_t>(&kiln_hk_mid));
    if (!s.cell_slow || !s.cell_structured || !s.cell_mid) return false;
    s.ready = true;
    return true;
}

bool ready() { return S().ready; }

KilnResult install_raw(KilnOwner owner, uintptr_t target, const Order& order, void* fn, void** original,
                       KilnHandle* out) {
    return install(Kind::Raw, owner, target, order, fn, nullptr, 0, original, out);
}
KilnResult install_structured(KilnOwner owner, uintptr_t target, const Order& order, uint32_t arg_count,
                              KilnDetourFn2 fn, void* user, KilnHandle* out) {
    if (arg_count > 12) return KILN_ERR_INVALID_ARGUMENT;
    return install(Kind::Structured, owner, target, order, reinterpret_cast<void*>(fn), user, arg_count, nullptr, out);
}
KilnResult install_v1(KilnOwner owner, uintptr_t target, int32_t priority, KilnDetourFn fn, void* user,
                      KilnHandle* out) {
    Order o;
    o.priority = priority;
    return install(Kind::V1, owner, target, o, reinterpret_cast<void*>(fn), user, 4, nullptr, out);
}
KilnResult install_mid(KilnOwner owner, uintptr_t target, const Order& order, KilnMidFn fn, void* user,
                       KilnHandle* out) {
    return install(Kind::Mid, owner, target, order, reinterpret_cast<void*>(fn), user, 0, nullptr, out);
}

// ---------------------------------------------------------------------------------------------------------------------
// Structured dispatch

namespace {

using Fn12 = uintptr_t(__fastcall*)(uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t,
                                    uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t);

struct Frame {
    uint32_t magic = kFrameMagic;
    Link* link = nullptr;
    uintptr_t args[12] = {};
    uintptr_t caller = 0;
    bool in_original = false;  // read by the SEH filter: a fault inside the rest of the chain is not this handler's
    bool called = false;
    uintptr_t original_ret = 0;
};

uintptr_t run_rest(Frame* f) {
    f->in_original = true;
    f->called = true;
    const uintptr_t* a = f->args;
    const uintptr_t r = reinterpret_cast<Fn12>(f->link->cont)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8],
                                                              a[9], a[10], a[11]);
    f->in_original = false;
    f->original_ret = r;
    return r;
}

}  // namespace
}  // namespace kiln::hooks::chain

extern "C" uintptr_t kiln_hk_structured_dispatch(const uintptr_t* regs, const uintptr_t* stack, void* lp) {
    using namespace kiln::hooks::chain;
    auto* link = static_cast<Link*>(lp);
    Frame f;
    f.link = link;
    for (int i = 0; i < 4; ++i) f.args[i] = regs[i];
    for (int i = 0; i < 8; ++i) f.args[4 + i] = stack[i];
    if (!link->live.load(std::memory_order_acquire)) return run_rest(&f);  // revoked or disabled: just forward
    S().detour_calls.fetch_add(1, std::memory_order_relaxed);
    if (Block* b = current_block()) f.caller = b->caller[link->slot];
    uintptr_t ret = 0;
    unsigned long code = 0;
    bool ok;
    if (link->kind == Kind::V1) {
        KilnHookCall c{};
        c.size = sizeof c;
        c.arg_count = 4;
        c.hook = link->handle;
        for (int i = 0; i < 4; ++i) c.args[i] = f.args[i];
        c.reserved = &f;
        ok = kiln::hooks::seh::call_detour(reinterpret_cast<KilnDetourFn>(link->fn), &c, link->user, &ret, &code,
                                           &f.in_original);
    } else {
        KilnHookCall2 c{};
        c.size = sizeof c;
        c.arg_count = link->arg_count;
        c.hook = link->handle;
        for (int i = 0; i < 12; ++i) c.args[i] = f.args[i];
        c.caller = f.caller;
        c.reserved = &f;
        ok = kiln::hooks::seh::call_detour2(reinterpret_cast<KilnDetourFn2>(link->fn), &c, link->user, &ret, &code,
                                            &f.in_original);
    }
    if (ok) return ret;
    link->live.store(false, std::memory_order_release);
    link->target->dirty.store(true, std::memory_order_release);
    KLOG_E("hooks: %s link \"%s\" (handle %llu, owner %s) faulted (0x%08lx) and was disabled",
           kind_name(link->kind), link->label.c_str(), static_cast<unsigned long long>(link->handle),
           link->owner_name.c_str(), code);
    if (S().env.changed) S().env.changed();
    return f.called ? f.original_ret : run_rest(&f);
}

extern "C" void kiln_hk_mid_dispatch(void* ctx) {
    using namespace kiln::hooks::chain;
    auto* regs = static_cast<KilnRegisters*>(ctx);
    auto* link = *reinterpret_cast<Link**>(regs->trampoline_rsp + 8);
    if (!link->live.load(std::memory_order_acquire)) return;
    S().mid_calls.fetch_add(1, std::memory_order_relaxed);
    unsigned long code = 0;
    if (!kiln::hooks::seh::call_mid(reinterpret_cast<KilnMidFn>(link->fn), regs, link->user, &code)) {
        link->live.store(false, std::memory_order_release);
        link->target->dirty.store(true, std::memory_order_release);
        KLOG_E("hooks: mid link \"%s\" (handle %llu, owner %s) faulted (0x%08lx) and was disabled",
               link->label.c_str(), static_cast<unsigned long long>(link->handle), link->owner_name.c_str(), code);
        if (S().env.changed) S().env.changed();
    }
}

namespace kiln::hooks::chain {

uintptr_t call_original2(KilnHookCall2* call) {
    if (!call || !call->reserved) return 0;
    auto* f = static_cast<Frame*>(call->reserved);
    if (f->magic != kFrameMagic) return 0;
    std::memcpy(f->args, call->args, sizeof f->args);
    return run_rest(f);
}

uintptr_t call_original_v1(KilnHookCall* call) {
    if (!call || !call->reserved) return 0;
    auto* f = static_cast<Frame*>(call->reserved);
    if (f->magic != kFrameMagic) return 0;
    for (int i = 0; i < 4; ++i) f->args[i] = call->args[i];
    return run_rest(f);
}

uintptr_t caller_of(KilnHandle h) {
    Link* l = lookup(h);
    if (!l) return 0;
    Block* b = current_block();
    return b ? b->caller[l->slot] : 0;
}

bool is_link_handle(KilnHandle h) { return lookup(h) != nullptr; }

KilnResult revoke(KilnHandle h) {
    State& s = S();
    Link* l = lookup(h);
    if (!l) return KILN_ERR_NOT_FOUND;
    std::lock_guard lock(s.mutex);
    if (!l->live.exchange(false, std::memory_order_acq_rel)) return KILN_ERR_NOT_FOUND;
    rebuild(*l->target);
    KLOG_D("hooks: revoked %s link \"%s\" on rva %s (handle %llu)", kind_name(l->kind), l->label.c_str(),
           hex(rva_of(l->target->addr)).c_str(), static_cast<unsigned long long>(l->handle));
    notify_changed();
    return KILN_OK;
}

size_t revoke_owner(KilnOwner owner) {
    State& s = S();
    std::lock_guard lock(s.mutex);
    size_t removed = 0;
    for (Target* t : s.targets) {
        bool any = false;
        for (Link* l : t->order)
            if (l->owner == owner && l->live.exchange(false, std::memory_order_acq_rel)) any = true, ++removed;
        if (any) rebuild(*t);
    }
    if (removed) notify_changed();
    return removed;
}

Stats stats() {
    State& s = S();
    Stats r;
    std::lock_guard lock(s.mutex);
    r.targets = static_cast<uint32_t>(s.targets.size());
    for (Target* t : s.targets) {
        bool detour = false, mid = false;
        for (Link* l : t->order) {
            if (!l->live.load()) continue;
            ++r.links;
            if (l->kind == Kind::Mid) mid = true;
            else detour = true;
            if (l->kind == Kind::Raw) ++r.raw_links;
        }
        r.detour_targets += detour;
        r.mid_targets += mid;
        if (t->before_present) (t->before.own ? r.own_before : r.foreign_before)++;
        if (t->over_present) (t->over.own ? r.own_over : r.foreign_over)++;
    }
    r.detour_calls = s.detour_calls.load(std::memory_order_relaxed);
    r.mid_calls = s.mid_calls.load(std::memory_order_relaxed);
    return r;
}

void note_world_checked() { S().world_checked.store(true, std::memory_order_release); }

void sweep() {
    State& s = S();
    std::lock_guard lock(s.mutex);
    for (Target* t : s.targets)
        if (t->dirty.exchange(false, std::memory_order_acq_rel)) rebuild(*t);
}

size_t check_foreign() {
    State& s = S();
    std::lock_guard lock(s.mutex);
    size_t over = 0;
    bool changed = false;
    for (Target* t : s.targets) {
        uint8_t now[kFootprint] = {};
        if (!read_guarded(t->addr, now, sizeof now)) continue;
        const bool differs = std::memcmp(now, t->own, t->own_len) != 0;
        if (differs && !t->over_present) {
            t->over = analyze(t->addr, now);
            t->over_present = true;
            changed = true;
            if (t->over.own)
                KLOG_I("hooks: %s (a Kiln module) also hooks %s and sits in front of the registry's detour; both run",
                       dest_text(t->over).c_str(), target_label(*t).c_str());
            else if (!t->over.module.empty())
                KLOG_W("hooks: %s hooked over Kiln's %s (Kiln's detour stays armed; it is reached through their jump)",
                       dest_text(t->over).c_str(), target_label(*t).c_str());
            else
                KLOG_W("hooks: something patched over Kiln's %s at rva %s (%s)", target_label(*t).c_str(),
                       hex(rva_of(t->addr)).c_str(), dest_text(t->over).c_str());
        } else if (!differs && t->over_present) {
            t->over_present = false;
            t->over = {};
            changed = true;
            KLOG_I("hooks: Kiln's detour on %s is no longer hooked over", target_label(*t).c_str());
        }
        over += t->over_present && !t->over.own;
    }
    if (changed) notify_changed();
    return over;
}

// ---------------------------------------------------------------------------------------------------------------------
// The hook map

void add_map_extension(MapExtension fn) {
    State& s = S();
    std::lock_guard lock(s.mutex);
    s.extensions.push_back(fn);
}

std::vector<TargetInfo> targets() {
    State& s = S();
    std::vector<TargetInfo> out;
    std::lock_guard lock(s.mutex);
    for (Target* t : s.targets) {
        TargetInfo ti;
        ti.address = t->addr;
        ti.rva = rva_of(t->addr);
        ti.symbol = s.env.symbol_at ? s.env.symbol_at(t->addr) : std::string();
        ti.label = target_label(*t);
        for (Link* l : t->order)
            if (l->live.load(std::memory_order_acquire))
                ti.links.push_back({l->owner_name, l->label, l->kind, l->priority});
        ti.has_before = t->before_present;
        ti.before = t->before;
        ti.has_over = t->over_present;
        ti.over = t->over;
        out.push_back(std::move(ti));
    }
    return out;
}

std::string map_text(const std::vector<ForeignSym>& foreign, const char* phase) {
    State& s = S();
    json doc;
    doc["format"] = 1;
    doc["kiln"] = std::string(kVersion);
    doc["phase"] = phase ? phase : "";
    doc["worldChecked"] = s.world_checked.load(std::memory_order_acquire);
    doc["generated"] = utc_now();
    json jts = json::array();
    for (const TargetInfo& t : targets()) {
        json jt;
        jt["rva"] = hex(t.rva);
        jt["symbol"] = t.symbol.empty() ? json(nullptr) : json(t.symbol);
        jt["label"] = t.label;
        json links = json::array();
        for (const auto& l : t.links) {
            json jl;
            jl["owner"] = l.owner;
            jl["kind"] = kind_name(l.kind);
            jl["priority"] = l.priority;
            jl["label"] = l.label;
            links.push_back(std::move(jl));
        }
        jt["links"] = std::move(links);
        jt["foreignBefore"] = t.has_before ? dest_json(t.before) : json(nullptr);
        jt["foreignOver"] = t.has_over ? dest_json(t.over) : json(nullptr);
        jts.push_back(std::move(jt));
    }
    doc["targets"] = std::move(jts);
    json fs = json::array();
    for (const ForeignSym& f : foreign) {
        json row;
        row["symbol"] = f.symbol;
        row["rva"] = hex(f.rva);
        const json jf = dest_json(f.dest);
        for (auto it = jf.begin(); it != jf.end(); ++it) row[it.key()] = it.value();
        fs.push_back(std::move(row));
    }
    doc["foreign"] = std::move(fs);
    std::vector<MapExtension> ext;
    {
        std::lock_guard lock(s.mutex);
        ext = s.extensions;
    }
    for (MapExtension fn : ext) fn(&doc, phase);
    return doc.dump(2, ' ', false, json::error_handler_t::replace) + "\n";  // never throw on a bad label
}

// ---------------------------------------------------------------------------------------------------------------------
// Introspection for the test and the map

size_t link_count() { return S().next_link; }

size_t thread_tables() {
    State& s = S();
    AcquireSRWLockShared(&s.block_lock);
    const size_t n = s.block_count;
    ReleaseSRWLockShared(&s.block_lock);
    return n;
}

bool link_info(KilnHandle h, LinkInfo* out) {
    Link* l = lookup(h);
    if (!l) return false;
    if (out) {
        out->live = l->live.load();
        out->kind = l->kind;
        out->entry = reinterpret_cast<uintptr_t>(l->entry);
        out->cont = reinterpret_cast<uintptr_t>(l->cont);
        out->target = l->target->addr;
        out->owner = l->owner_name;
        out->label = l->label;
        out->priority = l->priority;
    }
    return true;
}

std::vector<KilnHandle> chain_of(uintptr_t target) {
    State& s = S();
    std::lock_guard lock(s.mutex);
    std::vector<KilnHandle> out;
    auto it = s.by_addr.find(target);
    if (it == s.by_addr.end()) return out;
    for (Link* l : it->second->order)
        if (l->live.load()) out.push_back(l->handle);
    return out;
}

}  // namespace kiln::hooks::chain
