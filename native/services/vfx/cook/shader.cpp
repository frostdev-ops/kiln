// SPDX-License-Identifier: MIT
// A mod's own particle hooks (ShaderBlock, level D): reading the file and the hook ABI stamp that `kiln-vfxcook shader build` writes. The
// safety of what is served comes from cook/spvlink.cpp (verify + link into Kiln's sprite), never from the stamp: a stamp only lets an old
// build say "rebuild me" clearly. Guide: docs/kiln/modding/shaders.md. Evidence: docs/re/shaders.md.
#include <cstring>
#include <filesystem>
#include <fstream>

#include "../shaders/kiln_vfx_params.h"
#include "vfxcook.h"

namespace kvb {
namespace {

constexpr uint32_t kOpSourceExtension = 4, kOpExtension = 10, kOpExtInstImport = 11, kOpMemoryModel = 14, kOpEntryPoint = 15,
                   kOpExecutionMode = 16, kOpCapability = 17, kOpExecutionModeId = 331;

std::string literal(const uint8_t* d, size_t first_word, size_t words) {
    const char* s = reinterpret_cast<const char*>(d + first_word * 4);
    return std::string(s, strnlen(s, words * 4));
}

}  // namespace

bool read_mod_module(const std::string& path, Bytes* out, std::string* why) {
    out->clear();
    const std::filesystem::path file(std::u8string(reinterpret_cast<const char8_t*>(path.data()), path.size()));
    std::error_code ec;
    if (!std::filesystem::is_regular_file(file, ec)) { *why = "not a regular module file"; return false; }
    std::ifstream f(file, std::ios::binary | std::ios::ate);
    if (!f) { *why = "can't open it"; return false; }
    const auto size = f.tellg();
    if (size < 0 || static_cast<uint64_t>(size) > kMaxModModuleBytes) {
        *why = "module exceeds the size limit";
        return false;
    }
    out->resize(static_cast<size_t>(size));
    f.seekg(0);
    if (!f.read(reinterpret_cast<char*>(out->data()), static_cast<std::streamsize>(out->size())) || f.peek() != std::char_traits<char>::eof()) {
        out->clear();
        *why = "module changed or could not be read";
        return false;
    }
    return true;
}

std::string check_mod_module(const uint8_t* data, size_t size, ModModuleInfo* info, Bytes* linked) {
    return link_hook_module(data, size, linked, info);
}

bool stamp_module(Bytes* spv, std::string* why) {
    if (spv->size() > kMaxModModuleBytes) {
        *why = "module exceeds the size limit";
        return false;
    }
    *why = check_module(spv->data(), spv->size());
    if (!why->empty()) return false;
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
                           (literal(reinterpret_cast<const uint8_t*>(words.data()), i + 1, wc - 1).rfind("kiln-vfx-hooks-", 0) == 0 ||
                            literal(reinterpret_cast<const uint8_t*>(words.data()), i + 1, wc - 1).rfind("kiln-vfx-abi-", 0) == 0);
        if (head && !(op == kOpCapability || op == kOpExtension || op == kOpExtInstImport || op == kOpMemoryModel || op == kOpEntryPoint ||
                      op == kOpExecutionMode || op == kOpExecutionModeId)) {
            head = false;
            insert_at = kept.size();
        }
        if (!stamp) kept.insert(kept.end(), words.begin() + i, words.begin() + i + wc);
        i += wc;
    }
    if (head) insert_at = kept.size();
    const std::string marker = hook_abi_marker();
    std::vector<uint32_t> ins(1 + (marker.size() + 4) / 4, 0);
    std::memcpy(&ins[1], marker.data(), marker.size());
    ins[0] = static_cast<uint32_t>(ins.size()) << 16 | kOpSourceExtension;
    kept.insert(kept.begin() + static_cast<std::ptrdiff_t>(insert_at), ins.begin(), ins.end());
    if (kept.size() * 4 > kMaxModModuleBytes) {
        *why = "stamped module exceeds the size limit";
        return false;
    }
    spv->resize(kept.size() * 4);
    std::memcpy(spv->data(), kept.data(), spv->size());
    return true;
}

}  // namespace kvb
