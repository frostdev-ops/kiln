// SPDX-License-Identifier: MIT
// A Shroudtopia mod for compathost, written like Shroudtopia's example mods: a Mod subclass exported through
// CreateModInstance. It checks the whole ModContext (game/loader info, the ten config getters, the ten memory
// helpers, Log) and the lifecycle (Load once, Activate when "active", Update every cycle, Deactivate when switched
// off). Each check logs "CHECK <name> ok|fail". Built with ST_CLIENT_ONLY: a mod without server support; with
// ST_LAB: a probe for a real dedicated server (config, lifecycle, read-only memory helpers).
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include <shroudtopia/shroudtopia.h>

namespace {

#if defined(ST_LAB)
ModMetaData kMeta = {"Kiln Lab Probe", "lab checks on a real server", "1.0", "kiln", "0.1.1", false, true};
#elif defined(ST_CLIENT_ONLY)
ModMetaData kMeta = {"Kiln Client Mod", "client only", "1.0", "kiln", "0.1.1", true, false};
#else
ModMetaData kMeta = {"Kiln Test Mod", "conformance checks", "2.0", "kiln", "0.1.1", true, true};
#endif

int (*g_tramp)(int, int) = nullptr;
int detour(int a, int b) { return g_tramp(a, b) + 7; }

class TestMod : public Mod {
    int updates_ = 0;
    ModContext* ctx_ = nullptr;
    void check(const char* name, bool ok) {
        ctx_->Log((std::string("CHECK ") + name + (ok ? " ok" : " fail")).c_str());
    }

public:
    ModMetaData GetMetaData() override { return kMeta; }

    void Load(ModContext* c) override {
        ctx_ = c;
        const char* key = kMeta.name.c_str();
        check("version", c->shroudtopia.version == "0.1.1");
        check("config_file", c->shroudtopia.config_file.find("shroudtopia.json") != std::string::npos);
        check("side", c->game.isServer != c->game.isClient);
        check("get_bool", c->config.GetBool(key, "flag", false) == true);
        check("get_bool_default", c->config.GetBool(key, "missing", true) == true);
        check("get_bool_wrong_type", c->config.GetBool(key, "count", true) == true);
        check("get_int", c->config.GetInt(key, "count", 0) == 5);
        check("get_float", c->config.GetFloat(key, "ratio", 0.f) == 1.5f);
        check("get_string", c->config.GetString(key, "title", "") == "kiln");
        check("get_uint64", c->config.GetUint64(key, "big", 0) == 12345678901ull);
        check("get_bool_array", c->config.GetBoolArray(key, "bools", {}) == std::vector<bool>{true, false});
        check("get_int_array", c->config.GetIntArray(key, "ints", {}) == std::vector<int>{1, 2, 3});
        check("get_float_array", c->config.GetFloatArray(key, "floats", {}) == std::vector<float>{1.5f, 2.5f});
        check("get_string_array", c->config.GetStringArray(key, "strings", {}) == std::vector<std::string>{"a", "b"});
        check("get_uint64_array", c->config.GetUint64Array(key, "bigs", {}) == std::vector<uint64_t>{1, 2});

#ifdef ST_LAB
        {
            // Lab probe (a real dedicated server): the game's chat-log function by Shroudtopia's FindPattern, no writes.
            const uintptr_t exe = c->memory.GetModuleBaseAddress("enshrouded_server");
            check("lab_module_base", exe == reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)));
            const char p[] = "\x40\x55\x56\x41\x54\x41\x55\x41\x57\x48\x83\xEC\x30\x49\x8B\x41\x08\x33\xED";
            const uintptr_t at = c->memory.FindPattern(p, "xxxxxxxxxxxxxxxxxxx", exe, 0x2000000);
            uint8_t b[2] = {};
            char line[160];
            std::snprintf(line, sizeof line, "lab: ChatLogAdd by FindPattern: %s rva 0x%llx; read %s", at ? "found" : "not found (hooked?)",
                          static_cast<unsigned long long>(at ? at - exe : 0), c->memory.Read(exe, b, 2) && b[0] == 'M' && b[1] == 'Z' ? "MZ ok" : "failed");
            c->Log(line);
            c->Log("loaded");
            return;
        }
#endif
        const uintptr_t base = c->memory.GetModuleBaseAddress("enshrouded");
        check("module_base", base == reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)));
        const char pat[] = "\x8D\x04\x11\x05\xDE\xC0\x17\x5A";
        const uintptr_t hit = c->memory.FindPattern(pat, "xxxx?xxx", base, 0x1000000);
        auto target = reinterpret_cast<int (*)(int, int)>(GetProcAddress(GetModuleHandleW(nullptr), "gt_target"));
        check("find_pattern", hit && hit == reinterpret_cast<uintptr_t>(target));
        uint8_t buf[4] = {};
        check("read", c->memory.Read(hit, buf, 4) && buf[0] == 0x8D);
        static uint8_t data[8] = {1, 2, 3, 4, 5, 6, 7, 8};
        const uint8_t two[2] = {9, 9};
        check("write", c->memory.Write(reinterpret_cast<uintptr_t>(data), two, 2) && data[0] == 9);
        uint8_t three[1] = {7};
        check("write_ro", c->memory.WriteToReadOnlyMemory(data + 2, three, 1) && data[2] == 7);
        uint8_t patch[1] = {0x55};
        check("apply_patch", c->memory.ApplyPatch(reinterpret_cast<uintptr_t>(data + 3), patch, 1) && data[3] == 0x55);
        void* near_mem = c->memory.AllocateMemoryNearAddress(reinterpret_cast<void*>(hit), 0x1000);
        const intptr_t dist = reinterpret_cast<intptr_t>(near_mem) - static_cast<intptr_t>(hit);
        check("alloc_near", near_mem && dist > -0x7FFFFFFFll && dist < 0x7FFFFFFFll);
        uint8_t code[] = {0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3};  // mov eax, 42; ret
        uintptr_t at = 0;
        check("inject_shellcode", c->memory.InjectShellcode(code, sizeof code, hit, &at) && at &&
                                      reinterpret_cast<int (*)()>(at)() == 42);
        if (near_mem) {
            // A jump in the near block to the injected code: calling the block runs the shellcode.
            c->memory.WriteJump(near_mem, reinterpret_cast<void*>(at));
            check("write_jump", reinterpret_cast<int (*)()>(near_mem)() == 42);
        }
        void* tramp = nullptr;
        check("create_detour", c->memory.CreateDetour(reinterpret_cast<void*>(target), reinterpret_cast<void*>(&detour), &tramp) &&
                                   tramp);
        g_tramp = reinterpret_cast<int (*)(int, int)>(tramp);
        check("detour_runs", target && target(2, 3) == 2 + 3 + 0x5A17C0DE + 7);
        c->Log("loaded");
    }
    void Unload(ModContext*) override {}
    void Activate(ModContext* c) override {
        active = true;
        c->Log("activated");
    }
    void Deactivate(ModContext* c) override {
        active = false;
        c->Log("deactivated");
    }
    void Update(ModContext* c) override {
        if (++updates_ == 3) c->Log("third update");
    }
};

}  // namespace

extern "C" __declspec(dllexport) Mod* CreateModInstance() { return new TestMod(); }

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
