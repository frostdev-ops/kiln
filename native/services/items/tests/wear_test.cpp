// SPDX-License-Identifier: MIT
// wear.h with no game: the two layouts' offsets (static asserts in the header, and read here from bytes), the
// Durability reader on a made-up container, the approved-build list and the constants check. The bytes below are
// invented; the layout they follow is the header's.
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "../wear.h"

using namespace kitems::wear;

static int g_fail = 0;
#define CHECK(c) do { if (!(c)) { std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); g_fail++; } } while (0)

static Facts approved() {
    Facts f;
    f.spawn_request_local = 0x390; f.spawn_durability_local = 0x458; f.spawn_request_pide = 0x3c;
    f.spawn_iter_size = 0x218; f.spawn_iter_base = 0x280; f.spawn_itemstate_slot = 0x1f0;
    f.world_storage = 0xe179c0; f.storage_kind = 8; f.registry_components = 0x88;
    return f;
}

int main() {
    // WearEnv as the game fills it: the index at +8, the context at +0x10
    uint8_t env[0x28] = {};
    const uint16_t index = 3;
    const uint64_t ctx = 0x1122334455667788ull;
    std::memcpy(env + 0x08, &index, 2);
    std::memcpy(env + 0x10, &ctx, 8);
    WearEnv e;
    std::memcpy(&e, env, sizeof e);
    CHECK(e.durability_index == 3 && reinterpret_cast<uint64_t>(e.ctx) == ctx);

    // a tool's Durability: schema, values at +8, writable, two values (current 172 of 180)
    uint8_t dur[16] = {};
    const uint32_t schema = kAttrDurability;
    const uint16_t offset = 8;
    const int32_t values[2] = {172, 180};
    std::memcpy(dur, &schema, 4);
    std::memcpy(dur + 4, &offset, 2);
    dur[6] = 1;
    dur[7] = 2;
    std::memcpy(dur + 8, values, 8);
    int32_t cur = 0, max = 0;
    CHECK(durability_values(dur, sizeof dur, 0, &cur, &max) && cur == 172 && max == 180);
    CHECK(durability_values(dur, sizeof dur, 1, &cur, &max) && cur == 180 && max == 180);  // the last value has no next
    CHECK(!durability_values(dur, sizeof dur, 2, &cur, &max));                             // index past the count
    CHECK(!durability_values(dur, 11, 0, &cur, &max));                                     // the value is outside the slice
    CHECK(!durability_values(nullptr, 16, 0, &cur, &max) && !durability_values(dur, 4, 0, &cur, &max));
    dur[6] = 0;
    CHECK(!durability_values(dur, sizeof dur, 0, &cur, &max));                             // not writable: the game ignores it too
    dur[6] = 1;
    const uint16_t inside_header = 4;
    std::memcpy(dur + 4, &inside_header, 2);
    CHECK(!durability_values(dur, sizeof dur, 0, &cur, &max));                             // values cannot start inside the header

    // the approved builds: by hash and side, nothing else
    CHECK(approved_build("f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", 0) != nullptr);
    CHECK(approved_build("5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77", 1) != nullptr);
    CHECK(approved_build("f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", 1) == nullptr);  // the server's hash on a client
    CHECK(approved_build("0000000000000000000000000000000000000000000000000000000000000000", 0) == nullptr);
    CHECK(approved_build("", 0) == nullptr && approved_build(nullptr, 0) == nullptr);

    // the constants of the approved frames, and each one moved
    CHECK(check_constants(approved()) == nullptr);
    { auto f = approved(); f.spawn_iter_size = 0x220; CHECK(check_constants(f) != nullptr); }
    { auto f = approved(); f.spawn_itemstate_slot = 0x1f8; CHECK(check_constants(f) != nullptr); }
    { auto f = approved(); f.spawn_request_local = 0x398; CHECK(check_constants(f) != nullptr); }
    { auto f = approved(); f.spawn_durability_local = 0x460; CHECK(check_constants(f) != nullptr); }
    { auto f = approved(); f.spawn_iter_base = 0x288; CHECK(check_constants(f) != nullptr); }
    { auto f = approved(); f.spawn_request_pide = 0x40; CHECK(check_constants(f) != nullptr); }
    { auto f = approved(); f.storage_kind = 0x10; CHECK(check_constants(f) != nullptr); }
    { auto f = approved(); f.world_storage = 0; CHECK(check_constants(f) != nullptr); }
    { auto f = approved(); f.registry_components = 0; CHECK(check_constants(f) != nullptr); }

    std::printf("items-wear-test: %s\n", g_fail ? "FAILED" : "ok");
    return g_fail ? 1 : 0;
}
