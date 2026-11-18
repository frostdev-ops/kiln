# Kiln's graphics tools: frame cap, DLSS settings, readout (0.2.0-dev.29.18)

Kiln's own versions of what players use OptiScaler, DLSSTweaks and frame cap mods for, on Kiln's own hooks. Client
only. Everything is off until the player turns it on: a player who changes nothing gets the game as before, with no
hook on DLSS, no new hook on the game's library loads, and nothing done in a frame but two reads of a flag. Code:
`native/services/ui/graphics.cpp` (plain logic in `frame_pacer.h`, `graphics_settings.h`, `graphics_aside.h`,
`ngx_param.h`; host test `tests/graphics_test.cpp`), `managed/services/Kiln.Ui/UiGraphics.cs`. How the game uses DLSS:
[docs/re/client-upscale.md](../re/client-upscale.md).

## Where the player sets them
- In the game: the Kiln menu (Insert, or **Mods** in the game's menus), tab **Graphics**. Changes apply at once and are
  saved to `kiln/kiln.json` half a second later (the file's comments and other settings stay). Under "Now" the page says
  what the tools do: the cap, the frame rates, the DLSS in use (mode, render and output size, preset), what they step
  aside for, and when a restart is needed.
- In the launcher: Settings, card **Graphics** (the same `kiln.json` entries; it also names the tools Kiln steps aside
  for, from the game folder).
- By hand: `kiln/kiln.json`, section `graphics` (the game reads the file again every 2 seconds; a file that is not valid
  JSON keeps the settings in use, with a warning in kiln.log):

| key | values | default | applies |
|---|---|---|---|
| `fpsCap` | 0 (off) or 20..500 frames a second (1..19 count as 20) | 0 | at once |
| `dlssPreset` | `default`, `J`, `K`, `L`, `M` | `default` | when the game next sets DLSS up |
| `dlssScale` | `game` (DLSS's own), `perMode`, `custom` | `game` | when the game next sets DLSS up |
| `dlssRenderScale` | 0.33..1: with `custom`, the render width and height as a share of the output | 0.67 | " |
| `dlssQualityScale`, `dlssBalancedScale`, `dlssPerformanceScale`, `dlssUltraPerformanceScale` | 0.33..1 each: with `perMode` | DLSS's own: 0.667, 0.58, 0.5, 0.333 | " |
| `readout` | `true` / `false` | `false` | at once |

"When the game next sets DLSS up": loading a world, or changing the DLSS mode or the resolution in the game's own
settings. One more rule: Kiln hooks DLSS only when a DLSS setting or the readout is on **when the game starts**. A DLSS
setting turned on for the first time while the game runs applies from the next start (the page says so); one changed
from one non-default value to another applies at the next DLSS set-up. kiln.log names what was read
(`graphics: frame cap 60 fps, DLSS preset K, ...; DLSS is hooked when the game loads NVIDIA's DLSS core`).

## Frame cap
- Paces the frames the game renders, one per 1/fps second. Right after each of the game's presents, in Kiln's present
  hook, the game's render thread waits for the next slot before it goes back to the game: the finished frame is shown
  at once, and the game starts its next frame at the slot, as a frame limiter in the game would. So the cap adds no
  latency to a finished frame. The cost: frames are shown when they are done, so display times follow the game's render
  times (a frame that takes longer shows later), where holding each frame to its slot would show them evenly but later.
- It waits on a high-resolution waitable timer for most of the gap (a plain timer where the system has none) and spins
  on the clock for the last part; how long it spins is learnt from how late the timer wakes (0.25 to 4 ms, 2 ms at
  first). A frame that comes late by more than half an interval starts a new schedule from itself, so a hitch never
  turns into a burst of catch-up frames. Nothing is held while it waits (no lock, no overlay state).
- **What wins:** the lowest limit. With the game's own frame limit (`fpsCap` in the game's settings) or V-Sync at a
  lower rate than Kiln's cap, the game's frames come later than Kiln's schedule and Kiln never holds them: the game's
  limit decides. At a higher rate, Kiln's cap decides.
- **With V-Sync:** a cap equal to the display's refresh rate can stutter (the cap and the display's clock drift
  against each other, and now and then a frame misses its refresh). Set it a few frames a second below (57 on a 60 Hz
  display, 141 on 144 Hz, best with a variable refresh rate display), or use a divisor of the refresh rate (60 on
  120 Hz). A cap below the refresh rate that does not divide it gives uneven frame times with V-Sync on.
- It counts **rendered** frames. A later frame generation phase will add frames of its own between the game's: those
  are output frames, and the cap stays on the rendered ones (a cap of 60 with 2x generation shows 120), as frame cap
  add-ons for frame generation mods do. The readout already shows both counts (equal today).
- It lives in Kiln's present hook, so it needs Kiln's overlay hooks: with the overlay's drawing off (`KILN_UI_OVERLAY=0`,
  the overlay crash guard, Streamline, OptiScaler) the cap is off too, and the page and kiln.log say why.

## DLSS settings
For players whose game upscales with DLSS (an NVIDIA RTX card and DLSS chosen in the game's settings). On another card
the game never sets DLSS up and these settings do nothing (the page says "The game has not set DLSS up in this session").
- **Render preset.** The game's DLSS (`nvngx_dlss.dll` 310.9.1) has models for presets E, F, J, K, L and M; Kiln offers
  J and K (DLSS 4's transformer models) and L and M (DLSS 4.5's newer ones). `default` leaves the game's hint and DLSS's
  own choice. Kiln sets the documented hints `DLSS.Hint.Render.Preset.{Quality, Balanced, Performance,
  UltraPerformance}` right before DLSS is created, after the game's own (the game sets the same four, and DLAA's, before
  every creation, so "default" brings its own back at the next one); **DLAA keeps the game's preset**.
  The NVIDIA app's DLSS override, when the player set one there, wins over any app's hint, Kiln's included; Kiln cannot
  read back which preset runs, so the readout shows the one asked for. A newer `nvngx_dlss.dll` without a preset falls
  back to its default.
- **Render size.** `game`: DLSS's own size for the game's DLSS mode. `perMode`: a scale for each of Quality, Balanced,
  Performance and Ultra Performance (the defaults are DLSS's own; Ultra Quality keeps DLSS's). `custom`: one scale for
  every mode. Kiln puts its own step in front of DLSS's optimal-settings callback (the documented
  `DLSSOptimalSettingsCallback` parameter): DLSS answers, then Kiln sets `OutWidth`/`OutHeight` to the output times the
  scale, within DLSS's own range for that mode (`DLSS.Get.Dynamic.Min/Max.Render.*`). The game renders at that size; a
  size within a pixel of DLSS's own is left as DLSS's. **DLAA always renders at the output's size.** A scale outside
  DLSS's range for the mode is held to the nearest size DLSS allows: the Graphics page shows the size in effect and says
  so, and kiln.log names the range (`graphics: DLSS Quality for 2560x1440: Kiln's scale asks for 845x475, outside
  DLSS's range ...`). Where that range is narrow, the setting does little in that mode.
- How: Kiln hooks five exports of NVIDIA's NGX core (`_nvngx.dll`, the driver's; Proton has it too):
  `GetCapabilityParameters`, `CreateFeature`, `CreateFeature1`, `EvaluateFeature`, `ReleaseFeature`. The game's
  statically linked NGX SDK reaches them through `GetProcAddress`; no game code is patched and only NGX's own parameter
  object is used, through the documented parameter names. Kiln's work inside those calls runs under a fault guard: a
  fault turns Kiln's DLSS settings off for the session (logged), never the game's call. Should the game unload the
  core and load it again, Kiln hooks the new one by the same rules (`graphics: NVIDIA's DLSS core ... was loaded
  again`).

## Readout
A few lines in the top right corner of the game, drawn by Kiln's overlay:
- rendered and output frames a second and the mean frame time over the last half second;
- `DLSS Quality: 1707x960 -> 2560x1440, preset K` while the game upscales with DLSS (the render size is this frame's:
  dynamic resolution may lower it); else "Not DLSS now" (the game's FSR 3, XeSS or no upscaler: Kiln does not read the
  game's own upscalers) or, without Kiln's DLSS hooks, "read from the next game start";
- the frame cap and whether it is on.
No latency: the game sets no latency markers, so there is nothing for Kiln to read honestly. Turning the readout on makes
the overlay draw every frame (an ImGui frame and one submit per present); off, it costs nothing.

## Living next to other tools
Kiln's frame cap and DLSS settings step aside, with one kiln.log line (when one of them is on at start) and a line on
the Graphics page, when another tool does the same (the files are looked up in the game folder at start;
`graphics_aside.h`):

| found | frame cap | DLSS settings |
|---|---|---|
| OptiScaler's DLL (any name; [ui.md](ui.md#optiscaler)), with or without its overlay menu | aside | aside |
| `sl.interposer.dll` in the game folder or `Streamline/` (NVIDIA Streamline, frame generation mods) | aside | aside |
| `EnshroudedVKFG.ini` (a DLSS frame generation mod) | aside | aside |
| `dlsstweaks.ini` (DLSSTweaks) | stays | aside |
| `nvngx.dll` or `_nvngx.dll` next to the game (another DLSS loader: NGX loads it before the driver's) | stays | aside |
| NGX's exports already patched when the core loads (another tool hooks DLSS) | stays | aside |
| Kiln's overlay hooks off (`KILN_UI_OVERLAY=0`, the crash guard) | aside | aside |

The readout stays where Kiln's overlay draws. Kiln never patches over another tool's hook and never changes another
loader's files or mods. Example log line:
> graphics: frame cap 60 fps, DLSS preset K; Kiln's frame cap and DLSS settings step aside for OptiScaler (dxgi.dll)

## Wine and Proton
The same on Linux: the frame cap's timer and clock are Wine's (the learnt spin margin covers a timer that wakes late),
and DLSS through Proton's NVAPI loads the same `_nvngx.dll`, which Kiln hooks the same way. With nothing turned on,
nothing runs.

## For native plugins (kiln_ui.h)
Two slots after `set_texts` (byte offsets 1016 and 1024; the table is 1032 bytes; check `header.size` and the pointer):
- `graphics_status(&KilnUiGraphicsStatus)`: what the tools do now, for anyone: `flags` (`KILN_UI_GFX_CAP_ACTIVE`,
  `_DLSS_HOOKED`, `_DLSS_ACTIVE`, `_DLSS_RESTART`, `_DLSS_PRESET`, `_DLSS_SCALED`, `_COUNTING`, `_DLSS_LIMITED`), `aside_cap` /
  `aside_dlss` (`KILN_UI_GFX_ASIDE_*`), the cap, rendered and output frames a second and the frame time (while the cap or
  the readout counts), the swapchain's size, and the DLSS in use: its mode (`NVSDK_NGX_PerfQuality_Value`, -1 none), the
  preset asked for, this frame's render size and the output size. Set `size` first.
- `graphics_set(&KilnUiGraphics)`: Kiln.Ui's only (the player's settings); a native plugin's call is refused with
  `KILN_ERR_UNAVAILABLE`.
There is no managed (mod API) member for it in this release.

## Not in phase A
FSR 3's ratio and RCAS sharpness (compiled into the game: game-code hooks per build, [client-upscale.md](../re/client-upscale.md#fsr-3)),
an upscaler swap (phase B) and frame generation (phase C).
