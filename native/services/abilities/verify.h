// SPDX-License-Identifier: MIT
// kiln.abilities: verifier for keen::ImpactProgram blobs before they enter the ImpactRegistry. The rules are the
// program/native contract in docs/kiln/services/abilities.md (agreed with tools/impact).
#pragma once
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace kab {

struct VerifyResult {
    bool ok = false;
    std::string error;                    // why it was refused (empty when ok)
    std::vector<uint32_t> unknown_natives; // ECALL hashes nobody registered or declared (the program can wait)
    int max_depth = 0;                    // over code and shutdown
    uint32_t id = 0;
    uint8_t guid[16]{};
};

// argc_of(hash) -> argc of a known native, or -1 when unknown. Pure function of the bytes (thread-safe).
VerifyResult verify_program(const uint8_t* blob, size_t len, const std::function<int(uint32_t)>& argc_of);

}  // namespace kab
