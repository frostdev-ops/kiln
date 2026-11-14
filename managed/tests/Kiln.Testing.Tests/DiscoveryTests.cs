// SPDX-License-Identifier: MIT
using Kiln.Testing;
using Xunit;
using Assert = Xunit.Assert;

namespace Kiln.CheckRunnerTests;

public sealed class DiscoveryTests
{
    [Fact]
    public void Every_marked_method_is_found_with_its_attribute()
    {
        using var h = new Harness();
        var all = CheckRegistry.All;
        Assert.Equal(20, all.Count);  // 17 on CheckMod, one on OtherChecks, one static
        var proof = all.Single(c => c.Id == "t.proof");
        Assert.Equal("Cheats", proof.Area);
        Assert.True(proof.Cheats && proof.GuardProof);
        Assert.NotNull(proof.Confirm);
        Assert.Equal(9, proof.Order);
        var hosted = all.Single(c => c.Id == "t.hosted");
        Assert.Equal("Check test", hosted.Area);  // no Area: the mod's name
        Assert.Equal(ClientCheckNeeds.Hosted, hosted.Needs);
        Assert.Equal("1.2.3", hosted.ModVersion);
        Assert.Contains(all, c => c.Id == "t.other" && c.Problem is null);
        Assert.Contains(all, c => c.Id == "t.static" && c.Problem is null);
    }

    [Fact]
    public void Shape_problems_are_listed_and_refused()
    {
        using var h = new Harness();
        var all = CheckRegistry.All;
        Assert.Contains("lower-case", all.Single(c => c.Title == "bad id").Problem);
        Assert.Contains("one CheckContext", all.Single(c => c.Id == "t.sig").Problem);
        var pass = all.Where(c => c.Id == "t.pass").ToList();
        Assert.Equal(2, pass.Count);
        Assert.Null(pass.Single(c => c.Method!.Name == "Pass").Problem);  // the first declared keeps the id
        Assert.Contains("used by another check", pass.Single(c => c.Method!.Name == "Dup").Problem);
        h.Start("t.sig");
        Assert.Contains("one CheckContext", h.Last(RunEventKind.Refused)!.Message);
    }

    [Fact]
    public void Run_order_is_order_then_registration_then_declaration()
    {
        using var h = new Harness();
        var ids = CheckRegistry.All.Select(c => c.Id).ToList();
        Assert.Equal("t.early", ids[0]);  // Order -5
        var zero = ids.Where(id => id is "t.hosted" or "t.joined" or "t.confirm" or "t.other" or "t.static").ToList();
        Assert.Equal(["t.hosted", "t.joined", "t.confirm", "t.other", "t.static"], zero);  // CheckMod in declaration order, then the other classes
        Assert.True(ids.IndexOf("t.pass") < ids.IndexOf("t.two") && ids.IndexOf("t.two") < ids.IndexOf("t.proof") &&
                    ids.IndexOf("t.proof") < ids.IndexOf("t.cheats"));
    }

    [Fact]
    public void A_second_registration_of_the_same_instance_does_nothing_and_an_unload_removes_it()
    {
        using var h = new Harness();
        Assert.Equal(20, ClientChecks.Register(h.Mod));
        Assert.Single(CheckRegistry.Mods);
        TestHooks.Retire(h.Context);
        Assert.Empty(CheckRegistry.All);
    }

    [Fact]
    public void Register_without_a_context_throws()
    {
        Assert.Throws<ArgumentException>(() => ClientChecks.Register(new CheckMod()));
    }

    [Fact]
    public void Run_all_follows_the_order_and_a_filter()
    {
        using var h = new Harness();
        Assert.Equal(5, h.Runner.RequestAll("Basics", CheckOrigin.Local));
        h.Pump();
        Assert.Equal("t.pass", h.Runner.Current!.Case.Id);
        Assert.Equal(4, h.Runner.View.Queued);
        h.Answer(AnswerKind.Pass);
        Assert.Equal("t.two", h.Runner.Current!.Case.Id);
    }
}
