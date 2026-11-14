// SPDX-License-Identifier: MIT
// The consumer of a block kind: lanterns are written like any block. The handle is there at once; the items exist once the
// plan is applied, which Blocks.Application and BlockApplicationChangedEvent say.
using Kiln;
using Lanterns;

namespace CampLanterns;

public sealed class CampLanternsMod : Mod
{
    public override void OnLoad()
    {
        // where plans of a block kind cannot be applied, Define throws KilnUnavailableException naming the row (blocks.apply)
        BlockHandle camp;
        try
        {
            camp = Blocks.Define("camp", new LanternBlock { Name = "Camp lantern", Seconds = 600 });
        }
        catch (KilnUnavailableException e)
        {
            Log.Warn($"lanterns are off here: {e.Message}");
            return;
        }
        Log.Info($"camp lantern: {camp.GameName} ({Blocks.Application(camp).State})");

        Events.On<BlockApplicationChangedEvent>(e =>
        {
            if (e.ModId != Context.Info.Id) return;
            if (e.After.Ok) Log.Info($"lantern {e.Name} is in the game as {e.After.GameName}");
            else Log.Warn($"lantern {e.Name}: {e.After.State}, {e.After.Reason}");
        });
    }
}
