// SPDX-License-Identifier: MIT
using Kiln;

namespace TkSaves;

public sealed class Box
{
    public int Value { get; set; }
}

public sealed class WorldSettings
{
    [Comment("A per-world setting.")] public string Label { get; set; } = "default";
}

/// <summary>Saved data through commands (named &lt;mod id&gt;-&lt;verb&gt;), so a test reads it as this mod (its id is the store's scope): world, player and
/// entity data, a per-world config, and its own save request. Logs the save events and what it found at load.</summary>
public sealed class TkSavesMod : Mod
{
    public override void OnLoad()
    {
        var box = WorldData.Get<Box>("box");
        Log.Info($"load: world {WorldData.WorldId} loaded {WorldData.Loaded} box {box.Value} keys {string.Join(",", WorldData.Keys)}");
        var settings = WorldConfig<WorldSettings>();
        Log.Info($"config: {settings.Value.Label} at {settings.FilePath}");
        var c = Context.Info.Id;  // commands of their own when the mod is loaded twice under two ids
        Commands.Register($"{c}-set", ctx => WorldData.Get<Box>(ctx.Args[0]).Value = int.Parse(ctx.Args[1]));
        Commands.Register($"{c}-get", ctx => ctx.Reply(WorldData.TryGet<Box>(ctx.Args[0], out var b) ? $"{b.Value}" : "none"));
        Commands.Register($"{c}-pset", ctx => PlayerData.Get<Box>(ulong.Parse(ctx.Args[0]), "box").Value = int.Parse(ctx.Args[1]));
        Commands.Register($"{c}-pget", ctx => ctx.Reply(PlayerData.TryGet<Box>(ulong.Parse(ctx.Args[0]), "box", out var b) ? $"{b.Value}" : "none"));
        Commands.Register($"{c}-eset", ctx => EntityData.Get<Box>(new EntityId(uint.Parse(ctx.Args[0])), "box").Value = int.Parse(ctx.Args[1]));
        Commands.Register($"{c}-eget", ctx => ctx.Reply(EntityData.TryGet<Box>(new EntityId(uint.Parse(ctx.Args[0])), "box", out var b) ? $"{b.Value}" : "none"));
        Commands.Register($"{c}-entity-available", ctx => ctx.Reply($"{EntityData.Available}"));
        Commands.Register($"{c}-config", ctx => ctx.Reply($"{settings.Value.Label} at {settings.FilePath}"));
        Commands.Register($"{c}-save", ctx => { _ = SaveAndLog(); });
    }

    private async Task SaveAndLog()
    {
        var r = await Saves.Request();
        Log.Info($"request done: ok {r.Success} revision {r.Revision} reason {r.Reason} tick thread {Game.IsTickThread}");
    }

    public override void OnUnload() => Log.Info("unload");

    [Subscribe]
    private void OnSaving(WorldSavingEvent e) => Log.Info($"saving: {e.Reason} revision {e.Revision} world {e.WorldId}");

    [Subscribe]
    private void OnSaved(WorldSavedEvent e) => Log.Info($"saved: {e.Reason} ok {e.Success} revision {e.Revision}");

    [Subscribe]
    private void OnLoaded(WorldLoadedEvent e) => Log.Info($"world loaded: {e.WorldId} revision {e.Revision} new {e.NewWorld} data {e.Data}");
}
