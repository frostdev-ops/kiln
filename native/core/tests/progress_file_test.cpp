// SPDX-License-Identifier: MIT
// progress.json's model and writer (src/progress_file.h): the plan and its order, refused steps, the 250 ms rule with
// the last change never lost, atomic replacement, a failed write, idle with a reason, nothing after ready, and what the
// file costs for the shape of a real start. Host-only (no Win32): services/progress_file.cpp is the process's glue.
//   native/core/tests/test-progress-file.sh
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "../src/progress_file.h"

using namespace kiln::progress_file;
using nlohmann::json;

static int failures = 0;
#define CHECK(cond)                                                                    \
    do {                                                                               \
        if (!(cond)) {                                                                 \
            std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);       \
            ++failures;                                                                \
        }                                                                              \
    } while (0)

static const std::vector<std::string> kClientPlan = {"start", "native-early", "early-plugins", "signatures", "services", "plugins",
                                                     "native", "dotnet", "mods", "content", "game"};

static Identity who(const char* side = "client") { return Identity{392, "0.2.0-dev.27+20261113.2e75c1f", side, "2026-11-13T17:28:19.975Z"}; }

// A writer without its thread: the test's clock, every write kept.
struct Bench {
    uint64_t now = 0;
    std::vector<std::string> written;
    int fail_next = 0;  // this many writes fail
    int fails_told = 0;
    Writer w;
    explicit Bench(std::vector<std::string> plan = kClientPlan, const char* side = "client")
        : w(Model(who(side), std::move(plan)),
            [this](const std::string& t) {
                if (fail_next > 0) {
                    --fail_next;
                    return false;
                }
                written.push_back(t);
                return true;
            },
            [this] { return now; }, false, [this] { ++fails_told; }) {}
    json last() const { return json::parse(written.back()); }
    void at(uint64_t t) {
        now = t;
        w.pump();
    }
};

static void walk_boot(Bench& b) {
    b.w.first("start", 0);
    b.at(110);
    for (const char* id : {"native-early", "early-plugins", "signatures", "services", "plugins", "native", "dotnet"}) {
        CHECK(b.w.step(id) == Result::ok);
        b.at(b.now + 1000);
    }
}

int main() {
    std::setvbuf(stdout, nullptr, _IOLBF, 0);
    // ---- the plan and its order ----
    {
        Bench b;
        CHECK(b.w.step("native-early") == Result::refused);  // the first step is "start"
        CHECK(b.w.first("start", 0) == Result::ok);
        b.at(5);
        CHECK(b.written.size() == 1);
        auto d = b.last();
        CHECK(d["format"] == 1 && d["pid"] == 392 && d["side"] == "client" && d["state"] == "booting");
        CHECK(d["plan"].size() == 11 && d["steps"].size() == 1 && d["steps"][0]["id"] == "start" && d["steps"][0]["ms"] == 0);
        CHECK(!d["steps"][0].contains("tookMs") && d["why"].is_null());
        CHECK(d["steps"][0]["label"] == "Reading Kiln's settings and the profile");
        CHECK(b.w.step("signatures") == Result::refused);   // not the next one
        CHECK(b.w.step("nonsense") == Result::refused);     // not in the plan
        b.now = 100;
        CHECK(b.w.step("native-early") == Result::ok);      // the next one: start ends
        CHECK(b.w.step("start") == Result::refused);        // never back
        CHECK(b.w.step("native-early", "Waiting", "x", 1, 3) == Result::ok);  // the running one changes
        b.at(100);
        d = b.last();
        CHECK(d["steps"].size() == 2 && d["steps"][0]["tookMs"] == 100 && d["steps"][1]["ms"] == 100);
        CHECK(d["steps"][1]["label"] == "Waiting" && d["steps"][1]["detail"] == "x" && d["steps"][1]["done"] == 1 &&
              d["steps"][1]["total"] == 3);
        // label empty keeps it, done/total 0 keep them, detail empty clears it
        b.w.step("native-early");
        b.at(400);
        d = b.last();
        CHECK(d["steps"][1]["label"] == "Waiting" && !d["steps"][1].contains("detail") && d["steps"][1]["total"] == 3);
        // a plan without .NET: no dotnet, mods, content
        Bench nb({"start", "native-early", "early-plugins", "signatures", "services", "plugins", "native", "game"});
        nb.w.first("start", 0);
        for (const char* id : {"native-early", "early-plugins", "signatures", "services", "plugins", "native"}) nb.w.step(id);
        CHECK(nb.w.step("dotnet") == Result::refused && nb.w.step("game") == Result::ok);
    }
    // ---- the 250 ms rule; a step change at once; the last change never lost ----
    {
        Bench b;
        b.w.first("start", 0);
        b.at(0);
        CHECK(b.written.size() == 1);
        b.now = 10;
        b.w.step("native-early");
        b.at(10);
        CHECK(b.written.size() == 2);  // a step change: at once, whatever the last write's time
        for (uint64_t t = 20; t < 200; t += 20) {
            b.now = t;
            b.w.step("native-early", {}, "detail " + std::to_string(t));
            b.at(t);
        }
        CHECK(b.written.size() == 2);  // details within 250 ms of the last write wait
        b.at(259);
        CHECK(b.written.size() == 2);
        b.at(260);
        CHECK(b.written.size() == 3 && b.last()["steps"][1]["detail"] == "detail 180");  // the last one, not the first
        b.now = 300;
        b.w.step("native-early", {}, "pending when the step ends");
        b.now = 320;
        b.w.step("early-plugins");  // the step ends: written at once, with the pending detail in it
        b.at(320);
        CHECK(b.written.size() == 4);
        auto d = b.last();
        CHECK(d["steps"][1]["detail"] == "pending when the step ends" && d["steps"][1]["tookMs"] == 310 &&
              d["steps"][2]["id"] == "early-plugins");
        // nothing pending, nothing written
        b.at(5000);
        CHECK(b.written.size() == 4);
        // the same detail again is no change
        b.w.step("early-plugins");
        b.at(6000);
        CHECK(b.written.size() == 4);
    }
    // ---- the thread: the last change is written when its time comes, with no further call ----
    {
        std::atomic<int> writes{0};
        std::string last;
        std::mutex m;
        const auto t0 = std::chrono::steady_clock::now();
        Writer w(
            Model(who(), kClientPlan),
            [&](const std::string& t) {
                std::lock_guard lk(m);
                last = t;
                ++writes;
                return true;
            },
            [t0] { return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count()); },
            true);
        w.first("start", 0);
        for (int i = 0; i < 200; ++i) w.step("start", {}, "item " + std::to_string(i), static_cast<uint64_t>(i + 1), 200);
        std::this_thread::sleep_for(std::chrono::milliseconds(600));
        std::lock_guard lk(m);
        CHECK(writes.load() >= 2 && writes.load() <= 4);
        auto d = json::parse(last);
        CHECK(d["steps"][0]["detail"] == "item 199" && d["steps"][0]["done"] == 200);
    }
    // ---- atomic replacement: a reader never sees half a file (the sink's rule: <file>.tmp, then a rename) ----
    {
        const char* tmpdir = std::getenv("TMPDIR");
        const std::filesystem::path dir = std::filesystem::path(tmpdir && *tmpdir ? tmpdir : "/tmp") /
                                          ("kiln-progress-file-" + std::to_string(getpid()));
        std::filesystem::create_directories(dir);
        const auto file = dir / "progress.json";
        const auto sink = [file](const std::string& text) {
            const auto tmp = std::filesystem::path(file.string() + ".tmp");
            {
                std::ofstream o(tmp, std::ios::binary | std::ios::trunc);
                o.write(text.data(), static_cast<std::streamsize>(text.size()));
                if (!o) return false;
            }
            std::error_code ec;
            std::filesystem::rename(tmp, file, ec);  // replaces the old file in one step
            return !ec;
        };
        std::atomic<bool> stop{false};
        std::atomic<int> reads{0}, torn{0};
        std::thread reader([&] {
            while (!stop) {
                std::ifstream i(file, std::ios::binary);
                if (!i) continue;
                std::stringstream ss;
                ss << i.rdbuf();
                if (ss.str().empty()) continue;
                ++reads;
                if (json::parse(ss.str(), nullptr, false).is_discarded()) ++torn;
            }
        });
        uint64_t now = 0;
        Writer w(Model(who(), kClientPlan), sink, [&now] { return now; }, false);
        w.first("start", 0);
        for (int i = 0; i < 2000; ++i) {
            now += 300;  // every change is due
            w.step("start", {}, std::string(static_cast<size_t>(i % 200), 'x'), static_cast<uint64_t>(i), 2000);
            w.pump();
        }
        stop = true;
        reader.join();
        CHECK(reads.load() > 0 && torn.load() == 0);
        std::printf("atomic replacement: %d reads during 2000 writes, %d torn\n", reads.load(), torn.load());
        std::filesystem::remove_all(dir);
    }
    // ---- a failed write is ignored: told once, the next writes go on ----
    {
        Bench b;
        b.fail_next = 3;
        b.w.first("start", 0);
        b.at(0);
        b.w.step("native-early");
        b.at(1);
        b.w.step("early-plugins");
        b.at(2);
        CHECK(b.written.empty() && b.fails_told == 1 && b.w.failures() == 3);
        b.w.step("signatures");
        b.at(3);
        CHECK(b.written.size() == 1 && b.fails_told == 1 && b.last()["steps"].size() == 4);
    }
    // ---- idle with a reason: the running step ends, nothing is written after it ----
    {
        Bench b;
        walk_boot(b);
        b.now = 9000;
        CHECK(b.w.finish(State::idle, "build abc is not verified and policy is verified-only") == Result::ok);
        b.at(9000);
        auto d = b.last();
        CHECK(d["state"] == "idle" && d["why"] == "build abc is not verified and policy is verified-only");
        CHECK(d["steps"].back()["id"] == "dotnet" && d["steps"].back().contains("tookMs") && d["atMs"] == 9000);
        const size_t n = b.written.size();
        CHECK(b.w.step("mods") == Result::over && b.w.mark(Mark::menu) == Result::over && b.w.loading() == Result::over);
        CHECK(b.w.finish(State::failed, "x") == Result::over);
        b.at(20000);
        CHECK(b.written.size() == n && b.w.done());
    }
    // ---- a client: ready once the menu drew and "game" began, in whichever order; nothing after ready; tookMs add up to atMs ----
    {
        // the menu's mark before the mods end, and while content runs: remembered, not ready until "game" begins
        Bench b;
        walk_boot(b);
        b.w.step("mods", "Waiting for the game to load the mods");
        b.w.loading();
        b.at(b.now + 1);
        CHECK(b.last()["state"] == "loading" && b.last()["steps"].back()["id"] == "mods");
        b.now += 6000;
        b.w.step("mods", "Loading the mods", "kiln-equipment", 0, 4);
        b.now += 500;
        b.w.mark(Mark::menu);  // the menu drew while the mods load
        b.at(b.now);
        CHECK(b.last()["state"] == "loading");
        b.now += 100;
        b.w.step("mods", {}, {}, 4, 4);
        b.now += 50;
        CHECK(b.w.step("content", {}, "kiln-equipment: materials thunderhead.copper_wire", 0, 198) == Result::ok);
        b.at(b.now);
        CHECK(b.last()["state"] == "loading" && b.last()["steps"].back()["id"] == "content");  // the blocks are still applied
        b.w.mark(Mark::menu);  // again, during content
        for (uint64_t i = 1; i <= 198; ++i) {
            b.now += 100;
            CHECK(b.w.step("content", {}, "block", i, 198) == Result::ok);  // never refused: the file is not over
        }
        b.at(b.now);
        CHECK(b.last()["state"] == "loading");
        b.now += 10;
        CHECK(b.w.step("game", "The game is loading its main menu") == Result::ok);
        b.at(b.now);
        auto d = b.last();
        CHECK(d["state"] == "ready" && d["steps"].back()["id"] == "game" && d["steps"].back()["tookMs"] == 0);
        const auto& content = d["steps"][d["steps"].size() - 2];
        CHECK(content["id"] == "content" && content["tookMs"] == 19810 && content["done"] == 198);  // content's whole duration
        uint64_t sum = 0;
        for (const auto& s : d["steps"]) sum += s["tookMs"].get<uint64_t>();
        CHECK(sum == d["atMs"].get<uint64_t>());
        CHECK(b.w.step("game") == Result::over);
        const size_t n = b.written.size();
        b.at(b.now + 10000);
        CHECK(b.written.size() == n);
    }
    {
        // zero blocks: content ends at once, game begins, the menu comes after
        Bench b;
        walk_boot(b);
        b.w.step("mods");
        b.w.loading();
        b.w.step("mods", {}, {}, 0, 0);
        b.w.step("content");
        b.w.step("game");
        b.at(b.now);
        CHECK(b.last()["state"] == "loading");
        b.now += 900;
        b.w.mark(Mark::menu);
        b.at(b.now);
        CHECK(b.last()["state"] == "ready" && b.last()["steps"].back()["tookMs"] == 900);
    }
    {
        // the mark before the boot is even complete (not possible in the game, but the order must not matter)
        Bench b;
        walk_boot(b);
        b.w.mark(Mark::menu);
        b.w.step("mods");
        b.w.loading();
        b.w.step("content");
        b.at(b.now);
        CHECK(b.last()["state"] == "loading");
        b.w.step("game");
        b.at(b.now);
        CHECK(b.last()["state"] == "ready");
    }
    {
        // the usual order: content, game, then the menu
        Bench b;
        walk_boot(b);
        b.w.step("mods");
        b.w.loading();
        b.w.step("content");
        b.w.step("game", "The game is loading its main menu");
        b.at(b.now + 5);
        CHECK(b.last()["state"] == "loading");
        b.now += 1700;
        CHECK(b.w.mark(Mark::server_started) == Result::ok);  // not a client's signal
        b.at(b.now);
        CHECK(b.last()["state"] == "loading");
        b.w.mark(Mark::menu);
        b.at(b.now);
        auto d = b.last();
        CHECK(d["state"] == "ready" && d["steps"].back()["id"] == "game" && d["steps"].back()["tookMs"] == 1705);
    }
    {
        // a server with .NET: Kiln.Host's ServerStarted; the world's first step alone is not enough
        Bench b(kClientPlan, "server");
        walk_boot(b);
        b.w.step("mods");
        b.w.loading();
        b.w.mark(Mark::world_started);
        b.w.step("content");
        b.w.step("game", "The server is starting its world");
        b.at(b.now);
        CHECK(b.last()["state"] == "loading");
        b.w.mark(Mark::server_started);
        b.at(b.now + 1);
        CHECK(b.last()["state"] == "ready");
        // ServerStarted before the blocks are applied (Entry.cs marks it before the service tick that runs them): remembered
        Bench sb(kClientPlan, "server");
        walk_boot(sb);
        sb.w.step("mods");
        sb.w.loading();
        sb.w.step("mods", {}, {}, 2, 2);
        sb.w.mark(Mark::server_started);
        sb.w.step("content", {}, {}, 0, 40);
        sb.at(sb.now);
        CHECK(sb.last()["state"] == "loading");
        sb.now += 3000;
        CHECK(sb.w.step("content", {}, {}, 40, 40) == Result::ok);
        sb.w.step("game", "The server is starting its world");
        sb.at(sb.now);
        CHECK(sb.last()["state"] == "ready" && sb.last()["steps"][sb.last()["steps"].size() - 2]["tookMs"] == 3000);
        // a server without .NET: the world's first step
        Bench nb({"start", "native-early", "early-plugins", "signatures", "services", "plugins", "native", "game"}, "server");
        nb.w.first("start", 0);
        for (const char* id : {"native-early", "early-plugins", "signatures", "services", "plugins", "native", "game"}) nb.w.step(id);
        nb.w.loading();
        nb.w.mark(Mark::world_started);
        nb.at(10);
        CHECK(nb.last()["state"] == "ready");
    }
    // ---- a plan with steps the start does not have (kiln.json's side section switched .NET off after the plan): the core's
    // skip_to takes them out, and the start ends in ready rather than loading for ever ----
    {
        Bench b;
        b.w.first("start", 0);
        for (const char* id : {"native-early", "early-plugins", "signatures", "services", "plugins", "native"}) b.w.step(id);
        CHECK(b.w.step("game") == Result::refused);  // dotnet is next
        std::vector<std::string> dropped;
        CHECK(b.w.skip_to("game", "The game is loading its main menu", {}, &dropped) == Result::ok);
        CHECK((dropped == std::vector<std::string>{"dotnet", "mods", "content"}));
        b.w.loading();
        b.w.mark(Mark::menu);
        b.at(1);
        auto d = b.last();
        CHECK(d["state"] == "ready" && d["plan"].size() == 8 && d["plan"].back() == "game" && d["steps"].size() == 8);
        CHECK(b.w.skip_to("start", {}, {}, nullptr) == Result::over);
    }
    {
        Bench b;
        walk_boot(b);
        CHECK(b.w.skip_to("native", {}, {}, nullptr) == Result::refused);  // never back
    }
    // ---- parts: from 500 ms, at most 16 a step ----
    {
        Bench b;
        b.w.first("start", 0);
        b.w.step("native-early");
        b.w.step("early-plugins");
        b.w.part("EML", 499);
        b.w.part("ShroudForge", 6818);
        for (int i = 0; i < 20; ++i) b.w.part("p" + std::to_string(i), 600);
        b.at(1);
        auto parts = b.last()["steps"][2]["parts"];
        CHECK(parts.size() == 16 && parts[0]["label"] == "ShroudForge" && parts[0]["tookMs"] == 6818);
    }
    // ---- text is cut at 255 bytes, not inside a UTF-8 sequence; bad UTF-8 does not throw ----
    {
        Bench b;
        b.w.first("start", 0);
        std::string longer(254, 'a');
        longer += "\xc3\xa9\xc3\xa9";  // é é: the cut falls inside the first
        b.w.step("start", {}, longer);
        b.w.step("start", "\xff\xfe broken", longer);
        b.at(1);
        auto d = b.last();
        CHECK(d["steps"][0]["detail"].get<std::string>().size() == 254);
    }
    // ---- module names ----
    CHECK(module_name("kiln-shroudforge.dll") == "ShroudForge" && module_name("KILN-EML.DLL") == "EML" &&
          module_name("kiln-vfx.dll") == "kiln-vfx" && module_name("unlock-map-markers.dll") == "unlock-map-markers");

    // ---- what it costs, for the owner's start of 13 Nov (kiln.log): 31 services, 11 plugins, 42 on_ready, 2 early
    // plugins, 4 mods, 198 data blocks over 22 s, kiln-vfx's 120 materials and VFX over 19 s ----
    {
        Bench b;
        uint64_t t = 0;
        auto at = [&](uint64_t ms) {  // the thread: everything due by `ms` is written (it wakes when a change is due)
            for (; t < ms; t += 10) b.at(t);
            b.at(ms);
        };
        b.w.first("start", 0);
        at(110);
        b.w.step("native-early");
        b.w.step("native-early", "Waiting for other loaders' code scans");
        at(670);
        b.w.step("early-plugins");
        b.w.step("early-plugins", {}, "EML", 0, 4);
        at(700);
        b.w.step("early-plugins", {}, "ShroudForge", 1, 4);
        at(7518);
        b.w.part("ShroudForge", 6818);
        b.w.step("signatures");
        at(7788);
        b.w.step("services");
        for (int i = 0; i < 31; ++i) {
            b.w.step("services", {}, "kiln-svc" + std::to_string(i), static_cast<uint64_t>(i), 31);
            at(7788 + static_cast<uint64_t>(i) * 8);
        }
        b.w.step("plugins");
        for (int i = 0; i < 11; ++i) {
            b.w.step("plugins", {}, "plugin" + std::to_string(i));
            at(8050 + static_cast<uint64_t>(i));
        }
        b.w.step("plugins", "Getting Kiln's services and plugins ready", {}, 0, 42);
        for (int i = 0; i < 42; ++i) {
            b.w.step("plugins", {}, "kiln-mod" + std::to_string(i), static_cast<uint64_t>(i), 42);
            if (i == 30)  // kiln-vfx
                for (int k = 0; k < 120; ++k) {
                    b.w.step("plugins", {}, "kiln-vfx: building the materials and VFX of kiln-equipment: material m" + std::to_string(k) + " (" +
                                                std::to_string(k + 1) + " of 120)");
                    at(10000 + static_cast<uint64_t>(k) * 158);
                }
            at(29000 + static_cast<uint64_t>(i) * 40);
        }
        b.w.step("native");
        at(30700);
        b.w.step("dotnet", {}, "Kiln.Host");
        at(30810);
        b.w.step("mods", "Waiting for the game to load the mods", "they load in the game's first menu frame");
        b.w.loading();
        at(37130);
        for (int i = 0; i < 4; ++i) {
            b.w.step("mods", "Loading the mods", "mod" + std::to_string(i), static_cast<uint64_t>(i), 4);
            at(37130 + static_cast<uint64_t>(i) * 120);
        }
        b.w.step("content", {}, {}, 0, 198);
        for (int i = 0; i < 198; ++i) {
            b.w.step("content", {}, "kiln-equipment: materials thunderhead.copper_wire" + std::to_string(i), static_cast<uint64_t>(i), 198);
            at(37900 + static_cast<uint64_t>(i) * 111);
        }
        b.w.step("game", "The game is loading its main menu");
        at(59900);
        b.w.mark(Mark::menu);
        at(61600);
        size_t bytes = 0, biggest = 0;
        for (const auto& w : b.written) bytes += w.size(), biggest = std::max(biggest, w.size());
        CHECK(b.last()["state"] == "ready");
        // the time a write takes here: render + atomic replacement on this machine's disk
        const char* tmpdir = std::getenv("TMPDIR");
        const auto dir = std::filesystem::path(tmpdir && *tmpdir ? tmpdir : "/tmp") / ("kiln-progress-cost-" + std::to_string(getpid()));
        std::filesystem::create_directories(dir);
        const auto file = dir / "progress.json";
        const Model m = b.w.model();
        const auto c0 = std::chrono::steady_clock::now();
        constexpr int kRounds = 500;
        for (int i = 0; i < kRounds; ++i) {
            const std::string text = m.render(61600);
            std::ofstream o(dir / "progress.json.tmp", std::ios::binary | std::ios::trunc);
            o.write(text.data(), static_cast<std::streamsize>(text.size()));
            o.close();
            std::filesystem::rename(dir / "progress.json.tmp", file);
        }
        const double us = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - c0).count() / kRounds;
        const auto c1 = std::chrono::steady_clock::now();
        for (int i = 0; i < 100000; ++i) b.w.step("game", {}, i % 2 ? "a" : "b");  // after ready: over, a lock and a check
        const double call_ns = std::chrono::duration<double, std::nano>(std::chrono::steady_clock::now() - c1).count() / 100000;
        Bench live;
        live.w.first("start", 0);
        const auto c2 = std::chrono::steady_clock::now();
        for (int i = 0; i < 100000; ++i) live.w.step("start", {}, i % 2 ? "kiln-equipment: materials a" : "kiln-equipment: materials b", static_cast<uint64_t>(i), 100000);
        const double report_ns = std::chrono::duration<double, std::nano>(std::chrono::steady_clock::now() - c2).count() / 100000;
        std::printf("cost for the 13 Nov start's shape: %zu writes, %zu bytes in all, %zu bytes the largest, %.0f bytes on average; "
                    "render + atomic write %.1f us each here; a report call %.0f ns (a refused one %.0f ns)\n",
                    b.written.size(), bytes, biggest, static_cast<double>(bytes) / static_cast<double>(b.written.size()), us, report_ns, call_ns);
        std::filesystem::remove_all(dir);
    }

    if (failures) {
        std::fprintf(stderr, "%d check(s) failed\n", failures);
        return 1;
    }
    std::printf("progress_file_test: all checks passed\n");
    return 0;
}
