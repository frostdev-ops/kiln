// SPDX-License-Identifier: MIT
// Time.SetWeather / Time.Weather offline: names to layers, durations, records from native ones, the one error rule. What
// the game does is the lab test (mods/tests/chat-test, WeatherTest.cs); the native logic is native/services/time/time_test.cpp.
using Kiln;
using Kiln.TestKit;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Kiln.TimeTests;

public sealed unsafe class WeatherTests
{
    private static void Call(Func<Task> f) => _ = f();

    // The table of server 26430253 in short: clouds in layer 0, a rain per region, debug entries in no layer.
    private static readonly WeatherPhenomenon[] Table =
    [
        new(0, "clouds_few", Guid.NewGuid(), 0, WeatherEffect.None, 1, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)),
        new(1, "clouds_overcast", Guid.NewGuid(), 0, WeatherEffect.None, 1, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)),
        new(2, "rain", Guid.NewGuid(), 1, WeatherEffect.Rain, 0.3f, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(6)),
        new(3, "rain_snow", Guid.NewGuid(), 2, WeatherEffect.Rain, 1, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(6)),
        new(4, "debug_blizzard", Guid.NewGuid(), -1, WeatherEffect.Blizzard, 1, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
        new(5, "rain", Guid.NewGuid(), 3, WeatherEffect.Rain, 0.5f, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(10)),
    ];

    private static Time.NativeLayer Layer(byte index, byte first, byte count, int current, bool held = false, int heldPhen = -2,
        long next = 0, long now = 0) =>
        new() { Index = index, First = first, Count = count, Current = current, Held = (byte)(held ? 1 : 0), HeldPhenomenon = heldPhen,
                NextNs = next, NowNs = now, NothingProbability = index == 0 ? 0 : 1 };

    private static readonly Time.NativeLayer[] Layers = [Layer(0, 0, 2, 0), Layer(1, 2, 1, -1), Layer(2, 3, 1, -1), Layer(3, 5, 1, 5)];

    private static Time.NativeChange Change(ulong seq, byte layer, int before, int after, uint ib, uint ia, bool kiln = false, bool held = false) =>
        new() { Size = (uint)sizeof(Time.NativeChange), Seq = seq, Layer = layer, Before = before, After = after, InstanceBefore = ib,
                InstanceAfter = ia, ByKiln = (byte)(kiln ? 1 : 0), Held = (byte)(held ? 1 : 0) };

    [Fact]
    public void TheMirrorsKeepTheNativeLayouts()
    {
        Assert.Equal(40, sizeof(Time.NativeChange));
        var state = typeof(Time).GetNestedType("NativeState", System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(6, (int)System.Runtime.InteropServices.Marshal.OffsetOf(state, "WeatherChoiceReady"));
        Assert.Equal(8, (int)System.Runtime.InteropServices.Marshal.OffsetOf(state, "Hours"));
    }

    [Fact]
    public void EveryRecordedChangeIsOneEventInOrder()
    {
        // overcast, rain (Kiln's, held), overcast again within one weather update, then the game restarts overcast
        var changes = new[]
        {
            Change(7, 0, 1, 0, 10, 11, kiln: true, held: true),
            Change(8, 0, 0, 1, 11, 12, kiln: true),
            Change(9, 0, 1, 1, 12, 13),
            Change(10, 1, -1, 2, 0, 20),
            Change(11, 3, 5, -2, 30, 0, kiln: true),
        };
        var events = Time.EventsOf(changes, Table, Layers);
        Assert.Equal(5, events.Count);
        Assert.Equal(("clouds_overcast", "clouds_few", true, true), (events[0].Before!.Name, events[0].After!.Name, events[0].ByKiln, events[0].Held));
        Assert.Equal(("clouds_few", "clouds_overcast", true, false), (events[1].Before!.Name, events[1].After!.Name, events[1].ByKiln, events[1].Held));
        Assert.Equal(("clouds_overcast", "clouds_overcast", false), (events[2].Before!.Name, events[2].After!.Name, events[2].ByKiln));
        Assert.Equal((WeatherPhenomenon.NothingName, "rain", 1), (events[3].Before!.Name, events[3].After!.Name, events[3].Layer));
        Assert.Null(events[4].After);  // emptied (reset): nothing set until the game rolls
        Assert.Equal(3, events[4].Layer);
        // a layer the reader did not see: still an event, its "nothing" without a weight
        var lone = Time.EventsOf([Change(12, 9, -1, -1, 0, 0)], Table, Layers);
        Assert.Equal(9, Assert.Single(lone).Layer);
    }

    [Fact]
    public void AFullQueueIsARefusedResultAndNothingIsQueued()
    {
        var calls = 0;
        IReadOnlyList<WeatherLayer> Now()
        {
            calls++;
            return Time.BuildLayers(Table, Layers);
        }
        Assert.Null(Time.QueueRefusal(9, 9, Now));
        Assert.Equal(0, calls);
        var r = Time.QueueRefusal(3, 9, Now)!;
        IKilnResult k = r;
        Assert.False(k.Ok);
        Assert.Contains("253 weather requests are waiting", r.Reason);
        Assert.Contains("nothing of it was queued", r.Reason);
        Assert.Equal(4, r.Layers.Count);
    }

    [Fact]
    public void ARetiredAskersTaskIsDroppedAndTheRequestStillEnds()
    {
        IReadOnlyList<WeatherLayer> Now() => Time.BuildLayers(Table, Layers);
        var gone = new TaskCompletionSource<WeatherResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Time.TrackWeather(gone, null, [0, 1]);
        Time.RetireWeatherWhere(m => m is null);
        var stays = new TaskCompletionSource<WeatherResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = Time.TrackWeather(stays, null, [1, 3]);
        Time.CompleteWeather(a, true, null, Now);
        Time.CompleteWeather(a, true, null, Now);
        Assert.False(gone.Task.IsCompleted);   // both layers ran; the unloaded asker's code is never resumed
        Time.CompleteWeather(b, false, "layer 1: the server world closed", Now);
        Assert.False(stays.Task.IsCompleted);  // one layer still to come
        Time.CompleteWeather(b, true, null, Now);
        Assert.True(stays.Task.IsCompletedSuccessfully);
        Assert.False(stays.Task.Result.Ok);
        Assert.Equal("layer 1: the server world closed", stays.Task.Result.Reason);
        Assert.Equal([1, 3], stays.Task.Result.Layers.Select(l => l.Index));
        Time.CompleteWeather(b, true, null, Now);  // a key that ended: nothing
    }

    [Fact]
    public void ReadinessAnswersOnAnyThread()
    {
        // kiln.time's own flag, not the layers' tick-thread read: off the tick thread it answers, it does not throw
        using var kiln = new KilnHarness();
        var off = Task.Run(() => Time.WeatherChoiceAvailable).GetAwaiter().GetResult();
        Assert.False(off);  // no kiln.time here
        Assert.False(Time.WeatherChoiceReadyNow());
    }

    [Fact]
    public void ANameMeansEveryLayerThatHasIt()
    {
        Assert.Equal([(1, 2), (3, 5)], Time.Resolve(Table, Layers, "rain", null));
        Assert.Equal([(3, 5)], Time.Resolve(Table, Layers, "RAIN", 3));
        Assert.Equal([(0, 1)], Time.Resolve(Table, Layers, "clouds_overcast", null));
    }

    [Fact]
    public void NoneIsEveryLayersNothing()
    {
        Assert.Equal(4, Time.Resolve(Table, Layers, "none", null).Count);
        Assert.All(Time.Resolve(Table, Layers, "clear", null), p => Assert.Equal(-1, p.Index));
        Assert.Equal([(2, -1)], Time.Resolve(Table, Layers, "none", 2));
    }

    [Fact]
    public void UnknownNamesLayersAndDebugEntriesAreBadArguments()
    {
        var e = Assert.Throws<ArgumentException>(() => Time.Resolve(Table, Layers, "snowstorm", null));
        Assert.Contains("clouds_few", e.Message);
        Assert.Throws<ArgumentException>(() => Time.Resolve(Table, Layers, "debug_blizzard", null));
        Assert.Throws<ArgumentException>(() => Time.Resolve(Table, Layers, "rain", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Time.Resolve(Table, Layers, "rain", 9));
    }

    [Fact]
    public void DurationsAreBetweenASecondAndThirtyDays()
    {
        Time.CheckDuration(null);
        Time.CheckDuration(TimeSpan.FromMinutes(10));
        Time.CheckDuration(Time.MaxWeatherDuration);
        Assert.Throws<ArgumentOutOfRangeException>(() => Time.CheckDuration(TimeSpan.FromMilliseconds(500)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Time.CheckDuration(TimeSpan.FromDays(31)));
    }

    [Fact]
    public void ANativeRecordBecomesAPhenomenon()
    {
        var n = new Time.NativePhenomenon { Index = 7, Layer = 0xFF, Effect = 2, Probability = 1, DurationMinNs = 60_000_000_000, DurationMaxNs = 600_000_000_000 };
        var bytes = System.Text.Encoding.UTF8.GetBytes("debug_blizzard");
        for (var i = 0; i < bytes.Length; i++) n.Name[i] = bytes[i];
        var p = Time.ToPhenomenon(n);
        Assert.Equal("debug_blizzard", p.Name);
        Assert.Equal(-1, p.Layer);
        Assert.False(p.IsRolled);
        Assert.Equal(WeatherEffect.Blizzard, p.Effect);
        Assert.Equal(TimeSpan.FromMinutes(1), p.MinDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), p.MaxDuration);
    }

    [Fact]
    public void LayersShowWhatRunsAndWhatIsHeld()
    {
        var held = Layer(1, 2, 1, 2, held: true, heldPhen: 2, next: 90_000_000_000, now: 30_000_000_000);
        var layers = Time.BuildLayers(Table, [Layer(0, 0, 2, 1), held, Layer(2, 3, 1, -1), Layer(3, 5, 1, -2)]);
        Assert.Equal("clouds_overcast", layers[0].Current!.Name);
        Assert.Equal(3, layers[0].Choices.Count);  // two clouds and nothing
        Assert.True(layers[1].Held);
        Assert.Equal("rain", layers[1].HeldPhenomenon!.Name);
        Assert.Equal(TimeSpan.FromSeconds(60), layers[1].Remaining);
        Assert.True(layers[2].Current!.IsNothing);
        Assert.Null(layers[3].Current);  // after a reset, before the game's roll
    }

    [Fact]
    public void AResultFollowsTheErrorRule()
    {
        IKilnResult ok = new WeatherResult(true, null, []);
        Assert.True(ok.Ok);
        Assert.Null(ok.Reason);
        IKilnResult no = new WeatherResult(false, "layer 2: the server world closed", []);
        Assert.False(no.Ok);
        Assert.Contains("closed", no.Reason);
    }

    [Fact]
    public void BadArgumentsThrowBeforeTheCapability()
    {
        using var kiln = new KilnHarness();
        kiln.Run(() =>
        {
            Assert.Throws<ArgumentNullException>(() => Call(() => Time.SetWeather((string)null!)));
            Assert.Throws<ArgumentOutOfRangeException>(() => Call(() => Time.SetWeather("rain", TimeSpan.Zero)));
            Assert.Throws<ArgumentNullException>(() => Call(() => Time.SetWeather((WeatherPhenomenon)null!)));
            Assert.Throws<ArgumentException>(() => Call(() => Time.SetWeather(Table[4])));  // debug_blizzard: never rolled
        });
    }

    [Fact]
    public void WithoutKilnTimeTheCapabilityIsOff()
    {
        using var kiln = new KilnHarness();
        kiln.Run(() =>
        {
            var e = Assert.Throws<KilnUnavailableException>(() => Call(() => Time.SetWeather("rain")));
            Assert.Equal("time.weather_choose", e.Capability);
            Assert.Throws<KilnUnavailableException>(() => Call(() => Time.ReleaseWeather()));
            Assert.Throws<KilnUnavailableException>(() => _ = Time.Weather);
            Assert.False(Time.WeatherChoiceAvailable);
        });
    }

    [Fact]
    public void OffTheTickThreadThrows()
    {
        using var kiln = new KilnHarness();
        Assert.Throws<InvalidOperationException>(() => Call(() => Time.SetWeather("rain")));
        Assert.Throws<InvalidOperationException>(() => _ = Time.WeatherPhenomena);
    }
}
