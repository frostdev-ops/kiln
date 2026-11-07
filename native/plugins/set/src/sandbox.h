// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <cstdio>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>
#ifdef _WIN32
#include <windows.h>
#include <fcntl.h>
#include <io.h>
#else
#include <fcntl.h>
#include <unistd.h>
#include <sys/stat.h>
#endif

namespace kset::sandbox {
inline bool relative(std::string_view path) {
    if (path.empty() || path.find_first_of(":*?\"<>|") != path.npos) return false;
    for (unsigned char c : path) if (c < 32) return false;
    for (size_t pos = 0; pos < path.size();) {
        const size_t end = std::min(path.find_first_of("/\\", pos), path.size());
        const auto part = path.substr(pos, end - pos);
        if (part.empty() || part.back() == '.' || part.back() == ' ') return false;
        // Win32 device names remain devices even with an extension.
        std::string stem(part.substr(0, part.find('.')));
        for (char& c : stem) if (c >= 'a' && c <= 'z') c -= 'a' - 'A';
        if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
            (stem.size() == 4 && (stem.starts_with("COM") || stem.starts_with("LPT")) && stem[3] >= '0' && stem[3] <= '9')) return false;
        if (end == path.size()) return true;
        pos = end + 1;
    }
    return false;
}
// Keep directory handles open without delete sharing on Windows; on POSIX openat anchors every lookup. Reject
// reparse points/symlinks, including the final file, before truncating anything. No check-then-fopen race.
inline FILE* open(const std::string& root, std::string_view path, const char* mode) {
    if (!relative(path)) return nullptr;
    std::string m(mode);
    bool plus = m.find('+') != m.npos;
    if (m.empty() || (m[0] != 'r' && m[0] != 'w' && m[0] != 'a') || m.find_first_not_of("rwa+b") != m.npos ||
        std::count(m.begin(), m.end(), '+') > 1 || std::count(m.begin(), m.end(), 'b') > 1 ||
        std::count_if(m.begin(), m.end(), [](char c) { return c == 'r' || c == 'w' || c == 'a'; }) != 1) return nullptr;
    std::vector<std::string> parts;
    for (size_t i = 0; i < path.size();) {
        size_t end = std::min(path.find_first_of("/\\", i), path.size());
        parts.emplace_back(path.substr(i, end - i));
        i = end + 1;
    }
#ifdef _WIN32
    std::vector<HANDLE> dirs;
    auto closeDirs = [&] { for (HANDLE h : dirs) CloseHandle(h); dirs.clear(); };
    struct CloseOnExit { decltype(closeDirs)& fn; ~CloseOnExit() { fn(); } } cleanup{closeDirs};
    std::string current = root;
    // Root itself may not be redirected to somebody else's folder.
    for (size_t i = 0; i < parts.size(); ++i) {
        HANDLE d = CreateFileA(current.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                               OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        BY_HANDLE_FILE_INFORMATION info{};
        if (d == INVALID_HANDLE_VALUE || !GetFileInformationByHandle(d, &info) ||
            !(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
            if (d != INVALID_HANDLE_VALUE) CloseHandle(d);
            closeDirs(); return nullptr;
        }
        try { dirs.push_back(d); } catch (...) { CloseHandle(d); throw; }
        current += "\\" + parts[i];
    }
    DWORD access = (m[0] == 'r' || plus ? GENERIC_READ : 0) | (m[0] != 'r' || plus ? GENERIC_WRITE : 0);
    HANDLE f = CreateFileA(current.c_str(), access, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                          m[0] == 'r' ? OPEN_EXISTING : OPEN_ALWAYS, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    BY_HANDLE_FILE_INFORMATION info{};
    bool ok = f != INVALID_HANDLE_VALUE && GetFileType(f) == FILE_TYPE_DISK && GetFileInformationByHandle(f, &info) &&
              !(info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) && info.nNumberOfLinks == 1;
    // Truncate only after checking the actual opened handle.
    if (ok && m[0] == 'w') ok = SetFilePointer(f, 0, nullptr, FILE_BEGIN) != INVALID_SET_FILE_POINTER && SetEndOfFile(f);
    closeDirs();
    if (!ok) { if (f != INVALID_HANDLE_VALUE) CloseHandle(f); return nullptr; }
    int flags = plus ? _O_RDWR : m[0] == 'r' ? _O_RDONLY : _O_WRONLY;
    flags |= m.find('b') != m.npos ? _O_BINARY : _O_TEXT;
    if (m[0] == 'a') flags |= _O_APPEND;
    int fd = _open_osfhandle(reinterpret_cast<intptr_t>(f), flags);
    if (fd < 0) { CloseHandle(f); return nullptr; }
    FILE* file = _fdopen(fd, mode);
    if (!file) _close(fd);
    return file;
#else
    int dir = ::open(root.c_str(), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (dir < 0) return nullptr;
    for (size_t i = 0; i + 1 < parts.size(); ++i) {
        int next = openat(dir, parts[i].c_str(), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        ::close(dir); dir = next;
        if (dir < 0) return nullptr;
    }
    int flags = plus ? O_RDWR : m[0] == 'r' ? O_RDONLY : O_WRONLY;
    if (m[0] != 'r') flags |= O_CREAT;
    if (m[0] == 'a') flags |= O_APPEND;
    int fd = openat(dir, parts.back().c_str(), flags | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK, 0600);
    ::close(dir);
    if (fd < 0) return nullptr;
    struct stat st{};
    if (fstat(fd, &st) != 0 || !S_ISREG(st.st_mode) || st.st_nlink != 1 || (m[0] == 'w' && ftruncate(fd, 0) != 0)) { ::close(fd); return nullptr; }
    FILE* f = fdopen(fd, mode);
    if (!f) ::close(fd);
    return f;
#endif
}
} // namespace kset::sandbox
