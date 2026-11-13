using Kiln;

namespace KilnCreature1;

/// <summary>The creature itself is data (content/creature.json). This code checks that the clone registered and adds
/// "/kiln-mod-id [clear]" (admins): one creature 8 m east of you, spawned like any game template (kiln.spawn).</summary>
public sealed class KilnCreature1Mod : Mod
{
    private const string Template = "KilnCreature1_Elite";
    private readonly List<EntityId> _spawned = [];
    private CommandRegistration? _command;

    public override void OnLoad()
    {
        _command = Commands.Register("kiln-mod-id", Spawn1, CommandPermission.Admin, usage: "[clear]",
            description: $"spawn one {Template} 8 m away (clear: remove them)");
        // Content.OnReady needs kiln.content, the pack's data kiln.gamedata: two services, so two questions.
        if (!Content.Available || !Content.GameDataAvailable)
        {
            Log.Warn("kiln.content or kiln.gamedata is not available on this build: the creature pack does nothing");
            return;
        }
        Content.OnReady(_ =>
        {
            var t = Content.Templates.Get(Template);
            Log.Info(t is { IsClone: true }
                ? $"{t.Name} ({t.Guid}) registered: level {t.Get("Level.dataStorage[0]")}, scale {t.Get("ScaleComponent.uniformScale")}"
                : $"{Template} is not registered (see kiln.log for the pack's problems)");
        });
    }

    public override void OnUnload() => _command?.Dispose();

    private void Spawn1(CommandContext c)
    {
        if (c.Count > 0 && c.Args[0] == "clear")
        {
            c.Reply($"{_spawned.Count(Spawn.Destroy)} removed.");
            _spawned.Clear();
            return;
        }
        // From the console the first player in the game stands in, and only kiln.players knows who that is.
        if (c.Source.Player.IsNone && !Players.Available) throw new CommandException("no player to spawn it next to (kiln.players is missing)");
        var who = !c.Source.Player.IsNone ? c.Source.Player : Players.All.FirstOrDefault()?.Entity ?? EntityId.None;
        if (who.IsNone || World.Position(who) is not { } p) throw new CommandException("no player to spawn it next to");
        _ = SpawnAsync(p with { X = p.X + 8 });
        c.Reply($"A {Template} appears 8 m away.");
    }

    private async Task SpawnAsync(WorldPosition pos)
    {
        try
        {
            var e = await Spawn.Entity(Template, pos, 0, new SpawnOptions { Tag = "kiln-mod-id", Flags = SpawnFlags.Transient });
            _spawned.Add(e);
            Log.Info($"{Template} {e} at {pos}");
        }
        catch (Exception ex)
        {
            Log.Warn($"spawn failed: {ex.Message}");
        }
    }
}
