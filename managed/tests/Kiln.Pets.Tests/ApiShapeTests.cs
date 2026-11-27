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

    // ---- A4: one vocabulary of refusals, as a value (PetResult.Refusal), never matched as text ----

    [Theory]
    [InlineData("Capacity: the character keeps 32 pets of mod-a:wolf already", PetRefusal.Capacity)]
    [InlineData("OwnerOffline", PetRefusal.OwnerOffline)]
    [InlineData("RefusedByMod: mod b refused it (nothing was taken)", PetRefusal.RefusedByMod)]
    [InlineData("CanceledCostCommitted: the owner left while the cost settled", PetRefusal.CanceledCostCommitted)]
    [InlineData("SomethingNew: a later Kiln's word", PetRefusal.Other)]
    [InlineData("", PetRefusal.Other)]
    public void A_refusal_is_a_value_read_from_the_reasons_word(string reason, PetRefusal refusal)
    {
        Assert.Equal(refusal, new PetResult(false, reason, null, Guid.NewGuid(), []).Refusal);
        Assert.Equal(PetRefusal.None, new PetResult(true, null, null, Guid.NewGuid(), []).Refusal);
        Assert.Equal(refusal, new PetDefinitionResult(false, reason, null).Refusal);
    }

    [Fact]
    public void A_mods_veto_of_a_revive_is_RefusedByMod()
    {
        using var t = new ReviveTests();
        Assert.Equal(PetRefusal.RefusedByMod, t.Vetoed().Refusal);
    }

    /// <summary>Every leading word a reason of Kiln.Pets starts with ("Word: ...") is a <see cref="PetRefusal"/> member, so a
    /// mod never needs the text. The words of the follower's and the mover's own diagnostics (never a result) are left out.</summary>
    [Fact]
    public void Every_reason_word_Kiln_Pets_writes_is_a_refusal_member()
    {
        var notResults = new HashSet<string> { "NoRoute", "Stuck", "CatchUpRefused", "CatchUpUnsettled" };
        var words = SourceWords("Kiln.Pets").Where(w => !notResults.Contains(w)).ToHashSet();
        Assert.Contains("Capacity", words);
        Assert.Empty(words.Where(w => !Enum.TryParse<PetRefusal>(w, out _)).Order());
    }

    internal static IEnumerable<string> SourceWords(string service, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../services", service));
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "\\$?\"([A-Z][a-z]+(?:[A-Z][A-Za-z]*)?):"))
                yield return m.Groups[1].Value;
    }

    // ---- A5: one request shape: a request id (RequestId everywhere) and the view it expects ----

    [Fact]
    public void Pets_requests_name_their_id_RequestId_as_Taming_and_ItemCosts_do()
    {
        var r = PetRequest.New(7);
        Assert.NotEqual(Guid.Empty, r.RequestId);
        Assert.Equal(7ul, r.ExpectedStateRevision);
        var publicMembers = typeof(Pets).Assembly.GetExportedTypes().SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}").Where(n => n.Contains("Operation", StringComparison.Ordinal)).ToList();
        Assert.Empty(publicMembers);
        var cancel = typeof(Pets).GetMethod(nameof(Pets.CancelRevive))!.GetParameters();
        Assert.Equal("reviveRequest", cancel[2].Name);
        Assert.Equal(nameof(PetResult.RequestId), typeof(PetResult).GetProperty("RequestId")!.Name);
        Assert.NotNull(typeof(PetChange).GetProperty("RequestId"));
        Assert.NotNull(typeof(PetSnapshot).GetProperty("ReviveRequest"));
    }

    // ---- A10: PetsClient checks the capability it declares ----

    [Fact]
    public void PetsClient_Snapshot_and_Configure_throw_naming_pets_hud_client_while_it_is_off()
    {
        PetsClient.ResetForTests();
        Assert.False(Capabilities.Is(Pets.HudClientCapability));
        Assert.Equal(Pets.HudClientCapability, Assert.Throws<KilnUnavailableException>(() => PetsClient.Snapshot).Capability);
        Assert.Equal(Pets.HudClientCapability, Assert.Throws<KilnUnavailableException>(() => PetsClient.Configure(new PetHudOptions())).Capability);
        PetsRuntime.TestRows = true;
        try
        {
            Assert.Null(PetsClient.Snapshot);
            PetsClient.Configure(new PetHudOptions(Enabled: false));
            Assert.False(PetsClient.Options.Enabled);
        }
        finally
        {
            PetsRuntime.TestRows = false;
            PetsClient.ResetForTests();
        }
    }
}

