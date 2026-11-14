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

namespace kq {
// The game's path query calls (chapter 02 section 2.1). False: the call faulted.
bool seh_path_defaults(uintptr_t fn, void* query);
bool seh_path_create(uintptr_t fn, void* command_buffer, uint32_t* id, const void* query);
bool seh_path_result(uintptr_t fn, void* ctx, void* out, uint32_t id, bool* readable);
bool seh_path_destroy(uintptr_t fn, void* command_buffer, uint32_t owner, uint32_t id, bool* queued);
}  // namespace kq
