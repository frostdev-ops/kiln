// SPDX-License-Identifier: MIT
// ShroudForge's helper windows, started the way its bootstrap starts them. Ported from ShroudForge's
// modules/entrypoints/windows-bootstrap/module_processes.cpp (MIT, Copyright (c) bonsaibauer and ShroudForge
// contributors; licenses/ShroudForge-LICENSE.txt). Each helper is `shroudforge.exe --<mode> --root <game> --game-pid
// <pid> --stop-event <name>`; it closes when the named event is set. Skipped on dedicated servers and when the module
// is switched off in ShroudForge's settings, as in ShroudForge.
#include <fstream>
#include <vector>

#include "nlohmann/json.hpp"
#include "sf_internal.h"

namespace ksf {

namespace {

nlohmann::json read_config(const std::filesystem::path& root) {
    try {
        std::ifstream in(root / L"shroudforge" / L"config" / L"modloader-config.json");
        if (in) return nlohmann::json::parse(in);
    } catch (...) {
    }
    return nlohmann::json::object();
}

struct Helper {
    const char* module;   // modloader-config.json modules.<module>.enabled
    const wchar_t* flag;  // shroudforge.exe mode
    const wchar_t* event_prefix;
    const char* what;
    HANDLE stop = nullptr;
    HANDLE process = nullptr;
};

Helper g_helpers[] = {
    {"modloaderUi", L"--module-ui", L"Local\\ShroudForge.ModloaderUI.", "Modloader window"},
    {"worldEditor", L"--world-editor-ui", L"Local\\ShroudForge.WorldEditorUI.", "World Editor window"},
};
HANDLE g_menu = nullptr;
bool g_menu_enabled = false, g_server = true;

}  // namespace

bool sf_module_enabled(const std::filesystem::path& root, const char* name) {
    try {
        return read_config(root).at("modules").at(name).value("enabled", true);
    } catch (...) {
        return true;
    }
}

std::filesystem::path sf_directory(const std::filesystem::path& root, const char* name, const char* fallback) {
    try {
        const auto paths = read_config(root).value("paths", nlohmann::json::object());
        const auto value = paths.value(name, std::string{});
        if (!value.empty()) {
            const std::filesystem::path configured(to_wide(value));
            return configured.is_absolute() ? configured : root / configured;
        }
    } catch (...) {
    }
    return root / to_wide(fallback);
}

// Same auto-reset event as SF's public shroudforge.ui.toggle_modloader(). Only Kiln.Ui waits on it, and only
// after we know no helper is running. In self-hosted mode start_helper_processes is never called.
const char* helper_menu() {
    if (g_server) return "server";
    if (!g_menu_enabled) return "off";
    auto& h = g_helpers[0];
    if (h.process && WaitForSingleObject(h.process, 0) == WAIT_OBJECT_0) {
        CloseHandle(h.process);
        h.process = nullptr;
        if (h.stop) CloseHandle(h.stop), h.stop = nullptr;
        logf(KILN_LOG_INFO, "ShroudForge's Modloader window exited; its shortcut now opens Kiln's ShroudForge page");
    }
    if (h.process) return "helper";
    if (!g_menu) {
        const auto name = L"Local\\ShroudForge.ModMenu.Open." + std::to_wstring(GetCurrentProcessId());
        g_menu = CreateEventW(nullptr, FALSE, FALSE, name.c_str());
        if (!g_menu) {
            logf(KILN_LOG_WARN, "ShroudForge menu event could not be created (error %lu); use Kiln's Mods entry", GetLastError());
            return "unavailable";
        }
    }
    return "kiln";
}

// modules.modloaderUi.toggleKey: missing is ShroudForge's default (SDL:66, F9) and "" is no key. Any other value (null,
// a number) ShroudForge itself refuses ("Hotkey must be an SDL binding string"): no key then either, never F9 for a
// player who may have meant none.
std::string helper_menu_key(const std::filesystem::path& root) {
    try {
        const auto config = read_config(root);
        const auto& ui = config.at("modules").at("modloaderUi");
        const auto key = ui.find("toggleKey");
        if (key == ui.end()) return "SDL:66";
        if (key->is_string()) return key->get<std::string>();
        logf(KILN_LOG_WARN, "ShroudForge's modloaderUi.toggleKey is %s, not a key (ShroudForge refuses it too): no key opens "
                            "its menu; set one in ShroudForge's settings, or use Kiln's Mods entry", key->type_name());
        return "";
    } catch (...) { return "SDL:66"; }
}

void start_helper_processes(const std::filesystem::path& root, bool server) {
    g_server = server;
    g_menu_enabled = !server && sf_module_enabled(root, "modloaderUi");
    if (server) return;  // ShroudForge shows no windows in the dedicated server process
    const auto exe = root / L"shroudforge" / L"shroudforge.exe";
    std::error_code ec;
    if (!std::filesystem::is_regular_file(exe, ec)) {
        logf(KILN_LOG_INFO, "shroudforge.exe is not installed: use Kiln's Mods > ShroudForge for switches and settings (no World Editor window)");
        helper_menu();
        return;
    }
    const DWORD pid = GetCurrentProcessId();
    for (auto& h : g_helpers) {
        if (!sf_module_enabled(root, h.module)) {
            logf(KILN_LOG_INFO, "%s: switched off in ShroudForge's settings", h.what);
            continue;
        }
        const std::wstring event_name = std::wstring(h.event_prefix) + std::to_wstring(pid);
        h.stop = CreateEventW(nullptr, TRUE, FALSE, event_name.c_str());
        if (!h.stop) continue;
        std::wstring command = L"\"" + exe.wstring() + L"\" " + h.flag + L" --root \"" + root.wstring() +
                               L"\" --game-pid " + std::to_wstring(pid) + L" --stop-event \"" + event_name + L"\"";
        std::vector<wchar_t> line(command.begin(), command.end());
        line.push_back(0);
        STARTUPINFOW si{sizeof si};
        PROCESS_INFORMATION pi{};
        if (!CreateProcessW(exe.c_str(), line.data(), nullptr, nullptr, FALSE,
                            CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, nullptr, root.c_str(), &si, &pi)) {
            logf(KILN_LOG_WARN, "%s could not be started (error %lu)", h.what, GetLastError());
            CloseHandle(h.stop);
            h.stop = nullptr;
            continue;
        }
        CloseHandle(pi.hThread);
        h.process = pi.hProcess;
        logf(KILN_LOG_INFO, "%s started (pid %lu); ShroudForge's shortcut toggles it", h.what, pi.dwProcessId);
    }
    helper_menu();
}

void stop_helper_processes() {
    g_menu_enabled = false;
    if (g_menu) CloseHandle(g_menu), g_menu = nullptr;
    for (auto& h : g_helpers) {
        if (h.stop) SetEvent(h.stop);
        if (h.process) {
            if (WaitForSingleObject(h.process, 3000) != WAIT_OBJECT_0) TerminateProcess(h.process, 1);
            CloseHandle(h.process);
            h.process = nullptr;
        }
        if (h.stop) CloseHandle(h.stop), h.stop = nullptr;
    }
}

}  // namespace ksf
