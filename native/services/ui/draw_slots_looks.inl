// SPDX-License-Identifier: MIT
// kiln.ui drawing slots of the looks tail (kiln_ui.h, 0.2.0-dev.29.16): a mod's theme and a mod's font, inside a frame.
// Included by draw_slots.h inside its namespace. push_theme is popped by pop_style (style.cpp's stack, balanced at the end
// of the frame); push_font_handle by pop_font (font_depth, balanced by v3_frame_end).

bool KILN_CALL t_push_theme(KilnHandle theme) {
    IN_FRAME(false);
    return kui::theme_push(theme);
}

bool KILN_CALL t_push_font_handle(KilnHandle font) {
    IN_FRAME(false);
    if (!kui::style_push_font_handle(font)) return false;
    ++font_depth();
    return true;
}
