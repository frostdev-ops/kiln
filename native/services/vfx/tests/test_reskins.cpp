// SPDX-License-Identifier: MIT
// boot.cpp's actual reskin functions (included by run_shader.sh), with no Windows, game, or GPU.
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <set>
#include <stdexcept>
#include <vector>
#include "../cook/vfxcook.h"
#include "services/kiln_assets.h"

#define KV_I(...) ((void)0)
#define KV_W(...) ((void)0)
namespace fs = std::filesystem;
using kvb::Bytes;
static std::mutex g_mu;
static KilnApi api{};
static KilnAssetsApi assets{};
static const KilnApi* g_api = &api;
static const KilnAssetsApi* g_assets = &assets;
#include "reskin_impl.inc"

static void require(bool ok, const char* why) { if (!ok) throw std::runtime_error(why); }
static std::map<kvb::Guid, KilnOwner> served;
static std::set<KilnOwner> revoked;
static KilnOwner next_owner = 100;
static bool owner_failure = false;
static KilnOwner KILN_CALL create(KilnStr) { return owner_failure ? 0 : next_owner++; }
static KilnResult KILN_CALL post(KilnOwner o, KilnTaskFn, void*) { return revoked.count(o) ? KILN_ERR_REVOKED : KILN_OK; }
static KilnResult KILN_CALL put(KilnOwner o, const KilnAssetKey* key, const void*, size_t, uint32_t flags) {
    require(flags == KILN_ASSET_PUT_OVERRIDE, "reskins use override mode");
    kvb::Guid g; std::memcpy(g.b, key->guid, 16);
    served[g] = o;
    return KILN_OK;
}
static KilnResult KILN_CALL remove(KilnOwner o, const KilnAssetKey* key) {
    kvb::Guid g; std::memcpy(g.b, key->guid, 16);
    if (served.count(g) && served[g] == o) served.erase(g);
    return KILN_OK;
}
static void tick() { for (int i = 0; i < 60; ++i) reskin_tick(nullptr, nullptr); }

int main(int argc, char** argv) {
    require(argc == 2, "test_reskins OUTDIR");
    api.owner_create = create; api.post = post;
    assets.resource_put = put; assets.resource_remove = remove;
    kvb::Guid game;
    require(kvb::parse_guid("39745562-5d58-4749-bc64-582e5630324a", &game), "GUID");
    for (const char* mod : {"first", "second"}) {
        Reskin r; r.mod = mod; r.guid = game; r.order = g_reskins.size(); r.owner = reskin_owner_locked(mod);
        g_reskins.push_back(r);
        reskin_reconcile_locked(game);
    }
    const auto first = g_reskins[0].owner, second = g_reskins[1].owner;
    require(served[game] == first, "first mod wins, second stands by");
    require(reskin_attach_locked("first", game, 5) && reskin_attach_locked("second", game, 6), "attach both owners");
    revoked.insert(5); tick();
    require(g_reskins[0].released && served[game] == second, "revoking first promotes second");
    require(!reskin_attach_locked("first", game, 5) && served[game] == second, "revoked owner cannot resurrect a reskin");
    require(!reskin_attach_locked("first", game, 0), "zero owner cannot detach lifetime tracking");
    require(reskin_attach_locked("first", game, 7) && served[game] == first, "new owner reclaims first place");
    tick(); require(served[game] == first, "old owner's revocation cannot remove new owner's reskin");
    require(reskin_release_locked("first") == 1 && served[game] == second, "explicit unload promotes standby");
    require(reskin_release_locked("first") == 0, "explicit unload is idempotent");
    require(reskin_release_locked("second") == 1 && served.empty(), "last unload restores original serving");
    require(reskin_owner_locked("first") == first && next_owner == 102, "reloads reuse serving owners");
    owner_failure = true;
    require(!reskin_owner_locked("failed") && !g_reskin_owners.count("failed"), "failed owner allocation is never cached as global owner zero");
    owner_failure = false;

    // A synthetic directory: original material, another type, and nonzero material part. No installed game files are read.
    const fs::path file = fs::path(argv[1]) / "keys.kfc";
    Bytes bytes(0x90 + 3 * 32, 0);
    std::memcpy(bytes.data(), "KFC4", 4);
    uint32_t rel = 0x30, count = 3;
    std::memcpy(bytes.data() + 0x60, &rel, 4); std::memcpy(bytes.data() + 0x64, &count, 4);
    for (int i = 0; i < 3; ++i) {
        auto* key = bytes.data() + 0x90 + i * 32;
        std::memcpy(key, game.b, 16); key[0] += i;
        const uint32_t type = i == 1 ? kvb::kTypeVfx : kvb::kTypeMaterial, part = i == 2 ? 1 : 0;
        std::memcpy(key + 16, &type, 4); std::memcpy(key + 20, &part, 4);
    }
    auto write = [&] { std::ofstream f(file, std::ios::binary); f.write(reinterpret_cast<const char*>(bytes.data()), bytes.size()); };
    std::set<kvb::Guid> keys;
    write(); require(read_game_materials(file, &keys) && keys == std::set{game}, "only original material part zero is accepted");
    bytes.pop_back(); write(); require(!read_game_materials(file, &keys) && keys.empty(), "truncated keys fail closed");
    rel = 0xffffffffu; std::memcpy(bytes.data() + 0x60, &rel, 4);
    write(); require(!read_game_materials(file, &keys) && keys.empty(), "overflowing table offset fails closed");
    fs::remove(file);
    std::puts("reskins: original keys, load order, owner revocation, explicit release, standby and reload passed");
}
