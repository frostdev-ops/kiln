// SPDX-License-Identifier: MIT
// Compile the real manifest reader and ownership decision on the host; discard unused game-memory paths at link time.
#include "../refs.cpp"
#include <cstdarg>
#include <chrono>
#include <stdexcept>

namespace kc {
const KilnApi* g_api = nullptr;
std::string test_mods_dir;
std::vector<std::string> test_warnings;
std::string mods_dir() { return test_mods_dir; }
void logf(int, const char* fmt, ...) {
    char text[2048];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(text, sizeof text, fmt, args);
    va_end(args);
    test_warnings.emplace_back(text);
}
std::string lower(std::string s) {
    for (char& c : s) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    return s;
}
// Host fixture GUID decoder (same Windows byte layout as game.cpp; no game memory involved).
bool guid_parse(const std::string& s, uint8_t out[16]) {
    unsigned a, b, c, d[8];
    if (s.size() != 36 || std::sscanf(s.c_str(), "%8x-%4x-%4x-%2x%2x-%2x%2x%2x%2x%2x%2x", &a, &b, &c,
        &d[0], &d[1], &d[2], &d[3], &d[4], &d[5], &d[6], &d[7]) != 11) return false;
    const uint32_t aa = a;
    const uint16_t bb = b, cc = c;
    std::memcpy(out, &aa, 4);
    std::memcpy(out + 4, &bb, 2);
    std::memcpy(out + 6, &cc, 2);
    for (int i = 0; i < 8; ++i) out[8 + i] = static_cast<uint8_t>(d[i]);
    return true;
}
}

namespace {
int checks = 0;
void check(bool yes, const char* name) {
    if (!yes) throw std::runtime_error(name);
    ++checks;
}
void malformed_entries() {
    using namespace kc;
    for (const char* key : {"kind", "type", "guid"}) {
        for (const json& bad : {json(nullptr), json(42), json(false), json::array(), json::object()}) {
            json entry = {{"name", "bad-model"}, {"kind", "resource"}, {"type", "keen::RenderModel"}, {"guid", "11111111-2222-4333-8444-555555555555"}};
            entry[key] = bad;
            std::unordered_set<G, GH> models;
            const size_t before = test_warnings.size();
            try { declared_models(json{{"entries", json::array({entry})}}, &models); }
            catch (const json::exception& e) { throw std::runtime_error(std::string("malformed ") + key + " threw: " + e.what()); }
            check(models.empty(), "malformed entry must not declare a model");
            check(test_warnings.size() == before + 1, "malformed entry must be logged once");
            check(test_warnings.back().find("bad-model") != std::string::npos, "warning must name the entry");
            check(test_warnings.back().find(key) != std::string::npos, "warning must name the invalid field");
        }
    }
}

void own_mod_models(const std::filesystem::path& root) {
    using namespace kc;
    check(!fs::exists(root), "fixture directory must be new");
    struct Fixture {
        fs::path path;
        ~Fixture() {
            set_kitbash_mod(nullptr);
            g_mod_folders.clear();
            g_decls.clear();
            std::error_code ec;
            fs::remove_all(path, ec);
        }
    } fixture{root};
    test_mods_dir = root.string();
    const std::string own = "11111111-2222-4333-8444-555555555555";
    const std::string other_type = "21111111-2222-4333-8444-555555555555";
    const std::string other_kind = "31111111-2222-4333-8444-555555555555";
    const std::string foreign = "41111111-2222-4333-8444-555555555555";
    const std::string unloaded = "51111111-2222-4333-8444-555555555555";
    const std::string unknown = "61111111-2222-4333-8444-555555555555";
    json manifest = {{"entries", json::array({
        json{{"kind", "resource"}, {"type", "keen::RenderModel"}, {"guid", own}},
        json{{"kind", "resource"}, {"type", "keen::JointAnimation"}, {"guid", other_type}},
        json{{"kind", "texture"}, {"type", "keen::RenderModel"}, {"guid", other_kind}},
        json{{"name", "bad-guid"}, {"kind", "resource"}, {"type", "keen::RenderModel"}, {"guid", nullptr}}
    })}};
    auto write_mod = [&](const std::string& id, const json& assets) {
        const auto folder = root / id;
        fs::create_directories(folder / "assets");
        std::ofstream(folder / "kiln.mod.json") << json{{"id", id}};
        std::ofstream(folder / "assets/assets.json") << assets;
    };
    write_mod("owner", manifest);
    write_mod("other", json{{"entries", json::array({json{{"kind", "resource"}, {"type", "keen::RenderModel"}, {"guid", foreign}}})}});
    write_mod("unloaded", json{{"entries", json::array({json{{"kind", "resource"}, {"type", "keen::RenderModel"}, {"guid", unloaded}}})}});
    std::ofstream(root / "unloaded/.disabled");
    KilnApi api{};
    api.service = [](KilnStr, uint32_t) -> const void* { return nullptr; };
    g_api = &api;

    std::unordered_set<G, GH> models;
    const size_t before = test_warnings.size();
    declared_models(read_jsonc(root / "owner/assets/assets.json"), &models);
    check(models.size() == 1, "only resource RenderModel entries declare models");
    check(test_warnings.size() == before + 1, "non-string GUID is skipped and logged once");
    auto accepted = [&](const std::string& text) {
        uint8_t g[16];
        check(guid_parse(text, g), "fixture GUID must parse");
        return declared_by_own_mod(g);
    };
    check(!accepted(own), "pack rules without a mod cannot use declared models");
    const std::string owner = "owner";
    set_kitbash_mod(&owner);
    check(accepted(own), "the item's own mod's declared model is accepted");
    check(!accepted(other_type), "another resource type is refused");
    check(!accepted(other_kind), "another entry kind is refused");
    check(!accepted(foreign), "a model only another loaded mod declares is refused");
    check(!accepted(unloaded), "a model only a disabled mod declares is refused");
    check(!accepted(unknown), "an unknown GUID is refused");
    const size_t cached_warnings = test_warnings.size();
    check(accepted(own), "the cached manifest still accepts its model");
    check(test_warnings.size() == cached_warnings, "cached malformed entry is not logged again");
    const std::string missing_mod = "unloaded";
    set_kitbash_mod(&missing_mod);
    check(!accepted(unloaded), "a disabled mod's folder cannot authorize its model");
    set_kitbash_mod(&owner);
    const auto mf = root / "owner/assets/assets.json";
    const auto stamp = fs::last_write_time(mf);
    std::ofstream(mf) << json{{"entries", json::array()}};
    fs::last_write_time(mf, stamp + std::chrono::seconds(1));
    check(!accepted(own), "hot reload removes a model declaration from the cache");
    g_api = nullptr;
}
}

int main(int argc, char** argv) {
    try {
        malformed_entries();
        if (argc != 2) throw std::runtime_error("provide a new fixture directory");
        own_mod_models(argv[1]);
        std::printf("refs-test: %d checks passed, 0 failed\n", checks);
        return 0;
    } catch (const std::exception& e) {
        std::fprintf(stderr, "refs-test: FAIL: %s\n", e.what());
        return 1;
    }
}
