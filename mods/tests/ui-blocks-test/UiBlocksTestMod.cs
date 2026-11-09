// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;

namespace UiBlocksTest;

public sealed class HudSettings
{
    public HudSection Hud { get; set; } = new();
}

public sealed class HudSection
{
    public bool Show { get; set; } = true;
    public string Label { get; set; } = "lab";
    [Range(0, 100)] public int Volume { get; set; } = 40;
    [Range(0.5, 3.0)] public double Scale { get; set; } = 1.5;
    public Mood Mood { get; set; } = Mood.Calm;
}

public enum Mood { Calm, Busy, Loud }

/// <summary>
/// UI blocks on a dedicated server (docs/kiln/modding/ui-blocks.md): lines "ui-blocks-test: PASS|FAIL|INFO ..." in kiln.log. They compile in code and in blocks/ui.json, the two are the
/// same blocks, every mistake is named by block and field, an applied effect's hidden ability is registered and casts, windows that open each other have no errors, and nothing is drawn
/// or bound here. A client draws the same blocks: F8 opens "main", whose button opens the settings screen (the docs' client checklist).
/// </summary>
public sealed class UiBlocksTestMod : Mod
{
    private const string Me = "ui-blocks-test";
    private readonly List<ImpactBuffEvent> _buffs = new();
    private readonly List<EntityId> _spawned = new();
    private int _pass, _fail;

    private void Info(string s) => Log.Info($"ui-blocks-test: INFO {s}");

    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"ui-blocks-test: PASS {what}"); }
        else { _fail++; Log.Error($"ui-blocks-test: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    public override void OnLoad()
    {
        // the config first: the blocks bind to its entries by path
        Config.Register<HudSettings>();
        Info($"OnLoad: side {Mods.Side}, {(Client.Available && Client.IsClient ? $"client {Client.State}" : "dedicated server")}, overlay {(Ui.Available ? "present" : "absent")}");
        try
        {
            DefineTwin("twin-code");
        }
        catch (Exception e)
        {
            Check(false, $"defining the C# twin: {e.Message}");
        }
        if (!Client.IsDedicatedServer) return;
        Events.On<ImpactNativeEvent>(e => { if (e is ImpactBuffEvent b) _buffs.Add(b); });
        _ = Run();
    }

    public override void OnUnload() => Info($"OnUnload: summary: {_pass} PASS, {_fail} FAIL");

    // "twin-json" in blocks/ui.json, as C# records
    private static void DefineTwin(string name) => Blocks.Define(name, new WindowBlock
    {
        Title = "Twin",
        Refresh = 1,
        Rows =
        [
            new TextRow { Text = "{game.clock} {config.hud.label}", Color = "orange", When = "game.inGame" },
            new ToggleRow { Label = "Show", Bind = "config.hud.show", Tooltip = "tip" },
            new SeparatorRow { Text = "Heading" },
            new ButtonRow { Label = "Back", Does = new UiAction { Toggle = "config.hud.show" } },
        ],
    });

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(60);
            Compiled();
            Twins();
            Inert();
            Mistakes();
            await Hidden();
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        foreach (var e in _spawned)
            if (World.Exists(e)) Spawn.Destroy(e);
        Log.Info($"ui-blocks-test done: {_pass} PASS, {_fail} FAIL");
    }

    private static JsonObject? Window(string name) => JsonNode.Parse(Blocks.Lowered(name, Me) ?? "{}")?["window"] as JsonObject;

    private void Compiled()
    {
        var report = Blocks.Report();
        var errors = report.TryGetValue(Me, out var r) ? r.Errors : [];
        Check(errors.Count == 0, $"no errors in the data or the code ({errors.Count}){(errors.Count > 0 ? ": " + string.Join(" | ", errors) : "")}");
        // two window blocks open each other, one by a name that was not compiled yet: looked at once the pass was over, with nothing to say
        foreach (var name in new[] { "main", "settings", "twin-json", "twin-code", "fps", "open-main" })
            Check(Blocks.Find(name, Me) != null, $"block {name} is defined");
        Check(Blocks.Find("main", Me) is BlockHandle<WindowBlock>, "main is a window");
        Check(Blocks.Find("fps", Me) is BlockHandle<HudLineBlock>, "fps is a HUD line");
        Check(Blocks.Find("open-main", Me) is BlockHandle<KeyBindingBlock> k && k.GameName == $"{Me}:open-main", $"open-main is a key binding with the id {Me}:open-main");
        var settings = Window("settings");
        var rows = settings?["rows"] as JsonArray;
        Check(rows is { Count: 10 }, $"settings lowered to {rows?.Count} rows");
        Check((string?)rows?[0]?["bind"] == $"config.{Me}/hud.show", $"its toggle binds {rows?[0]?["bind"]}");
        Check(string.Join("..", (rows?[2]?["range"] as JsonArray ?? []).Select(x => (double)x!)) == "1..2", "its slider has the range the block gave");
        Check(string.Join("..", (rows?[1]?["range"] as JsonArray ?? []).Select(x => (double)x!)) == "0..100", "and the other the config's own range");
        Check(string.Join(",", (rows?[3]?["options"] as JsonArray ?? []).Select(x => (string?)x)) == "Calm,Busy,Loud", "its choice has the enum's options");
        Check((string?)rows?[5]?["does"]?["castAbility"] == $"{Me}:nova", "its cast button names the ability by id");
        Check((string?)rows?[6]?["does"]?["viaAbility"] == $"{Me}:ui-apply.calm-fx", "its apply button goes through a hidden ability");
        Check((string?)rows?[7]?["does"]?["viaAbility"] == $"{Me}:ui-apply.settings.rows.7.does", "and so does an effect written in place");
        Check(Blocks.Warnings(Me).Count == 0, "no warnings");
    }

    private void Twins()
    {
        var a = Window("twin-code")?["rows"]?.ToJsonString();
        var b = Window("twin-json")?["rows"]?.ToJsonString();
        Check(a != null && a == b, "the same window in C# and in data lowers to the same rows");
        Check(Window("twin-code")?["title"]?.ToString() == Window("twin-json")?["title"]?.ToString() && Window("twin-code")?["refresh"]?.ToString() == Window("twin-json")?["refresh"]?.ToString(), "with the same title and refresh");
    }

    private void Inert()
    {
        Check(!Ui.Available, "Kiln.Ui has no overlay on a dedicated server");
        Check(!KeyBindings.All.Any(k => k.Id.StartsWith(Me + ":", StringComparison.Ordinal)), "no key binding was made");
        Check(Ui.OpenScreens.Count == 0, "no screen is open");
        Check(!Ui.Status.Hooked, "the overlay is not hooked: there is nothing to draw a window or a HUD line on");
    }

    private void Refused(string what, string block, string field, Func<BlockHandle> define)
    {
        try
        {
            define();
            Check(false, $"{what}: nothing was refused");
        }
        catch (BlockError e)
        {
            Check(e.Field == field && e.Message.Contains(block, StringComparison.Ordinal), $"{what}: refused at {e.Field}: {e.Problem}");
        }
    }

    private void Mistakes()
    {
        Refused("a toggle on a number", "window bad-toggle", "rows[0].bind", () => Blocks.Define("bad-toggle", new WindowBlock { Title = "T", Rows = [new ToggleRow { Label = "x", Bind = "config.hud.scale" }] }));
        Refused("a game value that is none", "window bad-value", "rows[0].text", () => Blocks.Define("bad-value", new WindowBlock { Title = "T", Rows = [new TextRow { Text = "{game.fpz}" }] }));
        Refused("a config path that is none", "window bad-path", "rows[0].bind", () => Blocks.Define("bad-path", new WindowBlock { Title = "T", Rows = [new ValueRow { Label = "x", Bind = "config.hud.nope" }] }));
        Refused("a button with two actions", "window bad-action", "rows[0].does", () => Blocks.Define("bad-action", new WindowBlock { Title = "T", Rows = [new ButtonRow { Label = "x", Does = new UiAction { Command = "a", Toast = "b" } }] }));
        Refused("an ability that is none", "window bad-ability", "rows[0].does.castAbility", () => Blocks.Define("bad-ability", new WindowBlock { Title = "T", Rows = [new ButtonRow { Label = "x", Does = new UiAction { CastAbility = "nope" } }] }));
        Refused("a refresh that is every frame", "hudline bad-refresh", "refresh", () => Blocks.Define("bad-refresh", new HudLineBlock { Text = "x", Refresh = 0 }));
        Refused("a key that is none", "keybinding bad-key", "key", () => Blocks.Define("bad-key", new KeyBindingBlock { Key = "Hyper+Z", Does = new UiAction { Toast = "x" } }));
        Refused("a condition that is not one", "hudline bad-when", "when", () => Blocks.Define("bad-when", new HudLineBlock { Text = "x", When = "game.fps <" }));
        Check(Blocks.Find("bad-toggle", Me) == null, "a refused block is not defined");
    }

    // The hidden ability of an applied effect is a real ability: registered, and it casts what the effect says.
    private async Task Hidden()
    {
        var id = $"{Me}:ui-apply.calm-fx";
        Check(Abilities.Registered.Any(a => a.Id == id), "the hidden ability ui-apply.calm-fx is registered with kiln.abilities");
        Check(Abilities.Registered.Any(a => a.Id == $"{Me}:nova"), "and the ability a button casts");
        var at = await FindPlace();
        var caster = await Spawn.Entity("1_Player", at, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _spawned.Add(caster);
        await Steps(10);
        var calm = Blocks.Find("calm", Me)!;
        _buffs.Clear();
        var r = Abilities.Cast(caster, id);
        Check(r.Ok, $"the hidden ability casts: {r.Reason}");
        await Steps(20);
        Check(_buffs.Any(b => b.Buff == calm.Guid && b.Target == caster), $"and the effect it carries happened: status calm applied ({_buffs.Count} buff event(s))");
        var nova = Abilities.Cast(caster, $"{Me}:nova");
        Check(nova.Ok, $"the ability a button casts casts too: {nova.Reason}");
    }

    private async Task<WorldPosition> FindPlace()
    {
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf) return new WorldPosition(inf.Position.X + 60, inf.Position.Y + 0.5, inf.Position.Z + 60);
        }
        await Steps(1);
        throw new InvalidOperationException("no anchor entity");
    }
}
