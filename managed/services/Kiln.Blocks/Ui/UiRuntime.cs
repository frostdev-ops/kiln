// SPDX-License-Identifier: MIT
// UI blocks on a player's game. Each block, once committed, becomes a Kiln.Ui object here:
//   window     Ui.Window or Ui.Screen (drawn only while open), and entries in the pause and main menus
//   HUD line   Hud.Show while its condition holds, Hud.Remove when it does not; its text is worked out at its own rate by Pump
//   key        KeyBindings.Register under <mod>:<name>, so the Kiln menu's Keys tab can rebind it
// On a dedicated server, or where Kiln.Ui has no overlay, none of this runs: Register notes the block and returns, and nothing is drawn, bound or read.
// The service tick (UiRuntime.Pump) does the work that is not a frame: HUD lines, queued commands and config writes. It exists only once something was registered.
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Kiln;

/// <summary>A block's callbacks and queued work stop together, before its objects are removed.</summary>
internal sealed class UiLifetime : IDisposable
{
    private readonly object _gate = new();
    private bool _active = true;
    private Func<bool>? _ownerAlive;
    private IDisposable? _watch;
    private Action? _cleanup;

    internal static Func<string, Action, (Func<bool> Alive, IDisposable Watch)?> TrackOwner = BlocksBridge.TrackUiOwner;

    public UiLifetime(string mod)
    {
        if (TrackOwner(mod, Dispose) is { } owner)
        {
            lock (_gate)
            {
                if (_active) { _ownerAlive = owner.Alive; _watch = owner.Watch; }
                else owner.Watch.Dispose();
            }
        }
    }

    public void WhenRetired(Action cleanup)
    {
        lock (_gate)
        {
            if (_active) _cleanup += cleanup;
            else cleanup();
        }
    }

    public void Run(Action action)
        => Run(action, static run => run());

    public void Run<T>(T arg, Action<T> action)
    {
        lock (_gate)
        {
            if (!_active) return;
            if (_ownerAlive?.Invoke() == false) { Dispose(); return; }
            action(arg);
        }
    }

    public void Dispose()
    {
        Action? cleanup;
        IDisposable? watch;
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            cleanup = _cleanup;
            _cleanup = null;
            _ownerAlive = null;
            watch = _watch;
            _watch = null;
        }
        watch?.Dispose();
        UiActions.Forget(this);
        UiEdits.Forget(this);
        foreach (var action in cleanup?.GetInvocationList() ?? [])
            try { ((Action)action)(); }
            catch (Exception e) when (e is not OutOfMemoryException) { BlocksBridge.Log(LogLevel.Warn, $"blocks: ui cleanup: {e.Message}"); }
    }
}

/// <summary>A HUD line on the screen.</summary>
internal interface IUiHud
{
    string Text { get; set; }
    void Remove();
}

/// <summary>What UI blocks use of the game: Kiln.Ui and what its actions reach. The game's in <see cref="KilnUiBackend"/>; tests replace it.</summary>
internal interface IUiBackend
{
    /// <summary>A client with an overlay: windows can be drawn.</summary>
    bool Available { get; }
    /// <summary>The game's text language key.</summary>
    string Language { get; }
    /// <summary>This game joined a server: a server config is not its own to change.</summary>
    bool JoinedServer { get; }
    IUiSurface CreateSurface(WindowRuntime window, string title);
    IDisposable? AddMenu(UiMenuIn where, UiText text, int order, Action onClick);
    IUiHud ShowHud(string id, string text, HudAnchor anchor, Color color);
    IDisposable? BindKey(KeyPlan plan, string category, Action onPress);
    void Toast(string text, ToastKind kind);
    void RequestCast(string abilityId);
    (bool Ok, string Message) RunCommand(string line);
}

internal static class UiRuntime
{
    private sealed class ModUi
    {
        public readonly Dictionary<string, WindowRuntime> Windows = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HudState> Huds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, KeyState> Keys = new(StringComparer.Ordinal);
    }

    private sealed class KeyState : IDisposable
    {
        private readonly UiLifetime _lifetime;
        public KeyState(UiLifetime lifetime, IDisposable handle) { _lifetime = lifetime; lifetime.WhenRetired(handle.Dispose); }
        public void Dispose() => _lifetime.Dispose();
    }

    private sealed class HudState(HudPlan plan)
    {
        public readonly HudPlan Plan = plan;
        public readonly UiLifetime Lifetime = new(plan.Mod);
        public IUiHud? Line;
        public long NextDue;
        public bool Faulted;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ModUi> Mods_ = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Noted = new(StringComparer.Ordinal);
    private static HudState[] _huds = [];
    private static IDisposable? _tick;

    /// <summary>The game's Kiln.Ui, or a fake in tests.</summary>
    internal static IUiBackend Backend = new KilnUiBackend();

    /// <summary>Milliseconds, for refresh rates and edits. Tests replace it.</summary>
    internal static Func<long> Clock = static () => Environment.TickCount64;

    internal static Func<Action<ulong>, IDisposable> SubscribeTick = static tick => BlocksBridge.AddTick("kiln blocks ui", tick);

    /// <summary>Whether blocks are drawn here: a client with an overlay.</summary>
    public static bool Active => Backend.Available;

    private static void Problem(string mod, string text)
    {
        BlocksBridge.Log(LogLevel.Error, $"blocks: {mod}: ui: {text}");
        if (Blocks.AllMods.FirstOrDefault(m => m.Id == mod) is { } m)
        {
            lock (m.Errors) m.Errors.Add(text);
            m.ReportDirty = true;
        }
    }

    /// <summary>Said once per block where nothing is drawn: a dedicated server (the usual case of a mod that runs on both sides), or no overlay.</summary>
    private static void Inert(string mod, string what)
    {
        lock (Gate)
            if (!Noted.Add($"{mod}:{what}")) return;
        BlocksBridge.Log(LogLevel.Info, $"blocks: {mod}: ui: {what} kept, nothing drawn or bound here ({(BlocksBridge.IsClient ? "no overlay on this client" : "a dedicated server")})");
    }

    private static ModUi Of(string mod)
    {
        if (!Mods_.TryGetValue(mod, out var m)) Mods_[mod] = m = new ModUi();
        return m;
    }

    public static WindowRuntime? FindWindow(string mod, string name)
    {
        lock (Gate) return Mods_.TryGetValue(mod, out var m) && m.Windows.TryGetValue(name, out var w) ? w : null;
    }

    private static void Retired(string mod, Action<ModUi> remove)
    {
        lock (Gate)
        {
            if (Mods_.TryGetValue(mod, out var m))
            {
                remove(m);
                if (m.Windows.Count + m.Huds.Count + m.Keys.Count == 0) Mods_.Remove(mod);
            }
            _huds = [.. Mods_.Values.SelectMany(x => x.Huds.Values)];
            if (Mods_.Count == 0) { _tick?.Dispose(); _tick = null; }
        }
    }

    // ---- registering (the tick thread, from a block's commit) ----------------------------------------------------------

    public static void Register(WindowPlan plan)
    {
        if (!Active)
        {
            Inert(plan.Mod, $"window {plan.Name}");
            return;
        }
        var rt = new WindowRuntime(plan);
        WindowRuntime? old;
        lock (Gate) Of(plan.Mod).Windows.Remove(plan.Name, out old);
        old?.Dispose();
        rt.WhenRetired(() => Retired(plan.Mod, m =>
        {
            if (m.Windows.TryGetValue(plan.Name, out var current) && ReferenceEquals(current, rt)) m.Windows.Remove(plan.Name);
        }));
        try
        {
            rt.Attach();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            rt.Dispose();
            Problem(plan.Mod, $"window {plan.Name}: {e.Message}");
            return;
        }
        rt.WhenAlive(() => { lock (Gate) Of(plan.Mod).Windows[plan.Name] = rt; });
        EnsureTick();
        BlocksBridge.Log(LogLevel.Info, $"blocks: {plan.Mod}: ui: window {plan.Name} ready ({plan.Block.Kind.ToString().ToLowerInvariant()}, {plan.Rows.Count} row(s), refresh {plan.Block.Refresh}s)");
    }

    public static void Register(HudPlan plan)
    {
        if (!Active)
        {
            Inert(plan.Mod, $"HUD line {plan.Name}");
            return;
        }
        HudState? old;
        lock (Gate) Of(plan.Mod).Huds.Remove(plan.Name, out old);
        old?.Lifetime.Dispose();
        var state = new HudState(plan);
        state.Lifetime.WhenRetired(() => { state.Line?.Remove(); state.Line = null; });
        state.Lifetime.WhenRetired(() => Retired(plan.Mod, m =>
        {
            if (m.Huds.TryGetValue(plan.Name, out var current) && ReferenceEquals(current, state)) m.Huds.Remove(plan.Name);
        }));
        state.Lifetime.Run(() =>
        {
            lock (Gate)
            {
                Of(plan.Mod).Huds[plan.Name] = state;
                _huds = [.. Mods_.Values.SelectMany(x => x.Huds.Values)];
            }
        });
        EnsureTick();
        BlocksBridge.Log(LogLevel.Info, $"blocks: {plan.Mod}: ui: HUD line {plan.Name} ready ({plan.Block.Anchor}, refresh {plan.Block.Refresh}s)");
    }

    public static void Register(KeyPlan plan)
    {
        if (!Active)
        {
            Inert(plan.Mod, $"key binding {plan.Name}");
            return;
        }
        KeyState? old;
        lock (Gate) Of(plan.Mod).Keys.Remove(plan.Name, out old);
        old?.Dispose();
        var category = plan.Block.Category ?? ModName(plan.Mod);
        var lifetime = new UiLifetime(plan.Mod);
        try
        {
            IDisposable? key = null;
            lifetime.Run(() => key = Backend.BindKey(plan, category, () => UiActions.Run(plan.Does, plan.Mod, $"key {plan.Id}", lifetime)));
            if (key != null)
            {
                var state = new KeyState(lifetime, key);
                lifetime.WhenRetired(() => Retired(plan.Mod, m =>
                {
                    if (m.Keys.TryGetValue(plan.Name, out var current) && ReferenceEquals(current, state)) m.Keys.Remove(plan.Name);
                }));
                lifetime.Run(() => { lock (Gate) Of(plan.Mod).Keys[plan.Name] = state; });
            }
            else lifetime.Dispose();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
        {
            lifetime.Dispose();
            Problem(plan.Mod, $"key binding {plan.Name}: {e.Message}");
            return;
        }
        EnsureTick();
        BlocksBridge.Log(LogLevel.Info, $"blocks: {plan.Mod}: ui: key {plan.Id} bound to {plan.Block.Key.Trim()}");
    }

    private static string ModName(string mod)
    {
        try { return Kiln.Mods.Get(mod)?.Name ?? mod; }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException) { return mod; }
    }

    /// <summary>Takes away everything a mod's UI blocks made: it is reloading or gone. Safe to call for a mod with none.</summary>
    public static void Forget(string mod)
    {
        UiCompile.Forget(mod);
        ModUi? m;
        lock (Gate)
        {
            Noted.RemoveWhere(n => n.StartsWith(mod + ":", StringComparison.Ordinal));
            if (!Mods_.Remove(mod, out m)) return;
            _huds = [.. Mods_.Values.SelectMany(x => x.Huds.Values)];
        }
        foreach (var w in m.Windows.Values) w.Dispose();
        foreach (var h in m.Huds.Values) h.Lifetime.Dispose();
        foreach (var k in m.Keys.Values) k.Dispose();
        lock (Gate)
            if (Mods_.Values.All(m => m.Windows.Count + m.Huds.Count + m.Keys.Count == 0))
            {
                _tick?.Dispose();
                _tick = null;
            }
    }

    // ---- the service tick (tick thread) --------------------------------------------------------------------------------

    private static void EnsureTick()
    {
        lock (Gate) if (Mods_.Count > 0) _tick ??= SubscribeTick(_ => Pump());
    }

    /// <summary>Queued commands, due config writes, and the HUD lines that are due. Cheap when nothing is: a clock read and a look at each line's due time.</summary>
    internal static void Pump()
    {
        var now = Clock();
        UiActions.Drain();
        UiEdits.Flush(now);
        var huds = _huds;
        for (var i = 0; i < huds.Length; i++)
            if (now >= huds[i].NextDue)
            {
                var h = huds[i];
                h.Lifetime.Run((Hud: h, Now: now), static state => RefreshHud(state.Hud, state.Now));
            }
    }

    private static void RefreshHud(HudState h, long now)
    {
        var p = h.Plan;
        h.NextDue = now + (long)(p.Block.Refresh * 1000);
        if (h.Faulted) return;
        try
        {
            var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var b in p.Text.Bindings.Concat(p.When?.Bindings ?? []).DistinctBy(b => b.Name)) snapshot[b.Name] = UiBindings.Read(b);
            object? Value(UiBinding b) => snapshot.TryGetValue(b.Name, out var x) ? x : null;
            var text = p.When?.Test(Value) ?? true ? p.Text.Render(Backend.Language, Value) : "";
            if (text.Length == 0)
            {
                // hidden costs nothing: no line, so no demand on the overlay
                h.Line?.Remove();
                h.Line = null;
            }
            else if (h.Line == null) h.Line = Backend.ShowHud(p.Id, text, p.Block.Anchor, p.Color);
            else if (h.Line.Text != text) h.Line.Text = text;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            h.Faulted = true;
            h.Line?.Remove();
            h.Line = null;
            Problem(p.Mod, $"HUD line {p.Name}: {e.Message} (switched off)");
        }
    }

    /// <summary>Tests: forget everything and use a fake game.</summary>
    internal static void ResetForTests(IUiBackend backend)
    {
        string[] mods;
        lock (Gate) mods = [.. Mods_.Keys];
        foreach (var m in mods) Forget(m);
        UiActions.Clear();
        UiEdits.Clear();
        lock (Gate) Noted.Clear();
        Backend = backend;
        Clock = static () => Environment.TickCount64;
    }
}

/// <summary>Kiln.Ui, for real. Every call here is on a client with an overlay; nothing else reaches it.</summary>
internal sealed class KilnUiBackend : IUiBackend
{
    public bool Available => Probe();

    // Kiln.Ui is in every host folder, a dedicated server's too; the call is kept apart so that a host without it never loads the type
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Probe()
    {
        try { return BlocksBridge.IsClient && Ui.Available; }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or InvalidOperationException) { return false; }
    }

    public string Language
    {
        get
        {
            try { return GameLanguage.Current; }
            catch (Exception e) when (e is InvalidOperationException or TypeLoadException) { return "en-us"; }
        }
    }

    public bool JoinedServer
    {
        get
        {
            try { return Client.Available && Client.IsClient && Client.IsRemote; }
            catch (Exception e) when (e is InvalidOperationException or TypeLoadException) { return false; }
        }
    }

    public IUiSurface CreateSurface(WindowRuntime w, string title)
    {
        IUiSurface? surface = null;
        // A different mod's key or button may open this window after a language change. Its new surface still
        // belongs to the original window's live owner, so retiring the caller cannot take the window away.
        if (!BlocksBridge.RunAsUiOwner(w.Plan.Mod, () => surface = CreateOwnedSurface(w, title)))
            throw new InvalidOperationException($"mod {w.Plan.Mod} is unloading");
        return surface!;
    }

    private static IUiSurface CreateOwnedSurface(WindowRuntime w, string title)
    {
        var b = w.Plan.Block;
        var canvas = new UiDrawCanvas();
        void Draw(UiDraw ui)
        {
            canvas.Ui = ui;
            w.Draw(canvas);
        }
        if (b.Kind == WindowKind.Screen)
        {
            var screen = Ui.Screen(new UiText(title), Draw, new UiScreenOptions
            {
                Style = UiStyle.Game, Width = b.Width > 0 ? b.Width : 720, Height = b.Height > 0 ? b.Height : 520, DimBackground = b.Dim ?? true, TitleBar = b.TitleBar,
            });
            // the title follows the game's language, as a screen's does
            screen.Title = w.Plan.Title.Text;
            return new ScreenSurface(screen);
        }
        var window = Ui.Window(title, Draw, new UiWindowOptions
        {
            Interactive = b.Interactive, StartOpen = false, Width = b.Width, Height = b.Height, Position = b.Position is { Length: 2 } p ? (p[0], p[1]) : null,
            AutoResize = b.AutoResize, TitleBar = b.TitleBar, BackgroundAlpha = b.Alpha,
        });
        return new WindowSurface(window);
    }

    public IDisposable? AddMenu(UiMenuIn where, UiText text, int order, Action onClick) =>
        Ui.Menus.Add(where == UiMenuIn.Main ? MenuLocation.MainMenu : MenuLocation.PauseMenu, text, onClick, order);

    public IUiHud ShowHud(string id, string text, HudAnchor anchor, Color color) => new HudSurface(Hud.Show(id, text, anchor, color));

    public IDisposable? BindKey(KeyPlan plan, string category, Action onPress)
    {
        var description = plan.Block.Description?.ToString() ?? plan.Name;
        var b = KeyBindings.Register(plan.Id, plan.Block.Key.Trim(), onPress, description, category);
        b.OnGameScreens = plan.Block.OnGameScreens;
        b.WhileCapturing = plan.Block.WhileCapturing;
        return b;
    }

    public void Toast(string text, ToastKind kind) => Hud.Toast(text, kind, 3.5);

    public void RequestCast(string abilityId) => AbilityRegistry.RequestCast(abilityId);

    public (bool Ok, string Message) RunCommand(string line)
    {
        var r = Commands.Execute(ConsoleControl.DefaultSource(), line);
        if (r.Ok) return (true, r.Output.Count > 0 ? r.Output[0] : "");
        return (false, r.Error ?? $"{line} failed");
    }

    private sealed class WindowSurface(UiWindow window) : IUiSurface
    {
        public bool IsOpen => window.Open;
        public void Open() => window.Open = true;
        public void Close() => window.Open = false;
        public void Dispose() => window.Dispose();
    }

    private sealed class ScreenSurface(UiScreen screen) : IUiSurface
    {
        public bool IsOpen => screen.IsOpen;
        public void Open() => screen.Open();
        public void Close() => screen.Close();
        public void Dispose() => screen.Dispose();
    }

    private sealed class HudSurface(HudLine line) : IUiHud
    {
        public string Text
        {
            get => line.Text;
            set => line.Text = value;
        }

        public void Remove() => line.Dispose();
    }
}
