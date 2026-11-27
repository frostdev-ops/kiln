// SPDX-License-Identifier: MIT
// Kiln.Npcs (speech bubbles) without a game: the rule (SpeechBook), the texts' tags, what the host sends and what a
// joined client applies, against a recording kiln.npcspeech table.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.BlockTests;

[Collection("NpcSpeech")]
public sealed unsafe class NpcSpeechTests : IDisposable
{
    private static long _now = 10_000;
    private static readonly List<string> Calls = new();
    private static readonly List<object> Wire = new();
    private static bool _host = true;
    private static int _textRc;
    private static Action<uint>? _onSet;
    private static SpeechNative.Table* _table;

    public NpcSpeechTests()
    {
        if (_table == null)
        {
            _table = (SpeechNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(SpeechNative.Table));
            _table->HeaderSize = (uint)sizeof(SpeechNative.Table);
            _table->HeaderVersion = SpeechNative.Version;
            _table->Set = &FakeSet;
            _table->Say = &FakeSay;
            _table->Text = &FakeText;
            _table->ClearAll = &FakeClear;
            _table->GetStatus = &FakeStatus;
        }
        lock (Calls) Calls.Clear();
        Wire.Clear();
        _host = true;
        _textRc = 0;
        _onSet = null;
        NpcSpeechEngine.Clock = () => _now;
        NpcSpeechEngine.HostHook = () => _host;
        NpcSpeechEngine.SendHook = m => Wire.Add(m);
        SpeechNative.TableHook = () => (nint)_table;
        NpcSpeechEngine.ResetForTests();
    }

    public void Dispose()
    {
        NpcSpeechEngine.ResetForTests();
        NpcSpeechEngine.Clock = static () => Environment.TickCount64;
        NpcSpeechEngine.HostHook = null;
        NpcSpeechEngine.SendHook = null;
        SpeechNative.TableHook = null;
    }

    [UnmanagedCallersOnly]
    private static int FakeSet(uint npc, uint mode, uint* tags, uint n)
    {
        _onSet?.Invoke(mode);
        var t = new List<string>();
        for (var i = 0; i < n; i++) t.Add(tags[i].ToString("x8"));
        lock (Calls) Calls.Add($"set {npc} {mode} [{string.Join(",", t)}]");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeSay(uint npc, uint tag, uint ms)
    {
        lock (Calls) Calls.Add($"say {npc} {tag:x8} {ms}");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeText(uint tag, byte* json, nuint len)
    {
        lock (Calls) Calls.Add($"text {tag:x8} {System.Text.Encoding.UTF8.GetString(json, (int)len)}");
        return _textRc;
    }

    [UnmanagedCallersOnly]
    private static int FakeClear()
    {
        lock (Calls) Calls.Add("clear");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeStatus(SpeechNative.Status* s) => 0;

    private static string[] Snapshot()
    {
        lock (Calls) return Calls.ToArray();
    }

    private static readonly EntityId Ramone = new(1159), Balthazar = new(1160);

    // ------------------------------------------------------------------------------------------------- the rule ----

    [Fact]
    public void Highest_priority_wins_then_the_most_recent()
    {
        var b = new SpeechBook();
        b.Set(SpeechLayer.Local, 7, "hunger", SpeechMode.Suppress, [], 0);
        b.Set(SpeechLayer.Local, 7, "mood", SpeechMode.Replace, [new UiText("Lovely day")], 0);
        Assert.Equal("mood", b.Effective(7, 0).Owner);          // a tie: the most recent
        b.Set(SpeechLayer.Local, 7, "hunger", SpeechMode.Suppress, [], 5);
        Assert.Equal(SpeechMode.Suppress, b.Effective(7, 0).Mode); // higher priority
        b.Set(SpeechLayer.Local, 7, "hunger", SpeechMode.Game, [], 5);
        Assert.Equal(SpeechMode.Replace, b.Effective(7, 0).Mode);  // cleared: the next one shows
        Assert.Equal(new uint[] { 7 }, b.DropOwner("mood"));
        Assert.Equal(SpeechMode.Game, b.Effective(7, 0).Mode);
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void Local_hides_remote_and_a_say_ends_at_its_time()
    {
        var b = new SpeechBook();
        b.Set(SpeechLayer.Remote, 7, "server", SpeechMode.Replace, [new UiText("Feed me")], 0);
        b.Set(SpeechLayer.Local, 7, "mine", SpeechMode.Suppress, [], -10);
        Assert.Equal(SpeechMode.Suppress, b.Effective(7, 0).Mode);
        b.DropLayer(SpeechLayer.Local);
        Assert.Equal(SpeechMode.Replace, b.Effective(7, 0).Mode);
        b.Say(SpeechLayer.Remote, 7, "server", new UiText("I'm starving"), 5_000);
        Assert.NotNull(b.Effective(7, 4_999).Say);
        Assert.Null(b.Effective(7, 5_000).Say);
        Assert.False(b.StopSay(SpeechLayer.Remote, 7, "someone else"));
        Assert.True(b.StopSay(SpeechLayer.Remote, 7, "server"));
    }

    [Fact]
    public void Texts_have_stable_tags_and_limits()
    {
        var a = SpeechText.Json(UiText.Of(("de", "Hunger"), ("en", "Hungry")));
        var b = SpeechText.Json(UiText.Of(("EN", "Hungry"), ("de", "Hunger")));
        Assert.Equal(a, b);                                           // the language order and case do not matter
        Assert.Equal(SpeechText.Tag(a), SpeechText.Tag(b));
        Assert.NotEqual(SpeechText.Tag(a), SpeechText.Tag(SpeechText.Json(new UiText("Hungry"))));
        Assert.Equal("Hunger", SpeechText.FromJson(a).For("de-de"));
        Assert.Null(SpeechText.Problem(new UiText("ok")));
        Assert.NotNull(SpeechText.Problem(new UiText("")));
        Assert.NotNull(SpeechText.Problem(new UiText(new string('x', 201))));
        Assert.NotEqual(0u, SpeechText.Tag(""));
    }

    // ------------------------------------------------------------------------------------------------- the API -----

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentException>(() => Npcs.Say(EntityId.None, "x", TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentException>(() => Npcs.Say(Ramone, "x", TimeSpan.FromMilliseconds(100)));
        Assert.Throws<ArgumentException>(() => Npcs.Say(Ramone, "x", TimeSpan.FromMinutes(2)));
        Assert.Throws<ArgumentException>(() => Npcs.Say(Ramone, "", TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentException>(() => Npcs.Speech(Ramone).Replace());
        Assert.Throws<ArgumentException>(() => Npcs.Speech(Ramone).Replace(Enumerable.Repeat("a", 33).ToArray()));
        Assert.Throws<ArgumentException>(() => Npcs.Speech(EntityId.None));
    }

    [Fact]
    public void The_host_shows_it_and_sends_it_to_the_clients()
    {
        var r = Npcs.Say(Ramone, "I'm starving", TimeSpan.FromSeconds(6));
        Assert.True(r.Ok, r.ToString());
        var json = SpeechText.Json(new UiText("I'm starving"));
        var tag = SpeechText.Tag(json);
        var calls = Snapshot();
        Assert.Contains($"text {tag:x8} {json}", calls);
        Assert.Contains($"say {Ramone.Value} {tag:x8} 6000", calls);
        var say = Assert.IsType<SayMsg>(Assert.Single(Wire));
        Assert.Equal((Ramone.Value, json, 6000u), (say.Npc, say.Text, say.Ms));
        Assert.True(Npcs.IsSaying(Ramone));

        Wire.Clear();
        var speech = Npcs.Speech(Ramone);
        Assert.True(speech.Suppress().Ok);
        Assert.Equal(SpeechMode.Suppress, speech.Mode);
        Assert.Equal(SpeechMode.Suppress, Npcs.SpeechOf(Ramone));
        Assert.Contains($"set {Ramone.Value} 1 []", Snapshot());
        var m = Assert.Single(Wire.OfType<SpeechMsg>());             // v1 (dev.32 clients also get kiln:npcdialog's Lines)
        Assert.Equal(SpeechMode.Suppress, m.Mode);

        Wire.Clear();
        speech.Replace("No food, no work.", "My stomach...");
        var sent = Assert.Single(Wire.OfType<SpeechMsg>());
        Assert.Equal(2, sent.Lines.Length);
        var t1 = SpeechText.Tag(SpeechText.Json(new UiText("No food, no work.")));
        var t2 = SpeechText.Tag(SpeechText.Json(new UiText("My stomach...")));
        Assert.Contains($"set {Ramone.Value} 2 [{t1:x8},{t2:x8}]", Snapshot());

        Wire.Clear();
        speech.Replace("No food, no work.", "My stomach...");
        Assert.Empty(Wire);                                       // the same again: nothing goes out

        speech.Clear();
        Assert.Equal(SpeechMode.Game, Npcs.SpeechOf(Ramone));
        Assert.Contains($"set {Ramone.Value} 0 []", Snapshot());
        Assert.Equal(SpeechMode.Game, Assert.Single(Wire.OfType<SpeechMsg>()).Mode);
    }

    [Fact]
    public void A_say_that_does_not_interrupt_waits_for_another_mods()
    {
        NpcSpeechEngine.Say(Balthazar, "other-mod", new UiText("Mine"), TimeSpan.FromSeconds(5), new SayOptions());
        var r = NpcSpeechEngine.Say(Balthazar, "kiln", new UiText("Yours"), TimeSpan.FromSeconds(5), new SayOptions(Interrupt: false));
        Assert.False(r.Ok);
        Assert.Contains("other-mod", r.Reason);
        _now += 6_000;
        Assert.True(NpcSpeechEngine.Say(Balthazar, "kiln", new UiText("Yours"), TimeSpan.FromSeconds(5), new SayOptions(Interrupt: false)).Ok);
    }

    [Fact]
    public void A_joined_client_applies_what_the_server_sent_and_forgets_it_on_reset()
    {
        _host = false;
        var line = SpeechText.Json(UiText.Of(("en", "Feed me"), ("de", "Gib mir Essen")));
        NpcSpeechEngine.Received(new SpeechMsg(Ramone.Value, SpeechMode.Replace, [line]));
        NpcSpeechEngine.Received(new SayMsg(Ramone.Value, SpeechText.Json(new UiText("Hungry!")), 3000));
        var calls = Snapshot();
        Assert.Contains($"set {Ramone.Value} 2 [{SpeechText.Tag(line):x8}]", calls);
        Assert.Contains(calls, c => c.StartsWith($"say {Ramone.Value} ", StringComparison.Ordinal) && c.EndsWith(" 3000", StringComparison.Ordinal));
        Assert.Empty(Wire);                                       // a client sends nothing
        Assert.Equal(SpeechMode.Replace, Npcs.SpeechOf(Ramone));

        NpcSpeechEngine.Received(new SpeechMsg(Ramone.Value, SpeechMode.Replace, ["not json"]));   // ignored
        Assert.Equal(SpeechMode.Replace, Npcs.SpeechOf(Ramone));

        NpcSpeechEngine.ReceivedReset();
        Assert.Equal(SpeechMode.Game, Npcs.SpeechOf(Ramone));
        Assert.False(Npcs.IsSaying(Ramone));
        Assert.Contains($"set {Ramone.Value} 0 []", Snapshot());
    }

    [Fact]
    public void A_dedicated_server_without_bubbles_still_sends()
    {
        SpeechNative.TableHook = () => 0;
        var r = Npcs.Say(Ramone, "I'm starving", TimeSpan.FromSeconds(2));
        Assert.True(r.Ok);
        Assert.Null(r.Reason);
        Assert.IsType<SayMsg>(Assert.Single(Wire));
        Assert.Empty(Snapshot());
    }

    [Fact]
    public void A_joined_client_without_kiln_npcspeech_is_told_why()
    {
        _host = false;
        SpeechNative.TableHook = () => 0;
        var r = Npcs.Speech(Ramone).Suppress();
        Assert.False(r.Ok);
        Assert.Contains("kiln.npcspeech", r.Reason);
    }

    [Fact]
    public void The_world_ending_clears_everything()
    {
        Npcs.Speech(Ramone).Suppress();
        Npcs.Say(Balthazar, "Bye", TimeSpan.FromSeconds(3));
        Wire.Clear();
        NpcSpeechEngine.WorldEnded();
        Assert.Contains("clear", Snapshot());
        Assert.Single(Wire.OfType<SpeechResetMsg>());
        Assert.Single(Wire.OfType<DialogResetMsg>());               // kiln:npcdialog's clients forget theirs too
        Assert.Equal(SpeechMode.Game, Npcs.SpeechOf(Ramone));
        Assert.Equal(0, NpcSpeechEngine.Book.Count);
    }

    [Fact]
    public void The_wire_round_trips()
    {
        var w = new NetWriter();
        var m = new SpeechMsg(42, SpeechMode.Replace, [SpeechText.Json(new UiText("a")), SpeechText.Json(UiText.Of(("en", "b"), ("de", "c")))]);
        NpcSpeechEngine.EncodeSpeech(m, w);
        var r = new NetReader(w.ToArray());
        var back = NpcSpeechEngine.DecodeSpeech(ref r);
        Assert.Equal((m.Npc, m.Mode), (back.Npc, back.Mode));
        Assert.Equal(m.Lines, back.Lines);
        var bad = new NetWriter().UInt32(1).Byte(9).Byte(0);
        Assert.Throws<NetFormatException>(() => { var rr = new NetReader(bad.ToArray()); NpcSpeechEngine.DecodeSpeech(ref rr); });
    }

    // ------------------------------------------------------------------------------------- limits and edges ---

    [Fact]
    public void The_calls_a_server_mod_makes_name_no_capability()
    {
        // A dedicated server has no bubbles of its own (npcspeech.bubble is off there by design) and still sends. A
        // [RequiresCapability] on these would make KILN121 (and its code fix) put a server mod's calls under
        // Capabilities.Is("npcspeech.bubble"), so they would never reach the clients. None of them throws either.
        var members = typeof(Npcs).GetMethods().Where(m => m.Name is "Say" or "StopSaying" or "Speech")
            .Concat(typeof(NpcSpeech).GetMethods().Where(m => m.Name is "Suppress" or "Replace" or "Clear")).ToArray();
        Assert.Equal(8, members.Length);   // Say x2, StopSaying, Speech; Suppress, Replace x2, Clear
        foreach (var m in members)
            Assert.True(m.GetCustomAttributes(typeof(RequiresCapabilityAttribute), false).Length == 0, $"{m.DeclaringType!.Name}.{m.Name}");
    }

    [Fact]
    public void Lines_the_clients_would_drop_are_refused_and_three_languages_of_200_characters_fit()
    {
        // every language of a line travels in one string a client reads with a 2 KiB limit
        var many = UiText.Of(Enumerable.Range(0, 12).Select(i => ($"x{i}", new string('ж', 200))).ToArray());
        Assert.Throws<ArgumentException>(() => Npcs.Say(Ramone, many, TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentException>(() => Npcs.Speech(Ramone).Replace([many]));
        Assert.Empty(Wire);

        var three = UiText.Of(("en", new string('a', 200)), ("de", new string('ü', 200)), ("fr", new string('é', 200)));
        Assert.True(Npcs.Say(Ramone, three, TimeSpan.FromSeconds(2)).Ok);
        var say = Assert.IsType<SayMsg>(Assert.Single(Wire));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(say.Text) <= NpcSpeechEngine.MaxLineBytes, $"{say.Text.Length} chars");
        Assert.Equal(new string('ü', 200), SpeechText.FromJson(say.Text).For("de-de"));
        var w = new NetWriter();
        NpcSpeechEngine.EncodeSpeech(new SpeechMsg(Ramone.Value, SpeechMode.Replace, Enumerable.Repeat(say.Text, 32).ToArray()), w);
        var r = new NetReader(w.ToArray());
        Assert.Equal(32, NpcSpeechEngine.DecodeSpeech(ref r).Lines.Length);   // what a Kiln client reads back
    }

    [Fact]
    public void A_slow_call_does_not_overwrite_a_newer_one()
    {
        // "any thread": a joined client's network thread (Received) and its tick thread (a mod's call), or a host's mod
        // calling from two threads; whatever came last is what the bubble and the clients end with
        using var entered = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        _onSet = mode =>
        {
            if (mode != 1 || Thread.CurrentThread.Name != "slow") return;
            entered.Set();
            go.Wait(5000);
        };
        var slow = new Thread(() => NpcSpeechEngine.Set(Ramone, "a", SpeechMode.Suppress, [], 0)) { Name = "slow" };
        slow.Start();
        Assert.True(entered.Wait(5000));
        var fast = new Thread(() => NpcSpeechEngine.Set(Ramone, "a", SpeechMode.Game, [], 0));
        fast.Start();
        Thread.Sleep(100);
        go.Set();
        Assert.True(slow.Join(5000) && fast.Join(5000));
        var sets = Snapshot().Where(c => c.StartsWith($"set {Ramone.Value} ", StringComparison.Ordinal)).ToArray();
        Assert.Equal($"set {Ramone.Value} 0 []", sets[^1]);
        Assert.NotEqual(SpeechMode.Suppress, Wire.OfType<SpeechMsg>().LastOrDefault()?.Mode ?? SpeechMode.Game);
    }

    [Fact]
    public void Without_the_text_hooks_the_game_keeps_its_lines_until_they_come()
    {
        _textRc = SpeechNative.Unavailable;   // kiln.content's loca hooks are not (yet) here
        var r = Npcs.Speech(Ramone).Replace("No food, no work.");
        Assert.True(r.Ok);
        Assert.Contains("loca", r.Reason);
        Assert.DoesNotContain(Snapshot(), c => c.StartsWith($"set {Ramone.Value} 2", StringComparison.Ordinal));
        Npcs.Say(Balthazar, "Hungry!", TimeSpan.FromSeconds(3));
        Assert.Contains($"say {Balthazar.Value} 00000000 0", Snapshot());     // no empty bubble
        Assert.DoesNotContain(Snapshot(), c => c.StartsWith($"say {Balthazar.Value} ", StringComparison.Ordinal) && !c.EndsWith(" 0", StringComparison.Ordinal));
        Assert.Equal(2, Wire.Count(m => m is SpeechMsg or SayMsg));         // the clients still get both (v1)
        Assert.Single(Wire.OfType<LinesMsg>());                              // and dev.32 clients the Replace on kiln:npcdialog

        _textRc = 0;                                                         // kiln.content came: the same call shows
        Npcs.Speech(Ramone).Replace("No food, no work.");
        Assert.Contains(Snapshot(), c => c.StartsWith($"set {Ramone.Value} 2 [", StringComparison.Ordinal));
    }

    [Fact]
    public void Distinct_texts_stop_at_a_bound()
    {
        // every distinct text stays in kiln.content for the session: a mod (or a host) making new ones without end
        // would grow every Kiln client's memory without end
        for (var i = 0; i < NpcSpeechEngine.MaxTexts; i++)
            Assert.Null(Npcs.Say(Ramone, $"line {i}", TimeSpan.FromSeconds(2)).Reason);
        var r = Npcs.Say(Ramone, "one too many", TimeSpan.FromSeconds(2));
        Assert.True(r.Ok);
        Assert.Contains("texts", r.Reason);
        Assert.EndsWith(" 00000000 0", Snapshot()[^1]);
        Assert.Null(Npcs.Say(Ramone, "line 7", TimeSpan.FromSeconds(2)).Reason);   // a known one still shows
        NpcSpeechEngine.WorldEnded();
        Assert.Null(Npcs.Say(Ramone, "one too many", TimeSpan.FromSeconds(2)).Reason);   // a new world starts again
    }

    [Fact]
    public void Texts_keep_their_letters_and_have_a_text_for_every_language()
    {
        Assert.Contains("Zurück", SpeechText.Json(UiText.Of(("de", "Zurück"))));
        var json = SpeechText.Json(UiText.Of(("de", "Hallo")));
        Assert.Equal("Hallo", SpeechText.FromJson(json).For("*"));           // kiln.content shows it in English too
        Assert.DoesNotContain("\"*\"", SpeechText.Json(UiText.Of(("en", "Hi"), ("de", "Hallo"))));
    }

    [Fact]
    public void An_accepted_call_with_a_note_is_ok_by_the_result_rule()
    {
        // IKilnResult: Reason is null when Ok (KILN120, the kit's and tools' result checks read it so); the note stays on
        // SpeechResult's own Reason
        var r = new SpeechResult(true, "kiln.content's loca hooks are not here");
        Assert.Null(((IKilnResult)r).Reason);
        Assert.NotNull(r.Reason);
        Assert.Equal("why", ((IKilnResult)SpeechResult.No("why")).Reason);
    }
}
