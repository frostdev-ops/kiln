// SPDX-License-Identifier: MIT
// kiln.settings: the difficulty bounds of Kiln's registry (bounds.h) on the host. A registry edit that raises
// difficulty.enemyDamageFactor.max to 10 makes the game keep a world's Custom 8 at start (lab dev32-regr3b); kiln.settings'
// own write path must accept 8 too, and refuse what a lowered bound leaves out (coordinator decision, dev.32 review).
#include <cmath>
#include <cstdio>
#include <string>

#include "../bounds.h"
#include "services/kiln_settings.h"

static int checks = 0, failed = 0;
#define CHECK(c, what)                                                                       \
    do {                                                                                     \
        ++checks;                                                                            \
        if (!(c)) {                                                                          \
            ++failed;                                                                        \
            std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, std::string(what).c_str());   \
        }                                                                                    \
    } while (0)

namespace {

// a registry.json body as report.cpp writes it (schema/state-registry.schema.json), trimmed to what kiln.settings reads
const char* kReport = R"({
  "format": 1, "phase": "start",
  "fields": [
    {"registry": "balance", "id": "game:balance", "field": "levelCap", "applies": "restart", "base": {"origin": "archive", "value": 25},
     "writers": [], "result": 30, "winner": "other-mod"},
    {"registry": "difficulty", "id": "game:difficulty", "field": "enemyDamageFactor.max", "applies": "restart",
     "base": {"origin": "archive", "value": 2.5}, "writers": [{"kind": "registry", "mod": "hard-mode", "value": 10, "certainty": "certain"}],
     "result": 10.0, "winner": "hard-mode"},
    {"registry": "difficulty", "id": "game:difficulty", "field": "playerHealthFactor.min", "applies": "restart",
     "base": {"origin": "archive", "value": 0.25}, "writers": [], "result": 0.5, "winner": "easy-floor"},
    {"registry": "difficulty", "id": "game:difficulty", "field": "playerHealthFactor.max", "applies": "restart",
     "base": {"origin": "archive", "value": 4}, "writers": [], "result": 2, "winner": "easy-floor"},
    {"registry": "difficulty", "id": "game:difficulty", "field": "dayTimeDuration.max", "applies": "restart",
     "base": {"origin": "archive", "value": 3600}, "writers": [], "result": 7200, "winner": "long-days"},
    {"registry": "difficulty", "id": "game:difficulty", "field": "flameLevelFogRemoval.max", "applies": "restart",
     "base": {"origin": "archive", "value": 20}, "writers": [], "result": 40, "winner": "fog-mod"},
    {"registry": "difficulty", "id": "game:difficulty", "field": "raw:maxValues.threatBonus", "raw": true, "applies": "restart",
     "base": {"origin": "archive", "value": 4}, "writers": [], "result": 99, "winner": "raw-mod"},
    {"registry": "difficulty", "id": "game:difficulty", "field": "enemyHealthFactor.max", "applies": "restart",
     "base": {"origin": "archive", "value": 4}, "writers": [], "result": null, "winner": null}
  ],
  "conflicts": [], "refused": []
})";

bool in(double lo, double hi, double x) { return x >= lo - 1e-6 && x <= hi + 1e-6; }

}  // namespace

int main() {
    const auto b = kset::registry_bounds(kReport);
    // ---- what the report gives
    CHECK(b.count("enemydamagefactor") && b.at("enemydamagefactor").max == 10.0 && !b.at("enemydamagefactor").min,
          "the raised enemyDamageFactor.max 10, by setting name in lower case");
    CHECK(b.count("enemydamagefactor") && b.at("enemydamagefactor").max_by == "hard-mode", "who set it (registry.json's winner)");
    CHECK(b.count("playerhealthfactor") && b.at("playerhealthfactor").min == 0.5 && b.at("playerhealthfactor").max == 2.0,
          "both bounds of playerHealthFactor");
    CHECK(b.count("daytimeduration") && b.at("daytimeduration").max == 7200.0, "a duration bound in seconds, as the registry has it");
    CHECK(!b.count("threatbonus"), "a raw path's row is not a Kiln bound");
    CHECK(!b.count("enemyhealthfactor"), "a row without a numeric result gives nothing");
    CHECK(!b.count("levelcap"), "another registry's rows are not settings");
    CHECK(kset::registry_bounds("not json").empty() && kset::registry_bounds("{\"fields\": 3}").empty() && kset::registry_bounds("").empty(),
          "text that is not a report gives nothing");

    // ---- the ranges kiln.settings accepts
    double lo = 0, hi = 0;
    const kset::RegistryBound* dmg = b.count("enemydamagefactor") ? &b.at("enemydamagefactor") : nullptr;
    kset::effective_range(KILN_SETTING_FLOAT, 0.25, 5, dmg, &lo, &hi);
    CHECK(lo == 0.25 && hi == 10.0 && in(lo, hi, 8), "enemyDamageFactor: the table's 0.25 with the registry's 10: 8 is accepted");
    const kset::RegistryBound* ph = b.count("playerhealthfactor") ? &b.at("playerhealthfactor") : nullptr;
    kset::effective_range(KILN_SETTING_FLOAT, 0.25, 4, ph, &lo, &hi);
    CHECK(lo == 0.5 && hi == 2.0 && !in(lo, hi, 3) && !in(lo, hi, 0.25), "playerHealthFactor narrowed by the registry: 3 and 0.25 are refused");
    const kset::RegistryBound* day = b.count("daytimeduration") ? &b.at("daytimeduration") : nullptr;
    kset::effective_range(KILN_SETTING_DURATION, 120e9, 3600e9, day, &lo, &hi);
    CHECK(lo == 120e9 && hi == 7200e9, "dayTimeDuration: the registry's 7200 s is 7200e9 ns");
    const kset::RegistryBound* fog = b.count("flamelevelfogremoval") ? &b.at("flamelevelfogremoval") : nullptr;
    kset::effective_range(KILN_SETTING_INT, 0, 20, fog, &lo, &hi);
    CHECK(lo == 0 && hi == 40, "flameLevelFogRemoval: the registry's 40");
    kset::effective_range(KILN_SETTING_FLOAT, 0.25, 4, nullptr, &lo, &hi);
    CHECK(lo == 0.25 && hi == 4, "no registry bound: the table's range, as before");
    kset::RegistryBound cross;
    cross.max = 0.1;
    kset::effective_range(KILN_SETTING_FLOAT, 0.25, 4, &cross, &lo, &hi);
    CHECK(lo == 0.1 && hi == 0.1, "a registry max below the table's min: the registry's bound decides");
    kset::RegistryBound up;
    up.min = 6;
    kset::effective_range(KILN_SETTING_FLOAT, 0.25, 4, &up, &lo, &hi);
    CHECK(lo == 6 && hi == 6, "a registry min above the table's max: the registry's bound decides");

    std::printf("settings-bounds-test: %d checks, %d failed\n", checks, failed);
    return failed ? 1 : 0;
}
