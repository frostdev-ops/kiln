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
Client 26430625 (`enshrouded.exe` sha256 `5d9ef2f1…`). Static (python capstone, sigdb_check); not yet watched at run time.
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
- **Units:** the camera object's constructor (`0x8388df..0x8388fd`) sets aspect 1.0, near 0.1, far 100.0 and **fovY
  60.0**: degrees. kiln.render takes a value above pi as degrees. Whether the game's aspect value is kept up to date was
  not found statically: kiln.render projects with the swapchain's width / height.
- **Spaces:** left-handed, x east, y up, z north ([models.md](models.md)); `kiln.rules` rotates (0, 0, 1) by the
  camera's quaternion for the view direction (its glide pitch), which fixes the forward axis.
- **Not in it:** `player_camera_transform` (`0x2b7900`) adds camera shake (a glider wobble) to the render camera only.
- **Threads:** written on the main thread; presents run on the render thread, typically a frame behind. kiln.render
  copies the camera and its metadata under a mutex; readers try the mutex once and return unavailable on contention
  rather than waiting on the render thread.
- **To confirm in the game:** that the vertical fov reading is right (markers stay on their spot at the screen's
  edges; `Render.FieldOfViewIsHorizontal` flips it for the check) and how far behind the scene markers lag while
  turning fast.
