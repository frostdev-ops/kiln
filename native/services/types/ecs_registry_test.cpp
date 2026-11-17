// SPDX-License-Identifier: MIT
// T8 (#53) host test: kiln.types' ecs_registry builder over a fake catalog source.
#include <cstdio>
#include <cstring>

#include "ecs_registry.h"

namespace {
int checks = 0, failures = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) { ++failures; std::fprintf(stderr, "FAIL %s\n", what); }
}
struct Fake {
    uint64_t stamp = 42;
    uint32_t flip_after = 0xFFFFFFFFu; // row index after which the catalog is replaced
    uint32_t fail_at = 0xFFFFFFFFu;
    bool vanish = false;
};
uint32_t count_of(void*) { return 3; }
uint64_t stamp_of(void* u) { return static_cast<Fake*>(u)->stamp; }
bool row_of(void* u, uint32_t i, KilnEcsRegistryRow* out) {
    auto& F = *static_cast<Fake*>(u);
    if (F.vanish || i == F.fail_at) return false;
    *out = KilnEcsRegistryRow{};
    out->size = sizeof *out;
    out->index = i;
    out->component = 1000 + i;
    out->type_size = 8 + i;
    out->type_identity = 5000 + i;
    if (i == F.flip_after) F.stamp = 43; // the registry is replaced while it is being read
    return true;
}
} // namespace

int main() {
    Fake F;
    ktypes::ecs::Source src{&F, &count_of, &stamp_of, &row_of};
    KilnEcsRegistryInfo info{}; info.size = sizeof info;
    KilnEcsRegistryRow rows[3] = {}; uint32_t req = 0;
    check(ktypes::ecs::build(src, KILN_WORLD_SERVER, &info, rows, 3, &req) == KILN_OK && req == 3 &&
              info.count == 3 && info.layout_stamp == 42 && rows[1].component == 1001 && rows[1].type_identity == 5001 &&
              !(rows[1].flags & KILN_ECS_REGISTRY_TRACKING_KNOWN),
          "registry: copied rows, tracking unknown until validated");
    std::memset(rows, 0xAA, sizeof rows);
    check(ktypes::ecs::build(src, KILN_WORLD_SERVER, &info, rows, 2, &req) == KILN_ERR_BUFFER_TOO_SMALL && req == 3 &&
              (uint8_t)rows[0].component == 0xAA,
          "registry short buffer: required, nothing copied");
    F.flip_after = 1;
    std::memset(rows, 0xAA, sizeof rows);
    check(ktypes::ecs::build(src, KILN_WORLD_SERVER, &info, rows, 3, &req) == KILN_ECS_ERR_UNREADABLE &&
              info.status == KILN_ECS_UNREADABLE && rows[0].component == 0 && rows[2].component == 0,
          "registry replaced mid-read: unreadable, no partial catalog");
    F.flip_after = 0xFFFFFFFFu; F.stamp = 42; F.fail_at = 1;
    std::memset(rows, 0xAA, sizeof rows);
    check(ktypes::ecs::build(src, KILN_WORLD_SERVER, &info, rows, 3, &req) == KILN_ECS_ERR_UNREADABLE &&
              info.count == 0 && rows[0].component == 0 && req == 0,
          "registry row vanished after the first: the prefix is wiped");
    F.fail_at = 0xFFFFFFFFu; F.vanish = true;
    check(ktypes::ecs::build(src, KILN_WORLD_SERVER, &info, rows, 3, &req) == KILN_ECS_ERR_UNREADABLE,
          "registry row vanished mid-read: unreadable");
    std::printf("ecs_registry: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
