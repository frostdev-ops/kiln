// SPDX-License-Identifier: MIT
// host tests of kiln.labbot's portable part (bot.cpp), no game: test-labbot-host.sh. Numbered:
//  1 FNV-1a and the character upload, chunk, ack and final-request bytes as the game's Save2 receive reads them;
//  2 the Login message at the reader's offsets; a name too long is refused;
//  3 names, derived character ids and accounts (stable, distinct, GUID-shaped), handles;
//  4 the published state: entries written, a full player array asks for room, a slot or machine the session uses is
//    never overwritten, rewriting is idempotent;
//  5 a join on a fake GameServer that moves records and saves as the game does: login, upload, ready, entity, in game;
//  6 saves acknowledged once per version while in game;
//  7 a clean leave: final request, the record ends its game, the final save is acked, the record frees: Left;
//  8 a drop, and a leave before the bot is in game (always a drop);
//  9 failures, each with its reason: never in the published state, the login refused, the upload refused, no spawn
//    position, the record taken over; the game removing an in-game bot is Left with the record's state;
// 10 a leave whose record never frees is dropped after the limit.
#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>

#include "../bot.h"

using namespace labbot;

namespace {

int g_checks = 0, g_failures = 0;
#define CHECK(cond, ...)                                                   \
    do {                                                                   \
        ++g_checks;                                                        \
        if (!(cond)) {                                                     \
            ++g_failures;                                                  \
            std::printf("FAIL line %d: %s: ", __LINE__, #cond);            \
            std::printf(__VA_ARGS__);                                      \
            std::printf("\n");                                             \
        }                                                                  \
    } while (0)

uint32_t rd32(const uint8_t* p) { return p[0] | p[1] << 8 | p[2] << 16 | uint32_t(p[3]) << 24; }
uint16_t rd16(const uint8_t* p) { return uint16_t(p[0] | p[1] << 8); }

// ---- 1 ----
void t_wire() {
    CHECK(fnv1a32(nullptr, 0) == 0x811c9dc5u, "empty");
    const uint8_t a[] = {'a'};
    CHECK(fnv1a32(a, 1) == 0xe40c292cu, "'a' = %08x", fnv1a32(a, 1));
    const auto up = new_character_upload();
    const std::vector<uint8_t> want = {0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0};
    CHECK(up == want, "upload is %zu bytes", up.size());
    const auto c = save_chunk(up);
    CHECK(c.size() == 1 + 4 + 4 + 4 + 2 + up.size(), "chunk size %zu", c.size());
    if (c.size() >= 15) {
        CHECK(c[0] == 1, "chunk type");
        CHECK(rd32(&c[1]) == fnv1a32(up.data(), up.size()) && rd32(&c[1]) != 0, "hash");
        CHECK(rd32(&c[5]) == up.size(), "total");
        CHECK(rd32(&c[9]) == 0, "offset");
        CHECK(rd16(&c[13]) == up.size(), "length");
        CHECK(std::memcmp(&c[15], up.data(), up.size()) == 0, "bytes");
    }
    CHECK(save_chunk(std::vector<uint8_t>(70000, 1)).empty(), "a body over one chunk is refused");
    const auto k = save_ack(0x01020304);
    CHECK(k.size() == 6 && k[0] == 0 && k[1] == 4 && rd32(&k[2]) == 0x01020304, "ack");
    const auto f = save_final_request();
    CHECK(f.size() == 2 && f[0] == 0 && f[1] == 2, "final request");
}

// ---- 2 ----
void t_login() {
    LoginLayout L;
    uint8_t id[16];
    for (int i = 0; i < 16; ++i) id[i] = uint8_t(0xa0 + i);
    std::vector<uint8_t> m(L.size, 0xcc);
    CHECK(write_login(L, "Bot Alder", id, true, m.data(), m.size()), "written");
    CHECK(m[L.type] == 0, "type Login");
    CHECK(rd32(&m[L.name_len]) == 9 && std::memcmp(&m[L.name], "Bot Alder", 9) == 0, "name");
    CHECK(m[L.character_present] == 1 && std::memcmp(&m[L.character], id, 16) == 0, "character");
    CHECK(m[L.toggle_present] == 1, "the receive requires inputToggleConfig");
    CHECK(m[L.version_present] == 0, "no build version: the server skips its check");
    CHECK(m[L.new_present] == 1 && m[L.is_new] == 1, "new character");
    size_t other = 0;
    for (size_t i = L.name + 9; i < L.character_present; ++i) other += m[i] != 0;
    CHECK(other == 0, "the rest of the name field is zero");
    CHECK(!write_login(L, std::string(0x101, 'x'), id, true, m.data(), m.size()), "a name over the field");
    CHECK(!write_login(L, "x", id, true, m.data(), L.size - 1), "a buffer smaller than the message");
}

// ---- 3 ----
void t_identity() {
    std::string why;
    CHECK(valid_name("labbot-1", &why), "%s", why.c_str());
    CHECK(valid_name("Bot Alder_2.b", &why), "%s", why.c_str());
    CHECK(!valid_name("", &why), "empty");
    CHECK(!valid_name(std::string(33, 'a'), &why), "too long");
    CHECK(!valid_name(" a", &why) && !valid_name("a ", &why), "edge spaces");
    CHECK(!valid_name("a\nb", &why) && !valid_name("a/b", &why) && !valid_name("\xc3\xa9", &why), "bad characters");
    uint8_t a[16], b[16], c[16];
    derive_character("alder", a);
    derive_character("alder", b);
    derive_character("birch", c);
    CHECK(std::memcmp(a, b, 16) == 0, "stable");
    CHECK(std::memcmp(a, c, 16) != 0, "distinct");
    CHECK((a[6] >> 4) == 5 && (a[8] & 0xc0) == 0x80, "version 5, RFC 4122 variant: %02x %02x", a[6], a[8]);
    CHECK(derive_account("alder") == derive_account("alder") && derive_account("alder") != derive_account("birch"), "accounts");
    CHECK(derive_account("alder") != 0, "nonzero account");
    CHECK(machine_handle(16) == (0x55u << 7 | 16) && (machine_handle(16) & 0x7f) == 16, "machine handle");
    CHECK(player_handle(15) == (0x55u << 6 | 15) && (player_handle(15) & 0x3f) == 15, "player handle");
}

// ---- 4: a buffer with the snapshot's layout; each machine's player array points at storage of the test's own ----
struct Snap {
    SnapLayout L;
    std::vector<uint8_t> buf;
    std::vector<std::vector<uint32_t>> arrays;
    Snap() : buf(L.size, 0), arrays(L.machine_count) {}
    uint8_t* m(uint32_t i) { return &buf[L.machines + i * L.machine_stride]; }
    uint8_t* p(uint32_t s) { return &buf[L.players + s * L.player_stride]; }
    void give(uint32_t i, size_t cap) {  // the game's reserve: room for `cap` handles
        arrays[i].assign(cap, 0);
        uint8_t* a = m(i) + L.m_players;
        uint32_t* data = arrays[i].data();
        uint64_t c = cap;
        std::memcpy(a, &data, sizeof data);
        std::memcpy(a + 16, &c, 8);
    }
    uint64_t count(uint32_t i) {
        uint64_t n;
        std::memcpy(&n, m(i) + L.m_players + 8, 8);
        return n;
    }
};

void t_snapshot() {
    Snap s;
    CHECK(s.L.valid(), "default layout");
    SnapEntry e;
    e.machine_index = 16;
    e.slot = 15;
    e.machine = machine_handle(16);
    e.player = player_handle(15);
    e.group = 0x1234;
    e.account = 0x1122334455667788ull;
    CHECK(put_entries(s.buf.data(), s.L, e) == Put::NeedCapacity, "no room in the machine's player array");
    CHECK(!has_entries(s.buf.data(), s.L, e), "not listed yet");
    s.give(16, 1);
    CHECK(put_entries(s.buf.data(), s.L, e) == Put::Ok, "written");
    CHECK(has_entries(s.buf.data(), s.L, e), "listed");
    CHECK(rd32(s.p(15)) == e.player && rd32(s.p(15) + s.L.p_machine) == e.machine && rd32(s.p(15) + s.L.p_group) == 0x1234,
          "player entry");
    CHECK(rd32(s.p(15) + s.L.p_remote) != 0, "a remote player");
    uint64_t acct;
    std::memcpy(&acct, s.p(15) + s.L.p_account, 8);
    CHECK(acct == e.account, "account");
    CHECK(rd32(s.m(16)) == e.machine && s.m(16)[s.L.m_remote] == 1, "machine entry");
    CHECK(s.count(16) == 1 && s.arrays[16][0] == e.player, "the machine lists the player");
    CHECK(put_entries(s.buf.data(), s.L, e) == Put::Ok && s.count(16) == 1, "idempotent");
    CHECK(player_in(s.buf.data(), s.L, 15) == e.player && machine_in(s.buf.data(), s.L, 16) == e.machine, "readers");
    // the session uses the slot (a real player joined there): nothing of the bot's is written
    Snap t;
    t.give(16, 4);
    const uint32_t real = 0x40 | 15;
    std::memcpy(t.p(15), &real, 4);
    CHECK(put_entries(t.buf.data(), t.L, e) == Put::Taken, "slot taken");
    CHECK(rd32(t.m(16)) == 0 && t.count(16) == 0, "machine untouched when the slot is taken");
    Snap u;
    u.give(16, 4);
    const uint32_t realm = 0x80 | 16;
    std::memcpy(u.m(16), &realm, 4);
    CHECK(put_entries(u.buf.data(), u.L, e) == Put::Taken && rd32(u.p(15)) == 0, "machine taken: player untouched");
    SnapEntry bad = e;
    bad.slot = 16;
    CHECK(put_entries(s.buf.data(), s.L, bad) == Put::Bad, "slot out of range");
    // the bot left: its entries go from the buffer the session copies forward; a real connection's never do
    CHECK(clear_entries(s.buf.data(), s.L, e), "cleared");
    CHECK(!has_entries(s.buf.data(), s.L, e) && player_in(s.buf.data(), s.L, 15) == 0 && machine_in(s.buf.data(), s.L, 16) == 0,
          "no entries left");
    CHECK(s.count(16) == 0 && s.m(16)[s.L.m_remote] == 0 && rd32(s.p(15) + s.L.p_remote) == 0, "machine list and remote marks reset");
    std::memcpy(&acct, s.p(15) + s.L.p_account, 8);
    CHECK(acct == 0 && rd32(s.p(15) + s.L.p_group) == 0, "account and group reset");
    CHECK(!clear_entries(s.buf.data(), s.L, e), "nothing left to clear");
    CHECK(!clear_entries(t.buf.data(), t.L, e) && rd32(t.p(15)) == real, "a real player's slot is never cleared");
    CHECK(!clear_entries(u.buf.data(), u.L, e) && rd32(u.m(16)) == realm, "a real machine is never cleared");
    CHECK(!clear_entries(s.buf.data(), s.L, bad), "slot out of range: nothing");
}

// ---- 5..10: a fake GameServer ----
struct FakeGame : Engine {
    LoginLayout L;
    bool visible = true;           // the published state shows the bot
    int visible_after = 0;         // ... from this many in_snapshot calls on
    int snapshot_calls = 0;
    bool refuse_login = false;     // record state 6 at login (version / full)
    bool refuse_upload = false;    // Save2 status 9
    bool no_spawn = false;         // the spawn search fails: record state 6
    bool never_free = false;       // a leaving record never frees
    int load_frames = 3;           // spawn search
    uint64_t upload_capacity = 0x40000;  // Save2's upload buffer (save +0x110 {data, size, capacity})
    int save_after = 0;            // frames after the login until Save2 takes the player (waits for its upload)
    struct Rec { uint32_t handle = 0, peer = 0, entity = 0; uint8_t state = 0, perms = 0x3f; int wait = 0; };
    struct Sav { uint32_t handle = 0, version = 0; uint8_t state = 0, status = 3, has_entity = 0; bool final = false; int since = 0; };
    Rec rec[16];
    Sav sav[16];
    int pending_save[16] = {};
    std::vector<std::string> calls;
    std::string login_name;
    uint8_t login_char[16] = {};
    int ready_types = 0;
    uint64_t frame_no = 0;

    bool in_snapshot(const SnapEntry&) override { return visible && ++snapshot_calls > visible_after; }
    bool add_player(uint32_t machine, const uint8_t* m) override {
        calls.push_back("add_player");
        const uint32_t slot = machine == machine_handle(16) ? 15 : 14;
        const uint32_t n = rd32(m + L.name_len);
        login_name.assign(reinterpret_cast<const char*>(m + L.name), n);
        std::memcpy(login_char, m + L.character, 16);
        if (m[L.type] != 0 || !m[L.character_present] || !m[L.toggle_present]) return true;  // the game ignores it
        rec[slot] = Rec{player_handle(slot), machine, 0, uint8_t(refuse_login ? 6 : 1), 0x3f, load_frames};
        if (save_after) pending_save[slot] = save_after;
        else sav[slot] = Sav{player_handle(slot), 0, 1, 3, 0};
        return true;
    }
    RecordView record(uint32_t s) override {
        RecordView v;
        v.readable = true;
        v.handle = rec[s].handle;
        v.peer = rec[s].peer;
        v.entity = rec[s].entity;
        v.state = rec[s].state;
        v.permissions = rec[s].perms;
        return v;
    }
    SaveView save(uint32_t s) override {
        SaveView v;
        v.readable = true;
        v.handle = sav[s].handle;
        v.version = sav[s].version;
        v.state = sav[s].state;
        v.status = sav[s].status;
        v.has_entity = sav[s].has_entity;
        v.upload_capacity = upload_capacity;
        return v;
    }
    bool save_receive(uint32_t player, const std::vector<uint8_t>& m, bool* fin) override {
        *fin = false;
        Sav& r = sav[player & 0x3f];
        if (r.handle != player || m.empty()) return true;
        if (m[0] == 1) {
            calls.push_back("upload");
            if (r.state != 1) return true;
            const std::vector<uint8_t> body(m.begin() + 15, m.end());
            if (rd32(&m[1]) != fnv1a32(body.data(), body.size())) return true;
            r.state = 2;
            r.status = refuse_upload ? 9 : 0;
            r.has_entity = body.size() > 1 && body[1] == 1;
        } else if (m[0] == 0 && m.size() >= 2) {
            if (m[1] & 2) {
                calls.push_back("final");
                r.final = true;
                *fin = true;
            }
            if ((m[1] & 4) && m.size() >= 6 && rd32(&m[2]) == r.version && r.state == 4) {
                calls.push_back("ack " + std::to_string(r.version));
                r.state = r.final ? 5 : 2;
            }
        }
        return true;
    }
    bool machine_login(uint32_t machine, uint8_t type) override {
        calls.push_back("ready " + std::to_string(type));
        ++ready_types;
        if (type == 2)
            for (Rec& x : rec)
                if (x.peer == machine && x.state == 2) x.state = 3;
        return true;
    }
    bool end_game(uint32_t slot) override {
        calls.push_back("end");
        if (rec[slot].state == 3) rec[slot].state = 4;
        return true;
    }
    // one server frame of the game's own update (before the bot's step)
    void frame() {
        ++frame_no;
        for (uint32_t s = 0; s < 16; ++s) {
            Rec& x = rec[s];
            Sav& r = sav[s];
            if (pending_save[s] && --pending_save[s] == 0) r = Sav{player_handle(s), 0, 1, 3, 0};
            if (!x.handle) continue;
            if (x.state == 1 && r.state != 1 && r.handle && --x.wait <= 0) x.state = no_spawn ? 6 : 2;
            if (x.state == 3 && !x.entity && r.status == 0) x.entity = s + 1;
            if (x.state == 3 && frame_no % 50 == 0 && r.state == 2) {  // a world save goes out
                ++r.version;
                r.state = 4;
                r.since = 0;
            }
            if (r.state == 4 && ++r.since > 300) r.state = r.final ? 6 : 2;  // the 5 s ack timeout
            if (x.state == 4) {  // remove the entity, send the final save
                x.entity = 0;
                x.state = 5;
                ++r.version;
                r.state = 4;
                r.since = 0;
            } else if (x.state == 5 && (r.state == 5 || r.state == 6) && !never_free) {
                x = Rec{};
                r = Sav{};
            }
        }
    }
};

Bot make_bot() {
    Bot b;
    b.name = "Bot Alder";
    derive_character(b.name, b.character);
    b.account = derive_account(b.name);
    b.entry.machine_index = 16;
    b.entry.slot = 15;
    b.entry.machine = machine_handle(16);
    b.entry.player = player_handle(15);
    b.entry.group = 7;
    b.entry.account = b.account;
    b.phase = Phase::Connecting;
    return b;
}

// runs frames until `until` holds or `max` frames pass; returns the frames used
int run(FakeGame& g, Bot& b, int max, bool (*until)(const Bot&), uint64_t* step) {
    for (int i = 0; i < max; ++i) {
        g.frame();
        b.step(g, ++*step, g.L);
        if (until(b)) return i + 1;
    }
    return -1;
}
bool in_game(const Bot& b) { return b.phase == Phase::InGame; }
bool ended(const Bot& b) { return b.phase == Phase::Left || b.phase == Phase::Failed; }

void t_join() {
    FakeGame g;
    g.visible_after = 2;
    Bot b = make_bot();
    uint64_t step = 100;
    const int n = run(g, b, 200, in_game, &step);
    CHECK(n > 0, "in game: phase %s, reason %s", phase_name(b.phase), b.reason.c_str());
    CHECK(b.entity == 16, "entity %u", b.entity);
    CHECK(g.login_name == "Bot Alder" && std::memcmp(g.login_char, b.character, 16) == 0, "the login named the bot's character");
    CHECK(g.calls.size() >= 4 && g.calls[0] == "add_player" && g.calls[1] == "upload" && g.calls[2] == "ready 1" && g.calls[3] == "ready 2",
          "order: %s %s %s %s", g.calls.size() > 0 ? g.calls[0].c_str() : "", g.calls.size() > 1 ? g.calls[1].c_str() : "",
          g.calls.size() > 2 ? g.calls[2].c_str() : "", g.calls.size() > 3 ? g.calls[3].c_str() : "");
    int adds = 0, uploads = 0;
    for (auto& c : g.calls) adds += c == "add_player", uploads += c == "upload";
    CHECK(adds == 1 && uploads == 1, "one login (%d), one upload (%d)", adds, uploads);
    CHECK(b.injected(), "listed while in game");
    CHECK(b.in_game > b.joined && b.permissions == 0x3f && b.record_state == 3, "in game at %llu, perms %02x",
          (unsigned long long)b.in_game, b.permissions);
}

void t_acks() {
    FakeGame g;
    Bot b = make_bot();
    uint64_t step = 0;
    run(g, b, 200, in_game, &step);
    for (int i = 0; i < 400; ++i) {
        g.frame();
        b.step(g, ++step, g.L);
    }
    int acks = 0;
    for (auto& c : g.calls) acks += c.rfind("ack ", 0) == 0;
    CHECK(acks >= 7 && acks == int(b.saves_acked), "acks %d, counted %u", acks, b.saves_acked);
    CHECK(g.sav[15].state != 4 || g.sav[15].since < 5, "no save left waiting for its ack");
    CHECK(b.phase == Phase::InGame, "still in game: %s", phase_name(b.phase));
}

void t_leave() {
    FakeGame g;
    Bot b = make_bot();
    uint64_t step = 0;
    run(g, b, 200, in_game, &step);
    b.leave(false);
    const int n = run(g, b, 400, ended, &step);
    CHECK(n > 0 && b.phase == Phase::Left, "left: %s (%s)", phase_name(b.phase), b.reason.c_str());
    bool fin = false, end = false, final_ack = false;
    for (auto& c : g.calls) fin |= c == "final", end |= c == "end", final_ack |= c.rfind("ack ", 0) == 0 && g.sav[15].handle == 0;
    CHECK(fin && end, "final request and end of game");
    CHECK(g.rec[15].handle == 0, "the record is free");
    CHECK(!b.injected(), "no longer listed");
    CHECK(b.reason.find("logged out") != std::string::npos, "reason %s", b.reason.c_str());
}

void t_drop() {
    FakeGame g;
    Bot b = make_bot();
    uint64_t step = 0;
    run(g, b, 200, in_game, &step);
    b.leave(true);
    g.frame();
    b.step(g, ++step, g.L);
    CHECK(b.phase == Phase::Left && !b.injected(), "dropped at once: %s", phase_name(b.phase));
    CHECK(b.reason.find("dropped") != std::string::npos, "reason %s", b.reason.c_str());
    FakeGame h;
    h.visible = false;
    Bot c = make_bot();
    c.step(h, 1, h.L);
    c.leave(false);
    c.step(h, 2, h.L);
    CHECK(c.phase == Phase::Left && !c.injected(), "a leave before the game: a drop (%s)", phase_name(c.phase));
}

void t_failures() {
    struct Case {
        const char* what;
        void (*setup)(FakeGame&);
        const char* reason;
    } cases[] = {
        {"never published", [](FakeGame& g) { g.visible = false; }, "published"},
        {"login refused", [](FakeGame& g) { g.refuse_login = true; }, "refused the login"},
        {"upload refused", [](FakeGame& g) { g.refuse_upload = true; }, "upload"},
        {"no spawn position", [](FakeGame& g) { g.no_spawn = true; }, "spawn"},
    };
    for (const Case& c : cases) {
        FakeGame g;
        c.setup(g);
        Bot b = make_bot();
        uint64_t step = 0;
        Limits lim;
        lim.connect = lim.login = lim.load = lim.spawn = 100;
        for (int i = 0; i < 400 && !ended(b); ++i) {
            g.frame();
            b.step(g, ++step, g.L, lim);
        }
        CHECK(b.phase == Phase::Failed && !b.injected(), "%s: %s", c.what, phase_name(b.phase));
        CHECK(b.reason.find(c.reason) != std::string::npos, "%s: reason '%s'", c.what, b.reason.c_str());
    }
    // the server's upload buffer is smaller than the character: refused before anything is sent, with both sizes
    {
        FakeGame g;
        g.upload_capacity = 8;
        Bot b = make_bot();
        uint64_t step = 0;
        for (int i = 0; i < 20 && !ended(b); ++i) {
            g.frame();
            b.step(g, ++step, g.L);
        }
        bool uploaded = false;
        for (auto& c : g.calls) uploaded |= c == "upload";
        CHECK(b.phase == Phase::Failed && !uploaded, "too big: %s, uploaded %d", phase_name(b.phase), uploaded);
        CHECK(b.reason.find("14 bytes") != std::string::npos && b.reason.find("8") != std::string::npos, "reason '%s'", b.reason.c_str());
        FakeGame h;
        h.upload_capacity = 0;  // not known on this build: the bot sends it and the game decides
        Bot c = make_bot();
        step = 0;
        CHECK(run(h, c, 200, in_game, &step) > 0, "unknown capacity: in game (%s)", c.reason.c_str());
    }
    // Save2 takes the player some frames after the login: the upload waits for it and goes once (an early one is dropped)
    {
        FakeGame g;
        g.save_after = 6;
        Bot b = make_bot();
        uint64_t step = 0;
        const int n = run(g, b, 200, in_game, &step);
        int uploads = 0;
        for (auto& c : g.calls) uploads += c == "upload";
        CHECK(n > 0 && uploads == 1, "late save slot: %s, %d upload(s), reason '%s'", phase_name(b.phase), uploads, b.reason.c_str());
    }
    // taken over: the record names another handle while the bot logs in
    {
        FakeGame g;
        g.load_frames = 50;
        Bot b = make_bot();
        uint64_t step = 0;
        for (int i = 0; i < 5; ++i) {
            g.frame();
            b.step(g, ++step, g.L);
        }
        g.rec[15].handle = 0x40 | 15;
        g.frame();
        b.step(g, ++step, g.L);
        CHECK(b.phase == Phase::Failed && b.reason.find("record") != std::string::npos, "taken over: %s '%s'", phase_name(b.phase), b.reason.c_str());
    }
    // the game removes an in-game bot (a kick): Left, with the record's state
    {
        FakeGame g;
        Bot b = make_bot();
        uint64_t step = 0;
        run(g, b, 200, in_game, &step);
        g.rec[15] = FakeGame::Rec{};
        g.frame();
        b.step(g, ++step, g.L);
        CHECK(b.phase == Phase::Left && b.reason.find("removed") != std::string::npos, "kicked: %s '%s'", phase_name(b.phase), b.reason.c_str());
    }
}

void t_leave_timeout() {
    FakeGame g;
    g.never_free = true;
    Bot b = make_bot();
    uint64_t step = 0;
    run(g, b, 200, in_game, &step);
    b.leave(false);
    Limits lim;
    lim.leave = 120;
    for (int i = 0; i < 400 && !ended(b); ++i) {
        g.frame();
        b.step(g, ++step, g.L, lim);
    }
    CHECK(b.phase == Phase::Left && !b.injected(), "ended: %s", phase_name(b.phase));
    CHECK(b.reason.find("dropped") != std::string::npos, "reason '%s'", b.reason.c_str());
}

}  // namespace

int main() {
    t_wire();
    t_login();
    t_identity();
    t_snapshot();
    t_join();
    t_acks();
    t_leave();
    t_drop();
    t_failures();
    t_leave_timeout();
    std::printf("labbot host: %d checks, %d failed\n", g_checks, g_failures);
    return g_failures ? 1 : 0;
}
