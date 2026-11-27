// SPDX-License-Identifier: MIT
// assets.json's "hash" of a blob entry, as load_manifest (assets.cpp) reads it. Header-only so that the host test
// (tests/test_store.cpp, hash_text) runs the manifest's own rule on parsed JSON without the rest of assets.cpp.
#pragma once
#include <string>

#include "assets.h"
#include "nlohmann/json.hpp"

namespace ka {

// `entry`: a blob entry. "" with *named false when it has no "hash" (a new blob, its key derived from the bytes); "" with *named
// and *out for a hash that reads (an override under that key); else why the entry is refused (blob_hash_refusal), never a key
// kiln.assets derives instead. A value that is not a string is shown as its JSON when it is a number, true, false or null, and
// as [...] or {...} when it is an array or an object (dump() of those writes, recursively, every level the message then cuts).
inline std::string blob_entry_hash(const nlohmann::json& entry, const std::string& file, bool* named, HashK* out) {
    *named = false;
    if (!entry.is_object()) return {};
    const auto it = entry.find("hash");
    if (it == entry.end()) return {};
    *named = true;
    const nlohmann::json& v = *it;
    if (v.is_string())
        return blob_hash_refusal(file, &v.get_ref<const std::string&>(), v.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace),
                                 out);
    return blob_hash_refusal(file, nullptr, v.is_array() ? "[...]" : v.is_object() ? "{...}" : v.dump(), out);
}

// A blob under a hash that reads: "" when its file is as long as the hash says, else why the entry is refused, naming both
// (kiln.assets serves a fixed hash only for bytes of its size). A size written in hex reads when its digits are all decimal
// (0x2820 as "2820"), and only the length shows it.
inline std::string blob_size_refusal(const std::string& file, const HashK& fixed, size_t length) {
    if (fixed.size == length) return {};
    return "blob " + file + ": its hash says " + std::to_string(fixed.size) + " bytes, the file has " + std::to_string(length) +
           " (the size is decimal)";
}

}  // namespace ka
