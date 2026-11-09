// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using Xunit;

namespace Kiln.Ui.Tests;

/// <summary>Status lines: sources run only when someone looks, lines belong to their mod, and the game's language
/// reaches <see cref="Game.Language"/> on a client.</summary>
// These fixtures replace the same process-wide backend and native tables as the render and wheel tests.
[Collection("bus")]
public class StatusLinesTests : IDisposable
{
    private readonly List<ModContext> _mods = [];

    public StatusLinesTests()
    {
        StatusLines.Reset();
        ModsBackend.Current = null;
        GameLanguageSource.ResetForTests();
    }

    public void Dispose()
    {
        foreach (var m in _mods) m.Retire();
        StatusLines.Reset();
        ModsBackend.Current = null;
        GameLanguageSource.ResetForTests();
        SetScreenLanguage(0);
    }

    private ModContext NewMod(string id)
    {
        var mod = HostAccess.CreateContext(new ModInfo(id, id, "1.2.0", Path.GetTempPath()), 0, Path.GetTempPath());
        _mods.Add(mod);
        return mod;
    }

    private static T As<T>(ModContext? mod, Func<T> body)
    {
        var previous = HostAccess.Current;
        HostAccess.Current = mod;
        try { return body(); }
        finally { HostAccess.Current = previous; }
    }

    private static void As(ModContext? mod, Action body) => As(mod, () => { body(); return 0; });

    [Fact]
    public void A_source_runs_only_when_someone_looks()
    {
        var mod = NewMod("sl-lazy");
        var calls = 0;
        As(mod, () => StatusLines.Add(() => $"{++calls} zones merged"));
        Assert.Equal(0, calls);
        Assert.Equal(["1 zones merged"], StatusLines.Lines("sl-lazy"));
        Assert.Equal(["2 zones merged"], StatusLines.Lines("sl-lazy"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_source_runs_as_its_mod()
    {
        var mod = NewMod("sl-as");
        string? ranAs = null;
        As(mod, () => StatusLines.Add(() => ranAs = ModContext.CurrentMod?.Info.Id));
        StatusLines.Lines("sl-as");
        Assert.Equal("sl-as", ranAs);
        Assert.Null(ModContext.CurrentMod);
    }

    [Fact]
    public void Pushed_text_stays_until_it_changes_and_an_empty_text_removes_the_line()
    {
        var mod = NewMod("sl-push");
        As(mod, () =>
        {
            StatusLines.Set("main", "3 of 5 altars placed");
            StatusLines.Set("main", "4 of 5 altars placed");
            StatusLines.Set("extra", "second line");
        });
        Assert.Equal(["4 of 5 altars placed", "second line"], StatusLines.Lines("sl-push"));
        As(mod, () => Assert.Null(StatusLines.Set("extra", "")));
        Assert.Equal(["4 of 5 altars placed"], StatusLines.Lines("sl-push"));
        As(mod, () => Assert.True(StatusLines.Remove("main")));
        As(mod, () => Assert.False(StatusLines.Remove("main")));
        Assert.Empty(StatusLines.Lines("sl-push"));
    }

    [Fact]
    public void The_same_id_replaces_the_line_and_a_source_can_take_over_from_text()
    {
        var mod = NewMod("sl-replace");
        As(mod, () =>
        {
            var a = StatusLines.Add(() => "from a source", "x");
            var b = StatusLines.Set("x", "now text");
            Assert.Same(a, b);
        });
        Assert.Equal(["now text"], StatusLines.Lines("sl-replace"));
        As(mod, () => StatusLines.Add(() => "source again", "x"));
        Assert.Equal(["source again"], StatusLines.Lines("sl-replace"));
    }

    [Fact]
    public void Lines_are_split_and_empty_ones_dropped()
    {
        var mod = NewMod("sl-split");
        As(mod, () =>
        {
            StatusLines.Add(() => "first\r\n\r\n  second  \nthird", "a");
            StatusLines.Add(() => null, "b");
            StatusLines.Add(() => "  ", "c");
        });
        Assert.Equal(["first", "second", "third"], StatusLines.Lines("sl-split"));
    }

    [Fact]
    public void A_source_that_throws_shows_nothing_and_is_logged_once_until_it_works_again()
    {
        var mod = NewMod("sl-throw");
        var fail = true;
        As(mod, () => StatusLines.Add(() => fail ? throw new InvalidOperationException("no numbers yet") : "fine", "n"));
        As(mod, () => StatusLines.Add(() => "other", "o"));
        var line = As(mod, () => StatusLines.Add(() => fail ? throw new InvalidOperationException("again") : "fine", "n"));
        Assert.Empty(line.Text);
        Assert.Equal(["other"], StatusLines.Lines("sl-throw"));  // the others still show
        fail = false;
        Assert.Equal(["fine", "other"], StatusLines.Lines("sl-throw"));
    }

    [Fact]
    public void Lines_go_with_the_mod_that_added_them()
    {
        var mod = NewMod("sl-gone");
        var other = NewMod("sl-stay");
        As(mod, () => StatusLines.Set("a", "mine"));
        As(other, () => StatusLines.Set("a", "kept"));
        mod.Retire();
        Assert.Empty(StatusLines.Lines("sl-gone"));
        Assert.Equal(["kept"], StatusLines.Lines("sl-stay"));
        Assert.Throws<InvalidOperationException>(() => As(mod, () => StatusLines.Set("late", "x")));
    }

    [Fact]
    public void A_line_handle_removes_itself_on_dispose()
    {
        var mod = NewMod("sl-dispose");
        var line = As(mod, () => StatusLines.Add(() => "here"));
        Assert.Equal(["here"], StatusLines.Lines("sl-dispose"));
        line.Dispose();
        Assert.Empty(StatusLines.Lines("sl-dispose"));
    }

    [Fact]
    public void Two_mods_may_use_the_same_line_id()
    {
        var a = NewMod("sl-one");
        var b = NewMod("sl-two");
        As(a, () => StatusLines.Set("status", "A"));
        As(b, () => StatusLines.Set("status", "B"));
        Assert.Equal(["A"], StatusLines.Lines("sl-one"));
        Assert.Equal(["B"], StatusLines.Lines("sl-two"));
        As(a, () => StatusLines.Remove("status"));
        Assert.Equal(["B"], StatusLines.Lines("sl-two"));
    }

    [Fact]
    public void Kilns_own_lines_belong_to_kiln()
    {
        StatusLines.Set("net", "synced");
        Assert.Equal(["synced"], StatusLines.Lines("kiln"));
    }

    [Fact]
    public void All_and_Summary_list_the_mods_that_have_lines()
    {
        var a = NewMod("sl-b");
        var b = NewMod("sl-a");
        NewMod("sl-empty");
        As(a, () => StatusLines.Set("l1", "line one"));
        As(a, () => StatusLines.Set("l2", "line two"));
        As(b, () => StatusLines.Add(() => "from a"));
        var all = StatusLines.All();
        Assert.Equal(["sl-a", "sl-b"], all.Select(m => m.ModId));  // no load order known: by id
        Assert.Equal(["line one", "line two"], all.Single(m => m.ModId == "sl-b").Lines);
        Assert.Equal("sl-a\n  from a\nsl-b\n  line one\n  line two\n", StatusLines.Summary());
        StatusLines.Reset();
        Assert.Equal("", StatusLines.Summary());
        Assert.Empty(StatusLines.All());
    }

    [Fact]
    public void WriteToLog_writes_one_line_each()
    {
        var mod = NewMod("sl-log");
        As(mod, () => StatusLines.Set("a", "one\ntwo"));
        var lines = new List<string>();
        var log = new Logger(0);
        var previous = Backend.Current;
        Backend.Current = new CaptureBackend(lines);
        try { StatusLines.WriteToLog(); }
        finally { Backend.Current = previous; }
        Assert.Equal(["Info: status sl-log: one", "Info: status sl-log: two"], lines);
        _ = log;
    }

    private sealed class CaptureBackend(List<string> lines) : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text) => lines.Add($"{level}: {text}");
        public nint Service(string name, uint minVersion) => 0;
    }

    private static IReadOnlyList<ModStatus> SnapshotUntil(TimeSpan maxAge, Func<IReadOnlyList<ModStatus>, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<ModStatus> value;
        while (!done(value = StatusLines.Snapshot(maxAge)) && DateTime.UtcNow < deadline) Thread.Sleep(5);
        return value;
    }

    [Fact]
    public void The_menu_snapshot_is_not_read_more_often_than_it_is_asked_for()
    {
        var mod = NewMod("sl-snap");
        var calls = 0;
        As(mod, () => StatusLines.Add(() => $"{Interlocked.Increment(ref calls)}"));
        Assert.Empty(StatusLines.Snapshot(TimeSpan.FromSeconds(30)));  // never waits: the first answer is empty
        var first = SnapshotUntil(TimeSpan.FromSeconds(30), v => v.Count > 0);
        var second = StatusLines.Snapshot(TimeSpan.FromSeconds(30));
        Assert.Same(first, second);
        Assert.Equal(1, Volatile.Read(ref calls));
        As(mod, () => StatusLines.Set("more", "x"));  // a change shows soon (after ChangeRefresh), not after maxAge
        var changed = SnapshotUntil(TimeSpan.FromSeconds(30), v => v.Count > 0 && v[0].Lines.Contains("x"));
        Assert.Contains("x", changed[0].Lines);
    }

    [Fact]
    public void A_slow_source_does_not_hold_up_the_menu_or_the_admin_list()
    {
        var mod = NewMod("sl-slow");
        using var release = new ManualResetEventSlim();
        As(mod, () => StatusLines.Set("fast", "ready"));
        As(mod, () => StatusLines.Add(() => { release.Wait(TimeSpan.FromSeconds(10)); return "slow done"; }, "slow"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = StatusLines.Snapshot(TimeSpan.FromMilliseconds(500));
        Assert.Empty(first);  // nothing read yet, and the source still runs
        Assert.Empty(StatusLines.RecentLines("sl-slow"));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
        release.Set();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<string> lines = [];
        while (DateTime.UtcNow < deadline && (lines = StatusLines.RecentLines("sl-slow")).Count == 0) Thread.Sleep(10);
        Assert.Equal(["ready", "slow done"], lines);
    }

    [Fact]
    public void A_hanging_source_never_holds_up_the_tick_and_starts_one_refresh()
    {
        // Kiln.Admin's mod list asks once per mod on the tick thread: with a source that hangs, 200 mods must cost nothing.
        var mod = NewMod("sl-hang");
        using var release = new ManualResetEventSlim();
        var calls = 0;
        As(mod, () => StatusLines.Set("fast", "ready"));
        var first = SnapshotUntil(TimeSpan.FromMilliseconds(1), v => v.Count > 0);  // a snapshot to fall back on
        Assert.Equal(["ready"], first[0].Lines);
        As(mod, () => StatusLines.Add(() => { Interlocked.Increment(ref calls); release.Wait(TimeSpan.FromSeconds(10)); return "hung"; }, "hang"));
        Thread.Sleep(150);  // past ChangeRefresh: the next read starts the refresh that hangs
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(["ready"], StatusLines.RecentLines("sl-hang"));  // the last snapshot, at once
            if (i % 50 == 0) Thread.Sleep(60);  // past ChangeRefresh again and again: still one refresh
        }
        var elapsed = watch.ElapsedMilliseconds;
        Assert.True(elapsed < 1500, $"{elapsed} ms for 200 reads");
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) > 0, TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref calls));
        release.Set();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<string> lines = [];
        while (DateTime.UtcNow < deadline && (lines = StatusLines.RecentLines("sl-hang")).Count < 2) Thread.Sleep(10);
        Assert.Equal(["ready", "hung"], lines);
    }

    [Fact]
    public void A_line_set_every_tick_does_not_defeat_the_cache()
    {
        var mod = NewMod("sl-churn");
        var reads = 0;
        As(mod, () => StatusLines.Add(() => $"read {Interlocked.Increment(ref reads)}", "count"));
        var stop = false;
        var churn = new Thread(() =>
        {
            var n = 0;
            while (!Volatile.Read(ref stop)) As(mod, () => StatusLines.Set("tick", $"tick {++n}"));
        });
        churn.Start();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var got = SnapshotUntil(TimeSpan.FromSeconds(1), v => v.Count > 0);
            Assert.NotEmpty(got);  // a refresh lands even though the lines change all the time
            while (watch.ElapsedMilliseconds < 600) StatusLines.Snapshot(TimeSpan.FromSeconds(1));
            // one refresh per ChangeRefresh (100 ms) at most, not one per call
            Assert.InRange(Volatile.Read(ref reads), 1, 600 / 100 + 3);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            churn.Join();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddFromCollectibleScope(ModContext mod)
    {
        var holder = new object();
        As(mod, () => StatusLines.Add(() => holder.ToString(), "weak"));
        return new WeakReference(holder);
    }

    [Fact]
    public void A_retired_mods_sources_are_not_kept_alive()
    {
        var mod = NewMod("sl-weak");
        var weak = AddFromCollectibleScope(mod);
        mod.Retire();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.IsAlive);
    }

    [Fact]
    public void Retirement_while_an_add_waits_for_the_registry_cannot_leave_a_source_behind()
    {
        var mod = NewMod("sl-racing");
        var gate = typeof(StatusLines).GetField("Lock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { As(mod, () => StatusLines.Add(() => "stale", "racing")); }
            catch (Exception e) { failure = e; }
        });
        lock (gate)
        {
            worker.Start();
            Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 5000));
            mod.Retire();
        }
        Assert.True(worker.Join(5000));
        Assert.IsType<InvalidOperationException>(failure);
        var lines = (StatusLine[])typeof(StatusLines).GetField("_lines", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.Empty(lines);
    }

    // ---- the game's language, from kiln.ui to Game.Language ----

    private sealed class ClientMods : IModsBackend
    {
        public IReadOnlyList<ModEntry> All => [];
        public ModSide Side => ModSide.Client;
        public ModEntry? Get(string id) => null;
        public ModStats Stats(string id) => default;
        public bool Send(ModContext? sender, string targetId, string method, object? payload) => false;
        public Task<ModOperationResult> Request(ModOperation op, string arg) => throw new NotSupportedException();
        public void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller) => throw new NotSupportedException();
    }

    // kiln.ui's native screen state is not there in a unit test: put a screen with a language where OnScreen would.
    private static void SetScreenLanguage(uint languageId)
    {
        var field = Type.GetType("Kiln.Ui+Screens, Kiln.Ui")!.GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var screen = new GameScreen(GameUiState.Play, HudScreen.InGame, ScreenKind.InGame, 0, null, languageId, 1);
        field.SetValue(null, new StrongBox<GameScreen>(screen));
    }

    [Fact]
    public void A_dedicated_server_says_english_whatever_the_screen_says()
    {
        SetScreenLanguage(3);
        Assert.Equal("de-de", GameLanguage.Current);
        Assert.Equal("en-us", Game.Language);  // no client side
    }

    [Fact]
    public void A_client_says_the_language_kiln_ui_reads_from_the_game()
    {
        ModsBackend.Current = new ClientMods();
        SetScreenLanguage(0);
        Assert.Equal("en-us", Game.Language);  // before the game's UI has said
        SetScreenLanguage(3);
        Assert.Equal("de-de", Game.Language);
        SetScreenLanguage(16);
        Assert.Equal("pt-br", Game.Language);
        Assert.Equal(["pt-br", "pt", "en", "en-us"], ModLocale.LookupOrder(Game.Language));
        SetScreenLanguage(999);
        Assert.Equal("en-us", Game.Language);
    }

    [Fact]
    public void The_names_the_api_finds_the_client_language_by_exist()
    {
        // Kiln.Api reads it by reflection ("Kiln.GameLanguage, Kiln.Ui", static property Current) so it never loads Kiln.Ui itself.
        var type = Type.GetType("Kiln.GameLanguage, Kiln.Ui");
        Assert.NotNull(type);
        var p = type!.GetProperty("Current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(p?.GetGetMethod());
        Assert.Equal(typeof(string), p!.PropertyType);
    }
}
