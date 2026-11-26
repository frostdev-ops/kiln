// SPDX-License-Identifier: MIT
// Package 5 (19d §4.4-4.6, gates 3-5, managed half): the mirrors of kiln.pets' native records; the target rule for orders
// (the definition's own hostile factions and PvP mode, the mod's declared pairs: every row of the matrix); a down the native
// guard latched retires the body before PetDownedEvent; a lost protection retires every body with PetProtectionLostEvent
// first; declared pairs reach the world. The native rule itself is native/services/creature_control/tests/pets_test.cpp.
using System.Runtime.InteropServices;
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

public sealed unsafe class SafetyTests : IDisposable
{
    private readonly RosterTests _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void The_mirrors_keep_the_native_layouts()
    {
        Assert.Equal(96, sizeof(PetsNative.Binding));
        Assert.Equal(80, sizeof(PetsNative.State));
        Assert.Equal(56, sizeof(PetsNative.HitQuery));
        Assert.Equal(24, sizeof(PetsNative.CreatureControlNativeKey));
        Assert.Equal(8, (int)Marshal.OffsetOf<PetsNative.Binding>(nameof(PetsNative.Binding.Body)));
        Assert.Equal(32, (int)Marshal.OffsetOf<PetsNative.Binding>(nameof(PetsNative.Binding.Pet)));
        Assert.Equal(48, (int)Marshal.OffsetOf<PetsNative.Binding>(nameof(PetsNative.Binding.Owner)));
        Assert.Equal(80, (int)Marshal.OffsetOf<PetsNative.Binding>(nameof(PetsNative.Binding.OwnerEntity)));
        Assert.Equal(84, (int)Marshal.OffsetOf<PetsNative.Binding>(nameof(PetsNative.Binding.HostileFactions)));
        Assert.Equal(48, (int)Marshal.OffsetOf<PetsNative.State>(nameof(PetsNative.State.DirtySequence)));
        Assert.Equal(72, (int)Marshal.OffsetOf<PetsNative.State>(nameof(PetsNative.State.DeniedHits)));
        Assert.Equal(28, (int)Marshal.OffsetOf<PetsNative.HitQuery>(nameof(PetsNative.HitQuery.HealthChange)));
        Assert.Equal(48, (int)Marshal.OffsetOf<PetsNative.HitQuery>(nameof(PetsNative.HitQuery.RootFaction)));
        Assert.Equal(8 + 11 * IntPtr.Size, sizeof(PetsNative.Table));
    }

    private (PetSnapshot Pet, EntityRef Target) Attacker(PetTargetInfo info, uint target = 5000)
    {
        var p = _t.Summoned(_t.A);
        _t.World.Targets[target] = info;
        return (p, new EntityRef(new EntityId(target), 1));
    }

    private PetResult Attack(PetSnapshot p, EntityRef target) =>
        _t.Run(_t.Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.Command, OperationId = Guid.NewGuid(), OwnerKey = _t.A, Who = _t.Who(_t.A), Pet = p.Id, Order = PetOrder.Attack, Target = target, Payload = "a" + target }));

    [Fact]
    public void A_hostile_unowned_enemy_may_be_attacked()
    {
        var (p, target) = Attacker(new PetTargetInfo(true, true, false, CharacterKey.None, Faction.Vukah, true));
        var r = Attack(p, target);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(PetOrder.Attack, r.Pet!.RequestedOrder);
    }

    [Theory]
    [InlineData(Faction.PlayerAlly, true, "PlayerAlly")]
    [InlineData(Faction.FarmAnimal, true, "FarmAnimal")]
    [InlineData(Faction.Wildlife, true, "not a faction")]
    [InlineData(Faction.Vukah, false, "not an unowned enemy")]
    public void Friendly_or_undeclared_creatures_are_protected_and_no_lease_is_asked(Faction faction, bool enemy, string why)
    {
        var (p, target) = Attacker(new PetTargetInfo(true, true, false, CharacterKey.None, faction, enemy));
        var r = Attack(p, target);
        Assert.False(r.Ok);
        Assert.StartsWith("TargetProtected", r.Reason);
        Assert.Contains(why, r.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(_t.World.Orders, o => o.Order == PetOrder.Attack);
    }

    [Fact]
    public void Every_player_is_protected_unless_the_mod_declared_a_pair_for_a_by_mod_rule_pet()
    {
        var (p, target) = Attacker(new PetTargetInfo(true, true, true, _t.B, Faction.Player, false), 7);
        Assert.StartsWith("TargetProtected", Attack(p, target).Reason);   // PvP off (the default)
        _t.Roster.SetHostile(_t.A, _t.B, true);
        Assert.StartsWith("TargetProtected", Attack(p, target).Reason);   // a pair, but this pet's PvP is off
        Assert.True(_t.Roster.IsHostile(_t.B, _t.A));
    }

    [Fact]
    public void A_by_mod_rule_pet_may_fight_a_declared_player_and_that_players_by_mod_rule_pet()
    {
        Pets.DefineAs(new PetDefinition { Id = "mod-a:duelist", Creature = "game:enemy_wildbeast_wolf", Targets = new PetTargetPolicy { Pvp = PetPvpMode.ByModRule } }, "mod-a");
        _t.World.Join(_t.A, new WorldPosition(10, 0, 10));
        _t.World.Join(_t.B, new WorldPosition(20, 0, 10));
        var mine = _t.Grant(_t.A, "mod-a:duelist");
        var theirs = _t.Grant(_t.B, "mod-a:duelist");
        Assert.True(_t.Do(PetRoster.Kind.Summon, _t.A, mine.Id).Ok);
        Assert.True(_t.Do(PetRoster.Kind.Summon, _t.B, theirs.Id).Ok);
        var bPlayer = _t.World.Online[_t.B].Entity;
        _t.World.Targets[bPlayer.Id.Value] = new PetTargetInfo(true, true, true, _t.B, Faction.Player, false);
        var theirBody = _t.Roster.Get(theirs.Id)!.Body;
        _t.World.Targets[theirBody.Id.Value] = new PetTargetInfo(true, true, false, CharacterKey.None, Faction.PlayerAlly, true);
        var minePet = _t.Roster.Get(mine.Id)!;
        Assert.StartsWith("TargetProtected", Attack(minePet, bPlayer).Reason);
        Assert.StartsWith("TargetProtected", Attack(minePet, theirBody).Reason);
        _t.Roster.SetHostile(_t.A, _t.B, true);
        Assert.True(Attack(minePet, bPlayer).Ok);
        Assert.True(Attack(_t.Roster.Get(mine.Id)!, theirBody).Ok);
        _t.Roster.SetHostile(_t.B, _t.A, false);
        Assert.False(_t.Roster.IsHostile(_t.A, _t.B));
    }

    [Fact]
    public void The_same_owners_pets_never_fight_each_other()
    {
        Pets.DefineAs(new PetDefinition { Id = "mod-a:pack", Creature = "game:enemy_wildbeast_wolf", Caps = new PetCaps { LivePerCharacter = 2 } }, "mod-a");
        _t.World.Join(_t.A, new WorldPosition(10, 0, 10));
        var one = _t.Grant(_t.A, "mod-a:pack");
        var two = _t.Grant(_t.A, "mod-a:pack");
        Assert.True(_t.Do(PetRoster.Kind.Summon, _t.A, one.Id).Ok);
        Assert.True(_t.Do(PetRoster.Kind.Summon, _t.A, two.Id).Ok);
        var twoBody = _t.Roster.Get(two.Id)!.Body;
        _t.World.Targets[twoBody.Id.Value] = new PetTargetInfo(true, true, false, CharacterKey.None, Faction.PlayerAlly, true);
        Assert.Contains("same owner", Attack(_t.Roster.Get(one.Id)!, twoBody).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gone_target_is_refused()
    {
        var (p, target) = Attacker(new PetTargetInfo(false, false, false, CharacterKey.None, Faction.Vukah, true));
        Assert.StartsWith("TargetGone", Attack(p, target).Reason);
    }

    [Fact]
    public void A_latched_down_retires_the_body_before_the_downed_event()
    {
        var p = _t.Summoned(_t.A);
        _t.BodyOf(p.Id).DownPending = true;   // the native guard caught a lethal hit; the body still stands, health 100
        _t.Step();
        var d = _t.Roster.Get(p.Id)!;
        Assert.Equal(PetLife.Downed, d.Life);
        Assert.Empty(_t.World.Bodies);
        Assert.Equal(["TransitionChanged", "BodyRetired", "Downed"], _t.Names());
        Assert.Contains("NativeLethal", _t.World.Log[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_protection_retires_the_body_with_its_event_first()
    {
        var p = _t.Summoned(_t.A);
        _t.BodyOf(p.Id).ProtectionLost = true;
        _t.Step();
        Assert.Empty(_t.World.Bodies);
        Assert.Equal(["TransitionChanged", "ProtectionLost", "BodyRetired", "Downed"], _t.Names());
        Assert.Contains("ProtectionLost", _t.World.Log[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_whose_retirement_fails_stays_protected_and_is_retried()
    {
        var p = _t.Summoned(_t.A);
        _t.World.RetireRefuses = true;
        _t.BodyOf(p.Id).DownPending = true;
        _t.Step(5);
        var d = _t.Roster.Get(p.Id)!;
        Assert.Equal(PetTransition.Downing, d.Transition);   // never resumed as alive, never published down while it stands
        Assert.Single(_t.World.Bodies);
        Assert.DoesNotContain("Downed", _t.Names());
        _t.World.RetireRefuses = false;
        _t.Step();
        Assert.Equal(PetLife.Downed, _t.Roster.Get(p.Id)!.Life);
        Assert.Contains("Downed", _t.Names());
    }

    [Fact]
    public void Declared_pairs_reach_the_world_when_they_or_the_owners_change()
    {
        _t.World.Join(_t.A);
        _t.Step();
        var before = _t.World.HostilitySyncs.Count;
        _t.Roster.SetHostile(_t.A, _t.B, true);
        _t.Step();
        Assert.Equal(before + 1, _t.World.HostilitySyncs.Count);
        Assert.Single(_t.World.HostilitySyncs[^1]);
        _t.Step();
        Assert.Equal(before + 1, _t.World.HostilitySyncs.Count);   // nothing changed: nothing sent
        _t.World.Join(_t.B);
        _t.Step();
        Assert.Equal(before + 2, _t.World.HostilitySyncs.Count);   // an owner came: the pairs go again with its entity
    }
}
