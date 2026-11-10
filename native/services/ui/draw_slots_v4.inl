// SPDX-License-Identifier: MIT
// kiln.ui v4 drawing slots (kiln_ui.h "v4"): a font choice (the game's bold face for page titles). Included by
// draw_slots.h inside its namespace. v3_frame_end pops what a callback left pushed (font_depth).

bool KILN_CALL t_push_font(uint32_t font) {
    IN_FRAME(false);
    if (!kui::style_push_font(font)) return false;
    ++font_depth();
    return true;
}
void KILN_CALL t_pop_font() {
    IN_FRAME();
    if (font_depth() <= 0) return;
    ImGui::PopFont();
    --font_depth();
}
