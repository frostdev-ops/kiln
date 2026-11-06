// SPDX-License-Identifier: MIT
// A game client runs a mod's OnLoad off the tick thread, and has a server world only while it hosts one. A creature block's server-world part (its
// arsenal, variant and spawn rules) waits there for such a world instead of failing the mod; on a server's tick thread it is written at once.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kiln.BlockTests;

public sealed class CreatureClientTests : IDisposable
{
    private bool _tick, _client, _running;

    public CreatureClientTests()
    {
        NpcServerSide.OnTickThread = () => _tick;
        NpcServerSide.IsClient = () => _client;
        NpcServerSide.ServerRunning = () => _running;
        (_tick, _running) = (true, true);
        NpcServerSide.OnTick(0);   // nothing left from another test
        (_tick, _running) = (false, false);
    }

    private static readonly (Func<bool> Tick, Func<bool> Client, Func<bool> Running) Real = (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning);

    public void Dispose() => (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning) = Real;

    private static ModBlocks Mod(string id) => new(id, 1);

    [Fact]
    public void On_a_servers_tick_thread_it_runs_at_once()
    {
        _tick = true;
        var ran = 0;
        NpcServerSide.Post(Mod("srv"), "creature a", () => ran++);
        Assert.Equal(1, ran);
        Assert.Equal(0, NpcServerSide.Waiting);
    }

    [Fact]
    public void A_clients_OnLoad_waits_for_a_hosted_world()
    {
        _client = true;   // off the tick thread, no world
        var ran = 0;
        NpcServerSide.Post(Mod("cli"), "creature a", () => ran++);
        Assert.Equal(0, ran);
        Assert.Equal(1, NpcServerSide.Waiting);
        _tick = true;
        NpcServerSide.OnTick(1);   // its own world, joined to another server: still nothing
        Assert.Equal(0, ran);
        _running = true;           // it hosts a world now
        NpcServerSide.OnTick(2);
        Assert.Equal(1, ran);
        Assert.Equal(0, NpcServerSide.Waiting);
        NpcServerSide.OnTick(3);
        Assert.Equal(1, ran);      // once
    }

    [Fact]
    public void A_refusal_later_is_an_error_of_the_mod_by_block()
    {
        _client = true;
        var mod = Mod("late");
        NpcServerSide.Post(mod, "creature b", () => throw new BlockError("creature b", "behaviour", "the writer said no"));
        (_tick, _running) = (true, true);
        NpcServerSide.OnTick(1);
        Assert.Contains(mod.Errors, e => e.Contains("creature b", StringComparison.Ordinal) && e.Contains("behaviour", StringComparison.Ordinal));
    }

    [Fact]
    public void A_creature_block_compiles_on_a_client_and_leaves_its_server_part_waiting()
    {
        _client = true;
        var mod = Mod("client-creatures");
        var handle = Compiler.Define(mod, "vukah-mind", new CreatureBlock
        {
            Body = Bodies.Vukah,
            Behaviour = Behaviours.None with { Rules = [new RuleBlock { Name = "count", Every = 0.5, When = [Is.HealthBelow(0.5)], Do = [Act.SetPhase("hurt")] }] },
            Stats = new CreatureStats { Level = 12 },
        });
        Assert.Equal("client-creatures:vukah-mind", ((CreatureHandle)handle).Variant);
        Assert.Equal(2, NpcServerSide.Waiting);   // the arsenal, then the variant
        Assert.True(mod.Lowered.ContainsKey("vukah-mind"));
    }

    private sealed class StillBrain : NpcBrain
    {
        public override void Tick(NpcControl npc) { }
    }

    [Fact]
    public void Brains_never_run_in_a_clients_menu_or_joined_world()
    {
        _client = true;
        Compiler.Define(Mod("client-minds"), "sentinel", new CreatureBlock { Body = Bodies.Vukah, Behaviour = Behaviours.None with { Brain = () => new StillBrain() } });
        // The menu's UI frame and a joined world's step drive Kiln's service tick (Kiln's tick thread, not the game's server world): the poll
        // for the block's creatures would touch the game's world there, and a step of each never gets to it.
        _tick = true;
        for (var step = 0UL; step < 80; step++) NpcRuntime.OnTick(step);
        // The world a client hosts, as a server's: its poll looks at the world (none in a test: the world service says it is not here).
        _running = true;
        Assert.Throws<NotSupportedException>(() => NpcRuntime.OnTick(7));
    }
}
