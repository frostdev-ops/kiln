// SPDX-License-Identifier: MIT
// Kiln.Settings on the event bus: SettingChangedEvent (docs/kiln/events.md).
namespace Kiln;

/// <summary>A game setting changed. Posted on the tick thread: right after <see cref="Settings.Set"/>,
/// <see cref="Settings.Reset"/> or <see cref="Settings.ResetAll"/> changed it (<see cref="By"/> = the mod), or, for
/// changes made outside Kiln (the in-game admin menu), within a second (Kiln compares the values when the settings'
/// revision moves, only while someone listens; <see cref="By"/> = "game"). Not cancelable.</summary>
public sealed class SettingChangedEvent : KilnEvent
{
    static SettingChangedEvent() => SettingsEvents.Start();

    internal SettingChangedEvent(string name, object before, object after, string by)
    {
        Name = name;
        Before = before;
        After = after;
        By = by;
    }

    /// <summary>The setting (enshrouded_server.json key).</summary>
    public string Name { get; }

    /// <summary>Values as <see cref="Settings.Get"/> returns them (bool, double, long, string).</summary>
    public object Before { get; }

    public object After { get; }

    /// <summary>Id of the mod that changed it, "kiln" for Kiln itself, "game" for a change outside Kiln.</summary>
    public string By { get; }

    public bool FromGame => By == "game";

    public override string ToString() => $"{Name}: {Before} -> {After} ({By})";
}

internal static class SettingsEvents
{
    private const int PollEverySteps = 25;
    private static readonly object Lock = new();
    private static IDisposable? _tick;
    private static long _revision = -1;
    private static Dictionary<string, object>? _snapshot;

    public static void Start()
    {
        lock (Lock) _tick ??= ServiceTick.Add("kiln.settings events", Poll);
    }

    /// <summary>Kiln changed a setting: post it, and keep the poller from reporting it again as a game change.</summary>
    internal static void AfterKilnChange(string name, object before, object after)
    {
        if (_snapshot is not null)
        {
            _snapshot[name] = after;
            _revision = Settings.Revision;
        }
        if (!Events.HasSubscribers<SettingChangedEvent>()) return;
        Events.Post(new SettingChangedEvent(name, before, after, ModContext.CurrentMod?.Info.Id ?? "kiln"));
    }

    private static void Poll(in TickInfo tick)
    {
        if (tick.Step % PollEverySteps != 0) return;
        if (!Events.HasSubscribers<SettingChangedEvent>())
        {
            _snapshot = null;
            _revision = -1;
            return;
        }
        if (!Settings.Available || !Settings.Ready) return;
        var rev = Settings.Revision;
        if (_snapshot is not null && rev == _revision) return;
        var now = Settings.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var old = _snapshot;
        _snapshot = now;
        _revision = rev;
        if (old is null) return;  // first look: nothing to compare with
        foreach (var (k, v) in now)
            if (old.TryGetValue(k, out var was) && !Equals(was, v))
                Events.Post(new SettingChangedEvent(k, was, v, "game"));
    }
}
