// SPDX-License-Identifier: MIT
// Windows integration regressions; no game. Includes the implementation to exercise the actual sync transaction.
#include "../src/plugin.cpp"
#include <cstdlib>
using namespace kenshwick;
static int checks;
static void check(bool ok) { ++checks; if (!ok) { std::printf("contract check %d failed\n", checks); std::exit(1); } }
static std::filesystem::path fixture;
static KilnResult KILN_CALL test_path(int32_t kind, char* out, size_t cap, size_t* len) {
    const auto text = kcompat::path_utf8(kind == KILN_PATH_GAME ? fixture : fixture / L"kiln");
    *len = text.size();
    if (cap < text.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out, text.data(), text.size());
    return KILN_OK;
}
static uint32_t KILN_CALL test_side() { return KILN_SIDE_SERVER; }
static void KILN_CALL test_log(KilnOwner, int32_t, KilnStr) {}
static const void* KILN_CALL test_service(KilnStr, uint32_t) { return nullptr; }
static void __stdcall fault_start(uint32_t, uint32_t, uint32_t(__stdcall*)()) { RaiseException(0xE0000042, 0, 0, nullptr); }
int main() {
    KilnApi api{}; api.size = sizeof api; api.abi_version = KILN_ABI_VERSION;
    api.path = test_path;
    api.side = test_side; api.log = test_log; api.service = test_service;
    for (auto entry : {&kiln_plugin_main, &kiln_plugin_early}) {
        check(entry(&api, 1, nullptr) == KILN_ERR_UNAVAILABLE);
        for (uint32_t n = 0; n < sizeof(KilnPluginInfo); ++n) {
            struct { KilnPluginInfo info; uint64_t guard; } storage{};
            storage.info.size = n; storage.guard = 0x1122334455667788;
            const auto before = storage;
            check(entry(&api, 1, &storage.info) == KILN_ERR_UNAVAILABLE);
            check(std::memcmp(&storage, &before, sizeof storage) == 0);
        }
    }
    KilnPluginInfo early{}; early.size = sizeof early;
    check(kiln_plugin_early(&api, 1, &early) == KILN_OK && early.on_ready == &on_early_ready);
    auto later_api = api;
    KilnPluginInfo later{}; later.size = sizeof later;
    check(kiln_plugin_main(&later_api, 2, &later) == KILN_OK && later.on_ready == &on_ready);
    check(H.api == &api && H.owner == 1); // early worker's host must not race later entry-point writes
    check(call_start(&fault_start) == 0xE0000042);
    fixture = fs::temp_directory_path() / (L"kiln-enshwick-contract-" + std::to_wstring(GetCurrentProcessId()));
    fs::create_directories(fixture / L"source");
    fs::create_directories(fixture / L"mods");
    fs::create_directories(fixture / L"kiln/data");
    H.api = &api;
    kcompat::write_file_atomic(fixture / L"marker.bin", "mods\\enshwick\\enshwick.dll");
    check(!has_stub_marker(fixture / L"marker.bin"));
    kcompat::write_file_atomic(fixture / L"marker.bin", "enshwick-loader-stub mods\\enshwick\\enshwick.dll");
    check(has_stub_marker(fixture / L"marker.bin"));
    const auto mods = fixture / L"mods";
    const auto source = fixture / L"source";
    kcompat::write_file_atomic(source / L"pack.toml", "id='you.a'\n");
    std::string why;
    Action copy{Act::Copy, "you.a", kcompat::path_utf8(source), "stamp", ""};
    check(replace_copy(mods, copy, &why));
    check(owned_copy(mods / L"you.a", "you.a"));
    kcompat::EnginePlan plan;
    kcompat::ModRef unavailable;
    unavailable.id = "you.a"; unavailable.path = fixture / L"unavailable"; unavailable.enabled = true;
    plan.listed.push_back(unavailable);
    sync_profile(plan, mods);
    check(owned_copy(mods / L"you.a", "you.a")); // an enabled but unavailable source is not a removal request
    // Marker spoofing in a hand-installed folder, even when it copies the entire genuine marker.
    fs::copy(mods / L"you.a", mods / L"player.own", fs::copy_options::recursive);
    check(!owned_copy(mods / L"player.own", "player.own"));
    // A replacement with the same name and marker is not the directory Kiln created.
    fs::rename(mods / L"you.a", mods / L"old");
    fs::copy(mods / L"old", mods / L"you.a", fs::copy_options::recursive);
    check(!owned_copy(mods / L"you.a", "you.a"));
    fs::remove_all(mods / L"you.a"); fs::rename(mods / L"old", mods / L"you.a");
    check(owned_copy(mods / L"you.a", "you.a"));
    // Player additions revoke deletion permission.
    kcompat::write_file_atomic(mods / L"you.a/player.txt", "preserve me");
    Action remove{Act::Remove, "you.a", "", "", ""};
    check(!replace_copy(mods, remove, &why));
    check(fs::exists(mods / L"you.a/player.txt"));
    fs::remove(mods / L"you.a/player.txt");
    check(owned_copy(mods / L"you.a", "you.a"));
    // Failed publication (a missing source) leaves the old mod intact.
    Action refresh{Act::Refresh, "you.a", kcompat::path_utf8(fixture / L"missing"), "changed", ""};
    check(!replace_copy(mods, refresh, &why));
    check(owned_copy(mods / L"you.a", "you.a"));
    // Successful refresh and removal use receipts, not just marker parsing.
    kcompat::write_file_atomic(source / L"pack.toml", "id='you.changed'\n");
    refresh.source = kcompat::path_utf8(source);
    check(replace_copy(mods, refresh, &why));
    check(owned_copy(mods / L"you.a", "you.a"));
    check(replace_copy(mods, remove, &why));
    check(!fs::exists(mods / L"you.a"));
    check(fs::exists(mods / L"player.own/pack.toml"));
    check(!fs::exists(mods / L".kiln-enshwick-staging")); // staging/quarantine is outside the loader's scan
    run_flow("ready"); // no loader available: do not retain the stub's claim
    check(g_status == "no-loader" && g_claim == nullptr);
    fs::remove_all(mods / L"player.own");
    fs::create_directories(mods / L"other-engine");
    kcompat::write_file_atomic(mods / L"other-engine/readme.txt", "not an Enshwick mod");
    g_flow_done.store(false); g_mods.clear();
    kcompat::write_file_atomic(fixture / L"kiln/kiln-enshwick.json", "{\"start\":\"ready\"}");
    on_early_ready(nullptr);
    check(!g_flow_done.load());
    kcompat::write_file_atomic(fixture / L"kiln/kiln-enshwick.json", "{\"start\":\"early\"}");
    on_early_ready(nullptr);
    check(g_flow_done.load() && g_phase == "early" && g_status == "nothing" && g_claim == nullptr);
    fs::remove_all(fixture);
    std::printf("Enshwick contract: %d checks passed\n", checks);
}
