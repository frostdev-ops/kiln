# Screen effects

Tint the screen while a status is on a player, flash it when something hits, fade to black: a `ScreenEffectBlock` in C#, the same shape in a
`screenEffects` section of `blocks/*.json`. No shader, no compiler, no game data.

```csharp
var cold = Blocks.Define("cold", new ScreenEffectBlock
{
    Temperature = -0.6f, Saturation = 0.7f,
    Vignette = new ScreenVignette { Color = "#10243a", Amount = 0.6f },
    FadeIn = 1, FadeOut = 1,
});
Blocks.Define("frozen", new StatusBlock { Name = "Frozen", Seconds = 10, Screen = cold });
Blocks.Define("flashbang", new EffectBlock { Screen = new ScreenEffectBlock { Fill = "#ffffff", Seconds = 0.2f, FadeOut = 1.5f }, Area = /* ... */ });
ScreenEffects.Play(cold.Block);                                    // from code, on this client
```
```jsonc
{ "format": 1,
  "screenEffects": {
    "cold": { "temperature": -0.6, "saturation": 0.7, "vignette": { "color": "#10243a", "amount": 0.6 }, "fadeIn": 1, "fadeOut": 1 },
    "flash": { "fill": "#ffffff", "seconds": 0.2, "fadeOut": 1.5 } },
  "statuses": { "frozen": { "name": "Frozen", "seconds": 10, "screen": "cold" } },
  "effects": { "flashbang": { "screen": "flash" } } }
```

| Field | Means |
|---|---|
| `exposure` | -8..8 stops |
| `temperature` | -1 cold .. 1 warm |
| `contrast`, `saturation`, `gamma` | 1 changes nothing; saturation 0 is grey |
| `tint` | `"#rrggbb"`, or `{ "color": "#rrggbb", "amount": 0.5 }`: multiplies the picture |
| `vignette` | how much (`0.6`), or `{ "amount", "color", "radius", "softness" }` |
| `fill` | as `tint`: the picture goes to this colour. A fade, or a flash with `seconds: 0` and a `fadeOut` |
| `strength` | 0..1 of the whole effect |
| `fadeIn`, `seconds`, `fadeOut` | `seconds` left out: until stopped |
| `order` | several at once: lower first |
| `range` | metres: an effect at a place shows to players this near. Default 30 |

Every field is in [schemas/kiln-blocks.md](schemas/kiln-blocks.md#screeneffect).

| Field that takes one | Shows |
|---|---|
| `StatusBlock.Screen` | On the screen of the player who has it, while they have it, then by its fade out |
| `EffectBlock.Screen` | For the target, when it is a player, for its `seconds`; at a place, for the players within its `range`. It needs `seconds` |
| `ScreenEffects.Play` from code | Client code: here. Server code: every Kiln client; `PlayFor(player, …)`, `PlayNear(at, radius, …)` |

**What players need:** nothing for the screen effect itself (`clients: none`). Each play is sent with the whole effect to Kiln clients that should
see it; a vanilla client just doesn't see it. The status or spell carrying it asks what it always asks.

**It draws over the game's UI.** The effect goes on the finished frame, HUD and menus included. Kiln's own windows draw over it.

**If something goes wrong,** `kiln/config/screen.json` with `{"enabled": false}` turns screen effects off before the game creates its swapchain. A
failure turns them off by itself with one line in `kiln.log`, `screen: off, the game carries on: …`, and the game carries on.

The service: [services/screen.md](../services/screen.md).
