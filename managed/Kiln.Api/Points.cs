// SPDX-License-Identifier: MIT
// Extension points: a rule one mod lets other mods observe, advise or override. The model follows Enshwick's
// extension points (enshwick crate `points`, MIT): the owner names a point "<owner id>:<name>" with a mode, answers
// first, and each linked mod's link follows in a stable order; a link that throws is switched off alone.
using System.Collections.Concurrent;

namespace Kiln;

/// <summary>How links act on a point's answer, as the owning mod chooses.</summary>
public enum PointMode
{
    /// <summary>Links see the answer and change nothing.</summary>
    Observe,
    /// <summary>Links may change the answer; the owner may ignore the change.</summary>
    Advise,
    /// <summary>Links may change the answer, and the owner takes it.</summary>
    Override,
}

/// <summary>What a link does with the answer it is shown.</summary>
public enum PointVerdict
{
    /// <summary>The answer stays as it was.</summary>
    Pass,
    /// <summary>The answer becomes the link's value (points of mode Advise or Override).</summary>
    Set,
    /// <summary>Stop the chain: the owner gets <see cref="PointOutcome.Refused"/> (points that allow refusing).</summary>
    Refuse,
}

/// <summary>What asking a point came to.</summary>
public enum PointOutcome
{
    /// <summary>No link changed the owner's answer.</summary>
    Kept,
    /// <summary>Links changed the answer.</summary>
    Changed,
    /// <summary>A link refused.</summary>
    Refused,
}

/// <summary>The answer of <see cref="Point{TIn,TOut}.Decide"/>.</summary>
public readonly record struct PointDecision<TOut>(PointOutcome Outcome, TOut Value, string? DecidedBy)
{
    /// <summary>The answer, or <paramref name="refused"/> when a link refused.</summary>
    public TOut ValueOr(TOut refused) => Outcome == PointOutcome.Refused ? refused : Value;

    public bool IsRefused => Outcome == PointOutcome.Refused;
}

/// <summary>Where a link runs among the other links of a point: lower <see cref="Priority"/> first, then before or
/// after the links of the named mods. Ties go by mod id, so every start orders links the same way.</summary>
public sealed record PointOrder
{
    public int Priority { get; init; }

    /// <summary>Mods whose links run after this one.</summary>
    public IReadOnlyList<string> Before { get; init; } = [];

    /// <summary>Mods whose links run before this one.</summary>
    public IReadOnlyList<string> After { get; init; } = [];

    public static PointOrder Default { get; } = new();
}

/// <summary>One link on a point, for diagnostics and for switching it off.</summary>
public sealed class PointLink : IDisposable
{
    internal PointLink(PointRegistry registry, string point, string owner, bool observer, PointOrder order, Delegate run,
        Type input, Type output, long serial)
    {
        Registry = registry;
        Point = point;
        Owner = owner;
        Observer = observer;
        Order = order;
        Run = run;
        Input = input;
        Output = output;
        Serial = serial;
    }

    internal PointRegistry Registry { get; }
    internal Delegate Run { get; }
    internal Type Input { get; }
    internal Type Output { get; }
    internal long Serial { get; }

    /// <summary>The point's full name.</summary>
    public string Point { get; }

    /// <summary>The mod that attached the link.</summary>
    public string Owner { get; }

    /// <summary>True for a link attached with Observe: it never changes the answer.</summary>
    public bool Observer { get; }

    public PointOrder Order { get; }

    /// <summary>False when the link was switched off, threw, or does not match the point's types.</summary>
    public bool Enabled => OffReason == null;

    /// <summary>Why the link does not run, or null.</summary>
    public string? OffReason { get; internal set; }

    /// <summary>Detaches the link.</summary>
    public void Dispose() => Registry.Detach(this);

    public override string ToString() => $"{Owner} -> {Point}{(Observer ? " (observe)" : "")}{(OffReason is { } r ? $" [off: {r}]" : "")}";
}

/// <summary>A point a mod defined: it asks it with <see cref="Decide"/> whenever its rule applies.</summary>
public sealed class Point<TIn, TOut>
{
    internal Point(PointRegistry registry, string name, string owner, PointMode mode, bool allowRefuse)
    {
        Registry = registry;
        Name = name;
        Owner = owner;
        Mode = mode;
        AllowRefuse = allowRefuse;
    }

    internal PointRegistry Registry { get; }

    /// <summary>"&lt;owner id&gt;:&lt;name&gt;".</summary>
    public string Name { get; }

    public string Owner { get; }
    public PointMode Mode { get; }
    public bool AllowRefuse { get; }

    /// <summary>The links that run, in order.</summary>
    public IReadOnlyList<PointLink> Links => Registry.Chain(Name);

    /// <summary>Asks the point: <paramref name="answer"/> is the owner's own answer; each link follows in order and sees
    /// the answer as the links before it left it. With no link this costs one dictionary read.</summary>
    public PointDecision<TOut> Decide(TIn input, TOut answer)
    {
        var chain = Registry.Chain(Name);
        if (chain.Length == 0) return new(PointOutcome.Kept, answer, null);
        var value = answer;
        var changed = false;
        string? by = null;
        foreach (var link in chain)
        {
            if (link.OffReason != null) continue;
            try
            {
                if (link.Observer)
                {
                    ((Action<TIn, TOut>)link.Run)(input, value);
                    continue;
                }
                var (verdict, proposed) = ((Func<TIn, TOut, (PointVerdict, TOut)>)link.Run)(input, value);
                switch (verdict)
                {
                    case PointVerdict.Set when Mode != PointMode.Observe:
                        value = proposed;
                        changed = true;
                        by = link.Owner;
                        break;
                    case PointVerdict.Refuse when AllowRefuse:
                        return new(PointOutcome.Refused, value, link.Owner);
                    case PointVerdict.Refuse:
                        Registry.Note($"points: {link.Owner} refused {Name}, which does not allow refusing; ignored");
                        break;
                    case PointVerdict.Set:
                        Registry.Note($"points: {link.Owner} set an answer on {Name}, an observe-only point; ignored");
                        break;
                }
            }
            catch (Exception e)
            {
                Registry.TurnOff(link, $"threw {e.GetType().Name}: {e.Message}");
            }
        }
        return new(changed ? PointOutcome.Changed : PointOutcome.Kept, value, by);
    }
}

/// <summary>The points and links of one set of mods. <see cref="Points"/> is the shared registry every mod uses; a
/// registry of one's own is for tests and for tools.</summary>
public sealed class PointRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Owner, PointMode Mode, bool AllowRefuse, Type In, Type Out, object Point)> _points = new(StringComparer.Ordinal);
    private readonly List<PointLink> _links = [];
    private readonly ConcurrentDictionary<string, PointLink[]> _chains = new(StringComparer.Ordinal);
    private readonly HashSet<(string Point, string Owner)> _switchedOff = [];
    private long _serial;

    /// <summary>Where notes go (a link turned off, a cycle in the order). Default: dropped.</summary>
    public Action<string>? Log { get; set; }

    internal void Note(string line) => Log?.Invoke(line);

    /// <summary>Defines the point <c>&lt;owner&gt;:&lt;name&gt;</c>. A second definition of one name throws.</summary>
    public Point<TIn, TOut> Define<TIn, TOut>(string owner, string name, PointMode mode, bool allowRefuse = false)
    {
        Check(owner, nameof(owner));
        Check(name, nameof(name));
        if (name.Contains(':')) throw new ArgumentException("give the point's own name; the owner's id is added", nameof(name));
        var full = owner + ":" + name;
        lock (_lock)
        {
            if (_points.ContainsKey(full)) throw new InvalidOperationException($"point {full} is already defined");
            var p = new Point<TIn, TOut>(this, full, owner, mode, allowRefuse);
            _points[full] = (owner, mode, allowRefuse, typeof(TIn), typeof(TOut), p);
            Rebuild(full);
            return p;
        }
    }

    /// <summary>Attaches a link that may change the answer of <paramref name="point"/> (a full name). The point may be
    /// defined later; a link whose types differ from the point's stays attached but off, with the reason.</summary>
    public PointLink Extend<TIn, TOut>(string owner, string point, Func<TIn, TOut, (PointVerdict, TOut)> link, PointOrder? order = null)
        => Attach(owner, point, false, link, typeof(TIn), typeof(TOut), order);

    /// <summary>Attaches a link that sees each answer and never changes it.</summary>
    public PointLink Observe<TIn, TOut>(string owner, string point, Action<TIn, TOut> observer, PointOrder? order = null)
        => Attach(owner, point, true, observer, typeof(TIn), typeof(TOut), order);

    /// <summary>Switches the links of <paramref name="owner"/> on <paramref name="point"/> off or back on (a player's
    /// choice; Enshwick keeps it in mods.toml).</summary>
    public void SetEnabled(string point, string owner, bool enabled)
    {
        lock (_lock)
        {
            if (enabled) _switchedOff.Remove((point, owner));
            else _switchedOff.Add((point, owner));
            Rebuild(point);
        }
    }

    /// <summary>Removes every point and link of <paramref name="owner"/> (a mod that unloads).</summary>
    public void RemoveOwner(string owner)
    {
        lock (_lock)
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in _links.Where(l => l.Owner == owner).ToList())
            {
                _links.Remove(l);
                touched.Add(l.Point);
            }
            foreach (var name in _points.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToList())
            {
                _points.Remove(name);
                touched.Add(name);
            }
            foreach (var t in touched) Rebuild(t);
        }
    }

    /// <summary>Every defined point's name, owner and mode.</summary>
    public IReadOnlyList<(string Name, string Owner, PointMode Mode)> Defined
    {
        get
        {
            lock (_lock) return _points.Select(kv => (kv.Key, kv.Value.Owner, kv.Value.Mode)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Every link, including those that are off.</summary>
    public IReadOnlyList<PointLink> AllLinks
    {
        get
        {
            lock (_lock) return _links.ToList();
        }
    }

    internal PointLink[] Chain(string point) => _chains.TryGetValue(point, out var c) ? c : [];

    internal void Detach(PointLink link)
    {
        lock (_lock)
        {
            if (_links.Remove(link)) Rebuild(link.Point);
        }
    }

    internal void TurnOff(PointLink link, string reason)
    {
        lock (_lock)
        {
            if (link.OffReason != null) return;
            link.OffReason = reason;
            Rebuild(link.Point);
        }
        Note($"points: the link of {link.Owner} on {link.Point} is off: {reason}");
    }

    private PointLink Attach(string owner, string point, bool observer, Delegate run, Type tin, Type tout, PointOrder? order)
    {
        Check(owner, nameof(owner));
        Check(point, nameof(point));
        if (!point.Contains(':')) throw new ArgumentException("name the point in full, \"<owner id>:<name>\"", nameof(point));
        lock (_lock)
        {
            var link = new PointLink(this, point, owner, observer, order ?? PointOrder.Default, run, tin, tout, ++_serial);
            _links.Add(link);
            Rebuild(point);
            return link;
        }
    }

    // Recomputes the running chain of one point (under _lock): matching, switched-on links in a stable order.
    private void Rebuild(string point)
    {
        var links = _links.Where(l => l.Point == point).ToList();
        if (!_points.TryGetValue(point, out var def))
        {
            _chains.TryRemove(point, out _);
            return;
        }
        var live = new List<PointLink>();
        foreach (var l in links)
        {
            if (l.OffReason != null && !l.OffReason.StartsWith("does not match", StringComparison.Ordinal) &&
                l.OffReason != "switched off") continue;  // threw: stays off
            if (l.Input != def.In || l.Output != def.Out)
                l.OffReason = $"does not match the point: it takes ({l.Input.Name}, {l.Output.Name}), the point ({def.In.Name}, {def.Out.Name})";
            else if (_switchedOff.Contains((point, l.Owner)))
                l.OffReason = "switched off";
            else
            {
                l.OffReason = null;
                live.Add(l);
            }
        }
        _chains[point] = Sort(point, live);
    }

    // Priority, then mod id, then attach order; then the before/after constraints, kept stable. A cycle leaves the
    // priority order and is noted.
    private PointLink[] Sort(string point, List<PointLink> links)
    {
        var baseOrder = links.OrderBy(l => l.Order.Priority).ThenBy(l => l.Owner, StringComparer.Ordinal).ThenBy(l => l.Serial).ToList();
        var after = baseOrder.ToDictionary(l => l, _ => new HashSet<PointLink>());  // l runs after each of these
        foreach (var l in baseOrder)
        {
            foreach (var m in baseOrder.Where(m => m != l && l.Order.Before.Contains(m.Owner))) after[m].Add(l);
            foreach (var m in baseOrder.Where(m => m != l && l.Order.After.Contains(m.Owner))) after[l].Add(m);
        }
        var result = new List<PointLink>();
        var placed = new HashSet<PointLink>();
        while (result.Count < baseOrder.Count)
        {
            var next = baseOrder.FirstOrDefault(l => !placed.Contains(l) && after[l].All(placed.Contains));
            if (next == null)
            {
                Note($"points: the links on {point} name each other in before/after; they run by priority");
                return baseOrder.ToArray();
            }
            result.Add(next);
            placed.Add(next);
        }
        return result.ToArray();
    }

    private static void Check(string s, string what)
    {
        if (string.IsNullOrWhiteSpace(s)) throw new ArgumentException($"{what} is empty", what);
    }
}

/// <summary>Extension points between mods (see <see cref="PointRegistry"/>): the calling mod owns what it defines and
/// attaches, and its points and links go when it unloads.</summary>
public static class Points
{
    /// <summary>The registry every mod shares.</summary>
    public static PointRegistry Shared { get; } = new() { Log = line => Backend.Current.Log(0, LogLevel.Warn, line) };

    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);

    /// <summary>Defines the point "&lt;this mod's id&gt;:<paramref name="name"/>".</summary>
    public static Point<TIn, TOut> Define<TIn, TOut>(string name, PointMode mode, bool allowRefuse = false)
        => Shared.Define<TIn, TOut>(Owner(), name, mode, allowRefuse);

    /// <summary>Attaches this mod's link to another mod's point, "&lt;owner id&gt;:&lt;name&gt;".</summary>
    public static PointLink Extend<TIn, TOut>(string point, Func<TIn, TOut, (PointVerdict, TOut)> link, PointOrder? order = null)
        => Shared.Extend(Owner(), point, link, order);

    /// <summary>Attaches this mod's observer to a point.</summary>
    public static PointLink Observe<TIn, TOut>(string point, Action<TIn, TOut> observer, PointOrder? order = null)
        => Shared.Observe(Owner(), point, observer, order);

    private static string Owner()
    {
        var mod = ModContext.CurrentMod;
        if (mod == null) return "kiln";
        lock (Watched)
        {
            if (Watched.Add(mod))
            {
                var id = mod.Info.Id;
                mod.Retiring += () =>
                {
                    Shared.RemoveOwner(id);
                    lock (Watched) Watched.Remove(mod);
                };
            }
        }
        return mod.Info.Id;
    }
}
