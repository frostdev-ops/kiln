// SPDX-License-Identifier: MIT
// kiln.assets: the portable half of reading a mod's files from its own folder (scan.h).
#include "scan.h"

#include <cwctype>

namespace ka {
namespace {

std::string utf8(const std::filesystem::path& p) {
    const auto u = p.generic_u8string();
    return std::string(u.begin(), u.end());
}

}  // namespace

bool scan_tree(const std::filesystem::path& root, const ListFn& list, const IdFn& id, size_t budget, FileIds* ids, std::string* why) {
    namespace fs = std::filesystem;
    std::vector<fs::path> stack{root};
    size_t seen = 0;
    while (!stack.empty()) {
        const fs::path dir = stack.back();
        stack.pop_back();
        const auto entries = list(dir);
        if (!entries) {
            *why = "cannot list " + utf8(dir);
            return false;
        }
        for (const auto& e : *entries) {
            if (e.name == L"." || e.name == L"..") continue;
            if (++seen > budget) {
                *why = "more than " + std::to_string(budget) + " entries below " + utf8(root);
                return false;
            }
            const fs::path p = dir / e.name;
            if (e.kind == Entry::Dir) {
                stack.push_back(p);
            } else if (e.kind == Entry::File) {
                const auto i = id(p);
                if (!i) {
                    *why = "cannot read the identity of " + utf8(p);
                    return false;
                }
                ids->insert(*i);
            }  // a link is not followed: what it names is no file of this folder
        }
    }
    return true;
}

AliasVerdict alias_verdict(bool named_cooked, bool in_cooked_ids, bool ids_complete, CookedPolicy policy) {
    if (named_cooked) return AliasVerdict::Ok;
    if (in_cooked_ids) return AliasVerdict::Alias;
    if (ids_complete || policy == CookedPolicy::Any) return AliasVerdict::Ok;
    return policy == CookedPolicy::Local ? AliasVerdict::Unproven : AliasVerdict::UnprovenWarn;
}

bool names_plain(const std::filesystem::path& base, const std::filesystem::path& rel, const AttrFn& attr, std::string* why) {
    namespace fs = std::filesystem;
    switch (attr(base)) {
        case NameKind::Missing: *why = "cannot read " + utf8(base); return false;
        case NameKind::Link:
            *why = "the mod's assets folder is a link or a junction: a mod's assets are read from its own folder only";
            return false;
        case NameKind::Plain: break;
    }
    fs::path cur = base;
    for (const auto& part : rel) {
        cur /= part;
        switch (attr(cur)) {
            case NameKind::Missing: *why = "cannot read " + utf8(base / rel) + " (no " + utf8(cur.lexically_relative(base)) + ")"; return false;
            case NameKind::Link:
                *why = utf8(cur.lexically_relative(base)) + " is a link or a junction: a mod's assets are read from its own assets folder only";
                return false;
            case NameKind::Plain: break;
        }
    }
    return true;
}

bool named_in_cooked(const std::filesystem::path& rel) {
    auto it = rel.begin();
    if (it == rel.end()) return false;
    std::wstring first = it->wstring();
    for (auto& c : first) c = static_cast<wchar_t>(std::towlower(c));
    return first == L"cooked" && ++it != rel.end();
}

}  // namespace ka
