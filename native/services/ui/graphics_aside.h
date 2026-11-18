// SPDX-License-Identifier: MIT
// kiln.ui: when Kiln's frame cap and DLSS settings step aside for another tool (graphics.cpp; docs/kiln/graphics.md
// "Living next to other tools"). Plain logic on the game folder, no windows.h: host test tests/graphics_test.cpp.
//
// Tools that hook the game's DLSS (NVIDIA's NGX) or its presents for frame generation would fight Kiln's settings, so
// Kiln leaves both to them. Found by files in the game folder (Windows and Wine look names up in any case), the same
// rules the launcher's Graphics card uses (kiln_manage::game::graphics_step_aside):
//   OptiScaler (its DLL, found by its contents: overlay_optiscaler.h)    frame cap and DLSS
//   NVIDIA Streamline: sl.interposer.dll, in the folder or Streamline/   frame cap and DLSS
//   EnshroudedVKFG.ini: a DLSS frame generation mod                       frame cap and DLSS
//   dlsstweaks.ini: DLSSTweaks                                            DLSS
//   nvngx.dll or _nvngx.dll next to the game: another DLSS loader         DLSS
// At run time two more: NGX's own functions already patched by another module (DLSS), and Kiln's overlay hooks off
// (the frame cap lives in Kiln's present hook; both, with the overlay's reason).
// The readout stays: it only reads.
#pragma once
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

namespace kui::gfx {

enum Aside : uint32_t {
    kAsideOptiScaler = 1u << 0,
    kAsideStreamline = 1u << 1,
    kAsideVkfg = 1u << 2,
    kAsideDlssTweaks = 1u << 3,
    kAsideNvngx = 1u << 4,
    kAsideNgxPatched = 1u << 5,   // NGX's functions patched by another module (run time)
    kAsideOverlayOff = 1u << 6,   // Kiln's overlay hooks are off (KILN_UI_OVERLAY=0, the crash guard, ...)
};
// The reasons that stop the frame cap too; every reason stops the DLSS settings.
inline constexpr uint32_t kAsideCap = kAsideOptiScaler | kAsideStreamline | kAsideVkfg | kAsideOverlayOff;
inline constexpr uint32_t kAsideDlss = 0x7f;

// The tool's name for the log and the settings page.
inline const char* aside_name(uint32_t bit) {
    switch (bit) {
        case kAsideOptiScaler: return "OptiScaler";
        case kAsideStreamline: return "NVIDIA Streamline (a frame generation mod)";
        case kAsideVkfg: return "a DLSS frame generation mod (EnshroudedVKFG.ini)";
        case kAsideDlssTweaks: return "DLSSTweaks";
        case kAsideNvngx: return "another DLSS loader (nvngx.dll next to the game)";
        case kAsideNgxPatched: return "another module that patched NVIDIA's DLSS functions";
        case kAsideOverlayOff: return "Kiln's overlay hooks being off";
        default: return "?";
    }
}

// "OptiScaler, DLSSTweaks" for the bits set (lowest first).
inline std::string aside_names(uint32_t bits) {
    std::string out;
    for (uint32_t b = 1; b && b <= kAsideOverlayOff; b <<= 1) {
        if (!(bits & b)) continue;
        if (!out.empty()) out += ", ";
        out += aside_name(b);
    }
    return out;
}

struct AsideFiles {
    uint32_t bits = 0;
    std::vector<std::string> files;  // what was found, relative to the game folder ("Streamline/sl.interposer.dll")
};

// The game folder's files. `optiscaler` / `streamline`: what the overlay guard found at start (overlay_guard.cpp: its
// OptiScaler check reads the DLL's contents; its Streamline check also counts a loaded sl.interposer.dll).
inline AsideFiles detect_aside_files(const std::filesystem::path& game, bool optiscaler, bool streamline) {
    AsideFiles out;
    if (optiscaler) out.bits |= kAsideOptiScaler;
    if (streamline) out.bits |= kAsideStreamline;
    if (game.empty()) return out;
    auto has = [&](const std::filesystem::path& rel) {
        std::error_code ec;
        if (!std::filesystem::is_regular_file(game / rel, ec)) return false;
        const auto u = rel.generic_u8string();
        out.files.emplace_back(u.begin(), u.end());
        return true;
    };
    if (has("sl.interposer.dll") | has(std::filesystem::path("Streamline") / "sl.interposer.dll")) out.bits |= kAsideStreamline;
    if (has("EnshroudedVKFG.ini")) out.bits |= kAsideVkfg;
    if (has("dlsstweaks.ini")) out.bits |= kAsideDlssTweaks;
    if (has("nvngx.dll") | has("_nvngx.dll")) out.bits |= kAsideNvngx;
    return out;
}

// x64 code that jumps away at its first instruction (jmp rel32, jmp rel8, jmp [rip+x], mov rax/r11,imm64 + jmp): a hook
// somebody else put on a function before Kiln. `b`: at least 13 bytes of the function.
inline bool patched_entry(const uint8_t* b) {
    return b[0] == 0xE9 || b[0] == 0xEB || (b[0] == 0xFF && b[1] == 0x25) ||
           (b[0] == 0x48 && b[1] == 0xB8 && b[10] == 0xFF && b[11] == 0xE0) ||
           (b[0] == 0x49 && b[1] == 0xBB && b[10] == 0x41 && b[11] == 0xFF && b[12] == 0xE3);
}

}  // namespace kui::gfx
