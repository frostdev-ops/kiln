// SPDX-License-Identifier: MIT
// Status lines (Enshwick's `status`/add_status): what a mod says about itself where an admin or player looks it up, such as
// "12 zones merged" or "3 of 5 altars placed". A toast is gone in seconds and a HUD line is always on the screen; a status
// line is kept and read when asked: the Kiln menu's Mods tab shows it under the mod, StatusLines.Summary() writes it as text
// (Kiln.Admin adds it to the mod list for the dashboard). The registry works on a dedicated server as well: nothing here draws.
// Usage: docs/kiln/modding/lifecycle.md.
using System.Text;

namespace Kiln;

/// <summary>A status line of a mod (see <see cref="StatusLines"/>). Dispose removes it.</summary>
public sealed class StatusLine : IDisposable
{
    internal StatusLine(string modId, ModContext? owner, string id, Func<string?>? source, string? text, long order)
    {
        ModId = modId;
        Owner = owner;
        Id = id;
        Source = source;
        Pushed = text;
        Order = order;
    }

    /// <summary>The mod that added it ("kiln" for Kiln's own).</summary>
    public string ModId { get; }

    /// <summary>The line's name within its mod: giving the same one again replaces the line.</summary>
    public string Id { get; }

    internal ModContext? Owner { get; }
    internal Func<string?>? Source { get; set; }
    internal volatile string? Pushed;
    internal long Order { get; }
    internal bool Failing;

    /// <summary>The lines this entry reports right now (a source runs now, on the calling thread).</summary>
    public IReadOnlyList<string> Text => StatusLines.Read(this);

    public void Dispose() => StatusLines.Remove(this);
}

/// <summary>One mod's status lines, as <see cref="StatusLines.All"/> reads them.</summary>
public sealed record ModStatus(string ModId, string Name, string Version, IReadOnlyList<string> Lines);

/// <summary>
/// Persistent status lines of mods. A mod either gives a <b>source</b> (<see cref="Add"/>: a function Kiln calls only when
/// someone looks, so keeping the numbers, in fields or atomics, is the only cost while the game runs) or <b>pushes</b> a text
/// (<see cref="Set"/>: kept until it changes).
/// </summary>
/// <remarks>
/// <para>A source runs as the mod that added it, never inside a game hook: on the thread that asks for <see cref="Lines"/>,
/// <see cref="All"/> or <see cref="Summary"/>, and on a worker thread for the Kiln menu and Kiln.Admin (about twice a second
/// while someone looks, one refresh at a time; the menu and the dashboard always show the last lines read and never wait for a
/// source, so a new or changed line shows a moment later). Keep it quick, touch no game state and no locks the tick thread
/// holds; read fields the tick thread wrote. A source that hangs freezes every mod's lines in the menu and the dashboard at
/// their last value until it returns. A source that throws shows no lines (logged once until it
/// works again). Each line of a multi-line text shows on its own; empty lines are dropped.</para>
/// <para>Lines go away with the mod that added them.</para>
/// </remarks>
public static class StatusLines
{
    private static readonly object Lock = new();
    private static StatusLine[] _lines = [];
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static long _sequence, _generation, _epoch;
    private static volatile Cached? _cache;
    private static volatile Task<IReadOnlyList<ModStatus>>? _refresh;

    private sealed record Cached(long At, long Generation, IReadOnlyList<ModStatus> Value);

    /// <summary>Adds a line whose text <paramref name="source"/> computes whenever someone looks. <paramref name="id"/> names
    /// it within the calling mod (a second call with the same id replaces the source); without one a line of its own is made.</summary>
    public static StatusLine Add(Func<string?> source, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Upsert(id, source, null);
    }

    /// <summary>Sets the text of line <paramref name="id"/> of the calling mod, creating it (it stays until changed). An empty
    /// or null text removes the line. Any thread.</summary>
    public static StatusLine? Set(string id, string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.IsNullOrEmpty(text))
        {
            Remove(id);
            return null;
        }
        return Upsert(id, null, text);
    }

    /// <summary>Removes the calling mod's line <paramref name="id"/>; false when it has none.</summary>
    public static bool Remove(string id)
    {
        var owner = ModContext.CurrentMod;
        var modId = owner?.Info.Id ?? "kiln";
        lock (Lock)
        {
            var line = _lines.FirstOrDefault(l => ReferenceEquals(l.Owner, owner) && l.ModId == modId && l.Id == id);
            if (line is null) return false;
            _lines = _lines.Where(l => !ReferenceEquals(l, line)).ToArray();
            Invalidate();
            return true;
        }
    }

    /// <summary>The mod's lines now (in the order it added them); empty when it has none.</summary>
    public static IReadOnlyList<string> Lines(string modId)
    {
        ArgumentNullException.ThrowIfNull(modId);
        StatusLine[] mine;
        lock (Lock) mine = _lines.Where(l => l.ModId == modId).OrderBy(l => l.Order).ToArray();
        return mine.SelectMany(Read).ToArray();
    }

    /// <summary>Every mod that has lines, in load order, with the lines as of now.</summary>
    public static IReadOnlyList<ModStatus> All()
    {
        StatusLine[] all;
        lock (Lock) all = _lines;
        if (all.Length == 0) return [];
        var info = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
        try
        {
            foreach (var m in Mods.All) info[m.Id] = m;
        }
        catch (Exception)
        {
            // the mod list is not there (a unit test, an early call): ids only
        }
        return all.GroupBy(l => l.ModId)
            .OrderBy(g => info.TryGetValue(g.Key, out var e) && e.LoadIndex >= 0 ? e.LoadIndex : int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                info.TryGetValue(g.Key, out var e);
                var lines = g.OrderBy(l => l.Order).SelectMany(Read).ToArray();
                return new ModStatus(g.Key, e?.Name ?? g.Key, e?.Version ?? "", lines);
            })
            .Where(s => s.Lines.Count > 0)
            .ToArray();
    }

    /// <summary>The status as text, one block per mod: <c>mod-id 1.2.0</c> and its lines indented. Empty when no mod has lines.</summary>
    public static string Summary()
    {
        var sb = new StringBuilder();
        foreach (var m in All())
        {
            sb.Append(m.ModId);
            if (m.Version.Length > 0) sb.Append(' ').Append(m.Version);
            sb.Append('\n');
            foreach (var line in m.Lines) sb.Append("  ").Append(line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Writes <see cref="Summary"/> into the log (one line per status line, tagged "status"). For a server, where
    /// nobody has a menu open.</summary>
    public static void WriteToLog(Logger? log = null)
    {
        foreach (var m in All())
            foreach (var line in m.Lines)
            {
                var text = $"status {m.ModId}: {line}";
                if (log is not null) log.Info(text);
                else Backend.Current.Log(0, LogLevel.Info, text);
            }
    }

    // ---------------------------------------------------------------------------------------------- internals ----

    private static StatusLine Upsert(string? id, Func<string?>? source, string? text)
    {
        var owner = ModContext.CurrentMod;
        if (owner is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        var modId = owner?.Info.Id ?? "kiln";
        lock (Lock)
        {
            if (owner is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
            Watch(owner);
            id ??= "line" + (_sequence + 1);
            if (id.Length > 64) throw new ArgumentException("a status line's id is at most 64 characters", nameof(id));
            var existing = _lines.FirstOrDefault(l => ReferenceEquals(l.Owner, owner) && l.ModId == modId && l.Id == id);
            if (existing is not null)
            {
                existing.Source = source;
                existing.Pushed = text;
                existing.Failing = false;
                Invalidate();
                return existing;
            }
            var line = new StatusLine(modId, owner, id, source, text, ++_sequence);
            _lines = [.. _lines, line];
            Invalidate();
            return line;
        }
    }

    internal static void Remove(StatusLine line)
    {
        lock (Lock)
        {
            _lines = _lines.Where(l => !ReferenceEquals(l, line)).ToArray();
            Invalidate();
        }
    }

    // Under Lock.
    private static void Watch(ModContext? mod)
    {
        if (mod is null || !Watched.Add(mod)) return;
        Action retire = () =>
        {
            lock (Lock)
            {
                Watched.Remove(mod);
                _lines = _lines.Where(l => !ReferenceEquals(l.Owner, mod)).ToArray();
                Invalidate();
            }
        };
        mod.Retiring += retire;
        if (!mod.Alive)
        {
            mod.Retiring -= retire;
            Watched.Remove(mod);
            throw new InvalidOperationException("the mod is unloading");
        }
    }

    internal static IReadOnlyList<string> Read(StatusLine line)
    {
        string? text;
        if (line.Source is { } source)
        {
            var owner = line.Owner;
            if (owner is { Alive: false }) return [];
            var previous = ModContext.CurrentMod;
            if (owner is not null) ModContext.CurrentMod = owner;
            try
            {
                text = source();
                line.Failing = false;
            }
            catch (Exception e)
            {
                if (!line.Failing)
                {
                    line.Failing = true;
                    var what = $"status line '{line.Id}' of {line.ModId} threw (it shows nothing until it works again)";
                    if (owner is not null) owner.Log.Error(e, what);
                    else Backend.Current.Log(0, LogLevel.Error, $"{what}: {e}");
                }
                return [];
            }
            finally
            {
                if (owner is not null) ModContext.CurrentMod = previous;
            }
        }
        else text = line.Pushed;
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    // Under Lock: a line was added, changed or removed, so the next snapshot reads again (soon, not at once: see Snapshot).
    private static void Invalidate() => _generation++;

    /// <summary>The shortest time between two snapshot refreshes that a change (a <see cref="Set"/>, an added or removed line)
    /// starts; without changes a snapshot is refreshed once it is older than the caller's maxAge.</summary>
    internal static readonly TimeSpan ChangeRefresh = TimeSpan.FromMilliseconds(100);

    /// <summary>For the Kiln menu (render thread, every frame) and Kiln.Admin (tick thread, once per mod in the mod list): the
    /// same as <see cref="All"/>, but never run on the calling thread and never waited for. It answers at once with the last
    /// snapshot (empty until the first one is read; after a pause, the last one read, however old, until the refresh it starts
    /// lands a moment later) and, when that one is older than <paramref name="maxAge"/> (or lines
    /// changed and it is older than <see cref="ChangeRefresh"/>), starts a refresh on a worker. At most one refresh runs at a
    /// time: a source that is slow or hangs leaves every caller with the previous snapshot until it returns, and a mod that
    /// calls <see cref="Set"/> every tick gets at most one refresh per <see cref="ChangeRefresh"/>.</summary>
    internal static IReadOnlyList<ModStatus> Snapshot(TimeSpan maxAge)
    {
        var c = _cache;
        if (Fresh(c, maxAge)) return c!.Value;
        var refresh = _refresh;
        if (refresh is null || refresh.IsCompleted)
        {
            lock (Lock)
            {
                // a refresh that finished between the reads above has just made the snapshot fresh: no second one right behind it
                c = _cache;
                if (Fresh(c, maxAge)) return c!.Value;
                if (_refresh is null || _refresh.IsCompleted)
                {
                    var generation = _generation;
                    var epoch = _epoch;
                    var at = Environment.TickCount64;
                    _refresh = Task.Run(() =>
                    {
                        IReadOnlyList<ModStatus> value;
                        try
                        {
                            value = All();
                        }
                        catch (Exception)
                        {
                            value = _cache?.Value ?? [];  // All() catches the sources' exceptions; anything else: the last snapshot
                        }
                        // kept even when lines changed meanwhile (a mod that sets a line every tick must not starve the cache);
                        // the next call sees the newer generation and refreshes again after ChangeRefresh
                        if (epoch == Volatile.Read(ref _epoch)) _cache = new Cached(at, generation, value);
                        return value;
                    });
                }
            }
        }
        return c?.Value ?? [];
    }

    // The snapshot answers the caller as it is: not older than maxAge, and (when lines changed since) not older than ChangeRefresh.
    private static bool Fresh(Cached? c, TimeSpan maxAge)
    {
        if (c is null) return false;
        var age = Environment.TickCount64 - c.At;
        return age < maxAge.TotalMilliseconds && (c.Generation == Volatile.Read(ref _generation) || age < ChangeRefresh.TotalMilliseconds);
    }

    /// <summary>Kiln.Admin's mod list (tick thread, once per mod): one mod's lines from <see cref="Snapshot"/> (refreshed once
    /// a second; never waits).</summary>
    internal static IReadOnlyList<string> RecentLines(string modId) =>
        Snapshot(TimeSpan.FromSeconds(1)).FirstOrDefault(m => m.ModId == modId)?.Lines ?? [];

    /// <summary>Test hook.</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            _lines = [];
            Watched.Clear();
            Invalidate();
            _epoch++;  // a refresh still running (a hung test source) does not write into the next test's cache
            _cache = null;
            _refresh = null;
        }
    }
}
