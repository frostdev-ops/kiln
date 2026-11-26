// SPDX-License-Identifier: MIT
// kiln.content registry stage: the limits registry's back-end (registry/limits.cpp) on the host: the answers, the game's values
// from its code (fake sigdb and memory), apply() over composed rows (a hook only where a value is not the game's), the hooks'
// answers over a balancing table laid out as the game's, and the lab producer's probe over fake game functions that answer
// as the hooked ones do.
#include <cmath>
#include <cstdio>
#include <cstring>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

#include "../registry/limits.h"
#include "../registry/schema_ir.h"

using namespace kc::registry;
namespace lm = kc::registry::limits;

static int checks = 0, failed = 0;
#define CHECK(c, what)                                                     \
    do {                                                                   \
        ++checks;                                                          \
        if (!(c)) {                                                        \
            ++failed;                                                      \
            std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, std::string(what).c_str()); \
        }                                                                  \
    } while (0)

namespace {

constexpr int64_t kTable = 0x1b8;
std::set<std::string> g_installed;  // the fake installer's record: the hooks "in"
bool g_hooks_fail = false;

// the game's AltarLimit, then (when Kiln's hook is in) what the hook answers: as the hooked function behaves
uint32_t fake_altar(void* registry, uint32_t level) {
    uintptr_t table = 0;
    std::memcpy(&table, static_cast<uint8_t*>(registry) + 0x20, sizeof table);
    if (!table) return 1;
    int32_t rel = 0;
    std::memcpy(&rel, reinterpret_cast<void*>(table + kTable), 4);
    const uint8_t v = *reinterpret_cast<const uint8_t*>(table + kTable + rel + level);
    const uint32_t original = v > 16 ? 16 : v;
    return g_installed.count("altarCap") ? lm::altar_hook_answer(registry, level, original) : original;
}
float fake_gift() {
    float f = 0;
    if (g_installed.count("giftRange") && lm::gift_hook_answer(&f)) return f;
    return 2500.0f;
}
// what reading the getter's code gives: movss xmm0,[rip+d]; ret, the literal 2500 at code + 8 + d
uint8_t g_code[16] = {0xF3, 0x0F, 0x10, 0x05, 0, 0, 0, 0, 0xC3};
float g_literal = 2500.0f;

std::map<std::string, uintptr_t> g_symbols;

lm::Env env(bool with_gift = true) {
    lm::Env e;
    e.symbol = [with_gift](const char* n, uintptr_t* out) {
        const auto it = g_symbols.find(n);
        if (it == g_symbols.end() || (!with_gift && std::string(n) == "limits/GiftRange")) return false;
        *out = it->second;
        return true;
    };
    e.constant = [](const char* n, int64_t* out) {
        if (std::string(n) == "limits/altars.cap") return *out = 16, true;
        if (std::string(n) == "limits/altars.table") return *out = kTable, true;
        return false;
    };
    e.read = [](uintptr_t a, void* out, size_t n) {
        if (a == reinterpret_cast<uintptr_t>(&fake_gift)) return std::memcpy(out, g_code, n), true;  // the getter's code
        std::memcpy(out, reinterpret_cast<const void*>(a), n);
        return true;
    };
    e.install = [](const std::string& name, std::string* why) {
        if (g_hooks_fail) return *why = "kiln.hooks refused the hook", false;
        g_installed.insert(name);
        return true;
    };
    return e;
}

void setup(bool with_gift = true) {
    lm::reset();
    g_installed.clear();
    g_hooks_fail = false;
    g_symbols = {{"limits/AltarLimit", reinterpret_cast<uintptr_t>(&fake_altar)}, {"limits/GiftRange", reinterpret_cast<uintptr_t>(&fake_gift)}};
    // the literal lies at code + 8 + d, as the game's rip-relative read finds it: here d points from the fake's address at g_literal
    const int64_t d = static_cast<int64_t>(reinterpret_cast<intptr_t>(&g_literal) - (reinterpret_cast<intptr_t>(&fake_gift) + 8));
    const int32_t d32 = static_cast<int32_t>(d);
    if (d32 == d) std::memcpy(g_code + 4, &d32, 4);
    lm::bind(env(with_gift));
}

// a BootResource of the limits registry with one field's registry rows
map::RegistryMap g_reg;
stage::BootField field(const std::string& f, const std::string& path, Op op) {
    static Schema schema = generated_schema();
    g_reg.registry = "limits";
    g_reg.entries.kind = map::EntryKind::Hooks;
    map::Row row;
    row.field = f, row.paths = {path}, row.conv = f == "maxAltars" ? "int" : "float", row.backend = map::Backend::Hook;
    g_reg.rows[f] = row;
    ChainRow c;
    c.kind = WriterKind::Registry, c.mod = c.who = "limits-mod", c.op = op;
    return {{"limits", "game:limits", f}, &g_reg, &g_reg.rows[f], *schema.registry("limits")->field(f), {c}};
}
Op set(Value v) {
    Op o;
    o.set = true;
    o.value = std::move(v);
    return o;
}

}  // namespace

int main() {
    // ---- the answers
    CHECK(lm::altar_answer(16, 16, 0, std::nullopt) == 16, "no cap: the game's own answer");
    CHECK(lm::altar_answer(16, 16, 10, std::nullopt) == 10 && lm::altar_answer(7, 16, 10, std::nullopt) == 7, "a lower cap: min(the game's, cap)");
    CHECK(lm::altar_answer(16, 16, 20, 255u) == 20 && lm::altar_answer(16, 16, 20, 18u) == 18 && lm::altar_answer(5, 16, 20, 5u) == 5,
          "above the game's 16: min(the level's value, cap)");
    CHECK(lm::altar_answer(16, 16, 20, std::nullopt) == 16, "above 16 with an unreadable table: the game's own answer, never a guess");
    CHECK(lm::gift_answer(50) == 2500.0f && lm::gift_answer(1000) == 1e6f, "the gift getter answers the squared distance");
    CHECK(lm::gift_range_of(2500.0f) == 50.0 && !lm::gift_range_of(0) && !lm::gift_range_of(-4) && !lm::gift_range_of(NAN), "the game's range in metres");
    CHECK(lm::known("altarCap") && lm::known("giftRange") && !lm::known("maxAltars"), "the hook values by their rows' names");

    // ---- the game's values from its code
    setup();
    json v;
    std::string why;
    CHECK(lm::base_value("altarCap", &v, &why) && v == 16, "altarCap: the game's 16 (limits/altars.cap): " + why);
    CHECK(lm::base_value("giftRange", &v, &why) && v.get<double>() == 50.0, "giftRange: the getter's literal 2500 is 50 m: " + why);
    CHECK(!lm::base_value("noSuch", &v, &why) && why.find("no hook value") != std::string::npos, "an unknown value: " + why);
    g_code[0] = 0x90;
    CHECK(!lm::base_value("giftRange", &v, &why) && why.find("another shape") != std::string::npos, "a getter of another shape is refused: " + why);
    g_code[0] = 0xF3;

    // ---- apply: composed over the game's values; a hook only where a value is not the game's
    {
        stage::BootResource rb;
        rb.type = model::kHooksType;
        rb.fields = {field("maxAltars", "altarCap", set(Value::integer(20))), field("giftingRange", "giftRange", [] { Op o; o.mul = 4; return o; }())};
        std::vector<stage::BootOutcome> out;
        std::vector<std::string> notes;
        const size_t n = lm::apply(rb, &out, &notes);
        CHECK(n == 2 && out.size() == 2 && out[0].ok && out[1].ok && out[0].written == Value::integer(20) && out[1].written == Value::number(200),
              "both taken: 20 altars, 50 m x4 = 200 m");
        CHECK(g_installed == std::set<std::string>({"altarCap", "giftRange"}), "both hooks in");
        const lm::Answers a = lm::answers();
        CHECK(a.altar_cap == 20 && a.game_altar_cap == 16 && a.altar_table == kTable && a.gift_squared == 40000.0f, "the answers the hooks give");
        CHECK(notes.size() == 2 && notes[0].find("limits.maxAltars = 20 (Kiln's hook answers the game's check with it; the game's own is 16)") != std::string::npos,
              "kiln.log: " + (notes.empty() ? std::string() : notes[0]));
        const json st = lm::state();
        CHECK(st["altarCap"]["hooked"] == true && st["altarCap"]["value"] == 20 && st["giftRange"]["game"] == 50, "state: " + st.dump());
        // the hooks over a balancing table as the game lays it out: registry +0x20 -> table, table +0x1b8 -> {rel, count} -> levels
        std::vector<uint8_t> table(kTable + 8 + 3, 0);
        const int32_t rel = 8;
        const uint32_t count = 3;
        std::memcpy(table.data() + kTable, &rel, 4), std::memcpy(table.data() + kTable + 4, &count, 4);
        table[kTable + 8] = 5, table[kTable + 9] = 17, table[kTable + 10] = 30;
        uint8_t registry[0x40] = {};
        const uintptr_t t = reinterpret_cast<uintptr_t>(table.data());
        std::memcpy(registry + 0x20, &t, sizeof t);
        CHECK(lm::altar_hook_answer(registry, 2, 16) == 20 && lm::altar_hook_answer(registry, 1, 16) == 17 && lm::altar_hook_answer(registry, 0, 5) == 5,
              "cap 20: min(the level's value, 20) at each level");
        CHECK(lm::altar_hook_answer(registry, 7, 16) == 16, "a level past the table: the game's own answer");
        CHECK(fake_altar(registry, 2) == 20, "the hooked function answers 20 where the game says 16");
        float sq = 0;
        CHECK(lm::gift_hook_answer(&sq) && sq == 40000.0f && fake_gift() == 40000.0f, "the gift getter answers 200 m squared");
    }
    // the game's own values: nothing hooked, the game answers
    {
        setup();
        stage::BootResource rb;
        rb.type = model::kHooksType;
        rb.fields = {field("maxAltars", "altarCap", set(Value::integer(16)))};
        std::vector<stage::BootOutcome> out;
        std::vector<std::string> notes;
        CHECK(lm::apply(rb, &out, &notes) == 1 && out[0].ok && g_installed.empty() && lm::answers().altar_cap == 0,
              "16 is the game's own: taken, no hook");
        CHECK(!notes.empty() && notes[0].find("(the game's own: no hook)") != std::string::npos, "kiln.log says so");
    }
    // a range of 0 m is a value (no gift at any distance), not "the game's own"
    {
        setup();
        stage::BootResource rb;
        rb.type = model::kHooksType;
        rb.fields = {field("giftingRange", "giftRange", set(Value::number(0)))};
        std::vector<stage::BootOutcome> out;
        std::vector<std::string> notes;
        float sq = -1;
        CHECK(lm::apply(rb, &out, &notes) == 1 && out[0].ok && lm::answers().gift_answers && lm::gift_hook_answer(&sq) && sq == 0.0f && fake_gift() == 0.0f,
              "giftingRange 0: the getter answers 0, not the game's 2500");
    }
    // a build without the getter, and hooks that cannot go in: refused per field, with why
    {
        setup(false);
        stage::BootResource rb;
        rb.type = model::kHooksType;
        rb.fields = {field("giftingRange", "giftRange", set(Value::number(100))), field("maxAltars", "altarCap", set(Value::integer(30)))};
        g_hooks_fail = true;
        std::vector<stage::BootOutcome> out;
        std::vector<std::string> notes;
        CHECK(lm::apply(rb, &out, &notes) == 0 && out.size() == 2 && !out[0].ok && out[0].why.find("was not found on this game build") != std::string::npos,
              "no getter on this build: refused: " + (out.empty() ? std::string() : out[0].why));
        CHECK(out.size() == 2 && !out[1].ok && out[1].why.find("could not go in") != std::string::npos && lm::answers().altar_cap == 0,
              "the hook refused: not taken, the game's own stays: " + (out.size() < 2 ? std::string() : out[1].why));
    }
    // ---- the lab producer: a probe for a moment, the game's function called over a scratch table
    {
        setup();
        CHECK(lm::game_answer("altarCap", 255, &why) == 16.0 && lm::game_answer("giftRange", 0, &why) == 50.0, "before: the game's 16 and 50 m: " + why);
        CHECK(lm::probe_set("altarCap", 17, &why) && lm::probe_set("giftRange", 62.5, &why), "probes set: " + why);
        CHECK(lm::game_answer("altarCap", 255, &why) == 17.0 && lm::game_answer("giftRange", 0, &why) == 62.5, "the hooked functions answer the probes");
        lm::probe_clear("altarCap"), lm::probe_clear("giftRange");
        CHECK(lm::game_answer("altarCap", 255, &why) == 16.0 && lm::game_answer("giftRange", 0, &why) == 50.0, "taken back: the game's own again");
    }
    std::printf("limits-test: %d checks, %d failed\n", checks, failed);
    return failed ? 1 : 0;
}
