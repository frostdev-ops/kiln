// SPDX-License-Identifier: MIT
// kcompat::ExportCache: whether a DLL file exports a name, read once per version of the file (its size and last write
// time) instead of on every look. Header-only and without windows.h (host test: shroudtopia/tests/run.py).
//
// kiln-shroudtopia's loader thread looks for new mods every updateDelay (500 ms, as Shroudtopia's own loader does), and
// each look read every candidate DLL in <game>/mods and the profile's folders whole to parse its export table
// (kcompat::pe_exports): about 5 MB every half second for six XHL mods, and on a hard-disk player's PC 5 s of that
// thread's CPU in a 9.4 s freeze at the game's first frames (perfdiag 2026-11-17, inbox 50: CreateFileW <
// kcompat::CheckedPath::open < kcompat::CheckedDll::open < folder_dll < scan < check_and_load < loop). A DLL that has
// not changed has the same exports: one size-and-time query now, the export table only when the file changed.
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <mutex>
#include <string>
#include <string_view>

namespace kcompat {

class ExportCache {
public:
    // read(file, name): the real answer (kcompat::pe_exports_name), asked when the file is new or changed. A file whose
    // size or time cannot be read is asked every time and not kept.
    template <class Read>
    bool exports(const std::filesystem::path& file, std::string_view name, Read&& read) {
        std::error_code size_error, time_error;
        const auto size = std::filesystem::file_size(file, size_error);
        const auto time = std::filesystem::last_write_time(file, time_error);
        if (size_error || time_error) return read(file, name);
        const Key key{file.native(), std::string(name)};
        {
            std::lock_guard lock(mu_);
            const auto it = known_.find(key);
            if (it != known_.end() && it->second.size == size && it->second.time == time) return it->second.found;
        }
        const bool found = read(file, name);
        std::lock_guard lock(mu_);
        if (known_.size() >= kMax) known_.clear();  // a folder of many changing DLLs: start over, never grow without end
        known_[key] = {size, time, found};
        return found;
    }
    size_t size() const {
        std::lock_guard lock(mu_);
        return known_.size();
    }

private:
    static constexpr size_t kMax = 4096;
    using Key = std::pair<std::filesystem::path::string_type, std::string>;
    struct Entry {
        uintmax_t size;
        std::filesystem::file_time_type time;
        bool found;
    };
    mutable std::mutex mu_;
    std::map<Key, Entry> known_;
};

}  // namespace kcompat
