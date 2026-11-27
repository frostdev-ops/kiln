// SPDX-License-Identifier: MIT
// Package 1 (19d §5.1): pet definitions. Ranges are refused by field; the creature is a Kiln id Kiln translates (the
// creatures registry's id rules), never a game name; the same definition twice is one; a definition cannot change while its
// pets have bodies; the defaults are the safe ones and every one is the mod's to change.
using Kiln;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Kiln.PetsTests;

internal sealed class FakeCreatures : IPetCreatureSource
{
    public bool Ready { get; set; } = true;
    public List<(Guid Template, string Name, CreatureKind Kind, int LevelMax, bool Boss)> Templates { get; } =
    [
        (new Guid("11111111-0000-0000-0000-000000000001"), "Enemy_WildBeast_Wolf", CreatureKind.Enemy, 25, false),
        (new Guid("22222222-0000-0000-0000-000000000002"), "Enemy_Vukah_Melee", CreatureKind.Enemy, 60, false),
        (new Guid("33333333-0000-0000-0000-000000000003"), "Animal_Dog", CreatureKind.Animal, 10, false),
        (new Guid("44444444-0000-0000-0000-000000000004"), "Enemy_Twin", CreatureKind.Enemy, 10, false),
        (new Guid("55555555-0000-0000-0000-000000000005"), "Enemy_Twin", CreatureKind.Enemy, 10, false),
        (new Guid("66666666-0000-0000-0000-000000000006"), "", CreatureKind.Enemy, 10, false),
        (new Guid("77777777-0000-0000-0000-000000000007"), "Boss_Fell", CreatureKind.Enemy, 30, true),
    ];

    public PetCreatureInfo? Resolve(string id, out string? reason)
    {
        var (g, why) = PetCreatures.Match(id, Templates.Select(t => (t.Template, t.Name)));
        reason = why;
        if (why is not null) return null;
        var t = Templates.First(x => x.Template == g);
        var problem = t.Kind != CreatureKind.Enemy ? "CreatureUnsupported: not an enemy" : t.Boss ? "CreatureUnsupported: a boss" : null;
        return new PetCreatureInfo(id, g, null, t.LevelMax, t.Name, problem);
    }
}

public sealed class DefinitionTests : IDisposable
{
    private readonly FakeCreatures _creatures = new();

    public DefinitionTests()
    {
        PetsRuntime.ResetForTests();
        PetCreatures.ResetForTests();
        PetCreatures.Source = _creatures;
    }

    public void Dispose()
    {
        PetsRuntime.ResetForTests();
        PetCreatures.ResetForTests();
    }

    private static PetDefinition Wolf(string id = "mod-a:wolf") => new() { Id = id, Creature = "game:enemy_wildbeast_wolf" };

    [Fact]
    public void A_definition_registers_and_reads_back()
    {
        var r = Pets.DefineAs(Wolf(), "mod-a");
        Assert.True(r.Ok, r.Reason);
        Assert.Same(r.Definition, Pets.Definition("mod-a:wolf"));
        Assert.Contains(Pets.Definitions, d => d.Id == "mod-a:wolf");
    }

    [Fact]
    public void The_defaults_protect_players_and_leave_a_bodyless_anchor()
    {
        var d = Wolf();
        Assert.Equal(PetPvpMode.Off, d.Targets.Pvp);
        Assert.Null(d.Targets.HostileFactions);
        Assert.Equal(PetDownedOutcome.RecoveryAnchor, d.Downed.Outcome);
        Assert.Equal(PetMovementMode.QueryInput, d.Movement.Mode);
        Assert.True(d.Revive.Allowed);
        Assert.Null(d.Revive.Cost);
        Assert.Equal(1, d.Limits.LivePerCharacter);
        Assert.Equal(32, d.Limits.RetainedPerCharacter);
        Assert.Equal(16, d.Limits.LivePerWorld);
        // the hostile list never names players or their allies
        Assert.DoesNotContain(Faction.Player, PetTargetPolicy.DefaultHostileFactions);
        Assert.DoesNotContain(Faction.PlayerAlly, PetTargetPolicy.DefaultHostileFactions);
        Assert.Equal(8, PetTargetPolicy.DefaultHostileFactions.Count);
    }

    [Fact]
    public void The_same_definition_again_is_one_and_posts_nothing_new()
    {
        Assert.True(Pets.DefineAs(Wolf(), "mod-a").Ok);
        PetsRuntime.DrainEventsForTests(out var first);
        Assert.Single(first);
        Assert.True(Pets.DefineAs(Wolf() with { Targets = new PetTargetPolicy() }, "mod-a").Ok);
        PetsRuntime.DrainEventsForTests(out var second);
        Assert.Empty(second);
    }

    [Fact]
    public void Another_definition_replaces_it_unless_its_pets_are_in_use()
    {
        Assert.True(Pets.DefineAs(Wolf(), "mod-a").Ok);
        var changed = Wolf() with { Name = "Rex" };
        Assert.True(Pets.DefineAs(changed, "mod-a").Ok);
        Assert.Equal("Rex", Pets.Definition("mod-a:wolf")!.Name);
        PetsRuntime.InUse = id => id == "mod-a:wolf";
        var refused = Pets.DefineAs(Wolf() with { Name = "Max" }, "mod-a");
        Assert.False(refused.Ok);
        Assert.StartsWith("DefinitionInUse", refused.Reason, StringComparison.Ordinal);
        Assert.Equal("Rex", Pets.Definition("mod-a:wolf")!.Name);
    }

    [Theory]
    [InlineData("game:enemy_nowhere", "CreatureUnknown")]
    [InlineData("game:enemy_twin", "CreatureAmbiguous")]
    [InlineData("game:animal_dog", "CreatureUnsupported")]
    [InlineData("game:boss_fell", "CreatureUnsupported")]
    [InlineData("other-mod:wolf", "CreatureUnknown")]
    public void A_creature_Kiln_cannot_make_a_pet_of_is_refused_with_its_reason(string creature, string reason)
    {
        var r = Pets.DefineAs(Wolf() with { Creature = creature }, "mod-a");
        Assert.False(r.Ok);
        Assert.StartsWith(reason, r.Reason, StringComparison.Ordinal);
        Assert.Null(Pets.Definition("mod-a:wolf"));
    }

    [Theory]
    [InlineData("game:enemy_twin~44444444")]
    [InlineData("game:enemy_twin~55555555")]
    [InlineData("game:template_66666666")]
    [InlineData("game:enemy_vukah_melee")]
    public void The_registry_id_rules_name_one_template(string creature)
    {
        var r = Pets.DefineAs(Wolf() with { Creature = creature }, "mod-a");
        Assert.True(r.Ok, r.Reason);
    }

    [Fact]
    public void A_creature_block_registered_by_Kiln_Blocks_is_a_creature()
    {
        PetCreatures.RegisterBlock("mod-b:wolf", "mod-b:wolf", Guid.NewGuid(), "mod-b.wolf", 0, "Wolf");
        Assert.True(Pets.DefineAs(Wolf("mod-b:pet") with { Creature = "mod-b:wolf" }, "mod-b").Ok);
    }

    [Fact]
    public void Before_the_games_creatures_are_readable_a_definition_waits_for_its_first_body()
    {
        _creatures.Ready = false;
        Assert.True(Pets.DefineAs(Wolf() with { Creature = "game:enemy_nowhere" }, "mod-a").Ok);
    }

    [Fact]
    public void The_level_is_bounded_by_the_creatures_highest_level()
    {
        var e = Assert.Throws<ArgumentException>(() => Pets.DefineAs(Wolf() with { Level = new PetLevelPolicy { Level = 26 } }, "mod-a"));
        Assert.Contains("level.level", e.Message, StringComparison.Ordinal);
        Assert.True(Pets.DefineAs(Wolf() with { Level = new PetLevelPolicy { Level = 25 } }, "mod-a").Ok);
    }

    [Theory]
    [InlineData("Mod-A:wolf")]
    [InlineData("wolf")]
    [InlineData("a:b:c")]
    [InlineData("mod a:wolf")]
    public void An_id_that_is_no_Kiln_id_is_refused(string id)
    {
        var e = Assert.Throws<ArgumentException>(() => Pets.DefineAs(Wolf(id), "mod-a"));
        Assert.Contains("id", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cost_must_name_an_item_and_a_count()
    {
        Assert.Throws<ArgumentException>(() => Pets.DefineAs(Wolf() with { Revive = new PetRevivePolicy { Cost = new PetCostDefinition(new ItemId(0), 1) } }, "mod-a"));
        Assert.Throws<ArgumentException>(() => Pets.DefineAs(Wolf() with { Revive = new PetRevivePolicy { Cost = new PetCostDefinition(new ItemId(5), 1000) } }, "mod-a"));
        Assert.True(Pets.DefineAs(Wolf() with { Revive = new PetRevivePolicy { Cost = new PetCostDefinition(new ItemId(5), 3) } }, "mod-a").Ok);
    }

    // review of dev.32 pets: Define took FarmAnimal as a hostile faction while kiln.pets' native bind refuses a mask with
    // the players' side or farm animals in it, so every summon of such a pet failed ProtectionRefused
    [Fact]
    public void A_hostile_list_the_native_guard_would_refuse_is_refused_at_define()
    {
        var e = Assert.Throws<ArgumentException>(() => Pets.DefineAs(Wolf() with { Targets = new PetTargetPolicy { HostileFactions = [Faction.FarmAnimal] } }, "mod-a"));
        Assert.Contains("targets.hostileFactions", e.Message, StringComparison.Ordinal);
        const uint nativeNeverFair = (1u << 7) | (1u << 8) | (1u << 11) | (1u << 12);   // pets_safety.cpp kNeverFair
        foreach (var f in Enum.GetValues<Faction>())
        {
            var d = Wolf() with { Targets = new PetTargetPolicy { HostileFactions = [f] } };
            bool ok;
            try { ok = Pets.DefineAs(d, "mod-a").Ok; }
            catch (ArgumentException) { ok = false; }
            if (ok) Assert.Equal(0u, d.Targets.HostileMask & nativeNeverFair);
        }
    }

    [Fact]
    public void The_hostile_mask_is_the_factions_bits()
    {
        Assert.Equal((1u << 3) | (1u << 6), new PetTargetPolicy { HostileFactions = [Faction.Vukah, Faction.WildBeast] }.HostileMask);
        Assert.Equal(0u, new PetTargetPolicy { HostileFactions = [] }.HostileMask);
    }
}
