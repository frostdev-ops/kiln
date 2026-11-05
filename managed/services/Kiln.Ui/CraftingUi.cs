// SPDX-License-Identifier: MIT
// Crafting menus (Forge: container screens + ScreenEvent.Render to draw next to them):
//  - tabs and subcategories of the game's own crafting menu (kiln.craftmenu, implemented by kiln.content's hook on the
//    menu's category lookup; the game draws them itself, with its look, focus and gamepad navigation);
//  - info panels next to the open crafting menu (Kiln's overlay, game look), knowing the station, the workshop and
//    the selected recipe; their buttons take the mouse while it is over them, the keyboard stays the game's.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>Where a crafting panel sits.</summary>
public enum CraftingPanelAnchor
{
    /// <summary>Along the right edge of the screen (the crafting menu's details are on the left and centre).</summary>
    Right,
    Left,
    /// <summary>Above the bottom edge, centred.</summary>
    Bottom,
}

/// <summary>Options of a crafting panel.</summary>
public sealed record CraftingPanelOptions
{
    public CraftingPanelAnchor Anchor { get; init; } = CraftingPanelAnchor.Right;
    /// <summary>Width in pixels at scale 1 (height fits the contents).</summary>
    public float Width { get; init; } = 340;
    public UiStyle Style { get; init; } = UiStyle.Game;
    /// <summary>Show only when this returns true (e.g. one workshop); null = every crafting menu.</summary>
    public Func<CraftingScreen, bool>? When { get; init; }
    /// <summary>Buttons in the panel take the mouse while it is over the panel.</summary>
    public bool Interactive { get; init; } = true;
}

/// <summary>A tab or subcategory Kiln added to the game's crafting menu.</summary>
public sealed class CraftingMenuAddition : IDisposable
{
    internal ulong Handle;
    internal ModContext? Owner;
    public uint Workshop { get; internal init; }
    public bool IsTab { get; internal init; }

    /// <summary>Takes it out of the menu (the menu shows the change when it next rebuilds its lists: reopen it).</summary>
    public unsafe void Dispose()
    {
        if (Handle == 0) return;
        if (UiNative.Craft != null) UiNative.Craft->Remove(Handle);
        Ui.Crafting.Forget(this);
        Handle = 0;
    }
}

public static unsafe partial class Ui
{
    /// <summary>The game's crafting menus: <c>Ui.Crafting.Current</c>, <c>AddPanel</c>, <c>AddTab</c>, <c>AddSubcategory</c>.</summary>
    public static class Crafting
    {
        private sealed class Panel(UiText title, Action<UiDraw, CraftingScreen> draw, CraftingPanelOptions options, ModContext? owner)
        {
            public readonly UiText Title = title;
            public readonly Action<UiDraw, CraftingScreen> Draw = draw;
            public readonly CraftingPanelOptions Options = options;
            public readonly ModContext? Owner = owner;
            public readonly string Id = $"###kiln.craft{Interlocked.Increment(ref _ids)}";
            public bool Disposed;
        }

        private static int _ids;
        private static readonly object Lock = new();
        private static Panel[] _panels = [];
        private static readonly List<CraftingMenuAddition> Additions = new();
        private static volatile bool _visible;
        private static StrongBox<CraftingScreen> _current = new(default);

        /// <summary>The crafting menu open now (station or own, workshop, selected recipe), or null.</summary>
        public static CraftingScreen? Current => Screens.Current.Crafting;

        /// <summary>The menu extension works on this build (kiln.craftmenu: the client's kiln.content).</summary>
        public static bool MenuAvailable => UiNative.Craft != null;

        /// <summary>A panel drawn next to every open crafting menu (or those <see cref="CraftingPanelOptions.When"/>
        /// picks), with the menu's <see cref="CraftingScreen"/>. Render thread, every frame while shown. Owned by the
        /// calling mod; Dispose removes it.</summary>
        public static IDisposable AddPanel(UiText title, Action<UiDraw, CraftingScreen> draw, CraftingPanelOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(draw);
            UiEngine.EnsureStarted();
            var owner = UiModCalls.Caller;
            UiModCalls.Watch(owner);
            var p = new Panel(title, draw, options ?? new CraftingPanelOptions(), owner);
            lock (Lock) _panels = [.. _panels, p];
            Recount();
            return new UiEngine.Handle(() =>
            {
                p.Disposed = true;
                lock (Lock) _panels = _panels.Where(x => x != p).ToArray();
                Recount();
            });
        }

        /// <summary>A new tab in the crafting menu of <paramref name="workshop"/> (keen::WorkshopId; 0 = the player's own
        /// crafting), with one subcategory holding <paramref name="recipes"/> (recipe ids; the player sees the ones
        /// unlocked for them). Its icon is the one of tab <paramref name="iconFromTab"/> (-1 = the last). The game sizes
        /// its menu by the largest tab count in its data, so a workshop already at that count gets no new tab (null).
        /// Shown when the menu next rebuilds its lists (open it again). Owned by the calling mod.</summary>
        public static CraftingMenuAddition? AddTab(uint workshop, UiText label, IEnumerable<uint> recipes, int iconFromTab = -1) =>
            Add(true, workshop, iconFromTab, label, recipes);

        /// <summary>A new subcategory with its own label at the end of tab <paramref name="tab"/> (index, -1 = the last)
        /// of <paramref name="workshop"/>'s crafting menu. Recipes in it are not also placed automatically.</summary>
        public static CraftingMenuAddition? AddSubcategory(uint workshop, int tab, UiText label, IEnumerable<uint> recipes) =>
            Add(false, workshop, tab, label, recipes);

        /// <summary>The menu configuration and Kiln's additions as JSON (0 = every workshop).</summary>
        public static string Describe(uint workshop = 0)
        {
            var t = UiNative.Craft;
            if (t == null) return "{\"menu\":false}";
            nuint len = 0;
            t->Describe(workshop, null, 0, &len);
            var buf = new byte[(int)len + 1];
            fixed (byte* p = buf)
                if (t->Describe(workshop, p, (nuint)buf.Length, &len) != UiNative.Ok) return "{}";
            return Encoding.UTF8.GetString(buf, 0, (int)len);
        }

        private static CraftingMenuAddition? Add(bool tab, uint workshop, int index, UiText label, IEnumerable<uint> recipes)
        {
            ArgumentNullException.ThrowIfNull(label);
            ArgumentNullException.ThrowIfNull(recipes);
            var t = UiNative.Craft;
            if (t == null) return null;
            var ids = recipes.Distinct().ToArray();
            ulong h = 0;
            int rc;
            fixed (uint* p = ids)
                rc = tab ? t->TabAdd(0, workshop, index, Utf8.Of(label.ToJson()), p, (uint)ids.Length, &h)
                         : t->SetAdd(0, workshop, index, Utf8.Of(label.ToJson()), p, (uint)ids.Length, &h);
            if (rc != UiNative.Ok)
            {
                UiEngine.Log(LogLevel.Warn, $"crafting menu {(tab ? "tab" : "subcategory")} '{label}' for workshop 0x{workshop:x8} refused: rc {rc}");
                return null;
            }
            var a = new CraftingMenuAddition { Handle = h, Owner = UiModCalls.Caller, Workshop = workshop, IsTab = tab };
            UiModCalls.Watch(a.Owner);
            lock (Lock) Additions.Add(a);
            return a;
        }

        internal static void Forget(CraftingMenuAddition a)
        {
            lock (Lock) Additions.Remove(a);
        }

        internal static void RemoveOwnedBy(ModContext mod)
        {
            CraftingMenuAddition[] mine;
            lock (Lock)
            {
                mine = Additions.Where(a => ReferenceEquals(a.Owner, mod)).ToArray();
                _panels = _panels.Where(p => !ReferenceEquals(p.Owner, mod)).ToArray();
            }
            foreach (var a in mine) a.Dispose();
            Recount();
        }

        // UI thread (Screens.OnScreen)
        internal static void ScreenChanged(GameScreen prev, GameScreen cur)
        {
            Volatile.Write(ref _current, new StrongBox<CraftingScreen>(cur.Crafting ?? default));
            var was = _visible;
            _visible = cur.Crafting is not null;
            if (was != _visible) Recount();
        }

        internal static int VisiblePanels
        {
            get
            {
                if (!_visible) return 0;
                Panel[] ps;
                lock (Lock) ps = _panels;
                return ps.Length;
            }
        }

        internal static int InteractivePanels
        {
            get
            {
                if (!_visible) return 0;
                Panel[] ps;
                lock (Lock) ps = _panels;
                return ps.Count(p => p.Options.Interactive);
            }
        }

        private static void Recount() => UiEngine.Recount();

        // render thread
        internal static void DrawPanels(UiDraw ui)
        {
            if (!_visible) return;
            Panel[] ps;
            lock (Lock) ps = _panels;
            if (ps.Length == 0) return;
            var c = Volatile.Read(ref _current).Value;
            var s = ui.Scale;
            var y = new Dictionary<CraftingPanelAnchor, float>();
            foreach (var p in ps)
            {
                if (p.Disposed) continue;
                var o = p.Options;
                if (o.When is { } when)
                {
                    bool show;
                    try { show = when(c); }
                    catch (Exception) { show = false; }
                    if (!show) continue;
                }
                var w = o.Width * s;
                y.TryGetValue(o.Anchor, out var used);
                var (x, py, px, ppy) = o.Anchor switch
                {
                    CraftingPanelAnchor.Left => (24 * s, ui.Height * 0.18f + used, 0f, 0f),
                    CraftingPanelAnchor.Bottom => (ui.Width / 2, ui.Height - 24 * s - used, 0.5f, 1f),
                    _ => (ui.Width - 24 * s, ui.Height * 0.18f + used, 1f, 0f),
                };
                // placed once (then the player may drag it; ImGui keeps the spot in kiln/data/kiln.ui/imgui.ini)
                ui.NextWindowPos(x, py, 4 /* first use ever */, px, ppy);
                ui.NextWindowSize(w, 0, 1);
                uint flags = (1u << 1) | (1u << 5) | (1u << 6) | (1u << 12); // no resize/collapse, auto-size, no focus on appearing
                if (!o.Interactive) flags |= (1u << 9) | (1u << 18) | (1u << 19);
                ui.PushStyleRaw(o.Style);
                var open = true;
                if (ui.BeginWindowNoClose($"{p.Title}{p.Id}", flags))
                {
                    if (!UiModCalls.Invoke(p.Owner, x2 => p.Draw(x2, c), ui, $"crafting panel '{p.Title}'")) p.Disposed = true;
                    var (_, _, _, hh) = ui.WindowRect;
                    y[o.Anchor] = used + hh + 10 * s;
                }
                ui.EndWindow();
                ui.PopStyleRaw();
                _ = open;
            }
        }
    }
}
