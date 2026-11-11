// PEB loader-list shim for the plugin lifecycle regression test: the list is loader_test::modules (what the
// toolhelp shim also enumerates), so adding a module without dll_loaded() is a silent load.
#pragma once
#include <cstddef>
#include <map>
#include "windows.h"
struct LIST_ENTRY { LIST_ENTRY* Flink; LIST_ENTRY* Blink; };
struct UNICODE_STRING { uint16_t Length, MaximumLength; wchar_t* Buffer; };
struct LDR_DATA_TABLE_ENTRY {
    void* Reserved1[2]; LIST_ENTRY InMemoryOrderLinks; void* Reserved2[2]; void* DllBase; void* Reserved3[2];
    UNICODE_STRING FullDllName;
};
struct PEB_LDR_DATA { LIST_ENTRY InMemoryOrderModuleList; };
struct PEB { PEB_LDR_DATA* Ldr; };
struct TEB { PEB* ProcessEnvironmentBlock; };
#ifndef CONTAINING_RECORD
#define CONTAINING_RECORD(address, type, field) ((type*)((char*)(address) - offsetof(type, field)))
#endif
namespace loader_test {
inline size_t peb_walks = 0;
inline std::vector<LDR_DATA_TABLE_ENTRY> peb_entries;
inline std::map<std::wstring, std::wstring> peb_names;  // stable name buffers, as the loader's are
inline PEB_LDR_DATA peb_ldr;
inline PEB peb{&peb_ldr};
inline TEB teb{&peb};
}
inline int32_t loader_test::enumerate_loaded(void* reserved, void (*fn)(LDR_DATA_TABLE_ENTRY*, void*, BOOLEAN*), void* context) {
    ++walks;
    if (on_walk) on_walk();
    if (reserved || !fn) return static_cast<int32_t>(0xC000000D);  // STATUS_INVALID_PARAMETER
    if (walk_failures) { --walk_failures; return walk_status; }
    std::lock_guard lock(loader_lock);
    static wchar_t self_name[] = L"C:\\game\\kiln\\plugins\\kiln-set.dll";
    std::vector<LDR_DATA_TABLE_ENTRY> list(modules.size() + 1, LDR_DATA_TABLE_ENTRY{});
    list[0].DllBase = reinterpret_cast<void*>(1);
    // UNICODE_STRING lengths are in bytes; this host's wchar_t is wider than Windows' two bytes.
    list[0].FullDllName = {static_cast<uint16_t>((std::size(self_name) - 1) * sizeof(wchar_t)), static_cast<uint16_t>(sizeof self_name), self_name};
    for (size_t i = 0; i < modules.size(); ++i) {
        auto& name = peb_names.try_emplace(modules[i], modules[i]).first->second;
        list[i + 1].DllBase = reinterpret_cast<void*>(i + 2);  // the HMODULE GetModuleFileNameW's shim resolves
        const auto bytes = static_cast<uint16_t>(name.size() * sizeof(wchar_t));
        list[i + 1].FullDllName = {bytes, bytes, name.data()};
    }
    BOOLEAN stop = 0;
    for (auto& e : list) {
        fn(&e, context, &stop);
        if (stop) break;
    }
    return 0;
}
inline TEB* NtCurrentTeb() {
    using namespace loader_test;
    ++peb_walks;
    peb_entries.assign(modules.size(), LDR_DATA_TABLE_ENTRY{});
    LIST_ENTRY* head = &peb_ldr.InMemoryOrderModuleList;
    LIST_ENTRY* prev = head;
    for (size_t i = 0; i < modules.size(); ++i) {
        auto& e = peb_entries[i];
        auto& name = peb_names.try_emplace(modules[i], modules[i]).first->second;
        e.DllBase = reinterpret_cast<void*>(i + 2);  // the HMODULE the toolhelp shim reports
        e.FullDllName = {static_cast<uint16_t>(name.size() * 2), static_cast<uint16_t>(name.size() * 2), name.data()};
        prev->Flink = &e.InMemoryOrderLinks;
        e.InMemoryOrderLinks.Blink = prev;
        prev = &e.InMemoryOrderLinks;
    }
    prev->Flink = head;
    head->Blink = prev;
    return &teb;
}
