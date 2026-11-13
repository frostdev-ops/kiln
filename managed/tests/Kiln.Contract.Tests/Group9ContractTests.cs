// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

/// <summary>Time.SetSpeed on a fake kiln.time table (its table structs are private: laid out by hand, header, three v1
/// functions, then set_time_scale and time_scale_hold).</summary>
public sealed unsafe class TimeSpeedChangeTests : FakeTableTest
{
    private static float _hold;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSetScale(float v)
    {
        _hold = v < 0 ? -1 : v;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static float FakeHold() => _hold;

    private static bool _clock;
    private static nuint _world;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeGet(void* state) => _clock ? 0 : 3;  // unavailable: no clock bound

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint FakeServerWorld() => _world;

    public TimeSpeedChangeTests()
    {
        _hold = -1;  // the game decides
        _clock = false;
        _world = 0;
        var size = sizeof(ServiceHeader) + 5 * sizeof(nint);
        var t = (byte*)NativeMemory.AllocZeroed((nuint)size);
        *(ServiceHeader*)t = new ServiceHeader { Size = (uint)size, Version = 1 };
        *(delegate* unmanaged[Cdecl]<void*, int>*)(t + sizeof(ServiceHeader)) = &FakeGet;
        *(delegate* unmanaged[Cdecl]<float, int>*)(t + sizeof(ServiceHeader) + 3 * sizeof(nint)) = &FakeSetScale;
        *(delegate* unmanaged[Cdecl]<float>*)(t + sizeof(ServiceHeader) + 4 * sizeof(nint)) = &FakeHold;
        var f = typeof(Time).GetField("_table", BindingFlags.Static | BindingFlags.NonPublic)!;
        Swap(typeof(Time), "_table", Pointer.Box(null, f.FieldType));
        Backend.Tables["kiln.time"] = (nint)t;
    }

    [Fact]
    public void Setting_the_speed_posts_one_event_and_setting_it_again_none()
    {
        var seen = new List<TimeSpeedChangedEvent>();
        using var sub = Events.On<TimeSpeedChangedEvent>(seen.Add);
        Time.SetSpeed(0);
        Time.SetSpeed(0);
        Time.SetCycle(TimeCycle.Normal);  // gives the clock back to the game
        Assert.Equal(2, seen.Count);
        Assert.Equal(((double?)null, (double?)0, "writer"), (seen[0].Before, seen[0].After, seen[0].By));
        Assert.Equal(((double?)0, (double?)null), (seen[1].Before, seen[1].After));
    }

    [Fact]
    public void Time_speed_is_pending_until_the_server_world_s_clock_is_there()
    {
        var w = Alloc<WorldPrefix>();
        w->Header = new ServiceHeader { Size = (uint)sizeof(WorldPrefix), Version = 1 };
        w->ServerWorld = &FakeServerWorld;
        Backend.Tables["kiln.world"] = (nint)w;
        StartRows(typeof(Time).Assembly);  // a client in its menu: kiln.time can hold a speed, there is no server clock
        Assert.Equal(CapabilityState.Pending, Capabilities.Get("time.speed").State);
        _clock = true;
        _world = 0x1000;  // a server world runs and kiln.time bound its clock
        TimeCaps.Rows.Refresh();
        Assert.True(Capabilities.Is("time.speed"), Capabilities.Why("time.speed"));
    }
}
