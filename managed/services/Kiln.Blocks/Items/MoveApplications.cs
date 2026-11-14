// SPDX-License-Identifier: MIT
// A routed weapon's moves, applied as one generation (chapter 16c, "Atomic generation decision"). MoveRuntime's own path applies move by move and logs
// what it could not do; a weapon with follow-ups must not end up half routed, so it goes through here instead:
//   1. plan    MoveRoutes.Plan on the game's rows: every source, target, selector, state and timing is checked before anything changes;
//   2. stage   a fresh set of clones, named by the plan's hash, that no item slot points at yet. A clone the game refuses ends the attempt;
//   3. rules   one pass later, when the clones are in the world: their rules (states, retime, links) in one document, then every link read back;
//   4. switch  the item's slots in one document of their own, on the tick, each slot read back. An error or a slot that reads back wrong puts the
//              previous slots back in the same tick (the last applied generation's, or the class's when there is none);
//   5. only then the status says Applied and the event is raised.
// A clone cannot be taken out of a running game, so the clones of an attempt that was refused or replaced stay registered and unused; that is why a
// weapon takes at most 16 distinct plans per process. The applied generation's rules are kept with the mod's other rules, so a content application
// later in the process (a world loaded again) puts them back with everything else.
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>What applying a generation needs from the running game: MoveRuntime's on a game, a made-up one in tests.</summary>
internal interface IMoveHost
{
    /// <summary>The content generation the rows below are read in.</summary>
    ulong ContentGeneration { get; }
    IReadOnlyList<SlotRow>? Slots(string item);
    Func<string, IReadOnlyList<SubSequence>?> Reader();
    IPlayerStateLookup? Lookup { get; }
    /// <summary>Every sequence clone the game data service knows: its state (<c>registered</c>, <c>pending</c>, <c>waiting</c>, <c>error</c>) and error.</summary>
    IReadOnlyDictionary<Guid, (string State, string? Error)> Clones();
    /// <summary>Defines a sequence clone as <paramref name="owner"/>'s. Throws what kiln.gamedata throws when it refuses.</summary>
    void Define(JsonObject definition, ulong owner);
    /// <summary>Applies a rules document as <paramref name="owner"/>'s layers; the errors of its report.</summary>
    IReadOnlyList<string> Apply(ulong owner, JsonObject document, string source);
    /// <summary>What a clone's window leads to now. Null: it cannot be read.</summary>
    Guid? Link(string clone, int sub, int k);
    /// <summary>The sequence an item's slot plays now. Null: it cannot be read.</summary>
    Guid? Slot(string item, int index);
    /// <summary>Runs <paramref name="next"/> at the mod's next ready pass (once its clones are in the world).</summary>
    void Later(ModBlocks mod, Action<Action<string>> next);
    /// <summary>Keeps rules with the mod's own, for later content applications (not applied now).</summary>
    void Keep(ModBlocks mod, IReadOnlyList<(string Section, string Entry, JsonObject Rules)> rules);
    void Drop(ModBlocks mod, IReadOnlyList<(string Section, string Entry, JsonObject Rules)> rules);
}

internal static class MoveApplications
{
    private sealed class Entry(ModBlocks mod, ulong owner, MovePlan plan, JsonObject json)
    {
        public readonly ModBlocks Mod = mod;
        public readonly ulong Owner = owner;
        public readonly MovePlan Plan = plan;
        public readonly JsonObject Json = json;
        public volatile MoveApplicationResult Current = Pending;
        public ulong Requested;
        /// <summary>The content generation of the last attempt; null before the first.</summary>
        public ulong? Attempted;
        public RouteGeneration? Active;
        public IReadOnlyList<(string Section, string Entry, JsonObject Rules)> Kept = [];
        public bool Removed;
    }

    private static readonly MoveApplicationResult Pending = new() { State = MoveApplicationState.PendingContent, Reason = "PendingContent" };
    private static readonly ConcurrentDictionary<(string Mod, string Block), Entry> Entries = new();
    // the plans a weapon has staged clones for in this process: it outlives a reload, as the clones do
    private static readonly ConcurrentDictionary<(string Mod, string Block), HashSet<string>> Staged = new();

    /// <summary>The running game (tests set it).</summary>
    internal static IMoveHost Host = MoveRuntime.Host;
    /// <summary>Where the events go (tests set it): the tick pump delivers them.</summary>
    internal static Action<KilnEvent> Sink = ev => Events.Enqueue(ev);
    /// <summary>For the lab and tests: the next clone definitions that are refused as if kiln.gamedata had (a count; 0: none).</summary>
    internal static int RefuseDefines;

    /// <summary>For tests: forget every weapon.</summary>
    internal static void Reset()
    {
        Entries.Clear();
        Staged.Clear();
        RefuseDefines = 0;
    }

    /// <summary>A routed weapon was defined: its first attempt runs when content is ready.</summary>
    public static void Start(ModBlocks mod, MovePlan plan, JsonObject json)
    {
        var key = (plan.Mod, plan.Name);
        if (Entries.TryGetValue(key, out var old)) Remove(old);
        var entry = new Entry(mod, mod.Owner, plan, json);
        Entries[key] = entry;
        // a mod's code: its unload takes the weapon's layers off, and the status says so. A data-only mod's blocks go with ModBlocks.Reset (Forget).
        if (ModContext.CurrentMod is { } context && context.Info.Id == plan.Mod)
        {
            context.Retiring += () => Remove(entry);
            if (!context.Alive) Remove(entry);
        }
        Host.Later(mod, problem => Run(entry, problem));
    }

    /// <summary>A ready pass of the mod (after every content application, and whenever the mod has ready work): a weapon whose last attempt was refused
    /// on other content than the game has now is tried again, since what was missing may be there. On the same content nothing is tried twice.</summary>
    public static void Ready(ModBlocks mod, Action<string> problem)
    {
        if (Entries.IsEmpty) return;
        var content = Host.ContentGeneration;
        foreach (var e in Entries.Values.Where(e => ReferenceEquals(e.Mod, mod)).ToList())
        {
            bool again;
            lock (e) again = !e.Removed && e.Current.State == MoveApplicationState.Refused && e.Attempted is { } at && at != content;
            if (again) Run(e, problem);
        }
    }

    /// <summary>For the lab and tests: tries a refused weapon again now, on the same content.</summary>
    internal static bool Retry(string mod, string block, Action<string> problem)
    {
        if (!Entries.TryGetValue((mod, block), out var e)) return false;
        Run(e, problem);
        return true;
    }

    /// <summary>The mod reloads or is gone: its weapons' layers went with its owner.</summary>
    public static void Forget(string mod)
    {
        foreach (var e in Entries.Values.Where(e => e.Plan.Mod == mod).ToList()) Remove(e);
    }

    /// <summary>The snapshot for a weapon, or null: it has no follow-ups (or is not defined).</summary>
    public static MoveApplicationResult? Status(string mod, string block) => Entries.TryGetValue((mod, block), out var e) ? e.Current : null;

    private static void Remove(Entry e)
    {
        lock (e)
        {
            if (e.Removed) return;
            e.Removed = true;
            Host.Drop(e.Mod, e.Kept);
            e.Kept = [];
            e.Active = null;
            var now = e.Current;
            Publish(e, new MoveApplicationResult
            {
                State = MoveApplicationState.Removed, Reason = "Removed", RequestedGeneration = now.RequestedGeneration, AppliedGeneration = now.AppliedGeneration,
                ContentGeneration = now.ContentGeneration,
            });
        }
    }

    private static void Publish(Entry e, MoveApplicationResult after)
    {
        var before = e.Current;
        e.Current = after;
        if (before.State == after.State && before.Reason == after.Reason && before.AppliedGeneration == after.AppliedGeneration) return;
        BlocksBridge.Log(after.State == MoveApplicationState.Refused ? LogLevel.Error : LogLevel.Info,
            $"blocks: {e.Plan.Mod}: {e.Plan.Block}: moves {after.State} (attempt {after.RequestedGeneration}, applied {after.AppliedGeneration}){(after.Reason.Length > 0 && !after.Ok ? ": " + after.Reason : "")}");
        try { Sink(new WeaponMovesApplicationChangedEvent(e.Plan.Mod, e.Plan.Name, before, after)); }
        catch (Exception x) when (x is not OutOfMemoryException) { BlocksBridge.Log(LogLevel.Error, $"blocks: posting WeaponMovesApplicationChangedEvent failed: {x.Message}"); }
    }

    private static void Refuse(Entry e, ulong request, ulong content, string reason, Action<string> problem)
    {
        problem(reason);
        // the last applied generation keeps playing: its number and its clones stay in the answer
        Publish(e, e.Current with { Ok = false, State = MoveApplicationState.Refused, Reason = reason, RequestedGeneration = request, ContentGeneration = content });
    }

    private static JsonObject Document(string section, IEnumerable<(string Entry, JsonObject Rules)> entries)
    {
        var sec = new JsonObject();
        foreach (var (entry, rules) in entries)
        {
            if (sec[entry] is JsonObject have)
                foreach (var (k, v) in rules) have[k] = v?.DeepClone();
            else sec[entry] = rules.DeepClone();
        }
        return new JsonObject { ["format"] = 1, [section] = sec };
    }

    // Step 1 and 2: plan on the game's rows, then stage the clones.
    private static void Run(Entry e, Action<string> problem)
    {
        lock (e)
        {
            if (e.Removed) return;
            var plan = e.Plan;
            var content = Host.ContentGeneration;
            e.Attempted = content;
            RouteGeneration? gen;
            List<string> problems;
            try { (gen, problems) = MoveRoutes.Plan(plan, Host.Slots(plan.Item), Host.Reader(), Host.Lookup); }
            catch (Exception x) when (MoveRuntime.Readable(x))
            {
                (gen, problems) = (null, [$"block {plan.Block}, moves: the game's data could not be read: {x.Message}"]);
            }
            if (gen == null)
            {
                foreach (var p in problems.Skip(1)) problem(p);
                Refuse(e, ++e.Requested, content, problems.Count > 1 ? $"{problems[0]} (and {problems.Count - 1} more, in the log)" : problems[0], problem);
                return;
            }
            if (e.Active?.Hash == gen.Hash)
            {
                // the same moves on the same data as what plays: nothing to stage, nothing to switch, no event (unless a refused attempt stood in between)
                if (e.Current.State != MoveApplicationState.Applied)
                    Publish(e, e.Current with { Ok = true, State = MoveApplicationState.Applied, Reason = "", RequestedGeneration = ++e.Requested, ContentGeneration = content });
                return;
            }
            var request = ++e.Requested;
            var staged = Staged.GetOrAdd((plan.Mod, plan.Name), _ => new HashSet<string>(StringComparer.Ordinal));
            lock (staged)
            {
                if (!staged.Contains(gen.Hash) && staged.Count >= MoveRoutes.MaxHashes)
                {
                    Refuse(e, request, content, $"RestartRequired: block {plan.Block}, moves: this weapon has had {MoveRoutes.MaxHashes} different sets of moves in this process, and a clone stays "
                        + "in the game until it ends: restart to apply another", problem);
                    return;
                }
                staged.Add(gen.Hash);
            }
            var have = Host.Clones();
            foreach (var def in gen.Clones)
            {
                if (have.ContainsKey(Guid.Parse((string)def["guid"]!))) continue;     // staged by an earlier attempt with this plan
                try
                {
                    if (RefuseDefines > 0)
                    {
                        RefuseDefines--;
                        throw new InvalidOperationException("refused by the test switch");
                    }
                    Host.Define(def.DeepClone().AsObject(), e.Owner);
                }
                catch (Exception x) when (x is ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
                {
                    Refuse(e, request, content, $"block {plan.Block}, moves: the clone {def["name"]} of {def["base"]} was refused: {x.Message}", problem);
                    return;
                }
            }
            Host.Later(e.Mod, next => Finish(e, request, content, gen, next));
        }
    }

    // Steps 3 to 5, one ready pass later: the clones are in the world (BlockRuntime waits for that before it runs a mod's ready work).
    private static void Finish(Entry e, ulong request, ulong content, RouteGeneration gen, Action<string> problem)
    {
        lock (e)
        {
            if (e.Removed || e.Requested != request) return;      // stale: the weapon went, or a newer attempt owns it
            if (Host.ContentGeneration != content)
            {
                // content was applied again since the rows were read: read them again
                Run(e, problem);
                return;
            }
            var plan = e.Plan;
            var have = Host.Clones();
            foreach (var def in gen.Clones)
            {
                var st = have.TryGetValue(Guid.Parse((string)def["guid"]!), out var s) ? s : ("missing", null);
                if (st.Item1 == "registered") continue;
                Refuse(e, request, content, $"block {plan.Block}, moves: the clone {def["name"]} is {st.Item1}{(st.Item2 is { Length: > 0 } err ? ": " + err : "")}", problem);
                return;
            }
            var source = $"moves of {plan.Mod}:{plan.Name}";
            if (gen.SequenceRules.Count > 0)
            {
                var errors = Host.Apply(e.Owner, Document("sequences", gen.SequenceRules), source);
                if (errors.Count > 0)
                {
                    Refuse(e, request, content, $"block {plan.Block}, moves: a rule of the new moves was refused: {errors[0]}", problem);
                    return;
                }
            }
            foreach (var ((clone, sub, k), want) in gen.Links)
            {
                if (Host.Link(clone, sub, k) == want) continue;
                Refuse(e, request, content, $"block {plan.Block}, moves: the follow-up of {clone}{(sub > 0 ? "#" + sub : "")}, window {k}, did not read back as set", problem);
                return;
            }
            // the switch: only the item's slots, and what they were is put back in this same tick if any of them does not take
            var switchErrors = Host.Apply(e.Owner, Document("itemdata", [(gen.Item, gen.SlotRules)]), source);
            var wrong = switchErrors.Count > 0 ? switchErrors[0] : null;
            if (wrong == null)
                foreach (var (index, want) in gen.SlotGuids)
                    if (Host.Slot(gen.Item, index) != want)
                    {
                        wrong = $"slot {index} did not read back as set";
                        break;
                    }
            if (wrong != null)
            {
                var back = new JsonObject();
                foreach (var (path, _) in gen.SlotRules)
                    back[path] = e.Active?.SlotRules[path]?.DeepClone() ?? new JsonObject { ["reset"] = true };
                var undo = Host.Apply(e.Owner, Document("itemdata", [(gen.Item, back)]), source + " (put back)");
                Refuse(e, request, content, $"block {plan.Block}, moves: the item's slots were not switched: {wrong}"
                    + (undo.Count > 0 ? $"; and putting the previous slots back failed: {undo[0]}" : ""), problem);
                return;
            }
            Host.Drop(e.Mod, e.Kept);
            e.Kept = gen.SequenceRules.Select(r => ("sequences", r.Entry, r.Rules)).Append(("itemdata", gen.Item, gen.SlotRules)).ToList();
            Host.Keep(e.Mod, e.Kept);
            e.Active = gen;
            lock (e.Json)
                e.Json["movesReady"] = new JsonObject
                {
                    ["generation"] = request, ["plan"] = gen.Hash,
                    ["clones"] = new JsonObject(gen.LiveClones.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value.ToString()))),
                    ["rules"] = new JsonObject(e.Kept.Select(r => KeyValuePair.Create($"{r.Section} {r.Entry}", (JsonNode?)r.Rules.DeepClone()))),
                };
            e.Mod.ReportDirty = true;
            Publish(e, new MoveApplicationResult
            {
                Ok = true, State = MoveApplicationState.Applied, RequestedGeneration = request, AppliedGeneration = request, ContentGeneration = content,
                LiveClones = gen.LiveClones.ToFrozenDictionary(StringComparer.Ordinal),
            });
        }
    }
}
