// SPDX-License-Identifier: MIT
// The kit's recording fake of kiln.spawn: Kiln.Spawn's own code on a table of the kit's, entities in the fake world.
using System.Runtime.CompilerServices;
using TkFakes;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class SpawnFakeTests
{
    private const ulong AdaSteam = 76561198000000401;

    private static string Said(CommandResult r) => string.Join("\n", r.Error is null ? r.Output : r.Output.Append(r.Error));

    private static (KilnHarness Kiln, FakePlayer Ada) Start(KilnHarnessOptions? options = null)
    {
        var kiln = new KilnHarness(options);
        kiln.Load<TkFakesMod>();
        kiln.Spawned.AddTemplate("Enemy_Vukah_Melee", TemplateKind.Creature, "Vukah");
        var ada = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan, position: new WorldPosition(10, 0, 10));
        kiln.Step(2);
        return (kiln, ada);
    }

    [Fact]
    public void ASpawnedEntityStandsInTheFakeWorldWithItsTemplateTagAndEvents()
    {
        var (kiln, ada) = Start();
        using var _ = kiln;
        var reply = Said(kiln.RunCommand(ada, "tkf-spawn Enemy_Vukah_Melee raid"));
        var spawned = Assert.Single(kiln.Spawned.All);
        Assert.Equal($"spawned {spawned.Entity.Value}", reply);
        Assert.Equal(("Enemy_Vukah_Melee", TemplateKind.Creature, "raid", "tk-fakes"), (spawned.Template, spawned.Kind, spawned.Tag, spawned.Origin));
        Assert.Equal(new WorldPosition(12, 0, 10), spawned.SpawnPosition);
        Assert.Equal(new WorldPosition(12, 0, 10), spawned.Position);
        Assert.Equal(90f, spawned.YawDegrees);
        Assert.Equal(1UL, spawned.Serial);
        Assert.Equal(kiln.CurrentStep, spawned.Step);
        Assert.True(kiln.Entities.Exists(spawned.Entity));
        Assert.Contains(kiln.Lines("tk-fakes", "event spawned:"), l => l == $"event spawned: {spawned.Entity.Value} Entity Enemy_Vukah_Melee tag raid by tk-fakes");
        Assert.Equal($"inspect: Enemy_Vukah_Melee at 12,0,10 yaw 90 tracked True serial 1 spawntime True",
            Said(kiln.RunCommand($"tkf-inspect {spawned.Entity.Value}")));
        // the calls mods use find it again: by name (display name too), near a point, in the count of the world's entities
        Assert.Equal("Enemy_Vukah_Melee", kiln.Run(() => Spawn.Lookup("vukah"))?.Name);
        Assert.Equal($"found {spawned.Entity.Value} of 2", Said(kiln.RunCommand("tkf-find vukah")));  // Ada and the Vukah
        Assert.Equal($"found  of 2", Said(kiln.RunCommand("tkf-find vukah 5")));                     // 12 m away
        Assert.Equal($"found {spawned.Entity.Value} of 2", Said(kiln.RunCommand("tkf-find vukah 20")));
        Assert.Contains("1 Enemy_Vukah_Melee raid tk-fakes live True", Said(kiln.RunCommand("tkf-tracked")));
        // a test moves it: the reads follow the world
        kiln.Entities.SetPosition(spawned.Entity, new WorldPosition(0, 0, 1));
        Assert.Equal($"found {spawned.Entity.Value} of 2", Said(kiln.RunCommand("tkf-find vukah 5")));

        Assert.Equal("destroyed True", Said(kiln.RunCommand($"tkf-destroy {spawned.Entity.Value}")));
        Assert.Equal("destroyed False", Said(kiln.RunCommand($"tkf-destroy {spawned.Entity.Value}")));
        Assert.Empty(kiln.Spawned.All);
        Assert.False(kiln.Entities.Exists(spawned.Entity));
        Assert.Contains(kiln.Lines("tk-fakes", "event destroyed:"), l => l == $"event destroyed: {spawned.Entity.Value} Enemy_Vukah_Melee destroy by tk-fakes");
        Assert.Equal("tracked:", Said(kiln.RunCommand("tkf-tracked")));
        Assert.Equal([FakeSpawnKind.Entity, FakeSpawnKind.Destroy, FakeSpawnKind.Destroy], kiln.Spawned.Calls.Select(c => c.Kind));
        Assert.Equal([0, 0, 2], kiln.Spawned.Calls.Select(c => c.Code));
        Assert.All(kiln.Spawned.Calls, c => Assert.Equal("tk-fakes", c.ModId));
        Assert.Equal(new WorldPosition(12, 0, 10), kiln.Spawned.Calls[0].Position);
    }

    [Fact]
    public void AnUnknownTemplateIsRefusedAsTheGameRefusesIt()
    {
        var (kiln, ada) = Start();
        using var _ = kiln;
        Assert.Contains("refused: SpawnException: kiln.spawn: Enemy_Nope: unknown template or entity", Said(kiln.RunCommand(ada, "tkf-spawn Enemy_Nope")));
        Assert.StartsWith("try: ArgumentException:", Said(kiln.RunCommand(ada, "tkf-try Enemy_Nope")));
        Assert.Empty(kiln.Spawned.All);
        Assert.Equal(2, kiln.Spawned.Calls[0].Code);
        Assert.Equal("Enemy_Nope", kiln.Spawned.Calls[0].Subject);
        // the result form, with the entity's serial from kiln.spawn's stamps
        Assert.StartsWith("try: ok True entity ", Said(kiln.RunCommand(ada, "tkf-try Enemy_Vukah_Melee")));
        Assert.EndsWith("serial True", Said(kiln.RunCommand(ada, "tkf-try Enemy_Vukah_Melee")));
        // templates: the generic pickup and what the test added, sorted by name
        Assert.Equal([HarnessSpawn.PickupTemplate, "Enemy_Vukah_Melee"], kiln.Run(() => Spawn.Templates.Select(t => t.Name).ToArray()));
        var id = kiln.Spawned.AddTemplate("Prop_Barrel", TemplateKind.Prop, id: Guid.Parse("11111111-2222-3333-4444-555555555555"));
        Assert.Equal("Prop_Barrel", kiln.Run(() => Spawn.Template(id))?.Name);
        Assert.Throws<ArgumentException>(() => kiln.Spawned.AddTemplate("Prop_Barrel"));
    }

    [Fact]
    public void ItemDropsAndCleanupFollowTheTrackedList()
    {
        var (kiln, ada) = Start();
        using var _ = kiln;
        Assert.StartsWith("dropped ", Said(kiln.RunCommand(ada, "tkf-drop 79442800 3")));
        Said(kiln.RunCommand(ada, "tkf-spawn Enemy_Vukah_Melee raid"));
        Said(kiln.RunCommand(ada, "tkf-spawn Enemy_Vukah_Melee raid"));
        var drop = Assert.Single(kiln.Spawned.All, s => s.ItemId != 0);
        Assert.Equal((HarnessSpawn.PickupTemplate, 0x79442800u, 3, "item", TemplateKind.Pickup), (drop.Template, drop.ItemId, drop.Amount, drop.Tag, drop.Kind));
        Assert.Equal(new WorldPosition(5, 0, 5), drop.Position);
        Assert.Equal(2, kiln.Spawned.Of("enemy_vukah_melee").Count);
        // a test takes one away: the tracked list sees it missing at the next step, cleanup drops it
        kiln.Entities.Destroy(kiln.Spawned.Of("Enemy_Vukah_Melee")[0].Entity);
        kiln.Step();
        Assert.Contains("live False", Said(kiln.RunCommand("tkf-tracked")));
        Assert.Equal("cleanup: 1 destroyed 1 missing 1 kept", Said(kiln.RunCommand(ada, "tkf-cleanup raid")));
        Assert.Equal([drop.Entity], kiln.Spawned.All.Select(s => s.Entity));
        Assert.Single(kiln.Lines("tk-fakes", "event destroyed:"), l => l.EndsWith(" cleanup by tk-fakes", StringComparison.Ordinal));
        Assert.Equal(FakeSpawnKind.Cleanup, kiln.Spawned.Calls[^1].Kind);
    }

    [Fact]
    public void BeforeTheWorldRunsSpawningIsPendingAndTheRowSaysSo()
    {
        var (kiln, ada) = Start(new KilnHarnessOptions { WaitForWorldRunning = true });
        using var _ = kiln;
        Assert.False(kiln.Capabilities.Is("spawn.world"));
        Assert.Contains("pending", kiln.Capabilities.Why("spawn.world"));
        Assert.Contains("refused: KilnUnavailableException", Said(kiln.RunCommand(ada, "tkf-spawn Enemy_Vukah_Melee")));
        Assert.Contains("spawn.world", Said(kiln.RunCommand(ada, "tkf-try Enemy_Vukah_Melee")));
        kiln.ReportWorldRunning();
        kiln.Step(2);
        Assert.True(kiln.Capabilities.Is("spawn.world"));
        Assert.StartsWith("spawned ", Said(kiln.RunCommand(ada, "tkf-spawn Enemy_Vukah_Melee")));
    }

    [Fact]
    public void ATestCanSwitchTheFakeOffAndWithNoHarnessItIsGone()
    {
        Guid world;
        var (kiln, ada) = Start();
        using (kiln)
        {
            world = kiln.World.Id;
            Assert.Equal(world.ToString("D"), kiln.Run(() => Spawn.WorldKey));
            Said(kiln.RunCommand(ada, "tkf-spawn Enemy_Vukah_Melee"));
            var e = kiln.Spawned.All[0].Entity;
            kiln.Capabilities.Override("spawn.destroy", false);
            Assert.Contains("spawn.destroy", Said(kiln.RunCommand($"tkf-destroy {e.Value}")));
            kiln.Capabilities.ClearOverride("spawn.destroy");
            kiln.Capabilities.Override("kiln.spawn", false);
            Assert.Contains("refused: KilnUnavailableException", Said(kiln.RunCommand(ada, "tkf-spawn Enemy_Vukah_Melee")));
            Assert.False(kiln.Run(() => Spawn.WorldRunning));
            Assert.Empty(kiln.Run(() => Spawn.Templates));
            kiln.Capabilities.ClearOverride("kiln.spawn");
            Assert.Equal("destroyed True", Said(kiln.RunCommand($"tkf-destroy {e.Value}")));
        }
        Assert.False(Spawn.WorldRunning);
        Assert.Empty(Spawn.Templates);
        Assert.Empty(Spawn.Tracked);
        using var next = new KilnHarness();
        next.Step();
        Assert.Empty(next.Spawned.All);
        Assert.Empty(next.Spawned.Calls);
        Assert.Equal([HarnessSpawn.PickupTemplate], next.Run(() => Spawn.Templates.Select(t => t.Name)));
    }

    [Fact]
    public void TheTableMirrorsTheHeaderAsTheServicesReadIt()
    {
        Assert.Equal((120, 224, 352, 96, 16), (Unsafe.SizeOf<SpawnAbi.Request>(), Unsafe.SizeOf<SpawnAbi.TemplateInfo>(), Unsafe.SizeOf<SpawnAbi.Tracked>(),
            Unsafe.SizeOf<SpawnAbi.Snapshot>(), Unsafe.SizeOf<SpawnAbi.Cleanup>()));
        Assert.Equal(17 * 8, Unsafe.SizeOf<SpawnAbi.Table>());
        var spawn = typeof(Spawn);
        Assert.Equal(Unsafe.SizeOf<SpawnAbi.Request>(), Mirror.Size(spawn, "Request"));
        Assert.Equal(Unsafe.SizeOf<SpawnAbi.TemplateInfo>(), Mirror.Size(spawn, "NativeTemplate"));
        Assert.Equal(Unsafe.SizeOf<SpawnAbi.Tracked>(), Mirror.Size(spawn, "NativeTracked"));
        Assert.Equal(Unsafe.SizeOf<SpawnAbi.Snapshot>(), Mirror.Size(spawn, "NativeSnapshot"));
        Assert.Equal(Unsafe.SizeOf<SpawnAbi.Cleanup>(), Mirror.Size(spawn, "NativeCleanup"));
        Assert.Equal(Unsafe.SizeOf<SpawnAbi.Table>(), Mirror.Size(spawn, "Table"));
        var link = typeof(WorldData).Assembly.GetType("Kiln.SpawnLink", true)!;
        Assert.Equal(Unsafe.SizeOf<SpawnAbi.Snapshot>(), Mirror.Size(link, "Snapshot"));
        Assert.Equal(Unsafe.SizeOf<SpawnAbi.TemplateInfo>(), Mirror.Size(link, "TemplateInfo"));
    }
}
