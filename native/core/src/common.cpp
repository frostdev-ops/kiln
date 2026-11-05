// SPDX-License-Identifier: MIT
#include "common.h"

#include <algorithm>
#include <cstdio>

namespace kiln {
namespace {
Paths g_paths;
LARGE_INTEGER g_freq{};
}  // namespace

std::string to_utf8(std::wstring_view w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring to_wide(std::string_view s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

const Paths& paths() { return g_paths; }

void init_paths() {
    QueryPerformanceFrequency(&g_freq);
    std::wstring exe(MAX_PATH, L'\0');
    for (;;) {
        const DWORD n = GetModuleFileNameW(nullptr, exe.data(), static_cast<DWORD>(exe.size()));
        if (n < exe.size()) {
            exe.resize(n);
            break;
        }
        exe.resize(exe.size() * 2);
    }
    g_paths.exe = exe;
    g_paths.game = g_paths.exe.parent_path();
    g_paths.kiln = g_paths.game / L"kiln";
    g_paths.logs = g_paths.kiln / L"logs";
    g_paths.mods = g_paths.kiln / L"mods";
    g_paths.data = g_paths.kiln / L"data";
    g_paths.control = g_paths.kiln / L"control";
    g_paths.plugins = g_paths.kiln / L"plugins";
    g_paths.sigdb = g_paths.kiln / L"sigdb";
    g_paths.config = g_paths.kiln / L"config";
    g_paths.state = g_paths.kiln / L"state";
    std::error_code ec;
    for (auto* p : {&g_paths.logs, &g_paths.mods, &g_paths.data, &g_paths.control, &g_paths.state})
        std::filesystem::create_directories(*p, ec);
}

void set_config_dir(const std::filesystem::path& dir) { g_paths.config = dir; }

bool write_file_atomic(const std::filesystem::path& file, std::string_view text) {
    std::error_code ec;
    std::filesystem::create_directories(file.parent_path(), ec);
    const std::filesystem::path tmp = file.wstring() + L".tmp";
    HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    const BOOL ok = WriteFile(h, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) && written == text.size();
    CloseHandle(h);
    if (!ok || !MoveFileExW(tmp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(tmp.c_str());
        return false;
    }
    return true;
}

bool read_file(const std::filesystem::path& file, std::string* out) {
    out->clear();
    HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    bool ok = GetFileSizeEx(h, &size) && size.QuadPart < (64ll << 20);
    if (ok) {
        out->resize(static_cast<size_t>(size.QuadPart));
        DWORD got = 0;
        ok = out->empty() || (ReadFile(h, out->data(), static_cast<DWORD>(out->size()), &got, nullptr) && got == out->size());
    }
    CloseHandle(h);
    if (!ok) out->clear();
    return ok;
}

std::string jsonc_to_json(std::string_view t) {
    std::string out;
    out.reserve(t.size());
    size_t i = 0;
    while (i < t.size()) {
        const char c = t[i];
        if (c == '"') {  // a string, escapes included
            const size_t start = i++;
            while (i < t.size() && t[i] != '"') i += t[i] == '\\' ? 2 : 1;
            out.append(t.substr(start, std::min(i + 1, t.size()) - start));
            ++i;
        } else if (c == '/' && i + 1 < t.size() && t[i + 1] == '/') {
            while (i < t.size() && t[i] != '\n') ++i;
        } else if (c == '/' && i + 1 < t.size() && t[i + 1] == '*') {
            i += 2;
            while (i + 1 < t.size() && !(t[i] == '*' && t[i + 1] == '/')) {
                if (t[i] == '\n') out += '\n';
                ++i;
            }
            i += 2;
        } else if (c == ',') {  // trailing comma: the next significant character closes the container
            size_t j = i + 1;
            for (;;) {
                while (j < t.size() && (t[j] == ' ' || t[j] == '\t' || t[j] == '\r' || t[j] == '\n')) ++j;
                if (j + 1 < t.size() && t[j] == '/' && t[j + 1] == '/') {
                    while (j < t.size() && t[j] != '\n') ++j;
                } else if (j + 1 < t.size() && t[j] == '/' && t[j + 1] == '*') {
                    j += 2;
                    while (j + 1 < t.size() && !(t[j] == '*' && t[j + 1] == '/')) ++j;
                    j += 2;
                } else {
                    break;
                }
            }
            if (j >= t.size() || (t[j] != '}' && t[j] != ']')) out += ',';
            ++i;
        } else {
            out += c;
            ++i;
        }
    }
    return out;
}

std::string utc_now() {
    SYSTEMTIME t;
    GetSystemTime(&t);
    char b[32];
    std::snprintf(b, sizeof b, "%04u-%02u-%02uT%02u:%02u:%02uZ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return b;
}

double now_ms() {
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return static_cast<double>(c.QuadPart) * 1000.0 / static_cast<double>(g_freq.QuadPart);
}

}  // namespace kiln
