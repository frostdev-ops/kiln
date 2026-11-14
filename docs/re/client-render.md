# Client rendering and input (for kiln.ui)

Client Steam build 26404966 (`enshrouded.exe` sha256 `ce6fdba6…`), Proton GE-Proton11-11. Static analysis (python
capstone, `.pdata`, import tables), the client's own `enshrouded.log`, EMF 0.3.3's in-game notes (`ENGINE_MAP.md`,
"the in-game console"), and the Proton files. Observed at runtime with Kiln (GE-Proton, game build
1285601, windowed 1920x1280, XWayland): see "Runtime" at the end.
Service: `native/services/ui` (`kiln.ui`), managed `Kiln.Ui`; usage `docs/kiln/ui.md`.

## Renderer
| Fact | Evidence |
|---|---|
| Vulkan, loaded at run time | No `vulkan-1`/`d3d*`/`dxgi` import. `0xfe5750` calls the engine's LoadLibrary wrapper (`0xf50990`) with `"vulkan-1.dll"`, then GetProcAddress (`0xf4c6a0`) for `"vkGetInstanceProcAddr"`; everything else comes from vkGetInstanceProcAddr / vkGetDeviceProcAddr (strings `vkGetDeviceProcAddr`, `vkCreateSwapchainKHR`, `vkAcquireNextImageKHR`, `VK_KHR_swapchain`, `VK_KHR_win32_surface`, `VK_EXT_swapchain_colorspace`). |
| Present path | `vkQueuePresentKHR` on queue family 0 ("Selected Queue Family #0 for presentation" in the log). A DXGI-interop swapchain exists only behind the command-line flag `--enable-dxgi` (`0x908ba3`: parsed into `gameApp+0x130`; "Could not create the vulkan DXGI swap chain. Attempting to create a normal Vulkan swap chain."). The user's log has no DXGI lines. |
| Formats | "Selected format R8G8B8A8_unorm (37) as default sdr unorm format", "A2B10G10R10_unorm_pack32 (64) as default hdr unorm format". EMF saw the swapchain created as 37 then recreated as 64 with HDR on; HDR is off on the user's machine (`enableHdr: false`, monitors without HDR). |
| Device | one instance; at runtime the game creates **two** devices (the swapchain belongs to the first, with 3 queue families) — the static reading said one (`VK_KHR_swapchain`), RTX 3080 Ti selected (an AMD iGPU is enumerated too), DLSS (NGX) loaded. Debug layers named in strings (`VK_LAYER_KEEN_validation`, `VK_LAYER_KHRONOS_validation`) are not enabled in the release log. |
| Window | class `L"localWindowClass"` (UTF-16 at `.rdata 0x1db9860`), created with CreateWindowExW; the surface is `vkCreateWin32SurfaceKHR(hwnd)`. Manifest: PerMonitorV2 DPI aware. |

### Under Proton
- `vulkan-1.dll` in the prefix is Wine's thin loader: every export **forwards** to `winevulkan.dll`
  (`vkQueuePresentKHR -> winevulkan.vkQueuePresentKHR`, ...; checked with pefile on GE-Proton11-11's files).
- winevulkan's `vkGetDeviceProcAddr` hands out its own exported thunks, so the functions resolved through vulkan-1
  with GetProcAddress are exactly the functions the game calls. Inline hooks on them see every call, however the game
  obtained its pointers. Windows-side Vulkan layers are not supported by Wine's vulkan-1 (EMF's
  `VK_LAYER_EMF_console` approach works on Windows only); the Steam overlay is a Linux-side layer below winevulkan
  and does not interfere (`GameOverlayRenderer64.dll` is loaded in the process too; it did not get in the way).
- **The game unloads and reloads `vulkan-1.dll` / `winevulkan.dll` during start** (same base address, fresh code), so
  hooks installed at the first load vanish. kiln.ui re-checks after every `LoadLibraryExW` whether its hooks are still
  in place (the checked export's first 16 bytes equal what its patch wrote; the loader's unload notification for
  `vulkan-1.dll` marks them gone too) and reinstalls them on fresh code.
- **Windows (Khronos loader 1.4.309, checked on a real PC 2026-11-06):** the same probe-then-reload happens at the same
  base. The loader's exports are `jmp` thunks into its own image, so "the first byte is a jump" can't tell a fresh
  image from a hooked one (0.2.0-dev.10 kept dead hooks and never saw the real device). kiln.ui hooks the loader's
  exports (`vkCreateInstance`, `vkCreateDevice`, `vkDestroyDevice`, `vkGetDeviceQueue(2)`, the swapchain functions,
  `vkQueuePresentKHR`, `vkCreateWin32SurfaceKHR`) and wraps `vkGetInstanceProcAddr` / `vkGetDeviceProcAddr`, whose
  lookups hand out Kiln's functions; those call the exports' original code, so no call passes Kiln twice. The game
  takes its device functions from the exports. The checked export is `vkGetInstanceProcAddr` there (checking another
  export than the hooked one re-hooked on every load and recursed until the stack overflowed: the 0.2.0-dev.10 start
  crash). Steam's overlay is an ordinary implicit layer there and patches nothing. EMF warns that RTSS restores
  vulkan-1's code after start; not seen yet.

### How kiln.ui draws
1. `kiln_plugin_main` (before the game's entry point): IAT patches for `SetCursorPos` / `ClipCursor` (below), and an
   inline hook on `kernelbase!LoadLibraryExW`. After the call that loaded `vulkan-1.dll` returns (outside the loader
   lock), Kiln resolves and hooks `vkCreateInstance`, `vkCreateDevice`, `vkDestroyDevice`, `vkGetDeviceQueue(2)`,
   `vkCreateWin32SurfaceKHR`, `vkCreateSwapchainKHR`, `vkDestroySwapchainKHR`, `vkQueuePresentKHR`.
2. `vkCreateSwapchainKHR`: adds `VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT` (retries without it if refused; the overlay is
   then off for that swapchain), records format, colour space, extent, window.
3. `vkQueuePresentKHR`: nothing to draw → the call passes through unchanged (no ImGui frame, no submit). Otherwise:
   per-image view + framebuffer + command buffer + fence + semaphore (created once per swapchain), a render pass that
   LOADs and keeps `PRESENT_SRC_KHR`, ImGui's frame, one submit on the present queue waiting on the game's semaphores
   (stage COLOR_ATTACHMENT_OUTPUT) and signalling Kiln's, then the real present waiting on Kiln's semaphore only.
   Before the overlay, subscribers of the pre-overlay callback (kiln.screen's full-screen effects) may submit their own work the same way: it waits
   on the game's semaphores, and the overlay and the present wait on its semaphore instead. `vkCreateSwapchainKHR` adds the usage they asked for
   (`TRANSFER_SRC` for kiln.screen) where the surface supports it.
4. Swapchain recreation (resize, minimise, SDR↔HDR) destroys the old resources (after their fences); a format change
   rebuilds ImGui's pipeline (`ImGui_ImplVulkan_CreateMainPipeline`). `vkDestroyDevice` shuts the ImGui backend down.
5. ImGui's texture uploads (font atlas) submit on the present queue and wait for it (`vkQueueWaitIdle`) — once at the
   first frame, and when new glyphs are needed.

## Input
| Fact | Evidence |
|---|---|
| Raw input for mouse and keyboard, flags 0 | `0xfa9f30` ("[input] Registering raw input devices for mouse+keyboard!") builds `RAWINPUTDEVICE {page 1, usage 2, flags 0}` and `{1, 6, 0}`: no `RIDEV_NOLEGACY`, so WM_KEYDOWN/WM_CHAR/WM_MOUSEMOVE are generated as well. |
| Read in the WndProc | imports `GetRawInputData` (no `GetRawInputBuffer`), `RegisterRawInputDevices`, `DispatchMessageW`, `PeekMessageW`; no `GetAsyncKeyState` / `GetKeyState`. EMF: a subclassed WndProc that swallows messages keeps keys and the mouse from the game. |
| Cursor | imports `SetCursorPos`, `ClipCursor`, `SetCursor` (mouse look recentres / confines the cursor). |

kiln.ui subclasses the window (`SetWindowLongPtrW(GWLP_WNDPROC)`, re-applied if the game sets its own again):
- Keys: from `WM_INPUT` (RAWKEYBOARD; falls back to WM_KEYDOWN if no raw keyboard ever arrives) → the Kiln menu key
  (default Insert) → key subscribers (Kiln.Ui key bindings). A consumed key's `WM_INPUT` press and its legacy
  messages are kept from the game.
- Mouse presses (0.2.0-dev.27): the middle and side buttons and each wheel notch go the same way, as `KilnUiKey`s
  (`KILN_UI_VK_WHEEL_*` for the wheel), to the key subscribers that ask for them (`subscribe_key_ex`,
  `KILN_UI_KEYS_MOUSE`, kiln.ui v7; without one nothing of this runs). The window gets each from `WM_INPUT` (RAWMOUSE flags, `usButtonData`) and as a
  message (`WM_XBUTTON*`, `WM_MBUTTON*`, `WM_MOUSEWHEEL`, `WM_MOUSEHWHEEL`); whichever comes first is dispatched and
  the other, its twin (same input and direction at the closest message time within 50 ms, wheel deltas reconciled),
  follows its decision (`native/services/ui/mouse_press.h`). Wine 11.0 stamps a record and its message with one time
  (`server/queue.c` `queue_mouse_message`: `raw_msg.time = time` and `alloc_hardware_message(..., time, ...)`), and
  merges a queued WM_MOUSEWHEEL with the next at the same cursor position across the WM_INPUT records between them
  (`merge_mousewheel`, `find_mouse_message` skips WM_INPUT; the merged message takes the later turn's time): two
  records then one +240 message, which the reconciliation counts as the two notches it is.
  A taken press is kept from the game in both forms: a record holding only it is kept whole; one with motion or other
  buttons is handed on and kiln.ui's hook on the game's `GetRawInputData` import slot (`input_install_iat`, with the
  cursor slots) takes the taken flags out of what the game reads. The game imports no `GetRawInputBuffer`; a module
  that reads raw input through its own import gets the record unedited. Windows' message times count in system ticks
  (10 to 16 ms), so a pair split across a tick boundary may be a tick apart; not seen in this game yet: kiln.log says
  once when a pair matched only through the 50 ms tolerance.
  Which of the two forms the game's camera zoom reads was not looked up: both are kept, so either way it does not see
  a taken notch. Not verified in a real game yet (guided check `ui.mouse-bindings`).
- Capture (Kiln menu open, or an interactive Kiln window): `WM_INPUT`, key presses, characters and mouse messages
  are kept from the game (`DefWindowProc` for raw input so the system frees it); key releases always pass (nothing
  stays held); Alt+F4 passes. The game's `SetCursorPos` calls are ignored and `ClipCursor` is lifted (the game's last
  rectangle is restored when capture ends); ImGui draws the cursor.
- Every window message ImGui cares about is queued and replayed into ImGui's Win32 backend on the render thread just
  before each frame (ImGui's IO is not thread-safe; the WndProc runs on the main thread, presents on the render
  thread).
- Native subscribers of `subscribe_window_input` (kiln.ui v5, [ui.md](../kiln/ui.md#c-native-plugins)) get the key,
  button and focus-loss records on the main thread as they arrive, before any of the above.

## Runtime (GE-Proton, build 1285601, windowed 1920x1280 under XWayland)
Verified, with a synthetic absolute pointer and a virtual keyboard:
1. Hooks, device, surface and swapchain lines in kiln.log (`swapchain 1920x1280 format 44 colour space 0`), first
   overlay frame drawn, game picture intact. Draw uses the swapchain's own device (per-device tracking): assuming
   "the newest device" crashed on the first draw (second launch), fixed.
2. The HUD line (fps, 154 fps while hosting) and toasts draw; nothing drawn → presents pass through.
3. Insert opens the Kiln menu (also in the main menu, where only the native fallback window exists because the
   managed host starts with a world). The WndProc is attached from the present hook once a second, so the menu key
   works before any overlay frame.
4. Keyboard capture: while the menu or an interactive window is open the player does not move; keys typed into a
   Kiln text field reach ImGui (`InputText`), not the game; no stuck keys after closing.
5. Mouse, game pointer free (main menu, pause menu): clicks, tabs, buttons, text fields, the Keys tab's remap work.
   **Wine delivers both `WM_INPUT` and the legacy button messages** for a click; kiln.ui replays raw buttons only
   until the window has delivered a legacy button message (otherwise every click reached ImGui twice and a Button
   toggled twice — seen and fixed).
6. Mouse, in game (game holds the pointer): the click arrives, but a synthetic absolute pointer move does not reach
   the game window (no `WM_MOUSEMOVE`, no raw motion), so the cursor stays where it was. A real mouse under the
   pointer lock produces raw relative motion; kiln.ui then moves its own cursor from the raw deltas when no
   `WM_MOUSEMOVE` came for 250 ms. **Verified with a real mouse:** Kiln windows opened by a key during play (the game screen stayed Play/1) counted
   1014 `WM_MOUSEMOVE`, 2251 raw and 10 clicks in 7 s. The synthetic pointer stays without motion there (join session: 0 raw in 26 s).
7. Not verified: resize / alt-tab / minimise / fullscreen toggle, HDR (format 64, expected too bright), Windows.

## The camera
Client 26430625 (`enshrouded.exe` sha256 `5d9ef2f1…`). Static (python capstone, sigdb_check); dev.28 client observations and corrected rendered-lens path are below.
Used by kiln.render (`sigdb/symbols/client.d/render.json`, feature `render.camera`; usage `docs/kiln/render.md`).
- **Where:** `0x3af340` fills the local player's `ClientPlayerInput` each client frame on the main thread
  ([client.md](client.md), "The camera, health-bar line of sight"). With `rbp` = the client game and `r14` = the
  `ClientPlayerInput`: `[rbp+0x5c8]` is the 3C camera object (null: the block is skipped); `0x3af4e5` calls the
  camera transform (`0xe98ea0`); `0x3af4f5..0x3af55b` store `ClientPlayerInput.camera` (`keen::ecs::ClientCamera`):
  | Offset in `ClientPlayerInput` | Field | Source in the camera object |
  |---|---|---|
  | +0x20 | position, `WorldPosition` (3 x int64, 32.32 fixed point metres) | transform |
  | +0x38 | rotation, quaternion x y z w (camera space to world; +z is the view direction) | transform |
  | +0x48 | distance (third-person, camera to pivot) | +0x14a4 |
  | +0x4c | fovY | +0x20c4 |
  | +0x50 | aspect | +0x20b8 |
  | +0x54 / +0x58 | near / far | +0x20bc / +0x20c0 |
- **kiln.render's hook:** a read-only mid hook at `0x3af578` (`lea r8, [rbp+0x2c68]`, after the call at `0x3af573`),
  reached by both branches; `r14` and `rbp` are unchanged there (`r14` is read again at `0x3af5a1`). kiln.rules'
  camera check hooks `0x3af55f` (a 7-byte instruction), so the two never overlap. The pattern captures every store's
  offset and fixes it, so a build with another layout turns the feature off.
- **Corrected 2026-11 (dev.28 RE) [static] [seen]:** constructor `0x8388df..0x8388fd` sets metadata
  aspect/near/far/FOV to 1.0/0.1/100.0/60.0, but live input metadata FOV is 1.13446 radians. `+0x20c4` is
  **not the rendered FOV**, and camera_update does not recompute it every step. The historical above-pi conversion
  is a kiln.render heuristic, not an engine units contract. Use the selected renderer lens below for visual projection.
- **Spaces:** left-handed, x east, y up, z north ([models.md](models.md)); `kiln.rules` rotates (0, 0, 1) by the
  camera's quaternion for the view direction (its glide pitch), which fixes the forward axis.
- **Not in it:** `player_camera_transform` (`0x2b7900`) adds camera shake (a glider wobble) to the render camera only.
- **Threads:** written on the main thread; presents run on the render thread, typically a frame behind. kiln.render
  copies the camera and its metadata under a mutex; readers try the mutex once and return unavailable on contention
  rather than waiting on the render thread.
- **Still open:** final-camera overlay alignment and lag while turning. The input snapshot is not the final
  shaken renderer snapshot; changing a horizontal/vertical interpretation cannot fix that distinction.

## Camera control and final renderer selection (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`. Source: dev.28 dossier chapter 03, camera REPORT/VERIFY and sessions 1/2/4/5.
VERIFY confirmed the original 13 contracts with two corrections (update-context offset and basis rows); the later
chapter **refutes the original rendered-FOV target**. `[seen]` is the dossier's coordinator-run client evidence.
These are engine seams, not an implemented camera service.

### Authoritative mode control [static] [refl] [lab]

| Program, `void(ProgramContext*)` | S / C RVA | Effect |
|---|---|---|
| actor_camera | `0x77d20` / `0x264be0` | sequence camera events → ServerCameraControl; clears lock when no lock event remains |
| server_camera_control | `0xd6ff0` / `0x2eb050` | signed game-nanosecond expiry; clears at now>=end, INT64_MAX indefinite |
| network_camera_control | `0xc3980` / `0x2d79e0` | tracked SCC offsets 0/1/0x10 → NCC offsets 0/1/2; times do not replicate |
| player_camera_control | — / `0x2b5ee0` | consumes NCC, selects local mode, prepares update context |
| animated_camera | — / `0x303560` | another local ClientCameraControl pose writer |
| player_camera_transform | — / `0x2b7900` | samples pose, then adds stock shake/extra rotation |

Reflected layouts (both builds): SCC 32 B/align 8, lock bool `+0`, CameraId u8 `+1`, unlock Time `+8`,
CameraOverride u8 `+0x10`, override-end Time `+0x18`. NCC is **3 B/align 1** `{lock,id,override}`; it contains no
pose, target, FOV, distance or profile. Override enum: None 0 / BlinkLerpSpeed 1 / CameraTransitionLerpSpeed 2.
ClientCameraControl is 56 B: active id `+0`, three delay floats `+4/+8/+0xc`, position `+0x10`, quaternion `+0x28`.
There are 46 CameraIds (0..45); BuildingFirstPerson=34, Glider=22. NCC.override enters camera update context
**+0x100, not +0x168** (C `0x2b779a`, consumed `0xec502c`).

[lab] One SCC lock write was cleared by actor_camera within eight steps. Direct tracked NCC `{1,34,2}` appeared
in the game's per-player-format snapshot, but was overwritten on later steps. Maintain any override after game
writers each step; a one-shot SCC write is not durable. Snapshot construction alone was not peer delivery (the
shared snapshot/index ABI is in [network.md](network.md)). [seen, session 4] A maintained `{1,34,2}` on a real
player moved a **vanilla** client to first-person and handed back to third-person. This proves stock mode control,
not arbitrary vanilla-client camera transport.

### Shared camera versus render-only output [static], observations [seen]

`void camera_update(Camera3C*,const CameraUpdateContext*)` C `0xec4f60` runs from the ECS program, not Present.
`void camera_transform(WorldTransform* out,const Camera3C*)` C `0xe98ea0` writes 52 meaningful bytes of 56-byte,
align-8 output: signed 32.32 XYZ `+0`, quaternion `+0x18`, scale float3 `+0x28`; padding `+0x34` untouched.
Exactly two direct call continuations: render `0x2b7a37`, input `0x3af4ea`. Render output is **before stock shake**;
shake update `0xec02e0` operates on camera `+0x1508`, then player_camera_transform adds it to the render singleton.

Corrected 2026-11: camera `+0x14b0/+0x14c0/+0x14d0` are **float4 basis rows, not three quaternions**.
Pivot `+0x1468`, offset float3 `+0x148c`, distance `+0x14a4`; negative angles `+0x1498/+0x149c/+0x14a0` rotate about
rows 1/0/2, then the matrix is converted to the output quaternion. Those angles are updated each step.

[seen] Session 1's render-only transform substitution raised render Y by 5 m while input Y stayed unchanged;
Update and both consumers ran on thread 392. Distance/height and shake were visible, but the scalar profile also
changed shared input fields: it is **not** proof of a fully visual-only profile. Session 2 saw world/camera addresses
reused across generations (STARTED 1 → STOPPING 2 → STARTED 3); pointer equality is not lifetime identity.

### Rendered vertical FOV and final snapshot [static], FOV primitive [seen]

Session 2 wrote `+0x20c4=0.698132` radians with unchanged framing: **refuted as rendered-FOV control**. Session 1's
40/75 degree writes and subjective zoom were invalid evidence. Saving/restoring that metadata can clobber settings;
the game does not refresh it on each update.

Mode evaluator C `0xe76340` reads config `+0x368` in degrees, multiplies by pi/180 and writes pose `+0x14`.
Blend `0xe85b20` accumulates this into Camera3C `+0x4c`. Camera3C is embedded at renderer `+0x467300` and exported
to clientGame `+0x5c8`; thus active mode FOV is renderer `+0x46734c`. Extraction `0x87a290` selects active/fallback
pose/lens; fallback is 65° in radians.

- **Selection MID C `0x87b9fd`**: R13=renderer, XMM6.low=selected vertical radians; original instruction
  `movss [r13+0x4672e8],xmm6`. Preserve upper lanes/registers and execute the store. Selected WorldTransform is
  renderer `+0x4672a8`. This is a register-site hook, not a guessed whole-function C++ prototype.
- **Final acquisition MID C `0x86098f`**: RSI=renderer, after descriptor stores. Read selected pose `+0x4672a8`,
  aspect `+0x4693b8`, near/far `+0x4693bc/+0x4693c0`, vertical-radian FOV `+0x4693c4`. Descriptor prefix
  `+0x4693b4` has kind=0. This captures logical unjittered geometry, not pixel TAA/postprocess distortion.
- `const WorldTransform* selected_client_pose(const ClientGame*)` C `0x3aaaf0` returns clientGame `+0x5d0`, the
  **post-shake** singleton selected by extraction. It is not the earlier transform-call result.
- `float* perspective_raw(float out[16],float fovY,float aspect,float near,float far)` C `0xf6a050`: RCX/XMM1/
  XMM2/XMM3/stack argument 5, returns out. Computes `1/tan(fovY/2)`; out[5]=vertical scale, out[0]=scale/aspect.
  It and descriptor wrapper `0xf69ae0` are inspection anchors, not qualified arbitrary-thread calls.

[seen, session 4 joined client] The corrected selection MID gave visible narrow/wide views for 0.698132/1.309
radians; releasing the request used fresh game output, without saved-lens restoration. **Final acquisition and
combined renderer-only follow/profile were still static/unseen** (C11/C9); server visual RPC/host routing was not
proved. Never call game methods or managed callbacks from these renderer MID hooks.

### Bounds, profiles and ownership [static] [refl] [data]

Camera3C total sizeof/alignment is unknown; referenced timestamp establishes only minimum extent `0x161b0`.
Do not allocate/clone/memcpy the opaque camera or renderer. Diagnostic camera regions: mode `[0x38,0x88)`,
transform `[0x1468,0x14e0)`, input metadata `[0x20b8,0x20c8)`, profile table/owner `[0x2558,0x161b0)`.
Renderer borrowed regions: world identity `[0x1d0,0x1d8)`, pose `[0x4672a8,0x4672e0)`, near/far/FOV
`[0x4672e0,0x4672ec)`, descriptor `[0x4693b4,0x4693c8)`. These are known field ranges, not sizeof contracts.
Discard pointers before return; follow poses use copied RenderTransform (176 B), not retained ECS pointers.

Optional resource methods: C `0xe75ef0` `u8 apply(Camera3C*,const CameraStatesOverrideConfig*,u32 tag,s64 now)`;
C `0xeb0250` `u8 remove(Camera3C*,u32 tag,s64 now)`, bool in AL. Apply synchronously copies configs into
`camera+0x2558+id*0x6e0`; mask `+0x16198`, owner `+0x161a0`, timestamp `+0x161a8`. **One owner, no stack.**
Caller must validate nonempty relative array, all ids<46, unique **nonzero** tag and empty existing mask. Engine
has no id bound: id>=46 overwrites mask/owner/beyond; bit selection wraps mod 64. Same-tag apply is refused even
with empty mask; remove requires matching owner/nonempty mask and resets owner=0. Never trample another owner.

CameraStatesOverrideConfig is 8 B with relative BlobArray; CameraStateConfig stride 0x6e8 (id `+0`, config `+8`),
CameraModifierConfig 0x6e0. Distance config `+0x300`, FOV `+0x368`, offset `+0x36c`; distance settings 32 B
(start/end `+4/+8`, Time `+0x10`), FOV float degrees, offset local float3/worldY `+0/+0xc`.
CombatLifetimeCameraOverride is 20 B (resource GUID `+0`, wasApplied bool `+0x10`); consumer C `0x2b5ba0`.
Fog Dragon resource `d798778e-fc58-433c-b936-50b57b703042` changes only modes 5/6/14: 10–16 m, 50°, worldY+3.
It is combat-state-driven, not a generic per-player mount profile. Actor camera spans are baked sequence events;
collector C `0x2aef70` is not a network profile receiver.

[seen, session 5] Stock camera sat inside/under the 0.3-scale Fog Dragon at 10–25 m/s, rider height 2.6 m.
Collider self-hit versus short boom/pivot is **[inferred]**, not isolated. No size-aware rendered mount profile or
collision exclusion was tested. Full configuration layouts, scratch signatures (`camera/VisualFrameReady`,
`camera/RenderPoseGetter`) and proposed profile/transport designs remain in dossier chapter 03.
