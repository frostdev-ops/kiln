// SPDX-License-Identifier: MIT
// kiln.ui: text the overlay is asked to draw that needs a Chinese, Japanese or Korean font while neither Kiln's language nor
// the game's is one, such as a mod's own labels (HTR, Discord 2026-11-27: Mineral Production Boxes, Nexus 347, has Chinese
// ShroudForge settings, and Kiln's Mods page drew them as ????). The drawing slots note every text they get (draw_slots.h,
// render.cpp); style.cpp's group_wanted then loads the game's zh-CN font group (Chinese, kana, Hangul) into the overlay's
// atlas, for the rest of the process. The first frames of such a text still show ? until the group is read.
#pragma once
#include <atomic>
#include <cstddef>

#include "font_group.h"

namespace kui {

inline std::atomic<bool> g_text_cjk{false};  // some text drawn so far needed a CJK group (fontgroup::cjk_text)

inline void note_text(const char* s, size_t n) {
    if (s && n && !g_text_cjk.load(std::memory_order_relaxed) && fontgroup::cjk_text(s, n)) g_text_cjk.store(true, std::memory_order_relaxed);
}

}  // namespace kui
