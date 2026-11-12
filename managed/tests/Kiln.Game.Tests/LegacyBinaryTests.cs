// SPDX-License-Identifier: MIT
// A consumer compiled against the dev.24 shape of Kiln.Game (Fixtures/legacy: the frozen Generated.cs and a frozen
// copy of dev.24's Components.cs, assembly name Kiln.Game) runs against the Kiln.Game under test, in a load context of
// its own that resolves Kiln.Game to that assembly. A member that is gone or retyped fails to bind; the negative run
// against a variant with one field renamed proves that this test sees it.
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace Kiln.Game.Tests;

public class LegacyBinaryTests
{
    sealed class GameContext(string gameDll) : AssemblyLoadContext("legacy-consumer", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == "Kiln.Game" ? LoadFromAssemblyPath(gameDll) : null;  // everything else: the default context
    }

    static string ConsumerDll => GenRepo.Built("legacy", "Consumer", "Kiln.Game.LegacyConsumer.dll");

    /// <summary>Runs the consumer against <paramref name="gameDll"/>; returns its log, or throws what binding threw.</summary>
    static List<string> RunAgainst(string gameDll)
    {
        var ctx = new GameContext(gameDll);
        try
        {
            var consumer = ctx.LoadFromAssemblyPath(ConsumerDll);
            var run = consumer.GetType("Kiln.Game.LegacyConsumer.Probe", throwOnError: true)!.GetMethod("Run")!;
            var game = ctx.LoadFromAssemblyName(new AssemblyName("Kiln.Game"));
            Assert.Equal(Path.GetFullPath(gameDll), Path.GetFullPath(game.Location));
            try { return (List<string>)run.Invoke(null, null)!; }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }
        finally { ctx.Unload(); }
    }

    [Fact]
    public void A_consumer_built_against_dev24_binds_and_runs_against_the_Kiln_Game_under_test()
    {
        var log = RunAgainst(GenRepo.GameAssembly.Location);
        Assert.Contains("NetworkHealth 3 7 8", log);
        Assert.Contains("Health 3 40", log);
        Assert.Contains("Mana 3 40", log);
        Assert.Contains("Stamina 4 40", log);
        Assert.Contains("CurrentTransform 5 56", log);
        Assert.Contains("LastTransform 13 72", log);
        Assert.Contains("Velocity 8 12", log);
        Assert.Contains("TeleportCount 10 16", log);
        Assert.Contains("NpcCommon 5 27 1688", log);
        Assert.Contains("Hash 216EB5F9 DC101091 4D7229A8 92410664 027AC564 4B6ABCF9 FB332100 BFFE34EF 072DBDB7", log);
        Assert.Contains("ComponentTypes 216EB5F9 DC101091 4D7229A8 92410664 027AC564 4B6ABCF9 FB332100 BFFE34EF 072DBDB7", log);
        // Every Components method bound and ran: with no game, NameOf answers and the reads say kiln.world is not there.
        foreach (var n in new[] { "NetworkHealth", "Health", "Mana", "Stamina", "CurrentTransform", "LastTransform", "Velocity", "TeleportCount", "NpcCommon" })
            Assert.Contains($"Components {n} {n} False unavailable unavailable unavailable unavailable", log);
        Assert.Equal(21, log.Count);
    }

    [Fact]
    public void The_same_consumer_against_the_dev24_shape_itself_runs()
    {
        var log = RunAgainst(GenRepo.Built("legacy", "Dev24Shape", "Kiln.Game.dll"));
        Assert.Equal(21, log.Count);
    }

    [Fact]
    public void A_renamed_field_fails_to_bind()
    {
        var e = Assert.Throws<MissingFieldException>(() => RunAgainst(GenRepo.Built("legacy", "Dev24Renamed", "Kiln.Game.dll")));
        Assert.Contains("healthMax", e.Message, StringComparison.Ordinal);
    }
}
