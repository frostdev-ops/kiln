// SPDX-License-Identifier: MIT
// kiln.content registry stage: field edits that change what existing saves hold (saves.h).
#include "saves.h"

#include <algorithm>

namespace kc::registry::saves {

namespace {

std::string text(const Value& v) { return v.kind == Value::None ? std::string("none") : v.text_of(); }

bool lower(const Value& after, const Value& before) { return after.numeric() && before.numeric() && after.as_double() < before.as_double(); }
bool higher(const Value& after, const Value& before) { return after.numeric() && before.numeric() && after.as_double() > before.as_double(); }

// Any element (or any leaf of an element) of after lower than the same of before, by index.
bool any_element_lower(const Value& after, const Value& before) {
    for (size_t i = 0; i < after.list.size() && i < before.list.size(); ++i) {
        const Value &a = after.list[i], &b = before.list[i];
        if (a.kind == Value::Struct && b.kind == Value::Struct) {
            for (size_t k = 0; k < a.keys.size(); ++k)
                if (const Value* bl = b.leaf(a.keys[k]); bl && lower(a.list[k], *bl)) return true;
        } else if (lower(a, b)) {
            return true;
        }
    }
    return false;
}

bool empty_slot(const Value& v) { return v.kind == Value::None || (v.kind == Value::Text && v.t.empty()); }

}  // namespace

const char* decision_name(Decision d) {
    switch (d) {
        case Decision::Refuse: return "refuse";
        case Decision::Guard: return "guard";
        case Decision::Warn: return "warn";
        case Decision::Report: return "report";
    }
    return "warn";
}

Value before_registry(const FieldOutcome& fo) {
    Value v = fo.base;
    for (const ChainRow& c : fo.chain) {
        if (c.kind == WriterKind::Registry) break;
        v = c.after;
    }
    return v;
}

Value of_json(const json& j) {
    if (j.is_number_integer()) return Value::integer(j.get<int64_t>());
    if (j.is_number()) return Value::number(j.get<double>());
    if (j.is_boolean()) return Value::boolean(j.get<bool>());
    if (j.is_string()) return Value::text(j.get<std::string>());
    if (j.is_array()) {
        std::vector<Value> list;
        for (const json& x : j) list.push_back(of_json(x));
        return Value::of_list(std::move(list));
    }
    if (j.is_object()) {
        Value v;
        v.kind = Value::Struct;
        for (auto it = j.begin(); it != j.end(); ++it) v.keys.push_back(it.key()), v.list.push_back(of_json(it.value()));  // sorted keys
        return v;
    }
    return Value{};
}

std::optional<Verdict> judge(const std::string& registry, const std::string& field, const Value& before, const Value& after) {
    if (after == before) return std::nullopt;
    if (registry == "items") {
        if (field == "maxStack" && lower(after, before))
            return Verdict{"stackCut", Decision::Guard,
                "lowers the largest stack " + text(before) + " -> " + text(after) + ": the game would delete the part of a saved stack above " + text(after) +
                    " at its next move; Kiln's stack guard keeps such stacks whole (new stacks hold at most " + text(after) + ")",
                "lowers the largest stack " + text(before) + " -> " + text(after) + " and Kiln's stack guard is not available here: the game would delete the " +
                    "part of a saved stack above " + text(after) + " at its next move; this edit was not applied"};
        if ((field == "levels.max" && lower(after, before)) || (field == "levels.min" && higher(after, before)))
            return Verdict{"levelRange", Decision::Refuse,
                "narrows the level range (" + field + " " + text(before) + " -> " + text(after) +
                    "): a world load clamps saved copies outside it and the save keeps the clamped level; this edit was not applied"};
        if (field == "perks" && before.kind == Value::List) {
            std::string slots;
            for (size_t i = 0; i < before.list.size(); ++i) {
                if (empty_slot(before.list[i])) continue;
                const bool gone = i >= after.list.size() || empty_slot(after.list[i]);
                if (gone || !(after.list[i] == before.list[i])) slots += (slots.empty() ? "" : ", ") + std::to_string(i);
            }
            if (!slots.empty())
                return Verdict{"perkSlots", Decision::Warn,
                    "changes perk slot(s) " + slots + " that existing copies can have: a copy in a save keeps its rarity and unlocked count, and now has the slot's " +
                        "new perk (or none); perks are read from the item's slots while it is used"};
        }
        return std::nullopt;
    }
    if (registry == "limits") {
        // the most Flame Altars: a world in a save can hold more than a lower limit (as for balance.altarsPerFlameLevel)
        if (field == "maxAltars" && lower(after, before))
            return Verdict{"baseLimits", Decision::Warn,
                "lowers limits.maxAltars " + text(before) + " -> " + text(after) + ": bases in saves keep the altars they hold above the new limit; " +
                    "what the game does with them is not traced"};
        return std::nullopt;
    }
    if (registry == "difficulty") {
        // a setting's bounds: the game clamps a world's own (Custom) setting into them when it reads the settings at start
        auto ends = [&](const char* tail) { return field.size() > 4 && field.compare(field.size() - 4, 4, tail) == 0; };
        if ((ends(".max") && lower(after, before)) || (ends(".min") && higher(after, before)))
            return Verdict{"settingsRange", Decision::Warn,
                "narrows a world setting's range (" + field + " " + text(before) + " -> " + text(after) + "): a world's own (Custom) setting outside it " +
                    "is clamped into it when the game reads the settings at start; whether the game then saves the clamped value is not traced"};
        return std::nullopt;
    }
    if (registry != "balance") return std::nullopt;
    if (field.rfind("durability.", 0) == 0 || field.rfind("rarityDurability.", 0) == 0)
        return Verdict{"durability", Decision::Report,
            field + " " + text(before) + " -> " + text(after) + ": saved copies keep their share of durability; their maximum and current durability follow " +
                "the new value when a world loads (nothing is lost)"};
    if (field == "levelCap" && lower(after, before))
        return Verdict{"progressionCap", Decision::Warn,
            "lowers the level cap " + text(before) + " -> " + text(after) + ": characters in saves above it keep their level; what the game does with them " +
                "past the cap is not traced"};
    if (field == "skillPointsPerLevel" && lower(after, before))
        return Verdict{"progressionCap", Decision::Warn,
            "lowers the skill points per level " + text(before) + " -> " + text(after) + ": characters in saves keep the points they already spent; what the " +
                "game does with more spent than the new total is not traced"};
    if (field == "xpPerLevel" || field.rfind("xpCurve.", 0) == 0)
        return Verdict{"experienceCurve", Decision::Warn,
            field + " changes: characters in saves keep their experience; how the game treats a level whose threshold moved is not traced"};
    if (field == "buildzoneSizesPerAltarLevel" && before.kind == Value::List && after.list.size() < before.list.size())
        return Verdict{"listShape", Decision::Refuse,
            "shortens the build-zone sizes to " + std::to_string(after.list.size()) + " altar level(s) from " + std::to_string(before.list.size()) +
                ": a saved altar at a higher level would have no build-zone size; this edit was not applied"};
    if ((field == "altarsPerFlameLevel" || field == "waterSourcesPerFlameLevel" || field == "buildzoneSizesPerAltarLevel") && any_element_lower(after, before))
        return Verdict{"baseLimits", Decision::Warn,
            "lowers " + field + ": bases in saves keep what they hold above the new limit (altars, water sources, buildings outside a smaller zone); " +
                "what the game does with them is not traced"};
    return std::nullopt;
}

std::vector<SaveNote> notes(const Result& r) {
    std::vector<SaveNote> out;
    for (const auto& [ref, fo] : r.fields) {
        if (is_raw_field(ref.field) || std::none_of(fo.chain.begin(), fo.chain.end(), [](const ChainRow& c) { return c.kind == WriterKind::Registry; })) continue;
        const auto v = judge(ref.registry, ref.field, before_registry(fo), fo.result);
        if (v && v->decision != Decision::Refuse) out.push_back({ref, v->cls, decision_name(v->decision), v->text});
    }
    return out;
}

json notes_json(const std::vector<SaveNote>& notes) {
    json a = json::array();
    for (const SaveNote& n : notes)
        a.push_back({{"field", n.field.text()}, {"registry", n.field.registry}, {"id", n.field.id}, {"class", n.cls}, {"decision", n.decision}, {"text", n.text}});
    return a;
}

}  // namespace kc::registry::saves
