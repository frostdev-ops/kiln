// SPDX-License-Identifier: MIT
// The particle kit for mods, part 2: the hook module's entry. It names the hooks the mod defined (KILN_SPAWN, KILN_UPDATE, KILN_DRAW) as
// kiln_hook_spawn, kiln_hook_update and kiln_hook_draw, the functions Kiln's cook links into its own sprite module. The entry itself is
// a stub: it never runs in the game (the cook drops it), it only keeps the compiler from dropping the hooks.
// Build it with kiln-vfxcook shader build (glslangValidator -Od, which keeps functions apart). Guide: docs/kiln/modding/shaders.md.
#include "kiln_hooks.hlsl"

#ifdef KILN_SPAWN
void kiln_hook_spawn(inout KilnSprite s, KilnContext c) { KILN_SPAWN(s, c); }
#endif
#ifdef KILN_UPDATE
void kiln_hook_update(inout KilnSprite s, KilnContext c) { KILN_UPDATE(s, c); }
#endif
#ifdef KILN_DRAW
void kiln_hook_draw(inout KilnLook l, KilnSprite s, KilnContext c) { KILN_DRAW(l, s, c); }
#endif

[numthreads(32, 1, 1)]
void cs_main()
{
    KilnSprite s = (KilnSprite)0;
    KilnContext c = (KilnContext)0;
    KilnLook l = (KilnLook)0;
#ifdef KILN_SPAWN
    kiln_hook_spawn(s, c);
#endif
#ifdef KILN_UPDATE
    kiln_hook_update(s, c);
#endif
#ifdef KILN_DRAW
    kiln_hook_draw(l, s, c);
#endif
}
