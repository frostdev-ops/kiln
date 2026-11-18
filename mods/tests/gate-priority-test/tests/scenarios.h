// SPDX-License-Identifier: MIT
// gate-priority-test's scenarios over the fake Kiln of harness.h, shared by the host test and the Wine host.
#pragma once
#include "harness.h"

namespace harness {

// The player's components: real ones from sigdb's reflection of the dedicated server, and synthetic ones whose names
// sort first, so a broken rule would pick them.
inline const std::vector<std::string> kAll = {
    "keen::ecs::AaKitUnionLike",     // 8 bytes, two fields overlap: refused (a union?)
    "keen::ecs::AbKitGap",           // 8 bytes, a gap alignment does not explain: refused
    "keen::ecs::AcKitEmpty",         // 1 byte, nothing reflected: refused
    "keen::ecs::ClientFishing",      // 8 bytes, padding 2..3: the pick (smallest, then by name)
    "keen::ecs::ClientInteractionQuery",  // 8 bytes, padding 5..7
    "keen::ecs::CallFactionsForHelp",     // 12 bytes, padding 6..7
    "keen::ecs::BossDisplay",             // 16 bytes, padding 10..11
    "keen::ecs::NetworkHealth",           // 8 bytes, no padding
    "keen::ecs::Health",                  // 40 bytes, an 8-byte base class and an array: no padding
    "keen::ecs::CurrentTransform",        // 56 bytes, tail padding, but the game tracks it: not writable
};
inline const std::vector<std::string> kNoPadding = {"keen::ecs::AaKitUnionLike", "keen::ecs::AbKitGap", "keen::ecs::AcKitEmpty",
                                                    "keen::ecs::NetworkHealth", "keen::ecs::Health"};

inline Scenario scenario(const std::string& name) {
    const std::string world = "world=" + guid_hex() + "\n";
    Scenario s;
    s.name = name;
    s.comps = kAll;
    s.seconds = 4.5;
    if (name == "write") {
        s.run_file = "# a short check\n" + world + "seconds=2\nwarmup=1\nrate=200\nlabel=host-check\n";
        s.game_writes_every = 4;
        s.game_target = "keen::ecs::ClientFishing";
    } else if (name == "raise-off") {
        s.run_file = world + "seconds=2\nwarmup=1\nrate=200\n";
        s.raise = false;
    } else if (name == "noop") {
        s.run_file = world + "seconds=2\nwarmup=1\nrate=200\nmode=noop\n";
    } else if (name == "named") {
        s.run_file = world + "component=BossDisplay\nseconds=2\nwarmup=1\nrate=200\n";
    } else if (name == "named-offset") {
        s.run_file = world + "component=keen::ecs::NetworkHealth\noffset=3\nseconds=2\nwarmup=1\nrate=200\n";
    } else if (name == "named-gap") {
        s.run_file = world + "component=keen::ecs::AbKitGap\nseconds=2\nwarmup=1\n";
        s.seconds = 3;
    } else if (name == "tracked") {
        s.run_file = world + "component=CurrentTransform\nseconds=2\nwarmup=1\n";
        s.seconds = 3;
    } else if (name == "no-padding") {
        s.run_file = world + "seconds=2\nwarmup=1\nrate=200\n";
        s.comps = kNoPadding;
    } else if (name == "other-world") {
        s.run_file = "world=00112233445566778899aabbccddeeff\nseconds=2\nwarmup=1\n";
        s.seconds = 3;
    } else if (name == "no-world") {
        s.run_file = "";
        s.seconds = 3;
    } else if (name == "no-file") {
        s.run_file = "<none>";
        s.seconds = 1;
    } else if (name == "bad-config") {
        s.run_file = "rate=0\nminutes=999\nfoo=1\nworld=not-a-guid\nmode=fast\njust text\n";
        s.seconds = 3;
    } else if (name == "stop") {
        s.run_file = world + "minutes=10\nwarmup=0\nrate=200\n";
        s.stop_after_ms = 1500;
    } else if (name == "stop-warmup") {
        s.run_file = world + "minutes=10\nwarmup=60\n";
        s.stop_after_ms = 1500;
    } else {
        s.name.clear();
    }
    return s;
}

struct Outcome {
    nlohmann::json run;
    std::vector<std::vector<std::string>> writes, steps, gate;
    std::string writes_header, steps_header, gate_header;
    bool has_run = false;
};
inline Outcome read_outcome(Rig& r) {
    Outcome o;
    const fs::path d = run_dir(r);
    if (d.empty()) return o;
    o.has_run = true;
    try {
        std::ifstream in(d / "run.json");
        o.run = nlohmann::json::parse(in);
    } catch (...) {
        o.run = nlohmann::json::object();
    }
    o.writes = csv(d / "writes.csv", &o.writes_header);
    o.steps = csv(d / "steps.csv", &o.steps_header);
    o.gate = csv(d / "gate.csv", &o.gate_header);
    return o;
}
inline size_t rows_where(const Outcome& o, const std::function<bool(const std::vector<std::string>&)>& f) {
    return static_cast<size_t>(std::count_if(o.writes.begin(), o.writes.end(), f));
}
// writes.csv: utc_ms,op,attempt,status,result,why,engine_reason,flags,hold_ns,call_ns,step,priority
inline bool written(const std::vector<std::string>& w) { return w.size() > 7 && w[3] == "ok" && (std::stoul(w[7], nullptr, 16) & KILN_MUTATION_WRITTEN); }

// The whole component equals its initial bytes, except byte 0 of the game's target, which the game counted up.
inline bool untouched(Rig& r, const Scenario& sc, const std::string& name) {
    std::vector<uint8_t> want = r.initial.at(name);
    if (name == sc.game_target) want[0] = static_cast<uint8_t>(want[0] + r.game_changes.load());
    return bytes_of(r, name) == want;
}

inline void common_run_checks(Rig& r, const Scenario& sc, const Outcome& o, const std::string& component, uint32_t offset,
                              const std::string& mode) {
    const std::string k = sc.name + ": ";
    check(o.has_run && o.run.value("final", false), k + "run.json is final");
    check(o.run.value("component", "") == component && o.run.value("offset", -1) == static_cast<int>(offset),
          k + "the target is " + component + " byte " + std::to_string(offset) + " (got " + o.run.value("component", "?") +
              " byte " + std::to_string(o.run.value("offset", -1)) + ")");
    check(o.run.value("mode", "") == mode, k + "mode " + mode);
    check(o.run.value("end", "") == "the run's time is up", k + "the run ended on time: " + o.run.value("end", "?"));
    check(o.run.value("restored", false) && o.run.value("verified", false), k + "the byte is back and read back");
    const std::string raise = sc.raise ? "raise=on" : "raise=off";
    check(o.writes_header.find(raise) != std::string::npos && o.steps_header.find(raise) != std::string::npos &&
              o.gate_header.find(raise) != std::string::npos && o.run.value("raise", "") == (sc.raise ? "on" : "off"),
          k + "every file says " + raise);
    const size_t ok = rows_where(o, [](const auto& w) { return w.size() > 3 && w[3] == "ok"; });
    const size_t busy = rows_where(o, [](const auto& w) { return w.size() > 3 && w[3] == "busy"; });
    const size_t bad = rows_where(o, [](const auto& w) {
        return w.size() > 3 && w[3] != "ok" && w[3] != "busy" && w[3] != "conflict";
    });
    // counts that depend on the machine's load stay low: they prove the loop ran, the outcomes are checked apart
    check(ok > 20, k + "more than 20 exchanges succeeded (" + std::to_string(ok) + ")");
    check(busy > 0, k + "the frames made some busy (" + std::to_string(busy) + ")");
    check(bad == 0, k + "no other outcome (" + std::to_string(bad) + ")");
    check(rows_where(o, [](const auto& w) { return w.size() > 11 && w[11] != "-1"; }) == 0,
          k + "every call came from a below-normal thread (priority -1)");
    check(rows_where(o, [](const auto& w) { return w.size() > 8 && w[3] == "ok" && w[8] == "0"; }) == 0,
          k + "every successful exchange has a hold time");
    check(o.steps.size() > 200, k + "the steps were recorded (" + std::to_string(o.steps.size()) + ")");
    check(std::all_of(o.steps.begin(), o.steps.end(), [](const auto& s) { return s.size() == 4 && std::stoull(s[3]) >= std::stoull(s[2]); }),
          k + "every step's Post comes after its Pre");
    check(!o.gate.empty(), k + "gate counters recorded (" + std::to_string(o.gate.size()) + ")");
    for (const auto& c : sc.comps) check(untouched(r, sc, c), k + c + ": every byte as it was (the game's own change kept)");
    check(g_holds.load() > 0 && g_boosted_holds.load() == (sc.raise ? g_holds.load() : 0),
          k + (sc.raise ? "every hold ran above normal" : "no hold was raised") + " (" + std::to_string(g_boosted_holds.load()) +
              " of " + std::to_string(g_holds.load()) + ")");
    check(logs_with("gate-priority-test: done (the run's time is up)") == 1, k + "the summary line");
}

inline int run_scenario(const std::string& name, const std::string& sigdb, KilnNativeInitFn init) {
    const Scenario sc = scenario(name);
    if (sc.name.empty()) {
        std::printf("FAIL unknown scenario %s\n", name.c_str());
        return 2;
    }
    Rig r;
    if (!build(r, sc, sigdb)) {
        std::printf("FAIL no dedicated-server profile\n");
        return 1;
    }
    KilnNativeContext ctx{};
    ctx.size = sizeof ctx;
    ctx.abi_version = 1;
    ctx.api = &r.api;
    ctx.owner = 21;
    ctx.id = KilnStr{"gate-priority-test", 18};
    ctx.version = KilnStr{"1.0.0", 5};
    ctx.hooks_enabled = 1;
    check(init(&ctx) == KILN_NATIVE_INIT_OK, name + ": init");
    serve(r, sc);
    join_tasks();
    const Outcome o = read_outcome(r);
    const std::string k = name + ": ";
    if (name == "write") {
        common_run_checks(r, sc, o, "keen::ecs::ClientFishing", 2, "write");
        const size_t changes = rows_where(o, [](const auto& w) { return w[1] == "change" && written(w); });
        const size_t restores = rows_where(o, [](const auto& w) { return (w[1] == "restore" || w[1] == "final-restore") && written(w); });
        check(changes > 10 && changes == restores, k + "each change was put back (" + std::to_string(changes) + " changes, " +
                                                       std::to_string(restores) + " restores)");
        check(rows_where(o, [](const auto& w) { return w[3] == "conflict"; }) > 0 && r.game_changes.load() > 0,
              k + "the game's own changes came back as conflicts (" + std::to_string(r.game_changes.load()) + " changes)");
        check(o.run.value("label", "") == "host-check", k + "the label");
        check(logs_with("candidates, smallest first: keen::ecs::ClientFishing (8 bytes, byte 2), "
                        "keen::ecs::ClientInteractionQuery (8 bytes, byte 5), keen::ecs::CallFactionsForHelp (12 bytes, byte 6), "
                        "keen::ecs::BossDisplay (16 bytes, byte 10)") == 1,
              k + "the candidates: the union, the unexplained gaps, the empty type and the tracked one left out");
    } else if (name == "raise-off") {
        common_run_checks(r, sc, o, "keen::ecs::ClientFishing", 2, "write");
        check(logs_with("the priority raise is OFF for this run") == 1, k + "a warning says the raise is off");
    } else if (name == "noop") {
        check(o.has_run && o.run.value("mode", "") == "noop", k + "mode noop");
        const size_t ok = rows_where(o, [](const auto& w) { return w[3] == "ok"; });
        check(ok > 20 && rows_where(o, [](const auto& w) { return w[3] == "ok" && !(std::stoul(w[7], nullptr, 16) & KILN_MUTATION_NO_OP); }) == 0,
              k + "every successful exchange is a no-op (" + std::to_string(ok) + ")");
        check(rows_where(o, [](const auto& w) { return w[1] != "noop"; }) == 0, k + "nothing but no-op exchanges");
        for (const auto& c : sc.comps) check(untouched(r, sc, c), k + c + ": unchanged");
        check(o.run.value("restored", false) && o.run.value("verified", false), k + "nothing to put back");
    } else if (name == "named") {
        common_run_checks(r, sc, o, "keen::ecs::BossDisplay", 10, "write");
    } else if (name == "named-offset") {
        common_run_checks(r, sc, o, "keen::ecs::NetworkHealth", 3, "write");
        check(o.run.value("pick", "") == "named in the run file, with its byte", k + "the tester's own byte");
    } else if (name == "named-gap" || name == "tracked") {
        check(o.has_run && o.run.value("final", false) && o.writes.empty(), k + "the run did not start; nothing was written");
        const std::string why = name == "named-gap" ? "no padding byte we may use: a gap the alignment does not explain"
                                                    : "the write policy refuses it";
        check(o.run.value("end", "").find(why) == 0, k + "why: " + o.run.value("end", "?"));
        check(g_holds.load() == 0, k + "the gate was never taken");
        for (const auto& c : sc.comps) check(untouched(r, sc, c), k + c + ": unchanged");
    } else if (name == "no-padding") {
        check(o.has_run && o.run.value("mode", "") == "noop" && o.run.value("pick", "") == "no padding byte: no-op exchanges",
              k + "no component with padding: no-op exchanges instead");
        check(logs_with("no writable component of the player has a padding byte: it runs no-op exchanges instead") == 1,
              k + "a warning says so");
        check(rows_where(o, [](const auto& w) { return w[3] == "ok"; }) > 20, k + "the no-op exchanges ran");
        for (const auto& c : sc.comps) check(untouched(r, sc, c), k + c + ": unchanged");
    } else if (name == "other-world") {
        check(!o.has_run && g_holds.load() == 0, k + "no run, the gate never taken");
        check(logs_with("the run file names 00112233445566778899aabbccddeeff; the test stays off on this world") == 1,
              k + "one line says the test stays off on this world");
    } else if (name == "no-world") {
        check(!o.has_run && g_holds.load() == 0, k + "no run, the gate never taken");
        check(logs_with("this world is " + guid_hex() + "; put world=" + guid_hex()) == 1, k + "the world's GUID logged once");
    } else if (name == "no-file") {
        check(!o.has_run && r.host.pre.empty() && r.host.post.empty(), k + "nothing subscribed, no run");
        check(logs_with("no run file") == 1, k + "one line says there is no run file");
    } else if (name == "bad-config") {
        check(!o.has_run && g_holds.load() == 0, k + "no run");
        for (const char* w : {"rate= is not 1..1000", "minutes= is not 1..120", "unknown key foo", "world= is not a GUID",
                              "mode= is not write or noop", "a line without '='"})
            check(logs_with(std::string("run file: ") + w) == 1, k + "warned: " + w);
        check(logs_with("this world is " + guid_hex()) == 1, k + "an invalid world counts as none named");
    } else if (name == "stop-warmup") {
        check(o.has_run && o.run.value("end", "") == "the server world stopped" && o.writes.empty() && g_holds.load() == 0,
              k + "a stop before the first write ends the run, nothing written: " + o.run.value("end", "?"));
        check(o.steps.size() > 100, k + "the steps until the stop were recorded (" + std::to_string(o.steps.size()) + ")");
    } else if (name == "stop") {
        check(o.has_run && o.run.value("end", "") == "the server world stopped", k + "the run ended with the world: " +
                                                                                     o.run.value("end", "?"));
        // A stop between a change and its restore leaves the change in the dying world (an untracked padding byte, never
        // saved); the run says so. Either way no other bit moved.
        std::vector<uint8_t> want = r.initial.at("keen::ecs::ClientFishing");
        if (!o.run.value("restored", true)) want[2] ^= 1;
        check(bytes_of(r, "keen::ecs::ClientFishing") == want,
              k + (o.run.value("restored", true) ? "the last change was put back before the stop"
                                                 : "the stop came between a change and its restore, and the run says so"));
        check(rows_where(o, [](const auto& w) { return w[3] == "ok"; }) > 20, k + "writes ran until the stop");
    }
    const int failed = failures;
    std::error_code ec;
    fs::remove_all(r.root, ec);
    std::printf("%s: %d checks, %d failures\n", name.c_str(), checks, failed);
    return failed ? 1 : 0;
}

}  // namespace harness
