// SPDX-License-Identifier: MIT
// UI blocks (docs/kiln/modding/ui-blocks.md) without a game: the records and their JSON, the compile (every mistake by block and field), the names a field takes, the small
// languages for templates and conditions, and the runtime driven through a recording canvas and a fake Kiln.Ui: refresh rates, actions, the HUD pump, a server where nothing runs.
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

[Collection("ui blocks")]
public sealed class UiBlockTests : IDisposable
{
    private const string Mod = "ui-test";

    // ---- fakes --------------------------------------------------------------------------------------------------------

    private sealed class FakeConfig(string id, bool server, params ConfigEntry[] entries) : IUiConfig
    {
        public Dictionary<string, object?> Values { get; } = entries.ToDictionary(e => e.Path, e => e.Value);
        public HashSet<string> FromServer { get; } = new();
        public List<(string Path, object? Value)> Sets { get; } = new();
        public string Id => id;
        public bool ServerKind => server;
        public IReadOnlyList<ConfigEntry> Entries => entries;
        public object? Get(string path) => Values.TryGetValue(path, out var v) ? v : throw new KeyNotFoundException(path);
        public bool IsFromServer(string path) => FromServer.Contains(path);

        public bool TrySet(string path, object? value, out string error)
        {
            Sets.Add((path, value));
            if (path == "refuses") { error = "refused: not today"; return false; }
            Values[path] = value;
            error = "";
            return true;
        }
    }

    private static ConfigEntry Entry(string path, string type, object? value, double? min = null, double? max = null, params string[] options) =>
        new(path, type, null, value, value, value, min, max, options, false, false);

    private sealed class FakeSurface(FakeBackend owner, WindowRuntime window, string title) : IUiSurface
    {
        public string Title => title;
        public WindowRuntime Window => window;
        public bool IsOpen { get; private set; }
        public bool Disposed { get; private set; }
        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void Dispose() { Disposed = true; owner.Surfaces.Remove(this); }
    }

    private sealed class FakeHud(FakeBackend owner, string id, string text, HudAnchor anchor, Color color) : IUiHud
    {
        public string Id => id;
        public HudAnchor Anchor => anchor;
        public Color Color => color;
        public string Text { get; set; } = text;
        public void Remove() => owner.Huds.Remove(this);
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class SnapshotMods(ModEntry entry) : IModsBackend
    {
        public IReadOnlyList<ModEntry> All => [entry];
        public ModSide Side => ModSide.Client;
        public ModEntry? Get(string id) => id == entry.Id ? entry : null;
        public ModStats Stats(string id) => default;
        public bool Send(ModContext? sender, string targetId, string method, object? payload) => false;
        public Task<ModOperationResult> Request(ModOperation op, string arg) => throw new NotSupportedException();
        public void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller) => throw new NotSupportedException();
    }

    private sealed class FakeBackend : IUiBackend
    {
        public bool Available { get; set; } = true;
        public string Language { get; set; } = "en-us";
        public bool JoinedServer { get; set; }
        public List<FakeSurface> Surfaces { get; } = new();
        public List<(UiMenuIn Where, string Text, int Order, Action Click)> Menus { get; } = new();
        public List<FakeHud> Huds { get; } = new();
        public List<(KeyPlan Plan, string Category, Action Press)> Keys { get; } = new();
        public List<(string Text, ToastKind Kind)> Toasts { get; } = new();
        public List<string> Casts { get; } = new();
        public List<string> Commands { get; } = new();
        public (bool Ok, string Message) CommandResult { get; set; } = (true, "done");
        public bool FailSurfaces { get; set; }
        public bool RealSurfaces { get; set; }

        public IUiSurface CreateSurface(WindowRuntime window, string title)
        {
            if (RealSurfaces) return new KilnUiBackend().CreateSurface(window, title);
            if (FailSurfaces) throw new InvalidOperationException("the mod is unloading");
            var s = new FakeSurface(this, window, title);
            Surfaces.Add(s);
            return s;
        }

        public IDisposable? AddMenu(UiMenuIn where, UiText text, int order, Action onClick)
        {
            var entry = (where, text.For(Language), order, onClick);
            Menus.Add(entry);
            return new Handle(() => Menus.Remove(entry));
        }

        public IUiHud ShowHud(string id, string text, HudAnchor anchor, Color color)
        {
            var h = new FakeHud(this, id, text, anchor, color);
            Huds.Add(h);
            return h;
        }

        public IDisposable? BindKey(KeyPlan plan, string category, Action onPress)
        {
            var entry = (plan, category, onPress);
            Keys.Add(entry);
            return new Handle(() => Keys.Remove(entry));
        }

        public void Toast(string text, ToastKind kind) => Toasts.Add((text, kind));
        public void RequestCast(string abilityId) => Casts.Add(abilityId);

        public (bool Ok, string Message) RunCommand(string line)
        {
            Commands.Add(line);
            return CommandResult;
        }
    }

    /// <summary>The widgets a frame asked for, in order; what the "player" does is set up before the frame.</summary>
    private sealed class FakeCanvas : IUiCanvas
    {
        public List<string> Calls { get; } = new();
        public HashSet<string> Clicks { get; } = new();
        public HashSet<string> Flips { get; } = new();
        public Dictionary<string, double> Slides { get; } = new();
        public Dictionary<string, int> Picks { get; } = new();
        private readonly Stack<string> _ids = new();

        public void Text(string text, Color? color, bool wrap) => Calls.Add($"text:{text}{(color is { } c ? $"@{c.R:x2}{c.G:x2}{c.B:x2}" : "")}{(wrap ? "~" : "")}");
        public void Disabled(string text) => Calls.Add($"disabled:{text}");
        public bool Button(string label) { Calls.Add($"button:{label}"); return Clicks.Remove(label); }

        public bool Checkbox(string label, ref bool value)
        {
            Calls.Add($"checkbox:{label}={value}");
            if (!Flips.Remove(label)) return false;
            value = !value;
            return true;
        }

        public bool SliderInt(string label, ref int value, int min, int max)
        {
            Calls.Add($"sliderInt:{label}={value}[{min}..{max}]");
            if (!Slides.Remove(label, out var x)) return false;
            value = (int)x;
            return true;
        }

        public bool SliderFloat(string label, ref float value, float min, float max)
        {
            Calls.Add($"sliderFloat:{label}={value:0.##}[{min:0.##}..{max:0.##}]");
            if (!Slides.Remove(label, out var x)) return false;
            value = (float)x;
            return true;
        }

        public bool Combo(string label, ref int index, IReadOnlyList<string> items)
        {
            Calls.Add($"combo:{label}={items[index]}({string.Join("|", items)})");
            if (!Picks.Remove(label, out var i)) return false;
            index = i;
            return true;
        }

        public void Separator(string? heading) => Calls.Add($"separator:{heading}");
        public void SameLine() => Calls.Add("sameLine");
        public void Tooltip(string text) => Calls.Add($"tooltip:{text}");
        public void PushId(string id) => _ids.Push(id);
        public void PopId() => _ids.Pop();
    }

    // ---- setup --------------------------------------------------------------------------------------------------------

    private readonly FakeBackend _game = new();
    private long _now = 1_000;
    private FakeConfig _clock = null!, _server = null!;
    private readonly List<string> _deferredRuns = new();

    public UiBlockTests()
    {
        UiRuntime.ResetForTests(_game);
        UiRuntime.Clock = () => _now;
        UiCompile.Later = (_, _) => { };
        _clock = new FakeConfig("ui-test-client", false,
            Entry("clock.show", "bool", true), Entry("clock.scale", "float", 1.0, 0.5, 3), Entry("clock.size", "int", 14, 8, 32), Entry("clock.look", "enum", "Plain", null, null, "Plain", "Fancy", "Bold"),
            Entry("clock.label", "string", "now"), Entry("refuses", "int", 1, 0, 9), Entry("free", "float", 2.0), Entry("shared", "bool", false));
        _server = new FakeConfig("ui-test-server", true, Entry("rules.pvp", "bool", false), Entry("shared", "bool", true));
        Install(_clock, _server);
    }

    public void Dispose()
    {
        UiRuntime.ResetForTests(new KilnUiBackend());
        UiConfigs.OfMod = static mod => [.. Config.All.Where(f => f.ModId == mod).Select(f => (IUiConfig)new RealConfig(f))];
        UiConfigs.Find = static id => Config.Find(id) is { } f ? new RealConfig(f) : null;
        UiCompile.Later = BlocksBridge.After;
    }

    private static void Install(params FakeConfig[] configs)
    {
        UiConfigs.OfMod = mod => mod == Mod ? configs : [];
        UiConfigs.Find = id => configs.FirstOrDefault(c => c.Id == id);
    }

    private static ModBlocks NewMod(string clients = "mod") => new(Mod, 1) { ManifestClients = clients };

    private static (BlockHandle Handle, Lowered Low) Lower<T>(ModBlocks mod, string name, T block) where T : class, IBlock
    {
        var (handle, low, _) = Compiler.Lower(mod, name, block);
        mod.Handles[name] = handle;
        return (handle, low);
    }

    /// <summary>Lowers and commits: what a block does when the mod loads.</summary>
    private static ModBlocks Define<T>(string name, T block, ModBlocks? mod = null) where T : class, IBlock
    {
        mod ??= NewMod();
        var (_, low) = Lower(mod, name, block);
        low.Commit();
        return mod;
    }

    private static BlockError Refused<T>(T block, ModBlocks? mod = null, string name = "thing") where T : class, IBlock =>
        Assert.Throws<BlockError>(() => Lower(mod ?? NewMod(), name, block));

    private static WindowBlock Window(params UiRow[] rows) => new() { Title = "Clock", Rows = rows };
    private static UiRow Text(string text) => new TextRow { Text = text };
    private static UiRow Button(string label, UiAction does) => new ButtonRow { Label = label, Does = does };

    private static T Json<T>(string json) where T : class, IBlock => BlockJson.Parse<T>(json);

    private WindowRuntime Open(string name = "settings")
    {
        var w = UiRuntime.FindWindow(Mod, name) ?? throw new InvalidOperationException($"no window {name} registered");
        w.Open();
        return w;
    }

    // ---- the shapes: C# records and JSON are the same blocks ----------------------------------------------------------------

    private const string WindowJson = """
        {
          "title": { "en": "Clock", "de": "Uhr" },
          "kind": "screen", "width": 400, "height": 300, "refresh": 0.5,
          "menu": { "text": "Clock settings", "in": "both", "order": 5 },
          "rows": [
            { "type": "text", "text": "Now {game.clock}", "color": "orange" },
            { "type": "value", "label": "Scale", "bind": "config.clock.scale", "format": "0.0" },
            { "type": "toggle", "label": "Show", "bind": "config.clock.show", "when": "game.inGame" },
            { "type": "slider", "label": "Size", "bind": "config.clock.size", "tooltip": "Points" },
            { "type": "choice", "label": "Look", "bind": "config.clock.look" },
            { "type": "separator", "text": "More" },
            { "type": "button", "label": "Reset", "does": { "set": { "bind": "config.clock.scale", "to": 1.5 } }, "sameLine": false }
          ]
        }
        """;

    [Fact]
    public void A_window_reads_from_JSON_as_the_record_C_sharp_writes()
    {
        var w = Json<WindowBlock>(WindowJson);
        Assert.Equal(WindowKind.Screen, w.Kind);
        Assert.Equal("Uhr", w.Title.For("de-de"));
        Assert.Equal("Clock", w.Title.For("fr-fr"));
        Assert.Equal(UiMenuIn.Both, w.Menu!.In);
        Assert.Collection(w.Rows, r => Assert.IsType<TextRow>(r), r => Assert.IsType<ValueRow>(r), r => Assert.IsType<ToggleRow>(r), r => Assert.IsType<SliderRow>(r),
            r => Assert.IsType<ChoiceRow>(r), r => Assert.IsType<SeparatorRow>(r), r => Assert.IsType<ButtonRow>(r));
        Assert.Equal(1.5, ((JsonElement)((ButtonRow)w.Rows[6]).Does.Set!.To).GetDouble());
        // and back: a text in one language stays a string
        var back = JsonSerializer.Serialize(new TextRow { Text = "Hi" }, typeof(UiRow), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"type\":\"text\"", back);
        Assert.Contains("\"text\":\"Hi\"", back);
    }

    [Theory]
    [InlineData("""{ "title": "T", "rows": [ { "type": "dial", "text": "x" } ] }""")]
    [InlineData("""{ "title": "T", "rows": [ { "type": "text" } ] }""")]
    [InlineData("""{ "title": "T", "rows": [ { "type": "text", "text": "x", "colour": "red" } ] }""")]
    [InlineData("""{ "title": "T" }""")]
    [InlineData("""{ "title": ["T"], "rows": [] }""")]
    [InlineData("""{ "title": "T", "rows": [ { "type": "button", "label": "x", "does": { "open": "w" } } ] }""")]
    public void A_malformed_window_is_refused_when_it_is_read(string json) => Assert.ThrowsAny<JsonException>(() => Json<WindowBlock>(json));

    [Fact]
    public void A_text_in_languages_is_one_shape_both_ways()
    {
        var b = Json<HudLineBlock>("""{ "text": { "en": "FPS", "de": "Bilder/s" }, "anchor": "bottomRight", "refresh": 2 }""");
        Assert.Equal(HudAnchor.BottomRight, b.Anchor);
        Assert.Equal("Bilder/s", b.Text.For("de"));
        var again = Json<HudLineBlock>(JsonSerializer.Serialize(b, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal("Bilder/s", again.Text.For("de-de"));
        Assert.Equal(b.Anchor, again.Anchor);
    }

    // ---- lowering ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_window_lowers_to_a_description_with_every_name_resolved()
    {
        var w = Json<WindowBlock>(WindowJson);
        var (handle, low) = Lower(NewMod(), "settings", w);
        Assert.Equal("ui-test:settings", handle.GameName);
        Assert.Equal(BlockIds.Derive(Mod, "window", "settings"), handle.Guid);
        var j = low.Json["window"]!.AsObject();
        Assert.Equal("screen", (string?)j["kind"]);
        Assert.Equal("Clock settings", (string?)j["menu"]!["text"]);
        var rows = j["rows"]!.AsArray();
        Assert.Equal(7, rows.Count);
        // the names a row binds are the config's own, whichever way they were written
        Assert.Equal("config.ui-test-client/clock.scale", (string?)rows[1]!["bind"]);
        Assert.Equal("game.inGame", (string?)rows[2]!["when"]);
        Assert.Equal("8..32", string.Join("..", rows[3]!["range"]!.AsArray().Select(x => (double)x!)));
        Assert.Equal("Plain|Fancy|Bold", string.Join("|", rows[4]!["options"]!.AsArray().Select(x => (string?)x)));
        Assert.Equal("config.ui-test-client/clock.scale", (string?)rows[6]!["does"]!["set"]);
        Assert.Contains("config.ui-test-client/clock.size", j["reads"]!.AsArray().Select(x => (string?)x));
        Assert.Contains("game.clock", j["reads"]!.AsArray().Select(x => (string?)x));
    }

    [Fact]
    public void A_slider_takes_the_entry_range_or_its_own_and_a_whole_number_range_for_an_int()
    {
        var (_, low) = Lower(NewMod(), "settings", Window(
            new SliderRow { Label = "Size", Bind = "config.clock.size" },
            new SliderRow { Label = "Free", Bind = "config.free", Min = 0, Max = 10 },
            new SliderRow { Label = "Narrow", Bind = "config.clock.scale", Min = 1, Max = 2 }));
        var ranges = low.Json["window"]!["rows"]!.AsArray().Select(r => string.Join("..", r!["range"]!.AsArray().Select(x => (double)x!))).ToArray();
        Assert.Equal(["8..32", "0..10", "1..2"], ranges);
    }

    [Fact]
    public void A_HUD_line_and_a_key_lower_with_derived_ids()
    {
        var mod = NewMod();
        var (hud, hudLow) = Lower(mod, "fps", new HudLineBlock { Text = "{game.fps:0} fps", Anchor = HudAnchor.TopRight, When = "game.inGame && game.fps < 30", Refresh = 0.5 });
        Assert.Equal("ui-test:fps", hud.GameName);
        Assert.Equal("game.fps,game.inGame", string.Join(",", hudLow.Json["hudLine"]!["reads"]!.AsArray().Select(x => (string?)x)));
        var (key, keyLow) = Lower(mod, "open-settings", new KeyBindingBlock { Key = "Ctrl+F8", Was = "old-name", Does = new UiAction { Toggle = "config.clock.show" } });
        // the player's rebinding follows the oldest name
        Assert.Equal("ui-test:old-name", key.GameName);
        Assert.Equal("ui-test:old-name", (string?)keyLow.Json["keyBinding"]!["id"]);
        Assert.Equal("Ctrl+F8", (string?)keyLow.Json["keyBinding"]!["key"]);
        Assert.Equal("config.ui-test-client/clock.show", (string?)keyLow.Json["keyBinding"]!["does"]!["toggle"]);
    }

    [Fact]
    public void The_log_line_says_what_a_UI_block_is()
    {
        var (_, low) = Lower(NewMod(), "settings", Window(Text("hi")));
        Assert.Contains("1 row(s), drawn by players' games", low.Summary());
        var (_, hud) = Lower(NewMod(), "fps", new HudLineBlock { Text = "x" });
        Assert.Contains("HUD line ui-test:fps", hud.Summary());
    }

    // ---- mistakes, by block and field -------------------------------------------------------------------------------------

    private static void Says(BlockError e, string field, string contains)
    {
        Assert.Equal(field, e.Field);
        Assert.Contains(contains, e.Problem);
    }

    [Fact]
    public void A_window_that_is_wrong_names_the_field()
    {
        Says(Refused(new WindowBlock { Title = "T", Rows = [] }), "rows", "nothing");
        Says(Refused(new WindowBlock { Title = " ", Rows = [Text("x")] }), "title", "players can read");
        Says(Refused(new WindowBlock { Title = "T", Rows = [Text("x")], Refresh = 0.01 }), "refresh", "0.1");
        Says(Refused(new WindowBlock { Title = "T", Rows = [Text("x")], Refresh = double.NaN }), "refresh", "NaN");
        Says(Refused(new WindowBlock { Title = "T", Rows = [Text("x")], Width = -1 }), "width", "0..4096");
        Says(Refused(new WindowBlock { Title = "T", Rows = [Text("x")], Height = 9000 }), "height", "0..4096");
        Says(Refused(new WindowBlock { Title = "T", Rows = [Text("x")], Position = [0.5f] }), "position", "[x, y]");
        Says(Refused(new WindowBlock { Title = "T", Rows = [Text("x")], Position = [0.5f, 2] }), "position", "0..1");
        Says(Refused(new WindowBlock { Title = "T", Rows = [Text("x")], Alpha = 2 }), "alpha", "0..1");
        Says(Refused(new WindowBlock { Title = "T", Rows = [Text("x")], Dim = true }), "dim", "screen");
        Says(Refused(new WindowBlock { Title = "T", Kind = WindowKind.Screen, Rows = [Text("x")], Position = [0.5f, 0.5f] }), "position", "window");
        Says(Refused(new WindowBlock { Title = "T", Kind = WindowKind.Screen, Rows = [Text("x")], AutoResize = true }), "autoResize", "window");
        Says(Refused(new WindowBlock { Title = "T", Kind = WindowKind.Screen, Rows = [Text("x")], Interactive = false }), "interactive", "screen");
        Says(Refused(new WindowBlock { Title = "T", Rows = Enumerable.Range(0, 201).Select(_ => Text("x")).ToArray() }), "rows", "200");
        Says(Refused(new WindowBlock { Title = "T", Rows = [Text("x")], Menu = new UiMenu { Text = "" } }), "menu.text", "players can read");
        Assert.Contains("window thing", Refused(new WindowBlock { Title = "T", Rows = [] }).Message);
    }

    [Fact]
    public void A_row_that_is_wrong_names_the_row_and_the_field()
    {
        Says(Refused(Window(Text("a"), new TextRow { Text = "a {game.fpz}" })), "rows[1].text", "game.fpz");
        Says(Refused(Window(new TextRow { Text = "a {game.fps" })), "rows[0].text", "never closed");
        Says(Refused(Window(new TextRow { Text = "a } b" })), "rows[0].text", "lone");
        Says(Refused(Window(new TextRow { Text = "{game.fps:F9999999999}" })), "rows[0].text", "number format");
        Says(Refused(Window(new TextRow { Text = "{game.clock:0.0}" })), "rows[0].text", "takes no format");
        Says(Refused(Window(new TextRow { Text = UiText.Of(("en", "fine"), ("de", "kaputt {nope.x}")) })), "rows[0].text", "[de]");
        Says(Refused(Window(new TextRow { Text = "x", Color = "mauve" })), "rows[0].color", "not a colour");
        Says(Refused(Window(new TextRow { Text = "x", Color = "red", Wrap = true })), "rows[0].color", "wrap");
        Says(Refused(Window(new TextRow { Text = "x", When = "game.fps <" })), "rows[0].when", "not a condition");
        Says(Refused(Window(new TextRow { Text = "x", When = "game.fps < many" })), "rows[0].when", "number");
        Says(Refused(Window(new TextRow { Text = "x", When = "game.inGame < true" })), "rows[0].when", "== and !=");
        Says(Refused(Window(new TextRow { Text = "x", When = "!game.fps > 3" })), "rows[0].when", "! goes with a name");
        Says(Refused(Window(new TextRow { Text = "x", SameLine = true })), "rows[0].sameLine", "no row before");
        Says(Refused(Window(Text("a"), new TextRow { Text = "x", Tooltip = "{no.such}" })), "rows[1].tooltip", "no.such");
        Says(Refused(Window(new ValueRow { Label = "V", Bind = "config.clock.nope" })), "rows[0].bind", "no config entry \"clock.nope\"");
        Says(Refused(Window(new ValueRow { Label = "V", Bind = "clock" })), "rows[0].bind", "game.<name>");
        Says(Refused(Window(new ValueRow { Label = "V", Bind = "game.fps", Format = "F9999999999" })), "rows[0].format", "number format");
        Says(Refused(Window(new ValueRow { Label = "V", Bind = "game.inGame", Format = "yes" })), "rows[0].format", "bar");
        Says(Refused(Window(new ValueRow { Label = "", Bind = "game.fps" })), "rows[0].label", "players can read");
        Says(Refused(Window(new ToggleRow { Label = "T", Bind = "config.clock.scale" })), "rows[0].bind", "true/false");
        Says(Refused(Window(new ToggleRow { Label = "T", Bind = "game.inGame" })), "rows[0].bind", "config entry");
        Says(Refused(Window(new SliderRow { Label = "S", Bind = "config.clock.show" })), "rows[0].bind", "whole-number or decimal");
        Says(Refused(Window(new SliderRow { Label = "S", Bind = "config.free" })), "rows[0].min", "no minimum");
        Says(Refused(Window(new SliderRow { Label = "S", Bind = "config.free", Min = 0 })), "rows[0].max", "no maximum");
        Says(Refused(Window(new SliderRow { Label = "S", Bind = "config.free", Min = 5, Max = 5 })), "rows[0].min", "not a range");
        Says(Refused(Window(new SliderRow { Label = "S", Bind = "config.clock.scale", Min = 0, Max = 2 })), "rows[0].min", "wider");
        Says(Refused(Window(new SliderRow { Label = "S", Bind = "config.clock.size", Min = 8.5, Max = 20 })), "rows[0].min", "whole");
        Says(Refused(Window(new ChoiceRow { Label = "C", Bind = "config.clock.label" })), "rows[0].bind", "enum");
        Says(Refused(Window(new SeparatorRow { Text = "{x.y}" })), "rows[0].text", "x.y");
        Says(Refused(Window(Text("a"), null!)), "rows[1]", "object with a \"type\"");
    }

    [Fact]
    public void A_config_name_that_two_configs_share_says_which_to_use()
    {
        Says(Refused(Window(new ToggleRow { Label = "T", Bind = "config.shared" })), "rows[0].bind", "config.<config id>/shared");
        var (_, low) = Lower(NewMod(), "settings", Window(new ToggleRow { Label = "T", Bind = "config.ui-test-server/shared" }));
        Assert.Equal("config.ui-test-server/shared", (string?)low.Json["window"]!["rows"]![0]!["bind"]);
        Says(Refused(Window(new ToggleRow { Label = "T", Bind = "config.nope/shared" })), "rows[0].bind", "ui-test-client, ui-test-server");
    }

    [Fact]
    public void A_mod_with_no_config_says_to_register_one()
    {
        UiConfigs.OfMod = _ => [];
        Says(Refused(Window(new ToggleRow { Label = "T", Bind = "config.clock.show" })), "rows[0].bind", "Config.Register<T>()");
        // game values need none
        Lower(NewMod(), "settings", Window(new ValueRow { Label = "FPS", Bind = "game.fps" }));
    }

    [Fact]
    public void An_action_does_exactly_one_thing()
    {
        Says(Refused(Window(Button("B", new UiAction()))), "rows[0].does", "does something");
        Says(Refused(Window(Button("B", new UiAction { Command = "x", Toast = "hi" }))), "rows[0].does", "command and toast");
        Says(Refused(Window(Button("B", null!))), "rows[0].does", "needs an action");
        Says(Refused(Window(Button("B", new UiAction { Command = "  / " }))), "rows[0].does.command", "empty");
        Says(Refused(Window(Button("B", new UiAction { Command = "a\nb" }))), "rows[0].does.command", "one line");
        Says(Refused(Window(Button("B", new UiAction { Toggle = "config.clock.size" }))), "rows[0].does.toggle", "true/false");
        Says(Refused(Window(Button("B", new UiAction { Toggle = "game.inGame" }))), "rows[0].does.toggle", "true/false");
        Says(Refused(Window(Button("B", new UiAction { Set = new UiSet { Bind = "game.fps", To = 1 } }))), "rows[0].does.set.bind", "read-only");
        Says(Refused(Window(Button("B", new UiAction { Set = new UiSet { Bind = "config.clock.show", To = "yes" } }))), "rows[0].does.set.to", "true or false");
        Says(Refused(Window(Button("B", new UiAction { Set = new UiSet { Bind = "config.clock.size", To = 4 } }))), "rows[0].does.set.to", "outside");
        Says(Refused(Window(Button("B", new UiAction { Set = new UiSet { Bind = "config.clock.size", To = 9.5 } }))), "rows[0].does.set.to", "whole");
        Says(Refused(Window(Button("B", new UiAction { Set = new UiSet { Bind = "config.clock.look", To = "Gothic" } }))), "rows[0].does.set.to", "Plain, Fancy, Bold");
        Says(Refused(Window(Button("B", new UiAction { Set = new UiSet { Bind = "config.clock.size", To = "big" } }))), "rows[0].does.set.to", "number");
        Says(Refused(Window(Button("B", new UiAction { Toast = "" }))), "rows[0].does.toast", "players can read");
        Says(Refused(new KeyBindingBlock { Key = "F8", Does = new UiAction() }), "does", "does something");
    }

    [Fact]
    public void A_set_action_takes_JSON_values_and_names_the_options_by_their_own_spelling()
    {
        var w = Json<WindowBlock>("""
            { "title": "T", "rows": [
              { "type": "button", "label": "A", "does": { "set": { "bind": "config.clock.look", "to": "fancy" } } },
              { "type": "button", "label": "B", "does": { "set": { "bind": "config.clock.show", "to": false } } },
              { "type": "button", "label": "C", "does": { "set": { "bind": "config.clock.label", "to": "later" } } } ] }
            """);
        var (_, low) = Lower(NewMod(), "settings", w);
        var does = low.Json["window"]!["rows"]!.AsArray().Select(r => r!["does"]!).ToArray();
        Assert.Equal("Fancy", (string?)does[0]["to"]);
        Assert.False((bool)does[1]["to"]!);
        Assert.Equal("later", (string?)does[2]["to"]);
    }

    [Fact]
    public void A_key_that_is_wrong_names_the_field()
    {
        Says(Refused(new KeyBindingBlock { Key = "Hyper+Q", Does = new UiAction { Toast = "x" } }), "key", "unknown key");
        Says(Refused(new KeyBindingBlock { Key = " ", Does = new UiAction { Toast = "x" } }), "key", "default key");
        Says(Refused(new KeyBindingBlock { Key = "F8", Category = " ", Does = new UiAction { Toast = "x" } }), "category", "no name");
        Lower(NewMod(), "ok", new KeyBindingBlock { Key = "None", Does = new UiAction { Toast = "x" } });
    }

    [Fact]
    public void A_HUD_line_that_is_wrong_names_the_field()
    {
        Says(Refused(new HudLineBlock { Text = "" }), "text", "players can read");
        Says(Refused(new HudLineBlock { Text = "{config.clock.nope}" }), "text", "no config entry");
        Says(Refused(new HudLineBlock { Text = "x", Refresh = 0 }), "refresh", "0.1");
        Says(Refused(new HudLineBlock { Text = "x", Refresh = 4000 }), "refresh", "3600");
        Says(Refused(new HudLineBlock { Text = "x", Color = "#12345" }), "color", "not a colour");
        Says(Refused(new HudLineBlock { Text = "x", When = "game.nothing" }), "when", "game.nothing");
        Says(Refused(new HudLineBlock { Text = "x", Anchor = (HudAnchor)99 }), "anchor", "topLeft");
        Says(Refused(new HudLineBlock { Text = "x", When = "" }), "when", "empty condition");
    }

    [Fact]
    public void UI_blocks_need_the_mod_on_the_players_game()
    {
        foreach (var clients in new[] { "none", "kiln" })
        {
            var e = Refused<WindowBlock>(Window(Text("x")), NewMod(clients));
            Assert.Contains($"says \"clients\": \"{clients}\"", e.Problem);
            Assert.Contains("<KilnClients>mod</KilnClients>", e.Problem);
            Refused(new HudLineBlock { Text = "x" }, NewMod(clients));
            Refused(new KeyBindingBlock { Key = "F8", Does = new UiAction { Toast = "x" } }, NewMod(clients));
        }
    }

    // ---- names a field takes: windows, abilities, effects ----------------------------------------------------------------------

    [Fact]
    public void A_window_a_button_opens_must_be_a_window()
    {
        var mod = NewMod();
        Lower(mod, "settings", Window(Text("x")));
        Lower(mod, "burst", new EffectBlock { Heal = 5 });
        Lower(mod, "second", Window(Button("Go", new UiAction { OpenWindow = "settings" }), Button("Back", new UiAction { CloseWindow = "settings" })));
        Says(Refused(Window(Button("Go", new UiAction { OpenWindow = "burst" })), mod), "rows[0].does.openWindow", "an effect, not a window");
        Says(Refused(Window(Button("Go", new UiAction { OpenWindow = "other-mod:thing" })), mod), "rows[0].does.openWindow", "no block \"thing\" in mod other-mod");
        Says(Refused(Window(Button("Go", new UiAction { CloseWindow = "" })), mod), "rows[0].does.closeWindow", "needs a window");
    }

    private static ModBlocks LoadFile(string json)
    {
        var dir = Directory.CreateTempSubdirectory("kiln-ui-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "blocks"));
            File.WriteAllText(Path.Combine(dir, "blocks", "ui.json"), json);
            var mod = NewMod();
            BlockJson.LoadMod(mod, dir);
            return mod;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Windows_that_open_each_other_compile_whatever_order_a_data_file_gives()
    {
        var mod = LoadFile("""
            { "format": 1,
              "windows": {
                "main": { "title": "Main", "rows": [ { "type": "button", "label": "Settings", "does": { "openWindow": "settings" } } ] },
                "settings": { "title": "Settings", "rows": [ { "type": "button", "label": "Back", "does": { "openWindow": "main" } } ] } } }
            """);
        Assert.Empty(mod.Errors);
        Assert.Equal(["main", "settings"], mod.Handles.Keys.Order());
        // main named settings before it was defined: that is looked at when the pass is over, and it is a window
        Assert.Equal(1, UiCompile.VerifyDeferred(mod));
        Assert.Empty(mod.Errors);
        Assert.True(UiRuntime.FindWindow(Mod, "main")!.Plan.Rows[0].Does is WindowActionPlan { Window: "settings", Open: true });
    }

    [Fact]
    public void A_window_name_that_is_nothing_or_no_window_is_said_after_the_pass_by_block_and_field()
    {
        var mod = LoadFile("""
            { "format": 1,
              "windows": {
                "a": { "title": "A", "rows": [ { "type": "button", "label": "Go", "does": { "openWindow": "fps" } },
                                               { "type": "button", "label": "Lost", "does": { "closeWindow": "nowhere" } } ] } },
              "hudLines": { "fps": { "text": "{game.fps:0}" } } }
            """);
        Assert.Empty(mod.Errors);
        Assert.Equal(2, UiCompile.VerifyDeferred(mod));
        Assert.Equal(
        [
            "block window a, rows[0].does.openWindow: \"fps\" is a hudline, not a window",
            "block window a, rows[1].does.closeWindow: no window \"nowhere\" in this mod (a block that failed to compile is not defined); define it, or say \"other-mod:nowhere\"; there are: a",
        ], mod.Errors);
    }

    [Fact]
    public void A_window_a_C_sharp_mod_defines_later_is_found_when_the_pass_is_over()
    {
        var mod = Define("main", Window(Button("Go", new UiAction { OpenWindow = "settings" })));
        Define("settings", Window(Button("Back", new UiAction { OpenWindow = "main" })), mod);
        Assert.Equal(1, UiCompile.VerifyDeferred(mod));
        Assert.Empty(mod.Errors);
        var lost = Define("lost", Window(Button("Go", new UiAction { OpenWindow = "ghost" })), mod);
        Assert.Equal(1, UiCompile.VerifyDeferred(lost));
        Assert.Contains(lost.Errors, e => e.StartsWith("block window lost, rows[0].does.openWindow: no window \"ghost\"", StringComparison.Ordinal));
        Assert.Equal(0, UiCompile.VerifyDeferred(lost));
    }

    [Fact]
    public void An_ability_a_button_casts_must_be_one()
    {
        var mod = NewMod();
        Lower(mod, "nova", new AbilityBlock { Name = "Nova", OnCast = [new EffectBlock { Heal = 5 }] });
        Lower(mod, "burst", new EffectBlock { Heal = 5 });
        var (_, low) = Lower(mod, "settings", Window(Button("Cast", new UiAction { CastAbility = "nova" })));
        Assert.Equal("ui-test:nova", (string?)low.Json["window"]!["rows"]![0]!["does"]!["castAbility"]);
        Says(Refused(Window(Button("Cast", new UiAction { CastAbility = "burst" })), mod), "rows[0].does.castAbility", "not an ability");
        Says(Refused(Window(Button("Cast", new UiAction { CastAbility = "zap" })), mod), "rows[0].does.castAbility", "no ability \"zap\"");
        Says(Refused(new KeyBindingBlock { Key = "G", Does = new UiAction { CastAbility = "zap" } }, mod), "does.castAbility", "no ability \"zap\"");
    }

    [Fact]
    public void An_effect_a_button_applies_goes_through_one_hidden_ability_each()
    {
        var mod = NewMod();
        var (burst, _) = Lower(mod, "burst", new EffectBlock { Heal = 5 });
        var (_, low) = Lower(mod, "settings", Window(Button("Heal", new UiAction { ApplyEffect = "burst" }), Button("Again", new UiAction { ApplyEffect = (BlockHandle<EffectBlock>)burst }),
            Button("Inline", new UiAction { ApplyEffect = new EffectBlock { Restores = new RestoreBlock { Mana = 3 } } })));
        var rows = low.Json["window"]!["rows"]!.AsArray();
        Assert.Equal("burst", (string?)rows[0]!["does"]!["applyEffect"]);
        Assert.Equal("ui-test:ui-apply.burst", (string?)rows[0]!["does"]!["viaAbility"]);
        // the same effect twice is the same ability, named by its handle or by its name
        Assert.Equal((string?)rows[0]!["does"]!["viaAbility"], (string?)rows[1]!["does"]!["viaAbility"]);
        Assert.Equal("ui-test:ui-apply.settings.rows.2.does", (string?)rows[2]!["does"]!["viaAbility"]);
        Assert.Equal(2, rows.Select(r => (string?)r!["does"]!["viaAbility"]).Distinct().Count());
        // the ability is made when the window is committed, not before: a window that fails leaves none behind
        Assert.DoesNotContain(mod.Handles.Keys, k => k.StartsWith("ui-apply.", StringComparison.Ordinal));
        Says(Refused(Window(Button("X", new UiAction { ApplyEffect = "nope" })), mod), "rows[0].does.applyEffect", "nope");
        // a wrong inline effect is said at the field, and leaves no ability behind
        var before = mod.Handles.Count;
        Says(Refused(Window(Button("X", new UiAction { ApplyEffect = new EffectBlock { Chance = 7 } })), mod), "rows[0].does.applyEffect", "in the hidden ability");
        Assert.Equal(before, mod.Handles.Count);
    }

    // ---- templates and conditions --------------------------------------------------------------------------------------------

    private static UiBinding G(string name) => UiBindings.Resolve(Mod, name);

    [Fact]
    public void A_hidden_apply_ability_never_reuses_an_authored_or_different_effect_block()
    {
        var mod = NewMod();
        Lower(mod, "burst", new EffectBlock { Heal = 5 });
        Says(Refused(new AbilityBlock { Name = "Authored", OnCast = [new EffectBlock { Heal = 20 }] }, mod, "ui-apply.burst"), "name", "reserved");
        mod = NewMod();
        Lower(mod, "settings.rows.1.does", new EffectBlock { Heal = 5 });
        Says(Refused(Window(Button("Named", new UiAction { ApplyEffect = "settings.rows.1.does" }),
            Button("Inline", new UiAction { ApplyEffect = new EffectBlock { Heal = 1 } })), mod, "settings"),
            "rows[1].does.applyEffect", "two different effects");
    }

    [Fact]
    public void A_template_shows_values_and_keeps_braces()
    {
        var t = UiTemplate.Parse("{{ {game.fps:0.0} fps, {game.inGame:in|out}, {game.state}, {game.fps} }}", G);
        var values = new Dictionary<string, object?> { ["game.fps"] = 59.96, ["game.inGame"] = true, ["game.state"] = "Hosting" };
        Assert.Equal("{ 60.0 fps, in, Hosting, 59.96 }", t.Render(b => values[b.Name]));
        Assert.Equal(["game.fps", "game.inGame", "game.state", "game.fps"], t.Bindings.Select(b => b.Name));
        Assert.Equal("plain {text}", UiTemplate.Parse("plain {{text}}", G).Render(_ => null));
        Assert.True(UiTemplate.Parse("no values", G).IsPlain);
        // a value that cannot be read shows as nothing
        Assert.Equal("fps ", UiTemplate.Parse("fps {game.fps}", G).Render(_ => null));
        Assert.Equal("true / false", UiTemplate.Parse("{game.inGame} / {game.inGame}", G).Render(b => b.Name == "game.inGame" ? new object[] { true, false }[Interlocked.Increment(ref _calls) - 1] : null));
    }

    private int _calls;

    [Fact]
    public void A_text_picks_its_language_and_every_variant_is_checked()
    {
        var line = UiLine.Compile(UiText.Of(("en", "{game.fps:0} fps"), ("de", "{game.fps:0} Bilder/s")), G);
        Assert.Equal("30 Bilder/s", line.Render("de-de", _ => 30.0));
        Assert.Equal("30 fps", line.Render("fr-fr", _ => 30.0));
        Assert.Equal(["game.fps"], line.Bindings.Select(b => b.Name));
        // a label is plain: braces are braces
        var plain = UiLine.Compile("Show {it}", G, templated: false);
        Assert.Equal("Show {it}", plain.Render("en", _ => null));
        Assert.True(plain.IsPlain);
    }

    [Theory]
    [InlineData("game.inGame", true, true)]
    [InlineData("!game.inGame", true, false)]
    [InlineData("game.fps < 30", false, true)]
    [InlineData("game.fps >= 30 && game.inGame", false, false)]
    [InlineData("game.screen == InGame", false, true)]
    [InlineData("game.screen != 'PauseMenu' && game.screen == \"ingame\"", false, true)]
    [InlineData("game.inGame == false", false, true)]
    [InlineData("game.fps", false, true)]
    [InlineData("game.state", false, true)]
    public void A_condition_reads_the_values_it_names(string text, bool inGame, bool expected)
    {
        var values = new Dictionary<string, object?> { ["game.inGame"] = inGame, ["game.fps"] = 24.0, ["game.screen"] = "InGame", ["game.state"] = "Hosting" };
        Assert.Equal(expected, UiCondition.Parse(text, G).Test(b => values[b.Name]));
    }

    [Fact]
    public void A_condition_on_a_value_that_cannot_be_read_holds_for_not_equal_only()
    {
        Assert.False(UiCondition.Parse("game.fps < 30", G).Test(_ => null));
        Assert.True(UiCondition.Parse("game.screen != Map", G).Test(_ => null));
        Assert.False(UiCondition.Parse("game.inGame", G).Test(_ => null));
        Assert.True(UiCondition.Parse("!game.inGame", G).Test(_ => null));
    }

    [Fact]
    public void Colours_are_names_or_hex()
    {
        Assert.True(UiColors.TryParse("#ff8000", out var a));
        Assert.Equal(new Color(255, 128, 0), a);
        Assert.True(UiColors.TryParse("#11223344", out var b));
        Assert.Equal(new Color(0x11, 0x22, 0x33, 0x44), b);
        Assert.True(UiColors.TryParse("Cyan", out var c));
        Assert.Equal(Color.Cyan, c);
        Assert.False(UiColors.TryParse("#12", out _));
        Assert.False(UiColors.TryParse("pink", out _));
    }

    [Fact]
    public void Game_values_never_throw_when_there_is_no_game()
    {
        foreach (var v in UiGameValues.All) Assert.Null(Record.Exception(() => v.Read()));
        Assert.Equal("Menu", (string?)UiGameValues.Find("state")!.Read());
        Assert.False((bool)UiGameValues.Find("inGame")!.Read()!);
        Assert.Matches(@"^\d\d:\d\d$", (string)UiGameValues.Find("clock")!.Read()!);
        Assert.Equal(0.0, (double)UiGameValues.Find("fps")!.Read()!);
    }

    // ---- a client: windows, refresh rate, actions ------------------------------------------------------------------------------

    private ModBlocks Settings(WindowBlock? w = null, string name = "settings") => Define(name, w ?? Json<WindowBlock>(WindowJson));

    [Fact]
    public void A_window_is_made_closed_with_its_menu_entries_and_opens_from_them()
    {
        Settings();
        var s = Assert.Single(_game.Surfaces);
        Assert.Equal("Clock", s.Title);
        Assert.False(s.IsOpen);
        Assert.Equal(["Main", "Pause"], _game.Menus.Select(m => m.Where.ToString()).Order());
        Assert.All(_game.Menus, m => Assert.Equal(("Clock settings", 5), (m.Text, m.Order)));
        _game.Menus[0].Click();
        Assert.True(s.IsOpen);
    }

    [Fact]
    public void A_window_can_start_open_and_a_pause_only_entry_is_one_entry()
    {
        Settings(new WindowBlock { Title = "T", OpenAtStart = true, Menu = new UiMenu { Text = "T", In = UiMenuIn.Main }, Rows = [Text("x")] });
        Assert.True(Assert.Single(_game.Surfaces).IsOpen);
        Assert.Equal(UiMenuIn.Main, Assert.Single(_game.Menus).Where);
    }

    [Fact]
    public void A_window_works_out_its_rows_at_its_pace_not_every_frame()
    {
        Settings(Json<WindowBlock>(WindowJson));
        var w = Open();
        var c = new FakeCanvas();
        for (var frame = 0; frame < 50; frame++)
        {
            w.Draw(c);
            _now += 16;
        }
        // 800 ms of frames, refresh 0.5 s: two refreshes, not fifty
        Assert.Equal(2, w.Refreshes);
        _now += 600;
        w.Draw(c);
        Assert.Equal(3, w.Refreshes);
    }

    [Fact]
    public void A_closed_window_is_never_refreshed_and_a_reopened_one_reads_fresh()
    {
        Settings();
        var w = UiRuntime.FindWindow(Mod, "settings")!;
        Assert.Equal(0, w.Refreshes);
        _clock.Values["clock.scale"] = 2.0;
        w.Open();
        var c = new FakeCanvas();
        w.Draw(c);
        Assert.Contains("text:2.0@5ad2e6", c.Calls);
        w.Close();
        _clock.Values["clock.scale"] = 3.0;
        w.Open();
        var d = new FakeCanvas();
        w.Draw(d);
        Assert.Contains("text:3.0@5ad2e6", d.Calls);
        Assert.Equal(2, w.Refreshes);
    }

    [Fact]
    public void Rows_draw_as_widgets_with_their_values_ranges_and_conditions()
    {
        Settings();
        var w = Open();
        var c = new FakeCanvas();
        w.Draw(c);
        // the toggle's row says "game.inGame", which is false with no game: it is left out
        Assert.Matches(@"^text:Now \d\d:\d\d@ff963c$", c.Calls[0]);
        Assert.Equal(
        [
            "text:Scale:", "sameLine", "text:1.0@5ad2e6",
            "sliderInt:Size=14[8..32]", "tooltip:Points",
            "combo:Look=Plain(Plain|Fancy|Bold)",
            "separator:More",
            "button:Reset",
        ], c.Calls.Skip(1));
    }

    [Fact]
    public void A_row_with_a_condition_appears_when_it_holds_at_the_next_refresh()
    {
        Settings(new WindowBlock { Title = "T", Refresh = 0.5, Rows = [Text("always"), new TextRow { Text = "when on", When = "config.clock.show" }, new TextRow { Text = "when off", When = "!config.clock.show", SameLine = true }] });
        var w = Open();
        var c = new FakeCanvas();
        w.Draw(c);
        Assert.Equal(["text:always", "text:when on"], c.Calls);
        _clock.Values["clock.show"] = false;
        var d = new FakeCanvas();
        w.Draw(d);
        Assert.Equal(["text:always", "text:when on"], d.Calls);   // not yet: the window refreshes at its pace
        _now += 600;
        var e = new FakeCanvas();
        w.Draw(e);
        Assert.Equal(["text:always", "sameLine", "text:when off"], e.Calls);
    }

    [Fact]
    public void A_toggle_shows_at_once_and_writes_the_config_on_the_tick()
    {
        Settings(new WindowBlock { Title = "T", Rows = [new ToggleRow { Label = "Show", Bind = "config.clock.show" }] });
        var w = Open();
        var c = new FakeCanvas();
        c.Flips.Add("Show");
        w.Draw(c);
        Assert.Equal(["checkbox:Show=True"], c.Calls);
        // flipped on the render thread, written by the tick
        Assert.Empty(_clock.Sets);
        var next = new FakeCanvas();
        _now += 10;
        w.Draw(next);
        Assert.Equal(["checkbox:Show=False"], next.Calls);   // the row keeps what the click gave it, before and after the write
        UiRuntime.Pump();
        Assert.Equal([("clock.show", (object?)false)], _clock.Sets);
        _now += 1000;
        var after = new FakeCanvas();
        w.Draw(after);
        Assert.Equal(["checkbox:Show=False"], after.Calls);
        Assert.False(UiEdits.Any);
    }

    [Fact]
    public void A_slider_waits_until_it_rests_and_writes_a_whole_number_to_an_int()
    {
        Settings(new WindowBlock { Title = "T", Refresh = 0.1, Rows = [new SliderRow { Label = "Size", Bind = "config.clock.size" }, new SliderRow { Label = "Scale", Bind = "config.clock.scale" }] });
        var w = Open();
        var c = new FakeCanvas();
        c.Slides["Size"] = 20;
        c.Slides["Scale"] = 2.25;
        w.Draw(c);
        UiRuntime.Pump();
        Assert.Empty(_clock.Sets);      // still being dragged
        _now += 200;
        var again = new FakeCanvas();
        again.Slides["Size"] = 21;
        w.Draw(again);                  // another change: the rest starts again
        _now += 300;
        UiRuntime.Pump();
        Assert.Equal([("clock.scale", (object?)2.25)], _clock.Sets);   // the float had rested for 500 ms; the int for 300
        _now += 200;
        UiRuntime.Pump();
        Assert.Equal(("clock.size", (object?)21), _clock.Sets[1]);
        Assert.IsType<int>(_clock.Sets[1].Value);
        Assert.False(UiEdits.Any);
    }

    [Fact]
    public void A_tiny_slider_preserves_precision_and_writes_within_its_config_range()
    {
        var tiny = new FakeConfig("tiny", false, Entry("value", "float", 1e-5, 1e-5, 2e-5));
        Install(tiny);
        Define("settings", Window(new SliderRow { Label = "Tiny", Bind = "config.value" }));
        var window = Open();
        var frame = new FakeCanvas(); frame.Slides["Tiny"] = 1.5e-5;
        window.Draw(frame);
        _now += UiEdits.RestMs;
        UiRuntime.Pump();
        var saved = Assert.IsType<double>(Assert.Single(tiny.Sets).Value);
        Assert.InRange(saved, 1e-5, 2e-5);
        Assert.Equal((double)(float)1.5e-5, saved);
        // The native float at this minimum is a little below the double config's minimum.
        frame = new FakeCanvas(); frame.Slides["Tiny"] = 1e-5;
        window.Draw(frame);
        _now += UiEdits.RestMs;
        UiRuntime.Pump();
        Assert.Equal(1e-5, Assert.IsType<double>(tiny.Sets[^1].Value));
    }

    [Fact]
    public void A_choice_writes_the_option_and_a_refused_value_is_a_toast_and_not_kept()
    {
        Settings(new WindowBlock { Title = "T", Rows = [new ChoiceRow { Label = "Look", Bind = "config.clock.look" }, new ButtonRow { Label = "Bad", Does = new UiAction { Set = new UiSet { Bind = "config.refuses", To = 5 } } }] });
        var w = Open();
        var c = new FakeCanvas();
        c.Picks["Look"] = 2;
        c.Clicks.Add("Bad");
        w.Draw(c);
        UiRuntime.Pump();
        Assert.Equal([("clock.look", (object?)"Bold"), ("refuses", (object?)5)], _clock.Sets.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray());
        Assert.Equal([("ui-test-client: refused: not today", ToastKind.Error)], _game.Toasts);
        Assert.False(UiEdits.Any);
    }

    [Fact]
    public void A_server_config_is_read_only_while_joined_and_so_is_the_servers_own_value()
    {
        Settings(new WindowBlock { Title = "T", Rows = [new ToggleRow { Label = "PvP", Bind = "config.rules.pvp" }, new ToggleRow { Label = "Shown", Bind = "config.ui-test-client/clock.show" }] });
        var w = Open();
        var c = new FakeCanvas();
        w.Draw(c);
        Assert.Equal(["checkbox:PvP=False", "checkbox:Shown=True"], c.Calls);
        _game.JoinedServer = true;
        _clock.FromServer.Add("clock.show");
        w.Close();
        w.Open();
        var d = new FakeCanvas();
        w.Draw(d);
        Assert.Equal(["disabled:PvP: false", "disabled:Shown: true"], d.Calls);
    }

    [Fact]
    public void A_button_runs_its_action()
    {
        var mod = NewMod();
        Lower(mod, "nova", new AbilityBlock { Name = "Nova", OnCast = [new EffectBlock { Heal = 5 }] });
        Define("other", Window(Button("Back", new UiAction { CloseWindow = "other" })), mod);
        Define("settings", new WindowBlock
        {
            Title = "T", Rows =
            [
                Button("Run", new UiAction { Command = "/mymod-reset 5" }),
                Button("Cast", new UiAction { CastAbility = "nova" }),
                Button("Flip", new UiAction { Toggle = "config.clock.show" }),
                Button("Set", new UiAction { Set = new UiSet { Bind = "config.clock.size", To = 20 } }),
                Button("Tell", new UiAction { Toast = UiText.Of(("en", "Hello"), ("de", "Hallo")) }),
                Button("Other", new UiAction { OpenWindow = "other" }),
            ],
        }, mod);
        var w = Open();
        var c = new FakeCanvas();
        foreach (var label in new[] { "Run", "Cast", "Flip", "Set", "Tell", "Other" }) c.Clicks.Add(label);
        w.Draw(c);
        // what is not the tick's runs now; the command and the config writes wait for it
        Assert.Equal(["ui-test:nova"], _game.Casts);
        Assert.Equal([("Hello", ToastKind.Info)], _game.Toasts);
        Assert.True(UiRuntime.FindWindow(Mod, "other")!.IsOpen);
        Assert.Empty(_game.Commands);
        Assert.Empty(_clock.Sets);
        UiRuntime.Pump();
        Assert.Equal(["mymod-reset 5"], _game.Commands);
        Assert.Equal([("clock.show", (object?)false), ("clock.size", (object?)20)], _clock.Sets.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray());
        Assert.Contains(_game.Toasts, t => t == ("done", ToastKind.Info));
        // and the other window closes itself
        var o = new FakeCanvas();
        o.Clicks.Add("Back");
        UiRuntime.FindWindow(Mod, "other")!.Draw(o);
        Assert.False(UiRuntime.FindWindow(Mod, "other")!.IsOpen);
    }

    [Fact]
    public void A_failing_command_and_a_missing_window_are_toasts_not_exceptions()
    {
        _game.CommandResult = (false, "Unknown command 'x'");
        Define("settings", Window(Button("Run", new UiAction { Command = "x" })));
        var w = Open();
        var c = new FakeCanvas();
        c.Clicks.Add("Run");
        w.Draw(c);
        UiRuntime.Pump();
        Assert.Equal([("Unknown command 'x'", ToastKind.Error)], _game.Toasts);
        UiActions.Run(new WindowActionPlan(Mod, "gone", true), Mod, "test");
        Assert.Equal(("ui-test:gone is not a window this game has", ToastKind.Warning), _game.Toasts[1]);
    }

    [Fact]
    public void Commands_waiting_for_the_tick_are_limited()
    {
        for (var i = 0; i < 70; i++) UiActions.Run(new CommandPlan("x"), Mod, "test");
        Assert.Equal(6, _game.Toasts.Count);
        Assert.All(_game.Toasts, t => Assert.Contains("too many", t.Text));
        UiRuntime.Pump();
        Assert.Equal(64, _game.Commands.Count);
    }

    // ---- a window in another language is made again ---------------------------------------------------------------------------

    [Fact]
    public void A_window_title_follows_the_language_when_it_is_next_opened()
    {
        Define("settings", new WindowBlock { Title = UiText.Of(("en", "Clock"), ("de", "Uhr")), Rows = [Text("x")] });
        Assert.Equal("Clock", Assert.Single(_game.Surfaces).Title);
        _game.Language = "de-de";
        Open();
        var s = Assert.Single(_game.Surfaces);
        Assert.Equal("Uhr", s.Title);
        Assert.True(s.IsOpen);
        // a screen's title follows by itself: it is never made again
        Define("screen", new WindowBlock { Kind = WindowKind.Screen, Title = UiText.Of(("en", "Clock"), ("de", "Uhr")), Rows = [Text("x")] });
        _game.Language = "en-us";
        var screenSurface = _game.Surfaces.Single(x => x.Window.Plan.Name == "screen");
        UiRuntime.FindWindow(Mod, "screen")!.Open();
        Assert.Same(screenSurface, _game.Surfaces.Single(x => x.Window.Plan.Name == "screen"));
    }

    // ---- HUD lines -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_HUD_line_shows_while_its_condition_holds_and_updates_at_its_own_pace()
    {
        Define("clock", new HudLineBlock { Text = "{config.clock.label}: {config.clock.size}", When = "config.clock.show", Anchor = HudAnchor.BottomLeft, Color = "#ff0000", Refresh = 2 });
        Assert.Empty(_game.Huds);          // nothing until the pump has looked
        UiRuntime.Pump();
        var hud = Assert.Single(_game.Huds);
        Assert.Equal(("ui-test:clock", "now: 14", HudAnchor.BottomLeft, new Color(255, 0, 0)), (hud.Id, hud.Text, hud.Anchor, hud.Color));
        _clock.Values["clock.size"] = 20;
        _now += 1000;
        UiRuntime.Pump();
        Assert.Equal("now: 14", hud.Text);  // 1 s of 2
        _now += 1000;
        UiRuntime.Pump();
        Assert.Equal("now: 20", hud.Text);
        // hidden, it is not on the screen at all, so the overlay has nothing to draw for it
        _clock.Values["clock.show"] = false;
        _now += 2000;
        UiRuntime.Pump();
        Assert.Empty(_game.Huds);
        _clock.Values["clock.show"] = true;
        _now += 2000;
        UiRuntime.Pump();
        Assert.Single(_game.Huds);
    }

    [Fact]
    public void The_pump_reads_nothing_between_refreshes()
    {
        var reads = 0;
        _clock.Values["clock.size"] = 1;
        Define("clock", new HudLineBlock { Text = "{game.fps} {config.clock.size}", When = "config.clock.size > 0", Refresh = 1 });
        UiConfigs.Find = id => { reads++; return id == _clock.Id ? _clock : null; };
        for (var i = 0; i < 100; i++)
        {
            UiRuntime.Pump();
            _now += 5;
        }
        Assert.Equal(1, reads);   // one refresh in 500 ms
        _now += 1000;
        UiRuntime.Pump();
        Assert.Equal(2, reads);
    }

    [Fact]
    public void A_HUD_line_that_throws_is_switched_off_and_said()
    {
        var mod = NewMod();
        Define("clock", new HudLineBlock { Text = "{config.clock.size}" }, mod);
        UiRuntime.Pump();
        Assert.Single(_game.Huds);
        UiConfigs.Find = _ => throw new ArgumentException("boom");
        _now += 1000;
        UiRuntime.Pump();
        Assert.Empty(_game.Huds);
        _now += 5000;
        UiConfigs.Find = id => id == _clock.Id ? _clock : null;
        UiRuntime.Pump();
        Assert.Empty(_game.Huds);   // faulted: it stays off
    }

    // ---- keys ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_key_binds_under_its_id_with_the_players_description_and_runs_its_action()
    {
        Define("open", new KeyBindingBlock { Key = "F8", Description = "Open the clock", Category = "Clock", OnGameScreens = true, Does = new UiAction { Toast = "pressed" } });
        var (plan, category, press) = Assert.Single(_game.Keys);
        Assert.Equal(("ui-test:open", "Clock", "F8", true), (plan.Id, category, plan.Block.Key, plan.Block.OnGameScreens));
        press();
        Assert.Equal([("pressed", ToastKind.Info)], _game.Toasts);
    }

    [Fact]
    public void A_key_that_toggles_a_config_value_writes_it_on_the_tick()
    {
        Define("flip", new KeyBindingBlock { Key = "Ctrl+K", Does = new UiAction { Toggle = "config.clock.show" } });
        _game.Keys[0].Press();
        _game.Keys[0].Press();
        _game.Keys[0].Press();
        UiRuntime.Pump();
        // pressed three times before the tick: the last press wins, which is a flip of a flip of a flip
        Assert.Equal([("clock.show", (object?)false)], _clock.Sets);
    }

    // ---- reload and a server -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_cross_mod_language_change_keeps_the_window_with_its_original_owner()
    {
        var previous = ModsBackend.Current;
        var current = HostAccess.Current;
        var a = HostAccess.CreateContext(new ModInfo("ui-caller", "Caller", "1", "."), 1, ".");
        var b = HostAccess.CreateContext(new ModInfo(Mod, "Window owner", "1", "."), 2, ".");
        ModsBackend.Current = new SnapshotMods(new ModEntry(Mod, "Window owner", "1", ".", ModSide.Client, ModState.Loaded, null, [], 0, b.Info));
        _game.RealSurfaces = true;
        static UiWindow[] RealWindows() => (UiWindow[])typeof(Ui).Assembly.GetType("Kiln.UiEngine")!
            .GetField("_windows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        try
        {
            HostAccess.Current = b;
            Define("settings", new WindowBlock { Title = UiText.Of(("en", "English owned window"), ("de", "Deutsch owned window")), Rows = [Text("still here")] });
            var window = UiRuntime.FindWindow(Mod, "settings")!;
            window.Close();
            _game.Language = "de";
            HostAccess.Current = a;
            UiRuntime.Register(new KeyPlan("ui-caller", "open", "ui-caller:open", new KeyBindingBlock
            {
                Key = "F8", Does = new UiAction { OpenWindow = Mod + ":settings" },
            }, KeyChord.Parse("F8"), new WindowActionPlan(Mod, "settings", true)));
            _game.Keys.Single().Press();
            Assert.Same(a, HostAccess.Current);
            Assert.Contains(RealWindows(), w => w.Title == "Deutsch owned window");
            a.Retire();
            Assert.Contains(RealWindows(), w => w.Title == "Deutsch owned window");
            window.Close();
            HostAccess.Current = null;
            window.Open();
            Assert.Contains(RealWindows(), w => w.Title == "Deutsch owned window" && w.Open);
            var canvas = new FakeCanvas(); window.Draw(canvas);
            Assert.Contains("text:still here", canvas.Calls);
        }
        finally
        {
            a.Retire(); b.Retire();
            HostAccess.Current = current;
            ModsBackend.Current = previous;
            _game.RealSurfaces = false;
        }
    }

    [Fact]
    public void Hidden_ability_collisions_use_the_registration_case_comparer()
    {
        var mod = NewMod();
        Lower(mod, "burst", new EffectBlock { Heal = 1 });
        Lower(mod, "Burst", new EffectBlock { Heal = 2 });
        Says(Refused(Window(Button("One", new UiAction { ApplyEffect = "burst" }), Button("Two", new UiAction { ApplyEffect = "Burst" })), mod),
            "rows[1].does.applyEffect", "two different effects");
        mod = NewMod();
        Lower(mod, "burst", new EffectBlock { Heal = 1 });
        Says(Refused(new AbilityBlock { Name = "Authored", OnCast = [new EffectBlock()] }, mod, "ui-apply.Burst"), "name", "reserved");
    }

    [Theory]
    [InlineData("ui-apply.burst", null, "name")]
    [InlineData("UI-APPLY.Burst", null, "name")]
    [InlineData("authored", "ui-apply.burst", "was")]
    [InlineData("authored", "UI-APPLY.Burst", "was")]
    public void Authored_abilities_cannot_claim_hidden_names_or_aliases(string name, string? was, string field)
    {
        var mod = NewMod();
        Lower(mod, "burst", new EffectBlock { Heal = 1 });
        Lower(mod, "settings", Window(Button("Heal", new UiAction { ApplyEffect = "burst" })));
        Says(Refused(new AbilityBlock { Name = "Authored", Was = was, OnCast = [new EffectBlock { Heal = 20 }] }, mod, name), field, "reserved");
    }

    [Fact]
    public void Generated_abilities_retire_without_the_data_poll_and_reloads_do_not_accumulate_or_remove_other_abilities()
    {
        var previous = ModsBackend.Current;
        const string hidden = Mod + ":ui-apply.test";
        const string ordinary = Mod + ":ordinary";
        AbilityDefinition Definition(string id) => new() { Id = id, OnCast = _ => Task.CompletedTask };
        var nonUi = AbilityRuntime.Register(Definition(ordinary));
        ModContext? context = null;
        AbilityHandle? old = null;
        try
        {
            for (var generation = 0; generation < 3; generation++)
            {
                context = HostAccess.CreateContext(new ModInfo(Mod, "UI", "1.0", "."), (ulong)(generation + 1), ".");
                ModsBackend.Current = new SnapshotMods(new ModEntry(Mod, "UI", "1.0", ".", ModSide.Server, ModState.Loaded, null, [], 0, context.Info));
                AbilityHandle registered = null!;
                Assert.True(BlocksBridge.RunAsUiOwner(Mod, () => registered = AbilityRuntime.Register(Definition(hidden))));
                UiOwnedAbilities.Track(Mod, registered);
                if (old != null) AbilityRuntime.Remove(hidden, old);
                Assert.Equal(2, Abilities.Registered.Count(a => a.Id.StartsWith(Mod + ":", StringComparison.Ordinal)));
                Assert.Same(registered, AbilityRuntime.Find(hidden));
                context.Retire(); // no ModBlocks.Reset, and the snapshot deliberately still says loaded
                Assert.Null(AbilityRuntime.Find(hidden));
                Assert.Equal([ordinary], Abilities.Registered.Where(a => a.Id.StartsWith(Mod + ":", StringComparison.Ordinal)).Select(a => a.Id));
                old = registered;
            }
        }
        finally
        {
            context?.Retire();
            UiOwnedAbilities.Forget(Mod);
            AbilityRuntime.Remove(ordinary, nonUi);
            ModsBackend.Current = previous;
        }
    }

    [Fact]
    public void JSON_UI_uses_the_real_owner_and_retires_before_the_data_poll_or_snapshot_changes()
    {
        var previous = ModsBackend.Current;
        var subscribe = UiRuntime.SubscribeTick;
        var stopped = 0;
        UiRuntime.SubscribeTick = _ => new Handle(() => stopped++);
        var context = HostAccess.CreateContext(new ModInfo(Mod, "UI", "1.0", "."), 123, ".");
        var entry = new ModEntry(Mod, "UI", "1.0", ".", ModSide.Client, ModState.Loaded, null, [], 0, context.Info);
        ModsBackend.Current = new SnapshotMods(entry);
        try
        {
            // The data service has no CurrentMod, and its cached ModEntry still says loaded after retirement.
            Assert.Null(ModContext.CurrentMod);
            var mod = Define("settings", Window(Button("Run", new UiAction { Command = "stale" }),
                new ToggleRow { Label = "Flip", Bind = "config.clock.show" }));
            Define("clock", new HudLineBlock { Text = "visible" }, mod);
            Define("run", new KeyBindingBlock { Key = "F8", Does = new UiAction { Command = "stale-key" } }, mod);
            var copiedPress = _game.Keys[0].Press;
            var window = Open();
            UiRuntime.Pump();
            Assert.Single(_game.Huds);
            var frame = new FakeCanvas();
            frame.Clicks.Add("Run"); frame.Flips.Add("Flip");
            window.Draw(frame);
            copiedPress();
            context.Retire();
            Assert.True(Mods.IsLoaded(Mod)); // deliberately stale snapshot, no Reset or polling cleanup
            Assert.Empty(_game.Surfaces);
            Assert.Empty(_game.Menus);
            Assert.Empty(_game.Huds);
            Assert.Empty(_game.Keys);
            Assert.Null(UiRuntime.FindWindow(Mod, "settings"));
            Assert.Equal(1, stopped);
            copiedPress();
            var staleFrame = new FakeCanvas(); staleFrame.Clicks.Add("Run");
            window.Draw(staleFrame);
            _now += 1000;
            UiRuntime.Pump();
            Assert.Empty(staleFrame.Calls);
            Assert.Empty(_clock.Sets);
            Assert.Empty(_game.Commands);
        }
        finally { context.Retire(); ModsBackend.Current = previous; UiRuntime.SubscribeTick = subscribe; }
    }

    [Fact]
    public void A_captured_owner_guard_rejects_a_key_even_if_its_retirement_notification_is_missed()
    {
        var alive = true;
        UiLifetime.TrackOwner = (_, _) => (() => alive, new Handle(() => { }));
        try
        {
            Define("run", new KeyBindingBlock { Key = "F8", Does = new UiAction { Command = "stale" } });
            var press = _game.Keys[0].Press;
            press();
            alive = false;
            press();
            UiRuntime.Pump();
            Assert.Empty(_game.Commands);
            Assert.Empty(_game.Keys);
        }
        finally { UiLifetime.TrackOwner = BlocksBridge.TrackUiOwner; }
    }

    [Fact]
    public void Unload_cancels_queued_work_and_copied_callbacks_but_keeps_another_mods_work()
    {
        var mod = Define("settings", Window(
            Button("Run", new UiAction { Command = "old-command" }),
            new ToggleRow { Label = "Flip", Bind = "config.clock.show" },
            new SliderRow { Label = "Size", Bind = "config.clock.size" }));
        Define("tell", new KeyBindingBlock { Key = "F8", Does = new UiAction { Toast = "old-key" } }, mod);
        var copiedPress = _game.Keys[0].Press;
        var oldWindow = Open();
        var frame = new FakeCanvas();
        frame.Clicks.Add("Run");
        frame.Flips.Add("Flip");
        frame.Slides["Size"] = 20;
        oldWindow.Draw(frame);
        var other = new ModBlocks("ui-other", 2) { ManifestClients = "mod" };
        Define("run", new KeyBindingBlock { Key = "F9", Does = new UiAction { Command = "other-command" } }, other);
        _game.Keys.Single(k => k.Plan.Mod == "ui-other").Press();
        mod.Reset();
        copiedPress();
        var staleFrame = new FakeCanvas();
        staleFrame.Clicks.Add("Run");
        oldWindow.Draw(staleFrame);
        oldWindow.Open();
        _now += 1000;
        UiRuntime.Pump();
        Assert.Empty(staleFrame.Calls);
        Assert.Empty(_clock.Sets);
        Assert.Equal([("done", ToastKind.Info)], _game.Toasts);
        Assert.Equal(["other-command"], _game.Commands);
        Assert.Single(_game.Keys);
        Assert.Empty(_game.Surfaces);
    }

    [Fact]
    public void Replacing_a_window_or_key_cancels_its_old_callbacks_and_pending_commands()
    {
        var mod = Define("settings", Window(Button("Run", new UiAction { Command = "old-window" })));
        var old = Open();
        var frame = new FakeCanvas();
        frame.Clicks.Add("Run");
        old.Draw(frame);
        Define("run", new KeyBindingBlock { Key = "F8", Does = new UiAction { Command = "old-key" } }, mod);
        var copiedPress = _game.Keys[0].Press;
        copiedPress();
        Define("settings", Window(Text("new")), mod);
        Define("run", new KeyBindingBlock { Key = "F9", Does = new UiAction { Command = "new-key" } }, mod);
        copiedPress();
        _game.Keys[0].Press();
        UiRuntime.Pump();
        Assert.Equal(["new-key"], _game.Commands);
    }

    [Fact]
    public void The_UI_tick_is_removed_after_the_last_mod_and_can_start_again()
    {
        var subscribed = 0;
        var disposed = 0;
        UiRuntime.SubscribeTick = _ => { subscribed++; return new Handle(() => disposed++); };
        try
        {
            var first = Define("settings", Window(Text("x")));
            var second = new ModBlocks("ui-other", 2) { ManifestClients = "mod" };
            Define("clock", new HudLineBlock { Text = "x" }, second);
            Assert.Equal(1, subscribed);
            first.Reset();
            Assert.Equal(0, disposed);
            second.Reset();
            Assert.Equal(1, disposed);
            Define("clock", new HudLineBlock { Text = "x" });
            Assert.Equal(2, subscribed);
        }
        finally
        {
            UiRuntime.ResetForTests(_game);
            UiRuntime.SubscribeTick = static tick => BlocksBridge.AddTick("kiln blocks ui", tick);
        }
    }

    [Fact]
    public void A_deferred_check_from_the_previous_load_cannot_consume_the_new_loads_names()
    {
        var checks = new List<Action>();
        UiCompile.Later = (_, run) => checks.Add(run);
        var mod = Define("old", Window(Button("Go", new UiAction { OpenWindow = "old-missing" })));
        mod.Reset();
        Define("new", Window(Button("Go", new UiAction { OpenWindow = "new-missing" })), mod);
        Assert.Equal(2, checks.Count);
        checks[0]();
        Assert.Empty(mod.Errors);
        checks[1]();
        Assert.Contains("new-missing", Assert.Single(mod.Errors));
    }

    [Fact]
    public void C_sharp_enums_and_slider_endpoints_are_checked_before_any_surface_is_made()
    {
        Assert.Equal("kind", Refused(Window(Text("x")) with { Kind = (WindowKind)42 }).Field);
        Assert.Equal("menu.in", Refused(Window(Text("x")) with { Menu = new UiMenu { Text = "Go", In = (UiMenuIn)42 } }).Field);
        Install(new FakeConfig("ui-test-client", false, Entry("large", "int", 0), Entry("decimal", "float", 0.0)));
        Assert.Equal("rows[0].min", Refused(Window(new SliderRow { Label = "x", Bind = "config.large", Min = 1e12, Max = 1e12 + 1 })).Field);
        Assert.Equal("rows[0].min", Refused(Window(new SliderRow { Label = "x", Bind = "config.large", Min = int.MaxValue - 1, Max = int.MaxValue })).Field);
        Assert.Equal("rows[0].min", Refused(Window(new SliderRow { Label = "x", Bind = "config.decimal", Min = 0, Max = 1e300 })).Field);
        Assert.Equal("rows[0].min", Refused(Window(new SliderRow { Label = "x", Bind = "config.decimal", Min = 0, Max = float.MaxValue })).Field);
        Assert.Equal("rows[0].min", Refused(Window(new SliderRow { Label = "x", Bind = "config.decimal", Min = 1e10, Max = 1e10 + 1 })).Field);
        Assert.Empty(_game.Surfaces);
    }

    [Fact]
    public void Defining_a_block_again_replaces_what_it_made_and_forgetting_a_mod_takes_everything_away()
    {
        var mod = Settings();
        Define("clock", new HudLineBlock { Text = "x" }, mod);
        Define("open", new KeyBindingBlock { Key = "F8", Does = new UiAction { Toast = "x" } }, mod);
        UiRuntime.Pump();
        Assert.Equal((1, 2, 1, 1), (_game.Surfaces.Count, _game.Menus.Count, _game.Huds.Count, _game.Keys.Count));
        var first = _game.Surfaces[0];
        Settings(Window(Text("x")), "settings");
        Assert.True(first.Disposed);
        Assert.Single(_game.Surfaces);
        Define("open", new KeyBindingBlock { Key = "F9", Does = new UiAction { Toast = "x" } }, mod);
        Assert.Equal("F9", Assert.Single(_game.Keys).Plan.Block.Key);
        mod.Reset();
        Assert.Equal((0, 0, 0, 0), (_game.Surfaces.Count, _game.Menus.Count, _game.Huds.Count, _game.Keys.Count));
        Assert.Null(UiRuntime.FindWindow(Mod, "settings"));
        // the pump has nothing left to refresh
        UiRuntime.Pump();
        Assert.Empty(_game.Huds);
    }

    [Fact]
    public void On_a_server_the_blocks_are_checked_and_kept_and_nothing_runs()
    {
        _game.Available = false;
        var mod = NewMod();
        Define("settings", Json<WindowBlock>(WindowJson), mod);
        Define("fps", new HudLineBlock { Text = "{game.fps}" }, mod);
        Define("open", new KeyBindingBlock { Key = "F8", Does = new UiAction { OpenWindow = "settings" } }, mod);
        Assert.Equal(["fps", "open", "settings"], mod.Handles.Keys.Order());
        Assert.Empty(mod.Errors);
        Assert.Equal((0, 0, 0, 0), (_game.Surfaces.Count, _game.Menus.Count, _game.Huds.Count, _game.Keys.Count));
        Assert.Null(UiRuntime.FindWindow(Mod, "settings"));
        UiRuntime.Pump();
        Assert.Empty(_game.Huds);
        Assert.Empty(_game.Toasts);
        // and a mistake is a mistake on a server too: the data is the same on both sides
        Assert.Throws<BlockError>(() => Define("bad", new HudLineBlock { Text = "{game.nothing}" }, mod));
    }

    [Fact]
    public void A_window_whose_surface_cannot_be_made_is_said_and_the_mods_other_blocks_stay()
    {
        var mod = NewMod();
        _game.FailSurfaces = true;
        Settings(Window(Text("x")), "broken");
        _game.FailSurfaces = false;
        Define("open", new KeyBindingBlock { Key = "F8", Does = new UiAction { Toast = "x" } }, mod);
        Assert.Single(_game.Keys);
        Assert.Null(UiRuntime.FindWindow(Mod, "broken"));
        Assert.Empty(_game.Surfaces);
    }

    // ---- data files, through the loader ----------------------------------------------------------------------------------------

    [Fact]
    public void The_loader_names_the_file_the_block_and_the_field()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-ui-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "blocks"));
            File.WriteAllText(Path.Combine(dir, "blocks", "ui.json"), """
                { "format": 1,
                  "windows": {
                    "good": { "title": "Good", "rows": [ { "type": "text", "text": "ok {game.fps:0}" } ] },
                    "bad-field": { "title": "Bad", "rows": [ { "type": "toggle", "label": "T", "bind": "config.clock.scale" } ] },
                    "bad-shape": { "title": "Bad", "rows": [ { "type": "dial" } ] } },
                  "hudLines": { "bad-hud": { "text": "x", "refresh": 0 } },
                  "keyBindings": { "bad-key": { "key": "Hyper+9", "does": { "toast": "x" } } } }
                """);
            var mod = NewMod();
            Assert.Equal(1, BlockJson.LoadMod(mod, dir));
            Assert.Equal(["good"], mod.Handles.Keys);
            Assert.Contains(mod.Errors, e => e.StartsWith("blocks/ui.json: block window bad-field, rows[0].bind: config.ui-test-client/clock.scale is not a true/false config entry", StringComparison.Ordinal));
            Assert.Contains(mod.Errors, e => e.StartsWith("blocks/ui.json: windows.bad-shape", StringComparison.Ordinal));
            Assert.Contains(mod.Errors, e => e == "blocks/ui.json: block hudline bad-hud, refresh: 0 is outside 0.1 to 3600 seconds: a window or a line updates at a pace, never every frame");
            Assert.Contains(mod.Errors, e => e == "blocks/ui.json: block keybinding bad-key, key: unknown key 'Hyper+9' (examples: F8, Ctrl+K, Shift+Insert, Numpad5, None)");
            Assert.Equal(4, mod.Errors.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_sections_are_the_ones_the_schema_and_the_build_know()
    {
        var sections = UiJson.Kinds.Select(k => k.Section).ToArray();
        Assert.Equal(["windows", "hudLines", "keyBindings"], sections);
        Assert.All(new[] { typeof(WindowBlock), typeof(HudLineBlock), typeof(KeyBindingBlock) },
            t => Assert.Equal(ClientsLevel.Mod, ((BlockClientsAttribute)Attribute.GetCustomAttribute(t, typeof(BlockClientsAttribute))!).Level));
    }
}
