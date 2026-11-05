// SPDX-License-Identifier: MIT
// kiln.audio internals: the game layer (game.cpp: symbols, hooks, SEH-guarded calls into Keen's sound system and
// voice-over manager) and the service (audio.cpp: handles, queue, drains, the C table). Public header:
// native/core/abi/services/kiln_audio.h. Evidence: docs/re/audio.md, sigdb/symbols/client.d/audio.json.
#pragma once
#include <windows.h>

#include <atomic>
#include <cstdint>

#include "kiln_api.h"
#include "services/kiln_audio.h"

namespace kau {

extern const KilnApi* g_api;
extern KilnOwner g_owner;

void alog(int level, const char* fmt, ...);
#define KA_I(...) ::kau::alog(KILN_LOG_INFO, __VA_ARGS__)
#define KA_W(...) ::kau::alog(KILN_LOG_WARN, __VA_ARGS__)
#define KA_E(...) ::kau::alog(KILN_LOG_ERROR, __VA_ARGS__)
#define KA_D(...) ::kau::alog(KILN_LOG_DEBUG, __VA_ARGS__)

// The game's PlayParams (0x28 B, docs/re/audio.md 3.2).
struct PlayParams {
    uint64_t zero;      // +0x00: 0 in every game caller
    int64_t pos[3];     // +0x08: world position, 32.32 fixed-point metres
    float volume;       // +0x20: 0..1
    uint8_t flag24;     // +0x24: -> instance flag 0x10 (lore entries set it) [hyp]
    uint8_t flags25;    // +0x25: bit0 -> 0x02, bit1 -> 0x04 (local player's own sounds), bit2 -> 0x08 [hyp]
    uint16_t pad;
};
static_assert(sizeof(PlayParams) == 0x28);

// Voice-over manager layout (sigdb constants audio/vo.*).
struct VoLayout {
    int64_t sys = -1, scene = -1, position = -1, pending = -1, speaker = -1, instance = -1, playing = -1, start = -1;
    int64_t language = -1, volume = -1;
    int64_t lore_count = -1, lore_mode = -1, lore_stride = 0x48, lore_max = 16, lore_scene = -1;
    int64_t lore_head_playing = -1, lore_head_instance = -1;  // the head entry's playing byte / instance (mgr-relative)
    int64_t lore_base() const { return lore_count - lore_max * lore_stride; }  // entries end where the count byte is
};
extern VoLayout VL;

// What resolved (features audio.play / audio.voiceover / audio.narration).
struct Have {
    bool play = false, voiceover = false, narration = false, transform = false, probe = false;
};
extern Have H;

// Captured at run time.
extern std::atomic<uintptr_t> g_sys;                 // SoundSystem*
extern std::atomic<uint32_t> g_scenes[KILN_AUDIO_SCENES];
extern std::atomic<uint32_t> g_listeners[KILN_AUDIO_SCENES];
extern std::atomic<bool> g_scenes_ok;
extern std::atomic<uint32_t> g_main_thread;          // thread id of the drains (UI frames, client steps)

// Probe statistics of the game's own SoundStart calls.
struct ProbeThread {
    std::atomic<uint32_t> id{0};
    std::atomic<uint64_t> calls{0};
};
constexpr int kProbeThreads = 8;
extern ProbeThread g_probe_threads[kProbeThreads];
extern std::atomic<uint64_t> g_probe_calls, g_probe_off_main;
extern std::atomic<uint64_t> g_probe_first_ms;

// Resolves symbols and installs hooks. false = audio.play is not usable (the service is not registered).
bool game_init();

// SEH-guarded calls into the sound system (main thread only). Return false when the call faulted.
bool g_sound_start(uint32_t scene_id, const uint8_t guid[16], const PlayParams* p, uint32_t* out_instance);
bool g_sound_stop(uint32_t instance, int64_t fade_ns);
bool g_sound_set_volume(uint32_t instance, float volume, int64_t fade_ns);
bool g_sound_set_position(uint32_t instance, const int64_t pos[3]);
bool g_sound_is_playing(uint32_t instance, bool* out);
// The client world's entity position (main thread, inside a client step). false = no such entity / no transform.
bool g_entity_position(uintptr_t world, KilnEntity entity, KilnVec3* out);

// Voice-over manager calls (only inside the VoiceoverUpdate hook, with the manager the game passed).
bool g_vo_request(uintptr_t mgr, const uint8_t guid[16], KilnEntity speaker);
bool g_vo_set_position(uintptr_t mgr, const int64_t pos[3]);
bool g_lore_push(uintptr_t mgr, const uint8_t guid[16], uint32_t name, uint32_t desc);
// SEH-guarded reads of the manager.
bool g_read(uintptr_t addr, void* out, size_t n);

// Service callbacks from the hooks (audio.cpp).
void on_scenes_captured();
void on_vo_update_pre(uintptr_t mgr);
void on_vo_update_post(uintptr_t mgr);

int64_t m_to_fixed(double m);
double fixed_to_m(int64_t v);
uint64_t now_ms();

}  // namespace kau
