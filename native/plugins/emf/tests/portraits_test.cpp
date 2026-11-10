// SPDX-License-Identifier: MIT
// Production manifest/selection/cache adapter with only the GPL ABI call replaced. Renderer tests live in gpl/.
#include "emf_internal.h"
#include "portrait_bridge.h"
#include "nlohmann/json.hpp"
#include <fstream>
#include <cstdio>
#include <atomic>
#include <chrono>
#include <thread>
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
static std::atomic<int> checks{0}, failures{0}, calls{0}; // the fake renderer runs on the worker thread
static bool fail = false;
static int forced_code = 0;
static std::string forced_message = "fake";
#define CHECK(x)                                                                                                       \
    do {                                                                                                               \
        ++checks;                                                                                                      \
        if (!(x)) {                                                                                                    \
            ++failures;                                                                                                \
            std::printf("FAIL %d: %s\n", __LINE__, #x);                                                                \
        }                                                                                                              \
    } while (0)
static json request;
static std::thread::id draw_thread;
static std::atomic<bool> hold{false}, drawing{false};
static void write(const std::filesystem::path &p, const std::string &text);
static int32_t draw(const uint8_t *in, size_t n, char *out, size_t cap, size_t *written) {
    ++calls;
    draw_thread = std::this_thread::get_id();
    drawing = true;
    while (hold)
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    request = json::parse(std::string_view(reinterpret_cast<const char *>(in), n));
    json items = json::array();
    const bool whole = forced_code == 2 || forced_code == 5 || forced_code == 6 || forced_code == 7; // request-level: no items
    for (const auto &e : request["items"]) {
        if (whole)
            break;
        items.push_back({{"key", e["key"]}, {"status", fail ? "failed" : "drawn"}});
        if (!fail) { // what the real renderer leaves: index.json and the frames
            const std::filesystem::path folder =
                std::filesystem::path(request["cache_dir"].get<std::string>()) / e["key"].get<std::string>();
            write(folder / "index.json", e.dump());
            for (int f = 0; f < e["frames"].get<int>(); ++f) {
                char name[16];
                std::snprintf(name, sizeof name, "%02d.png", f);
                write(folder / name, "png");
            }
        }
    }
    const auto text = json({{"code", forced_code ? forced_code : (fail ? 4 : 0)}, {"message", forced_message}, {"items", items}}).dump();
    CHECK(cap > text.size());
    std::memcpy(out, text.c_str(), text.size() + 1);
    *written = text.size() + 1;
    drawing = false;
    return 0;
}
static void ready() {
    portraits_on_ready();
    portraits_test_join();
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
    ready();
    CHECK(calls == 0);
    g_side = KILN_SIDE_CLIENT;
    ready();
    CHECK(calls == 1);
    CHECK(draw_thread != std::this_thread::get_id()); // drawn off the boot thread
    CHECK(request["items"].size() == 1);
    CHECK(request["items"][0]["key"] == "beastmaster.wolf");
    CHECK(request["items"][0]["frames"] == 4);
    const auto cache = P.emf / "cache/portraits";
    const auto memory = cache / ".kiln-attempts.json";
    CHECK(std::filesystem::is_regular_file(memory));
    ready();
    CHECK(calls == 1); // every picture current and inputs unchanged: the renderer is not called at all
    std::filesystem::remove(cache / "beastmaster.wolf/03.png");
    ready();
    CHECK(calls == 2); // a frame went missing: drawn again
    ready();
    CHECK(calls == 2);
    fail = true;
    write(file, manifest(2).dump());
    ready();
    CHECK(calls == 3);
    ready();
    CHECK(calls == 3); // unchanged failed pictures are not retried every boot
    fail = false;
    write(P.game / "enshrouded.kfc", "changed baseline");
    ready();
    CHECK(calls == 4);
    CHECK(json::parse(read(memory))["failed"].empty());
    const auto original = file.parent_path() / "saved.json";
    std::filesystem::rename(file, original);
    std::filesystem::create_symlink(original, file);
    ready();
    CHECK(calls == 4);
    std::filesystem::remove(file);
    std::filesystem::rename(original, file);
    const auto outside = root / "outside";
    std::filesystem::create_directory(outside);
    std::filesystem::remove_all(cache);
    std::filesystem::create_directory_symlink(outside, cache);
    ready();
    CHECK(calls == 4);
    CHECK(std::filesystem::is_empty(outside));
    std::filesystem::remove(cache);
    std::filesystem::create_directory(cache);
    const auto marker = outside / "marker";
    write(marker, "untouched");
    std::filesystem::create_symlink(marker, memory);
    ready();
    CHECK(calls == 4);
    CHECK(read(marker) == "untouched");
    std::filesystem::remove(memory);
    std::filesystem::create_symlink(marker, cache / ".kiln-attempts.json.tmp");
    ready();
    CHECK(calls == 5);
    CHECK(read(marker) ==
          "untouched"); // attempt writes use exclusive unique staging, never predictable symlink targets
    write(file, std::string((1u << 20) + 1, 'x'));
    ready();
    CHECK(calls == 5);
    write(file, manifest(3).dump());
    forced_code = 5;
    ready();
    CHECK(calls == 6);
    ready();
    CHECK(calls == 7); // transient cache busy is retried
    forced_code = 2;
    forced_message = "another portrait generator owns this cache: from a kiln-eml before dev.21";
    ready();
    CHECK(calls == 8);
    ready();
    CHECK(calls == 9); // code 2 with the old busy message is contention too: retried
    forced_code = 7;
    forced_message = "Permission denied (os error 13)";
    ready();
    CHECK(calls == 10);
    ready();
    CHECK(calls == 11); // an I/O error on the cache is transient: retried, not blocked
    CHECK(!json::parse(read(memory)).contains("blocked"));
    forced_code = 2;
    ready();
    CHECK(calls == 12);
    ready();
    CHECK(calls == 13); // code 2 with an OS error text (a kiln-eml without code 7) is retried too
    CHECK(!json::parse(read(memory)).contains("blocked"));
    forced_message = "portrait cache contains an invalid entry";
    ready();
    CHECK(calls == 14);
    ready();
    CHECK(calls == 14); // a lasting refusal (an invalid cache entry, a link) is remembered until inputs or the cache change
    CHECK(json::parse(read(memory))["failed"].empty() && json::parse(read(memory)).contains("blocked"));
    std::filesystem::create_directory(cache / "unused.folder");
    ready();
    CHECK(calls == 15); // the user changed the cache's folders: asked again
    ready();
    CHECK(calls == 15);
    std::filesystem::remove(cache / "unused.folder");
    forced_code = 6;
    write(file, manifest(5).dump());
    ready();
    CHECK(calls == 16);
    ready();
    CHECK(calls == 16); // a full cache is remembered too
    std::filesystem::create_directory(cache / "extra");
    std::filesystem::remove(cache / "extra");
    ready();
    CHECK(calls == 16); // the same folders again: still remembered
    std::filesystem::create_directory(cache / "other");
    ready();
    CHECK(calls == 17); // delete-and-add changes the folders: asked again (the log advises deleting unused folders)
    std::filesystem::remove(cache / "other");
    forced_message = "fake";
    forced_code = 0;
    write(file, manifest(3).dump());
    ready();
    CHECK(calls == 18);
    write(memory, json({{"stamp", 42}, {"failed", json::array()}}).dump());
    ready();
    CHECK(calls == 19); // malformed attempt memory cannot disable future generation
    ready();
    CHECK(calls == 19);
    {
        auto m = json::parse(read(memory));
        m["current"] = 7;
        write(memory, m.dump());
    }
    ready();
    CHECK(calls == 20); // nor can a malformed current list
    {
        auto m = json::parse(read(memory));
        m["current"] = {{"beastmaster.wolf", nullptr}};
        write(memory, m.dump());
        std::filesystem::remove_all(cache / "beastmaster.wolf");
    }
    ready();
    CHECK(calls == 21); // nor a null fact standing for a picture that is missing
    write(P.state / "shroudforge.json", R"({"mode":"self-hosted"})");
    std::filesystem::remove_all(cache / "beastmaster.wolf");
    ready();
    CHECK(calls == 21); // no archive read racing an external startup writer
    write(P.state / "shroudforge.json", R"({"mode":"hosted"})");
    ready();
    CHECK(calls == 22);
    // image_load of a picture still being drawn: the starting thread waits for it, no other thread does, other keys never
    std::filesystem::remove_all(cache / "beastmaster.wolf");
    hold = true;
    portraits_on_ready();
    for (int i = 0; i < 400 && !drawing; ++i)
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    CHECK(drawing); // portraits_on_ready returned while the picture is still being drawn
    CHECK(portraits_wait("other.key") && drawing);
    bool tick_thread_answer = true;
    std::thread other([&] { tick_thread_answer = portraits_wait("beastmaster.wolf"); });
    other.join();
    CHECK(!tick_thread_answer && drawing); // another thread is told "not yet", at once
    CHECK(portraits_drawing("BeastMaster.Wolf") && !portraits_drawing("other.key")); // never waits
    static std::atomic<int> drawn_calls{0};
    portraits_when_drawn([] { drawn_calls++; });
    std::thread release([] {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        hold = false;
    });
    CHECK(portraits_wait("BeastMaster.Wolf"));
    CHECK(!drawing && std::filesystem::is_regular_file(cache / "beastmaster.wolf/02.png"));
    release.join();
    portraits_test_join();
    CHECK(calls == 23);
    CHECK(portraits_wait("beastmaster.wolf")); // nothing is drawing any more
    CHECK(drawn_calls == 1 && !portraits_drawing("beastmaster.wolf")); // the image handles' loader ran once, after drawing
    portraits_when_drawn(nullptr);
    // a renderer that hangs: the wait is bounded, and later calls do not wait again
    std::filesystem::remove_all(cache / "beastmaster.wolf");
    portraits_test_wait_ms(150);
    hold = true;
    portraits_on_ready();
    for (int i = 0; i < 400 && !drawing; ++i)
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    const auto began = std::chrono::steady_clock::now();
    CHECK(!portraits_wait("beastmaster.wolf"));
    CHECK(std::chrono::steady_clock::now() - began >= std::chrono::milliseconds(100));
    const auto again = std::chrono::steady_clock::now();
    CHECK(!portraits_wait("beastmaster.wolf"));
    CHECK(std::chrono::steady_clock::now() - again < std::chrono::milliseconds(50));
    hold = false;
    portraits_test_join();
    CHECK(portraits_wait("beastmaster.wolf"));
    std::filesystem::remove_all(root);
    std::printf("EMF portrait planner: %d checks, %d failures\n", checks.load(), failures.load());
    return failures ? 1 : 0;
}
