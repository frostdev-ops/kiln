// SPDX-License-Identifier: MIT
// SEH-guarded calls for kiln.client.game_input (client_game_input_service.cpp). Kept in their own translation unit, as
// kiln.hooks' are (hooks_seh.cpp): a function using __try/__except holds no C++ object with a destructor. A fault in a
// subscriber, a repeat condition or the game's GetComponent returns false instead of taking the game down.
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include "services/kiln_client_game_input.h"

namespace kiln::game_input_seh {

using GetComponentFn = void (*)(void* out, uintptr_t world, uint32_t hash, uint32_t entity);

bool call(KilnGameInputFn fn, KilnGameInputSample* sample, void* user, unsigned long* code) {
    __try {
        fn(sample, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool call_cond(KilnGameInputCondFn fn, const KilnGameInputSample* sample, void* user, bool* out, unsigned long* code) {
    __try {
        *out = fn(sample, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// The game's GetComponent(out {ptr, size}, world, type hash, entity): the component's address in *out (0: none).
bool get_component(void* fn, uintptr_t world, uint32_t hash, uint32_t entity, uintptr_t* out) {
    struct Slice {
        const void* ptr;
        uint64_t size;
    } s = {nullptr, 0};
    __try {
        reinterpret_cast<GetComponentFn>(fn)(&s, world, hash, entity);
        *out = reinterpret_cast<uintptr_t>(s.ptr);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kiln::game_input_seh
