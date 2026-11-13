// SPDX-License-Identifier: MIT
#define KILN_TIME_HOST_TEST
#include "time.cpp"
#include <cstdlib>
#include <thread>
#include <vector>

namespace {
int failures = 0;
void check(bool ok, const char* what) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", what); failures += !ok; }

// A WeatherSystemResource as the game lays it out (26430253): BlobArrays of layers (200 B) and phenomena (176 B) after
// the header, the layer's range at +0x12/+0x13, its nothing phenomenon at +0x18, names as BlobStrings.
struct FakeWeather {
    std::vector<uint8_t> b = std::vector<uint8_t>(8192, 0);
    WeatherTableOffsets o;
    FakeWeather() {
        o.res_layers = 0x5c; o.res_phenomena = 0x6c; o.layer_stride = 0xc8; o.phenomenon_stride = 0xb0;
        o.layer_first = 0x12; o.layer_count = 0x13; o.layer_nothing = 0x18; o.layer_phenomenon = 8;
        const uint32_t layers_at = 0x100, phen_at = 0x400, names_at = 0x1000;
        put32(o.res_layers, layers_at - o.res_layers); put32(o.res_layers + 4, 3);
        put32(o.res_phenomena, phen_at - o.res_phenomena); put32(o.res_phenomena + 4, 5);
        const uint8_t ranges[3][2] = {{0, 2}, {2, 1}, {5, 0}};  // clouds 0-1, rain 2, an empty layer; 3-4 in no range
        for (uint32_t l = 0; l < 3; ++l) {
            const uint32_t L = layers_at + l * 0xc8;
            b[L] = static_cast<uint8_t>(0xA0 + l);  // guid
            b[L + 0x10] = l == 1 ? 7 : 0xFF;
            b[L + 0x11] = 0xFF;
            b[L + 0x12] = ranges[l][0];
            b[L + 0x13] = ranges[l][1];
            putf(L + 0x18 + 0x28, l == 0 ? 0.0f : 1.0f);
        }
        const char* names[5] = {"clouds_few", "clouds_overcast", "rain", "debug_rain", "debug_blizzard"};
        uint32_t at_name = names_at;
        for (uint32_t i = 0; i < 5; ++i) {
            const uint32_t P = phen_at + i * 0xb0;
            b[P] = static_cast<uint8_t>(i + 1);
            const uint32_t len = static_cast<uint32_t>(std::strlen(names[i]));
            std::memcpy(&b[at_name], names[i], len);
            put32(P + 0x10, at_name - (P + 0x10));
            put32(P + 0x14, len);
            at_name += len + 3;
            putf(P + 0x28, i == 2 ? 0.3f : 1.0f);
            put64(P + 0x60, i == 1 ? 30000000000ll : 20000000000ll);  // despawn fade
            put64(P + 0x30, 120000000000ll);
            put64(P + 0x38, 300000000000ll);
            b[P + 0xa8] = i == 2 || i == 3 ? 1 : i == 4 ? 2 : 0;
        }
    }
    void put32(size_t at, uint32_t v) { std::memcpy(&b[at], &v, 4); }
    void put64(size_t at, int64_t v) { std::memcpy(&b[at], &v, 8); }
    void putf(size_t at, float v) { std::memcpy(&b[at], &v, 4); }
};

void weather_tests() {
    static_assert(sizeof(KilnTimeWeatherPhenomenon) == 112 && sizeof(KilnTimeWeatherLayer) == 72 &&
                  sizeof(KilnTimeWeatherRequest) == 24 && sizeof(KilnTimeWeatherResult) == 176,
                  "the v2 records keep their sizes (Kiln.Time mirrors them)");
    static_assert(offsetof(KilnTimeApi, weather_table) == offsetof(KilnTimeApi, time_scale_hold) + sizeof(void*) &&
                  sizeof(KilnTimeApi) == offsetof(KilnTimeApi, weather_set) + sizeof(void*),
                  "v2 appends after time_scale_hold");
    uint8_t c = 0;
    check(weather_choice(5, 5, 1, &c) && c == 0, "a phenomenon in the layer's range is its offset in the range");
    check(weather_choice(-1, 5, 1, &c) && c == 1, "nothing is the range's count");
    check(weather_choice(-1, 13, 0, &c) && c == 0, "nothing on a layer without phenomena");
    check(!weather_choice(6, 5, 1, &c) && !weather_choice(4, 5, 1, &c) && !weather_choice(-2, 5, 1, &c),
          "outside the range (the game's debug entries) is refused");
    check(weather_current(0, 9, 1) == 9 && weather_current(1, 9, 1) == -1 && weather_current(~0ull, 9, 1) == -2,
          "the layer's choice field reads as table index, nothing, or none");
    check(weather_request_flags_ok(0, 0) && weather_request_flags_ok(KILN_TIME_WEATHER_HOLD, 0) &&
          weather_request_flags_ok(KILN_TIME_WEATHER_RELEASE | KILN_TIME_WEATHER_NOW, 0), "set, hold, release now");
    check(!weather_request_flags_ok(0, -1) && !weather_request_flags_ok(0x80, 0) &&
          !weather_request_flags_ok(KILN_TIME_WEATHER_RELEASE | KILN_TIME_WEATHER_HOLD, 0) &&
          !weather_request_flags_ok(KILN_TIME_WEATHER_NOW, 0), "a negative duration and contradicting flags are refused");
    FakeWeather w;
    check(weather_layout_ok(w.o), "the 26430253 layout is accepted");
    WeatherTableOffsets bad = w.o;
    bad.phenomenon_stride = 0xb8;
    check(!weather_layout_ok(bad), "another phenomenon size switches choosing off (no field is guessed)");
    std::vector<uint8_t> table(8 * sizeof(KilnTimeWeatherPhenomenon));
    const uint32_t n = weather_fill_table(w.b.data(), w.o, table.data(), 8, sizeof(KilnTimeWeatherPhenomenon));
    auto at_t = [&](uint32_t i) { KilnTimeWeatherPhenomenon p; std::memcpy(&p, &table[i * sizeof p], sizeof p); return p; };
    check(n == 5, "the table has every phenomenon");
    check(std::strcmp(at_t(0).name, "clouds_few") == 0 && at_t(0).layer == 0 && std::strcmp(at_t(2).name, "rain") == 0 &&
          at_t(2).layer == 1 && at_t(2).effect == 1 && at_t(2).probability > 0.29f && at_t(2).probability < 0.31f,
          "names, layers, effect and weight come from the records");
    check(at_t(3).layer == 0xFF && at_t(4).layer == 0xFF && at_t(4).effect == 2, "entries in no range have no layer");
    check(at_t(1).duration_min_ns == 120000000000ll && at_t(1).duration_max_ns == 300000000000ll && at_t(1).guid[0] == 2,
          "durations and guid");
    check(weather_fill_table(w.b.data(), w.o, table.data(), 2, sizeof(KilnTimeWeatherPhenomenon)) == 5,
          "a short buffer still reports the table's size");
    KilnTimeWeatherLayer l{};
    check(weather_fill_layer(w.b.data(), w.o, 1, &l) && l.first == 2 && l.count == 1 && l.restrict_mask == 7 &&
          l.exclude_mask == 0xFF && l.guid[0] == 0xA1 && l.nothing_probability == 1.0f, "a layer's range, masks and nothing weight");
    check(!weather_fill_layer(w.b.data(), w.o, 3, &l), "no layer past the table");
    check(weather_fade(w.b.data(), w.o, 0, 1) == 30000000000ll && weather_fade(w.b.data(), w.o, 1, 0) == 20000000000ll,
          "the fade of the chosen phenomenon is added to a mod's duration");
    check(weather_fade(w.b.data(), w.o, 1, 1) == 0 && weather_fade(w.b.data(), w.o, 9, 0) == 0,
          "nothing (no fade in the fake) and a missing layer add nothing");
}
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
    weather_tests();
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
