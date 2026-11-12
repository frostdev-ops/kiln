// SPDX-License-Identifier: MIT
// sigdb::feature_problem on the real resolver (the test PE of sigdisk_test.cpp): which symbol or constant of a feature
// did not resolve, in the feature list's order, with sigdb's own text. Run by test-caps.sh.
#ifndef _WIN32
#define __try try
#define __except(x) catch (...)
#endif
#define main sigdisk_helpers_main
#include "sigdisk_test.cpp"
#undef main
#include "../src/sigdb.cpp"

namespace kiln {
ImageInfo test_image;
const ImageInfo& image() { return test_image; }
double now_ms() { return 0; }
std::string to_utf8(std::wstring_view w) { return std::string(w.begin(), w.end()); }
namespace log {
void writef(int, const char*, const char*, ...) {}
}
}  // namespace kiln

namespace {

// The test PE "loaded" at another base, as sigdb_runtime_test.cpp does it.
struct Loaded {
    TestPe pe;
    std::vector<uint8_t> live;
    Loaded() : live(0x3040, 0) {
        pe::FileText ft;
        std::string err;
        if (!pe::parse_file_text(pe.file.data(), pe.file.size(), ft, err)) std::abort();
        std::vector<uint8_t> text(ft.text_vsize, 0);
        std::memcpy(text.data(), pe.file.data() + ft.text_raw, ft.file_bytes());
        pe::relocate_text(text.data(), text.size(), ft, reinterpret_cast<uintptr_t>(live.data()));
        std::memcpy(live.data() + ft.text_rva, text.data(), text.size());
        auto& img = kiln::test_image;
        img = {};
        img.base = reinterpret_cast<uintptr_t>(live.data());
        img.size = ft.size_of_image;
        img.text = live.data() + ft.text_rva;
        img.text_size = ft.text_vsize;
        img.timestamp = ft.timestamp;
        img.file = pe.file.data();
        img.file_size = pe.file.size();
        sigdb::g_code = {};
    }
};

void write(const std::filesystem::path& file, const char* text) {
    std::ofstream out(file);
    out << text;
}

struct Problem {
    bool found = false;
    std::string name, text;
    bool constant = false;
};

Problem problem(std::string_view feature) {
    Problem p;
    p.constant = true;  // so a call that leaves it alone is seen
    p.found = sigdb::feature_problem(feature, &p.name, &p.text, &p.constant);
    return p;
}

void core_features(const std::filesystem::path& dir) {
    Loaded x;
    const auto a = dir / "problem-a.json", b = dir / "problem-b.json";
    write(a, R"({"format":1,
      "symbols":{
        "root":{"find":[{"pattern":"48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}"}]},
        "tail":{"find":[{"pattern":"8B 81 {field:d32} C3"}]},
        "broken":{"find":[{"pattern":"48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E9"}]},
        "alsobroken":{"find":[{"pattern":"0F 0B 0F 0B"}]}
      },
      "constants":{"field2":{"min_votes":2}},
      "features":{
        "f.ok":{"symbols":["root","tail","helper"],"constants":["field"]},
        "f.broken":{"symbols":["root","broken","alsobroken"]},
        "f.order":{"symbols":["root","alsobroken","broken"]},
        "f.const":{"symbols":["root"],"constants":["field","field2"]},
        "f.symfirst":{"symbols":["broken"],"constants":["field2"]},
        "f.undef":{"symbols":["ghost"]},
        "f.undefconst":{"constants":["nothere"]},
        "f.dup":{"symbols":["root"]}
      }})");
    // A second definitions file (server.d/*.json) that defines f.dup again: resolve() keeps the last definition.
    write(b, R"({"format":1,"symbols":{},"features":{"f.dup":{"symbols":["broken"]}}})");
    sigdb::g_core = sigdb::Resolver();
    CHECK(sigdb::g_core.load(a, "") && sigdb::g_core.load(b, ""), "problem: definitions load");
    sigdb::g_core.resolve();

    CHECK(sigdb::feature("f.ok") && !problem("f.ok").found, "problem: a resolved feature has no problem");
    Problem p = problem("f.broken");
    CHECK(p.found && p.name == "broken" && !p.constant && p.text == "broken.find[0]: 0 matches",
          "problem: the first unresolved symbol, with sigdb's text (%s: %s)", p.name.c_str(), p.text.c_str());
    p = problem("f.order");
    CHECK(p.found && p.name == "alsobroken", "problem: the list's order decides, not the name's (%s)", p.name.c_str());
    p = problem("f.const");
    CHECK(p.found && p.name == "field2" && p.constant && p.text == "0 captures, need 2",
          "problem: no symbol failed, so the first unresolved constant (%s: %s)", p.name.c_str(), p.text.c_str());
    p = problem("f.symfirst");
    CHECK(p.found && p.name == "broken" && !p.constant, "problem: symbols come before constants (%s)", p.name.c_str());
    p = problem("f.undef");
    CHECK(p.found && p.name == "ghost" && p.text == "not defined", "problem: a symbol no definition has (%s)",
          p.text.c_str());
    p = problem("f.undefconst");
    CHECK(p.found && p.name == "nothere" && p.constant && p.text == "not defined",
          "problem: a constant no definition has (%s)", p.text.c_str());
    p = problem("f.dup");
    CHECK(!sigdb::feature("f.dup") && p.found && p.name == "broken",
          "problem: a feature defined twice is judged by its last definition, as feature() is (%s)", p.name.c_str());
    p = problem("no.such.feature");
    CHECK(!p.found && p.constant && p.name.empty(), "problem: an unknown feature is no problem and leaves out alone");
    CHECK(sigdb::feature_problem("f.broken", nullptr, nullptr, nullptr), "problem: every out pointer is optional");
    std::filesystem::remove(a);
    std::filesystem::remove(b);
    sigdb::g_core = sigdb::Resolver();
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    core_features(argv[1]);
    return g_fail ? 1 : 0;
}
