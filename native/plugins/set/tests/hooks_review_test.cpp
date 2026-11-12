// SPDX-License-Identifier: MIT
// Exercise the real hook adapter with guarded fake game memory and a fake kiln.hooks table.
#include <cassert>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <unistd.h>
#include "../src/hooks.cpp"
#include "../src/coexist.h"

namespace {
std::vector<std::pair<uintptr_t, size_t>> readable;
std::map<uintptr_t, std::pair<KilnMidFn, void*>> callbacks;
KilnHooksApi hooks{};
KilnResult KILN_CALL read(uintptr_t at, void* dst, size_t n) {
    for (auto [p, len] : readable) if (at >= p && at - p <= len && n <= len - (at - p)) {
        std::memcpy(dst, reinterpret_cast<const void*>(at), n); return KILN_OK;
    }
    return KILN_ERR_INVALID_ARGUMENT;
}
KilnResult KILN_CALL mid(KilnOwner, uintptr_t at, int32_t, KilnMidFn fn, void* user, KilnHandle* out) {
    callbacks[at] = {fn, user}; *out = callbacks.size(); return KILN_OK;
}
const void* KILN_CALL service(KilnStr, uint32_t) { return &hooks; }
KilnResult KILN_CALL symbol(KilnStr, uintptr_t*) { return KILN_ERR_NOT_FOUND; }
void fire(uintptr_t at, KilnRegisters& r) { auto [fn, user] = callbacks.at(at); fn(&r, user); }
}

// Snapshot layout validation is irrelevant to the string capture cases, but linked by subscribe.
bool refl::plain(Type) { return true; }
uint32_t refl::Type::u32(size_t off) const { uint32_t n = 0; if (p) std::memcpy(&n, p + off, 4); return n; }
uint8_t refl::Type::prim() const { return p ? p[0x4c] : 0; }
std::string_view refl::Type::str(size_t) const { return {}; }
bool refl::path(Type, std::string_view, uint32_t&, Type&) { return false; }

int main() {
    hooks.header = {sizeof hooks, 1}; hooks.read = &read; hooks.mid = &mid;
    KilnApi api{}; api.service = &service; api.symbol = &symbol;
    kset::bindHooks(&api, 7);
    std::string err;
    assert(hk::subscribe(0x1000, "invalid", {}, 0, err) < 0);
    assert(hk::subscribe(0x1000, "invalid", std::vector<hk::Arg>(9), 1, err) < 0);
    assert(callbacks.empty());
    std::vector<hk::Arg> args(8, {hk::CStr, {}});
    const int id = hk::subscribe(0x1000, "mod/strings", args, 2, err);
    assert(id > 0);
    assert(hk::subscribe(0x1001, "overlap", {}, 1, err) < 0);
    hk::reserve(0x7000, "runtime");
    assert(hk::subscribe(0x7001, "reserved overlap", {}, 1, err) < 0);
    std::array<std::array<char, 256>, 8> strings;
    for (auto& s : strings) { s.fill('x'); readable.push_back({uintptr_t(s.data()), s.size()}); }
    std::array<uint64_t, 9> stack{};
    readable.push_back({uintptr_t(stack.data()), sizeof stack});
    for (size_t i = 4; i < 8; ++i) stack[5 + i - 4] = uintptr_t(strings[i].data());
    KilnRegisters r{};
    r.rcx = uintptr_t(strings[0].data()); r.rdx = uintptr_t(strings[1].data());
    r.r8 = uintptr_t(strings[2].data()); r.r9 = uintptr_t(strings[3].data()); r.rsp = uintptr_t(stack.data());
    fire(0x1000, r);
    hk::Batch batch;
    assert(hk::drain(id, batch));
    assert(batch.events.size() == 1 && batch.payload.size() == 8 * 256);
    for (size_t i = 0; i < 8; ++i) assert(batch.events[0].v[i].off == i * 256 && batch.events[0].v[i].len == 256);
    const size_t retained = hk::retainedBytes();
    for (int i = 0; i < 100; ++i) {
        hk::unsubscribeAll();
        assert(hk::subscribe(0x1000, "mod/strings", args, 2, err) == id);
        assert(hk::retainedBytes() == retained);
    }
    r.rcx = 1;  // Guard failure must leave no locked metadata mutex, no queued partial event.
    fire(0x1000, r);
    assert(hk::info().front().faulted);
    assert(!hk::diagnostics().empty());
    assert(!hk::drain(id, batch));
    assert(hk::subscribe(0x2000, "mod/healthy", {{hk::U32, {}}}, 1, err) > 0);
    for (int i = 0; i < 100; ++i) {
        const int sub = hk::subscribe(0x3000 + i * 16, "budget", args, 4096, err);
        if (sub < 0) break;
    }
    assert(hk::retainedBytes() <= hk::kMemoryBudget);
    assert(callbacks.size() < 10); // Payload storage must count against the budget.
    for (bool wide : {false, true}) {
        std::string marker;
        for (char c : std::string("Shrouded Engine Tweaks")) { marker += c; if (wide) marker += '\0'; }
        std::istringstream dll(std::string(65530, 'x') + marker + "tail");
        assert(kset::set_marker(dll));
    }
    {  // The shapes the name has in SET's own powrprof.dll: its version resource (UTF-16) and its start line (ASCII).
       // Checked on SET 0.1.1's release file (SHA-256 de2249b9db93f4f3...; `strings`), not on 0.2.0's: that 0.2.0
       // still carries them is assumed. These inputs are built from the name, so they test the scan, not the file.
        std::string resource(4096, '\0');
        for (const char* text : {"FileDescription", "Shrouded Engine Tweaks: Lua mods and an in-game overlay", "ProductVersion", "0.2.0"}) {
            for (const char* c = text; *c; ++c) { resource += *c; resource += '\0'; }
            resource += std::string(2, '\0');
        }
        std::istringstream v020(resource);
        assert(kset::set_marker(v020));
        std::istringstream line(std::string(100, '\x01') + "Shrouded Engine Tweaks %s (%04u-%02u-%02u)");
        assert(kset::set_marker(line));
    }
    // A real SET powrprof.dll, when one is given (third-party files are never in the repo): KILN_SET_POWRPROF=<file>.
    if (const char* real = std::getenv("KILN_SET_POWRPROF"); real && *real) {
        std::ifstream file(real, std::ios::binary);
        assert(file && kset::set_marker(file));
        std::printf("hooks/coexist review: %s carries SET's name\n", real);
    }
    std::istringstream system_dll("Windows power API");
    assert(!kset::set_marker(system_dll));
    assert(kset::power_proxy("/relocated/POWRPROF.DLL", "/system32"));
    assert(!kset::power_proxy("/relocated/normal.dll", "/system32"));
    {  // Issue 23: a powrprof.dll is SET's by SET's markers only, never by its name. Files in TMPDIR.
        namespace fs = std::filesystem;
        const fs::path dir = fs::temp_directory_path() / ("kset-power-" + std::to_string(::getpid()));
        fs::create_directories(dir);
        auto put = [&](const char* name, const std::string& bytes) {
            std::ofstream(dir / name, std::ios::binary) << bytes;
            return dir / name;
        };
        auto wide = [](const char* s) { std::string w; for (; *s; ++s) { w += *s; w += '\0'; } return w; };
        const std::string pe = "MZ" + std::string(4094, '\0') + "PowerGetActiveScheme";
        // A proxy loader as a server's POWRPROF.dll: forwards the system DLL's exports and loads other DLLs.
        const auto forwarding = put("forward.dll", pe + "Forwarding exports to %s\\powrprof.dll" + wide("mods\\*.dll"));
        assert(kcompat::power_proxy_owner(forwarding) == kcompat::PowerProxy::other);
        assert(kcompat::power_proxy_tool(forwarding).empty());
        // SET's name in its version resource (UTF-16), its start line, its folder, its Vulkan layer: each one is SET.
        for (const std::string& marker : {wide("Shrouded Engine Tweaks: Lua mods and an in-game overlay"),
                                          std::string("Shrouded Engine Tweaks %s (%04u-%02u-%02u)"),
                                          wide("shrouded_engine_tweaks\\plugins"), std::string("VK_LAYER_SHROUDED_ENGINE_TWEAKS")}) {
            const auto set = put("set.dll", pe + std::string(70000, 'x') + marker);
            assert(kcompat::power_proxy_owner(set) == kcompat::PowerProxy::set);
            assert(kcompat::power_proxy_tool(set) == "Shrouded Engine Tweaks");
        }
        // The folder name alone does not make a renamed module SET (kset::set_marker, the scan of every module).
        std::istringstream folder_only("shrouded_engine_tweaks\\mods");
        assert(!kset::set_marker(folder_only));
        assert(kcompat::power_proxy_tool(put("xp.dll", pe + "xpshare.ini")) == "Shared XP");
        assert(kcompat::power_proxy_owner(dir / "missing.dll") == kcompat::PowerProxy::unreadable);
        if (const char* real = std::getenv("KILN_SET_POWRPROF"); real && *real)
            assert(kcompat::power_proxy_owner(real) == kcompat::PowerProxy::set);
        fs::remove_all(dir);
    }
    std::puts("hooks/coexist review: PASS");
}
