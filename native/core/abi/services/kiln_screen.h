/* SPDX-License-Identifier: MIT
 * kiln.screen v1: full-screen effects at present: colour grading, tint, vignette, fades and flashes (docs/kiln/services/screen.md). Client
 * only: a dedicated server has no picture, and the service is absent there. Kiln.Vfx's ScreenEffects relays server calls to Kiln clients.
 *
 * How: kiln.ui's pre-overlay callback (kiln_ui.h). While something plays, each present copies the frame the game is about to show, runs one
 * full-screen pass over it with Kiln's own shaders, and chains its work into the present through a semaphore. While nothing plays the present
 * passes through as if the service were not there. The pass runs after the game's UI is in the image, so it also draws over the game's UI.
 *
 * Threads: every function may be called from any thread.
 * Handles: Kiln's own (1..2^32-1, not reused in a process). A finished handle just reads as not playing.
 * Fail soft: a failed pipeline, a lost device or a swapchain that can't be read turns the service off with one log line, and presents go on
 *   untouched. Then play returns 0. kiln/config/screen.json {"enabled": false} keeps it off from the start: the service is not registered
 *   and the game's swapchain stays as the game asked for it.
 * Owners: the service cannot see owner_revoke: call stop_owner when an owner goes away (Kiln.Vfx does for mods). */
#ifndef KILN_SCREEN_H
#define KILN_SCREEN_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_SCREEN_SERVICE "kiln.screen"
#define KILN_SCREEN_VERSION 1u

/* How many effects can play at once. A play beyond it is refused (0, one log line). */
#define KILN_SCREEN_MAX_EFFECTS 8u

/* Values outside the ranges below are clamped, and a NaN reads as the field's "no change". KilnScreenEffect zeroed is not "no change":
 * start from kiln_screen_effect_defaults. Colours are sRGB, as in #rrggbb, 0..1. */
typedef struct KilnScreenEffect {
    uint32_t size;          /* sizeof(KilnScreenEffect) */
    int32_t order;          /* lower runs first; among equals, the older first */
    float strength;         /* 0..1: how much of the whole effect shows. 1 */
    float fade_in;          /* seconds to reach full, 0..60. 0 */
    float seconds;          /* seconds at full, 0..3600; less than 0: until stopped. -1 */
    float fade_out;         /* seconds to fade once it ends or is stopped, 0..60. 0 */
    float exposure;         /* stops, -8..8. 0 */
    float contrast;         /* around mid grey, 0.05..4. 1 */
    float saturation;       /* 0 is grey, 0..4. 1 */
    float gamma;            /* above 1 lifts the mid tones, 0.1..5. 1 */
    float temperature;      /* -1 cold .. 1 warm, keeping brightness. 0 */
    float tint[4];          /* rgb multiplies the picture, a how much (0..1). a = 0 */
    float vignette[4];      /* rgb the colour at the edges, a how much (0..1). a = 0 */
    float vignette_radius;  /* where it starts: 0 the centre, 1 the corners. 0.5 */
    float vignette_softness;/* how far past that it takes to reach full, 0.001..2. 0.5 */
    float fill[4];          /* rgb the whole picture goes to this colour, a how much (0..1): a fade or a flash. a = 0 */
} KilnScreenEffect;

typedef struct KilnScreenStatus {
    uint32_t size;          /* set to sizeof(KilnScreenStatus) before calling */
    uint8_t enabled;        /* not switched off, by the config or by a failure */
    uint8_t ready;          /* the pipeline exists: the next frame with an effect draws it */
    uint8_t transfer;       /* how the swapchain's values are read: 0 linear, 1 sRGB, 2 PQ */
    uint8_t reserved;
    uint32_t playing;       /* effects playing */
    uint32_t format;        /* VkFormat of the last swapchain drawn to */
    uint32_t width, height;
    uint64_t plays;         /* plays since start */
    uint64_t refused;
    uint64_t frames;        /* presents the pass drew into */
    uint64_t skipped;       /* frames left alone because the last use of that image was not done */
    double pipeline_ms;     /* how long the pipeline took to build */
    char reason[128];       /* why it is off, "" = it is not */
} KilnScreenStatus;

typedef struct KilnScreenApi {
    KilnServiceHeader header; /* { sizeof(KilnScreenApi), KILN_SCREEN_VERSION } */
    KilnResult(KILN_CALL* status)(KilnScreenStatus* out);
    /* 0: refused (off, or KILN_SCREEN_MAX_EFFECTS already playing). */
    uint32_t(KILN_CALL* play)(KilnOwner owner, const KilnScreenEffect* effect);
    /* immediate: gone from the next frame. Otherwise it fades by its fade_out from where it is. KILN_ERR_NOT_FOUND: it had already ended. */
    KilnResult(KILN_CALL* stop)(uint32_t handle, int32_t immediate);
    /* Changes `strength` while it plays: for what follows a value (health, depth). */
    KilnResult(KILN_CALL* set_strength)(uint32_t handle, float strength);
    /* 1 while it plays (fading out counts), else 0. */
    int32_t(KILN_CALL* is_playing)(uint32_t handle);
    /* Stops all of an owner's effects, fading unless immediate. Returns how many. */
    uint32_t(KILN_CALL* stop_owner)(KilnOwner owner, int32_t immediate);
} KilnScreenApi;

/* What "no change" is for every field. */
static inline KilnScreenEffect kiln_screen_effect_defaults(void) {
    KilnScreenEffect e;
    e.size = sizeof(KilnScreenEffect);
    e.order = 0;
    e.strength = 1.0f;
    e.fade_in = 0.0f;
    e.seconds = -1.0f;
    e.fade_out = 0.0f;
    e.exposure = 0.0f;
    e.contrast = 1.0f;
    e.saturation = 1.0f;
    e.gamma = 1.0f;
    e.temperature = 0.0f;
    e.tint[0] = e.tint[1] = e.tint[2] = 1.0f;
    e.tint[3] = 0.0f;
    e.vignette[0] = e.vignette[1] = e.vignette[2] = 0.0f;
    e.vignette[3] = 0.0f;
    e.vignette_radius = 0.5f;
    e.vignette_softness = 0.5f;
    e.fill[0] = e.fill[1] = e.fill[2] = 0.0f;
    e.fill[3] = 0.0f;
    return e;
}

#ifdef __cplusplus
}
#endif
#endif
