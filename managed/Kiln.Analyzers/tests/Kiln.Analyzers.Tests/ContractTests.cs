// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

/// <summary>KILN120: a Kiln result (IKilnResult, or a task of one) that nobody looks at.</summary>
public sealed class ResultTests
{
    private static readonly ResultAnalyzer A = new();

    // A result type of the mod's own: the rule goes by the type, not by Kiln's method names.
    private const string Own = """
        public sealed record Done(bool Ok, string? Reason) : IKilnResult;
        public sealed class Door { public Done Open() => new(true, null); public ValueTask<Done> OpenLater() => new(Open()); }
        """;

    [Fact]
    public async Task Results_thrown_away_are_reported()
    {
        var d = await Run(A, Own + """
            public sealed class M : Mod
            {
                private readonly Door? _door = new();
                public async Task Run(EntityId p)
                {
                    NoClip.Set(p, true);
                    await Items.Equip(p, default);
                    await Items.Equip(p, default).ConfigureAwait(false);
                    _door?.Open();
                    await _door!.OpenLater();
                    Context.Scheduler.Post(() => NoClip.Set(p, false));
                    Func<Task> later = async () => await Time.SetWeather("rain");
                    _ = later;
                }
                public void Sync(EntityId p)
                {
                    Items.Equip(p, default);
                    _door!.OpenLater();
                }
            }
            """);
        Ids(d, "KILN120", "KILN120", "KILN120", "KILN120", "KILN120", "KILN120", "KILN120", "KILN120", "KILN120");
        Assert.Equal("Set", d[0].At());
        Assert.Contains("'NoClip.Set' is thrown away", d[0].GetMessage());
        Assert.Contains("'Items.Equip' is thrown away", d[1].GetMessage());
        Assert.Contains("'Items.Equip' is thrown away", d[2].GetMessage());   // through ConfigureAwait
        Assert.Contains("'Door.Open'", d[3].GetMessage());                    // ?. and the mod's own result type
        Assert.Contains("'Door.OpenLater'", d[4].GetMessage());               // ValueTask
        Assert.Contains("'NoClip.Set'", d[5].GetMessage());                   // an expression lambda for an Action
        Assert.Contains("'Time.SetWeather'", d[6].GetMessage());              // an async lambda for a Func<Task>
        Assert.Contains("is a task nobody awaits or reads", d[7].GetMessage());
        Assert.Contains("is a task nobody awaits or reads", d[8].GetMessage());
    }

    [Fact]
    public async Task A_local_that_is_never_read_is_reported()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    var r = await Items.Equip(p, default);
                    var t = Items.Unequip(p, EquipmentSlot.Ring0);
                    NoClipResult n;
                    n = NoClip.Set(p, true);
                }
            }
            """);
        Ids(d, "KILN120", "KILN120");
        Assert.Contains("is stored in 'r' and never read", d[0].GetMessage());
        Assert.Contains("is stored in 't' and never read", d[1].GetMessage());
    }

    [Fact]
    public async Task Results_looked_at_kept_or_dropped_on_purpose_are_fine()
    {
        var d = await Run(A, Own + """
            public sealed class M : Mod
            {
                private NoClipResult? _last;
                public async Task Run(EntityId p)
                {
                    _ = NoClip.Set(p, true);                                  // dropped on purpose
                    _ = await Items.Equip(p, default);
                    _ = Items.Equip(p, default);
                    var r = await Items.Equip(p, default);
                    if (!r.Ok) Log.Info($"not equipped: {r.Reason}");          // read
                    if (!(await Items.Unequip(p, EquipmentSlot.Ring0)).Ok) return;
                    Log.Info(NoClip.Set(p, true).Message);                    // a member read
                    Keep(NoClip.Set(p, true));                                // passed on
                    _last = NoClip.Set(p, true);                              // stored in a field
                    var t = Items.Equip(p, default);                          // a task kept and awaited later
                    Log.Info((await t).Reason ?? "ok");
                    Chat.Broadcast("not a result");
                    await Task.Delay(1);
                }
                private static void Keep(IKilnResult r) { }
                private static void Named(EntityId p) { var _ = NoClip.Set(p, false); }   // a local named _ says the same
                public NoClipResult Fly(EntityId p) => NoClip.Set(p, true);    // returned
                public Task<EquipResult> Wear(EntityId p) => Items.Equip(p, default);
                public Func<NoClipResult> Later(EntityId p) => () => NoClip.Set(p, true);
                public IKilnResult Ask(Door door) { var done = door.Open(); return done; }
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task Fix_checks_the_result_with_the_mods_log()
    {
        var s = await Fix(A, new ResultCodeFix(), """
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    await Items.Equip(p, default);
                    Log.Info("done");
                }
            }
            """);
        Assert.Equal("""
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    var result = await Items.Equip(p, default);
                    if (!result.Ok)
                    {
                        Log.Warn("Items.Equip refused: " + result.Reason);
                    }
                    Log.Info("done");
                }
            }
            """, s);
    }

    [Fact]
    public async Task Fix_without_a_log_leaves_a_comment_and_finds_a_free_name()
    {
        var s = await Fix(A, new ResultCodeFix(), """
            public static class Helper
            {
                public static void Run(EntityId p, int result)
                {
                    NoClip.Set(p, true);
                }
            }
            """);
        Assert.Equal("""
            public static class Helper
            {
                public static void Run(EntityId p, int result)
                {
                    var result2 = NoClip.Set(p, true);
                    if (!result2.Ok)
                    {
                        // NoClip.Set was refused: result2.Reason says why
                    }
                }
            }
            """, s);
    }

    [Fact]
    public async Task Fix_drops_it_on_purpose()
    {
        var s = await Fix(A, new ResultCodeFix(), """
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    await Items.Equip(p, default);
                }
            }
            """, action: 1);
        Assert.Contains("_ = await Items.Equip(p, default);", s);
    }

    [Fact]
    public async Task A_task_outside_async_code_is_only_dropped()
    {
        const string source = """
            public sealed class M : Mod
            {
                public void Run(EntityId p)
                {
                    Items.Equip(p, default);
                }
            }
            """;
        Assert.Equal(new[] { "Drop it on purpose (_ =)" }, await Titles(A, new ResultCodeFix(), source));
        Assert.Contains("_ = Items.Equip(p, default);", await Fix(A, new ResultCodeFix(), source));
    }

    [Fact]
    public async Task A_task_in_async_code_is_awaited_by_the_check()
    {
        var s = await Fix(A, new ResultCodeFix(), """
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    Items.Equip(p, default);
                    await Task.Delay(1);
                }
            }
            """);
        Assert.Contains("var result = await Items.Equip(p, default);", s);
    }

    [Fact]
    public async Task Fix_for_a_local_checks_it_or_drops_it()
    {
        const string source = """
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    var r = await Items.Equip(p, default);
                    Log.Info("next");
                }
            }
            """;
        var check = await Fix(A, new ResultCodeFix(), source);
        Assert.Contains("var r = await Items.Equip(p, default);", check);
        Assert.Contains("if (!r.Ok)", check);
        Assert.Contains("Log.Warn(\"Items.Equip refused: \" + r.Reason);", check);
        var drop = await Fix(A, new ResultCodeFix(), source, action: 1);
        Assert.Contains("_ = await Items.Equip(p, default);", drop);
        Assert.DoesNotContain("var r", drop);
    }

    [Fact]
    public async Task A_local_set_through_a_conversion_or_a_conditional_is_reported()
    {
        // Codex review, finding 4
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public void Run(EntityId p, bool on)
                {
                    var r = on ? NoClip.Set(p, true) : NoClip.Set(p, false);
                    var k = (IKilnResult)NoClip.Set(p, true);
                    var read = on ? NoClip.Set(p, true) : NoClip.Set(p, false);
                    Log.Info(read.Message);
                }
            }
            """);
        Ids(d, "KILN120", "KILN120");
        Assert.Contains("is stored in 'r' and never read", d[0].GetMessage());
        Assert.Contains("is stored in 'k' and never read", d[1].GetMessage());
    }

    [Fact]
    public async Task A_task_kept_in_a_local_is_only_dropped()
    {
        // Codex review, finding 5: `if (!t.Ok)` on a Task does not compile
        const string source = """
            public sealed class M : Mod
            {
                public void Run(EntityId p)
                {
                    var t = Items.Equip(p, default);
                }
            }
            """;
        Assert.Equal(new[] { "Drop it on purpose (_ =)" }, await Titles(A, new ResultCodeFix(), source));
    }

    [Fact]
    public async Task A_result_through_conditional_access_is_only_dropped()
    {
        // Codex review, finding 6: result.Ok would throw where f?.Invoke() did nothing
        const string source = """
            public sealed class M : Mod
            {
                public void Run()
                {
                    Func<IKilnResult>? f = null;
                    f?.Invoke();
                }
            }
            """;
        Assert.Equal(new[] { "Drop it on purpose (_ =)" }, await Titles(A, new ResultCodeFix(), source));
    }

    [Fact]
    public async Task No_drop_where_underscore_names_a_parameter()
    {
        // Codex review, finding 7: `_ = ...` would assign the parameter called _
        const string source = """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Action<EntityId> a = _ => NoClip.Set(_, true);
                    a(default);
                }
            }
            """;
        Assert.Empty(await Titles(A, new ResultCodeFix(), source));
    }

    [Fact]
    public async Task Fix_names_the_call_as_a_symbol_and_escapes_it()
    {
        // Codex review, finding 9: no source text of the call goes into the generated string
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public async Task Run(bool rain) => await (rain ? Time.SetWeather("rain") : Time.SetWeather("sun"));
            }
            """);
        Ids(d, "KILN120");
        Assert.Contains("'Time.SetWeather' is thrown away", d[0].GetMessage());
        var s = await Fix(A, new ResultCodeFix(), """
            public sealed class M : Mod
            {
                public async Task Run(bool rain)
                {
                    await (rain ? Time.SetWeather("rain") : Time.SetWeather("sun"));
                }
            }
            """);
        Assert.Equal("""
            public sealed class M : Mod
            {
                public async Task Run(bool rain)
                {
                    var result = await (rain ? Time.SetWeather("rain") : Time.SetWeather("sun"));
                    if (!result.Ok)
                    {
                        Log.Warn("Time.SetWeather refused: " + result.Reason);
                    }
                }
            }
            """, s);
        Compile(s!);
    }

    [Fact]
    public async Task Fix_in_an_expression_lambda_drops_it()
    {
        var s = await Fix(A, new ResultCodeFix(), """
            public sealed class M : Mod
            {
                public void Run(EntityId p) => Context.Scheduler.Post(() => NoClip.Set(p, false));
            }
            """);
        Assert.Contains("Context.Scheduler.Post(() => _ = NoClip.Set(p, false));", s);
    }
}

/// <summary>KILN121: a [RequiresCapability] member used with nothing in reach that asks for its row.</summary>
public sealed class CapabilityTests
{
    private static readonly CapabilityAnalyzer A = new();

    // A gated type and member of the mod's own (the attribute works on any code).
    private const string Gated = """
        [RequiresCapability("lab.gate")] public sealed class Gate { public static void Open() { } }
        public static class Lab { [RequiresCapability("lab.door")] public static int Door { get; set; } }
        """;

    [Fact]
    public async Task Uses_with_nothing_in_reach_are_reported()
    {
        var d = await Run(A, Gated + """
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    NoClip.Set(p, true);
                    Log.Info($"{Time.OfDay}");
                    new Gate();
                    Gate.Open();
                    Lab.Door = 3;
                    if (Capabilities.Is("kiln.items")) await Items.Equip(p, default);   // another row
                }
            }
            """);
        Ids(d, "KILN121", "KILN121", "KILN121", "KILN121", "KILN121", "KILN121");
        Assert.Equal("Set", d[0].At());
        Assert.Contains("'NoClip.Set' needs capability rules.noclip", d[0].GetMessage());
        Assert.Contains("Capabilities.Is(\"rules.noclip\")", d[0].GetMessage());
        Assert.Contains("kiln.time", d[1].GetMessage());     // [RequiresCapability] on the property
        Assert.Contains("lab.gate", d[2].GetMessage());      // on the type: its constructor
        Assert.Contains("lab.gate", d[3].GetMessage());      // and its members
        Assert.Contains("lab.door", d[4].GetMessage());      // a setter
        Assert.Contains("items.equip", d[5].GetMessage());
    }

    [Fact]
    public async Task Checks_that_come_too_late_or_ask_too_little_are_reported()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public void After(EntityId p)
                {
                    NoClip.Set(p, true);
                    if (!Capabilities.Is("rules.noclip")) return;
                }
                public void NoExit(EntityId p)
                {
                    if (!Capabilities.Is("rules.noclip")) Log.Warn("off");
                    NoClip.Set(p, true);
                }
                public void OtherException(EntityId p)
                {
                    try { NoClip.Set(p, true); }
                    catch (ArgumentException) { }
                }
                public void InTheCatch(EntityId p)
                {
                    try { }
                    catch (KilnUnavailableException) { NoClip.Set(p, false); }
                }
            }
            """);
        Ids(d, "KILN121", "KILN121", "KILN121", "KILN121");
    }

    [Fact]
    public async Task A_helper_that_checks_is_not_followed()
    {
        // The documented cost of looking at one member at a time: the check is in Need, the call in Fly.
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public void Fly(EntityId p)
                {
                    Need("rules.noclip");
                    NoClip.Set(p, true);
                }
                private static void Need(string row)
                {
                    if (!Capabilities.Is(row)) throw new InvalidOperationException(Capabilities.Why(row));
                }
            }
            """);
        Ids(d, "KILN121");
    }

    [Fact]
    public async Task Tests_that_dominate_or_guard_the_use_are_fine()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                private const string Fly = "rules.noclip";
                private static readonly string FlyToo = "rules.noclip";
                private static bool CanFly => Capabilities.Is(Fly);
                public void Under(EntityId p)
                {
                    if (Capabilities.Is("rules.noclip")) NoClip.Set(p, true);
                    if (Capabilities.Is(Fly) && !p.IsNone) { NoClip.Set(p, true); }
                    _ = Capabilities.Is(NoClip.Capability) ? NoClip.Set(p, true) : null;
                    _ = Capabilities.Is(FlyToo) && NoClip.Set(p, true).Ok;
                    if (!Capabilities.Is(Fly)) Log.Warn("off"); else NoClip.Set(p, true);
                    if (Capabilities.Get(Fly).IsAvailable) NoClip.Set(p, true);
                    if (CanFly) NoClip.Set(p, true);
                    while (Capabilities.Is(Fly)) { NoClip.Set(p, true); break; }
                }
                public void Leaves(EntityId p)
                {
                    if (!Capabilities.Is("rules.noclip") || !Capabilities.Is("kiln.time")) return;
                    NoClip.Set(p, true);
                    Log.Info($"{Time.OfDay}");
                }
                public void Throws(EntityId p)
                {
                    if (Capabilities.Is(Fly) is false)
                    {
                        Log.Warn(Capabilities.Why(Fly));
                        throw new InvalidOperationException("no noclip");
                    }
                    if (p.IsNone) return;
                    NoClip.Set(p, true);
                }
                public void Local(EntityId p)
                {
                    var on = Capabilities.Is(Fly);
                    if (!on) return;
                    NoClip.Set(p, true);
                }
                public void InLoop(IEnumerable<EntityId> players)
                {
                    foreach (var p in players)
                    {
                        if (!Capabilities.Is(Fly)) continue;
                        NoClip.Set(p, true);
                    }
                }
                public void Later(EntityId p)
                {
                    if (!Capabilities.Is(Fly)) return;
                    Context.Scheduler.Post(() => NoClip.Set(p, true));   // the delegate is made after the check
                }
                public string Name() => nameof(Time.OfDay);
                public string Toggle(EntityId p, bool on)
                {
                    // a test of the row with another condition: the author knows when it matters (turning it off never does)
                    if (!on && !NoClip.Available) return "not on this build";
                    return NoClip.Set(p, !on).Message;
                }
                public void Either(EntityId p)
                {
                    if (Capabilities.Is("rules.noclip") || p.IsNone) NoClip.Set(p, true);
                }
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task A_handler_handed_out_after_the_check_is_in_reach()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    if (!NoClip.Available) return;
                    Context.Tick.Pre += OnPre;
                }
                private void OnPre(in TickInfo t)
                {
                    if (t.Step % 100 == 0) NoClip.Set(default, false);
                }
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task Callbacks_and_objects_that_come_from_the_same_row_are_in_reach()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    if (!Content.Available) return;
                    // the callback is only kept when OnReady (kiln.content) went through
                    Content.OnReady(_ => Content.Revert());
                }
                public void Unchecked()
                {
                    var wood = Content.Items.Get("Material_T1_Wood_Logs");    // reported: the first use of kiln.content
                    Log.Info($"{wood?.MaxStack} {wood?.Original("maxStackSize")}");  // wood came from it
                    if (Content.Items.Get("Material_T1_Stone") is { } stone) Log.Info($"{stone.MaxStack}");
                }
            }
            """);
        Ids(d, "KILN121", "KILN121");
        Assert.All(d, x => Assert.Equal("Get", x.At()));
    }

    [Fact]
    public async Task Objects_Kiln_hands_to_a_callback_are_in_reach()
    {
        var lib = Library("""
            namespace Kiln.Lab
            {
                public sealed class View { [RequiresCapability("lab.view")] public int Health => 1; }
                public static class Runs { public static void Each(Action<View> run) { } }
                public sealed record Block { public Action<View>? OnHit { get; init; } }
            }
            """, "Kiln.Lab");
        var d = await Run(A, """
            using Kiln.Lab;
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Runs.Each(v => Log.Info($"{v.Health}"));
                    var b = new Block { OnHit = v => Log.Info($"{v.Health}") };
                }
                public void Mine(View v) => Log.Info($"{v.Health}");    // a method of the mod: anyone may pass a View
            }
            """, extra: [lib]);
        Ids(d, "KILN121");
    }

    [Fact]
    public async Task Init_properties_of_a_block_are_not_uses()
    {
        var lib = Library("""
            namespace Kiln.Lab
            {
                public sealed record Weapon { [RequiresCapability("lab.moves")] public int Moves { get; init; } }
            }
            """, "Kiln.Lab");
        var d = await Run(A, """
            using Kiln.Lab;
            public sealed class M : Mod
            {
                public Weapon Make() => new Weapon { Moves = 3 } with { Moves = 4 };
                public int Read(Weapon w) => w.Moves;
            }
            """, extra: [lib]);
        Ids(d, "KILN121");    // the read
    }

    [Fact]
    public async Task Service_properties_that_ask_the_same_row_are_checks()
    {
        // From the generated guard table: NoClip.Available is Capabilities.Is("rules.noclip"); Time.WeatherChoiceAvailable
        // asks time.weather_choose; Time.Available's summary names kiln.time.
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    if (!NoClip.Available) return;
                    NoClip.Set(p, true);
                    if (Time.WeatherChoiceAvailable) await Time.SetWeather("rain");
                    if (Time.Available) Log.Info($"{Time.OfDay}");
                    if (Time.Available) await Time.SetWeather("rain");     // kiln.time is not time.weather_choose
                }
            }
            """);
        Ids(d, "KILN121");
        Assert.Contains("time.weather_choose", d[0].GetMessage());
    }

    [Fact]
    public async Task Catching_the_exception_or_a_base_of_it_is_fine()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public void Run(EntityId p)
                {
                    try { NoClip.Set(p, true); } catch (KilnUnavailableException e) { Log.Warn(e.Message); }
                    try { NoClip.Set(p, true); } catch (NotSupportedException) { }
                    try { NoClip.Set(p, true); } catch (Exception e) when (e is KilnUnavailableException) { }
                    try { NoClip.Set(p, true); } catch { }
                    try { if (p.IsNone) return; NoClip.Set(p, true); } finally { }
                }
            }
            """);
        Ids(d, "KILN121");   // try/finally alone catches nothing
    }

    [Fact]
    public async Task A_member_marked_with_the_row_passes_it_on()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                [RequiresCapability("rules.noclip")]
                public void Fly(EntityId p) => NoClip.Set(p, true);
                [RequiresCapability("rules.noclip")]
                public bool Flying { get => NoClip.Set(default, true).On; }
                public void Local(EntityId p)
                {
                    [RequiresCapability("rules.noclip")] void Up() => NoClip.Set(p, true);
                    if (Capabilities.Is("rules.noclip")) Up();
                }
                public void Caller(EntityId p) => Fly(p);    // now the caller is asked
            }
            [RequiresCapability("rules.noclip")]
            public static class Flight { public static void Up(EntityId p) => NoClip.Set(p, true); }
            """);
        Ids(d, "KILN121");
        Assert.Contains("'M.Fly' needs capability rules.noclip", d[0].GetMessage());
    }

    [Fact]
    public async Task Private_helpers_called_only_where_the_command_catches_are_covered()
    {
        // The shape of mods/samples/Loadouts: the command catches KilnUnavailableException, its helpers do the calls.
        var d = await Run(A, """
            public sealed class M : Mod
            {
                [Command("fly")]
                private async Task FlyCommand(CommandContext c, string what)
                {
                    try
                    {
                        if (what == "up") c.Reply(Up(c.Source.Player));
                        else await Down(c.Source.Player);
                    }
                    catch (Exception e) when (e is KilnUnavailableException or NotSupportedException)
                    {
                        throw new CommandException($"not here: {e.Message}");
                    }
                }
                private static string Up(EntityId p) => Again(p, 3);
                private static string Again(EntityId p, int n) => n == 0 ? NoClip.Set(p, true).Message : Again(p, n - 1);
                private static async Task Down(EntityId p) { await Task.Yield(); NoClip.Set(p, false); }
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task Private_helpers_with_one_unchecked_caller_are_reported()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public void Checked(EntityId p) { if (NoClip.Available) Up(p); }
                public void Unchecked(EntityId p) => Up(p);
                private static void Up(EntityId p) => NoClip.Set(p, true);
                public void Grouped() => Context.Tick.Pre += OnTick;
                private void OnTick(in TickInfo t) { if (t.Step < 0) Down(default); }
                private static void Down(EntityId p) => NoClip.Set(p, false);
                public static void Public(EntityId p) => NoClip.Set(p, true);
                [Subscribe] private void OnStarted(ServerStartedEvent e) => NoClip.Set(default, false);
                private void Unused(EntityId p) => NoClip.Set(p, true);
            }
            """);
        // Up: one caller asks nothing; Down: OnTick is handed out where nothing asks; Public: anyone may call it;
        // OnStarted: Kiln calls it; Unused: nobody calls it.
        Ids(d, "KILN121", "KILN121", "KILN121", "KILN121", "KILN121");
    }

    [Fact]
    public async Task Tests_and_client_checks_report_what_they_throw()
    {
        var d = await Run(A, """
            using Kiln.Testing;
            public sealed class M : Mod
            {
                [KilnTest] public void Flies() => NoClip.Set(default, true);
                [ClientCheck("noclip.on", "NoClip turns on")]
                public async Task On(CheckContext c) { Give(c.Player); await c.Verdict(); }
                private static void Give(EntityId p) => NoClip.Set(p, true);    // only a check calls it
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task A_negated_test_kept_in_a_bool_local_is_a_check()
    {
        // Codex review, finding 1
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public void Run(EntityId p)
                {
                    bool unavailable = !NoClip.Available;
                    if (unavailable) return;
                    _ = NoClip.Set(p, true);
                }
                public void Twice(EntityId p)
                {
                    bool unavailable = !NoClip.Available;
                    unavailable = false;
                    if (unavailable) return;
                    _ = NoClip.Set(p, true);      // written again: not followed
                }
            }
            """);
        Ids(d, "KILN121");
        Assert.Equal(13, d[0].Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task A_property_of_the_mod_with_a_getter_block_is_a_check()
    {
        // Codex review, finding 2
        var d = await Run(A, """
            public sealed class M : Mod
            {
                static bool CanFly { get { return NoClip.Available; } }
                static bool Grounded { get { return !NoClip.Available; } }
                public void Fly(EntityId p) { if (CanFly) _ = NoClip.Set(p, true); }
                public void Land(EntityId p) { if (Grounded) return; _ = NoClip.Set(p, false); }
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task A_local_function_is_judged_where_it_is_called()
    {
        // Codex review, finding 3: a check written before the local function's declaration is not a check before it runs
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public void Early(EntityId p)
                {
                    Fly();
                    if (!NoClip.Available) return;
                    void Fly() => _ = NoClip.Set(p, true);
                }
                public void Late(EntityId p)
                {
                    if (!NoClip.Available) return;
                    Fly();
                    void Fly() => _ = NoClip.Set(p, true);
                }
            }
            """);
        Ids(d, "KILN121");
        Assert.Equal(6, d[0].Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task No_wrap_fix_for_a_statement_that_declares_a_variable()
    {
        // Codex review, finding 8: h would move into the new block
        var s = await Fix(A, new CapabilityCodeFix(), """
            public sealed class M : Mod
            {
                public void Run(EntityId p)
                {
                    Combat.TryGetHealth(p, out var h);
                    Log.Info($"{h}");
                }
            }
            """);
        Assert.Null(s);
    }

    [Fact]
    public async Task Fix_writes_the_row_as_an_escaped_literal()
    {
        // Codex review, finding 9: the row goes in as a literal SyntaxFactory escapes
        var s = await Fix(A, new CapabilityCodeFix(), """
            public static class Lab { [RequiresCapability("lab.\"odd\"\\row")] public static void Open() { } }
            public sealed class M : Mod
            {
                public void Run() => Go();
                private static void Go()
                {
                    Lab.Open();
                }
            }
            """);
        Assert.Contains("if (Capabilities.Is(\"lab.\\\"odd\\\"\\\\row\"))", s);
        Compile(s!);
    }

    [Fact]
    public async Task Rows_the_manifest_requires_are_checked_by_the_loader()
    {
        const string source = """
            public sealed class M : Mod
            {
                public void Run(EntityId p) { NoClip.Set(p, true); Log.Info($"{Time.OfDay}"); }
            }
            """;
        var d = await Run(A, source, manifest: """{ "id": "m", "features": ["rules.noclip", "tick"] }""");
        Ids(d, "KILN121");
        Assert.Contains("kiln.time", d[0].GetMessage());
        d = await Run(A, source, properties: Props(("KilnFeatures", "kiln.time;rules.noclip")));
        Ids(d);
        // what Kiln.Analyzers.targets passes: KilnFeatures with commas (an .editorconfig value ends at ';')
        d = await Run(A, source, properties: Props(("KilnFeaturesList", "kiln.time,rules.noclip"), ("KilnFeatures", "kiln.time")));
        Ids(d);
    }

    [Fact]
    public async Task Fix_wraps_the_statement_in_a_check()
    {
        var s = await Fix(A, new CapabilityCodeFix(), """
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    await Items.Equip(p, default);
                }
            }
            """);
        Assert.Equal("""
            public sealed class M : Mod
            {
                public async Task Run(EntityId p)
                {
                    if (Capabilities.Is("items.equip"))
                    {
                        await Items.Equip(p, default);
                    }
                }
            }
            """, s);
    }

    [Fact]
    public async Task No_fix_when_the_use_is_part_of_a_declaration()
    {
        var s = await Fix(A, new CapabilityCodeFix(), """
            public sealed class M : Mod
            {
                public void Run(EntityId p)
                {
                    var r = NoClip.Set(p, true);
                    Log.Info(r.Message);
                }
            }
            """);
        Assert.Null(s);
    }
}
