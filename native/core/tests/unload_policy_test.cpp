// SPDX-License-Identifier: MIT
// Host test of on_unload's arming decision: (under Wine or on native Windows) x (kiln.json "unloadAtExit"). The code
// under test is the block between "unload-policy:begin" and "unload-policy:end" in native/core/src/boot.cpp, which
// test-unload.sh cuts out of that file as it is (unload_policy_block.inc) and builds into this file with the host's
// C++ compiler. Every row is checked on its own: armed or not, warned or not, and a piece of the boot line. The exit
// code is the number of rows that failed.
#include <cstdio>
#include <string>
#include <string_view>

namespace kiln_test {
#include "unload_policy_block.inc"
}  // namespace kiln_test

namespace {
struct Row {
    const char* name;
    const char* wine;     // "" = native Windows
    const char* setting;  // nullptr = "unloadAtExit" not in kiln.json
    bool armed;
    bool warn;
    const char* says;     // must be the start of the boot line (after "unload: ")
};

const Row kRows[] = {
    {"wine, not set", "11.0", nullptr, true, false, "on (Wine 11.0; kiln.json \"unloadAtExit\" not set: auto)"},
    {"wine, auto", "11.0", "auto", true, false, "on (Wine 11.0; kiln.json \"unloadAtExit\": auto)"},
    {"wine, on", "11.0", "on", true, false, "on (Wine 11.0; kiln.json \"unloadAtExit\": \"on\")"},
    {"wine, off", "11.0", "off", false, false, "off (kiln.json \"unloadAtExit\": \"off\")"},
    {"wine, unknown value", "11.0", "yes", true, true,
     "on (Wine 11.0; kiln.json \"unloadAtExit\": \"yes\", counted as auto)"},
    {"wine, a JSON true", "11.0", "true", true, true, "on (Wine 11.0;"},
    {"native, not set", "", nullptr, false, false, "off on native Windows by default (not yet proven there;"},
    {"native, auto", "", "auto", false, false, "off on native Windows by default"},
    {"native, on", "", "on", true, false,
     "on on native Windows (kiln.json \"unloadAtExit\": \"on\"; proven under Wine only)"},
    {"native, off", "", "off", false, false, "off (kiln.json \"unloadAtExit\": \"off\")"},
    {"native, unknown value", "", "maybe", false, true, "off on native Windows by default"},
    {"native, empty string", "", "", false, true, "off on native Windows by default"},
    {"native, \"ON\" (case does not matter)", "", "ON", true, false, "on on native Windows"},
    {"wine, \"Off\" (case does not matter)", "9.0-ge", "Off", false, false, "off ("},
};
}  // namespace

int main() {
    int failed = 0;
    for (const Row& r : kRows) {
        const std::string setting = r.setting ? r.setting : "";
        const auto p = kiln_test::unload_policy(r.wine, r.setting ? &setting : nullptr);
        const bool ok = p.armed == r.armed && p.warn == r.warn && p.says.rfind(r.says, 0) == 0;
        if (!ok) ++failed;
        std::printf("%s %-36s armed=%d warn=%d  unload: %s\n", ok ? "PASS" : "FAIL", r.name, p.armed, p.warn,
                    p.says.c_str());
        if (!ok)
            std::printf("     wanted armed=%d warn=%d, a line starting \"%s\"\n", r.armed, r.warn, r.says);
    }
    std::printf("%zu rows, %d failed\n", sizeof kRows / sizeof kRows[0], failed);
    return failed;
}
