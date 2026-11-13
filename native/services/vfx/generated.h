// SPDX-License-Identifier: MIT
// The limits kiln.vfx reads a mod's JSON with at boot: its data files (blocks/*.json) and what Kiln.Sdk baked from its code
// (blocks/generated/*.json, docs/kiln/modding/baked.md). A mod's folder is input from the internet, so nothing is allocated
// or parsed past these: how many entries a folder listing looks at, how many JSON files, bytes per file and in all, and how
// deep the JSON nests. What leads out of the mod's folder is refused as well: the boot reader opens a folder or a file first
// and then checks the final path of what it opened (path_inside), so a link, junction or path swapped anywhere from the
// mod's folder down cannot lead out. Plain functions: the host test runs them (native/services/vfx/tests/test_generated.cpp).
#pragma once
#include <cstddef>
#include <cstdint>
#include <cwctype>
#include <string_view>

namespace kvx_generated {

struct Limits {
    size_t entries;        // folder entries looked at (any kind): the listing stops there and nothing of it is read
    size_t files;          // JSON files: more and nothing of the folder is read
    uint64_t file_bytes;   // one file
    uint64_t total_bytes;  // all of the folder's files read
    int depth;             // arrays and objects nested
};

// blocks/generated: written by the SDK, a few KiB; room for any build of it.
constexpr Limits kGenerated{1024, 16, 4ull << 20, 16ull << 20, 64};
// blocks/*.json, the author's: real block files are a few KiB and a handful (the repository's largest is 6 KiB, its most in
// one mod 6). Generous so no real mod meets them; the same per-file size and depth kiln.quests' start-up build takes for the
// same files (questpack.h kMaxBlockFile, kMaxBlockDepth).
constexpr Limits kBlocks{4096, 256, 16ull << 20, 64ull << 20, 64};

// Why one more file is not read (nullptr: it is read). files: files read so far; total: their bytes.
inline const char* refusal(const Limits& l, size_t files, uint64_t size, uint64_t total) {
    if (files >= l.files) return "more files than the limit";
    if (size > l.file_bytes) return "over the size limit for one file";
    if (total + size > l.total_bytes) return "over the size limit with the files before it";
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

// Whether `path` lies below `root` (both final paths of opened handles, \\?\ form): case-insensitive, on a separator
// boundary ("...\mods\a" does not hold "...\mods\ab\x"), '/' taken as '\'. Empty: never.
inline bool path_inside(std::wstring_view root, std::wstring_view path) {
    while (!root.empty() && (root.back() == L'\\' || root.back() == L'/')) root.remove_suffix(1);
    if (root.empty() || path.size() <= root.size() + 1) return false;
    auto same = [](wchar_t a, wchar_t b) {
        if (a == L'/') a = L'\\';
        if (b == L'/') b = L'\\';
        return std::towlower(static_cast<wint_t>(a)) == std::towlower(static_cast<wint_t>(b));
    };
    for (size_t i = 0; i < root.size(); ++i)
        if (!same(root[i], path[i])) return false;
    if (path[root.size()] != L'\\' && path[root.size()] != L'/') return false;
    for (size_t i = root.size() + 1; i + 1 < path.size(); ++i)  // no ".." step back out
        if (path[i] == L'.' && path[i + 1] == L'.' && (path[i - 1] == L'\\' || path[i - 1] == L'/') &&
            (i + 2 == path.size() || path[i + 2] == L'\\' || path[i + 2] == L'/'))
            return false;
    return true;
}

}  // namespace kvx_generated
