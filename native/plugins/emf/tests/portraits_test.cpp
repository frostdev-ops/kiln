// SPDX-License-Identifier: MIT
// Production manifest/selection/cache adapter with only the GPL ABI call replaced. Renderer tests live in gpl/.
#include "emf_internal.h"
#include "portrait_bridge.h"
#include "nlohmann/json.hpp"
#include <fstream>
#include <cstdio>
namespace kemf {
uint32_t g_side = KILN_SIDE_CLIENT;
Paths P;
std::vector<std::unique_ptr<Mod>> g_mods;
void logf(int, const char *, ...) {}
uint64_t now_ms() { return 1000; }
std::wstring to_wide(std::string_view s) { return {s.begin(), s.end()}; }
std::string path_utf8(const std::filesystem::path &p) { return p.string(); }
bool seh_call(ThunkFn f, void *p, unsigned long *, uintptr_t *) {
    f(p);
    return true;
}
std::string lower(std::string_view v) {
    std::string s(v);
    for (char &c : s)
        if (c >= 'A' && c <= 'Z')
            c += 32;
    return s;
}
} // namespace kemf
using namespace kemf;
using json = nlohmann::json;
static int checks = 0, failures = 0, calls = 0;
static bool fail = false;
static int forced_code = 0;
#define CHECK(x)                                                                                                       \
    do {                                                                                                               \
        ++checks;                                                                                                      \
        if (!(x)) {                                                                                                    \
            ++failures;                                                                                                \
            std::printf("FAIL %d: %s\n", __LINE__, #x);                                                                \
        }                                                                                                              \
    } while (0)
static json request;
static int32_t draw(const uint8_t *in, size_t n, char *out, size_t cap, size_t *written) {
    ++calls;
    request = json::parse(std::string_view(reinterpret_cast<const char *>(in), n));
    json items = json::array();
    for (const auto &e : request["items"])
        items.push_back({{"key", e["key"]}, {"status", fail ? "failed" : "drawn"}});
    const auto text = json({{"code", forced_code ? forced_code : (fail ? 4 : 0)}, {"items", items}}).dump();
    CHECK(cap > text.size());
    std::memcpy(out, text.c_str(), text.size() + 1);
    *written = text.size() + 1;
    return 0;
}
static void write(const std::filesystem::path &p, const std::string &text) {
    std::filesystem::create_directories(p.parent_path());
    std::ofstream(p) << text;
}
static std::string read(const std::filesystem::path &p) {
    std::ifstream f(p);
    return {std::istreambuf_iterator<char>(f), {}};
}
static json manifest(int frames = 4) {
    return {{"id", "selected"},
            {"portraits",
             json::array({{{"key", "beastmaster.wolf"},
                           {"template", "478381c5-585f-4264-b373-dbb1ea596eca"},
                           {"frames", frames},
                           {"size", 128}},
                          {{"key", "../escape"}, {"model", "91e2d4cc-01c9-4203-a92e-b08769364522"}},
                          {{"key", "bad.huge"},
                           {"model", "91e2d4cc-01c9-4203-a92e-b08769364522"},
                           {"frames", uint64_t(0x100000018)}},
                          {{"key", "bad.float"}, {"model", "91e2d4cc-01c9-4203-a92e-b08769364522"}, {"size", 128.5}},
                          {{"key", "bad.both"},
                           {"template", "478381c5-585f-4264-b373-dbb1ea596eca"},
                           {"model", "91e2d4cc-01c9-4203-a92e-b08769364522"}}})}};
}
int main(int argc, char **argv) {
    if (argc != 2)
        return 2;
    const auto root = std::filesystem::path(argv[1]) / "portrait-plan-fixture";
    std::filesystem::remove_all(root);
    std::filesystem::create_directories(root);
    P.game = root / "game";
    P.kiln = P.game / "kiln";
    P.emf = P.game / "emf";
    P.state = P.kiln / "state";
    auto m = std::make_unique<Mod>();
    m->id = "selected";
    m->folder = root / "library/selected";
    m->enabled = true;
    m->status = EMF_MOD_SCRIPT;
    const auto file = m->folder / "mod.json";
    write(file, manifest().dump());
    g_mods.push_back(std::move(m));
    auto off = std::make_unique<Mod>();
    off->id = "off";
    off->folder = root / "library/off";
    off->status = EMF_MOD_DISABLED;
    write(off->folder / "mod.json", manifest().dump());
    g_mods.push_back(std::move(off));
    // A drop-in not selected by the active profile is never visited: g_mods is the authoritative discovery result.
    write(P.emf / "mods/not-selected/mod.json", manifest().dump());
    write(P.game / "enshrouded.kfc", "baseline");
    portraits_test_export(draw);
    g_side = KILN_SIDE_SERVER;
    portraits_on_ready();
    CHECK(calls == 0);
    g_side = KILN_SIDE_CLIENT;
    portraits_on_ready();
    CHECK(calls == 1);
    CHECK(request["items"].size() == 1);
    CHECK(request["items"][0]["key"] == "beastmaster.wolf");
    CHECK(request["items"][0]["frames"] == 4);
    const auto cache = P.emf / "cache/portraits";
    const auto memory = cache / ".kiln-attempts.json";
    CHECK(std::filesystem::is_regular_file(memory));
    fail = true;
    write(file, manifest(2).dump());
    portraits_on_ready();
    CHECK(calls == 2);
    portraits_on_ready();
    CHECK(calls == 2); // unchanged failed pictures are not retried every boot
    fail = false;
    write(P.game / "enshrouded.kfc", "changed baseline");
    portraits_on_ready();
    CHECK(calls == 3);
    CHECK(json::parse(read(memory))["failed"].empty());
    const auto original = file.parent_path() / "saved.json";
    std::filesystem::rename(file, original);
    std::filesystem::create_symlink(original, file);
    portraits_on_ready();
    CHECK(calls == 3);
    std::filesystem::remove(file);
    std::filesystem::rename(original, file);
    const auto outside = root / "outside";
    std::filesystem::create_directory(outside);
    std::filesystem::remove_all(cache);
    std::filesystem::create_directory_symlink(outside, cache);
    portraits_on_ready();
    CHECK(calls == 3);
    CHECK(std::filesystem::is_empty(outside));
    std::filesystem::remove(cache);
    std::filesystem::create_directory(cache);
    const auto marker = outside / "marker";
    write(marker, "untouched");
    std::filesystem::create_symlink(marker, memory);
    portraits_on_ready();
    CHECK(calls == 3);
    CHECK(read(marker) == "untouched");
    std::filesystem::remove(memory);
    std::filesystem::create_symlink(marker, cache / ".kiln-attempts.json.tmp");
    portraits_on_ready();
    CHECK(calls == 4);
    CHECK(read(marker) ==
          "untouched"); // attempt writes use exclusive unique staging, never predictable symlink targets
    write(file, std::string((1u << 20) + 1, 'x'));
    portraits_on_ready();
    CHECK(calls == 4);
    write(file, manifest(3).dump());
    forced_code = 2;
    portraits_on_ready();
    CHECK(calls == 5);
    portraits_on_ready();
    CHECK(calls == 6); // transient cache busy is retried
    forced_code = 0;
    portraits_on_ready();
    CHECK(calls == 7);
    write(memory, json({{"stamp", 42}, {"failed", json::array()}}).dump());
    portraits_on_ready();
    CHECK(calls == 8); // malformed attempt memory cannot disable future generation
    write(P.state / "shroudforge.json", R"({"mode":"self-hosted"})");
    portraits_on_ready();
    CHECK(calls == 8); // no archive read racing an external startup writer
    write(P.state / "shroudforge.json", R"({"mode":"hosted"})");
    portraits_on_ready();
    CHECK(calls == 9);
    std::filesystem::remove_all(root);
    std::printf("EMF portrait planner: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
