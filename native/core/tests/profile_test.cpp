// SPDX-License-Identifier: MIT
// Runs the production profile parser on the host, with only its platform I/O stubbed.
#include <cassert>
#include <cstdarg>
#include <cstdio>
#include <fstream>
#include <stdexcept>

#include "../src/profile.cpp"

namespace kiln {
std::filesystem::path g_test_root;
std::string to_utf8(std::wstring_view w) { return std::string(w.begin(), w.end()); }
std::wstring to_wide(std::string_view s) { return std::wstring(s.begin(), s.end()); }
const Paths& paths() {
    static Paths p = [] { Paths p; p.kiln = g_test_root; return p; }();
    return p;
}
void set_config_dir(const std::filesystem::path&) {}
bool read_file(const std::filesystem::path& p, std::string* out) {
    std::ifstream in(p); out->assign(std::istreambuf_iterator<char>(in), {}); return !!in;
}
const KilnBootInfo& boot_info() { static KilnBootInfo b{}; return b; }
namespace log { void writef(int, const char*, const char*, ...) {} }
}

using nlohmann::json;
using namespace kiln::profile;
static json profile() { return {{"format", 1}, {"id", "test"}, {"generation", 1}, {"mods", json::object()}}; }
static void invalid(const json& doc, std::string_view expected) {
    g_state = State{};
    try { parse(doc.dump()); }
    catch (const Invalid& e) {
        if (e.what.find(expected) != std::string::npos) return;
        throw std::runtime_error("wrong error: " + e.what);
    }
    throw std::runtime_error("accepted invalid profile: " + doc.dump().substr(0, 256));
}

int main(int argc, char** argv) try {
    assert(argc == 2);
    kiln::g_test_root = argv[1];
    std::filesystem::create_directories(kiln::g_test_root);
    for (const char* kind : kEngineKinds) {
        auto p = profile();
        p["mods"][kind] = {{{"id", "safe"}, {"path", "mods/safe"}, {"version", "../escape"}}};
        invalid(p, "version");
        p["mods"][kind][0]["version"] = "1.0.0";
        p["mods"][kind][0]["hash"] = "bad";
        invalid(p, "hash");
        p["mods"][kind][0].erase("hash");
        p["mods"][kind][0]["path"] = std::string("mods/safe\0/hidden", 17);
        invalid(p, "path");
        p["mods"][kind][0]["path"] = std::string(32768, 'a');
        invalid(p, "32767");
        p["mods"][kind] = json::array();
        for (size_t i = 0; i < 4097; ++i) p["mods"][kind].push_back({{"id", "safe"}, {"path", "mods/safe"}});
        invalid(p, "4096");
        p["mods"][kind].erase(p["mods"][kind].end() - 1);
        g_state = State{}; parse(p.dump()); // the 4096 boundary is accepted
        p["mods"][kind] = {{{"id", "safe"}, {"path", "plain.dll"}, {"version", "1.0.0"},
                              {"hash", "kdh1:" + std::string(64, 'a')}}};
        std::ofstream(kiln::g_test_root / "plain.dll") << "test";
        g_state = State{}; parse(p.dump());
        const size_t index = static_cast<size_t>(std::find(kEngineKinds, kEngineKinds + kEngineKindCount, kind) - kEngineKinds);
        if (std::string_view(kind) == "shroudtopia") assert(g_state.engine_mods[index].size() == 1);
        else assert(g_state.engine_mods[index].empty() && g_state.missing.size() == 1);
        p["mods"][kind][0]["version"] = "V1.2.3"; // SemVersion accepts both prefix cases; existing profiles stay valid
        g_state = State{}; parse(p.dump());
    }
    auto p = profile();
    p["mods"]["eml"] = {{{"id", "safe"}, {"path", "mods/safe"}, {"enabled", 1}}};
    invalid(p, "enabled");
    p["mods"]["eml"] = json::object();
    invalid(p, "array");
    p = profile(); p["mods"]["future-engine"] = json::object();
    g_state = State{}; parse(p.dump()); // an older core ignores newer kinds
    assert(g_state.id == "test");
    // Deep unknown data must be refused before JSON parsing can exhaust the game's stack.
    const std::string deep = "{\"format\":1,\"id\":\"test\",\"generation\":1,\"mods\":{},\"extra\":" +
                             std::string(512, '[') + "0" + std::string(512, ']') + "}";
    try { parse(deep); throw std::runtime_error("accepted excessive nesting"); }
    catch (const Invalid& e) { assert(e.what.find("nesting") != std::string::npos); }
    const auto file = kiln::g_test_root / "profile.json";
    std::ofstream(file) << profile().dump();
    std::filesystem::resize_file(file, 16 * 1024 * 1024 + 1);
    g_state = State{}; read_profile(file, KILN_PROFILE_ACTIVE);
    assert(g_state.mode == KILN_PROFILE_INVALID && g_state.error.find("16 MiB") != std::string::npos);
    std::ofstream(file) << p.dump(); // unknown newer kind stays compatible on the file-reading path too
    g_state = State{}; read_profile(file, KILN_PROFILE_ACTIVE);
    assert(g_state.mode == KILN_PROFILE_ACTIVE); // unknown newer kind stays compatible
    p["mods"]["eml"] = {{{"id", "safe"}, {"path", "mods/safe"}, {"hash", "bad"}}};
    std::ofstream(file) << p.dump();
    read_profile(file, KILN_PROFILE_ACTIVE);
    assert(g_state.mode == KILN_PROFILE_INVALID && g_state.error.find("hash") != std::string::npos);
    assert(g_state.dropins == 0 && g_state.mods[0].empty() && g_state.engine_mods[0].empty());
    std::puts("profile parser checks passed");
} catch (const std::exception& e) {
    std::fprintf(stderr, "%s\n", e.what()); return 1;
}
