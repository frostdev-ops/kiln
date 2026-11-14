// SPDX-License-Identifier: MIT
// No frame here holds a C++ object with a destructor, as __try requires.
#include <windows.h>

#include <cstring>

#include "seh.h"

namespace kq {

bool seh_copy(uintptr_t address, void* out, size_t n) {
    if (address < 0x10000) return false;
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(address), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_ray(uintptr_t fn, void* hit, uintptr_t grid, const float* from, const float* to, const uint8_t* options, unsigned long* code) {
    // void* VoxelRay(void* hit, void* voxelGrid, const float from[4], const float to[4], const uint8_t options[4])
    using Fn = void* (*)(void*, void*, const float*, const float*, const uint8_t*);
    __try {
        reinterpret_cast<Fn>(fn)(hit, reinterpret_cast<void*>(grid), from, to, options);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kq
