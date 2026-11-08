// SPDX-License-Identifier: MIT
// kiln.ui's radial wheel without ImGui or Windows: pages, the ring's size, sectors and the hit test, label splitting and
// shortening, and the open / select / cancel rules (Model). Header only, so tests/wheel_math_test.cpp builds it on the
// host. wheel.cpp draws it and feeds it the pointer, the clicks and the hold key.
//
// The rules follow EMF's documented wheel (EmfWheel in native/plugins/emf/include/emf/emf_api.h, MIT): 8 entries per page
// and the mouse wheel turns the page; a left click on a highlighted entry picks; Esc or a right click cancel; with a hold
// key, releasing it picks the highlighted entry and cancels in the centre, except that a quick tap (released within
// 300 ms with nothing highlighted) leaves the wheel open for a click.
//
// Layout: the entries of the page share the ring equally, the first one centred at the top and the rest clockwise. The
// pointer's highlight is the sector it is in; nothing is highlighted inside the centre hole (the dead zone), however far
// out the pointer is otherwise.
#pragma once
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

namespace kwheel {

constexpr int kPageItems = 8;
constexpr int kMaxItems = 64;
constexpr uint64_t kTapMs = 300;  // a hold-key release this soon with nothing highlighted is a tap
constexpr float kPi = 3.14159265358979323846f;
constexpr size_t kMaxLabelBytes = 256;
constexpr size_t kMaxTitleBytes = 64;

// ---------------------------------------------------------------------------------------------- pages ------------
inline int page_count(int count) { return count <= 0 ? 0 : (count + kPageItems - 1) / kPageItems; }

// Entries on `page` (0 for a page that does not exist).
inline int page_size(int count, int page) {
    if (count <= 0 || page < 0 || page >= page_count(count)) return 0;
    const int left = count - page * kPageItems;
    return left < kPageItems ? left : kPageItems;
}

// Turns the page by `steps` (negative = back), wrapping round.
inline int turn_page(int page, int pages, int steps) {
    if (pages <= 1) return 0;
    int p = (page + steps) % pages;
    return p < 0 ? p + pages : p;
}

// ---------------------------------------------------------------------------------------------- geometry ---------
struct Rings {
    float inner, outer;  // pixels: the centre hole's radius (the dead zone) and the ring's outer edge
};

// The ring's size from the screen's: about 30% of its short side, at least 150 and at most 280 pixels at scale 1 (never
// more than 46% of the short side on a tiny window); the hole is 32% of the ring.
inline Rings rings(float screen_w, float screen_h, float scale) {
    const float s = scale > 0.25f && std::isfinite(scale) ? scale : 1.0f;
    float shortest = screen_w < screen_h ? screen_w : screen_h;
    if (!(shortest > 0) || !std::isfinite(shortest)) shortest = 720.0f;
    float outer = shortest * 0.30f;
    const float lo = 150.0f * s, hi = 280.0f * s;
    outer = outer < lo ? lo : outer > hi ? hi : outer;
    const float cap = shortest * 0.46f;
    if (outer > cap) outer = cap;
    return {outer * 0.32f, outer};
}

// Angle of (dx, dy) on a clock face: 0 = up, growing clockwise, in [0, 2pi). dy points down, as on the screen.
inline float clock_angle(float dx, float dy) {
    float a = std::atan2(dx, -dy);
    if (a < 0) a += 2.0f * kPi;
    return a;
}

inline float sector_span(int n) { return n > 0 ? 2.0f * kPi / static_cast<float>(n) : 2.0f * kPi; }

// The centre angle of sector `s` of `n` (clock angle).
inline float sector_center(int s, int n) { return sector_span(n) * static_cast<float>(s); }

// The sector (0..n-1) the offset (dx, dy) from the wheel's centre is in, or -1: in the centre hole (distance below
// `dead_radius`), no entries, or an offset that is not a number.
inline int hit_sector(float dx, float dy, float dead_radius, int n) {
    if (n <= 0 || !std::isfinite(dx) || !std::isfinite(dy)) return -1;
    if (std::sqrt(dx * dx + dy * dy) < dead_radius) return -1;
    const float span = sector_span(n);
    int s = static_cast<int>(std::floor((clock_angle(dx, dy) + span * 0.5f) / span));
    if (s >= n) s %= n;
    return s < 0 ? 0 : s;
}

// The offset from the centre of the point at clock angle `angle` and distance `radius`.
inline void polar(float angle, float radius, float* dx, float* dy) {
    *dx = std::sin(angle) * radius;
    *dy = -std::cos(angle) * radius;
}

// ---------------------------------------------------------------------------------------------- labels -----------
// "Name (details)" -> name and details: the final ')' must close a '(' that follows a space and a non-empty name.
// Anything else is all name. The views point into `label`.
inline void split_label(std::string_view label, std::string_view* name, std::string_view* details) {
    *name = label;
    *details = {};
    if (label.size() < 4 || label.back() != ')') return;
    int depth = 0;
    for (size_t i = label.size(); i-- > 0;) {
        const char c = label[i];
        if (c == ')') {
            ++depth;
        } else if (c == '(' && --depth == 0) {
            if (i < 2 || label[i - 1] != ' ') return;
            size_t end = i - 1;
            while (end > 0 && label[end - 1] == ' ') --end;
            if (end == 0) return;
            *name = label.substr(0, end);
            *details = label.substr(i + 1, label.size() - i - 2);
            return;
        }
    }
}

inline bool utf8_continuation(char c) { return (static_cast<unsigned char>(c) & 0xC0) == 0x80; }

// The text for storage: control characters (a newline would break the layout) become spaces, and it is cut to at most
// `max_bytes` without splitting a UTF-8 sequence.
inline std::string clean_text(std::string_view s, size_t max_bytes) {
    size_t n = s.size() < max_bytes ? s.size() : max_bytes;
    while (n > 0 && n < s.size() && utf8_continuation(s[n])) --n;
    std::string out(s.substr(0, n));
    for (char& c : out)
        if (static_cast<unsigned char>(c) < 0x20 || c == 0x7F) c = ' ';
    return out;
}

// `s` shortened with "..." until measure(text) <= max_px (measure: float(std::string_view)). Never splits a UTF-8
// sequence; the empty string when not even "..." fits.
template <class Measure>
std::string ellipsize(std::string_view s, float max_px, Measure measure) {
    if (measure(s) <= max_px) return std::string(s);
    size_t lo = 0, hi = s.size();  // the longest prefix that fits with "..." is in [lo, hi]
    while (lo < hi) {
        const size_t mid = (lo + hi + 1) / 2;
        size_t cut = mid;
        while (cut > 0 && cut < s.size() && utf8_continuation(s[cut])) --cut;
        const std::string t = std::string(s.substr(0, cut)) + "...";
        if (measure(t) <= max_px) lo = mid;
        else hi = mid - 1;
    }
    size_t cut = lo;
    while (cut > 0 && cut < s.size() && utf8_continuation(s[cut])) --cut;
    std::string t = std::string(s.substr(0, cut)) + "...";
    return measure(t) <= max_px ? t : std::string();
}

// ---------------------------------------------------------------------------------------------- rules ------------
// A hold key (EmfWheel.hold_key, KilnUiWheel.hold_key) is 0 or a keyboard key's generic virtual-key code: no mouse
// buttons (VK_LBUTTON..VK_XBUTTON2 = 1..6) and no VK_LSHIFT-style codes (0xA0..0xA5).
inline bool valid_hold_key(uint32_t vk) { return vk <= 0xFF && !(vk >= 0x01 && vk <= 0x06) && !(vk >= 0xA0 && vk <= 0xA5); }

struct Inputs {
    float dx = 0, dy = 0;       // the pointer's offset from the wheel's centre, pixels (y down)
    bool left_click = false;    // the left button went down since the last step
    bool right_click = false;
    bool escape = false;
    int turn = 0;               // +1 = next page, -1 = previous (the mouse wheel)
    bool hold_released = false; // the hold key was released since the last step
    uint64_t release_ms = 0;    // when, on the same clock as Model's `opened_ms`
};

struct Step {
    enum Kind { None, Pick, Cancel };
    Kind kind = None;
    int choice = -1;     // Pick: the index into all the entries
    int highlight = -1;  // the highlighted entry (index into all the entries), or -1
    int page = 0;
};

class Model {
public:
    // hold_down: the hold key is held now (false for no key, or one that is not held as the wheel opens).
    Model(int count, bool hold_down, uint64_t opened_ms, float dead_radius)
        : count_(count), hold_(hold_down), opened_ms_(opened_ms), dead_(dead_radius) {}

    int count() const { return count_; }
    int page() const { return page_; }
    int pages() const { return page_count(count_); }
    int page_items() const { return page_size(count_, page_); }
    bool hold_mode() const { return hold_; }
    int highlight() const { return highlight_; }
    void set_dead_radius(float r) { dead_ = r; }
    void set_hold(bool down) { hold_ = down; }

    // The entry (index into all) of sector `s` of the current page.
    int entry_of(int s) const { return page_ * kPageItems + s; }

    Step step(const Inputs& in) {
        Step out;
        if (in.escape || in.right_click) {
            out.kind = Step::Cancel;
            out.page = page_;
            return out;
        }
        if (in.turn != 0) page_ = turn_page(page_, pages(), in.turn > 0 ? 1 : -1);
        const int s = hit_sector(in.dx, in.dy, dead_, page_items());
        highlight_ = s < 0 ? -1 : entry_of(s);
        out.highlight = highlight_;
        out.page = page_;
        if (hold_ && in.hold_released) {
            if (highlight_ >= 0) {
                out.kind = Step::Pick;
                out.choice = highlight_;
            } else if (in.release_ms >= opened_ms_ && in.release_ms - opened_ms_ < kTapMs) {
                hold_ = false;  // a tap: stays open for a click
            } else {
                out.kind = Step::Cancel;
            }
            return out;
        }
        if (in.left_click && highlight_ >= 0) {
            out.kind = Step::Pick;
            out.choice = highlight_;
        }
        return out;
    }

private:
    int count_;
    bool hold_;
    uint64_t opened_ms_;
    float dead_;
    int page_ = 0;
    int highlight_ = -1;
};

}  // namespace kwheel
