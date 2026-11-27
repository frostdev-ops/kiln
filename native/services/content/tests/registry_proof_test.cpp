// SPDX-License-Identifier: MIT
// kiln.content registry stage: the lab's registry proof producer, native half (registry/proof.{h,cpp}), on the host with
// ASan/UBSan, over a fake tree view of the game's data and hand-made reflection types:
//   - without the request file nothing happens: the request is looked up once, nothing else is called (no base read, no
//     regmap load, no file written): player installs and normal servers are unchanged;
//   - regmapDigest is the contract kilnctl checks (the same vector as runner/kilnctl's tests);
//   - probes: another value the schema allows, per kind;
//   - a boot-generation sample on a resource's bytes by the boot back-end itself: read, probe written, read back, raw members,
//     composed again without the probe (the base's bytes);
//   - with the request: the boot suite and the values export written, in the formats kilnctl reads; the boot suite also reads
//     the read-only rows of the registries without a boot row (the first entry that reads a value; none that reads: no sample).
#include <cstdio>
#include <cstring>
#include <set>
#include <stdexcept>
#include <string>
#include <tuple>

#include "../registry/proof.h"
#include "../registry/limits.h"
#include "../registry/schema_ir.h"

namespace kc {
bool guid_parse(const std::string& s, uint8_t out[16]);
namespace rbtest {
void balance_type();  // keen::BalancingTable {playerLevelCap u32 +0, skillPointsPerLevel u32 +4}, 8 bytes
void byte_blob_type(uintptr_t unreadable);
void byte_table_type();  // keen::ByteTable {altarsPerFlameLevel u8[21] +0, playerLevelCap u32 +24}, 28 bytes
void timed_type();       // test::Timed {t keen::Time +0}, 8 bytes
}
}  // namespace kc

namespace {
using namespace kc::registry;
int checks = 0;
void check(bool ok, const std::string& why) {
    if (!ok) throw std::runtime_error(why);
    ++checks;
}
std::string g(int n) {
    char b[40];
    std::snprintf(b, sizeof b, "%08x-0000-4000-8000-%012x", n, n);
    return b;
}
KilnAssetKey key_of(const std::string& guid, const std::string& type) {
    KilnAssetKey k{};
    kc::guid_parse(guid, k.guid);
    k.type = kiln::boot_gd::fnv1a32(type);
    return k;
}
const char* kBalance = "82706b40-61b1-4b8f-8b23-dcec6971bda1";
const std::string kExe = "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2";

struct TreeView final : kiln::boot_view::BaseView {
    std::map<std::string, json> values;
    std::map<std::string, std::vector<std::string>> types;
    const char* source() const override { return "live"; }
    bool ready(std::string*) override { return true; }
    std::string digest() override { return "d"; }
    KilnResult tree(std::string_view q, size_t cap, size_t, uint64_t, std::string* out, size_t* req, size_t*, std::string* why) override {
        const auto it = values.find(std::string(q.substr(9, 36)));
        if (it == values.end()) return *why = "none", KILN_ERR_NOT_FOUND;
        const std::string text = json{{"value", it->second}}.dump();
        if (text.size() + 1 > cap) return *req = text.size() + 1, KILN_ERR_BUFFER_TOO_SMALL;
        *out = text;
        return KILN_OK;
    }
    KilnResult bytes(const KilnAssetKey&, size_t, std::string*, std::string* why) override { return *why = "trees", KILN_ERR_UNAVAILABLE; }
    void release() override {}
    void add(const std::string& type, const std::string& guid, json v) {
        types[type].push_back(guid);
        values[guid] = std::move(v);
    }
};

// The registries of the test: items (a live row, read-only rows, an unmapped row) and balance (one resource; a boot and a
// read-only row).
map::RegMap regmap() {
    std::vector<std::string> errs;
    map::RegMap rm;
    map::load_common(json::parse(R"({"regmap": 1, "registry": "items",
        "entries": {"kind": "resource", "resourceType": "keen::ItemInfo", "name": "debugName", "guid": "objectId", "id": "itemId"},
        "rows": {"maxStack": {"paths": ["maxStackSize"], "conv": "int", "read": "worldLoad", "backend": "live"},
                 "levels.min": {"paths": ["maxStackSize"], "conv": "int", "read": "now", "backend": "none"},
                 "levels.max": {"paths": ["levelCap"], "conv": "int", "read": "now", "backend": "none"},
                 "hasDamage": {"paths": ["damageSetup.isSet"], "conv": "bool", "read": "now", "backend": "none"},
                 "rolled": {"paths": ["nowhere"], "conv": "bool", "read": "now", "backend": "none"},
                 "comfort": {"state": "unmapped", "reason": "not mapped yet"}}})"), &rm.registries["items"], &errs);
    map::load_common(json::parse(std::string(R"({"regmap": 1, "registry": "balance",
        "entries": {"kind": "single", "resourceType": "keen::BalancingTable", "resourceGuid": ")") + kBalance + R"(", "unnamed": "balance"},
        "rows": {"levelCap": {"paths": ["playerLevelCap"], "conv": "int", "read": "restart", "backend": "boot"},
                 "skillPointsPerLevel": {"paths": ["skillPointsPerLevel"], "conv": "int", "read": "restart", "backend": "none"}}})"),
                     &rm.registries["balance"], &errs);
    check(errs.empty(), "the test regmap loads: " + (errs.empty() ? std::string() : errs[0]));
    rm.side = "server", rm.exe_sha256 = kExe;
    return rm;
}

std::vector<uint8_t> balance_bytes(uint32_t cap, uint32_t sp) {
    std::vector<uint8_t> b(8, 0);
    std::memcpy(b.data(), &cap, 4);
    std::memcpy(b.data() + 4, &sp, 4);
    return b;
}

void nothing_without_the_request() {
    proof::Env e;
    int looked = 0, other = 0;
    e.read_data = [&](const std::string& rel) -> std::optional<std::string> {
        ++looked;
        check(rel == proof::kRequest, "only the request is looked up: " + rel);
        return std::nullopt;
    };
    e.write_data = [&](const std::string&, const std::string&) { return ++other, true; };
    e.source = [&](std::string*) -> std::unique_ptr<model::Source> { return ++other, nullptr; };
    e.bytes = [&](const KilnAssetKey&, std::vector<uint8_t>*, std::string*) { return ++other, false; };
    e.regmap = [&](map::RegMap*, std::vector<std::string>*) { return ++other, false; };
    e.common_files = [&] { return ++other, std::vector<std::pair<std::string, std::string>>{}; };
    e.log = [&](int, const std::string&) { ++other; };
    const Schema schema = generated_schema();
    e.schema = &schema;
    check(!proof::requested(e) && looked == 1, "no request file: not requested");
    looked = 0;
    check(proof::run(e).empty(), "no request file: nothing written");
    check(looked == 1 && other == 0, "no request file: one lookup and nothing else (no base read, no regmap, no file, no log)");
    // review2 N1: with an existence check the request is never read (one file-system lookup at every start)
    int exists = 0;
    looked = 0;
    e.exists = [&](const std::string& rel) { return check(rel == proof::kRequest, "only the request: " + rel), ++exists, false; };
    check(!proof::requested(e) && exists == 1 && looked == 0, "the existence check alone, the request file not read");
    e.exists = [&](const std::string&) { return ++exists, true; };
    check(proof::requested(e) && looked == 0, "a request file there is requested without reading it");
}

void digest_contract() {
    // the same vector as runner/kilnctl (regproof::regmap tests and tests/regproof.rs): sha256 over "<name>\n<sha256>\n" by name
    check(proof::regmap_digest({{"loot.json", "{}"}, {"items.json", "{\"rows\": {}}"}}) ==
              "8ddfafff9317e303bff4f9f2c2f4a5eb1575391c0a58b1e30317e032f829d019",
          "regmapDigest as kilnctl computes it");
}

void probes() {
    FieldSpec n;
    n.type = FieldType::Int, n.min = 1, n.max = 60;
    check(proof::probe_for(n, Value::integer(55)) == Value::integer(56), "a whole number moves up within its range");
    check(proof::probe_for(n, Value::integer(60)) == Value::integer(59), "... and down at its top");
    FieldSpec f;
    f.type = FieldType::Float;
    check(proof::probe_for(f, Value::number(2.0)) == Value::number(2.5), "a number grows by a quarter");
    check(proof::probe_for(f, Value::number(0)) == Value::number(1.0), "zero becomes one");
    FieldSpec b;
    b.type = FieldType::Bool;
    check(proof::probe_for(b, Value::boolean(true)) == Value::boolean(false), "a bool flips");
    FieldSpec en;
    en.type = FieldType::Enum, en.enum_values = {"common", "rare"};
    check(proof::probe_for(en, Value::text("common")) == Value::text("rare"), "another enum member");
    FieldSpec l;
    l.type = FieldType::List, l.element = FieldType::Int, l.max = 100;
    const auto pl = proof::probe_for(l, Value::of_list({Value::integer(4), Value::integer(9)}));
    check(pl && pl->list[0] == Value::integer(5) && pl->list[1] == Value::integer(9), "a list's first number moves, the rest stays");
    check(!proof::probe_for(l, Value::of_list({})), "an empty list has no probe");
    FieldSpec r;
    r.type = FieldType::Id;
    check(!proof::probe_for(r, Value::text("game:x")), "a reference has no probe here");
}

void boot_sample_on_bytes() {
    kc::rbtest::balance_type();
    const Schema schema = generated_schema();
    const map::RegMap rm = regmap();
    const map::RegistryMap& reg = rm.registries.at("balance");
    const auto base = balance_bytes(55, 4);
    const KilnAssetKey key = key_of(kBalance, "keen::BalancingTable");
    const json s = proof::boot_sample(reg, *schema.registry("balance"), "levelCap", "game:balance", key, "keen::BalancingTable", base);
    check(!s.contains("error"), "the sample has no error: " + s.dump());
    check(s["field"] == "balance.levelCap" && s["entry"] == "game:balance" && s["element"].is_null(), "names the field and the entry");
    check(s["read"] == 55 && s["rawBefore"] == json::array({55}), "read 55 through the schema and raw: " + s.dump());
    check(s["probe"] == 56 && s["readBack"] == 56 && s["rawAfter"] == json::array({56}), "the boot back-end wrote the probe: " + s.dump());
    check(s["reverted"] == true && s["bytesBefore"] == s["bytesAfter"], "composed again without the probe: the base's bytes");
    const json ro = proof::boot_sample(reg, *schema.registry("balance"), "skillPointsPerLevel", "game:balance", key, "keen::BalancingTable", base);
    check(ro["read"] == 4 && ro["rawBefore"] == json::array({4}) && !ro.contains("probe") && !ro.contains("error"), "a read-only field is read only: " + ro.dump());
    const json none = proof::boot_sample(reg, *schema.registry("balance"), "nothing", "game:balance", key, "keen::BalancingTable", base);
    check(none.is_null(), "a field without a mapped row gives no sample");
}

// R3b (the lab's first proof of difficulty: six rows "changed"): a boot-routed time field's raw value is the member's
// nanoseconds, as the record's contract says (the reflection reads a keen::Time as seconds)
void boot_time_raw_is_nanoseconds() {
    kc::rbtest::timed_type();
    const Schema schema = generated_schema();
    map::RegistryMap reg;
    reg.registry = "difficulty";
    map::Row row;
    row.field = "dayTimeDuration.max", row.paths = {"t"}, row.conv = "time", row.backend = map::Backend::Boot;
    reg.rows[row.field] = row;
    std::vector<uint8_t> base(8);
    const int64_t ns = 3600LL * 1000000000LL;
    std::memcpy(base.data(), &ns, 8);
    const json s = proof::boot_sample(reg, *schema.registry("difficulty"), "dayTimeDuration.max", "game:difficulty",
                                      key_of("46e14df6-fb8f-4f66-9463-e23b9e19e48e", "test::Timed"), "test::Timed", base);
    check(!s.is_null() && !s.contains("error") && s["read"] == 3600.0 && s["rawBefore"] == json::array({ns}),
          "read 3600 s, raw 3.6e12 ns: " + s.dump());
    check(s["probe"] == 4500.0 && s["rawAfter"] == json::array({4500LL * 1000000000LL}), "the probe written: its raw nanoseconds: " + s.dump());
}

// The balance table's per-flame-level altar and water-source counts are arrays of 21 bytes: the registry reads and writes
// them as lists of numbers, never as the gamedata dumps' "(21 bytes)".
void byte_array_lists() {
    kc::rbtest::byte_table_type();
    const Schema schema = generated_schema();
    std::vector<uint8_t> base(28, 0);
    for (int i = 0; i < 21; ++i) base[static_cast<size_t>(i)] = static_cast<uint8_t>(i < 4 ? 1 + i : 6);
    const uint32_t cap = 50;
    std::memcpy(base.data() + 24, &cap, 4);
    json m;
    std::string why;
    check(model::bytes_member(base.data(), base.size(), "keen::ByteTable", "altarsPerFlameLevel", &m, &why) && m.is_array() && m.size() == 21 &&
              m[0] == 1 && m[3] == 4 && m[20] == 6,
          "a byte array member reads as its 21 numbers: " + m.dump() + " " + why);
    std::vector<std::string> errs;
    map::RegistryMap reg;
    map::load_common(json::parse(R"({"regmap": 1, "registry": "balance",
        "entries": {"kind": "single", "resourceType": "keen::ByteTable", "resourceGuid": "82706b40-61b1-4b8f-8b23-dcec6971bda1", "unnamed": "balance"},
        "rows": {"altarsPerFlameLevel": {"paths": ["altarsPerFlameLevel"], "conv": "list", "read": "restart", "backend": "boot"}}})"), &reg, &errs);
    check(errs.empty(), "the byte table's regmap loads");
    const json s = proof::boot_sample(reg, *schema.registry("balance"), "altarsPerFlameLevel", "game:balance", key_of(kBalance, "keen::ByteTable"),
                                      "keen::ByteTable", base);
    check(!s.contains("error"), "the list round-trips in the boot generation: " + s.dump());
    check(s["read"].size() == 21 && s["read"][0] == 1 && s["probe"][0] == 2 && s["readBack"] == s["probe"], "read, probe, read back: " + s.dump());
    check(s["rawAfter"][0][0] == 2 && s["rawBefore"][0][0] == 1 && s["bytesBefore"] == s["bytesAfter"], "raw before and after, bytes back");
}

// review2 S6: a boot write refused because the row's path is not a scalar member (or has no such member) is a disagreement
// (an error sample, kilnctl: changed); a refusal for anything else stays missing evidence.
void structural_refusals_are_disagreements() {
    check(proof::disagreement("\"objectId\" is not a scalar member of the resource"), "not a scalar member");
    check(proof::disagreement("\"playerLevelCapp\": no such member"), "no such member");
    check(proof::disagreement("\"a.b\": keen::X has no member b"), "has no member");
    check(!proof::disagreement("\"playerLevelCap\": 70000 is outside the member's range"), "a range refusal is not a disagreement");
    check(!proof::disagreement(""), "no reason, no disagreement");
}

void empty_boot_list_is_missing_evidence() {
    kc::rbtest::byte_blob_type(0);
    const Schema schema = generated_schema();
    std::vector<std::string> errors;
    map::RegistryMap reg;
    map::load_common(json::parse(R"({"regmap": 1, "registry": "balance",
        "entries": {"kind": "single", "resourceType": "test::ByteBlob", "resourceGuid": "82706b40-61b1-4b8f-8b23-dcec6971bda1", "unnamed": "balance"},
        "rows": {"altarsPerFlameLevel": {"paths": ["values"], "conv": "list", "read": "restart", "backend": "boot"}}})"), &reg, &errors);
    const json s = proof::boot_sample(reg, *schema.registry("balance"), "altarsPerFlameLevel", "game:balance", {}, "test::ByteBlob", std::vector<uint8_t>(8));
    check(s.is_null(), "an empty boot list is no evidence, not a changed/error sample: " + s.dump());
}

void with_the_request() {
    kc::rbtest::balance_type();
    const Schema schema = generated_schema();
    TreeView view;
    auto item = [&](int n, const std::string& name, int stack) {
        view.add("keen::ItemInfo", g(n), {{"itemId", 1000 + n}, {"objectId", g(n)}, {"debugName", name}, {"maxStackSize", stack}, {"damageSetup", {{"isSet", n == 2}}}});
    };
    item(1, "Wood_Logs", 10);
    item(2, "Plank", 20);
    view.values[g(2)]["levelCap"] = 7;  // only the second item has it: the first reads no levels.max
    view.add("keen::BalancingTable", kBalance, {{"playerLevelCap", 55}, {"skillPointsPerLevel", 4}});
    std::map<std::string, std::string> files;
    proof::Env e;
    e.read_data = [&](const std::string& rel) -> std::optional<std::string> {
        return rel == proof::kRequest ? std::optional<std::string>("{}") : std::nullopt;
    };
    e.write_data = [&](const std::string& rel, const std::string& text) { return files[rel] = text, true; };
    e.source = [&](std::string*) {
        return model::tree_source(view, [&](const std::string& type, std::vector<KilnAssetKey>* out, std::string*) {
            for (const std::string& gu : view.types[type]) out->push_back(key_of(gu, type));
            return true;
        });
    };
    e.bytes = [&](const KilnAssetKey& k, std::vector<uint8_t>* out, std::string* why) {
        if (k.type != kiln::boot_gd::fnv1a32("keen::BalancingTable")) return *why = "only the balance table here", false;
        return *out = balance_bytes(55, 4), true;
    };
    e.regmap = [](map::RegMap* out, std::vector<std::string>*) { return *out = regmap(), true; };
    e.common_files = [] { return std::vector<std::pair<std::string, std::string>>{{"loot.json", "{}"}, {"items.json", "{\"rows\": {}}"}}; };
    e.schema = &schema;
    e.side = "server", e.exe_sha256 = kExe, e.writer = "none";
    const auto written = proof::run(e);
    check(written.size() == 2, "two files written: " + std::to_string(written.size()));
    const json rt = json::parse(files.at("proof/roundtrip-server-boot.json"));
    check(rt["format"] == 1 && rt["kind"] == "registry-roundtrip" && rt["generation"] == "boot" && rt["exeSha256"] == kExe &&
              rt["regmapDigest"] == "8ddfafff9317e303bff4f9f2c2f4a5eb1575391c0a58b1e30317e032f829d019",
          "the boot suite's head: " + rt.dump().substr(0, 300));
    check(rt["samples"].size() == 5, "balance's boot and read-only fields, and the read-only items fields that read: " + rt["samples"].dump());
    bool probed = false;
    std::map<std::string, json> by;
    for (const json& s : rt["samples"]) probed |= s["field"] == "balance.levelCap" && s["readBack"] == 56, by[s["field"]] = s;
    check(probed, "levelCap round-tripped in the boot generation");
    const json& lo = by["items.levels.min"];
    check(lo["entry"] == "game:wood_logs" && lo["read"] == 10 && lo["rawBefore"] == json::array({10}) && !lo.contains("probe") &&
              !lo.contains("error") && lo["element"].is_null(),
          "a read-only items field read from the first entry, raw by its member, never written: " + lo.dump());
    const json& hi = by["items.levels.max"];
    check(hi["entry"] == "game:plank" && hi["read"] == 7 && hi["rawBefore"] == json::array({7}) && !hi.contains("probe"),
          "an entry that reads no value is passed over for the next: " + hi.dump());
    check(by["items.hasDamage"]["entry"] == "game:plank" && by["items.hasDamage"]["read"] == true,
          "read-only bool evidence prefers true, not zero padding: " + by["items.hasDamage"].dump());
    check(!by.count("items.rolled") && !by.count("items.maxStack"),
          "a read-only field no entry reads has no sample (unproven, not an error); a live field is the live suite's");
    const json v = json::parse(files.at("proof/values-server.json"));
    check(v["kind"] == "registry-values" && v["base"]["origin"] == "live" && v["base"]["writer"] == "none" && v["regmapDigest"] == rt["regmapDigest"],
          "the values export's head: " + v.dump().substr(0, 300));
    check(v["registries"]["items"]["game:wood_logs"]["maxStack"] == 10 && v["registries"]["items"]["game:plank"]["maxStack"] == 20,
          "every item through the schema: " + v["registries"]["items"].dump());
    check(!v["registries"]["items"]["game:plank"].contains("comfort"), "an unmapped field is not exported");
    check(v["registries"]["balance"].begin().value()["levelCap"] == 55, "the balance entry: " + v["registries"]["balance"].dump());
    // A mapped boot row whose entries are an array is outside this producer's coverage, not a disproof.
    view.values[kBalance] = {{"entries", json::array({{{"playerLevelCap", 55}, {"skillPointsPerLevel", 4}}})}};
    e.regmap = [](map::RegMap* out, std::vector<std::string>*) {
        *out = regmap();
        out->registries["balance"].entries.kind = map::EntryKind::Array;
        out->registries["balance"].entries.array = "entries";
        return true;
    };
    proof::run(e);
    const json array_suite = json::parse(files.at("proof/roundtrip-server-boot.json"));
    for (const json& sample : array_suite["samples"])
        check(sample["field"].get<std::string>().rfind("balance.", 0) != 0, "unsupported array-entry boot fields get no error sample: " + sample.dump());

}

}  // namespace

// R3b: a hooks registry's sample (limits.h): the game's value from its code, the hooked function's answer with the probe given
// for a moment, and the game's own again after it (the fake functions answer as the hooked game functions do)
namespace hooks {
std::set<std::string> installed;
uint32_t altar(void* registry, uint32_t level) {
    uintptr_t table = 0;
    std::memcpy(&table, static_cast<uint8_t*>(registry) + 0x20, sizeof table);
    int32_t rel = 0;
    std::memcpy(&rel, reinterpret_cast<void*>(table + 0x1b8), 4);
    const uint8_t v = *reinterpret_cast<const uint8_t*>(table + 0x1b8 + rel + level);
    const uint32_t original = v > 16 ? 16 : v;
    return installed.count("altarCap") ? kc::registry::limits::altar_hook_answer(registry, level, original) : original;
}
float gift() {
    float f = 0;
    return installed.count("giftRange") && kc::registry::limits::gift_hook_answer(&f) ? f : 2500.0f;
}
uint8_t code[9] = {0xF3, 0x0F, 0x10, 0x05, 0, 0, 0, 0, 0xC3};
float literal = 2500.0f;
}  // namespace hooks

void hooks_registry_samples() {
    namespace lm = kc::registry::limits;
    lm::reset();
    hooks::installed.clear();
    const int64_t d = reinterpret_cast<intptr_t>(&hooks::literal) - (reinterpret_cast<intptr_t>(&hooks::gift) + 8);
    const int32_t d32 = static_cast<int32_t>(d);
    std::memcpy(hooks::code + 4, &d32, 4);
    lm::Env e;
    e.symbol = [](const char* n, uintptr_t* out) {
        if (std::string(n) == "limits/AltarLimit") return *out = reinterpret_cast<uintptr_t>(&hooks::altar), true;
        if (std::string(n) == "limits/GiftRange") return *out = reinterpret_cast<uintptr_t>(&hooks::gift), true;
        return false;
    };
    e.constant = [](const char* n, int64_t* out) { return std::string(n) == "limits/altars.cap" ? (*out = 16, true) : std::string(n) == "limits/altars.table" ? (*out = 0x1b8, true) : false; };
    e.read = [](uintptr_t a, void* out, size_t n) {
        if (a == reinterpret_cast<uintptr_t>(&hooks::gift)) return std::memcpy(out, hooks::code, n), true;
        std::memcpy(out, reinterpret_cast<const void*>(a), n);
        return true;
    };
    e.install = [](const std::string& name, std::string*) { return hooks::installed.insert(name), true; };
    lm::bind(e);
    map::RegistryMap reg;
    reg.registry = "limits";
    reg.entries.kind = map::EntryKind::Hooks, reg.entries.unnamed = "limits";
    for (const auto& [f, path, conv] : {std::tuple{"maxAltars", "altarCap", "int"}, std::tuple{"giftingRange", "giftRange", "float"}}) {
        map::Row r;
        r.field = f, r.paths = {path}, r.conv = conv, r.backend = map::Backend::Hook;
        reg.rows[f] = r;
    }
    const Schema schema = generated_schema();
    std::string why;
    const json a = proof::hook_sample(reg, *schema.registry("limits"), "maxAltars", "game:limits", &why);
    check(!a.is_null() && a["read"] == 16 && a["probe"] == 17 && a["readBack"] == 17 && a["rawBefore"] == json::array({16}) && a["rawAfter"] == json::array({17}) &&
              a["reverted"] == true && a["bytesBefore"] == a["bytesAfter"],
          "maxAltars: the game's 16, the hooked function answers the probe 17 over a table of 255, 16 again after: " + a.dump() + " " + why);
    const json g = proof::hook_sample(reg, *schema.registry("limits"), "giftingRange", "game:limits", &why);
    check(!g.is_null() && g["read"] == 50.0 && g["probe"] == 62.5 && g["readBack"] == 62.5 && g["rawAfter"] == json::array({62.5}) && g["reverted"] == true,
          "giftingRange: 50 m, the probe 62.5 m answered squared by the getter, 50 m again: " + g.dump() + " " + why);
    check(kc::registry::limits::answers().altar_cap == 0 && kc::registry::limits::answers().gift_squared == 0, "the probes are taken back: the game's own answers");
    lm::reset();
    const json none = proof::hook_sample(reg, *schema.registry("limits"), "maxAltars", "game:limits", &why);
    check(none.is_null() && why.find("not found on this game build") != std::string::npos, "a build without the hook: no sample (unproven), with why: " + why);
}

int main() {
    int failed = 0;
    const std::pair<const char*, void (*)()> groups[] = {{"nothing_without_the_request", nothing_without_the_request},
                                                       {"digest_contract", digest_contract},
                                                       {"probes", probes},
                                                       {"boot_sample_on_bytes", boot_sample_on_bytes},
                                                       {"byte_array_lists", byte_array_lists},
                                                       {"empty_boot_list_is_missing_evidence", empty_boot_list_is_missing_evidence},
                                                       {"structural_refusals_are_disagreements", structural_refusals_are_disagreements},
                                                       {"with_the_request", with_the_request},
                                                       {"hooks_registry_samples", hooks_registry_samples},
                                                       {"boot_time_raw_is_nanoseconds", boot_time_raw_is_nanoseconds}};
    for (const auto& [name, fn] : groups) {
        try {
            fn();
        } catch (const std::exception& e) {
            ++failed;
            std::printf("registry-proof-test: FAIL %s: %s (after %d checks)\n", name, e.what(), checks);
        }
    }
    if (failed) {
        std::printf("registry-proof-test: %d group(s) failed, %d checks passed\n", failed, checks);
        return 1;
    }
    std::printf("registry-proof-test: %d checks passed\n", checks);
    return 0;
}
