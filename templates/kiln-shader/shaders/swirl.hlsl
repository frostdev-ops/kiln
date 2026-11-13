// Two particle hooks of this mod's own, linked into Kiln's sprite when the game starts. Particles drift through a curl-noise field, so
// they swirl around each other without bunching up, and glow brighter the faster they go. Build: kiln-vfxcook shader build --mod .
// The block passes three numbers: "constants": [strength, scale, lift].
#include "kiln_hooks.hlsl"

void swirl_update(inout KilnSprite s, KilnContext c)
{
    float strength = c.user0.x;
    float scale = max(c.user0.y, 0.01f);
    s.velocity += kiln_curl(s.position * scale, 0.0f) * strength * c.dt;
    s.velocity.y += c.user0.z * c.dt;
    s.aux += length(s.velocity) * c.dt;   // how far it has travelled, kept from tick to tick
}

void swirl_draw(inout KilnLook l, KilnSprite s, KilnContext c)
{
    l.color *= 1.0f + 0.3f * length(s.velocity);
    l.roll = s.aux * 2.0f;                // it turns as it travels
}

#define KILN_UPDATE swirl_update
#define KILN_DRAW swirl_draw
#include "kiln_hooks_main.hlsl"
