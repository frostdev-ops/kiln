// SPDX-License-Identifier: MIT
// Kiln.Taming in a game: the attempt engine (TamingEngine) over the running world (owners from kiln.players, targets from
// kiln.creatures and the game's own taming, NativeTaming), its costs through kiln.item_costs (ItemCosts) and its tames
// bound to Kiln.Pets records (PetsTamingBinder). Kiln translates a target's template to its creatures registry id here and
// nowhere else; a mod names creatures only by those ids.
namespace Kiln;

/// <summary>Creatures registry ids of a template (docs/kiln/modding/registries/creatures.md): <c>game:&lt;name in lower
/// case&gt;</c>, <c>game:&lt;name&gt;~&lt;first 8 hex digits&gt;</c> for a name two templates share, <c>game:template_&lt;8
/// hex&gt;</c> for a template with no name.</summary>
internal static class TamingCreatureIds
{
    internal static string IdOf(Guid template, string? name, IEnumerable<(Guid Template, string Name)> all)
    {
        var hex = template.ToString("N")[..8];
        if (string.IsNullOrEmpty(name)) return $"game:template_{hex}";
        var shared = all.Count(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) > 1;
        return shared ? $"game:{name.ToLowerInvariant()}~{hex}" : $"game:{name.ToLowerInvariant()}";
    }
}

/// <summary>The engine's world in a game.</summary>
internal sealed class GameTamingWorld : ITamingWorld
{
    private static double _seconds;

    public ulong WorldEpoch => Lifecycle.IsServerRunning ? (ulong)Lifecycle.ServerSession : 0;

    /// <summary>Seconds of the game's own 60 Hz world clock, as Kiln.Pets and kiln.creature_control count them: a step adds
    /// what the game's clock advanced in it (2 to 4 of its ticks on a dedicated server, which steps 15 to 25 times a
    /// second, never 60). A paused world's clock stands still. One clock for every GameTamingWorld; it holds between
    /// steps.</summary>
    public double Now => Volatile.Read(ref _seconds);

    /// <summary>Kiln.Taming's service tick, before the engine's (tick thread).</summary>
    internal static void Advance(in TickInfo tick) => Volatile.Write(ref _seconds, _seconds + tick.ClockDelta / 60.0);

    public TamingOwner? Owner(PlayerRef player, out string? reason)
    {
        reason = null;
        Player? p = null;
        try { p = !player.Entity.IsNone ? Players.Find(player.Entity.Id) : Players.FindPerson(player); }
        catch (NotSupportedException) { /* no kiln.players */ }
        if (p is not { InGame: true })
        {
            reason = "OwnerOffline: the player is not in game";
            return null;
        }
        EntityRef entity;
        try { entity = EntityRef.Of(p.Entity); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { entity = new EntityRef(p.Entity, 0); }
        if (entity.IsNone) entity = new EntityRef(p.Entity, 0);
        return new TamingOwner(player, entity, ((ulong)p.Handle << 32) ^ p.JoinedStep, p.Position ?? default);
    }

    public TamingTarget? Target(EntityRef target)
    {
        try
        {
            if (target.IsNone || !target.IsAlive) return null;
            var info = Creatures.Available ? Creatures.Of(target.Id).Inspect() : null;
            if (info is null || World.Position(target.Id) is not { } at) return null;
            var dead = info.Dead || info.State == EnemyState.Dead || info.HealthMax > 0 && info.Health <= 0;
            var health = info.HealthMax > 0 ? Math.Clamp((double)info.Health / info.HealthMax, 0, 1) : 1;
            var key = TamingCreatureIds.IdOf(info.Template, info.TemplateName, Creatures.Templates.Select(t => (t.Id, t.Name)));
            var native = NativeTamingRuntime.Available() ? NativeTamingRuntime.Read(target.Id) : null;
            return new TamingTarget(true, dead, at, health, key, native?.Progress ?? 0, native is { Tameable: true, Phase: not NativeTamingPhase.Tamed })
            {
                Template = info.Template,
            };
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The engine's costs in a game: kiln.item_costs, submitted as Kiln's own (the engine consumes every receipt
/// itself, so a cost settles even when the mod whose request it pays unloads meanwhile; its events still name that mod).</summary>
internal sealed class GameTamingCosts : ITamingCosts
{
    public Task<CostReceipt> Take(ItemSelection selection, int count, Guid requestId) => ItemCosts.AsService(() => ItemCosts.Take(selection, count, requestId));
    public Task<CostReceipt> Wear(ItemSelection selection, int wear, Guid requestId, bool allowBreak) =>
        ItemCosts.AsService(() => ItemCosts.Wear(selection, wear, requestId, allowBreak));
    public Task<CostReceipt> Refund(CostReceipt paid, Guid requestId) => ItemCosts.AsService(() => ItemCosts.Refund(paid, requestId));
    public IReadOnlyList<InventorySlot> Slots(EntityRef owner) => Items.Read(owner.Id)?.Slots ?? [];
}

/// <summary>The one engine of the running process, its tick and the mods' definitions.</summary>
internal static class TamingRuntime
{
    private static TamingEngine? _engine;
    private static readonly HashSet<string> Watched = new(StringComparer.Ordinal);

    internal static TamingEngine Engine
    {
        get
        {
            if (_engine is not null) return _engine;
            var e = new TamingEngine(new GameTamingWorld(), new GameTamingCosts());
            e.Binder = new PetsTamingBinder();
            e.ResolveCreature = id => PetCreatures.Resolve(id, out _)?.Template;
            // Taming.Definition reads on any thread: the first engine made is the one (never two)
            return Interlocked.CompareExchange(ref _engine, e, null) ?? e;
        }
        set => _engine = value;
    }

    /// <summary>The engine if one was made (never makes one).</summary>
    internal static TamingEngine? Current => Volatile.Read(ref _engine);

    /// <summary>A mod's definition; its definitions go when it unloads (their running attempts fail DefinitionGone).</summary>
    internal static TamingDefinitionResult Define(TamingDefinition d, string mod)
    {
        var r = Engine.Define(d, mod);
        if (ModContext.CurrentMod is { } ctx && ctx.Info.Id == mod && Watched.Add(mod))
            ctx.Retiring += () =>
            {
                Watched.Remove(mod);
                _engine?.RemoveMod(mod);
            };
        return r;
    }

    /// <summary>The service tick (every step).</summary>
    internal static void Tick(ulong step)
    {
        if (_engine is { } e && Lifecycle.IsServerRunning) e.Tick();
        NativeTamingRuntime.Tick(step);
    }

    /// <summary>For tests: a fresh engine on the next use.</summary>
    internal static void Reset()
    {
        _engine = null;
        Watched.Clear();
    }
}
