/* SPDX-License-Identifier: MIT
 * kiln.render v1 (client only): per-frame drawing for mods on top of the game's picture, in screen space and at world
 * positions. Registered by kiln-ui.dll next to kiln.ui (native/core/abi/services/kiln_ui.h), whose overlay frame it
 * draws into. Usage: docs/kiln/render.md. RE: docs/re/client-render.md ("The camera").
 *
 * Frames
 *  - Drawing happens inside a kiln.ui frame callback (kiln_ui.h subscribe_frame): the game's render thread, once per
 *    presented frame while the overlay draws. acquire_frames asks for a frame at every present (as a HUD line would);
 *    release gives it back (it does not unsubscribe the callback). Before unloading, unsubscribe from outside a callback,
 *    then release the frame handle. Unsubscribe waits for an in-flight frame callback; core owner_revoke does not clean
 *    up these service handles. With nothing acquired and nothing else on the overlay the presents pass through untouched.
 *  - A frame that draws nothing records no GPU work (kiln.ui skips the submit when ImGui's draw data is empty), so a
 *    per-frame callback that only computes costs CPU time only.
 *  - Everything below is drawn on ImGui's background list: under every Kiln window, screen, HUD line and toast, over
 *    the game's picture (including the game's own HUD).
 * Camera
 *  - The view the game renders with, as the client wrote it this frame (the local player's camera: position, rotation,
 *    vertical field of view, aspect, near and far planes). Read on the game's main thread by a hook where the client
 *    fills it (feature render.camera); camera() returns the latest copy on any thread. Camera shake is not included.
 *  - Spaces: world metres, x east, y up, z north (left-handed); camera space x right, y up, z forward (+z = into the
 *    screen). Pixels: origin top left, y down.
 * Threads: draw functions only inside a frame callback (elsewhere they do nothing); camera, world_to_screen, status,
 * acquire_frames, release: any thread. */
#ifndef KILN_RENDER_H
#define KILN_RENDER_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_RENDER_SERVICE "kiln.render"
#define KILN_RENDER_VERSION 1u

/* text flags */
enum {
    KILN_RENDER_TEXT_SHADOW = 1u,      /* a dark copy one pixel down-right */
    KILN_RENDER_TEXT_OUTLINE = 2u,     /* a dark outline (four copies) */
    KILN_RENDER_TEXT_CENTER_X = 4u,    /* x is the text's centre */
    KILN_RENDER_TEXT_CENTER_Y = 8u,    /* y is the text's middle */
    KILN_RENDER_TEXT_RIGHT = 16u,      /* x is the text's right edge */
};

typedef struct KilnRenderCamera {
    uint32_t size;          /* set to sizeof(KilnRenderCamera) before calling camera() */
    uint8_t valid;          /* a camera was written in the last 500 ms (may remain valid briefly after leaving play) */
    uint8_t available;      /* the camera hook runs on this build (feature render.camera) */
    uint8_t reserved[2];
    double position[3];     /* metres */
    float rotation[4];      /* quaternion x y z w: camera space -> world (rotate (0,0,1) for the view direction) */
    float fov_y;            /* vertical field of view, radians */
    float aspect;           /* width / height the game projects with */
    float near_plane, far_plane; /* metres */
    float distance;         /* the third-person camera's distance from its pivot (the character), metres */
    float reserved2;
    uint64_t frame;         /* camera writes since start (one per client frame) */
    uint32_t age_ms;        /* since the last write */
    uint32_t reserved3;
} KilnRenderCamera;

typedef struct KilnRenderStatus {
    uint32_t size;          /* set to sizeof(KilnRenderStatus) before calling */
    uint8_t camera;         /* feature render.camera hooked */
    uint8_t camera_valid;   /* see KilnRenderCamera.valid */
    uint8_t reserved[2];
    uint32_t frame_holders; /* acquire_frames handles held */
    uint64_t camera_frames; /* camera writes seen */
    uint64_t draws;         /* draw calls since start */
} KilnRenderStatus;

typedef struct KilnRenderApi {
    KilnServiceHeader header; /* { sizeof(KilnRenderApi), KILN_RENDER_VERSION } */
    KilnResult(KILN_CALL* status)(KilnRenderStatus* out);

    /* ---- frames ---- */
    KilnResult(KILN_CALL* acquire_frames)(KilnOwner owner, KilnHandle* out);
    KilnResult(KILN_CALL* release)(KilnHandle handle);

    /* ---- camera (any thread) ----
     * camera: KILN_ERR_UNAVAILABLE when feature render.camera is off (out->available = 0, the rest zero).
     * world_to_screen: pixels of a world point with the latest camera on a screen of width x height (0 = the swapchain's
     *   size). out = {x, y, depth (metres along the view)}. Returns false when the camera is not valid or the point is
     *   behind the near plane (out still holds the projection then, mirrored: do not draw it). */
    KilnResult(KILN_CALL* camera)(KilnRenderCamera* out);
    bool(KILN_CALL* world_to_screen)(const double world[3], float width, float height, float out[3]);

    /* ---- drawing (inside a kiln.ui frame callback; pixels; colours 0xRRGGBBAA) ---- */
    void(KILN_CALL* line)(float x0, float y0, float x1, float y1, uint32_t rgba, float thickness);
    void(KILN_CALL* rect)(float x0, float y0, float x1, float y1, uint32_t rgba, float rounding, float thickness, int32_t filled);
    /* segments 0 = automatic */
    void(KILN_CALL* circle)(float x, float y, float radius, uint32_t rgba, int32_t segments, float thickness, int32_t filled);
    void(KILN_CALL* triangle)(float x0, float y0, float x1, float y1, float x2, float y2, uint32_t rgba, float thickness, int32_t filled);
    /* xy = count pairs; filled draws a convex polygon */
    void(KILN_CALL* polyline)(const float* xy, uint32_t count, uint32_t rgba, float thickness, int32_t closed, int32_t filled);
    /* size 0 = the default font size; flags KILN_RENDER_TEXT_* */
    void(KILN_CALL* text)(float x, float y, uint32_t rgba, float size, uint32_t flags, KilnStr text);
    void(KILN_CALL* text_size)(KilnStr text, float size, float* width, float* height);
    /* an image from kiln.ui image_load, stretched over the rectangle; uv 0..1; tint 0xFFFFFFFF = as is */
    void(KILN_CALL* image)(KilnHandle image, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint32_t tint);
} KilnRenderApi;

#ifdef __cplusplus
}
#endif
#endif
