// SPDX-License-Identifier: MIT
// A mod's own particle module (ShaderBlock, level D): the checks it has to pass before the game can see it, and the ABI stamp that
// `kiln-vfxcook shader build` writes into it. Guide: docs/kiln/modding/shaders.md. Feasibility and evidence: docs/re/shaders.md.
//
// A module that gets past these can still draw nonsense or waste GPU time, but it can't reach a binding the kit doesn't declare, use a
// capability the game's own modules don't, or be built against another parameter layout. Anything that fails here falls back to Kiln's
// own sprite module, so the effect still shows and the game never compiles the module.
#include <cstring>
#include <map>
#include <set>

#include "../shaders/kiln_vfx_params.h"
#include "vfxcook.h"

namespace kvb {
namespace {

constexpr uint32_t kMagic = 0x07230203;
constexpr uint32_t kOpSourceExtension = 4, kOpExtension = 10, kOpExtInstImport = 11, kOpMemoryModel = 14, kOpEntryPoint = 15,
                   kOpExecutionMode = 16, kOpCapability = 17, kOpVariable = 59, kOpDecorate = 71, kOpExecutionModeId = 331;
constexpr uint32_t kDecorationBinding = 33, kDecorationDescriptorSet = 34;
// Storage classes
constexpr uint32_t kUniformConstant = 0, kUniform = 2, kPushConstant = 9, kStorageBuffer = 12, kPhysicalStorageBuffer = 5349;

uint32_t word_at(const uint8_t* d, size_t i) {
    uint32_t v;
    std::memcpy(&v, d + i * 4, 4);
    return v;
}

std::string literal(const uint8_t* d, size_t first_word, size_t words) {
    const char* s = reinterpret_cast<const char*>(d + first_word * 4);
    return std::string(s, strnlen(s, words * 4));
}

}  // namespace

std::string shader_abi_marker() { return "kiln-vfx-abi-" + std::to_string(KILN_VFX_ABI); }

// (set, binding) pairs kiln_vfx.hlsl declares: the counter buffer of set 0 and these of VfxUpdateDescriptorSetLayout (set 1). The kit's
// helpers only reach these, and a module that declares more is reaching past what Kiln has checked against the game.
const std::set<std::pair<uint32_t, uint32_t>>& shader_allowed_bindings() {
    static const std::set<std::pair<uint32_t, uint32_t>> s = {{0, 2},  {1, 0},  {1, 1},  {1, 2},  {1, 3},  {1, 4},  {1, 9},
                                                              {1, 10}, {1, 13}, {1, 17}, {1, 39}, {1, 40}, {1, 41}, {1, 42}};
    return s;
}

std::string check_mod_module(const uint8_t* data, size_t size, ModModuleInfo* info) {
    ModModuleInfo local;
    ModModuleInfo& out = info ? *info : local;
    out = ModModuleInfo{};
    out.size = size;
    if (size > kMaxModModuleBytes) return "is " + std::to_string(size) + " bytes, more than " + std::to_string(kMaxModModuleBytes);
    const std::string shape = check_module(data, size);
    if (!shape.empty()) return shape;
    const size_t n = size / 4;
    std::map<uint32_t, uint32_t> set_of, binding_of;
    struct Var {
        uint32_t id, storage;
    };
    std::vector<Var> vars;
    for (size_t i = 5; i < n;) {
        const uint32_t w = word_at(data, i), op = w & 0xffff, wc = w >> 16;
        if (op == kOpSourceExtension && wc >= 2) {
            const std::string s = literal(data, i + 1, wc - 1);
            if (s.rfind("kiln-vfx-abi-", 0) == 0) out.abi = s.substr(13);
        } else if (op == kOpDecorate && wc >= 4) {
            if (word_at(data, i + 2) == kDecorationDescriptorSet) set_of[word_at(data, i + 1)] = word_at(data, i + 3);
            if (word_at(data, i + 2) == kDecorationBinding) binding_of[word_at(data, i + 1)] = word_at(data, i + 3);
        } else if (op == kOpVariable && wc >= 4) {
            vars.push_back({word_at(data, i + 2), word_at(data, i + 3)});
        }
        i += wc;
    }
    if (out.abi.empty()) return "has no Kiln ABI stamp: build it with kiln-vfxcook shader build";
    if (out.abi != std::to_string(KILN_VFX_ABI))
        return "was built for particle ABI " + out.abi + ", and this Kiln has ABI " + std::to_string(KILN_VFX_ABI) + ": rebuild it with this Kiln's kiln-vfxcook shader build";
    for (const Var& v : vars) {
        if (v.storage == kPhysicalStorageBuffer) return "uses buffer device addresses, which the game's modules don't";
        const bool resource = v.storage == kUniform || v.storage == kStorageBuffer || v.storage == kUniformConstant;
        if (!resource) continue;
        const auto s = set_of.find(v.id), b = binding_of.find(v.id);
        if (s == set_of.end() || b == binding_of.end()) return "has a resource without a descriptor set and binding";
        if (!shader_allowed_bindings().count({s->second, b->second}))
            return "uses set " + std::to_string(s->second) + " binding " + std::to_string(b->second) + ", which the kit (kiln_vfx.hlsl) doesn't declare";
        out.bindings.push_back({s->second, b->second});
    }
    (void)kPushConstant;
    return "";
}

bool stamp_module(Bytes* spv, std::string* why) {
    if (spv->size() < 20 || spv->size() % 4 || word_at(spv->data(), 0) != kMagic) {
        *why = "not SPIR-V";
        return false;
    }
    std::vector<uint32_t> words(spv->size() / 4);
    std::memcpy(words.data(), spv->data(), spv->size());
    // Drop an old stamp, then put one at the start of the debug section: after capabilities, extensions, imports, the memory model, entry
    // points and execution modes (the order SPIR-V 2.4 requires).
    std::vector<uint32_t> kept(words.begin(), words.begin() + 5);
    size_t insert_at = 0;
    bool head = true;
    for (size_t i = 5; i < words.size();) {
        const uint32_t op = words[i] & 0xffff, wc = words[i] >> 16;
        if (wc == 0 || i + wc > words.size()) {
            *why = "a broken instruction";
            return false;
        }
        const bool stamp = op == kOpSourceExtension &&
                           literal(reinterpret_cast<const uint8_t*>(words.data()), i + 1, wc - 1).rfind("kiln-vfx-abi-", 0) == 0;
        if (head && !(op == kOpCapability || op == kOpExtension || op == kOpExtInstImport || op == kOpMemoryModel || op == kOpEntryPoint ||
                      op == kOpExecutionMode || op == kOpExecutionModeId)) {
            head = false;
            insert_at = kept.size();
        }
        if (!stamp) kept.insert(kept.end(), words.begin() + i, words.begin() + i + wc);
        i += wc;
    }
    if (head) insert_at = kept.size();
    const std::string marker = shader_abi_marker();
    std::vector<uint32_t> ins(1 + (marker.size() + 4) / 4, 0);
    std::memcpy(&ins[1], marker.data(), marker.size());
    ins[0] = static_cast<uint32_t>(ins.size()) << 16 | kOpSourceExtension;
    kept.insert(kept.begin() + static_cast<std::ptrdiff_t>(insert_at), ins.begin(), ins.end());
    spv->resize(kept.size() * 4);
    std::memcpy(spv->data(), kept.data(), spv->size());
    return true;
}

}  // namespace kvb
