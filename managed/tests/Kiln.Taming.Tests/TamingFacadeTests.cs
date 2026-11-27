// SPDX-License-Identifier: MIT
// The Taming facade's capability rules (review of dev.32 TAME): a definition is data a mod declares when it loads; the
// attempts need a running server world.
using Kiln;
using Xunit;

namespace Kiln.TamingTests;

[Collection("TamingCaps")]   // Backend.Current and the runtimes' seams are process-wide: never two such test classes at once
public sealed class TamingFacadeTests : IDisposable
{
    private sealed class Backend : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public nint Service(string name, uint minVersion) => 0;
        public void Log(ulong owner, LogLevel level, string text) { }
    }

    private readonly IKilnBackend _was = Kiln.Backend.Current;
    private readonly Func<string, bool> _caps = TamingCaps.Is;

    public TamingFacadeTests()
    {
        Kiln.Backend.Current = new Backend();
        TamingRuntime.Reset();
        TamingCaps.Is = name => name == TamingCaps.Service;   // Kiln.Taming is loaded; no server world runs (a mod's OnLoad)
    }

    public void Dispose()
    {
        TamingCaps.Is = _caps;
        TamingRuntime.Reset();
        Kiln.Backend.Current = _was;
    }

    // ---- finding: a mod could not define its tamings when it loads ------------------------------------------------------
    // Taming.Define, DefineJson, Definition and Definitions required taming.steps, a session row that is on only while a
    // server world runs, so a mod's OnLoad (and a TamingBlock, compiled there) got KilnUnavailableException; the sample
    // worked around it by polling the tick until a world ran.

    [Fact]
    public void A_mod_defines_its_tamings_at_load_before_any_world_runs()
    {
        var d = TamingFixture.Def(new WeakenStep(0.5)) with { Id = "kiln:wolf" };
        Taming.Define(d);
        Assert.Same(d, Taming.Definition("kiln:wolf"));
        Assert.Single(Taming.Definitions);
        var json = Taming.DefineJson("""{ "id": "kiln:pup", "creatures": ["game:animal_wildlife_dog_*"], "steps": [ { "kind": "native" } ] }""");
        Assert.Equal("kiln:pup", Assert.Single(json).Id);
        // the attempts need the world: Begin names taming.steps
        var e = Assert.Throws<KilnUnavailableException>(() => Taming.Begin(new PlayerRef(1, Guid.NewGuid(), new EntityRef(new EntityId(10), 1)),
            new EntityRef(new EntityId(500), 7), "kiln:wolf", Guid.NewGuid()));
        Assert.Equal(Taming.Capability, e.Capability);
    }

    [Fact]
    public void Without_Kiln_Taming_a_definition_names_kiln_taming()
    {
        TamingCaps.Is = _ => false;
        var e = Assert.Throws<KilnUnavailableException>(() => Taming.Define(TamingFixture.Def(new WeakenStep(0.5)) with { Id = "kiln:wolf" }));
        Assert.Equal(TamingCaps.Service, e.Capability);
    }
}
