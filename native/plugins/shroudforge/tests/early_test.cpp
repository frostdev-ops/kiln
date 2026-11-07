// SPDX-License-Identifier: MIT
#include "../../../core/src/early_plugins.h"
#include <cassert>
#include <algorithm>
#include "../src/contract.h"
#include <vector>
int main() {
    using kiln::early_plugins::exports_early;
    std::vector<std::string_view> order{"kiln-emf.dll", "KILN-EML.DLL", "kiln-shroudforge.dll"};
    std::stable_sort(order.begin(), order.end(), kiln::early_plugins::provider_before);
    assert(order[0] == "KILN-EML.DLL" && order[1] == "kiln-emf.dll");
    assert(ksf::supported_version("1.7.8") && ksf::supported_version("1.7.9"));
    assert(!ksf::supported_version("") && !ksf::supported_version("1.8.0"));
    assert(ksf::supported_abi(12) && !ksf::supported_abi(11));
    std::vector<uint8_t> b(1024);
    auto w = [&](size_t at, uint32_t v, size_t n = 4) { std::memcpy(b.data() + at, &v, n); };
    w(0, 0x5a4d, 2); w(0x3c, 64); w(64, 0x4550); w(68, 0x8664, 2); w(70, 1, 2);
    w(84, 240, 2); w(88, 0x20b, 2); w(196, 16); w(200, 0x1000); w(204, 128);
    w(340, 0x1000); w(344, 512); w(348, 512);
    w(532, 1); w(536, 1); w(540, 0x1040); w(544, 0x1044); w(548, 0x1048);
    w(576, 0x1100); w(580, 0x1050); w(584, 0, 2);
    std::memcpy(b.data() + 592, "kiln_plugin_early", 18);
    assert(exports_early(b.data(), b.size()));
    for (size_t n = 0; n < 610; ++n) assert(!exports_early(b.data(), n));
    w(200, 0); assert(!exports_early(b.data(), b.size())); // marker in a DLL is insufficient
    w(200, 0x1000); w(576, 0x1050); assert(!exports_early(b.data(), b.size())); // forwarded export
    w(536, 0xffffffff); assert(!exports_early(b.data(), b.size()));
}
