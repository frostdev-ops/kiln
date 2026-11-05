// SPDX-License-Identifier: MIT
#include "clr.h"

#include "log.h"

namespace kiln::clr {
namespace {

// hostfxr.h / coreclr_delegates.h, reduced to what we call.
struct hostfxr_initialize_parameters {
    size_t size;
    const wchar_t* host_path;
    const wchar_t* dotnet_root;
};
using hostfxr_initialize_for_runtime_config_fn = int(__cdecl*)(const wchar_t* runtime_config,
                                                               const hostfxr_initialize_parameters* params,
                                                               void** host_context);
using hostfxr_get_runtime_delegate_fn = int(__cdecl*)(void* host_context, int type, void** delegate);
using hostfxr_close_fn = int(__cdecl*)(void* host_context);
using hostfxr_set_error_writer_fn = void*(__cdecl*)(void(__cdecl*)(const wchar_t*));
// .NET 8+: load into the default AssemblyLoadContext, so Kiln.Host and Kiln.Api live there and every mod's
// collectible context can fall back to them by name.
using load_assembly_fn = int(__stdcall*)(const wchar_t* assembly_path, void* load_context, void* reserved);
using get_function_pointer_fn = int(__stdcall*)(const wchar_t* type_name, const wchar_t* method_name,
                                                const wchar_t* delegate_type_name, void* load_context, void* reserved,
                                                void** delegate);
using load_assembly_and_get_function_pointer_fn = int(__stdcall*)(const wchar_t* assembly_path,
                                                                  const wchar_t* type_name,
                                                                  const wchar_t* method_name,
                                                                  const wchar_t* delegate_type_name, void* reserved,
                                                                  void** delegate);
constexpr int kHdtLoadAssemblyAndGetFunctionPointer = 5;
constexpr int kHdtGetFunctionPointer = 6;
constexpr int kHdtLoadAssembly = 7;
const wchar_t* const kUnmanagedCallersOnly = reinterpret_cast<const wchar_t*>(-1);

using HostInitFn = int(__cdecl*)(const KilnApi* api);

bool g_running = false;

void __cdecl error_writer(const wchar_t* message) {
    log::write(KILN_LOG_ERROR, "hostfxr", to_utf8(message ? message : L"(null)"));
}

std::filesystem::path find_hostfxr(const std::filesystem::path& root) {
    std::error_code ec;
    std::filesystem::path best;
    for (auto& entry : std::filesystem::directory_iterator(root / L"host" / L"fxr", ec)) {
        const auto candidate = entry.path() / L"hostfxr.dll";
        if (std::filesystem::exists(candidate, ec) && (best.empty() || entry.path().filename() > best.parent_path().filename()))
            best = candidate;
    }
    return best;
}

}  // namespace

bool running() { return g_running; }

bool boot(const KilnApi* api) {
    const auto root = paths().kiln / L"dotnet";
    const auto host_dir = paths().kiln / L"host";
    const auto fxr = find_hostfxr(root);
    if (fxr.empty()) {
        KLOG_I("clr: no runtime under kiln/dotnet; managed mods are off");
        return false;
    }
    // Wine maps W^X double-mapped JIT pages poorly; the CLR runs fine without it.
    SetEnvironmentVariableW(L"DOTNET_EnableWriteXorExecute", L"0");
    SetEnvironmentVariableW(L"DOTNET_gcServer", L"0");
    const double t0 = now_ms();
    HMODULE h = LoadLibraryExW(fxr.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!h) {
        KLOG_E("clr: LoadLibrary(%s) failed: %lu", to_utf8(fxr.wstring()).c_str(), GetLastError());
        return false;
    }
    auto init = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        GetProcAddress(h, "hostfxr_initialize_for_runtime_config"));
    auto get_delegate =
        reinterpret_cast<hostfxr_get_runtime_delegate_fn>(GetProcAddress(h, "hostfxr_get_runtime_delegate"));
    auto close = reinterpret_cast<hostfxr_close_fn>(GetProcAddress(h, "hostfxr_close"));
    auto set_writer = reinterpret_cast<hostfxr_set_error_writer_fn>(GetProcAddress(h, "hostfxr_set_error_writer"));
    if (!init || !get_delegate || !close) {
        KLOG_E("clr: hostfxr exports missing");
        return false;
    }
    if (set_writer) set_writer(&error_writer);
    const std::wstring exe = paths().exe.wstring();
    const std::wstring dotnet_root = root.wstring();
    hostfxr_initialize_parameters params{sizeof(params), exe.c_str(), dotnet_root.c_str()};
    void* ctx = nullptr;
    const auto runtime_config = host_dir / L"Kiln.Host.runtimeconfig.json";
    int rc = init(runtime_config.c_str(), &params, &ctx);
    if (rc < 0 || !ctx) {  // 1 and 2 are success codes (already initialised / different properties)
        KLOG_E("clr: hostfxr_initialize_for_runtime_config failed (0x%08x)", rc);
        return false;
    }
    // Preferred: Kiln.Api + Kiln.Host in the default load context. Fallback (seen under Wine: load_assembly returns
    // 0x8007007E): hostfxr's isolated component context; mods still share one Kiln.Api because each mod context
    // hands out the host's copy by name.
    load_assembly_fn load = nullptr;
    get_function_pointer_fn get_fn = nullptr;
    load_assembly_and_get_function_pointer_fn load_isolated = nullptr;
    get_delegate(ctx, kHdtLoadAssembly, reinterpret_cast<void**>(&load));
    get_delegate(ctx, kHdtGetFunctionPointer, reinterpret_cast<void**>(&get_fn));
    get_delegate(ctx, kHdtLoadAssemblyAndGetFunctionPointer, reinterpret_cast<void**>(&load_isolated));
    close(ctx);
    HostInitFn host_init = nullptr;
    const char* mode = "default context";
    if (load && get_fn) {
        for (const wchar_t* name : {L"Kiln.Api.dll", L"Kiln.Host.dll"}) {
            const auto assembly = host_dir / name;
            rc = load(assembly.c_str(), nullptr, nullptr);
            if (rc != 0) {
                KLOG_W("clr: load_assembly(%s) failed (0x%08x); trying the isolated loader",
                       to_utf8(assembly.filename().wstring()).c_str(), rc);
                break;
            }
        }
        if (rc == 0)
            rc = get_fn(L"Kiln.Host.Entry, Kiln.Host", L"Init", kUnmanagedCallersOnly, nullptr, nullptr,
                        reinterpret_cast<void**>(&host_init));
    }
    if (!host_init && load_isolated) {
        mode = "isolated context";
        const auto assembly = host_dir / L"Kiln.Host.dll";
        rc = load_isolated(assembly.c_str(), L"Kiln.Host.Entry, Kiln.Host", L"Init", kUnmanagedCallersOnly, nullptr,
                           reinterpret_cast<void**>(&host_init));
    }
    if (!host_init) {
        KLOG_E("clr: loading Kiln.Host.Entry.Init failed (0x%08x)", rc);
        return false;
    }
    KLOG_I("clr: Kiln.Host loaded (%s)", mode);
    const double t1 = now_ms();
    rc = host_init(api);
    if (rc != 0) {
        KLOG_E("clr: Kiln.Host.Init returned %d", rc);
        return false;
    }
    g_running = true;
    KLOG_I("clr: .NET up in %.0f ms, Kiln.Host initialised in %.0f ms", t1 - t0, now_ms() - t1);
    return true;
}

}  // namespace kiln::clr
