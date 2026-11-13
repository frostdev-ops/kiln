// SPDX-License-Identifier: MIT
// The kit's example of ordinary world code. It leaves out the capability checks a published mod would have (the tests
// that load it serve the world rows), so KILN121 (a capability asked first) is switched off for the file.
#pragma warning disable KILN121
using Kiln;

namespace TkWorld;

/// <summary>A water keeper: tops up everything that holds water, resizes a prop, and says what it did. Nothing in it
/// knows about tests: the same code runs in a server.</summary>
public sealed class TkWorldMod : Mod
{
    public override void OnLoad()
    {
        Events.On<ComponentWrittenEvent>(e => Log.Info($"event: {e.Component} of {e.Entity} written by {e.ModId}, now {Units(e.Result.After)}"));
        var fuel = World.WriteInfo<Ecs.Fuel>();
        Log.Info($"loaded: tracked writes {World.SupportsTrackedWrites}; Fuel {fuel.State}, attribute {fuel.Attribute}, " +
                 $"{fuel.GameWriterCount} game writer(s); Stamina {World.WriteInfo("Stamina").State}");
    }

    private static string Units(ReadOnlyMemory<byte> component) => component.Length >= 12 ? BitConverter.ToInt32(component.Span[8..]).ToString() : "?";

    [Command("tk-fill", Description = "set the water of everything that holds some")]
    private async Task Fill(CommandContext c, int units)
    {
        var filled = 0;
        foreach (var holder in World.Query<Ecs.Fuel>())
        {
            var r = await World.Update<Ecs.Fuel>(holder, (ref Ecs.Fuel f) => f.Values[0] = Math.Min(units, f.Values[1]));
            Log.Info($"fill {holder}: {r}; steps {r.Steps}; writers [{string.Join(", ", r.GameWriters)}]; save {r.SaveTracked?.ToString() ?? "?"}");
            if (r.Changed) filled++;
        }
        Log.Info($"filled {filled}");
    }

    [Command("tk-read", Description = "what one entity holds")]
    private string Read(CommandContext c, int entity)
    {
        var e = new EntityId((uint)entity);
        var typed = Ecs.TryGet<Ecs.Fuel>(e, out var fuel) ? $"{fuel.Values[0]}/{fuel.Values[1]}" : "none";
        var reflected = Reflected.Component(e, "Fuel")?["dataStorage[0]"]?.ToString() ?? "none";
        return $"fuel {typed}; reflected {reflected}; has scale {World.Has(e, "ScaleComponent")}; at {World.Position(e)?.ToString() ?? "nowhere"}; " +
               $"exists {World.Exists(e)}";
    }

    [Command("tk-scale", Description = "resize one entity")]
    private async Task Scale(CommandContext c, int entity, float scale)
    {
        var r = await World.Write(new EntityId((uint)entity), new Ecs.ScaleComponent { uniformScale = scale });
        Log.Info($"scale: {r}; steps {r.Steps}; kept {r.Kept?.ToString() ?? "unchecked"}");
    }

    [Command("tk-header", Description = "a whole Fuel built from nothing")]
    private async Task Header(CommandContext c, int entity) => Log.Info($"header: {await World.Write(new EntityId((uint)entity), new Ecs.Fuel())}");

    [Command("tk-field", Description = "one field by its path")]
    private async Task Field(CommandContext c, int entity, int units) =>
        Log.Info($"field: {await World.WriteField(new EntityId((uint)entity), "Fuel", "dataStorage[0]", units)}");

    [Command("tk-stamina", Description = "a write of a type the manifest does not list")]
    private async Task Stamina(CommandContext c, int entity) => Log.Info($"stamina: {await World.WriteAt(new EntityId((uint)entity), "Stamina", 8, 5)}");

    [Command("tk-throw", Description = "a change that throws")]
    private async Task Throw(CommandContext c, int entity) =>
        Log.Info($"throw: {await World.Update<Ecs.Fuel>(new EntityId((uint)entity), (ref Ecs.Fuel f) => throw new InvalidOperationException("on purpose"))}");
}
