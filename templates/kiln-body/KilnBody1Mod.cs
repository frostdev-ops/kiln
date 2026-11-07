using Kiln;

namespace KilnBody1;

/// <summary>The creature is data (blocks/creature.json): its body and animation are files the build cooks. This code adds "/kiln-mod-id [clear]"
/// (admins): one creature 6 m east of you.</summary>
public sealed class KilnBody1Mod : Mod
{
    private readonly List<EntityId> _spawned = [];
    private CommandRegistration? _command;

    public override void OnLoad() =>
        _command = Commands.Register("kiln-mod-id", Spawn1, CommandPermission.Admin, usage: "[clear]", description: "spawn one kiln-mod-id 6 m away (clear: remove them)");

    public override void OnUnload() => _command?.Dispose();

    private void Spawn1(CommandContext c)
    {
        if (c.Count > 0 && c.Args[0] == "clear")
        {
            c.Reply($"{_spawned.Count(Spawn.Destroy)} removed.");
            _spawned.Clear();
            return;
        }
        var who = !c.Source.Player.IsNone ? c.Source.Player : Players.All.FirstOrDefault()?.Entity ?? EntityId.None;
        if (who.IsNone || World.Position(who) is not { } p) throw new CommandException("no player to spawn it next to");
        _ = SpawnAsync(p with { X = p.X + 6 });
        c.Reply("A kiln-mod-id appears 6 m away.");
    }

    private async Task SpawnAsync(WorldPosition pos)
    {
        try
        {
            var e = await Blocks.Spawn("kiln-mod-id", pos, 0, new CreatureSpawnOptions { Tag = "kiln-mod-id", Flags = SpawnFlags.Transient });
            _spawned.Add(e);
            Log.Info($"kiln-mod-id {e} at {pos}");
        }
        catch (Exception ex)
        {
            Log.Warn($"spawn failed: {ex.Message}");
        }
    }
}
