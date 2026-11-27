// SPDX-License-Identifier: MIT
// kiln.npcspeech: Kiln's lines in the game's own speech bubble over a friendly NPC. Contract:
// native/core/abi/services/kiln_npcspeech.h; evidence: docs/re/npc-speech.md; usage: docs/kiln/services/npc-speech.md.
//
// The bubble is client data from end to end: the HUD's NPC layer calls the per-NPC bubble function (NpcBubble) for each
// nearby NPC with its dialogue record (the NPC's NpcDialog lines sorted into lists), which picks a line and calls the
// bubble widget (BubbleDraw) with the line's loca tag and a show flag. Kiln changes what that one call draws, for NPCs
// the host gave an override or a Say:
//   - NpcBubble's detour notes the NPC (thread-local, the game's UI thread) and mutes the voice-overs of the record's
//     greeting lists while an override or Say is on (puts them back after);
//   - BubbleDraw's detour, inside a noted NPC's call only, swaps the tag, hides the bubble (Suppress) or shows it (Say).
// Kiln's texts are loca tags that kiln.content's loca hooks answer (kc_nc_set_texts). Nothing here runs a managed callback.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "services/kiln_npcspeech.h"
#include "npcspeech_core.h"

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;

void logf(int level, const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= static_cast<int>(sizeof buf)) n = sizeof buf - 1;
    std::string line = "npcspeech: ";
    line.append(buf, static_cast<size_t>(n));
    g_api->log(g_owner, level, KilnStr{line.data(), line.size()});
}
#define NS_I(...) logf(KILN_LOG_INFO, __VA_ARGS__)
#define NS_W(...) logf(KILN_LOG_WARN, __VA_ARGS__)

KilnStr S(const char* s) { return KilnStr{s, std::strlen(s)}; }

// ------------------------------------------------------------------------------------------------ resolution ----
uintptr_t g_bubble_at = 0, g_draw_at = 0;
npcspeech::Layout g_lay;      // the record's lists (voice) and the two structs (bubble)
bool g_bubble_ok = false;     // npcspeech.bubble: both hooks and their layout
bool g_voice_ok = false;      // npcspeech.voice: the record's lists
using SetTextsFn = void (*)(uint32_t, const char*, size_t);
// kiln.content's text setter; found once, never reset (any thread reads it)
std::atomic<SetTextsFn> g_set_texts{nullptr};

bool constant(const char* name, int64_t* out) { return g_api->constant(S(name), out) == KILN_OK; }

void resolve() {
    g_bubble_ok = g_api->feature(S("npcspeech.bubble")) && g_api->symbol(S("npcspeech/NpcBubble"), &g_bubble_at) == KILN_OK &&
                  g_bubble_at && g_api->symbol(S("npcspeech/BubbleDraw"), &g_draw_at) == KILN_OK && g_draw_at;
    int64_t v = 0;
    if (g_bubble_ok) {
        g_bubble_ok = constant("npcspeech/entry.record", &v) && (g_lay.entry_record = v, true) && constant("npcspeech/bubble.text", &v) &&
                      (g_lay.bubble_text = v, true) && constant("npcspeech/bubble.show", &v) && (g_lay.bubble_show = v, true) &&
                      constant("npcspeech/bubble.reset", &v) && (g_lay.bubble_reset = v, true);
    }
    g_voice_ok = g_bubble_ok && g_api->feature(S("npcspeech.voice")) && constant("npcspeech/rec.prio_list", &v) && (g_lay.prio_list = v, true) &&
                 constant("npcspeech/rec.prio_count", &v) && (g_lay.prio_count = v, true) && constant("npcspeech/rec.plain_list", &v) &&
                 (g_lay.plain_list = v, true) && constant("npcspeech/rec.plain_count", &v) && (g_lay.plain_count = v, true) &&
                 constant("npcspeech/rec.entry_size", &v) && (g_lay.entry_size = v, true) && constant("npcspeech/rec.entry_vo", &v) &&
                 (g_lay.entry_vo = v, true) && constant("npcspeech/rec.vo_slots", &v) && (g_lay.vo_slots = v, true) &&
                 constant("npcspeech/rec.entry_line", &v) && (g_lay.entry_line = v, true) && g_lay.voice_sane();
    if (!g_voice_ok) g_lay.entry_size = 0;  // the core never touches a record without the whole layout
}

// True when kiln.content's loca hooks answer Kiln's tags; looks again while not (kiln.content may load after this service).
bool texts_here() {
    if (g_set_texts.load(std::memory_order_acquire)) return true;
    if (!g_api->feature(S("content.loca"))) return false;
    if (HMODULE m = GetModuleHandleW(L"kiln-content.dll"))
        if (auto f = reinterpret_cast<SetTextsFn>(GetProcAddress(m, "kc_nc_set_texts"))) g_set_texts.store(f, std::memory_order_release);
    return g_set_texts.load(std::memory_order_acquire) != nullptr;
}

// ------------------------------------------------------------------------------------------------ hooks ----------
npcspeech::Core g_core;
kiln::RawHook g_h_bubble, g_h_draw;
std::mutex g_install_mu;
std::atomic<bool> g_installed{false}, g_install_failed{false};

// The NPC whose bubble call runs on this thread (the game's UI thread); 0 outside one.
thread_local KilnEntity t_npc = 0;

// Reads/writes game memory under SEH: a record the game freed or rebuilt between frames must not take the game down.
// No C++ objects with destructors in here (MSVC's __try).
bool seh_voice(npcspeech::Core* core, KilnEntity npc, uint8_t* record) {
    __try {
        core->voice(npc, record, g_lay);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

using BubbleFn = void (*)(void*, void*, void*, void*);
using DrawFn = void (*)(void*);
std::atomic<uint32_t> g_voice_faults{0};

void hk_bubble(void* entry, void* layer, void* slots, void* pos) {
    if (!entry || !g_core.active()) return g_h_bubble.original<BubbleFn>()(entry, layer, slots, pos);
    const KilnEntity npc = *static_cast<const uint32_t*>(entry);
    if (g_voice_ok && g_voice_faults.load(std::memory_order_relaxed) < 8) {
        auto* record = *reinterpret_cast<uint8_t**>(static_cast<uint8_t*>(entry) + g_lay.entry_record);
        if (record && !seh_voice(&g_core, npc, record) && g_voice_faults.fetch_add(1) == 7)
            NS_W("the dialogue record faulted 8 times: voice-overs are left to the game for this session");
    }
    const KilnEntity outer = t_npc;
    t_npc = npc;
    g_h_bubble.original<BubbleFn>()(entry, layer, slots, pos);
    t_npc = outer;
}

void hk_draw(void* params) {
    if (params && t_npc) g_core.draw(t_npc, static_cast<uint8_t*>(params), g_lay, GetTickCount64());
    g_h_draw.original<DrawFn>()(params);
}

bool install() {
    if (g_installed.load()) return true;
    if (g_install_failed.load() || !g_bubble_ok) return false;
    std::lock_guard lock(g_install_mu);
    if (g_installed.load()) return true;
    if (!g_h_draw.install(g_api, g_owner, g_draw_at, reinterpret_cast<void*>(&hk_draw), "npcspeech/BubbleDraw") ||
        !g_h_bubble.install(g_api, g_owner, g_bubble_at, reinterpret_cast<void*>(&hk_bubble), "npcspeech/NpcBubble")) {
        NS_W("the bubble hooks could not go in (%s %s): Kiln's NPC speech is off for this session", g_h_draw.error() ? g_h_draw.error() : "",
             g_h_bubble.error() ? g_h_bubble.error() : "");
        g_h_draw.revoke();
        g_h_bubble.revoke();
        g_install_failed.store(true);
        return false;
    }
    g_installed.store(true);
    NS_I("bubble hooks in (NpcBubble 0x%llx, BubbleDraw 0x%llx); voice-overs %s", static_cast<unsigned long long>(g_bubble_at),
         static_cast<unsigned long long>(g_draw_at), g_voice_ok ? "muted while Kiln speaks" : "left to the game (layout not resolved)");
    return true;
}

// ------------------------------------------------------------------------------------------------ dialogue menu --
// The dialogue window's parts (farewell, the menu's line, topics): docs/re/npc-speech.md section 6. Until they are
// resolved, the dev.32 kinds other than the greeting answer KILN_ERR_UNAVAILABLE.
KilnResult dialog_usable(uint32_t) { return KILN_ERR_UNAVAILABLE; }
bool install_dialog() { return false; }
uint32_t dialog_resolved() { return 0; }
uint32_t dialog_installed() { return 0; }
void dialog_counts(KilnNpcDialogStatus*) {}

// ------------------------------------------------------------------------------------------------ table ----------
KilnResult usable() {
    if (!g_bubble_ok || g_install_failed.load()) return KILN_ERR_UNAVAILABLE;
    return KILN_OK;
}
// A tag kiln.content's loca hooks do not answer shows an empty bubble: Replace and Say need them (Suppress does not).

KilnResult KILN_CALL set(KilnEntity npc, uint32_t mode, const uint32_t* tags, uint32_t n) {
    if (KilnResult rc = usable(); rc != KILN_OK) return rc;
    if (!npcspeech::Core::valid_set(npc, mode, tags, n)) return KILN_ERR_INVALID_ARGUMENT;
    if (mode == KILN_SPEECH_REPLACE && !texts_here()) return KILN_ERR_UNAVAILABLE;
    if (mode != KILN_SPEECH_GAME && !install()) return KILN_ERR_UNAVAILABLE;
    g_core.set(npc, mode, tags, n);
    return KILN_OK;
}

KilnResult KILN_CALL say(KilnEntity npc, uint32_t tag, uint32_t ms) {
    if (KilnResult rc = usable(); rc != KILN_OK) return rc;
    if (!npc || ms > KILN_SPEECH_MAX_SAY_MS) return KILN_ERR_INVALID_ARGUMENT;
    if (tag && ms && (!texts_here() || !install())) return KILN_ERR_UNAVAILABLE;
    g_core.say(npc, tag, ms, GetTickCount64());
    return KILN_OK;
}

KilnResult KILN_CALL text(uint32_t tag, const char* json, size_t len) {
    if (!tag || !json || !len || len > 64 * 1024) return KILN_ERR_INVALID_ARGUMENT;
    if (!texts_here()) return KILN_ERR_UNAVAILABLE;
    g_set_texts.load(std::memory_order_acquire)(tag, json, len);
    g_core.count_text(tag);
    return KILN_OK;
}

KilnResult KILN_CALL clear_all() {
    g_core.clear_all();
    return KILN_OK;
}

// ---- since 0.2.0-dev.32 ----
KilnResult KILN_CALL set_lines(KilnEntity npc, uint32_t kind, uint32_t mode, const uint32_t* tags, uint32_t n, uint32_t pick, uint32_t seed) {
    if (!npcspeech::Core::valid_lines(npc, kind, mode, tags, n, pick)) return KILN_ERR_INVALID_ARGUMENT;
    if (kind == KILN_SPEECH_KIND_GREETING) {
        if (KilnResult rc = usable(); rc != KILN_OK) return rc;
        if (mode == KILN_SPEECH_REPLACE && !texts_here()) return KILN_ERR_UNAVAILABLE;
        if (mode != KILN_SPEECH_GAME && !install()) return KILN_ERR_UNAVAILABLE;
    } else if (mode != KILN_SPEECH_GAME) {
        if (KilnResult rc = dialog_usable(kind); rc != KILN_OK) return rc;
        if (!texts_here() || !install_dialog()) return KILN_ERR_UNAVAILABLE;
    }
    g_core.set_lines(npc, kind, mode, tags, n, pick, seed);
    return KILN_OK;
}

KilnResult KILN_CALL next(KilnEntity npc, uint32_t kind, int32_t line) {
    return g_core.next(npc, kind, line) ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;
}

KilnResult KILN_CALL events(KilnSpeechEvent* out, uint32_t max, uint32_t* n) {
    if (!n || (max && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *n = g_core.drain(out, max);
    return KILN_OK;
}

KilnResult KILN_CALL dialog_status(KilnNpcDialogStatus* out) {
    if (!out || out->size < sizeof(KilnNpcDialogStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(reinterpret_cast<uint8_t*>(out) + sizeof(uint32_t), 0, sizeof(KilnNpcDialogStatus) - sizeof(uint32_t));
    out->resolved = dialog_resolved();
    out->installed = dialog_installed();
    const auto c = g_core.counts();
    out->lines = c.lines;
    out->queued = c.queued;
    out->opens = c.opens;
    out->dropped = c.dropped;
    dialog_counts(out);
    return KILN_OK;
}

KilnResult KILN_CALL status(KilnNpcSpeechStatus* out) {
    if (!out || out->size < sizeof(KilnNpcSpeechStatus)) return KILN_ERR_INVALID_ARGUMENT;
    out->resolved = (g_bubble_at ? 1u : 0u) | (g_draw_at ? 2u : 0u) | (g_voice_ok ? 4u : 0u) | (texts_here() ? 8u : 0u);
    if (!g_bubble_ok) out->resolved &= ~3u;
    out->installed = g_installed.load() ? 3u : 0u;
    const auto c = g_core.counts();
    out->overrides = c.overrides;
    out->says = c.says;
    out->texts = c.texts;
    out->bubbles = c.bubbles;
    out->changed = c.changed;
    out->muted = c.muted;
    return KILN_OK;
}

const KilnNpcSpeechApi kTable = {{sizeof(KilnNpcSpeechApi), KILN_NPCSPEECH_VERSION}, &set, &say, &text, &clear_all, &status,
                                  &set_lines, &next, &events, &dialog_status};

void KILN_CALL on_ready(void*) {
    if (!texts_here()) NS_W("kiln.content's loca hooks (content.loca) are not here: Kiln's lines cannot be shown, only Suppress works");
}

void KILN_CALL on_unload(void*) {
    g_core.clear_all();
    g_h_bubble.revoke();
    g_h_draw.revoke();
    g_installed.store(false);
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_NPCSPEECH_SERVICE, sizeof(KILN_NPCSPEECH_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    info->on_unload = &on_unload;
    if (!api->feature(S("npcspeech.bubble"))) {
        // a dedicated server has no HUD (its decisions go to the clients through Kiln.Npcs); on a game client the
        // feature is off when sigdb did not find the bubble functions on this build
        NS_I("npcspeech.bubble is off in this process (a dedicated server, or a client build without the bubble functions): NPCs keep the game's lines here");
        return KILN_ERR_UNAVAILABLE;
    }
    resolve();
    if (!g_bubble_ok) {
        NS_W("npcspeech.bubble's symbols or layout did not resolve: NPCs keep the game's lines");
        return KILN_ERR_UNAVAILABLE;
    }
    if (!g_voice_ok) NS_W("the dialogue record's layout did not check out (npcspeech.voice off): Kiln's lines show, voice-overs stay the game's");
    NS_I("ready: bubble 0x%llx, draw 0x%llx, voice %s; hooks go in with the first override or Say",
         static_cast<unsigned long long>(g_bubble_at), static_cast<unsigned long long>(g_draw_at), g_voice_ok ? "on" : "off");
    return api->register_service(owner, KilnStr{KILN_NPCSPEECH_SERVICE, sizeof(KILN_NPCSPEECH_SERVICE) - 1}, &kTable);
}
