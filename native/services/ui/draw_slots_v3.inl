// SPDX-License-Identifier: MIT
// kiln.ui v3 drawing slots (kiln_ui.h "v3"): font sizes, child regions with flags, cursor and item queries, sized
// selectables, focus, layered drawing, colour and spacing pushes, key queries. Included by draw_slots.h inside its
// namespace; plain Dear ImGui calls.

// Font-size pushes made through push_font_size, so pop_font_size never pops a font Kiln's style stack pushed.
inline int& font_size_depth() {
    thread_local int depth = 0;
    return depth;
}
inline int& color_depth() {
    thread_local int depth = 0;
    return depth;
}
inline int& var_depth() {
    thread_local int depth = 0;
    return depth;
}
inline int& font_depth() {  // v4 push_font (draw_slots_v4.inl)
    thread_local int depth = 0;
    return depth;
}

// End of frame (ui.cpp, the preview): pushes a callback left open.
inline void v3_frame_end() {
    while (font_size_depth() > 0) {
        ImGui::PopFont();
        --font_size_depth();
    }
    while (font_depth() > 0) {
        ImGui::PopFont();
        --font_depth();
    }
    if (color_depth() > 0) ImGui::PopStyleColor(color_depth());
    if (var_depth() > 0) ImGui::PopStyleVar(var_depth());
    color_depth() = var_depth() = 0;
}

void KILN_CALL t_push_font_size(float size) {
    IN_FRAME();
    if (!(size >= 0.0f) || size > 512.0f) size = 0.0f;  // NaN, negative or absurd: the default
    ImGui::PushFont(nullptr, size > 0.0f ? size : ImGui::GetStyle().FontSizeBase);
    ++font_size_depth();
}
void KILN_CALL t_pop_font_size() {
    IN_FRAME();
    if (font_size_depth() <= 0) return;
    ImGui::PopFont();
    --font_size_depth();
}

bool KILN_CALL t_begin_child_ex(KilnStr id, float w, float h, uint32_t child_flags, uint32_t window_flags) {
    IN_FRAME(false);
    constexpr uint32_t kChild = KILN_UI_CHILD_BORDERS | KILN_UI_CHILD_ALWAYS_PADDING | KILN_UI_CHILD_AUTO_RESIZE_Y |
                                KILN_UI_CHILD_FRAME_STYLE | KILN_UI_CHILD_NAV_FLATTENED;
    return ImGui::BeginChild(z(id), ImVec2(w, h), static_cast<ImGuiChildFlags>(child_flags & kChild),
                             static_cast<ImGuiWindowFlags>(window_flags));
}

void KILN_CALL t_set_next_item_width(float w) {
    IN_FRAME();
    ImGui::SetNextItemWidth(w);
}

void KILN_CALL t_cursor(KilnUiCursor* out) {
    if (!out || out->size < sizeof(KilnUiCursor)) return;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    if (!kui::in_frame()) return;
    const ImVec2 c = ImGui::GetCursorPos(), sc = ImGui::GetCursorScreenPos(), av = ImGui::GetContentRegionAvail();
    const ImVec2 wp = ImGui::GetWindowPos(), ws = ImGui::GetWindowSize();
    out->x = c.x;
    out->y = c.y;
    out->screen_x = sc.x;
    out->screen_y = sc.y;
    out->avail_width = av.x;
    out->avail_height = av.y;
    out->window_x = wp.x;
    out->window_y = wp.y;
    out->window_width = ws.x;
    out->window_height = ws.y;
    out->font_size = ImGui::GetFontSize();
    out->line_height = ImGui::GetTextLineHeightWithSpacing();
    out->scroll_y = ImGui::GetScrollY();
    out->scroll_max_y = ImGui::GetScrollMaxY();
}

void KILN_CALL t_set_cursor_pos(float x, float y) {
    IN_FRAME();
    ImGui::SetCursorPos(ImVec2(x, y));
}

void KILN_CALL t_item(KilnUiItem* out) {
    if (!out || out->size < sizeof(KilnUiItem)) return;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    if (!kui::in_frame()) return;
    const ImVec2 a = ImGui::GetItemRectMin(), b = ImGui::GetItemRectMax();
    out->min_x = a.x;
    out->min_y = a.y;
    out->max_x = b.x;
    out->max_y = b.y;
    out->hovered = ImGui::IsItemHovered() ? 1 : 0;
    out->active = ImGui::IsItemActive() ? 1 : 0;
    out->focused = ImGui::IsItemFocused() ? 1 : 0;
    out->clicked = ImGui::IsItemClicked(ImGuiMouseButton_Left) ? 1 : 0;
    out->visible = ImGui::IsItemVisible() ? 1 : 0;
    out->edited = ImGui::IsItemEdited() ? 1 : 0;
    out->activated = ImGui::IsItemActivated() ? 1 : 0;
    out->deactivated = ImGui::IsItemDeactivated() ? 1 : 0;
}

bool KILN_CALL t_selectable_ex(KilnStr label, bool selected, uint32_t flags, float w, float h) {
    IN_FRAME(false);
    constexpr uint32_t kFlags = KILN_UI_SELECTABLE_SPAN_ALL_COLUMNS | KILN_UI_SELECTABLE_ALLOW_DOUBLE_CLICK |
                                KILN_UI_SELECTABLE_DISABLED | KILN_UI_SELECTABLE_ALLOW_OVERLAP | KILN_UI_SELECTABLE_HIGHLIGHT;
    return ImGui::Selectable(z(label), selected, static_cast<ImGuiSelectableFlags>(flags & kFlags), ImVec2(w, h));
}

void KILN_CALL t_set_keyboard_focus_here(int32_t offset) {
    IN_FRAME();
    ImGui::SetKeyboardFocusHere(offset < 0 ? 0 : offset);
}
void KILN_CALL t_set_item_default_focus() {
    IN_FRAME();
    ImGui::SetItemDefaultFocus();
}

inline ImDrawList* layer_list(uint32_t layer) {
    switch (layer) {
        case KILN_UI_LAYER_BACKGROUND: return ImGui::GetBackgroundDrawList();
        case KILN_UI_LAYER_FOREGROUND: return ImGui::GetForegroundDrawList();
        default: return ImGui::GetWindowDrawList();
    }
}

void KILN_CALL t_draw_rect_in(uint32_t layer, float x0, float y0, float x1, float y1, uint32_t rgba, float rounding, float thickness) {
    IN_FRAME();
    ImDrawList* dl = layer_list(layer);
    if (thickness <= 0.0f) dl->AddRectFilled(ImVec2(x0, y0), ImVec2(x1, y1), col(rgba), rounding);
    else dl->AddRect(ImVec2(x0, y0), ImVec2(x1, y1), col(rgba), rounding, 0, thickness);
}

void KILN_CALL t_draw_text_in(uint32_t layer, float x, float y, uint32_t rgba, float size, float wrap, KilnStr s) {
    IN_FRAME();
    if (!s.ptr || !s.len) return;
    const float fs = size > 0 ? size : ImGui::GetFontSize();
    layer_list(layer)->AddText(ImGui::GetFont(), fs, ImVec2(x, y), col(rgba), s.ptr, s.ptr + s.len, wrap > 0 ? wrap : 0.0f);
}

void KILN_CALL t_text_size_ex(KilnStr s, float size, float wrap, float* w, float* h) {
    if (w) *w = 0;
    if (h) *h = 0;
    if (!kui::in_frame() || !s.ptr) return;
    const float fs = size > 0 ? size : ImGui::GetFontSize();
    const ImVec2 v = ImGui::GetFont()->CalcTextSizeA(fs, FLT_MAX, wrap > 0 ? wrap : 0.0f, s.ptr, s.ptr + s.len);
    if (w) *w = v.x;
    if (h) *h = v.y;
}

inline int color_index(uint32_t slot) {
    static const ImGuiCol map[KILN_UI_COLOR_COUNT] = {
        ImGuiCol_Text,          ImGuiCol_TextDisabled,   ImGuiCol_WindowBg,      ImGuiCol_ChildBg,        ImGuiCol_Border,
        ImGuiCol_FrameBg,       ImGuiCol_FrameBgHovered, ImGuiCol_FrameBgActive, ImGuiCol_Button,         ImGuiCol_ButtonHovered,
        ImGuiCol_ButtonActive,  ImGuiCol_Header,         ImGuiCol_HeaderHovered, ImGuiCol_HeaderActive,   ImGuiCol_Separator,
        ImGuiCol_ScrollbarBg,   ImGuiCol_ScrollbarGrab,  ImGuiCol_NavCursor,     ImGuiCol_CheckMark,      ImGuiCol_SliderGrab,
    };
    return slot < KILN_UI_COLOR_COUNT ? map[slot] : -1;
}

void KILN_CALL t_push_color(uint32_t slot, uint32_t rgba) {
    IN_FRAME();
    const int i = color_index(slot);
    if (i < 0) return;
    ImGui::PushStyleColor(i, col4(rgba));
    ++color_depth();
}
void KILN_CALL t_pop_color(uint32_t n) {
    IN_FRAME();
    const int k = static_cast<int>(std::min<uint32_t>(n, static_cast<uint32_t>(color_depth())));
    if (k <= 0) return;
    ImGui::PopStyleColor(k);
    color_depth() -= k;
}

void KILN_CALL t_push_var(uint32_t var, float x, float y) {
    IN_FRAME();
    if (!(x == x) || !(y == y)) return;  // NaN
    switch (var) {
        case KILN_UI_VAR_WINDOW_PADDING: ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(x, y)); break;
        case KILN_UI_VAR_FRAME_PADDING: ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(x, y)); break;
        case KILN_UI_VAR_ITEM_SPACING: ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(x, y)); break;
        case KILN_UI_VAR_ITEM_INNER_SPACING: ImGui::PushStyleVar(ImGuiStyleVar_ItemInnerSpacing, ImVec2(x, y)); break;
        case KILN_UI_VAR_FRAME_ROUNDING: ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, x); break;
        case KILN_UI_VAR_FRAME_BORDER_SIZE: ImGui::PushStyleVar(ImGuiStyleVar_FrameBorderSize, x); break;
        case KILN_UI_VAR_CHILD_ROUNDING: ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, x); break;
        case KILN_UI_VAR_CHILD_BORDER_SIZE: ImGui::PushStyleVar(ImGuiStyleVar_ChildBorderSize, x); break;
        case KILN_UI_VAR_SELECTABLE_TEXT_ALIGN: ImGui::PushStyleVar(ImGuiStyleVar_SelectableTextAlign, ImVec2(x, y)); break;
        case KILN_UI_VAR_BUTTON_TEXT_ALIGN: ImGui::PushStyleVar(ImGuiStyleVar_ButtonTextAlign, ImVec2(x, y)); break;
        case KILN_UI_VAR_SCROLLBAR_SIZE: ImGui::PushStyleVar(ImGuiStyleVar_ScrollbarSize, x); break;
        case KILN_UI_VAR_WINDOW_BORDER_SIZE: ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, x); break;
        default: return;
    }
    ++var_depth();
}
void KILN_CALL t_pop_var(uint32_t n) {
    IN_FRAME();
    const int k = static_cast<int>(std::min<uint32_t>(n, static_cast<uint32_t>(var_depth())));
    if (k <= 0) return;
    ImGui::PopStyleVar(k);
    var_depth() -= k;
}

inline ImGuiKey key_of(uint32_t k) {
    switch (k) {
        case KILN_UI_KEY_TAB: return ImGuiKey_Tab;
        case KILN_UI_KEY_LEFT: return ImGuiKey_LeftArrow;
        case KILN_UI_KEY_RIGHT: return ImGuiKey_RightArrow;
        case KILN_UI_KEY_UP: return ImGuiKey_UpArrow;
        case KILN_UI_KEY_DOWN: return ImGuiKey_DownArrow;
        case KILN_UI_KEY_PAGE_UP: return ImGuiKey_PageUp;
        case KILN_UI_KEY_PAGE_DOWN: return ImGuiKey_PageDown;
        case KILN_UI_KEY_HOME: return ImGuiKey_Home;
        case KILN_UI_KEY_END: return ImGuiKey_End;
        case KILN_UI_KEY_ENTER: return ImGuiKey_Enter;
        case KILN_UI_KEY_ESCAPE: return ImGuiKey_Escape;
        case KILN_UI_KEY_BACKSPACE: return ImGuiKey_Backspace;
        case KILN_UI_KEY_F: return ImGuiKey_F;
        case KILN_UI_KEY_GAMEPAD_LB: return ImGuiKey_GamepadL1;
        case KILN_UI_KEY_GAMEPAD_RB: return ImGuiKey_GamepadR1;
        case KILN_UI_KEY_GAMEPAD_X: return ImGuiKey_GamepadFaceLeft;
        case KILN_UI_KEY_GAMEPAD_Y: return ImGuiKey_GamepadFaceUp;
        case KILN_UI_KEY_GAMEPAD_START: return ImGuiKey_GamepadStart;
        case KILN_UI_KEY_GAMEPAD_BACK: return ImGuiKey_GamepadBack;
        default: return ImGuiKey_None;
    }
}

bool KILN_CALL t_key_pressed(uint32_t key, uint32_t flags) {
    IN_FRAME(false);
    const ImGuiKey k = key_of(key);
    if (k == ImGuiKey_None) return false;
    const ImGuiInputFlags repeat = (flags & KILN_UI_KEYQ_REPEAT) ? ImGuiInputFlags_Repeat : ImGuiInputFlags_None;
    if (flags & KILN_UI_KEYQ_CLAIM) {
        // Owned keys are invisible to ImGui's own navigation (it asks with ImGuiKeyOwner_NoOwner); the owner carries
        // over to the next frame's NewFrame, so a key claimed every frame never moves ImGui's focus.
        const ImGuiID owner = ImHashStr("kiln.ui.key_claim");
        ImGui::SetKeyOwner(k, owner);
        return ImGui::IsKeyPressed(k, repeat, owner);
    }
    return ImGui::IsKeyPressed(k, repeat, ImGuiKeyOwner_Any);
}

uint32_t KILN_CALL t_key_mods() {
    IN_FRAME(0);
    const ImGuiIO& io = ImGui::GetIO();
    return (io.KeyCtrl ? uint32_t{KILN_UI_MOD_CTRL} : 0u) | (io.KeyShift ? uint32_t{KILN_UI_MOD_SHIFT} : 0u) | (io.KeyAlt ? uint32_t{KILN_UI_MOD_ALT} : 0u);
}

void KILN_CALL t_set_scroll_here_y(float ratio) {
    IN_FRAME();
    ImGui::SetScrollHereY(ratio < 0 ? 0 : ratio > 1 ? 1 : ratio);
}
