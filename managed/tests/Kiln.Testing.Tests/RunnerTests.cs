// SPDX-License-Identifier: MIT
using System.Text.Json;
using Kiln.Testing;
using Xunit;
using Assert = Xunit.Assert;

namespace Kiln.CheckRunnerTests;

public sealed class RunnerTests
{
    [Fact]
    public void A_pass_on_its_assertions_and_the_verdict()
    {
        using var h = new Harness();
        h.Start("t.pass");
        var c = h.Runner.View.Current!;
        Assert.Equal("asking", c.Phase);
        Assert.Equal("Look at it.", c.Instruction);
        Assert.Contains("Look at it.", CheckScreen.HudText(h.Runner.View, "P", "F", "S", "O"));
        var e = h.Answer(AnswerKind.Pass);
        Assert.Equal(("cct", "1.2.3", "t.pass", "pass", "hosted"), (e.Mod, e.ModVersion, e.Id, e.Verdict, e.Session));
        Assert.Equal(("client", FakeEnv.Sha, true), (e.Game.Side, e.Game.Sha256, e.Game.Verified));
        Assert.Equal(FakeEnv.KilnVersion, e.Kiln);
        Assert.Equal("2026-11-12T01:02:03Z", e.Time);
        Assert.Single(e.Assertions, a => a.Ok && a.What == "one is one");
        Assert.Null(e.Guard);  // not a cheats check
        Assert.Single(h.OnDisk());
        Assert.Equal("pass", h.Runner.View.Items.First(i => i.Id == "t.pass").Verdict);
        Assert.Equal("", CheckScreen.HudText(h.Runner.View, "P", "F", "S", "O"));
    }

    [Fact]
    public void A_fail_with_a_note_and_a_skip_with_one()
    {
        using var h = new Harness();
        h.Start("t.pass");
        Assert.Equal("the flash was green, not blue", h.Answer(AnswerKind.Fail, "the flash was green,\nnot blue").Note);
        h.Start("t.pass");
        var s = h.Answer(AnswerKind.Skip, "not on this build");
        Assert.Equal(("skip", "not on this build"), (s.Verdict, s.Note));
        Assert.Equal(2, h.OnDisk().Count);  // run again: a second entry; the newest counts
        Assert.Equal("skip", h.Runner.View.Items.First(i => i.Id == "t.pass").Verdict);
    }

    [Fact]
    public void Ticks_between_two_instructions()
    {
        using var h = new Harness();
        h.Runner.Request(new StartRequest("cct", "t.two", CheckOrigin.Local));
        h.Runner.Step();
        Assert.Equal("first", h.Runner.View.Current!.Instruction);
        Assert.Equal("running", h.Runner.View.Current.Phase);
        h.Pump(2);
        Assert.Equal("first", h.Runner.View.Current!.Instruction);
        h.Pump(4);
        Assert.Equal("second", h.Runner.View.Current!.Instruction);
        Assert.Equal("asking", h.Runner.View.Current.Phase);
        Assert.Equal("pass", h.Answer(AnswerKind.Pass).Verdict);
    }

    [Fact]
    public void A_verdict_before_the_body_asks_ends_the_body()
    {
        using var h = new Harness();
        h.Runner.Request(new StartRequest("cct", "t.two", CheckOrigin.Local));
        h.Runner.Step();
        var e = h.Answer(AnswerKind.Skip, "bailed out");
        Assert.Equal("skip", e.Verdict);
        Assert.Equal(0, CheckMod.TwoSecond);  // canceled at its next await
    }

    [Fact]
    public void A_body_that_returns_without_asking_is_asked()
    {
        using var h = new Harness();
        h.Start("t.noask");
        var c = h.Runner.View.Current!;
        Assert.Equal("asking", c.Phase);
        Assert.Contains("without asking", c.Instruction);
        var e = h.Answer(AnswerKind.Pass);
        Assert.Equal("pass", e.Verdict);  // the player's verdict; the failed assertion sits beside it
        Assert.Single(e.Assertions, a => !a.Ok && a.What == "two hits" && a.Detail == "seen 1");
    }

    [Fact]
    public void A_body_that_throws_is_asked_and_the_error_recorded()
    {
        using var h = new Harness();
        h.Start("t.throws");
        Assert.Equal("asking", h.Runner.View.Current!.Phase);
        var e = h.Answer(AnswerKind.Fail, "it broke");
        Assert.Contains(e.Assertions, a => !a.Ok && a.Detail!.StartsWith("InvalidOperationException"));
    }

    [Fact]
    public void Clean_ups_run_last_first_however_the_check_ends()
    {
        using var h = new Harness();
        h.Start("t.cleanup");
        h.Answer(AnswerKind.Pass);
        Assert.Equal(11, CheckMod.CleanupRan);
        CheckMod.Reset();
        h.Start("t.cleanup");
        Assert.Null(h.Runner.Answer(AnswerKind.Stop));
        h.Pump();
        Assert.Equal(11, CheckMod.CleanupRan);
        Assert.Single(h.OnDisk());  // the stopped run has no entry
        Assert.Null(h.Last(RunEventKind.Finished)!.Entry);
    }

    [Fact]
    public void A_stop_leaves_no_entry_and_clears_run_all()
    {
        using var h = new Harness();
        h.Runner.RequestAll("Basics", CheckOrigin.Local);
        h.Pump();
        Assert.Null(h.Runner.Answer(AnswerKind.Stop));
        h.Pump();
        Assert.Null(h.Runner.Current);
        Assert.Equal(0, h.Runner.View.Queued);
        Assert.Empty(h.OnDisk());
    }

    [Fact]
    public void An_unload_during_a_check_leaves_no_entry()
    {
        using var h = new Harness();
        h.Start("t.pass");
        TestHooks.Retire(h.Context);
        h.Pump();
        Assert.Null(h.Runner.Current);
        Assert.Empty(h.OnDisk());
    }

    [Fact]
    public void A_confirm_text_waits_for_yes_on_any_check()
    {
        using var h = new Harness();
        h.Start("t.confirm");
        Assert.Equal("confirming", h.Runner.View.Current!.Phase);
        Assert.Equal("Sure?", h.Runner.View.Current.Confirm);
        Assert.Contains("question", CheckScreen.HudText(h.Runner.View, "P", "F", "S", "O"));
        Assert.Null(h.Runner.Answer(AnswerKind.Yes));
        h.Pump();
        Assert.Equal("asking", h.Runner.View.Current!.Phase);
        Assert.Equal("pass", h.Answer(AnswerKind.Pass).Verdict);
    }

    [Theory]
    [InlineData("t.hosted", true, "needs a world this game hosts; this session is joined")]
    [InlineData("t.joined", false, "needs a game joined to a server; this session is hosted")]
    public void Needs_refuses_the_other_session(string id, bool remote, string reason)
    {
        var origin = remote ? CheckOrigin.Remote : CheckOrigin.Local;
        using var h = new Harness();
        h.Start(id, origin, null, origin == CheckOrigin.Remote ? "peer" : null);
        Assert.Equal(reason, h.Last(RunEventKind.Refused)!.Refusal!.Reason);
        Assert.Empty(h.OnDisk());
    }

    [Fact]
    public void Needs_hosted_on_a_dedicated_server_is_refused()
    {
        using var h = new Harness(x =>
        {
            x.Env.IsClient = false;
            x.Env.Hosting = false;
        });
        h.Start("t.hosted", CheckOrigin.Control);
        Assert.Contains("this session is server", h.Last(RunEventKind.Refused)!.Message);
        Assert.Contains(h.Runner.View.Items, i => i.Id == "t.hosted" && i.Refusal!.Contains("needs a world this game hosts"));
        h.Start("t.pass", CheckOrigin.Control);
        Assert.Equal("server", h.Answer(AnswerKind.Pass).Session);
    }

    [Fact]
    public void A_client_in_the_menu_refuses_with_the_reason()
    {
        using var h = new Harness(x => x.Env.Hosting = false);
        h.Start("t.pass");
        Assert.Contains("not hosting a world", h.Last(RunEventKind.Refused)!.Message);
        Assert.Equal("menu", h.Runner.View.Session);
    }

    [Fact]
    public void A_joined_run_answers_only_its_own_client()
    {
        using var h = new Harness();
        h.Start("t.pass", CheckOrigin.Remote, null, "peer-a");
        h.Runner.Request(new StartRequest("cct", "t.two", CheckOrigin.Remote, null, default, "peer-b"));
        Assert.Equal("another check is running (cct/t.pass)", h.Last(RunEventKind.Refused)!.Refusal!.Reason);
        Assert.NotNull(h.Runner.Answer(AnswerKind.Pass));  // this process is not its controller
        Assert.Equal("joined", h.Answer(AnswerKind.Pass, null, "peer-a").Session);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("skip")]
    public void Control_folder_cannot_answer_a_locally_started_check(string command)
    {
        using var h = new Harness(x => x.Env.Character = null);
        h.Start("t.proof");
        if (command is "pass" or "fail" or "skip")
        {
            h.Runner.Answer(AnswerKind.Yes);
            h.Runner.Answer(AnswerKind.Yes);
            h.Pump();
        }
        var dir = Path.Combine(h.Dir, "control");
        Directory.CreateDirectory(dir);
        var control = new ControlFolder(h.Runner, dir, h.Env);
        File.WriteAllText(Path.Combine(dir, "answer.cmd"), command);
        control.Poll(now: true);
        h.Pump();
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "answer.result.json")));
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("control folder", result.RootElement.GetProperty("message").GetString());
        Assert.Empty(h.OnDisk());
        Assert.NotNull(h.Runner.Current);
        if (command is "yes" or "no") Assert.Equal(CheatsRule.ThrowawayQuestion, h.Runner.View.Current!.Confirm);
    }

    [Fact]
    public void Control_folder_can_confirm_and_judge_its_own_noncheats_check()
    {
        using var h = new Harness();
        h.Start("t.confirm", CheckOrigin.Control);
        var dir = Path.Combine(h.Dir, "control");
        Directory.CreateDirectory(dir);
        var control = new ControlFolder(h.Runner, dir, h.Env);
        foreach (var command in new[] { "yes", "pass" })
        {
            File.WriteAllText(Path.Combine(dir, command + ".cmd"), command);
            control.Poll(now: true);
            h.Pump();
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, command + ".result.json")));
            Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Single(h.OnDisk());
    }

    [Fact]
    public void The_control_folder_runs_answers_and_refuses()
    {
        using var h = new Harness();
        var dir = Path.Combine(h.Dir, "control", "checks");
        Directory.CreateDirectory(dir);
        var control = new ControlFolder(h.Runner, dir, h.Env);
        JsonElement Do(string name, string command, int steps = 8)
        {
            File.WriteAllText(Path.Combine(dir, name + ".cmd"), command);
            control.Poll(now: true);
            h.Pump(steps);
            var file = Path.Combine(dir, name + ".result.json");
            Assert.True(File.Exists(file), $"{command}: no result");
            var r = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
            File.Delete(file);
            return r;
        }
        var list = Do("l", "list");
        Assert.True(list.GetProperty("ok").GetBoolean());
        Assert.Equal(19, list.GetProperty("data").GetProperty("checks").GetArrayLength());
        var run = Do("r", "run cct t.pass");
        Assert.True(run.GetProperty("ok").GetBoolean(), run.ToString());
        Assert.Equal("asking", Do("s", "status").GetProperty("data").GetProperty("current").GetProperty("phase").GetString());
        var fail = Do("f", "fail it was red at 10.0.0.9");
        Assert.True(fail.GetProperty("ok").GetBoolean(), fail.ToString());
        Assert.Equal("it was red at <ip>", fail.GetProperty("data").GetProperty("entry").GetProperty("note").GetString());
        var cheats = Do("c", "run cct t.cheats");
        Assert.False(cheats.GetProperty("ok").GetBoolean());
        Assert.StartsWith("refused: line 1", cheats.GetProperty("message").GetString());
        Assert.Equal(0, CheckMod.CheatsRan);
        Assert.False(Do("p", "pass").GetProperty("ok").GetBoolean());  // nothing runs
        Assert.False(Do("u", "dance").GetProperty("ok").GetBoolean());
        var confirm = Do("k", "run cct t.confirm");
        Assert.Equal("confirming", confirm.GetProperty("data").GetProperty("kind").GetString());
        Assert.False(Do("n", "no").GetProperty("ok").GetBoolean());  // declined: ok false, no entry
        Assert.Single(h.OnDisk());
    }
}
