// SPDX-License-Identifier: MIT
#ifndef KILN_QUERIES_SEH_H
#define KILN_QUERIES_SEH_H
#include <cstddef>
#include <cstdint>

namespace kq {
// Structured-exception guards around every read of game memory and the one call into the game. False: it faulted.
bool seh_copy(uintptr_t address, void* out, size_t n);
bool seh_ray(uintptr_t fn, void* hit, uintptr_t grid, const float* from, const float* to, const uint8_t* options, unsigned long* code);
}  // namespace kq
#endif
