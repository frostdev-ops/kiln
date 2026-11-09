// SPDX-License-Identifier: MIT
// Stand-in process: verifies the actual Windows GPL portrait export without starting a game executable.
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <fstream>
#include <string>
#include <vector>
int main(int argc, char **argv) {
    if (argc != 3)
        return 2;
    const HMODULE h = LoadLibraryExA(argv[1], nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!h) {
        std::printf("FAIL LoadLibrary: %lu\n", GetLastError());
        return 1;
    }
    if(GetModuleHandleA("kiln-eml.dll")!=h){std::puts("FAIL portrait DLL module lookup");return 1;}
    using Draw = int32_t (*)(const uint8_t *, size_t, char *, size_t, size_t *);
    const auto draw = reinterpret_cast<Draw>(GetProcAddress(h, "kiln_eml_portraits"));
    if (!draw) {
        std::puts("FAIL portrait export missing");
        return 1;
    }
    std::ifstream f(argv[2], std::ios::binary);
    std::string request{std::istreambuf_iterator<char>(f), {}};
    if (request.empty() || request.size() > 256 * 1024)
        return 2;
    for (int i = 0; i < 2; ++i) {
        std::vector<char> reply(1u << 20);
        size_t written = 0;
        const int32_t rc = draw(reinterpret_cast<const uint8_t *>(request.data()), request.size(), reply.data(),
                                reply.size(), &written);
        if (rc || !written || written > reply.size() || reply[written - 1] != '\0') {
            std::printf("FAIL ABI result %d/%zu\n", rc, written);
            return 1;
        }
        const std::string text(reply.data(), written - 1);
        std::puts(text.c_str());
        if (text.find("\"code\":0") == std::string::npos ||
            text.find(i ? "\"status\":\"current\"" : "\"status\":\"drawn\"") == std::string::npos)
            return 1;
    }
    std::puts("PASS Windows portrait ABI: real archive draw + cache reuse");
    return 0;
}
