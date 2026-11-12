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
    fs::remove_all(root);
    return ok ? 0 : 1;
}
