// SPDX-License-Identifier: MIT
// kiln.npcspeech's dialogue menu parts (dev.32) on a fake dialogue window, as on client 26430625: the speech box's text
// at +0x2b0, the close request +0x16, the open request +0x15, straight-to-crafting +0x14 (sigdb client.d/npcdialog.json).
// The game's DialogUpdate has run when Core::dialog is asked; it sees the requests the window had at the call's entry.
#include <cstdio>
#include <cstring>
#include <vector>

#include "npcspeech_core.h"

static int g_fail = 0, g_pass = 0;
#define CHECK(c, msg)                                          \
    do {                                                       \
        if (c) {                                               \
            ++g_pass;                                          \
        } else {                                               \
            ++g_fail;                                          \
            std::printf("FAIL %s (line %d)\n", msg, __LINE__); \
        }                                                      \
    } while (0)

namespace {
struct Window {
    std::vector<uint8_t> b = std::vector<uint8_t>(0x4000);
    uint32_t line() const {
        uint32_t t;
        std::memcpy(&t, &b[0x2b0], 4);
        return t;
    }
    void set_line(uint32_t t) { std::memcpy(&b[0x2b0], &t, 4); }
};

constexpr uint32_t kSeeYouSoon = 653977772u;  // Ramone's farewell "See you soon!"
}  // namespace

int main() {
    npcspeech::DialogLayout d;
    CHECK(d.sane(), "the game's window layout is sane");
    npcspeech::DialogLayout bad = d;
    bad.line = 0;
    CHECK(!bad.sane(), "a line offset of 0 is refused");

    const uint32_t bye[] = {0xf1, 0xf2, 0xf3};
    {  // a farewell of Kiln's replaces the game's after the window's close request, in turn with InOrder
        npcspeech::Core c;
        CHECK(!c.dialog_active(), "nothing set: the window's hook has nothing to do");
        CHECK(c.set_lines(7, KILN_SPEECH_KIND_FAREWELL, KILN_SPEECH_REPLACE, bye, 3, KILN_SPEECH_PICK_ORDER, 0), "farewell lines");
        CHECK(c.dialog_active() && !c.active(), "a farewell needs the window's hook, not the bubble's");
        Window w;
        w.set_line(kSeeYouSoon);
        auto st = c.dialog(7, w.b.data(), d, false, true, true, 1000);
        CHECK(w.line() == 0xf1 && st.set_line == 0 && !st.clear_direct, "the farewell is Kiln's first line");
        w.set_line(kSeeYouSoon);
        c.dialog(7, w.b.data(), d, false, true, true, 2000);
        CHECK(w.line() == 0xf2, "the next farewell, the next line");
        w.set_line(1310400781u);
        c.dialog(7, w.b.data(), d, false, false, true, 2100);
        CHECK(w.line() == 1310400781u, "no close request: the box is the game's");
        w.set_line(kSeeYouSoon);
        c.dialog(8, w.b.data(), d, false, true, true, 2200);
        CHECK(w.line() == kSeeYouSoon, "another NPC's farewell is the game's");
        c.dialog(0, w.b.data(), d, false, true, true, 2300);
        CHECK(w.line() == kSeeYouSoon, "no dialogue partner: nothing");
        CHECK(c.counts().farewells == 2, "farewells Kiln showed are counted");
        c.set_lines(7, KILN_SPEECH_KIND_FAREWELL, KILN_SPEECH_GAME, nullptr, 0, 0, 0);
        w.set_line(kSeeYouSoon);
        c.dialog(7, w.b.data(), d, false, true, true, 3000);
        CHECK(w.line() == kSeeYouSoon && c.tracked() == 0 && !c.dialog_active(), "cleared: the game's farewell, NPC forgotten");
    }
    {  // random farewells never repeat; the host's pick, and -1 for the game's own
        npcspeech::Core c;
        c.set_lines(7, KILN_SPEECH_KIND_FAREWELL, KILN_SPEECH_REPLACE, bye, 3, KILN_SPEECH_PICK_RANDOM, 0);
        Window w;
        uint32_t prev = 0;
        bool repeat = false;
        for (int i = 0; i < 20; ++i) {
            w.set_line(kSeeYouSoon);
            c.dialog(7, w.b.data(), d, false, true, true, 1000 + i);
            repeat |= w.line() == prev;
            prev = w.line();
        }
        CHECK(!repeat, "random farewells: never the same twice in a row");
        c.set_lines(9, KILN_SPEECH_KIND_FAREWELL, KILN_SPEECH_REPLACE, bye, 3, KILN_SPEECH_PICK_HOST, 0);
        CHECK(c.next(9, KILN_SPEECH_KIND_FAREWELL, 2), "the host's farewell pick");
        w.set_line(kSeeYouSoon);
        c.dialog(9, w.b.data(), d, false, true, true, 5000);
        CHECK(w.line() == 0xf3, "the host's pick shows");
        c.next(9, KILN_SPEECH_KIND_FAREWELL, KILN_SPEECH_LINE_GAME);
        w.set_line(kSeeYouSoon);
        c.dialog(9, w.b.data(), d, false, true, true, 6000);
        CHECK(w.line() == kSeeYouSoon, "-1: the game's own farewell");
        KilnSpeechEvent ev[4];
        const uint32_t n = c.drain(ev, 4);
        CHECK(n == 2 && ev[0].kind == KILN_SPEECH_KIND_FAREWELL && ev[0].line == 2 && ev[1].line == -1 && ev[1].value == 2,
              "farewells of host lines are reported");
        // a greeting override of the same NPC is its own: the farewell stays
        c.set_lines(9, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_SUPPRESS, nullptr, 0, KILN_SPEECH_PICK_RANDOM, 0);
        c.set_lines(9, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_GAME, nullptr, 0, 0, 0);
        c.next(9, KILN_SPEECH_KIND_FAREWELL, 0);
        w.set_line(kSeeYouSoon);
        c.dialog(9, w.b.data(), d, false, true, true, 7000);
        CHECK(w.line() == 0xf1, "the greeting cleared, the farewell stays");
        c.clear_all();
        CHECK(!c.dialog_active() && c.tracked() == 0, "clear_all ends farewells too");
    }
    std::printf("npcdialog core: %d PASS, %d FAIL\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
