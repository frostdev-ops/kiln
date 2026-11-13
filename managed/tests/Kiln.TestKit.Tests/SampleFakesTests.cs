// SPDX-License-Identifier: MIT
// Kiln's samples that could not run offline before dev.27's recording fakes, running their real logic on them. A sample
// is what a mod author copies: what it does here is what it does on a server, as far as the fakes model the game.
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class SampleFakesTests
{
    private const ulong AdaSteam = 76561198000000501, BoSteam = 76561198000000502;

    private static string Said(CommandResult r) => string.Join("\n", r.Error is null ? r.Output : r.Output.Append(r.Error));

    [Fact]
    public void CreatureTweaksSpawnsItsEliteVukahNextToTheAdminAndClearsThem()
    {
        using var kiln = new KilnHarness();
        kiln.LoadFolder(TestMods.Folder("CreatureTweaks"));
        // the clone kiln.gamedata registers from the sample's data pack is a template the game then has
        kiln.Spawned.AddTemplate("Kiln_Elite_Vukah", TemplateKind.Creature, "Elite Vukah");
        var admin = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan, position: new WorldPosition(100, 5, 50));
        kiln.Step(2);
        Assert.Contains("kiln.gamedata is not available", string.Join("\n", kiln.LogsOf("creaturetweaks").Select(l => l.Text)));

        Assert.Equal("An Elite Vukah appears 8 m away.", Said(kiln.RunCommand(admin, "elitevukah")));
        var elite = Assert.Single(kiln.Spawned.All);
        Assert.Equal(("Kiln_Elite_Vukah", "creaturetweaks", "creaturetweaks", SpawnFlags.Transient), (elite.Template, elite.Tag, elite.Origin, elite.Flags));
        Assert.Equal(new WorldPosition(108, 5, 50), elite.Position);
        Assert.Contains(kiln.Lines("creaturetweaks", "Elite Vukah "), l => l.StartsWith($"Elite Vukah {elite.Entity} (Kiln_Elite_Vukah)", StringComparison.Ordinal) && l.EndsWith("exists True", StringComparison.Ordinal));
        // offsets, and a template by name
        kiln.Spawned.AddTemplate("Enemy_Vukah_Melee");
        Said(kiln.RunCommand(admin, "elitevukah -3 4 template:Enemy_Vukah_Melee"));
        Assert.Equal(new WorldPosition(97, 5, 54), Assert.Single(kiln.Spawned.Of("Enemy_Vukah_Melee")).Position);
        // an ally needs a Faction component on the spawn: extra spawn components are offline, and the sample says why
        Said(kiln.RunCommand(admin, "elitevukah ally"));
        Assert.Contains(kiln.Lines("creaturetweaks", "Elite Vukah spawn failed:"), l => l.Contains("spawn.components", StringComparison.Ordinal));
        Assert.Equal(2, kiln.Spawned.All.Count);
        // a template the game does not have is refused, and the sample logs it
        Said(kiln.RunCommand(admin, "elitevukah template:Enemy_Nope"));
        Assert.Contains(kiln.Lines("creaturetweaks", "Elite Vukah spawn failed:"), l => l.Contains("unknown template", StringComparison.Ordinal));

        Assert.Equal("2 Elite Vukah(s) removed.", Said(kiln.RunCommand(admin, "elitevukah clear")));
        Assert.Empty(kiln.Spawned.All);
        Assert.Equal([FakeSpawnKind.Entity, FakeSpawnKind.Entity, FakeSpawnKind.Entity, FakeSpawnKind.Destroy, FakeSpawnKind.Destroy],
            kiln.Spawned.Calls.Select(c => c.Kind));
        Assert.Equal(2, kiln.Spawned.Calls[2].Code);  // Enemy_Nope; the ally never reached kiln.spawn
    }

    [Fact]
    public void AdminToolsWarnsAPlayerInChatAtTheFirstStrike()
    {
        using var kiln = new KilnHarness();
        kiln.LoadFolder(TestMods.Folder("AdminTools"));
        var admin = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan);
        var bo = kiln.Players.Join("Bo", BoSteam);
        kiln.Step(2);
        Assert.Equal("Bo: first strike, warned", Said(kiln.RunCommand(admin, "strike Bo breaking the gate")));
        Assert.Equal(["[Server] A warning from Ada: breaking the gate"], bo.Chat.Select(l => l.Text));
        Assert.Equal("admin-tools", Assert.Single(kiln.Chat.Sent, l => l.To == bo).ModId);
        // the admin typed it in chat: the line is a command, nobody sees it, and the reply goes to the admin's chat
        admin.Say("/strike Bo again");
        kiln.Step();
        Assert.True(Assert.Single(kiln.Chat.Typed).Consumed);
        Assert.Contains("[Server] Bo: second strike recorded. Not kicked: this server cannot kick (players.kick is off).", admin.Chat.Select(l => l.Text));
        Assert.DoesNotContain(bo.Chat, l => l.Text.Contains("again", StringComparison.Ordinal));
        Assert.Contains("Bo (76561198000000502): 2 strike(s), last: again", Said(kiln.RunCommand(admin, "strikes")));
    }

    [Fact]
    public void SavedDataLabelsTheChestNextToAPlayer()
    {
        using var kiln = new KilnHarness();
        kiln.LoadFolder(TestMods.Folder("SavedData"));
        var admin = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan, position: new WorldPosition(50, 0, 50));
        var bo = kiln.Players.Join("Bo", BoSteam, position: new WorldPosition(10, 0, 10));
        kiln.Step(2);
        Assert.Contains("no chest within", Said(kiln.RunCommand(bo, "label mine")));
        // a chest of the fake world with an inventory: what Items.FindInventories finds, and EntityData keeps data on
        var chest = kiln.Entities.Create(new WorldPosition(11, 0, 10));
        kiln.Items.AddContainer(chest, inventories: 3);
        kiln.Items.AddContainer(kiln.Entities.Create(new WorldPosition(200, 0, 200)));  // out of reach
        Assert.Equal("labelled: \"mine\" (until the server stops: the game does not save this container)", Said(kiln.RunCommand(bo, "label mine")));
        var labels = Said(kiln.RunCommand(admin, "labels"));
        Assert.Contains("\"mine\" by Bo at (11", labels);
        Assert.Contains("1 label(s)", labels);
        // another player near the same chest may not relabel it (an admin may: saved-data.relabel)
        var cy = kiln.Players.Join("Cy", 76561198000000503, position: new WorldPosition(10, 0, 11));
        kiln.Step();
        Assert.Contains("Bo labelled this chest \"mine\"", Said(kiln.RunCommand(cy, "label ours")));
        // the chest is gone: so is its label (Kiln.Saves looks for gone entities once a second of real time)
        kiln.Entities.Destroy(chest);
        kiln.StepUntil(() => Said(kiln.RunCommand(admin, "labels")).Contains("0 label(s)", StringComparison.Ordinal), maxSteps: 200,
            pace: TimeSpan.FromMilliseconds(20));
    }
}
