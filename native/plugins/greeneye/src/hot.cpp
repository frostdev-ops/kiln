// SPDX-License-Identifier: MIT
// Ported from Greeneye Loader 1.2.2 src/hot.cpp (MIT, Copyright (c) 2026 Greeneye0; licenses/GreeneyeLoader-LICENSE.txt),
// github.com/Greeneye0/greeneye-loader-enshrouded commit 4b13108. Changes for Kiln: logging through Kiln (one log
// tag per mod), mod folders from Kiln profiles as well as greeneye/, an extra safe point on Kiln's client-world tick,
// and status queries for kiln/state/greeneye.json. Kiln additionally hardens loading, staging, leases and reads.
//
// The loader owns every hook of every mod (API 2 of common/greeneye.h), so a mod can be replaced
// while the game or the dedicated server runs. Ported from Find's development loader
// (FindMod/loader/main.cpp, HOTLOADING.md), extended to several mods.
//
// Hooks: a mod calls host->hook / host->redirectCall while it starts. The loader patches the
// target once, to jump to a small thunk ("jmp [rip+10]"; destination at thunk + 16), and points
// the thunk at the mod's detour. A reload starts the new generation of the mod against the same
// targets and then swaps every thunk of that mod at once (an aligned pointer store). Patched
// bytes stay as they are. Another mod's jump already at a target (our mods' "mov rax, imm64;
// jmp rax", Creative Mode's safetyhook "jmp rel32" / "jmp [rip+x]", or one of our own slots) is
// chained, never overwritten.
//
// New hook sites on a reload: a place no earlier generation hooked gets its slot, thunk,
// trampoline and patch bytes while the new generation starts, but nothing is written yet
// ("staged"). If the generation does not start, the staged slots are dropped. If it starts, the
// swap writes them: every other thread of the process is suspended, and if none stands inside a
// byte range about to change, the patches are written before the threads resume. Otherwise the
// threads resume and the swap waits for the next safe point (the old generation stays active);
// after kCommitTries safe points the reload fails. While threads are suspended the loader
// allocates nothing, logs nothing and takes no lock, since a suspended thread may hold one.
//
// Leases: every detour enters its mod's gate (shared, nestable per thread) and leaves when done.
// A swap takes that one mod's gate exclusively, only when no thread is inside that mod and the
// swapping thread holds none of its leases, so no code of the mod runs while its generation
// changes. A thread inside another mod (a render hook that runs most of the frame) never holds a
// reload up. Nobody ever waits for exclusive access, so a nested shared acquire cannot deadlock.
// A reload that waits longer than kWaitReport says in hotload.status what it waits for.
//
// Reloads (only with greeneye\hotreload.enable): mods load from shadow copies in
// greeneye\<id>\.hot\, so a build can replace greeneye\<id>\<dll> while the game runs. A watcher
// thread picks up greeneye\<id>\reload, loads the new copy (outside the gate: LoadLibrary takes
// the loader lock) and leaves it pending. At the next safe point (game: the top of the UI frame,
// or a mod's host->poll; dedicated server: the watcher itself), the old generation prepares
// until ready, the new one starts, and the thunks swap; if the new one does not start, the old
// one resumes. greeneye\<id>\hotload.status says how it went. No generation is ever unloaded.
#include <windows.h>
#include <tlhelp32.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <limits>
#include <mutex>
#include <cctype>

#include "../../compat/compat.h"
#include <utility>
#include <cstdio>
#include <map>
#include <memory>
#include <string>
#include <vector>
#include <greeneye/jumppatch.h>

#include "greeneye_internal.h"

using kgreeneye::Log;
using kgreeneye::Narrow;

namespace hot {
namespace {

struct Mod;

struct Generation {
    Mod* mod = nullptr;
    unsigned number = 0;
    HMODULE module = nullptr;
    GreeneyeHost host{};
    const GreeneyeHot* api = nullptr;
    GreeneyeHot hotApi{};
    ULONGLONG begin = 0; // when it started
};

struct Mod {
    ModInfo info;
    std::vector<std::unique_ptr<Generation>> generations; // all retained, never unloaded
    std::atomic<Generation*> active{nullptr};
    Generation* pending = nullptr; // guarded by g_pendingLock
    bool preparing = false;
    bool started = false;          // the first generation ran (new hook sites are staged from now on)
    unsigned serial = 0;
    SRWLOCK gate = SRWLOCK_INIT;   // shared: a thread is inside this mod; exclusive: a swap
    ULONGLONG waitSince = 0;       // when the pending generation first had to wait
    const char* waitReason = nullptr;
    bool waitReported = false;
};

// One patched place, owned by one mod.
struct Slot {
    Mod* owner = nullptr;
    uint8_t* target = nullptr;
    size_t length = 0;
    uint8_t saved[32] = {}; // the bytes before this slot patched them
    bool call = false;      // a redirected "call rel32", not a function start
    uint8_t* thunk = nullptr;
    void* base = nullptr;   // what to call on: trampoline, previous jump target or callee
    void* pending = nullptr;
    Generation* boundBy = nullptr; // the generation that last bound it (one mod may hook a place twice)
    // The patch: write[0, writeLength) goes to writeAt. None for a second hook of a staged place.
    bool staged = false;           // prepared by a reload generation, not written yet
    uint8_t* writeAt = nullptr;
    uint8_t write[32] = {};
    uint8_t beforeWrite[32] = {};
    size_t writeLength = 0;
    uint8_t* lead = nullptr;       // the thunk the patch jumps to
    uint8_t* leadsTo = nullptr;    // where the patch keeps lead (in write or stub; not for a call)
    uint8_t* trampoline = nullptr;
    uint8_t* stub = nullptr;       // "jmp [rip+0]; lead" near a chained "jmp rel32"
};

int g_side = 0;
bool g_hotReload = false;
std::wstring g_gameDir;
SRWLOCK g_pollLock = SRWLOCK_INIT;  // one Poll at a time (the UI frame and a mod's host->poll)
SRWLOCK g_slotsLock = SRWLOCK_INIT; // g_slots changing (a reload) vs. host->read from any thread
// This thread's leases, innermost last (leases are scoped, so they end in order). A mod's gate is
// taken by its outermost lease on the thread only.
constexpr unsigned kMaxNest = 64;
thread_local Mod* t_leases[kMaxNest];
thread_local unsigned t_depth = 0, t_overflow = 0;
thread_local Mod* t_swapping = nullptr; // this thread already owns the exclusive gate for these callbacks
constexpr ULONGLONG kWaitReport = 10000; // ms before a waiting reload says why in hotload.status
CRITICAL_SECTION g_pendingLock;
CRITICAL_SECTION g_keepLock;
std::vector<std::unique_ptr<Slot>> g_slots;      // in creation order
std::vector<std::unique_ptr<Mod>> g_mods;
thread_local Generation* g_staging = nullptr;                // the generation starting right now
thread_local bool g_stagingFailed = false;
Generation* g_committing = nullptr;             // started; its new hook sites wait to be written
unsigned g_commitTries = 0;
constexpr unsigned kCommitTries = 100;          // safe points to wait for them, then the reload fails
std::map<std::string, void*> g_keep;
std::atomic<bool> g_initialized{false}, g_starting{true}, g_stopping{false};
HANDLE g_watcherStop = nullptr;
std::vector<std::unique_ptr<Slot>> g_retiredSlots; // mod threads may retain returned trampolines after failed starts
std::unique_ptr<Mod> g_loaderMod;               // the loader's own safe-point hook

// --- leases ---------------------------------------------------------------------------------

bool Holds(const Mod* mod)
{
    for (unsigned i = 0; i < t_depth; ++i)
        if (t_leases[i] == mod)
            return true;
    return false;
}

bool Enter(void* token)
{
    auto* g = static_cast<Generation*>(token);
    Mod* mod = g ? g->mod : nullptr;
    if (t_depth == kMaxNest) {
        ++t_overflow; // absurdly deep: the detour calls straight on, untouched by any swap
        return false;
    }
    // The swapping thread already owns this gate exclusively while starting the new generation.
    // Its lease is inactive, just as at the initial start, and must not recursively acquire the gate.
    if (g == g_staging || mod == t_swapping)
        mod = nullptr;
    if (mod && !Holds(mod))
        AcquireSRWLockShared(&mod->gate);
    t_leases[t_depth++] = mod;
    return mod && mod->active.load() == g;
}

void Leave()
{
    if (t_overflow) {
        --t_overflow;
        return;
    }
    if (!t_depth)
        return;
    Mod* mod = t_leases[--t_depth];
    if (mod && !Holds(mod))
        ReleaseSRWLockShared(&mod->gate);
}

// --- the code as it was before our hooks ----------------------------------------------------

bool Span(const void* p, size_t n)
{
    return p && n <= (std::numeric_limits<uintptr_t>::max)() - reinterpret_cast<uintptr_t>(p);
}

bool Copy(void* out, const void* p, size_t n)
{
    if (!n) return true;
    if (!Span(p, n) || !Span(out, n)) return false;
    __try {
        jumppatch::CopyBytes(out, p, n); // a real call: clang-cl's __try only sees faults inside calls
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void Read(const void* p, void* out, size_t n)
{
    if (!n || !Span(p, n) || !Span(out, n)) return;
    AcquireSRWLockShared(&g_slotsLock);
    if (Copy(out, p, n)) {
        // Restore the actual patch operands, including a RIP-indirect jump's pointer outside target's prologue.
        const auto at = reinterpret_cast<uintptr_t>(p);
        for (auto it = g_slots.rbegin(); it != g_slots.rend(); ++it) {
            const Slot& s = **it;
            if (s.staged || !s.writeLength) continue;
            const auto a = reinterpret_cast<uintptr_t>(s.writeAt);
            const uintptr_t lo = (std::max)(at, a), hi = (std::min)(at + n, a + s.writeLength);
            if (lo < hi)
                Copy(static_cast<uint8_t*>(out) + (lo - at), s.beforeWrite + (lo - a), hi - lo);
        }
    }
    ReleaseSRWLockShared(&g_slotsLock);
}

uint8_t* g_text = nullptr;
size_t g_textSize = 0;
std::once_flag g_canonicalOnce;
std::vector<uint8_t> g_canonical; // .text as it was before our hooks (made on first find)

bool TextSection(uint8_t*& start, size_t& size)
{
    auto base = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
    auto sec = IMAGE_FIRST_SECTION(nt);
    for (int i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++sec) {
        if (memcmp(sec->Name, ".text", 5) == 0) {
            start = base + sec->VirtualAddress;
            size = sec->Misc.VirtualSize;
            return true;
        }
    }
    return false;
}

std::vector<int> ParsePattern(const char* pattern)
{
    std::vector<int> bytes;
    if (!pattern) return bytes;
    auto hex = [](unsigned char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    for (const char* p = pattern; *p;) {
        if (std::isspace(static_cast<unsigned char>(*p))) { ++p; continue; }
        if (*p == '?') {
            bytes.push_back(-1);
            p += p[1] == '?' ? 2 : 1;
        } else {
            const int a = hex(static_cast<unsigned char>(p[0]));
            if (!p[1]) return {};
            const int b = hex(static_cast<unsigned char>(p[1]));
            if (a < 0 || b < 0) return {};
            bytes.push_back((a << 4) | b);
            p += 2;
        }
    }
    return bytes;
}

uint8_t* Find(const char* pattern, void* module)
{
    if (module && module != GetModuleHandleW(nullptr))
        return nullptr; // only the exe's code
    std::call_once(g_canonicalOnce, [] {
        if (!TextSection(g_text, g_textSize)) return;
        g_canonical.resize(g_textSize);
        Read(g_text, g_canonical.data(), g_textSize);
    });
    std::vector<int> pat = ParsePattern(pattern);
    const uint8_t* text = g_canonical.data();
    const size_t size = g_canonical.size(), n = pat.size();
    if (!n || n > size || pat[0] < 0) return nullptr;
    const uint8_t first = static_cast<uint8_t>(pat[0]);
    const uint8_t* found = nullptr;
    for (size_t offset = 0; offset <= size - n;) {
        const auto* p = static_cast<const uint8_t*>(memchr(text + offset, first, size - n - offset + 1));
        if (!p) break;
        offset = static_cast<size_t>(p - text) + 1;
        size_t k = 1;
        while (k < n && (pat[k] < 0 || p[k] == pat[k])) ++k;
        if (k == n) {
            if (found) return nullptr;
            found = p;
        }
    }
    if (!found)
        found = jumppatch::FindBehindHook(g_canonical.data(), size, pat.data(), n);
    return found ? g_text + (found - text) : nullptr;
}

// --- hooks ------------------------------------------------------------------------------------

uint8_t* NewThunk(void* destination, const uint8_t* near_)
{
    // jmp [rip+10]; ...; destination at +16. Near the target when a rel32 must reach it.
    auto* t = near_ ? jumppatch::AllocNear(near_, 32)
                    : static_cast<uint8_t*>(VirtualAlloc(nullptr, 32, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    if (!t)
        return nullptr;
    t[0] = 0xFF;
    t[1] = 0x25;
    int32_t displacement = 10;
    memcpy(t + 2, &displacement, 4);
    memcpy(t + 16, &destination, 8);
    FlushInstructionCache(GetCurrentProcess(), t, 32);
    return t;
}

// The mod's next slot at target not yet bound by generation g: a mod may hook the same place
// twice (Auto Loot's chat line for its window and for /loot), each in order, every generation.
Slot* OwnSlot(Generation* g, uint8_t* target)
{
    for (auto& s : g_slots)
        if (s->owner == g->mod && s->target == target && s->boundBy != g)
            return s.get();
    return nullptr;
}

// Frees what a slot allocated (a staged slot that is dropped, or one that failed to prepare).
void FreeSlot(Slot& s)
{
    for (uint8_t* p : {s.thunk, s.trampoline, s.stub})
        if (p)
            VirtualFree(p, 0, MEM_RELEASE);
}

void* Refuse(Slot* s)
{
    if (s)
        FreeSlot(*s);
    g_stagingFailed = true;
    return nullptr;
}

std::unique_ptr<Slot> NewSlot(Generation* g, uint8_t* target, size_t n, void* detour)
{
    auto s = std::make_unique<Slot>();
    s->owner = g->mod;
    s->target = target;
    s->length = n;
    if (!Copy(s->saved, target, n)) return nullptr;
    s->pending = detour;
    s->boundBy = g;
    return s;
}

// Points s's patch (or, for a second hook of a staged place, the patch that holds it) at thunk.
void Retarget(Slot& s, uint8_t* thunk)
{
    s.lead = thunk;
    if (s.call) {
        int32_t d = static_cast<int32_t>(thunk - (s.target + 5));
        memcpy(s.write, &d, 4);
    } else {
        memcpy(s.leadsTo, &thunk, 8);
        if (s.stub)
            FlushInstructionCache(GetCurrentProcess(), s.stub, 16);
    }
}

// The staged slot that holds the patch at target, if any: a reload that hooks a new place twice
// chains the second hook onto the first inside the staged patch.
Slot* StagedAt(uint8_t* target, bool call)
{
    for (auto& s : g_slots)
        if (s->staged && s->target == target && s->call == call && s->writeLength)
            return s.get();
    return nullptr;
}

// Stages s's patch for publication after the generation finishes starting (WriteStaged).
void* Place(std::unique_ptr<Slot> s)
{
    // Never publish a hook until the mod has stored the returned trampoline and finished starting.
    s->staged = true;
    if (s->writeLength && !Copy(s->beforeWrite, s->writeAt, s->writeLength))
        return Refuse(s.get());
    void* base = s->base;
    AcquireSRWLockExclusive(&g_slotsLock);
    g_slots.push_back(std::move(s));
    ReleaseSRWLockExclusive(&g_slotsLock);
    return base;
}

void* Hook(uint8_t* target, void* detour, size_t n, const uint8_t* code, size_t codeSize)
{
    Generation* g = g_staging;
    if (!g || !target || !detour || n < 12 || n > 32 || codeSize > 48 || (!code && codeSize))
        return Refuse(nullptr);
    if (Slot* s = OwnSlot(g, target)) { // a reload: same place, new detour
        s->pending = detour;
        s->boundBy = g;
        return s->base;
    }
    auto s = NewSlot(g, target, n, detour);
    if (!s) return Refuse(nullptr);
    if (Slot* first = StagedAt(target, false)) {
        // This generation hooks a new place twice: in front of its first hook, as if written.
        s->base = first->lead;
        s->thunk = NewThunk(s->base, nullptr);
        if (!s->thunk)
            return Refuse(s.get());
        Retarget(*first, s->thunk);
    } else if (jumppatch::Kind kind = jumppatch::KindOf(target); kind != jumppatch::None) {
        // Another mod's (or another of our slots') jump: point it at our thunk, call on through it.
        s->base = jumppatch::Destination(target);
        s->thunk = s->base ? NewThunk(s->base, nullptr) : nullptr;
        if (!s->thunk)
            return Refuse(s.get());
        if (kind == jumppatch::MovRax) {
            s->writeAt = target + 2;
            s->writeLength = 8;
            s->leadsTo = s->write;
        } else if (kind == jumppatch::Rel32) {
            s->stub = jumppatch::AllocNear(target, 16); // jmp [rip+0]; thunk
            if (!s->stub)
                return Refuse(s.get());
            s->stub[0] = 0xFF;
            s->stub[1] = 0x25;
            memset(s->stub + 2, 0, 4);
            int32_t d = static_cast<int32_t>(s->stub - (target + 5));
            s->writeAt = target + 1;
            memcpy(s->write, &d, 4);
            s->writeLength = 4;
            s->leadsTo = s->stub + 6;
        } else { // jmp [rip+x]: the pointer it jumps through
            int32_t d;
            memcpy(&d, target + 2, 4);
            s->writeAt = target + 6 + d;
            s->writeLength = 8;
            s->leadsTo = s->write;
        }
        Retarget(*s, s->thunk);
    } else {
        // Trampoline: the overwritten instructions (or their replacement), then back.
        auto* t = static_cast<uint8_t*>(VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
        if (!t)
            return Refuse(s.get());
        s->trampoline = t;
        if (!Copy(t, code ? code : target, code ? codeSize : n))
            return Refuse(s.get());
        size_t len = code ? codeSize : n;
        void* back = target + n;
        t[len] = 0xFF;
        t[len + 1] = 0x25;
        memset(t + len + 2, 0, 4);
        memcpy(t + len + 6, &back, 8);
        FlushInstructionCache(GetCurrentProcess(), t, 64);
        s->base = t;
        s->thunk = NewThunk(s->base, nullptr);
        if (!s->thunk)
            return Refuse(s.get());
        s->write[0] = 0x48; // mov rax, thunk; jmp rax; nops
        s->write[1] = 0xB8;
        s->write[10] = 0xFF;
        s->write[11] = 0xE0;
        memset(s->write + 12, 0x90, n - 12);
        s->writeAt = target;
        s->writeLength = n;
        s->leadsTo = s->write + 2;
        Retarget(*s, s->thunk);
    }
    return Place(std::move(s));
}

void* RedirectCall(uint8_t* call, void* detour)
{
    Generation* g = g_staging;
    uint8_t opcode = 0;
    if (!g || !call || !detour || !Copy(&opcode, call, 1) || opcode != 0xE8)
        return Refuse(nullptr);
    if (Slot* s = OwnSlot(g, call)) {
        s->pending = detour;
        s->boundBy = g;
        return s->base;
    }
    auto s = NewSlot(g, call, 5, detour);
    if (!s) return Refuse(nullptr);
    s->call = true;
    Slot* first = StagedAt(call, true); // this generation redirects a new call twice
    s->base = first ? first->lead : jumppatch::CallTarget(call);
    s->thunk = NewThunk(s->base, call); // the call's rel32 must reach it
    if (!s->thunk)
        return Refuse(s.get());
    if (first) {
        Retarget(*first, s->thunk);
    } else {
        s->writeAt = call + 1;
        s->writeLength = 4;
        Retarget(*s, s->thunk);
    }
    return Place(std::move(s));
}

void* Keep(const char* key, size_t size)
{
    EnterCriticalSection(&g_keepLock);
    void*& p = g_keep[key];
    if (!p)
        p = VirtualAlloc(nullptr, size ? size : 1, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    void* result = p;
    LeaveCriticalSection(&g_keepLock);
    return result;
}

// host->log has no context argument, so every mod gets its own function: HostLogAt<N> logs under the tag of the mod
// with index N (kMaxLogSlots of them; mods past that share HostLogShared, which logs under the loader's tag).
constexpr size_t kMaxLogSlots = 128;
template <size_t N>
void HostLogAt(const char* line)
{
    kgreeneye::ModLog(N, line ? line : "");
}
void HostLogShared(const char* line)
{
    Log("  %s", line ? line : "");
}
template <size_t... I>
constexpr auto MakeLogTable(std::index_sequence<I...>)
{
    return std::array<void (*)(const char*), sizeof...(I)>{&HostLogAt<I>...};
}
constexpr auto kHostLogs = MakeLogTable(std::make_index_sequence<kMaxLogSlots>{});

void (*HostLogFor(size_t index))(const char*)
{
    return index < kMaxLogSlots ? kHostLogs[index] : &HostLogShared;
}

void Poll();

// --- generations ------------------------------------------------------------------------------

void Status(Mod& mod, const std::string& text)
{
    Log("%s: %s", mod.info.name.c_str(), text.c_str());
    if (!g_hotReload)
        return;
    if (FILE* f = _wfopen((mod.info.dir + L"hotload.status").c_str(), L"w")) {
        fprintf(f, "%s\n", text.c_str());
        fclose(f);
    }
}

// Loads a new generation of mod (a shadow copy when hot reload is on). Outside the gate.
Generation* Load(Mod& mod, std::string& why)
{
    unsigned number = ++mod.serial;
    std::filesystem::path root(mod.info.dir);
    if (root.filename().empty()) root = root.parent_path();
    kcompat::CheckedDll source;
    if (!source.open(root, mod.info.dll, "GreeneyeModStart")) {
        why = "unsafe DLL: " + source.error;
        return nullptr;
    }
    std::wstring path = mod.info.dll;
    kcompat::CheckedPath shadowDirectory;
    if (g_hotReload) {
        std::wstring shadowDir = mod.info.dir + L".hot\\";
        CreateDirectoryW(shadowDir.c_str(), nullptr);
        if (!shadowDirectory.open(root, std::filesystem::path(shadowDir).parent_path(), false)) {
            why = "unsafe shadow directory: " + shadowDirectory.error;
            return nullptr;
        }
        SetFileAttributesW(shadowDir.c_str(), FILE_ATTRIBUTE_HIDDEN);
        const wchar_t* file = wcsrchr(mod.info.dll.c_str(), L'\\');
        std::wstring stem = file ? file + 1 : mod.info.dll;
        if (stem.size() > 4)
            stem.resize(stem.size() - 4);
        path = shadowDir + stem + L"." + std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(number) + L".dll";
        if (!CopyFileW(mod.info.dll.c_str(), path.c_str(), TRUE)) {
            why = "could not copy " + Narrow(mod.info.dll) + " (error " + std::to_string(GetLastError()) + ")";
            return nullptr;
        }
    }
    kcompat::CheckedDll image;
    if (!image.open(root, path, "GreeneyeModStart")) {
        why = "unsafe DLL: " + image.error;
        return nullptr;
    }
    DWORD loadFault = 0;
    HMODULE module = image.load(&loadFault);
    if (!module) {
        why = "could not load " + Narrow(path) + (loadFault ? " (DllMain fault " + std::to_string(loadFault) + ")" : " (error " + std::to_string(GetLastError()) + ")");
        return nullptr;
    }
    auto g = std::make_unique<Generation>();
    g->mod = &mod;
    g->number = number;
    g->module = module;
    GreeneyeHost& h = g->host;
    h.size = sizeof(GreeneyeHost);
    h.apiVersion = kGreeneyeApiVersion;
    h.side = g_side;
    h.modDir = mod.info.dir.c_str();
    h.gameDir = g_gameDir.c_str();
    h.log = HostLogFor(mod.info.index);
    h.generation = number;
    h.hotReload = g_hotReload;
    h.token = g.get();
    h.enter = &Enter;
    h.leave = &Leave;
    h.find = &Find;
    h.read = &Read;
    h.hook = &Hook;
    h.redirectCall = &RedirectCall;
    h.poll = &Poll;
    h.keep = &Keep;
    Generation* raw = g.get();
    EnterCriticalSection(&g_pendingLock);
    mod.generations.push_back(std::move(g)); // retained even if it never starts
    LeaveCriticalSection(&g_pendingLock);
    return raw;
}

bool SafeStart(GreeneyeModStartFn start, const GreeneyeHost* host, DWORD* fault)
{
    __try {
        return start(host);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *fault = GetExceptionCode();
        return false;
    }
}

// Reads the mod's GreeneyeHot table; out of line so a bad pointer faults inside a call SafeHot's __try covers
// (clang-cl, without /EHa, does not map a fault in an inlined load to the handler).
__declspec(noinline) void TakeHot(const GreeneyeHot* api, GreeneyeHot* out)
{
    if (api && api->size >= sizeof(GreeneyeHot)) *out = *api;
}

bool SafeHot(GreeneyeModHotFn fn, GreeneyeHot* out, DWORD* fault)
{
    __try {
        TakeHot(fn(), out);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *fault = GetExceptionCode();
        return false;
    }
}

DWORD SafeCall(void (*fn)())
{
    __try {
        fn();
        return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return GetExceptionCode();
    }
}

bool SafeBool(bool (*fn)(), bool fallback, DWORD* fault)
{
    __try {
        return fn();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *fault = GetExceptionCode();
        return fallback;
    }
}

// Drops the hook sites mod's new generation staged: none of it was ever written.
void DropStaged(Mod& mod)
{
    AcquireSRWLockExclusive(&g_slotsLock);
    std::erase_if(g_slots, [&](std::unique_ptr<Slot>& s) {
        if (s->owner != &mod || !s->staged)
            return false;
        g_retiredSlots.push_back(std::move(s));
        return true;
    });
    ReleaseSRWLockExclusive(&g_slotsLock);
}

enum class Patch { Written, Busy, Failed };

// Writes the hook sites mod's new generation staged, with every other thread of the process
// suspended and none of them inside a range about to change. Busy: one was, try again later.
// From the first SuspendThread to the last ResumeThread: no allocation, no Log, no lock.
Patch WriteStaged(Mod& mod, size_t& count, std::string& why)
{
    struct Range {
        uintptr_t lo, hi;
    };
    struct Thread {
        HANDLE handle;
        bool suspended;
    };
    struct SlotLock {
        SlotLock() { AcquireSRWLockExclusive(&g_slotsLock); }
        ~SlotLock() { ReleaseSRWLockExclusive(&g_slotsLock); }
    } slotLock;
    std::vector<Slot*> slots;
    std::vector<Range> ranges;
    for (auto& s : g_slots) {
        if (s->owner != &mod || !s->staged)
            continue;
        uint8_t current[32] = {};
        uint8_t operand[32] = {};
        if (!Copy(current, s->target, s->length) || memcmp(current, s->saved, s->length) ||
            (s->writeLength && (!Copy(operand, s->writeAt, s->writeLength) ||
                                memcmp(operand, s->beforeWrite, s->writeLength)))) {
            char line[96];
            sprintf_s(line, "the code at %p changed before its new hook was written", s->target);
            why = line;
            return Patch::Failed;
        }
        slots.push_back(s.get());
        auto at = reinterpret_cast<uintptr_t>(s->target);
        ranges.push_back({at, at + s->length});
    }
    count = slots.size();
    if (slots.empty())
        return Patch::Written;

    std::vector<Thread> threads;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        why = "could not list the threads (error " + std::to_string(GetLastError()) + ")";
        return Patch::Busy;
    }
    const DWORD process = GetCurrentProcessId(), self = GetCurrentThreadId();
    THREADENTRY32 entry{sizeof(entry)};
    bool listed = true;
    BOOL more = Thread32First(snapshot, &entry);
    if (!more) listed = false;
    for (; more; more = Thread32Next(snapshot, &entry)) {
        if (entry.th32OwnerProcessID != process || entry.th32ThreadID == self) continue;
        HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_LIMITED_INFORMATION,
                              FALSE, entry.th32ThreadID);
        if (h) threads.push_back({h, false});
        else if (GetLastError() != ERROR_INVALID_PARAMETER) listed = false;
    }
    if (GetLastError() != ERROR_NO_MORE_FILES) listed = false;
    CloseHandle(snapshot);

    bool clear = listed, written = true;
    for (auto& t : threads)
        t.suspended = SuspendThread(t.handle) != static_cast<DWORD>(-1);
    for (auto& t : threads) {
        if (!t.suspended) {
            // Only a thread that has ended may refuse; anything else could be running.
            DWORD code = 0;
            if (!GetExitCodeThread(t.handle, &code) || code == STILL_ACTIVE)
                clear = false;
            continue;
        }
        CONTEXT context{};
        context.ContextFlags = CONTEXT_CONTROL; // also waits until the thread has stopped
        if (!GetThreadContext(t.handle, &context)) {
            clear = false;
            continue;
        }
        for (const Range& r : ranges)
            if (context.Rip >= r.lo && context.Rip < r.hi)
                clear = false;
    }
    bool unchanged = true;
    if (clear) {
        // A foreign loader may have patched the site between the first validation and suspension.
        // Recheck after all observed threads stop; only fixed stack buffers are used here.
        for (Slot* s : slots) {
            uint8_t current[32] = {}, operand[32] = {};
            if (!Copy(current, s->target, s->length) || memcmp(current, s->saved, s->length) ||
                (s->writeLength && (!Copy(operand, s->writeAt, s->writeLength) ||
                                    memcmp(operand, s->beforeWrite, s->writeLength))))
                unchanged = false;
        }
        if (unchanged)
            for (Slot* s : slots)
                if (s->writeLength && !jumppatch::Write(s->writeAt, s->write, s->writeLength))
                    written = false;
    }
    for (auto& t : threads) {
        if (t.suspended)
            ResumeThread(t.handle);
        CloseHandle(t.handle);
    }

    if (!clear) {
        why = "a thread was where a new hook goes";
        return Patch::Busy;
    }
    if (!unchanged) {
        why = "the code changed while stopping threads for its new hooks";
        return Patch::Failed;
    }
    for (Slot* s : slots)
        s->staged = false; // written (or, if a write failed, passing through: never dropped)
    if (!written) {
        why = "could not write a new hook";
        return Patch::Failed;
    }
    return Patch::Written;
}

// Points every thunk of g's mod at what g bound (or past it), making g the active generation.
void Swap(Generation* g)
{
    g->mod->active.store(g);
    for (auto& s : g_slots)
        if (s->owner == g->mod)
            InterlockedExchangePointer(reinterpret_cast<void* volatile*>(s->thunk + 16), s->pending);
}

// g did not become active: its staged hook sites go, and the old generation carries on.
void Decline(Generation* g, const std::string& why, bool reload, std::string& outcome)
{
    Mod& mod = *g->mod;
    DropStaged(mod);
    Generation* active = mod.active.load();
    if (active && active->api && active->api->resume)
        if (DWORD fault = SafeCall(active->api->resume))
            Log("%s: GreeneyeModHot resume faulted (0x%08lX); its generation remains resident", mod.info.name.c_str(), fault);
    outcome = why;
    if (reload)
        outcome = "error: generation " + std::to_string(g->number) + " " + why + "; generation " +
                  std::to_string(active ? active->number : 0) + " stays active";
}

// Ends the start of g: writes its new hook sites, if any, and swaps. While a thread stands where
// one goes, g waits in g_committing for the next safe point (outcome empty after the first try).
void Commit(Generation* g, std::string& outcome)
{
    size_t count = 0;
    std::string why;
    Patch patch = WriteStaged(*g->mod, count, why);
    // An initial start also stages its patches, but has no active generation or future safe point to fall back to.
    // Retry immediately while other threads leave the affected instructions; never leave a non-hot mod pending.
    if (patch == Patch::Busy && !g->mod->active.load()) {
        for (unsigned attempt = 1; attempt < kCommitTries && patch == Patch::Busy; ++attempt) {
            SwitchToThread();
            patch = WriteStaged(*g->mod, count, why);
        }
    }
    if (patch == Patch::Busy && g->mod->active.load() && ++g_commitTries < kCommitTries) {
        if (!g_committing) {
            outcome = "pending: generation " + std::to_string(g->number) + " started; its " + std::to_string(count) +
                      " new hooks wait for a safe moment (" + why + ")";
            g_committing = g;
        }
        return;
    }
    g_committing = nullptr;
    g_commitTries = 0;
    if (patch == Patch::Busy)
        why = "could not write its new hooks: " + why + " at " + std::to_string(kCommitTries) + " safe points";
    if (patch != Patch::Written) {
        Decline(g, why, true, outcome);
        return;
    }
    Swap(g);
    outcome = "active generation " + std::to_string(g->number) + " (" + std::to_string(GetTickCount64() - g->begin) + " ms";
    if (count)
        outcome += ", " + std::to_string(count) + " new hooks";
    outcome += ")";
}

// Starts generation g of its mod and, if it starts, makes it the active one (see Commit). For a
// reload the caller holds the gate exclusively.
void Activate(Generation* g, std::string& outcome)
{
    Mod& mod = *g->mod;
    auto start = reinterpret_cast<GreeneyeModStartFn>(GetProcAddress(g->module, "GreeneyeModStart"));
    if (!start) {
        outcome = "no GreeneyeModStart in " + Narrow(mod.info.dll);
        return;
    }
    for (auto& s : g_slots)
        if (s->owner == &mod)
            s->pending = s->base; // a hook the new generation does not take passes through
    g_staging = g;
    g_stagingFailed = false;
    DWORD fault = 0;
    g->begin = GetTickCount64();
    bool ok = SafeStart(start, &g->host, &fault);
    g_staging = nullptr;
    bool reload = mod.started;
    mod.started = true; // new hook sites are staged from now on
    if (fault || !ok || g_stagingFailed) {
        std::string why;
        if (fault)
            why = "crashed while starting (0x" + std::to_string(fault) + ")";
        else if (g_stagingFailed)
            why = "a hook could not be installed";
        else
            why = "did not start";
        Decline(g, why, reload, outcome);
        return;
    }
    auto hotFn = reinterpret_cast<GreeneyeModHotFn>(GetProcAddress(g->module, "GreeneyeModHot"));
    if (hotFn && !SafeHot(hotFn, &g->hotApi, &fault)) {
        Decline(g, "GreeneyeModHot faulted", reload, outcome);
        return;
    }
    g->api = hotFn && g->hotApi.size >= sizeof(GreeneyeHot) ? &g->hotApi : nullptr;
    Commit(g, outcome);
}

// A pending generation that cannot swap yet: after kWaitReport, hotload.status says why, once.
void Wait(Mod& mod, Generation* g, const char* reason)
{
    ULONGLONG now = GetTickCount64();
    if (!mod.waitSince || mod.waitReason != reason) {
        mod.waitSince = now;
        mod.waitReason = reason;
        mod.waitReported = false;
    }
    if (!mod.waitReported && now - mod.waitSince >= kWaitReport) {
        mod.waitReported = true;
        Status(mod, "pending: generation " + std::to_string(g->number) + " waits: " + reason);
    }
}

// A safe point: swaps any pending generation whose old one is ready. Never waits. Each mod is
// swapped under its own gate (see Leases), so one busy mod holds up only its own reload.
void Poll()
{
    if (g_starting || g_stopping || g_staging) return;
    if (!TryAcquireSRWLockExclusive(&g_pollLock))
        return;
    if (Generation* g = g_committing) {
        Mod& mod = *g->mod;
        if (!Holds(&mod) && TryAcquireSRWLockExclusive(&mod.gate)) {
            t_swapping = &mod;
            std::string outcome;
            Commit(g, outcome);
            t_swapping = nullptr;
            ReleaseSRWLockExclusive(&mod.gate);
            if (!outcome.empty())
                Status(mod, outcome);
        }
    }
    for (auto& m : g_mods) {
        if (g_committing)
            break; // one generation with new hook sites at a time
        Mod& mod = *m;
        EnterCriticalSection(&g_pendingLock);
        Generation* g = mod.pending;
        LeaveCriticalSection(&g_pendingLock);
        if (!g)
            continue;
        if (Holds(&mod) || !TryAcquireSRWLockExclusive(&mod.gate)) {
            Wait(mod, g, "a thread stays inside the mod's hooks");
            continue;
        }
        t_swapping = &mod;
        Generation* active = mod.active.load();
        const GreeneyeHot* api = active ? active->api : nullptr;
        DWORD hotFault = 0;
        if (!mod.preparing)
            mod.preparing = !api || !api->prepare || SafeBool(api->prepare, false, &hotFault);
        const bool ready = mod.preparing && (!api || !api->ready || SafeBool(api->ready, false, &hotFault));
        if (hotFault) {
            // A failed quiescence callback is never permission to replace the generation.
            EnterCriticalSection(&g_pendingLock);
            mod.pending = nullptr;
            LeaveCriticalSection(&g_pendingLock);
            mod.preparing = false;
            mod.waitSince = 0;
            std::string outcome;
            Decline(g, "GreeneyeModHot prepare/ready faulted", true, outcome);
            t_swapping = nullptr;
            ReleaseSRWLockExclusive(&mod.gate);
            Status(mod, outcome);
            continue;
        }
        if (!ready) {
            t_swapping = nullptr;
            ReleaseSRWLockExclusive(&mod.gate);
            Wait(mod, g, "the mod is not ready to be swapped (its prepare/ready said no)");
            continue; // e.g. Find's dialog is open: ask again at the next safe point
        }
        EnterCriticalSection(&g_pendingLock);
        mod.pending = nullptr;
        LeaveCriticalSection(&g_pendingLock);
        mod.preparing = false;
        mod.waitSince = 0;
        std::string outcome;
        Activate(g, outcome);
        t_swapping = nullptr;
        ReleaseSRWLockExclusive(&mod.gate);
        if (!outcome.empty())
            Status(mod, outcome);
    }
    ReleaseSRWLockExclusive(&g_pollLock);
}

// --- the loader's own safe point in the game: the top of each UI frame -------------------------

using UiLoopFn = void (*)(void* app);
UiLoopFn g_uiLoopNext = nullptr;

void UiLoopHook(void* app)
{
    Poll(); // no lease held here
    g_uiLoopNext(app);
}

// --- reload requests ---------------------------------------------------------------------------

DWORD WINAPI Watcher(void*)
{
    for (;;) {
        if (WaitForSingleObject(g_watcherStop, 250) != WAIT_TIMEOUT) return 0;
        for (auto& m : g_mods) {
            Mod& mod = *m;
            std::wstring request = mod.info.dir + L"reload";
            if (GetFileAttributesW(request.c_str()) == INVALID_FILE_ATTRIBUTES || !DeleteFileW(request.c_str()))
                continue;
            EnterCriticalSection(&g_pendingLock);
            bool busy = mod.pending != nullptr;
            LeaveCriticalSection(&g_pendingLock);
            if (busy) {
                Status(mod, "busy: a reload is already pending");
                continue;
            }
            std::string why;
            Generation* g = Load(mod, why); // outside the gate
            if (!g) {
                AcquireSRWLockShared(&g_pollLock);
                const Generation* activeGeneration = mod.active.load();
                const unsigned active = activeGeneration ? activeGeneration->number : 0;
                ReleaseSRWLockShared(&g_pollLock);
                Status(mod, "error: " + why + "; generation " + std::to_string(active) + " stays active");
                continue;
            }
            EnterCriticalSection(&g_pendingLock);
            mod.pending = g;
            LeaveCriticalSection(&g_pendingLock);
            Status(mod, "pending: generation " + std::to_string(g->number) + " waits for a safe point");
        }
        // The dedicated server has no UI frame; its mods keep no state tied to a thread.
        if (g_side == kGreeneyeServer)
            Poll();
    }
}

} // namespace

void Init(int side, const std::wstring& gameDir, bool hotReload)
{
    g_starting = true;
    g_stopping = false;
    g_side = side;
    g_gameDir = gameDir;
    g_hotReload = hotReload;
    InitializeCriticalSection(&g_pendingLock);
    InitializeCriticalSection(&g_keepLock);
    g_initialized = true;
}

void InstallSafePoint()
{
    if (!g_hotReload || g_side != kGreeneyeClient)
        return;
    // UiCommandLoop: push rbx/rbp/rsi/rdi/r12-r15 (13 bytes), as Quick Deposit and Auto Loot find it.
    uint8_t* loop = Find("40 53 55 56 57 41 54 41 55 41 56 41 57 48 81 EC 98 00 00 00 48 8D 81 ?? 4F 03 00", nullptr);
    if (!loop) {
        Log("hot reload: UI frame not found; reloads only at a mod's own safe point");
        return;
    }
    g_loaderMod = std::make_unique<Mod>();
    g_loaderMod->info.name = "Greeneye Loader";
    auto g = std::make_unique<Generation>();
    g->mod = g_loaderMod.get();
    g->number = 1;
    g_staging = g.get();
    g_uiLoopNext = reinterpret_cast<UiLoopFn>(Hook(loop, reinterpret_cast<void*>(&UiLoopHook), 13, nullptr, 0));
    g_staging = nullptr;
    g_loaderMod->started = true;
    Generation* raw = g.get();
    g_loaderMod->generations.push_back(std::move(g));
    if (!g_uiLoopNext) {
        Log("hot reload: could not hook the UI frame");
        return;
    }
    std::string outcome;
    Commit(raw, outcome);
    Log("hot reload: UI frame hook: %s", outcome.c_str());
}

std::string StartMod(const ModInfo& info, bool* started)
{
    if (started)
        *started = false;
    auto m = std::make_unique<Mod>();
    m->info = info;
    Mod& mod = *m;
    g_mods.push_back(std::move(m));
    std::string why;
    Generation* g = Load(mod, why);
    if (!g)
        return why;
    std::string outcome;
    Activate(g, outcome);
    bool ok = mod.active.load() == g;
    if (started)
        *started = ok;
    if (g_hotReload)
        Status(mod, ok ? outcome : "error: " + outcome);
    return ok ? "started (" + outcome + ")" : outcome;
}

void StartWatcher()
{
    g_starting = false;
    if (!g_hotReload)
        return;
    g_watcherStop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE t = g_watcherStop ? CreateThread(nullptr, 0, Watcher, nullptr, 0, nullptr) : nullptr;
    if (!t) {
        Log("hot reload: could not start the reload watcher (error %lu)", GetLastError());
        return;
    }
    CloseHandle(t);
    Log("hot reload: watching <mod folder>\\reload");
}

void PollFromKiln()
{
    if (g_hotReload)
        Poll();
}

void Stop()
{
    g_stopping = true;
    if (g_watcherStop) SetEvent(g_watcherStop);
}

std::vector<ModState> Describe()
{
    std::vector<ModState> out;
    if (!g_initialized) return out;
    AcquireSRWLockShared(&g_pollLock);
    EnterCriticalSection(&g_pendingLock);
    AcquireSRWLockShared(&g_slotsLock);
    for (auto& m : g_mods) {
        ModState st;
        st.id = m->info.id;
        const Generation* active = m->active.load();
        st.generation = active ? active->number : 0;
        st.generations = static_cast<unsigned>(m->generations.size());
        st.hotApi = active && active->api;
        for (auto& s : g_slots)
            st.hooks += s->owner == m.get();
        out.push_back(std::move(st));
    }
    ReleaseSRWLockShared(&g_slotsLock);
    LeaveCriticalSection(&g_pendingLock);
    ReleaseSRWLockShared(&g_pollLock);
    return out;
}

uint8_t* FindForTest(const char* pattern)
{
    return Find(pattern, nullptr);
}

void ReadForTest(const void* p, void* out, size_t n)
{
    Read(p, out, n);
}

} // namespace hot
