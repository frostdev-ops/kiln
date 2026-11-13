// SPDX-License-Identifier: MIT
using Kiln;

namespace LightsOut;

/// <summary>
/// <c>/lights off</c> and <c>/lights on</c>: every lantern, torch and candle of the world, through the game's own write
/// tracking. A light switched this way looks the same to the game as one a player switched: the next save keeps it.
/// </summary>
public sealed class LightsOutMod : Mod
{
    private static readonly string[] Lights = ["Lantern", "Torch", "Candle", "FireflyBasket"];

    public override void OnLoad()
    {
        // What the game says about the type on this build, before anything is written.
        var info = World.WriteInfo<Ecs.ServerToggleState>();
        Log.Info(info.CanWrite
            ? $"lights-out: ready ({info.GameWriterCount} of the game's programs write ServerToggleState too; tracked for the save: {info.SaveTracked})"
            : $"lights-out: cannot switch lights: {info.Reason}");
        Events.On<ComponentWrittenEvent>(e => Log.Debug($"lights-out: {e.Component} of {e.Entity} changed"));
    }

    [Command("lights", Description = "switch every lantern, torch and candle: /lights off | on", Permission = CommandPermission.Admin)]
    private async Task Switch(CommandContext c, string state)
    {
        if (state is not ("on" or "off")) throw c.Usage("say on or off");
        if (!World.SupportsTrackedWrites) c.Fail(Capabilities.Why(World.TrackedWritesCapability));
        var on = state == "on";
        int switched = 0, already = 0;
        string? problem = null;
        foreach (var light in World.Query<Ecs.ServerToggleState>())
        {
            if (Spawn.Inspect(light)?.TemplateName is not { } template || !Lights.Any(template.Contains)) continue;
            // Update reads the component after the game's own programs of the step, changes it and writes it back tracked.
            var r = await World.Update<Ecs.ServerToggleState>(light, (ref Ecs.ServerToggleState s) => s.state = on);
            if (!r.Ok) problem ??= r.Reason;        // the entity went away, or the type is not listed: never an exception
            else if (r.Changed) switched++;
            else already++;
        }
        c.Reply(problem is null ? $"{switched} light(s) switched {state}, {already} already were"
            : $"{switched} light(s) switched {state}; one answer was: {problem}");
    }
}
