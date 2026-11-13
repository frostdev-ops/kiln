// SPDX-License-Identifier: MIT
// Choosing the weather: kiln.time v2 (native/core/abi/services/kiln_time.h "weather"). The native side calls the game's own
// start of a weather phenomenon where the game rolls its weather layers; docs/re/time.md "Choosing the weather".
// Capability time.weather_choose.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>What a weather phenomenon does besides clouds (the game's <c>WeatherPhenomenonEffect</c>).</summary>
public enum WeatherEffect
{
    /// <summary>Clouds only.</summary>
    None = 0,
    /// <summary>Rain (snowy rain in the cold regions).</summary>
    Rain = 1,
    /// <summary>A blizzard.</summary>
    Blizzard = 2,
}

/// <summary>One phenomenon of the game's weather table (its <c>WeatherSystemResource</c>), or a layer's "nothing".</summary>
/// <param name="Index">Its index in the game's table; -1 for a layer's "nothing".</param>
/// <param name="Name">The game's name: <c>clouds_few</c>, <c>clouds_medium</c>, <c>clouds_overcast</c>, <c>clouds_fluffy</c>,
/// <c>rain</c>, <c>rain_snow</c> on 26430253; <c>none</c> for "nothing". Several layers have their own <c>rain</c>.</param>
/// <param name="Guid">Its GUID in the game's data (empty for "nothing").</param>
/// <param name="Layer">The layer whose roll picks it; -1 when the game never rolls it (its debug entries, not offered).</param>
/// <param name="Effect">Rain or blizzard besides the clouds.</param>
/// <param name="Probability">Its weight in its layer's roll.</param>
/// <param name="MinDuration">The shortest run the game rolls for it (game time).</param>
/// <param name="MaxDuration">The longest.</param>
public sealed record WeatherPhenomenon(int Index, string Name, Guid Guid, int Layer, WeatherEffect Effect, float Probability,
    TimeSpan MinDuration, TimeSpan MaxDuration)
{
    /// <summary>The name <see cref="Time.SetWeather(string, TimeSpan?, int?)"/> takes for a layer's "nothing".</summary>
    public const string NothingName = "none";

    /// <summary>A layer's "nothing": no phenomenon (clear sky there).</summary>
    public bool IsNothing => Index < 0;

    /// <summary>The game's roll picks it (it lies in a layer's range); only such phenomena can be chosen.</summary>
    public bool IsRolled => Layer >= 0;

    /// <summary>Its name, index and layer, for a log.</summary>
    public override string ToString() => IsNothing ? $"none (layer {Layer})" : $"{Name} (#{Index}, layer {Layer})";
}

/// <summary>One weather layer of the server: what runs there now and what can.</summary>
/// <param name="Index">The layer (0 is the clouds over the whole world on 26430253; the others are regions).</param>
/// <param name="Guid">Its GUID in the game's data.</param>
/// <param name="Current">What runs now (a layer's "nothing" when nothing does); null right after a reset, until the game
/// rolls.</param>
/// <param name="Held">Kiln holds a choice here (<see cref="Time.SetWeather(string, TimeSpan?, int?)"/> without a duration).</param>
/// <param name="HeldPhenomenon">The held choice.</param>
/// <param name="Remaining">Game time until the layer's next roll.</param>
/// <param name="Choices">What the layer's roll can pick: its phenomena and its "nothing".</param>
public sealed record WeatherLayer(int Index, Guid Guid, WeatherPhenomenon? Current, bool Held, WeatherPhenomenon? HeldPhenomenon,
    TimeSpan Remaining, IReadOnlyList<WeatherPhenomenon> Choices)
{
    /// <summary>The layer and what runs there, for a log.</summary>
    public override string ToString() =>
        $"layer {Index}: {(Current is null ? "none yet" : Current.IsNothing ? "nothing" : Current.Name)}, next roll in {Remaining.TotalSeconds:0} s" +
        (Held ? $", held {HeldPhenomenon}" : "");
}

/// <summary>What <see cref="Time.SetWeather(string, TimeSpan?, int?)"/> or <see cref="Time.ReleaseWeather"/> did:
/// <see cref="Ok"/> when the game ran it on every layer asked; otherwise <see cref="Reason"/> says why (the server world
/// closed, its weather did not update, the game's call faulted).</summary>
/// <param name="Ok">True when every layer's request ran.</param>
/// <param name="Reason">Why not; null when <paramref name="Ok"/>.</param>
/// <param name="Layers">The layers asked for, as they are when the result arrives.</param>
public sealed record WeatherResult(bool Ok, string? Reason, IReadOnlyList<WeatherLayer> Layers) : IKilnResult
{
    /// <summary>The outcome and the layers, for a log.</summary>
    public override string ToString() => (Ok ? "weather ok" : $"weather refused: {Reason}") + ": " + string.Join("; ", Layers);
}

/// <summary>What runs on a weather layer changed: the game rolled the next phenomenon, or Kiln started or released one.
/// One event per change, in the order they happened (kiln.time records each where it is made, so A then B then A on one
/// layer within one weather update is two events), including a restart of the same phenomenon (<see cref="Before"/> and
/// <see cref="After"/> equal: a new instance). From the first subscription on Kiln reads the changes once a step and
/// posts them on the tick thread while a handler subscribes; nothing is read before. kiln.time keeps the newest 512
/// changes: after more than 512 between two steps the oldest are not reported (kiln.log says so, once). Not cancelable (it
/// already happened). Capability <c>time.weather_choose</c>.</summary>
[ServerOnly]
public sealed class WeatherChangedEvent : KilnEvent
{
    static WeatherChangedEvent() => WeatherWatch.Start();

    internal WeatherChangedEvent(int layer, WeatherPhenomenon? before, WeatherPhenomenon? after, bool byKiln, bool held)
    {
        Layer = layer;
        Before = before;
        After = after;
        ByKiln = byKiln;
        Held = held;
    }

    /// <summary>The layer.</summary>
    public int Layer { get; }

    /// <summary>What ran before (null: nothing was set, after a reset).</summary>
    public WeatherPhenomenon? Before { get; }

    /// <summary>What runs now.</summary>
    public WeatherPhenomenon? After { get; }

    /// <summary>Kiln made this change: a mod's choice, a held roll, or the game's roll that a release with <c>now</c> or
    /// <see cref="Time.ResetWeather"/> asked for; false for the game's own roll.</summary>
    public bool ByKiln { get; }

    /// <summary>Kiln holds a choice on the layer now.</summary>
    public bool Held { get; }

    /// <summary>The change in one line, for a log.</summary>
    public override string ToString() =>
        $"weather layer {Layer}: {Before?.ToString() ?? "none"} -> {After?.ToString() ?? "none"}{(ByKiln ? " (Kiln)" : "")}{(Held ? ", held" : "")}";
}

public static unsafe partial class Time
{
    internal const string WeatherCapability = "time.weather_choose";
    private const uint WeatherHold = 0x01, WeatherRelease = 0x02, WeatherNow = 0x04;
    private const int BufferTooSmall = 5;

    /// <summary>The longest duration <see cref="SetWeather(string, TimeSpan?, int?)"/> takes (game time).</summary>
    public static readonly TimeSpan MaxWeatherDuration = TimeSpan.FromDays(30);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePhenomenon
    {
        public uint Size;
        public ushort Index;
        public byte Layer, Effect;
        public Guid Guid;
        public float Probability;
        public uint Flags;
        public long DurationMinNs, DurationMaxNs;
        public fixed byte Name[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeLayer
    {
        public uint Size;
        public byte Index, First, Count, Held, RestrictMask, ExcludeMask, R0, R1;
        public Guid Guid;
        public float NothingProbability;
        public int Current, HeldPhenomenon;
        public uint Instance, KilnChanges, R2;
        public long NextNs, NowNs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWeatherRequest
    {
        public uint Size, Flags, Layer;
        public int Phenomenon;
        public long DurationNs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWeatherResult
    {
        public uint Size;
        public int Status;
        public ulong Ticket;
        public uint Layer, Flags;
        public int Current;
        public uint Instance;
        public long NextNs, NowNs;
        public fixed byte Message[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeChange
    {
        public uint Size;
        public byte Layer, ByKiln, Held, R0;
        public ulong Seq;
        public int Before, After;
        public uint InstanceBefore, InstanceAfter;
        public long NowNs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TableV3
    {
        public TableV2 V2;
        public delegate* unmanaged[Cdecl]<NativePhenomenon*, uint, uint, uint*, int> WeatherTable;
        public delegate* unmanaged[Cdecl]<NativeLayer*, uint, uint, uint*, int> WeatherLayers;
        public delegate* unmanaged[Cdecl]<ulong, NativeWeatherRequest*, delegate* unmanaged[Cdecl]<NativeWeatherResult*, void*, void>, void*, ulong*, int> WeatherSet;
        public delegate* unmanaged[Cdecl]<uint> WeatherRoom;
        public delegate* unmanaged[Cdecl]<ulong> WeatherChangeSeq;
        public delegate* unmanaged[Cdecl]<ulong, NativeChange*, uint, uint, uint*, int> WeatherChanges;
    }

    private static TableV3* T3 => T != null && T->Header.Size >= (uint)sizeof(TableV3) ? (TableV3*)T : null;

    /// <summary>True when the weather can be chosen right now: capability <c>time.weather_choose</c>, and the server's
    /// weather ran at least once in this world. Any thread (kiln.time's own flag, not a read of the game's layers).</summary>
    public static bool WeatherChoiceAvailable => T3 != null && Capabilities.Is(WeatherCapability) && WeatherChoiceReadyNow();

    /// <summary>The game's weather table (the server's <c>WeatherSystemResource</c>): every phenomenon with its name, layer,
    /// effect, weight and durations, read from the game's data, not a list of Kiln's. Empty until the server's weather
    /// has run once in this world.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>time.weather_choose</c> is off (an older Kiln, a
    /// game build whose weather table is not known).</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(WeatherCapability)]
    public static IReadOnlyList<WeatherPhenomenon> WeatherPhenomena
    {
        get
        {
            Services.RequireTickThread("Time.WeatherPhenomena");
            RequireWeather();
            return ReadTable();
        }
    }

    /// <summary>Every weather layer: what runs there, until when, whether Kiln holds it, and what it can run. Empty until
    /// the server's weather has run once in this world.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>time.weather_choose</c> is off.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(WeatherCapability)]
    public static IReadOnlyList<WeatherLayer> Weather
    {
        get
        {
            Services.RequireTickThread("Time.Weather");
            RequireWeather();
            return BuildLayers(ReadTable(), ReadLayers());
        }
    }

    /// <summary>
    /// Starts a weather phenomenon by its name, on every layer that has one of that name (<c>"rain"</c>: every region's rain;
    /// <c>"none"</c>: nothing on every layer, a clear sky), or on <paramref name="layer"/> only. The game's own start of a
    /// phenomenon runs it, as its own weather states do: what ran there ends, the chosen one starts at a random place
    /// in the layer's region, and every player's game is told by the game itself.
    /// </summary>
    /// <param name="phenomenon">A name of <see cref="WeatherPhenomena"/> (case does not matter), or <c>"none"</c>.</param>
    /// <param name="duration">How long it runs before the game rolls the layer again (game time: the day/night clock); its
    /// fade-out follows while the next one fades in. Then the game's own weather goes on. Null holds it: every time the game rolls the layer again, Kiln starts it again, until <see cref="ReleaseWeather"/>.</param>
    /// <param name="layer">One layer only; null = every layer that has it.</param>
    /// <returns>Completes on the tick thread in the step after the server's next weather update (the same frame as the next
    /// sim step).</returns>
    /// <remarks>Persistence: none. After a restart the game runs weather it rolls anew (lab: a two-hour phenomenon left
    /// before a save was gone after the load), and a hold is Kiln's and ends with the process. Set it again once the world
    /// runs.</remarks>
    /// <exception cref="ArgumentException">No rolled phenomenon of that name (on that layer), a layer that is not there,
    /// or a duration not between one second and <see cref="MaxWeatherDuration"/>.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>time.weather_choose</c> is off, or the server's
    /// weather has not run yet in this world.</exception>
    /// <example><code>
    /// await Time.SetWeather("rain", TimeSpan.FromMinutes(10));   // every region's rain for ten minutes
    /// await Time.SetWeather("clouds_overcast", layer: 0);       // overcast held until released
    /// await Time.ReleaseWeather();                               // the game's weather again
    /// </code></example>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(WeatherCapability)]
    public static Task<WeatherResult> SetWeather(string phenomenon, TimeSpan? duration = null, int? layer = null)
    {
        Services.RequireTickThread("Time.SetWeather");
        ArgumentNullException.ThrowIfNull(phenomenon);
        CheckDuration(duration);
        RequireWeather();
        var table = ReadTable();
        var layers = ReadLayers();
        if (layers.Length == 0) throw new KilnUnavailableException(WeatherCapability, "the server's weather has not run yet in this world");
        var picks = Resolve(table, layers, phenomenon, layer);
        return Submit(picks.Select(p => (p.Layer, p.Index, duration)).ToArray(), WeatherHold, "Time.SetWeather");
    }

    /// <summary>Starts one phenomenon of <see cref="WeatherPhenomena"/> (or a layer's "nothing" from
    /// <see cref="WeatherLayer.Choices"/>) on its layer. See <see cref="SetWeather(string, TimeSpan?, int?)"/>.</summary>
    /// <exception cref="ArgumentException">A phenomenon the game never rolls (<see cref="WeatherPhenomenon.IsRolled"/>
    /// false), or a duration out of range.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(WeatherCapability)]
    public static Task<WeatherResult> SetWeather(WeatherPhenomenon phenomenon, TimeSpan? duration = null)
    {
        Services.RequireTickThread("Time.SetWeather");
        ArgumentNullException.ThrowIfNull(phenomenon);
        if (!phenomenon.IsRolled) throw new ArgumentException($"{phenomenon.Name} is in no layer's roll: the game never runs it", nameof(phenomenon));
        CheckDuration(duration);
        RequireWeather();
        if (ReadLayers().Length == 0) throw new KilnUnavailableException(WeatherCapability, "the server's weather has not run yet in this world");
        return Submit([(phenomenon.Layer, phenomenon.Index, duration)], WeatherHold, "Time.SetWeather");
    }

    /// <summary>
    /// Hands the weather back to the game: Kiln's holds end on <paramref name="layer"/> (null: every layer). With
    /// <paramref name="now"/> the layer's phenomenon ends and the game rolls a new one in the same update; without it the
    /// running one finishes its own time first and then the game rolls as usual.
    /// </summary>
    /// <exception cref="ArgumentException">A layer that is not there.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>time.weather_choose</c> is off, or the server's
    /// weather has not run yet in this world.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(WeatherCapability)]
    public static Task<WeatherResult> ReleaseWeather(int? layer = null, bool now = false)
    {
        Services.RequireTickThread("Time.ReleaseWeather");
        RequireWeather();
        var layers = ReadLayers();
        if (layers.Length == 0) throw new KilnUnavailableException(WeatherCapability, "the server's weather has not run yet in this world");
        if (layer is { } l && (l < 0 || l >= layers.Length)) throw new ArgumentOutOfRangeException(nameof(layer), $"0..{layers.Length - 1}");
        var which = layer is { } one ? [one] : Enumerable.Range(0, layers.Length).ToArray();
        return Submit(which.Select(i => (i, -1, (TimeSpan?)null)).ToArray(), WeatherRelease | (now ? WeatherNow : 0), "Time.ReleaseWeather");
    }

    // ---- the pure parts (unit-tested) ----

    internal static void CheckDuration(TimeSpan? duration)
    {
        if (duration is { } d && (d < TimeSpan.FromSeconds(1) || d > MaxWeatherDuration))
            throw new ArgumentOutOfRangeException(nameof(duration), $"between 1 s and {MaxWeatherDuration.TotalDays} days of game time, or null to hold");
    }

    /// <summary>Which (layer, table index) a name means; -1 = the layer's nothing.</summary>
    internal static IReadOnlyList<(int Layer, int Index)> Resolve(IReadOnlyList<WeatherPhenomenon> table, NativeLayer[] layers,
        string name, int? layer)
    {
        if (layer is { } l && (l < 0 || l >= layers.Length)) throw new ArgumentOutOfRangeException(nameof(layer), $"0..{layers.Length - 1}");
        var picks = new List<(int, int)>();
        var nothing = string.Equals(name, WeatherPhenomenon.NothingName, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(name, "clear", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < layers.Length; i++)
        {
            if (layer is { } only && only != i) continue;
            if (nothing)
            {
                picks.Add((i, -1));
                continue;
            }
            var p = table.FirstOrDefault(x => x.Layer == i && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p != null) picks.Add((i, p.Index));
        }
        if (picks.Count == 0)
        {
            var names = string.Join(", ", table.Where(x => x.IsRolled).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            throw new ArgumentException($"no weather '{name}'{(layer is { } lay ? $" on layer {lay}" : "")}; the game rolls: {names}, none", nameof(name));
        }
        return picks;
    }

    internal static WeatherPhenomenon ToPhenomenon(in NativePhenomenon n)
    {
        string name;
        fixed (byte* p = n.Name)
        {
            var len = 0;
            while (len < 64 && p[len] != 0) len++;
            name = System.Text.Encoding.UTF8.GetString(p, len);
        }
        return new WeatherPhenomenon(n.Index, name, n.Guid, n.Layer == 0xFF ? -1 : n.Layer,
            Enum.IsDefined(typeof(WeatherEffect), (int)n.Effect) ? (WeatherEffect)n.Effect : WeatherEffect.None, n.Probability,
            TimeSpan.FromTicks(n.DurationMinNs / 100), TimeSpan.FromTicks(n.DurationMaxNs / 100));
    }

    internal static WeatherPhenomenon? PhenomenonOf(IReadOnlyList<WeatherPhenomenon> table, in NativeLayer l, int index) =>
        index == -1 ? new WeatherPhenomenon(-1, WeatherPhenomenon.NothingName, Guid.Empty, l.Index, WeatherEffect.None,
                          l.NothingProbability, TimeSpan.Zero, TimeSpan.Zero)
        : index >= 0 && index < table.Count ? table[index]
        : null;

    internal static IReadOnlyList<WeatherLayer> BuildLayers(IReadOnlyList<WeatherPhenomenon> table, NativeLayer[] layers)
    {
        var list = new List<WeatherLayer>(layers.Length);
        foreach (var l in layers)
        {
            var choices = table.Where(p => p.Layer == l.Index).ToList();
            choices.Add(PhenomenonOf(table, l, -1)!);
            var remaining = l.NextNs > l.NowNs && l.NextNs != long.MaxValue ? TimeSpan.FromTicks((l.NextNs - l.NowNs) / 100) : TimeSpan.Zero;
            list.Add(new WeatherLayer(l.Index, l.Guid, PhenomenonOf(table, l, l.Current), l.Held != 0,
                l.Held != 0 ? PhenomenonOf(table, l, l.HeldPhenomenon) : null, remaining, choices));
        }
        return list;
    }

    // ---- native ----

    private static void RequireWeather()
    {
        if (T3 == null)
            throw new KilnUnavailableException(WeatherCapability, T == null ? "kiln.time is not loaded" : $"kiln.time v{T->Header.Version} has no weather choice (update Kiln)");
        if (!Capabilities.Is(WeatherCapability))
            throw new KilnUnavailableException(WeatherCapability, Capabilities.Get(WeatherCapability).ReasonText);
    }

    internal static WeatherPhenomenon[] ReadTable()
    {
        var t = T3;
        if (t == null) return [];
        var buf = new NativePhenomenon[64];
        while (true)
        {
            uint n = 0;
            int rc;
            fixed (NativePhenomenon* p = buf) rc = t->WeatherTable(p, (uint)buf.Length, (uint)sizeof(NativePhenomenon), &n);
            if (rc == BufferTooSmall && n > buf.Length && n < 4096)
            {
                buf = new NativePhenomenon[n];
                continue;
            }
            if (rc != Ok) return [];
            return buf.Take((int)n).Select(x => ToPhenomenon(x)).ToArray();
        }
    }

    internal static NativeLayer[] ReadLayers()
    {
        var t = T3;
        if (t == null) return [];
        var buf = new NativeLayer[64];
        uint n = 0;
        int rc;
        fixed (NativeLayer* p = buf) rc = t->WeatherLayers(p, (uint)buf.Length, (uint)sizeof(NativeLayer), &n);
        return rc == Ok ? buf.Take((int)n).ToArray() : [];
    }

    // A request in flight, held by Kiln.Time itself (owner 0): it runs and its layers change whether or not the mod that
    // asked is still loaded. A retired asker's task is dropped, never completed: its continuations are the unloaded
    // mod's code. WeatherChangedEvent does not depend on it (WeatherWatch reads kiln.time's ordered changes).
    /// <summary>The newest change kiln.time recorded (0: none, or no kiln.time). Any thread.</summary>
    internal static ulong WeatherChangeSeq() => T3 is var t && t != null ? t->WeatherChangeSeq() : 0;

    /// <summary>The changes after <paramref name="after"/>, oldest first. Any thread.</summary>
    internal static NativeChange[] ReadWeatherChanges(ulong after)
    {
        var t = T3;
        if (t == null) return [];
        var buf = new NativeChange[32];
        while (true)
        {
            uint n = 0;
            int rc;
            fixed (NativeChange* p = buf) rc = t->WeatherChanges(after, p, (uint)buf.Length, (uint)sizeof(NativeChange), &n);
            if (rc == BufferTooSmall && n > buf.Length && n <= 4096)
            {
                buf = new NativeChange[n];
                continue;
            }
            return rc == Ok ? buf.Take((int)n).ToArray() : [];
        }
    }

    /// <summary>One event per recorded change, in order (pure: the unit tests build the records by hand).</summary>
    internal static IReadOnlyList<WeatherChangedEvent> EventsOf(IReadOnlyList<NativeChange> changes, IReadOnlyList<WeatherPhenomenon> table,
        NativeLayer[] layers)
    {
        var list = new List<WeatherChangedEvent>(changes.Count);
        foreach (var c in changes)
        {
            var at = Array.FindIndex(layers, x => x.Index == c.Layer);
            var l = at >= 0 ? layers[at] : new NativeLayer { Index = c.Layer };
            list.Add(new WeatherChangedEvent(c.Layer, PhenomenonOf(table, l, c.Before), PhenomenonOf(table, l, c.After), c.ByKiln != 0, c.Held != 0));
        }
        return list;
    }

    private sealed class PendingWeather(TaskCompletionSource<WeatherResult> tcs, ModContext? mod, int[] layers)
    {
        public readonly TaskCompletionSource<WeatherResult> Tcs = tcs;
        public readonly ModContext? Mod = mod;
        public readonly int[] Layers = layers;
        public int Waiting = layers.Length;
        public string? Reason;
        public bool Retired;
    }

    private static readonly object WeatherGate = new();
    private static readonly Dictionary<long, PendingWeather> WeatherInFlight = new();
    private static readonly HashSet<ModContext> WeatherWatched = new();
    private static long _nextWeather;

    internal const int WeatherQueueSize = 256;

    /// <summary>A request that does not fit the queue now, as a result (the capability is there): null when it fits.
    /// Nothing of it is queued, so no layer changes.</summary>
    internal static WeatherResult? QueueRefusal(uint room, int needed, Func<IReadOnlyList<WeatherLayer>> layersNow) =>
        room >= needed ? null
        : new WeatherResult(false, $"{WeatherQueueSize - room} weather requests are waiting and this one needs room for {needed}: try again in a step " +
                                   "(nothing of it was queued)", layersNow());

    internal static long TrackWeather(TaskCompletionSource<WeatherResult> tcs, ModContext? mod, int[] layers)
    {
        lock (WeatherGate)
        {
            var key = ++_nextWeather;
            WeatherInFlight[key] = new PendingWeather(tcs, mod, layers);
            if (mod != null && WeatherWatched.Add(mod)) mod.Retiring += () => RetireWeather(mod);
            return key;
        }
    }

    private static void RetireWeather(ModContext mod)
    {
        lock (WeatherGate) WeatherWatched.Remove(mod);
        RetireWeatherWhere(m => m == mod);
    }

    internal static void RetireWeatherWhere(Func<ModContext?, bool> which)
    {
        lock (WeatherGate)
            foreach (var p in WeatherInFlight.Values)
                if (which(p.Mod)) p.Retired = true;
    }

    /// <summary>One layer's result (tick thread): the last one completes the asking mod's task, unless it retired.</summary>
    internal static void CompleteWeather(long key, bool ok, string? reason, Func<IReadOnlyList<WeatherLayer>> layersNow)
    {
        PendingWeather? p;
        lock (WeatherGate)
        {
            if (!WeatherInFlight.TryGetValue(key, out p)) return;
            if (!ok && p.Reason == null) p.Reason = reason;
            if (--p.Waiting > 0) return;
            WeatherInFlight.Remove(key);
        }
        if (p.Retired) return;
        var mine = layersNow().Where(l => p.Layers.Contains(l.Index)).ToList();
        p.Tcs.TrySetResult(new WeatherResult(p.Reason == null, p.Reason, mine));
    }

    private static IReadOnlyList<WeatherLayer> LayersNow() => BuildLayers(ReadTable(), ReadLayers());

    private static Task<WeatherResult> Submit((int Layer, int Index, TimeSpan? Duration)[] picks, uint flags, string api)
    {
        var t = T3!;
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        // room for the whole request first: only the tick thread adds to kiln.time's queue, so what fits now still fits
        if (QueueRefusal(t->WeatherRoom(), picks.Length, LayersNow) is { } full) return Task.FromResult(full);
        var tcs = new TaskCompletionSource<WeatherResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = TrackWeather(tcs, mod, picks.Select(p => p.Layer).ToArray());
        var pending = WeatherInFlight[key];
        var submitted = 0;
        foreach (var (layer, index, duration) in picks)
        {
            var hold = (flags & WeatherHold) != 0 && duration is null;
            var r = new NativeWeatherRequest
            {
                Size = (uint)sizeof(NativeWeatherRequest),
                Flags = (flags & ~WeatherHold) | (hold ? WeatherHold : 0),
                Layer = (uint)layer,
                Phenomenon = index,
                DurationNs = duration is { } d ? d.Ticks * 100 : 0,
            };
            ulong ticket = 0;
            // owner 0: Kiln's own, so releasing the asking mod's owner drops nothing that is already queued
            var rc = t->WeatherSet(0, &r, &OnWeatherDone, (void*)key, &ticket);
            if (rc == Ok)
            {
                submitted++;
                continue;
            }
            lock (WeatherGate)
            {
                pending.Waiting -= picks.Length - submitted;
                if (submitted == 0) WeatherInFlight.Remove(key);
                else if (rc == BufferTooSmall) pending.Reason ??= $"layers {string.Join(", ", picks.Skip(submitted).Select(x => x.Layer))} were not queued (the queue filled); " +
                                                                  $"layers {string.Join(", ", picks.Take(submitted).Select(x => x.Layer))} ran";
            }
            if (rc == BufferTooSmall)
            {
                // cannot happen after the room check (only this thread adds); if it does, say exactly what ran
                if (submitted == 0) return Task.FromResult(QueueRefusal(0, picks.Length, LayersNow)!);
                return tcs.Task;
            }
            if (rc == InvalidArgument) throw new ArgumentException($"{api}: kiln.time refused layer {layer}, phenomenon {index}" +
                                                                  (submitted > 0 ? $" (layers {string.Join(", ", picks.Take(submitted).Select(x => x.Layer))} were queued before it)" : ""));
            throw new KilnUnavailableException(WeatherCapability, $"kiln.time refused it (error {rc}): the server's weather is not available");
        }
        return tcs.Task;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnWeatherDone(NativeWeatherResult* r, void* user)
    {
        try
        {
            string? reason = null;
            if (r->Status != 0)
            {
                var len = 0;
                while (len < 128 && r->Message[len] != 0) len++;
                reason = $"layer {r->Layer}: {System.Text.Encoding.UTF8.GetString(r->Message, len)}";
            }
            CompleteWeather((long)user, r->Status == 0, reason, LayersNow);
        }
        catch
        {
            // never let an exception cross into native code
        }
    }
}

// kiln.time's ordered weather changes, drained once a step from the first subscription on (nothing before): each is one
// WeatherChangedEvent, in the order the changes happened, A then B then A on one layer in one update included, and a
// restart of the same phenomenon (a new instance). The newest seq at the subscription is the baseline, so a change
// made in the same step as the subscription is reported.
internal static class WeatherWatch
{
    private static readonly object Lock = new();
    private static IDisposable? _tick;
    private static ulong _seq;

    public static void Start()
    {
        lock (Lock)
        {
            if (_tick != null) return;
            _seq = Time.WeatherChangeSeq();
            _tick = ServiceTick.Add("kiln.time weather", Poll);
        }
    }

    private static bool _toldGap;

    private static void Poll(in TickInfo tick)
    {
        var changes = Time.ReadWeatherChanges(_seq);
        if (changes.Length == 0) return;
        var lost = LostBefore(_seq, changes);
        _seq = changes[^1].Seq;
        if (lost > 0 && !_toldGap)
        {
            _toldGap = true;
            Backend.Current.Log(0, LogLevel.Warn, $"time: {lost} weather change(s) were not reported as WeatherChangedEvent: more than " +
                                                  "512 happened between two reads, and kiln.time keeps the newest 512 (said once)");
        }
        if (!Events.HasSubscribers<WeatherChangedEvent>()) return;
        foreach (var e in Time.EventsOf(changes, Time.ReadTable(), Time.ReadLayers())) Events.Post(e);
    }

    /// <summary>How many changes kiln.time no longer had when they were read after <paramref name="after"/> (its first
    /// record should be the next one): 0 when none were lost.</summary>
    internal static ulong LostBefore(ulong after, IReadOnlyList<Time.NativeChange> read) =>
        read.Count == 0 || read[0].Seq <= after + 1 ? 0 : read[0].Seq - after - 1;
}
