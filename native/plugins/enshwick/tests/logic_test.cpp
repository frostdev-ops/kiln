// SPDX-License-Identifier: MIT
// Unit tests of kiln-enshwick's decisions (src/logic.h), on any machine:
//   c++ -std=c++20 -O1 -o /tmp/logic_test native/plugins/enshwick/tests/logic_test.cpp && /tmp/logic_test
#include "../src/logic.h"

#include <cstdio>

using namespace kenshwick;

static int g_fail = 0, g_ok = 0;
#define CHECK(cond)                                                       \
    do {                                                                  \
        if (cond) ++g_ok;                                                 \
        else ++g_fail, std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
    } while (0)

static const Action* find(const std::vector<Action>& v, const std::string& folder) {
    for (const auto& a : v)
        if (a.folder == folder) return &a;
    return nullptr;
}

int main() {
    // release answers as the loader reads them
    CHECK(release_answer(Hold::Holding) == 1);
    CHECK(release_answer(Hold::Expired) == 2);
    CHECK(release_answer(Hold::None) == 0);

    // folder names
    CHECK(safe_folder_name("you.material-stacks"));
    CHECK(safe_folder_name("enshwick.better-altars"));
    CHECK(!safe_folder_name("enshwick"));
    CHECK(!safe_folder_name("EnShWiCk"));
    CHECK(!safe_folder_name(""));
    CHECK(!safe_folder_name(".."));
    CHECK(!safe_folder_name("a/b"));
    CHECK(!safe_folder_name("a\\b"));
    CHECK(!safe_folder_name("c:x"));
    CHECK(!safe_folder_name("trail."));
    CHECK(!safe_folder_name("trail "));
    CHECK(!safe_folder_name(".hidden"));
    CHECK(!safe_folder_name("con"));
    CHECK(!safe_folder_name("NUL.mod"));
    CHECK(safe_folder_name("console.tools"));
    std::string why;
    CHECK(!safe_folder_name("x|y", &why) && !why.empty());

    // forms
    CHECK(classify(true, true, false) == ModForm::Pack);
    CHECK(classify(false, true, false) == ModForm::Luau);
    CHECK(classify(true, false, true) == ModForm::Dll);
    CHECK(classify(false, false, false) == ModForm::Unknown);
    CHECK(std::string(form_name(ModForm::Pack)) == "pack");

    // loader choice
    CHECK(pick_loader("auto", true, true) == LoaderPick::Game);
    CHECK(pick_loader("auto", false, true) == LoaderPick::Bundled);
    CHECK(pick_loader("", false, false) == LoaderPick::None);
    CHECK(pick_loader("Bundled", true, true) == LoaderPick::Bundled);
    CHECK(pick_loader("bundled", true, false) == LoaderPick::None);
    CHECK(pick_loader("game", false, true) == LoaderPick::None);

    // step aside
    CHECK(step_aside_reason({}).empty());
    StepAsideFacts f;
    f.stub_loaded = true;
    CHECK(step_aside_reason(f).find("powrprof.dll") != std::string::npos);
    f = {};
    f.stub_claimed = true;
    CHECK(!step_aside_reason(f).empty());
    f = {};
    f.loader_loaded = true;
    CHECK(!step_aside_reason(f).empty());
    f = {};
    f.claim_failed = true;
    CHECK(!step_aside_reason(f).empty());

    // marker round trip
    std::string s, fp;
    CHECK(parse_marker(marker_text("C:\\lib\\a b", "00ff"), &s, &fp) && s == "C:\\lib\\a b" && fp == "00ff");
    CHECK(parse_marker("source=x\r\nfingerprint=y\r\n", &s, &fp) && s == "x" && fp == "y");
    CHECK(!parse_marker("source=x\n", &s, &fp));
    CHECK(!parse_marker("", &s, &fp));

    // fingerprint: order-independent, sensitive to size/time/name, case-folded names
    const std::string a = fingerprint({{"pack.toml", 10, 5}, {"items.toml", 3, 1}});
    CHECK(a == fingerprint({{"items.toml", 3, 1}, {"pack.toml", 10, 5}}));
    CHECK(a != fingerprint({{"items.toml", 3, 1}, {"pack.toml", 11, 5}}));
    CHECK(a != fingerprint({{"items.toml", 3, 1}, {"pack.toml", 10, 6}}));
    CHECK(a != fingerprint({{"items.toml", 3, 1}}));
    CHECK(a.size() == 16);

    // sync plan
    std::vector<Wanted> wanted = {{"you.a", "L/a", "1"}, {"you.b", "L/b", "2"}, {"you.c", "L/c", "3"},
                                  {"you.hand", "L/h", "4"}, {"You.A", "L/a2", "9"}};
    std::vector<Existing> existing = {{"you.b", true, "L/b", "2"},       // unchanged copy
                                      {"YOU.C", true, "L/c", "old"},     // changed source
                                      {"you.hand", false, "", ""},       // player's own
                                      {"you.gone", true, "L/g", "5"},    // no longer listed
                                      {"player.mod", false, "", ""}};    // player's, untouched
    const auto plan = plan_sync(wanted, existing);
    CHECK(find(plan, "you.a") && find(plan, "you.a")->act == Act::Copy);
    CHECK(find(plan, "you.b") && find(plan, "you.b")->act == Act::Keep);
    CHECK(find(plan, "you.c") && find(plan, "you.c")->act == Act::Refresh);
    CHECK(find(plan, "you.hand") && find(plan, "you.hand")->act == Act::Conflict);
    CHECK(find(plan, "You.A") && find(plan, "You.A")->act == Act::Conflict);
    CHECK(find(plan, "you.gone") && find(plan, "you.gone")->act == Act::Remove);
    CHECK(!find(plan, "player.mod"));
    CHECK(plan_sync({}, {{"x", false, "", ""}}).empty());
    // a managed folder the profile lists again under another case stays (no remove + copy)
    const auto again = plan_sync({{"Mod.X", "L/x", "1"}}, {{"mod.x", true, "L/x", "1"}});
    CHECK(again.size() == 1 && again[0].act == Act::Keep);

    // log mirroring
    CHECK(worth_mirroring("2026-11-07 INFO enshwick: Enshwick 1.0.0-alpha.13 starting on Wine 10.0"));
    CHECK(worth_mirroring("INFO enshwick: ready: 2 of 2 mods running"));
    CHECK(worth_mirroring("INFO enshwick: you.welcome 1.0.0 initialised"));
    CHECK(!worth_mirroring("20:35:26.260Z debug [enshwick] page 0x1000 sealed"));
    CHECK(worth_mirroring("20:35:26.260Z error [enshwick] game build f075/1 reflects no types"));
    CHECK(worth_mirroring("20:35:26.260Z warn  [you.a] something"));
    size_t off = 0;
    auto l1 = new_lines("a\nb\r\nc", &off);
    CHECK(l1.size() == 2 && l1[0] == "a" && l1[1] == "b" && off == 5);
    auto l2 = new_lines("a\nb\r\nc\n", &off);
    CHECK(l2.size() == 1 && l2[0] == "c" && off == 7);

    CHECK(runs("copy") && runs("keep") && runs("run") && runs("in-place") && runs("refresh"));
    CHECK(!runs("off") && !runs("remove") && !runs("conflict") && !runs("failed") && !runs("missing"));
    CHECK(should_start(1, false) && should_start(0, true) && !should_start(0, false));

    std::printf("logic_test: %d ok, %d failed\n", g_ok, g_fail);
    return g_fail ? 1 : 0;
}
