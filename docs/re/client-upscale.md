# The client's upscalers: DLSS (NGX), FSR 3 and what Kiln hooks

Static reading of `enshrouded.exe` (client build of 2026-11-04, sha256 `5d9ef2f1…`, image base `0x140000000`; RVAs
below) and of the `nvngx_dlss.dll` it ships (310.9.1.0, sha256 `3975567b…`, image base `0x180000000`), with Python, pefile and capstone
(scripts in the wave's out folder, not in the tree). No game was started for this. Used by
`native/services/ui/graphics.cpp` ([graphics.md](../kiln/graphics.md)).

## What the game ships
- **DLSS super resolution** through NVIDIA's NGX SDK on Vulkan, **linked statically** (the SDK's source paths are in
  the exe: `NGX/core/rel_310_9/source/lib/nvsdk_ngx_vulkan_lib.cpp`). `nvngx_dlss.dll` 310.9.1 in the game folder is
  the DLSS model ("snippet"); NGX's core (`_nvngx.dll`) is NVIDIA's driver component (Proton ships it with its NVAPI).
- A compiled-in **FSR 3 upscaler** (`Fsr3Upscaler`, `Fsr3UpscalerRcasConstants`, `rcasConfig`), and XeSS as a menu
  choice (`antialiasingXess`; no libxess DLL ships).
- Settings in the machine settings (kiln.log of the game: `[g38_options] machine settings`): `render.antiAliasing`
  (`Dlss`, ...), `render.dlss` / `fsr3` / `xess` (the quality mode), `renderScale`, `automaticRenderScale`, `vsync`,
  `tripleBuffering`, `fpsCap` (the game's own frame limit; 0 off), `nvidiaReflex`, `amdAntiLag`,
  `renderPreferences.sharpness`. The types `keen::pc::RenderDlssPresetSetting` / `RenderFsr3PresetSetting` exist.
- **No frame generation** of any kind.
- **No Reflex and no Anti-Lag code** in the PC client: no `nvapi64.dll` or `nvapi_QueryInterface` string, none of NVAPI's
  interface ids as immediates, no `VK_NV_low_latency2` and no `VK_AMD_anti_lag`. `nvidiaReflex` and `amdAntiLag` exist
  only as option and UI data (shared with the console builds). Its one latency control is `vkWaitForPresent2KHR`
  (at most one present in flight, with V-Sync on).

## How the game reaches NGX
- The static SDK loads the core with `LoadLibraryExW` (import call at `0x12f8375`), trying `\_nvngx.dll` then
  `\nvngx.dll` (wide strings at `0x1e5adb0` / `0x1e5adc8`, list built at `0x12f50eb`) next to the game first ("NGXCore
  not found next to the application"), then in the folder from the registry (`NGXCore\NGXPath`) or the display
  driver's query (`NGXGetPathUsingQAI`). A core next to the game is taken first, but only when NVIDIA signed it: the
  library checks the file's signature there (`0x12f8260` with verify on, WinVerifyTrust at `0x12f8f00`, a signer name
  starting "NVIDIA " at `0x12f4400`). The driver's and the registry's folders are loaded without that check. So an
  unsigned `nvngx.dll` in the game folder is refused unless the system's signature override for NGX is set, which
  is what DLSS replacements ask their users to do.
- It takes the core's functions with `GetProcAddress` (wrapper `0x12f4b30`, the table filled at `0x12f0708..0x12f084c`:
  `NVSDK_NGX_VULKAN_CreateFeature` at `0x12f074f` -> global `0x2846c38`, `CreateFeature1` at `0x12f0769`,
  `EvaluateFeature` `0x12f0783`, `ReleaseFeature` `0x12f079d`, `AllocateParameters` `0x12f0805`,
  `GetCapabilityParameters` `0x12f081f`, ...). Every NGX call of the game goes through these pointers: an inline hook on
  the core's export sees all of them, wherever the game got the pointer. This is what Kiln hooks; no game code.
- **The parameter object** (`NVSDK_NGX_Parameter`) lives in the core. The SDK's C helpers in the exe call its virtual
  functions; their table offsets give the layout (MSVC puts overloads in reverse order of declaration):
  `SetVoidPointer` +0x00 (helper `0x12efc70`), `SetI` +0x18 (`0x12efb50`), `SetUI` +0x20 (`0x12efbb0`), `SetF` +0x30
  (`0x12efb00`), `SetULL` +0x38 (`0x12efc10`), `GetVoidPointer` +0x40 (`0x12ef990`), `GetI` +0x58, `GetUI` +0x60
  (`0x12ef8d0`), `GetD` +0x68, `GetF` +0x70 (`0x12ef810`), `GetULL` +0x78 (found by listing every `mov rbx,[rax+X]`
  in `0x12ee000..0x12f3000`). Kiln calls the same slots (`native/services/ui/ngx_param.h`).

## DLSS set-up, step by step
1. **Init** (graphics system start, about 0.4 s into the game): `NVSDK_NGX_VULKAN_Init*`, then
   `GetCapabilityParameters(&gfx+0x5d0)` (`0xfd930d` in `0xfd5670`, the only call of the SDK's wrapper `0x12f0e90` in
   game code; the `GetParameters` / `AllocateParameters` wrappers `0x12f10b0` / `0x12f0c10` are never called from game
   code; log "NVSDK_NGX_GetCapabilityParameters failed" at `0xfd9339`).
   The capability parameters hold DLSS's callbacks `DLSSOptimalSettingsCallback` and `DLSSGetStatsCallback`.
2. **Optimal settings** (function `0xfefb80`, each time the render size is set up): `GetVoidPointer(caps,
   "DLSSOptimalSettingsCallback")`, then `SetUI Width`, `SetUI Height` (the output), `SetI PerfQualityValue`, `SetI
   RTXValue 0`, calls the callback with the capability parameters, and reads `OutWidth`, `OutHeight` (the render size
   the game then uses: "[render] upscaled size: (1920, 1080) render size:(960, 540)"), `DLSS.Get.Dynamic.Max/Min.Render.
   Width/Height`, and `Sharpness`. So whatever the callback leaves in `OutWidth/OutHeight` is the game's render size.
   The callback is read from the parameter object at every query, so a pointer set there once holds for the session;
   but it has to be set before the query that sets DLSS up, which is why Kiln hooks DLSS only when a setting asks for
   it at the game's start.
   **Kiln:** in its `GetCapabilityParameters` hook it reads the callback and sets its own in its place
   (`SetVoidPointer`, the documented name); its own calls DLSS's, then sets `OutWidth/OutHeight` from the player's
   render scale, within DLSS's min/max.
3. **Creation** (around `0x1020fed`, "DLSS Initialize"): `SetUI DLSS.Hint.Render.Preset.{DLAA, Quality, Balanced,
   Performance, UltraPerformance}` all to the game's own value (`[obj+0xd4]`), releases the old feature
   (`0x12f14b0`), then `SetUI CreationNodeMask/VisibilityNodeMask 1`, `Width/Height` (render), `OutWidth/OutHeight`
   (output), `SetI PerfQualityValue`, `DLSS.Feature.Create.Flags 0xb` (HDR, low-res motion vectors, inverted depth),
   `DLSS.Enable.Output.Subrects`, and calls `CreateFeature(cmd, 1 = SuperSampling, params, &handle)` through
   `0x12f0c70` (the variant without a device). Logs "Successfully created DLSS features" (`0x10212d4`).
   The game sets these five hints before **every** creation, and never `DLSS.Hint.Render.Preset.UltraQuality` (nor does
   it offer Ultra Quality). **Kiln:** in its `CreateFeature` hook, before the call, it sets the four the game also sets
   besides DLAA's (Quality, Balanced, Performance, Ultra Performance) to the player's preset, after the game's own. So
   going back to "default" gives the game's own value at the next creation; DLAA's hint is left as the game set it.
4. **Each frame** (`0xfc5600..`): `SetUI DLSS.Render.Subrect.Dimensions.Width/Height` (this frame's render size;
   dynamic resolution may lower it), `DLSS.Output.Subrect.Base.*`, `SetF DLSS.Pre.Exposure`, ..., then
   `EvaluateFeature` ("DLSS Execute", error log at `0x1021737`). **Kiln:** reads the two subrect values for the
   readout while it is on.
5. A world unload releases the feature ("Successfully destroyed DLSS context", `0xfe9103`); a new world, a DLSS mode or
   a resolution change sets DLSS up again (steps 2 and 3), so Kiln's settings follow there.

## Presets in nvngx_dlss.dll 310.9.1
- The preset name of a value is `'A' + value - 1` (`0x3adf0`: values below 14 are a letter, else a number).
- Whether a preset exists: `0xb5d60` checks a table of 256 model pointers (vector at `0x3840038`). Its only filler,
  the static initializer `0x10db0`, sets entries **5 (E), 6 (F), 10 (J), 11 (K), 12 (L) and 13 (M)**; A to D are gone.
  Callers only read it (`0x49547`, `0x559d7`, `0x6df89`, `0x85448`). An app hint for a missing preset falls back to
  the title default ("App hint Preset %s is not available, using title default Preset %s").
- The NVIDIA driver's override (DRS `NGX_OVERRIDE_RENDER_PRESET_SELECTION`, "Using DRS Overridden Preset") wins over
  an app hint. There is no parameter to read back which preset runs. Kiln offers default, J, K, L, M.

## FSR 3
The FSR 3 upscaler is compiled into the game: no DLL, no export to hook. It is the studio's own port of FSR 3's
passes, not AMD's SDK: context creation at `0x805d0`, dispatch at `0x81900`. The function that records the upscaler
pass, `0x11a870` (called at `0x13d5ab`), holds every input of both upscalers in one struct: jitter, the colour, depth
and motion-vector textures, render and output size, frame time, camera near and far, the vertical field of view and
pre-exposure. `0xfa440` is likely the preset-to-ratio function (3.0, 2.0, 1.7, 1.5, 1.0 as a switch, not a float
table). The RCAS sharpness is computed into `Fsr3UpscalerRcasConstants` in game code, and the game's own
`renderScale` / `sharpness` settings already change the render size and sharpening. Changing FSR 3's ratio or RCAS
sharpness needs a hook in game code (a sigdb pattern per game build), found and tested per build. Not done in
phase A. More in the phase B and C client dossier (the wave's `out/upscale-bc/client/DOSSIER.md`).

## Latency
The client has no Reflex or Anti-Lag code and sets no latency markers (see "What the game ships"), so there is
nothing honest to read. Kiln shows no latency number.

## NVIDIA's own files (the driver's Wine copies in /usr/lib/nvidia/wine/, read only)
`_nvngx.dll`'s five exports Kiln hooks start with ordinary prologues (no false "already patched"). `nvngx.dll` there is
a wrapper whose exports are NOP sleds and a stub jumping into `_nvngx.dll`: the game's order (`_nvngx.dll` first) and
Kiln's are the same. A jump at an export's entry into the core itself (a thunk) counts as NVIDIA's own code; one into
another module, or one Kiln cannot follow, as another tool's hook. `nvngx_dlssg.dll` (DLSS frame generation) ships
there too.

## Other NGX hookers
OptiScaler (as a proxy DLL), DLSSTweaks (`dlsstweaks.ini`), a DLSS frame generation mod with Streamline
(`EnshroudedVKFG.ini`, a `Streamline/` folder) or an `nvngx.dll` in the game folder all sit at these same points. Kiln
steps aside for them, and also when the core's exports are already patched when it loads (the first instruction a jump).
