// SPDX-License-Identifier: MIT
// kiln.audio: sounds, music, voice-over and narration from mods, in the game client (native/core/abi/services/
// kiln_audio.h, docs/kiln/services/audio.md).
//
// One thread. Every request from any thread becomes a handle in PENDING; the game is only called on the main thread:
//  - sounds: in the drain that runs after each UI frame (kiln.ui.tick, menus included) and before each client-world
//    step (in game), whichever comes first; requests that follow an entity wait for a client step;
//  - voice-over lines and narration: inside the game's voice-over update (hook), with the manager the game passes.
// Each drain also polls the game for every live handle (SoundIsPlaying: the instance still exists), moves following
// sounds to their entity's CurrentTransform, applies stop/volume/position requests, and forgets finished handles
// after 60 s. Handles are Kiln ids; the game's instance ids behind them are generation-checked by the game.
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "audio.h"
#include "services/kiln_assets.h"
#include "services/kiln_client.h"
#include "services/kiln_ui.h"

namespace kau {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;

namespace {

constexpr uint32_t kMaxLive = 4096;
constexpr uint64_t kPendingTimeoutMs = 10000;
constexpr uint64_t kKeepFinishedMs = 60000;
constexpr int64_t kFollowGoneFadeNs = 100'000'000;  // 100 ms
constexpr int64_t kMusicSlotMinFadeNs = 250'000'000;
constexpr uint32_t kVoiceoverType = 0x53e9cf8au;    // FNV-1a("keen::VoiceoverResource"), the manager's resolve type
constexpr uint32_t kContainerType = 0xf257640cu;    // FNV-1a("keen::SoundContainerResource"), what SoundStart resolves
constexpr uint64_t kCatalogRefreshMs = 2000;
constexpr size_t kCatalogSane = 1000;               // fewer cues than this: the catalog didn't read, so no cue is refused
constexpr int kLogFirst = 40;                       // info lines for the first N plays/ends/failures, then debug

const char* const kSceneNames[KILN_AUDIO_SCENES] = {"music", "ui", "game", "ambience", "character", "voicechat", "lore"};
const char* const kSlotNames[5] = {"en", "de", "fr", "es", "zh"};

struct Entry {
    KilnSound id = 0;
    KilnOwner owner = 0;
    uint32_t kind = KILN_AUDIO_KIND_SOUND;
    uint32_t state = KILN_SOUND_PENDING;
    uint32_t reason = KILN_AUDIO_END_NONE;
    uint32_t info_flags = 0;
    uint32_t req_flags = 0;
    uint8_t guid[16] = {};
    uint32_t scene = 0;
    KilnEntity entity = 0;
    KilnVec3 position{};
    float volume = 1.f;
    float verify_radius = 10.f;
    int64_t fade_in = 0;
    uint32_t instance = 0;
    uint32_t thread = 0;
    uint32_t language_slot = 0xFFFFFFFFu;
    uint32_t name_loca = 0, desc_loca = 0;
    uint64_t queued_ms = 0, started_ms = 0, ended_ms = 0;
    bool following = false;
    bool pushed = false;      // voice-over: requested from the manager / narration: pushed into the lore queue
    bool seen_queued = false; // narration: seen in the lore queue after the push
    uint32_t frames = 0;      // voice-over: manager updates since the push
    // requests from other threads, applied by the next drain / voice-over update
    bool stop_req = false;
    int64_t stop_fade = 0;
    bool vol_req = false;
    float vol_new = 1.f;
    int64_t vol_fade = 0;
    bool pos_req = false;
    KilnVec3 pos_new{};
};

std::mutex g_mu;  // everything below
std::unordered_map<KilnSound, Entry> g_entries;
std::deque<KilnSound> g_vo_queue;  // voice-over and narration requests in call order
KilnSound g_next = 1;
uint32_t g_live = 0;
KilnSound g_music_slot = 0, g_vo_current = 0;
uint64_t g_played = 0, g_failed = 0;
int g_logged_plays = 0, g_logged_ends = 0, g_logged_fails = 0;
bool g_world_ended = false;
uint64_t g_last_cleanup = 0;

// cached from the main thread
std::atomic<uint32_t> g_vo_language{0}, g_vo_scene{0}, g_lore_scene{0}, g_lore_mode{0}, g_lore_count{0};
std::atomic<float> g_vo_volume{0.f};
std::atomic<uint64_t> g_vo_seen_ms{0};
std::atomic<uint32_t> g_ui_thread{0}, g_client_thread{0};
std::atomic<bool> g_frames{false};
KilnVec3 g_local_pos{};
bool g_local_pos_ok = false;
std::atomic<bool> g_thread_warned{false};
std::atomic<bool> g_probe_summary{false};

const KilnAssetsApi* g_assets = nullptr;
const KilnClientApi* g_client = nullptr;
const KilnUiTickApi* g_uitick = nullptr;

bool final_state(uint32_t s) { return s == KILN_SOUND_STOPPED || s == KILN_SOUND_FAILED; }

std::string guid_text(const uint8_t g[16]) {  // .NET Guid text form of the game's byte order
    char b[40];
    std::snprintf(b, sizeof b, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[3], g[2], g[1],
                  g[0], g[5], g[4], g[7], g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
    return b;
}

uint32_t slot_of_language(uint32_t lang) {  // the manager's mapping (docs/re/audio.md 3.3): other languages -> English
    switch (lang) {
        case 3: return 1;   // De_De
        case 5: return 2;   // Fr_Fr
        case 7: return 3;   // Es_Es
        case 20: return 4;  // Zh_Cn
        default: return 0;  // En_Us and the rest
    }
}

const char* reason_text(uint32_t r) {
    switch (r) {
        case KILN_AUDIO_END_FINISHED: return "finished";
        case KILN_AUDIO_END_STOPPED: return "stopped";
        case KILN_AUDIO_END_ENTITY_GONE: return "entity gone";
        case KILN_AUDIO_END_START_FAILED: return "SoundStart returned 0 (unknown cue, no data, voice limit)";
        case KILN_AUDIO_END_NO_SYSTEM: return "no sound system";
        case KILN_AUDIO_END_TIMEOUT: return "timed out";
        case KILN_AUDIO_END_REPLACED: return "replaced";
        case KILN_AUDIO_END_SILENT: return "silent (no take for the voice language)";
        case KILN_AUDIO_END_REFUSED: return "refused by the lore queue";
        case KILN_AUDIO_END_WORLD_ENDED: return "the client world ended";
        case KILN_AUDIO_END_NOT_FOUND: return "no such cue or voice-over line (not the game's, not a mod's)";
        default: return "-";
    }
}
const char* kind_text(uint32_t k) {
    return k == KILN_AUDIO_KIND_VOICEOVER ? "voice-over" : k == KILN_AUDIO_KIND_NARRATION ? "narration" : "sound";
}

void finish(Entry& e, uint32_t state, uint32_t reason) {
    if (final_state(e.state)) return;
    e.state = state;
    e.reason = reason;
    e.ended_ms = now_ms();
    if (g_live) --g_live;
    if (state == KILN_SOUND_FAILED) ++g_failed;
    if (g_music_slot == e.id) g_music_slot = 0;
    if (g_vo_current == e.id) g_vo_current = 0;
    const bool info = state == KILN_SOUND_FAILED ? g_logged_fails++ < kLogFirst
                                                  : (e.kind != KILN_AUDIO_KIND_SOUND || g_logged_ends++ < kLogFirst);
    alog(info ? (state == KILN_SOUND_FAILED ? KILN_LOG_WARN : KILN_LOG_INFO) : KILN_LOG_DEBUG,
         "audio: #%u %s %s %s: %s%s", e.id, kind_text(e.kind), guid_text(e.guid).c_str(),
         state == KILN_SOUND_FAILED ? "FAILED" : "ended", reason_text(reason),
         e.started_ms ? (std::string(", played ") + std::to_string(e.ended_ms - e.started_ms) + " ms").c_str() : "");
}

// The game's SoundStart hands out an instance for any GUID, also one no resource has: that instance never plays and never
// ends, so the handle would read Playing for good. The cue catalog (the game's containers plus the ones mods serve,
// kiln.assets resource_list) tells an unknown cue from one that just isn't loaded yet. Read on the first use and again when a
// cue is missing from it (a mod's new cue), at most every 2 s. A catalog that didn't read refuses nothing.
std::vector<std::array<uint8_t, 16>> g_catalog;  // sorted
uint64_t g_catalog_ms = 0;

bool catalog_has(const uint8_t guid[16]) {
    std::array<uint8_t, 16> k;
    std::memcpy(k.data(), guid, 16);
    return std::binary_search(g_catalog.begin(), g_catalog.end(), k);
}

// No resource answers the key either (resource_read: NOT_FOUND only when neither a mod nor the game has it).
bool cue_missing(const uint8_t guid[16]) {
    KilnAssetKey k{};
    std::memcpy(k.guid, guid, 16);
    k.type = kContainerType;
    size_t len = 0;
    return g_assets->resource_read(&k, nullptr, 0, &len) == KILN_ERR_NOT_FOUND;
}

// true: the cue may play (known, or the catalog can't tell). false: no resource has this GUID.
bool cue_known(const uint8_t guid[16]) {
    if (!g_assets || g_assets->header.size < offsetof(KilnAssetsApi, resource_list) + sizeof(void*)) return true;
    if (g_catalog.size() >= kCatalogSane && catalog_has(guid)) return true;
    const uint64_t now = now_ms();
    if (g_catalog_ms && now - g_catalog_ms < kCatalogRefreshMs) return g_catalog.size() < kCatalogSane || !cue_missing(guid);
    g_catalog_ms = now;
    size_t n = 0;
    g_assets->resource_list(kContainerType, nullptr, 0, &n);
    if (n == 0 || n > 1'000'000) return true;
    std::vector<KilnAssetKey> keys(n + 64);
    if (g_assets->resource_list(kContainerType, keys.data(), keys.size(), &n) != KILN_OK) return true;
    keys.resize(std::min(n, keys.size()));
    g_catalog.clear();
    g_catalog.reserve(keys.size());
    for (const auto& k : keys) {
        std::array<uint8_t, 16> g;
        std::memcpy(g.data(), k.guid, 16);
        g_catalog.push_back(g);
    }
    std::sort(g_catalog.begin(), g_catalog.end());
    if (g_catalog.size() < kCatalogSane) return true;
    return catalog_has(guid) || !cue_missing(guid);
}

void fixed3(const KilnVec3& v, int64_t out[3]) {
    out[0] = m_to_fixed(v.x);
    out[1] = m_to_fixed(v.y);
    out[2] = m_to_fixed(v.z);
}

double dist(const KilnVec3& a, const KilnVec3& b) {
    const double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
    return std::sqrt(dx * dx + dy * dy + dz * dz);
}

// ---- sounds (drain, main thread, g_mu held) ----
void start_sound(Entry& e, bool client_tick, uintptr_t world) {
    const uint64_t now = now_ms();
    if (!g_scenes_ok.load(std::memory_order_acquire) || !g_sys.load()) {
        if (now - e.queued_ms > kPendingTimeoutMs) finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_NO_SYSTEM);
        return;
    }
    if ((e.req_flags & KILN_AUDIO_FOLLOW) && !client_tick) {
        if (now - e.queued_ms > kPendingTimeoutMs) finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_TIMEOUT);
        return;  // waits for a client step (the entity lives in the client world)
    }
    bool at = (e.req_flags & KILN_AUDIO_AT) != 0;
    KilnVec3 pos = e.position;
    if (e.req_flags & KILN_AUDIO_FOLLOW) {
        KilnVec3 ep{};
        if (g_entity_position(world, e.entity, &ep)) {
            if ((e.req_flags & KILN_AUDIO_VERIFY) && at && dist(ep, e.position) > e.verify_radius) {
                e.info_flags |= KILN_AUDIO_INFO_UNMATCHED;
                KA_I("audio: #%u entity %u is %.1f m from the expected position (limit %.1f m): playing at the expected "
                     "position without following (ids differ on this client)",
                     e.id, e.entity, dist(ep, e.position), e.verify_radius);
            } else {
                pos = ep;
                at = true;
                e.following = true;
                e.info_flags |= KILN_AUDIO_INFO_FOLLOWING;
            }
        } else if (at) {
            e.info_flags |= KILN_AUDIO_INFO_UNMATCHED;
            KA_I("audio: #%u entity %u not in the client world: playing at the given position without following", e.id,
                 e.entity);
        } else {
            finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_ENTITY_GONE);
            return;
        }
    }
    const uint32_t scene_id = g_scenes[e.scene].load(std::memory_order_relaxed);
    if (!scene_id) {
        finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_NO_SYSTEM);
        return;
    }
    if (!cue_known(e.guid)) {
        e.thread = GetCurrentThreadId();
        finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_NOT_FOUND);
        return;
    }
    PlayParams p{};
    if (at) fixed3(pos, p.pos);
    p.volume = e.fade_in > 0 ? 0.f : e.volume;
    uint32_t inst = 0;
    const bool ok = g_sound_start(scene_id, e.guid, &p, &inst);
    e.thread = GetCurrentThreadId();
    if (!ok || !inst) {
        if (!ok) KA_E("audio: #%u SoundStart faulted (cue %s)", e.id, guid_text(e.guid).c_str());
        finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_START_FAILED);
        return;
    }
    e.instance = inst;
    e.state = KILN_SOUND_PLAYING;
    e.started_ms = now_ms();
    e.position = pos;
    ++g_played;
    if (e.fade_in > 0) {
        e.info_flags |= KILN_AUDIO_INFO_FADE_IN;
        g_sound_set_volume(inst, e.volume, e.fade_in);
    }
    if (e.req_flags & KILN_AUDIO_MUSIC_SLOT) {
        if (g_music_slot && g_music_slot != e.id) {
            auto it = g_entries.find(g_music_slot);
            if (it != g_entries.end() && !final_state(it->second.state)) {
                if (it->second.instance)
                    g_sound_stop(it->second.instance, std::max<int64_t>(e.fade_in, kMusicSlotMinFadeNs));
                finish(it->second, KILN_SOUND_STOPPED, KILN_AUDIO_END_REPLACED);
            }
        }
        g_music_slot = e.id;
    }
    const uint32_t main = g_main_thread.load();
    alog(g_logged_plays++ < kLogFirst ? KILN_LOG_INFO : KILN_LOG_DEBUG,
         "audio: #%u play %s scene %s (id %u)%s vol %.2f -> instance 0x%x, thread %lu%s, %llu ms after the request", e.id,
         guid_text(e.guid).c_str(), kSceneNames[e.scene], scene_id,
         e.following ? (" following entity " + std::to_string(e.entity)).c_str()
         : at        ? (" at " + std::to_string(pos.x) + "," + std::to_string(pos.y) + "," + std::to_string(pos.z)).c_str()
                     : " 2D",
         e.volume, inst, e.thread, e.thread == main ? " (main)" : " (NOT main)",
         static_cast<unsigned long long>(e.started_ms - e.queued_ms));
}

void update_sound(Entry& e, bool client_tick, uintptr_t world) {
    if (e.stop_req) {
        g_sound_stop(e.instance, e.stop_fade);
        finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_STOPPED);
        return;
    }
    if (g_world_ended && (e.following || e.scene == KILN_AUDIO_SCENE_GAME || e.scene == KILN_AUDIO_SCENE_AMBIENCE)) {
        g_sound_stop(e.instance, kFollowGoneFadeNs);
        finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_WORLD_ENDED);
        return;
    }
    bool playing = false;
    if (!g_sound_is_playing(e.instance, &playing) || !playing) {
        finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_FINISHED);
        return;
    }
    if (e.vol_req) {
        e.vol_req = false;
        e.volume = e.vol_new;
        g_sound_set_volume(e.instance, e.vol_new, e.vol_fade);
    }
    if (e.pos_req) {
        e.pos_req = false;
        e.following = false;
        e.info_flags &= ~KILN_AUDIO_INFO_FOLLOWING;
        e.position = e.pos_new;
        int64_t f[3];
        fixed3(e.pos_new, f);
        g_sound_set_position(e.instance, f);
    }
    if (e.following && client_tick) {
        KilnVec3 ep{};
        if (!g_entity_position(world, e.entity, &ep)) {
            g_sound_stop(e.instance, kFollowGoneFadeNs);
            finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_ENTITY_GONE);
            return;
        }
        if (dist(ep, e.position) > 0.01) {
            e.position = ep;
            int64_t f[3];
            fixed3(ep, f);
            g_sound_set_position(e.instance, f);
        }
    }
}

void cleanup_locked(uint64_t now) {
    if (now - g_last_cleanup < 1000) return;
    g_last_cleanup = now;
    for (auto it = g_entries.begin(); it != g_entries.end();) {
        if (final_state(it->second.state) && now - it->second.ended_ms > kKeepFinishedMs)
            it = g_entries.erase(it);
        else
            ++it;
    }
}

void note_thread(bool client_tick) {
    const DWORD tid = GetCurrentThreadId();
    (client_tick ? g_client_thread : g_ui_thread).store(tid, std::memory_order_relaxed);
    const uint32_t prev = g_main_thread.exchange(tid, std::memory_order_relaxed);
    if (prev == 0) {
        g_frames.store(true);
        KA_I("audio: main-thread drain runs on thread %lu (%s)", tid, client_tick ? "client-world steps" : "UI frames");
    } else if (prev != tid && !g_thread_warned.exchange(true)) {
        KA_W("audio: UI frames and client steps run on different threads (%u and %lu): sound calls come from both", prev,
             tid);
    }
}

void probe_summary(uint64_t now) {
    const uint64_t first = g_probe_first_ms.load(std::memory_order_relaxed);
    if (!H.probe || !first || now - first < 30000 || g_probe_summary.exchange(true)) return;
    std::string threads;
    for (auto& t : g_probe_threads) {
        const uint32_t id = t.id.load();
        if (!id) break;
        threads += (threads.empty() ? "" : ", ") + std::to_string(id) + (id == g_main_thread.load() ? " (main)" : "") +
                   ": " + std::to_string(t.calls.load());
    }
    KA_I("audio: the game's SoundStart calls in the first 30+ s: %llu, %llu not on the main thread; threads {%s}",
         static_cast<unsigned long long>(g_probe_calls.load()), static_cast<unsigned long long>(g_probe_off_main.load()),
         threads.c_str());
}

void drain(bool client_tick, uintptr_t world) {
    note_thread(client_tick);
    const uint64_t now = now_ms();
    probe_summary(now);
    std::lock_guard lk(g_mu);
    if (client_tick && g_client) {
        KilnClientState cs{};
        cs.size = sizeof cs;
        if (g_client->state(&cs) == KILN_OK && cs.local_player)
            g_local_pos_ok = g_entity_position(world, cs.local_player, &g_local_pos);
    }
    if (g_entries.empty()) {
        g_world_ended = false;
        return;
    }
    for (auto& [id, e] : g_entries) {
        if (final_state(e.state)) continue;
        if (e.kind != KILN_AUDIO_KIND_SOUND) {
            // voice-over work happens in the manager's update; time out when there is none (main menu)
            if (e.state == KILN_SOUND_PENDING && now - e.queued_ms > kPendingTimeoutMs &&
                now - g_vo_seen_ms.load() > 2000) {
                finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_TIMEOUT);
            } else if (g_world_ended) {
                finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_WORLD_ENDED);
            }
            continue;
        }
        if (e.state == KILN_SOUND_PENDING) {
            if (e.stop_req) {
                finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_STOPPED);
                continue;
            }
            if (g_world_ended && (e.req_flags & KILN_AUDIO_FOLLOW)) {
                finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_WORLD_ENDED);
                continue;
            }
            start_sound(e, client_tick, world);
        } else {
            update_sound(e, client_tick, world);
        }
    }
    g_world_ended = false;
    cleanup_locked(now);
}

void KILN_CALL on_client_pre(const KilnTickInfo*, void*) {
    const uintptr_t world = g_api->world(KILN_WORLD_CLIENT);
    drain(true, world);
}
void KILN_CALL on_ui_frame(uint64_t, void*) { drain(false, 0); }

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (!ev || ev->event != KILN_WORLD_STOPPING || ev->kind != KILN_WORLD_CLIENT) return;
    std::lock_guard lk(g_mu);
    g_world_ended = true;  // the next drain stops world sounds; voice-over lines end with the manager
    g_local_pos_ok = false;
    KA_I("audio: client world stopping: Kiln's world sounds and voice-over lines end at the next frame");
}

// ---- voice-over (inside the manager's update, main thread) ----
bool read_vo_slot(const uint8_t guid[16], uint32_t slot, uint8_t cue[16], bool* known) {
    *known = false;
    std::memset(cue, 0, 16);
    if (!g_assets) return true;
    KilnAssetKey k{};
    std::memcpy(k.guid, guid, 16);
    k.type = kVoiceoverType;
    uint8_t buf[128];
    size_t n = 0;
    const KilnResult rc = g_assets->resource_read(&k, buf, sizeof buf, &n);
    if (rc == KILN_ERR_NOT_FOUND) return false;  // not served by Kiln and not resident in the game (yet)
    if (rc != KILN_OK || n < 80) return true;
    std::memcpy(cue, buf + slot * 16, 16);
    *known = true;
    return true;
}

bool zero16(const uint8_t g[16]) {
    for (int i = 0; i < 16; ++i)
        if (g[i]) return false;
    return true;
}

template <class T>
T rd(uintptr_t a, T fallback = T{}) {
    T v{};
    return g_read(a, &v, sizeof v) ? v : fallback;
}

}  // namespace

void on_scenes_captured() {
    KA_I("audio: sound system 0x%llx, scenes music=%u ui=%u game=%u ambience=%u character=%u voicechat=%u lore=%u; "
         "listeners %u %u %u %u %u %u %u",
         static_cast<unsigned long long>(g_sys.load()), g_scenes[0].load(), g_scenes[1].load(), g_scenes[2].load(),
         g_scenes[3].load(), g_scenes[4].load(), g_scenes[5].load(), g_scenes[6].load(), g_listeners[0].load(),
         g_listeners[1].load(), g_listeners[2].load(), g_listeners[3].load(), g_listeners[4].load(),
         g_listeners[5].load(), g_listeners[6].load());
    if (!g_scenes_ok.load()) KA_W("audio: the scene table looks empty; sounds stay unavailable");
}

void on_vo_update_pre(uintptr_t mgr) {
    const uint32_t lang = rd<uint32_t>(mgr + VL.language);
    g_vo_language.store(lang, std::memory_order_relaxed);
    g_vo_volume.store(rd<float>(mgr + VL.volume), std::memory_order_relaxed);
    g_vo_scene.store(rd<uint32_t>(mgr + VL.scene), std::memory_order_relaxed);
    if (H.narration) {
        g_lore_scene.store(rd<uint32_t>(mgr + VL.lore_scene), std::memory_order_relaxed);
        g_lore_mode.store(rd<uint8_t>(mgr + VL.lore_mode), std::memory_order_relaxed);
    }
    const uint64_t prev_seen = g_vo_seen_ms.exchange(now_ms(), std::memory_order_relaxed);
    if (!prev_seen)
        KA_I("audio: voice-over manager 0x%llx running (thread %lu): voice language id %u (slot %s), voice scene %u, "
             "lore scene %u, lore mode %u",
             static_cast<unsigned long long>(mgr), GetCurrentThreadId(), lang, kSlotNames[slot_of_language(lang)],
             g_vo_scene.load(), g_lore_scene.load(), g_lore_mode.load());

    std::lock_guard lk(g_mu);
    const uint32_t slot = slot_of_language(lang);
    // stop requests for the current line
    if (g_vo_current) {
        auto it = g_entries.find(g_vo_current);
        if (it != g_entries.end() && it->second.stop_req && !final_state(it->second.state)) {
            Entry& e = it->second;
            uint8_t pending[16];
            const bool ours = g_read(mgr + VL.pending, pending, 16) && std::memcmp(pending, e.guid, 16) == 0;
            const uint32_t inst = rd<uint32_t>(mgr + VL.instance);
            if (ours && inst) g_sound_stop(inst, e.stop_fade);
            finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_STOPPED);
        }
    }
    // stop requests for narration: the head line's instance is stopped (the queue then pops it); a line still queued
    // behind another one is stopped when it reaches the head
    if (H.narration) {
        for (auto& [id, e] : g_entries) {
            if (e.kind != KILN_AUDIO_KIND_NARRATION || !e.pushed || !e.stop_req || final_state(e.state)) continue;
            uint8_t head[16];
            if (!rd<uint8_t>(mgr + VL.lore_count) || !g_read(mgr + VL.lore_base(), head, 16) ||
                std::memcmp(head, e.guid, 16) != 0)
                continue;
            const uint32_t inst = rd<uint32_t>(mgr + VL.lore_head_instance);
            if (!inst) continue;  // not started yet: next frame
            g_sound_stop(inst, e.stop_fade);
            finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_STOPPED);
        }
    }
    while (!g_vo_queue.empty()) {
        const KilnSound id = g_vo_queue.front();
        g_vo_queue.pop_front();
        auto it = g_entries.find(id);
        if (it == g_entries.end()) continue;
        Entry& e = it->second;
        if (final_state(e.state)) continue;
        if (e.stop_req) {
            finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_STOPPED);
            continue;
        }
        e.language_slot = slot;
        e.thread = GetCurrentThreadId();
        uint8_t cue[16];
        bool known = false;
        if (!read_vo_slot(e.guid, slot, cue, &known) && e.kind == KILN_AUDIO_KIND_NARRATION) {
            // a narration of an unknown line would sit in the lore queue forever: refuse it
            finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_NOT_FOUND);
            continue;
        }
        if (known && zero16(cue)) {
            KA_I("audio: #%u %s %s: no take for voice language id %u (slot %s): silent, as the cooked data says", e.id,
                 kind_text(e.kind), guid_text(e.guid).c_str(), lang, kSlotNames[slot]);
            finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_SILENT);
            continue;
        }
        if (e.kind == KILN_AUDIO_KIND_VOICEOVER) {
            if (g_vo_current && g_vo_current != e.id) {
                auto cur = g_entries.find(g_vo_current);
                if (cur != g_entries.end()) finish(cur->second, KILN_SOUND_STOPPED, KILN_AUDIO_END_REPLACED);
            }
            if (!g_vo_request(mgr, e.guid, e.entity)) {
                KA_E("audio: #%u VoiceoverRequest faulted", e.id);
                finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_START_FAILED);
                continue;
            }
            KilnVec3 at = e.position;
            bool have_at = (e.req_flags & KILN_AUDIO_AT) != 0;
            if (!have_at && !e.entity && g_local_pos_ok) {  // no speaker, no position: at the player (+1.5 m), as the game
                at = {g_local_pos.x, g_local_pos.y + 1.5, g_local_pos.z};
                have_at = true;
            }
            if (have_at) {
                int64_t f[3];
                fixed3(at, f);
                g_vo_set_position(mgr, f);
                e.position = at;
            }
            e.pushed = true;
            e.frames = 0;
            g_vo_current = e.id;
            KA_I("audio: #%u voice-over %s requested: speaker %u, language slot %s%s", e.id, guid_text(e.guid).c_str(),
                 e.entity, kSlotNames[slot], known ? (" -> cue " + guid_text(cue)).c_str() : " (cue not checked)");
        } else {
            const uint32_t before = rd<uint8_t>(mgr + VL.lore_count);
            if (!g_lore_push(mgr, e.guid, e.name_loca, e.desc_loca)) {
                KA_E("audio: #%u lore push faulted", e.id);
                finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_START_FAILED);
                continue;
            }
            e.pushed = true;
            KA_I("audio: #%u narration %s pushed into the lore queue (mode %u, %u queued before), language slot %s%s",
                 e.id, guid_text(e.guid).c_str(), g_lore_mode.load(), before, kSlotNames[slot],
                 known ? (" -> cue " + guid_text(cue)).c_str() : " (cue not checked)");
        }
    }
}

void on_vo_update_post(uintptr_t mgr) {
    std::lock_guard lk(g_mu);
    if (g_vo_current) {
        auto it = g_entries.find(g_vo_current);
        if (it != g_entries.end() && !final_state(it->second.state)) {
            Entry& e = it->second;
            ++e.frames;
            uint8_t pending[16] = {};
            g_read(mgr + VL.pending, pending, 16);
            const bool ours = std::memcmp(pending, e.guid, 16) == 0;
            const bool playing = rd<uint8_t>(mgr + VL.playing) != 0;
            const uint32_t inst = rd<uint32_t>(mgr + VL.instance);
            if (!ours) {
                finish(e, KILN_SOUND_STOPPED,
                       e.state == KILN_SOUND_PLAYING ? KILN_AUDIO_END_FINISHED : KILN_AUDIO_END_REPLACED);
            } else if (playing && e.state == KILN_SOUND_PENDING) {
                e.instance = inst;
                if (inst) {
                    e.state = KILN_SOUND_PLAYING;
                    e.started_ms = now_ms();
                    ++g_played;
                    KA_I("audio: #%u voice-over playing: instance 0x%x in scene %u, %llu ms after the request", e.id,
                         inst, g_vo_scene.load(), static_cast<unsigned long long>(e.started_ms - e.queued_ms));
                } else {
                    finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_SILENT);  // the game found no cue for the language
                }
            } else if (e.state == KILN_SOUND_PENDING && now_ms() - e.queued_ms > kPendingTimeoutMs) {
                uint8_t none[16] = {};
                g_vo_request(mgr, none, 0);  // withdraw the line (its resource never loaded)
                finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_TIMEOUT);
            }
        }
    }
    if (!H.narration) return;
    const uint32_t count = std::min<uint32_t>(rd<uint8_t>(mgr + VL.lore_count), static_cast<uint32_t>(VL.lore_max));
    g_lore_count.store(count, std::memory_order_relaxed);
    for (auto& [id, e] : g_entries) {
        if (e.kind != KILN_AUDIO_KIND_NARRATION || !e.pushed || final_state(e.state)) continue;
        int index = -1;
        for (uint32_t i = 0; i < count; ++i) {
            uint8_t g[16];
            if (g_read(mgr + VL.lore_base() + i * VL.lore_stride, g, 16) && std::memcmp(g, e.guid, 16) == 0) {
                index = static_cast<int>(i);
                break;
            }
        }
        if (index >= 0) {
            e.seen_queued = true;
            if (index == 0) {
                e.info_flags &= ~KILN_AUDIO_INFO_QUEUED;
                if (e.state == KILN_SOUND_PENDING || !e.started_ms) {
                    e.state = KILN_SOUND_PLAYING;
                    e.started_ms = now_ms();
                    ++g_played;
                    KA_I("audio: #%u narration at the head of the lore queue (playing), %llu ms after the request", e.id,
                         static_cast<unsigned long long>(e.started_ms - e.queued_ms));
                }
            } else {
                e.info_flags |= KILN_AUDIO_INFO_QUEUED;
                if (e.state == KILN_SOUND_PENDING) e.state = KILN_SOUND_PLAYING;  // queued behind another line
            }
        } else if (e.seen_queued) {
            finish(e, KILN_SOUND_STOPPED, KILN_AUDIO_END_FINISHED);
        } else {
            finish(e, KILN_SOUND_FAILED, KILN_AUDIO_END_REFUSED);
        }
    }
}

namespace {

// ---- the C table ----
KilnResult KILN_CALL api_status(KilnAudioStatus* out) {
    if (!out || out->size < sizeof(KilnAudioStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    uint32_t f = 0;
    if (H.play) f |= KILN_AUDIO_HAS_PLAY;
    if (H.voiceover) f |= KILN_AUDIO_HAS_VOICEOVER;
    if (H.narration) f |= KILN_AUDIO_HAS_NARRATION;
    if (g_scenes_ok.load()) f |= KILN_AUDIO_SYSTEM;
    if (g_vo_seen_ms.load() && now_ms() - g_vo_seen_ms.load() < 2000) f |= KILN_AUDIO_MANAGER;
    if (g_frames.load()) f |= KILN_AUDIO_FRAMES;
    if (H.probe) f |= KILN_AUDIO_PROBE;
    out->flags = f;
    for (int i = 0; i < KILN_AUDIO_SCENES; ++i) {
        out->scenes[i] = g_scenes[i].load();
        out->listeners[i] = g_listeners[i].load();
    }
    out->main_thread = g_main_thread.load();
    out->voice_language = g_vo_language.load();
    out->voice_volume = g_vo_volume.load();
    out->voice_scene = g_vo_scene.load();
    out->lore_scene = g_lore_scene.load();
    out->lore_mode = g_lore_mode.load();
    out->lore_queued = g_lore_count.load();
    uint32_t threads = 0;
    for (auto& t : g_probe_threads)
        if (t.id.load()) ++threads;
    out->game_threads = threads;
    out->game_starts = g_probe_calls.load();
    out->game_starts_off_main = g_probe_off_main.load();
    out->system = g_sys.load();
    std::lock_guard lk(g_mu);
    out->live = g_live;
    out->played = g_played;
    out->failed = g_failed;
    return KILN_OK;
}

KilnSound add_entry(Entry&& e) {
    std::lock_guard lk(g_mu);
    if (g_live >= kMaxLive) return 0;
    e.id = g_next++;
    if (g_next == 0) g_next = 1;
    e.queued_ms = now_ms();
    const KilnSound id = e.id;
    const bool voice = e.kind != KILN_AUDIO_KIND_SOUND;
    g_entries[id] = std::move(e);
    ++g_live;
    if (voice) g_vo_queue.push_back(id);
    return id;
}

KilnSound KILN_CALL api_play(KilnOwner owner, const KilnAudioPlay* r) {
    if (!r || r->size < sizeof(KilnAudioPlay) || r->scene >= KILN_AUDIO_SCENES || zero16(r->cue)) return 0;
    if (!std::isfinite(r->volume)) return 0;
    if ((r->flags & KILN_AUDIO_FOLLOW) && !r->entity) return 0;
    if ((r->flags & KILN_AUDIO_AT) &&
        !(std::isfinite(r->position.x) && std::isfinite(r->position.y) && std::isfinite(r->position.z)))
        return 0;
    Entry e;
    e.owner = owner;
    e.kind = KILN_AUDIO_KIND_SOUND;
    e.req_flags = r->flags;
    std::memcpy(e.guid, r->cue, 16);
    e.scene = r->scene;
    e.entity = r->entity;
    e.position = r->position;
    e.volume = std::clamp(r->volume, 0.f, 1.f);
    e.verify_radius = r->verify_radius > 0 ? r->verify_radius : 10.f;
    e.fade_in = r->fade_in_ns > 0 ? r->fade_in_ns : 0;
    return add_entry(std::move(e));
}

KilnResult KILN_CALL api_stop(KilnSound h, int64_t fade_ns) {
    std::lock_guard lk(g_mu);
    auto it = g_entries.find(h);
    if (it == g_entries.end()) return KILN_ERR_NOT_FOUND;
    if (!final_state(it->second.state)) {
        it->second.stop_req = true;
        it->second.stop_fade = fade_ns > 0 ? fade_ns : 0;
    }
    return KILN_OK;
}

KilnResult KILN_CALL api_set_volume(KilnSound h, float volume, int64_t fade_ns) {
    if (!std::isfinite(volume)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    auto it = g_entries.find(h);
    if (it == g_entries.end()) return KILN_ERR_NOT_FOUND;
    Entry& e = it->second;
    if (e.kind != KILN_AUDIO_KIND_SOUND) return KILN_ERR_UNAVAILABLE;  // voice-over volume is the player's setting
    if (final_state(e.state)) return KILN_OK;
    if (e.state == KILN_SOUND_PENDING) {
        e.volume = std::clamp(volume, 0.f, 1.f);
        return KILN_OK;
    }
    e.vol_req = true;
    e.vol_new = std::clamp(volume, 0.f, 1.f);
    e.vol_fade = fade_ns > 0 ? fade_ns : 0;
    return KILN_OK;
}

KilnResult KILN_CALL api_set_position(KilnSound h, const KilnVec3* pos) {
    if (!pos || !std::isfinite(pos->x) || !std::isfinite(pos->y) || !std::isfinite(pos->z))
        return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    auto it = g_entries.find(h);
    if (it == g_entries.end()) return KILN_ERR_NOT_FOUND;
    Entry& e = it->second;
    if (e.kind != KILN_AUDIO_KIND_SOUND) return KILN_ERR_UNAVAILABLE;
    if (final_state(e.state)) return KILN_OK;
    if (e.state == KILN_SOUND_PENDING) {
        e.position = *pos;
        e.req_flags = (e.req_flags | KILN_AUDIO_AT) & ~(KILN_AUDIO_FOLLOW | KILN_AUDIO_VERIFY);
        return KILN_OK;
    }
    e.pos_req = true;
    e.pos_new = *pos;
    return KILN_OK;
}

uint32_t KILN_CALL api_state(KilnSound h) {
    std::lock_guard lk(g_mu);
    auto it = g_entries.find(h);
    if (it == g_entries.end()) return KILN_SOUND_NONE;
    const Entry& e = it->second;
    if (!final_state(e.state) && e.stop_req) return KILN_SOUND_STOPPED;  // stop requested: reads stopped at once
    return e.state;
}

KilnResult KILN_CALL api_info(KilnSound h, KilnSoundInfo* out) {
    if (!out || out->size < sizeof(KilnSoundInfo)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    auto it = g_entries.find(h);
    if (it == g_entries.end()) return KILN_ERR_NOT_FOUND;
    const Entry& e = it->second;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    // a stop request reads as done at once (state and reason), as api_state does; a narration queued behind another line is
    // taken off the screen and the API now and stopped by the game when it reaches the head of the lore queue
    const bool stopping = !final_state(e.state) && e.stop_req;
    out->state = stopping ? KILN_SOUND_STOPPED : e.state;
    out->kind = e.kind;
    out->reason = stopping ? KILN_AUDIO_END_STOPPED : e.reason;
    out->flags = e.info_flags;
    out->scene = e.scene;
    out->instance = e.instance;
    out->thread = e.thread;
    out->entity = e.entity;
    out->language_slot = e.language_slot;
    out->queued_ms = e.queued_ms;
    out->started_ms = e.started_ms;
    out->ended_ms = e.ended_ms;
    out->position = e.position;
    return KILN_OK;
}

uint32_t KILN_CALL api_stop_owner(KilnOwner owner, int64_t fade_ns) {
    std::lock_guard lk(g_mu);
    uint32_t n = 0;
    for (auto& [id, e] : g_entries) {
        if (e.owner != owner || final_state(e.state) || e.stop_req) continue;
        e.stop_req = true;
        e.stop_fade = fade_ns > 0 ? fade_ns : 0;
        ++n;
    }
    return n;
}

KilnSound KILN_CALL api_voiceover_play(KilnOwner owner, const uint8_t vo[16], KilnEntity speaker, const KilnVec3* pos) {
    if (!H.voiceover || !vo || zero16(vo)) return 0;
    Entry e;
    e.owner = owner;
    e.kind = KILN_AUDIO_KIND_VOICEOVER;
    std::memcpy(e.guid, vo, 16);
    e.scene = KILN_AUDIO_SCENE_GAME;
    e.entity = speaker;
    if (pos && std::isfinite(pos->x) && std::isfinite(pos->y) && std::isfinite(pos->z)) {
        e.position = *pos;
        e.req_flags = KILN_AUDIO_AT;
    }
    return add_entry(std::move(e));
}

KilnResult KILN_CALL api_voiceover_stop(int64_t fade_ns) {
    std::lock_guard lk(g_mu);
    const int64_t fade = fade_ns > 0 ? fade_ns : 0;
    // the line on the channel, and lines still waiting for the manager's next update
    if (g_vo_current) {
        auto it = g_entries.find(g_vo_current);
        if (it != g_entries.end() && !final_state(it->second.state)) {
            it->second.stop_req = true;
            it->second.stop_fade = fade;
        }
    }
    for (const KilnSound id : g_vo_queue) {
        auto it = g_entries.find(id);
        if (it == g_entries.end() || it->second.kind != KILN_AUDIO_KIND_VOICEOVER || final_state(it->second.state))
            continue;
        it->second.stop_req = true;
        it->second.stop_fade = fade;
    }
    return KILN_OK;
}

KilnSound KILN_CALL api_narrate(KilnOwner owner, const uint8_t vo[16], uint32_t name_loca, uint32_t desc_loca) {
    if (!H.narration || !vo || zero16(vo)) return 0;
    Entry e;
    e.owner = owner;
    e.kind = KILN_AUDIO_KIND_NARRATION;
    std::memcpy(e.guid, vo, 16);
    e.scene = KILN_AUDIO_SCENE_LORE;
    e.name_loca = name_loca;
    e.desc_loca = desc_loca;
    return add_entry(std::move(e));
}

const char* state_text(uint32_t s) {
    switch (s) {
        case KILN_SOUND_PENDING: return "pending";
        case KILN_SOUND_PLAYING: return "playing";
        case KILN_SOUND_STOPPED: return "stopped";
        case KILN_SOUND_FAILED: return "failed";
        default: return "none";
    }
}

KilnResult KILN_CALL api_status_json(char* buf, size_t cap, size_t* out_len) {
    KilnAudioStatus s{};
    s.size = sizeof s;
    api_status(&s);
    std::string j = "{";
    char b[512];
    std::snprintf(b, sizeof b,
                  "\"play\":%s,\"voiceover\":%s,\"narration\":%s,\"system\":%s,\"manager\":%s,\"frames\":%s,"
                  "\"probe\":%s,\"systemPtr\":\"0x%llx\",\"mainThread\":%u,\"voiceLanguage\":%u,\"voiceSlot\":\"%s\","
                  "\"voiceVolume\":%.3f,\"voiceScene\":%u,\"loreScene\":%u,\"loreMode\":%u,\"loreQueued\":%u,"
                  "\"live\":%u,\"played\":%llu,\"failed\":%llu,\"gameStarts\":%llu,\"gameStartsOffMain\":%llu,",
                  (s.flags & KILN_AUDIO_HAS_PLAY) ? "true" : "false", (s.flags & KILN_AUDIO_HAS_VOICEOVER) ? "true" : "false",
                  (s.flags & KILN_AUDIO_HAS_NARRATION) ? "true" : "false", (s.flags & KILN_AUDIO_SYSTEM) ? "true" : "false",
                  (s.flags & KILN_AUDIO_MANAGER) ? "true" : "false", (s.flags & KILN_AUDIO_FRAMES) ? "true" : "false",
                  (s.flags & KILN_AUDIO_PROBE) ? "true" : "false", static_cast<unsigned long long>(s.system),
                  s.main_thread, s.voice_language, kSlotNames[slot_of_language(s.voice_language)], s.voice_volume,
                  s.voice_scene, s.lore_scene, s.lore_mode, s.lore_queued, s.live,
                  static_cast<unsigned long long>(s.played), static_cast<unsigned long long>(s.failed),
                  static_cast<unsigned long long>(s.game_starts), static_cast<unsigned long long>(s.game_starts_off_main));
    j += b;
    j += "\"scenes\":[";
    for (int i = 0; i < KILN_AUDIO_SCENES; ++i) j += (i ? "," : "") + std::to_string(s.scenes[i]);
    j += "],\"gameThreads\":[";
    bool first = true;
    for (auto& t : g_probe_threads) {
        const uint32_t id = t.id.load();
        if (!id) break;
        std::snprintf(b, sizeof b, "%s{\"thread\":%u,\"calls\":%llu,\"main\":%s}", first ? "" : ",", id,
                      static_cast<unsigned long long>(t.calls.load()), id == s.main_thread ? "true" : "false");
        j += b;
        first = false;
    }
    j += "],\"handles\":[";
    {
        std::lock_guard lk(g_mu);
        std::vector<const Entry*> list;
        for (auto& [id, e] : g_entries) list.push_back(&e);
        std::sort(list.begin(), list.end(), [](const Entry* a, const Entry* c) { return a->id > c->id; });
        if (list.size() > 64) list.resize(64);
        first = true;
        for (const Entry* e : list) {
            std::snprintf(b, sizeof b,
                          "%s{\"id\":%u,\"kind\":\"%s\",\"cue\":\"%s\",\"state\":\"%s\",\"reason\":\"%s\","
                          "\"scene\":\"%s\",\"instance\":%u,\"thread\":%u,\"entity\":%u,\"following\":%s,"
                          "\"queuedMs\":%llu,\"startMs\":%llu,\"endMs\":%llu}",
                          first ? "" : ",", e->id, kind_text(e->kind), guid_text(e->guid).c_str(), state_text(e->state),
                          reason_text(e->reason), kSceneNames[e->scene], e->instance, e->thread, e->entity,
                          e->following ? "true" : "false", static_cast<unsigned long long>(e->queued_ms),
                          static_cast<unsigned long long>(e->started_ms), static_cast<unsigned long long>(e->ended_ms));
            j += b;
            first = false;
        }
    }
    j += "]}";
    if (out_len) *out_len = j.size();
    if (!buf || cap < j.size() + 1) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, j.data(), j.size());
    buf[j.size()] = 0;
    return KILN_OK;
}

// the managed mirror (managed/services/Kiln.Audio/AudioNative.cs) relies on these sizes
static_assert(sizeof(KilnAudioPlay) == 72 && sizeof(KilnSoundInfo) == 88 && sizeof(KilnAudioStatus) == 144 &&
              sizeof(KilnAudioApi) == 104);

const KilnAudioApi kTable = {
    {sizeof(KilnAudioApi), KILN_AUDIO_VERSION},
    &api_status,
    &api_play,
    &api_stop,
    &api_set_volume,
    &api_set_position,
    &api_state,
    &api_info,
    &api_stop_owner,
    &api_voiceover_play,
    &api_voiceover_stop,
    &api_narrate,
    &api_status_json,
};

void KILN_CALL on_ready(void*) {
    g_assets = static_cast<const KilnAssetsApi*>(
        g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, KILN_ASSETS_VERSION));
    if (g_assets && g_assets->header.size < offsetof(KilnAssetsApi, resource_read) + sizeof(void*)) g_assets = nullptr;
    g_client = static_cast<const KilnClientApi*>(
        g_api->service(KilnStr{KILN_CLIENT_SERVICE, sizeof(KILN_CLIENT_SERVICE) - 1}, KILN_CLIENT_VERSION));
    g_uitick = static_cast<const KilnUiTickApi*>(
        g_api->service(KilnStr{KILN_UI_TICK_SERVICE, sizeof(KILN_UI_TICK_SERVICE) - 1}, KILN_UI_TICK_VERSION));
    KilnHandle h = 0;
    if (g_uitick && g_uitick->subscribe(g_owner, &on_ui_frame, nullptr, &h) == KILN_OK) {
        KA_I("audio: drains after every UI frame (kiln.ui.tick) and before every client-world step");
    } else {
        KA_W("audio: no kiln.ui.tick: sounds start at the next client-world step only (nothing plays in the menus)");
    }
    if (!g_assets) KA_W("audio: no kiln.assets: voice-over language slots are not checked before playing");
}

}  // namespace
}  // namespace kau

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kau;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_AUDIO_SERVICE, sizeof(KILN_AUDIO_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    if (api->side() != KILN_SIDE_CLIENT) {
        KA_I("audio: not a game client (the dedicated server has no sound system): kiln.audio unavailable here; "
             "Kiln.Audio relays server calls to Kiln clients");
        return KILN_ERR_UNAVAILABLE;
    }
    if (!game_init()) return KILN_ERR_UNAVAILABLE;
    KilnHandle h = 0;
    if (api->subscribe_world_tick(owner, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h) != KILN_OK)
        KA_W("audio: could not subscribe to client-world steps: PlayOn (follow) is unavailable");
    api->subscribe_world(owner, &on_world, nullptr, &h);
    info->on_ready = &on_ready;
    const KilnResult rc = api->register_service(owner, KilnStr{KILN_AUDIO_SERVICE, sizeof(KILN_AUDIO_SERVICE) - 1}, &kTable);
    if (rc == KILN_OK)
        KA_I("audio: kiln.audio v1 ready (play%s%s%s); waiting for the game's sound system", H.voiceover ? ", voice-over" : "",
             H.narration ? ", narration" : "", H.probe ? ", SoundStart probe" : "");
    return rc;
}
