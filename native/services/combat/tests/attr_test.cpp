// SPDX-License-Identifier: MIT
// kiln.combat attributes, host test (no game, no Wine): the append-only ABI layout of kiln.combat v2, and the parser
// that reads the game's attribute resource. With KILN_ATTR_RESOURCE set to the raw bytes of the game's
// keen::AttributeContainerResource (run.sh extracts them with tools/kfc), it also resolves the five pairs from the real
// resource and checks them against EMF's ids and the game's formulas.
#include <cassert>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

#include "../attr_table.h"
#include "services/kiln_combat.h"

#include "emf/emf_api.h"  // EMF's attribute ids (MIT): the cross-check of the resolved ids

static int g_fail = 0;
#define CHECK(cond, ...)                                          \
    do {                                                          \
        if (!(cond)) {                                            \
            ++g_fail;                                             \
            std::printf("FAIL %s:%d: %s: ", __FILE__, __LINE__, #cond); \
            std::printf(__VA_ARGS__);                             \
            std::printf("\n");                                    \
        }                                                         \
    } while (0)

// ---------------------------------------------------------------- ABI layout (append-only) ----------------------
// v1 offsets are frozen: 10 function pointers after the 8-byte header. v2 entries follow `status`.
static_assert(KILN_COMBAT_VERSION == 2);
static_assert(offsetof(KilnCombatApi, header) == 0);
static_assert(offsetof(KilnCombatApi, health) == 8);
static_assert(offsetof(KilnCombatApi, set_health) == 16);
static_assert(offsetof(KilnCombatApi, heal) == 24);
static_assert(offsetof(KilnCombatApi, damage) == 32);
static_assert(offsetof(KilnCombatApi, hit_status) == 40);
static_assert(offsetof(KilnCombatApi, set_invulnerable) == 48);
static_assert(offsetof(KilnCombatApi, invulnerable) == 56);
static_assert(offsetof(KilnCombatApi, release_owner) == 64);
static_assert(offsetof(KilnCombatApi, set_listener) == 72);
static_assert(offsetof(KilnCombatApi, status) == 80);
static_assert(offsetof(KilnCombatApi, attribute_ids) == 88);
static_assert(offsetof(KilnCombatApi, attribute_get) == 96);
static_assert(offsetof(KilnCombatApi, attribute_set) == 104);
static_assert(offsetof(KilnCombatApi, attribute_add) == 112);
static_assert(sizeof(KilnCombatApi) == 120);
// the v1 structs did not move
static_assert(sizeof(KilnHealth) == 16);
static_assert(sizeof(KilnDamageRequest) == 32);
static_assert(sizeof(KilnHitStatus) == 24);
static_assert(sizeof(KilnCombatEvent) == 144);
// the new structs (managed mirror: Kiln.Combat/Native.cs AttributeIds / AttributeValue)
static_assert(offsetof(KilnAttributeIds, size) == 0 && offsetof(KilnAttributeIds, kind) == 4 &&
              offsetof(KilnAttributeIds, current_id) == 8 && offsetof(KilnAttributeIds, min_id) == 12 &&
              offsetof(KilnAttributeIds, max_id) == 16 && offsetof(KilnAttributeIds, component) == 20 &&
              offsetof(KilnAttributeIds, game_clamp) == 24 && sizeof(KilnAttributeIds) == 28);
static_assert(offsetof(KilnAttributeValue, size) == 0 && offsetof(KilnAttributeValue, id) == 4 &&
              offsetof(KilnAttributeValue, kind) == 8 && offsetof(KilnAttributeValue, current_id) == 12 &&
              offsetof(KilnAttributeValue, max_id) == 16 && offsetof(KilnAttributeValue, value) == 20 &&
              offsetof(KilnAttributeValue, current) == 24 && offsetof(KilnAttributeValue, min) == 28 &&
              offsetof(KilnAttributeValue, max) == 32 && offsetof(KilnAttributeValue, has_min) == 36 &&
              offsetof(KilnAttributeValue, has_max) == 37 && offsetof(KilnAttributeValue, is_current) == 38 &&
              sizeof(KilnAttributeValue) == 40);
static_assert(KILN_ATTR_HEALTH == 1 && KILN_ATTR_STAMINA == 2 && KILN_ATTR_MANA == 3 && KILN_ATTR_BODY_HEAT == 4 &&
              KILN_ATTR_OXYGEN == 5);
// the parser's kinds are the ABI's
static_assert(kc::attr::kKinds[0].kind == KILN_ATTR_HEALTH && kc::attr::kKinds[1].kind == KILN_ATTR_STAMINA &&
              kc::attr::kKinds[2].kind == KILN_ATTR_MANA && kc::attr::kKinds[3].kind == KILN_ATTR_BODY_HEAT &&
              kc::attr::kKinds[4].kind == KILN_ATTR_OXYGEN);

// ---------------------------------------------------------------- a synthetic resource ---------------------------
namespace {
using kc::attr::Layout;

struct GroupSpec {
    std::vector<std::pair<std::string, uint32_t>> attrs;
    std::vector<uint32_t> formula;
};

struct Builder {
    std::vector<uint8_t> b;
    size_t grow(size_t n) {
        const size_t at = b.size();
        b.resize(at + n);
        return at;
    }
    template <class T>
    void put(size_t off, T v) { std::memcpy(b.data() + off, &v, sizeof v); }
    // a BlobArray at `field` pointing at `to` (an offset in the buffer)
    void blob(size_t field, size_t to, uint32_t count) {
        put<int32_t>(field, static_cast<int32_t>(static_cast<int64_t>(to) - static_cast<int64_t>(field)));
        put<uint32_t>(field + 4, count);
    }
};

// Layout of the default Layout: groups of 28 bytes, ids 4, names 8, structures 16.
std::vector<uint8_t> build(const std::vector<GroupSpec>& groups, const Layout& L = Layout{}) {
    Builder B;
    B.grow(L.container_attrs + 8);  // the container: {attributes}
    const size_t array = B.grow(groups.size() * L.elem_size);
    B.blob(L.container_attrs, array, static_cast<uint32_t>(groups.size()));
    for (size_t g = 0; g < groups.size(); ++g) {
        const size_t base = array + g * L.elem_size;
        const GroupSpec& s = groups[g];
        B.put<uint32_t>(base + L.off_type, 0x9859f31eu);
        const size_t ids = B.grow(s.attrs.size() * L.id_size);
        for (size_t i = 0; i < s.attrs.size(); ++i) B.put<uint32_t>(ids + i * L.id_size, s.attrs[i].second);
        B.blob(base + L.off_ids, ids, static_cast<uint32_t>(s.attrs.size()));
        const size_t names = B.grow(s.attrs.size() * L.name_size);
        for (size_t i = 0; i < s.attrs.size(); ++i) {
            const size_t str = B.grow(s.attrs[i].first.size() + 1);
            std::memcpy(B.b.data() + str, s.attrs[i].first.c_str(), s.attrs[i].first.size() + 1);
            B.blob(names + i * L.name_size, str, static_cast<uint32_t>(s.attrs[i].first.size()));
        }
        B.blob(base + L.off_names, names, static_cast<uint32_t>(s.attrs.size()));
        const size_t st = B.grow(s.attrs.size() * L.st_size);
        const size_t calc = B.grow(s.formula.size() * L.cmd_size);
        for (size_t i = 0; i < s.formula.size(); ++i) B.put<uint32_t>(calc + i * L.cmd_size, s.formula[i]);
        B.blob(st + L.st_calc, calc, static_cast<uint32_t>(s.formula.size()));
        B.blob(base + L.off_structure, st, static_cast<uint32_t>(s.attrs.size()));
    }
    return B.b;
}

constexpr uint32_t cmd(uint32_t op, uint32_t arg) { return (op << 16) | arg; }

void synthetic() {
    const Layout L;
    const std::vector<GroupSpec> groups = {
        {{{"Stamina", 11}, {"Stamina_Min", 12}, {"Stamina_Max", 13}, {"Stamina_Recharge", 14}},
         {cmd(2, 0), cmd(2, 1), cmd(2, 2), cmd(12, 0xFFFF)}},
        {{{"Mana", 21}, {"Mana_Max", 22}, {"Mana_Min", 23}}, {cmd(2, 0), cmd(2, 2), cmd(2, 1), cmd(12, 0xFFFF)}},
        {{{"Oxygen", 31}, {"Oxygen_Max", 32}}, {cmd(2, 0), cmd(1, 0xFFFF), cmd(2, 1), cmd(12, 0xFFFF)}},  // 0..max: not [min,max]
        {{{"BodyHeat", 41}, {"BodyHeat_Min", 42}}, {}},                                                    // no maximum
    };
    const auto bytes = build(groups);
    std::vector<kc::attr::Group> parsed;
    std::string why;
    CHECK(kc::attr::parse_container(bytes.data(), bytes.size(), L, &parsed, &why), "%s", why.c_str());
    CHECK(parsed.size() == 4, "groups %zu", parsed.size());
    CHECK(parsed[0].root() == 11 && parsed[0].root_name() == "Stamina" && parsed[0].attrs.size() == 4, "group 0");
    CHECK(parsed[0].first_formula.size() == 4, "formula");

    kc::attr::Pair p;
    CHECK(kc::attr::resolve_pair(parsed, kc::attr::kKinds[1], L, &p, &why), "%s", why.c_str());
    CHECK(p.kind == KILN_ATTR_STAMINA && p.cur_id == 11 && p.min_id == 12 && p.max_id == 13 && p.min_slot == 1 &&
              p.max_slot == 2 && p.game_clamp && p.root_id == 11 && p.attrs.size() == 4,
          "stamina pair");
    CHECK(kc::attr::resolve_pair(parsed, kc::attr::kKinds[2], L, &p, &why), "%s", why.c_str());
    CHECK(p.cur_id == 21 && p.max_id == 22 && p.min_id == 23 && p.max_slot == 1 && p.min_slot == 2 && p.game_clamp,
          "mana: slots follow the names, the formula's operands follow the slots");
    CHECK(kc::attr::resolve_pair(parsed, kc::attr::kKinds[4], L, &p, &why), "%s", why.c_str());
    CHECK(p.max_id == 32 && p.min_id == 0 && p.min_slot == -1 && !p.game_clamp, "oxygen: no minimum, a 0..max formula is not game_clamp");
    CHECK(!kc::attr::resolve_pair(parsed, kc::attr::kKinds[3], L, &p, &why) && !why.empty(), "a group without a maximum is refused: %s", why.c_str());
    CHECK(!kc::attr::resolve_pair(parsed, kc::attr::kKinds[0], L, &p, &why), "no Health group: %s", why.c_str());

    // another layout (a build that moved the fields): the parser follows it
    Layout M = L;
    M.elem_size = 36, M.off_ids = 8, M.off_structure = 0x10, M.off_names = 0x18, M.off_type = 4, M.container_attrs = 8;
    const auto moved = build(groups, M);
    std::vector<kc::attr::Group> parsed2;
    CHECK(kc::attr::parse_container(moved.data(), moved.size(), M, &parsed2, &why), "%s", why.c_str());
    CHECK(parsed2.size() == 4 && parsed2[1].attrs[2].name == "Mana_Min" && parsed2[1].attrs[2].id == 23, "moved layout");
}

// Truncating or damaging the resource never reads outside the buffer (run under ASan).
void robustness(const std::vector<uint8_t>& good) {
    const Layout L;
    std::vector<kc::attr::Group> out;
    std::string why;
    size_t ok = 0;
    for (size_t n = 0; n < good.size(); n += (n < 256 ? 1 : 7)) {
        std::vector<uint8_t> cut(good.begin(), good.begin() + static_cast<std::ptrdiff_t>(n));
        ok += kc::attr::parse_container(cut.data(), cut.size(), L, &out, &why);  // must not crash
    }
    CHECK(ok < good.size(), "some truncations parse");  // a cut that still holds everything parsed is fine, not all do
    uint32_t seed = 12345;
    for (int i = 0; i < 4000; ++i) {
        std::vector<uint8_t> bad = good;
        for (int k = 0; k < 4; ++k) {
            seed = seed * 1664525u + 1013904223u;
            bad[(seed >> 8) % bad.size()] = static_cast<uint8_t>(seed >> 24);
        }
        kc::attr::parse_container(bad.data(), bad.size(), L, &out, &why);
        for (const auto& spec : kc::attr::kKinds) {
            kc::attr::Pair p;
            kc::attr::resolve_pair(out, spec, L, &p, &why);
        }
    }
    CHECK(!kc::attr::parse_container(nullptr, 0, L, &out, &why), "an empty resource is refused");
}

// ---------------------------------------------------------------- the real resource -------------------------------
void real(const std::vector<uint8_t>& bytes) {
    const Layout L;
    std::vector<kc::attr::Group> groups;
    std::string why;
    CHECK(kc::attr::parse_container(bytes.data(), bytes.size(), L, &groups, &why), "%s", why.c_str());
    CHECK(groups.size() > 40, "the game's resource has %zu groups", groups.size());
    struct Want {
        uint32_t kind;
        uint32_t cur, max;
    };
    // EMF's ids (emf_api.h, MIT) for the pairs it names; the others have no EMF constant.
    const Want emf[] = {{KILN_ATTR_HEALTH, EMF_ATTR_HEALTH, EMF_ATTR_MAX_HEALTH},
                        {KILN_ATTR_STAMINA, EMF_ATTR_STAMINA, EMF_ATTR_MAX_STAMINA},
                        {KILN_ATTR_MANA, EMF_ATTR_MANA, EMF_ATTR_MAX_MANA}};
    for (const auto& spec : kc::attr::kKinds) {
        kc::attr::Pair p;
        CHECK(kc::attr::resolve_pair(groups, spec, L, &p, &why), "%s: %s", spec.root, why.c_str());
        CHECK(p.root_id == p.cur_id && p.cur_slot == 0 && p.max_slot > 0 && p.min_slot > 0 && p.min_slot != p.max_slot,
              "%s slots", spec.root);
        CHECK(p.game_clamp, "%s: the game's formula must clamp current to [min, max]", spec.root);
        std::printf("  %-9s root 0x%08X current 0x%08X min 0x%08X (slot %d) max 0x%08X (slot %d), %zu slots, game clamp %d\n", spec.root,
                    p.root_id, p.cur_id, p.min_id, p.min_slot, p.max_id, p.max_slot, p.attrs.size(), p.game_clamp);
        for (const Want& w : emf)
            if (w.kind == spec.kind)
                CHECK(p.cur_id == w.cur && p.max_id == w.max, "%s: the resolved ids differ from EMF's 0x%08X / 0x%08X", spec.root, w.cur, w.max);
    }
}
}  // namespace

int main() {
    synthetic();
    robustness(build({{{{"Stamina", 11}, {"Stamina_Min", 12}, {"Stamina_Max", 13}}, {cmd(2, 0), cmd(2, 1), cmd(2, 2), cmd(12, 0xFFFF)}},
                      {{{"Health", 1}, {"Health_Min", 2}, {"Health_Max", 3}}, {cmd(2, 0), cmd(2, 1), cmd(2, 2), cmd(12, 0xFFFF)}}}));
    if (const char* path = std::getenv("KILN_ATTR_RESOURCE"); path && *path) {
        std::ifstream f(path, std::ios::binary);
        std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
        if (bytes.empty()) {
            std::printf("FAIL: %s is empty or unreadable\n", path);
            ++g_fail;
        } else {
            std::printf("real resource: %zu bytes\n", bytes.size());
            real(bytes);
            robustness(bytes);
        }
    } else {
        std::printf("KILN_ATTR_RESOURCE not set: the real-resource checks are skipped\n");
    }
    std::printf(g_fail ? "attr_test: %d FAIL\n" : "attr_test: PASS\n", g_fail);
    return g_fail ? 1 : 0;
}
