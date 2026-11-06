// SPDX-License-Identifier: MIT
// Creature Tweaks: the changes are all in content/creaturetweaks.json: a templates rule for every Vukah (shared
// template data: every Vukah, spawned now or later, reads it), a template clone "Kiln_Elite_Vukah" (a new template
// GUID; kiln.gamedata registers it in the game's template table) and a kiln.creatures spawn rule that turns a share of
// natural melee Vukah spawns into elites. This code logs what the game uses and adds "/elitevukah" (admins): one Elite
// Vukah 8 m in front of the player, spawned like any template (kiln.spawn, by name).
using Kiln;

namespace CreatureTweaks;

public sealed class CreatureTweaksMod : Mod
{
    // how far it sees a target by day: the field the game's sight check reads (aggroSetup.viewRange is only the radius of its search for candidates)
    private const string ViewRange = "Enemy01BehaviorState.combatCommitmentSetup.view.range";

    private IDisposable? _command;

    public override void OnLoad()
    {
        _command = Commands.Register("elitevukah", SpawnElite, CommandPermission.Admin, usage: "[ally|clear] [dx dz]",
            description: "spawn one Elite Vukah 8 m away (ally: on your side; clear: remove them)");
        if (!Content.GameDataAvailable)
        {
            Log.Warn("kiln.gamedata is not available on this build: the pack does nothing");
            return;
        }
        Content.OnReady(_ =>
        {
            var vukah = Content.Templates.Get("Enemy_Vukah_Melee");
            var elite = Content.Templates.Get("Kiln_Elite_Vukah");
            var changed = Content.Templates.Count(t => t.Name.StartsWith("Enemy_Vukah_", StringComparison.Ordinal) && t.Modified);
            Log.Info($"Vukah view range {vukah?.Get(ViewRange)} m (vanilla {vukah?.Original(ViewRange)}; {changed} Vukah templates changed); " +
                     (elite is { IsClone: true }
                         ? $"{elite.Name} ({elite.Guid}) registered: level {elite.Get("Level.dataStorage[0]")}, scale {elite.Get("ScaleComponent.uniformScale")}, view range {elite.Get(ViewRange)} m"
                         : "the Elite Vukah template is not registered (see kiln.log)"));
        });
    }

    public override void OnUnload() => _command?.Dispose();

    private readonly List<EntityId> _spawned = [];

    // /elitevukah [ally|clear]: one Elite Vukah 8 m away (ally: faction PlayerAlly, it fights for you); clear: removes
    // the ones this command spawned.
    private void SpawnElite(CommandContext c)
    {
        var arg = c.Count > 0 ? c.Args[0].ToLowerInvariant() : "";
        if (arg == "clear")
        {
            var n = _spawned.Count(Spawn.Destroy);
            _spawned.Clear();
            c.Reply($"{n} Elite Vukah(s) removed.");
            return;
        }
        // a player: next to them; the console (kiln/control/console, offline worlds have no chat): next to the local player
        var who = !c.Source.Player.IsNone ? c.Source.Player
            : Kiln.Client.IsClient && Kiln.Client.HasLocalPlayer ? Kiln.Client.LocalPlayer
            : Players.All.FirstOrDefault()?.Entity ?? EntityId.None;  // a dedicated server's console: the first player
        if (who.IsNone || World.Position(who) is not { } p) throw new CommandException("no player to spawn it next to");
        // optional offsets in metres: /elitevukah [ally] <dx> <dz> (default 8 m along +x)
        var template = c.Args.FirstOrDefault(a => a.StartsWith("template:", StringComparison.Ordinal))?[9..] ?? "Kiln_Elite_Vukah";
        var nums = c.Args.Skip(arg == "ally" ? 1 : 0).Where(a => !a.StartsWith("template:", StringComparison.Ordinal)).Select(a => double.TryParse(a, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        var (dx, dz) = nums.Length >= 2 && !double.IsNaN(nums[0]) && !double.IsNaN(nums[1]) ? (nums[0], nums[1]) : (8.0, 0.0);
        _ = SpawnAsync(p with { X = p.X + dx, Z = p.Z + dz }, arg == "ally", template);
        c.Reply(arg == "ally" ? "An allied Elite Vukah appears 8 m away." : "An Elite Vukah appears 8 m away.");
    }

    private async Task SpawnAsync(WorldPosition pos, bool ally, string template = "Kiln_Elite_Vukah")
    {
        try
        {
            var options = new SpawnOptions
            {
                Tag = "creaturetweaks", Flags = SpawnFlags.Transient,
                Components = ally ? [SpawnComponent.Of("Faction", [11])] : null,  // keen::FactionType PlayerAlly = 11
            };
            var e = await Spawn.Entity(template, pos, 0, options);
            _spawned.Add(e);
            Log.Info($"Elite Vukah {e} ({template}) at {pos}{(ally ? " (ally)" : "")}; exists {World.Exists(e)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Elite Vukah spawn failed: {ex.Message}");
        }
    }
}
