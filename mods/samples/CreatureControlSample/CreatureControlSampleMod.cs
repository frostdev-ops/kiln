// SPDX-License-Identifier: MIT
// kiln.creature_control from a mod (docs/kiln/services/creature-control.md). Two ways to move a creature the game's AI
// would otherwise steer:
//   /hold [seconds]  a lease of your own on a game Vukah: Acquire, WaitActive, Inspect, Release. While the lease drives
//                    the body its AI is off and its SteeringAgent removed, so it stands where it is; Release gives both
//                    back and the Vukah is itself again.
//   /pacer           a creature block with a brain: Kiln.Blocks takes the lease for it (you write no lease code), and the
//                    brain's MoveTo walks the body. It walks to the nearest enemy it sees (you) and stops 3 m away.
using Kiln;

namespace CreatureControlSample;

public sealed class CreatureControlSampleMod : Mod
{
    private readonly HashSet<ulong> _held = [];

    public override void OnLoad()
    {
        Blocks.Define("pacer", new CreatureBlock
        {
            Name = "Pacer",
            Body = Bodies.Vukah,
            AI = AIs.Vukah with { Sight = 30, Faction = Faction.Vukah },
            Behaviour = new BehaviourBlock { Brain = () => new Pacer() },
        });
        // every change of a lease taken through the managed face, the step after the broker made it: log ours
        Events.On<ControlChangedEvent>(e =>
        {
            if (_held.Contains(e.Lease.Value))
                Log.Info($"lease {e.Lease.Value}: {e.Before?.State.ToString() ?? "-"} -> {e.After.State} ({e.After.Reason})");
        });
    }

    [Command("hold", Description = "a Vukah 8 m from you, held still by a creature-control lease for a while (default 10 s)", Permission = CommandPermission.Admin)]
    private async Task Hold(CommandContext c, double seconds = 10)
    {
        if (c.Source.Player.IsNone || World.Position(c.Source.Player) is not { } at)
            throw new CommandException("only a player in the world can do this");
        if (!CreatureControl.Available || !Capabilities.Is("creature_control"))
            throw new CommandException($"the creature control broker is off: {Capabilities.Why("creature_control")}");
        if (!Capabilities.Is("spawn.world"))
            throw new CommandException($"spawning is off: {Capabilities.Why("spawn.world")}");
        var e = await Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(at.X + 8, at.Y, at.Z), 270);

        // admitted is not granted: the broker switches the AI off, waits for the current action to end, then takes the
        // SteeringAgent; WaitActive answers when the lease drives (a body far from every player never gets ready)
        var r = CreatureControl.Acquire(new EntityRef(e, 0), ControlRole.PetStay, CreatureControl.NewRequestId());
        if (!r.Ok)
        {
            c.Reply($"hold: refused: {r.Reason}");
            return;
        }
        _held.Add(r.Lease.Value);
        var active = await CreatureControl.WaitActive(r.Lease, 10);
        if (!active.Ok)
        {
            // a timed-out wait keeps the lease: give it up
            var gaveUp = await CreatureControl.Release(r.Lease);
            c.Reply($"hold: {e} is not held: {active.Reason}{(gaveUp.Ok ? "" : $"; release: {gaveUp.Reason}")}");
            _held.Remove(r.Lease.Value);
            return;
        }
        c.Reply($"hold: {e} is held for {seconds:0} s (lease {r.Lease.Value}, {active.State!.EffectiveRole}): its AI is off and it stands");
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120)));

        // a higher lease (a rider) could have taken the body meanwhile: Inspect says who drives it now
        if (CreatureControl.Inspect(r.Lease) is { CanDrive: false } now && !now.IsTerminal)
            c.Reply($"hold: lease {r.Lease.Value} is {now.State} ({now.Reason})");
        var end = await CreatureControl.Release(r.Lease);
        _held.Remove(r.Lease.Value);
        c.Reply(end.Ok ? $"hold: {e} is given back (its SteeringAgent and AI restored)" : $"hold: {e}: {end.Reason}");
    }

    [Command("pacer", Description = "a Vukah driven by a brain, 12 m from you: it walks to you", Permission = CommandPermission.Admin)]
    private async Task SpawnPacer(CommandContext c)
    {
        if (c.Source.Player.IsNone || World.Position(c.Source.Player) is not { } at)
            throw new CommandException("only a player in the world can do this");
        var e = await Blocks.Spawn("pacer", new WorldPosition(at.X + 12, at.Y, at.Z), 270);
        c.Reply($"pacer: {e}");
    }
}

/// <summary>Walks to the nearest enemy it sees and stops 3 m from it, facing it; walks again once it is 4.5 m away.</summary>
internal sealed class Pacer : NpcBrain
{
    private bool _there;

    public override void Tick(NpcControl npc)
    {
        if (npc.Target.IsNone || World.Position(npc.Target) is not { } to || npc.TargetDistance is not { } d)
        {
            npc.Stop();
            return;
        }
        _there = _there ? d < 4.5 : d <= 3;
        if (_there)
        {
            npc.Stop();
            npc.Face(to);
        }
        else npc.MoveTo(to);
    }
}
