// SPDX-License-Identifier: MIT
#include "src/plugin.cpp"
#include <cstdlib>
using namespace kember;
static int checks;
static void check(bool ok) { ++checks; if (!ok) { std::printf("Ember check %d failed\n", checks); std::exit(1); } }
int main() {
    KilnApi api{}; api.size = sizeof api; api.abi_version = KILN_ABI_VERSION;
    KilnPluginInfo info{}; info.size = sizeof info;
    check(kiln_plugin_main(&api, 1, &info) == KILN_ERR_UNAVAILABLE); // required slots are not optional
    const auto base = fs::temp_directory_path() / (L"kiln-ember-contract-" + std::to_wstring(GetCurrentProcessId()));
    fs::create_directories(base / L"mod");
    const auto dll = base / L"mod/test.dll";
    // A malformed DLL is inspected without executing it; its lack of an ABI is still reported.
    kcompat::write_file_atomic(dll, "MZ deliberately not executable");
    const auto m = read_mod(dll, "test", true, "profile", dll);
    check(m.status == "waiting-for-abi" && m.exports.empty());
    check(!GetModuleHandleW(dll.c_str()));
    check(read_mod(base / L"mod/../mod/test.dll", "test", true, "profile", base).status == "skipped");
    check(read_mod(base / L"mod/test.dll:stream", "test", true, "profile", base).status == "skipped");
    if (CreateSymbolicLinkW((base / L"linked").c_str(), (base / L"mod").c_str(), SYMBOLIC_LINK_FLAG_DIRECTORY | 2)) {
        check(read_mod(base / L"linked/test.dll", "test", true, "folder", base).status == "skipped");
        check(read_mod(base / L"linked", "test", true, "profile", base / L"linked").status == "skipped");
        fs::remove(base / L"linked");
    } else std::puts("SKIP symlinks: OS did not permit creating a directory link");
    // File parser bounds and malformed export metadata, without mapping a DLL as executable code.
    std::string pe(0x600, '\0');
    auto u16 = [&](size_t o, uint16_t v) { std::memcpy(pe.data() + o, &v, 2); };
    auto u32 = [&](size_t o, uint32_t v) { std::memcpy(pe.data() + o, &v, 4); };
    pe[0] = 'M'; pe[1] = 'Z'; u32(0x3c, 0x80); u32(0x80, 0x4550);
    u16(0x84, 0x8664); u16(0x86, 1); u16(0x94, 240); u16(0x96, 0x2000);
    u16(0x98, 0x20b); u32(0x98+108, 16); u32(0x98+112, 0x1000); u32(0x98+116, 0x80);
    const size_t sec = 0x188;
    u32(sec+12, 0x1000); u32(sec+16, 0x400); u32(sec+20, 0x200); u32(sec+36, 0x60000020);
    u32(0x214, 1); u32(0x218, 1); u32(0x21c, 0x1090); u32(0x220, 0x1094); u32(0x224, 0x1098);
    u32(0x290, 0x1200); u32(0x294, 0x10a0); u16(0x298, 0);
    pe.replace(0x2a0, 11, "ember_test\0", 11);
    kcompat::write_file_atomic(dll, pe);
    check(kcompat::pe_exports(dll) == std::vector<std::string>{"ember_test"});
    for (size_t n : {size_t(1), size_t(63), size_t(0x80), size_t(0x188), size_t(0x201), size_t(0x500)}) {
        kcompat::write_file_atomic(dll, std::string_view(pe).substr(0, n));
        check(kcompat::pe_exports(dll).empty());
    }
    u16(0x298, 9); kcompat::write_file_atomic(dll, pe); check(kcompat::pe_exports(dll).empty());
    u16(0x298, 0); u32(0x294, UINT32_MAX); kcompat::write_file_atomic(dll, pe); check(kcompat::pe_exports(dll).empty());
    fs::remove_all(base);
    std::printf("Ember contract: %d checks passed\n", checks);
}
