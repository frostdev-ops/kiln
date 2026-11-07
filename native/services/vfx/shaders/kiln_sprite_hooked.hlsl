// SPDX-License-Identifier: MIT
// Kiln's sprite with link points: kiln_particle_main.hlsl with three empty hook functions that stay separate functions in the build
// (build.sh marks them DontInline). The cook (cook/spvlink.cpp) replaces their bodies with calls into a mod's verified hook module.
// Shipped as build/kiln_sprite_hooked.spv; never served on its own.
#include "kiln_particle.hlsl"

void kiln_hook_spawn(inout KilnSprite s, KilnContext c) {}
void kiln_hook_update(inout KilnSprite s, KilnContext c) {}
void kiln_hook_draw(inout KilnLook l, KilnSprite s, KilnContext c) {}

#define KILN_SPAWN kiln_hook_spawn
#define KILN_UPDATE kiln_hook_update
#define KILN_DRAW kiln_hook_draw
#include "kiln_particle_main.hlsl"
