// SPDX-License-Identifier: MIT
// kiln-shroudforge: shared declarations (docs/kiln/shroudforge-compat.md).
#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <filesystem>
#include <string>

#include "kiln_api.h"

namespace ksf {

void logf(int32_t level, const char* fmt, ...);
std::string to_utf8(const std::wstring& w);
std::wstring to_wide(const std::string& s);

// ShroudForge's own settings file, <game>/shroudforge/config/modloader-config.json (read on each call, small file).
bool sf_module_enabled(const std::filesystem::path& root, const char* name);
std::filesystem::path sf_directory(const std::filesystem::path& root, const char* name, const char* fallback);

// ShroudForge's installed helper windows (client only), originally ported from windows-bootstrap/module_processes.cpp
// (MIT, bonsaibauer/shroudforge). Without the Modloader helper, retain its public Lua toggle event for Kiln.Ui.
// Never create/consume that event while the real helper owns it (or when self-hosted).
void start_helper_processes(const std::filesystem::path& root, bool server);
const char* helper_menu();  // helper | kiln | off | server | unavailable; also notices an exited helper
std::string helper_menu_key(const std::filesystem::path& root);
void stop_helper_processes();

// Engine hooks through kiln.hooks (host_hooks.cpp, kfc_host.h): the table kfc-runtime.dll accepts through
// KfcRuntimeSetHost, or null when kiln.hooks v2 is not available to this plugin.
struct KfcHost;
const KfcHost* host_table(const KilnApi* api, KilnOwner owner);

}  // namespace ksf
