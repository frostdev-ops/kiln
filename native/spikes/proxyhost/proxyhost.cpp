// SPDX-License-Identifier: MIT
// proxyhost.exe: a stand-in for the game. It imports DINPUT8!DirectInput8Create (so Wine loads the dinput8.dll next
// to it, the proxy under test), and its entry point is the ordinary CRT entry, which the proxy's gate patches. main()
// runs only after every gate unwound: it logs that, which test DLLs are loaded, GetdfDIJoystick and KilnProxyInfo.
#include <cstdint>
#include <initializer_list>

#include "harness.h"
#include "kiln_api.h"

extern "C" HRESULT WINAPI DirectInput8Create(HINSTANCE, DWORD, REFIID, void**, void*);

namespace {
struct DiDataFormat {
    DWORD size, object_size, flags, data_size, objects;
    const void* format;
};
const GUID kIidDirectInput8W = {0xBF798031, 0x483A, 0x4DA2, {0xAA, 0x99, 0x5D, 0x64, 0xED, 0x36, 0x97, 0x00}};
}  // namespace

int main() {
    ph_log("main reached");
    for (const wchar_t* name : {L"chain-a.dll", L"chain-b.dll", L"chain-c.dll", L"emf-x.dll", L"emf-y.dll",
                                L"entrypatch-1.dll", L"entrypatch-2.dll", L"failing.dll", L"fake-sf.dll", L"kiln-core.dll"})
        if (GetModuleHandleW(name)) ph_log("main module %S loaded", name);
    HMODULE proxy = GetModuleHandleW(L"dinput8.dll");
    using GetDf = const DiDataFormat*(WINAPI*)();
    if (auto f = reinterpret_cast<GetDf>(GetProcAddress(proxy, "GetdfDIJoystick"))) {
        const DiDataFormat* df = f();
        if (df)
            ph_log("main GetdfDIJoystick size=%lu objsize=%lu flags=%lu datasize=%lu objects=%lu", df->size,
                   df->object_size, df->flags, df->data_size, df->objects);
        else
            ph_log("main GetdfDIJoystick returned null");
    } else {
        ph_log("main GetdfDIJoystick not exported");
    }
    if (auto f = reinterpret_cast<KilnProxyInfoFn>(GetProcAddress(proxy, KILN_PROXY_INFO))) {
        const KilnProxyInfoData* p = f();
        char marker[128] = {};
        for (size_t i = 0; i < p->marker.len && i < sizeof marker - 1; ++i) marker[i] = p->marker.ptr[i];
        ph_log("main KilnProxyInfo abi=%u boot_info_size=%u marker=%s", p->abi, p->boot_info_size, marker);
    }
    void* di = nullptr;
    const HRESULT hr = DirectInput8Create(GetModuleHandleW(nullptr), 0x0800, kIidDirectInput8W, &di, nullptr);
    ph_log("main DirectInput8Create hr=0x%08lx %s", static_cast<unsigned long>(hr), di ? "object" : "null");
    ph_log("main done");
    return 0;
}
