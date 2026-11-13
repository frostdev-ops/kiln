// SPDX-License-Identifier: MIT
// kiln.assets: which cooked files a game loads (docs/kiln/modding/cooking.md, "Only what this PC cooked").
//
// A mod's assets/cooked holds files made on each machine from that machine's game by a cook (models, animations, sounds,
// Impact programs). They are no part of the mod (its identity leaves them out), so nothing that brings a mod (an archive,
// a modpack, a server's sync, a copy by hand) vouches for them. Every cook Kiln runs records what it wrote in the game it
// read: <game>/kiln/state/cook-receipts/*.json, each file with its SHA-256 (tools/modelcook/cook/cookpaths.py receipt).
// kiln.assets loads a file of a mod's assets/cooked only when a receipt of this game lists its bytes. kiln.json
// "cooked": "any" (top level, or in the "server"/"client" section) turns that off for development servers.
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

struct CookTrust {
    bool any = false;                         // kiln.json "cooked": "any": every cooked file loads (a note in the log)
    std::unordered_set<std::string> digests;  // SHA-256 (hex) of every file a receipt of this game lists
    int receipts = 0;                         // receipts read
    std::vector<std::string> notes;           // receipts that could not be read, an unknown "cooked" value
};

// Reads <kiln_dir>/kiln.json's "cooked" (then the side's section: "client" when `client`, else "server") and every
// <state_dir>/cook-receipts/*.json (at most 4 MB each). Missing files are no error: no receipt means nothing cooked loads.
CookTrust load_cook_trust(const std::filesystem::path& kiln_dir, const std::filesystem::path& state_dir, bool client);

// Whether these bytes of a cooked file may load: a receipt lists them, or the policy is "any".
bool cooked_trusted(const CookTrust& t, const void* data, size_t len);

}  // namespace ka
