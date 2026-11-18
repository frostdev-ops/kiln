// SPDX-License-Identifier: MIT
// ui_text.h: Kiln's own texts kiln.ui draws. The handoff from Kiln.Ui (set_texts: the language and the "native." keys),
// English for what was not handed over, plural forms, placeholders (ModLocale.Fill's rules), the footer's line in another
// language, and the built-in English against runtime/locales/en.json (argv[1]) key by key.
#include <cstdio>
#include <fstream>
#include <map>
#include <sstream>
#include <string>

#include "../menu_footer.h"
#include "../ui_text.h"
#include "nlohmann/json.hpp"

static int g_fail = 0, g_checks = 0;
#define CHECK(c)                                                     \
    do {                                                             \
        ++g_checks;                                                  \
        if (!(c)) {                                                  \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c); \
            ++g_fail;                                                \
        }                                                            \
    } while (0)

using kui::text::fill;

static void flatten(const nlohmann::json& j, const std::string& prefix, std::map<std::string, std::string>& out) {
    for (auto it = j.begin(); it != j.end(); ++it) {
        const std::string key = prefix.empty() ? it.key() : prefix + "." + it.key();
        if (it->is_object()) flatten(*it, key, out);
        else if (it->is_string()) out[key] = it->get<std::string>();
    }
}

int main(int argc, char** argv) {
    // ---- placeholders ----
    CHECK(fill("Kiln {version}", {{"version", "0.2.0"}}) == "Kiln 0.2.0");
    CHECK(fill("{Count:N0} of {count}", {{"count", "5"}}) == "5 of 5");             // case-insensitive, the format ignored
    CHECK(fill("{{literal}} {x}", {{"x", "1"}}) == "{literal} 1");                  // braces
    CHECK(fill("{missing} {x}", {{"x", "1"}}) == "{missing} 1");                     // no value: as written
    CHECK(fill("open {", {}) == "open {");                                           // an unclosed brace
    CHECK(fill("按 {key} 关闭。", {{"key", "Insert"}}) == "按 Insert 关闭。");         // UTF-8 around

    // ---- the table: English until Kiln.Ui hands texts over ----
    kui::text::Table t;
    CHECK(t.get("native.menu.mods") == "Mods");
    CHECK(t.plural("native.footer.loaded", 1) == "Kiln {version} - {count} Mod Loaded");
    CHECK(t.plural("native.footer.loaded", 2) == "Kiln {version} - {count} Mods Loaded");
    CHECK(t.get("native.no_such") == "native.no_such");
    CHECK(t.language().empty() && t.generation() == 0);

    // the handoff (set_texts): Chinese, its .one equal to its .other (no singular form), a stray key ignored
    const std::vector<std::pair<std::string, std::string>> zh = {
        {"native.menu.mods", "模组"},
        {"native.footer.loaded.one", "Kiln {version} - 已加载 {count} 个模组"},
        {"native.footer.loaded.other", "Kiln {version} - 已加载 {count} 个模组"},
        {"mods.title", "not native"},
    };
    CHECK(t.set("zh-cn", zh));
    CHECK(!t.set("zh-cn", zh));  // the same again: nothing changed
    CHECK(t.generation() == 1 && t.language() == "zh-cn");
    CHECK(t.get("native.menu.mods") == "模组");
    CHECK(t.get("mods.title") == "mods.title");                                     // not taken
    CHECK(t.get("native.fallback.close") == "Press {key} to close.");                // English where none was handed
    CHECK(t.plural("native.footer.loaded", 1) == "Kiln {version} - 已加载 {count} 个模组");
    // a translation with only .other: .other for every count, never the English .one
    CHECK(t.set("ja-jp", {{"native.footer.loaded.other", "{count} 個"}}));
    CHECK(t.plural("native.footer.loaded", 1) == "{count} 個");
    // back to English (an empty set)
    CHECK(t.set("en-us", {}));
    CHECK(t.get("native.menu.mods") == "Mods" && t.generation() == 3);

    // ---- the footer's line in another language ----
    using menu_footer::line_text;
    CHECK(line_text("0.2.0-dev.29.16+x", 3, false, "Kiln {version} - 已加载 {count} 个模组") == "Kiln 0.2.0-dev.29.16 - 已加载 3 个模组");
    CHECK(line_text("", 3, false, "Kiln {version} - {count} Mods Loaded") == "Kiln - 3 Mods Loaded");
    CHECK(line_text("0.2.0", 1, false) == "Kiln 0.2.0 - 1 Mod Loaded");
    CHECK(line_text("0.2.0", -1, false, "x").empty());

    // ---- the built-in English is en.json's "native." section ----
    if (argc > 1) {
        std::ifstream f(argv[1]);
        std::stringstream ss;
        ss << f.rdbuf();
        const auto j = nlohmann::json::parse(ss.str(), nullptr, false, true);
        CHECK(!j.is_discarded());
        std::map<std::string, std::string> en;
        if (!j.is_discarded()) flatten(j, "", en);
        size_t native = 0;
        for (const auto& [k, v] : en) {
            if (k.rfind("native.", 0) != 0) continue;
            ++native;
            const bool same = kui::text::english(k) == v;
            if (!same) std::printf("FAIL en.json %s: \"%s\", ui_text.h \"%s\"\n", k.c_str(), v.c_str(), kui::text::english(k).c_str());
            CHECK(same);
        }
        CHECK(native == std::size(kui::text::kEnglish));
        for (const auto& e : kui::text::kEnglish) CHECK(en.count(e.key) == 1);
    }

    std::printf("ui_text_test: %d checks, %d failed\n", g_checks, g_fail);
    return g_fail ? 1 : 0;
}
