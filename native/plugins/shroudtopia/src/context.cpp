// SPDX-License-Identifier: MIT
// The ModContext of Shroudtopia's mod API (include/shroudtopia/shroudtopia.h), built on Kiln:
//  - config.*: typed getters over shroudtopia.json "mods.<modKey>.<key>", with nlohmann::json's conversions (the
//    library Shroudtopia itself uses), so a value reads the same as under Shroudtopia;
//  - memory.*: Shroudtopia's ten helpers. Read/Write/WriteToReadOnlyMemory/ApplyPatch go through kiln.hooks when it is
//    there (SEH-guarded; ApplyPatch is a Kiln patch owned by the mod, so it is undone when the plugin is revoked);
//    FindPattern scans committed readable memory from `start` for `length` bytes, first match, like Shroudtopia's;
//    CreateDetour and InjectShellcode do what their names say (see docs/kiln/engines/shroudtopia.md for where
//    Shroudtopia's own versions differ);
//  - Log: [shroudtopia:<mod>] in kiln.log, plus logs/shroudtopia.log.
#include <algorithm>
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
bool g_log_enabled = true;

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
fs::file_time_type g_mtime{};

const json kDefault = {{"active", true},        {"bootDelay", 3000},    {"updateDelay", 500},
                       {"enableLogging", true}, {"logLevel", "INFO"},   {"mods", json::object()}};

bool read_locked() {
    std::string text;
    if (!kcompat::read_file(g_file, &text)) return false;
    try {
        g_doc = json::parse(text);
    } catch (...) {
        return false;  // keep the last good values, as Shroudtopia keeps its old config on a parse error
    }
    std::error_code ec;
    g_mtime = fs::last_write_time(g_file, ec);
    return true;
}

void write_locked() {
    kcompat::write_file_atomic(g_file, g_doc.dump(4));
    std::error_code ec;
    g_mtime = fs::last_write_time(g_file, ec);
}
}  // namespace

void init(const fs::path& kiln_file, const fs::path& game_file) {
    std::lock_guard lock(g_mu);
    g_file = kiln_file;
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
    std::error_code ec;
    const auto t = fs::last_write_time(g_file, ec);
    if (ec || t == g_mtime) return false;
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

bool seh_copy(void* dst, const void* src, size_t n) {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// A protected write: through kiln.hooks when present, else VirtualProtect + guarded copy.
bool write_any(uintptr_t address, const void* data, size_t size) {
    if (!address || !data) return false;
    if (g_hooks) return g_hooks->write(address, data, size) == KILN_OK;
    DWORD old = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), size, PAGE_EXECUTE_READWRITE, &old)) return false;
    const bool ok = seh_copy(reinterpret_cast<void*>(address), data, size);
    VirtualProtect(reinterpret_cast<void*>(address), size, old, &old);
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), size);
    return ok;
}

bool read_any(uintptr_t address, void* out, size_t size) {
    if (!address || !out) return false;
    if (g_hooks) return g_hooks->read(address, out, size) == KILN_OK;
    return seh_copy(out, reinterpret_cast<const void*>(address), size);
}

bool match_at(const uint8_t* p, const char* pattern, const char* mask, size_t n) {
    for (size_t j = 0; j < n; ++j)
        if (mask[j] != '?' && static_cast<uint8_t>(pattern[j]) != p[j]) return false;
    return true;
}

uintptr_t scan_region(const uint8_t* lo, const uint8_t* hi, const char* pattern, const char* mask, size_t n) {
    __try {
        for (const uint8_t* p = lo; p + n <= hi; ++p)
            if (match_at(p, pattern, mask, n)) return reinterpret_cast<uintptr_t>(p);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
    return 0;
}

uintptr_t find_pattern(const char* pattern, const char* mask, uintptr_t start, size_t length) {
    if (!pattern || !mask || !*mask) return 0;
    const size_t n = std::strlen(mask);
    const DWORD readable = PAGE_EXECUTE_READ | PAGE_EXECUTE | PAGE_READONLY | PAGE_EXECUTE_READWRITE | PAGE_READWRITE;
    const uintptr_t end = start + length;
    MEMORY_BASIC_INFORMATION mbi;
    for (uintptr_t at = start; at < end;) {
        if (VirtualQuery(reinterpret_cast<const void*>(at), &mbi, sizeof mbi) != sizeof mbi) break;
        const uintptr_t base = reinterpret_cast<uintptr_t>(mbi.BaseAddress), top = base + mbi.RegionSize;
        if (mbi.State == MEM_COMMIT && (mbi.Protect & readable) && !(mbi.Protect & PAGE_GUARD)) {
            // Shroudtopia scans each region whole (from its base); matches may run past `start + length` within it.
            if (uintptr_t hit = scan_region(reinterpret_cast<const uint8_t*>(base), reinterpret_cast<const uint8_t*>(top), pattern,
                                            mask, n))
                return hit;
        }
        at = top;
    }
    return 0;
}

uint8_t* alloc_near(uintptr_t near_addr, size_t size) {
    if (!near_addr) return static_cast<uint8_t*>(VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    const uintptr_t gran = si.dwAllocationGranularity, reach = 0x7FF00000;
    uintptr_t lo = near_addr > reach ? near_addr - reach : 0, hi = near_addr + reach;
    lo = std::max(lo, reinterpret_cast<uintptr_t>(si.lpMinimumApplicationAddress));
    hi = std::min(hi, reinterpret_cast<uintptr_t>(si.lpMaximumApplicationAddress));
    MEMORY_BASIC_INFORMATION mbi;
    // Closest free block first: search downwards, then upwards, from the address (a rel32 reaches either way).
    for (uintptr_t p = near_addr; p > lo && VirtualQuery(reinterpret_cast<void*>(p), &mbi, sizeof mbi);) {
        const uintptr_t base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        if (mbi.State == MEM_FREE) {
            uintptr_t top = base + mbi.RegionSize;
            if (top > near_addr) top = near_addr;
            const uintptr_t at = top >= size ? ((top - size) & ~(gran - 1)) : 0;
            if (at >= base && at >= lo)
                if (void* m = VirtualAlloc(reinterpret_cast<void*>(at), size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE))
                    return static_cast<uint8_t*>(m);
        }
        if (base == 0) break;
        p = base - 1;
    }
    for (uintptr_t p = near_addr; p < hi && VirtualQuery(reinterpret_cast<void*>(p), &mbi, sizeof mbi);
         p = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize) {
        if (mbi.State != MEM_FREE) continue;
        const uintptr_t at = (reinterpret_cast<uintptr_t>(mbi.BaseAddress) + gran - 1) & ~(gran - 1);
        if (at + size > reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize || at + size > hi) continue;
        if (void* m = VirtualAlloc(reinterpret_cast<void*>(at), size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE))
            return static_cast<uint8_t*>(m);
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
    const intptr_t d = reinterpret_cast<intptr_t>(destination) - (reinterpret_cast<intptr_t>(target) + 5);
    if (d < INT32_MIN || d > INT32_MAX) {
        ctx_log("Failed to write jump: the destination is out of rel32 reach");
        return;
    }
    uint8_t jmp[5] = {0xE9};
    const int32_t rel = static_cast<int32_t>(d);
    std::memcpy(jmp + 1, &rel, 4);
    if (!write_any(reinterpret_cast<uintptr_t>(target), jmp, sizeof jmp)) ctx_log("Failed to write jump");
}

LPVOID ctx_alloc_near(LPVOID address, SIZE_T size) {
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
    if (!address || !opcode) return false;
    if (g_hooks) {
        KilnHandle h = 0;
        return g_hooks->patch(H.owner, address, opcode, size, &h) == KILN_OK;
    }
    return write_any(address, opcode, size);
}

bool ctx_inject_shellcode(uint8_t* opcode, size_t size, uintptr_t near_addr, uintptr_t* injected_at) {
    uint8_t* mem = alloc_near(near_addr, std::max<size_t>(size, 0x1000));
    if (injected_at) *injected_at = reinterpret_cast<uintptr_t>(mem);
    if (!mem || !opcode || !seh_copy(mem, opcode, size)) {
        ctx_log("Failed to inject shellcode");
        return false;
    }
    FlushInstructionCache(GetCurrentProcess(), mem, size);
    ctx_log(("Successfully injected shellcode at address: " + std::to_string(reinterpret_cast<uintptr_t>(mem))).c_str());
    return true;
}

bool ctx_create_detour(LPVOID target, LPVOID detour, void** trampoline) {
    if (!target || !detour) return false;
    auto hook = safetyhook::InlineHook::create(target, detour);
    if (!hook) {
        ctx_log("Failed to install detour");
        return false;
    }
    auto h = std::make_unique<safetyhook::InlineHook>(std::move(*hook));
    if (trampoline) *trampoline = h->original<void*>();
    std::lock_guard lock(g_detour_mu);
    g_detours.push_back(std::move(h));
    ctx_log("Successfully installed detour");
    return true;
}

}  // namespace

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
