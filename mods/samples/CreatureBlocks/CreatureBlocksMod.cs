// SPDX-License-Identifier: MIT
// Creatures from building blocks (docs/kiln/modding/npc-blocks.md). The stoneling's body and animation are in blocks/stoneling.json, where the cook sees
// them; its mind is here. A Vukah keeps its own fighting and rallies once hurt, and a stone sentinel is driven by C# with the game's AI off.
using Kiln;

namespace CreatureBlocks;

public sealed class CreatureBlocksMod : Mod
{
    public override void OnLoad()
    {
        var crush = Blocks.Define("crush", new EffectBlock { Damage = Damage.Blunt(35), Knockback = Knockback.Small, Flinch = Flinch.Medium });
        var shards = Blocks.Define("shards", new EffectBlock { Area = new AreaBlock { Radius = 4, Effects = [new EffectBlock { Damage = Damage.Pierce(15) }] } });

        // a creature of the mod's own files: skeleton, clips, graph and model cooked from models/stoneling.glb
        var stoneling = (CreatureHandle)Blocks.Define("stoneling", new CreatureBlock
        {
            Name = "Stoneling",
            Body = "stone-body",
            Animation = "stone-moves",
            AI = AIs.Vukah with { Sight = 24, Aggro = 12, Faction = Faction.Vukah },
            Behaviour = new BehaviourBlock
            {
                Phases = ["cracked"],
                Attacks = [new AttackBlock { Name = "slam", Plays = "slam", Range = 2.8, HitAt = 0.55, Cooldown = 1.5, OnHit = [crush] }],
                Rules =
                [
                    // the game's own kinds, and one of Kiln's: data, no code
                    new RuleBlock { Name = "crack", Every = 0.5, Cooldown = 30, When = [Is.HealthBelow(0.5), Is.InPhase("cracked").Not()], Do = [Act.SetPhase("cracked"), Act.Effect(shards, NpcEffectOn.Self)] },
                    // code of your own: it runs on the tick thread, every second for each stoneling
                    new RuleBlock { Name = "taunt", Every = 1, Cooldown = 20, When = [Is.Custom(npc => npc.TargetDistance is < 6)], Do = [Act.Custom(npc => Log.Info($"stoneling {npc.Entity}: it grinds its stones at {npc.Target}"))] },
                ],
            },
            Stats = new CreatureStats { Level = 12, Takes = { [DamageKind.Blunt] = 1.5, [DamageKind.Pierce] = 0.5 } },
            Spawns = [new SpawnBlock { Replaces = "Enemy_Vukah_Melee", Chance = 0.1, NightOnly = true }],
        });

        // the game's Vukah with a rule of its own: vanilla players watch it with no mod
        Blocks.Define("rallying-vukah", new CreatureBlock
        {
            Body = Bodies.Vukah,
            Behaviour = Behaviours.VukahMelee with
            {
                Rules = [new RuleBlock { Name = "rally", Every = 1, Cooldown = 20, When = [Is.HealthBelow(0.5)], Do = [Act.Custom(npc => Combat.Heal(npc.Entity, npc.HealthMax / 4))] }],
            },
            Stats = new CreatureStats { Level = 15 },
        });

        // your code in place of the game's AI
        Blocks.Define("sentinel", new CreatureBlock
        {
            Name = "Stone sentinel",
            Body = "stone-body",
            Animation = "stone-moves",
            AI = AIs.Vukah with { Sight = 16, Faction = Faction.Vukah },
            Behaviour = new BehaviourBlock
            {
                Attacks = [new AttackBlock { Name = "slam", Plays = "slam", Range = 2.8, HitAt = 0.55, OnHit = [crush] }],
                Brain = () => new Sentinel(),
            },
        });

        Log.Info($"stonelings: {stoneling.Variant} on {stoneling.Template}");
    }

    [Command("stoneling", Description = "a stoneling 10 m from you", Permission = CommandPermission.Admin)]
    private async Task Spawn(CommandContext c) => await Near(c, "stoneling");

    [Command("sentinel", Description = "a stone sentinel 10 m from you", Permission = CommandPermission.Admin)]
    private async Task SpawnSentinel(CommandContext c) => await Near(c, "sentinel");

    private static async Task Near(CommandContext c, string creature)
    {
        if (c.Source.Player.IsNone || World.Position(c.Source.Player) is not { } at)
            throw new CommandException("only a player in the world can do this");
        var e = await Blocks.Spawn(creature, new WorldPosition(at.X + 10, at.Y, at.Z), 270);
        c.Reply($"{creature}: {e}");
    }
}

/// <summary>Stands its ground at the place it appeared: turns to anything that comes near, strikes within reach, and walks back when it has nothing to do.</summary>
internal sealed class Sentinel : NpcBrain
{
    private WorldPosition _post;

    public override void Start(NpcControl npc) => _post = npc.Position ?? default;

    public override void Tick(NpcControl npc)
    {
        if (npc.Target.IsNone || World.Position(npc.Target) is not { } at || at.DistanceTo(_post) > 12)
        {
            if (npc.Position is { } here && here.DistanceTo(_post) > 1) npc.MoveTo(_post);
            else npc.Stop();
            return;
        }
        if (npc.TargetDistance is < 2.8)
        {
            npc.Face(at);
            npc.Attack("slam");
        }
        else npc.MoveTo(at, run: true);
    }
}
