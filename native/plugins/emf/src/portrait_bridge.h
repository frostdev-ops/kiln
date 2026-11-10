// SPDX-License-Identifier: MIT
#pragma once
#include <cstddef>
#include <cstdint>
namespace kemf {
using PortraitDrawFn = int32_t (*)(const uint8_t *, size_t, char *, size_t, size_t *);
#ifdef KEMF_PORTRAIT_TEST
void portraits_test_export(PortraitDrawFn fn);
void portraits_test_join(); // waits for a drawing job started by portraits_on_ready
void portraits_test_wait_ms(int ms); // shortens the 120 s a waiting image_load is given
#endif
} // namespace kemf
