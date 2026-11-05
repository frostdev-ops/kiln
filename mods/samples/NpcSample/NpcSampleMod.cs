// SPDX-License-Identifier: MIT
using Kiln;

namespace NpcSample;

/// <summary>
/// Sample: a friendly NPC with a short voiced quest (docs/kiln/services/npcs.md). Everything he is comes from data, read at
/// boot on the server and on every client: quests/npcsample.json (the clone, his roster entry, his dialogue, the quest,
/// the marker, a trade) and assets/cook.json (his voice lines: SoundSample's test tones). This code only adds a command for
/// testing, because a world position in a data file cannot know where a player is: <c>/npcsample here</c>.
/// </summary>
public sealed class NpcSampleMod : Mod
{
    private const string Id = "npcsample";
    private static readonly Guid Tobin = new("299d68c5-10b9-4af9-91e0-194b308ee461");

    private static EntityId PlayerOf(CommandContext c)
    {
        if (!c.Source.Player.IsNone) return c.Source.Player;
        // the console folder (kiln/control/console) has no player: the first in game
        foreach (var p in Players.All) return p.Entity;
        return EntityId.None;
    }

    [Command("npcsample here", Description = "brings Tobin next to you (he is unlocked for this world); his quest's marker finds its own place near him",
        Permission = CommandPermission.Admin)]
    private async Task Here(CommandContext c)
    {
        var player = PlayerOf(c);
        if (player.IsNone || World.Position(player) is not { } at)
        {
            c.Reply("no player in game");
            return;
        }
        var unlock = NativeQuests.KnowledgeId(Id, "tobin_met");
        if (unlock is null)
        {
            c.Reply("the native build has no npcsample data: see kiln.log (\"knowledge: native:\")");
            return;
        }
        await Knowledge.World.Grant(unlock.Value);
        for (var i = 0; i < 5; i++) await Context.Scheduler.NextTick();  // the NPC registry follows the knowledge in the next step
        var spot = new WorldPosition(at.X + 3, at.Y, at.Z + 1);
        var v = Villagers.Find(Tobin);
        if (v is not null) v.Teleport(spot, 180);
        else
        {
            var e = await Spawn.Entity(Tobin, spot, 180, new SpawnOptions { Flags = SpawnFlags.NoTrack });
            Quests.BindNpc($"{Id}:tobin", e);
        }
        var text = $"Tobin is at ({spot.X:F0}, {spot.Y:F0}, {spot.Z:F0}); talk to him";
        c.Reply(text);
        Log.Info("npcsample: " + text);
    }

    [Command("npcsample status", Description = "what the sample's quest and NPC are doing", Permission = CommandPermission.Admin)]
    private string Status(CommandContext c)
    {
        var player = PlayerOf(c);
        var v = Villagers.Find(Tobin);
        var quest = player.IsNone ? "no player" : Quests.StatusOf($"{Id}:tobin_kindling", QuestHolder.Of(player)).ToString();
        return $"Tobin: {(v is null ? "not in the world" : v.ToString())}; unlocked {Knowledge.FindNpc("Kiln_NpcSample_Tobin")?.Unlocked}; quest {quest}";
    }

    [Command("npcsample follow", Description = "Tobin follows you (Villagers.Follow)", Permission = CommandPermission.Admin)]
    private string Follow(CommandContext c)
    {
        var player = PlayerOf(c);
        var v = Villagers.Find(Tobin);
        if (v is null || player.IsNone) return "no Tobin or no player";
        v.Follow(player);
        return $"{v} follows {player}";
    }

    [Command("npcsample stay", Description = "Tobin stays where he stands; that becomes his home (Villagers.Stay)", Permission = CommandPermission.Admin)]
    private string Stay()
    {
        var v = Villagers.Find(Tobin);
        if (v is null) return "no Tobin";
        v.Stay();
        return $"{v} stays";
    }

    [Command("npcsample twigs", Description = "gives you 3 Twigs (what the quest asks for)", Permission = CommandPermission.Admin)]
    private async Task Twigs(CommandContext c)
    {
        var player = PlayerOf(c);
        if (player.IsNone)
        {
            c.Reply("no player in game");
            return;
        }
        var r = await Items.Give(player, "Material_T1_Twigs", 3);
        c.Reply(r.ToString());
    }

    [Command("npcsample whistle", Description = "gives you Tobin's Whistle (the quest's reward: his summon tool)", Permission = CommandPermission.Admin)]
    private async Task Whistle(CommandContext c)
    {
        var player = PlayerOf(c);
        if (player.IsNone)
        {
            c.Reply("no player in game");
            return;
        }
        var r = await Items.Give(player, "Kiln_NpcSample_TobinTool");
        c.Reply(r.ToString());
    }
}
