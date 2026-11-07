// SPDX-License-Identifier: MIT
// kiln-enshwick: the decisions that need no Windows call, so they are unit-tested on any machine
// (tests/logic_test.cpp). plugin.cpp gathers the facts (files, modules, the profile) and acts on the answers.
#pragma once

#include <algorithm>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace kenshwick {

// What Enshwick's loader exports and what its powrprof.dll stub passes it (crates/internal/stub/src/lib.rs,
// crates/internal/loader/src/lib.rs, Enshwick v1.0.0-alpha.13):
//   enshwick_start(stub_abi, missing_forwards, release), release() -> 1 held until now / 2 hold ran out / 0 none.
constexpr const char* kStartExport = "enshwick_start";
constexpr uint32_t kStubAbi = 2;
// The stub holds the dedicated server's entry point at most this long (stub/src/gate.rs LONGEST_HOLD).
constexpr uint32_t kLongestHoldMs = 10000;
// The stub's per-process claim (stub/src/load.rs MUTEX_PREFIX + pid): the first holder starts the loader.
constexpr const wchar_t* kStubMutexPrefix = L"Local\\enshwick-stub-";
// Texts that only Enshwick's stub carries (its thread name and the loader path it loads).
constexpr const char* kStubMarkers[] = {"enshwick-loader-stub", "mods\\enshwick\\enshwick.dll"};
// The loader's own folder below <game>/mods; it is never a mod (loader start/library.rs folders()).
constexpr const char* kLoaderFolder = "enshwick";
// A folder Kiln copied into <game>/mods for the profile carries this file; other folders are never touched.
constexpr const char* kManagedMarker = ".kiln-managed";

// The release() answers, as the loader logs them (loader start.rs release_server).
enum class Hold : int { None = 0, Holding = 1, Expired = 2 };
inline uint32_t release_answer(Hold state) {
    return state == Hold::Holding ? 1u : state == Hold::Expired ? 2u : 0u;
}

inline std::string lower(std::string_view s) {
    std::string out(s);
    for (char& c : out)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return out;
}

// A name that can be one folder directly below <game>/mods: no separators, no "." / "..", no Windows-reserved
// characters or device names, no trailing dot or space, not the loader's own folder.
inline bool safe_folder_name(std::string_view name, std::string* why = nullptr) {
    auto fail = [&](const char* w) {
        if (why) *why = w;
        return false;
    };
    if (name.empty() || name.size() > 128) return fail("empty or longer than 128 characters");
    if (name == "." || name == "..") return fail("'.' or '..'");
    for (unsigned char c : name)
        if (c < 0x20 || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' ||
            c == '>' || c == '|')
            return fail("a path separator or a character Windows does not allow");
    if (name.back() == '.' || name.back() == ' ') return fail("ends with a dot or a space");
    if (name.front() == '.') return fail("starts with a dot");
    const std::string l = lower(name);
    const std::string stem = l.substr(0, l.find('.'));
    static const char* kDevices[] = {"con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6",
                                     "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7",
                                     "lpt8", "lpt9"};
    for (const char* d : kDevices)
        if (stem == d) return fail("a Windows device name");
    if (l == kLoaderFolder) return fail("the name of Enshwick's loader folder");
    return true;
}

// What a mod folder holds, by Enshwick's rules (book: a DLL exporting enshwick_mod_abi/_query/_init/_ready, a Luau
// mod with main.luau, a data pack with pack.toml; a pack may carry main.luau as well).
enum class ModForm { Unknown, Dll, Luau, Pack };
inline const char* form_name(ModForm f) {
    switch (f) {
        case ModForm::Dll: return "dll";
        case ModForm::Luau: return "luau";
        case ModForm::Pack: return "pack";
        default: return "unknown";
    }
}
inline ModForm classify(bool has_pack_toml, bool has_main_luau, bool has_enshwick_dll) {
    if (has_enshwick_dll) return ModForm::Dll;
    if (has_pack_toml) return ModForm::Pack;
    if (has_main_luau) return ModForm::Luau;
    return ModForm::Unknown;
}

// Which loader DLL to run. "auto": the game's own copy (<game>/mods/enshwick/enshwick.dll, what the player's mods
// were installed with) when it is there, else the copy Kiln ships (<game>/kiln/plugins/enshwick/enshwick.dll).
enum class LoaderPick { None, Game, Bundled };
inline LoaderPick pick_loader(std::string_view preference, bool game_ok, bool bundled_ok) {
    const std::string p = lower(preference);
    if (p == "game") return game_ok ? LoaderPick::Game : LoaderPick::None;
    if (p == "bundled") return bundled_ok ? LoaderPick::Bundled : LoaderPick::None;
    if (game_ok) return LoaderPick::Game;
    if (bundled_ok) return LoaderPick::Bundled;
    return LoaderPick::None;
}

// Why Kiln leaves Enshwick to its own stub. Empty: Kiln starts the loader.
struct StepAsideFacts {
    bool stub_loaded = false;      // a powrprof.dll from the game folder with Enshwick's stub markers is loaded
    bool stub_claimed = false;     // the stub's per-process mutex already exists
    bool loader_loaded = false;    // a module exporting enshwick_start is already in the process
    bool claim_failed = false;     // Kiln could not create the claim itself
};
inline std::string step_aside_reason(const StepAsideFacts& f) {
    if (f.stub_loaded)
        return "Enshwick's own powrprof.dll is loaded in this process and starts the loader itself";
    if (f.stub_claimed) return "an Enshwick stub already claimed this process (Local\\enshwick-stub-<pid>)";
    if (f.loader_loaded) return "Enshwick's loader is already loaded in this process";
    if (f.claim_failed) return "the per-process claim could not be created, so the loader never starts twice";
    return {};
}

// Whether a mod-folder line means the mod will be in mods/ for the loader (copied, kept, in place, or installed there).
inline bool runs(std::string_view action) {
    return action == "copy" || action == "refresh" || action == "keep" || action == "in-place" || action == "run";
}
// The loader starts only when it has a mod to run, unless the config says "always": Enshwick with no mods only hooks
// the game (its hooks then cost Kiln the functions they cover).
inline bool should_start(size_t running_mods, bool always) { return running_mods > 0 || always; }

// --- profile -> <game>/mods ---
// Enshwick's loader reads only <game>/mods (start/game.rs: game_dir.join("mods")). A profile's Enshwick entries live
// in Kiln's library, so the plugin copies each enabled one into <game>/mods/<folder>/ with a marker, refreshes it when
// the source changed, and removes copies the profile no longer enables. Folders without the marker belong to the
// player and are never changed.
struct Wanted {
    std::string folder;      // the folder name below <game>/mods
    std::string source;      // the library folder (UTF-8 path)
    std::string fingerprint; // of the source's files (names, sizes, times)
};
struct Existing {
    std::string folder;
    bool managed = false;       // carries the marker
    std::string source;         // the marker's source
    std::string fingerprint;    // the marker's fingerprint
};
enum class Act { Copy, Refresh, Keep, Remove, Conflict };
struct Action {
    Act act;
    std::string folder;
    std::string source;
    std::string fingerprint;
    std::string note;
};
inline const char* act_name(Act a) {
    switch (a) {
        case Act::Copy: return "copy";
        case Act::Refresh: return "refresh";
        case Act::Keep: return "keep";
        case Act::Remove: return "remove";
        default: return "conflict";
    }
}

inline std::vector<Action> plan_sync(const std::vector<Wanted>& wanted, const std::vector<Existing>& existing) {
    std::vector<Action> out;
    auto find = [&](const std::string& folder) -> const Existing* {
        const std::string l = lower(folder);
        for (const auto& e : existing)
            if (lower(e.folder) == l) return &e;
        return nullptr;
    };
    std::vector<std::string> taken;
    for (const auto& w : wanted) {
        const std::string l = lower(w.folder);
        if (std::find(taken.begin(), taken.end(), l) != taken.end()) {
            out.push_back({Act::Conflict, w.folder, w.source, w.fingerprint, "listed twice in the profile"});
            continue;
        }
        taken.push_back(l);
        const Existing* e = find(w.folder);
        if (!e) {
            out.push_back({Act::Copy, w.folder, w.source, w.fingerprint, ""});
        } else if (!e->managed) {
            out.push_back({Act::Conflict, w.folder, w.source, w.fingerprint,
                           "a folder of that name in mods/ was installed by hand; it runs, the profile's copy does not"});
        } else if (e->source != w.source || e->fingerprint != w.fingerprint) {
            out.push_back({Act::Refresh, w.folder, w.source, w.fingerprint, ""});
        } else {
            out.push_back({Act::Keep, w.folder, w.source, w.fingerprint, ""});
        }
    }
    for (const auto& e : existing) {
        if (!e.managed) continue;
        if (std::find(taken.begin(), taken.end(), lower(e.folder)) == taken.end())
            out.push_back({Act::Remove, e.folder, e.source, e.fingerprint, "no longer enabled in the profile"});
    }
    return out;
}

// Marker file text: two lines, "source=<path>" and "fingerprint=<hex>".
inline std::string marker_text(const std::string& source, const std::string& fingerprint) {
    return "source=" + source + "\nfingerprint=" + fingerprint + "\n";
}
inline bool parse_marker(std::string_view text, std::string* source, std::string* fingerprint) {
    bool s = false, f = false;
    size_t at = 0;
    while (at < text.size()) {
        size_t end = text.find('\n', at);
        if (end == std::string_view::npos) end = text.size();
        std::string_view line = text.substr(at, end - at);
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        if (line.rfind("source=", 0) == 0) *source = std::string(line.substr(7)), s = true;
        else if (line.rfind("fingerprint=", 0) == 0) *fingerprint = std::string(line.substr(12)), f = true;
        at = end + 1;
    }
    return s && f;
}

// FNV-1a over "name|size|time" lines, sorted by name: cheap, and changes when any file is added, removed or rewritten.
struct FileStamp {
    std::string rel;
    uint64_t size = 0;
    uint64_t time = 0;
};
inline std::string fingerprint(std::vector<FileStamp> files) {
    std::sort(files.begin(), files.end(), [](const FileStamp& a, const FileStamp& b) { return a.rel < b.rel; });
    uint64_t h = 1469598103934665603ull;
    auto mix = [&](std::string_view s) {
        for (unsigned char c : s) h = (h ^ c) * 1099511628211ull;
    };
    for (const auto& f : files) {
        mix(lower(f.rel));
        mix("|" + std::to_string(f.size) + "|" + std::to_string(f.time) + "\n");
    }
    char buf[17];
    static const char* hex = "0123456789abcdef";
    for (int i = 15; i >= 0; --i, h >>= 4) buf[i] = hex[h & 15];
    buf[16] = 0;
    return buf;
}

// The lines worth copying from Enshwick's own log into kiln.log (the start, each mod's fate, the summary, problems).
// The loader writes "<time>Z <level> [<source>] <text>" (log.rs), levels lower-case.
inline bool worth_mirroring(std::string_view line) {
    static const char* keys[] = {"starting on", "initialised", "ready: ", "skipped", "not an Enshwick mod",
                                 "server's own code", "game build", "turned off", "arming", " error ", " warn ",
                                 "ERROR", "WARN"};
    for (const char* k : keys)
        if (line.find(k) != std::string_view::npos) return true;
    return false;
}

// Complete lines of `text` from byte `from` on; `from` moves past the last newline taken.
inline std::vector<std::string> new_lines(std::string_view text, size_t* from) {
    std::vector<std::string> out;
    size_t at = *from;
    while (at < text.size()) {
        const size_t end = text.find('\n', at);
        if (end == std::string_view::npos) break;
        std::string line(text.substr(at, end - at));
        if (!line.empty() && line.back() == '\r') line.pop_back();
        out.push_back(std::move(line));
        at = end + 1;
    }
    *from = at;
    return out;
}

}  // namespace kenshwick
