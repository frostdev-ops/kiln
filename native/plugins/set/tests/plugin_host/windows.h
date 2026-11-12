// Windows loader shim used only by the standalone plugin lifecycle regression test.
#pragma once
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>
#include <functional>
#include <mutex>
#include <cstdio>
#include <sys/stat.h>
#include "../hooks_host/windows.h"
using DWORD = uint32_t; using UINT = unsigned; using SIZE_T = size_t;
using HANDLE = void*; using HMODULE = void*; using LPCWSTR = const wchar_t*;
using ULONG = uint32_t; using ULONG_PTR = uintptr_t; using LONG = int32_t; using BOOLEAN = unsigned char;
#define MAX_PATH 260
#define WINAPI
#define INVALID_HANDLE_VALUE ((HANDLE)(intptr_t)-1)
#define GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS 4
#define GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT 2
#define IMAGE_DOS_SIGNATURE 0x5a4d
#define IMAGE_NT_SIGNATURE 0x4550
#define ERROR_NO_MORE_FILES 18
#define ERROR_MOD_NOT_FOUND 126
#define PROCESS_VM_READ 0x10
#define PROCESS_QUERY_INFORMATION 0x400
#define FALSE 0
#define FILE_READ_ATTRIBUTES 128
#define FILE_SHARE_READ 1
#define FILE_SHARE_WRITE 2
#define FILE_SHARE_DELETE 4
#define OPEN_EXISTING 3
#define FILE_ATTRIBUTE_NORMAL 128
struct FILETIME { DWORD dwLowDateTime, dwHighDateTime; };
struct BY_HANDLE_FILE_INFORMATION {
    DWORD dwFileAttributes;
    FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
    DWORD dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
};
struct IMAGE_DOS_HEADER { uint16_t e_magic; int32_t e_lfanew; };
struct IMAGE_NT_HEADERS64 { uint32_t Signature; struct { uint32_t TimeDateStamp; } FileHeader; };
namespace loader_test {
inline std::wstring system;
inline std::wstring exe;  // GetModuleFileNameW(nullptr): the game's exe (empty: unknown)
inline std::vector<std::wstring> modules;
inline size_t cursor = 0;
// Counters and controls for the per-frame cost regression test.
inline size_t snapshots = 0;           // CreateToolhelp32Snapshot calls (the full conflict check)
inline size_t snapshot_failures = 0;   // this many CreateToolhelp32Snapshot calls fail first (a module inventory that cannot be read)
inline uint64_t now_ms = 1000000;      // GetTickCount64
inline bool notifications = true;      // whether ntdll exports LdrRegisterDllNotification
inline void (*notify)(uint32_t, const void*, void*) = nullptr;
inline void* notify_context = nullptr;
inline int registrations = 0;
inline bool registration_fails = false;
inline bool unregister_export = true;
inline bool load_during_register = false;
inline std::recursive_mutex loader_lock;
inline std::function<void()> on_snapshot;
// The loader lock as LdrLockLoaderLock/LdrUnlockLoaderLock see it, and the PEB loader list (winternl.h shim).
inline bool lock_exports = true;       // whether ntdll exports the loader-lock functions
inline bool lock_busy = false;         // another thread holds the loader lock
inline size_t lock_tries = 0, lock_busy_results = 0;
inline int32_t lock_loader(ULONG flags, ULONG* disposition, ULONG_PTR* cookie) {
    ++lock_tries;
    if (flags != 2) return -1;  // only the try-only form is allowed: the game thread must never block
    if (lock_busy || !loader_lock.try_lock()) { *disposition = 2; ++lock_busy_results; return 0; }
    *disposition = 1; *cookie = 0x1234; return 0;
}
inline int32_t unlock_loader(ULONG, ULONG_PTR cookie) {
    if (cookie != 0x1234) return -1;
    loader_lock.unlock(); return 0;
}
inline int32_t register_dll(uint32_t, void (*fn)(uint32_t, const void*, void*), void* context, void** cookie) {
    std::lock_guard lock(loader_lock);
    if (registration_fails) return -1;
    notify = fn; notify_context = context; ++registrations; *cookie = reinterpret_cast<void*>(0x77);
    if (load_during_register) fn(1, nullptr, context);
    return 0;
}
inline int32_t unregister_dll(void* cookie) {
    std::lock_guard lock(loader_lock);
    if (cookie == reinterpret_cast<void*>(0x77)) { notify = nullptr; --registrations; } return 0;
}
// What the loader does after mapping a DLL.
inline void dll_loaded() { std::lock_guard lock(loader_lock); if (notify) notify(1, nullptr, notify_context); }
inline void* const ntdll = reinterpret_cast<void*>(0x5000);
// ntdll's LdrEnumerateLoadedModules (defined in winternl.h, with the loader list): the inspecting module (HMODULE 1, what
// GetModuleHandleExW reports for it), then loader_test::modules, under the loader lock.
inline bool walk_export = true;        // whether ntdll exports LdrEnumerateLoadedModules (apart from `notifications`)
inline size_t walks = 0;               // LdrEnumerateLoadedModules calls (the full conflict check)
inline size_t walk_failures = 0;       // this many walks fail first (status walk_status): an inventory that cannot be read
inline std::function<void()> on_walk;  // runs as a walk starts (a DLL loading during the check)
inline int32_t walk_status = static_cast<int32_t>(0xC0000001);  // STATUS_UNSUCCESSFUL
// Reading this process through a handle to its own pid (kset::handle_read_probe): OpenProcess, then ReadProcessMemory.
inline bool open_own_process = true, read_own_process = true;
inline size_t probes = 0;
inline void* const own_process = reinterpret_cast<void*>(0x4000);  // not a FILE* to CloseHandle's shim
inline DWORD last_error = ERROR_NO_MORE_FILES;  // GetLastError
// A listed module that is gone (or refused) by the time GetModuleHandleExW(FROM_ADDRESS) asks for it.
inline void* gone = nullptr;
inline DWORD gone_error = ERROR_MOD_NOT_FOUND;
}
struct LDR_DATA_TABLE_ENTRY;
namespace loader_test {
inline int32_t enumerate_loaded(void* reserved, void (*fn)(LDR_DATA_TABLE_ENTRY*, void*, BOOLEAN*), void* context);
}
inline uint64_t GetTickCount64() { return loader_test::now_ms; }
inline void Sleep(DWORD) {}
inline DWORD GetLastError() { return loader_test::last_error; }
inline UINT GetSystemDirectoryW(wchar_t* out, UINT n) {
    if (loader_test::system.size() >= n) return n;
    std::wcscpy(out, loader_test::system.c_str()); return loader_test::system.size();
}
inline bool GetModuleHandleExW(DWORD flags, LPCWSTR address, HMODULE* out) {
    if (loader_test::gone && address == loader_test::gone && (flags & GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS)) {
        loader_test::last_error = loader_test::gone_error;
        *out = nullptr;
        return false;
    }
    *out = flags & GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT ? reinterpret_cast<void*>(1) : const_cast<wchar_t*>(address);
    return true;
}
inline bool FreeLibrary(HMODULE) { return true; }
inline HMODULE GetModuleHandleW(LPCWSTR name) {
    return name && std::wstring(name) == L"ntdll.dll" ? loader_test::ntdll : nullptr;
}
inline void* GetProcAddress(HMODULE h, const char* name) {
    if (h == loader_test::ntdll) {
        if (std::string(name) == "LdrEnumerateLoadedModules")
            return loader_test::walk_export ? reinterpret_cast<void*>(&loader_test::enumerate_loaded) : nullptr;
        if (!loader_test::notifications) return nullptr;
        if (loader_test::lock_exports && std::string(name) == "LdrLockLoaderLock") return reinterpret_cast<void*>(&loader_test::lock_loader);
        if (loader_test::lock_exports && std::string(name) == "LdrUnlockLoaderLock") return reinterpret_cast<void*>(&loader_test::unlock_loader);
        if (std::string(name) == "LdrRegisterDllNotification") return reinterpret_cast<void*>(&loader_test::register_dll);
        if (std::string(name) == "LdrUnregisterDllNotification" && loader_test::unregister_export) return reinterpret_cast<void*>(&loader_test::unregister_dll);
        return nullptr;
    }
    const size_t i = reinterpret_cast<uintptr_t>(h) - 2;
    return i < loader_test::modules.size() && loader_test::modules[i].find(L"framework.dll") != std::wstring::npos
        ? reinterpret_cast<void*>(1) : nullptr;
}
inline DWORD GetModuleFileNameW(HMODULE h, wchar_t* out, DWORD n) {
    if (!h) {
        if (loader_test::exe.empty() || loader_test::exe.size() >= n) return 0;
        std::wcscpy(out, loader_test::exe.c_str()); return loader_test::exe.size();
    }
    const size_t i = reinterpret_cast<uintptr_t>(h) - 2;
    if (i >= loader_test::modules.size() || loader_test::modules[i].size() >= n) return 0;
    std::wcscpy(out, loader_test::modules[i].c_str()); return loader_test::modules[i].size();
}
inline DWORD GetCurrentProcessId() { return 1; }
inline HANDLE GetCurrentProcess() { return nullptr; }
inline HANDLE CreateFileW(const char* file, DWORD, DWORD, void*, DWORD, DWORD, HANDLE) {
    const auto f = std::fopen(file, "rb");
    return f ? static_cast<HANDLE>(f) : INVALID_HANDLE_VALUE;
}
inline bool GetFileInformationByHandle(HANDLE h, BY_HANDLE_FILE_INFORMATION* i) {
    struct stat s{};
    if (fstat(fileno(static_cast<FILE*>(h)), &s)) return false;
    const uint64_t time = static_cast<uint64_t>(s.st_mtim.tv_sec) * 1000000000 + s.st_mtim.tv_nsec;
    i->dwVolumeSerialNumber = s.st_dev;
    i->nFileIndexHigh = static_cast<uint64_t>(s.st_ino) >> 32; i->nFileIndexLow = s.st_ino;
    i->nFileSizeHigh = static_cast<uint64_t>(s.st_size) >> 32; i->nFileSizeLow = s.st_size;
    i->ftLastWriteTime = {static_cast<DWORD>(time), static_cast<DWORD>(time >> 32)};
    return true;
}
inline bool CloseHandle(HANDLE h) {
    if (reinterpret_cast<uintptr_t>(h) > 0x5000 && h != INVALID_HANDLE_VALUE) std::fclose(static_cast<FILE*>(h));
    return true;
}
inline HANDLE OpenProcess(DWORD, int, DWORD) { ++loader_test::probes; return loader_test::open_own_process ? loader_test::own_process : nullptr; }
inline bool ReadProcessMemory(HANDLE h, const void* from, void* to, SIZE_T n, SIZE_T* got) {
    if (h != loader_test::own_process || !loader_test::read_own_process) return false;
    std::memcpy(to, from, n);
    if (got) *got = n;
    return true;
}
