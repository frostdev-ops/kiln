// SPDX-License-Identifier: MIT
// Runs the real radial wheel (wheel.cpp) on a headless Dear ImGui context, with OS stand-ins, under ASan/UBSan: the open
// refusals, a pick by click, cancel by Esc and by right click, the hold key (a flick, a hold, a tap), paging with the mouse
// wheel, wheel_close (also against an ending frame), the overlay turning off, a callback that opens another wheel, that windows below get no pointer, and that drawing makes
// geometry without tripping ImGui's asserts.
#include <cassert>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "../wheel.cpp"

#undef NDEBUG
namespace kui {
std::atomic<uint32_t> g_interactive{0};
std::atomic<bool> g_menu_open{false};
static uint64_t g_now = 1000;
static bool g_attached = true, g_swapchain = true;
static bool g_held[256];
static std::vector<std::string> g_errors;
uint64_t now_ms() { return g_now; }
void logf(int, const char*, ...) {}
void set_error(const char* fmt, ...) { g_errors.push_back(fmt); }
KilnHandle next_handle() { static std::atomic<KilnHandle> h{1}; return h++; }
bool input_attached() { return g_attached; }
bool input_key_held(uint32_t vk) { return vk < 256 && g_held[vk]; }
void key_name(uint32_t vk, char* out, size_t cap) { std::snprintf(out, cap, "Key%u", vk); }
void vk_status(KilnUiStatus* out) {
    out->width = 1920;
    out->height = 1080;
    out->dpi_scale = 1.0f;
    out->swapchain = g_swapchain ? 1 : 0;
}
}  // namespace kui

static int g_fail = 0, g_checks = 0;
#define CHECK(cond)                                                     \
    do {                                                                \
        ++g_checks;                                                     \
        if (!(cond)) {                                                  \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
            ++g_fail;                                                   \
        }                                                               \
    } while (0)

static const float W = 1920, H = 1080, CX = W / 2, CY = H / 2;
static std::vector<int> g_results;
static void KILN_CALL on_result(int32_t choice, void*) { g_results.push_back(choice); }

// one overlay frame the way ui.cpp builds it: NewFrame, the wheel, Render
static int g_vertices = 0;
static void frame() {
    kui::g_now += 16;
    ImGui::NewFrame();
    if (kui::wheel_active()) kui::wheel_frame(W, H, 1.0f);
    ImGui::Render();
    g_vertices = ImGui::GetDrawData()->TotalVtxCount;
}
static void at(float deg, float r) {
    float dx, dy;
    kwheel::polar(deg * kwheel::kPi / 180.0f, r, &dx, &dy);
    ImGui::GetIO().AddMousePosEvent(CX + dx, CY + dy);
}
static void click() {
    ImGuiIO& io = ImGui::GetIO();
    io.AddMouseButtonEvent(0, true);
    frame();
    io.AddMouseButtonEvent(0, false);
}
static KilnUiKey key(uint32_t vk, bool down) {
    KilnUiKey k{};
    k.size = sizeof k;
    k.vk = vk;
    k.down = down ? 1 : 0;
    return k;
}
static KilnStr S(const char* s) { return KilnStr{s, std::strlen(s)}; }

static KilnUiWheel def(const KilnStr* items, uint32_t n, uint32_t hold = 0, const char* title = "Pets") {
    KilnUiWheel d{};
    d.size = sizeof d;
    d.title = S(title);
    d.items = items;
    d.count = n;
    d.hold_key = hold;
    return d;
}

int main() {
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.DisplaySize = ImVec2(W, H);
    unsigned char* pixels = nullptr;
    int fw = 0, fh = 0;
    io.Fonts->AddFontDefault();
    io.Fonts->GetTexDataAsRGBA32(&pixels, &fw, &fh);  // the legacy path: no backend to upload textures
    const KilnStr three[] = {S("Wolf"), S("Bear (level 3)"), S("Fox")};
    KilnHandle h = 0;

    // ---- refusals ----
    KilnUiWheel d = def(three, 3);
    CHECK(kui::wheel_open(1, &d, nullptr, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(kui::wheel_open(1, nullptr, on_result, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT);
    KilnUiWheel bad = d;
    bad.count = 0;
    CHECK(kui::wheel_open(1, &bad, on_result, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT);
    bad.count = 65;
    CHECK(kui::wheel_open(1, &bad, on_result, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT);
    bad = d;
    bad.items = nullptr;
    CHECK(kui::wheel_open(1, &bad, on_result, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT);
    for (uint32_t vk : {1u, 2u, 4u, 5u, 6u, 0xA0u, 0xA5u, 0x100u}) {
        bad = d;
        bad.hold_key = vk;
        CHECK(kui::wheel_open(1, &bad, on_result, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT);
    }
    bad = d;
    bad.flags = 1;
    CHECK(kui::wheel_open(1, &bad, on_result, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT);
    bad = d;
    bad.size = 8;
    CHECK(kui::wheel_open(1, &bad, on_result, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT);
    const KilnStr broken[] = {KilnStr{nullptr, 4}};
    bad = def(broken, 1);
    CHECK(kui::wheel_open(1, &bad, on_result, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(!kui::wheel_active());
    kui::g_menu_open = true;
    CHECK(kui::wheel_open(1, &d, on_result, nullptr, &h) == KILN_ERR_UNAVAILABLE && !kui::wheel_active());
    kui::g_menu_open = false;
    kui::g_attached = false;
    CHECK(kui::wheel_open(1, &d, on_result, nullptr, &h) == KILN_ERR_UNAVAILABLE);
    kui::g_attached = true;
    kui::g_swapchain = false;
    CHECK(kui::wheel_open(1, &d, on_result, nullptr, &h) == KILN_ERR_UNAVAILABLE);
    kui::g_swapchain = true;
    CHECK(kui::wheel_close(12345) == KILN_ERR_NOT_FOUND);

    // ---- open, highlight, pick by a click ----
    CHECK(kui::wheel_open(7, &d, on_result, nullptr, &h) == KILN_OK && h != 0 && kui::wheel_active() && kui::capturing());
    CHECK(kui::wheel_open(7, &d, on_result, nullptr, nullptr) == KILN_ERR_UNAVAILABLE);  // one at a time
    at(0, 400);
    frame();
    CHECK(g_vertices > 100);  // the ring, the labels, the middle
    CHECK(g_results.empty());
    // a click on the very first frame is not a pick (it may be the click that opened the wheel): the wheel here already
    // drew once, so click now at 90 degrees (three sectors: 60..180 is the second, "Bear")
    at(90, 220);
    frame();
    click();
    CHECK(g_results == std::vector<int>{1});
    CHECK(!kui::wheel_active() && !kui::capturing());
    frame();  // nothing left to draw: the frame callback is not even asked
    CHECK(g_results.size() == 1);

    // ---- a click in the first frame after opening does not pick ----
    g_results.clear();
    CHECK(kui::wheel_open(7, &d, on_result, nullptr, &h) == KILN_OK);
    at(90, 220);
    io.AddMouseButtonEvent(0, true);
    frame();
    io.AddMouseButtonEvent(0, false);
    CHECK(g_results.empty() && kui::wheel_active());
    // a click in the centre does nothing either
    at(0, 20);
    frame();
    click();
    CHECK(g_results.empty() && kui::wheel_active());
    // cancel by a right click
    io.AddMouseButtonEvent(1, true);
    frame();
    io.AddMouseButtonEvent(1, false);
    CHECK(g_results == std::vector<int>{-1} && !kui::wheel_active());

    // ---- cancel by Esc (the window thread's key) ----
    g_results.clear();
    CHECK(kui::wheel_open(7, &d, on_result, nullptr, &h) == KILN_OK);
    frame();
    CHECK(kui::wheel_on_key(key(VK_ESCAPE, true)));  // consumed: the game does not open its pause menu
    CHECK(!kui::wheel_on_key(key('A', true)));
    frame();
    CHECK(g_results == std::vector<int>{-1} && !kui::wheel_active());
    CHECK(!kui::wheel_on_key(key(VK_ESCAPE, true)));  // no wheel: not ours

    // ---- the hold key: a flick over an entry picks ----
    g_results.clear();
    kui::g_held[0x10] = true;
    KilnUiWheel held = def(three, 3, 0x10);
    CHECK(kui::wheel_open(7, &held, on_result, nullptr, &h) == KILN_OK);
    at(200, 220);  // 180..300: the third, "Fox"
    frame();
    kui::g_held[0x10] = false;
    CHECK(!kui::wheel_on_key(key(0x10, false)));  // releases always reach the game
    frame();
    CHECK(g_results == std::vector<int>{2});

    // ... a release in the middle after a hold cancels ...
    g_results.clear();
    kui::g_held[0x10] = true;
    CHECK(kui::wheel_open(7, &held, on_result, nullptr, &h) == KILN_OK);
    at(0, 10);
    frame();
    kui::g_now += 500;
    kui::g_held[0x10] = false;
    kui::wheel_on_key(key(0x10, false));
    frame();
    CHECK(g_results == std::vector<int>{-1} && !kui::wheel_active());

    // ... a quick tap leaves it open, and a click then picks ...
    g_results.clear();
    kui::g_held[0x10] = true;
    CHECK(kui::wheel_open(7, &held, on_result, nullptr, &h) == KILN_OK);
    at(0, 10);
    frame();
    kui::g_now += 100;
    kui::g_held[0x10] = false;
    kui::wheel_on_key(key(0x10, false));
    frame();
    CHECK(g_results.empty() && kui::wheel_active());
    at(90, 220);
    frame();
    click();
    CHECK(g_results == std::vector<int>{1});

    // ... a key that is not held as the wheel opens is no hold key: its release means nothing, a click picks
    g_results.clear();
    kui::g_held[0x10] = false;
    CHECK(kui::wheel_open(7, &held, on_result, nullptr, &h) == KILN_OK);
    at(0, 10);
    frame();
    kui::g_now += 600;
    kui::wheel_on_key(key(0x10, false));
    frame();
    CHECK(g_results.empty() && kui::wheel_active());
    at(90, 220);
    frame();
    click();
    CHECK(g_results == std::vector<int>{1});

    // ---- paging: 20 entries are three pages; the mouse wheel turns them ----
    g_results.clear();
    std::vector<std::string> names;
    std::vector<KilnStr> many;
    for (int i = 0; i < 20; ++i) names.push_back("Entry " + std::to_string(i) + (i % 3 ? " (details)" : ""));
    for (const auto& n : names) many.push_back(KilnStr{n.data(), n.size()});
    KilnUiWheel big = def(many.data(), 20);
    CHECK(kui::wheel_open(7, &big, on_result, nullptr, &h) == KILN_OK);
    at(0, 220);
    frame();
    io.AddMouseWheelEvent(0, -1);  // down: the next page
    frame();
    at(0, 221);
    frame();
    click();
    CHECK(g_results == std::vector<int>{8});
    g_results.clear();
    CHECK(kui::wheel_open(7, &big, on_result, nullptr, &h) == KILN_OK);
    at(0, 220);
    frame();
    io.AddMouseWheelEvent(0, 1);  // up from the first page wraps to the last (4 entries)
    frame();
    at(90, 220);  // four sectors of 90 degrees: the second is entry 17
    frame();
    click();
    CHECK(g_results == std::vector<int>{17});

    // ---- wheel_close: no callback; a callback that opens another wheel ----
    g_results.clear();
    CHECK(kui::wheel_open(7, &d, on_result, nullptr, &h) == KILN_OK);
    frame();
    CHECK(kui::wheel_close(h) == KILN_OK && !kui::wheel_active() && !kui::capturing());
    CHECK(kui::wheel_close(h) == KILN_ERR_NOT_FOUND);
    frame();
    CHECK(g_results.empty());
    static KilnHandle second = 0;
    static const KilnStr one[] = {S("sub")};
    static KilnUiWheel sub = def(one, 1);
    auto reopen = [](int32_t choice, void*) {
        g_results.push_back(choice);
        CHECK(kui::wheel_open(7, &sub, on_result, nullptr, &second) == KILN_OK);  // the first is closed before the call
    };
    CHECK(kui::wheel_open(7, &d, reopen, nullptr, &h) == KILN_OK);
    at(90, 220);
    frame();
    click();
    CHECK(g_results == std::vector<int>{1} && kui::wheel_active() && second != 0);
    CHECK(kui::wheel_close(second) == KILN_OK);
    // a callback that closes its own wheel's handle is harmless (the gate is recursive)
    g_results.clear();
    static KilnHandle self = 0;
    auto self_close = [](int32_t choice, void*) {
        g_results.push_back(choice);
        CHECK(kui::wheel_close(self) == KILN_ERR_NOT_FOUND);
    };
    CHECK(kui::wheel_open(7, &d, self_close, nullptr, &self) == KILN_OK);
    at(90, 220);
    frame();
    click();
    CHECK(g_results == std::vector<int>{1});

    // ---- a window below gets no pointer while the wheel is open ----
    g_results.clear();
    auto below = [&](bool* clicked) {
        ImGui::SetNextWindowPos(ImVec2(CX + 300, CY - 20));
        ImGui::SetNextWindowSize(ImVec2(300, 100));
        if (ImGui::Begin("below")) *clicked = ImGui::Button("press me", ImVec2(250, 60)) || *clicked;
        ImGui::End();
    };
    bool pressed = false;
    at(0, 800);  // hover the button's place without the wheel
    ImGui::GetIO().AddMousePosEvent(CX + 400, CY + 10);
    for (int i = 0; i < 2; ++i) {
        kui::g_now += 16;
        ImGui::NewFrame();
        below(&pressed);
        ImGui::Render();
    }
    ImGui::GetIO().AddMouseButtonEvent(0, true);
    for (int i = 0; i < 2; ++i) {
        kui::g_now += 16;
        ImGui::NewFrame();
        below(&pressed);
        ImGui::Render();
    }
    ImGui::GetIO().AddMouseButtonEvent(0, false);
    kui::g_now += 16;
    ImGui::NewFrame();
    below(&pressed);
    ImGui::Render();
    CHECK(pressed);  // the button works when nothing covers it
    pressed = false;
    CHECK(kui::wheel_open(7, &d, on_result, nullptr, &h) == KILN_OK);
    auto frame_with_below = [&] {
        kui::g_now += 16;
        ImGui::NewFrame();
        below(&pressed);
        if (kui::wheel_active()) kui::wheel_frame(W, H, 1.0f);
        ImGui::Render();
    };
    ImGui::GetIO().AddMousePosEvent(CX + 400, CY + 10);  // over the button, in a sector
    frame_with_below();
    frame_with_below();
    ImGui::GetIO().AddMouseButtonEvent(0, true);
    frame_with_below();
    frame_with_below();
    ImGui::GetIO().AddMouseButtonEvent(0, false);
    frame_with_below();
    CHECK(!pressed);              // the wheel took the pointer
    CHECK(!g_results.empty());    // and the click picked an entry instead
    if (kui::wheel_active()) kui::wheel_close(h);

    // ---- exactly once: a wheel_close that wins against an ending frame leaves no callback ----
    g_results.clear();
    CHECK(kui::wheel_open(7, &d, on_result, nullptr, &h) == KILN_OK);
    std::shared_ptr<kui::Wheel> taken;
    {
        std::lock_guard<std::mutex> lk(kui::g_mu);
        taken = kui::g_wheel;  // what the render thread holds while it steps the model
    }
    CHECK(kui::wheel_close(h) == KILN_OK);
    kui::finish(taken, 1);  // the frame's pick arrives after the close
    CHECK(g_results.empty() && !kui::wheel_active());

    // ---- the overlay turned off with a wheel open: cancelled once, the input released ----
    CHECK(kui::wheel_open(7, &d, on_result, nullptr, &h) == KILN_OK && kui::capturing());
    kui::wheel_cancel_undrawable();
    CHECK(g_results.size() == 1 && g_results[0] == -1 && !kui::wheel_active() && !kui::capturing());
    kui::wheel_cancel_undrawable();  // nothing open: nothing happens
    CHECK(g_results.size() == 1 && kui::wheel_close(h) == KILN_ERR_NOT_FOUND);

    CHECK(kui::g_errors.empty());
    ImGui::DestroyContext();
    if (g_fail == 0) std::printf("wheel_host: %d checks passed\n", g_checks);
    return g_fail == 0 ? 0 : 1;
}
