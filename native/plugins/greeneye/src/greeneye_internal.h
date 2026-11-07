// SPDX-License-Identifier: MIT
// kiln-greeneye: shared between the plugin (plugin.cpp: Kiln, mod discovery, status) and the hook owner ported from
// Greeneye Loader (hot.cpp: GreeneyeHost API 2, hooks, leases, hot reload).
#pragma once

#include <string>
#include <vector>

#include <greeneye/greeneye.h>

namespace kgreeneye {

constexpr const char* kPluginId = "kiln-greeneye";
constexpr const char* kPluginVersion = "1.0.0";
// The Greeneye Loader release whose API (and behaviour) this plugin reproduces.
constexpr const char* kUpstreamVersion = "1.2.2";

void Log(const char* fmt, ...);                         // [greeneye] ... in kiln.log (and logs/greeneye-loader.log)
void ModLog(size_t index, const char* line);            // [greeneye:<id>] ... for the mod at that index
std::string Narrow(const std::wstring& w);
std::wstring Wide(const std::string& s);

}  // namespace kgreeneye

namespace hot {

// One mod from a greeneye folder's mod.json.
struct ModInfo {
    std::string id, name, version;
    std::wstring dir, dll;  // dir has a trailing backslash; dll is the full path
    size_t index = 0;       // the mod's index for ModLog
};

// What the status file and the log need about a started mod.
struct ModState {
    std::string id;
    unsigned generation = 0;  // the active generation, 0 = none
    unsigned generations = 0; // generations loaded (never unloaded)
    size_t hooks = 0;         // hook sites the mod owns
    bool hotApi = false;      // exports GreeneyeModHot
};

// Before any mod starts. hotReload: load from shadow copies, watch for reload requests, swap generations.
void Init(int side, const std::wstring& gameDir, bool hotReload);

// The loader's own safe point for reloads in the game (top of the UI frame). After the mods started.
void InstallSafePoint();

// Loads and starts the first generation of a mod. Returns a line for the log; ok = it runs.
std::string StartMod(const ModInfo& mod, bool* ok);

// After every mod started: the reload watcher (hot reload only).
void StartWatcher();

// A safe point offered from outside (Kiln's client-world tick): pending reloads may happen here.
void PollFromKiln();

std::vector<ModState> Describe();

// Test hooks for the conformance host: the GreeneyeHost functions, reachable without a mod.
uint8_t* FindForTest(const char* pattern);
void ReadForTest(const void* p, void* out, size_t n);

}  // namespace hot
