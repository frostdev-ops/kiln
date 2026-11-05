// SPDX-License-Identifier: MIT
// A manual check that a cooked clip plays in the client: spawns the creature whose clip tools/animcook/samples/clip_override.py replaced, next to the player, friendly and
// standing still, so you can look at its idle. The clip itself is served by kiln.assets from assets/assets.json.
using System.Text.Json;
using Kiln;

namespace ClipCheck;

public sealed class ClipCheckMod : Mod
{
    private string Template()
    {
        var f = Path.Combine(Context.Info.Directory, "assets/check.json");
        if (!File.Exists(f)) return "Enemy_Vukah_Melee";
        using var d = JsonDocument.Parse(File.ReadAllText(f));
        return d.RootElement.GetProperty("template").GetString() ?? "Enemy_Vukah_Melee";
    }

    [Command("clipcheck spawn", Description = "the checked creature 4 m from you, friendly, standing still", Permission = CommandPermission.Admin)]
    private async Task SpawnIt(CommandContext c)
    {
        var player = c.Source.Player;
        if (player.IsNone)
            foreach (var p in Players.All) { player = p.Entity; break; }
        if (player.IsNone || World.Position(player) is not { } at)
        {
            c.Reply("no player in game");
            return;
        }
        var t = Template();
        var e = await Spawn.Entity(t, new WorldPosition(at.X + 4, at.Y, at.Z), 270, new SpawnOptions { Tag = "clipcheck", Flags = SpawnFlags.Transient });
        var cr = Creatures.Of(e);
        cr.Faction = Faction.PlayerAlly;
        cr.Features.Disable(EnemyFeatures.Move | EnemyFeatures.Roaming | EnemyFeatures.Combat | EnemyFeatures.Awareness | EnemyFeatures.Investigate);
        var text = $"clipcheck: {t} is at ({at.X + 4:F0}, {at.Y:F0}, {at.Z:F0}), entity {e}";
        c.Reply(text);
        Log.Info(text);
    }
}
