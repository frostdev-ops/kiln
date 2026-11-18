// SPDX-License-Identifier: MIT
// Host test of gate-priority-test (tests/run.sh, ASan/UBSan and TSan): the mod's code linked in and run against the
// fake Kiln of harness.h, one scenario per process: kit_test <scenario> <sigdb/types dir>. Scenario "padding" checks
// the padding rule over sigdb's reflection of both exact builds against an independent walk of the same JSON, and the
// run file's parser.
#include "scenarios.h"

#include "kit.h"

namespace {
using namespace harness;
using nlohmann::json;

// An independent reading of the rule, straight from the JSON: offsets, sizes and alignments as sigdb records them.
// Returns the padding bytes, or nullopt-like false when the layout is not explained.
bool json_leaf(int kind) { return (kind >= KILN_TYPE_BOOL && kind <= KILN_TYPE_BITMASK64) || kind == KILN_TYPE_GUID; }
bool json_empty(const json& by, const std::string& n, int d) {
    if (d > 32 || !by.contains(n)) return false;
    const auto& t = by.at(n);
    if (t.at("kind").get<int>() != KILN_TYPE_STRUCT || (t.contains("fields") && !t.at("fields").empty()) ||
        t.at("size").get<uint64_t>() > 1)
        return false;
    return !t.contains("inner") || json_empty(by, t.at("inner").get<std::string>(), d + 1);
}
bool json_pads(const json& by, const std::string& n, uint64_t base, std::vector<uint32_t>& out, int d) {
    if (d > 32 || !by.contains(n)) return false;
    const auto& t = by.at(n);
    const int kind = t.at("kind").get<int>();
    const uint64_t size = t.at("size").get<uint64_t>();
    if (json_leaf(kind)) return true;
    if (kind == KILN_TYPE_TYPEDEF) return t.contains("inner") && by.contains(t.at("inner").get<std::string>()) &&
                                          by.at(t.at("inner").get<std::string>()).at("size").get<uint64_t>() == size &&
                                          json_pads(by, t.at("inner").get<std::string>(), base, out, d + 1);
    if (kind == KILN_TYPE_STATIC_ARRAY) {
        if (!t.contains("inner") || !by.contains(t.at("inner").get<std::string>())) return false;
        const std::string e = t.at("inner").get<std::string>();
        const uint64_t es = by.at(e).at("size").get<uint64_t>();
        if (!es || size % es) return false;
        for (uint64_t i = 0; i < size / es; ++i)
            if (!json_pads(by, e, base + i * es, out, d + 1)) return false;
        return true;
    }
    if (kind != KILN_TYPE_STRUCT) return false;
    struct M { uint64_t off, size, align; std::string type; bool empty; };
    std::vector<M> ms;
    if (t.contains("inner") && !json_empty(by, t.at("inner").get<std::string>(), d + 1)) {
        const auto& b = by.at(t.at("inner").get<std::string>());
        ms.push_back({0, b.at("size").get<uint64_t>(), b.value("align", 0ull), t.at("inner").get<std::string>(), false});
    }
    if (t.contains("fields"))
        for (const auto& f : t.at("fields")) {
            const std::string ft = f.at("type").get<std::string>();
            if (!by.contains(ft)) return false;
            ms.push_back({f.at("offset").get<uint64_t>(), by.at(ft).at("size").get<uint64_t>(), by.at(ft).value("align", 0ull), ft,
                          json_empty(by, ft, d + 1)});
        }
    if (ms.empty()) return false;
    std::stable_sort(ms.begin(), ms.end(), [](const M& a, const M& b) { return a.off < b.off; });
    uint64_t end = 0;
    for (const M& m : ms) {
        if (!m.size || m.off < end) return false;
        if (m.off > end) {
            if (!m.align || (end + m.align - 1) / m.align * m.align != m.off) return false;
            for (uint64_t b = end; b < m.off; ++b) out.push_back(static_cast<uint32_t>(base + b));
        }
        if (!m.empty && !json_pads(by, m.type, base + m.off, out, d + 1)) return false;
        end = m.off + m.size;
    }
    const uint64_t a = t.value("align", 0ull);
    if (end > size) return false;
    if (end < size) {
        if (!a || (end + a - 1) / a * a != size) return false;
        for (uint64_t b = end; b < size; ++b) out.push_back(static_cast<uint32_t>(base + b));
    }
    return true;
}

int padding(const std::string& sigdb) {
    for (const auto& p : kiln::engine::generated::kProfiles) {
        const std::string path = sigdb + (p.layout.dedicated_server ? "/server/" : "/client/") + p.sha256 + ".json";
        Types types;
        json doc;
        try {
            types.load(path);
            types.link();
            std::ifstream in(path);
            doc = json::parse(in);
        } catch (const std::exception& e) {
            check(false, std::string("read ") + path + ": " + e.what());
            continue;
        }
        g_types = &types;
        const KilnTypesApi api = types_table();
        json by = json::object();
        for (const auto& t : doc.at("types")) by[t.at("qualifiedName").get<std::string>()] = t;
        size_t with = 0, agree = 0;
        for (const auto& name : types.components) {
            const gpt::Padding pad = gpt::padding_of(&api, t_by_name(KilnStr{name.data(), name.size()}));
            std::vector<uint32_t> ref;
            const bool ok = json_pads(by, name, 0, ref, 0);
            std::sort(ref.begin(), ref.end());
            ref.erase(std::unique(ref.begin(), ref.end()), ref.end());
            if (!ok) ref.clear();
            if (pad.bytes == ref) ++agree;
            else std::printf("  disagree on %s\n", name.c_str());
            if (!pad.bytes.empty()) ++with;
        }
        std::printf("  [%s] %zu components, %zu with padding the reflection explains\n", p.id, types.components.size(), with);
        check(types.components.size() > 600 && agree == types.components.size() && with > 100,
              std::string(p.id) + ": the padding rule and an independent walk of sigdb's reflection agree on every component");
        // named cases from the real reflection
        const auto pads = [&](const char* n) { return gpt::padding_of(&api, t_by_name(KilnStr{n, std::strlen(n)})); };
        check(pads("keen::ecs::ClientFishing").bytes == std::vector<uint32_t>{2, 3}, std::string(p.id) + ": ClientFishing pads 2..3");
        check(pads("keen::ecs::CurrentTransform").bytes == std::vector<uint32_t>{52, 53, 54, 55},
              std::string(p.id) + ": CurrentTransform's tail pads 52..55");
        check(pads("keen::ecs::NetworkHealth").why == "no padding", std::string(p.id) + ": NetworkHealth has none");
        check(pads("keen::ecs::Health").why == "no padding",
              std::string(p.id) + ": Health (a base class of 8 bytes, then its array) has none: " + pads("keen::ecs::Health").why);
    }
    // synthetic shapes
    Types t;
    t.add("u8", KILN_TYPE_U8, 1, 1);
    t.add("u16", KILN_TYPE_U16, 2, 2);
    t.add("u32", KILN_TYPE_U32, 4, 4);
    t.add("empty", KILN_TYPE_STRUCT, 1, 1);
    t.add("pair", KILN_TYPE_STRUCT, 8, 4, "", {{"a", "u32", 0}, {"b", "u8", 4}});            // tail 5..7
    t.add("arr", KILN_TYPE_STATIC_ARRAY, 24, 4, "pair");                                      // 5..7, 13..15, 21..23
    t.add("td", KILN_TYPE_TYPEDEF, 8, 4, "pair");
    t.add("derived", KILN_TYPE_STRUCT, 12, 4, "pair", {{"c", "u16", 8}});                    // base 5..7, tail 10..11
    t.add("ebo", KILN_TYPE_STRUCT, 4, 4, "empty", {{"a", "u16", 0}});                         // EBO, tail 2..3
    t.add("union", KILN_TYPE_STRUCT, 4, 4, "", {{"a", "u32", 0}, {"b", "u16", 0}});
    t.add("hole", KILN_TYPE_STRUCT, 8, 4, "", {{"a", "u16", 0}, {"b", "u16", 6}});           // 2..5 not alignment
    t.add("front", KILN_TYPE_STRUCT, 8, 4, "", {{"a", "u32", 4}});
    t.add("noalign", KILN_TYPE_STRUCT, 8, 0, "", {{"a", "u32", 0}});
    t.add("tag", KILN_TYPE_STRUCT, 1, 1, "empty");
    t.add("opaque", KILN_TYPE_STRUCT, 8, 4);                                                   // no member, 8 bytes
    t.add("onopaque", KILN_TYPE_STRUCT, 12, 4, "opaque", {{"a", "u8", 8}});                    // its base is not empty
    t.add("ref", KILN_TYPE_OBJECT_REFERENCE, 8, 8);
    t.add("withref", KILN_TYPE_STRUCT, 16, 8, "", {{"a", "ref", 0}, {"b", "u8", 8}});
    t.link();
    g_types = &t;
    const KilnTypesApi api = types_table();
    const auto pads = [&](const char* n) { return gpt::padding_of(&api, t_by_name(KilnStr{n, std::strlen(n)})); };
    check(pads("pair").bytes == std::vector<uint32_t>{5, 6, 7}, "a struct's tail padding");
    check(pads("arr").bytes == std::vector<uint32_t>{5, 6, 7, 13, 14, 15, 21, 22, 23}, "an array repeats its element's padding");
    check(pads("td").bytes == std::vector<uint32_t>{5, 6, 7}, "a typedef has its target's padding");
    check(pads("derived").bytes == std::vector<uint32_t>{5, 6, 7, 10, 11}, "a base class's padding and the tail");
    check(pads("ebo").bytes == std::vector<uint32_t>{2, 3}, "an empty base takes no room");
    check(pads("union").why == "members overlap (a union?)", "overlapping fields: none");
    check(pads("hole").why == "a gap the alignment does not explain", "a gap alignment does not explain: none");
    check(pads("front").why == "a gap the alignment does not explain", "bytes before the first field: none");
    check(pads("noalign").why == "a tail the alignment does not explain", "no alignment: none");
    check(pads("tag").why == "no reflected member", "an empty tag: none (nothing proves its byte unused)");
    check(pads("onopaque").why == "no reflected member", "a base of 8 bytes without members is not an empty base: none");
    check(pads("withref").why == "a kind that owns memory or points somewhere", "a reference: none");
    // the run file
    const gpt::RunConfig c = gpt::parse_run("\xEF\xBB\xBF# comment\n world = 6B69-6C6E2D74-6573742D776F726C6401 \r\n"
                                            "component=BossDisplay\noffset=10\nrate=7\nminutes=3\nseconds=9\nwarmup=0\n"
                                            "mode=NOOP\nlabel=win 11/raise-off!\n");
    check(c.world == "6b696c6e2d746573742d776f726c6401" && c.component == "BossDisplay" && c.offset == 10 && c.rate == 7 &&
              c.minutes == 3 && c.seconds == 9 && c.warmup_s == 0 && c.noop && c.label == "win11raise-off" && c.warnings.empty(),
          "the run file: every key, a byte-order mark, CRLF, a GUID with dashes, a label cleaned");
    const gpt::RunConfig d = gpt::parse_run("rate=1001\nwarmup=-1\noffset=x\nseconds=0\n");
    check(d.rate == 50 && d.warmup_s == 30 && d.offset == -1 && d.seconds == 0 && d.warnings.size() == 4,
          "the run file: values out of range keep their defaults, each with a warning");
    std::printf("padding: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
}  // namespace

int main(int argc, char** argv) {
    if (argc < 3) {
        std::printf("usage: kit_test <scenario> <sigdb/types dir>\n");
        return 2;
    }
    const std::string scenario = argv[1];
    if (scenario == "padding") return padding(argv[2]);
    gpt::g_priority_hook = [] { return kiln::engine::mem::test_priority; };
    return run_scenario(scenario, argv[2], &kiln_native_init);
}
