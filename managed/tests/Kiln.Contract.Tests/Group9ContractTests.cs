// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Time (the clock, its speed, the weather) by the contract.</summary>
public sealed class TimeContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Time).Assembly);
        Assert.False(Time.Available);
        Assert.False(Time.Ready);
        Assert.False(Time.CanSetSpeed);
        AssertOffline("kiln.time");
        AssertOffOffline("time.speed", "needs kiln.time");
        foreach (var name in (string[])["time.read", "time.set", "time.scale", "time.weather"])
            AssertOffOffline(name, "needs kiln.time");
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.time", () => _ = Time.State);
        AssertUnavailable("kiln.time", () => Time.Set(7.5));
        AssertUnavailable("kiln.time", () => Time.ResetWeather());
        AssertUnavailable("time.speed", () => Time.SetSpeed(0));
        Assert.False(Time.TryGetState(out _));
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Time));
        AssertHas<AnyThreadAttribute>(Property(typeof(Time), "State"));
        AssertRequires(Property(typeof(Time), "State"), "kiln.time");
        var set = Method(typeof(Time), "Set", typeof(double), typeof(TimeSetMode));
        AssertHas<TickThreadOnlyAttribute>(set);
        AssertRequires(set, "kiln.time");
        AssertRequires(Method(typeof(Time), "SetSpeed", typeof(double?)), "time.speed");
    }
}
