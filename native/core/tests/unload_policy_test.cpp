// SPDX-License-Identifier: MIT
// Host test of on_unload's arming decision: (under Wine or on native Windows) x (kiln.json "unloadAtExit"). The code
// under test is the block between "unload-policy:begin" and "unload-policy:end" in native/core/src/boot.cpp, which
// test-unload.sh cuts out of that file as it is (unload_policy_block.inc) and builds into this file with the host's
// C++ compiler. Every row is checked on its own: armed or not (the link runs Kiln's own exit steps: its services'
// handlers, the task pool's park), whether the plugins' handlers are called after them (never without the link),
// warned or not, and a piece of the boot line. The exit code is the number of rows that failed.
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
    bool plugins;         // the plugins' on_unload handlers are called (when armed)
    bool warn;
    const char* says;     // must be the start of the boot line (after "unload: ")
};

const Row kRows[] = {
    {"wine, not set", "11.0", nullptr, true, true, false,
     "on (Wine 11.0; kiln.json \"unloadAtExit\" not set: auto)"},
    {"wine, auto", "11.0", "auto", true, true, false, "on (Wine 11.0; kiln.json \"unloadAtExit\": auto)"},
    {"wine, on", "11.0", "on", true, true, false, "on (Wine 11.0; kiln.json \"unloadAtExit\": \"on\")"},
    {"wine, off", "11.0", "off", false, false, false, "off (kiln.json \"unloadAtExit\": \"off\")"},
    {"wine, unknown value", "11.0", "yes", true, true, true,
     "on (Wine 11.0; kiln.json \"unloadAtExit\": \"yes\", counted as auto)"},
    {"wine, a JSON true", "11.0", "true", true, true, true, "on (Wine 11.0;"},
    // native Windows: "auto" places no link in this version (Kiln's own exit steps too wait until they are confirmed on
    // Windows: the behaviour before the steps existed); "on" is the explicit opt-in, every handler in the new order
    {"native, not set", "", nullptr, false, false, false,
     "off on native Windows under \"auto\" in this version (Kiln's own exit steps and the plugins' on_unload handlers "
     "wait until they are confirmed on Windows; kiln.json \"unloadAtExit\" not set: auto); kiln.json "
     "\"unloadAtExit\": \"on\" turns them on"},
    {"native, auto", "", "auto", false, false, false,
     "off on native Windows under \"auto\" in this version (Kiln's own exit steps and the plugins' on_unload handlers "
     "wait until they are confirmed on Windows; kiln.json \"unloadAtExit\": auto); kiln.json \"unloadAtExit\": "
     "\"on\" turns them on"},
    {"native, on", "", "on", true, true, false,
     "on on native Windows (kiln.json \"unloadAtExit\": \"on\"; proven under Wine only)"},
    {"native, off", "", "off", false, false, false, "off (kiln.json \"unloadAtExit\": \"off\")"},
    {"native, unknown value", "", "maybe", false, false, true, "off on native Windows under \"auto\" in this version"},
    {"native, empty string", "", "", false, false, true, "off on native Windows under \"auto\" in this version"},
    {"native, \"ON\" (case does not matter)", "", "ON", true, true, false, "on on native Windows"},
    {"wine, \"Off\" (case does not matter)", "9.0-ge", "Off", false, false, false, "off ("},
};
}  // namespace

int main() {
    int failed = 0;
    for (const Row& r : kRows) {
        const std::string setting = r.setting ? r.setting : "";
        const auto p = kiln_test::unload_policy(r.wine, r.setting ? &setting : nullptr);
        const bool ok = p.armed == r.armed && p.plugins == r.plugins && p.warn == r.warn &&
                        p.says.rfind(r.says, 0) == 0;
        if (!ok) ++failed;
        std::printf("%s %-36s armed=%d plugins=%d warn=%d  unload: %s\n", ok ? "PASS" : "FAIL", r.name, p.armed,
                    p.plugins, p.warn, p.says.c_str());
        if (!ok)
            std::printf("     wanted armed=%d plugins=%d warn=%d, a line starting \"%s\"\n", r.armed, r.plugins, r.warn,
                        r.says);
    }
    std::printf("%zu rows, %d failed\n", sizeof kRows / sizeof kRows[0], failed);
    return failed;
}
