// SPDX-License-Identifier: MIT
// Decides which mods load and in which order (Forge: ModSorter + dependency checks). Pure: no I/O, no loading.
namespace Kiln.Host;

/// <summary>A mod folder found in <c>kiln/mods</c>: its manifest, or why the manifest could not be read.</summary>
internal sealed class ModCandidate
{
    public required string Directory { get; init; }
    public Manifest? Manifest { get; init; }
    public string? Error { get; init; }
    public string Folder => System.IO.Path.GetFileName(Directory.TrimEnd('/', '\\'));
    public string Id => Manifest?.Id ?? Folder;

    public static ModCandidate Read(string directory)
    {
        try
        {
            return new ModCandidate { Directory = directory, Manifest = Manifest.Read(directory) };
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ModCandidate { Directory = directory, Error = e.Message };
        }
        catch (InvalidOperationException e)
        {
            // A string the JSON parser accepted and the decoder refuses (an escape that is half a surrogate pair): one
            // mod's bad manifest is that mod's error, never the end of the scan of all mods.
            return new ModCandidate { Directory = directory, Error = $"kiln.mod.json holds a string that is not text: {e.Message}" };
        }
    }

    public override string ToString() => Manifest is { } m ? $"{m.Id} {m.Version}" : $"folder {Folder}";
}

/// <summary>What the resolver checks against.</summary>
/// <param name="Loaded">Mods already running (a runtime load or reload resolves against them; they are not reordered).</param>
internal sealed record ResolveEnvironment(
    SemVersion KilnVersion, SemVersion ApiVersion, ManifestSide Side, Func<string, bool> HasFeature,
    IReadOnlyDictionary<string, Manifest> Loaded)
{
    public static readonly IReadOnlyDictionary<string, Manifest> NoneLoaded = new Dictionary<string, Manifest>();

    /// <summary>A client that hosts a world right now: its server world runs here, so <c>side: "server"</c> mods load
    /// too (docs/kiln/client.md, "Mods and sides"). Off by default (the launcher's Rust port and the parity fixtures
    /// resolve the menu state).</summary>
    public bool Hosting { get; init; }

    /// <summary>The capability registry (<see cref="Kiln.Capabilities.Get"/> in the game): a manifest's <c>features</c>
    /// are checked against it and a refusal carries the row's reason; a session row cannot be required. Null: only
    /// <see cref="HasFeature"/> is asked, with the text the launcher's Rust port and the parity fixtures pin.</summary>
    public Func<string, Capability>? Capability { get; init; }
}

internal sealed record Rejection(ModCandidate Candidate, ModState State, string Reason);

internal sealed class ResolveResult
{
    /// <summary>Mods to load, dependencies first.</summary>
    public List<ModCandidate> Order { get; } = [];

    /// <summary>Mods that will not load (<see cref="ModState.Failed"/> or <see cref="ModState.Skipped"/>) and why.</summary>
    public List<Rejection> Rejected { get; } = [];
}

internal static class Resolver
{
    public static bool SideMatches(ManifestSide mod, ManifestSide current) => mod == ManifestSide.Both || mod == current;

    /// <summary>Side check with a hosting client: server mods match too.</summary>
    public static bool SideMatches(ManifestSide mod, ResolveEnvironment env) =>
        SideMatches(mod, env.Side) || (env.Hosting && env.Side == ManifestSide.Client && mod == ManifestSide.Server);

    public static ResolveResult Resolve(IReadOnlyList<ModCandidate> candidates, ResolveEnvironment env)
    {
        var result = new ResolveResult();
        var active = new Dictionary<string, ModCandidate>(StringComparer.Ordinal);
        var rejected = new Dictionary<string, Rejection>(StringComparer.Ordinal);
        void Reject(ModCandidate c, ModState state, string reason)
        {
            var r = new Rejection(c, state, reason);
            result.Rejected.Add(r);
            rejected.TryAdd(c.Id, r);
        }

        // 1. per-mod checks: manifest, duplicates, side, API, features
        foreach (var c in candidates)
        {
            if (c.Manifest is not { } m)
            {
                Reject(c, ModState.Failed, c.Error ?? "unreadable manifest");
                continue;
            }
            if (active.TryGetValue(m.Id, out var first))
            {
                Reject(c, ModState.Failed, $"duplicate mod id '{m.Id}' (also in folder {first.Folder}); only the first loads");
                continue;
            }
            if (env.Loaded.ContainsKey(m.Id))
            {
                Reject(c, ModState.Failed, $"mod {m.Id} is already loaded");
                continue;
            }
            if (!SideMatches(m.Side, env))
            {
                Reject(c, ModState.Skipped, $"side '{Lower(m.Side)}': not loaded on a {(env.Side == ManifestSide.Client ? "client" : "server")}");
                continue;
            }
            if (!m.ApiRange.Satisfies(env.ApiVersion))
            {
                Reject(c, ModState.Failed, $"needs Kiln API {m.ApiVersion}; this Kiln provides API {env.ApiVersion}");
                continue;
            }
            if (env.Capability is { } capability)
            {
                if (FeatureProblems(m, capability) is { } problems)
                {
                    Reject(c, ModState.Failed, problems);
                    continue;
                }
            }
            else
            {
                var missing = m.Features.Where(f => !env.HasFeature(f)).ToArray();
                if (missing.Length > 0)
                {
                    Reject(c, ModState.Failed, $"needs feature{(missing.Length > 1 ? "s" : "")} {string.Join(", ", missing)}, unavailable on this game build");
                    continue;
                }
            }
            active[m.Id] = c;
        }

        while (true)
        {
            // 2. dependencies and incompatibilities, to a fixpoint (a failure can break a dependent)
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var c in active.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray())
                {
                    var reason = CheckDependencies(c.Manifest!, active, rejected, env);
                    if (reason is null) continue;
                    active.Remove(c.Id);
                    Reject(c, ModState.Failed, reason);
                    changed = true;
                }
            }

            // 3. order (Kahn, ties by id); what is left sits in or behind a cycle
            var edges = Edges(active);
            var order = TopologicalOrder(active, edges, out var leftover);
            if (leftover.Count == 0)
            {
                result.Order.AddRange(order.Select(id => active[id]));
                return result;
            }
            var cycles = Cycles(leftover, edges);
            if (cycles.Count == 0)  // cannot happen; never loop forever
                cycles = [leftover.Order(StringComparer.Ordinal).ToList()];
            foreach (var cycle in cycles)
            {
                var text = string.Join(" -> ", cycle.Append(cycle[0]));
                foreach (var id in cycle.Distinct())
                {
                    if (!active.Remove(id, out var c)) continue;
                    Reject(c, ModState.Failed, $"dependency cycle: {text} (each must load after the next)");
                }
            }
        }
    }

    private static string Lower(ManifestSide s) => s.ToString().ToLowerInvariant();

    /// <summary>Each of the manifest's <c>features</c> that the registry says is off, or that depends on the session,
    /// in manifest order and joined with "; "; null when all of them are fixed and available.</summary>
    private static string? FeatureProblems(Manifest m, Func<string, Capability> capability)
    {
        List<string>? problems = null;
        foreach (var f in m.Features)
        {
            var row = capability(f);
            string? problem = null;
            if (row.Reason == CapabilityReason.Unknown && !f.StartsWith("kiln.", StringComparison.Ordinal) &&
                f.IndexOf('.') is var dot && dot > 0 && dot < f.Length - 1)
                problem = $"needs feature {f}, unavailable: no capability of this name is registered [unknown]; " +
                          "for a managed <service>.<thing> row, the service registers it only after manifests are checked. " +
                          $"Check Capabilities.Is(\"{f}\") at run time instead";
            else if (row.State == CapabilityState.Unavailable)
                problem = row.ReasonText is { Length: > 0 } text
                    ? $"needs feature {f}, unavailable: {text} [{Kiln.Capabilities.CodeName(row.Reason)}]"
                    : $"needs feature {f}, unavailable [{Kiln.Capabilities.CodeName(row.Reason)}]";
            else if (row.SessionDependent || row.State == CapabilityState.Pending)
                problem = $"requires {f}, which depends on the session ({Kiln.Capabilities.StateName(row.State)}" +
                          (row.ReasonText is { Length: > 0 } t ? ": " + t : "") +
                          $"); a manifest may only require capabilities that are fixed at boot. Check Capabilities.Is(\"{f}\") at run time instead";
            if (problem is not null) (problems ??= []).Add(problem);
        }
        return problems is null ? null : string.Join("; ", problems);
    }

    private static string? CheckDependencies(Manifest m, Dictionary<string, ModCandidate> active,
        Dictionary<string, Rejection> rejected, ResolveEnvironment env)
    {
        foreach (var d in m.Dependencies)
        {
            if (d.Id == Manifest.KilnId)
            {
                // a dev build (0.1.0-dev) counts as its release for dependency checks
                if (!d.Range.Satisfies(env.KilnVersion) && !d.Range.Satisfies(env.KilnVersion.Release))
                    return $"needs Kiln {d.RangeText}; this is Kiln {env.KilnVersion}";
                continue;
            }
            var provider = active.TryGetValue(d.Id, out var c) ? c.Manifest : env.Loaded.GetValueOrDefault(d.Id);
            if (provider is null)
            {
                if (d.Optional) continue;
                return rejected.TryGetValue(d.Id, out var r)
                    ? $"dependency {d.Id} {d.RangeText} did not load ({(r.State == ModState.Skipped ? "skipped" : "failed")}: {r.Reason})"
                    : $"missing dependency {d.Id} {d.RangeText}";
            }
            if (!d.Range.Satisfies(provider.ParsedVersion))
                return $"needs {d.Id} {d.RangeText}, found {provider.Id} {provider.Version}" +
                       (d.Optional ? " (an optional dependency must match when present)" : "");
        }
        foreach (var x in m.Incompatible)
        {
            var other = active.TryGetValue(x.Id, out var c) ? c.Manifest : env.Loaded.GetValueOrDefault(x.Id);
            if (other is not null && x.Range.Satisfies(other.ParsedVersion))
                return $"incompatible with {other.Id} {other.Version} (declared in its incompatible list)";
        }
        return null;
    }

    /// <summary>before → after edges among the active mods: dependencies (required and optional), loadAfter, loadBefore.</summary>
    private static Dictionary<string, SortedSet<string>> Edges(Dictionary<string, ModCandidate> active)
    {
        var edges = active.Keys.ToDictionary(k => k, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (id, c) in active)
        {
            var m = c.Manifest!;
            foreach (var d in m.Dependencies)
                if (active.ContainsKey(d.Id))
                    edges[d.Id].Add(id);
            foreach (var x in m.LoadAfter)
                if (active.ContainsKey(x))
                    edges[x].Add(id);
            foreach (var x in m.LoadBefore)
                if (active.ContainsKey(x))
                    edges[id].Add(x);
        }
        return edges;
    }

    private static List<string> TopologicalOrder(Dictionary<string, ModCandidate> active,
        Dictionary<string, SortedSet<string>> edges, out HashSet<string> leftover)
    {
        var indegree = active.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        foreach (var targets in edges.Values)
            foreach (var t in targets)
                indegree[t]++;
        var ready = new SortedSet<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            order.Add(next);
            foreach (var t in edges[next])
                if (--indegree[t] == 0)
                    ready.Add(t);
        }
        leftover = new HashSet<string>(indegree.Where(kv => kv.Value > 0).Select(kv => kv.Key), StringComparer.Ordinal);
        return order;
    }

    /// <summary>Strongly connected components with a cycle, each as a cycle path in "must load after" direction.</summary>
    private static List<List<string>> Cycles(HashSet<string> nodes, Dictionary<string, SortedSet<string>> edges)
    {
        // dependsOn[a] = {b | b must load before a}, restricted to the leftover nodes
        var dependsOn = nodes.ToDictionary(n => n, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (from, targets) in edges)
        {
            if (!nodes.Contains(from)) continue;
            foreach (var to in targets)
                if (nodes.Contains(to))
                    dependsOn[to].Add(from);
        }
        var cycles = new List<List<string>>();
        foreach (var scc in StronglyConnected(dependsOn))
        {
            var start = scc.Min(StringComparer.Ordinal)!;
            if (scc.Count == 1 && !dependsOn[start].Contains(start)) continue;
            cycles.Add(ShortestCycle(start, dependsOn, scc));
        }
        return cycles;
    }

    private static List<string> ShortestCycle(string start, Dictionary<string, SortedSet<string>> next, HashSet<string> within)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var n in next[start].Where(within.Contains))
        {
            if (n == start) return [start];
            if (parent.TryAdd(n, start)) queue.Enqueue(n);
        }
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var n in next[cur].Where(within.Contains))
            {
                if (n == start)
                {
                    var path = new List<string> { cur };
                    for (var p = cur; parent[p] != start; p = parent[p]) path.Add(parent[p]);
                    path.Add(start);
                    path.Reverse();
                    return path;
                }
                if (parent.TryAdd(n, cur)) queue.Enqueue(n);
            }
        }
        return within.Order(StringComparer.Ordinal).ToList();
    }

    private static List<HashSet<string>> StronglyConnected(Dictionary<string, SortedSet<string>> graph)
    {
        // Tarjan, iterative enough for mod counts (recursion depth = longest chain)
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<HashSet<string>>();
        var counter = 0;
        void Visit(string v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in graph[v])
            {
                if (!index.ContainsKey(w))
                {
                    Visit(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
            }
            if (low[v] != index[v]) return;
            var scc = new HashSet<string>(StringComparer.Ordinal);
            string x;
            do
            {
                x = stack.Pop();
                onStack.Remove(x);
                scc.Add(x);
            } while (x != v);
            result.Add(scc);
        }
        foreach (var v in graph.Keys.Order(StringComparer.Ordinal))
            if (!index.ContainsKey(v))
                Visit(v);
        return result;
    }
}
