// SPDX-License-Identifier: MIT
// kiln.ui's drawing slots that are plain Dear ImGui calls (native/core/abi/services/kiln_ui.h): shared by the service
// table (ui.cpp) and the headless preview shim (tools/ui-preview/shim.cpp), so the preview draws with the same code
// the game does. Every slot does nothing (returns false / zeros) outside a frame callback (kui::in_frame()).
#pragma once
#include <algorithm>
#include <cfloat>
#include <cstdint>
#include <cstring>
#include <string>

#include "imgui.h"
#include "imgui_internal.h"
#include "kiln_api.h"
#include "services/kiln_ui.h"
#include "slider.h"

namespace kui {
bool in_frame();
void style_push(uint32_t style);
void style_pop();
void image_draw(KilnHandle h, float w, float hgt);
bool image_button(const char* id, KilnHandle h, float w, float hgt);
}  // namespace kui

namespace kui_draw {
namespace {  // internal linkage: one copy per table that includes this

#define IN_FRAME(ret) \
    if (!kui::in_frame()) return ret

// KilnStr -> NUL-terminated (labels); a few rotating buffers per thread.
const char* z(KilnStr s) {
    thread_local std::string buf[8];
    thread_local int i = 0;
    std::string& b = buf[i++ & 7];
    b.assign(s.ptr ? s.ptr : "", s.ptr ? s.len : 0);
    return b.c_str();
}

ImU32 col(uint32_t rgba) {
    return IM_COL32((rgba >> 24) & 0xFF, (rgba >> 16) & 0xFF, (rgba >> 8) & 0xFF, rgba & 0xFF);
}
ImVec4 col4(uint32_t rgba) {
    return ImVec4(((rgba >> 24) & 0xFF) / 255.f, ((rgba >> 16) & 0xFF) / 255.f, ((rgba >> 8) & 0xFF) / 255.f, (rgba & 0xFF) / 255.f);
}



bool KILN_CALL t_begin(KilnStr title, bool* open, uint32_t flags) {
    IN_FRAME(false);
    return ImGui::Begin(z(title), open, static_cast<ImGuiWindowFlags>(flags));
}
void KILN_CALL t_end() { IN_FRAME(); ImGui::End(); }
void KILN_CALL t_text(KilnStr s) { IN_FRAME(); ImGui::TextUnformatted(s.ptr, s.ptr + s.len); }
void KILN_CALL t_text_colored(uint32_t rgba, KilnStr s) {
    IN_FRAME();
    ImGui::PushStyleColor(ImGuiCol_Text, col4(rgba));
    ImGui::TextUnformatted(s.ptr, s.ptr + s.len);
    ImGui::PopStyleColor();
}
void KILN_CALL t_text_wrapped(KilnStr s) {
    IN_FRAME();
    ImGui::PushTextWrapPos(0.0f);
    ImGui::TextUnformatted(s.ptr, s.ptr + s.len);
    ImGui::PopTextWrapPos();
}
void KILN_CALL t_text_disabled(KilnStr s) {
    IN_FRAME();
    ImGui::PushStyleColor(ImGuiCol_Text, ImGui::GetStyle().Colors[ImGuiCol_TextDisabled]);
    ImGui::TextUnformatted(s.ptr, s.ptr + s.len);
    ImGui::PopStyleColor();
}
bool KILN_CALL t_button(KilnStr l, float w, float h) { IN_FRAME(false); return ImGui::Button(z(l), ImVec2(w, h)); }
bool KILN_CALL t_small_button(KilnStr l) { IN_FRAME(false); return ImGui::SmallButton(z(l)); }
bool KILN_CALL t_checkbox(KilnStr l, bool* v) { IN_FRAME(false); return v && ImGui::Checkbox(z(l), v); }
bool KILN_CALL t_slider_float(KilnStr l, float* v, float a, float b) { IN_FRAME(false); return v && kui::slider_float(z(l), v, a, b); }
bool KILN_CALL t_slider_int(KilnStr l, int32_t* v, int32_t a, int32_t b) { IN_FRAME(false); return v && ImGui::SliderInt(z(l), v, a, b); }
bool KILN_CALL t_input_text(KilnStr l, char* buf, size_t cap, uint32_t flags) {
    IN_FRAME(false);
    return buf && cap && ImGui::InputText(z(l), buf, cap, static_cast<ImGuiInputTextFlags>(flags));
}
bool KILN_CALL t_collapsing_header(KilnStr l, uint32_t flags) { IN_FRAME(false); return ImGui::CollapsingHeader(z(l), static_cast<ImGuiTreeNodeFlags>(flags)); }
bool KILN_CALL t_tree_node(KilnStr l) { IN_FRAME(false); return ImGui::TreeNode(z(l)); }
void KILN_CALL t_tree_pop() { IN_FRAME(); ImGui::TreePop(); }
bool KILN_CALL t_selectable(KilnStr l, bool sel) { IN_FRAME(false); return ImGui::Selectable(z(l), sel); }
void KILN_CALL t_separator() { IN_FRAME(); ImGui::Separator(); }
void KILN_CALL t_separator_text(KilnStr s) { IN_FRAME(); ImGui::SeparatorText(z(s)); }
void KILN_CALL t_same_line(float off, float sp) { IN_FRAME(); ImGui::SameLine(off, sp); }
void KILN_CALL t_spacing() { IN_FRAME(); ImGui::Spacing(); }
void KILN_CALL t_push_id(KilnStr id) { IN_FRAME(); ImGui::PushID(id.ptr, id.ptr + id.len); }
void KILN_CALL t_pop_id() { IN_FRAME(); ImGui::PopID(); }
void KILN_CALL t_set_next_window_pos(float x, float y, uint32_t cond, float px, float py) {
    IN_FRAME();
    ImGui::SetNextWindowPos(ImVec2(x, y), static_cast<ImGuiCond>(cond), ImVec2(px, py));
}
void KILN_CALL t_set_next_window_size(float w, float h, uint32_t cond) { IN_FRAME(); ImGui::SetNextWindowSize(ImVec2(w, h), static_cast<ImGuiCond>(cond)); }
void KILN_CALL t_set_next_window_bg_alpha(float a) { IN_FRAME(); ImGui::SetNextWindowBgAlpha(a); }
void KILN_CALL t_progress_bar(float f, float w, float h, KilnStr overlay) {
    IN_FRAME();
    ImGui::ProgressBar(f, ImVec2(w, h), overlay.len ? z(overlay) : nullptr);
}
bool KILN_CALL t_begin_table(KilnStr id, int32_t cols, uint32_t flags) {
    IN_FRAME(false);
    return cols > 0 && cols <= 64 && ImGui::BeginTable(z(id), cols, static_cast<ImGuiTableFlags>(flags));
}
void KILN_CALL t_table_setup_column(KilnStr l, uint32_t flags, float w) { IN_FRAME(); ImGui::TableSetupColumn(z(l), static_cast<ImGuiTableColumnFlags>(flags), w); }
void KILN_CALL t_table_headers_row() { IN_FRAME(); ImGui::TableHeadersRow(); }
void KILN_CALL t_table_next_row() { IN_FRAME(); ImGui::TableNextRow(); }
bool KILN_CALL t_table_next_column() { IN_FRAME(false); return ImGui::TableNextColumn(); }
void KILN_CALL t_end_table() { IN_FRAME(); ImGui::EndTable(); }
bool KILN_CALL t_begin_tab_bar(KilnStr id) { IN_FRAME(false); return ImGui::BeginTabBar(z(id)); }
bool KILN_CALL t_begin_tab_item(KilnStr l) { IN_FRAME(false); return ImGui::BeginTabItem(z(l)); }
void KILN_CALL t_end_tab_item() { IN_FRAME(); ImGui::EndTabItem(); }
void KILN_CALL t_end_tab_bar() { IN_FRAME(); ImGui::EndTabBar(); }
bool KILN_CALL t_begin_child(KilnStr id, float w, float h, uint32_t flags) {
    IN_FRAME(false);
    return ImGui::BeginChild(z(id), ImVec2(w, h), ImGuiChildFlags_Borders, static_cast<ImGuiWindowFlags>(flags));
}
void KILN_CALL t_end_child() { IN_FRAME(); ImGui::EndChild(); }
bool KILN_CALL t_is_item_hovered() { IN_FRAME(false); return ImGui::IsItemHovered(); }
void KILN_CALL t_set_tooltip(KilnStr s) { IN_FRAME(); ImGui::SetTooltip("%s", z(s)); }
void KILN_CALL t_draw_text(float x, float y, uint32_t rgba, float size, KilnStr s) {
    IN_FRAME();
    ImDrawList* dl = ImGui::GetForegroundDrawList();
    const float fs = size > 0 ? size : ImGui::GetFontSize();
    dl->AddText(ImGui::GetFont(), fs, ImVec2(x + 1, y + 1), IM_COL32(0, 0, 0, (rgba & 0xFF) * 3 / 4), s.ptr, s.ptr + s.len);
    dl->AddText(ImGui::GetFont(), fs, ImVec2(x, y), col(rgba), s.ptr, s.ptr + s.len);
}
void KILN_CALL t_draw_rect(float x0, float y0, float x1, float y1, uint32_t rgba, float rounding, int32_t filled) {
    IN_FRAME();
    ImDrawList* dl = ImGui::GetForegroundDrawList();
    if (filled) dl->AddRectFilled(ImVec2(x0, y0), ImVec2(x1, y1), col(rgba), rounding);
    else dl->AddRect(ImVec2(x0, y0), ImVec2(x1, y1), col(rgba), rounding);
}
void KILN_CALL t_text_size(KilnStr s, float size, float* w, float* h) {
    if (!kui::in_frame()) {
        if (w) *w = 0;
        if (h) *h = 0;
        return;
    }
    const float fs = size > 0 ? size : ImGui::GetFontSize();
    const ImVec2 v = ImGui::GetFont()->CalcTextSizeA(fs, FLT_MAX, 0.0f, s.ptr, s.ptr + s.len);
    if (w) *w = v.x;
    if (h) *h = v.y;
}

void KILN_CALL t_push_style(uint32_t s) { IN_FRAME(); kui::style_push(s); }
void KILN_CALL t_pop_style() { IN_FRAME(); kui::style_pop(); }
void KILN_CALL t_image(KilnHandle h, float w, float hh) { IN_FRAME(); kui::image_draw(h, w, hh); }
bool KILN_CALL t_image_button(KilnStr id, KilnHandle h, float w, float hh) { IN_FRAME(false); return kui::image_button(z(id), h, w, hh); }
void KILN_CALL t_set_next_window_focus() { IN_FRAME(); ImGui::SetNextWindowFocus(); }
bool KILN_CALL t_combo(KilnStr l, int32_t* index, const char* items) {
    IN_FRAME(false);
    return index && items && ImGui::Combo(z(l), index, items);
}
bool KILN_CALL t_input_multiline(KilnStr l, char* buf, size_t cap, float w, float h) {
    IN_FRAME(false);
    return buf && cap && ImGui::InputTextMultiline(z(l), buf, cap, ImVec2(w, h));
}
void KILN_CALL t_dummy(float w, float h) { IN_FRAME(); ImGui::Dummy(ImVec2(w, h)); }
void KILN_CALL t_window_rect(float* x, float* y, float* w, float* h) {
    ImVec2 p(0, 0), s(0, 0);
    if (kui::in_frame()) {
        p = ImGui::GetWindowPos();
        s = ImGui::GetWindowSize();
    }
    if (x) *x = p.x;
    if (y) *y = p.y;
    if (w) *w = s.x;
    if (h) *h = s.y;
}
bool KILN_CALL t_is_item_clicked(uint32_t button) {
    IN_FRAME(false);
    return button < 3 && ImGui::IsItemClicked(static_cast<ImGuiMouseButton>(button));
}

#include "draw_slots_v3.inl"

}  // namespace
}  // namespace kui_draw
