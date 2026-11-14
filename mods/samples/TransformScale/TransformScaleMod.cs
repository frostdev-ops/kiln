// SPDX-License-Identifier: MIT
// Transform scale: an entity drawn larger or smaller, on each axis on its own (docs/kiln/services/world.md, "Transform
// scale"). ShroudForge's scale setter, in C#.
//   /tscale <entity> <x> <y> <z>   set the scale (1 1 1 is the game's size)
//   /tscale <entity>               show it
// What to take from it: the capability is asked first (a build whose transform has no scale of a known shape turns it
// off); a factor must be finite and above zero; the answer says the scale before and after, and whether the game's write
// tracking marks CurrentTransform for the save; the change event fires for a write that changed the vector. It is the
// rendered transform only: colliders and attachments keep their size (a creature's uniform size: CreatureRef.Scale).
using Kiln;

namespace TransformScale;

public sealed class TransformScaleMod : Mod
{
    public override void OnLoad()
    {
        Log.Info($"transform-scale: {Capabilities.Why(World.TransformScaleCapability)}");
        Events.On<EntityScaleChangedEvent>(e => Log.Info($"transform-scale: {e}"));
    }

    [Command("tscale", Description = "an entity's transform scale: <entity> to show it, <entity> <x> <y> <z> to set it", Permission = CommandPermission.Admin)]
    private async Task Scale(CommandContext c, long entity, float x = float.NaN, float y = float.NaN, float z = float.NaN)
    {
        if (!Capabilities.Is(World.TransformScaleCapability))
            throw new CommandException($"no transform scale here: {Capabilities.Why(World.TransformScaleCapability)}");
        if (!Capabilities.Is(World.TrackedWritesCapability))
            throw new CommandException($"no tracked writes here: {Capabilities.Why(World.TrackedWritesCapability)}");
        if (entity is < 1 or > uint.MaxValue) throw new CommandException("an entity id is a number from 1 to 4294967295");
        var id = new EntityId((uint)entity);
        if (float.IsNaN(x))
        {
            c.Reply(World.WriteInfo("CurrentTransform") is { CanWrite: true } ? $"{id} can be scaled" : $"{id}: {World.WriteInfo("CurrentTransform").Reason}");
            return;
        }
        var scale = new Scale3(x, float.IsNaN(y) ? x : y, float.IsNaN(z) ? x : z);
        if (!scale.IsValid) throw new CommandException("each factor finite and above 0 (1 is the game's size)");
        var r = await World.SetTransformScale(id, scale);
        c.Reply(r.Ok
            ? $"{id}: {r.Before} -> {r.Now} ({r.WorldWriteStatus}; {(r.SaveTracked == true ? "marked for the save" : "the save may not keep it")})"
            : $"{id} was not scaled: {r.Reason}");
    }
}
