// SPDX-License-Identifier: MIT
// The ModContext of Shroudtopia's mod API (include/shroudtopia/shroudtopia.h), built on Kiln:
//  - config.*: typed getters over shroudtopia.json "mods.<modKey>.<key>", with nlohmann::json's conversions (the
//    library Shroudtopia itself uses), so a value reads the same as under Shroudtopia;
//  - memory.*: Shroudtopia's ten helpers. Guarded, bounded copies restore protection for every memory region;
//    ApplyPatch saves its originals locally and restores them when the worker has stopped on plugin unload;
//    FindPattern scans committed readable memory from `start` for `length` bytes, first match, like Shroudtopia's;
//    CreateDetour and InjectShellcode do what their names say (see docs/kiln/engines/shroudtopia.md for where
//    Shroudtopia's own versions differ);
//  - Log: [shroudtopia:<mod>] in kiln.log, plus logs/shroudtopia.log.
#include <algorithm>
#include <atomic>
#include <limits>
#include <mutex>

#include "safetyhook.hpp"
#include "shroudtopia_internal.h"

#include <psapi.h>

namespace kshroud {

namespace fs = std::filesystem;
using json = nlohmann::json;

// ---------------------------------------------------------------------------------------------- logging --------
namespace {
std::mutex g_log_mu;
FILE* g_log_file = nullptr;
std::mutex g_cur_mu;
std::string g_last_mod;
thread_local std::string t_mod;
std::atomic<bool> g_log_enabled{true};

void file_line(const std::string& text) {
    std::lock_guard lock(g_log_mu);
    if (!g_log_file || !g_log_enabled) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    std::fprintf(g_log_file, "[%02d:%02d:%02d.%03d] %s\n", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, text.c_str());
    std::fflush(g_log_file);
}
}  // namespace

void set_current_mod(const std::string& name) {
    t_mod = name;
    if (!name.empty()) {
        std::lock_guard lock(g_cur_mu);
        g_last_mod = name;
    }
}

std::string current_mod() {
    if (!t_mod.empty()) return t_mod;
    std::lock_guard lock(g_cur_mu);
    return g_last_mod;
}

void log_line(const std::string& mod, const std::string& text) {
    H.mod_log(KILN_LOG_INFO, mod.empty() ? "?" : mod, text);
    file_line("[shroudtopia][INFO] (" + mod + ") " + text);
}

void loader_log(int32_t level, const std::string& text) {
    H.log(level, text);
    file_line(std::string("[shroudtopia][") + (level <= KILN_LOG_ERROR ? "ERRR" : level == KILN_LOG_WARN ? "WARN" : "INFO") +
              "] " + text);
}

// ---------------------------------------------------------------------------------------------- config ---------
namespace config {
namespace {
std::mutex g_mu;
json g_doc = json::object();
fs::path g_file;
std::string g_text;

const json kDefault = {{"active", true},        {"bootDelay", 3000},    {"updateDelay", 500},
                       {"enableLogging", true}, {"logLevel", "INFO"},   {"mods", json::object()}};

bool read_locked() {
    std::string text;
    if (!kcompat::read_file(g_file, &text)) return false;
    try {
        json doc = json::parse(text);
        if (!doc.is_object()) return false;
        if (!doc.contains("mods") || !doc["mods"].is_object()) doc["mods"] = json::object();
        g_doc = std::move(doc);
        g_text = std::move(text);
    } catch (...) {
        return false;  // keep the last good values, as Shroudtopia keeps its old config on a parse error
    }
    return true;
}

void write_locked() {
    const auto text = g_doc.dump(4);
    if (kcompat::write_file_atomic(g_file, text)) g_text = text;
}
}  // namespace

void init(const fs::path& kiln_file, const fs::path& game_file) {
    std::lock_guard lock(g_mu);
    g_file = kiln_file;
    g_doc = kDefault;
    std::error_code ec;
    if (!kcompat::exists(g_file) && kcompat::exists(game_file)) {
        fs::create_directories(g_file.parent_path(), ec);
        if (fs::copy_file(game_file, g_file, ec))
            H.logf(KILN_LOG_INFO, "settings: took %s as %s (edit the Kiln copy from now on)", kcompat::path_utf8(game_file).c_str(),
                   kcompat::path_utf8(g_file).c_str());
    }
    if (!read_locked()) {
        if (kcompat::exists(g_file))
            H.logf(KILN_LOG_WARN, "settings: %s is not valid JSON; using the defaults until it is fixed", kcompat::path_utf8(g_file).c_str());
        else {
            g_doc = kDefault;
            write_locked();
            H.logf(KILN_LOG_INFO, "settings: wrote the default %s", kcompat::path_utf8(g_file).c_str());
        }
        if (!g_doc.is_object()) g_doc = kDefault;
    }
    if (!g_doc.contains("mods") || !g_doc["mods"].is_object()) g_doc["mods"] = json::object();
    g_log_enabled = !g_doc.contains("enableLogging") || !g_doc["enableLogging"].is_boolean() || g_doc["enableLogging"].get<bool>();
}

bool reload_if_changed() {
    std::lock_guard lock(g_mu);
    // Editors can replace a file while retaining its timestamp (or change it within the filesystem's clock
    // granularity). Compare the text as well, and keep the last good object on every invalid replacement.
    std::string text;
    if (!kcompat::read_file(g_file, &text) || text == g_text) return false;
    const bool ok = read_locked();
    if (ok && (!g_doc.contains("mods") || !g_doc["mods"].is_object())) g_doc["mods"] = json::object();
    if (ok) g_log_enabled = !g_doc.contains("enableLogging") || !g_doc["enableLogging"].is_boolean() || g_doc["enableLogging"].get<bool>();
    return ok;
}

json get(const char* key) {
    std::lock_guard lock(g_mu);
    if (!key || !g_doc.is_object() || !g_doc.contains(key)) return nullptr;
    return g_doc[key];
}

json mod_get(const char* mod, const char* key) {
    std::lock_guard lock(g_mu);
    if (!mod || !key || !g_doc.contains("mods")) return nullptr;
    const json& mods = g_doc["mods"];
    if (!mods.is_object() || !mods.contains(mod) || !mods[mod].is_object() || !mods[mod].contains(key)) return nullptr;
    return mods[mod][key];
}

void apply_manifest_defaults(const std::string& id, const json& manifest) {
    if (!manifest.is_object() || !manifest.contains("shroudtopia") || !manifest["shroudtopia"].is_object() ||
        !manifest["shroudtopia"].contains("default"))
        return;
    std::lock_guard lock(g_mu);
    if (g_doc["mods"].contains(id)) return;
    g_doc["mods"][id] = manifest["shroudtopia"]["default"];
    if (g_doc["mods"][id].is_object() && !g_doc["mods"][id].contains("active")) g_doc["mods"][id]["active"] = true;
    write_locked();
    H.logf(KILN_LOG_INFO, "settings: first start of %s: wrote its defaults to %s", id.c_str(), kcompat::path_utf8(g_file).c_str());
}

fs::path file() {
    std::lock_guard lock(g_mu);
    return g_file;
}
}  // namespace config

// ---------------------------------------------------------------------------------------------- memory ---------
namespace {

template <class T>
T typed(const char* mod, const char* key, T fallback) {
    const json v = config::mod_get(mod, key);
    if (v.is_null()) return fallback;
    try {
        return v.get<T>();
    } catch (...) {
        return fallback;
    }
}

// The guarded accesses below happen in out-of-line calls on purpose: clang-cl (without /EHa) only maps a hardware
// fault to a __try's handler when it is raised inside a call made from the guarded block, never in inlined loads.
__declspec(noinline) void raw_move(void* dst, const void* src, size_t n) { std::memmove(dst, src, n); }

bool seh_copy(void* dst, const void* src, size_t n) {
    __try {
        raw_move(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Validate every region before copying or changing protection. SEH still covers mappings changing after the
// query. In particular, never wrap an address range or turn a PAGE_GUARD/PAGE_NOACCESS region into readable data.
bool valid_range(uintptr_t address, size_t size) {
    return address && size && size <= std::numeric_limits<uintptr_t>::max() - address;
}

bool accessible_range(uintptr_t address, size_t size, bool writing) {
    if (!valid_range(address, size)) return false;
    const uintptr_t end = address + size;
    for (uintptr_t at = address; at < end;) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<void*>(at), &mbi, sizeof mbi) || mbi.State != MEM_COMMIT ||
            (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) ||
            !(mbi.Protect & (writing ? (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)
                                    : (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ |
                                       PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))) return false;
        const auto base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        if (mbi.RegionSize > std::numeric_limits<uintptr_t>::max() - base) return false;
        const auto top = base + mbi.RegionSize;
        if (top <= at) return false;
        at = std::min(top, end);
    }
    return true;
}

bool readable_range(uintptr_t address, size_t size) { return accessible_range(address, size, false); }
bool writable_range(uintptr_t address, size_t size) { return accessible_range(address, size, true); }

struct Region {
    void* address;
    size_t size;
    DWORD protection;
};
std::recursive_mutex g_memory_mu;
struct Patch { uintptr_t address; std::vector<uint8_t> original; };
std::vector<Patch> g_patches;
std::atomic<bool> g_memory_stopped{false};

bool write_any(uintptr_t address, const void* data, size_t size) {
    if (g_memory_stopped || !valid_range(address, size) ||
        !readable_range(reinterpret_cast<uintptr_t>(data), size)) return false;
    std::lock_guard lock(g_memory_mu);
    if (g_memory_stopped) return false;
    std::vector<Region> regions;
    const auto end = address + size;
    for (auto at = address; at < end;) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<void*>(at), &mbi, sizeof mbi) || mbi.State != MEM_COMMIT ||
            (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
        const auto base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        if (mbi.RegionSize > std::numeric_limits<uintptr_t>::max() - base) return false;
        const auto top = std::min(base + mbi.RegionSize, end);
        if (top <= at) return false;
        regions.push_back({reinterpret_cast<void*>(at), top - at, 0});
        at = top;
    }
    size_t changed = 0;
    for (; changed < regions.size(); ++changed) {
        auto& r = regions[changed];
        if (!VirtualProtect(r.address, r.size, PAGE_EXECUTE_READWRITE, &r.protection)) break;
    }
    bool ok = changed == regions.size() && seh_copy(reinterpret_cast<void*>(address), data, size);
    // VirtualProtect returns only the first page's old protection. Restore every queried region separately,
    // including regions changed before a later VirtualProtect/copy failure.
    while (changed) {
        auto& r = regions[--changed];
        DWORD ignored = 0;
        if (!VirtualProtect(r.address, r.size, r.protection, &ignored)) ok = false;
    }
    if (!FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), size)) ok = false;
    return ok;
}

bool read_any(uintptr_t address, void* out, size_t size) {
    if (!readable_range(address, size) || !writable_range(reinterpret_cast<uintptr_t>(out), size)) return false;
    return seh_copy(out, reinterpret_cast<const void*>(address), size);
}

bool match_at(const uint8_t* p, const char* pattern, const char* mask, size_t n) {
    for (size_t j = 0; j < n; ++j)
        if (mask[j] != '?' && static_cast<uint8_t>(pattern[j]) != p[j]) return false;
    return true;
}

__declspec(noinline) uintptr_t scan_unguarded(uintptr_t lo, uintptr_t hi, const char* pattern, const char* mask, size_t n) {
    if (hi < lo || n > hi - lo) return 0;
    const auto last = hi - n;
    for (auto p = lo; p <= last; ++p)
        if (match_at(reinterpret_cast<const uint8_t*>(p), pattern, mask, n)) return p;
    return 0;
}

uintptr_t scan_region(uintptr_t lo, uintptr_t hi, const char* pattern, const char* mask, size_t n) {
    __try {
        return scan_unguarded(lo, hi, pattern, mask, n);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
    return 0;
}

__declspec(noinline) size_t raw_length(const char* s) { return std::strlen(s); }

size_t mask_length(const char* mask) {
    __try { return mask ? raw_length(mask) : 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

uintptr_t find_pattern(const char* pattern, const char* mask, uintptr_t start, size_t length) {
    const size_t n = mask_length(mask);
    if (!n || n > length || !valid_range(start, length) ||
        !readable_range(reinterpret_cast<uintptr_t>(pattern), n)) return 0;
    const uintptr_t end = start + length;
    MEMORY_BASIC_INFORMATION mbi{};
    for (uintptr_t at = start; at < end;) {
        if (!VirtualQuery(reinterpret_cast<const void*>(at), &mbi, sizeof mbi)) break;
        const auto base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        if (mbi.RegionSize > std::numeric_limits<uintptr_t>::max() - base) break;
        const uintptr_t top = std::min(base + mbi.RegionSize, end);
        if (top <= at) break;
        if (mbi.State == MEM_COMMIT && !(mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) &&
            (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ |
                            PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) {
            // Start at the caller's start, and bound the final candidate by its length. A match may straddle
            // adjacent readable VirtualQuery regions, but must never cross a guard/unmapped page.
            const auto extended = n - 1 <= end - top ? top + n - 1 : end;
            if (uintptr_t hit = scan_region(at, extended, pattern, mask, n)) {
                if (readable_range(hit, n)) return hit;
            }
        }
        at = top;
    }
    return 0;
}

uint8_t* alloc_near(uintptr_t near_addr, size_t size) {
    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    const uintptr_t gran = si.dwAllocationGranularity;
    const auto min_addr = reinterpret_cast<uintptr_t>(si.lpMinimumApplicationAddress);
    const auto max_addr = reinterpret_cast<uintptr_t>(si.lpMaximumApplicationAddress);
    constexpr uintptr_t reach = 0x7FF00000;
    if (!size || size > max_addr - min_addr || (near_addr && (near_addr < min_addr || near_addr > max_addr))) return nullptr;
    if (!near_addr) return static_cast<uint8_t*>(VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    const uintptr_t lo = near_addr - min_addr > reach ? near_addr - reach : min_addr;
    const uintptr_t hi = max_addr - near_addr > reach ? near_addr + reach : max_addr;
    if (size > hi - lo) return nullptr;
    MEMORY_BASIC_INFORMATION mbi{};
    for (uintptr_t p = near_addr; p >= lo && VirtualQuery(reinterpret_cast<void*>(p), &mbi, sizeof mbi);) {
        const auto base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        if (mbi.State == MEM_FREE && mbi.RegionSize <= max_addr - base) {
            const auto top = std::min(base + mbi.RegionSize, near_addr);
            const auto at = top >= size ? ((top - size) & ~(gran - 1)) : 0;
            if (at >= base && at >= lo)
                if (auto* m = VirtualAlloc(reinterpret_cast<void*>(at), size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE))
                    return static_cast<uint8_t*>(m);
        }
        if (base <= lo) break;
        p = base - 1;
    }
    for (uintptr_t p = near_addr; p < hi;) {
        if (!VirtualQuery(reinterpret_cast<void*>(p), &mbi, sizeof mbi)) break;
        const auto base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        if (mbi.RegionSize > max_addr - base) break;
        const auto top = base + mbi.RegionSize;
        if (top <= p) break;
        if (mbi.State == MEM_FREE && base <= max_addr - (gran - 1)) {
            const auto at = (std::max(base, near_addr) + gran - 1) & ~(gran - 1);
            if (at <= hi && size <= hi - at && at <= top && size <= top - at)
                if (auto* m = VirtualAlloc(reinterpret_cast<void*>(at), size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE))
                    return static_cast<uint8_t*>(m);
        }
        p = top;
    }
    return nullptr;
}

std::mutex g_detour_mu;
std::vector<std::unique_ptr<safetyhook::InlineHook>> g_detours;  // kept for the process life

// ---- the ModContext functions, as Shroudtopia's loader passes them ----
void ctx_log(const char* msg) { log_line(current_mod(), msg ? msg : ""); }

bool ctx_write_ro(LPVOID target, LPVOID data, SIZE_T size) {
    const bool ok = write_any(reinterpret_cast<uintptr_t>(target), data, size);
    if (!ok) ctx_log("Failed to write to read-only memory");
    return ok;
}

uintptr_t ctx_find_pattern(const char* pattern, const char* mask, uintptr_t start, size_t length) {
    return find_pattern(pattern, mask, start, length);
}

bool ctx_write(uintptr_t address, const void* buffer, size_t size) { return write_any(address, buffer, size); }
bool ctx_read(uintptr_t address, void* buffer, size_t size) { return read_any(address, buffer, size); }

void ctx_write_jump(LPVOID target, LPVOID destination) {
    const auto from = reinterpret_cast<uintptr_t>(target), to = reinterpret_cast<uintptr_t>(destination);
    if (!valid_range(from, 5) || !to) return;
    const auto next = from + 5;
    if ((to >= next && to - next > INT32_MAX) || (to < next && next - to > 0x80000000ull)) {
        ctx_log("Failed to write jump: the destination is out of rel32 reach");
        return;
    }
    uint8_t jmp[5] = {0xE9};
    const int32_t rel = static_cast<int32_t>(static_cast<uint32_t>(to - next));
    std::memcpy(jmp + 1, &rel, 4);
    if (!write_any(reinterpret_cast<uintptr_t>(target), jmp, sizeof jmp)) ctx_log("Failed to write jump");
}

LPVOID ctx_alloc_near(LPVOID address, SIZE_T size) {
    std::lock_guard lock(g_memory_mu);
    if (g_memory_stopped) return nullptr;
    LPVOID p = alloc_near(reinterpret_cast<uintptr_t>(address), size);
    if (!p) ctx_log("Memory allocation error");
    return p;
}

uintptr_t ctx_module_base(const std::string& name) {
    HMODULE mods[1024];
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &needed)) return 0;
    const std::string want = kcompat::lower(name);
    const DWORD n = std::min<DWORD>(needed / sizeof(HMODULE), 1024);
    for (DWORD i = 0; i < n; ++i) {
        char path[MAX_PATH] = "";
        if (!K32GetModuleFileNameExA(GetCurrentProcess(), mods[i], path, MAX_PATH)) continue;
        if (kcompat::lower(path).find(want) != std::string::npos) return reinterpret_cast<uintptr_t>(mods[i]);
    }
    return 0;
}

bool ctx_apply_patch(uintptr_t address, uint8_t* opcode, size_t size) {
    if (g_memory_stopped || !readable_range(address, size) ||
        !readable_range(reinterpret_cast<uintptr_t>(opcode), size)) return false;
    std::lock_guard lock(g_memory_mu);
    if (g_memory_stopped) return false;
    Patch patch{address, std::vector<uint8_t>(size)};
    if (!read_any(address, patch.original.data(), size)) return false;
    // Reserve the record before writing: an allocation failure must not leave an untracked patch.
    g_patches.push_back(std::move(patch));
    if (write_any(address, opcode, size)) return true;
    if (write_any(address, g_patches.back().original.data(), size)) g_patches.pop_back();
    // If a concurrent mapping change defeated the rollback, retain its snapshot for shutdown to retry.
    return false;
}

bool ctx_inject_shellcode(uint8_t* opcode, size_t size, uintptr_t near_addr, uintptr_t* injected_at) {
    std::lock_guard lock(g_memory_mu);
    const uintptr_t zero = 0;
    if (g_memory_stopped || (injected_at && (!writable_range(reinterpret_cast<uintptr_t>(injected_at), sizeof zero) ||
        !seh_copy(injected_at, &zero, sizeof zero))) ||
        !readable_range(reinterpret_cast<uintptr_t>(opcode), size)) return false;
    uint8_t* mem = alloc_near(near_addr, std::max<size_t>(size, 0x1000));
    if (!mem || !seh_copy(mem, opcode, size)) {
        if (mem) VirtualFree(mem, 0, MEM_RELEASE);
        ctx_log("Failed to inject shellcode");
        return false;
    }
    const auto address = reinterpret_cast<uintptr_t>(mem);
    if (!FlushInstructionCache(GetCurrentProcess(), mem, size) || (injected_at && !seh_copy(injected_at, &address, sizeof address))) {
        VirtualFree(mem, 0, MEM_RELEASE);
        return false;
    }
    ctx_log(("Successfully injected shellcode at address: " + std::to_string(reinterpret_cast<uintptr_t>(mem))).c_str());
    return true;
}

bool ctx_create_detour(LPVOID target, LPVOID detour, void** trampoline) {
    std::lock_guard memory_lock(g_memory_mu);
    void* previous = nullptr;
    if (g_memory_stopped || !target || !detour || (trampoline &&
        (!writable_range(reinterpret_cast<uintptr_t>(trampoline), sizeof previous) ||
         !read_any(reinterpret_cast<uintptr_t>(trampoline), &previous, sizeof previous) ||
         !seh_copy(trampoline, &previous, sizeof previous))) ||
        !readable_range(reinterpret_cast<uintptr_t>(target), 16) ||
        !readable_range(reinterpret_cast<uintptr_t>(detour), 1)) return false;
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(detour, &mbi, sizeof mbi) ||
        !(mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return false;
    std::lock_guard lock(g_detour_mu);
    if (g_memory_stopped) return false;
    auto hook = safetyhook::InlineHook::create(target, detour, safetyhook::InlineHook::StartDisabled);
    if (!hook) {
        ctx_log("Failed to install detour");
        return false;
    }
    auto h = std::make_unique<safetyhook::InlineHook>(std::move(*hook));
    void* original = h->original<void*>();
    // Allocate the owning record before publishing anything to the mod. An allocation failure must not leave
    // its output pointing at the trampoline of a destroyed, disabled hook.
    g_detours.push_back(std::move(h));
    if (trampoline && !seh_copy(trampoline, &original, sizeof original)) {
        g_detours.pop_back();
        return false;
    }
    // Publish the callable original before another thread can enter the detour. The caller must pass the
    // trampoline storage its detour actually uses, as with other concurrent native hook APIs.
    std::atomic_thread_fence(std::memory_order_release);
    if (!g_detours.back()->enable()) {
        g_detours.pop_back();
        if (trampoline) seh_copy(trampoline, &previous, sizeof previous);
        return false;
    }
    ctx_log("Successfully installed detour");
    return true;
}

}  // namespace

void stop_memory() {
    std::lock_guard lock(g_memory_mu);
    // Undo in reverse order so overlapping patches recover the bytes that preceded the first patch.
    for (auto it = g_patches.rbegin(); it != g_patches.rend(); ++it)
        if (!write_any(it->address, it->original.data(), it->original.size()))
            loader_log(KILN_LOG_WARN, "Failed to restore a mod patch during shutdown");
    g_patches.clear();
    g_memory_stopped = true;
    // Detours, trampolines and successful shellcode allocations remain mapped: a mod thread may still use them.
}

ModContext* make_context(bool is_server, const std::string& game_version, const fs::path& config_file, const fs::path& log_file,
                         const fs::path& mod_folder) {
    {
        std::lock_guard lock(g_log_mu);
        if (!g_log_file) g_log_file = _wfsopen(log_file.c_str(), L"w", _SH_DENYWR);
    }
    auto* ctx = new ModContext();
    ctx->shroudtopia.version = kShroudtopiaVersion;
    ctx->shroudtopia.config_file = kcompat::path_utf8(config_file);
    ctx->shroudtopia.log_file = kcompat::path_utf8(log_file);
    ctx->shroudtopia.mod_folder = kcompat::path_utf8(mod_folder);
    ctx->game.version = game_version;
    ctx->game.isClient = !is_server;
    ctx->game.isServer = is_server;
    auto& c = ctx->config;
    c.GetBool = [](const char* m, const char* k, bool d) { return typed<bool>(m, k, d); };
    c.GetBoolArray = [](const char* m, const char* k, std::vector<bool> d) { return typed<std::vector<bool>>(m, k, std::move(d)); };
    c.GetString = [](const char* m, const char* k, std::string d) { return typed<std::string>(m, k, std::move(d)); };
    c.GetStringArray = [](const char* m, const char* k, std::vector<std::string> d) {
        return typed<std::vector<std::string>>(m, k, std::move(d));
    };
    c.GetInt = [](const char* m, const char* k, int d) { return typed<int>(m, k, d); };
    c.GetIntArray = [](const char* m, const char* k, std::vector<int> d) { return typed<std::vector<int>>(m, k, std::move(d)); };
    c.GetFloat = [](const char* m, const char* k, float d) { return typed<float>(m, k, d); };
    c.GetFloatArray = [](const char* m, const char* k, std::vector<float> d) { return typed<std::vector<float>>(m, k, std::move(d)); };
    c.GetUint64 = [](const char* m, const char* k, uint64_t d) { return typed<uint64_t>(m, k, d); };
    c.GetUint64Array = [](const char* m, const char* k, std::vector<uint64_t> d) {
        return typed<std::vector<uint64_t>>(m, k, std::move(d));
    };
    auto& mem = ctx->memory;
    mem.WriteToReadOnlyMemory = &ctx_write_ro;
    mem.FindPattern = &ctx_find_pattern;
    mem.Write = &ctx_write;
    mem.Read = &ctx_read;
    mem.WriteJump = &ctx_write_jump;
    mem.AllocateMemoryNearAddress = &ctx_alloc_near;
    mem.GetModuleBaseAddress = &ctx_module_base;
    mem.ApplyPatch = &ctx_apply_patch;
    mem.InjectShellcode = &ctx_inject_shellcode;
    mem.CreateDetour = &ctx_create_detour;
    ctx->Log = &ctx_log;
    return ctx;
}

}  // namespace kshroud
