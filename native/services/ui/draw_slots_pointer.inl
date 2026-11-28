// SPDX-License-Identifier: MIT
// kiln.ui's pointer and drag and drop slots (0.2.0-dev.30.3, GitHub issue 113): the pointer and the held mouse buttons from
// ImGui's io, and ImGui's drag and drop after a widget. Included by draw_slots.h inside its anonymous namespace (the
// service table and the preview shim share them). Frame callbacks only, like every drawing slot.

// A kind as drag_source / drop_target take it: 1..KILN_UI_DRAG_KIND_MAX bytes, no NUL inside, not ImGui's own ("_...").
// out gets it NUL-terminated (ImGui compares payload types as C strings).
bool drag_kind(KilnStr kind, char (&out)[KILN_UI_DRAG_KIND_MAX + 1]) {
    if (!kind.ptr || kind.len == 0 || kind.len > KILN_UI_DRAG_KIND_MAX || kind.ptr[0] == '_') return false;
    if (std::memchr(kind.ptr, 0, kind.len)) return false;
    std::memcpy(out, kind.ptr, kind.len);
    out[kind.len] = 0;
    return true;
}

bool KILN_CALL t_pointer(KilnUiPointer* out) {
    if (!out || out->size < sizeof(KilnUiPointer)) return false;
    if (!kui::in_frame()) return false;
    const ImGuiIO& io = ImGui::GetIO();
    KilnUiPointer p{};
    p.size = out->size;
    if (ImGui::IsMousePosValid(&io.MousePos)) {
        p.flags |= KILN_UI_POINTER_VALID;
        p.x = io.MousePos.x;
        p.y = io.MousePos.y;
    }
    if (ImGui::GetDragDropPayload() != nullptr) p.flags |= KILN_UI_POINTER_DRAGGING;
    for (int b = 0; b < 3; ++b)
        if (io.MouseDown[b]) p.held |= 1u << b;
    std::memcpy(out, &p, sizeof p);  // a larger record (a newer caller) keeps its tail
    return true;
}

bool KILN_CALL t_drag_source(KilnStr kind, const void* payload, size_t len, KilnStr preview) {
    IN_FRAME(false);
    char type[KILN_UI_DRAG_KIND_MAX + 1];
    if (!drag_kind(kind, type) || len > KILN_UI_DRAG_PAYLOAD_MAX || (len && !payload)) return false;
    const bool label = preview.ptr && preview.len;
    // SourceAllowNullID: text and images have no id; ImGui makes one from the widget's rectangle
    const ImGuiDragDropFlags flags = ImGuiDragDropFlags_SourceAllowNullID | (label ? 0 : ImGuiDragDropFlags_SourceNoPreviewTooltip);
    if (!ImGui::BeginDragDropSource(flags)) return false;
    ImGui::SetDragDropPayload(type, len ? payload : nullptr, len, ImGuiCond_Always);  // ImGui: an empty payload is NULL, 0
    if (label) {
        kui::note_text(preview.ptr, preview.len);
        ImGui::TextUnformatted(preview.ptr, preview.ptr + preview.len);
    }
    ImGui::EndDragDropSource();
    return true;
}

bool KILN_CALL t_drop_target(KilnStr kind, void* out, size_t capacity, size_t* len) {
    IN_FRAME(false);
    char type[KILN_UI_DRAG_KIND_MAX + 1];
    if (!drag_kind(kind, type) || (capacity && !out)) return false;
    if (!ImGui::BeginDragDropTarget()) return false;
    bool got = false;
    if (const ImGuiPayload* p = ImGui::AcceptDragDropPayload(type)) {  // delivered on release (no AcceptBeforeDelivery)
        const size_t n = p->DataSize > 0 ? static_cast<size_t>(p->DataSize) : 0;
        if (n && capacity) std::memcpy(out, p->Data, std::min(n, capacity));
        if (len) *len = n;
        got = true;
    }
    ImGui::EndDragDropTarget();
    return got;
}
