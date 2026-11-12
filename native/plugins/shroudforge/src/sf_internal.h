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

// Where ShroudForge's mod shortcuts get their key state this start (key_feed.cpp): source "kiln" (Kiln's host key
// feed passes records), "waiting" (the feed has begun, kiln.ui comes at Kiln's plugin step; nothing is passed yet),
// "helper" (ShroudForge's own shroudforge.exe --keyboard-input or input already running) or "none", and why.
struct KeySource {
    const char* source;
    std::string reason;
};
// Client, a runtime with all seven host key feed exports (the fork's kiln/abi/input_host.h, version 1): calls begin, and
// with kiln.ui v5 there subscribes to the game window's input records first ("kiln"). `plugin_step_later`: the early
// on_ready, where kiln.ui is not published yet; without shroudforge.exe and with kiln.hooks served (this start has a
// plugin step) the feed begins alone ("waiting") and key_feed_attach subscribes. Call before shroudforge_create, on the
// thread of on_ready; `runtime` must stay loaded. Anything missing: no export is called and nothing changes.
KeySource key_feed_start(const KilnApi* api, KilnOwner owner, HMODULE runtime, const std::filesystem::path& game, bool server,
                         bool plugin_step_later);
// The plugin step's on_ready (boot thread; the worker may run): a waiting feed subscribes to kiln.ui v5 now. True with the
// final word in *out ("kiln" or "none"); false when there was nothing to attach or the teardown came first.
bool key_feed_attach(const KilnApi* api, KilnOwner owner, KeySource* out);
// A record export faulted and forwarding stopped for the session. Lock-free: the status writer can report it
// even if the boot thread is still publishing its attach result; no status allocation or file I/O on the window thread.
bool key_feed_faulted();
// Unsubscribes (waiting for a record in flight) and ends the feed; no feed export is called afterwards. Call before
// shroudforge_destroy. Does nothing without a feed. True with the final word in *out when the feed was still waiting
// for the plugin step's attach.
bool key_feed_stop(KeySource* out);

}  // namespace ksf
