// SPDX-License-Identifier: MIT
// kiln.ui.blocks v1 drawing slots (kiln_ui_blocks.h): controlled tabs and trees, the last item's edit state and a text
// field with Kiln's own flags, for the richer UI blocks (dossier 09). Included by draw_slots.h inside its namespace;
// plain Dear ImGui calls. Kiln's flag values are mapped to ImGui's by name, never passed through.

bool KILN_CALL t_begin_tab_item_ex(KilnStr l, uint32_t flags) {
    IN_FRAME(false);
    return ImGui::BeginTabItem(z(l), nullptr, (flags & KUI_TAB_SELECT) ? ImGuiTabItemFlags_SetSelected : ImGuiTabItemFlags_None);
}
void KILN_CALL t_set_next_item_open(bool open, uint32_t condition) {
    IN_FRAME();
    if (condition != KUI_COND_ALWAYS) return;  // v1: Always only
    ImGui::SetNextItemOpen(open, ImGuiCond_Always);
}
uint32_t KILN_CALL t_item_state() {
    IN_FRAME(0u);
    uint32_t s = 0;
    if (ImGui::IsItemActive()) s |= KUI_ITEM_ACTIVE;
    if (ImGui::IsItemDeactivated()) s |= KUI_ITEM_DEACTIVATED;
    if (ImGui::IsItemDeactivatedAfterEdit()) s |= KUI_ITEM_DEACTIVATED_AFTER_EDIT;
    if (ImGui::IsItemEdited()) s |= KUI_ITEM_EDITED;
    return s;
}
// ImGui sizes its own copy of a field's text when an editable field is activated (InputTextState::TextA = buf_size + 1),
// and bounds what it inserts by each frame's buf_size: a capacity that grows while the field has the keyboard would let
// typed bytes land past that copy. While this field is the active one, keep the capacity it was activated with; it gets
// the new one when it is activated again. A smaller capacity is ImGui's own bound. Never in a frame where ImGui sizes the
// copy again from this frame's capacity (it re-initialises the field): when the field changes between read-only and
// editable, or between one line and several, or reloads the caller's buffer. A field activated read-only keeps another
// field's copy (read-only never resizes it), so forcing that size on its first editable frame would overflow it.
size_t active_text_cap(const char* label, size_t cap, bool read_only, bool multiline) {
    const ImGuiContext& g = *GImGui;
    const ImGuiInputTextState& st = g.InputTextState;
    const ImGuiID id = ImGui::GetID(label);
    if (id == 0 || g.ActiveId != id || st.ID != id || st.TextA.Size <= 0) return cap;
    // the flags it was drawn with last (ImGui compares them, and its stb state's single_line, to decide on a re-init)
    const bool was_read_only = (st.Flags & ImGuiInputTextFlags_ReadOnly) != 0;
    const bool was_multiline = (st.Flags & ImGuiInputTextFlags_Multiline) != 0;
    if (read_only || was_read_only || was_multiline != multiline || st.WantReloadUserBuf) return cap;
    return std::min(cap, static_cast<size_t>(st.TextA.Size - 1));
}
bool KILN_CALL t_input_text_ex(KilnStr l, char* buf, size_t cap, uint32_t flags, float w, float h) {
    IN_FRAME(false);
    if (!buf || !cap) return false;
    kui::note_text(buf, strnlen(buf, cap));
    const char* label = z(l);
    cap = active_text_cap(label, cap, (flags & KUI_INPUT_READ_ONLY) != 0, (flags & KUI_INPUT_MULTILINE) != 0);
    ImGuiInputTextFlags f = ImGuiInputTextFlags_None;
    if (flags & KUI_INPUT_READ_ONLY) f |= ImGuiInputTextFlags_ReadOnly;
    if (flags & KUI_INPUT_MULTILINE) return ImGui::InputTextMultiline(label, buf, cap, ImVec2(w, h), f);
    if (flags & KUI_INPUT_ENTER_RETURNS) f |= ImGuiInputTextFlags_EnterReturnsTrue;
    if (w > 0.0f) ImGui::SetNextItemWidth(w);
    return ImGui::InputText(label, buf, cap, f);
}
