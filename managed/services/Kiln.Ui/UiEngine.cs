// SPDX-License-Identifier: MIT
// Kiln.Ui's frame (render thread) and key (window thread) handling, demand/capture bookkeeping, and the Kiln menu
// (Forge's Mods screen + Controls screen).
namespace Kiln;

internal static class UiModCalls
{
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static readonly object Lock = new();

    public static ModContext? Caller => ModContext.CurrentMod;

    public static void Watch(ModContext? mod)
    {
        if (mod is null) return;
        lock (Lock)
            if (!Watched.Add(mod)) return;
        mod.Retiring += () =>
        {
            lock (Lock) Watched.Remove(mod);
            UiEngine.Retire(mod);
        };
    }

    /// <summary>Runs <paramref name="fn"/>(<paramref name="arg"/>) as <paramref name="mod"/>; false when it threw.</summary>
    public static bool Invoke<T>(ModContext? mod, Action<T> fn, T arg, string what)
    {
        if (mod is not null && !mod.Alive) return false;
        var prevMod = ModContext.CurrentMod;
        var prevSync = SynchronizationContext.Current;
        if (mod is not null)
        {
            ModContext.CurrentMod = mod;
            SynchronizationContext.SetSynchronizationContext(mod.Scheduler.SynchronizationContext);
        }
        try
        {
            fn(arg);
            return true;
        }
        catch (Exception e)
        {
            if (mod is not null) mod.Log.Error(e, $"{what} threw");
            else UiEngine.Log(LogLevel.Error, $"{what} threw: {e}");
            return false;
        }
        finally
        {
            if (mod is not null)
            {
                ModContext.CurrentMod = prevMod;
                SynchronizationContext.SetSynchronizationContext(prevSync);
            }
        }
    }
}

internal static unsafe class UiEngine
{
    private static readonly object Lock = new();
    private static int _started;
    private static UiWindow[] _windows = [];
    private static UiScreen[] _screens = [];
    private static readonly List<UiImage> Images = new();
    internal static long ScreenOrder;
    private static HudLine[] _hud = [];
    private static (string ModId, Action<UiDraw> Draw, ModContext? Owner, Handle H)[] _configPages = [];
    private static readonly List<(string Text, ToastKind Kind, DateTime Until, DateTime Start)> Toasts = new();
    internal static readonly OwnedList<Action<UiDraw>> FrameHandlers = new();
    private static readonly Dictionary<object, int> Faults = new(ReferenceEqualityComparer.Instance);
    private static readonly UiDraw Draw = new();
    private static uint _lastDemand = uint.MaxValue, _lastCapture = uint.MaxValue, _lastHover = uint.MaxValue;
    private static string? _remapping;  // binding id waiting for "press a key"

    public static void Log(LogLevel level, string text) => Backend.Current.Log(0, level, "ui: " + text);

    public static void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        if (!UiNative.Subscribe())
        {
            Log(LogLevel.Info, "kiln.ui is not available here (dedicated server, or no overlay on this build)");
            return;
        }
        KeyBindings.Load();
        ApplyMenuKey(KeyBindings.MenuKey);
        Ui.Screens.Start();
        Recount();
        Log(LogLevel.Info, $"Kiln.Ui ready; Kiln menu on {KeyBindings.MenuKey}; game UI {(UiNative.HasGameUi && Ui.Screens.Available ? "on" : "off")}, " +
                           $"crafting menu extension {(UiNative.Craft != null ? "on" : "off")}");
    }

    public static void ApplyMenuKey(KeyChord k)
    {
        if (UiNative.Available && !k.IsNone) UiNative.T->SetMenuKey(k.VirtualKey, (uint)k.Mods);
    }

    // ---- registry ----

    public static UiWindow AddWindow(UiWindow w)
    {
        UiModCalls.Watch(w.Owner);
        lock (Lock) _windows = [.. _windows, w];
        Recount();
        return w;
    }

    public static void Remove(UiWindow w)
    {
        lock (Lock)
        {
            w.Disposed = true;
            _windows = _windows.Where(x => x != w).ToArray();
        }
        Recount();
    }

    public static UiScreen AddScreen(UiScreen s)
    {
        UiModCalls.Watch(s.Owner);
        lock (Lock) _screens = [.. _screens, s];
        Recount();
        return s;
    }

    public static void RemoveScreen(UiScreen s)
    {
        lock (Lock)
        {
            s.Disposed = true;
            _screens = _screens.Where(x => x != s).ToArray();
        }
        Recount();
    }

    public static IReadOnlyList<UiScreen> OpenScreens()
    {
        UiScreen[] ss;
        lock (Lock) ss = _screens;
        return ss.Where(s => s.IsOpen && !s.Disposed).OrderBy(s => s.OpenedAt).ToArray();
    }

    public static void TrackImage(UiImage img)
    {
        UiModCalls.Watch(img.Owner);
        lock (Images) Images.Add(img);
    }

    public static HudLine AddHud(string id, HudAnchor anchor)
    {
        var owner = UiModCalls.Caller;
        lock (Lock)
        {
            var existing = _hud.FirstOrDefault(h => h.Id == id);
            if (existing != null) return existing;
            UiModCalls.Watch(owner);
            var l = new HudLine(id, anchor, owner);
            _hud = [.. _hud, l];
            Recount();
            return l;
        }
    }

    public static HudLine? FindHud(string id)
    {
        lock (Lock) return _hud.FirstOrDefault(h => h.Id == id);
    }

    public static void RemoveHud(HudLine l)
    {
        lock (Lock)
        {
            l.Disposed = true;
            _hud = _hud.Where(x => x != l).ToArray();
        }
        Recount();
    }

    public static void AddToast(string text, ToastKind kind, double seconds)
    {
        lock (Toasts)
        {
            if (Toasts.Count >= 8) Toasts.RemoveAt(0);
            var now = DateTime.UtcNow;
            Toasts.Add((text, kind, now.AddSeconds(Math.Clamp(seconds, 0.5, 60)), now));
        }
        Recount();
    }

    public sealed class Handle(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    public static IDisposable AddConfigPage(string modId, Action<UiDraw> draw)
    {
        var owner = UiModCalls.Caller;
        UiModCalls.Watch(owner);
        Handle? h = null;
        h = new Handle(() =>
        {
            lock (Lock) _configPages = _configPages.Where(p => p.H != h).ToArray();
        });
        lock (Lock) _configPages = [.. _configPages, (modId, draw, owner, h)];
        return h;
    }

    public static void Retire(ModContext mod)
    {
        lock (Lock)
        {
            _windows = _windows.Where(w => !ReferenceEquals(w.Owner, mod)).ToArray();
            _hud = _hud.Where(h => !ReferenceEquals(h.Owner, mod)).ToArray();
            _configPages = _configPages.Where(p => !ReferenceEquals(p.Owner, mod)).ToArray();
            foreach (var s in _screens.Where(s => ReferenceEquals(s.Owner, mod))) s.Disposed = true;
            _screens = _screens.Where(s => !ReferenceEquals(s.Owner, mod)).ToArray();
            foreach (var k in Faults.Keys.ToList()) Faults.Remove(k);
        }
        UiImage[] imgs;
        lock (Images)
        {
            imgs = Images.Where(i => ReferenceEquals(i.Owner, mod)).ToArray();
            Images.RemoveAll(i => ReferenceEquals(i.Owner, mod));
        }
        foreach (var i in imgs) i.Dispose();
        Ui.Menus.RemoveOwnedBy(mod);
        Ui.Screens.RemoveOwnedBy(mod);
        Ui.Crafting.RemoveOwnedBy(mod);
        KeyBindings.RemoveOwnedBy(mod);
        FrameHandlers.RemoveOwnedBy(mod);
        Recount();
    }

    /// <summary>Tells the native overlay how much there is to draw and whether input must be captured.</summary>
    public static void Recount()
    {
        if (!UiNative.Available) return;
        uint demand, capture;
        int screens;
        lock (Lock)
        {
            screens = _screens.Count(s => s.IsOpen && !s.Disposed);
            demand = (uint)(_hud.Length + _windows.Count(w => w.Open) + screens);
            capture = (uint)(_windows.Count(w => w.Open && w.Options.Interactive) + screens);
        }
        lock (Toasts) demand += (uint)Toasts.Count;
        demand += (uint)FrameHandlers.Count;
        demand += (uint)Ui.Crafting.VisiblePanels;
        var hover = (uint)Ui.Crafting.InteractivePanels;
        if (hover != _lastHover && UiNative.HasGameUi)
        {
            _lastHover = hover;
            UiNative.T->SetHoverCapture(hover);
        }
        if (demand != _lastDemand)
        {
            _lastDemand = demand;
            UiNative.T->SetDemand(demand);
        }
        if (capture != _lastCapture)
        {
            _lastCapture = capture;
            UiNative.T->SetCapture(capture);
        }
    }

    private static bool Fault(object key, ModContext? owner, string what)
    {
        lock (Lock)
        {
            Faults.TryGetValue(key, out var n);
            Faults[key] = ++n;
            if (n < 5) return false;
        }
        Log(LogLevel.Warn, $"{what} ({owner?.Info.Id ?? "kiln"}) threw 5 times: disabled");
        return true;
    }

    // ---- render thread ----

    public static void Frame(in UiNative.Frame f)
    {
        Draw.Frame = f;
        var ui = Draw;
        if (f.MenuOpen != 0) Menu(ui);
        UiWindow[] windows;
        HudLine[] hud;
        lock (Lock)
        {
            windows = _windows;
            hud = _hud;
        }
        foreach (var w in windows)
        {
            if (!w.Open || w.Disposed) continue;
            DrawWindow(ui, w);
        }
        Ui.Crafting.DrawPanels(ui);
        DrawScreens(ui);
        foreach (var (h, owner) in FrameHandlers.Snapshot())
            if (!UiModCalls.Invoke(owner, h, ui, "Ui.Frame handler") && Fault(h, owner, "a frame handler")) FrameHandlers.Remove(h);
        DrawHud(ui, hud);
        DrawToasts(ui);
    }

    private static void DrawWindow(UiDraw ui, UiWindow w)
    {
        var o = w.Options;
        var s = ui.Scale;
        if (o.Width > 0 || o.Height > 0) ui.NextWindowSize(o.Width * s, o.Height * s, 4 /* FirstUseEver */);
        if (o.Position is { } p) ui.NextWindowPos(p.X * ui.Width, p.Y * ui.Height, 4, 0.5f, 0.5f);
        if (o.BackgroundAlpha is { } a) ui.NextWindowAlpha(a);
        uint flags = 0;
        if (o.AutoResize) flags |= 1u << 6;
        if (!o.TitleBar) flags |= 1u << 0;
        if (!o.Interactive && !ui.Capturing) flags |= (1u << 9) | (1u << 18) | (1u << 19);  // no inputs
        var open = true;
        if (ui.BeginWindow(w.Key, ref open, flags))
        {
            if (!UiModCalls.Invoke(w.Owner, w.Draw, ui, $"window '{w.Title}'") && Fault(w, w.Owner, $"window '{w.Title}'"))
                w.SetOpenFromUi(false);
        }
        ui.EndWindow();
        if (!open) w.SetOpenFromUi(false);
    }

    private static void DrawScreens(UiDraw ui)
    {
        UiScreen[] ss;
        lock (Lock) ss = _screens;
        var open = ss.Where(s => s.IsOpen && !s.Disposed).OrderBy(s => s.OpenedAt).ToArray();
        if (open.Length == 0) return;
        var s0 = ui.Scale;
        if (open.Any(s => s.Options.DimBackground))
        {
            // a full-screen, input-less window behind the screens
            ui.NextWindowPos(0, 0, 1);
            ui.NextWindowSize(ui.Width, ui.Height, 1);
            ui.NextWindowAlpha(0.45f);
            uint dim = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 5) | (1u << 9) | (1u << 12) | (1u << 13) | (1u << 18) | (1u << 19);
            ui.PushStyleRaw(UiStyle.Kiln);
            if (ui.BeginWindowNoClose("###kiln.screens.dim", dim)) { }
            ui.EndWindow();
            ui.PopStyleRaw();
        }
        foreach (var s in open)
        {
            var o = s.Options;
            ui.PushStyleRaw(o.Style);
            ui.NextWindowSize(o.Width * s0, o.Height * s0, 8 /* appearing */);
            ui.NextWindowPos(ui.Width / 2, ui.Height / 2, 8, 0.5f, 0.5f);
            if (s.FocusNext)
            {
                s.FocusNext = false;
                ui.FocusNextWindow();
            }
            uint flags = (1u << 5);  // no collapse
            if (!o.Resizable) flags |= 1u << 1;
            if (!o.TitleBar) flags |= 1u << 0;
            var keep = true;
            ui.CurrentScreen = s;
            var visible = o.CloseButton ? ui.BeginWindow($"{s.Title}{s.Id}", ref keep, flags) : ui.BeginWindowNoClose($"{s.Title}{s.Id}", flags);
            if (visible && !UiModCalls.Invoke(s.Owner, s.Draw, ui, $"screen '{s.Title}'") && Fault(s, s.Owner, $"screen '{s.Title}'")) keep = false;
            ui.EndWindow();
            ui.CurrentScreen = null;
            ui.PopStyleRaw();
            if (!keep) s.Close();
        }
    }

    private static void DrawHud(UiDraw ui, HudLine[] hud)
    {
        if (hud.Length == 0) return;
        var pad = 10 * ui.Scale;
        var lineGap = 2 * ui.Scale;
        foreach (var group in hud.GroupBy(h => h.Anchor))
        {
            var lines = new List<(string Text, Color Color, float W, float H)>();
            foreach (var h in group)
            {
                var text = h.Text;
                if (h.Provider is { } p)
                {
                    try { text = p() ?? ""; }
                    catch (Exception e)
                    {
                        text = "!" + e.GetType().Name;
                        if (Fault(h, h.Owner, $"HUD line '{h.Id}'")) h.Provider = null;
                    }
                }
                if (text.Length == 0) continue;
                var (tw, th) = ui.MeasureText(text);
                lines.Add((text, h.Color, tw, th));
            }
            if (lines.Count == 0) continue;
            var boxW = lines.Max(l => l.W) + pad;
            var boxH = lines.Sum(l => l.H + lineGap) + pad * 0.5f;
            var (ax, ay) = Anchor(group.Key);
            var x0 = ax switch { 0 => pad, 1 => (ui.Width - boxW) / 2, _ => ui.Width - boxW - pad };
            var y0 = ay switch { 0 => pad * 4, 1 => (ui.Height - boxH) / 2, _ => ui.Height - boxH - pad * 4 };
            ui.DrawRect(x0, y0, x0 + boxW, y0 + boxH, new Color(0, 0, 0, 110), 4 * ui.Scale);
            var y = y0 + pad * 0.25f;
            foreach (var l in lines)
            {
                var x = ax switch { 0 => x0 + pad / 2, 1 => x0 + (boxW - l.W) / 2, _ => x0 + boxW - l.W - pad / 2 };
                ui.DrawText(x, y, l.Text, l.Color);
                y += l.H + lineGap;
            }
        }
    }

    private static (int, int) Anchor(HudAnchor a) => a switch
    {
        HudAnchor.TopLeft => (0, 0), HudAnchor.TopCenter => (1, 0), HudAnchor.TopRight => (2, 0),
        HudAnchor.MiddleLeft => (0, 1), HudAnchor.Center => (1, 1), HudAnchor.MiddleRight => (2, 1),
        HudAnchor.BottomLeft => (0, 2), HudAnchor.BottomCenter => (1, 2), _ => (2, 2),
    };

    private static void DrawToasts(UiDraw ui)
    {
        List<(string Text, ToastKind Kind, DateTime Until, DateTime Start)> list;
        var now = DateTime.UtcNow;
        bool changed;
        lock (Toasts)
        {
            var before = Toasts.Count;
            Toasts.RemoveAll(t => t.Until <= now);
            changed = Toasts.Count != before;
            list = Toasts.ToList();
        }
        if (changed) Recount();
        if (list.Count == 0) return;
        var s = ui.Scale;
        var y = ui.Height - 120 * s;
        foreach (var t in Enumerable.Reverse(list))
        {
            var (w, h) = ui.MeasureText(t.Text);
            var left = (t.Until - now).TotalSeconds;
            var alpha = (byte)Math.Clamp(255 * Math.Min(1, left / 0.5) * Math.Min(1, (now - t.Start).TotalSeconds / 0.2), 0, 255);
            var accent = t.Kind switch { ToastKind.Success => Color.Green, ToastKind.Warning => Color.Yellow, ToastKind.Error => Color.Red, _ => Color.Orange };
            var x1 = ui.Width - 20 * s;
            var x0 = x1 - w - 28 * s;
            var y0 = y - h - 12 * s;
            ui.DrawRect(x0, y0, x1, y, new Color(16, 16, 20, (byte)(alpha * 0.85)), 5 * s);
            ui.DrawRect(x0, y0, x0 + 5 * s, y, accent.WithAlpha(alpha), 2 * s);
            ui.DrawText(x0 + 16 * s, y0 + 6 * s, t.Text, Color.White.WithAlpha(alpha));
            y = y0 - 8 * s;
        }
    }

    // ---- window thread ----

    public static bool Key(in UiNative.Key k)
    {
        if (k.Vk == 0) return false;
        var mods = (KeyMods)(k.Mods & 7);
        var down = k.Down != 0;
        // "press a key" in the Keys tab
        if (_remapping is { } id && down && k.Repeat == 0)
        {
            if (k.Vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) return true;  // wait for a non-modifier
            _remapping = null;
            if (k.Vk == 0x1B) return true;  // Escape cancels
            KeyBindings.Rebind(id, new KeyChord(k.Vk, mods));
            Hud.Toast($"{id}: {new KeyChord(k.Vk, mods)}", ToastKind.Success, 2.5);
            return true;
        }
        if (k.TextInput != 0) return false;
        // Escape closes the top Kiln screen (the game does not see it: Kiln has the keyboard while a screen is open)
        if (k.Vk == 0x1B && down && k.Repeat == 0 && !Ui.MenuOpen)
        {
            var top = OpenScreens().LastOrDefault(s => s.Options.CloseOnEscape);
            if (top is not null)
            {
                top.Close();
                return true;
            }
        }
        var consumed = false;
        foreach (var b in KeyBindings.All)
        {
            if (!b.Enabled || b.Disposed || b.Key.IsNone || b.Key.VirtualKey != k.Vk) continue;
            if (!down)
            {
                if (!b.IsDown) continue;
                b.IsDown = false;
                if (b.OnRelease is { } r) UiModCalls.Invoke(b.Owner, static a => a(), r, $"key {b.Id} release");
                continue;
            }
            if (b.Key.Mods != mods) continue;
            // bindings keep working while a mod's own window has the input (its toggle key must close it); only the
            // Kiln menu takes the keys for itself
            if (k.Captured != 0 && !b.WhileCapturing && Ui.MenuOpen) continue;
            if (k.Captured == 0 && !b.OnGameScreens && TypesText(k.Vk, mods) && !KeysAreForPlay()) continue;
            if (k.Repeat != 0)
            {
                consumed |= b.Consume;
                continue;
            }
            b.IsDown = true;
            Interlocked.Increment(ref b._presses);
            if (b.OnPress is { } p) UiModCalls.Invoke(b.Owner, static a => a(), p, $"key {b.Id}");
            consumed |= b.Consume;
        }
        return consumed;
    }

    /// <summary>The key would type a character in a text field (Shift still types).</summary>
    internal static bool TypesText(uint vk, KeyMods mods)
    {
        if ((mods & (KeyMods.Ctrl | KeyMods.Alt)) != 0) return false;
        return vk is 0x20 or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;
    }

    /// <summary>The player is playing, not in one of the game's own screens. The game's text fields (chat, a rename, the main
    /// menu's boxes) don't tell Kiln they have the keyboard: typing "skillpoints" in chat used to open a tree bound to K, and
    /// the rest of the line went to the Kiln screen (seen as capture on/off on every k at Play/43).
    /// Without the game UI hooks (an unknown build) this can't be known, so it says yes.</summary>
    private static bool KeysAreForPlay()
    {
        if (!UiNative.HasGameUi) return true;
        var kind = Ui.Screens.Current.Kind;
        return kind is ScreenKind.InGame or ScreenKind.None;
    }

    // ---- the Kiln menu (render thread) ----


    private static void Menu(UiDraw ui)
    {
        var s = ui.Scale;
        ui.NextWindowSize(760 * s, 520 * s, 4);
        ui.NextWindowPos(ui.Width / 2, ui.Height / 2, 8 /* Appearing: centred whenever it opens */, 0.5f, 0.5f);
        var open = true;
        if (ui.BeginWindow($"Kiln {Game.KilnVersion}###kiln.menu.main", ref open, 1u << 5))
        {
            if (ui.BeginTabBar("kiln.menu.tabs"))
            {
                if (ui.BeginTab("Mods")) { ModsTab(ui); ui.EndTab(); }
                if (ui.BeginTab("Network")) { NetworkTab(ui); ui.EndTab(); }
                if (ui.BeginTab("Keys")) { KeysTab(ui); ui.EndTab(); }
                if (ui.BeginTab("Overlay")) { OverlayTab(ui); ui.EndTab(); }
                ui.EndTabBar();
            }
        }
        ui.EndWindow();
        if (!open) Ui.MenuOpen = false;
    }

    private static string? _configOpen;

    private static void ModsTab(UiDraw ui)
    {
        IReadOnlyList<ModEntry> mods;
        try { mods = Mods.All; }
        catch (Exception) { mods = []; }
        ui.Text($"{mods.Count(m => m.IsLoaded)} loaded, {mods.Count} found (side: {Mods.Side})");
        if (ConfigScreens.HasConfigs("kiln"))
        {
            ui.SameLine();
            if (ui.SmallButton(_configOpen == "kiln" ? "Close Kiln settings" : "Kiln settings")) _configOpen = _configOpen == "kiln" ? null : "kiln";
        }
        (string ModId, Action<UiDraw> Draw, ModContext? Owner, Handle H)[] pages;
        lock (Lock) pages = _configPages;
        if (ui.BeginTable("kiln.mods", "Mod", "Version", "Side", "State", ""))
        {
            foreach (var m in mods)
            {
                ui.NextRow();
                ui.NextColumn();
                ui.Text(m.Name == m.Id ? m.Id : $"{m.Name} ({m.Id})");
                ui.Tooltip(m.Error ?? m.Directory);
                ui.NextColumn();
                ui.Text(m.Version);
                ui.NextColumn();
                ui.Text(m.Side.ToString().ToLowerInvariant());
                ui.NextColumn();
                var c = m.State switch { ModState.Loaded => Color.Green, ModState.Failed => Color.Red, ModState.Disabled => Color.Yellow, _ => Color.Gray };
                ui.Text(c, m.State.ToString().ToLowerInvariant());
                if (m.Error is { } err) ui.Tooltip(err);
                ui.NextColumn();
                if (pages.Any(p => p.ModId == m.Id) || ConfigScreens.HasConfigs(m.Id))  // generated pages: ConfigScreens.cs
                {
                    using (ui.Id(m.Id))
                        if (ui.SmallButton(_configOpen == m.Id ? "Close" : "Config")) _configOpen = _configOpen == m.Id ? null : m.Id;
                }
            }
            ui.EndTable();
        }
        if (_configOpen is { } open)
        {
            ui.Separator($"Config: {open}");
            ConfigScreens.Draw(ui, open);
            foreach (var p in pages.Where(p => p.ModId == open))
                if (!UiModCalls.Invoke(p.Owner, p.Draw, ui, $"config page {open}") && Fault(p.Draw, p.Owner, $"config page {open}")) p.H.Dispose();
        }
        else if (pages.Length == 0)
        {
            ui.TextDisabled("Mods add config pages with Ui.AddConfigPage (the config registry's editor plugs in here).");
        }
    }

    private static void NetworkTab(UiDraw ui)
    {
        var st = Network.Status;
        ui.Text($"Kiln.Network {(st.Available ? "available" : "unavailable")}: roles {(st.ServerRole ? "server " : "")}{(st.ClientRole ? "client" : "")}, " +
                $"max frame {st.MaxFrame} B, channels {st.Channels}");
        ui.Text($"frames in/out {st.FramesIn}/{st.FramesOut}, dropped {st.Dropped}");
        if (Network.Server is { } srv)
        {
            ui.Separator("Server");
            var c = srv.State switch { NetPeerState.Ready => Color.Green, NetPeerState.Rejected => Color.Red, NetPeerState.Vanilla => Color.Yellow, _ => Color.Gray };
            ui.Text(c, srv.State switch
            {
                NetPeerState.Ready => $"Kiln {srv.KilnVersion} on the server",
                NetPeerState.Vanilla => "the server has no Kiln (vanilla)",
                NetPeerState.Rejected => "the server refused this client",
                _ => "handshake in progress",
            });
            if (srv.RoundTrip is { } rtt) ui.Text($"round trip {rtt.TotalMilliseconds:F0} ms");
            foreach (var w in srv.Warnings) ui.Text(Color.Yellow, w);
            if (srv.Mods.Count > 0) ui.TextWrapped("server mods: " + string.Join(", ", srv.Mods.Select(m => $"{m.Id} {m.Version}")));
            SyncLines(ui);
        }
        else
        {
            ui.TextDisabled("Not connected to a server.");
        }
        var peers = Network.Peers;
        if (peers.Count > 0 && ui.CollapsingHeader($"Clients ({peers.Count})", true))
        {
            if (ui.BeginTable("kiln.peers", "Player", "State", "Kiln", "Mods", "RTT"))
            {
                foreach (var p in peers)
                    ui.Row(p.Name, p.State.ToString(), p.KilnVersion ?? "-", p.Mods.Count.ToString(),
                        p.RoundTrip is { } r ? $"{r.TotalMilliseconds:F0} ms" : "-");
                ui.EndTable();
            }
        }
        if (Network.Channels.Count > 0 && ui.CollapsingHeader($"Channels ({Network.Channels.Count})"))
            foreach (var ch in Network.Channels)
                ui.Text($"{ch.Name} v{ch.Version}{(ch.Required ? " (required)" : "")}: in {ch.MessagesIn}, out {ch.MessagesOut}");
    }

    /// <summary>Kiln's server-to-client sync, client side (docs/kiln/sync.md).</summary>
    private static void SyncLines(UiDraw ui)
    {
        try
        {
            var cs = ContentSync.Status;
            var c = cs.State switch { ContentSyncState.Applied => Color.Green, ContentSyncState.Failed => Color.Red, _ => Color.Gray };
            ui.Text(c, cs.State switch
            {
                ContentSyncState.Applied => $"content: {cs.Applied} change(s) from the server{(cs.Reset > 0 ? $", {cs.Reset} own change(s) put back" : "")}" +
                                            $"{(cs.Errors > 0 ? $", {cs.Errors} error(s)" : "")} ({cs.Bytes / 1024.0:F0} KiB)",
                ContentSyncState.Hosting => "content: hosting (the server's content is this game's)",
                ContentSyncState.Waiting => $"content: {cs.Detail}",
                ContentSyncState.Failed => $"content: not synced: {cs.Detail}",
                _ => "content: the server sent no content state",
            });
            var n = ConfigSync.ServerConfigs;
            ui.Text(n > 0 ? Color.Green : Color.Gray, ConfigSync.LastReceived is { } r
                ? $"configs: {r.Configs} synced config(s) from the server, {n} in use here"
                : "configs: the server sent no synced configs");
        }
        catch (Exception)
        {
            // Kiln.Content absent: no content sync line
        }
    }

    private static void KeysTab(UiDraw ui)
    {
        ui.TextDisabled("Click a key, then press the new one (Escape cancels). Saved to kiln/config/keybinds.json.");
        if (!ui.BeginTable("kiln.keys", "Action", "Key", "Default", "")) return;
        void Line(string id, string what, KeyChord key, KeyChord def)
        {
            using var _ = ui.Id(id);
            ui.NextRow();
            ui.NextColumn();
            ui.Text(what);
            ui.Tooltip(id);
            ui.NextColumn();
            if (ui.Button(_remapping == id ? "press a key..." : key.ToString(), 160 * ui.Scale)) _remapping = _remapping == id ? null : id;
            ui.NextColumn();
            ui.TextDisabled(def.ToString());
            ui.NextColumn();
            if (key != def && ui.SmallButton("Reset")) KeyBindings.Reset(id);
        }
        Line(KeyBindings.MenuId, "Kiln: open this menu", KeyBindings.MenuKey, KeyBindings.MenuDefault);
        foreach (var g in KeyBindings.All.GroupBy(b => b.Category))
            foreach (var b in g)
                Line(b.Id, $"{g.Key}: {b.Description}", b.Key, b.Default);
        ui.EndTable();
    }

    private static void OverlayTab(UiDraw ui)
    {
        var s = Ui.Status;
        ui.Text($"{s.Width}x{s.Height}, format {s.Format}, scale {s.Scale:F2}");
        ui.Text($"presents {s.Presents}, drawn {s.FramesDrawn}, last overlay frame {s.FrameMs:F2} ms");
        if (s.Error.Length > 0) ui.Text(Color.Red, s.Error);
        int windows, hud;
        lock (Lock)
        {
            windows = _windows.Length;
            hud = _hud.Length;
        }
        ui.Text($"windows {windows}, HUD lines {hud}, frame handlers {FrameHandlers.Count}");
        var g = Ui.Screens.Current;
        ui.Text($"game screen: {g.Kind} ({g.State}/{g.Hud}), language {g.Language}, UI frame {g.Frame}" +
                (g.Crafting is { } c ? $"; crafting {(c.AtStation ? "station" : "own")} workshop 0x{c.Workshop:x8} recipe 0x{c.Recipe:x8}" : ""));
        ui.Text($"screens open {OpenScreens().Count}, crafting panels {Ui.Crafting.VisiblePanels}, menu extension {(Ui.Crafting.MenuAvailable ? "on" : "off")}");
        if (ui.Button("Test toast")) Hud.Toast("Kiln overlay works", ToastKind.Success);
    }
}

/// <summary>Delegates owned by mods, for the render thread (snapshot iteration).</summary>
internal sealed class OwnedList<T> where T : Delegate
{
    private readonly object _lock = new();
    private (T Fn, ModContext? Owner)[] _items = [];

    public int Count => _items.Length;

    public void Add(T? fn)
    {
        if (fn is null) return;
        var owner = UiModCalls.Caller;
        UiModCalls.Watch(owner);
        lock (_lock) _items = [.. _items, (fn, owner)];
    }

    public void Remove(T? fn)
    {
        if (fn is null) return;
        lock (_lock)
        {
            var i = Array.FindLastIndex(_items, x => x.Fn == fn);
            if (i >= 0) _items = _items.Where((_, k) => k != i).ToArray();
        }
    }

    public void RemoveOwnedBy(ModContext mod)
    {
        lock (_lock) _items = _items.Where(x => !ReferenceEquals(x.Owner, mod)).ToArray();
    }

    public (T Fn, ModContext? Owner)[] Snapshot() => _items;
}

/// <summary>Started by Kiln's service autostart (or the first use).</summary>
internal static class UiStartup
{
    static UiStartup() => UiEngine.EnsureStarted();
}
