// The production drawing slot and its append-only x64 ABI, without a renderer or game.
#include "../draw_slots.h"
#include <cassert>
#include <cstdio>
namespace kui {
bool drawing = false;
bool in_frame() { return drawing; }
void style_push(uint32_t) {}
void style_pop() {}
bool style_push_font(uint32_t) { return false; }
void image_draw(KilnHandle, float, float) {}
bool image_button(const char*, KilnHandle, float, float) { return false; }
}
static_assert(offsetof(KilnUiApi, set_next_window_size_constraints) == offsetof(KilnUiApi, subscribe_key_ex) + sizeof(void*));
static_assert(offsetof(KilnUiApi, subscribe_key_ex) == 880);
static_assert(offsetof(KilnUiApi, set_next_window_size_constraints) == 888);
// menu_footer (0.2.0-dev.29.11) is appended after it; the constraints keep their offset
static_assert(offsetof(KilnUiApi, menu_footer) == offsetof(KilnUiApi, set_next_window_size_constraints) + sizeof(void*));
static_assert(offsetof(KilnUiApi, menu_footer) == 896);
// image_load_rgba (0.2.0-dev.29.15) is appended after it; nothing before it moves
static_assert(offsetof(KilnUiApi, image_load_rgba) == offsetof(KilnUiApi, menu_footer) + sizeof(void*));
static_assert(offsetof(KilnUiApi, image_load_rgba) == 904);
static_assert(offsetof(KilnUiApi, screen_target) == 912);
static_assert(offsetof(KilnUiApi, image_state) == 920);
// the looks tail (0.2.0-dev.29.16) is appended after it; nothing before it moves
static_assert(offsetof(KilnUiApi, theme_create) == offsetof(KilnUiApi, image_state) + sizeof(void*));
static_assert(offsetof(KilnUiApi, push_font_handle) == 984);
// image_ex and image_opaque_rect (0.2.0-dev.29.16) after it; nothing before them moves
static_assert(offsetof(KilnUiApi, image_ex) == offsetof(KilnUiApi, push_font_handle) + sizeof(void*));
static_assert(offsetof(KilnUiApi, image_ex) == 992);
static_assert(offsetof(KilnUiApi, image_opaque_rect) == 1000);
// set_texts (0.2.0-dev.29.16, Kiln's own texts) after them
static_assert(offsetof(KilnUiApi, set_texts) == offsetof(KilnUiApi, image_opaque_rect) + sizeof(void*));
// graphics_set and graphics_status (0.2.0-dev.29.18, Kiln's own graphics tools) after it; nothing before them moves
static_assert(offsetof(KilnUiApi, set_texts) == 1008);
static_assert(offsetof(KilnUiApi, graphics_set) == offsetof(KilnUiApi, set_texts) + sizeof(void*));
static_assert(offsetof(KilnUiApi, graphics_status) == 1024);
// chat_completion (internal to Kiln.Ui, GitHub issue 44) after them
static_assert(offsetof(KilnUiApi, chat_completion) == 1032);
// subscribe_pointer_press (0.2.0-dev.30, presses on the game's own UI) after it
static_assert(offsetof(KilnUiApi, subscribe_pointer_press) == offsetof(KilnUiApi, chat_completion) + sizeof(void*));
static_assert(offsetof(KilnUiApi, subscribe_pointer_press) == 1040);
static_assert(offsetof(KilnUiApi, chat_search) == offsetof(KilnUiApi, subscribe_pointer_press) + sizeof(void*));
static_assert(offsetof(KilnUiApi, chat_search) == 1048);
static_assert(sizeof(KilnUiApi) == offsetof(KilnUiApi, chat_search) + sizeof(void*));
int main() {
    // Drawing off: no frame/context, just as KILN_UI_OVERLAY=0. Must not enter ImGui.
    kui_draw::t_set_next_window_size_constraints(300, 0, 300, 918);
    auto* ctx = ImGui::CreateContext();
    kui::drawing = true;
    kui_draw::t_set_next_window_size_constraints(300, 0, 300, 918);
    assert(ctx->NextWindowData.HasFlags & ImGuiNextWindowDataFlags_HasSizeConstraint);
    auto r = ctx->NextWindowData.SizeConstraintRect;
    assert(r.Min.x == 300 && r.Min.y == 0 && r.Max.x == 300 && r.Max.y == 918);
    kui::drawing = false;
    kui_draw::t_set_next_window_size_constraints(100, 0, 100, 100);
    assert(ctx->NextWindowData.SizeConstraintRect.Max.y == 918);
    ImGui::DestroyContext();
    std::printf("window_constraints: 4 checks passed; offset=%zu size=%zu\n",
                offsetof(KilnUiApi, set_next_window_size_constraints), sizeof(KilnUiApi));
}
