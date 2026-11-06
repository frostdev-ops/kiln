# kiln.screen / ScreenEffects: full-screen effects

Colour grading, a tint, a vignette, fades and flashes, drawn over each frame the game presents. Forge: post chains (`PostChain`). Blocks:
[modding/screen-effects.md](../modding/screen-effects.md).

| Layer | Where |
|---|---|
| C table | `native/core/abi/services/kiln_screen.h` (`kiln.screen` v1) |
| Native | `native/services/screen/` → `kiln/services/kiln-screen.dll`. Client only |
| Shaders | `native/services/screen/shaders/screen.hlsl`, compiled to `shaders/build/*.spv` by `shaders/build.sh` (local `glslc`); the `.spv` are committed and embedded, so building Kiln needs no shader compiler |
| Hook | kiln.ui's pre-overlay callback (`kiln_ui.h`: `subscribe_pre_overlay`, `set_pre_overlay_demand`, `add_swapchain_usage`) |
| Managed | `managed/services/Kiln.Vfx/` (`ScreenEffects`, `ScreenEffect`, `ScreenColor`, `ScreenVignette`, `ScreenHandle`); the block is `ScreenEffectBlock` in Kiln.Blocks |
| Network | Kiln channel `kiln:screen` v1, `required: false` |
| Tests | `native/services/screen/tests/run.sh` (the effect list), `tests/test_gpu.py` (the pass on a GPU), `mods/tests/screen-test` (blocks, programs, relay) |

## C#
```csharp
var h = ScreenEffects.Play(new ScreenEffect { Fill = "#000000", FadeIn = 1.5f });                 // fade to black, until stopped
ScreenEffects.Stop(h);                                                                            // fades out by its FadeOut; Stop(h, immediate: true)
ScreenEffects.Play(new ScreenEffect { Fill = "#ffffff", Seconds = 0, FadeOut = 0.4f });          // a flash
var cold = ScreenEffects.Play(new ScreenEffect { Temperature = -0.6f, Saturation = 0.7f, Vignette = 0.5f });
ScreenEffects.SetStrength(cold, 0.3f);                                                            // follow a value while it plays
ScreenEffects.PlayFor(player, effect);                                                            // server: that player's client
ScreenEffects.PlayNear(position, 20, effect);                                                     // server: players within 20 m
```
| Field | Range, default | What |
|---|---|---|
| `Exposure` | -8..8 stops, 0 | 1 is twice as bright |
| `Temperature` | -1..1, 0 | cold to warm, brightness kept |
| `Contrast` | 0.05..4, 1 | around mid grey |
| `Saturation` | 0..4, 1 | 0 is grey |
| `Gamma` | 0.1..5, 1 | above 1 lifts the mid tones |
| `Tint` | `"#rrggbb"` or `new ScreenColor(c, amount)` | multiplies the picture |
| `Vignette` | amount, or `{ Amount, Color, Radius, Softness }` | darker or coloured edges; radius 0 centre .. 1 corners |
| `Fill` | as `Tint` | the whole picture goes to this colour: fades, flashes |
| `Strength` | 0..1, 1 | how much of the whole effect shows |
| `FadeIn`, `Seconds`, `FadeOut` | 0..60, 0..3600 or left out, 0..60 | `Seconds` left out: until stopped |
| `Order` | int, 0 | several playing: lower first, then the older first |

The picture is decoded to linear light (Rec.709), graded in that order, and encoded back, whatever the swapchain holds: 8-bit sRGB, 10-bit HDR10
(PQ, white at 203 cd/m²) or scRGB. A value out of range throws `ArgumentException` naming the field. Up to 8 effects play at once; one more is
refused (`ScreenHandle.None`, one log line).

- **Client:** any thread. Effects of a mod stop when it unloads.
- **Server** (a dedicated server, or a hosting client's server-world tick thread): the effect goes over `kiln:screen` to Kiln clients, the whole effect in
  each message, so a client needs Kiln and not the mod. A hosting client plays its own player's. A joiner doesn't see what started before; a client stops
  what a server started when it leaves that server. Vanilla clients see nothing.
- **Blocks:** a status's screen effect plays on its player while it is applied, and an effect's on its target (or the players near the place): the
  program calls `kiln.Signal`, and the server relays. See [modding/screen-effects.md](../modding/screen-effects.md).

## How it draws
At each present of the game window, while an effect plays: copy the swapchain image, run one full-screen triangle over the copy with every playing
effect in one loop, write the result back into the image, and chain the work into the present by a semaphore. kiln.ui's overlay draws after it.
This is after the game's UI is in the image, so **it covers the game's UI** (HUD, menus). Passes inside the game's frame, under its UI, are later
work under the same API.

## Safety
- A present never waits for this. The pipeline, the copy and the rest are built on a thread of their own; until they exist the frames are the game's.
  A frame whose image the GPU is still using from Kiln's last pass is left alone after at most 2 ms.
- Any failure (a pipeline or memory that can't be made, a lost device, a submit that fails, 600 frames in a row with the GPU not done, a swapchain format
  it can't read) turns it off with one line, `screen: off, the game carries on: <why>`, and the frame goes on as the game asked. Nothing retries.
- A swapchain that changes (resize, minimise, SDR to HDR) or goes: the pass stands down, and builds again for the new one when something plays.
- **Off switch:** `kiln/config/screen.json` `{"enabled": false}`. The service is then not registered, and the game's swapchain is created as the game
  asks. `KILN_UI_DISABLE=1` turns it off with the overlay.

## Cost
- **Nothing playing:** no call at all. kiln.ui's present reads one counter. Three seconds after the last effect ends, the copy of the frame is freed;
  the pipeline stays.
- **Playing:** one image copy and one full-screen draw per present, about 0.2 ms of CPU per frame, and the GPU time below (local harness, headless).
- The swapchain is created with `TRANSFER_SRC` usage (as far as the surface allows it) when kiln.screen is on, from the first swapchain.

| GPU | 1920x1080 | 2560x1440 | 3840x2160 |
|---|---|---|---|
| NVIDIA RTX 3080 Ti | 0.06 ms | 0.10 ms | 0.20 ms |
| AMD Raphael iGPU (RADV) | 1.9 ms | 3.1 ms | 6.8 ms |

## C (native plugins)
```c
const KilnScreenApi* s = api->service((KilnStr){KILN_SCREEN_SERVICE, 11}, KILN_SCREEN_VERSION);
KilnScreenEffect e = kiln_screen_effect_defaults();
e.fill[0] = e.fill[1] = e.fill[2] = 1.0f; e.fill[3] = 0.8f; e.seconds = 0; e.fade_out = 0.5f;
uint32_t h = s->play(owner, &e);
```
`status`, `play`, `stop`, `set_strength`, `is_playing`, `stop_owner` (call it when an owner goes away). `kiln_screen.h` has the ranges.

kiln.ui's pre-overlay callback is open to other native services: `subscribe_pre_overlay(owner, fn, gone, user, &handle)`. `fn` gets the frame
(`KilnUiPreOverlay`: device, queue, swapchain, image, the game's semaphores, format, colour space, usage) and either submits its own work, waiting on
those semaphores and signalling its own, or returns false having touched nothing.

## Tests
```sh
native/services/screen/tests/run.sh                        # the effect list: ranges, fades, order, limits, threads (ASan, UBSan, TSan)
native/services/screen/tests/build.sh
python3 native/services/screen/tests/test_gpu.py --device NVIDIA --validate
python3 native/services/screen/tests/test_gpu.py --device RADV --validate
```
`test_gpu.py` runs the same `pass.cpp` and `core.cpp` as the DLL, headless, under the Khronos validation layer with synchronization validation: every
effect and combination against a reference written in Python on five swapchain formats, the fades reaching the pixels, a real swapchain on
`VK_EXT_headless_surface` where the driver offers one (with a resize), failures made by a wrapped `vkGetDeviceProcAddr` (each turns it off and every later
frame is the game's, byte for byte), and the cost. `mods/tests/screen-test` on a dedicated server checks the blocks and the relay; with
`fixtures/screen_client.py` connected, `/screentest relay` checks every byte the relay sends.

### Manual test
`mods/tests/screen-test` on a client; lines `screen-test:` and `screen:` in `kiln.log`.

| Key | You should see | `kiln.log` |
|---|---|---|
| Ctrl+8 | A white flash with darker corners, gone in half a second. The game's picture exactly as before after it | `screen: #N plays`, the first time `screen: building the pass for swapchain …` and `screen: first frame drawn over the game's picture` |
| Ctrl+Shift+8 | The picture turns cold and paler with blue-dark corners over a second; again: it fades back over a second | `INFO cold: #N`, `INFO cold: stopping` |
| Ctrl+Alt+8 | Fade to black over 2 s, black for 1 s, back over 2 s. The HUD goes black too | `INFO fade: #N` |
| Ctrl+Alt+9 | Slightly brighter, tinted; again: it stops at once | `PASS eight play at once`, `PASS a ninth is refused` |
| Ctrl+Alt+0, hosting | Cold for 3 s, with a flash as it starts | `INFO frozen: applied to you for 3 s` |
