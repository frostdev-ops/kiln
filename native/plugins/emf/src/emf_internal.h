// SPDX-License-Identifier: MIT
// kiln-emf: loads unmodified EMF (Enshrouded Modding Framework 0.5.1, API v26, MIT) mods on Kiln. Shared state.
//
// Layout of the plugin:
//   plugin.cpp    kiln_plugin_main / on_ready: step aside when the real EMF runs, discover, order and load the mods,
//                 world ticks (where EMF's "after the step" callbacks run), status file
//   mods.cpp      mod.json, the legacy folder and kiln.profile, version ranges, dependency order, the load itself
//   settings.cpp  EMF mod settings: <configDir>/emf/<id>.ini (+ mirror to <game>/emf/config/), schema snapshots
//   api_core.cpp  the EmfApi table: log, worlds, components, entities, ticks, build, mod info, stubs
//   api_game.cpp  items, spawn, chat, saves, combat, teleport, world settings, HUD/notify
//   api_events.cpp on_event and mod programs through kiln.world
//   api_hooks.cpp resolve / hook / patterns / write_memory, the EMF signature -> sigdb alias table
//   commands.cpp  EMF commands from players' chat (kiln.chat) and <game>/emf/commands.txt
//   api_level.cpp v22: a player's XP and level through kiln.progression
//   api_components.cpp v23 change_component (a kiln.world program) and v25 E prompts (offers)
//   api_windows.cpp v24 mod windows and v26 pictures through kiln.ui (client); refused on a dedicated server
//   v26_model.*   the v22-v26 logic without the game (host tests: tests/run.sh)
//   seh.cpp       structured-exception guards around every call into a mod
// Docs: docs/kiln/emf.md (users), docs/re/emf-compat.md (how each function maps onto Kiln).
#pragma once
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "emf/emf_api.h"
#include "kiln_api.h"
#include "services/kiln_chat.h"
#include "services/kiln_client.h"
#include "services/kiln_combat.h"
#include "services/kiln_content.h"
#include "services/kiln_hooks.h"
#include "services/kiln_items.h"
#include "services/kiln_players.h"
#include "services/kiln_profile.h"
#include "services/kiln_saves.h"
#include "services/kiln_settings.h"
#include "services/kiln_spawn.h"
#include "services/kiln_ui.h"
#include "services/kiln_world.h"
#include "kiln_progression.h"

namespace kemf {

constexpr const char* kPluginId = "kiln-emf";
constexpr const char* kPluginVersion = "0.1.0";
constexpr uint32_t kEmfApiVersion = EMF_API_VERSION;  // 26

// ---------------------------------------------------------------------------------------------- core handles ----
extern const KilnApi* g_api;
extern KilnOwner g_owner;
extern uint32_t g_side;  // KILN_SIDE_*

struct Services {
    const KilnWorldApi* world = nullptr;
    const KilnClientApi* client = nullptr;
    const KilnHooksApi* hooks = nullptr;
    const KilnItemsApi* items = nullptr;
    const KilnContentApi* content = nullptr;
    const KilnSpawnApi* spawn = nullptr;
    const KilnChatApi* chat = nullptr;
    const KilnPlayersApi* players = nullptr;
    const KilnSavesApi* saves = nullptr;
    const KilnSettingsApi* settings = nullptr;
    const KilnCombatApi* combat = nullptr;
    const KilnUiApi* ui = nullptr;
    const KilnProfileApi* profile = nullptr;
    const KilnProgressionApi* progression = nullptr;
};
extern Services S;

struct Paths {
    std::filesystem::path game, kiln, config, state, logs, emf;  // emf = <game>/emf
    bool state_is_logs = false;                                   // core without KILN_PATH_STATE
};
extern Paths P;

// ---------------------------------------------------------------------------------------------- mods ------------
struct Dependency {
    std::string id;
    std::string range;  // "" = any
    bool optional = false;
};

struct Mod {
    std::string id, name, version, description, side;  // side: "server" | "client" | "both" | "" (unknown)
    std::vector<std::string> authors;
    std::filesystem::path folder;  // mod folder (folder mods) or emf/mods (loose DLLs)
    std::filesystem::path dll;     // empty for script mods
    std::string main;              // script mods: entry file ("main.lua")
    std::string source;            // EmfModInfo.source: "clock\" or "old_mod.dll"
    std::string origin;            // "legacy" | "profile"
    uint32_t emf_min = 1;
    bool loose = false;
    bool enabled_default = true;
    bool enabled = true;
    std::vector<Dependency> deps;
    std::string settings_json;  // the manifest's "settings" array (JSON text), "" when none
    uint32_t status = EMF_MOD_PENDING;
    std::string reason;  // why it failed / was rejected, in words
    HMODULE module = nullptr;
    KilnOwner owner = 0;  // "emf:<id>": log lines, content layers
    double load_ms = 0;
    std::atomic<uint32_t> faults{0};
    std::atomic<uint64_t> tick_ns{0};      // time in this mod's after-step callbacks (on_tick, run_on_world, events)
    std::atomic<uint64_t> tick_calls{0};
    std::atomic<uint64_t> tick_max_ns{0};  // the slowest single callback
};
// Adds one callback's time to its mod (plugin.cpp writes the totals into state/emf.json).
void account(Mod* m, uint64_t ns);
uint64_t qpc_ns();
extern std::vector<std::unique_ptr<Mod>> g_mods;  // stable pointers, never freed
extern std::mutex g_mods_mutex;

Mod* find_mod(std::string_view id);
Mod* mod_of_address(const void* address);  // the mod whose DLL contains `address`
Mod* caller_mod(const void* return_address);  // mod_of_address, else the mod running emf_mod_load / a callback
const char* mod_name(const Mod* m);         // "?" for none

// discover + order + load (mods.cpp). Returns the number of mods that loaded.
void discover_mods();
void load_mods(const EmfApi* table);
bool valid_mod_id(std::string_view id);

// ---------------------------------------------------------------------------------------------- calling context -
// What the current thread is doing on a mod's behalf. Set while kiln-emf runs a mod callback (or emf_mod_load);
// read by API functions that EMF allows only inside world callbacks (spawn, destroy, give_item) and by the command
// functions (command_reply, command_sender).
struct CommandRun;
struct Context {
    Mod* mod = nullptr;
    EmfWorld* world = nullptr;  // the world the callback was given (null: emf_mod_load, a program, another thread)
    bool world_cb = false;      // inside on_tick / run_on_world / on_event / a command / a result callback
    std::shared_ptr<CommandRun> cmd;
};
extern thread_local Context t_ctx;

struct ContextScope {
    Context saved;
    ContextScope(Mod* m, EmfWorld* w, bool world_cb, std::shared_ptr<CommandRun> cmd = nullptr) : saved(t_ctx) {
        t_ctx.mod = m;
        t_ctx.world = w;
        t_ctx.world_cb = world_cb;
        t_ctx.cmd = std::move(cmd);
    }
    ~ContextScope() { t_ctx = saved; }
};

// ---------------------------------------------------------------------------------------------- logging ---------
void logf(int level, const char* fmt, ...);                       // kiln-emf's own lines
void mod_log(const Mod* m, int level, std::string_view text);     // a line under the mod's owner
void mod_logf(const Mod* m, int level, const char* fmt, ...);
// "emf: <mod> called <fn>, not supported" once per mod and function; records it for the status file.
void unsupported(const Mod* m, const char* fn, const char* why = nullptr);
// A refusal line (EMF logs every refusal); `once` = at most once per mod+key.
void refuse(const Mod* m, const char* fn, const char* why, bool once = false);
void note_call(const char* fn);  // counts calls per function for the status file

// ---------------------------------------------------------------------------------------------- SEH -------------
// Runs fn(ctx) under a structured-exception guard. False (and the exception code) when it faulted; C++ exceptions
// that escape a mod (0xE06D7363) count as faults.
using ThunkFn = void (*)(void*);
bool seh_call(ThunkFn fn, void* ctx, unsigned long* code, uintptr_t* address);
bool seh_copy(void* dst, const void* src, size_t n);
bool seh_read_u32(uintptr_t a, uint32_t* out);
bool seh_read_u64(uintptr_t a, uint64_t* out);
bool seh_read_u16(uintptr_t a, uint16_t* out);
bool seh_read_u8(uintptr_t a, uint8_t* out);
// Calls a mod callback with the context set; on a fault logs it under the mod and returns false (the caller then
// disables the callback).
bool guarded(Mod* m, const char* what, ThunkFn fn, void* ctx);

// ---------------------------------------------------------------------------------------------- worlds ----------
uint8_t world_kind_of(EmfWorld* w);  // EMF_WORLD_* (0xFF unknown)
EmfWorld* world_of_kind(uint32_t kind);
bool on_thread_of(EmfWorld* w);
uint32_t server_session();
const void* component_of(EmfWorld* w, uint32_t hash, uint32_t entity, uint64_t* size);
void* template_component_of(EmfWorld* w, uint32_t hash, uint32_t entity, uint64_t* size);
uint32_t type_size(uint32_t qualified_hash);  // the game's reflected size of a keen type, 0 unknown
uint32_t fnv1a(std::string_view s);

// ---------------------------------------------------------------------------------------------- ticks -----------
// Runs after each step of a world of `kind`, on its thread (plugin.cpp calls these from Kiln's Post / Pre).
void dispatch_after_step(EmfWorld* w, uint8_t kind);  // run_on_world, events, commands, on_tick, setting changes
void dispatch_server_pre();                            // saves events, world-setting writes, timers, commands.txt
void install_ticks();

// ---------------------------------------------------------------------------------------------- the table -------
const EmfApi* api_table();
void fill_core(EmfApi* t);    // api_core.cpp
void fill_game(EmfApi* t);    // api_game.cpp
void fill_events(EmfApi* t);  // api_events.cpp
void fill_hooks(EmfApi* t);   // api_hooks.cpp
void fill_commands(EmfApi* t);  // commands.cpp
void fill_settings(EmfApi* t);  // settings.cpp
void fill_level(EmfApi* t);       // api_level.cpp (v22)
void fill_components(EmfApi* t);  // api_components.cpp (v23, v25)
void fill_windows(EmfApi* t);     // api_windows.cpp (v24, v26)

// tick hooks of the parts
void core_after_step(EmfWorld* w, uint8_t kind);
void events_after_step(EmfWorld* w, uint8_t kind);
void commands_after_step(EmfWorld* w, uint8_t kind);
void settings_after_step(EmfWorld* w, uint8_t kind);
void game_server_pre();
void commands_server_pre();
void events_on_ready();    // registers the kiln.world programs that on_event / register_program asked for
void commands_on_ready();  // subscribes to kiln.chat
void game_on_ready();
void hud_on_ready();
// v22-v26 parts
void level_after_step(EmfWorld* w, uint8_t kind);
void components_on_ready();  // before events_on_ready: the emf_components program and the E-prompt listener
void components_after_step(EmfWorld* w, uint8_t kind);
void windows_after_step(EmfWorld* w, uint8_t kind);
bool windows_want_frame();  // a mod window is open (kiln.ui demand)
// attribute_get / attribute_add of EMF_ATTR_XP* / EMF_ATTR_LEVEL (api_game.cpp hands them over)
bool level_attribute_get(Mod* m, EmfWorld* w, uint32_t entity, uint32_t attr, int32_t* out);
bool level_attribute_add(Mod* m, EmfWorld* w, uint32_t entity, uint32_t attr, int32_t delta);
// spawn as EMF's spawn does (api_game.cpp), for spawn_offer
uint32_t spawn_for(Mod* m, EmfWorld* w, const uint8_t guid[16], double x, double y, double z, const char* fn);
// a listener of kiln-emf itself for an engine event (no mod), before events_on_ready
bool events_add_internal(const char* name, uint32_t expected_size, EmfWorldEventCallback cb, void* user);
// the HUD and windows share kiln.ui's demand
void ui_refresh_demand();

// ---------------------------------------------------------------------------------------------- settings --------
void settings_load_mod(Mod* m);   // declares the manifest settings, seeds and mirrors the ini, reads `enabled`
bool settings_enabled(const Mod* m, bool fallback);
void settings_write_schema(const Mod* m);
std::string settings_summary(const Mod* m);  // for `settings <mod>`
bool settings_set_text(const char* mod, const char* key, const char* value, std::string* why);

// ---------------------------------------------------------------------------------------------- commands --------
struct CommandRun {
    std::string line;
    EmfCommandSender sender{};
    uint32_t replies = 0;   // chat lines sent back so far (at most 10 reach the chat)
    bool to_chat = false;   // reply into the sender's chat
};
void command_reply_text(const std::shared_ptr<CommandRun>& run, std::string_view text);
bool run_builtin_or_mod_command(EmfWorld* w, const std::shared_ptr<CommandRun>& run);

// ---------------------------------------------------------------------------------------------- status ----------
void write_status();
void mark_status_dirty();
extern std::string g_stepped_aside;  // why kiln-emf steps aside ("" = it runs)

// ---------------------------------------------------------------------------------------------- strings ---------
std::string vformat(const char* fmt, va_list ap);
std::string format(const char* fmt, ...);
std::string to_utf8(const std::wstring& w);
std::wstring to_wide(std::string_view s);
std::string lower(std::string_view s);
bool iequals(std::string_view a, std::string_view b);
inline KilnStr kstr(std::string_view s) { return KilnStr{s.data(), s.size()}; }
inline KilnStr kstr(const char* s) { return KilnStr{s, s ? std::strlen(s) : 0}; }
inline std::string_view sv(KilnStr s) { return std::string_view(s.ptr ? s.ptr : "", s.ptr ? s.len : 0); }
void copy_cstr(char* dst, size_t cap, std::string_view s);
bool read_file(const std::filesystem::path& p, std::string* out);
bool write_file_atomic(const std::filesystem::path& p, std::string_view data);
std::string path_utf8(const std::filesystem::path& p);
uint64_t now_ms();

}  // namespace kemf
