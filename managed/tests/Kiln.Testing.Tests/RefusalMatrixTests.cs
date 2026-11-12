// SPDX-License-Identifier: MIT
using Kiln.Testing;
using Xunit;
using Assert = Xunit.Assert;

namespace Kiln.CheckRunnerTests;

/// <summary>The rule for checks tagged cheats: every state but the one that holds every line refuses, names its line,
/// leaves no entry, and never runs the body.</summary>
public sealed class RefusalMatrixTests
{
    private static Attestation Good(DateTime now) => new()
    {
        Latched = true, Status = new GuardStatus(true, true, true, true, 0, 0, "latched"), Proof = "pass", Sha256 = FakeEnv.Sha,
        Kiln = FakeEnv.KilnVersion, Taken = now,
    };

    private static void Refused(Harness h, string line, string? words = null)
    {
        var r = h.Last(RunEventKind.Refused);
        Assert.NotNull(r);
        Assert.NotNull(r!.Refusal);
        Assert.Equal(line, r.Refusal!.Line);
        if (words is not null) Assert.Contains(words, r.Refusal.Reason);
        Assert.Equal(0, CheckMod.CheatsRan);
        Assert.Equal(0, CheckMod.ProofRan);
        Assert.Null(h.Runner.Current);
        Assert.Equal(h.Seeded, h.OnDisk().Count);  // no entry
        Assert.DoesNotContain(h.Events, e => e.Kind == RunEventKind.Started);
        // the refusal is in kiln.log and in the list
        Assert.Contains(h.Env.Written, l => l.Contains("refused cct/") && l.Contains(line));
        Assert.Contains(h.Runner.View.Items, i => i.Refusal is { } x && x.StartsWith(line));
    }

    [Fact]
    public void Guard_proof_without_cheats_is_refused_at_registration()
    {
        using var h = new Harness();
        var c = CheckRegistry.All.Single(c => c.Id == "t.proof-unsafe");
        Assert.Contains("cheats", c.Problem);
        h.Start(c.Id);
        Assert.Contains("cheats", h.Last(RunEventKind.Refused)!.Message);
        Assert.Equal(0, CheckMod.ProofRan);
        Assert.Contains(h.Runner.View.Items, i => i.Id == c.Id && i.Refusal != null);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_handwritten_pass_without_both_tags_and_the_counter_assertion_opens_nothing(bool cheats, bool assertion)
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass") with
        {
            Tags = cheats ? [ClientCheckTags.Cheats, ClientCheckTags.GuardProof] : [ClientCheckTags.GuardProof],
            Assertions = assertion ? [new LedgerAssertion { What = "the guard dropped at least one report", Ok = true }] : [],
        }));
        h.Start("t.cheats");
        Refused(h, "line 4");
    }

    // ---- line 1 ----

    [Fact]
    public void From_the_control_folder_in_a_hosted_world()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Start("t.cheats", CheckOrigin.Control);
        Refused(h, "line 1", "control folder");
        Assert.Equal(0, h.Guard!.Latches);
    }

    [Fact]
    public void On_a_dedicated_server()
    {
        using var h = new Harness(x =>
        {
            x.Env.IsClient = false;
            x.Env.Hosting = false;
            x.Env.Game = x.Env.Game with { Side = "server" };
        });
        h.Start("t.cheats", CheckOrigin.Control);
        Refused(h, "line 1", "control folder");
    }

    [Fact]
    public void Together_with_two_players()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Start("t.cheats-two");
        Refused(h, "line 1", "two players");
    }

    [Fact]
    public void Joined_without_the_clients_status()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Start("t.cheats", CheckOrigin.Remote, null, "peer");
        Refused(h, "line 1", "did not come");
    }

    [Fact]
    public void Joined_with_a_late_status()
    {
        using var h = new Harness();
        h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now.AddSeconds(-60)), "peer");
        Refused(h, "line 1", "late");
    }

    // ---- line 2 ----

    [Fact]
    public void The_guard_is_absent()
    {
        using var h = new Harness(x =>
        {
            x.Seed(Harness.ProofEntry("pass"));
            x.Env.Guard = null;
        });
        h.Start("t.cheats");
        Refused(h, "line 2", "not there");
    }

    [Fact]
    public void The_latch_throws()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Guard!.LatchThrows = true;
        h.Start("t.cheats");
        Refused(h, "line 2", "threw");
    }

    [Fact]
    public void The_latch_returns_false()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Guard!.LatchReturns = false;
        h.Start("t.cheats");
        Refused(h, "line 2", "returned false");
    }

    [Fact]
    public void Joined_latch_returned_false_on_the_client()
    {
        using var h = new Harness();
        h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now) with { Latched = false }, "peer");
        Refused(h, "line 2", "latch returned false");
    }

    [Fact]
    public void Joined_client_could_not_answer()
    {
        using var h = new Harness();
        h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now) with { Problem = "the achievement guard is not there" }, "peer");
        Refused(h, "line 2", "could not answer");
    }

    // ---- line 3 ----

    [Fact]
    public void The_status_did_not_arrive_after_the_latch()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Guard!.NullAfterLatch = true;
        h.Start("t.cheats");
        Refused(h, "line 3", "did not arrive");
    }

    [Fact]
    public void The_status_read_throws()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Guard!.StatusThrows = true;
        h.Start("t.cheats");
        Refused(h, "line 3", "threw");
    }

    [Fact]
    public void The_status_arrives_late()
    {
        var keep = CheatsRule.StatusDeadline;
        CheatsRule.StatusDeadline = TimeSpan.FromMilliseconds(5);
        try
        {
            using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
            h.Guard!.StatusDelay = TimeSpan.FromMilliseconds(60);
            h.Start("t.cheats");
            Refused(h, "line 3", "late");
        }
        finally
        {
            CheatsRule.StatusDeadline = keep;
        }
    }

    [Theory]
    [InlineData(false, true, true, "hook not in")]
    [InlineData(true, false, true, "target not verified")]
    [InlineData(true, true, false, "off")]
    public void Not_protecting_after_the_latch(bool hook, bool target, bool active, string words)
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Guard!.Value = new GuardStatus(true, hook, target, false, 0, 0, "test");
        h.Guard.StaysOff = !active;
        h.Start("t.cheats");
        Refused(h, "line 3", words);
    }

    [Fact]
    public void Not_available_after_the_latch()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Guard!.Value = new GuardStatus(false, false, false, false, 0, 0, "no service");
        h.Start("t.cheats");
        Refused(h, "line 3", "not available");
    }

    [Fact]
    public void Joined_client_not_protecting()
    {
        using var h = new Harness();
        h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now) with { Status = new GuardStatus(true, true, false, true, 0, 0, "x") }, "peer");
        Refused(h, "line 3", "not protecting");
    }

    [Fact]
    public void Joined_status_missing_after_the_clients_latch()
    {
        using var h = new Harness();
        h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now) with { Status = null }, "peer");
        Refused(h, "line 3", "did not arrive");
    }

    // ---- line 4 ----

    [Fact]
    public void No_guard_proof_entry()
    {
        using var h = new Harness();
        h.Start("t.cheats");
        Refused(h, "line 4", "no guard-proof check has run");
    }

    [Fact]
    public void Newest_guard_proof_failed_after_an_older_pass()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass"), Harness.ProofEntry("fail")));
        h.Start("t.cheats");
        Refused(h, "line 4", "failed");
    }

    [Fact]
    public void Newest_guard_proof_was_skipped()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass"), Harness.ProofEntry("skip")));
        h.Start("t.cheats");
        Refused(h, "line 4", "skipped");
    }

    [Fact]
    public void A_pass_for_another_game_build()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass", sha: new string('c', 64))));
        h.Start("t.cheats");
        Refused(h, "line 4", "no guard-proof check has run");
    }

    [Fact]
    public void A_pass_for_another_kiln_version()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass", kiln: "0.2.0-dev.24")));
        h.Start("t.cheats");
        Refused(h, "line 4", "no guard-proof check has run");
    }

    [Fact]
    public void A_pass_written_by_a_server_does_not_count()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass", side: "server")));
        h.Start("t.cheats");
        Refused(h, "line 4", "no guard-proof check has run");
    }

    [Fact]
    public void The_ledger_cannot_be_read()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        File.WriteAllText(h.LedgerPath, "{ not json");  // after the runner loaded it: the rule reads the file again
        h.Start("t.cheats");
        Refused(h, "line 4", "could not be read");
    }

    [Fact]
    public void The_game_build_is_not_known()
    {
        using var h = new Harness(x =>
        {
            x.Seed(Harness.ProofEntry("pass"));
            x.Env.Game = x.Env.Game with { Sha256 = null };
        });
        h.Start("t.cheats");
        Refused(h, "line 4", "not known");
    }

    [Fact]
    public void Joined_client_ledger_has_no_pass()
    {
        foreach (var proof in new[] { "fail", "skip", "none", "unreadable", "unknown" })
        {
            using var h = new Harness();
            h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now) with { Proof = proof }, "peer");
            Refused(h, "line 4");
        }
    }

    [Fact]
    public void Guard_proof_without_a_confirm_text()
    {
        using var h = new Harness();
        h.Start("t.proof-bare");
        Refused(h, "line 4", "Confirm");
        Assert.Equal(0, h.Guard!.Latches);
    }

    [Fact]
    public void Guard_proof_without_a_yes_does_not_start()
    {
        using var h = new Harness();
        h.Start("t.proof");
        Assert.NotNull(h.Last(RunEventKind.Confirming));
        Assert.Equal(0, h.Guard!.Latches);  // nothing read or latched before the yes
        Assert.NotNull(h.Runner.Answer(AnswerKind.Pass));  // the pass key is not a yes
        h.Pump();
        Assert.Equal(0, CheckMod.ProofRan);
        Assert.Null(h.Runner.Answer(AnswerKind.No));
        h.Pump();
        Assert.NotNull(h.Last(RunEventKind.Declined));
        Assert.Equal(0, CheckMod.ProofRan);
        Assert.Empty(h.OnDisk());
        Assert.DoesNotContain(h.Events, e => e.Kind == RunEventKind.Started);
    }

    [Fact]
    public void Stop_while_waiting_for_the_yes_does_not_start()
    {
        using var h = new Harness();
        h.Start("t.proof");
        Assert.Null(h.Runner.Answer(AnswerKind.Stop));
        h.Pump();
        Assert.Equal(0, CheckMod.ProofRan);
        Assert.Empty(h.OnDisk());
    }

    // ---- the states that run ----

    private static void DropOneReport(Harness h) => CheckMod.DuringBody = () =>
        h.Guard!.Value = h.Guard.Value!.Value with { Dropped = h.Guard.Value.Value.Dropped + 1 };

    [Fact]
    public void Guard_proof_runs_after_yes_with_the_guard_latched()
    {
        using var h = new Harness();
        DropOneReport(h);
        h.Start("t.proof");
        Assert.Null(h.Runner.Answer(AnswerKind.Yes));
        h.Pump();
        Assert.Equal(1, CheckMod.ProofRan);
        Assert.True(h.Guard!.Latched);
        var e = h.Answer(AnswerKind.Pass);
        Assert.Equal("pass", e.Verdict);
        Assert.NotNull(e.Guard);
        Assert.True(e.Guard!.Active && e.Guard.HookInstalled && e.Guard.TargetVerified);
        Assert.Equal("", e.Note);
        Assert.Contains(e.Assertions, a => a.Ok && a.What == "the guard dropped at least one report" && a.Detail == "0 before, 1 after");
        Assert.Null(h.Runner.View.Warning);
    }

    [Fact]
    public void Cheats_runs_when_the_newest_guard_proof_passed_on_this_build()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("fail"), Harness.ProofEntry("pass")));
        h.Start("t.cheats");
        Assert.Equal(1, CheckMod.CheatsRan);
        Assert.True(h.Guard!.Latched);
        var e = h.Answer(AnswerKind.Pass);
        Assert.NotNull(e.Guard);
    }

    [Fact]
    public void Joined_runs_with_the_clients_full_answer()
    {
        using var h = new Harness();
        h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now), "peer");
        Assert.Equal(1, CheckMod.CheatsRan);
        Assert.Equal(0, h.Guard!.Latches);  // the joining client's guard answered, not this process's
        Assert.NotNull(h.Runner.Answer(AnswerKind.Pass, null, "someone else"));
        var e = h.Answer(AnswerKind.Pass, null, "peer");
        Assert.Equal("joined", e.Session);
        Assert.NotNull(e.Guard);
        Assert.Contains("guard warning", e.Note);  // the verdict came without the client's status at the end
    }

    [Fact]
    public void The_gate_opens_with_a_pass_and_closes_again_with_a_later_fail()
    {
        using var h = new Harness();
        DropOneReport(h);
        h.Start("t.cheats");
        Assert.Equal("line 4", h.Last(RunEventKind.Refused)!.Refusal!.Line);
        h.Start("t.proof");
        h.Runner.Answer(AnswerKind.Yes);
        h.Pump();
        h.Answer(AnswerKind.Pass);
        h.Start("t.cheats");
        Assert.Equal(1, CheckMod.CheatsRan);
        h.Answer(AnswerKind.Pass);
        h.Start("t.proof");
        h.Runner.Answer(AnswerKind.Yes);
        h.Pump();
        h.Answer(AnswerKind.Fail, "the counter did not move");
        h.Events.Clear();
        h.Start("t.cheats");
        Assert.Equal(1, CheckMod.CheatsRan);
        Assert.Contains("failed", h.Last(RunEventKind.Refused)!.Refusal!.Reason);
    }

    [Fact]
    public void Passed_rising_during_the_check_is_flagged()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Start("t.cheats");
        Assert.Equal(1, CheckMod.CheatsRan);
        h.Guard!.Value = h.Guard.Value!.Value with { Passed = 1 };
        var e = h.Answer(AnswerKind.Pass);
        Assert.Contains("guard warning", e.Note);
        Assert.Contains("passed 1 achievement report", e.Note);
        Assert.Equal(1, e.Guard!.Passed);
        Assert.NotNull(h.Runner.View.Warning);
        Assert.Contains(h.Env.Written, l => l.StartsWith("[Error]") && l.Contains("guard warning"));
        // the warning stays (the HUD line shows it) after the check
        h.Pump();
        Assert.NotNull(h.Runner.View.Warning);
        Assert.Contains("GUARD WARNING", CheckScreen.HudText(h.Runner.View, "a", "b", "c", "d"));
    }

    [Fact]
    public void Protecting_going_false_during_the_check_is_flagged()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Start("t.cheats");
        h.Guard!.Value = h.Guard.Value!.Value with { HookInstalled = false };
        var e = h.Answer(AnswerKind.Skip);
        Assert.Contains("not protecting", e.Note);
        Assert.False(e.Guard!.HookInstalled);
    }

    [Fact]
    public void The_runner_never_releases_the_latch_and_holds_the_guard_for_the_run()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Start("t.pass");
        Assert.Equal(1, h.Guard!.Holds);
        h.Answer(AnswerKind.Pass);
        Assert.Equal(1, h.Guard.Releases);  // the run ended: its hold went
        h.Start("t.cheats");
        h.Answer(AnswerKind.Pass);
        Assert.True(h.Guard.Latched);  // no call ends a latch
    }

    [Fact]
    public void A_failed_hold_does_not_stop_checks_without_the_tag()
    {
        using var h = new Harness(x => x.Env.Guard = new NoHoldGuard());
        h.Start("t.pass");
        Assert.Equal("pass", h.Answer(AnswerKind.Pass).Verdict);
    }

    // ---- line 5: a throwaway character ----

    [Fact]
    public void A_character_without_the_prefix()
    {
        using var h = new Harness(x =>
        {
            x.Seed(Harness.ProofEntry("pass"));
            x.Env.Character = "Ada";
        });
        h.Start("t.cheats");
        Refused(h, "line 5", "(this one is 'Ada')");
    }

    [Theory]
    [InlineData("kilncheck lower case")]
    [InlineData(" KilnCheck leading space")]
    [InlineData("MyKilnCheck")]
    public void The_prefix_is_ordinal_and_at_the_start(string name)
    {
        using var h = new Harness(x =>
        {
            x.Seed(Harness.ProofEntry("pass"));
            x.Env.Character = name;
        });
        h.Start("t.cheats");
        Refused(h, "line 5", "throwaway character");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unreadable_name_asks_and_no_yes_does_not_start(bool throws)
    {
        using var h = new Harness(x =>
        {
            x.Seed(Harness.ProofEntry("pass"));
            x.Env.Character = null;
            x.Env.CharacterThrows = throws;
        });
        h.Start("t.cheats");
        Assert.Equal(CheatsRule.ThrowawayQuestion, h.Runner.View.Current!.Confirm);
        Assert.Equal(0, h.Guard!.Latches);  // nothing latched before the yes
        Assert.NotNull(h.Runner.Answer(AnswerKind.Pass));  // the pass key is not a yes
        Assert.Null(h.Runner.Answer(AnswerKind.No));
        h.Pump();
        Assert.NotNull(h.Last(RunEventKind.Declined));
        Assert.Equal(0, CheckMod.CheatsRan);
        Assert.Equal(h.Seeded, h.OnDisk().Count);
    }

    [Fact]
    public void An_unreadable_name_and_a_yes_runs_and_says_so_in_the_entry()
    {
        using var h = new Harness(x =>
        {
            x.Seed(Harness.ProofEntry("pass"));
            x.Env.Character = null;
        });
        h.Start("t.cheats");
        Assert.Null(h.Runner.Answer(AnswerKind.Yes));
        h.Pump();
        Assert.Equal(1, CheckMod.CheatsRan);
        var e = h.Answer(AnswerKind.Pass);
        Assert.Contains(e.Assertions, a => a.Ok && a.What == "throwaway character: confirmed by the player");
        // asked again for the next cheats check: a yes is never remembered
        h.Events.Clear();
        h.Start("t.cheats");
        Assert.Equal(CheatsRule.ThrowawayQuestion, h.Runner.View.Current!.Confirm);
        Assert.Equal(1, CheckMod.CheatsRan);
    }

    [Fact]
    public void Guard_proof_with_an_unreadable_name_asks_twice_the_runner_first()
    {
        using var h = new Harness(x => x.Env.Character = null);
        DropOneReport(h);
        h.Start("t.proof");
        Assert.Equal(CheatsRule.ThrowawayQuestion, h.Runner.View.Current!.Confirm);
        Assert.Null(h.Runner.Answer(AnswerKind.Yes));
        h.Pump();
        Assert.StartsWith("This writes an achievement.", h.Runner.View.Current!.Confirm);
        Assert.Equal(0, CheckMod.ProofRan);
        Assert.Null(h.Runner.Answer(AnswerKind.Yes));
        h.Pump();
        Assert.Equal(1, CheckMod.ProofRan);
        Assert.Equal("pass", h.Answer(AnswerKind.Pass).Verdict);
    }

    [Fact]
    public void A_readable_name_without_the_prefix_is_refused_without_a_question()
    {
        using var h = new Harness(x =>
        {
            x.Seed(Harness.ProofEntry("pass"));
            x.Env.Character = "Ada";
        });
        h.Start("t.cheats");
        Assert.Null(h.Last(RunEventKind.Confirming));  // no question: a yes could not replace the name
        Refused(h, "line 5", "'Ada'");
    }

    [Fact]
    public void Joined_reads_the_name_on_the_server_too()
    {
        using var h = new Harness(x => x.Env.Character = "Bob");
        h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now) with { Throwaway = true }, "peer");
        Refused(h, "line 5", "'Bob'");
    }

    [Fact]
    public void Joined_with_an_unreadable_name_needs_the_clients_yes()
    {
        using var h = new Harness(x => x.Env.Character = null);
        h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now), "peer");
        Refused(h, "line 5", "did not confirm");
        h.Events.Clear();
        h.Start("t.cheats", CheckOrigin.Remote, Good(h.Env.Now) with { Throwaway = true }, "peer");
        Assert.Equal(1, CheckMod.CheatsRan);
        var e = h.Answer(AnswerKind.Pass, null, "peer");
        Assert.Contains(e.Assertions, a => a.What == "throwaway character: confirmed by the player");
    }

    [Fact]
    public void The_prefix_lets_it_run_without_the_question()
    {
        using var h = new Harness(x =>
        {
            x.Seed(Harness.ProofEntry("pass"));
            x.Env.Character = "KilnCheck 1";
        });
        h.Start("t.cheats");
        Assert.Null(h.Last(RunEventKind.Confirming));
        Assert.Equal(1, CheckMod.CheatsRan);
        Assert.Contains(h.Answer(AnswerKind.Pass).Assertions, a => a.Ok && a.What == "throwaway character: name starts with KilnCheck");
    }

    // ---- guard-proof and the dropped counter ----

    [Fact]
    public void Guard_proof_with_the_counter_unchanged_is_a_fail_even_after_pass_and_the_gate_stays_closed()
    {
        using var h = new Harness();
        h.Start("t.proof");
        h.Runner.Answer(AnswerKind.Yes);
        h.Pump();
        Assert.Equal(1, CheckMod.ProofRan);
        var e = h.Answer(AnswerKind.Pass, "looked fine");
        Assert.Equal("fail", e.Verdict);
        Assert.StartsWith("the guard's dropped counter did not go up (0 before, 0 after): the guard did not see an achievement report", e.Note);
        Assert.Contains("the player answered pass", e.Note);
        Assert.Contains("looked fine", e.Note);
        Assert.Contains(e.Assertions, a => !a.Ok && a.What == "the guard dropped at least one report");
        h.Events.Clear();
        h.Start("t.cheats");
        Assert.Equal(0, CheckMod.CheatsRan);
        Assert.Contains("failed", h.Last(RunEventKind.Refused)!.Refusal!.Reason);
    }

    [Fact]
    public void Guard_proof_with_the_counter_up_and_pass_opens_the_gate()
    {
        using var h = new Harness();
        DropOneReport(h);
        h.Start("t.proof");
        h.Runner.Answer(AnswerKind.Yes);
        h.Pump();
        Assert.Equal("pass", h.Answer(AnswerKind.Pass).Verdict);
        h.Start("t.cheats");
        Assert.Equal(1, CheckMod.CheatsRan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_dropped_report_does_not_pass_proof_if_protection_failed_or_a_report_escaped(bool escaped)
    {
        using var h = new Harness();
        h.Start("t.proof");
        Assert.Null(h.Runner.Answer(AnswerKind.Yes));
        h.Pump();
        var ready = h.Guard!.Value!.Value;
        h.Guard.Value = ready with { Dropped = 1, Passed = escaped ? 1 : 0, HookInstalled = escaped };
        var entry = h.Answer(AnswerKind.Pass);
        Assert.Equal("fail", entry.Verdict);
        Assert.Contains("guard warning", entry.Note);
        h.Guard.Value = ready;
        h.Start("t.cheats");
        Assert.Equal(0, CheckMod.CheatsRan);
        Assert.Contains("failed", h.Last(RunEventKind.Refused)!.Message);
    }

    [Fact]
    public void Joined_guard_proof_reads_the_clients_counters()
    {
        using var h = new Harness();
        var start = Good(h.Env.Now) with { Proof = "none" };
        h.Start("t.proof", CheckOrigin.Remote, start, "peer");
        h.Runner.Answer(AnswerKind.Yes, null, "peer");
        h.Pump();
        Assert.Equal(1, CheckMod.ProofRan);
        Assert.Null(h.Runner.Answer(AnswerKind.Pass, null, "peer", start.Status!.Value with { Dropped = 2 }));
        h.Pump();
        var e = h.Last(RunEventKind.Finished)!.Entry!;
        Assert.Equal("pass", e.Verdict);
        Assert.Contains(e.Assertions, a => a.Ok && a.Detail == "0 before, 2 after");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Guard_warnings_and_proof_overrides_keep_the_note_within_the_schema(bool proof)
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Start(proof ? "t.proof" : "t.cheats");
        if (proof)
        {
            Assert.Null(h.Runner.Answer(AnswerKind.Yes));
            h.Pump();
        }
        h.Guard!.Value = h.Guard.Value!.Value with { HookInstalled = false, Reason = string.Concat(Enumerable.Repeat("guard failure ", 20)) };
        var entry = h.Answer(AnswerKind.Pass, string.Concat(Enumerable.Repeat("a long note ", 30)));
        Assert.True(entry.Note.Length <= 600, $"note length {entry.Note.Length} exceeds schema maxLength 600");
        Assert.Contains("guard warning", entry.Note);
        Assert.Equal(entry.Note, h.OnDisk().Last().Note);
    }

    // ---- the line that stays after a cheats check ----

    [Fact]
    public void The_delete_the_character_line_comes_after_a_cheats_check_started_and_stays()
    {
        using var h = new Harness(x => x.Seed(Harness.ProofEntry("pass")));
        h.Start("t.pass");
        Assert.Null(h.Runner.View.Changed);
        h.Answer(AnswerKind.Pass);
        Assert.Null(h.Runner.View.Changed);
        h.Env.Character = "Ada";
        h.Start("t.cheats");  // refused: never started, so no line
        Assert.Null(h.Runner.View.Changed);
        h.Env.Character = "KilnCheck 2";
        h.Start("t.cheats");
        Assert.Equal(CheckRunner.ChangedCharacter, h.Runner.View.Changed);
        Assert.Contains("Delete it before you play on", CheckScreen.HudText(h.Runner.View, "a", "b", "c", "d"));
        h.Answer(AnswerKind.Skip);
        h.Start("t.pass");
        h.Answer(AnswerKind.Pass);
        Assert.Equal(CheckRunner.ChangedCharacter, h.Runner.View.Changed);  // no answer clears it
        Assert.Contains("Delete it before you play on", CheckScreen.HudText(h.Runner.View, "a", "b", "c", "d"));
        h.Env.WorldSession = 2;  // the world was left
        h.Pump();
        Assert.Null(h.Runner.View.Changed);
    }

    private sealed class NoHoldGuard : IAchievementGuard
    {
        public GuardStatus? Status() => new GuardStatus(false, false, false, false, 0, 0, "none");
        public bool Latch(string reason) => false;
        public bool TryHold(string reason, out IDisposable? hold)
        {
            hold = null;
            return false;
        }
    }
}
