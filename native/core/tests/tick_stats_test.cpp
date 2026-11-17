// SPDX-License-Identifier: MIT
// tick-stats.jsonl's line and the parts of Pre and Post it reports since 0.2.0-dev.29 (src/tick_stats.h; issue #39): the old
// fields byte for byte, the job wait, the window services' end, the subscribers by owner name, the Pre top 8, the worst step,
// what a subscriber list change keeps, and that the step's bookkeeping allocates nothing. tick.cpp is checked as source
// (its hooks need the game). Prints the sample server line (crates/kiln-manage/src/logs.rs reads the same line).
#include <cstdio>
#include <cmath>
#include <cstring>
#include <limits>
#include <fstream>
#include <memory>
#include <sstream>
#include <string>
#include <vector>

#include "../src/tick_stats.h"

namespace {
int checks = 0, failures = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) {
        ++failures;
        std::printf("FAIL: %s\n", what);
    }
}
void check_eq(const std::string& got, const std::string& want, const char* what) {
    check(got == want, what);
    if (got != want) std::printf("  got:  %s\n  want: %s\n", got.c_str(), want.c_str());
}

using namespace kiln::tick::stats;

struct FakeSub {
    uint64_t handle, owner;
};
using FakePtr = std::shared_ptr<FakeSub>;
FakePtr sub(uint64_t handle, uint64_t owner) { return std::make_shared<FakeSub>(FakeSub{handle, owner}); }

std::string name_of(uint64_t owner) {
    switch (owner) {
    case 0: return "kiln";
    case 1: return "service:kiln-gameplay.dll";
    case 2: return "service:kiln-saves.dll";
    case 3: return "plugin:kiln-emf.dll";
    case 4: return "host:Kiln.Host";
    default: return "native:mod-" + std::to_string(owner);
    }
}

// The line as tick.cpp wrote it before 0.2.0-dev.29 (its format string, unchanged).
std::string old_line(const Line& l) {
    char tag[48] = "";
    if (l.world) std::snprintf(tag, sizeof tag, "\"world\":\"%s\",", l.world);
    char buf[1024];
    std::snprintf(buf, sizeof buf,
                  "{\"time\":\"%04u-%02u-%02uT%02u:%02u:%02u\",%s\"steps\":%llu,\"rate\":%.2f,\"clock_rate\":%.2f,"
                  "\"step_ms\":{\"p50\":%.3f,\"p99\":%.3f,\"max\":%.3f},"
                  "\"pre_ms\":{\"p50\":%.4f,\"p99\":%.4f,\"max\":%.3f},"
                  "\"post_ms\":{\"p50\":%.4f,\"p99\":%.4f,\"max\":%.3f},"
                  "\"interval_ms\":{\"p50\":%.2f,\"p99\":%.2f,\"max\":%.2f},\"subs\":{\"pre\":%zu,\"post\":%zu}}\n",
                  l.year, l.month, l.day, l.hour, l.minute, l.second, tag, l.steps, l.rate, l.clock_rate, l.step.p50,
                  l.step.p99, l.step.max, l.pre.p50, l.pre.p99, l.pre.max, l.post.p50, l.post.p99, l.post.max,
                  l.interval.p50, l.interval.p99, l.interval.max, l.pre_count, l.post_count);
    return buf;
}

Line base(const char* world) {
    Line l;
    l.year = 2026;
    l.month = 11;
    l.day = 15;
    l.hour = 13;
    l.minute = 25;
    l.second = 9;
    l.world = world;
    l.steps = 2621;
    l.rate = 43.68;
    l.clock_rate = 60.02;
    l.step = {9.812f, 11.004f, 11.0f};
    l.pre = {0.9375f, 9.625f, 127.25f};
    l.post = {0.3125f, 0.84375f, 10249.5f};
    l.interval = {16.75f, 21.5f, 10271.25f};
    l.pre_count = 30;
    l.post_count = 5;
    return l;
}
}  // namespace

int main(int argc, char** argv) {
    // ---- a part ----
    Part p;
    p.add(10);
    p.add(50);     // not over 50
    p.add(50.5f);  // over
    p.add(6034);
    check(p.max == 6034 && p.over == 2 && p.total == 6144.5, "a part: its longest call, the sum, the calls over 50 ms");

    // ---- the slots follow the subscriber lists ----
    Subs s;
    std::vector<FakePtr> pre = {sub(11, 1), sub(12, 2), sub(13, 3)};
    std::vector<FakePtr> post = {sub(21, 1), sub(22, 0), sub(23, 2), sub(24, 3)};
    rebind(s.pre, s.gone_pre, pre);
    rebind(s.post, s.gone_post, post);
    check(s.pre.size() == 3 && s.post.size() == 4 && s.post[2].handle == 23 && s.post[2].owner == 2,
          "slots line up with the lists, in run order");
    s.post[2].part.add(7000);  // kiln.saves' Post took 7 s
    s.pre[0].part.add(3);
    std::vector<FakePtr> post2 = {sub(21, 1), sub(23, 2), sub(25, 4)};  // kiln.saves stays, one goes, the host joins
    rebind(s.post, s.gone_post, post2);
    check(s.post.size() == 3 && s.post[1].handle == 23 && s.post[1].part.max == 7000 && s.post[2].part.empty(),
          "a subscriber that stays keeps its window's numbers; a new one starts at zero");
    check(s.gone_post.empty(), "one that left without a call this window is not kept");
    s.post[2].part.add(1);
    std::vector<FakePtr> post3 = {sub(21, 1), sub(23, 2)};
    rebind(s.post, s.gone_post, post3);
    check(s.gone_post.size() == 1 && s.gone_post[0].owner == 4 && s.gone_post[0].part.total == 1,
          "one that left keeps its numbers in gone until the line is written");

    // ---- the worst step: no allocation per step ----
    {
        Worst w;
        reserve(w, s);
        const auto* pre_data = w.pre_subs.data();
        const auto* post_data = w.post_subs.data();
        s.pre[0].cur = 1.5f;
        s.pre[1].cur = 0.25f;
        s.pre[2].cur = 4.0f;
        s.post[0].cur = 0.125f;
        s.post[1].cur = 6400.0f;
        check(beat(w, 6.0f, 9.0f, 6402.0f, 1.5f, 0.0625f, s, true), "the first step is the worst so far");
        check(!beat(w, 6.0f, 9.0f, 100.0f, 0, 0, s, true), "a shorter step does not replace it");
        check(w.post == 6402.0f && w.wait == 1.5f && w.post_subs.size() == 2 && w.post_subs[1].owner == 2 &&
                  w.post_subs[1].ms == 6400.0f && w.pre_subs.size() == 3 && w.pre_subs[2].ms == 4.0f,
              "the worst step keeps its parts and each subscriber's time");
        check(w.pre_subs.data() == pre_data && w.post_subs.data() == post_data, "copying a worst step allocates nothing");
        check(beat(w, 9000.0f, 9.0f, 0.5f, 0, 0, s, false) && w.post_subs.empty() && w.pre_subs.size() == 3,
              "a worst step without Post keeps no Post subscriber times");
        reset(w);
        check(w.total < 0 && w.pre_subs.empty() && w.pre_subs.capacity() >= 3, "a new window keeps the capacity");
    }

    // ---- the old fields, byte for byte ----
    {
        const Line l = base(nullptr);
        std::string got = format(l), want = old_line(l);
        const std::string head = want.substr(0, want.size() - 2);  // up to "subs":{...} without the closing "}\n"
        check(got.compare(0, head.size(), head) == 0 && got.size() > head.size() && got[head.size()] == ',',
              "a dedicated server's line starts with the old line, field for field; new fields follow \"subs\"");
        const Line c = base("client");
        got = format(c);
        want = old_line(c);
        check(got.compare(0, want.size() - 2, want, 0, want.size() - 2) == 0, "a client world's line too (\"world\" tag)");
        check(got.find("\"wait_ms\"") == std::string::npos && got.find("\"windows_ms\"") == std::string::npos,
              "a client-side world does not wait for jobs: no wait_ms, no windows_ms");
        check(got.back() == '\n' && got.find('\n') == got.size() - 1, "one line, newline-terminated");
    }

    // ---- a server line with its parts (the player's case: a 10 s stall in kiln.saves' Post) ----
    {
        Line l = base(nullptr);
        l.waits = true;
        l.wait = {0.25f, 0.6875f, 1.5f};
        l.wait_part.add(0.25f);
        l.wait_part.add(1.5f);
        l.windows.add(0.0625f);
        Subs t;
        std::vector<FakePtr> tpre, tpost = {sub(1, 1), sub(2, 0), sub(3, 2), sub(4, 3), sub(5, 0)};
        for (uint64_t i = 0; i < 10; ++i) tpre.push_back(sub(100 + i, 10 + i));
        rebind(t.pre, t.gone_pre, tpre);
        rebind(t.post, t.gone_post, tpost);
        for (size_t i = 0; i < t.pre.size(); ++i) t.pre[i].part.add(static_cast<float>(i));  // 0..9 ms
        t.post[0].part.add(0.5f);
        t.post[1].part.add(0.25f);
        t.post[2].part.add(10240.0f);
        t.post[3].part.add(4.0f);
        t.post[4].part.add(60.0f);  // a second subscriber of "kiln": merged with the first
        l.post_subs = by_owner(t.post, t.gone_post, name_of);
        l.pre_subs = by_owner(t.pre, t.gone_pre, name_of);
        top(l.pre_subs, kPreTop);
        check(l.post_subs.size() == 4 && l.post_subs[1].owner == "kiln" && l.post_subs[1].part.max == 60.0f &&
                  l.post_subs[1].part.total == 60.25 && l.post_subs[1].part.over == 1,
              "one owner's subscribers add up into one entry, at its first place");
        check(l.pre_subs.size() == 8 && l.pre_subs[0].owner == "native:mod-19" && l.pre_subs[7].owner == "native:mod-12",
              "Pre: the 8 with the longest call, longest first");
        l.has_worst = true;
        l.worst_hour = 13;
        l.worst_minute = 24;
        l.worst_second = 58;
        l.worst_milli = 512;
        l.worst_pre = 6.0f;
        l.worst_step = 9.5f;
        l.worst_post = 10249.5f;
        l.worst_wait = 1.5f;
        l.worst_windows = 0.0625f;
        l.worst_post_subs = by_owner(std::vector<OwnerMs>{{1, 0.5f}, {0, 0.25f}, {2, 10240.0f}, {3, 4.0f}, {0, 1.0f}}, name_of);
        l.worst_pre_subs = by_owner(std::vector<OwnerMs>{{10, 1.0f}, {11, 5.0f}}, name_of);
        top(l.worst_pre_subs, kPreTop);
        const std::string got = format(l);
        const std::string tail =
            ",\"post_parts\":{\"wait_ms\":{\"p50\":0.2500,\"p99\":0.6875,\"max\":1.500,\"total\":1.750,\"over50\":0},"
            "\"windows_ms\":{\"max\":0.062,\"total\":0.062,\"over50\":0},"
            "\"subs\":[{\"owner\":\"service:kiln-gameplay.dll\",\"max\":0.500,\"total\":0.500,\"over50\":0},"
            "{\"owner\":\"kiln\",\"max\":60.000,\"total\":60.250,\"over50\":1},"
            "{\"owner\":\"service:kiln-saves.dll\",\"max\":10240.000,\"total\":10240.000,\"over50\":1},"
            "{\"owner\":\"plugin:kiln-emf.dll\",\"max\":4.000,\"total\":4.000,\"over50\":0}]},"
            "\"pre_parts\":{\"subs\":[{\"owner\":\"native:mod-19\",\"max\":9.000,\"total\":9.000,\"over50\":0},"
            "{\"owner\":\"native:mod-18\",\"max\":8.000,\"total\":8.000,\"over50\":0},"
            "{\"owner\":\"native:mod-17\",\"max\":7.000,\"total\":7.000,\"over50\":0},"
            "{\"owner\":\"native:mod-16\",\"max\":6.000,\"total\":6.000,\"over50\":0},"
            "{\"owner\":\"native:mod-15\",\"max\":5.000,\"total\":5.000,\"over50\":0},"
            "{\"owner\":\"native:mod-14\",\"max\":4.000,\"total\":4.000,\"over50\":0},"
            "{\"owner\":\"native:mod-13\",\"max\":3.000,\"total\":3.000,\"over50\":0},"
            "{\"owner\":\"native:mod-12\",\"max\":2.000,\"total\":2.000,\"over50\":0}]},"
            "\"worst\":{\"at\":\"13:24:58.512\",\"pre\":6.000,\"step\":9.500,\"post\":10249.500,\"wait\":1.500,"
            "\"windows\":0.062,\"post_subs\":[{\"owner\":\"service:kiln-gameplay.dll\",\"ms\":0.500},"
            "{\"owner\":\"kiln\",\"ms\":1.250},{\"owner\":\"service:kiln-saves.dll\",\"ms\":10240.000},"
            "{\"owner\":\"plugin:kiln-emf.dll\",\"ms\":4.000}],"
            "\"pre_subs\":[{\"owner\":\"native:mod-11\",\"ms\":5.000},{\"owner\":\"native:mod-10\",\"ms\":1.000}]}}\n";
        const std::string want_head = old_line(l);
        check_eq(got, want_head.substr(0, want_head.size() - 2) + tail, "the server line with its parts, exactly");
        std::printf("sample: %s", got.c_str());
    }

    // ---- owner names are JSON strings whatever they hold ----
    {
        std::string out;
        detail::escape(out, std::string("mod:\"q\"\\x\x01\t") + "\xC3\xA9" + "\xE2\x82\xAC" + "\xF0\x9F\x94\xA5" + "\xFF" +
                                "\xC3" + "\xED\xA0\x80" + "\xC0\xAF" + "end");
        check_eq(out, std::string("mod:\\\"q\\\"\\\\x\\u0001\\u0009") + "\xC3\xA9" + "\xE2\x82\xAC" + "\xF0\x9F\x94\xA5" +
                          "??" + "???" + "??" + "end",
                 "quotes, backslashes and control characters escaped; well-formed UTF-8 kept, other bytes '?'");
    }

    // ---- the tasks section (issue #49): appended last, absent without a pool ----
    {
        Line l = base(nullptr);
        check(format(l).find("\"tasks\"") == std::string::npos, "no pool: no tasks object, the old line unchanged");
        l.has_tasks = true;
        l.tasks.id = 7;
        l.tasks.span_ns = 60'000'000'000ull;
        l.tasks.pool.workers = 3;
        l.tasks.pool.long_running = 1;
        l.tasks.pool.queue_max = 12;
        l.tasks.owner_count = 1;
        auto& o = l.tasks.owners[0];
        o.present = true;
        std::strcpy(o.owner_name, "kiln-auto-loot");
        o.queued_max = 4;
        o.running_current = 1;
        o.running_max = 2;
        o.completed = 120;
        o.cancelled = 2;
        o.cpu_ns = 830'500'000ull;
        o.wall_ns = 910'200'000ull;
        o.longest_ns = 44'100'000ull;
        std::strcpy(o.longest_name, "loot-scan");
        l.tasks.apply.applied = 96;
        l.tasks.apply.deferred = 3;
        l.tasks.apply.apply_ns = 4'200'000ull;
        l.tasks.apply.owner_budget_stops = 1;
        l.tasks.apply.owners_served = 1;
        const std::string got = format(l);
        const std::string want_tasks =
            ",\"tasks\":{\"window\":{\"id\":7,\"span_ms\":60000.000,\"age_ms\":0.000},"
            "\"pool\":{\"workers\":3,\"long_running\":1,\"queue_max\":12},"
            "\"owners\":[{\"owner\":\"kiln-auto-loot\",\"queued\":{\"current\":0,\"max\":4},"
            "\"running\":{\"current\":1,\"max\":2},\"completed\":120,\"faulted\":0,\"cancelled\":2,"
            "\"cpu_ms\":830.500,\"wall_ms\":910.200,\"longest_ms\":44.100,\"longest_name\":\"loot-scan\","
            "\"queued_too_long\":0,\"running_too_long\":0,\"queue_full\":0,\"unload_blocked\":0}],"
            "\"apply\":{\"applied\":96,\"deferred\":3,\"cancelled\":0,\"ms\":4.200,\"owner_budget_stops\":1,"
            "\"step_budget_stops\":0,\"owners_served\":1,\"backlog\":0},\"hops\":{\"refused\":0}}}\n";
        check(got.size() > want_tasks.size() && got.compare(got.size() - want_tasks.size(), want_tasks.size(), want_tasks) == 0,
              "with a pool: the tasks object is appended after the existing fields, exactly");
        if (got.size() <= want_tasks.size() || got.compare(got.size() - want_tasks.size(), want_tasks.size(), want_tasks) != 0)
            std::printf("  got tail: %s", got.c_str());
    }

    // ---- no number that is not a number ----
    {
        Line l = base(nullptr);
        l.rate = std::nan("");
        l.post.max = std::numeric_limits<float>::infinity();
        const std::string got = format(l);
        check(got.find("nan") == std::string::npos && got.find("inf") == std::string::npos, "non-finite numbers are 0");
    }

    // ---- tick.cpp uses it as described ----
    if (argc < 2) {
        std::printf("usage: tick_stats_test <tick.cpp>\n");
        return 2;
    }
    std::ifstream in(argv[1]);
    std::stringstream ss;
    ss << in.rdbuf();
    const std::string src = ss.str();
    auto body = [&](const char* head) {
        const size_t fn = src.find(head);
        const size_t end = fn == std::string::npos ? fn : src.find("\n}\n", fn);
        return fn == std::string::npos || end == std::string::npos ? std::string() : src.substr(fn, end - fn);
    };
    const std::string server = body("uintptr_t server_step(");
    const std::string other = body("uintptr_t other_step(");
    check(server.find("run(g_pre, g_slots.pre, info, now_ms());") != std::string::npos &&
              server.find("run(g_post, g_slots.post, info, te);") != std::string::npos,
          "the server world times each Pre and Post subscriber");
    const size_t wait = server.find("g_wait_jobs(storage");
    const size_t tw = server.find("const double tw = now_ms();");
    const size_t end = server.find("window_clock::end();");
    const size_t te = server.find("const double te = now_ms();");
    check(wait != std::string::npos && wait < tw && tw < end && end < te,
          "the server world times the job wait and the window services' end apart");
    check(other.find("run_subs(K.pre, K.slots.pre, info, now_ms());") != std::string::npos &&
              other.find("run_subs(K.post, K.slots.post, info, t2);") != std::string::npos,
          "the client-side worlds time each subscriber");
    check(!server.empty() && server.find("owner_name") == std::string::npos, "no owner name is looked up on a server step");
    const std::string line = body("void write_line(");
    check(line.find("reg::owner_name(owner)") != std::string::npos && line.find("stats::format(l)") != std::string::npos,
          "owner names are looked up and the line formatted when it is written");
    check(src.find("interval_ms") == std::string::npos, "tick.cpp has no line format of its own any more");

    std::printf("%d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
