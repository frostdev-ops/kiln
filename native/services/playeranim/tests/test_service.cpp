// SPDX-License-Identifier: MIT
// Execute the actual service lifecycle on the host, with fake assets/types/core tables, never a game process.
#include "../playeranim.cpp"
#include <iostream>

using namespace kpa;
namespace {
std::string root;
int callbacks = 0;
KilnAssetsApi assets{};
KilnApi api{};
KilnResult path(int32_t kind, char* out, size_t cap, size_t* n) {
    const std::string s = root + (kind == KILN_PATH_MODS ? "/mods" : "/state");
    *n = s.size();
    if (cap <= s.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out, s.c_str(), s.size() + 1);
    return KILN_OK;
}
const void* service(KilnStr name, uint32_t) {
    return std::string_view(name.ptr, name.len) == "kiln.assets" ? &assets : nullptr;
}
KilnResult ready(void(KILN_CALL* fn)(void*), void* user) {
    ++callbacks;
    fn(user);  // kiln.assets calls synchronously when already up; the callback must be able to take g_mu.
    return KILN_OK;
}
void log(KilnOwner, int32_t, KilnStr s) { std::cout << std::string_view(s.ptr, s.len) << std::endl; }
KilnResult tick(KilnOwner, int32_t, KilnTickFn, void*, KilnHandle*) { return KILN_OK; }
uint32_t side() { return KILN_SIDE_SERVER; }
}
int main(int argc, char** argv) {
    if (argc != 2) return 2;
    root = argv[1];
    fs::remove_all(root);
    fs::create_directories(root + "/mods/a/assets");
    std::ofstream(root + "/mods/a/kiln.mod.json") << R"({"id":"a","side":"both","clients":"mod"})";
    std::ofstream(root + "/mods/a/assets/player-states.json") << R"({"format":1,"states":{"slash":{"clip":"slash","like":"Parry_FullBody"}}})";
    assets.header.size = sizeof assets;
    assets.when_resources_ready = ready;
    api.path = path;
    api.service = service;
    api.log = log;
    api.subscribe_tick = tick;
    api.side = side;
    g_api = &api;
    on_ready(nullptr);
    const bool ok = callbacks == 1 && g_started && g_state == KILN_PA_OFF && g_off_reason.find("kiln.types") != std::string::npos;
    std::cout << (ok ? "PASS" : "FAIL") << " synchronous assets-ready callback completes and publishes status" << std::endl;
    // A malformed definitions file must retain its named states as Refused in the public lookup.
    g_mods.clear(); g_states.clear(); g_kept.clear(); g_errors.clear(); g_folders.clear(); g_dir_of.clear();
    g_started = false; callbacks = 0;
    std::ofstream(root + "/mods/a/assets/player-states.json") << R"({"format":1,"states":{"slash":{"clip":"slash","like":"Parry_FullBody"},"guard": )";
    on_ready(nullptr);
    KilnPaState found{}; found.size = sizeof found;
    const bool malformed = t_mod_state(KilnStr{"a", 1}, KilnStr{"slash", 5}, &found) == KILN_OK &&
        found.status == KILN_PA_REFUSED && std::string_view(found.reason.ptr, found.reason.len).find("player-states.json") != std::string::npos &&
        g_row_text.find("no state in") == std::string::npos;
    std::cout << (malformed ? "PASS" : "FAIL") << " malformed definitions remain Refused through PlayerAnimStates lookup" << std::endl;
    const auto unreadable = refused_file("not json", "a", "player-states.json: invalid JSON");
    const bool sentinel = unreadable.states.size() == 1 && unreadable.states[0].name == "<file>" && unreadable.states[0].status == "refused";
    std::cout << (sentinel ? "PASS" : "FAIL") << " wholly unreadable file has a visible Refused file entry" << std::endl;
    g_mods.clear(); g_states.clear(); g_kept.clear(); g_errors.clear(); g_folders.clear(); g_dir_of.clear(); g_started = false;
    std::ofstream(root + "/mods/a/kiln.mod.json") << R"({"side":"both","clients":"mod"})";
    on_ready(nullptr);
    found.size = sizeof found;
    const bool manifest = t_mod_state(KilnStr{"a", 1}, KilnStr{"slash", 5}, &found) == KILN_OK && found.status == KILN_PA_REFUSED &&
        std::string_view(found.reason.ptr, found.reason.len).find("no \"id\"") != std::string::npos;
    std::cout << (manifest ? "PASS" : "FAIL") << " unreadable manifest uses folder identity and preserves the refusal" << std::endl;
    fs::remove_all(root);
    return ok && malformed && sentinel && manifest ? 0 : 1;
}
