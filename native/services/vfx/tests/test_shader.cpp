// SPDX-License-Identifier: MIT
// Cook policy regression tests for mods' particle hooks, including the switches boot.cpp reads. No GPU or game.
//   tests/run_shader.sh
// Hostile modules (buffers, barriers, loops, malformed SPIR-V, fuzzing) are in tools/vfxcook/tests/test_shader_safety.py.
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <string>

#include "../cook/vfxcook.h"

static void require(bool ok, const char* what) {
    if (!ok) throw std::runtime_error(std::string("shader safety regression: ") + what);
}

static kvb::Bytes slurp(const std::filesystem::path& p) {
    std::ifstream f(p, std::ios::binary);
    return kvb::Bytes(std::istreambuf_iterator<char>(f), {});
}

static void spit(const std::filesystem::path& p, const kvb::Bytes& b) {
    std::ofstream f(p, std::ios::binary);
    f.write(reinterpret_cast<const char*>(b.data()), static_cast<std::streamsize>(b.size()));
}

int main(int argc, char** argv) {
    using namespace kvb;
    require(argc == 3, "usage: test_shader OUTDIR REPO");
    const std::filesystem::path out = argv[1], repo = argv[2];
    const Bytes swirl = slurp(repo / "mods/samples/ShaderSample/shaders/build/swirl.spv");
    require(!swirl.empty(), "the sample's hook module is there");

    // A hook module links; the linked module is Kiln's sprite plus the hooks, passes Kiln's own check, and is what gets served.
    HookLinkInfo info;
    Bytes linked;
    const std::string why = link_hook_module(swirl.data(), swirl.size(), &linked, &info);
    if (!why.empty()) std::fprintf(stderr, "%s\n", why.c_str());
    require(why.empty(), "the sample links");
    require(check_module(linked.data(), linked.size()).empty(), "the linked module has the game's shape");
    require(info.hooks.size() == 2 && info.hooks[0] == "update" && info.hooks[1] == "draw", "both hooks linked");
    require(info.cost > 0 && info.cost <= kHookBudget, "cost within the budget");
    require(linked.size() > linkable_sprite().size, "the linked module holds Kiln's sprite and more");
    Bytes again;
    require(link_hook_module(swirl.data(), swirl.size(), &again, nullptr).empty() && again == linked, "linking is deterministic");

    // Boot validates replaces against original KFC keys, not resource_put's deferred success or another mod's synthetic resource.
    {
        Guid game;
        require(parse_guid("39745562-5d58-4749-bc64-582e5630324a", &game), "game GUID parses");
        ModInput first, second;
        first.id = "first";
        first.side = second.side = "client";
        first.clients = second.clients = "mod";
        second.id = "second";
        first.documents.emplace_back("test", R"({"format":1,"materials":{"own":{"tint":"#ff0000"}}})");
        const Guid custom = block_id("first", "material", "own");
        second.documents.emplace_back("test", "{\"format\":1,\"materials\":{\"game\":{\"replaces\":\"" + guid_text(game) +
            "\"},\"unknown\":{\"replaces\":\"00000000-0000-0000-0000-000000000001\"},\"custom\":{\"replaces\":\"" + guid_text(custom) + "\"}}}");
        CookOptions policy;
        policy.game_has_material = [game](const Guid& g) { return g == game; };
        for (const auto& mods : {std::vector<ModInput>{first, second}, std::vector<ModInput>{second, first}}) {
            const CookResult r = cook_mods(mods, policy);
            require(r.errors.size() == 2 && r.materials.size() == 2, "unknown and mod-defined replacement targets refused in either load order");
            require(r.errors[0].find(".replaces:") != std::string::npos, "replacement refusal names the field");
            for (const ServedResource& s : r.resources)
                require(!s.override || s.guid == game, "only a known original material is overridden");
        }
        policy.game_has_material = [](const Guid&) { return false; };
        require(cook_mods({second}, policy).materials.empty(), "unreadable original keys fail closed for reskins");
    }

    // Stamping is idempotent and the stamp alone authorises nothing: Kiln's whole sprite with a hook stamp is refused.
    Bytes restamped = swirl;
    std::string sw;
    require(stamp_module(&restamped, &sw) && restamped == swirl, "restamping changes nothing");
    const ModuleInfo& sprite = starter_module(Module::Sprite);
    Bytes whole(sprite.data, sprite.data + sprite.size);
    require(stamp_module(&whole, &sw), "a whole module can be stamped");
    require(!link_hook_module(whole.data(), whole.size(), nullptr, nullptr).empty(), "a stamped whole particle module is refused");

    // The cook: gates and kill switches, and what is served.
    std::filesystem::create_directories(out / "shaders");
    spit(out / "shaders/hooks.spv", swirl);
    ModInput mod;
    mod.id = "shader-safety";
    mod.dir = out.string();
    mod.advanced = true;
    mod.documents.emplace_back("test", R"({"format":1,"vfx":{"test":{"particles":[{"burst":1,"shader":"shaders/hooks.spv"}]}}})");
    CookOptions opt;
    auto check = [&](uint32_t modules, const char* warning) {
        CookResult r = cook_mods({mod}, opt);
        require(r.errors.empty() && r.blocks.size() == 1 && r.mod_modules == modules, "cook result");
        if (warning) require(r.warnings.size() == 1 && r.warnings[0].find(warning) != std::string::npos, warning);
        else require(r.warnings.empty(), "no warning");
        if (modules) {
            const ContentHash want = content_hash(linked.data(), linked.size());
            bool served = false, raw = false;
            for (const ServedBlob& b : r.blobs) {
                served = served || b.hash == want;
                raw = raw || b.data == swirl;
            }
            require(served && !raw, "the linked module is served, never the mod's bytes");
        }
    };
    check(1, nullptr);
    opt.skip_shaders = true;
    check(0, "\"shaders\": false");
    opt.skip_shaders = false;
    opt.skip_shader_mods.insert(mod.id);
    check(0, "skipShaderMods");
    opt.skip_shader_mods.clear();
    mod.advanced = false;
    check(0, "\"advanced\": true");
    mod.advanced = true;
    spit(out / "shaders/hooks.spv", whole);
    check(0, "Kiln's own sprite draws this particle instead");  // the hook stamp authorises nothing
    Bytes cut(swirl.begin(), swirl.begin() + static_cast<std::ptrdiff_t>(swirl.size() / 2 / 4 * 4));
    spit(out / "shaders/hooks.spv", cut);
    {
        CookResult r = cook_mods({mod}, opt);
        require(r.mod_modules == 0 && r.warnings.size() == 1, "a truncated module falls back");
    }
    std::puts("shader safety: link, determinism, stamps, advanced gate, kill switches and fallback passed");
}
