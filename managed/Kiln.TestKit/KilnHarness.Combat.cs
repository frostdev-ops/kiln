// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessCombat? _combat;

    /// <summary>The fake kiln.combat state of this harness (null before the constructor set it up).</summary>
    internal CombatFake? CombatFake { get; private set; }

    /// <summary>Health, hits and deaths in this server's fake world as the kit's fake of kiln.combat keeps them: the test
    /// gives entities health, makes "the game" deal a hit, and reads every hit back.</summary>
    public HarnessCombat Combat => _combat ??= new HarnessCombat(this);

    private void StartCombat() => CombatFake = new CombatFake(this);
}

/// <summary>Where a hit is (<see cref="FakeHit.State"/>), as kiln.combat reports it.</summary>
public enum FakeHitState
{
    /// <summary>Waits for the next hit conclusion (the start of the next step).</summary>
    Queued = 0,
    /// <summary>Held for the handlers of <c>EntityHurtEvent</c> / <c>EntityHealEvent</c> this step.</summary>
    Held = 1,
    /// <summary>Applied: the health changed.</summary>
    Applied = 2,
    /// <summary>Not applied: canceled by a handler, the target has no health or is gone, or god mode.</summary>
    Dropped = 3,
}

/// <summary>An entity's health in the fake kiln.combat (<see cref="HarnessCombat.Health"/>).</summary>
public sealed class FakeHealth
{
    internal FakeHealth(int current, int max, bool dead)
    {
        Current = current;
        Max = max;
        Dead = dead;
    }

    /// <summary>Health points now.</summary>
    public int Current { get; }

    /// <summary>The maximum.</summary>
    public int Max { get; }

    /// <summary>Dead: its health fell below 1 (and it was not healed or respawned since).</summary>
    public bool Dead { get; }

    /// <summary>The health, for a log line.</summary>
    public override string ToString() => $"{Current}/{Max}{(Dead ? " (dead)" : "")}";
}

/// <summary>One hit, from a mod (<c>Combat.Damage</c>, <c>Kill</c>) or from the game (<see cref="HarnessCombat.Hit"/>), as it
/// went (<see cref="HarnessCombat.Hits"/>).</summary>
public sealed class FakeHit
{
    private readonly CombatFake.Hit _hit;

    internal FakeHit(CombatFake.Hit hit) => _hit = hit;

    /// <summary>kiln.combat's id of the hit.</summary>
    public ulong Id => _hit.Id;

    /// <summary>The step during which it was dealt or asked for.</summary>
    public ulong Step => _hit.Requested;

    /// <summary>The mod that asked; null for the game's hits (and a call outside any mod).</summary>
    public string? ModId => _hit.Mod;

    /// <summary>A hit of the game (the test's <see cref="HarnessCombat.Hit"/>); false for a mod's.</summary>
    public bool FromGame => _hit.Origin == CombatAbi.OriginGame;

    /// <summary>The target.</summary>
    public EntityId Target => new(_hit.Target);

    /// <summary>The attacker (none when unknown).</summary>
    public EntityId Attacker => new(_hit.Attacker);

    /// <summary>The damage (negative: a healing hit), as the handlers left it.</summary>
    public int Amount => _hit.Amount;

    /// <summary>Where it is.</summary>
    public FakeHitState State => (FakeHitState)_hit.State;

    /// <summary>The target's health before and after, once applied (-1 before).</summary>
    public (int Before, int After) Health => (_hit.Before, _hit.After);

    /// <summary>It took the target from above 0 to 0.</summary>
    public bool KillingBlow => _hit.Killing;

    /// <summary>The hit, for a log line.</summary>
    public override string ToString() => $"hit {Id} step {Step} {(FromGame ? "game" : ModId ?? "kiln")}: {Amount} on {Target}: {State}{(KillingBlow ? " (killing blow)" : "")}";
}

/// <summary>
/// The fake kiln.combat of a harness (<see cref="KilnHarness.Combat"/>): Kiln.Combat's own code (<c>Combat.GetHealth</c>,
/// <c>SetHealth</c>, <c>Heal</c>, <c>Damage</c>, <c>Kill</c>, god mode, <c>EntityHurtEvent</c>, <c>EntityHealEvent</c>,
/// <c>EntityDamagedEvent</c>, <c>EntityDeathEvent</c>, <c>PlayerRespawnEvent</c>) runs on it.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> Health is what the test gives an entity of the fake world or a fake player
/// (<see cref="SetHealth"/>): the kit does not know the game's values. A hit, a mod's or the game's
/// (<see cref="Hit"/>), applies at the start of the next step, as the engine concludes hits in the step after they were
/// dealt; while a mod has a handler for <c>EntityHurtEvent</c> or <c>EntityHealEvent</c>, the game's hits are held one
/// step for it (the handler can change the amount or cancel, as on a server) and apply at the start of the step after.
/// The damage and the death (health below 1, the killing blow's attacker) reach mods at the next step: a death is what the
/// game's death check finds then, so an entity healed back before it lives. <c>SetHealth</c> to 0 kills without a
/// killer; god mode drops damage hits and keeps 1 health on a set; <c>Heal</c> adds up to the maximum. A mod that unloads
/// takes its hits not yet concluded along (a hit already held for the handlers still applies). A player's health is a
/// component of that player's entity: the next player in the slot has none until the test gives it.</para>
/// <para><b>Not modelled</b> (offline): the game's attributes (<c>combat.attributes</c>), the hold filter, fall damage,
/// regeneration, attribute heals, knockback, loot drops, the game's own damage formulas, armour, hit reactions and kill
/// credit or XP.</para>
/// </remarks>
public sealed class HarnessCombat
{
    private readonly KilnHarness _harness;

    internal HarnessCombat(KilnHarness harness) => _harness = harness;

    private CombatFake Fake => _harness.CombatFake ?? throw new InvalidOperationException("the harness has no combat yet");

    /// <summary>Gives an entity health, as the game's Health component holds it (no event).</summary>
    /// <exception cref="ArgumentException">No such entity, a maximum below 1, or a current value below 0.</exception>
    public void SetHealth(EntityId entity, int current, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        _harness.Run(() => Fake.Give(entity.Value, current, max));
    }

    /// <summary>The entity's health; null when it has none.</summary>
    public FakeHealth? Health(EntityId entity) =>
        _harness.Run(() => Fake.HealthOf(entity.Value)) is { } hp ? new FakeHealth(hp.Current, hp.Max, hp.Dead) : null;

    /// <summary>The game deals a hit: <paramref name="amount"/> damage (negative: a healing hit) on
    /// <paramref name="target"/>. Mods' <c>EntityHurtEvent</c> / <c>EntityHealEvent</c> handlers see it at the next step
    /// and may change or cancel it; it applies at the start of the step after (the next step when no handler holds hits).
    /// Returns the hit's id (<see cref="Hits"/>).</summary>
    /// <exception cref="ArgumentException">An amount of 0.</exception>
    public ulong Hit(EntityId target, int amount, EntityId attacker = default, EntityId source = default)
    {
        if (amount == 0) throw new ArgumentException("a hit of 0 changes nothing", nameof(amount));
        return _harness.Run(() => Fake.GameHit(target.Value, amount, attacker.Value, source.Value));
    }

    /// <summary>A dead player (or creature) is alive again with <paramref name="health"/>: <c>PlayerRespawnEvent</c> at the
    /// next step.</summary>
    /// <exception cref="ArgumentException">The entity has no health.</exception>
    /// <exception cref="InvalidOperationException">It is not dead.</exception>
    public void Respawn(EntityId entity, int health) => _harness.Run(() => Fake.Respawn(entity.Value, health));

    /// <summary>Every hit, mods' and the game's, in the order they were dealt.</summary>
    public IReadOnlyList<FakeHit> Hits => _harness.Run(() => (IReadOnlyList<FakeHit>)[.. Fake.Hits.Select(h => new FakeHit(h))]);
}
