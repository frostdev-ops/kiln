// SPDX-License-Identifier: MIT
// kiln.assets: a file a mod's manifest names is read from that mod's own assets folder and nowhere else.
#include "contained.h"

#include <windows.h>

#include <algorithm>
#include <cwctype>

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

bool id_of(HANDLE h, std::tuple<uint32_t, uint32_t, uint32_t>* id) {
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(h, &info)) return false;
    *id = {info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow};
    return true;
}

void walk_ids(const std::filesystem::path& dir, FileIds* ids, int depth) {
    if (depth > 16) return;
    WIN32_FIND_DATAW fd{};
    HANDLE ff = FindFirstFileW((dir / L"*").c_str(), &fd);
    if (ff == INVALID_HANDLE_VALUE) return;
    do {
        const std::wstring n = fd.cFileName;
        if (n == L"." || n == L"..") continue;
        const std::filesystem::path p = dir / n;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            walk_ids(p, ids, depth + 1);
            continue;
        }
        HANDLE h = CreateFileW(p.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) continue;
        std::tuple<uint32_t, uint32_t, uint32_t> id;
        if (id_of(h, &id)) ids->insert(id);
        CloseHandle(h);
    } while (FindNextFileW(ff, &fd));
    FindClose(ff);
}

}  // namespace

void add_cooked_ids(const std::filesystem::path& mod_dir, FileIds* ids) { walk_ids(mod_dir / L"assets" / L"cooked", ids, 0); }

bool read_contained(const std::filesystem::path& base, const std::filesystem::path& file, std::vector<uint8_t>* out, std::string* why,
                    const FileIds* cooked_ids) {
    namespace fs = std::filesystem;
    const fs::path rel = file.lexically_normal().lexically_relative(base.lexically_normal());
    if (rel.empty() || *rel.begin() == L"..") {
        *why = narrow(file.wstring()) + " is not in the mod's assets folder";
        return false;
    }
    // 1. by name, every folder and the file from the assets folder down: no link and no junction. On Windows a link is
    //    a reparse point; Wine reports a Unix symlink as one to a query by name too (its handles follow the link).
    fs::path cur = base;
    for (const auto& part : rel) {
        cur /= part;
        const DWORD a = GetFileAttributesW(cur.c_str());
        if (a == INVALID_FILE_ATTRIBUTES) {
            *why = "cannot read " + narrow(file.wstring()) + " (no " + narrow(cur.lexically_relative(base).generic_wstring()) + ")";
            return false;
        }
        if (a & FILE_ATTRIBUTE_REPARSE_POINT) {
            *why = narrow(cur.lexically_relative(base).generic_wstring()) +
                   " is a link or a junction: a mod's assets are read from its own assets folder only";
            return false;
        }
    }
    // 2. what is opened: its final path lies in the assets folder's final path (Windows resolves every link here)
    HANDLE dir = CreateFileW(base.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                             OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (dir == INVALID_HANDLE_VALUE) {
        *why = "cannot open " + narrow(base.wstring());
        return false;
    }
    const std::wstring root = final_path(dir);
    CloseHandle(dir);
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
    //    name for a cook's output (a link to a file, which Wine cannot show; a hard link)
    const bool named_cooked = [&] {
        auto it = rel.begin();
        if (it == rel.end()) return false;
        std::wstring first = it->wstring();
        for (auto& c : first) c = static_cast<wchar_t>(std::towlower(c));
        return first == L"cooked" && ++it != rel.end();
    }();
    if (cooked_ids && !named_cooked && cooked_ids->count({info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow})) {
        CloseHandle(h);
        *why = narrow(rel.generic_wstring()) + " is a cooked file of a mod (its assets/cooked) under another name: a mod's assets are "
               "read from its own assets folder only";
        return false;
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
