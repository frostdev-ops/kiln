// SPDX-License-Identifier: MIT
// Fake runtime host only: never launches or loads Enshrouded.
#include <windows.h>
#include <filesystem>
#include <string>
#include <cstdio>
#include <cstring>
#include "kiln_api.h"
static std::filesystem::path game;
static KilnResult path(int32_t kind, char* out, size_t cap, size_t* len) {
    auto p = game;
    if (kind == KILN_PATH_KILN) p /= L"kiln";
    if (kind == KILN_PATH_STATE) p /= L"kiln/state";
    const auto u = p.u8string(); *len = u.size();
    if (cap < u.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out, u.data(), u.size()); return KILN_OK;
}
static void log(KilnOwner, int32_t, KilnStr text) { std::printf("%.*s\n", int(text.len), text.ptr); }
static const void* service(KilnStr, uint32_t) { return nullptr; }
static uint32_t side() { return KILN_SIDE_SERVER; }
int main(int argc, char** argv) {
    if (argc < 3) return 2;
    game = std::filesystem::absolute(argv[1]);
    auto dll = std::filesystem::absolute(argv[2]);
    if (argc > 3 && std::string(argv[3]) == "early") LoadLibraryW((game / L"kiln/plugins/kiln-eml.dll").c_str());
    HMODULE h = LoadLibraryW(dll.c_str()); if (!h) return 3;
    auto fn = reinterpret_cast<KilnPluginMainFn>(GetProcAddress(h, argc > 3 ? "kiln_plugin_early" : "kiln_plugin_main"));
    if (!fn) return 4;
    KilnApi api{}; api.size = sizeof api; api.abi_version = KILN_ABI_VERSION;
    api.path = path; api.log = log; api.service = service; api.side = side;
    KilnPluginInfo info{}; info.size = sizeof info;
    if (fn(&api, 1, &info) != KILN_OK) return 5;
    if (info.on_ready) info.on_ready(info.user);
    Sleep(120);
    // The core may call normal main again after the early pass; it must not prepare or start a second worker.
    if (argc > 3) {
        auto normal = reinterpret_cast<KilnPluginMainFn>(GetProcAddress(h, "kiln_plugin_main"));
        info = {}; info.size = sizeof info;
        if (normal(&api, 2, &info) != KILN_OK) return 6;
        if (info.on_ready) info.on_ready(info.user);
    }
    if (info.on_unload) info.on_unload(info.user);
    Sleep(150); // allow signalled, serial worker cleanup before the fake host exits
    return 0;
}
