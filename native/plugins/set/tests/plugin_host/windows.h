// Windows loader shim used only by the standalone plugin lifecycle regression test.
#pragma once
#include <cstdint>
#include <cstddef>
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
using ULONG = uint32_t; using ULONG_PTR = uintptr_t;
#define MAX_PATH 260
#define WINAPI
#define INVALID_HANDLE_VALUE ((HANDLE)(intptr_t)-1)
#define GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS 4
#define GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT 2
#define IMAGE_DOS_SIGNATURE 0x5a4d
#define IMAGE_NT_SIGNATURE 0x4550
#define ERROR_NO_MORE_FILES 18
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
inline std::vector<std::wstring> modules;
inline size_t cursor = 0;
// Counters and controls for the per-frame cost regression test.
inline size_t snapshots = 0;           // CreateToolhelp32Snapshot calls (the full conflict check)
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
}
inline uint64_t GetTickCount64() { return loader_test::now_ms; }
inline void Sleep(DWORD) {}
inline DWORD GetLastError() { return ERROR_NO_MORE_FILES; }
inline UINT GetSystemDirectoryW(wchar_t* out, UINT n) {
    if (loader_test::system.size() >= n) return n;
    std::wcscpy(out, loader_test::system.c_str()); return loader_test::system.size();
}
inline bool GetModuleHandleExW(DWORD, LPCWSTR, HMODULE* out) { *out = reinterpret_cast<void*>(1); return true; }
inline HMODULE GetModuleHandleW(LPCWSTR name) {
    return name && std::wstring(name) == L"ntdll.dll" ? loader_test::ntdll : nullptr;
}
inline void* GetProcAddress(HMODULE h, const char* name) {
    if (h == loader_test::ntdll) {
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
inline bool ReadProcessMemory(HANDLE, const void*, void*, SIZE_T, SIZE_T*) { return false; }
