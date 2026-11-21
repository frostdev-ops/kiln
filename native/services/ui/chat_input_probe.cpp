// SPDX-License-Identifier: MIT
// Chat history (GitHub issue 44, first built by bisdat): Up/Down in the game's HUD and Social chat field recall lines
// the player sent; commands are kept in kiln/data/kiln.ui/chat-history.json. Off unless kiln.json "ui":
// {"chatHistory": true}; Tab completion (pull request 73) is on unless {"chatCompletion": false}; diagnostics through the environment (chat_input_probe_install). Game side:
// docs/re/chat-input.md; player side: docs/kiln/chat-history.md. Every game call and read is scoped to the two chat
// call sites of TextInputProcess, on the game's UI thread; the window thread only queues keys (ChatInputSink).
#include <windows.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <algorithm>
#include <deque>
#include <mutex>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "chat_history.h"
#include "chat_search.h"
#include "chat_completion_text.h"
#include "chat_focus.h"
#include "chat_keys.h"
#include "chat_setting.h"
#include "chat_store.h"
#include "kiln_hook.hpp"
#include "ui.h"

namespace kui {
std::atomic<uint32_t> g_chat_completion_demand{0};
uint32_t g_chat_completion_cleanup_frames = 0; // UI thread: let the overlay clear hover state after chat closes
namespace {

kiln::RawHook g_input;
uintptr_t g_hud_ret = 0, g_social_ret = 0;
uintptr_t g_state_fn = 0, g_set_text_fn = 0;
std::atomic<bool> g_hud_seen{false}, g_social_seen{false};
std::atomic<bool> g_history_on{false};
std::atomic<bool> g_completion_on{false};
std::atomic<bool> g_search_on{false}; // search geometry may fail independently of history
// Read only on the game's UI thread. Never let the overlay render thread
// dereference a game widget or its native editor.
uintptr_t g_rect_fn = 0;
int64_t g_frame_arg = 0, g_viewport = 0, g_ppu = 0;
bool g_rect_metrics = false;
using RectFn = const float*(__fastcall*)(void*);
struct CompletionSnapshot {
    bool focused = false;
    uint32_t field = 0, revision = 0, cursor = 0;
    std::string text;
    float raw[4]{}, pixels[4]{};
    bool has_rect = false;
};
struct PendingEdit {
    uint32_t field = 0, revision = 0;
    std::string expected, replacement;
};
std::mutex g_completion_mu;
CompletionSnapshot g_completion_snapshot;
std::deque<chat_keys::Action> g_completion_actions;
PendingEdit g_pending_edit;
bool g_pending_valid = false;
std::atomic<uint32_t> g_completion_edit_log_count{0};

// No chat text, player identity, or candidate names in these diagnostics.
void completion_edit_log(const char* stage, uint32_t a, uint32_t b = 0, uint32_t c = 0) {
    if (g_completion_edit_log_count.fetch_add(1, std::memory_order_relaxed) < 24)
        logf(KILN_LOG_INFO, "ui: chat completion: edit %s a=%u b=%u c=%u", stage, a, b, c);
}

void completion_action(chat_keys::Action action) {
    bool queued = false;
    {
        std::lock_guard lock(g_completion_mu);
        if (g_completion_actions.size() < 32) {
            g_completion_actions.push_back(action);
            queued = true;
        }
    }
    completion_edit_log("action-queued", static_cast<uint32_t>(action), queued ? 1u : 0u);
}

// Unicode codepoints are the game's UTF-32 cursor units. We only complete
// at the end of the current line; cursor-in-middle is deferred to a later PR.
uint32_t codepoints(std::string_view bytes) {
    uint32_t n = 0;
    for (unsigned char c : bytes) if ((c & 0xC0) != 0x80) ++n;
    return n;
}

bool safe_line(std::string_view bytes) {
    // Snapshotting an empty editor must clear old completion candidates.
    // History::valid alone intentionally rejects empty history entries.
    return chat_completion_text::valid(bytes);
}

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
void invalidate_completion_snapshot() {
    std::lock_guard lock(g_completion_mu);
    g_completion_snapshot.focused = false;
    g_completion_snapshot.has_rect = false;
    g_pending_valid = false;
    g_completion_actions.clear();
    g_keys.completion(false, false, false);
}

chat_history::History g_history;
// UI thread owns the search; the render thread reads only a locked snapshot.
std::mutex g_search_mu;
chat_search::Search g_search;
uint32_t g_search_field = 0, g_search_revision = 0, g_search_cursor = 0;
std::string g_search_original;
bool g_search_suppress_completion = false;
struct SearchClick { uint32_t field, revision, generation, index; };
std::optional<SearchClick> g_search_click;
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
// Completion-specific offsets are captured by chat.ui.completion in sigdb.
// History's existing chat.ui.input layout remains independent.

// The game's layout, from sigdb (chatui.json, feature chat.ui.input; docs/re/chat-input.md), read once at install.
struct Layout {
    uint64_t state_hash = 0;  // TextInputState's key of a text field's state block
    uintptr_t edit = 0;       // state + edit: the field's editor (EditSetText's second argument)
    uintptr_t editing = 0;    // state + editing: a byte, set while the field has typing focus
    uintptr_t buffer = 0;     // widget + buffer: pointer to the caller's chat string (StringHead)
    uintptr_t node = 0;       // widget + node: layout for UiNodeRect, captured from TextInputState
    uintptr_t cursor = 0;     // editor + cursor: UTF-32 cursor, captured twice from EditSetText
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

// The native UI thread owns every game pointer. Overlay and network code
// receive only value snapshots and may queue a full-line replacement.
// Keep Win32 SEH isolated from the std::string and lock lifetime in
// completion_capture. ClangCL 19's x86 assembly printer crashed while
// compiling the combined SEH/C++ function in CI runs 44269918037 and
// 44270356834. This is the same guarded game call and 4-float copy.
__declspec(noinline) bool game_node_rect(uintptr_t node, float* rect) {
    if (!node || !rect || !g_rect_fn) return false;
    __try {
        const float* raw = reinterpret_cast<RectFn>(g_rect_fn)(reinterpret_cast<void*>(node));
        return raw && game_copy(reinterpret_cast<const char*>(raw), reinterpret_cast<char*>(rect),
                                4 * sizeof(float));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void completion_capture(void* widget, void* state, bool hud) {
    if (!(g_completion_on.load(std::memory_order_acquire) || g_search_on.load(std::memory_order_acquire)) ||
        !editing(state)) return;
    std::string line;
    if (!widget_text(widget, &line) ||
        (!line.empty() && !chat_history::History::valid(line))) {
        invalidate_completion_snapshot();
        return;
    }
    uint32_t cursor = 0;
    if (!game_read(reinterpret_cast<const uint8_t*>(state) + L.edit + L.cursor, &cursor) ||
        cursor > codepoints(line)) {
        invalidate_completion_snapshot();
        return;
    }
    uintptr_t node = 0;
    float rect[4]{};
    const bool valid = g_rect_fn && game_read(reinterpret_cast<const uint8_t*>(widget) + L.node, &node) &&
        node && game_node_rect(node, rect);
    if (!valid || !std::isfinite(rect[0]) || !std::isfinite(rect[1]) ||
        !std::isfinite(rect[2]) || !std::isfinite(rect[3]) ||
        rect[2] <= 0 || rect[3] <= 0 || rect[2] > 4000 || rect[3] > 4000) {
        invalidate_completion_snapshot();
        return;
    }
    std::lock_guard lock(g_completion_mu);
    auto& snap = g_completion_snapshot;
    const uint32_t field = hud ? 1u : 2u;
    if (!snap.focused || snap.field != field || snap.text != line || snap.cursor != cursor)
        ++snap.revision;
    snap.focused = true; snap.field = field; snap.text = std::move(line); snap.cursor = cursor;
    for (int i = 0; i < 4; ++i) snap.raw[i] = rect[i];
    snap.has_rect = true;
}

void completion_before(void* widget, bool hud) {
    if (!g_completion_on.load(std::memory_order_acquire)) return;
    if (g_keys.searching()) { // pending Tab edits never race a Ctrl+R search
        std::lock_guard lock(g_completion_mu);
        g_pending_valid = false;
        g_completion_actions.clear();
        g_keys.completion(false, false, false);
        return;
    }
    void* state = game_state(widget);
    if (!editing(state)) return;
    PendingEdit pending;
    bool queued = false, matching_snapshot = false;
    {
        std::lock_guard lock(g_completion_mu);
        if (g_pending_valid) {
            pending = std::move(g_pending_edit);
            g_pending_valid = false;
            queued = true;
            const auto& snap = g_completion_snapshot;
            // The last completed TextInputProcess provided this authoritative
            // UTF-8 text and cursor. The caller-owned StringHead is NOT a
            // reliable source before the next TextInputProcess runs.
            matching_snapshot = snap.has_rect &&
                chat_completion_text::can_apply_editor_snapshot(
                    snap.field, snap.revision, snap.text, snap.cursor,
                    pending.field, pending.revision, pending.expected,
                    snap.focused, true, false, false);
        }
    }
    if (queued) {
        const bool same_field = pending.field == (hud ? 1u : 2u);
        const bool no_user_edit = !g_manual_text_edit.load(std::memory_order_acquire) &&
            !g_ime_composing.load(std::memory_order_acquire) &&
            !g_window_blurred.load(std::memory_order_acquire);
        uint32_t cursor = 0;
        const bool cursor_matches = game_read(
            reinterpret_cast<const uint8_t*>(state) + L.edit + L.cursor, &cursor) &&
            cursor == codepoints(pending.expected);
        const bool valid_replacement = safe_line(pending.replacement) &&
            !pending.replacement.empty() && pending.replacement.front() == '/';
        const bool allowed = same_field && matching_snapshot && no_user_edit &&
            cursor_matches && valid_replacement;
        const bool applied = allowed && game_set_text(
            reinterpret_cast<uint8_t*>(state) + L.edit,
            pending.replacement.data(), pending.replacement.size());
        completion_edit_log("apply",
            (same_field ? 1u : 0u) | (matching_snapshot ? 2u : 0u) |
            (no_user_edit ? 4u : 0u) | (cursor_matches ? 8u : 0u) |
            (valid_replacement ? 16u : 0u), applied ? 1u : 0u);
        if (applied) {
            g_last_applied = pending.replacement;
            g_set_text_pending = true; // post-call widget verification in history_after
            g_history.edited();
        }
    }
    if (!g_history_on.load(std::memory_order_acquire)) {
        chat_keys::Action action{};
        while (g_keys.take(action))
            if (action != chat_keys::Action::Older && action != chat_keys::Action::Newer)
                completion_action(action);
    }
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

// The player's switches: kiln.json "ui": {"chatHistory": true} or {"chatCompletion": false} (the launcher's Settings,
// "Kiln"), read once at start. The setting's default (`fallback`) when the file, the section or the key is missing or
// unreadable (chat_setting.h).
bool kiln_json_chat_option(const char* key, bool fallback) {
    char buffer[4096]{};
    size_t length = 0;
    if (g_api->path(KILN_PATH_KILN, buffer, sizeof buffer, &length) != KILN_OK || !length || length >= sizeof buffer)
        return fallback;
    try {
        const std::u8string utf8(reinterpret_cast<const char8_t*>(buffer), length);
        std::ifstream in(std::filesystem::path(utf8) / "kiln.json", std::ios::binary);
        if (!in) return fallback;
        const std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        return chat_setting(text, key, fallback);
    } catch (...) {
        return fallback;
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

// All search model changes and the sole EditSetText call occur on the game UI
// thread. WndProc queues UTF-16 input and keys; no native game pointers cross threads.
bool search_event(chat_keys::Action action, uint32_t character, void* state, bool hud) {
    using A = chat_keys::Action;
    if (static_cast<uint32_t>(action) < static_cast<uint32_t>(A::SearchStart)) return false;
    std::lock_guard search_lock(g_search_mu);
    const uint32_t field = hud ? 1u : 2u;
    if (action == A::SearchStart) {
        std::lock_guard completion_lock(g_completion_mu);
        const auto& snap = g_completion_snapshot;
        if (!g_search_on.load(std::memory_order_acquire) || !g_keys.focused() ||
            !snap.focused || !snap.has_rect || snap.field != field || snap.text.size() > 1024) {
            g_keys.search(false);
            return true;
        }
        g_search.begin(g_history.entries());
        g_search_original = snap.text;
        g_search_field = field;
        g_search_revision = snap.revision;
        g_search_cursor = snap.cursor;
        g_search_suppress_completion = false;
        g_search_click.reset();
        g_keys.search(true);
        g_keys.completion(false, false, false);
        return true;
    }
    if (!g_search.active() || field != g_search_field) return true;
    switch (action) {
        case A::SearchOlder: g_search.older(); break;
        case A::SearchNewer: g_search.newer(); break;
        case A::SearchCharacter: g_search.append_utf16(character, g_history.entries()); break;
        case A::SearchBackspace: g_search.backspace(g_history.entries()); break;
        case A::SearchCancel:
            g_search.cancel(); g_keys.search(false); g_search_click.reset(); break;
        case A::SearchAccept: {
            const auto* match = g_search.chosen();
            if (!match) break; // no match: leave panel open, never send
            bool valid = editing(state) && !g_ime_composing.load(std::memory_order_acquire) &&
                !g_window_blurred.load(std::memory_order_acquire) &&
                !g_manual_text_edit.load(std::memory_order_acquire) && g_keys.focused();
            {
                std::lock_guard completion_lock(g_completion_mu);
                const auto& snap = g_completion_snapshot;
                valid = valid && snap.focused && snap.field == g_search_field &&
                    snap.revision == g_search_revision && snap.text == g_search_original;
            }
            uint32_t cursor = 0;
            valid = valid && game_read(reinterpret_cast<const uint8_t*>(state) + L.edit + L.cursor, &cursor) &&
                cursor == g_search_cursor && chat_history::History::valid(*match);
            const bool applied = valid && game_set_text(reinterpret_cast<uint8_t*>(state) + L.edit,
                                                        match->data(), match->size());
            if (applied) {
                g_last_applied = *match;
                g_set_text_pending = true; // history_after verifies the widget after TextInputProcess
                g_history.edited();
                g_search_suppress_completion = true; // until the next real manual edit
            }
            g_search.cancel();
            g_keys.search(false);
            g_search_click.reset();
            if (!applied) logf(KILN_LOG_WARN, "ui: chat search: accept refused (editor verification); no text logged");
            break;
        }
        default: break;
    }
    return true;
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
        std::lock_guard search_lock(g_search_mu);
        g_search_suppress_completion = false;
        const bool was_browsing = g_history.browsing();
        g_history.edited();
        g_last_applied.clear();
        if (was_browsing && g_trace_on.load(std::memory_order_relaxed))
            logf(KILN_LOG_INFO, "ui: chat history trace: navigation reset by user text edit");
    }
    // Process input before a queued mouse click. If the player typed in the
    // same frame, search's result generation changes and the stale click
    // cannot select a different entry from the new result list.
    chat_keys::Action action{};
    uint32_t character = 0;
    while (g_keys.take(action, &character)) {
        if (search_event(action, character, state, hud)) continue;
        if (action != chat_keys::Action::Older && action != chat_keys::Action::Newer) {
            completion_action(action);
            continue;
        }
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
            g_search_on.store(false, std::memory_order_release);
            g_keys.history(false);
            g_keys.search_enabled(false);
            logf(KILN_LOG_WARN, "ui: chat history: edit replace failed; preview disabled for this process");
            return;
        }
        current = next;
        g_last_applied = next;
        g_set_text_pending = true;
        // Post-call check in history_after(): the game's TextInputProcess
        // first synchronizes its private edit with the outgoing widget string.
    }
    // Mouse selection is a queued index, never an edit on the render thread.
    // Both the clicked snapshot and the current result list must agree.
    bool clicked = false;
    {
        std::lock_guard lock(g_search_mu);
        const auto selection = g_search_click;
        g_search_click.reset();
        clicked = selection && g_search.active() && selection->field == (hud ? 1u : 2u) &&
            selection->field == g_search_field && selection->revision == g_search_revision &&
            g_search.select_at_generation(selection->index, selection->generation);
    }
    if (clicked) search_event(chat_keys::Action::SearchAccept, 0, state, hud);
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
            g_search_on.store(false, std::memory_order_release);
            g_keys.history(false);
            g_keys.search_enabled(false);
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
        if (event == 1 && readable && g_history_on.load(std::memory_order_relaxed) && !g_diagnose_on.load(std::memory_order_relaxed)) {
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
    completion_capture(widget, state, hud);
    if (g_search_on.load(std::memory_order_relaxed)) {
        std::lock_guard search_lock(g_search_mu);
        if (g_search.active()) {
            std::lock_guard completion_lock(g_completion_mu);
            const auto& snap = g_completion_snapshot;
            if (!snap.focused || snap.field != g_search_field ||
                snap.text != g_search_original || snap.cursor != g_search_cursor) {
                g_search.cancel();
                g_keys.search(false);
                g_search_click.reset();
            }
        }
    }
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
    if (scoped && g_completion_on.load(std::memory_order_relaxed)) completion_before(widget, hud);
    if (g_history_on.load(std::memory_order_relaxed) && scoped)
        history_before(widget, hud);
    void* const out = g_input.call<void*>(widget, result, label, mode);
    if (hud && !g_hud_seen.exchange(true))
        logf(KILN_LOG_INFO, "ui: chat input probe: native HUD chat call observed (call-site confirmation)");
    else if (social && !g_social_seen.exchange(true))
        logf(KILN_LOG_INFO, "ui: chat input probe: native Social chat call observed (call-site confirmation)");
    if (scoped && (g_history_on.load(std::memory_order_relaxed) ||
                   g_completion_on.load(std::memory_order_relaxed) ||
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

// Completion's editor and geometry offsets are optional. A game version that
// fails either capture disables completion; history keeps its verified layout.
bool read_completion_layout() {
    if (!g_api->feature(KilnStr{"chat.ui.completion", sizeof("chat.ui.completion") - 1}))
        return false;
    int64_t node = 0, cursor = 0;
    if (!cst("chatui/widget.node", &node) || !cst("chatui/edit.cursor", &cursor))
        return false;
    if (node <= 0 || node >= 0x200 || cursor <= 0 || cursor >= 0x2000)
        return false;
    L.node = static_cast<uintptr_t>(node);
    L.cursor = static_cast<uintptr_t>(cursor);
    return true;
}
} // namespace

namespace {
// ---- window thread: input.cpp's chat sink (ui.h ChatInputSink), set only while history or its diagnostics run ----
// The window procedure has already folded raw input and key messages into one transition per key.

// Typed text ends a history browse (chat_keys::inserts_text); a transient difference in the widget's buffer never does.
bool on_text(uint32_t ch) {
    // Tab's WM_CHAR is separate from the raw key. Swallow it only if the
    // shared router took that key for completion. Plain chat is unchanged.
    if (ch == '\t') {
        if (g_keys.take_tab_character()) return true;
        // Some Proton keyboard routes deliver WM_CHAR for Tab without a
        // matching raw VK_TAB event (input.cpp skips WM_KEYDOWN once raw
        // keyboard input was seen). Recover ONLY inside a focused native
        // chat with live suggestions; never steal ordinary chat or Alt+Tab.
        if (!g_ime_composing.load(std::memory_order_acquire) &&
            !g_diagnose_on.load(std::memory_order_acquire)) {
            uint32_t mods = 0;
            if (GetKeyState(VK_CONTROL) & 0x8000) mods |= 1u;
            if (GetKeyState(VK_SHIFT) & 0x8000) mods |= 2u;
            if (GetKeyState(VK_MENU) & 0x8000) mods |= 4u;
            if (g_keys.key(VK_TAB, mods, true)) {
                g_keys.take_tab_character(); // this WM_CHAR was the Tab
                static std::atomic<unsigned> reported{0};
                if (reported.fetch_add(1, std::memory_order_relaxed) < 4)
                    logf(KILN_LOG_INFO, "ui: chat completion: recovered Tab through WM_CHAR (focused suggestions)");
                return true;
            }
        }
    }
    if (g_search_on.load(std::memory_order_acquire) && g_keys.search_character(ch)) return true;
    if (chat_keys::inserts_text(ch) && g_history_on.load(std::memory_order_acquire) &&
        g_keys.focused() && !g_keys.searching())
        g_manual_text_edit.store(true, std::memory_order_release);
    return false;
}

bool on_key(uint32_t vk, uint32_t mods, bool down, bool captured) {
    if (!g_history_on.load(std::memory_order_acquire) &&
        !g_completion_on.load(std::memory_order_acquire) &&
        !g_diagnose_on.load(std::memory_order_acquire)) return false;
    // While an IME composes, Up/Down pick its candidates and Backspace edits the composition: none of it is ours.
    const bool composing = g_ime_composing.load(std::memory_order_acquire);
    // Keys that change the text without a character (chat_keys::edits_text) end a browse as typing does.
    if (down && !captured && !composing && !g_keys.searching() && chat_keys::edits_text(vk, mods) &&
        g_history_on.load(std::memory_order_relaxed) && g_keys.focused())
        g_manual_text_edit.store(true, std::memory_order_release);
    const bool kept = !captured && !composing &&
        (g_history_on.load(std::memory_order_acquire) || g_completion_on.load(std::memory_order_acquire)) &&
        g_keys.key(vk, mods, down);
    if (down && vk == VK_TAB && g_completion_on.load(std::memory_order_relaxed)) {
        static std::atomic<unsigned> reported{0};
        if (reported.fetch_add(1, std::memory_order_relaxed) < 6)
            logf(KILN_LOG_INFO,
                 "ui: chat completion: Tab keydown mods=%u focus=%u captured=%u ime=%u consumed=%u",
                 mods, g_keys.focused() ? 1u : 0u, captured ? 1u : 0u, composing ? 1u : 0u, kept ? 1u : 0u);
    }
    if (down && (vk == VK_UP || vk == VK_DOWN) && g_trace_on.load(std::memory_order_relaxed) &&
        g_key_log_count.fetch_add(1) < 24)
        logf(KILN_LOG_INFO, "ui: chat history trace: key %s mods=%u focus=%u captured=%u ime=%u consumed=%u",
             vk == VK_UP ? "Up" : "Down", mods, g_keys.focused() ? 1u : 0u, captured ? 1u : 0u, composing ? 1u : 0u,
             kept ? 1u : 0u);
    return kept;
}

void on_ime(bool composing) {
    g_ime_composing.store(composing, std::memory_order_release);
    // IME may handle WM_IME_COMPOSITION directly. Close search on composition
    // start instead of risking one key reaching an editor behind the panel.
    if (composing) g_keys.ime_started();
}

void on_blur() {
    g_keys.focus(false);
    g_manual_text_edit.store(false, std::memory_order_release);
    g_ime_composing.store(false, std::memory_order_release);
    g_window_blurred.store(true, std::memory_order_release);
    {
        std::lock_guard lock(g_completion_mu);
        g_completion_snapshot.focused = false;
        g_completion_actions.clear();
        g_pending_valid = false;
    }
}

const ChatInputSink kSink{&on_key, &on_text, &on_ime, &on_blur};
} // namespace

// UI thread, after the game finishes each GameUiFrame.
void chat_input_frame_end(uintptr_t sys10) {
    if (!g_history_on.load(std::memory_order_acquire) &&
        !g_completion_on.load(std::memory_order_acquire) &&
        !g_diagnose_on.load(std::memory_order_acquire)) return;
    const bool active = g_editing_in_frame && !g_window_blurred.exchange(false);
    // Never swallow gameplay arrows when the native chat edit lacks focus.
    g_keys.focus(active);
    if (g_completion_on.load(std::memory_order_relaxed) || g_search_on.load(std::memory_order_relaxed)) {
        std::lock_guard lock(g_completion_mu);
        auto& snap = g_completion_snapshot;
        if (!active) {
            snap.focused = false;
            snap.has_rect = false;
            g_pending_valid = false;
            g_completion_actions.clear();
            g_keys.completion(false, false, false);
        } else if (g_rect_metrics && snap.focused && snap.has_rect &&
                   sys10 > static_cast<uintptr_t>(g_frame_arg)) {
            const uintptr_t sys = sys10 - static_cast<uintptr_t>(g_frame_arg);
            float viewport[4]{}, ppu = 0;
            if (game_copy(reinterpret_cast<const char*>(sys + g_viewport),
                          reinterpret_cast<char*>(viewport), sizeof viewport) &&
                game_read(reinterpret_cast<const void*>(sys + g_ppu), &ppu) &&
                std::isfinite(ppu) && ppu > 0 && ppu < 128) {
                snap.pixels[0] = viewport[0] + snap.raw[0] * ppu;
                snap.pixels[1] = viewport[1] + snap.raw[1] * ppu;
                snap.pixels[2] = snap.raw[2] * ppu;
                snap.pixels[3] = snap.raw[3] * ppu;
            } else snap.has_rect = false;
        }
    }
    // IME may take over the game's editor without another TextInputProcess
    // call; dismiss the model as well as the key router during composition.
    if ((!active || g_ime_composing.load(std::memory_order_acquire)) &&
        g_search_on.load(std::memory_order_relaxed)) {
        std::lock_guard lock(g_search_mu);
        g_search.cancel();
        g_search_suppress_completion = false;
        g_search_click.reset();
        g_keys.search(false);
    }
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
    {
        std::lock_guard lock(g_completion_mu);
        const auto& snap = g_completion_snapshot;
        const bool visible = active && snap.focused && snap.has_rect &&
            ((g_completion_on.load(std::memory_order_relaxed) && !snap.text.empty() && snap.text[0] == '/') ||
             (g_search_on.load(std::memory_order_relaxed) && g_keys.searching()));
        if (visible) g_chat_completion_cleanup_frames = 3;
        else if (g_chat_completion_cleanup_frames > 0) --g_chat_completion_cleanup_frames;
        g_chat_completion_demand.store(
            visible || g_chat_completion_cleanup_frames > 0 ? 1u : 0u,
            std::memory_order_relaxed);
    }
    g_editing_in_frame = false;
}

// Only managed Kiln.Ui may call this, enforced by ui.cpp. All game edit
// functions execute on the scoped UI thread in completion_before(), NEVER here.
KilnResult chat_completion_io(KilnUiChatCompletion* io) {
    if (!io || io->size < sizeof(KilnUiChatCompletion)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_completion_on.load(std::memory_order_acquire)) return KILN_ERR_UNAVAILABLE;
    switch (io->operation) {
    case 1: { // read: one stable snapshot and one queued navigation action
        std::lock_guard lock(g_completion_mu);
        const auto& s = g_completion_snapshot;
        if (!s.focused || !s.has_rect || s.text.size() > 256) return KILN_ERR_UNAVAILABLE;
        io->focused = 1; io->field = s.field; io->revision = s.revision;
        io->cursor = s.cursor; io->text_len = static_cast<uint32_t>(s.text.size());
        std::memset(io->text, 0, sizeof io->text);
        std::memcpy(io->text, s.text.data(), s.text.size());
        io->x = s.pixels[0]; io->y = s.pixels[1];
        io->width = s.pixels[2]; io->height = s.pixels[3];
        io->action = 0;
        if (!g_completion_actions.empty()) {
            io->action = static_cast<uint32_t>(g_completion_actions.front());
            g_completion_actions.pop_front();
            completion_edit_log("action-delivered", io->action);
        }
        return KILN_OK;
    }
    case 2: { // set suggestion/list state from the render thread
        // Turning suggestions on must name the snapshot they belong to: frame_end may have just cleared them on a
        // focus loss, and a late call from the render thread must not re-arm Tab for a field it no longer sees.
        // Turning them off is always safe.
        std::lock_guard lock(g_completion_mu);
        const auto& snap = g_completion_snapshot;
        if (io->suggestions == 0 && io->list_open == 0) {
            g_keys.completion(true, false, false);
            return KILN_OK;
        }
        if (!snap.focused || io->field != snap.field || io->revision != snap.revision) return KILN_ERR_UNAVAILABLE;
        g_keys.completion(true, io->suggestions != 0, io->list_open != 0);
        return KILN_OK;
    }
    case 3: { // replace: expected text, matching revision and focused field
        if (io->text_len > 256 || io->replacement_len > 256) return KILN_ERR_INVALID_ARGUMENT;
        const std::string expected(io->text, io->text_len), replacement(io->replacement, io->replacement_len);
        if (!safe_line(expected) || !safe_line(replacement) || replacement.empty() || replacement.front() != '/')
            return KILN_ERR_INVALID_ARGUMENT;
        std::lock_guard lock(g_completion_mu);
        const auto& snap = g_completion_snapshot;
        if (!snap.focused || io->field != snap.field || io->revision != snap.revision ||
            expected != snap.text) return KILN_ERR_UNAVAILABLE;
        g_pending_edit = PendingEdit{snap.field, snap.revision, expected, replacement};
        g_pending_valid = true;
        completion_edit_log("replace-queued", snap.field, snap.revision);
        g_keys.completion(true, false, false);
        return KILN_OK;
    }
    case 4: // reset UI candidates; never turn history off
        g_keys.completion(true, false, false);
        return KILN_OK;
    default:
        return KILN_ERR_INVALID_ARGUMENT;
    }
}

// Private client-only snapshot/selection channel. Managed code never sees a
// native game pointer, and can only queue an index for the next validated hook.
KilnResult chat_search_io(KilnUiChatSearch* io) {
    if (!io || io->size < sizeof(KilnUiChatSearch)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_search_on.load(std::memory_order_acquire)) return KILN_ERR_UNAVAILABLE;
    // An older Kiln.Ui.dll does not understand this slot: do not intercept
    // Ctrl+R until the matching managed search panel explicitly opts in.
    if (io->operation == 3) {
        g_keys.search_enabled(true);
        return KILN_OK;
    }
    std::lock_guard search_lock(g_search_mu);
    if (io->operation == 1) {
        io->active = g_search.active() ? 1u : 0u;
        io->suppressed = g_search_suppress_completion ? 1u : 0u;
        if (!io->active) return KILN_OK;
        std::lock_guard completion_lock(g_completion_mu);
        const auto& snap = g_completion_snapshot;
        if (!snap.focused || !snap.has_rect || snap.field != g_search_field ||
            snap.revision != g_search_revision) return KILN_ERR_UNAVAILABLE;
        io->field = g_search_field; io->revision = g_search_revision;
        io->x = snap.pixels[0]; io->y = snap.pixels[1];
        io->width = snap.pixels[2]; io->height = snap.pixels[3];
        io->query_len = static_cast<uint32_t>(g_search.query().size());
        std::memset(io->query, 0, sizeof io->query);
        std::memcpy(io->query, g_search.query().data(), io->query_len);
        const auto& matches = g_search.matches();
        io->total = static_cast<uint32_t>(matches.size());
        io->index = g_search.generation(); // op 1: opaque match-list generation
        io->first = g_search.selected() >= KILN_UI_CHAT_SEARCH_ROWS ?
            static_cast<uint32_t>(g_search.selected() - KILN_UI_CHAT_SEARCH_ROWS + 1) : 0u;
        io->selected = static_cast<uint32_t>(g_search.selected());
        io->count = static_cast<uint32_t>(std::min(matches.size() - io->first,
            static_cast<size_t>(KILN_UI_CHAT_SEARCH_ROWS)));
        std::memset(io->lines, 0, sizeof io->lines);
        for (uint32_t i = 0; i < io->count; ++i) {
            const auto& line = matches[io->first + i];
            std::memcpy(io->lines[i], line.data(), std::min(line.size(), sizeof io->lines[i] - 1));
        }
        return KILN_OK;
    }
    if (io->operation == 2) {
        if (!g_search.active() || !g_keys.searching() ||
            io->field != g_search_field || io->revision != g_search_revision ||
            io->selected != g_search.generation() ||
            !g_search.match(io->index)) return KILN_ERR_UNAVAILABLE;
        // op 2: 'selected' echoes the generation from the rendered snapshot,
        // while 'index' carries the clicked row; ABI layout is unchanged.
        g_search_click = SearchClick{io->field, io->revision, io->selected, io->index};
        return KILN_OK;
    }
    return KILN_ERR_INVALID_ARGUMENT;
}

// on_unload (ui.cpp): the last list is written and the writer thread joined, while the process still runs.
void chat_input_exit() {
    if (chat_store::Saver* saver = g_saver.load(std::memory_order_acquire)) saver->stop();
}

// Chat history is off unless the player switches it on (kiln.json ui.chatHistory); Tab completion is on unless the
// player switches it off (ui.chatCompletion: false, since 0.2.0-dev.29.22). The environment is for
// diagnostics: KILN_CHAT_HISTORY=1/0 overrides the switch, KILN_CHAT_HISTORY_TRACE=1 adds counts to kiln.log (never
// text), KILN_CHAT_HISTORY_DIAG=1 watches keys and submits without taking a key, KILN_CHAT_INPUT_PROBE=1 only confirms
// the two call sites. Nothing is hooked while all of them are off.
bool chat_input_probe_install() {
    const bool diag = env_enabled("KILN_CHAT_HISTORY_DIAG");
    const int forced = env_switch("KILN_CHAT_HISTORY");
    const bool switched = forced < 0 && kiln_json_chat_option("chatHistory", false);
    const int comp_forced = env_switch("KILN_CHAT_COMPLETION");
    const bool comp_switched = comp_forced < 0 && kiln_json_chat_option("chatCompletion", true);
    bool completion = (comp_forced == 1 || comp_switched) && !diag;
    const char* why = forced >= 0 ? "KILN_CHAT_HISTORY" : "kiln.json ui.chatHistory";
    const bool history = (forced == 1 || switched) && !diag;  // diagnostics take precedence: never swallow keys
    const bool probe = env_enabled("KILN_CHAT_INPUT_PROBE");
    if (!history && !completion && !diag && !probe) {
        logf(KILN_LOG_INFO, "ui: chat history and completion off (%s, %s)",
             forced == 0 ? "KILN_CHAT_HISTORY=0" : "kiln.json ui.chatHistory",
             comp_forced == 0 ? "KILN_CHAT_COMPLETION=0" : "kiln.json ui.chatCompletion");
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
    if ((history || completion || diag) && (!symbol("chatui/TextInputState", &g_state_fn) ||
                              ((history || completion) && !symbol("chatui/EditSetText", &g_set_text_fn)))) {
        logf(KILN_LOG_WARN, "ui: chat history unavailable: edit state/replace symbols unresolved");
        return false;
    }
    // A verified native rectangle is required for search and completion,
    // but never for basic history recall.
    bool geometry = false;
    if (completion || history) {
        geometry = read_completion_layout() &&
            symbol("ui/UiNodeRect", &g_rect_fn) &&
            cst("ui/sys.frame_arg", &g_frame_arg) &&
            cst("ui/sys.viewport", &g_viewport) &&
            cst("ui/sys.px_per_unit", &g_ppu) &&
            g_frame_arg >= 0 && g_viewport >= 0 && g_ppu >= 0 &&
            g_frame_arg < 0x10000 && g_viewport < 0x10000 && g_ppu < 0x10000;
        g_rect_metrics = geometry;
        if (completion && !geometry) logf(KILN_LOG_WARN,
            "ui: chat completion unavailable: sigdb cursor/node or native rect/viewport unverified; history unaffected");
        completion = completion && geometry;
        if (history && !geometry) logf(KILN_LOG_WARN,
            "ui: chat reverse search unavailable: chat rectangle unverified; Up/Down history unaffected");
    }
    const bool search = history && geometry;
    if (!history && !completion && !diag && !probe) return false;
    if (!g_input.install(g_api, g_owner, input, reinterpret_cast<void*>(&hk_input), "chatui/TextInputProcess")) {
        logf(KILN_LOG_WARN, "ui: chat input hook unavailable: %s", g_input.error());
        return false;
    }
    if (history || completion || diag) input_set_chat_sink(&kSink);
    g_keys.history(history);
    g_keys.search_enabled(false); // activated by managed search panel op 3
    g_search_on.store(search, std::memory_order_release);
    g_completion_on.store(completion, std::memory_order_release);
    if (history) {
        if (completion) logf(KILN_LOG_INFO, "ui: chat completion on with chat history (native scoped hook)");
        load_history();
        g_trace_on.store(env_enabled("KILN_CHAT_HISTORY_TRACE"), std::memory_order_release);
        g_history_on.store(true, std::memory_order_release);
        logf(KILN_LOG_INFO, "ui: chat history on (%s): Up/Down in the HUD and Social chat, %zu command(s) kept from "
             "earlier games", why, g_history.count());
    } else if (completion) {
        logf(KILN_LOG_INFO, "ui: chat completion on: native HUD and Social input, no history (unless enabled separately)");
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
