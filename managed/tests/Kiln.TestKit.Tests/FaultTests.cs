// SPDX-License-Identifier: MIT
using TkFaulty;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class FaultTests
{
    [Fact]
    public void AFaultFailsTheCallByDefault()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkFaultyMod>(new ModManifest("tk-faulty"));
        var e = Assert.Throws<ModFaultException>(() => kiln.Step());
        var fault = Assert.Single(e.Faults);
        Assert.StartsWith("mod tk-faulty threw in Pre (1/5): System.InvalidOperationException: tk-faulty broke at step 1", fault);
        Assert.Equal(1UL, kiln.CurrentStep);  // the step ran to its end
        Assert.Throws<ModFaultException>(() => kiln.Step());
        Assert.Equal(2UL, kiln.CurrentStep);
    }

    [Fact]
    public void WithTheOptionOffTheFaultPolicyDisablesTheMod()
    {
        using var kiln = new KilnHarness(new KilnHarnessOptions { ThrowOnModFault = false });
        var mod = kiln.Load<TkFaultyMod>(new ModManifest("tk-faulty"));
        var disabled = new List<string>();
        kiln.Run(() => Events.On<ModDisabledEvent>(e => disabled.Add($"{e.Id} {e.Faults} {e.Reason}")));
        kiln.Step(4);
        Assert.Equal(ModState.Loaded, mod.State);
        Assert.Empty(disabled);
        kiln.Step();
        Assert.Equal(ModState.Disabled, mod.State);
        Assert.StartsWith("disabled after 5 faults within 60 s", mod.Error);
        Assert.Equal(["tk-faulty 5 InvalidOperationException: tk-faulty broke at step 5 (in Pre)"], disabled);
        kiln.Step(3);
        Assert.Equal(5, kiln.FindLogs("tk-faulty broke").Count(l => l.Source == "host" && l.Text.StartsWith("mod tk-faulty threw", StringComparison.Ordinal)));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(kiln.KilnDirectory, "crash-reports")));
    }

    [Fact]
    public void TheFaultWindowCountsVirtualTime()
    {
        // one fault a step, 40.8 ms of virtual time apart: never 3 within 50 ms (in real time 10 steps take less than that)
        using var kiln = new KilnHarness(new KilnHarnessOptions { ThrowOnModFault = false, FaultLimit = 3, FaultWindowSeconds = 0.05 });
        var mod = kiln.Load<TkFaultyMod>(new ModManifest("tk-faulty"));
        kiln.Step(10);
        Assert.Equal(ModState.Loaded, mod.State);
    }
}
