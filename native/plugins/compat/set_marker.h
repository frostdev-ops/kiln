// SPDX-License-Identifier: MIT
// Whether a DLL file is Shrouded Engine Tweaks' (SET's) own loader, decided by SET's own markers and never by the
// file name. Header-only and without windows.h (host tests build it on Linux). Used by kiln-set and kiln.types
// (native/plugins/set/src/coexist.h: step aside / runner passive only for SET) and by kiln-enshwick (naming the
// powrprof.dll of another tool).
//
// SET's markers (docs/re/shrouded-engine-tweaks.md; checked with `strings` on SET 0.1.1's release powrprof.dll, assumed
// for 0.2.0): its name "Shrouded Engine Tweaks" (the version resource in UTF-16, the start line in ASCII) and its folder
// name shrouded_engine_tweaks (the plugins\, layer\ and mods\ folders it reads; its Vulkan layer
// VK_LAYER_SHROUDED_ENGINE_TWEAKS). Both are matched case-insensitively, as ASCII or UTF-16LE: the same text the
// launcher's engine table (crates/kiln-manage/src/engines.rs) knows SET by. SET has no export of its own (it forwards
// the system powrprof.dll's), so exports cannot tell it from any other power proxy.
//
// A powrprof.dll without these markers (a proxy loader that only forwards the system DLL's exports and loads other
// DLLs, Shared XP, Ember ...) is not SET: kiln-set keeps running SET's Lua mods next to it (GitHub issue 23).
#pragma once
#include <algorithm>
#include <array>
#include <cstddef>
#include <filesystem>
#include <fstream>
#include <istream>
#include <string>
#include <string_view>

namespace kcompat {

struct SetMarks {
    bool name = false;    // "Shrouded Engine Tweaks"
    bool folder = false;  // shrouded_engine_tweaks
};

// Scans a stream incrementally (never the whole file in RAM, at most 256 MiB), markers split over a read boundary
// included. Stops at the name; with `any`, at either marker.
inline SetMarks set_marks(std::istream& in, bool any) {
    constexpr std::string_view name = "shrouded engine tweaks", folder = "shrouded_engine_tweaks";
    auto wide = [](std::string_view s) {
        std::string w;
        for (char c : s) { w += c; w += '\0'; }
        return w;
    };
    const std::string name_w = wide(name), folder_w = wide(folder);
    SetMarks marks;
    std::array<char, 65536> block{};
    std::string tail;
    constexpr size_t limit = 256 * 1024 * 1024;
    for (size_t read = 0; in && read < limit;) {
        in.read(block.data(), static_cast<std::streamsize>(std::min(block.size(), limit - read)));
        const size_t n = static_cast<size_t>(in.gcount());
        if (!n) break;
        read += n;
        const size_t from = tail.size();
        tail.append(block.data(), n);
        std::transform(tail.begin() + static_cast<std::ptrdiff_t>(from), tail.end(), tail.begin() + static_cast<std::ptrdiff_t>(from),
                       [](char c) { return c >= 'A' && c <= 'Z' ? static_cast<char>(c + 32) : c; });
        marks.name = marks.name || tail.find(name) != std::string::npos || tail.find(name_w) != std::string::npos;
        marks.folder = marks.folder || tail.find(folder) != std::string::npos || tail.find(folder_w) != std::string::npos;
        if (marks.name || (any && marks.folder)) return marks;
        if (tail.size() >= name_w.size()) tail.erase(0, tail.size() - name_w.size() + 1);
    }
    return marks;
}

// A file whose name is powrprof.dll (any case).
inline bool named_powrprof(const std::filesystem::path& file) {
    auto name = file.filename().wstring();
    std::transform(name.begin(), name.end(), name.begin(), [](wchar_t c) { return c >= L'A' && c <= L'Z' ? c + 32 : c; });
    return name == L"powrprof.dll";
}

// What a powrprof.dll outside the system folder is, as far as SET goes. unreadable: the file could not be opened, so
// nothing tells it apart from SET (the caller treats that as a failed look, not as SET and not as clean).
enum class PowerProxy { other, set, unreadable };
inline PowerProxy power_proxy_owner(const std::filesystem::path& file) {
    std::ifstream f(file, std::ios::binary);
    if (!f) return PowerProxy::unreadable;
    const SetMarks m = set_marks(f, true);
    return m.name || m.folder ? PowerProxy::set : PowerProxy::other;
}

// The tool a powrprof.dll that is not Enshwick's stub belongs to, by the markers the launcher's engine table
// (crates/kiln-manage/src/engines.rs) knows them by; "" when none matches (a proxy loader that only forwards, or a tool
// Kiln does not know).
inline std::string power_proxy_tool(const std::filesystem::path& file) {
    if (power_proxy_owner(file) == PowerProxy::set) return "Shrouded Engine Tweaks";
    std::ifstream f(file, std::ios::binary | std::ios::ate);
    if (!f) return {};
    const auto size = f.tellg();
    if (size <= 0 || size > 64 * 1024 * 1024) return {};
    std::string data(static_cast<size_t>(size), '\0');
    f.seekg(0);
    if (!f.read(data.data(), size)) return {};
    std::transform(data.begin(), data.end(), data.begin(), [](char c) { return c >= 'A' && c <= 'Z' ? static_cast<char>(c + 32) : c; });
    static constexpr struct { const char* tool; std::array<const char*, 2> texts; } known[] = {
        {"Shared XP", {"xpshare.ini", nullptr}},
        {"Ember", {"ember-loader", "ember_loader"}},
        {"Flame (Creative Mode's server build)", {"flamestartruntime", "flame.json"}},
        {"ModLoader Proxy", {"modloader-proxy.dll", "chainloader.ini"}},
    };
    for (const auto& k : known)
        for (const char* t : k.texts)
            if (t && data.find(t) != std::string::npos) return k.tool;
    return {};
}

}  // namespace kcompat
