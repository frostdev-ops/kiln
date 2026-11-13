// SPDX-License-Identifier: MIT
// kiln.assets: a file a mod's manifest names is read from that mod's own assets folder and nowhere else.
#include "contained.h"

#include <windows.h>

#include <algorithm>
#include <cwctype>
#include <optional>

#include "assets.h"

namespace ka {
namespace {

std::wstring final_path(HANDLE h) {
    std::wstring out(32768, L'\0');
    const DWORD n = GetFinalPathNameByHandleW(h, out.data(), static_cast<DWORD>(out.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (!n || n >= out.size()) return {};
    out.resize(n);
    return out;
}

bool inside(const std::wstring& root, const std::wstring& file) {
    if (root.empty() || file.size() <= root.size() || _wcsnicmp(root.c_str(), file.c_str(), root.size()) != 0) return false;
    return root.back() == L'\\' || file[root.size()] == L'\\';
}

bool id_of(HANDLE h, FileId* id) {
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(h, &info)) return false;
    *id = {info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow};
    return true;
}

// By name: a link (a reparse point; Wine shows its Unix links to folders so), a plain file or folder, or nothing.
NameKind name_kind(const std::filesystem::path& p) {
    const DWORD a = GetFileAttributesW(p.c_str());
    if (a == INVALID_FILE_ATTRIBUTES) return NameKind::Missing;
    return (a & FILE_ATTRIBUTE_REPARSE_POINT) ? NameKind::Link : NameKind::Plain;
}

std::optional<std::vector<Entry>> list_dir(const std::filesystem::path& dir) {
    WIN32_FIND_DATAW fd{};
    HANDLE ff = FindFirstFileW((dir / L"*").c_str(), &fd);
    if (ff == INVALID_HANDLE_VALUE) return std::nullopt;
    std::vector<Entry> out;
    do {
        const std::wstring n = fd.cFileName;
        if (n == L"." || n == L"..") continue;
        Entry::Kind k = Entry::File;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) k = Entry::Link;
        else if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) k = name_kind(dir / n) == NameKind::Link ? Entry::Link : Entry::Dir;
        out.push_back({n, k});
    } while (FindNextFileW(ff, &fd));
    const bool done = GetLastError() == ERROR_NO_MORE_FILES;
    FindClose(ff);
    if (!done) return std::nullopt;
    return out;
}

std::optional<FileId> id_by_name(const std::filesystem::path& p) {
    HANDLE h = CreateFileW(p.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return std::nullopt;
    FileId id;
    const bool ok = id_of(h, &id);
    CloseHandle(h);
    if (!ok) return std::nullopt;
    return id;
}

std::wstring final_path_of(const std::filesystem::path& dir) {
    HANDLE h = CreateFileW(dir.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (h == INVALID_HANDLE_VALUE) return {};
    const std::wstring out = final_path(h);
    CloseHandle(h);
    return out;
}

}  // namespace

CookedIds cooked_ids_of(const std::vector<std::filesystem::path>& mod_dirs) {
    return scan_load_order(mod_dirs, list_dir, id_by_name, name_kind);
}

bool read_contained(const std::filesystem::path& base, const std::filesystem::path& file, std::vector<uint8_t>* out, std::string* why,
                    const CookedIds* cooked_ids, CookedPolicy policy, bool* unproven) {
    namespace fs = std::filesystem;
    const fs::path rel = file.lexically_normal().lexically_relative(base.lexically_normal());
    if (rel.empty() || *rel.begin() == L"..") {
        *why = narrow(file.wstring()) + " is not in the mod's assets folder";
        return false;
    }
    // 1. by name, the assets folder itself and every folder and the file below it: no link and no junction (scan.cpp)
    if (!names_plain(base, rel, name_kind, why)) return false;
    // 2. what is opened: the assets folder's final path lies in the mod folder's (a junction or link at assets itself,
    //    which Windows resolves here), and the file's final path in the assets folder's (any link below it)
    const std::wstring root = final_path_of(base), mod = final_path_of(base.parent_path());
    if (root.empty() || mod.empty()) {
        *why = "cannot open " + narrow(base.wstring());
        return false;
    }
    if (!inside(mod, root)) {
        *why = "the mod's assets folder is a link or a junction: a mod's assets are read from its own folder only";
        return false;
    }
    HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        *why = "cannot open " + narrow(file.wstring());
        return false;
    }
    const std::wstring opened = final_path(h);
    BY_HANDLE_FILE_INFORMATION info{};
    if (!inside(root, opened) || !GetFileInformationByHandle(h, &info) ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) {
        CloseHandle(h);
        *why = narrow(rel.generic_wstring()) + " is not a file of the mod's own assets folder";
        return false;
    }
    // 3. the same file as a cooked file of a mod being loaded, under a name outside this mod's assets/cooked: another
    //    name for a cook's output (a link to a file, which Wine cannot show; a hard link). When the cooked folders were
    //    not all scanned, a file outside its own assets/cooked cannot be shown to be no such name (scan.cpp)
    if (cooked_ids) {
        const AliasVerdict v = alias_verdict(named_in_cooked(rel), cooked_ids->ids.count({info.dwVolumeSerialNumber, info.nFileIndexHigh,
                                                                                          info.nFileIndexLow}) != 0,
                                             cooked_ids->complete, policy);
        if (v == AliasVerdict::Alias || v == AliasVerdict::Unproven) {
            CloseHandle(h);
            *why = narrow(rel.generic_wstring()) + (v == AliasVerdict::Alias
                ? " is a cooked file of a mod (its assets/cooked) under another name: a mod's assets are read from its own assets folder only"
                : " cannot be shown to be no other name for a cooked file: a mod's cooked folder could not be scanned whole (kiln.log says which)");
            return false;
        }
        if (v == AliasVerdict::UnprovenWarn && unproven) *unproven = true;
    }
    // 4. the bytes come from that same handle
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart < 0 || size.QuadPart > (1ll << 31)) {
        CloseHandle(h);
        *why = "cannot read " + narrow(file.wstring());
        return false;
    }
    out->resize(static_cast<size_t>(size.QuadPart));
    size_t got = 0;
    while (got < out->size()) {
        DWORD n = 0;
        const DWORD want = static_cast<DWORD>((std::min<size_t>)(out->size() - got, size_t{1} << 24));
        if (!ReadFile(h, out->data() + got, want, &n, nullptr) || n == 0) break;
        got += n;
    }
    CloseHandle(h);
    if (got != out->size()) {
        *why = "cannot read " + narrow(file.wstring());
        return false;
    }
    return true;
}

}  // namespace ka
