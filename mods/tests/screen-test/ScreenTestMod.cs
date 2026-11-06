// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;

namespace ScreenTest;

/// <summary>
/// kiln.screen and ScreenEffectBlock test (docs/kiln/services/screen.md). Lines "screen-test: PASS|FAIL|INFO ..." in kiln.log.
/// <list type="bullet">
/// <item>Dedicated server: the blocks in code and in blocks/screen.json are the same, mistakes are refused by block and field, a status's program signals
/// when it starts and when it ends, and nothing draws here. <c>/screentest relay</c> from the console folder, with fixtures/screen_client.py connected,
/// checks what the relay sends byte by byte.</item>
/// <item>Client: each effect on a key, for a person to look at. Ctrl+8 a white flash with a vignette, half a second. Ctrl+Shift+8 a cold grade, on
/// and off. Ctrl+Alt+8 a fade to black and back. Ctrl+Alt+9 all eight at once and a ninth refused, then stops them. Ctrl+Alt+0 (hosting) puts the
/// status frozen-json on you for 3 s: the cold grade while it lasts, a flash as it starts.</item>
/// </list>
/// </summary>
public sealed class ScreenTestMod : Mod
{
    private const uint ScreenTags = 0xB1100000;

    private int _pass, _fail;
    private readonly List<(uint Tag, EntityId Entity, ulong Step)> _signals = new();
    private readonly List<EntityId> _spawned = new();
    private ScreenHandle _cold;
    private readonly List<ScreenHandle> _many = new();

    private static readonly ScreenEffectBlock Flash = new() { Fill = new ScreenColor("#ffffff", 0.85f), Vignette = new ScreenVignette { Amount = 0.5f, Radius = 0.3f }, Seconds = 0, FadeOut = 0.5f };
    private static readonly ScreenEffectBlock Cold = new() { Temperature = -0.6f, Saturation = 0.7f, Vignette = new ScreenVignette { Color = "#10243a", Amount = 0.6f }, FadeIn = 1, FadeOut = 1 };
    private static readonly ScreenEffectBlock Fade = new() { Fill = "#000000", FadeIn = 2, Seconds = 1, FadeOut = 2, Order = 10 };
    private static readonly ScreenEffectBlock Hurt = new() { Tint = new ScreenColor("#ff4030", 0.35f), Vignette = new ScreenVignette { Color = "#400000", Amount = 0.8f }, FadeIn = 0.1f, Seconds = 0.3f, FadeOut = 0.6f, Range = 12 };

    private void Info(string s) => Log.Info($"screen-test: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"screen-test: PASS {what}"); }
        else { _fail++; Log.Error($"screen-test: FAIL {what}"); }
        return ok;
    }

    public override void OnLoad()
    {
        var st = ScreenEffects.Status();
        Info($"OnLoad: side {Mods.Side}, {(Client.Available && Client.IsClient ? $"client {Client.State}" : "dedicated server")}, kiln.screen {(st.Native ? "present" : "absent")}, relay {(st.Relay ? "open" : "off")}, {st.Reason}");
        Commands.Register("screentest", OnCommand, usage: "status|relay|flash|cold|fade|many|frozen", description: "kiln.screen test");
        Check(ScreenTestSeams.NativeSizes == (100, 192, 56), $"the managed mirror has the C header's sizes: {ScreenTestSeams.NativeSizes}");

        if (Client.Available && Client.IsClient)
        {
            Check(st.Native, "kiln.screen is in the client");
            Check(st.Enabled || st.Reason.Length > 0, $"on, or it says why not: {st.Reason}");
            KeyBindings.Register("screen-test.flash", "Ctrl+8", () => Play("flash", Flash), "screen-test: a white flash with a vignette");
            KeyBindings.Register("screen-test.cold", "Ctrl+Shift+8", ToggleCold, "screen-test: a cold grade, on and off");
            KeyBindings.Register("screen-test.fade", "Ctrl+Alt+8", () => Play("fade", Fade), "screen-test: fade to black and back");
            KeyBindings.Register("screen-test.many", "Ctrl+Alt+9", Many, "screen-test: eight at once, a ninth refused");
            KeyBindings.Register("screen-test.frozen", "Ctrl+Alt+0", () => Context.Scheduler.Post(() => _ = Frozen()), "screen-test: the status frozen-json on you (hosting)");
        }
        // the same blocks in code as blocks/screen.json has; after the keys, so that a client's keys work whatever happens here
        try
        {
            var flash = Blocks.Define("flash", Flash);
            var cold = Blocks.Define("cold", Cold);
            Blocks.Define("fade", Fade);
            var hurt = Blocks.Define("hurt", Hurt);
            Blocks.Define("frozen", new StatusBlock { Name = "Frozen", Seconds = 3, Screen = cold, OnApply = [new EffectBlock { Screen = flash }] });
            Blocks.Define("flash-hit", new EffectBlock { Screen = hurt });
        }
        catch (Exception e)
        {
            Check(false, $"defining the blocks: {e.Message}");
        }
        if (Lifecycle.IsServerRunning) Context.Scheduler.Post(ServerStart);
    }

    public override void OnUnload() => Info($"OnUnload: summary: {_pass} PASS, {_fail} FAIL");

    [Subscribe]
    private void OnServerStarted(ServerStartedEvent e) => ServerStart();

    [Subscribe]
    private void OnSignal(ImpactSignalEvent e)
    {
        if ((e.Tag & 0xFFFF0000) == ScreenTags) _signals.Add((e.Tag, e.AsEntity, e.Step));
    }

    private void ServerStart()
    {
        if (Client.Available && Client.IsClient) return;  // the suite is a dedicated server's
        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(60);
            Server();
            Twins();
            Mistakes();
            Programs();
            await Statuses();
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        foreach (var e in _spawned)
            if (World.Exists(e)) Spawn.Destroy(e);
        Log.Info($"screen-test done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    // ---- a dedicated server draws nothing, and relays ----

    private void Server()
    {
        var st = ScreenEffects.Status();
        Check(!st.Native && st.Reason.Contains("dedicated server", StringComparison.Ordinal), $"no kiln.screen on a dedicated server: {st.Reason}");
        Check(st.Relay && ScreenEffects.Available, "the relay is open, so a server's play goes to Kiln clients");
        var before = st.Sent;
        var h = ScreenEffects.Play(Flash);
        Check(!h.IsNone && h.ToString().StartsWith("relayed", StringComparison.Ordinal), $"a play from server code is relayed: {h}");
        Check(ScreenEffects.IsPlaying(h), "and counts as playing for its length");
        ScreenEffects.Stop(h, immediate: true);
        Check(!ScreenEffects.IsPlaying(h), "until it is stopped");
        Check(ScreenEffects.Status().Sent >= before, $"with no client connected nothing is sent, and nothing throws (sent {ScreenEffects.Status().Sent - before})");

        // what the client gets is what kiln.screen gets
        foreach (var e in new ScreenEffect[] { Flash, Cold, Fade, Hurt, new() })
            Check(ScreenTestSeams.RoundTrip(e).SequenceEqual(ScreenTestSeams.Native(e)), $"the wire carries every field: {string.Join(",", ScreenTestSeams.Native(e).Select(x => x.ToString("0.###")))}");
        var none = ScreenTestSeams.Native(new ScreenEffect());
        float[] defaults = [0, 1, 0, -1, 0, 0, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0.5f, 0.5f, 0, 0, 0, 0];
        Check(none.SequenceEqual(defaults), "an empty effect is kiln_screen_effect_defaults: it changes nothing");
        var fill = ScreenTestSeams.Native(new ScreenEffect { Fill = "#ff8000", Vignette = 0.6f });
        Check(fill[20] == 1 && Math.Abs(fill[21] - 128 / 255f) < 1e-6 && fill[22] == 0 && fill[23] == 1, "a fill colour is its sRGB values and amount 1");
        Check(fill[14] == 0 && fill[15] == 0 && fill[16] == 0 && Math.Abs(fill[17] - 0.6f) < 1e-6, "a vignette's short form is that much black");

        // what a play refuses
        string Thrown(Action a)
        {
            try { a(); return ""; }
            catch (ArgumentException e) { return e.Message; }
        }
        Check(Thrown(() => ScreenEffects.Play(new ScreenEffect { Exposure = 20 })).Contains("exposure: 20 is outside -8..8", StringComparison.Ordinal), "ScreenEffects.Play names the field out of range");
        Check(Thrown(() => ScreenEffects.Play(new ScreenEffect { Tint = "red" })).Contains("tint.color", StringComparison.Ordinal), "and a colour that is not #rrggbb");
        Check(Thrown(() => ScreenEffects.PlayNear(default, 0, Flash)) != "", "PlayNear needs a radius");
    }

    // ---- the same shape ----

    private void Twins()
    {
        var report = Blocks.Report();
        Check(report.TryGetValue("screen-test", out var mine) && mine.Errors.Count == 0, $"blocks/screen.json loads without errors: {(mine.Errors is { } e ? string.Join("; ", e) : "no report")}");
        foreach (var name in new[] { "flash", "cold", "fade", "hurt" })
        {
            var cs = Blocks.Find(name);
            var json = Blocks.Find($"{name}-json");
            Check(cs is BlockHandle<ScreenEffectBlock> && json is BlockHandle<ScreenEffectBlock>, $"{name}: a screen effect from code and from data");
            var a = JsonNode.Parse(Blocks.Lowered(name) ?? "{}")?["screenEffect"]?.ToJsonString();
            var b = JsonNode.Parse(Blocks.Lowered($"{name}-json") ?? "{}")?["screenEffect"]?.ToJsonString();
            Check(a != null && a == b, $"{name}: C# and blocks/screen.json are the same effect: {a}");
        }
        Check(Blocks.Find("frozen-json") is BlockHandle<StatusBlock> && Blocks.Find("flash-hit-json") is BlockHandle<EffectBlock>, "a status and an effect name them in data");
        Check(Blocks.Find("cold")!.Guid != Blocks.Find("cold-json")!.Guid, "each has its own id");
        var renamed = Blocks.Define("cold-renamed", Cold with { Was = "cold-old" });
        Check(renamed.Guid == BlockIds.Derive("screen-test", "screen", "cold-old"), "Was keeps the id");
    }

    // ---- mistakes, by block and field ----

    private void Mistakes()
    {
        string Refused(Func<object> define)
        {
            try { define(); return ""; }
            catch (BlockError e) { return e.Message; }
        }
        void Says(string what, string message, params string[] parts) =>
            Check(message.Length > 0 && parts.All(p => message.Contains(p, StringComparison.Ordinal)), $"{what}: {(message.Length > 0 ? message : "not refused")}");

        Says("exposure out of range", Refused(() => Blocks.Define("bad1", new ScreenEffectBlock { Exposure = 20 })), "screeneffect bad1", "exposure", "-8..8");
        Says("a colour", Refused(() => Blocks.Define("bad2", new ScreenEffectBlock { Tint = "red" })), "screeneffect bad2", "tint.color", "#rrggbb");
        Says("a vignette's softness", Refused(() => Blocks.Define("bad3", new ScreenEffectBlock { Vignette = new ScreenVignette { Softness = 0 } })), "vignette.softness");
        Says("a range of 0", Refused(() => Blocks.Define("bad4", new ScreenEffectBlock { Range = 0 })), "range", "more than 0");
        Says("an effect's screen without seconds", Refused(() => Blocks.Define("bad5", new EffectBlock { Screen = "cold" })), "effect bad5", "screen.seconds", "nothing would ever stop it");
        Says("a screen that is a status", Refused(() => Blocks.Define("bad6", new EffectBlock { Screen = "frozen" })), "\"frozen\" is a status, not a screen effect");
        Says("a screen that is nothing", Refused(() => Blocks.Define("bad7", new StatusBlock { Name = "x", Seconds = 1, Screen = "no-such" })), "status bad7", "no screen effect \"no-such\"");
        Says("an inline screen's field", Refused(() => Blocks.Define("bad8", new EffectBlock { Screen = new ScreenEffectBlock { Seconds = 1, Gamma = 0 } })), "screen.gamma", "0.1..5");
        Check(Refused(() => Blocks.Define("ok-inline", new EffectBlock { Screen = new ScreenEffectBlock { Seconds = 1, Exposure = -1 } })) == "", "an effect with an inline screen effect and seconds is fine");
        var report = Blocks.Report()["screen-test"].Blocks;
        Check(!report.Any(b => b.StartsWith("bad", StringComparison.Ordinal)), "none of the wrong blocks became anything");
    }

    // ---- what the programs say ----

    private void Programs()
    {
        var dir = Path.Combine(Context.DataDirectory, "programs");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Info($"{Blocks.DumpPrograms(dir)} generated program(s) written to {dir}");
        foreach (var status in new[] { "frozen", "frozen-json" })
        {
            var listing = Directory.GetFiles(dir, "*.txt").Select(File.ReadAllText).FirstOrDefault(t => t.StartsWith($"screen-test {status}.status", StringComparison.Ordinal) || t.StartsWith($"screen-test {status}", StringComparison.Ordinal) && t.Contains(".status", StringComparison.Ordinal));
            Check(listing != null, $"{status}: its program is there");
            if (listing == null) continue;
            var signals = listing.Split('\n').Count(l => l.Contains("kiln.Signal", StringComparison.Ordinal));
            Check(signals == 3, $"{status}: three signals: its screen on, the flash of its OnApply, its screen off ({signals})");
            var order = listing.Split('\n').Where(l => l.Contains("kiln.Signal", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
            // the listing has no sections: that the last runs when the status ends is the run below
            Check(order.Count == 3 && order[0].EndsWith("screenOn)", StringComparison.Ordinal) && order[2].EndsWith("screenOff)", StringComparison.Ordinal),
                $"{status}: on first, off last, for the status's target: {string.Join("; ", order)}");
        }
    }

    // ---- a status on a creature: its program signals as it starts and as it ends ----

    private async Task Statuses()
    {
        var at = await Anchor();
        var victim = await Make("Enemy_Vukah_Melee", at);
        var frozen = Blocks.Find("frozen-json")!;
        _signals.Clear();
        await Abilities.ApplyBuff(victim, frozen.Guid);
        await Steps(25);
        var started = _signals.ToList();
        Info($"after the start: {string.Join(", ", started.Select(s => $"0x{s.Tag:x8} {s.Entity}"))}");
        Check(started.Count(s => (s.Tag & 3) == 2 && s.Entity == victim) == 1, "its screen starts on the creature that has it");
        Check(started.Count(s => (s.Tag & 3) == 0 && s.Entity == victim) == 1, "and its OnApply's flash is for that creature");
        Check(!started.Any(s => (s.Tag & 3) == 3), "nothing stops yet");
        await Steps(25 * 4);
        var all = _signals.ToList();
        var on = all.FirstOrDefault(s => (s.Tag & 3) == 2);
        var off = all.FirstOrDefault(s => (s.Tag & 3) == 3);
        Check(off.Tag == (on.Tag | 1) && off.Entity == victim, $"when it ends its screen stops, for the same creature and effect: 0x{off.Tag:x8}");
        Check(off.Step > on.Step, $"{(off.Step - on.Step) / 25.0:0.0} s later (steps {on.Step} -> {off.Step})");

        // applied again while it runs
        _signals.Clear();
        await Abilities.ApplyBuff(victim, frozen.Guid);
        await Steps(25);
        await Abilities.ApplyBuff(victim, frozen.Guid);
        await Steps(25 * 5);
        Info($"applied twice: {string.Join(", ", _signals.Select(s => (s.Tag & 3) switch { 2 => "on", 3 => "off", 0 => "flash", _ => "?" }))}");
        Check(_signals.Count(s => (s.Tag & 3) == 2) == _signals.Count(s => (s.Tag & 3) == 3), "as many offs as ons, so the count the server keeps returns to nothing");
        Spawn.Destroy(victim);
    }

    private async Task<WorldPosition> Anchor()
    {
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf) return new WorldPosition(inf.Position.X + 40, inf.Position.Y + 0.5, inf.Position.Z + 40);
        }
        await Steps(1);
        throw new InvalidOperationException("no anchor entity");
    }

    private async Task<EntityId> Make(string template, WorldPosition at)
    {
        var e = await Spawn.Entity(template, at, 0, new SpawnOptions { Tag = "screen-test", Flags = SpawnFlags.Transient });
        _spawned.Add(e);
        await Steps(10);
        return e;
    }

    // ---- the console: the relay's bytes, and the client's keys ----

    private void OnCommand(CommandContext ctx)
    {
        var arg = ctx.Args.Count > 0 ? ctx.Args[0].ToLowerInvariant() : "status";
        switch (arg)
        {
            case "relay": Context.Scheduler.Post(Relay); break;
            case "flash": Play("flash", Flash); break;
            case "cold": ToggleCold(); break;
            case "fade": Play("fade", Fade); break;
            case "many": Many(); break;
            case "frozen": Context.Scheduler.Post(() => _ = Frozen()); break;
            default: Info($"status: {ScreenEffects.Status()}"); break;
        }
    }

    // What fixtures/screen_client.py checks, in this order.
    private void Relay()
    {
        var a = ScreenEffects.Play(new ScreenEffect { Order = -3, Strength = 0.75f, FadeIn = 0.25f, Seconds = 2, FadeOut = 0.5f, Exposure = 1.5f, Contrast = 1.25f, Saturation = 0.5f,
            Gamma = 1.1f, Temperature = -0.4f, Tint = new ScreenColor("#ff8000", 0.5f), Vignette = new ScreenVignette { Color = "#102030", Amount = 0.6f, Radius = 0.25f, Softness = 0.75f },
            Fill = new ScreenColor("#ffffff", 0.2f) });
        var b = ScreenEffects.Play(new ScreenEffect());
        ScreenEffects.SetStrength(a, 0.25f);
        ScreenEffects.Stop(a, immediate: true);
        ScreenEffects.Stop(b);
        Info($"relay: sent {a} and {b}; {ScreenEffects.Status().Sent} message(s) sent in all");
    }

    // ---- the client's keys ----

    private void Play(string what, ScreenEffect e)
    {
        var h = ScreenEffects.Play(e);
        Info($"{what}: {h}, {ScreenEffects.Status()}");
    }

    private void ToggleCold()
    {
        if (ScreenEffects.IsPlaying(_cold))
        {
            ScreenEffects.Stop(_cold);
            Info($"cold: stopping {_cold}, it fades for 1 s");
            _cold = ScreenHandle.None;
            return;
        }
        _cold = ScreenEffects.Play(Cold);
        Info($"cold: {_cold}");
    }

    private void Many()
    {
        if (_many.Count > 0)
        {
            foreach (var h in _many) ScreenEffects.Stop(h, immediate: true);
            Info($"many: {_many.Count} stopped");
            _many.Clear();
            return;
        }
        for (var i = 0; i < 9; i++)
            _many.Add(ScreenEffects.Play(new ScreenEffect { Order = i, Exposure = 0.05f * i, Tint = new ScreenColor(i % 2 == 0 ? "#ffd0d0" : "#d0d0ff", 0.3f) }));
        Check(_many.Take(8).All(h => !h.IsNone), "eight play at once");
        Check(_many[8].IsNone, "a ninth is refused");
        _many.RemoveAll(h => h.IsNone);
        Info("many: press again to stop them");
    }

    private async Task Frozen()
    {
        if (!Client.IsHosting)
        {
            Info("frozen: only when this client hosts the world (single player, or the host)");
            return;
        }
        await Abilities.ApplyBuff(Client.LocalPlayer, Blocks.Find("frozen-json")!.Guid);
        Info("frozen: applied to you for 3 s");
    }
}
