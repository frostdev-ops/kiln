// SPDX-License-Identifier: MIT
// Chat history (GitHub issue 44, first built by bisdat): Up/Down in the game's HUD and Social chat field recall lines
// the player sent; commands are kept in kiln/data/kiln.ui/chat-history.json. Off unless kiln.json "ui":
// {"chatHistory": true}; diagnostics through the environment (chat_input_probe_install). Game side:
// docs/re/chat-input.md; player side: docs/kiln/chat-history.md. Every game call and read is scoped to the two chat
// call sites of TextInputProcess, on the game's UI thread; the window thread only queues keys (ChatInputSink).
#include <windows.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>
#include <vector>

#include "chat_history.h"
#include "chat_focus.h"
#include "chat_keys.h"
#include "chat_store.h"
#include "kiln_hook.hpp"
#include "ui.h"

namespace kui {
namespace {

kiln::RawHook g_input;
uintptr_t g_hud_ret = 0, g_social_ret = 0;
uintptr_t g_state_fn = 0, g_set_text_fn = 0;
std::atomic<bool> g_hud_seen{false}, g_social_seen{false};
std::atomic<bool> g_history_on{false};
std::atomic<bool> g_diagnose_on{false};
std::atomic<bool> g_trace_on{false};
std::atomic<uint32_t> g_key_log_count{0};
std::atomic<uint32_t> g_event_log_count{0};
std::atomic<uint32_t> g_recall_log_count{0};
// A literal user-edit signal, sent by WndProc. Do not infer manual edits from
// transient differences in the game's widget buffer between UI frames.
std::atomic<bool> g_manual_text_edit{false};
std::atomic<bool> g_window_blurred{false};
std::atomic<bool> g_ime_composing{false};  // window thread: between WM_IME_STARTCOMPOSITION and its end
chat_keys::Router g_keys;
chat_history::History g_history;
chat_history::DraftCapture g_hud_draft, g_social_draft;
chat_focus::Grace g_focus_grace;
std::filesystem::path g_history_file;
// UI-thread-only.
bool g_editing_in_frame = false;
bool g_hud_focus_logged = false, g_social_focus_logged = false;
std::string g_last_applied;
// SetText changes the editor. The caller-owned widget buffer is updated
// by the normal TextInputProcess, so verify only after that call returns.
bool g_set_text_pending = false;

constexpr uint64_t kMaxEditBytes = 1024;

// The game's layout, from sigdb (chatui.json, feature chat.ui.input; docs/re/chat-input.md), read once at install.
struct Layout {
    uint64_t state_hash = 0;  // TextInputState's key of a text field's state block
    uintptr_t edit = 0;       // state + edit: the field's editor (EditSetText's second argument)
    uintptr_t editing = 0;    // state + editing: a byte, set while the field has typing focus
    uintptr_t buffer = 0;     // widget + buffer: pointer to the caller's chat string (StringHead)
} L;

struct GameText { const char* data; uint64_t length; uint64_t hash; };
struct TextSlice { const char* data; uint64_t length; };
struct StringHead { const char* data; uint64_t length; uint64_t capacity; };  // the engine's UTF-8 string
static_assert(sizeof(GameText) == 24 && sizeof(StringHead) == 24 && sizeof(TextSlice) == 16);
// EditSetText's result on client 26430625: 8 bytes, {u8 error, u32 at +4}, written as one qword (an error) or as the
// two fields (error 0: the text was set). docs/re/chat-input.md. The buffer has 64 bytes so a later build that grows
// the result stays inside it until the RE notes are checked again.
struct EditResult { uint8_t error; uint8_t pad[3]; uint32_t detail; };
static_assert(sizeof(EditResult) == 8);
constexpr size_t kEditResultBuffer = 64;

template <class T> bool game_read(const void* p, T* out) {
    if (!p || reinterpret_cast<uintptr_t>(p) < 0x10000) return false;
    __try {
        std::memcpy(out, p, sizeof(T));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool game_copy(const char* p, char* out, size_t len) {
    if (!p || reinterpret_cast<uintptr_t>(p) < 0x10000 || len > 1024) return false;
    __try {
        std::memcpy(out, p, len);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// TextInputState(widget, hash, 1): the field's state block, as TextInputProcess itself asks for it each frame. Called
// only from the scoped input hook, for the widget the game is processing in this very call.
using StateFn = void* (__fastcall*)(void*, uint64_t, uint32_t);
void* game_state(void* widget) {
    if (!g_state_fn || !widget) return nullptr;
    __try {
        return reinterpret_cast<StateFn>(g_state_fn)(widget, L.state_hash, 1);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

// EditSetText(&result, editor, &slice): the engine keeps the cursor, selection and undo history right, instead of Kiln
// writing to a game buffer. The editor holds UTF-32; the slice is UTF-8, as the chat's own clear passes it.
using SetTextFn = void (__fastcall*)(void*, void*, const TextSlice*);
bool game_set_text(void* edit, const char* data, size_t length) {
    if (!g_set_text_fn || !edit || length > kMaxEditBytes) return false;
    const TextSlice slice{data, length};
    alignas(16) uint8_t result[kEditResultBuffer]{};
    __try {
        reinterpret_cast<SetTextFn>(g_set_text_fn)(result, edit, &slice);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    EditResult r{};
    std::memcpy(&r, result, sizeof r);
    return r.error == 0;
}
bool editing(void* state) {
    uint8_t yes = 0;
    return state && game_read(reinterpret_cast<const uint8_t*>(state) + L.editing, &yes) && yes != 0;
}

// The chat's widget points at the caller's chat string (widget + L.buffer): the UTF-8 line the caller sends on Enter
// and clears after. The editor's own text is UTF-32 and is never read. Both pointers and their lifetimes are the
// game's: every read is SEH-guarded and size-checked.
bool widget_text(void* widget, std::string* out) {
    uintptr_t caller_buffer = 0;
    if (!widget || !game_read(reinterpret_cast<const uint8_t*>(widget) + L.buffer, &caller_buffer)) return false;
    StringHead b{};
    if (!game_read(reinterpret_cast<const void*>(caller_buffer), &b) ||
        b.length > kMaxEditBytes || b.length > b.capacity || b.capacity > (1ull << 20))
        return false;
    if (b.length == 0) { out->clear(); return true; }
    char bytes[1024];
    if (!game_copy(b.data, bytes, static_cast<size_t>(b.length))) return false;
    out->assign(bytes, static_cast<size_t>(b.length));
    return true;
}

size_t controls(std::string_view line) {
    size_t n = 0;
    for (const unsigned char c : line)
        if (c < 0x20 || c == 0x7f) ++n;
    return n;
}

bool symbol(const char* name, uintptr_t* out) {
    *out = 0;
    return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK && *out;
}
// An environment switch: 1 or 0, else -1 (unset or anything else).
int env_switch(const char* name) {
    char env[8]{};
    const DWORD n = GetEnvironmentVariableA(name, env, sizeof env);
    if (n != 1) return -1;
    return env[0] == '1' ? 1 : env[0] == '0' ? 0 : -1;
}
bool env_enabled(const char* name) { return env_switch(name) == 1; }

// The player's switch: kiln.json "ui": {"chatHistory": true} (the launcher's Settings, "Kiln"), read once at start.
// Off when the file, the section or the key is missing or unreadable.
bool kiln_json_chat_history() {
    char buffer[4096]{};
    size_t length = 0;
    if (g_api->path(KILN_PATH_KILN, buffer, sizeof buffer, &length) != KILN_OK || !length || length >= sizeof buffer)
        return false;
    try {
        const std::u8string utf8(reinterpret_cast<const char8_t*>(buffer), length);
        std::ifstream in(std::filesystem::path(utf8) / "kiln.json", std::ios::binary);
        if (!in) return false;
        const auto doc = nlohmann::json::parse(in, nullptr, false, true);  // JSONC, as the core reads it
        if (!doc.is_object()) return false;
        const auto ui = doc.find("ui");
        if (ui == doc.end() || !ui->is_object()) return false;
        const auto on = ui->find("chatHistory");
        return on != ui->end() && on->is_boolean() && on->get<bool>();
    } catch (...) {
        return false;
    }
}
// <game>/kiln/data/kiln.ui/chat-history.json, beside imgui.ini (ui.cpp): kiln.ui's own data, per game folder.
std::filesystem::path history_path() {
    char buffer[4096]{};
    size_t length = 0;
    if (g_api->path(KILN_PATH_DATA, buffer, sizeof buffer, &length) != KILN_OK || !length || length >= sizeof buffer)
        return {};
    const std::u8string utf8(reinterpret_cast<const char8_t*>(buffer), length);
    return std::filesystem::path(utf8) / "kiln.ui" / "chat-history.json";
}
void load_history() {
    g_history_file = history_path();
    if (g_history_file.empty()) return;
    try {
        std::error_code ec;
        const auto size = std::filesystem::file_size(g_history_file, ec);
        if (ec || size > chat_store::kMaxFileBytes) return;  // none yet, or not ours
        std::ifstream in(g_history_file, std::ios::binary);
        std::string text(static_cast<size_t>(size), '\0');
        if (!in.read(text.data(), static_cast<std::streamsize>(text.size()))) return;
        g_history.load(chat_store::parse(text));
    } catch (...) {
        // History is optional; file problems must never interfere with chat.
    }
}
// The writer thread's write (chat_store::Saver): a temp file, then one rename over the old file.
void write_history_file(const std::string& text) {
    static std::atomic<bool> said{false};
    try {
        std::error_code ec;
        std::filesystem::create_directories(g_history_file.parent_path(), ec);
        const std::filesystem::path temp = g_history_file.wstring() + L".tmp";
        bool ok = false;
        {
            std::ofstream file(temp, std::ios::binary | std::ios::trunc);
            ok = file && file.write(text.data(), static_cast<std::streamsize>(text.size())) && file.flush();
        }
        if (ok && MoveFileExW(temp.c_str(), g_history_file.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            return;
        DeleteFileW(temp.c_str());
    } catch (...) {
    }
    if (!said.exchange(true)) logf(KILN_LOG_WARN, "ui: chat history: could not write chat-history.json (said once)");
}
// Created at the first save and never destroyed: a static destructor must not join a thread (chat_store.h).
std::atomic<chat_store::Saver*> g_saver{nullptr};
// UI thread: hands the persisted lines to the writer thread; never touches the disk itself.
void save_history() {
    if (g_history_file.empty()) return;
    chat_store::Saver* saver = g_saver.load(std::memory_order_acquire);
    if (!saver) {
        saver = new chat_store::Saver(&write_history_file);
        g_saver.store(saver, std::memory_order_release);
    }
    saver->request(g_history.persisted());
}

// UI-thread-only, before the underlying TextInputProcess.
// The live draft cache is refreshed after the original function returns.
void history_before(void* widget, bool hud) {
    void* state = game_state(widget);
    if (!editing(state)) return;
    std::string current;
    // The editor's own text is UTF-32 (docs/re/chat-input.md): the draft and the sent line are the caller's string.
    if (!widget_text(widget, &current)) return;
    // A widget readback can legitimately differ for a frame as Enshrouded
    // reconciles the native edit state. A mismatch alone does NOT mean the
    // player edited a recalled line: clearing the cursor here lost the draft
    // before Down could restore it. Only explicit user text-edit input resets.
    if (g_manual_text_edit.exchange(false, std::memory_order_acq_rel)) {
        const bool was_browsing = g_history.browsing();
        g_history.edited();
        g_last_applied.clear();
        if (was_browsing && g_trace_on.load(std::memory_order_relaxed))
            logf(KILN_LOG_INFO, "ui: chat history trace: navigation reset by user text edit");
    }
    chat_keys::Action action{};
    while (g_keys.take(action)) {
        const bool browsing_before = g_history.browsing();
        // Critical: before the input call, widget_text may still be empty
        // even when the player's unsent draft is visible. TextInputProcess
        // has already supplied the authoritative post-call snapshot during
        // previous frames. Use it ONLY when beginning an Up browse.
        const auto& draft = hud ? g_hud_draft : g_social_draft;
        const bool first_up = action == chat_keys::Action::Older && !browsing_before;
        const std::string_view source = first_up ? draft.source(current) : std::string_view(current);
        if (first_up && g_trace_on.load(std::memory_order_relaxed) &&
            g_recall_log_count.fetch_add(1) < 80)
            logf(KILN_LOG_INFO,
                 "ui: chat history trace: capture draft from_postcall=%u bytes=%zu precall_bytes=%zu",
                 draft.valid() ? 1u : 0u, source.size(), current.size());
        const std::string next = action == chat_keys::Action::Older
                                     ? g_history.older(source) : g_history.newer(current);
        // The widget may still expose the original draft even after the
        // last SetText was successfully displayed. Comparing to that stale
        // snapshot caused Down to skip restoring the draft. Use the last
        // successfully applied text while browsing instead.
        const bool changed = chat_history::needs_replacement(
            next, current, g_last_applied, browsing_before);
        const bool stale_widget = browsing_before && !g_last_applied.empty() &&
                                  current != g_last_applied;
        if (g_trace_on.load(std::memory_order_relaxed) && g_recall_log_count.fetch_add(1) < 80)
            logf(KILN_LOG_INFO,
                 "ui: chat history trace: recall %s entries=%zu changed=%u browsing_before=%u browsing_after=%u stale_widget=%u",
                 action == chat_keys::Action::Older ? "older" : "newer",
                 g_history.count(), changed ? 1u : 0u,
                 browsing_before ? 1u : 0u, g_history.browsing() ? 1u : 0u,
                 stale_widget ? 1u : 0u);
        if (!changed) continue;
        if (!game_set_text(reinterpret_cast<uint8_t*>(state) + L.edit, next.data(), next.size())) {
            g_keys.focus(false);
            g_history_on.store(false, std::memory_order_release);
            logf(KILN_LOG_WARN, "ui: chat history: edit replace failed; preview disabled for this process");
            return;
        }
        current = next;
        g_last_applied = next;
        g_set_text_pending = true;
        // Post-call check in history_after(): the game's TextInputProcess
        // first synchronizes its private edit with the outgoing widget string.
    }
}

// The submit result is set by TextInputProcess, before the caller sends and clears.
void history_after(void* widget, void* result, bool hud) {
    void* state = game_state(widget);
    if (!state) return;
    if (g_set_text_pending) {
        g_set_text_pending = false;
        std::string applied;
        if (!widget_text(widget, &applied) || applied != g_last_applied) {
            g_keys.focus(false);
            g_history_on.store(false, std::memory_order_release);
            logf(KILN_LOG_WARN,
                 "ui: chat history: SetText did not reach the chat widget after input processing; "
                 "preview disabled for this process");
            return;
        }
        if (g_trace_on.load(std::memory_order_relaxed) && g_recall_log_count.fetch_add(1) < 24)
            logf(KILN_LOG_INFO, "ui: chat history trace: engine edit replacement verified (widget readback)");
    }
    // The result's event is a u32 TextInputProcess zeroes on entry and sets to 1 (Enter) or 2 (Escape); the callers
    // compare the whole dword. Its low byte carries the same.
    uint8_t event = 0;
    if (game_read(result, &event) && event != 0) {
        (hud ? g_hud_draft : g_social_draft).clear(); // next edit session starts fresh
        // The line sent is the caller's chat string (the caller hands exactly these bytes to the send).
        std::string sent;
        const bool readable = widget_text(widget, &sent);
        // Only validity flags, lengths and counts: never the text, addresses or command names.
        if (g_trace_on.load(std::memory_order_relaxed) && g_event_log_count.fetch_add(1) < 24)
            logf(KILN_LOG_INFO,
                 "ui: chat history trace: %s event=%u widget_ok=%u widget_bytes=%zu widget_command=%u widget_valid=%u "
                 "widget_sensitive=%u widget_controls=%zu",
                 hud ? "HUD" : "Social", static_cast<unsigned>(event),
                 readable ? 1u : 0u, readable ? sent.size() : 0,
                 readable && !sent.empty() && sent.front() == '/' ? 1u : 0u,
                 readable && chat_history::History::valid(sent) ? 1u : 0u,
                 readable && chat_history::History::sensitive(sent) ? 1u : 0u,
                 readable ? controls(sent) : 0);
        if (event == 1 && readable && !g_diagnose_on.load(std::memory_order_relaxed)) {
            const size_t before = g_history.count();
            const bool persisted_changed = g_history.submit(sent);
            if (persisted_changed) save_history();
            if (g_trace_on.load(std::memory_order_relaxed) && g_event_log_count.fetch_add(1) < 24)
                logf(KILN_LOG_INFO, "ui: chat history trace: submit processed entries=%zu added=%u persisted=%u",
                     g_history.count(), g_history.count() != before ? 1u : 0u, persisted_changed ? 1u : 0u);
        }
        // Enter/escape end the active browse session even if focus briefly
        // lingers while the native widget finishes closing.
        if (event == 1 || event == 2) {
            if (event == 2) g_history.edited(); // Enter already calls Submit.
            g_manual_text_edit.store(false, std::memory_order_release);
            g_editing_in_frame = false; // do not rearm keys at the end of the close/send frame
            g_keys.focus(false);
        }
        g_last_applied.clear();
        return;
    }
    if (!editing(state)) return;
    g_editing_in_frame = true;
    // Refresh the live draft ONLY after TextInputProcess synchronizes the
    // caller-owned widget, and never while history browsing is active.
    if (!g_history.browsing()) {
        std::string fresh;
        if (widget_text(widget, &fresh) &&
            (fresh.empty() || chat_history::History::valid(fresh)))
            (hud ? g_hud_draft : g_social_draft).observe(fresh);
        else
            (hud ? g_hud_draft : g_social_draft).clear();
    }
    bool& logged = hud ? g_hud_focus_logged : g_social_focus_logged;
    if (!logged) {
        logged = true;
        logf(KILN_LOG_INFO, "ui: chat history: %s edit focus confirmed (no text logged)", hud ? "HUD" : "Social");
    }
    // No text-difference reset here. The next history_before observes the
    // WndProc's explicit text-edit signal, not a transient UI buffer snapshot.
}

// TextInputProcess returns &result in rax (MSVC hidden result pointer); every text field of the game passes through
// here, so the hook hands that back even though none of today's 14 callers reads it.
void* __fastcall hk_input(void* widget, void* result, const GameText* label, uint32_t mode) {
    const uintptr_t caller = g_input.caller();
    const bool hud = caller == g_hud_ret;
    const bool social = caller == g_social_ret;
    const bool scoped = hud || social;
    if (g_history_on.load(std::memory_order_relaxed) && scoped)
        history_before(widget, hud);
    void* const out = g_input.call<void*>(widget, result, label, mode);
    if (hud && !g_hud_seen.exchange(true))
        logf(KILN_LOG_INFO, "ui: chat input probe: native HUD chat call observed (call-site confirmation)");
    else if (social && !g_social_seen.exchange(true))
        logf(KILN_LOG_INFO, "ui: chat input probe: native Social chat call observed (call-site confirmation)");
    if (scoped && (g_history_on.load(std::memory_order_relaxed) ||
                   g_diagnose_on.load(std::memory_order_relaxed)))
        history_after(widget, result, hud);
    return out;
}

bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }

// The layout constants of feature chat.ui.input. The ranges only catch a capture gone wrong (the state block is 0x1990
// bytes and the widget about 0x280 on 26430625); sigdb's captures are what follows a game update.
bool read_layout() {
    int64_t lo = 0, hi = 0, edit = 0, editing = 0, buffer = 0;
    if (!cst("chatui/state.hash.lo", &lo) || !cst("chatui/state.hash.hi", &hi) || !cst("chatui/state.edit", &edit) ||
        !cst("chatui/state.editing", &editing) || !cst("chatui/widget.buffer", &buffer))
        return false;
    if (lo < 0 || lo > 0xffffffffll || hi < 0 || hi > 0xffffffffll || (lo | hi) == 0 || edit <= 0 || edit >= 0x100 ||
        editing <= edit || editing >= 0x10000 || buffer <= 0 || buffer >= 0x1000)
        return false;
    L.state_hash = (static_cast<uint64_t>(hi) << 32) | static_cast<uint64_t>(lo);
    L.edit = static_cast<uintptr_t>(edit);
    L.editing = static_cast<uintptr_t>(editing);
    L.buffer = static_cast<uintptr_t>(buffer);
    return true;
}
} // namespace

namespace {
// ---- window thread: input.cpp's chat sink (ui.h ChatInputSink), set only while history or its diagnostics run ----
// The window procedure has already folded raw input and key messages into one transition per key.

// Typed text ends a history browse (chat_keys::inserts_text); a transient difference in the widget's buffer never does.
void on_text(uint32_t ch) {
    if (chat_keys::inserts_text(ch) && g_history_on.load(std::memory_order_acquire) && g_keys.focused())
        g_manual_text_edit.store(true, std::memory_order_release);
}

bool on_key(uint32_t vk, uint32_t mods, bool down, bool captured) {
    if (!g_history_on.load(std::memory_order_acquire) && !g_diagnose_on.load(std::memory_order_acquire))
        return false;
    // While an IME composes, Up/Down pick its candidates and Backspace edits the composition: none of it is ours.
    const bool composing = g_ime_composing.load(std::memory_order_acquire);
    // Keys that change the text without a character (chat_keys::edits_text) end a browse as typing does.
    if (down && !captured && !composing && chat_keys::edits_text(vk, mods) &&
        g_history_on.load(std::memory_order_relaxed) && g_keys.focused())
        g_manual_text_edit.store(true, std::memory_order_release);
    const bool kept = !captured && !composing && g_history_on.load(std::memory_order_acquire) && g_keys.key(vk, mods, down);
    if (down && (vk == VK_UP || vk == VK_DOWN) && g_trace_on.load(std::memory_order_relaxed) &&
        g_key_log_count.fetch_add(1) < 24)
        logf(KILN_LOG_INFO, "ui: chat history trace: key %s mods=%u focus=%u captured=%u ime=%u consumed=%u",
             vk == VK_UP ? "Up" : "Down", mods, g_keys.focused() ? 1u : 0u, captured ? 1u : 0u, composing ? 1u : 0u,
             kept ? 1u : 0u);
    return kept;
}

void on_ime(bool composing) { g_ime_composing.store(composing, std::memory_order_release); }

void on_blur() {
    g_keys.focus(false);
    g_manual_text_edit.store(false, std::memory_order_release);
    g_ime_composing.store(false, std::memory_order_release);
    g_window_blurred.store(true, std::memory_order_release);
}

const ChatInputSink kSink{&on_key, &on_text, &on_ime, &on_blur};
} // namespace

// UI thread, after the game finishes each GameUiFrame.
void chat_input_frame_end() {
    if (!g_history_on.load(std::memory_order_acquire) && !g_diagnose_on.load(std::memory_order_acquire)) return;
    const bool active = g_editing_in_frame && !g_window_blurred.exchange(false);
    // Never swallow gameplay arrows when the native chat edit lacks focus.
    g_keys.focus(active);
    // A single game UI frame may temporarily fail to render either chat
    // control during a transition. That must not erase the saved draft.
    // Exit events explicitly reset the session above; for a real close or
    // a missing editor, forget the cursor only after sustained absence.
    const bool focus_expired = g_focus_grace.frame(active);
    if (focus_expired) {
        const bool was_browsing = g_history.browsing();
        g_history.edited();
        g_last_applied.clear();
        g_hud_draft.clear();
        g_social_draft.clear();
        g_manual_text_edit.store(false, std::memory_order_release);
        if (was_browsing && g_trace_on.load(std::memory_order_relaxed))
            logf(KILN_LOG_INFO, "ui: chat history trace: navigation reset after sustained loss of chat focus");
    }
    g_editing_in_frame = false;
}

// on_unload (ui.cpp): the last list is written and the writer thread joined, while the process still runs.
void chat_input_exit() {
    if (chat_store::Saver* saver = g_saver.load(std::memory_order_acquire)) saver->stop();
}

// Chat history is off unless the player switches it on (kiln.json ui.chatHistory). The environment is for
// diagnostics: KILN_CHAT_HISTORY=1/0 overrides the switch, KILN_CHAT_HISTORY_TRACE=1 adds counts to kiln.log (never
// text), KILN_CHAT_HISTORY_DIAG=1 watches keys and submits without taking a key, KILN_CHAT_INPUT_PROBE=1 only confirms
// the two call sites. Nothing is hooked while all of them are off.
bool chat_input_probe_install() {
    const bool diag = env_enabled("KILN_CHAT_HISTORY_DIAG");
    const int forced = env_switch("KILN_CHAT_HISTORY");
    const bool switched = forced < 0 && kiln_json_chat_history();
    const char* why = forced >= 0 ? "KILN_CHAT_HISTORY" : "kiln.json ui.chatHistory";
    const bool history = (forced == 1 || switched) && !diag;  // diagnostics take precedence: never swallow keys
    const bool probe = env_enabled("KILN_CHAT_INPUT_PROBE");
    if (!history && !diag && !probe) {
        logf(KILN_LOG_INFO, "ui: chat history off (%s)", forced == 0 ? "KILN_CHAT_HISTORY=0" : "kiln.json ui.chatHistory");
        return false;
    }
    if (!g_api->feature(KilnStr{"chat.ui.input", sizeof("chat.ui.input") - 1})) {
        logf(KILN_LOG_WARN, "ui: chat history is on (%s), but this game build lacks feature chat.ui.input: the chat field "
             "stays the game's own", history ? why : "a diagnostic");
        return false;
    }
    if (!read_layout()) {
        logf(KILN_LOG_WARN, "ui: chat history is on, but chat.ui.input's layout constants are unusable: the chat field "
             "stays the game's own");
        return false;
    }
    uintptr_t input = 0;
    if (!symbol("chatui/TextInputProcess", &input) ||
        !symbol("chatui/HudInputReturn", &g_hud_ret) ||
        !symbol("chatui/SocialInputReturn", &g_social_ret) || g_hud_ret == g_social_ret) {
        logf(KILN_LOG_WARN, "ui: chat input probe requested, but scoped call sites were not resolved");
        return false;
    }
    if ((history || diag) && (!symbol("chatui/TextInputState", &g_state_fn) ||
                              (history && !symbol("chatui/EditSetText", &g_set_text_fn)))) {
        logf(KILN_LOG_WARN, "ui: chat history unavailable: edit state/replace symbols unresolved");
        return false;
    }
    if (!g_input.install(g_api, g_owner, input, reinterpret_cast<void*>(&hk_input), "chatui/TextInputProcess")) {
        logf(KILN_LOG_WARN, "ui: chat input hook unavailable: %s", g_input.error());
        return false;
    }
    if (history || diag) input_set_chat_sink(&kSink);
    if (history) {
        load_history();
        g_trace_on.store(env_enabled("KILN_CHAT_HISTORY_TRACE"), std::memory_order_release);
        g_history_on.store(true, std::memory_order_release);
        logf(KILN_LOG_INFO, "ui: chat history on (%s): Up/Down in the HUD and Social chat, %zu command(s) kept from "
             "earlier games", why, g_history.count());
    } else if (diag) {
        g_trace_on.store(true, std::memory_order_release);
        g_diagnose_on.store(true, std::memory_order_release);
        logf(KILN_LOG_INFO, "ui: chat history diagnostic installed (read-only; no keys intercepted or text logged/persisted)");
    } else {
        logf(KILN_LOG_INFO, "ui: chat input probe installed for HUD and Social (read-only; no history keys intercepted)");
    }
    return true;
}
} // namespace kui
