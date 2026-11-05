/* SPDX-License-Identifier: MIT
 * kiln.audio v1: play sounds, music and voice-over in the game client (docs/kiln/services/audio.md, research
 * docs/re/audio.md). Client only: the dedicated server has no sound system, and the service is absent there.
 *
 * What it drives: Keen's own sound system. A sound is a SoundContainerResource GUID (a "cue": takes, volume, loop,
 * 3D distances, voice limits), vanilla or served by kiln.assets, started in one of the game's sound scenes. Voice-over
 * lines are VoiceoverResource GUIDs (one cue per voice language) played through the game's voice-over manager: the
 * dialogue channel (one line at a time, at a speaker, the voice-over volume setting) or the lore queue (narration:
 * LoreVoiceover scene, ducks music and game sounds, the player's auto-play setting).
 *
 * Threads
 *  - Every function may be called from any thread. Requests are queued and executed on the game's main thread: sound
 *    calls in the next UI frame (menus included, needs kiln.ui) or the next client-world step, whichever comes first;
 *    voice-over calls inside the game's voice-over update (in game only). A handle is returned at once in state
 *    PENDING.
 *  - state / info / status read Kiln's own bookkeeping (updated every frame on the main thread), never game memory.
 * Handles
 *  - KilnSound ids are Kiln's (1..2^32-1, never reused within a process); the game's instance id behind a handle is
 *    generation-checked, so a stale handle never touches a reused game instance. A finished handle reads STOPPED for at
 *    least 60 s, then NONE.
 * Fail soft
 *  - Unknown build or missing symbols: the service is not registered (feature audio.play off), or voiceover_play /
 *    narrate return 0 (features audio.voiceover / audio.narration off). Nothing is hooked that is not resolved.
 * Owners
 *  - The service cannot see owner_revoke: call stop_owner when an owner goes away (Kiln.Audio does for mods). */
#ifndef KILN_AUDIO_H
#define KILN_AUDIO_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_AUDIO_SERVICE "kiln.audio"
#define KILN_AUDIO_VERSION 1u

typedef uint32_t KilnSound; /* 0 = none */

/* keen::SoundSceneType: the game's sound scenes (each with its own voices, listener and the user's volume slider). */
enum {
    KILN_AUDIO_SCENE_MUSIC = 0,     /* 8 voices: music (Kiln's music slot plays here) */
    KILN_AUDIO_SCENE_UI = 1,        /* 128 voices: 2D interface sounds */
    KILN_AUDIO_SCENE_GAME = 2,      /* 8192 instances / 128 voices: the world (positional) */
    KILN_AUDIO_SCENE_AMBIENCE = 3,  /* 1024 instances / 128 voices: ambience beds */
    KILN_AUDIO_SCENE_CHARACTER = 4, /* character creation */
    KILN_AUDIO_SCENE_VOICECHAT = 5, /* voice chat (not for mods) */
    KILN_AUDIO_SCENE_LORE = 6,      /* 2 voices: lore narration (ducks Music and Game) */
    KILN_AUDIO_SCENES = 7,
};

/* KilnAudioPlay.flags */
enum {
    KILN_AUDIO_AT = 1u,         /* `position` is set: start there (world position, metres) */
    KILN_AUDIO_FOLLOW = 2u,     /* follow client-world `entity`: start at its CurrentTransform, move with it every
                                   client step, stop (100 ms fade) when it is gone. Waits for a client-world step */
    KILN_AUDIO_VERIFY = 4u,     /* with FOLLOW and AT: `position` is where the entity should be (e.g. the server's view
                                   of an entity of the same id). Follow only if the client's entity is within
                                   verify_radius of it; else play at `position` without following (no id mapping) */
    KILN_AUDIO_MUSIC_SLOT = 8u, /* Kiln's music slot: stops the sound previously started with this flag (fade_in_ns,
                                   at least 250 ms) */
};

typedef struct KilnAudioPlay {
    uint32_t size;          /* sizeof(KilnAudioPlay) */
    uint32_t flags;         /* KILN_AUDIO_AT / FOLLOW / VERIFY / MUSIC_SLOT */
    uint8_t cue[16];        /* SoundContainerResource GUID, game byte order (.NET Guid.ToByteArray()) */
    uint32_t scene;         /* KILN_AUDIO_SCENE_* */
    KilnEntity entity;      /* FOLLOW: a client-world entity (on a host the ids equal the server world's) */
    KilnVec3 position;      /* AT: metres */
    float volume;           /* 0..1, multiplies the cue's own volume and the user's scene slider */
    float verify_radius;    /* VERIFY: metres (0 = 10) */
    int64_t fade_in_ns;     /* > 0: start silent and fade to `volume` (best effort: a streamed take that is still
                               loading starts at `volume`) */
} KilnAudioPlay;

/* state() */
enum {
    KILN_SOUND_NONE = 0,    /* unknown or expired handle */
    KILN_SOUND_PENDING = 1, /* queued: not started yet (waiting for the main thread, a client step or the manager) */
    KILN_SOUND_PLAYING = 2, /* the game's instance exists (a looping cue stays here until stop) */
    KILN_SOUND_STOPPED = 3, /* ended, stopped, or silent (see KilnSoundInfo.reason) */
    KILN_SOUND_FAILED = 4,  /* never started (see KilnSoundInfo.reason) */
};

/* KilnSoundInfo.reason */
enum {
    KILN_AUDIO_END_NONE = 0,
    KILN_AUDIO_END_FINISHED = 1,      /* the game's instance ended (one-shot done) */
    KILN_AUDIO_END_STOPPED = 2,       /* stop / stop_owner / voiceover_stop */
    KILN_AUDIO_END_ENTITY_GONE = 3,   /* FOLLOW: the entity disappeared (or the client world ended) */
    KILN_AUDIO_END_START_FAILED = 4,  /* SoundStart returned 0: unknown cue, no data, voice limit, bad scene */
    KILN_AUDIO_END_NO_SYSTEM = 5,     /* the sound system or scenes were never captured */
    KILN_AUDIO_END_TIMEOUT = 6,       /* waited 10 s (no client world for FOLLOW, no voice-over manager, a voice-over
                                         resource that never loaded) */
    KILN_AUDIO_END_REPLACED = 7,      /* another line took the voice-over channel / the music slot */
    KILN_AUDIO_END_SILENT = 8,        /* voice-over: the line has no cue for the player's voice language (as the game,
                                         Kiln plays nothing; a mod's cook may fill slots from another language) */
    KILN_AUDIO_END_REFUSED = 9,       /* narration: the lore queue did not take it (auto-play "never", queue full) */
    KILN_AUDIO_END_WORLD_ENDED = 10,  /* the client world ended (Game/Ambience-scene sounds and voice-over stop) */
    KILN_AUDIO_END_NOT_FOUND = 11,    /* voice-over: no VoiceoverResource with that GUID */
};

/* KilnSoundInfo.kind */
enum {
    KILN_AUDIO_KIND_SOUND = 1,
    KILN_AUDIO_KIND_VOICEOVER = 2, /* the dialogue channel */
    KILN_AUDIO_KIND_NARRATION = 3, /* the lore queue */
};

/* KilnSoundInfo.flags */
enum {
    KILN_AUDIO_INFO_FOLLOWING = 1u,  /* the position follows the entity */
    KILN_AUDIO_INFO_UNMATCHED = 2u,  /* VERIFY failed: played at `position` without following */
    KILN_AUDIO_INFO_FADE_IN = 4u,
    KILN_AUDIO_INFO_QUEUED = 8u,     /* narration: in the lore queue behind another line */
};

typedef struct KilnSoundInfo {
    uint32_t size;          /* set by the caller: sizeof(KilnSoundInfo) */
    uint32_t state;         /* KILN_SOUND_* */
    uint32_t kind;          /* KILN_AUDIO_KIND_* */
    uint32_t reason;        /* KILN_AUDIO_END_* (STOPPED / FAILED) */
    uint32_t flags;         /* KILN_AUDIO_INFO_* */
    uint32_t scene;         /* KILN_AUDIO_SCENE_* */
    uint32_t instance;      /* the game's instance id (0 = none) */
    uint32_t thread;        /* thread id that made the game call (the main thread) */
    KilnEntity entity;
    uint32_t language_slot; /* voice-over / narration: slot played 0 en, 1 de, 2 fr, 3 es, 4 zh (0xFFFFFFFF = n/a) */
    uint64_t queued_ms, started_ms, ended_ms; /* GetTickCount64; 0 = not yet */
    KilnVec3 position;      /* last position set (AT / FOLLOW) */
} KilnSoundInfo;

/* KilnAudioStatus.flags */
enum {
    KILN_AUDIO_HAS_PLAY = 1u,       /* feature audio.play */
    KILN_AUDIO_HAS_VOICEOVER = 2u,  /* feature audio.voiceover */
    KILN_AUDIO_HAS_NARRATION = 4u,  /* feature audio.narration */
    KILN_AUDIO_SYSTEM = 8u,         /* the sound system and its scenes are captured (boot hook ran) */
    KILN_AUDIO_MANAGER = 16u,       /* the voice-over manager is running (in game) */
    KILN_AUDIO_FRAMES = 32u,        /* a main-thread drain runs (kiln.ui frames or client steps) */
    KILN_AUDIO_PROBE = 64u,         /* the SoundStart probe (counts the game's own calls per thread) is on */
};

typedef struct KilnAudioStatus {
    uint32_t size;                  /* set by the caller: sizeof(KilnAudioStatus) */
    uint32_t flags;                 /* KILN_AUDIO_* above */
    uint32_t scenes[KILN_AUDIO_SCENES];    /* the game's scene ids (0 = not captured) */
    uint32_t listeners[KILN_AUDIO_SCENES];
    uint32_t main_thread;           /* thread id of the drains */
    uint32_t voice_language;        /* keen::LanguageId of the voice-over setting (1 en-us, 3 de-de, 5 fr-fr, 7 es-es,
                                       20 zh-cn; others play English), 0 = not seen yet */
    float voice_volume;             /* the voice-over volume the manager applies */
    uint32_t voice_scene, lore_scene;
    uint32_t lore_mode;             /* the lore auto-play mode byte (0 play now, 1 queue, 2+ never) [hyp] */
    uint32_t lore_queued;           /* entries in the lore queue */
    uint32_t live;                  /* handles PENDING or PLAYING */
    uint32_t game_threads;          /* distinct threads seen calling SoundStart (the game's own calls) */
    uint64_t played, failed;        /* Kiln starts that worked / failed since boot */
    uint64_t game_starts;           /* the game's own SoundStart calls (probe) */
    uint64_t game_starts_off_main;  /* ... of them not on the main thread */
    uintptr_t system;               /* SoundSystem* (diagnostics) */
} KilnAudioStatus;

typedef struct KilnAudioApi {
    KilnServiceHeader header; /* { sizeof(KilnAudioApi), KILN_AUDIO_VERSION } */
    KilnResult(KILN_CALL* status)(KilnAudioStatus* out);
    /* A cue in a scene: 2D (no AT/FOLLOW), at a position, or following an entity. 0 = refused (invalid argument, more
     * than 4096 live handles). Everything else is reported through the handle's state. */
    KilnSound(KILN_CALL* play)(KilnOwner owner, const KilnAudioPlay* request);
    /* fade_ns 0 = the game's default (immediate); typical 20..100 ms. A PENDING handle never starts. */
    KilnResult(KILN_CALL* stop)(KilnSound sound, int64_t fade_ns);
    KilnResult(KILN_CALL* set_volume)(KilnSound sound, float volume, int64_t fade_ns);
    /* Moves the sound (and ends FOLLOW). */
    KilnResult(KILN_CALL* set_position)(KilnSound sound, const KilnVec3* position);
    uint32_t(KILN_CALL* state)(KilnSound sound);
    KilnResult(KILN_CALL* info)(KilnSound sound, KilnSoundInfo* out);
    /* Stops every sound and voice-over line `owner` started; returns how many. */
    uint32_t(KILN_CALL* stop_owner)(KilnOwner owner, int64_t fade_ns);

    /* ---- voice-over (in game; features audio.voiceover / audio.narration) ----
     * voiceover_play: a VoiceoverResource line on the game's dialogue channel (what NPC dialogue uses): the player's
     *   voice language, the voice-over volume, one line at a time (replaces the current one). `speaker` (client-world
     *   entity, 0 = none): the game keeps the line at that entity; `position` (or NULL): where to start it.
     * voiceover_stop: stops the dialogue line if Kiln started it.
     * narrate: a VoiceoverResource line through the lore queue (LoreVoiceover scene: music and game sounds duck), as
     *   lore readings do; name/description are loca tag ids the game's lore UI may show (0 = none). A line without a
     *   cue for the player's voice language is not queued (STOPPED, reason SILENT): the game would keep such an
     *   entry at the head of its queue.
     * Both return 0 when the feature is off. */
    KilnSound(KILN_CALL* voiceover_play)(KilnOwner owner, const uint8_t voiceover[16], KilnEntity speaker,
                                         const KilnVec3* position);
    KilnResult(KILN_CALL* voiceover_stop)(int64_t fade_ns);
    KilnSound(KILN_CALL* narrate)(KilnOwner owner, const uint8_t voiceover[16], uint32_t name_loca,
                                  uint32_t description_loca);

    /* Diagnostics as JSON (status, live handles, the game's SoundStart threads). out_len gets the full length. */
    KilnResult(KILN_CALL* status_json)(char* buffer, size_t capacity, size_t* out_len);
} KilnAudioApi;

#ifdef __cplusplus
}
#endif
#endif
