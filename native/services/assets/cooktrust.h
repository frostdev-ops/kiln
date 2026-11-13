// SPDX-License-Identifier: MIT
// kiln.assets: which cooked files a game loads (docs/kiln/modding/cooking.md, "Only what this PC cooked").
//
// A mod's assets/cooked holds files made on each machine from that machine's game by a cook (models, animations, sounds,
// Impact programs). They are no part of the mod (its identity leaves them out), so nothing that brings a mod (an archive,
// a modpack, a server's sync, a copy by hand) vouches for them. Every cook Kiln runs records what it wrote in the game it
// read: <game>/kiln/state/cook-receipts/*.json, each file with its SHA-256 (tools/modelcook/cook/cookpaths.py receipt).
// kiln.json "cooked" says what happens to a cooked file no receipt lists: "local" (it is not loaded), "warn" (it is
// loaded, and kiln.log says once per mod which files and how to cook them), "any" (it is loaded, said once). The default,
// when kiln.json says nothing, is kCookedDefault. A receipt store that is a link (or lies under one) counts as none.
// What the rule is for: shipped, copied or altered cooked files (content a mod's identity does not cover) do not load
// as part of a mod. What it is not: a barrier against code that already runs under the player's account, which can
// write a receipt as well as any file of the game.
// Pure C++ (no Windows API): the host test (tests/test_cooktrust.cpp) runs it as the game does.
#pragma once
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <unordered_set>
#include <vector>

namespace ka {

// SHA-256 of `len` bytes, as 64 lower-case hex digits.
std::string sha256_hex(const void* data, size_t len);

// Whether a manifest path (relative to the mod's assets folder, as assets.json names files: "cooked/x.bin") is in the
// cook's folder: its first component is "cooked" in any case (Windows' file names ignore it).
bool is_cooked_path(const std::string& rel);

enum class CookedPolicy { Local, Warn, Any };

// The policy when kiln.json names none. The owner's decision on how the rule ships is this one line.
constexpr CookedPolicy kCookedDefault = CookedPolicy::Warn;

const char* policy_name(CookedPolicy p);  // "local" | "warn" | "any"

struct CookTrust {
    CookedPolicy policy = kCookedDefault;     // kiln.json "cooked", else kCookedDefault
    std::unordered_set<std::string> digests;  // SHA-256 (hex) of every file a receipt of this game lists
    int receipts = 0;                         // receipts read
    bool store_refused = false;               // the store is a link, or lies under one: no receipt is read from it
    std::vector<std::string> notes;           // receipts that could not be read, an unknown "cooked" value, the store
};

// Reads <kiln_dir>/kiln.json's "cooked" (then the side's section: "client" when `client`, else "server") and every
// <state_dir>/cook-receipts/*.json (at most 4 MB each). Missing files are no error: no receipt means no cooked file is
// this machine's. `store_linked`: the caller found the state folder or the store to be a link (the Windows check by
// name, which also sees Wine's Unix links); a link std::filesystem sees counts too. Either: no receipt is read.
CookTrust load_cook_trust(const std::filesystem::path& kiln_dir, const std::filesystem::path& state_dir, bool client,
                          bool store_linked = false);

// What a cooked file with these bytes gets: Load (a receipt lists them, or the policy is "any"), LoadUnmade (no receipt,
// policy "warn": loaded, and said), Refuse (no receipt, policy "local").
enum class CookedVerdict { Load, LoadUnmade, Refuse };
CookedVerdict cooked_verdict(const CookTrust& t, const void* data, size_t len);

// Whether these bytes of a cooked file may load at all (cooked_verdict is not Refuse).
bool cooked_trusted(const CookTrust& t, const void* data, size_t len);

}  // namespace ka
