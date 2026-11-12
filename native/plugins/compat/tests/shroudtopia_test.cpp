// SPDX-License-Identifier: MIT
// A Shroudtopia mod for compathost, written like Shroudtopia's example mods: a Mod subclass exported through
// CreateModInstance. It checks the whole ModContext (game/loader info, the ten config getters, the ten memory
// helpers, Log) and the lifecycle (Load once, Activate when "active", Update every cycle, Deactivate when switched
// off). Each check logs "CHECK <name> ok|fail". Built with ST_CLIENT_ONLY: a mod without server support; with
// ST_LAB: a probe for a real dedicated server (config, lifecycle, read-only memory helpers).
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <cwchar>
#include <limits>
#include <stdexcept>
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
volatile LONG g_missing_original = 0, g_calling = 0, g_calls = 0;
int detour(int a, int b) {
    auto original = g_tramp;
    if (!original) { InterlockedIncrement(&g_missing_original); return 0; }
    return original(a, b) + 7;
}
DWORD WINAPI call_target(void* p) {
    auto target = reinterpret_cast<int (*)(int, int)>(p);
    while (InterlockedCompareExchange(&g_calling, 0, 0)) {
        target(2, 3);
        InterlockedIncrement(&g_calls);
    }
    return 0;
}
int constant_detour() { return 99; }
#ifdef ST_LAB
// Lab: a pass-through CreateDetour on ChatLogAdd (nine arguments, four in registers), next to kiln.chat's link on it. The
// RVA comes from lab_detour_rva.txt in the mod's folder (hex; kiln.log's "chat: hooks on ... ChatLogAdd rva 0x..." line).
using LabChatAddFn = void* (*)(void*, void*, uint8_t, const void*, uint32_t, uint32_t, uint32_t, uint64_t, const void*);
LabChatAddFn g_lab_chat_tramp = nullptr;
volatile LONG g_lab_chat_calls = 0;
void* lab_chat_add(void* out, void* sys, uint8_t type, const void* text, uint32_t a, uint32_t b, uint32_t h, uint64_t acct,
                   const void* time) {
    InterlockedIncrement(&g_lab_chat_calls);
    return g_lab_chat_tramp(out, sys, type, text, a, b, h, acct, time);
}
#endif
uint8_t g_patch_data[8] = {1, 2, 3, 4, 5, 6, 7, 8};

bool named(const wchar_t* needle) {
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                      reinterpret_cast<LPCWSTR>(&named), &self);
    wchar_t path[MAX_PATH]{};
    return GetModuleFileNameW(self, path, MAX_PATH) && std::wcsstr(path, needle);
}

void fault_if(const wchar_t* name) {
    if (named(name)) RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
}

class TestMod : public Mod {
    int updates_ = 0;
    ModContext* ctx_ = nullptr;
    void check(const char* name, bool ok) {
        ctx_->Log((std::string("CHECK ") + name + (ok ? " ok" : " fail")).c_str());
    }

public:
    ModMetaData GetMetaData() override { fault_if(L"metadata-fault"); return kMeta; }

    void Load(ModContext* c) override {
        ctx_ = c;
        fault_if(L"load-fault");
        if (named(L"load-throw")) throw std::runtime_error("test C++ exception from Load");
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
            {
                FILE* f = std::fopen((c->shroudtopia.mod_folder + "\\lab_detour_rva.txt").c_str(), "rb");
                char rva_text[32] = {};
                if (f) {
                    std::fread(rva_text, 1, sizeof rva_text - 1, f);
                    std::fclose(f);
                    const uintptr_t target = exe + static_cast<uintptr_t>(std::strtoull(rva_text, nullptr, 16));
                    const bool ok = c->memory.CreateDetour(reinterpret_cast<void*>(target), reinterpret_cast<void*>(&lab_chat_add),
                                                           reinterpret_cast<void**>(&g_lab_chat_tramp)) && g_lab_chat_tramp;
                    std::snprintf(line, sizeof line, "lab: CreateDetour on ChatLogAdd rva %s: %s", rva_text, ok ? "installed" : "FAILED");
                    c->Log(line);
                }
            }
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
        uint8_t* data = g_patch_data;
        const uint8_t two[2] = {9, 9};
        check("write", c->memory.Write(reinterpret_cast<uintptr_t>(data), two, 2) && data[0] == 9);
        uint8_t three[1] = {7};
        check("write_ro", c->memory.WriteToReadOnlyMemory(data + 2, three, 1) && data[2] == 7);
        uint8_t patch[1] = {0x55};
        check("apply_patch", c->memory.ApplyPatch(reinterpret_cast<uintptr_t>(data + 3), patch, 1) && data[3] == 0x55);
        {
            SYSTEM_INFO si{};
            GetSystemInfo(&si);
            const size_t page = si.dwPageSize;
            auto* pages = static_cast<uint8_t*>(VirtualAlloc(nullptr, page * 3, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
            check("memory_pages_allocated", pages != nullptr);
            if (pages) {
                const char marker[] = "\xD3\x71\x52\x99";
                std::memcpy(pages + 16, marker, 4);
                check("find_start_bound", !c->memory.FindPattern(marker, "xxxx", reinterpret_cast<uintptr_t>(pages + 32), 64));
                check("find_length_bound", !c->memory.FindPattern(marker, "xxxx", reinterpret_cast<uintptr_t>(pages), 16));
                check("find_pattern_longer_than_range", !c->memory.FindPattern(marker, "xxxx", reinterpret_cast<uintptr_t>(pages + 16), 3));
                check("find_empty_mask", !c->memory.FindPattern(marker, "", reinterpret_cast<uintptr_t>(pages), page));
                check("find_address_overflow", !c->memory.FindPattern(marker, "xxxx", UINTPTR_MAX - 2, 8));
                std::memcpy(pages + page - 2, marker, 4);
                DWORD old = 0;
                VirtualProtect(pages, page, PAGE_READONLY, &old);
                VirtualProtect(pages + page, page, PAGE_EXECUTE_READ, &old);
                auto protection = [](void* p) {
                    MEMORY_BASIC_INFORMATION mbi{};
                    return VirtualQuery(p, &mbi, sizeof mbi) ? mbi.Protect : 0;
                };
                check("find_cross_readable_regions", c->memory.FindPattern(marker, "xxxx", reinterpret_cast<uintptr_t>(pages + page - 2), 4) ==
                      reinterpret_cast<uintptr_t>(pages + page - 2));
                const uint8_t replacement[] = {8, 7, 6, 5};
                check("write_mixed_regions", c->memory.Write(reinterpret_cast<uintptr_t>(pages + page - 2), replacement, 4) &&
                      std::memcmp(pages + page - 2, replacement, 4) == 0);
                check("write_restores_each_protection", protection(pages) == PAGE_READONLY && protection(pages + page) == PAGE_EXECUTE_READ);
                check("write_bad_source", !c->memory.Write(reinterpret_cast<uintptr_t>(pages + page - 2), reinterpret_cast<void*>(1), 4) &&
                      protection(pages) == PAGE_READONLY && protection(pages + page) == PAGE_EXECUTE_READ);
                VirtualProtect(pages + page * 2, page, PAGE_NOACCESS, &old);
                check("write_noaccess_tail", !c->memory.Write(reinterpret_cast<uintptr_t>(pages + page * 2 - 2), replacement, 4) &&
                      protection(pages + page) == PAGE_EXECUTE_READ && protection(pages + page * 2) == PAGE_NOACCESS);
                VirtualProtect(pages + page * 2, page, PAGE_READWRITE | PAGE_GUARD, &old);
                check("read_preserves_source_guard", !c->memory.Read(reinterpret_cast<uintptr_t>(pages + page * 2), buf, 4) &&
                      (protection(pages + page * 2) & PAGE_GUARD));
                check("read_preserves_output_guard", !c->memory.Read(reinterpret_cast<uintptr_t>(pages), pages + page * 2, 4) &&
                      (protection(pages + page * 2) & PAGE_GUARD));
                check("write_address_overflow", !c->memory.Write(UINTPTR_MAX - 1, replacement, 4));
                check("read_address_overflow", !c->memory.Read(UINTPTR_MAX - 1, buf, 4));
                check("write_zero_size", !c->memory.Write(reinterpret_cast<uintptr_t>(pages), replacement, 0));
                // A mixed-page patch must restore both protection regions just like a plain write.
                check("patch_mixed_regions", c->memory.ApplyPatch(reinterpret_cast<uintptr_t>(pages + page - 2),
                      const_cast<uint8_t*>(replacement), 4) && protection(pages) == PAGE_READONLY &&
                      protection(pages + page) == PAGE_EXECUTE_READ);
                // The patched reservation remains live until process exit so plugin-unload restoration can run.
            }
        }
        check("alloc_zero_size", !c->memory.AllocateMemoryNearAddress(reinterpret_cast<void*>(hit), 0));
        check("alloc_size_overflow", !c->memory.AllocateMemoryNearAddress(reinterpret_cast<void*>(hit), SIZE_MAX));
        check("alloc_address_overflow", !c->memory.AllocateMemoryNearAddress(reinterpret_cast<void*>(UINTPTR_MAX - 2), 4096));
        void* near_mem = c->memory.AllocateMemoryNearAddress(reinterpret_cast<void*>(hit), 0x1000);
        const intptr_t dist = reinterpret_cast<intptr_t>(near_mem) - static_cast<intptr_t>(hit);
        check("alloc_near", near_mem && dist > -0x7FFFFFFFll && dist < 0x7FFFFFFFll);
        uint8_t code[] = {0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3};  // mov eax, 42; ret
        uintptr_t at = 0;
        uintptr_t failed_at = 123;
        check("inject_null_source", !c->memory.InjectShellcode(nullptr, sizeof code, hit, &failed_at) && !failed_at);
        failed_at = 123;
        check("inject_zero_size", !c->memory.InjectShellcode(code, 0, hit, &failed_at) && !failed_at);
        check("inject_invalid_output", !c->memory.InjectShellcode(code, sizeof code, hit, reinterpret_cast<uintptr_t*>(1)));
        failed_at = 123;
        check("inject_oversize", !c->memory.InjectShellcode(code, SIZE_MAX, hit, &failed_at) && !failed_at);
        check("inject_shellcode", c->memory.InjectShellcode(code, sizeof code, hit, &at) && at &&
                                      reinterpret_cast<int (*)()>(at)() == 42);
        check("inject_optional_output", c->memory.InjectShellcode(code, sizeof code, hit, nullptr));
        if (near_mem) {
            // A jump in the near block to the injected code: calling the block runs the shellcode.
            c->memory.WriteJump(near_mem, reinterpret_cast<void*>(at));
            check("write_jump", reinterpret_cast<int (*)()>(near_mem)() == 42);
            auto* spare = static_cast<uint8_t*>(near_mem) + 64;
            std::memset(spare, 0x90, 32);
            std::memcpy(spare, code, sizeof code);
            FlushInstructionCache(GetCurrentProcess(), spare, 32);
            check("detour_optional_output", c->memory.CreateDetour(spare, reinterpret_cast<void*>(&constant_detour), nullptr) &&
                  reinterpret_cast<int (*)()>(spare)() == 99);
        }
        check("detour_invalid_output", !c->memory.CreateDetour(reinterpret_cast<void*>(target), reinterpret_cast<void*>(&detour),
                      reinterpret_cast<void**>(1)) && target(2, 3) == 2 + 3 + 0x5A17C0DE);
        check("detour_invalid_target", !c->memory.CreateDetour(reinterpret_cast<void*>(1), reinterpret_cast<void*>(&detour),
                      reinterpret_cast<void**>(&g_tramp)));
        InterlockedExchange(&g_calling, 1);
        HANDLE caller = CreateThread(nullptr, 0, &call_target, reinterpret_cast<void*>(target), 0, nullptr);
        for (int i = 0; caller && !InterlockedCompareExchange(&g_calls, 0, 0) && i < 100; ++i) Sleep(1);
        check("create_detour", c->memory.CreateDetour(reinterpret_cast<void*>(target), reinterpret_cast<void*>(&detour),
                      reinterpret_cast<void**>(&g_tramp)) && g_tramp);
        InterlockedExchange(&g_calling, 0);
        if (caller) { WaitForSingleObject(caller, INFINITE); CloseHandle(caller); }
        check("detour_original_published_before_enable", caller && g_calls && !g_missing_original);
        check("detour_runs", target && target(2, 3) == 2 + 3 + 0x5A17C0DE + 7);
        c->Log("loaded");
    }
    void Unload(ModContext*) override {}
    void Activate(ModContext* c) override {
        fault_if(L"activate-fault");
        active = true;
        c->Log("activated");
    }
    void Deactivate(ModContext* c) override {
        fault_if(L"deactivate-fault");
        active = false;
        c->Log("deactivated");
    }
    void Update(ModContext* c) override {
        fault_if(L"update-fault");
        if (++updates_ == 3) c->Log("third update");
    }
};

}  // namespace

volatile LONG g_instances = 0;  // CreateModInstance calls on this module (compathost --load-create, issue 23)
extern "C" __declspec(dllexport) Mod* CreateModInstance() {
    fault_if(L"factory-fault");
    InterlockedIncrement(&g_instances);
    return new TestMod();
}
extern "C" __declspec(dllexport) long kiln_shroudtopia_test_instances() { return InterlockedCompareExchange(&g_instances, 0, 0); }
extern "C" __declspec(dllexport) bool kiln_shroudtopia_test_patch_restored() { return g_patch_data[3] == 4; }

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
