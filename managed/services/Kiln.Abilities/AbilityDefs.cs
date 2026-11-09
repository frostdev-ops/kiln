// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;

namespace Kiln;

/// <summary>What an ability costs: mana, stamina and special-ability charge (attribute points; the game's own costs
/// come from the same attributes). Paid when the cast starts.</summary>
public readonly record struct AbilityCost(int Mana = 0, int Stamina = 0, int Special = 0)
{
    public static readonly AbilityCost None = default;
    public static AbilityCost OfMana(int n) => new(Mana: n);
    public static AbilityCost OfStamina(int n) => new(Stamina: n);
    public static AbilityCost OfSpecial(int n) => new(Special: n);
    public static AbilityCost operator +(AbilityCost a, AbilityCost b) => new(a.Mana + b.Mana, a.Stamina + b.Stamina, a.Special + b.Special);
    public bool IsFree => Mana <= 0 && Stamina <= 0 && Special <= 0;
    public override string ToString() => IsFree ? "free" :
        string.Join(", ", new[] { Mana > 0 ? $"{Mana} mana" : null, Stamina > 0 ? $"{Stamina} stamina" : null,
            Special > 0 ? $"{Special} special" : null }.Where(s => s is not null));
}

/// <summary>Where an ability aims.</summary>
public enum AbilityTargeting
{
    /// <summary>The caster (and its position).</summary>
    Self,
    /// <summary>A point <see cref="AbilityDefinition.Range"/> metres in front of the caster (its body yaw).</summary>
    Forward,
    /// <summary>The nearest enemy (an entity with NetworkEnemy) within <see cref="AbilityDefinition.Range"/>; the cast
    /// is refused without one.</summary>
    NearestEnemy,
}

/// <summary>How a cast was started.</summary>
public enum CastOrigin
{
    /// <summary>A key binding on a Kiln client with the mod (kiln:abilities channel).</summary>
    Key,
    /// <summary>The chat command (<c>/cast &lt;ability&gt;</c> or the ability's own command); vanilla clients can use it.</summary>
    Command,
    /// <summary><see cref="Abilities.Cast"/> from mod code (an event, a timer, an NPC).</summary>
    Server,
}

/// <summary>A scripted ability. Register it with <see cref="Abilities.Register"/> on both sides (a side "both" mod):
/// the server casts it, a Kiln client with the mod binds its key.</summary>
public sealed class AbilityDefinition
{
    /// <summary>"&lt;modid&gt;:&lt;name&gt;", e.g. "firenova:fire-nova".</summary>
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }

    /// <summary>The client's default key ("G", "Shift+F"); null = no key binding. Players remap it in the Kiln menu.</summary>
    public string? DefaultKey { get; init; }

    /// <summary>A chat command that casts it (without the prefix, e.g. "nova"); null = only <c>/cast &lt;id&gt;</c>.</summary>
    public string? ChatCommand { get; init; }

    public AbilityCost Cost { get; init; }

    /// <summary>Kiln's cooldown per caster (the game has no cooldown system for players).</summary>
    public TimeSpan Cooldown { get; init; }

    /// <summary>Cast lock: the caster cannot start another Kiln ability for this long (it does not lock the game's own
    /// attacks).</summary>
    public TimeSpan CastTime { get; init; }

    /// <summary>An existing action sequence (ActorSequenceResource GUID) the caster plays when the cast starts: its
    /// animation, VFX/SFX and its own timed events (SpawnImpact, PayUsageCost, ability locks), exactly as the game
    /// plays it (<see cref="Abilities.PlaySequence"/>). Pick one whose own costs and spawns you want; null = none.</summary>
    public Guid? CastSequence { get; init; }

    public AbilityTargeting Targeting { get; init; } = AbilityTargeting.Self;
    public double Range { get; init; } = 20;

    /// <summary>Only players may cast it through keys and commands (server code may cast for any actor).</summary>
    public bool PlayersOnly { get; init; } = true;

    /// <summary>An extra check before the cost is paid: return a refusal reason, or null to allow.</summary>
    public Func<AbilityCast, string?>? CanCast { get; init; }

    /// <summary>The effect (server, tick thread). Await <see cref="AbilityCast.At"/> for timing.</summary>
    public required Func<AbilityCast, Task> OnCast { get; init; }

    public string Name => DisplayName ?? Id;
}

/// <summary>One cast of an ability (server, tick thread).</summary>
public sealed class AbilityCast
{
    internal AbilityCast(AbilityDefinition ability, EntityId caster, CastOrigin origin, ulong step)
    {
        Ability = ability;
        Caster = caster;
        Origin = origin;
        Step = step;
        StartedMs = Environment.TickCount64;
    }

    public AbilityDefinition Ability { get; }
    public EntityId Caster { get; }
    public CastOrigin Origin { get; }
    public ulong Step { get; }
    public long StartedMs { get; }

    /// <summary>The target entity (NearestEnemy: the enemy; Self: the caster; Forward: none).</summary>
    public EntityId Target { get; internal set; }

    /// <summary>The point the ability aims at.</summary>
    public WorldPosition Position { get; internal set; }

    public WorldPosition CasterPosition { get; internal set; }
    public float CasterYaw { get; internal set; }

    /// <summary>Completes on the tick thread once <paramref name="sinceStart"/> has passed since the cast started.</summary>
    public Task At(TimeSpan sinceStart) => AbilityRegistry.Timer(StartedMs + (long)sinceStart.TotalMilliseconds);

    /// <summary>Spawns an impact template bound to the caster (<see cref="Abilities.RunImpact"/>), at
    /// <paramref name="at"/> or the cast's <see cref="Position"/>.</summary>
    public Task<EntityId> Impact(string template, WorldPosition? at = null, ImpactConfigs? configs = null) =>
        Abilities.RunImpact(template, at ?? Position, new ImpactOptions { Root = Caster, Configs = configs, Tag = Ability.Id });

    /// <summary>Plays an existing action sequence on the caster (<see cref="Abilities.PlaySequence"/>).</summary>
    public bool PlaySequence(Guid sequence, bool abortCurrent = true) => Abilities.PlaySequence(Caster, sequence, abortCurrent);

    /// <summary>Damages an entity as the caster (kiln.combat: a real engine hit with kill credit).</summary>
    public Task<HitResult> Damage(EntityId target, int amount, DamageOptions? options = null) =>
        Combat.Damage(target, amount, Caster, options);
}

/// <summary>The outcome of a cast request.</summary>
public sealed record CastResult(bool Ok, string? Reason, TimeSpan CooldownLeft, AbilityCast? Cast = null)
{
    public static CastResult Refused(string reason, TimeSpan cooldown = default) => new(false, reason, cooldown);
}

/// <summary>A registered ability; dispose to take it off (also when the mod unloads).</summary>
public sealed class AbilityHandle : IDisposable
{
    internal AbilityHandle(AbilityDefinition d) => Definition = d;
    public AbilityDefinition Definition { get; }
    public void Dispose() => AbilityRegistry.Remove(Definition.Id);
}

/// <summary>Client to server: cast an ability.</summary>
internal sealed record CastRequest(string Ability) : INetMessage<CastRequest>
{
    public void Encode(NetWriter w) => w.String(Ability);
    public static CastRequest Decode(ref NetReader r) => new(r.String(128));
}

/// <summary>Server to client: the outcome (cooldown left in ms).</summary>
internal sealed record CastReply(string Ability, bool Ok, string Reason, int CooldownMs) : INetMessage<CastReply>
{
    public void Encode(NetWriter w) => w.String(Ability).Bool(Ok).String(Reason).Int32(CooldownMs);
    public static CastReply Decode(ref NetReader r) => new(r.String(128), r.Bool(), r.String(256), r.Int32());
}

internal static class AbilityRegistry
{
    private sealed class Entry
    {
        public required AbilityDefinition Def;
        public required ModContext? Mod;
        public KeyBinding? Key;
        public HudLine? Hud;
        public IDisposable? Command;
        public long LocalCooldownUntil;  // client: from the server's reply
    }

    private static readonly ConcurrentDictionary<string, Entry> All = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(uint, string), long> Cooldowns = new();
    private static readonly Dictionary<uint, long> Locks = new();
    private static readonly List<(long Due, TaskCompletionSource Tcs)> Timers = new();
    private static NetChannel? _channel;
    private static IDisposable? _castCommand;
    private static bool _ticking;

    public static IReadOnlyList<AbilityDefinition> Definitions => All.Values.Select(e => e.Def).ToArray();
    public static AbilityDefinition? Get(string id) => All.TryGetValue(id, out var e) ? e.Def : null;

    public static AbilityHandle Add(AbilityDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (string.IsNullOrWhiteSpace(d.Id) || !d.Id.Contains(':')) throw new ArgumentException("ability id must be \"<modid>:<name>\"");
        var mod = ModContext.CurrentMod;
        var e = new Entry { Def = d, Mod = mod };
        if (!All.TryAdd(d.Id, e)) throw new InvalidOperationException($"ability {d.Id} is already registered");
        EnsureServices();
        if (mod is not null) AbilitiesDispatch.Watch(mod);
        // client: key binding + HUD cooldown line (needs Kiln.Ui; a dedicated server has none)
        if (d.DefaultKey is { Length: > 0 } key && Ui.Available)
        {
            e.Key = KeyBindings.Register(d.Id, key, () => RequestCast(d.Id), d.Description ?? d.Name);
            e.Hud = Hud.Show($"{d.Id}.cooldown", () => HudText(e), HudAnchor.BottomRight);
        }
        // the ability's own chat command (server)
        if (d.ChatCommand is { Length: > 0 })
        {
            try
            {
                e.Command = Commands.Register(d.ChatCommand!, ctx => CommandCast(ctx, d.Id), description: $"cast {d.Name}");
            }
            catch (Exception ex)
            {
                mod?.Log.Warn($"abilities: command /{d.ChatCommand} not registered: {ex.Message}");
            }
        }
        mod?.Log.Info($"abilities: {d.Id} registered ({d.Cost}, cooldown {d.Cooldown.TotalSeconds:F1} s{(e.Key is not null ? $", key {e.Key.Key}" : "")})");
        return new AbilityHandle(d);
    }

    public static void Remove(string id)
    {
        if (!All.TryRemove(id, out var e)) return;
        e.Key?.Dispose();
        e.Hud?.Dispose();
        e.Command?.Dispose();
    }

    public static void Retire(ModContext mod)
    {
        foreach (var kv in All)
            if (ReferenceEquals(kv.Value.Mod, mod)) Remove(kv.Key);
    }

    private static string HudText(Entry e)
    {
        var left = e.LocalCooldownUntil - Environment.TickCount64;
        return left > 0 ? $"{e.Def.Name} [{e.Key?.Key}]: {left / 1000.0:F1} s" : "";
    }

    // ---- services: channel, /cast, tick ----
    private static void EnsureServices()
    {
        if (_channel is not null) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's own channel and command, not the registering mod's
        try
        {
            if (Network.Available)
                _channel = Network.Channel("kiln:abilities", "1", required: false)
                    .Register<CastRequest>(OnRequest, NetDirection.ToServer)
                    .Register<CastReply>(OnReply, NetDirection.ToClient);
            _castCommand ??= Commands.Register("cast", ctx => CommandCast(ctx, ctx.Count > 0 ? ctx.Args[0] : ""),
                    usage: "<ability>", description: "cast a Kiln ability");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"abilities: channel/command setup: {ex.Message}");
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
        if (!_ticking)
        {
            _ticking = true;
            AbilitiesDispatch.Tick += OnTick;
            AbilitiesDispatch.Ensure();
        }
    }

    internal static void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        AbilitiesDispatch.Tick += OnTick;
    }

    private static void OnRequest(CastRequest m, NetContext ctx)
    {
        var r = CastNow(ctx.Player, m.Ability, CastOrigin.Key);
        ctx.Reply(new CastReply(m.Ability, r.Ok, r.Reason ?? "", (int)r.CooldownLeft.TotalMilliseconds));
    }

    private static void OnReply(CastReply m, NetContext ctx)
    {
        if (All.TryGetValue(m.Ability, out var e))
            e.LocalCooldownUntil = Environment.TickCount64 + Math.Max(0, m.CooldownMs);
        if (!m.Ok) Hud.Toast($"{e?.Def.Name ?? m.Ability}: {m.Reason}", ToastKind.Warning, 2.5);
    }

    internal static void RequestCast(string id)
    {
        if (_channel is null || !_channel.SendToServer(new CastRequest(id)))
            Hud.Toast(Network.ServerHasKiln ? "the server does not have this ability" : "not connected to a Kiln server",
                ToastKind.Warning, 2.5);
    }

    private static void CommandCast(CommandContext ctx, string id)
    {
        var caster = ctx.Source.Player;
        if (!ctx.Source.IsPlayer)
        {
            // the server console casts for the only player in the world (a hosted single-player game), else refuses
            var players = World.Available ? World.Find("PlayerInput") : [];
            if (players.Count != 1)
            {
                ctx.Reply("only players can cast abilities");
                return;
            }
            caster = players[0];
        }
        var r = CastNow(caster, id, CastOrigin.Command);
        ctx.Reply(r.Ok ? $"{Get(id)?.Name ?? id}: cast" : $"{Get(id)?.Name ?? id}: {r.Reason}");
    }

    // ---- casting (server, tick thread) ----
    public static CastResult CastNow(EntityId caster, string id, CastOrigin origin)
    {
        Game.AssertTickThread();
        if (!All.TryGetValue(id, out var e)) return CastResult.Refused($"unknown ability '{id}'");
        var d = e.Def;
        if (e.Mod is { Alive: false }) return CastResult.Refused("unavailable");
        if (caster.IsNone) return CastResult.Refused("no caster");
        if (d.PlayersOnly && origin != CastOrigin.Server && !caster.IsPlayer) return CastResult.Refused("players only");
        var now = Environment.TickCount64;
        if (Locks.TryGetValue(caster.Value, out var lockUntil) && lockUntil > now) return CastResult.Refused("busy casting");
        if (Cooldowns.TryGetValue((caster.Value, d.Id), out var cdUntil) && cdUntil > now)
        {
            var left = TimeSpan.FromMilliseconds(cdUntil - now);
            return CastResult.Refused($"cooldown {left.TotalSeconds:F1} s", left);
        }
        var cast = new AbilityCast(d, caster, origin, AbilitiesDispatch.Step);
        var t = World.Transform(caster);
        if (t is null) return CastResult.Refused("the caster has no position");
        cast.CasterPosition = t.Value.Position;
        cast.CasterYaw = (float)t.Value.YawDegrees;
        switch (d.Targeting)
        {
            case AbilityTargeting.Self:
                cast.Target = caster;
                cast.Position = cast.CasterPosition;
                break;
            case AbilityTargeting.Forward:
            {
                var yaw = t.Value.YawDegrees * Math.PI / 180;
                // the game's forward is +Z rotated by yaw about +Y
                cast.Position = cast.CasterPosition with
                {
                    X = cast.CasterPosition.X + Math.Sin(yaw) * d.Range,
                    Z = cast.CasterPosition.Z + Math.Cos(yaw) * d.Range,
                };
                break;
            }
            case AbilityTargeting.NearestEnemy:
            {
                EntityId best = default;
                var bestD = double.MaxValue;
                foreach (var en in World.Find("NetworkEnemy"))
                {
                    if (en == caster) continue;
                    if (World.Position(en) is not { } p) continue;
                    var dist = p.DistanceTo(cast.CasterPosition);
                    if (dist <= d.Range && dist < bestD) (best, bestD) = (en, dist);
                }
                if (best.IsNone) return CastResult.Refused("no target in range");
                cast.Target = best;
                cast.Position = World.Position(best)!.Value;
                break;
            }
        }
        if (d.CanCast is { } check)
        {
            string? why;
            try
            {
                why = RunAs(e.Mod, () => check(cast));
            }
            catch (Exception ex)
            {
                e.Mod?.Log.Error(ex, $"{d.Id}: CanCast threw");
                why = "error";
            }
            if (why is not null) return CastResult.Refused(why);
        }
        var cost = d.Cost;
        if (Events.HasSubscribers<AbilityCastEvent>())
        {
            var ev = Events.Post(new AbilityCastEvent(cast, cost));
            if (ev.Canceled) return CastResult.Refused(ev.Reason);
            cost = ev.Cost;
        }
        // check, then pay
        foreach (var (res, amount) in new[] { (AbilityResource.Mana, cost.Mana), (AbilityResource.Stamina, cost.Stamina),
                     (AbilityResource.SpecialAbility, cost.Special) })
        {
            if (amount <= 0) continue;
            var have = Abilities.Attributes.Get(caster, res);
            if (have is null) return CastResult.Refused($"the caster has no {res}");
            if (have < amount) return CastResult.Refused($"not enough {res.ToString().ToLowerInvariant()} ({have}/{amount})");
        }
        if (cost.Mana > 0) Abilities.Attributes.Add(caster, AbilityResource.Mana, -cost.Mana);
        if (cost.Stamina > 0) Abilities.Attributes.Add(caster, AbilityResource.Stamina, -cost.Stamina);
        if (cost.Special > 0) Abilities.Attributes.Add(caster, AbilityResource.SpecialAbility, -cost.Special);
        if (d.Cooldown > TimeSpan.Zero) Cooldowns[(caster.Value, d.Id)] = now + (long)d.Cooldown.TotalMilliseconds;
        if (d.CastTime > TimeSpan.Zero) Locks[caster.Value] = now + (long)d.CastTime.TotalMilliseconds;
        if (d.CastSequence is { } seq)
        {
            try
            {
                if (!Abilities.PlaySequence(caster, seq)) e.Mod?.Log.Warn($"{d.Id}: cast sequence {seq} not played (no ActorInput or unknown sequence)");
            }
            catch (Exception ex)
            {
                e.Mod?.Log.Warn($"{d.Id}: cast sequence: {ex.Message}");
            }
        }
        if (Events.HasSubscribers<AbilityCastedEvent>()) Events.Post(new AbilityCastedEvent(cast, cost));
        // run the effect as the ability's mod (its scheduler resumes awaits on the tick thread)
        RunAs<object?>(e.Mod, () =>
        {
            Task task;
            try
            {
                task = d.OnCast(cast);
            }
            catch (Exception ex)
            {
                e.Mod?.Log.Error(ex, $"{d.Id}: OnCast threw");
                return null;
            }
            task.ContinueWith(tk => e.Mod?.Log.Error(tk.Exception!, $"{d.Id}: OnCast failed"),
                TaskContinuationOptions.OnlyOnFaulted);
            return null;
        });
        return new CastResult(true, null, d.Cooldown, cast);
    }

    public static TimeSpan CooldownLeft(EntityId caster, string id)
    {
        if (!Cooldowns.TryGetValue((caster.Value, id), out var until)) return TimeSpan.Zero;
        var left = until - Environment.TickCount64;
        return left > 0 ? TimeSpan.FromMilliseconds(left) : TimeSpan.Zero;
    }

    public static void ResetCooldown(EntityId caster, string id) => Cooldowns.Remove((caster.Value, id));

    private static T RunAs<T>(ModContext? mod, Func<T> f)
    {
        var outer = ModContext.CurrentMod;
        var outerSync = SynchronizationContext.Current;
        ModContext.CurrentMod = mod;
        if (mod is not null) SynchronizationContext.SetSynchronizationContext(mod.Scheduler.SynchronizationContext);
        try
        {
            return f();
        }
        finally
        {
            ModContext.CurrentMod = outer;
            SynchronizationContext.SetSynchronizationContext(outerSync);
        }
    }

    // ---- timers and the per-step work ----
    public static Task Timer(long dueMs)
    {
        if (dueMs <= Environment.TickCount64) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.None);  // continuations run inline on the tick thread
        lock (Timers) Timers.Add((dueMs, tcs));
        return tcs.Task;
    }

    private static void OnTick(ulong step)
    {
        var now = Environment.TickCount64;
        List<TaskCompletionSource>? due = null;
        lock (Timers)
            for (var i = Timers.Count - 1; i >= 0; i--)
                if (Timers[i].Due <= now)
                {
                    (due ??= new()).Add(Timers[i].Tcs);
                    Timers.RemoveAt(i);
                }
        if (due is not null)
            foreach (var t in due) t.TrySetResult();
        if (step % 250 == 0)
        {
            foreach (var k in Cooldowns.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) Cooldowns.Remove(k);
            foreach (var k in Locks.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) Locks.Remove(k);
        }
    }
}

public static partial class Abilities
{
    /// <summary>Registers a scripted ability (see <see cref="AbilityDefinition"/>). On the server it can be cast by
    /// key (Kiln clients with the mod), by chat command and by <see cref="Cast"/>; on a client it binds its key.
    /// Ends when the mod unloads.</summary>
    public static AbilityHandle Register(AbilityDefinition definition) => AbilityRegistry.Add(definition);

    /// <summary>Every registered ability.</summary>
    public static IReadOnlyList<AbilityDefinition> Registered => AbilityRegistry.Definitions;

    /// <summary>Casts an ability for <paramref name="caster"/> (a player or any actor with the attributes it costs):
    /// the same checks as a key press (cooldown, cast lock, cost, targeting). Server, tick thread.</summary>
    public static CastResult Cast(EntityId caster, string abilityId, CastOrigin origin = CastOrigin.Server) =>
        AbilityRegistry.CastNow(caster, abilityId, origin);

    public static TimeSpan CooldownLeft(EntityId caster, string abilityId) => AbilityRegistry.CooldownLeft(caster, abilityId);
    public static void ResetCooldown(EntityId caster, string abilityId) => AbilityRegistry.ResetCooldown(caster, abilityId);
}
