// SPDX-License-Identifier: MIT
// What players do, driving blocks: tables from a state, an action or an item to generated programs, read by handlers of Kiln.Players' events. Those events run on the tick thread in Pre of the step
// after the change, and their scan runs only while someone subscribes (docs/kiln/services/player-state.md): nothing here subscribes until a block needs it, and then only to what it needs. So a
// mod with no triggers costs nothing, and the 351 states cost the same as one: a table lookup per change.
using System.Text.Json.Nodes;

namespace Kiln;

internal static class PlayerTriggers
{
    /// <summary>A status held by a state has no time of its own: longer than a session, and taken off when the state ends.</summary>
    internal const double WhileSeconds = 86400;

    /// <summary>One trigger, however it was written. <see cref="Stop"/> and <see cref="Refuse"/>: on a match it stops the action; a rule stops it when it does not match.</summary>
    private sealed record Entry(string Mod, string Block, string Field, Guid? Do, Guid? End, PlayerStateId[]? When, PlayerStateId[]? Unless, Func<Player, bool>? If, TimeSpan Cooldown, bool Stop, bool Refuse, Guid? Refused, string? ItemName)
    {
        public uint Item;
        public bool Off;
        public readonly Dictionary<uint, long> Last = new();
    }

    private sealed record While(string Mod, PlayerStateId State, Guid Give, Guid Take);

    private static readonly object Gate = new();
    private static readonly Dictionary<PlayerStateId, List<Entry>> States = new(), Actions = new();
    private static readonly List<Entry> Eating = new(), Eaten = new();
    private static readonly Dictionary<PlayerStateId, List<While>> Whiles = new();
    private static readonly Dictionary<(uint Entity, Guid Give), bool> Held = new();
    private static IDisposable? _states, _actions, _eating, _eaten, _reconcile;
    private static bool _warned;

    private static bool ServerSide => Client.IsDedicatedServer || Client.IsHosting;

    // ---- names ----------------------------------------------------------------------------------------------

    /// <summary>What a name stands for: a constant of Kiln's, or the game's own name. What the running game does not have is said by name, and a name nobody has is refused.</summary>
    internal static PlayerStateId Resolve(BlockContext ctx, PlayerStateRef r, string path)
    {
        var name = r.Name?.Trim() ?? "";
        if (name.Length == 0) throw new BlockError(ctx.Block, path, "this needs a state or an action: a PlayerStateId, or its name");
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var raw)) return Known(ctx, (PlayerStateId)raw, name, path);
        if (Enum.TryParse<PlayerStateId>(name, ignoreCase: true, out var id) && !char.IsDigit(name[0])) return Known(ctx, id, name, path);
        if (PlayerStates.Find(name) is { } found) return found;
        var like = PlayerStates.All.Select(x => x.GameName).Concat(Enum.GetNames<PlayerStateId>()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(x, StringComparison.OrdinalIgnoreCase)).Order().Take(8).ToList();
        throw new BlockError(ctx.Block, path, $"\"{name}\" is no state or action of the game{(like.Count > 0 ? $"; did you mean {string.Join(", ", like)}" : "")}. docs/kiln/modding/player-states.md has all of them");
    }

    private static PlayerStateId Known(BlockContext ctx, PlayerStateId id, string name, string path)
    {
        if (PlayerStates.Available && PlayerStates.InfoOf(id) == null)
            BlocksBridge.Log(LogLevel.Warn, $"blocks: block {ctx.Block}, {path}: this game build has no state \"{name}\": it reads as unknown, and this never happens");
        return id;
    }

    private static PlayerStateId[]? ResolveAll(BlockContext ctx, IReadOnlyList<PlayerStateRef>? list, string path) =>
        list is { Count: > 0 } ? list.Select((x, i) => Resolve(ctx, x, $"{path}[{i}]")).ToArray() : null;

    private static string Show(PlayerStateId id)
    {
        var name = PlayerStates.NameOf(id);
        return name.Length > 0 ? $"0x{(ushort)id:x4} {name}" : $"0x{(ushort)id:x4}";
    }

    private static bool IsAction(PlayerStateId id) => PlayerStates.SourceOf(id) == PlayerStateSource.Action;

    private static Guid? Program(BlockContext ctx, Lowered low, string key, IReadOnlyList<EffectRef>? effects, string path)
    {
        if (effects is not { Count: > 0 }) return null;
        var (program, blob, _, listing) = EffectEmitter.SelfProgram(ctx, key, effects, path);
        low.Program(program, key, blob, listing);
        return program;
    }

    private static void Warn()
    {
        if (_warned || PlayerStates.Available) return;
        _warned = true;
        BlocksBridge.Log(LogLevel.Warn, "blocks: kiln.playerstate is not on this build: triggers on what players do never fire");
    }

    private static void AddLate(BlockContext ctx, Entry e)
    {
        // an item's id is known once content is: a name that is nothing, and a rule that cannot work, are said then, by name
        if (e.ItemName == null) return;
        BlockRuntime.AddReady(ctx.Mod, problem => Bind(e, problem));
    }

    private static void Bind(Entry e, Action<string> problem)
    {
        var item = Content.Items.Get(e.ItemName!);
        if (item == null)
        {
            e.Off = true;
            problem($"block {e.Block}, {e.Field}: the game has no item \"{e.ItemName}\"");
            return;
        }
        e.Item = item.Id;
        if ((e.Stop || e.Refuse) && UsedUpAtOnce(e.ItemName!) is { } sequence)
        {
            e.Off = true;
            problem($"block {e.Block}, {e.Field}: \"{e.ItemName}\" is used up in the first moment of {sequence}, before anything can stop it: the item would be gone with nothing to show for it. This rule stays off");
        }
    }

    /// <summary>The sequence of an item that uses it up at once, from the game's data: an event that consumes it at time 0.</summary>
    private static string? UsedUpAtOnce(string item)
    {
        try
        {
            if (Content.ItemData.Get(item)?.GetJson("sequences")?["value"] is not JsonArray sequences) return null;
            foreach (var s in sequences)
            {
                if (s?["sequence"]?.ToString() is not { Length: > 0 } id || Content.Sequences.Get(id) is not { } sequence) continue;
                if (sequence.GetJson("events")?["value"] is not JsonArray events) continue;
                foreach (var ev in events)
                    if (ev?["consume"] is JsonValue c && c.TryGetValue<bool>(out var on) && on && TimeOf(ev) <= 0) return sequence.Name;
            }
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: cannot read how {item} is used: {e.Message}");
        }
        return null;
    }

    private static double TimeOf(JsonNode ev)
    {
        var t = ev["time"];
        if (t is JsonObject o) t = o["value"];
        return t is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.MaxValue;
    }

    // ---- what a block becomes -------------------------------------------------------------------------------

    public static BlockHandle Define(BlockContext ctx, Lowered low, string name, TriggerBlock t)
    {
        var key = t.Was ?? name;
        var mod = ctx.Mod.Id;
        var field = t.On.Name ?? "";
        var eating = field.Equals(PlayerStateRef.Eating.Name, StringComparison.OrdinalIgnoreCase);
        var eaten = field.Equals(PlayerStateRef.Eaten.Name, StringComparison.OrdinalIgnoreCase);
        var state = eating || eaten ? PlayerStateId.None : Resolve(ctx, t.On, "on");
        var action = !eating && !eaten && IsAction(state);
        if (t.Cooldown < 0) throw new BlockError(ctx.Block, "cooldown", $"{t.Cooldown} is less than 0");
        if (t.Do is not { Count: > 0 } && t.OnEnd is not { Count: > 0 } && !t.Stop) throw new BlockError(ctx.Block, "do", "a trigger that does nothing: give it do, onEnd or stop");
        if (t.OnEnd is { Count: > 0 } && (eating || eaten || action)) throw new BlockError(ctx.Block, "onEnd", $"\"{field}\" is no state: it has no end to run at");
        if (t.Stop && eaten) throw new BlockError(ctx.Block, "stop", "the item has gone by then: say \"on\": \"eating\"");
        if (t.Stop && !eating && !action) throw new BlockError(ctx.Block, "stop", $"\"{field}\" is a state, and a state cannot be stopped: stop the action that starts it");
        var entry = new Entry(mod, ctx.Block, "item", Program(ctx, low, $"{key}.do", t.Do, "do"), Program(ctx, low, $"{key}.end", t.OnEnd, "onEnd"),
            ResolveAll(ctx, t.When, "when"), ResolveAll(ctx, t.Unless, "unless"), t.If, TimeSpan.FromSeconds(t.Cooldown), t.Stop, false, null, t.Item is { } i ? ctx.ItemName(i, "item") : null);
        low.Json["trigger"] = new JsonObject
        {
            ["on"] = eating || eaten ? field.ToLowerInvariant() : Show(state), ["item"] = entry.ItemName, ["stop"] = t.Stop, ["cooldown"] = t.Cooldown,
            ["when"] = entry.When is { } w ? new JsonArray(w.Select(x => (JsonNode?)JsonValue.Create(Show(x))).ToArray()) : null,
            ["unless"] = entry.Unless is { } u ? new JsonArray(u.Select(x => (JsonNode?)JsonValue.Create(Show(x))).ToArray()) : null,
        };
        low.AfterCommit(() =>
        {
            Warn();
            lock (Gate)
                if (eating) Eating.Add(entry);
                else if (eaten) Eaten.Add(entry);
                else if (action) Add(Actions, state, entry);
                else Add(States, state, entry);
            AddLate(ctx, entry);
        });
        return new BlockHandle<TriggerBlock>(mod, name, BlockIds.Derive(mod, "trigger", key), name, t);
    }

    private static void Add(Dictionary<PlayerStateId, List<Entry>> table, PlayerStateId state, Entry e)
    {
        if (!table.TryGetValue(state, out var list)) table[state] = list = new();
        list.Add(e);
    }

    /// <summary>What an item block says about being eaten. Its own id is known when content is, as any other's.</summary>
    public static void DefineForItem(BlockContext ctx, Lowered low, string key, string game, ItemBlock b)
    {
        var mod = ctx.Mod.Id;
        if (b.OnEat is { Count: > 0 } onEat)
        {
            var entry = new Entry(mod, ctx.Block, "onEat", Program(ctx, low, $"{key}.eaten", onEat, "onEat"), null, null, null, null, TimeSpan.Zero, false, false, null, game);
            low.AfterCommit(() =>
            {
                Warn();
                lock (Gate) Eaten.Add(entry);
                AddLate(ctx, entry);
            });
        }
        if (b.CanEat is { } rule)
        {
            if (rule.When is not { Count: > 0 } && rule.Unless is not { Count: > 0 } && rule.If == null) throw new BlockError(ctx.Block, "canEat", "a rule with nothing in it: give it when, unless, or if");
            var entry = new Entry(mod, ctx.Block, "canEat", null, null, ResolveAll(ctx, rule.When, "canEat.when"), ResolveAll(ctx, rule.Unless, "canEat.unless"), rule.If, TimeSpan.Zero, false, true,
                Program(ctx, low, $"{key}.refused", rule.OnRefused, "canEat.onRefused"), game);
            low.AfterCommit(() =>
            {
                Warn();
                lock (Gate) Eating.Add(entry);
                AddLate(ctx, entry);
            });
        }
    }

    public static void DefineWhile(BlockContext ctx, Lowered low, string key, Guid status, PlayerStateRef state)
    {
        var id = Resolve(ctx, state, "while");
        var named = new StatusRef(null, status.ToString());
        var give = Program(ctx, low, $"{key}.while.on", [new EffectBlock { Applies = new StatusApply { Status = named, Seconds = WhileSeconds } }], "while")!.Value;
        var take = Program(ctx, low, $"{key}.while.off", [new EffectBlock { Removes = [named] }], "while")!.Value;
        low.Json["while"] = Show(id);
        var mod = ctx.Mod.Id;
        low.AfterCommit(() =>
        {
            Warn();
            lock (Gate)
            {
                if (!Whiles.TryGetValue(id, out var list)) Whiles[id] = list = new();
                list.Add(new While(mod, id, give, take));
            }
        });
    }

    /// <summary>A mod is making its blocks again.</summary>
    public static void Forget(string mod)
    {
        lock (Gate)
        {
            foreach (var table in new[] { States, Actions })
                foreach (var list in table.Values) list.RemoveAll(e => e.Mod == mod);
            Eating.RemoveAll(e => e.Mod == mod);
            Eaten.RemoveAll(e => e.Mod == mod);
            foreach (var list in Whiles.Values) list.RemoveAll(w => w.Mod == mod);
        }
    }

    // ---- while the game runs --------------------------------------------------------------------------------

    /// <summary>From the service's own tick, so that the subscriptions are the service's: a mod's would end with the mod. Only what a block asks for, and only where the server world is.</summary>
    public static void Subscribe()
    {
        lock (Gate)
        {
            if (States.Count == 0 && Actions.Count == 0 && Eating.Count == 0 && Eaten.Count == 0 && Whiles.Count == 0) return;
            if (!ServerSide || !PlayerStates.Available) return;
            if (States.Count > 0 || Whiles.Count > 0) _states ??= Events.On<PlayerStateChangedEvent>(OnState);
            if (Actions.Count > 0) _actions ??= Events.On<PlayerActionEvent>(OnAction);
            if (Eating.Count > 0) _eating ??= Events.On<PlayerConsumeEvent>(OnEating);
            if (Eaten.Count > 0) _eaten ??= Events.On<PlayerConsumedEvent>(OnEaten);
            if (Whiles.Count > 0) _reconcile ??= BlocksBridge.AddTick("kiln blocks whiles", Reconcile);
        }
    }

    private static bool Matches(Entry e, Player p)
    {
        if (e.When is { } when && !when.Any(s => PlayerStates.Is(p.Entity, s))) return false;
        if (e.Unless is { } unless && unless.Any(s => PlayerStates.Is(p.Entity, s))) return false;
        if (e.If is { } check)
        {
            try
            {
                if (!check(p)) return false;
            }
            catch (Exception x)
            {
                BlocksBridge.Log(LogLevel.Warn, $"blocks: block {e.Block}: its check threw: {x.Message}");
                return false;
            }
        }
        return true;
    }

    private static bool Cooled(Entry e, EntityId who)
    {
        if (e.Cooldown <= TimeSpan.Zero) return true;
        var now = Environment.TickCount64;
        if (e.Last.TryGetValue(who.Value, out var last) && now - last < e.Cooldown.TotalMilliseconds) return false;
        e.Last[who.Value] = now;
        return true;
    }

    private static void Run(Guid? program, EntityId who, string block)
    {
        if (program is not { } g || !World.Exists(who) || World.Position(who) is not { } at) return;
        var started = Abilities.RunProgram(g, at, new ImpactOptions { Root = who, Tag = "blocks-trigger" });
        if (started.IsFaulted) BlocksBridge.Log(LogLevel.Warn, $"blocks: block {block}: its program did not start on {who}: {started.Exception?.GetBaseException().Message}");
    }

    private static List<Entry>? Copy(Dictionary<PlayerStateId, List<Entry>> table, PlayerStateId state)
    {
        lock (Gate) return table.TryGetValue(state, out var list) && list.Count > 0 ? list.ToList() : null;
    }

    private static bool Holds(Entry e, Player p)
    {
        if (e.Off) return false;
        if (e.ItemName == null) return true;
        if (e.Item == 0) return false;
        if (PlayerStates.EquipmentOf(p.Entity) is not { } gear) return false;
        if (gear.Held.Id == e.Item) return true;
        foreach (var slot in Enum.GetValues<PlayerEquipSlot>())
            if (gear[slot].Id == e.Item) return true;
        return false;
    }

    private static void OnState(PlayerStateChangedEvent e)
    {
        if (Copy(States, e.State) is { } entries)
            foreach (var t in entries)
            {
                if (!Holds(t, e.Player) || !Matches(t, e.Player)) continue;
                if (e.Entered)
                {
                    if (Cooled(t, e.Player.Entity)) Run(t.Do, e.Player.Entity, t.Block);
                }
                else Run(t.End, e.Player.Entity, t.Block);
            }
        List<While>? whiles = null;
        lock (Gate)
            if (Whiles.TryGetValue(e.State, out var list) && list.Count > 0) whiles = list.ToList();
        if (whiles == null) return;
        foreach (var w in whiles) Hold(w, e.Player.Entity, e.Entered);
    }

    private static void Hold(While w, EntityId who, bool on)
    {
        lock (Gate)
        {
            if (Held.TryGetValue((who.Value, w.Give), out var was) && was == on) return;
            Held[(who.Value, w.Give)] = on;
        }
        Run(on ? w.Give : w.Take, who, w.State.ToString());
    }

    private static void OnAction(PlayerActionEvent e)
    {
        if (Copy(Actions, e.Trigger) is not { } entries) return;
        foreach (var t in entries)
        {
            if (t.Off || (t.ItemName != null && e.Item.Id != t.Item) || !Matches(t, e.Player) || !Cooled(t, e.Player.Entity)) continue;
            if (t.Stop) Stop(e, t);
            Run(t.Do, e.Player.Entity, t.Block);
        }
    }

    private static void OnEating(PlayerConsumeEvent e)
    {
        List<Entry> entries;
        lock (Gate) entries = Eating.ToList();
        foreach (var t in entries)
        {
            if (t.Off || (t.ItemName != null && e.Item.Id != t.Item)) continue;
            var matches = Matches(t, e.Player);
            if (t.Refuse)
            {
                if (matches) continue;
                Stop(e, t);
                Run(t.Refused, e.Player.Entity, t.Block);
                continue;
            }
            if (!matches || !Cooled(t, e.Player.Entity)) continue;
            if (t.Stop) Stop(e, t);
            Run(t.Do, e.Player.Entity, t.Block);
        }
    }

    private static void Stop(PlayerActionEvent e, Entry t)
    {
        if (!e.CanCancel) BlocksBridge.Log(LogLevel.Warn, $"blocks: block {t.Block}: this build cannot stop {e.SequenceName}, so it goes on");
        e.Cancel();
    }

    private static void OnEaten(PlayerConsumedEvent e)
    {
        List<Entry> entries;
        lock (Gate) entries = Eaten.ToList();
        foreach (var t in entries)
            if (!t.Off && (t.ItemName == null || e.Item.Id == t.Item) && Matches(t, e.Player) && Cooled(t, e.Player.Entity)) Run(t.Do, e.Player.Entity, t.Block);
    }

    // The events say what changes. A player who joins in the state, or joins with a status left in their save from a state they are no longer in, has seen no change: every 2 s the roster is compared.
    private static void Reconcile(ulong step)
    {
        if (step % 50 != 7 || !ServerSide || !Players.Available) return;
        While[] whiles;
        lock (Gate) whiles = Whiles.Values.SelectMany(x => x).ToArray();
        if (whiles.Length == 0) return;
        foreach (var p in Players.All)
        {
            if (!p.InGame || p.Entity.IsNone) continue;
            foreach (var w in whiles)
                if (PlayerStates.Check(p.Entity, w.State) is { } on) Hold(w, p.Entity, on);
        }
    }
}
