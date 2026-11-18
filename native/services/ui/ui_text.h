// SPDX-License-Identifier: MIT
// kiln.ui: Kiln's own texts that kiln-ui.dll draws (the Mods entry in the game's menus, Kiln's line in the main menu's
// footer, the fallback window, the drawing-off caption), in the language Kiln's UI shows (docs/kiln/translating-game.md).
// One table for both sides: the texts live in runtime/locales/<language>.json under "native."; Kiln.Ui reads them with the
// rest (KilnText) and hands the "native." keys to kiln.ui (set_texts, kiln_ui.h) at start and whenever the language
// changes. Until then, and without the managed host, the English below is used: it is en.json's "native." section, which
// tests/ui_text_test.cpp checks key by key. Plain logic: no Windows, no game, no ImGui.
#pragma once

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <initializer_list>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace kui::text {

struct English {
    const char* key;
    const char* text;
};

// en.json's "native." texts, in the file's order. A key ending in .one / .other is a plural form (plural() below).
inline constexpr English kEnglish[] = {
    {"native.menu.mods", "Mods"},
    {"native.menu.mods_off", "Mods: {reason}"},
    {"native.footer.loaded.one", "Kiln {version} - {count} Mod Loaded"},
    {"native.footer.loaded.other", "Kiln {version} - {count} Mods Loaded"},
    {"native.fallback.title", "Kiln {version}"},
    {"native.fallback.body",
     "Kiln's managed host (Kiln.Ui) is not running in this session, so the mod list and mod screens are unavailable here. "
     "Check kiln/logs/kiln.log."},
    {"native.fallback.close", "Press {key} to close."},
};

inline std::string english(std::string_view key) {
    for (const English& e : kEnglish)
        if (key == e.key) return e.text;
    return std::string(key);  // a key Kiln does not have: shown as the key (the test keeps that from shipping)
}

// Fills {name} and {name:format} (the format is not applied here: kiln.ui's values are already text) from `values`;
// {{ and }} are braces; a placeholder without a value stays as written. The rules of ModLocale.Fill.
inline std::string fill(std::string_view tmpl, std::initializer_list<std::pair<std::string_view, std::string_view>> values) {
    std::string out;
    out.reserve(tmpl.size() + 16);
    for (size_t i = 0; i < tmpl.size(); ++i) {
        const char c = tmpl[i];
        if ((c == '{' || c == '}') && i + 1 < tmpl.size() && tmpl[i + 1] == c) {
            out += c;
            ++i;
            continue;
        }
        if (c == '{') {
            const size_t end = tmpl.find('}', i + 1);
            if (end != std::string_view::npos) {
                std::string_view token = tmpl.substr(i + 1, end - i - 1);
                const size_t colon = token.find(':');
                std::string_view name = colon == std::string_view::npos ? token : token.substr(0, colon);
                while (!name.empty() && name.front() == ' ') name.remove_prefix(1);
                while (!name.empty() && name.back() == ' ') name.remove_suffix(1);
                bool done = false;
                for (const auto& [n, v] : values)
                    if (!name.empty() && n.size() == name.size() &&
                        std::equal(n.begin(), n.end(), name.begin(), [](char a, char b) { return (a | 0x20) == (b | 0x20); })) {
                        out.append(v);
                        done = true;
                        break;
                    }
                if (!done) out.append(tmpl.substr(i, end - i + 1));
                i = end;
                continue;
            }
        }
        out += c;
    }
    return out;
}

// The texts in use: what Kiln.Ui handed over (set), English for a key it did not. Any thread.
class Table {
public:
    // The texts of `language` (a lower-case tag, "zh-cn"); keys outside "native." are ignored. Returns whether anything
    // changed.
    bool set(std::string_view language, const std::vector<std::pair<std::string, std::string>>& pairs) {
        auto m = std::make_shared<std::map<std::string, std::string, std::less<>>>();
        for (const auto& [k, v] : pairs)
            if (k.rfind("native.", 0) == 0 && k.size() < 256 && v.size() < 4096) (*m)[k] = v;
        std::lock_guard lk(mu_);
        if (language_ == language && map_ && *map_ == *m) return false;
        language_ = std::string(language);
        map_ = std::move(m);
        ++generation_;
        return true;
    }

    std::string get(std::string_view key) const {
        std::lock_guard lk(mu_);
        if (map_) {
            const auto it = map_->find(key);
            if (it != map_->end()) return it->second;
        }
        return english(key);
    }

    // `base`.one for 1, else `base`.other (Kiln.Ui hands .one equal to .other for a language without a singular form).
    std::string plural(std::string_view base, long long n) const {
        const std::string one = std::string(base) + ".one", other = std::string(base) + ".other";
        std::lock_guard lk(mu_);
        if (map_) {
            const auto o = map_->find(other);
            if (o != map_->end()) {
                const auto s = map_->find(one);
                return n == 1 && s != map_->end() ? s->second : o->second;
            }
        }
        return english(n == 1 ? one : other);
    }

    std::string language() const {
        std::lock_guard lk(mu_);
        return language_;
    }

    uint64_t generation() const {
        std::lock_guard lk(mu_);
        return generation_;
    }

private:
    mutable std::mutex mu_;
    std::string language_;
    std::shared_ptr<const std::map<std::string, std::string, std::less<>>> map_;
    uint64_t generation_ = 0;
};

inline Table& table() {
    static Table t;
    return t;
}

inline std::string get(std::string_view key) { return table().get(key); }

}  // namespace kui::text
