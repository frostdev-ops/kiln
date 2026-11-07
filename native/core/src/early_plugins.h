// SPDX-License-Identifier: MIT
// Early plugins (kiln/plugins/kiln-*.dll exporting kiln_plugin_early): run after the native mods of phase "early",
// before sigdb and hooks, in safe idle too. See early_plugins.cpp.
#pragma once

namespace kiln::early_plugins {

void run();

}  // namespace kiln::early_plugins

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>
namespace kiln::early_plugins {
inline bool provider_before(std::string_view a, std::string_view b) {
    const auto eml = [](std::string_view name) {
        constexpr std::string_view wanted = "kiln-eml.dll";
        if (name.size() != wanted.size()) return false;
        for (size_t i = 0; i < name.size(); ++i) {
            char c = name[i];
            if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
            if (c != wanted[i]) return false;
        }
        return true;
    };
    return eml(a) && !eml(b);
}
// Inspect the PE export directory, not arbitrary strings (including imports and debug data).
inline bool exports_early(const uint8_t* bytes, size_t size) {
    auto read = [&](size_t at, size_t n) -> uint32_t {
        uint32_t v = 0;
        if (at > size || n > size - at) return 0;
        std::memcpy(&v, bytes + at, n);
        return v;
    };
    if (!bytes || size < 64 || read(0, 2) != 0x5a4d) return false;
    const size_t nt = read(0x3c, 4);
    if (nt > size || size - nt < 144 || read(nt, 4) != 0x4550 || read(nt + 4, 2) != 0x8664) return false;
    const size_t sections = read(nt + 6, 2), opt_size = read(nt + 20, 2), opt = nt + 24;
    if (opt_size < 120 || opt_size > size - opt || read(opt, 2) != 0x20b || read(opt + 108, 4) == 0) return false;
    const size_t table = opt + opt_size;
    if (!sections || sections > 96 || sections > (size - table) / 40) return false;
    auto offset = [&](uint32_t rva, size_t n) -> size_t {
        for (size_t i = 0; i < sections; ++i) {
            const size_t s = table + i * 40;
            const uint32_t va = read(s + 12, 4), raw_size = read(s + 16, 4), raw = read(s + 20, 4);
            if (rva < va || rva - va > raw_size || n > raw_size - (rva - va)) continue;
            const uint64_t at = uint64_t(raw) + rva - va;
            if (at <= size && n <= size - at) return static_cast<size_t>(at);
        }
        return size;
    };
    const uint32_t export_rva = read(opt + 112, 4), export_size = read(opt + 116, 4);
    if (!export_rva || export_size < 40) return false;
    const size_t exp = offset(export_rva, 40);
    if (exp == size) return false;
    const size_t count = read(exp + 24, 4), functions = read(exp + 20, 4);
    if (count > 65536 || functions > 65536) return false;
    const size_t names = offset(read(exp + 32, 4), count * 4), ord = offset(read(exp + 36, 4), count * 2);
    const size_t funcs = offset(read(exp + 28, 4), functions * 4);
    if (names == size || ord == size || funcs == size) return false;
    constexpr char symbol[] = "kiln_plugin_early";
    for (size_t i = 0; i < count; ++i) {
        const size_t name = offset(read(names + i * 4, 4), sizeof symbol);
        if (name == size || std::memcmp(bytes + name, symbol, sizeof symbol)) continue;
        const uint32_t index = read(ord + i * 2, 2);
        if (index >= functions) return false;
        const uint32_t fn = read(funcs + index * 4, 4);
        // Forwarders would load some other DLL at the early phase. Only local code exports qualify.
        return !(fn >= export_rva && uint64_t(fn) < uint64_t(export_rva) + export_size) && offset(fn, 1) != size;
    }
    return false;
}
} // namespace kiln::early_plugins
