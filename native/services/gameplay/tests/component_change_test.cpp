// SPDX-License-Identifier: MIT
// Exercise the real queue and ownership code without a game or Windows hooks.
#include "../program.cpp"
#include <cstdio>
namespace kg {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 1;
Fns fn;
Features F;
EventIds H;
void logf(int, const char*, ...) {}
}
using namespace kg;
namespace {
bool present = false;
KilnResult queue_result = KILN_OK;
int failures = 0, checks = 0;
void check(bool ok, const char* message) {
    ++checks;
    if (!ok) { ++failures; std::fprintf(stderr, "FAIL %s\n", message); }
}
void apply() { each(nullptr, nullptr, 0, nullptr); }
void reset() { program_world_reset(); present = false; queue_result = KILN_OK; }
}
int main() {
    KilnWorldApi world{};
    world.program_info = [](KilnHandle, KilnProgramInfo* out) -> KilnResult { out->state = KILN_PROGRAM_ACTIVE; return KILN_OK; };
    world.exists = [](KilnEntity) { return true; };
    world.component = [](KilnEntity, uint32_t, uint32_t* size) -> const void* {
        *size = present ? 56 : 0; return present ? &present : nullptr;
    };
    world.queue_add = [](KilnProgramCtx*, KilnEntity, uint32_t, const void*, uint32_t) -> KilnResult {
        if (queue_result == KILN_OK) present = true;
        return queue_result;
    };
    world.queue_remove = [](KilnProgramCtx*, KilnEntity, uint32_t) -> KilnResult {
        if (queue_result == KILN_OK) present = false;
        return queue_result;
    };
    g_world_api = &world; g_active = true;
    const uint32_t hash = kOfferComponents[0];
    uint8_t data[56]{};
    auto add = [&](KilnOwner owner) { return component_change(owner, 42, hash, data, sizeof data, KILN_GAMEPLAY_COMPONENT_ADD); };
    auto remove = [&](KilnOwner owner) { return component_change(owner, 42, hash, nullptr, 0, KILN_GAMEPLAY_COMPONENT_REMOVE); };
    check(add(10) == KILN_OK, "first owner queues an absent component");
    check(add(20) == KILN_ERR_INVALID_ARGUMENT, "pending add cannot be claimed by a second owner");
    check(remove(20) == KILN_ERR_NOT_FOUND, "pending ownership rejects another owner's removal");
    check(remove(10) == KILN_ERR_BUFFER_TOO_SMALL, "remove waits for its pending add");
    apply();
    check(present && remove(20) == KILN_ERR_NOT_FOUND, "second owner cannot remove the first owner's component");
    check(remove(10) == KILN_OK, "first owner retains removal rights after dispatch");
    apply();
    check(!present, "the rightful removal is applied");
    reset();
    check(add(10) == KILN_OK, "queue add before simulated command-buffer failure");
    queue_result = KILN_ERR_BUFFER_TOO_SMALL; apply();
    present = true; // an unrelated game component appeared after the failed add
    check(remove(10) == KILN_ERR_NOT_FOUND, "failed add confers no right to remove a later game component");
    reset();
    check(add(10) == KILN_OK, "queue a fresh component"); apply();
    check(remove(10) == KILN_OK, "queue its removal");
    queue_result = KILN_ERR_BUFFER_TOO_SMALL; apply();
    queue_result = KILN_OK;
    check(present && remove(10) == KILN_OK, "failed removal retains ownership for retry"); apply();
    check(!present, "retried removal succeeds");
    reset();
    std::printf("gameplay component ownership: %s %d checks, %d failures\n", failures ? "FAIL" : "PASS", checks, failures);
    return failures ? 1 : 0;
}
