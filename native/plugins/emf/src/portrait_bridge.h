// SPDX-License-Identifier: MIT
#pragma once
#include <cstddef>
#include <cstdint>
namespace kemf {
using PortraitDrawFn = int32_t (*)(const uint8_t *, size_t, char *, size_t, size_t *);
#ifdef KEMF_PORTRAIT_TEST
void portraits_test_export(PortraitDrawFn fn);
#endif
} // namespace kemf
