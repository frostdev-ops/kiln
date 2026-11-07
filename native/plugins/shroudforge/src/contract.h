// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <string_view>
namespace ksf {
inline bool supported_version(std::string_view version) { return version == "1.7.8" || version == "1.7.9"; }
inline bool supported_abi(uint32_t abi) { return abi == 12; }
} // namespace ksf
