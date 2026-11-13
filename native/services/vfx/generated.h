// SPDX-License-Identifier: MIT
// blocks/generated/*.json: what Kiln.Sdk baked from a mod's code (docs/kiln/modding/baked.md), read by kiln.vfx at boot beside the
// mod's blocks/*.json. A folder anyone with the mod can fill, so the boot reads it with limits, before anything is
// allocated or parsed: no link or reparse point (the folder or a file: it could point outside the mod), at most 16 files,
// 4 MiB each and 16 MiB in all, JSON nested at most 64 deep. Plain functions: the host test runs them
// (native/services/vfx/tests/test_generated.cpp).
#pragma once
#include <cstddef>
#include <cstdint>
#include <string_view>

namespace kvx_generated {

constexpr size_t kMaxFiles = 16;
constexpr uint64_t kMaxFileBytes = 4ull << 20;
constexpr uint64_t kMaxTotalBytes = 16ull << 20;
constexpr int kMaxDepth = 64;

// Why one more file is not read (nullptr: it is read). files: files read so far; total: their bytes.
inline const char* refusal(bool link, size_t files, uint64_t size, uint64_t total) {
    if (link) return "a link or reparse point (it could point outside the mod)";
    if (files >= kMaxFiles) return "more than 16 files";
    if (size > kMaxFileBytes) return "over 4 MiB";
    if (total + size > kMaxTotalBytes) return "over 16 MiB with the files before it";
    return nullptr;
}

// Whether JSON text (with the // and /* */ comments the relaxed parser takes) nests arrays and objects at most `max`
// deep: brackets outside strings and comments, counted before the parser sees the text.
inline bool depth_ok(std::string_view s, int max) {
    int depth = 0;
    for (size_t i = 0; i < s.size(); ++i) {
        const char c = s[i];
        if (c == '"') {
            for (++i; i < s.size() && s[i] != '"'; ++i)
                if (s[i] == '\\') ++i;
            continue;
        }
        if (c == '/' && i + 1 < s.size() && s[i + 1] == '/') {
            while (i < s.size() && s[i] != '\n') ++i;
            continue;
        }
        if (c == '/' && i + 1 < s.size() && s[i + 1] == '*') {
            const size_t end = s.find("*/", i + 2);
            if (end == std::string_view::npos) return true;  // unterminated: the parser says what is wrong
            i = end + 1;
            continue;
        }
        if (c == '{' || c == '[') {
            if (++depth > max) return false;
        } else if ((c == '}' || c == ']') && depth > 0) {
            --depth;
        }
    }
    return true;
}

}  // namespace kvx_generated
