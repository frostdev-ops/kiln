// SPDX-License-Identifier: MIT
// Kiln's starter modules, embedded as committed: native/services/vfx/shaders/build/*.spv. Building Kiln needs no shader compiler.
#include <cstring>

#include "vfxcook.h"
#include "../shaders/kiln_vfx_params.h"

#if defined(__clang__)
#pragma clang diagnostic ignored "-Wc23-extensions"
#endif

namespace kvb {
namespace {

const uint8_t kEmitter[] = {
#embed "../shaders/build/kiln_emitter.spv"
};
const uint8_t kSprite[] = {
#embed "../shaders/build/kiln_sprite.spv"
};
const uint8_t kLight[] = {
#embed "../shaders/build/kiln_light.spv"
};

// SPIR-V words
constexpr uint32_t kMagic = 0x07230203;
constexpr uint32_t kOpExtension = 10, kOpEntryPoint = 15, kOpExecutionMode = 16, kOpCapability = 17;
constexpr uint32_t kExecLocalSize = 17, kGlCompute = 5;

}  // namespace

const ModuleInfo& starter_module(Module m) {
    static const ModuleInfo infos[3] = {
        {"kiln_emitter", kEmitter, sizeof kEmitter, content_hash(kEmitter, sizeof kEmitter)},
        {"kiln_sprite", kSprite, sizeof kSprite, content_hash(kSprite, sizeof kSprite)},
        {"kiln_light", kLight, sizeof kLight, content_hash(kLight, sizeof kLight)},
    };
    return infos[static_cast<int>(m)];
}

std::string check_module(const uint8_t* data, size_t size) {
    if (size < 20 || size % 4) return "not whole SPIR-V words";
    const size_t n = size / 4;
    auto word = [&](size_t i) {
        uint32_t v;
        std::memcpy(&v, data + i * 4, 4);
        return v;
    };
    if (word(0) != kMagic) return "no SPIR-V magic";
    const uint32_t version = word(1);
    if (version > 0x00010500u) return "SPIR-V newer than 1.5";
    bool entry = false, local = false;
    uint32_t entry_id = 0;
    for (size_t i = 5; i < n;) {
        const uint32_t w = word(i), op = w & 0xffff, wc = w >> 16;
        if (wc == 0 || i + wc > n) return "a broken instruction";
        if (op == kOpCapability) {
            const uint32_t c = word(i + 1);
            // Shader, GroupNonUniform, GroupNonUniformArithmetic, SignedZeroInfNanPreserve: all that the game's 2,921 modules use
            if (c != 1 && c != 61 && c != 63 && c != 4466) return "capability " + std::to_string(c) + " is outside what the game's modules use";
        } else if (op == kOpExtension) {
            const char* s = reinterpret_cast<const char*>(data + (i + 1) * 4);
            const size_t cap = (wc - 1) * 4;
            if (strnlen(s, cap) != std::strlen("SPV_KHR_float_controls") || std::strncmp(s, "SPV_KHR_float_controls", cap) != 0)
                return "an extension outside what the game's modules use";
        } else if (op == kOpEntryPoint) {
            if (wc < 4 || word(i + 1) != kGlCompute) return "the entry point is not a compute shader";
            const char* s = reinterpret_cast<const char*>(data + (i + 3) * 4);
            if (strnlen(s, (wc - 3) * 4) != 7 || std::memcmp(s, "cs_main", 7) != 0) return "the entry point is not cs_main";
            if (entry) return "more than one entry point";
            entry = true;
            entry_id = word(i + 2);
        } else if (op == kOpExecutionMode) {
            if (wc >= 6 && word(i + 2) == kExecLocalSize) {
                if (word(i + 1) == entry_id && word(i + 3) == 32 && word(i + 4) == 1 && word(i + 5) == 1) local = true;
                else return "LocalSize is not 32 1 1";
            }
        }
        i += wc;
    }
    if (!entry) return "no entry point";
    if (!local) return "no LocalSize";
    return "";
}

}  // namespace kvb
