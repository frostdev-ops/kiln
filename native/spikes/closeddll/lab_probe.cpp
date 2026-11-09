// SPDX-License-Identifier: MIT
// kiln-labprobe.dll: a lab-only Kiln plugin that puts one kiln.hooks v2 mid link on a sigdb symbol, so a lab server has a
// registry target that a foreign-stub.dll (native/spikes/hooksv2) can hook before or over. Not deployed.
//
//   <game>/kiln/plugins/kiln-labprobe.dll and kiln-labprobe.ini next to it:  symbol=gameplay/InventoryTransfer
// kiln.log: "labprobe: hooked <symbol> at rva 0x..." (the RVA to give foreign-stub.ini).
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>

#include "kiln_api.h"
#include "services/kiln_hooks.h"

namespace {

void say(const KilnApi* api, KilnOwner owner, int32_t level, const std::string& text) {
    api->log(owner, level, KilnStr{text.data(), text.size()});
}

void KILN_CALL on_hit(KilnRegisters*, void*) {}  // the link only has to exist

std::string symbol_from_ini() {
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&on_hit), &self);
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(self, path, MAX_PATH);
    std::wstring ini(path, n);
    ini.replace(ini.size() - 3, 3, L"ini");
    FILE* f = _wfopen(ini.c_str(), L"r");
    std::string symbol = "gameplay/InventoryTransfer";
    if (f) {
        char line[512];
        while (std::fgets(line, sizeof line, f))
            if (std::strncmp(line, "symbol=", 7) == 0) {
                symbol = line + 7;
                while (!symbol.empty() && (symbol.back() == '\n' || symbol.back() == '\r' || symbol.back() == ' ')) symbol.pop_back();
            }
        std::fclose(f);
    }
    return symbol;
}

}  // namespace

extern "C" __declspec(dllexport) KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    info->id = KilnStr{"labprobe", 8};
    info->version = KilnStr{"1", 1};
    const auto* hooks = static_cast<const KilnHooksApi*>(api->service(KilnStr{KILN_HOOKS_SERVICE, sizeof(KILN_HOOKS_SERVICE) - 1}, 2));
    if (!hooks || hooks->header.version < 2) {
        say(api, owner, KILN_LOG_WARN, "labprobe: kiln.hooks v2 is not available");
        return KILN_OK;
    }
    const std::string symbol = symbol_from_ini();
    uintptr_t target = 0;
    if (hooks->symbol(KilnStr{symbol.data(), symbol.size()}, &target) != KILN_OK) {
        say(api, owner, KILN_LOG_WARN, "labprobe: symbol " + symbol + " is not resolved on this build");
        return KILN_OK;
    }
    const std::string label = "labprobe/" + symbol;
    KilnHookOrder order{sizeof(KilnHookOrder), 0, KilnStr{label.data(), label.size()}, KilnStr{"", 0}, KilnStr{"", 0}};
    KilnHandle handle = 0;
    char rva[32];
    std::snprintf(rva, sizeof rva, "0x%llx", static_cast<unsigned long long>(target - hooks->image_base()));
    if (hooks->mid2(owner, target, &order, &on_hit, nullptr, &handle) != KILN_OK) {
        say(api, owner, KILN_LOG_WARN, "labprobe: could not hook " + symbol);
        return KILN_OK;
    }
    say(api, owner, KILN_LOG_INFO, "labprobe: hooked " + symbol + " at rva " + rva);
    return KILN_OK;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(inst);
    return TRUE;
}
