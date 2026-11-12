// SPDX-License-Identifier: MIT
// Exercise the production chain loader, including its on-disk export checks, without a game.
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include "../dinput8.cpp"

static unsigned checks = 0;
#define CHECK(test) do { ++checks; if (!(test)) { std::fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #test); return 1; } } while (0)

int main(int argc, char** argv) {
    if (argc != 4) return 2; // scratch folder, addon DLL, ordinary proxy DLL
    namespace fs = std::filesystem;
    const auto root = fs::absolute(argv[1]);
    fs::create_directories(root);
    g_capacity = 32;
    g_chain = static_cast<KilnChainEntry*>(halloc(g_capacity * sizeof(KilnChainEntry)));
    g_canon = static_cast<const wchar_t**>(halloc(g_capacity * sizeof(wchar_t*)));
    g_game_canon = canonical(root.c_str());
    Late late{};
    g_late = &late;
    g_late_count = 1;
    // Identity is by exports, not file name or chain slot. Cover every chain source and the second install slot.
    for (uint32_t source : {KILN_CHAIN_SOURCE_CHAIN, KILN_CHAIN_SOURCE_PROFILE, KILN_CHAIN_SOURCE_EMF}) {
        const auto name = L"renamed-addon-" + std::to_wstring(source) + L"-2.dll";
        const auto file = root / name;
        fs::copy_file(fs::absolute(argv[2]), file, fs::copy_options::overwrite_existing);
        late = {nullptr, file.c_str(), canonical(file.c_str()), false};
        const uint32_t at = g_count;
        chain_one(source, root.c_str(), name.c_str(), kAccept, false);
        CHECK(g_chain[at].status == KILN_CHAIN_LOADED);
        CHECK(g_chain[at].module != nullptr && late.used);
        chain_one(source, root.c_str(), name.c_str(), kAccept, false);
        CHECK(g_chain[at + 1].status == KILN_CHAIN_DUPLICATE);
        CHECK(g_chain[at + 1].module == g_chain[at].module);
    }
    // A DirectInput proxy without shroudlink_shutdown must still obey [late].
    const auto plain = root / L"plain.dll";
    fs::copy_file(fs::absolute(argv[3]), plain, fs::copy_options::overwrite_existing);
    late = {nullptr, plain.c_str(), canonical(plain.c_str()), false};
    const uint32_t at = g_count;
    chain_one(KILN_CHAIN_SOURCE_CHAIN, root.c_str(), L"plain.dll", kAccept, false);
    CHECK(g_chain[at].status == KILN_CHAIN_LATE && !g_chain[at].module);
    CHECK(late.used);
    // Do not override play-once no-mods or a corrupt profile's skip decision.
    chain_one(KILN_CHAIN_SOURCE_PROFILE, root.c_str(), L"plain.dll", kAccept, true);
    CHECK(g_chain[g_count - 1].status == KILN_CHAIN_SKIPPED);
    // Issue 23: a chain.ini put in kiln\chain\ is not read. proxy.log says where it belongs, and only while
    // kiln\chain.ini does not exist.
    {
        const auto game = root / L"misplaced";
        fs::create_directories(game / L"kiln" / L"chain");
        std::ofstream(game / L"kiln" / L"chain" / L"chain.ini") << "[chain]\nloader.dll\n";
        lstrcpynW(g_game, game.c_str(), kPath);
        lstrcpynW(g_kiln, (game / L"kiln").c_str(), kPath);
        auto start = [&] {
            open_log();
            load_chain(false, false);
            CloseHandle(g_log);
            g_log = INVALID_HANDLE_VALUE;
            std::ifstream in(game / L"kiln" / L"logs" / L"proxy.log", std::ios::binary);
            return std::string(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
        };
        const std::string hint = "\\kiln\\chain\\chain.ini is not read: the chain file belongs in ";
        std::string log = start();
        std::printf("misplaced chain.ini: %s", log.c_str());
        CHECK(log.find(hint) != std::string::npos);
        CHECK(log.find("\\kiln\\chain.ini (one folder up") != std::string::npos);
        std::ofstream(game / L"kiln" / L"chain.ini") << "[chain]\n";
        log = start();
        CHECK(log.find(hint) == std::string::npos);
    }
    std::printf("proxy chain: %u checks passed\n", checks);
    return 0;
}
