// SPDX-License-Identifier: MIT
#pragma once
#include "logic.h"
#include <filesystem>
#include <fstream>
#include <functional>

namespace kenshwick {
namespace fs = std::filesystem;
// Also reject aliases before CheckedPath normalizes them. Ancestors may be links, but not traversal or ADS.
inline bool safe_source_path(const fs::path& p) {
    if (!p.is_absolute()) return false;
    for (const auto& part : p.relative_path()) {
        const auto s = part.native();
        if (s.empty() || part == fs::path(".") || part == fs::path("..") || s.find(':') != s.npos || s.find(fs::path::value_type{}) != s.npos ||
            s.back() == '.' || s.back() == ' ') return false;
    }
    return true;
}
inline std::string tree_utf8(const fs::path& p) {
    const auto s = p.generic_u8string();
    return {reinterpret_cast<const char*>(s.data()), s.size()};
}
using PathCheck = std::function<bool(const fs::path&, const fs::path&, bool)>;
struct Tree {
    std::vector<fs::path> dirs, files;
    std::string stamp;
};
// No permission-skipping, no followed links, no partial fingerprint on an I/O error. Include directories and
// content as well as timestamps, so player additions/edits revoke permission to refresh or remove a copy.
inline bool scan_tree(const fs::path& root, Tree* out, const PathCheck& check) {
    *out = {};
    if (!safe_source_path(root) || !check(root, root, false)) return false;
    std::error_code ec;
    std::vector<FileStamp> stamps;
    for (fs::recursive_directory_iterator it(root, ec), end; !ec && it != end; it.increment(ec)) {
        auto status = it->symlink_status(ec);
        if (ec || fs::is_symlink(status)) return false;
        const bool dir = fs::is_directory(status);
        if ((!dir && !fs::is_regular_file(status)) || !check(root, it->path(), !dir)) return false;
        const fs::path rel = it->path().lexically_relative(root);
        if (lower(tree_utf8(rel)) == kManagedMarker) continue;
        if (dir) out->dirs.push_back(rel);
        else out->files.push_back(rel);
        uint64_t hash = 1469598103934665603ull, size = 0;
        if (!dir) {
            std::ifstream f(it->path(), std::ios::binary);
            if (!f) return false;
            char buf[16384];
            while (f.read(buf, sizeof buf) || f.gcount()) {
                size += static_cast<uint64_t>(f.gcount());
                for (std::streamsize n = 0; n < f.gcount(); ++n) hash = (hash ^ static_cast<unsigned char>(buf[n])) * 1099511628211ull;
            }
            if (!f.eof()) return false;
        }
        const auto time = it->last_write_time(ec);
        if (ec) return false;
        stamps.push_back({(dir ? "d/" : "f/") + tree_utf8(rel) + "/" + std::to_string(hash), size,
                          static_cast<uint64_t>(time.time_since_epoch().count())});
    }
    if (ec) return false;
    out->stamp = fingerprint(std::move(stamps));
    return true;
}
// The caller creates this directory exclusively. Never clear a predictable temporary path or overwrite a file.
inline bool copy_tree_files(const fs::path& source, const fs::path& stage, const Tree& tree) {
    std::error_code ec;
    for (const auto& p : tree.dirs) {
        if (!fs::create_directory(stage / p, ec) || ec) return false;
    }
    for (const auto& p : tree.files) {
        if (!fs::copy_file(source / p, stage / p, fs::copy_options::none, ec) || ec) return false;
    }
    return true;
}
} // namespace kenshwick
