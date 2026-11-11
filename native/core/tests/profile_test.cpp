// SPDX-License-Identifier: MIT
// Runs the production profile parser on the host, with only its platform I/O stubbed.
#include <cassert>
#include <cstdarg>
#include <cstdio>
#include <fstream>
#include <stdexcept>
#include <utility>
#include <vector>

#include "../src/profile.cpp"

namespace kiln {
std::filesystem::path g_test_root;
// UTF-8 <-> code points (wchar_t is 32 bits here; Windows' UTF-16 conversion keeps the same characters)
std::string to_utf8(std::wstring_view w) {
    std::string out;
    for (wchar_t wc : w) {
        const auto c = static_cast<uint32_t>(wc);
        if (c < 0x80) out += static_cast<char>(c);
        else if (c < 0x800) out += {static_cast<char>(0xC0 | c >> 6), static_cast<char>(0x80 | (c & 0x3F))};
        else if (c < 0x10000) out += {static_cast<char>(0xE0 | c >> 12), static_cast<char>(0x80 | (c >> 6 & 0x3F)), static_cast<char>(0x80 | (c & 0x3F))};
        else out += {static_cast<char>(0xF0 | c >> 18), static_cast<char>(0x80 | (c >> 12 & 0x3F)), static_cast<char>(0x80 | (c >> 6 & 0x3F)), static_cast<char>(0x80 | (c & 0x3F))};
    }
    return out;
}
std::wstring to_wide(std::string_view s) {
    std::wstring out;
    for (size_t i = 0; i < s.size();) {
        const auto b = static_cast<unsigned char>(s[i]);
        const int n = b < 0x80 ? 1 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4;
        uint32_t c = n == 1 ? b : b & (0x3F >> (n - 1));
        for (int k = 1; k < n && i + k < s.size(); ++k) c = c << 6 | (static_cast<unsigned char>(s[i + k]) & 0x3F);
        out += static_cast<wchar_t>(c);
        i += n;
    }
    return out;
}
const Paths& paths() {
    static Paths p = [] { Paths p; p.kiln = g_test_root; return p; }();
    return p;
}
void set_config_dir(const std::filesystem::path&) {}
bool read_file(const std::filesystem::path& p, std::string* out) {
    std::ifstream in(p); out->assign(std::istreambuf_iterator<char>(in), {}); return !!in;
}
const KilnBootInfo& boot_info() { static KilnBootInfo b{}; return b; }
std::vector<std::pair<int, std::string>> g_logged;
namespace log { void writef(int level, const char*, const char* fmt, ...) {
    char buf[1024]; va_list a; va_start(a, fmt); std::vsnprintf(buf, sizeof buf, fmt, a); va_end(a);
    g_logged.emplace_back(level, buf);
} }
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

static bool warned_about(std::string_view what) {
    for (const auto& [level, text] : kiln::g_logged)
        if (level == KILN_LOG_WARN && text.find(what) != std::string::npos) return true;
    return false;
}

// argv: <scratch game folder> <schema/fixtures/profile/game-folder-names.json> [profile.json written by kiln-manage ...]
int main(int argc, char** argv) try {
    assert(argc >= 3);
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
    // Only an EML fixed game/mods folder may use the launcher's Copy-mode sibling spelling.
    for (const char* path : {"../mods/auto-loot-plus", "..\\mods\\XHL-Auto-Loot"}) {
        p = profile(); p["mods"]["eml"] = {{{"id", "safe"}, {"path", path}}};
        g_state = State{}; parse(p.dump());
    }
    for (const char* path : {"../../mods/safe", "../mods/../outside", "../mods/safe/child", "../mods/.hidden",
                             "../mods/safe.", "../mods/safe ", "../mods/con", "../mods/CON.txt", "../mods/safe:stream"}) {
        p = profile(); p["mods"]["eml"] = {{{"id", "safe"}, {"path", path}}};
        invalid(p, "path");
    }
    p = profile(); p["mods"]["native"] = {{{"id", "safe"}, {"path", "../mods/safe"}}}; invalid(p, "path");
    p = profile(); p["configDir"] = "../mods/safe"; invalid(p, "configDir");
    // XHL companion metadata must never be interpreted as an executable ABI kind, even if it looks like code.
    p = profile(); p["mods"]["xhl"] = {{{"id", "safe"}, {"path", "plain.dll"}}};
    g_state = State{}; parse(p.dump());
    for (const auto& entries : g_state.mods) assert(entries.empty());
    for (const auto& entries : g_state.engine_mods) assert(entries.empty());
    p["dropins"] = {"xhl"}; invalid(p, "unknown kind");
    // The launcher writes mods.xhl by design: no "unknown kind (a newer launcher?)" warning at every start for it.
    kiln::g_logged.clear();
    p = profile(); p["mods"]["xhl"] = {{{"id", "safe"}, {"path", "../mods/XHL-Vein-Mining"}}};
    g_state = State{}; parse(p.dump());
    if (warned_about("mods.xhl")) throw std::runtime_error("mods.xhl is warned about as an unknown kind");
    p = profile(); p["mods"]["future-engine"] = json::array();
    g_state = State{}; parse(p.dump());
    if (!warned_about("mods.future-engine")) throw std::runtime_error("a really unknown kind is no longer warned about");
    // One table of folder names for the three validators of ../mods/<folder> (kiln-manage, this parser, the schema).
    std::ifstream names_file(argv[2]);
    if (!names_file) throw std::runtime_error(std::string("cannot read ") + argv[2]);
    const json names = json::parse(names_file);
    size_t checked = 0;
    for (const auto& [kind, root] : {std::pair{"eml", "mods"}, std::pair{"greeneye", "greeneye"}}) {
        for (const char* verdict : {"accept", "reject"}) {
            for (const auto& n : names.at(verdict)) {
                const std::string path = std::string("../") + root + "/" + n.get<std::string>();
                p = profile(); p["mods"][kind] = {{{"id", "safe"}, {"path", path}}};
                if (std::string_view(verdict) == "accept") {
                    if (!placed_game_path(path, root)) throw std::runtime_error(std::string(kind) + " refused a folder name the launcher writes: " + n.dump());
                    g_state = State{}; parse(p.dump());
                } else {
                    if (placed_game_path(path, root)) throw std::runtime_error(std::string(kind) + " accepted a folder name the launcher refuses: " + n.dump());
                    invalid(p, "path");
                }
                ++checked;
            }
        }
    }
    // each kind has its one folder: no other kind's, and none for the rest
    for (const auto& [kind, path] : {std::pair{"eml", "../greeneye/safe"}, std::pair{"greeneye", "../mods/safe"},
                                     std::pair{"native", "../greeneye/safe"}, std::pair{"shroudforge", "../greeneye/safe"},
                                     std::pair{"greeneye", "../greeneye/../outside"}, std::pair{"greeneye", "../greeneye/a/b"}}) {
        p = profile(); p["mods"][kind] = {{{"id", "safe"}, {"path", path}}};
        invalid(p, "path");
    }
    std::printf("folder name table: %zu names agree\n", checked);
    // The actual profiles kiln-manage renders in Copy mode (crates/kiln-manage/tests/eml_packages.rs passes them), not a
    // hand-retyped approximation.
    if (argc == 3)
        std::fprintf(stderr, "SKIP: no profile written by kiln-manage was given, so the launcher-to-core contract was NOT checked "
                             "(cargo test -p kiln-manage --features full --test eml_packages runs this script with them)\n");
    for (int i = 3; i < argc; ++i) {
        read_profile(argv[i], KILN_PROFILE_ACTIVE);
        if (g_state.mode != KILN_PROFILE_ACTIVE) throw std::runtime_error(std::string(argv[i]) + ": " + g_state.error);
        std::printf("launcher profile ok: %s\n", argv[i]);
    }
    std::puts("profile parser checks passed");
} catch (const Invalid& e) {
    std::fprintf(stderr, "%s\n", e.what.c_str()); return 1;
} catch (const std::exception& e) {
    std::fprintf(stderr, "%s\n", e.what()); return 1;
}
