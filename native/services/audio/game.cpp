// SPDX-License-Identifier: MIT
// kiln.audio, the game layer: symbols from sigdb/symbols/client.d/audio.json, three inline hooks and SEH-guarded calls
// into Keen's sound system (docs/re/audio.md section 3).
//  - SoundScenesInit (post): runs once at start-up from the client state machine with (scene table, SoundSystem*,
//    reverb id); after it the table holds the u32 scene ids [7] and listener ids [7] (keen::SoundSceneType order).
//  - SoundStart (probe, pass-through): counts the game's own calls per thread (the one-thread rule: all sound calls
//    on the main thread), and learns the SoundSystem pointer should the boot hook have missed it. Kiln's own calls go
//    through the trampoline and are not counted. KILN_AUDIO_PROBE=0 turns it off.
//  - VoiceoverUpdate (pre + post): the voice-over manager's per-frame update (main thread, in game). Kiln runs its
//    voice-over and narration requests right before it, with the manager the game passes (never a stored pointer),
//    and reads the result right after.
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>

#include "audio.h"
#include "safetyhook.hpp"

namespace kau {

VoLayout VL;
Have H;
std::atomic<uintptr_t> g_sys{0};
std::atomic<uint32_t> g_scenes[KILN_AUDIO_SCENES];
std::atomic<uint32_t> g_listeners[KILN_AUDIO_SCENES];
std::atomic<bool> g_scenes_ok{false};
std::atomic<uint32_t> g_main_thread{0};
ProbeThread g_probe_threads[kProbeThreads];
std::atomic<uint64_t> g_probe_calls{0}, g_probe_off_main{0};
std::atomic<uint64_t> g_probe_first_ms{0};

namespace {

using SoundStartFn = uint32_t (*)(void* sys, uint64_t unused, uint32_t scene, const uint8_t* guid, const PlayParams* p);
using SoundStopFn = void (*)(void* sys, uint32_t instance, int64_t fade_ns);
using SoundSetVolumeFn = void (*)(void* sys, uint32_t instance, float volume, int64_t fade_ns);
using SoundSetPositionFn = void (*)(void* sys, uint32_t instance, const int64_t* pos);
using SoundIsPlayingFn = bool (*)(void* sys, uint32_t instance);
using ScenesInitFn = void (*)(uint32_t* table, void* sys, uint32_t reverb);
using VoRequestFn = void (*)(void* mgr, const uint8_t* guid, uint32_t speaker, uint8_t language_hint);
using VoSetPositionFn = void (*)(void* mgr, const int64_t* pos);
using VoUpdateFn = void (*)(void* mgr);
using LorePushFn = void (*)(void* mgr, const uint8_t* guid, uint32_t name, uint32_t desc, uint16_t version);
using GetTransformFn = const void* (*)(uintptr_t world, uint32_t entity);

struct Fns {
    SoundStartFn start = nullptr;
    SoundStopFn stop = nullptr;
    SoundSetVolumeFn set_volume = nullptr;
    SoundSetPositionFn set_position = nullptr;
    SoundIsPlayingFn is_playing = nullptr;
    VoRequestFn vo_request = nullptr;
    VoSetPositionFn vo_set_position = nullptr;
    LorePushFn lore_push = nullptr;
    GetTransformFn get_transform = nullptr;
} F;

SafetyHookInline g_scenes_hook, g_start_hook, g_vo_hook;
thread_local bool t_in_vo_hook = false;

bool sym(const char* n, uintptr_t* out) {
    *out = 0;
    return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out;
}
bool cst(const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }
bool feat(const char* n) { return g_api->feature(KilnStr{n, std::strlen(n)}); }

// ---- SEH helpers (no C++ objects with destructors in these frames) ----
bool seh_start(SoundStartFn fn, void* sys, uint32_t scene, const uint8_t* guid, const PlayParams* p, uint32_t* out) {
    __try {
        *out = fn(sys, 0, scene, guid, p);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = 0;
        return false;
    }
}
bool seh_stop(void* sys, uint32_t inst, int64_t fade) {
    __try {
        F.stop(sys, inst, fade);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_volume(void* sys, uint32_t inst, float v, int64_t fade) {
    __try {
        F.set_volume(sys, inst, v, fade);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_position(void* sys, uint32_t inst, const int64_t* pos) {
    __try {
        F.set_position(sys, inst, pos);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_playing(void* sys, uint32_t inst, bool* out) {
    __try {
        *out = F.is_playing(sys, inst);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = false;
        return false;
    }
}
bool seh_transform(uintptr_t world, uint32_t e, int64_t out[3]) {
    __try {
        const auto* t = static_cast<const int64_t*>(F.get_transform(world, e));
        if (!t) return false;
        out[0] = t[0];
        out[1] = t[1];
        out[2] = t[2];
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_vo_request(uintptr_t mgr, const uint8_t* guid, uint32_t speaker) {
    __try {
        F.vo_request(reinterpret_cast<void*>(mgr), guid, speaker, 0);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_vo_position(uintptr_t mgr, const int64_t* pos) {
    __try {
        F.vo_set_position(reinterpret_cast<void*>(mgr), pos);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_lore(uintptr_t mgr, const uint8_t* guid, uint32_t name, uint32_t desc) {
    __try {
        F.lore_push(reinterpret_cast<void*>(mgr), guid, name, desc, 0);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_read(uintptr_t a, void* out, size_t n) {
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(a), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// ---- hooks ----
void hk_scenes_init(uint32_t* table, void* sys, uint32_t reverb) {
    g_scenes_hook.call<void>(table, sys, reverb);
    uint32_t t[2 * KILN_AUDIO_SCENES] = {};
    if (!table || !seh_read(reinterpret_cast<uintptr_t>(table), t, sizeof t)) {
        KA_W("audio: SoundScenesInit ran but its table could not be read; sounds stay unavailable");
        return;
    }
    g_sys.store(reinterpret_cast<uintptr_t>(sys), std::memory_order_release);
    for (int i = 0; i < KILN_AUDIO_SCENES; ++i) {
        g_scenes[i].store(t[i], std::memory_order_relaxed);
        g_listeners[i].store(t[KILN_AUDIO_SCENES + i], std::memory_order_relaxed);
    }
    g_scenes_ok.store(sys != nullptr && t[KILN_AUDIO_SCENE_UI] != 0 && t[KILN_AUDIO_SCENE_GAME] != 0,
                      std::memory_order_release);
    on_scenes_captured();
}

uint32_t hk_sound_start(void* sys, uint64_t unused, uint32_t scene, const uint8_t* guid, const PlayParams* p) {
    const DWORD tid = GetCurrentThreadId();
    g_probe_calls.fetch_add(1, std::memory_order_relaxed);
    uint64_t zero = 0;
    g_probe_first_ms.compare_exchange_strong(zero, now_ms(), std::memory_order_relaxed);
    const uint32_t main = g_main_thread.load(std::memory_order_relaxed);
    if (main && tid != main) g_probe_off_main.fetch_add(1, std::memory_order_relaxed);
    for (auto& slot : g_probe_threads) {
        uint32_t id = slot.id.load(std::memory_order_relaxed);
        if (id == tid) {
            slot.calls.fetch_add(1, std::memory_order_relaxed);
            break;
        }
        if (id == 0) {
            uint32_t expected = 0;
            if (slot.id.compare_exchange_strong(expected, tid, std::memory_order_relaxed) || expected == tid) {
                slot.calls.fetch_add(1, std::memory_order_relaxed);
                if (expected == 0)
                    KA_I("audio: the game calls SoundStart on thread %lu%s (scene %u)", tid,
                         main == 0 ? " (main thread not seen yet)" : tid == main ? " (main thread)" : " (NOT the main thread)",
                         scene);
                break;
            }
        }
    }
    if (sys && !g_sys.load(std::memory_order_relaxed)) {
        uintptr_t none = 0;
        if (g_sys.compare_exchange_strong(none, reinterpret_cast<uintptr_t>(sys)))
            KA_W("audio: sound system 0x%llx learned from a SoundStart call (the boot hook did not run)",
                 static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(sys)));
    }
    return g_start_hook.call<uint32_t>(sys, unused, scene, guid, p);
}

void hk_vo_update(void* mgr) {
    const uintptr_t m = reinterpret_cast<uintptr_t>(mgr);
    if (t_in_vo_hook || !m) {  // never re-enter (Kiln's requests do not call back into the update)
        g_vo_hook.call<void>(mgr);
        return;
    }
    t_in_vo_hook = true;
    on_vo_update_pre(m);
    g_vo_hook.call<void>(mgr);  // the game's own code: a fault there stays the game's (crash handler), not swallowed
    on_vo_update_post(m);
    t_in_vo_hook = false;
}

}  // namespace

void alog(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n <= 0 || !g_api) return;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n < static_cast<int>(sizeof(buf)) ? n : sizeof(buf) - 1)});
}

int64_t m_to_fixed(double m) { return static_cast<int64_t>(std::llround(m * 4294967296.0)); }
double fixed_to_m(int64_t v) { return static_cast<double>(v) / 4294967296.0; }
uint64_t now_ms() { return GetTickCount64(); }

bool game_init() {
    if (!feat("audio.play")) {
        KA_W("audio: feature audio.play is off on this game build (sigdb client.d/audio.json); kiln.audio unavailable");
        return false;
    }
    uintptr_t a_start = 0, a_stop = 0, a_vol = 0, a_pos = 0, a_play = 0, a_scenes = 0;
    if (!sym("audio/SoundStart", &a_start) || !sym("audio/SoundStop", &a_stop) || !sym("audio/SoundSetVolume", &a_vol) ||
        !sym("audio/SoundSetPosition", &a_pos) || !sym("audio/SoundIsPlaying", &a_play) ||
        !sym("audio/SoundScenesInit", &a_scenes)) {
        KA_W("audio: a sound symbol is missing although audio.play is on; kiln.audio unavailable");
        return false;
    }
    F.start = reinterpret_cast<SoundStartFn>(a_start);
    F.stop = reinterpret_cast<SoundStopFn>(a_stop);
    F.set_volume = reinterpret_cast<SoundSetVolumeFn>(a_vol);
    F.set_position = reinterpret_cast<SoundSetPositionFn>(a_pos);
    F.is_playing = reinterpret_cast<SoundIsPlayingFn>(a_play);
    g_scenes_hook = safetyhook::create_inline(reinterpret_cast<void*>(a_scenes), reinterpret_cast<void*>(&hk_scenes_init));
    if (!g_scenes_hook) {
        KA_W("audio: could not hook SoundScenesInit; kiln.audio unavailable");
        return false;
    }
    H.play = true;

    const char* probe_env = std::getenv("KILN_AUDIO_PROBE");
    if (!(probe_env && probe_env[0] == '0')) {
        g_start_hook = safetyhook::create_inline(reinterpret_cast<void*>(a_start), reinterpret_cast<void*>(&hk_sound_start));
        H.probe = static_cast<bool>(g_start_hook);
        if (!H.probe) KA_W("audio: SoundStart probe not installed (the game's thread statistics stay empty)");
    }

    uintptr_t a_tf = 0;
    if (sym("GetEntityTransform", &a_tf)) {
        F.get_transform = reinterpret_cast<GetTransformFn>(a_tf);
        H.transform = true;
    } else {
        KA_W("audio: GetEntityTransform missing: PlayOn (follow an entity) plays at the start position only");
    }

    // Voice-over manager (dialogue channel) and the lore queue (narration).
    uintptr_t a_req = 0, a_vpos = 0, a_upd = 0, a_lore = 0;
    const bool vo = feat("audio.voiceover") && sym("audio/VoiceoverRequest", &a_req) &&
                    sym("audio/VoiceoverSetPosition", &a_vpos) && sym("audio/VoiceoverUpdate", &a_upd) &&
                    cst("audio/vo.sys", &VL.sys) && cst("audio/vo.scene", &VL.scene) &&
                    cst("audio/vo.position", &VL.position) && cst("audio/vo.pending", &VL.pending) &&
                    cst("audio/vo.speaker", &VL.speaker) && cst("audio/vo.instance", &VL.instance) &&
                    cst("audio/vo.playing", &VL.playing) && cst("audio/vo.start", &VL.start) &&
                    cst("audio/vo.language", &VL.language) && cst("audio/vo.volume", &VL.volume);
    const bool lore = vo && feat("audio.narration") && sym("audio/VoiceoverLorePush", &a_lore) &&
                      cst("audio/vo.lore_count", &VL.lore_count) && cst("audio/vo.lore_mode", &VL.lore_mode) &&
                      cst("audio/vo.lore_stride", &VL.lore_stride) && cst("audio/vo.lore_max", &VL.lore_max) &&
                      cst("audio/vo.lore_scene", &VL.lore_scene) &&
                      cst("audio/vo.lore_head_playing", &VL.lore_head_playing) &&
                      cst("audio/vo.lore_head_instance", &VL.lore_head_instance);
    if (vo) {
        F.vo_request = reinterpret_cast<VoRequestFn>(a_req);
        F.vo_set_position = reinterpret_cast<VoSetPositionFn>(a_vpos);
        if (lore) F.lore_push = reinterpret_cast<LorePushFn>(a_lore);
        g_vo_hook = safetyhook::create_inline(reinterpret_cast<void*>(a_upd), reinterpret_cast<void*>(&hk_vo_update));
        if (g_vo_hook) {
            H.voiceover = true;
            H.narration = lore && VL.lore_stride > 0 && VL.lore_max > 0 && VL.lore_count > VL.lore_max * VL.lore_stride;
        } else {
            KA_W("audio: could not hook VoiceoverUpdate: voice-over and narration unavailable");
        }
    } else {
        KA_W("audio: feature audio.voiceover is off: Voiceover.Play / Narrate unavailable");
    }
    if (H.voiceover && !H.narration) KA_W("audio: feature audio.narration is off: Voiceover.Narrate unavailable");
    return true;
}

bool g_sound_start(uint32_t scene_id, const uint8_t guid[16], const PlayParams* p, uint32_t* out) {
    const uintptr_t sys = g_sys.load(std::memory_order_acquire);
    if (!sys) return false;
    // Through the trampoline when the probe is installed: Kiln's own calls are not counted as the game's.
    const SoundStartFn fn = g_start_hook ? g_start_hook.original<SoundStartFn>() : F.start;
    return seh_start(fn, reinterpret_cast<void*>(sys), scene_id, guid, p, out);
}
bool g_sound_stop(uint32_t instance, int64_t fade_ns) {
    const uintptr_t sys = g_sys.load(std::memory_order_acquire);
    return sys && instance && seh_stop(reinterpret_cast<void*>(sys), instance, fade_ns);
}
bool g_sound_set_volume(uint32_t instance, float volume, int64_t fade_ns) {
    const uintptr_t sys = g_sys.load(std::memory_order_acquire);
    return sys && instance && seh_volume(reinterpret_cast<void*>(sys), instance, volume, fade_ns);
}
bool g_sound_set_position(uint32_t instance, const int64_t pos[3]) {
    const uintptr_t sys = g_sys.load(std::memory_order_acquire);
    return sys && instance && seh_position(reinterpret_cast<void*>(sys), instance, pos);
}
bool g_sound_is_playing(uint32_t instance, bool* out) {
    const uintptr_t sys = g_sys.load(std::memory_order_acquire);
    *out = false;
    return sys && instance && seh_playing(reinterpret_cast<void*>(sys), instance, out);
}
bool g_entity_position(uintptr_t world, KilnEntity entity, KilnVec3* out) {
    if (!H.transform || !world || !entity) return false;
    int64_t p[3];
    if (!seh_transform(world, entity, p)) return false;
    out->x = fixed_to_m(p[0]);
    out->y = fixed_to_m(p[1]);
    out->z = fixed_to_m(p[2]);
    // a sane world position (the map is a few km; anything else is not a transform)
    return std::fabs(out->x) < 1e6 && std::fabs(out->y) < 1e6 && std::fabs(out->z) < 1e6;
}
bool g_vo_request(uintptr_t mgr, const uint8_t guid[16], KilnEntity speaker) {
    return F.vo_request && mgr && seh_vo_request(mgr, guid, speaker);
}
bool g_vo_set_position(uintptr_t mgr, const int64_t pos[3]) {
    return F.vo_set_position && mgr && seh_vo_position(mgr, pos);
}
bool g_lore_push(uintptr_t mgr, const uint8_t guid[16], uint32_t name, uint32_t desc) {
    return F.lore_push && mgr && seh_lore(mgr, guid, name, desc);
}
bool g_read(uintptr_t addr, void* out, size_t n) { return addr && seh_read(addr, out, n); }

}  // namespace kau
