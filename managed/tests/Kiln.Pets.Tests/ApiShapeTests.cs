// SPDX-License-Identifier: MIT
// The public shape of Kiln.Pets before mod API 0.2.27 freezes it (dev.32 API-SHAPE-2; findings docs-api-ref A3-A13 and
// docs-skill C1-C11): one test group per finding.
using System.Reflection;
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

public sealed class ApiShapeTests
{
    // ---- A3: the capability names are public (Pets.<Row>Capability), one per row Kiln.Pets reports ----

    private static HashSet<string> Literals(Type t, Func<FieldInfo, bool> which) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && which(f)).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();

    [Fact]
    public void Every_row_Kiln_Pets_reports_has_a_public_capability_name()
    {
        var rows = Literals(typeof(Pets).GetNestedType("Caps", BindingFlags.NonPublic)!, _ => true);
        var names = Literals(typeof(Pets), f => f.Name.EndsWith("Capability", StringComparison.Ordinal));
        Assert.NotEmpty(rows);
        // a row added later (the internal Pets.Caps) needs its public constant too
        Assert.Empty(rows.Except(names));
        Assert.Empty(names.Except(rows));
    }

    [Fact]
    public void The_capability_names_are_the_rows()
    {
        Assert.Equal("kiln.pets", Pets.ServiceCapability);
        Assert.Equal("pets.definitions", Pets.DefinitionsCapability);
        Assert.Equal("pets.records", Pets.RecordsCapability);
        Assert.Equal("pets.summon", Pets.SummonCapability);
        Assert.Equal("pets.control", Pets.ControlCapability);
        Assert.Equal("pets.follow", Pets.FollowCapability);
        Assert.Equal("pets.fight", Pets.FightCapability);
        Assert.Equal("pets.downed", Pets.DownedCapability);
        Assert.Equal("pets.hud.server", Pets.HudServerCapability);
        Assert.Equal("pets.hud.client", Pets.HudClientCapability);
    }

    [Fact]
    public void A_definitions_limits_are_PetLimits_and_no_public_type_is_named_caps()
    {
        var d = new PetDefinition { Id = "mod-a:wolf", Creature = "game:enemy_wildbeast_wolf", Limits = new PetLimits { LivePerCharacter = 2 } };
        Assert.Equal(2, d.Limits.LivePerCharacter);
        Assert.Equal(32, d.Limits.RetainedPerCharacter);
        Assert.Null(typeof(Pets).Assembly.GetType("Kiln.PetCaps"));
        Assert.Null(typeof(PetDefinition).GetProperty("Caps", BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(("limits.livePerWorld", "257 is outside 1..256"), PetDefinitionRules.Check(d with { Limits = new PetLimits { LivePerWorld = 257 } }));
    }
}

