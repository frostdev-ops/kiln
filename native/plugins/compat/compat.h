// SPDX-License-Identifier: MIT
// Shared helpers for Kiln's compatibility plugins for other mod loaders (kiln-greeneye, kiln-flame,
// kiln-shroudtopia): logging through Kiln, paths, files, and the mod list of one engine from the active profile plus
// that engine's own folder. Header-only; each plugin includes it once per translation unit that needs it.
//
// Profile rule, the same for every engine (docs/kiln/engines/README.md):
//  - the profile's "mods.<engine>" entries load first, in the profile's order (kiln.profile keeps the file's text;
//    the core ignores mod kinds it does not know, so these lists never make a profile invalid);
//  - the engine's own folder (greeneye/, mods/ ...) is also read when the profile has no "mods.<engine>" list (the
//    launcher does not manage that engine, so what the player installed by hand keeps working), or when the profile
//    has "dropins": true, or with no profile at all (legacy layout);
//  - play-once no-mods and an invalid profile load nothing;
//  - an id listed in the profile wins over a folder with the same id.
#pragma once

#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_api.h"
#include "nlohmann/json.hpp"
#include "services/kiln_profile.h"

namespace kcompat {

inline KilnStr kstr(const char* s) { return KilnStr{s, s ? std::strlen(s) : 0}; }
inline KilnStr kstr(std::string_view s) { return KilnStr{s.data(), s.size()}; }
inline std::string_view sv(KilnStr s) { return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view(); }

inline std::string to_utf8(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

inline std::wstring to_wide(std::string_view s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

inline std::string path_utf8(const std::filesystem::path& p) { return to_utf8(p.wstring()); }

inline std::string lower(std::string_view s) {
    std::string o(s);
    for (char& c : o)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return o;
}

inline std::string vformat(const char* fmt, va_list ap) {
    char small[1024];
    va_list copy;
    va_copy(copy, ap);
    const int n = std::vsnprintf(small, sizeof small, fmt, copy);
    va_end(copy);
    if (n < 0) return fmt;
    if (static_cast<size_t>(n) < sizeof small) return std::string(small, static_cast<size_t>(n));
    std::string big(static_cast<size_t>(n) + 1, '\0');
    std::vsnprintf(big.data(), big.size(), fmt, ap);
    big.resize(static_cast<size_t>(n));
    return big;
}

inline bool read_file(const std::filesystem::path& p, std::string* out) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return false;
    out->assign(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
    return true;
}

inline bool write_file_atomic(const std::filesystem::path& p, std::string_view data) {
    std::error_code ec;
    std::filesystem::create_directories(p.parent_path(), ec);
    auto tmp = p;
    tmp += L".tmp";
    {
        std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
        if (!f) return false;
        f.write(data.data(), static_cast<std::streamsize>(data.size()));
        if (!f) return false;
    }
    return MoveFileExW(tmp.c_str(), p.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
}

inline bool exists(const std::filesystem::path& p) {
    std::error_code ec;
    return std::filesystem::exists(p, ec);
}

inline int64_t unix_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch())
        .count();
}

// One plugin's connection to Kiln: the API table, its owner and the log prefix ("greeneye" -> "[greeneye] ...").
struct Host {
    const KilnApi* api = nullptr;
    KilnOwner owner = 0;
    const char* tag = "";

    void log(int32_t level, const std::string& text) const {
        if (!api) return;
        const std::string line = std::string("[") + tag + "] " + text;
        api->log(owner, level, kstr(line));
    }
    void logf(int32_t level, const char* fmt, ...) const {
        va_list ap;
        va_start(ap, fmt);
        const std::string s = vformat(fmt, ap);
        va_end(ap);
        log(level, s);
    }
    // A line under a mod's own tag: "[greeneye:my-mod] ...".
    void mod_log(int32_t level, std::string_view mod, std::string_view text) const {
        if (!api) return;
        std::string line = std::string("[") + tag + ":" + std::string(mod) + "] " + std::string(text);
        while (!line.empty() && (line.back() == '\n' || line.back() == '\r')) line.pop_back();
        api->log(owner, level, kstr(line));
    }
    std::filesystem::path path(int32_t kind) const {
        char buf[2048];
        size_t n = 0;
        if (!api || api->path(kind, buf, sizeof buf, &n) != KILN_OK) return {};
        return std::filesystem::path(to_wide(std::string_view(buf, n < sizeof buf ? n : sizeof buf)));
    }
    template <class T>
    const T* service(const char* name, uint32_t version) const {
        if (!api) return nullptr;
        const auto* t = static_cast<const T*>(api->service(kstr(name), version));
        if (t && t->header.size < sizeof(KilnServiceHeader)) return nullptr;
        return t;
    }
    // Directory for status files (<game>/kiln/state), falling back to the logs folder on an older core.
    std::filesystem::path state_dir() const {
        auto p = path(KILN_PATH_STATE);
        return p.empty() ? path(KILN_PATH_LOGS) : p;
    }
    // Mod configs: the active profile's configDir, else <game>/kiln/config.
    std::filesystem::path config_dir() const {
        if (const auto* prof = service<KilnProfileApi>(KILN_PROFILE_SERVICE, 1))
            if (!sv(prof->config_dir()).empty()) return std::filesystem::path(to_wide(sv(prof->config_dir())));
        auto p = path(KILN_PATH_CONFIG);
        return p.empty() ? path(KILN_PATH_KILN) / L"config" : p;
    }
};

// A game-side path from a profile, as kiln-core resolves it: '/' -> '\', relative to <game>/kiln/.
inline std::filesystem::path resolve_profile_path(std::string_view game_path, const std::filesystem::path& kiln_dir) {
    std::string p(game_path);
    for (char& c : p)
        if (c == '/') c = '\\';
    const bool drive = p.size() >= 3 && ((p[0] >= 'A' && p[0] <= 'Z') || (p[0] >= 'a' && p[0] <= 'z')) &&
                       p[1] == ':' && p[2] == '\\';
    const bool unc = p.size() >= 2 && p[0] == '\\' && p[1] == '\\';
    std::filesystem::path out = (drive || unc) ? std::filesystem::path(to_wide(p)) : kiln_dir / to_wide(p);
    return out.lexically_normal();
}

// One mod of an engine to consider: a folder (or, for flat layouts, a file), where it came from and whether the
// profile switched it off.
struct ModRef {
    std::string id;      // the profile entry's id, or the folder's name for drop-ins
    std::string version; // from the profile entry, may be empty
    std::filesystem::path path;
    std::string origin;  // "profile" or "folder"
    bool enabled = true; // false: listed in the profile with "enabled": false
};

// What the profile says about one engine (mods.<engine> and whether its folder is read).
struct EnginePlan {
    uint32_t mode = KILN_PROFILE_LEGACY;
    bool profile_lists_engine = false; // the profile has a "mods.<engine>" array
    bool read_folder = true;           // also read the engine's own folder
    std::vector<ModRef> listed;        // the profile's entries, in order
    std::string note;                  // one line for the log: where the list came from
};

inline EnginePlan plan_engine(const Host& host, const char* engine) {
    EnginePlan plan;
    const auto* prof = host.service<KilnProfileApi>(KILN_PROFILE_SERVICE, 1);
    if (!prof) {
        plan.note = "no kiln.profile service: the engine's folder only";
        return plan;
    }
    plan.mode = prof->mode();
    if (plan.mode == KILN_PROFILE_NO_MODS || plan.mode == KILN_PROFILE_INVALID) {
        plan.read_folder = false;
        plan.note = plan.mode == KILN_PROFILE_NO_MODS ? "play-once no-mods: nothing loads" : "the profile is invalid: nothing loads";
        return plan;
    }
    if (plan.mode == KILN_PROFILE_LEGACY) {
        plan.note = "no profile (legacy layout): the engine's folder";
        return plan;
    }
    nlohmann::json doc;
    try {
        doc = nlohmann::json::parse(std::string(sv(prof->json())), nullptr, true, true);
    } catch (...) {
        plan.note = "the profile text could not be read again: the engine's folder";
        return plan;
    }
    bool dropins_all = doc.contains("dropins") && doc["dropins"].is_boolean() && doc["dropins"].get<bool>();
    const auto kiln_dir = host.path(KILN_PATH_KILN);
    if (doc.contains("mods") && doc["mods"].is_object() && doc["mods"].contains(engine) && doc["mods"][engine].is_array()) {
        plan.profile_lists_engine = true;
        for (const auto& m : doc["mods"][engine]) {
            if (!m.is_object() || !m.contains("path") || !m["path"].is_string()) continue;
            ModRef r;
            r.id = m.contains("id") && m["id"].is_string() ? m["id"].get<std::string>() : std::string();
            r.version = m.contains("version") && m["version"].is_string() ? m["version"].get<std::string>() : std::string();
            r.path = resolve_profile_path(m["path"].get<std::string>(), kiln_dir);
            r.enabled = !(m.contains("enabled") && m["enabled"].is_boolean() && !m["enabled"].get<bool>());
            r.origin = "profile";
            plan.listed.push_back(std::move(r));
        }
    }
    plan.read_folder = !plan.profile_lists_engine || dropins_all;
    std::string name(sv(prof->id()));
    plan.note = "profile '" + name + "': " + std::to_string(plan.listed.size()) + " listed" +
                (plan.read_folder ? (plan.profile_lists_engine ? ", plus the engine's folder (dropins)" : ", plus the engine's folder (the profile does not manage this engine)")
                                  : "");
    return plan;
}

// The exports of a PE file on disk, read through a plain file read (the DLL is never loaded). Empty when the file is
// not a 64-bit PE image or has no export directory.
inline std::vector<std::string> pe_exports(const std::filesystem::path& file) {
    std::vector<std::string> names;
    std::string d;
    if (!read_file(file, &d) || d.size() < 0x200 || d[0] != 'M' || d[1] != 'Z') return names;
    auto u16 = [&](size_t o) -> uint32_t { return o + 2 <= d.size() ? static_cast<uint8_t>(d[o]) | static_cast<uint8_t>(d[o + 1]) << 8 : 0; };
    auto u32 = [&](size_t o) -> uint32_t { return o + 4 <= d.size() ? u16(o) | u16(o + 2) << 16 : 0; };
    const uint32_t pe = u32(0x3C);
    if (pe + 24 > d.size() || u32(pe) != 0x00004550) return names;
    const uint32_t sections = u16(pe + 6), opt_size = u16(pe + 20), opt = pe + 24;
    if (u16(opt) != 0x20B) return names;  // PE32+ only
    const uint32_t exp_rva = u32(opt + 112);
    if (!exp_rva) return names;
    const uint32_t sec0 = opt + opt_size;
    auto file_off = [&](uint32_t rva) -> size_t {
        for (uint32_t i = 0; i < sections; ++i) {
            const size_t s = sec0 + i * 40;
            const uint32_t va = u32(s + 12), vsz = u32(s + 8), raw = u32(s + 20), rsz = u32(s + 16);
            if (rva >= va && rva < va + (vsz > rsz ? vsz : rsz)) return raw + (rva - va);
        }
        return static_cast<size_t>(-1);
    };
    const size_t ed = file_off(exp_rva);
    if (ed == static_cast<size_t>(-1) || ed + 40 > d.size()) return names;
    const uint32_t count = u32(ed + 24);
    const size_t tbl = file_off(u32(ed + 32));
    if (tbl == static_cast<size_t>(-1)) return names;
    for (uint32_t i = 0; i < count && i < 65536; ++i) {
        const size_t no = file_off(u32(tbl + i * 4));
        if (no == static_cast<size_t>(-1) || no >= d.size()) continue;
        const size_t end = d.find('\0', no);
        if (end == std::string::npos) break;
        names.emplace_back(d, no, end - no);
    }
    return names;
}

inline bool pe_exports_name(const std::filesystem::path& file, std::string_view name) {
    for (const auto& n : pe_exports(file))
        if (n == name) return true;
    return false;
}

// True when the file contains the byte string (a marker text, never loaded as code).
inline bool file_contains(const std::filesystem::path& file, std::string_view marker) {
    std::string d;
    return read_file(file, &d) && d.find(marker) != std::string::npos;
}

// The module in this process whose file name is `name` and that lives in `dir` (not System32): 0 when none.
inline HMODULE module_in_dir(const wchar_t* name, const std::filesystem::path& dir) {
    HMODULE h = GetModuleHandleW(name);
    if (!h) return nullptr;
    wchar_t p[MAX_PATH] = L"";
    GetModuleFileNameW(h, p, MAX_PATH);
    std::error_code ec;
    return std::filesystem::equivalent(std::filesystem::path(p).parent_path(), dir, ec) ? h : nullptr;
}

}  // namespace kcompat
