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
#include <algorithm>
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
    std::ifstream f(p, std::ios::binary | std::ios::ate);
    if (!f) return false;
    const auto n = f.tellg();
    if (n < 0 || n > 256 * 1024 * 1024) return false;
    out->resize(static_cast<size_t>(n));
    f.seekg(0);
    return static_cast<bool>(f.read(out->data(), static_cast<std::streamsize>(n)));
}

inline bool write_file_atomic(const std::filesystem::path& p, std::string_view data) {
    static std::mutex mu;
    std::lock_guard lock(mu);
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
        if (t && t->header.size < sizeof(T)) return nullptr;
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
        plan.mode = KILN_PROFILE_INVALID;
        plan.read_folder = false;
        plan.note = "the profile text is invalid: nothing loads";
        return plan;
    }
    if (!doc.is_object() || (doc.contains("mods") && !doc["mods"].is_object()) ||
        (doc.contains("mods") && doc["mods"].contains(engine) && !doc["mods"][engine].is_array())) {
        plan.mode = KILN_PROFILE_INVALID;
        plan.read_folder = false;
        plan.note = "the engine's profile list is invalid: nothing loads";
        return plan;
    }
    bool dropins_all = doc.contains("dropins") && doc["dropins"].is_boolean() && doc["dropins"].get<bool>();
    const auto kiln_dir = host.path(KILN_PATH_KILN);
    if (doc.contains("mods") && doc["mods"].is_object() && doc["mods"].contains(engine) && doc["mods"][engine].is_array()) {
        plan.profile_lists_engine = true;
        for (const auto& m : doc["mods"][engine]) {
            if (!m.is_object() || !m.contains("path") || !m["path"].is_string() ||
                m["path"].get_ref<const std::string&>().empty() ||
                m["path"].get_ref<const std::string&>().find('\0') != std::string::npos ||
                (m.contains("id") && (!m["id"].is_string() || m["id"].get_ref<const std::string&>().empty() ||
                                      m["id"].get_ref<const std::string&>().find('\0') != std::string::npos)) ||
                (m.contains("enabled") && !m["enabled"].is_boolean())) {
                plan.mode = KILN_PROFILE_INVALID;
                plan.read_folder = false;
                plan.listed.clear();
                plan.note = "the engine's profile entry is invalid: nothing loads";
                return plan;
            }
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
    if (!read_file(file, &d) || d.size() < 64 || d[0] != 'M' || d[1] != 'Z') return names;
    auto range = [&](size_t o, size_t n) { return o <= d.size() && n <= d.size() - o; };
    auto u16 = [&](size_t o) -> uint16_t { return range(o, 2) ? static_cast<uint8_t>(d[o]) | static_cast<uint8_t>(d[o + 1]) << 8 : 0; };
    auto u32 = [&](size_t o) -> uint32_t { return range(o, 4) ? u16(o) | uint32_t(u16(o + 2)) << 16 : 0; };
    const size_t pe = u32(0x3C);
    if (!range(pe, 24) || u32(pe) != 0x00004550 || u16(pe + 4) != IMAGE_FILE_MACHINE_AMD64 ||
        !(u16(pe + 22) & IMAGE_FILE_DLL)) return names;
    const size_t sections = u16(pe + 6), opt_size = u16(pe + 20), opt = pe + 24;
    if (opt_size < 120 || !range(opt, opt_size) || u16(opt) != 0x20B || u32(opt + 108) < 1) return names;
    const uint32_t exp_rva = u32(opt + 112), exp_size = u32(opt + 116);
    const size_t sec0 = opt + opt_size;
    if (!sections || sections > 96 || !range(sec0, sections * 40) || exp_size < 40) return names;
    // Only file-backed RVAs count; zero-filled virtual tails must never alias another file offset.
    auto file_off = [&](uint32_t rva, size_t n, bool executable = false) -> size_t {
        for (size_t i = 0; i < sections; ++i) {
            const size_t s = sec0 + i * 40;
            const uint32_t va = u32(s + 12), raw = u32(s + 20), rsz = u32(s + 16);
            if (rva < va || uint64_t(rva) - va >= rsz) continue;
            const size_t delta = size_t(rva) - va;
            if (n > rsz - delta || !range(size_t(raw) + delta, n) ||
                (executable && !(u32(s + 36) & IMAGE_SCN_MEM_EXECUTE))) return SIZE_MAX;
            return size_t(raw) + delta;
        }
        return SIZE_MAX;
    };
    const size_t ed = file_off(exp_rva, 40);
    if (ed == SIZE_MAX) return names;
    const size_t count = u32(ed + 24), functions = u32(ed + 20);
    if (!count || count > 65536 || !functions || functions > 65536) return names;
    const size_t tbl = file_off(u32(ed + 32), count * 4);
    const size_t ord = file_off(u32(ed + 36), count * 2);
    const size_t fn = file_off(u32(ed + 28), functions * 4);
    if (tbl == SIZE_MAX || ord == SIZE_MAX || fn == SIZE_MAX) return names;
    std::string previous;
    for (size_t i = 0; i < count; ++i) {
        const uint32_t rva = u32(tbl + i * 4);
        std::string name;
        bool terminated = false;
        for (size_t j = 0; j < 4096; ++j) {
            if (uint64_t(rva) + j > UINT32_MAX) break;
            const size_t o = file_off(rva + static_cast<uint32_t>(j), 1);
            if (o == SIZE_MAX) break;
            if (!d[o]) { terminated = true; break; }
            name.push_back(d[o]);
        }
        // GetProcAddress binary-searches this table. Refuse duplicates/unsorted or truncated names before
        // selecting callable entries, so the precheck and Windows cannot choose different same-named exports.
        if (!terminated || name.empty() || (i && name <= previous)) return {};
        previous = name;
        const size_t index = u16(ord + i * 2);
        if (index >= functions) return {};
        const uint32_t target = u32(fn + index * 4);
        if (!target || (target >= exp_rva && uint64_t(target) - exp_rva < exp_size) ||
            file_off(target, 1, true) == SIZE_MAX) continue;
        names.push_back(std::move(name));
    }
    return names;
}

inline bool pe_exports_name(const std::filesystem::path& file, std::string_view name) {
    for (const auto& n : pe_exports(file))
        if (n == name) return true;
    return false;
}

// Pin a compatibility host before it exposes callbacks or starts permanent foreign runtimes.
// Neither Flame nor Shroudtopia has a shutdown/join contract, and Greeneye generations retain trampolines.
inline bool pin_module(const void* address) {
    HMODULE module = nullptr;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                             reinterpret_cast<LPCWSTR>(address), &module) != 0;
}

// Path policy (docs/kiln/engines/README.md): keep every component of the path open without delete sharing while
// validating/loading. From the authorized root (the mod folder, or the engine folder for a scan) down, reject reparse
// points (symlinks, junctions), alternate streams, "..", device/path aliases and a final path outside the root.
// Ancestors above the root are the player's layout (a Steam library behind a symlink, Proton's Z:\ drive link) and
// are pinned but not judged. Under Wine a symlink to a *file* is invisible to every Win32/NT query (Wine follows it and
// flags only symlinks to directories), so that one case is left to the installer that writes the mod folder.
class CheckedPath {
    std::vector<HANDLE> handles;
public:
    std::string error;
    ~CheckedPath() { for (HANDLE h : handles) CloseHandle(h); }
    CheckedPath() = default;
    CheckedPath(const CheckedPath&) = delete;
    CheckedPath& operator=(const CheckedPath&) = delete;
    bool open(const std::filesystem::path& root, const std::filesystem::path& file, bool regular) {
        namespace fs = std::filesystem;
        auto fail = [&](const char* why) { error = why; return false; };
        if (root.empty() || file.empty() || root.native().find(L'\0') != std::wstring::npos ||
            file.native().find(L'\0') != std::wstring::npos) return fail("empty path or embedded NUL");
        std::error_code ec;
        fs::path base = fs::absolute(root, ec).lexically_normal();
        if (ec) return fail("invalid root");
        fs::path full = fs::absolute(file, ec).lexically_normal();
        if (ec) return fail("invalid DLL path");
        while (base != base.root_path() && base.filename().empty()) base = base.parent_path();
        while (full != full.root_path() && full.filename().empty()) full = full.parent_path();
        // Windows path comparison is case-insensitive. lexically_relative is case-sensitive.
        auto bi = base.begin(), fi = full.begin();
        for (; bi != base.end(); ++bi, ++fi)
            if (fi == full.end() || _wcsicmp(bi->c_str(), fi->c_str())) return fail("path outside mod folder");
        // Pin the whole path (ancestors included) rather than trusting a checked leaf folder after a queued worker
        // has allowed its ancestors to change; only components from the root down are inspected.
        std::vector<fs::path> paths{base.root_path()};
        fs::path current = base.root_path();
        for (const auto& part : base.relative_path()) {
            current /= part;
            paths.push_back(current);
        }
        const size_t root_index = paths.size() - 1;
        for (; fi != full.end(); ++fi) {
            const auto part = fi->native();
            if (part.empty() || part == L"." || part == L".." || part.find(L':') != std::wstring::npos ||
                part.back() == L'.' || part.back() == L' ') return fail("unsafe path component");
            current /= *fi;
            paths.push_back(current);
        }
        if (regular && paths.size() == root_index + 1) return fail("DLL is not inside mod folder");
        for (size_t i = 0; i < paths.size(); ++i) {
            const bool last = i + 1 == paths.size();
            HANDLE h = CreateFileW(paths[i].c_str(), GENERIC_READ,
                                  FILE_SHARE_READ | ((!last || !regular) ? FILE_SHARE_WRITE : 0), nullptr,
                                  OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
            if (h == INVALID_HANDLE_VALUE) return fail("cannot open path safely (missing or in use)");
            handles.push_back(h);
            // Components above the authorized root are the player's layout (a Steam library behind a symlink,
            // Proton's drive links such as Z:\ -> /). They stay open (pinned) but are not judged; containment is
            // enforced from the root down, and by the final-path comparison below.
            if (i < root_index) continue;
            // Wine ignores FILE_FLAG_OPEN_REPARSE_POINT for Unix symlinks: the handle is the link's target and
            // reports its attributes. Wine's path queries do flag a symlink to a directory as a reparse point, so ask
            // by name as well (on Windows both agree).
            const DWORD by_name = GetFileAttributesW(paths[i].c_str());
            BY_HANDLE_FILE_INFORMATION info{};
            if (by_name == INVALID_FILE_ATTRIBUTES || (by_name & FILE_ATTRIBUTE_REPARSE_POINT) ||
                !GetFileInformationByHandle(h, &info) || (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) ||
                ((!last || !regular) && !(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) ||
                (last && regular && (info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))) return fail("symlink, junction or wrong file type");
        }
        // Also compare final paths; Wine and filesystem redirectors may resolve links while opening a handle.
        auto final_path = [](HANDLE h) {
            std::wstring path(32768, L'\0');
            DWORD n = GetFinalPathNameByHandleW(h, path.data(), static_cast<DWORD>(path.size()), FILE_NAME_NORMALIZED);
            if (!n || n >= path.size()) return std::wstring();
            path.resize(n);
            return path;
        };
        const auto resolved_root = final_path(handles[root_index]);
        const auto resolved_file = final_path(handles.back());
        if (resolved_root.empty() || resolved_file.empty() ||
            _wcsnicmp(resolved_root.c_str(), resolved_file.c_str(), resolved_root.size()) ||
            (resolved_file.size() > resolved_root.size() && resolved_root.back() != L'\\' && resolved_file[resolved_root.size()] != L'\\'))
            return fail("resolved path outside mod folder");
        return true;
    }
};

inline bool contained_path(const std::filesystem::path& root, const std::filesystem::path& candidate) {
    CheckedPath checked;
    return checked.open(root, candidate, false);
}

inline HMODULE guarded_load(const wchar_t* path, DWORD* fault) {
    if (fault) *fault = 0;
    __try {
        HMODULE module = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module) pin_module(module);
        return module;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { if (fault) *fault = GetExceptionCode(); return nullptr; }
}

class CheckedDll : public CheckedPath {
    std::filesystem::path dll;
public:
    bool open(const std::filesystem::path& root, const std::filesystem::path& file, std::string_view entry) {
        if (!CheckedPath::open(root, file, true)) return false;
        if (!pe_exports_name(file, entry)) { error = "no " + std::string(entry) + " callable export in DLL"; return false; }
        dll = file;
        return true;
    }
    HMODULE load(DWORD* fault) const { return guarded_load(dll.c_str(), fault); }
};

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
