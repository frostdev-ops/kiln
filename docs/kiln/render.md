# Render events: code every frame, drawing on screen and in the world

Client only. A mod subscribes to `RenderFrameEvent` and draws through it: shapes, text and images in pixels, and
markers, labels, lines, boxes and rings at world positions, projected with the camera the game renders with.
Forge: `RenderGuiEvent` (2D) and `RenderLevelStageEvent` (world). Sample: `mods/samples/RenderSample` (FPS counter,
compass, waypoints).

| Layer | Where |
|---|---|
| C# | `managed/services/Kiln.Ui/Render.cs` (`RenderFrameEvent`, `RenderDraw`, `WorldDraw`, `RenderCamera`, `Render`), `RenderDispatch.cs` |
| C table | `native/core/abi/services/kiln_render.h` (`kiln.render` v1), registered by `kiln-ui.dll` |
| Native | `native/services/ui/render.cpp`, projection `render_math.h` |
| Camera hook | `sigdb/symbols/client.d/render.json` (feature `render.camera`); RE: [../re/client-render.md](../re/client-render.md#the-camera) |
| Tests | `managed/tests/Kiln.Ui.Tests` (projection, budget, dispatch), `native/services/ui/tests/run.sh` (projection, host) |

## C#
```csharp
public sealed class Markers : Mod
{
    private UiImage? _pin;
    private WorldPosition _home = new(3790, 846, 1467);

    public override void OnLoad()
    {
        _pin = Ui.LoadImage("pin.png");                      // a PNG in the mod's folder
        Events.On<RenderFrameEvent>(OnFrame);                // or [Subscribe] private void OnFrame(RenderFrameEvent e)
    }

    private void OnFrame(RenderFrameEvent e)                 // the render thread, every frame
    {
        e.Draw.Text(10, 10, $"{e.Fps:F0} fps");             // pixels, top left
        if (!e.Camera.Valid) return;                         // menu, loading
        e.World.Marker(_home, Color.Orange);                 // skipped when behind the camera
        e.World.Label(_home, $"Home {e.World.DistanceTo(_home):F0} m");
        e.World.Circle(_home, 3, Color.Orange);              // a ring on the ground, cut at the camera
        if (!e.World.ToScreenClamped(_home, 40, out var edge, out var angle))
            e.Draw.Arrow(edge, angle, 16, Color.Orange);     // off screen: an arrow on the edge
    }
}
```

### `RenderFrameEvent`
| Member | What |
|---|---|
| `Frame`, `DeltaSeconds`, `Fps` | overlay frame number, seconds since the last frame (at most 0.25), smoothed frames per second |
| `Width`, `Height`, `Scale` | screen size in pixels, UI scale (multiply your pixel sizes by it) |
| `MenuOpen`, `Capturing`, `Screen` | the Kiln menu is open, a Kiln screen has the input, the game's screen (`GameScreen`) |
| `Camera` | `RenderCamera` for this frame (below) |
| `Draw` | `RenderDraw`: pixels |
| `World` | `WorldDraw`: world positions |

`RenderDraw` (origin top left, y down, colours `Color`): `Line`, `Rect` / `FillRect` (rounding), `Circle` / `FillCircle`,
`Triangle` / `FillTriangle`, `Polyline` (closed or not), `FillPolygon` (convex), `Arrow(tip, angle, size)`,
`Text(x, y, text, color, size, TextAlign, TextStyle, middle)` (`Plain`, `Shadow`, `Outline`), `MeasureText`,
`Image(UiImage, x0, y0, x1, y1, tint)`, `ImageCentered`. Images come from `Ui.LoadImage` (a PNG file in the mod or
bytes) and show from the frame after loading; the mod's images are freed when it unloads.

`WorldDraw` (metres, x east, y up, z north): `Ready` (the camera is known), `DistanceTo`, `ToScreen(p, out pixel)`,
`ToScreenClamped(p, margin, out pixel, out angle)` (off screen or behind: on the edge, in its direction),
`Marker(p, color, size, MarkerShape)` (`Diamond`, `Circle`, `Square`, `Triangle`, `Cross`), `Label(p, text)`,
`Icon(p, image, size)`, `Line(a, b)`, `Box(min, max)` / `Box(center, size)`, `Circle(center, radius)`. Points behind the
camera are not drawn; lines, boxes and rings are cut at the near plane, so the part in front still shows.

### `RenderCamera`
`Valid`, `Position`, `Rotation` (quaternion, camera space to world), `Forward` / `Right` / `Up`, `Heading` (degrees: 0
north, 90 east), `Pitch`, `FovY` (radians, vertical), `Near`, `Far`, `Distance` and `Pivot` (what the third-person
camera looks at: about the character's head), `ToCameraSpace(p)`, `Project(p, width, height, out pixel, out depth)`.
`Render.Camera` reads the latest one from any thread (key handlers, `Client.Tick`).

### `Render`
`Available`, `CameraAvailable`, `Camera`, `HandlerBudgetMs` (default 2), `Stats` (`Handlers`, `Frames`, `LastFrameMs`,
`MaxFrameMs`, `Disabled`, camera state), `FieldOfViewIsHorizontal` (a calibration switch for checking the projection on a
new game build; off).

## Threads
`RenderFrameEvent` is posted on the **game's render thread** (the thread that presents frames), inside Kiln's overlay
frame, not on the tick thread. It runs on the bus's subscriptions: `Events.On`, `[Subscribe]`, priorities, the handler
runs as its mod, and every subscription ends when the mod unloads. In a handler:
- read what your mod keeps (copies, immutable snapshots swapped whole); don't touch game state;
- act with `Client.Post` (client world, main thread) or `Context.Scheduler.Post` (server world when hosting);
- post bus events with `Events.Enqueue` (`Events.Post` is for the tick thread and throws here);
- don't keep `e`, `e.Draw` or `e.World`: they are reused next frame. Drawing outside the handler does nothing.

A handler of a base class (`KilnEvent`) does not get render frames; subscribe to `RenderFrameEvent` itself.

## Cost
- Nobody subscribed: no frame is asked for; the game's presents pass through untouched (as without Kiln's overlay).
- Subscribed: one overlay frame per present. A frame in which nothing is drawn records no GPU work; drawing adds one
  render pass over the swapchain image (the same pass Kiln's HUD and windows use).
- Checking whether to post costs two memory reads; the overlay's frame wish follows subscriptions within a quarter
  second.

## Safety: the budget
Nothing can stop a handler that is running, so a slow one is turned off instead:
- over `Render.HandlerBudgetMs` (2 ms) in more than 60 of the last 120 frames, or three frames of 100 ms or more (the
  first 30 calls are not counted: JIT, first uploads);
- five exceptions (like the bus).
The handler is unsubscribed, the mod's log says why, and a toast names the mod. Other handlers keep running. Handler
times show in `Events.Diagnostics.Handlers` like any bus handler.

## Layers
Everything drawn here goes under Kiln's own windows, screens, HUD lines and toasts and over the game's picture,
including the game's HUD and menus. Handlers draw in priority order (a `High` handler's shapes are under a `Normal`
one's). Full-screen colour effects (`ScreenEffects`, [services/screen.md](services/screen.md)) are under all of it.

## The camera
The camera is the local player's view as the client writes it each frame (position, rotation, field of view), read by a
hook on the game's main thread (feature `render.camera`). Points drawn this way follow the scene; camera shake (a glider's
wobble) is not in it, so markers may tremble a little against the scene there. Not valid in the main menu and while
loading. On a game build where the camera is not found, `Render.CameraAvailable` is false, `Camera.Valid` stays false and
world drawing does nothing; screen drawing still works.

## C (native plugins)
`kiln_render.h`: draw inside a kiln.ui frame callback (`kiln_ui.h` `subscribe_frame`); call `acquire_frames` so a frame
happens at every present while you draw, and `release` before unloading.
```c
#define S(x) (KilnStr){x, sizeof(x) - 1}
const KilnUiApi* ui = api->service(S(KILN_UI_SERVICE), 1);
const KilnRenderApi* r = api->service(S(KILN_RENDER_SERVICE), 1);
r->acquire_frames(owner, &frames);
ui->subscribe_frame(owner, on_frame, NULL, &sub);
static void on_frame(const KilnUiFrame* f, void* user) {
    double home[3] = {3790, 846, 1467};
    float px[3];
    if (r->world_to_screen(home, f->width, f->height, px)) r->circle(px[0], px[1], 6, 0xFF9030FF, 0, 1, 1);
    r->text(10, 10, 0xFFFFFFFF, 0, KILN_RENDER_TEXT_SHADOW, S("hello"));
}
```
`camera(KilnRenderCamera*)` and `world_to_screen` work on any thread.
