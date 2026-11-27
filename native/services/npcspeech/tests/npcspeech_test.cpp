// SPDX-License-Identifier: MIT
// kiln.npcspeech core on fake game structs: the bubble params (text +0, show +4, reset +5) and a dialogue record with
// the game's two greeting lists (entries of 0x294 bytes, 32 inline voice-over GUIDs at +0x8c), as on client 26430625.
#include <cstdio>
#include <cstring>
#include <thread>
#include <vector>

#include "npcspeech_core.h"

static int g_fail = 0, g_pass = 0;
#define CHECK(c, msg)                                   \
    do {                                                \
        if (c) {                                        \
            ++g_pass;                                   \
        } else {                                        \
            ++g_fail;                                   \
            std::printf("FAIL %s (line %d)\n", msg, __LINE__); \
        }                                               \
    } while (0)

namespace {
npcspeech::Layout layout() {
    npcspeech::Layout l;
    l.prio_list = 0x90;
    l.prio_count = 0x98;
    l.plain_list = 0x5338;
    l.plain_count = 0x5340;
    l.entry_size = 0x294;
    l.entry_vo = 0x8c;
    l.vo_slots = 32;
    return l;
}

struct Params {
    uint8_t b[0x20]{};
    uint32_t text() const { uint32_t t; std::memcpy(&t, b, 4); return t; }
    void set(uint32_t t, uint8_t show) { std::memcpy(b, &t, 4); b[4] = show; b[5] = 0; }
};

struct Record {
    std::vector<uint8_t> mem = std::vector<uint8_t>(0x14b80);
    std::vector<uint8_t> prio, plain;
    Record(int nprio, int nplain) : prio(0x294 * (nprio ? nprio : 1)), plain(0x294 * (nplain ? nplain : 1)) {
        uint8_t* p = prio.data();
        uint8_t* q = plain.data();
        uint64_t a = nprio, b = nplain;
        std::memcpy(&mem[0x90], &p, 8);
        std::memcpy(&mem[0x98], &a, 8);
        std::memcpy(&mem[0x5338], &q, 8);
        std::memcpy(&mem[0x5340], &b, 8);
        for (int i = 0; i < nprio; ++i) std::memset(&prio[i * 0x294 + 0x8c], 0x10 + i, 16);
        for (int i = 0; i < nplain; ++i) std::memset(&plain[i * 0x294 + 0x8c], 0x40 + i, 16);
    }
    uint8_t vo_byte(bool pl, int i) const { return (pl ? plain : prio)[i * 0x294 + 0x8c]; }
};
}  // namespace

int main() {
    const auto l = layout();
    CHECK(l.voice_sane(), "the game's layout is sane");
    npcspeech::Layout bad = l;
    bad.entry_vo = 0x290;
    CHECK(!bad.voice_sane(), "a voice-over array past the entry is refused");

    const uint32_t tags[] = {0xa1, 0xa2, 0xa3};
    CHECK(npcspeech::Core::valid_set(7, KILN_SPEECH_REPLACE, tags, 3), "replace with tags");
    CHECK(!npcspeech::Core::valid_set(7, KILN_SPEECH_REPLACE, tags, 0), "replace without tags");
    CHECK(!npcspeech::Core::valid_set(0, KILN_SPEECH_SUPPRESS, nullptr, 0), "entity 0");
    CHECK(!npcspeech::Core::valid_set(7, 3, nullptr, 0), "unknown mode");
    const uint32_t zero_tag[] = {0xa1, 0};
    CHECK(!npcspeech::Core::valid_set(7, KILN_SPEECH_REPLACE, zero_tag, 2), "a zero tag");

    {  // nothing set: the game's bubble is untouched
        npcspeech::Core c;
        Params p;
        p.set(2262475715u, 1);
        CHECK(!c.active(), "idle core is inactive");
        c.draw(7, p.b, l, 1000);
        CHECK(p.text() == 2262475715u && p.b[4] == 1, "no override: the game's line");
    }
    {  // Suppress hides, another NPC is untouched, clearing gives the game back
        npcspeech::Core c;
        c.set(7, KILN_SPEECH_SUPPRESS, nullptr, 0);
        CHECK(c.active(), "suppress makes it active");
        Params p, q;
        p.set(2262475715u, 1);
        q.set(3935509066u, 1);
        c.draw(7, p.b, l, 1000);
        c.draw(8, q.b, l, 1000);
        CHECK(p.b[4] == 0, "suppressed NPC's bubble hidden");
        CHECK(q.b[4] == 1 && q.text() == 3935509066u, "other NPC untouched");
        c.set(7, KILN_SPEECH_GAME, nullptr, 0);
        p.set(2262475715u, 1);
        c.draw(7, p.b, l, 1000);
        CHECK(p.b[4] == 1, "cleared: shown again");
        CHECK(c.tracked() == 0 && !c.active(), "cleared NPC forgotten");
    }
    {  // Replace: a game pick becomes one of ours, stable while the game's pick stays; nothing when the game shows nothing
        npcspeech::Core c;
        c.set(7, KILN_SPEECH_REPLACE, tags, 3);
        Params p;
        p.set(2262475715u, 1);
        c.draw(7, p.b, l, 1000);
        const uint32_t first = p.text();
        CHECK(first == 0xa1 || first == 0xa2 || first == 0xa3, "replaced by one of ours");
        for (int i = 0; i < 20; ++i) {
            p.set(2262475715u, 1);
            c.draw(7, p.b, l, 1000 + i);
            if (p.text() != first) break;
        }
        CHECK(p.text() == first, "same game pick, same line of ours");
        p.set(0, 0);
        c.draw(7, p.b, l, 2000);
        CHECK(p.text() == 0 && p.b[4] == 0, "no game pick: nothing shown");
        bool varied = false;
        uint32_t last = first;
        for (int i = 0; i < 40 && !varied; ++i) {
            p.set(i % 2 ? 2262475715u : 3935509066u, 1);
            c.draw(7, p.b, l, 3000 + i);
            varied = p.text() != last;
            last = p.text();
        }
        CHECK(varied, "a new game pick can bring another line");
    }
    // ---- dev.32: a new pick at each greeting (Ramone's one greeting showed the same Replace line forever) ----------
    // A greeting = the bubble shows after at least kReopenMs hidden (or for the first time), or the game picks anew.
    auto greet = [&](npcspeech::Core& c, uint32_t npc, uint64_t& now, uint32_t game = 2262475715u) {
        Params p;
        p.set(game, 0);
        c.draw(npc, p.b, l, now);  // hidden (the player looks away)
        now += npcspeech::kReopenMs;
        p.set(game, 1);
        c.draw(npc, p.b, l, now);  // shown again
        const uint32_t shown = p.text();
        now += 50;
        p.set(game, 1);
        c.draw(npc, p.b, l, now);  // the next frame keeps it
        CHECK(p.text() == shown, "a greeting keeps its line while it shows");
        now += 50;
        return shown;
    };
    {  // Random: the same game line (Ramone), yet a new line at each greeting, never the same twice in a row
        npcspeech::Core c;
        CHECK(c.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_RANDOM, 0), "set_lines random");
        uint64_t now = 1000;
        uint32_t prev = greet(c, 7, now), changes = 0;
        bool repeat = false;
        for (int i = 0; i < 30; ++i) {
            const uint32_t t = greet(c, 7, now);
            CHECK(t == 0xa1 || t == 0xa2 || t == 0xa3, "random: one of ours");
            repeat |= t == prev;
            changes += t != prev;
            prev = t;
        }
        CHECK(!repeat && changes == 30, "random: a new line at every greeting, never the same twice in a row");
        // a glance away shorter than kReopenMs is the same greeting
        Params p;
        p.set(2262475715u, 0);
        c.draw(7, p.b, l, now);
        p.set(2262475715u, 1);
        c.draw(7, p.b, l, now + npcspeech::kReopenMs / 2);
        CHECK(p.text() == prev, "hidden for less than kReopenMs: the same greeting, the same line");
        const uint32_t one[] = {0xb1};
        c.set_lines(8, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, one, 1, KILN_SPEECH_PICK_RANDOM, 0);
        CHECK(greet(c, 8, now) == 0xb1 && greet(c, 8, now) == 0xb1, "random with one line: that line");
    }
    {  // the NPC leaves the HUD's NPC layer (no draws at all) and comes back: a new greeting, as after looking away
        npcspeech::Core c;
        c.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_ORDER, 0);
        Params p;
        p.set(2262475715u, 1);
        c.draw(7, p.b, l, 1000);
        const uint32_t a1 = p.text();
        p.set(2262475715u, 1);
        c.draw(7, p.b, l, 1000 + npcspeech::kReopenMs + 500);  // the next draw after a long gap, shown at once
        CHECK(a1 == 0xa1 && p.text() == 0xa2, "back in the layer after a gap: the next line");
        p.set(2262475715u, 1);
        c.draw(7, p.b, l, 1000 + npcspeech::kReopenMs + 550);
        CHECK(p.text() == 0xa2, "and it stays while it shows");
    }
    {  // the v1 rule stays for set(): a new pick only when the game's pick changes
        npcspeech::Core c;
        c.set(7, KILN_SPEECH_REPLACE, tags, 3);
        uint64_t now = 1000;
        const uint32_t first = greet(c, 7, now);
        bool same = true;
        for (int i = 0; i < 10; ++i) same &= greet(c, 7, now) == first;
        CHECK(same, "set() (PICK_CHANGE): the same game line keeps the same line of ours, as in 31.16");
    }
    {  // InOrder: the lines in turn from seed % n; Seeded: the same line for the same open, wherever it runs
        npcspeech::Core c;
        c.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_ORDER, 1);
        uint64_t now = 1000;
        uint32_t seq[6];
        for (auto& t : seq) t = greet(c, 7, now);
        CHECK(seq[0] == 0xa2 && seq[1] == 0xa3 && seq[2] == 0xa1 && seq[3] == 0xa2 && seq[4] == 0xa3 && seq[5] == 0xa1, "in order from seed 1");
        npcspeech::Core a, b;
        a.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_SEEDED, 42);
        b.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_SEEDED, 42);
        uint64_t na = 1000, nb = 777777;
        bool equal = true;
        uint32_t distinct = 0, prev = 0;
        for (int i = 0; i < 12; ++i) {
            const uint32_t x = greet(a, 7, na), y = greet(b, 7, nb);
            equal &= x == y;
            distinct += x != prev;
            prev = x;
        }
        CHECK(equal, "seeded: two clients show the same line for the same open");
        CHECK(distinct >= 4, "seeded: the lines vary across opens");
        CHECK(npcspeech::Core::seeded(42, 7, 0, 3) == npcspeech::Core::seeded(42, 7, 0, 3) &&
                  npcspeech::Core::seeded(42, 7, 0, 3) < 3,
              "seeded: a pure function of seed, NPC, open and count");
    }
    {  // HOST (By): the line next() gave, reported once per open; -1 = the game's line; none yet: random among ours
        npcspeech::Core c;
        c.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_HOST, 0);
        uint64_t now = 1000;
        const uint32_t before = greet(c, 7, now);
        CHECK(before == 0xa1 || before == 0xa2 || before == 0xa3, "host pick not there yet: one of ours");
        KilnSpeechEvent ev[8];
        uint32_t n = c.drain(ev, 8);
        CHECK(n == 1 && ev[0].npc == 7 && ev[0].type == KILN_SPEECH_EVENT_OPEN && ev[0].kind == KILN_SPEECH_KIND_GREETING &&
                  ev[0].value == 1,
              "an open of a host-picked line is reported (its count)");
        CHECK(c.next(7, KILN_SPEECH_KIND_GREETING, 2), "next() for host lines");
        CHECK(greet(c, 7, now) == 0xa3, "the host's pick shows at the next greeting");
        CHECK(greet(c, 7, now) == 0xa3, "and again until the host sends another");
        n = c.drain(ev, 8);
        CHECK(n == 2 && ev[0].line == 2 && ev[0].value == 2 && ev[1].value == 3, "each open reported with the line shown");
        c.next(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_LINE_GAME);
        CHECK(greet(c, 7, now) == 2262475715u, "-1: the game's own line this time");
        n = c.drain(ev, 8);
        CHECK(n == 1 && ev[0].line == -1, "reported as the game's line");
        CHECK(!c.next(7, KILN_SPEECH_KIND_GREETING, 3), "an index past the lines is refused");
        CHECK(!c.next(9, KILN_SPEECH_KIND_GREETING, 0), "next() for an NPC without host lines is refused");
        c.set_lines(8, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_RANDOM, 0);
        greet(c, 8, now);
        CHECK(c.drain(ev, 8) == 0, "random lines report nothing (no traffic per greeting)");
        const auto k = c.counts();
        CHECK(k.opens >= 5, "opens are counted");
    }
    {  // the event queue is bounded: the oldest go, the count of dropped ones says so
        npcspeech::Core c;
        c.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_HOST, 0);
        uint64_t now = 1000;
        for (uint32_t i = 0; i < npcspeech::kMaxEvents + 10; ++i) greet(c, 7, now);
        KilnSpeechEvent ev[4];
        CHECK(c.drain(ev, 4) == 4 && ev[0].value == 11, "the oldest events were dropped first");
        CHECK(c.counts().dropped == 10, "dropped events are counted");
    }
    {  // set_lines checks its arguments; the same lines again keep the rotation going
        const uint32_t zero[] = {0xa1, 0};
        CHECK(!npcspeech::Core::valid_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, 5), "unknown pick");
        CHECK(!npcspeech::Core::valid_lines(7, 3, KILN_SPEECH_REPLACE, tags, 3, 1), "unknown kind");
        CHECK(!npcspeech::Core::valid_lines(7, KILN_SPEECH_KIND_FAREWELL, KILN_SPEECH_SUPPRESS, nullptr, 0, 1), "a farewell is replaced or the game's");
        CHECK(!npcspeech::Core::valid_lines(7, KILN_SPEECH_KIND_MENU, KILN_SPEECH_REPLACE, zero, 2, 1), "a zero tag");
        CHECK(npcspeech::Core::valid_lines(7, KILN_SPEECH_KIND_MENU, KILN_SPEECH_GAME, nullptr, 0, 0), "clearing the menu line");
        npcspeech::Core c;
        c.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_ORDER, 0);
        uint64_t now = 1000;
        const uint32_t a = greet(c, 7, now);
        c.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_REPLACE, tags, 3, KILN_SPEECH_PICK_ORDER, 0);  // a host resends it
        CHECK(a == 0xa1 && greet(c, 7, now) == 0xa2, "the same lines again: the rotation goes on");
        c.set_lines(7, KILN_SPEECH_KIND_GREETING, KILN_SPEECH_GAME, nullptr, 0, 0, 0);
        Params p;
        p.set(2262475715u, 1);
        c.draw(7, p.b, l, now + 5000);
        CHECK(p.text() == 2262475715u && c.tracked() == 0, "cleared through set_lines: the game's line, NPC forgotten");
    }
    {  // Say: over an override, shown even when the game hides the bubble, ends at its time
        npcspeech::Core c;
        c.set(7, KILN_SPEECH_SUPPRESS, nullptr, 0);
        c.say(7, 0xbeef, 5000, 1000);
        Params p;
        p.set(0, 0);
        c.draw(7, p.b, l, 2000);
        CHECK(p.text() == 0xbeef && p.b[4] == 1, "Say shows over Suppress, also when the game hides it");
        p.set(0, 0);
        c.draw(7, p.b, l, 6001);
        CHECK(p.b[4] == 0, "after its time: back to Suppress");
        c.set(7, KILN_SPEECH_GAME, nullptr, 0);
        c.say(9, 0xcafe, 1000, 0);
        c.say(9, 0, 0, 10);
        p.set(5, 1);
        c.draw(9, p.b, l, 20);
        CHECK(p.text() == 5, "Say ended by tag 0");
    }
    {  // voice: mute both greeting lists while speaking, put them back after
        npcspeech::Core c;
        Record r(2, 3);
        c.set(7, KILN_SPEECH_REPLACE, tags, 1);
        c.voice(7, r.mem.data(), l);
        CHECK(r.vo_byte(false, 0) == 0 && r.vo_byte(false, 1) == 0 && r.vo_byte(true, 2) == 0, "muted");
        // the game rebuilds the record (refills a voice-over): muted again, first copy kept
        std::memset(&r.plain[1 * 0x294 + 0x8c], 0x41, 16);
        c.voice(7, r.mem.data(), l);
        CHECK(r.vo_byte(true, 1) == 0, "refilled slot muted again");
        c.set(7, KILN_SPEECH_GAME, nullptr, 0);
        CHECK(c.active(), "still active until the voice-overs are back");
        c.voice(7, r.mem.data(), l);
        CHECK(r.vo_byte(false, 0) == 0x10 && r.vo_byte(false, 1) == 0x11, "priority back");
        CHECK(r.vo_byte(true, 0) == 0x40 && r.vo_byte(true, 1) == 0x41 && r.vo_byte(true, 2) == 0x42, "plain back");
        CHECK(!c.active() && c.tracked() == 0, "forgotten after restore");
    }
    {  // a list that does not look like one is left alone; clear_all keeps the restore
        npcspeech::Core c;
        Record r(1, 1);
        uint64_t huge = 100000;
        std::memcpy(&r.mem[0x5340], &huge, 8);
        c.set(7, KILN_SPEECH_SUPPRESS, nullptr, 0);
        c.voice(7, r.mem.data(), l);
        CHECK(r.vo_byte(true, 0) == 0x40, "an implausible count is not walked");
        CHECK(r.vo_byte(false, 0) == 0, "the other list muted");
        c.clear_all();
        CHECK(c.active(), "clear_all keeps the NPC whose voice-overs are muted");
        c.voice(7, r.mem.data(), l);
        CHECK(r.vo_byte(false, 0) == 0x10 && !c.active(), "restored after clear_all");
        npcspeech::Layout none;
        c.set(8, KILN_SPEECH_SUPPRESS, nullptr, 0);
        Record r2(1, 1);
        c.voice(8, r2.mem.data(), none);
        CHECK(r2.vo_byte(false, 0) == 0x10, "unknown layout: the record is not touched");
    }
    {  // a record the NPC no longer has is not written to
        npcspeech::Core c;
        Record a(1, 1), b(1, 1);
        c.set(7, KILN_SPEECH_SUPPRESS, nullptr, 0);
        c.voice(7, a.mem.data(), l);
        c.set(7, KILN_SPEECH_GAME, nullptr, 0);
        c.voice(7, b.mem.data(), l);
        CHECK(a.vo_byte(false, 0) == 0 && b.vo_byte(false, 0) == 0x10, "an old record is not written to; a new one keeps its own");
    }
    {  // the game moves the record (its map grows: every record is copied to new storage, Kiln's zeros with it)
        npcspeech::Core c;
        Record a(2, 2);
        c.set(7, KILN_SPEECH_REPLACE, tags, 1);
        c.voice(7, a.mem.data(), l);
        Record b(2, 2);
        b.prio = a.prio;  // the copy carries the muted entries
        b.plain = a.plain;
        CHECK(b.vo_byte(false, 0) == 0 && b.vo_byte(true, 1) == 0, "the moved copy is muted too");
        c.voice(7, b.mem.data(), l);  // still speaking, at the new address
        c.set(7, KILN_SPEECH_GAME, nullptr, 0);
        c.voice(7, b.mem.data(), l);
        CHECK(b.vo_byte(false, 0) == 0x10 && b.vo_byte(false, 1) == 0x11, "moved record: priority voice-overs back");
        CHECK(b.vo_byte(true, 0) == 0x40 && b.vo_byte(true, 1) == 0x41, "moved record: plain voice-overs back");
        CHECK(!c.active() && c.tracked() == 0, "moved record: forgotten after restore");
    }
    {  // a rebuilt list: a refill with another voice-over is the one put back; a slot whose line changed is left alone
        npcspeech::Core c;
        Record r(1, 2);
        const uint32_t line_a = 111, line_b = 222;
        std::memcpy(&r.plain[0], &line_a, 4);
        std::memcpy(&r.plain[0x294], &line_a, 4);
        c.set(7, KILN_SPEECH_SUPPRESS, nullptr, 0);
        c.voice(7, r.mem.data(), l);
        std::memset(&r.plain[0 * 0x294 + 0x8c], 0x77, 16);  // the game refills entry 0 with another voice-over
        c.voice(7, r.mem.data(), l);
        std::memcpy(&r.plain[0x294], &line_b, 4);  // entry 1 is now another line, without a voice-over
        c.set(7, KILN_SPEECH_GAME, nullptr, 0);
        c.voice(7, r.mem.data(), l);
        CHECK(r.vo_byte(true, 0) == 0x77, "the game's latest voice-over is the one put back");
        CHECK(r.vo_byte(true, 1) == 0, "a slot that now holds another line gets no voice-over of the old one");
    }
    {  // without the record layout an ended Say is dropped at its next draw (nothing left to do for that NPC)
        npcspeech::Core c;
        c.say(9, 0xbeef, 1000, 0);
        Params p;
        p.set(5, 1);
        c.draw(9, p.b, l, 2000);
        CHECK(p.text() == 5 && c.tracked() == 0 && !c.active(), "an ended Say is forgotten at its draw");
    }
    {  // concurrent writers and a drawing thread
        npcspeech::Core c;
        std::thread w([&] {
            for (int i = 0; i < 20000; ++i) {
                c.set(1 + i % 5, i % 3, tags, 3);
                c.say(1 + i % 7, i % 2 ? 0xbeef : 0, 1000, i);
            }
        });
        Params p;
        for (int i = 0; i < 20000; ++i) {
            p.set(42, 1);
            c.draw(1 + i % 5, p.b, l, i);
        }
        w.join();
        c.clear_all();
        CHECK(!c.active(), "clear_all after the race");
    }
    const auto k = npcspeech::Core().counts();
    CHECK(k.overrides == 0 && k.says == 0, "fresh counts");
    std::printf("npcspeech core: %d PASS, %d FAIL\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
