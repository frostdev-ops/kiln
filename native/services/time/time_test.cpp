// SPDX-License-Identifier: MIT
#define KILN_TIME_HOST_TEST
#include "time.cpp"
#include <cstdlib>
#include <thread>

namespace {
int failures = 0;
void check(bool ok, const char* what) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", what); failures += !ok; }
}
int main() {
    // An old caller's prefix has exactly the old layout; the added pointers never change get/set/weather offsets.
    struct OldApi { KilnServiceHeader header; decltype(KilnTimeApi::get) get;
        decltype(KilnTimeApi::set_time_of_day) set; decltype(KilnTimeApi::reset_weather) weather; };
    static_assert(sizeof(OldApi) == offsetof(KilnTimeApi, set_time_scale));
    static_assert(offsetof(OldApi, weather) == offsetof(KilnTimeApi, reset_weather));
    check(!valid_scale(NAN) && !valid_scale(INFINITY) && !valid_scale(-INFINITY) && !valid_scale(1001), "scale rejects all non-finite and oversized values");
    check(valid_scale(-1) && valid_scale(0) && valid_scale(1000), "release, stopped and maximum scales accepted");
    reset_scale_state();
    float applied = -99;
    auto apply = [&](float v) { applied = v; return true; };
    check(change_scale(0, apply) && applied == 0 && g_scale_hold == 0, "stopped publishes only after a successful game call");
    float request = 100;
    check(!scale_request(true, 0, &request) && request == 0, "sleep cannot advance a stopped clock or bump its counter");
    request = 100;
    check(scale_request(false, 1, &request) && request == 100, "client/prediction world keeps the game's sleep speed");
    check(change_scale(28, apply) && g_scale_hold == 28, "fast cycle holds the requested speed");
    request = 100;
    check(!scale_request(true, 28, &request) && request == 28, "sleep cannot re-anchor an already-fast clock");
    check(!change_scale(0, [](float) { return false; }) && g_scale_hold == 28, "failed set preserves the previous hold");
    check(!change_scale(-1, [](float) { return false; }) && g_scale_hold == 28, "failed release preserves the previous hold");
    check(change_scale(-1, apply) && applied == 1 && g_scale_hold == -1, "normal releases at speed one and lets sleep take over");
    request = 100;
    check(scale_request(true, 1, &request) && request == 100, "sleep resumes after normal");
    change_scale(0, apply);
    g_scale_refused = 42;
    g_scale_logged_refusal = true;
    scale_world_event(KILN_WORLD_STOPPING, 1, false);
    check(g_scale_hold == -1 && g_scale_refused == 0 && !g_scale_logged_refusal, "stopping a world clears hold and refusal state");
    scale_world_event(KILN_WORLD_STARTED, 2, true);
    check(g_scale_hold == -1, "restarting after a stop keeps the new clock normal");
    change_scale(28, apply);
    scale_world_event(KILN_WORLD_STARTED, 3, false);
    check(g_scale_hold == -1, "a missed stop event cannot carry the hold into another world");
    std::atomic<bool> done{false}, bad{false};
    std::thread reader([&] { while (!done) { float v = g_scale_hold.load(); if (v != -1 && v != 0 && v != 28) bad = true; } });
    for (int i = 0; i < 100000; ++i) { change_scale(i & 1 ? 0 : 28, apply); reset_scale_state(); }
    done = true;
    reader.join();
    check(!bad, "concurrent hold reads observe only whole published values");
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
