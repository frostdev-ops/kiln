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

// The window's topic list as on 26430625: {ptr, count +8, cap +0x10, grow +0x18}; topics of 0x2a0 bytes, the line entry
// at +4 (line id +0, option text +4, body texts +8, count +0x88, voice-overs' count +0x28c, hint +0x290, end action +0x292),
// a pointer at +0x298. The grow callback is the game's: bool(list*, -needed, 0).
struct TopicList {
    uint8_t* ptr = nullptr;
    uint64_t count = 0, cap = 0;
    bool (*grow)(void*, int64_t, uint8_t) = nullptr;
};
static_assert(sizeof(TopicList) == 0x20);
std::vector<uint8_t> g_storage;
int g_grows = 0;
bool g_grow_fails = false;
bool fake_grow(void* list, int64_t neg, uint8_t flag) {
    auto* l = static_cast<TopicList*>(list);
    ++g_grows;
    if (g_grow_fails || flag != 0 || neg >= 0) return false;
    const uint64_t want = static_cast<uint64_t>(-neg);
    std::vector<uint8_t> bigger((want + 4) * 0x2a0);
    if (l->ptr) std::memcpy(bigger.data(), l->ptr, l->count * 0x2a0);
    g_storage.swap(bigger);
    l->ptr = g_storage.data();
    l->cap = want + 4;
    return true;
}
struct Topic {
    const uint8_t* p;
    uint8_t kind() const { return p[0]; }
    uint32_t u32(size_t off) const {
        uint32_t v;
        std::memcpy(&v, p + 4 + off, 4);
        return v;
    }
    uint32_t id() const { return u32(0); }
    uint32_t label() const { return u32(4); }
    uint32_t reply() const { return u32(8); }
    uint8_t texts() const { return p[4 + 0x88]; }
    uint8_t vo() const { return p[4 + 0x28c]; }
    uint8_t hint() const { return p[4 + 0x290]; }
    uint8_t end() const { return p[4 + 0x292]; }
    uint64_t ptr() const {
        uint64_t v;
        std::memcpy(&v, p + 0x298, 8);
        return v;
    }
};
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
    // ---- slice 3: the menu's own line at an open, and a host's reply to one player ------------------------------
    const uint32_t say[] = {0xc1, 0xc2};
    {  // Kiln's menu line comes back for the caller to show with SetLine at the open, with straight-to-crafting off
        npcspeech::Core c;
        c.set_lines(7, KILN_SPEECH_KIND_MENU, KILN_SPEECH_REPLACE, say, 2, KILN_SPEECH_PICK_ORDER, 0);
        CHECK(c.dialog_active(), "a menu line needs the window's hook");
        Window w;
        auto st = c.dialog(7, w.b.data(), d, true, false, true, 1000);
        CHECK(st.set_line == 0xc1 && st.clear_direct, "at the open: Kiln's first line, the menu stays (no straight-to-crafting)");
        CHECK(w.line() == 0, "the core leaves the box to SetLine");
        st = c.dialog(7, w.b.data(), d, false, false, true, 1050);
        CHECK(st.set_line == 0 && !st.clear_direct, "the next frames: nothing");
        st = c.dialog(7, w.b.data(), d, true, false, false, 1500);
        CHECK(st.set_line == 0, "the game gave the open up (no record): nothing");
        st = c.dialog(7, w.b.data(), d, true, false, true, 2000);
        CHECK(st.set_line == 0xc2, "the next open: the next line");
        st = c.dialog(8, w.b.data(), d, true, false, true, 2100);
        CHECK(st.set_line == 0, "another NPC's menu: nothing");
        CHECK(c.counts().menu_lines == 2, "menu lines Kiln showed are counted");
        c.set_lines(9, KILN_SPEECH_KIND_MENU, KILN_SPEECH_REPLACE, say, 2, KILN_SPEECH_PICK_HOST, 0);
        st = c.dialog(9, w.b.data(), d, true, false, true, 2500);
        // review: before the host's first answer a menu line meant as a condition per player showed anyway
        CHECK(st.set_line == 0 && !st.clear_direct, "the host has not answered yet: no line, the game's menu as it is");
        KilnSpeechEvent ev0[4];
        CHECK(c.drain(ev0, 4) == 1 && ev0[0].kind == KILN_SPEECH_KIND_MENU && ev0[0].line == -1,
              "that open is still reported, so the host answers this player");
        c.next(9, KILN_SPEECH_KIND_MENU, KILN_SPEECH_LINE_GAME);
        st = c.dialog(9, w.b.data(), d, true, false, true, 3000);
        CHECK(st.set_line == 0 && !st.clear_direct, "the host said -1 for this player: no line, the game's menu as it is");
        c.next(9, KILN_SPEECH_KIND_MENU, 1);
        st = c.dialog(9, w.b.data(), d, true, false, true, 4000);
        CHECK(st.set_line == 0xc2, "the host's pick for this player");
        // a farewell and a menu line of one NPC: a close shows the farewell, an open the line
        c.set_lines(7, KILN_SPEECH_KIND_FAREWELL, KILN_SPEECH_REPLACE, bye, 1, KILN_SPEECH_PICK_RANDOM, 0);
        w.set_line(kSeeYouSoon);
        st = c.dialog(7, w.b.data(), d, false, true, true, 5000);
        CHECK(w.line() == 0xf1 && st.set_line == 0, "the close: the farewell, no menu line");
    }
    {  // menu_say: shown once in the box of that NPC's open menu, if it opens within kMenuSayMs; tag 0 cancels
        npcspeech::Core c;
        CHECK(c.menu_say(7, 0xd1, 1000), "menu_say");
        CHECK(c.dialog_active() && c.menu_say_pending(), "a pending reply needs the window's hook every frame");
        Window w;
        auto st = c.dialog(8, w.b.data(), d, false, false, true, 1100);
        CHECK(st.set_line == 0, "another NPC's menu: not there");
        st = c.dialog(7, w.b.data(), d, false, false, true, 1200);
        CHECK(st.set_line == 0xd1 && !st.clear_direct, "the reply shows in this NPC's menu");
        st = c.dialog(7, w.b.data(), d, false, false, true, 1250);
        CHECK(st.set_line == 0 && !c.menu_say_pending() && !c.dialog_active(), "once");
        c.menu_say(7, 0xd2, 1000);
        st = c.dialog(7, w.b.data(), d, false, false, true, 1000 + npcspeech::kMenuSayMs + 1);
        CHECK(st.set_line == 0 && !c.menu_say_pending(), "too late: dropped");
        c.menu_say(7, 0xd3, 1000);
        c.menu_say(7, 0, 1001);
        CHECK(!c.menu_say_pending(), "tag 0 cancels");
        CHECK(!c.menu_say(0, 0xd4, 1000), "entity 0 is refused");
        c.set_lines(7, KILN_SPEECH_KIND_MENU, KILN_SPEECH_REPLACE, say, 2, KILN_SPEECH_PICK_ORDER, 0);
        c.menu_say(7, 0xd5, 2000);
        st = c.dialog(7, w.b.data(), d, true, false, true, 2001);
        CHECK(st.set_line == 0xd5 && st.clear_direct, "a reply waiting at an open wins over the menu line, the menu stays");
        CHECK(c.counts().menu_lines == 2, "both replies counted as menu lines");
        c.menu_say(7, 0xd6, 3000);
        c.clear_all();
        CHECK(!c.menu_say_pending() && !c.dialog_active(), "clear_all drops waiting replies");
    }
    // ---- slice 4: talk pages: Kiln's topics after the game's own, and their picks -----------------------------------
    npcspeech::TopicLayout tl;
    CHECK(tl.sane(), "the game's topic layout is sane");
    const KilnSpeechTopic food[] = {{0x70d1, 0xa001, 0xb001, 2, 0, {0, 0}}, {0x70d2, 0xa002, 0, 0, 1, {0, 0}}};
    {  // an NPC with no topic of its own (Ramone) gets Kiln's: the list grows through the game's own callback
        npcspeech::Core c;
        CHECK(c.topics(7, food, 2), "topics");
        CHECK(c.dialog_active() && c.has_topics(), "topics need the list's hook");
        TopicList list;
        list.grow = &fake_grow;
        g_grows = 0;
        CHECK(c.append(7, reinterpret_cast<uint8_t*>(&list), tl) == 2, "two topics added");
        CHECK(list.count == 2 && g_grows >= 1, "count 2, grown by the game's callback");
        Topic a{list.ptr}, b{list.ptr + 0x2a0};
        CHECK(a.kind() == 0 && a.id() == 0x70d1 && a.label() == 0xa001 && a.reply() == 0xb001 && a.texts() == 1 && a.vo() == 0 &&
                  a.hint() == 2 && a.end() == 0 && a.ptr() == 0,
              "a topic: say its entry, Kiln's id, label, one reply, no voice-over, its hint");
        CHECK(b.id() == 0x70d2 && b.texts() == 0 && b.end() == 1, "a topic without a reply, closing the talk");
        CHECK(c.counts().topic_rows == 2, "rows counted");
    }
    {  // after the game's own topics, within its capacity; another NPC gets nothing; a list that cannot grow stops
        npcspeech::Core c;
        c.topics(7, food, 2);
        std::vector<uint8_t> mem(4 * 0x2a0);
        TopicList list;
        list.ptr = mem.data();
        list.cap = 4;
        list.count = 1;
        const uint32_t game_id = 1116149345u;
        std::memcpy(mem.data() + 4, &game_id, 4);
        list.grow = &fake_grow;
        g_grows = 0;
        CHECK(c.append(7, reinterpret_cast<uint8_t*>(&list), tl) == 2 && list.count == 3 && g_grows == 0, "room: no grow");
        CHECK(Topic{mem.data()}.id() == game_id && Topic{mem.data() + 0x2a0}.id() == 0x70d1, "the game's topic stays first (additive)");
        TopicList other;
        other.grow = &fake_grow;
        CHECK(c.append(8, reinterpret_cast<uint8_t*>(&other), tl) == 0 && other.count == 0, "another NPC: nothing");
        TopicList stuck;
        stuck.grow = nullptr;
        CHECK(c.append(7, reinterpret_cast<uint8_t*>(&stuck), tl) == 0 && stuck.count == 0, "no grow callback: nothing");
        g_grow_fails = true;
        TopicList refused;
        refused.grow = &fake_grow;
        CHECK(c.append(7, reinterpret_cast<uint8_t*>(&refused), tl) == 0 && refused.count == 0, "the callback refuses: nothing");
        g_grow_fails = false;
    }
    {  // a pick of a Kiln topic is reported once per click; the game's own topics are not Kiln's
        npcspeech::Core c;
        c.topics(7, food, 2);
        bool reply = false;
        CHECK(c.picked(7, 0x70d1, &reply) && reply, "Kiln's topic with a reply");
        CHECK(c.picked(7, 0x70d2, &reply) && !reply, "Kiln's topic without one (the caller skips the empty box)");
        CHECK(!c.picked(7, 1116149345u, &reply), "the game's topic is the game's");
        CHECK(!c.picked(8, 0x70d1, &reply), "another NPC's menu");
        KilnSpeechEvent ev[4];
        const uint32_t n = c.drain(ev, 4);
        CHECK(n == 2 && ev[0].type == KILN_SPEECH_EVENT_TOPIC && ev[0].npc == 7 && ev[0].value == 0x70d1 && ev[1].value == 0x70d2,
              "picks are events with the topic's id");
        CHECK(c.counts().picks == 2, "picks counted");
    }
    {  // limits and clearing
        npcspeech::Core c;
        std::vector<KilnSpeechTopic> many(npcspeech::kMaxTopics + 1, KilnSpeechTopic{0x7000, 0xa001, 0xb001, 0, 0, {0, 0}});
        for (size_t i = 0; i < many.size(); ++i) many[i].id = 0x7000 + static_cast<uint32_t>(i);
        CHECK(!npcspeech::Core::valid_topics(7, many.data(), static_cast<uint32_t>(many.size())), "more than kMaxTopics");
        KilnSpeechTopic zero = food[0];
        zero.id = 1;
        CHECK(!npcspeech::Core::valid_topics(7, &zero, 1), "ids 0 and 1 are the game's (farewell, missing requirement)");
        zero = food[0];
        zero.label = 0;
        CHECK(!npcspeech::Core::valid_topics(7, &zero, 1), "a topic needs a label");
        zero = food[0];
        zero.hint = 4;
        CHECK(!npcspeech::Core::valid_topics(7, &zero, 1), "an unknown hint");
        KilnSpeechTopic dup[] = {food[0], food[0]};
        CHECK(!npcspeech::Core::valid_topics(7, dup, 2), "the same id twice");
        CHECK(npcspeech::Core::valid_topics(7, nullptr, 0), "none: clears");
        c.topics(7, food, 2);
        c.topics(7, nullptr, 0);
        CHECK(!c.has_topics() && c.tracked() == 0, "cleared, NPC forgotten");
        c.topics(7, food, 2);
        c.clear_all();
        CHECK(!c.has_topics(), "clear_all ends topics");
    }
    std::printf("npcdialog core: %d PASS, %d FAIL\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
