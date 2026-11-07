// SPDX-License-Identifier: MIT
// Parameter layout of Kiln's starter particle modules. Plain #defines: HLSL and the C++ builder both include this file.
//
// A VfxResource particle entry's evaluateParametersGpuResourceDataRange points at one of these blocks inside the VFX's
// gpuResourceData. All offsets are bytes, all fields 32-bit little-endian, so a block needs 4-byte alignment.
// Evidence and the rules around them: docs/re/vfx.md §3.5.
#ifndef KILN_VFX_PARAMS_H
#define KILN_VFX_PARAMS_H

// Bumped when a block below changes shape. Written to the first word of every block. A mod's own module (ShaderBlock) is built
// against one ABI and refused under another (docs/kiln/modding/shaders.md).
// 2 (2026-11-07): spawn shapes, velocity stretch, user constants, per-play parameters (KX_*).
#define KILN_VFX_ABI 2
// The layout of KilnSprite, KilnContext and KilnLook (kiln_hooks.hlsl) that a mod's hook module is built against. Stamped into the module
// as "kiln-vfx-hooks-<n>" by kiln-vfxcook shader build; another one is refused and falls back to Kiln's sprite.
#define KILN_HOOKS_ABI 1

// ---- kiln_emitter: the root entry (depth 0). Spawns into its child entry. ---------------------------------------------
#define KE_VERSION          0   // u32 KILN_VFX_ABI
#define KE_FLAGS            4   // u32 KE_FLAG_*
#define KE_DURATION         8   // f32 seconds of emission. 0 = until the instance stops
#define KE_DELAY           12   // f32 seconds before the first spawn
#define KE_RATE            16   // f32 particles per second
#define KE_BURST           20   // u32 particles at the start (and at each loop)
#define KE_CHILD_STATE     24   // u32 stateSizeInBytes of the child entry: 4-aligned, 20..320
#define KE_RESERVED        28   // u32 0
#define KE_BLOCK_SIZE      32

#define KE_FLAG_LOOP        1   // restart after KE_DURATION (needs KE_DURATION > 0)

// State of a kiln_emitter entry (stateSizeInBytes = KE_STATE_SIZE, after the game's 20-byte VfxBaseParticleState).
#define KE_S_AGE           20   // f32
#define KE_S_ACCUM         24   // f32 fractional spawns carried over
#define KE_S_PENDING       28   // u32 spawns owed (bursts larger than one tick's budget)
#define KE_STATE_SIZE      32

// ---- kiln_sprite: the child entry (depth 1). Draws a camera-facing quad or an oriented mesh per particle. -------------
#define KS_VERSION          0   // u32 KILN_VFX_ABI
#define KS_DRAW_FLAGS       4   // u32 written to TransparentInstanceData.flags: KS_DRAW_*
#define KS_FLAGS            8   // u32 KS_FLAG_*
#define KS_IMAGE           12   // patch point (images, 8 bytes): u32 bindless texture, then u32 sampler. 0 = white
#define KS_SAMPLER         16
#define KS_MESH            20   // patch point (meshes, 4 bytes): u32 mesh index. 0 = draws nothing
#define KS_RESERVED0       24   // u32 0
#define KS_LIFE_MIN        28   // f32 seconds
#define KS_LIFE_MAX        32   // f32
#define KS_SPEED_MIN       36   // f32 m/s
#define KS_SPEED_MAX       40   // f32
#define KS_SPREAD_KIND     44   // u32 KS_SPREAD_*
#define KS_SPREAD_ANGLE    48   // f32 cone half angle, radians
#define KS_SPAWN_RADIUS    52   // f32 metres: ABI 1's sphere. The builder writes KS_SHAPE now and keeps this equal to KS_SHAPE_A for a sphere
#define KS_ACCEL_X         56   // f32 m/s² in world space (gravity is y)
#define KS_ACCEL_Y         60   // f32
#define KS_ACCEL_Z         64   // f32
#define KS_DRAG            68   // f32 per second: velocity *= exp(-drag dt)
#define KS_SPIN_MIN        72   // f32 radians per second
#define KS_SPIN_MAX        76   // f32
#define KS_ROLL_MIN        80   // f32 radians at spawn
#define KS_ROLL_MAX        84   // f32
#define KS_DEPTH_FADE      88   // f32 metres of soft fade against the scene. 0 = off
#define KS_FLIPBOOK        92   // u32 columns | rows << 16. 0 = no flipbook
#define KS_FLIPBOOK_FPS    96   // f32 frames per second. 0 = play once over the particle's life
#define KS_ROUGHNESS      100   // f32 perceptual roughness, shaded sprites only
#define KS_EMISSIVE       104   // f32 0..1: how much the colour follows the camera's exposure correction
#define KS_RESERVED1      108   // u32 0
#define KS_SIZE_CURVE     112   // f32[8] metres, evenly spaced over the particle's life
#define KS_ALPHA_CURVE    144   // f32[8] 0..1
#define KS_COLOR_CURVE    176   // f32[3][8] linear RGB (may exceed 1), stride 12
#define KS_SHAPE          272   // u32 KS_SHAPE_*: where a particle starts, in the instance's space (scaled by its scale)
#define KS_SHAPE_A        276   // f32 metres: radius (sphere, hemisphere, disc), half width (box), length (line)
#define KS_SHAPE_B        280   // f32 metres: inner radius (sphere, hemisphere, disc; = A for a shell or ring), half height (box)
#define KS_SHAPE_C        284   // f32 metres: half depth (box)
#define KS_STRETCH        288   // f32 extra length in metres per m/s along the velocity (KS_FLAG_ALIGN_VELOCITY)
#define KS_RESERVED2      292   // u32 0
#define KS_USER           296   // f32[8] constants for a mod's own module (ShaderBlock "constants"). Kiln's sprite doesn't read them
#define KS_BLOCK_SIZE     328
#define KS_CURVE_SAMPLES    8
#define KS_USER_COUNT       8

#define KS_FLAG_FACE_CAMERA    1   // rotation = camera * roll. Otherwise instance orientation * roll
#define KS_FLAG_ALIGN_VELOCITY 2   // a camera-facing quad turns so its +y follows the velocity on screen, and stretches by KS_STRETCH

#define KS_SHAPE_POINT      0   // at the instance's position
#define KS_SHAPE_SPHERE     1   // inside a sphere, outside the inner radius
#define KS_SHAPE_HEMISPHERE 2   // the same, upper half (+y)
#define KS_SHAPE_BOX        3   // inside a box
#define KS_SHAPE_DISC       4   // on a flat disc in the instance's xz plane, outside the inner radius
#define KS_SHAPE_LINE       5   // on a line along the instance's x, centred
#define KS_SHAPE_LAST       5

#define KS_SPREAD_UP        0   // along the instance's +y
#define KS_SPREAD_CONE      1   // around the instance's +y, up to KS_SPREAD_ANGLE
#define KS_SPREAD_SPHERE    2   // any direction
#define KS_SPREAD_OUTWARD   3   // away from the shape's centre (in the disc's plane for a disc, across the line for a line); up at the centre

// TransparentInstanceData.flags: the bits the game's Transparent_Forward* fragment shaders test (docs/re/vfx.md §3.3).
#define KS_DRAW_ADDITIVE    1   // output alpha 0: colour adds to the scene
#define KS_DRAW_LIT         2   // lit by ambient and sun through the fog volume
#define KS_DRAW_CLIP        4   // alphaClipValue / alphaClipHalfRange erode
#define KS_DRAW_CLIP_VERTEX 8   // the clip reads vertex alpha
#define KS_DRAW_SHADED    128   // full shading with normals

// State of a kiln_sprite entry (stateSizeInBytes = KS_STATE_SIZE).
#define KS_S_AGE           20   // f32
#define KS_S_LIFE          24   // f32
#define KS_S_POS           28   // f32[3] world
#define KS_S_VEL           40   // f32[3]
#define KS_S_ROLL          52   // f32
#define KS_S_SPIN          56   // f32
#define KS_S_AUX           60   // f32 a mod module's own (KilnSprite.aux); Kiln's sprite keeps it 0
#define KS_STATE_SIZE      64

// ---- kiln_light: a root entry with no child. One point light. ---------------------------------------------------------
#define KL_VERSION          0   // u32 KILN_VFX_ABI
#define KL_FLAGS            4   // u32 written to PointLightData.flags. 0 today (bit 0 is unnamed in the game)
#define KL_LIFETIME         8   // f32 seconds. 0 = until the instance stops
#define KL_FADE_IN         12   // f32 seconds
#define KL_FADE_OUT        16   // f32 seconds, at the end of the lifetime or after the instance stops
#define KL_RADIUS          20   // f32 metres
#define KL_BRIGHTNESS      24   // f32
#define KL_COLOR           28   // f32[3] linear RGB
#define KL_FLICKER         40   // f32 0..1
#define KL_OFFSET          44   // f32[3] metres, in the instance's space
#define KL_BLOCK_SIZE      56

// State of a kiln_light entry (stateSizeInBytes = KL_STATE_SIZE).
#define KL_S_AGE           20   // f32
#define KL_S_STOP_AGE      24   // f32 age when the instance stopped, or a negative value
#define KL_S_RESERVED      28
#define KL_STATE_SIZE      32

// ---- Per-play parameters (VfxCombinedEffectParameters.user, 64 bytes). Every Kiln VFX describes the same ones, so a play, a template,
// a sequence or a buff can set them by name (Vfx.Set, VfxParams, VfxRef.Parameters). They multiply what the block says. ---------------
#define KX_BASE            64   // offset of the user block in VfxCombinedEffectParameters (layout 0x12e19673)
#define KX_COLOR            0   // u32 RGBA8, sRGB (DsTypeId VfxEffectParameterColor): tints particles and light; alpha multiplies alpha
#define KX_SIZE             4   // f32: particle size
#define KX_SPEED            8   // f32: particle start speed
#define KX_BRIGHTNESS      12   // f32: particle colour and light brightness
#define KX_COUNT           16   // f32: burst and rate
#define KX_MAGIC           60   // u32 KX_MAGIC_VALUE in Kiln's defaults. A block without it (a start that passed none) reads as all defaults
#define KX_MAGIC_VALUE  0x4e4c494bu  // "KILN"
#define KX_MAX_FACTOR      16.0 // a factor is clamped to 0..this; not finite reads as 1

// ---- Limits the modules enforce, whatever the data says: a shared GPU buffer can't afford a bad effect. -----------------
#define KILN_VFX_MAX_SPAWNS_PER_TICK 64    // a tick's spawn loop runs at most this many times
#define KILN_VFX_MAX_ACCUM          256.0  // spawns owed by rate never grow past this
#define KILN_VFX_MAX_SCALE           64.0  // metres: larger particles are clamped
#define KILN_VFX_MAX_WORLD        16000.0  // metres: the position fixed point (1/131072 m) overflows near 16,384
#define KILN_VFX_MAX_CHILD_STATE    320    // the largest stateSizeInBytes vanilla uses

// What the builder must keep within. The modules cannot check these: the game reads them on the CPU.
#define KILN_VFX_MAX_PARTICLES     4096    // VfxParticleResource.maxCount of a kiln_sprite entry (vanilla's largest is 20,000)

#endif
