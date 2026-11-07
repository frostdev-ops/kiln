# Wiki note: render events (drawing every frame)

What changed: client mods can run code every frame and draw on top of the game: shapes, text and images in screen
pixels, and markers, labels, lines, boxes and rings at world positions that follow the game's camera. Reference:
`docs/kiln/render.md`. Sample: `mods/samples/RenderSample`.

## Suggested page: "Drawing on screen and in the world" (under Client UI, after "HUD and toasts")

> Subscribe to `RenderFrameEvent` and draw through it. It comes once per frame, on the game's render thread.
>
> ```csharp
> [Subscribe]
> private void OnFrame(RenderFrameEvent e)
> {
>     e.Draw.Text(10, 10, $"{e.Fps:F0} fps");
>     if (!e.Camera.Valid) return;                     // main menu, loading screen
>     e.World.Marker(_home, Color.Orange);
>     e.World.Label(_home, $"Home {e.World.DistanceTo(_home):F0} m");
> }
> ```
>
> - `e.Draw` draws in pixels: lines, rectangles, circles, triangles, polygons, arrows, text, and PNG images from your mod
>   (`Ui.LoadImage("pin.png")`).
> - `e.World` draws at world positions: markers, labels, icons, lines, boxes and rings. Anything behind the camera is
>   hidden; `ToScreenClamped` puts an off-screen point on the screen's edge so you can draw an arrow to it.
> - `e.Camera` is the game's camera this frame: position, heading (0 = north), pitch, field of view, and `Pivot`, about
>   where the character stands.
> - Your drawing sits under Kiln's own windows and menus and over the game's picture. It goes away when your mod
>   unloads.
>
> **Rules of the render thread.** Don't touch the game from the handler: read what your mod keeps, and use
> `Client.Post` to act on the game. Keep it fast: a handler gets 2 ms per frame. One that is too slow in most frames,
> or throws five times, is turned off. Your log says why, and a toast names your mod.
>
> **No requested overlay frames when unused.** With no subscribers, render events do not request frames. The camera
> hook and periodic subscription checks still run; Kiln windows and HUD can independently request overlay frames.

## Sample mod blurb (samples list)
> **Render sample** (`rendersample`, client): an FPS counter, a compass strip, and waypoints. Ctrl+J drops a waypoint
> where you stand, Ctrl+Shift+J removes the nearest, Ctrl+Alt+J hides everything.

## Native plugins (C API page)
> `kiln_render.h` (`kiln.render` v1): drawing calls for a kiln.ui frame callback, `acquire_frames` / `release`, and
> `camera` / `world_to_screen` (any thread). Unsubscribe frame callbacks and release frame demand before unloading.

## Known limits to mention
- Client only. World drawing needs the game's camera, which Kiln finds on the current game build. On an unknown build
  only screen drawing works.
- Camera shake (gliding) is not included, so markers can tremble slightly against the scene.
