// SPDX-License-Identifier: MIT
// A window block, drawn. The plan's rows become widget calls on an IUiCanvas (Kiln.Ui's UiDraw in the game, a recording fake in tests). What a row shows is worked out
// when the window refreshes, every Refresh seconds, from one snapshot of the values it reads; the frames between only issue the widget calls with the strings and numbers
// the refresh left. A closed window is never drawn, so it reads nothing.
using System.Collections.Generic;

namespace Kiln;

/// <summary>The widgets a window block uses, as Kiln.Ui's <see cref="UiDraw"/> has them.</summary>
internal interface IUiCanvas
{
    void Text(string text, Color? color, bool wrap);
    void Disabled(string text);
    bool Button(string label);
    bool Checkbox(string label, ref bool value);
    bool SliderInt(string label, ref int value, int min, int max);
    bool SliderFloat(string label, ref float value, float min, float max);
    bool Combo(string label, ref int index, IReadOnlyList<string> items);
    void Separator(string? heading);
    void SameLine();
    void Tooltip(string text);
    void PushId(string id);
    void PopId();
}

/// <summary><see cref="UiDraw"/> as a canvas.</summary>
internal sealed class UiDrawCanvas : IUiCanvas
{
    private readonly Stack<UiDraw.IdScope> _ids = new();

    /// <summary>The frame being drawn.</summary>
    public UiDraw Ui { get; set; } = null!;

    public void Text(string text, Color? color, bool wrap)
    {
        if (wrap) Ui.TextWrapped(text);
        else if (color is { } c) Ui.Text(c, text);
        else Ui.Text(text);
    }

    public void Disabled(string text) => Ui.TextDisabled(text);
    public bool Button(string label) => Ui.Button(label);
    public bool Checkbox(string label, ref bool value) => Ui.Checkbox(label, ref value);
    public bool SliderInt(string label, ref int value, int min, int max) => Ui.Slider(label, ref value, min, max);
    public bool SliderFloat(string label, ref float value, float min, float max) => Ui.Slider(label, ref value, min, max);
    public bool Combo(string label, ref int index, IReadOnlyList<string> items) => Ui.Combo(label, ref index, items);

    public void Separator(string? heading)
    {
        if (heading is { Length: > 0 }) Ui.Separator(heading);
        else Ui.Separator();
    }

    public void SameLine() => Ui.SameLine();
    public void Tooltip(string text) => Ui.Tooltip(text);
    public void PushId(string id) => _ids.Push(Ui.Id(id));
    public void PopId() => _ids.Pop().Dispose();
}

/// <summary>Where a window block is shown: a Kiln.Ui window or screen.</summary>
internal interface IUiSurface : IDisposable
{
    bool IsOpen { get; }
    void Open();
    void Close();
}

/// <summary>One row's state between refreshes: what to show, and what a widget is holding.</summary>
internal sealed class RowView
{
    public required RowPlan Plan { get; init; }
    public bool Visible { get; set; } = true;
    public string Text { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Tooltip { get; set; }
    public string ValueText { get; set; } = "";
    public bool ReadOnly { get; set; }
    public bool Bool { get; set; }
    public int Int { get; set; }
    public float Float { get; set; }
    public int Index { get; set; }
}

/// <summary>A window block on a client: its Kiln.Ui surface, its menu entries and the state its rows draw from.</summary>
internal sealed class WindowRuntime : IDisposable
{
    private readonly List<IDisposable> _menus = new();
    private IUiSurface? _surface;
    private string _titleLanguage = "";
    private volatile RowView[]? _views;
    private long _nextRefresh;
    private readonly UiLifetime _lifetime;

    public WindowRuntime(WindowPlan plan)
    {
        Plan = plan;
        _lifetime = new UiLifetime(plan.Mod);
        _lifetime.WhenRetired(Release);
    }

    public WindowPlan Plan { get; }

    /// <summary>How many times the rows were worked out (tests: a window refreshes at its pace, not every frame).</summary>
    public int Refreshes { get; private set; }

    public bool IsOpen => _surface is { IsOpen: true };

    internal void WhenRetired(Action cleanup) => _lifetime.WhenRetired(cleanup);
    internal void WhenAlive(Action run) => _lifetime.Run(run);

    /// <summary>Makes the surface and the menu entries. Client, a thread that may use Kiln.Ui (the tick thread, a mod's OnLoad).</summary>
    public void Attach() => _lifetime.Run(AttachCore);

    private void AttachCore()
    {
        var backend = UiRuntime.Backend;
        _titleLanguage = backend.Language;
        _surface = backend.CreateSurface(this, Plan.Title.Render(_titleLanguage, _ => null));
        if (Plan.Block.Menu is { } menu && Plan.MenuText != null)
        {
            void Add(UiMenuIn where)
            {
                if (backend.AddMenu(where, Plan.MenuText.Text, menu.Order, Open) is { } entry) _menus.Add(entry);
            }
            if (menu.In is UiMenuIn.Pause or UiMenuIn.Both) Add(UiMenuIn.Pause);
            if (menu.In is UiMenuIn.Main or UiMenuIn.Both) Add(UiMenuIn.Main);
        }
        if (Plan.Block.OpenAtStart) _surface.Open();
    }

    public void Open() => _lifetime.Run(OpenCore);

    private void OpenCore()
    {
        // a window's title is fixed when it is made: one made in another language is made again
        if (Plan.Block.Kind == WindowKind.Window && _surface != null && !_surface.IsOpen && UiRuntime.Backend.Language != _titleLanguage)
        {
            _surface.Dispose();
            _titleLanguage = UiRuntime.Backend.Language;
            _surface = UiRuntime.Backend.CreateSurface(this, Plan.Title.Render(_titleLanguage, _ => null));
        }
        // what it shows is read fresh when it opens: the next frame refreshes (this may be the window thread, and the render thread is drawing)
        Volatile.Write(ref _nextRefresh, 0);
        _surface?.Open();
    }

    public void Close() => _lifetime.Run(() => _surface?.Close());

    public void Dispose() => _lifetime.Dispose();

    private void Release()
    {
        foreach (var m in _menus) m.Dispose();
        _menus.Clear();
        _surface?.Dispose();
        _surface = null;
    }

    // ---- frames (render thread) ----

    /// <summary>One frame: refresh when it is time, then the visible rows as widgets.</summary>
    public void Draw(IUiCanvas c) => _lifetime.Run((Window: this, Canvas: c), static state => state.Window.DrawCore(state.Canvas));

    private void DrawCore(IUiCanvas c)
    {
        var now = UiRuntime.Clock();
        var views = _views;
        if (views == null || now >= Volatile.Read(ref _nextRefresh))
        {
            Refresh(now);
            views = _views!;
        }
        var drawn = false;
        foreach (var v in views)
        {
            if (!v.Visible) continue;
            if (v.Plan.SameLine && drawn) c.SameLine();
            c.PushId(v.Plan.Id);
            try
            {
                DrawRow(c, v);
                if (v.Tooltip != null) c.Tooltip(v.Tooltip);
            }
            finally { c.PopId(); }
            drawn = true;
        }
    }

    private void DrawRow(IUiCanvas c, RowView v)
    {
        var p = v.Plan;
        switch (p.Type)
        {
            case "text":
                c.Text(v.Text, p.Color, p.Wrap);
                break;
            case "value":
                c.Text(v.Text, null, false);
                c.SameLine();
                c.Text(v.ValueText, Color.Cyan, false);
                break;
            case "toggle" when v.ReadOnly:
            case "slider" when v.ReadOnly:
            case "choice" when v.ReadOnly:
                c.Disabled(v.Text);
                break;
            case "toggle":
            {
                var b = v.Bool;
                if (c.Checkbox(v.Label, ref b))
                {
                    v.Bool = b;
                    UiEdits.Set(p.Bind!, b, immediate: true, _lifetime);
                }
                break;
            }
            case "slider":
                if (p.IsInt)
                {
                    var i = v.Int;
                    if (c.SliderInt(v.Label, ref i, (int)p.Min, (int)p.Max))
                    {
                        v.Int = i;
                        UiEdits.Set(p.Bind!, (double)i, immediate: false, _lifetime);
                    }
                }
                else
                {
                    var f = v.Float;
                    if (c.SliderFloat(v.Label, ref f, (float)p.Min, (float)p.Max))
                    {
                        v.Float = f;
                        UiEdits.Set(p.Bind!, Math.Clamp((double)f, p.Min, p.Max), immediate: false, _lifetime);
                    }
                }
                break;
            case "choice":
            {
                var index = v.Index;
                if (c.Combo(v.Label, ref index, p.Options))
                {
                    v.Index = index;
                    UiEdits.Set(p.Bind!, p.Options[Math.Clamp(index, 0, p.Options.Count - 1)], immediate: true, _lifetime);
                }
                break;
            }
            case "separator":
                c.Separator(v.Text.Length > 0 ? v.Text : null);
                break;
            case "button":
                if (c.Button(v.Label)) UiActions.Run(p.Does!, Plan.Mod, $"window {Plan.Name}", _lifetime);
                break;
        }
    }

    /// <summary>Reads every value the rows need once, and works out what each row shows.</summary>
    internal void Refresh(long now)
    {
        Refreshes++;
        Volatile.Write(ref _nextRefresh, now + (long)(Plan.Block.Refresh * 1000));
        var language = UiRuntime.Backend.Language;
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var b in Plan.Reads) snapshot[b.Name] = UiEdits.Pending(b) ?? UiBindings.Read(b);
        object? Value(UiBinding b) => snapshot.TryGetValue(b.Name, out var x) ? x : null;
        var views = _views ??= Plan.Rows.Select(r => new RowView { Plan = r }).ToArray();
        foreach (var v in views)
        {
            var p = v.Plan;
            v.Visible = p.When?.Test(Value) ?? true;
            if (!v.Visible) continue;
            v.Tooltip = p.Tooltip?.Render(language, Value);
            v.Label = p.Label?.Render(language, Value) ?? "";
            v.Text = p.Text?.Render(language, Value) ?? "";
            if (p.Bind == null) continue;
            var value = Value(p.Bind);
            v.ValueText = UiBindings.Text(value, p.Format);
            if (p.Type is "toggle" or "slider" or "choice") v.ReadOnly = UiEdits.ReadOnly(p.Bind);
            // what the frames draw as it is: no string is made per frame
            if (p.Type == "value") v.Text = v.Label + ":";
            else if (v.ReadOnly) v.Text = $"{v.Label}: {v.ValueText}";
            switch (p.Type)
            {
                case "toggle":
                    v.Bool = value is true;
                    break;
                case "slider" when p.IsInt:
                    v.Int = value is double di ? (int)Math.Clamp(Math.Round(di), p.Min, p.Max) : (int)p.Min;
                    break;
                case "slider":
                    v.Float = value is double df ? (float)Math.Clamp(df, p.Min, p.Max) : (float)p.Min;
                    break;
                case "choice":
                    v.Index = value is string s ? Math.Max(0, p.Options.ToList().FindIndex(o => string.Equals(o, s, StringComparison.OrdinalIgnoreCase))) : 0;
                    break;
            }
        }
    }
}
