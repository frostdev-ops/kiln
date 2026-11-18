// SPDX-License-Identifier: MIT
// kiln.ui: OptiScaler next to the game, and whether Kiln's overlay draws next to it (overlay_guard.cpp; host test:
// tests/overlay_guard_test.cpp). No windows.h.
//
// OptiScaler (github.com/optiscaler/OptiScaler, GPL-3.0; read at 2df7ad9f, release v0.9.5; no code of it here) is an
// upscaler and frame generation tool installed as a proxy DLL in the game folder. Its profile for enshrouded.exe
// (misc/GameConfigs.cpp) loads vulkan-1.dll in OptiScaler's DllMain and patches its exports before the game runs. With
// its overlay menu on (the default) it also patches the game device's present and swapchain functions when the device
// is made (hooks/Vulkan_Hooks.cpp HookDevice), draws its own ImGui menu in every present and rewrites the present's wait
// semaphores (menu/menu_overlay_vk.cpp QueuePresent), subclasses the game window and hooks raw input, and opens its menu
// with Insert, Kiln's menu key too. With frame generation on, the game draws into FFX's stand-in swapchain and the real
// presents come from FFX's own thread, past other overlays' hooks on purpose (framegen/ffx/FSRFG_Vk.cpp
// BypassOverlayHooks). With `OverlayMenu=false` under [Menu] in OptiScaler.ini none of the device, present or window
// patches is made, and frame generation is off with it (OptiScaler.ini: "without this option OptiScaler will disable
// all FG features"): Kiln's overlay then draws as without OptiScaler.
//
// So: OptiScaler with its menu on (or frame generation set) -> Kiln's drawing steps aside on its own, as for Streamline;
// OptiScaler with `OverlayMenu=false` and no frame generation -> Kiln draws. KILN_UI_OVERLAY=0/1 wins over both.
//
// Found by contents, never by a name alone: dxgi.dll, winmm.dll, version.dll and dbghelp.dll are other tools' and
// loaders' names too (ReShade, ShroudForge, EML, Shroudtopia). A candidate under one of OptiScaler's names is read only
// when OptiScaler.ini is next to it or the file is named OptiScaler*; it is OptiScaler when it names its own
// "OptiScaler.ini" (ASCII or UTF-16, case ignored). A game without OptiScaler.ini reads no extra file and lists no
// folder: one look-up of the ini's name per candidate.
#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>
#include <vector>

namespace kui::overlay_guard {

// The names OptiScaler's DLL works under (dllmain.cpp CheckWorkingMode), and its own.
inline constexpr std::array<const wchar_t*, 9> optiscaler_names = {
    L"dxgi.dll", L"winmm.dll", L"version.dll", L"dbghelp.dll", L"d3d12.dll", L"wininet.dll", L"winhttp.dll",
    L"OptiScaler.dll", L"OptiScaler.asi"
};
inline constexpr std::string_view optiscaler_ini_name = "OptiScaler.ini";
// A file larger than this is not read (OptiScaler's DLL is tens of MB).
inline constexpr uint64_t optiscaler_max_bytes = 512ull * 1024 * 1024;

// True when the stream names "optiscaler.ini" (ASCII or UTF-16LE, case ignored): OptiScaler's DLL reads its settings by
// that name (Config.h). Read in chunks, the whole file.
inline bool optiscaler_contents(std::istream& in, uint64_t size) {
    if (!in || size > optiscaler_max_bytes) return false;
    constexpr std::string_view word = "optiscaler.ini";
    std::string wide;
    for (char c : word) { wide += c; wide += '\0'; }
    const size_t keep = wide.size();  // the longer marker: a match across two chunks is still seen
    std::string window;
    std::array<char, 1 << 16> buffer{};
    in.seekg(0);
    while (in) {
        in.read(buffer.data(), std::streamsize(buffer.size()));
        const auto n = size_t(in.gcount());
        if (!n) break;
        for (size_t i = 0; i < n; ++i) {
            char c = buffer[i];
            if (c >= 'A' && c <= 'Z') c = char(c - 'A' + 'a');
            window += c;
        }
        if (window.find(word) != std::string::npos || window.find(wide) != std::string::npos) return true;
        if (window.size() > keep) window.erase(0, window.size() - keep);
    }
    return false;
}

// What OptiScaler.ini says about the parts of OptiScaler that touch the game's presents.
struct OptiScalerIni {
    bool read = false;          // the file was there and read
    int overlay_menu = -1;      // [Menu] OverlayMenu: -1 auto (OptiScaler's default: on), 0 false, 1 true
    std::string fg_input, fg_output;  // [FrameGen] FGInput / FGOutput, lower case; "" or "auto" = OptiScaler's nofg
    // Frame generation is set up: an input and an output other than nofg (Vulkan_Hooks.cpp CreateFGFeature needs both).
    bool frame_gen() const {
        auto none = [](const std::string& v) { return v.empty() || v == "auto" || v == "nofg"; };
        return !none(fg_input) && !none(fg_output);
    }
};

// OptiScaler.ini's text (SimpleIni's rules as OptiScaler reads it: sections and keys case ignored, ';' and '#' start a
// comment line, a value is the rest of the line, trimmed; the last of two equal keys wins).
inline OptiScalerIni parse_optiscaler_ini(std::string_view text) {
    OptiScalerIni out;
    out.read = true;
    auto lower = [](std::string s) {
        for (auto& c : s) if (c >= 'A' && c <= 'Z') c = char(c - 'A' + 'a');
        return s;
    };
    auto trim = [](std::string_view s) {
        while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\r' || s.front() == '\xEF' ||
                              s.front() == '\xBB' || s.front() == '\xBF'))
            s.remove_prefix(1);
        while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\r')) s.remove_suffix(1);
        return s;
    };
    std::string section;
    while (!text.empty()) {
        const size_t end = text.find('\n');
        const std::string_view line = trim(text.substr(0, end));
        text = end == std::string_view::npos ? std::string_view{} : text.substr(end + 1);
        if (line.empty() || line.front() == ';' || line.front() == '#') continue;
        if (line.front() == '[') {
            const size_t close = line.find(']');
            section = lower(std::string(trim(line.substr(1, close == std::string_view::npos ? line.npos : close - 1))));
            continue;
        }
        const size_t eq = line.find('=');
        if (eq == std::string_view::npos) continue;
        const std::string key = lower(std::string(trim(line.substr(0, eq))));
        const std::string value = lower(std::string(trim(line.substr(eq + 1))));
        if (section == "menu" && key == "overlaymenu") out.overlay_menu = value == "false" ? 0 : value == "true" ? 1 : -1;
        else if (section == "framegen" && key == "fginput") out.fg_input = value;
        else if (section == "framegen" && key == "fgoutput") out.fg_output = value;
    }
    return out;
}

struct OptiScaler {
    std::filesystem::path file;  // OptiScaler's DLL; empty: none found
    OptiScalerIni ini;           // OptiScaler.ini next to it
    bool ini_only = false;       // OptiScaler.ini in the game folder, but no OptiScaler DLL next to it (an uninstall's leftover)
    bool found() const { return !file.empty(); }
    // Kiln's overlay draws next to it: OptiScaler's own menu is off and so no present or swapchain of the game is patched.
    bool coexists() const { return found() && ini.overlay_menu == 0 && !ini.frame_gen(); }
};

inline bool optiscaler_named(const std::filesystem::path& file) {
    auto name = file.filename().u8string();
    for (auto& c : name) if (c >= u8'A' && c <= u8'Z') c = char8_t(c - u8'A' + u8'a');
    return name.starts_with(u8"optiscaler");
}

// The ini next to `file`: one look-up by name, never a listing of the folder. Windows matches the name in any case, and
// so does Wine (its own case-insensitive look-up), so every spelling is found in the game; this runs at the game's entry
// point for each candidate, Windows' own copies in System32 included, where a listing would cost thousands of entries.
inline std::filesystem::path optiscaler_ini_next_to(const std::filesystem::path& file) {
    std::error_code ec;
    const auto ini = file.parent_path() / std::filesystem::path(std::u8string(optiscaler_ini_name.begin(), optiscaler_ini_name.end()));
    return std::filesystem::is_regular_file(ini, ec) ? ini : std::filesystem::path{};
}

inline OptiScalerIni read_optiscaler_ini(const std::filesystem::path& ini) {
    if (ini.empty()) return {};
    std::error_code ec;
    const auto size = std::filesystem::file_size(ini, ec);
    if (ec || size > 4 * 1024 * 1024) return {};
    std::ifstream in(ini, std::ios::binary);
    std::string text(size_t(size), '\0');
    if (!in.read(text.data(), std::streamsize(text.size()))) return {};
    return parse_optiscaler_ini(text);
}

// OptiScaler under one of its names directly in `game`, or a loaded copy (`loaded`: full paths of modules with those
// names, from anywhere; Windows' own copies have no OptiScaler.ini next to them and are never read).
inline OptiScaler detect_optiscaler(const std::filesystem::path& game, const std::vector<std::filesystem::path>& loaded = {}) {
    OptiScaler out;
    std::vector<std::filesystem::path> candidates;
    if (!game.empty())
        for (const wchar_t* name : optiscaler_names) candidates.push_back(game / name);
    candidates.insert(candidates.end(), loaded.begin(), loaded.end());
    std::vector<std::filesystem::path> seen;
    for (const auto& file : candidates) {
        std::error_code ec;
        if (file.empty() || !std::filesystem::is_regular_file(file, ec)) continue;
        bool again = false;
        for (const auto& s : seen) if (std::filesystem::equivalent(s, file, ec)) again = true;
        if (again) continue;
        seen.push_back(file);
        const auto ini = optiscaler_ini_next_to(file);
        if (ini.empty() && !optiscaler_named(file)) continue;
        const auto size = std::filesystem::file_size(file, ec);
        if (ec) continue;
        std::ifstream in(file, std::ios::binary);
        if (!optiscaler_contents(in, size)) continue;
        out.file = file;
        out.ini = read_optiscaler_ini(ini);
        return out;
    }
    out.ini_only = !game.empty() && !optiscaler_ini_next_to(game / L"x").empty();
    return out;
}

}  // namespace kui::overlay_guard
