// SPDX-License-Identifier: MIT
// kiln.settings: writes live values into enshrouded_server.json. Every other key of the file is kept as it is (order
// included); the previous file stays next to it as <name>.kiln-backup; the new file replaces the old one atomically.
#include "config.h"

#include <windows.h>

#include <cstdio>
#include <vector>

namespace kset {
namespace {

bool read_file(const std::wstring& path, std::string& out) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    bool ok = GetFileSizeEx(h, &size) && size.QuadPart >= 0 && size.QuadPart < (16 << 20);
    if (ok) {
        out.resize(static_cast<size_t>(size.QuadPart));
        DWORD got = 0;
        ok = out.empty() || (ReadFile(h, out.data(), static_cast<DWORD>(out.size()), &got, nullptr) && got == out.size());
    }
    CloseHandle(h);
    return ok;
}

bool write_file(const std::wstring& path, const std::string& data) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD put = 0;
    const bool ok = WriteFile(h, data.data(), static_cast<DWORD>(data.size()), &put, nullptr) && put == data.size() &&
                    FlushFileBuffers(h);
    CloseHandle(h);
    return ok;
}

std::string utf8(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

}  // namespace

bool save_config(const std::wstring& path, const std::vector<std::pair<std::string, Json>>& values,
                 std::string& message) {
    std::string text;
    if (!read_file(path, text)) {
        message = "cannot read " + utf8(path) + " (error " + std::to_string(GetLastError()) + ")";
        return false;
    }
    Json doc;
    try {
        doc = Json::parse(text);
    } catch (const std::exception& e) {
        message = utf8(path) + " is not valid JSON (" + e.what() + "); nothing written";
        return false;
    }
    if (!doc.is_object()) {
        message = utf8(path) + " is not a JSON object; nothing written";
        return false;
    }
    const std::string old_preset = doc.contains("gameSettingsPreset") && doc["gameSettingsPreset"].is_string()
                                       ? doc["gameSettingsPreset"].get<std::string>()
                                       : std::string("(none)");
    // The game applies "gameSettings" only with the Custom preset ("'gameSettings' will not be applied Preset is not
    // set to 'Custom'"), so a save always switches to it; the values written are the ones the world uses now.
    doc["gameSettingsPreset"] = "Custom";
    Json& gs = doc["gameSettings"];
    if (!gs.is_object()) gs = Json::object();
    size_t changed = 0;
    for (const auto& [name, value] : values) {
        if (!gs.contains(name) || gs[name] != value) ++changed;
        gs[name] = value;
    }
    std::string out;
    try {
        out = doc.dump(1, '\t', false, Json::error_handler_t::replace);
    } catch (const std::exception& e) {
        message = std::string("cannot serialise the config: ") + e.what();
        return false;
    }
    out += '\n';
    const std::wstring tmp = path + L".kiln-tmp", backup = path + L".kiln-backup";
    if (!write_file(tmp, out)) {
        message = "cannot write " + utf8(tmp) + " (error " + std::to_string(GetLastError()) + ")";
        DeleteFileW(tmp.c_str());
        return false;
    }
    CopyFileW(path.c_str(), backup.c_str(), FALSE);  // best effort
    if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        message = "cannot replace " + utf8(path) + " (error " + std::to_string(GetLastError()) + ")";
        DeleteFileW(tmp.c_str());
        return false;
    }
    message = "saved " + std::to_string(values.size()) + " settings (" + std::to_string(changed) + " changed) to " +
              utf8(path) + "; gameSettingsPreset " + old_preset + " -> Custom; applies from the next server start";
    return true;
}

}  // namespace kset
