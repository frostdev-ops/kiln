// SPDX-License-Identifier: MIT
// Crafting props: a prop that recipes elsewhere can ask to stand near (PropBlock.Provides, RecipeBlock.NeedsProps).
//
// In the game a recipe may name props that have to be near the station that makes it. Every such prop is an entry of WorkshopRegistryResource.craftingProps (a GUID, an id that is FNV-1a of it, a
// place 0..63 in a 64-bit mask, a name and the item that places it); a placed thing is one by a RequiredCraftingProp component with that id; the station's CraftingInteraction collects the
// places of the props within its range into a mask, and a recipe runs only when its requiredPropsMask is in it (docs/re/building.md 2.4). The game uses 51 of the 64 places, so 13 are free for every
// mod together. The table is one resource for the whole game: kiln.worldtables merges the mods' props into it at boot and gives each the next free place, in the order of the props' GUIDs, so
// every game with the same mods agrees (the handshake hash covers them). A recipe of this mod names a prop by block: kiln.content gets its id and place. Native side:
// native/services/worldtables (merge.cpp, "craftingProps"), docs/kiln/services/worldtables.md.
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    // The factory craft path supplies no nearby-prop mask, even though its CraftingInteraction collects one. The normal player request does.
    private static bool FactoryStation(BlockContext ctx, StationRef station, string path)
    {
        if (StationKind.All.FirstOrDefault(k => string.Equals(k.Workshop, station.Value, StringComparison.OrdinalIgnoreCase)) is { } game) return game.Timed;
        if (station.Value == Stations.Hand || station.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        var name = station.Value;
        if (name.StartsWith(ctx.Mod.Id + ":", StringComparison.Ordinal)) name = name[(ctx.Mod.Id.Length + 1)..];
        if (ctx.Find(name, path) is not StationHandle h) return false;
        return (h.Block.Kind ?? (h.Block.Processing != null ? StationKind.DryingRack : StationKind.Workbench)).Timed;
    }

    /// <summary>The GUID of the crafting prop a block provides: from the mod and the block's name (or its <c>Was</c>), as kiln.worldtables derives it.</summary>
    internal static Guid CraftingPropGuid(string mod, string key) => BlockIds.Derive(mod, "craftingprop", key);

    /// <summary>The prop a handle is, when it is one: a prop, a station, or furniture.</summary>
    internal static PropBlock? PropOf(BlockHandle h) => h switch
    {
        BlockHandle<PropBlock> x => x.Block,
        BlockHandle<StationBlock> x => x.Block,
        BlockHandle<ContainerBlock> x => x.Block,
        BlockHandle<DoorBlock> x => x.Block,
        BlockHandle<BedBlock> x => x.Block,
        BlockHandle<SeatBlock> x => x.Block,
        _ => null,
    };

    // The prop's own part: a place in the game's table (asked of kiln.worldtables), and the component that makes the placed thing one.
    private static void ProvideProp(Placed p, PropBlock b)
    {
        var ctx = p.Item.Ctx;
        var low = p.Item.Low;
        ctx.Need(ClientsLevel.Mod, "provides", "a crafting prop has its place in a table that every game merges from the mods' files at its start: a player's game needs the mod");
        var guid = CraftingPropGuid(ctx.Mod.Id, p.Item.Key);
        var id = BlockIds.Fnv(guid);
        var (status, index) = AskCraftingProp(ctx, low, p.Item.Name, p.Item.Key, p.Item.Guid, b.Name);
        var json = new JsonObject { ["guid"] = guid.ToString(), ["id"] = $"0x{id:x8}", ["status"] = status.ToString().ToLowerInvariant() };
        if (index >= 0) json["index"] = index;
        low.Json["provides"] = json;
        // Until the table has it, nothing is made: a placed thing with an id the game's table doesn't know is one nothing of the game has answered for.
        if (status != TableBlockStatus.Registered)
        {
            p.Waiting = true;
            return;
        }
        Components(p, "RequiredCraftingProp");
        p.Fields["RequiredCraftingProp.craftingPropId"] = id;
    }

    /// <summary>kiln.worldtables' word on a crafting prop: registered (this game's table has it as defined), pending (kept, there from the next start), or off (this build keeps none).</summary>
    private static (TableBlockStatus Status, int Index) AskCraftingProp(BlockContext ctx, Lowered low, string name, string key, Guid item, string displayName)
    {
        var mod = ctx.Mod;
        mod.ManifestClients ??= BlocksBridge.ManifestClients(mod.Id);
        var block = new JsonObject { ["key"] = key, ["item"] = item.ToString(), ["name"] = displayName };
        var doc = new JsonObject { ["format"] = 1, ["mod"] = mod.Id, ["dir"] = BlocksBridge.ModDirectory(mod.Id), ["clients"] = mod.ManifestClients, ["craftingProps"] = new JsonObject { [name] = block } };
        var report = WorldTablesNative.DefineJson(doc.ToJsonString());
        JsonObject? r = null;
        try { r = report != null ? JsonNode.Parse(report) as JsonObject : null; }
        catch (System.Text.Json.JsonException) { }
        foreach (var e in r?["errors"] as JsonArray ?? [])
        {
            var (field, text) = TableError(e?.GetValue<string>() ?? "", name);
            throw new BlockError(ctx.Block, field ?? "provides", text);
        }
        foreach (var x in r?["blocks"] as JsonArray ?? [])
        {
            if ((string?)x?["name"] != name || (string?)x?["kind"] != "craftingProp") continue;
            var index = (int?)x["index"] ?? -1;
            switch ((string?)x["status"])
            {
                case "registered":
                    return (TableBlockStatus.Registered, index);
                case "pending":
                    Warn($"{name}: provides a crafting prop that is new to this game: its table is read once, at the start. The block is made from the next start");
                    return (TableBlockStatus.Pending, -1);
            }
        }
        Warn($"{name}: kiln.worldtables on this build keeps no crafting props: the block is checked and nothing is made");
        return (TableBlockStatus.Off, -1);

        void Warn(string text) =>
            low.AfterCommit(() =>
            {
                lock (mod.Warnings)
                    if (!mod.Warnings.Contains(text)) mod.Warnings.Add(text);
                mod.ReportDirty = true;
                BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: {text}");
            });
    }

    /// <summary>The crafting props a recipe asks for: each one's id and place in the game's table. Null: some prop has no place in this game's table yet, so the recipe waits for it (the
    /// block was made but its table is read at the start): the reason is in <paramref name="waiting"/>.</summary>
    private static List<RecipeProp>? NeededProps(BlockContext ctx, IReadOnlyList<PropRef>? refs, string path, out string? waiting)
    {
        waiting = null;
        var props = new List<RecipeProp>();
        if (refs is not { Count: > 0 }) return props;
        if (refs.Count > 8) throw new BlockError(ctx.Block, path, $"{refs.Count} props: a recipe asks for at most 8");
        var seen = new HashSet<Guid>();
        for (var i = 0; i < refs.Count; i++)
        {
            var at = $"{path}[{i}]";
            var name = refs[i].Value;
            if (string.IsNullOrWhiteSpace(name)) throw new BlockError(ctx.Block, at, "a prop's name: a block of this mod that provides one, or \"other-mod:name\"");
            // a handle of this very mod names itself "mod:name": the mod's own blocks are found by their short name, as it may not be registered as a mod yet
            if (name.StartsWith(ctx.Mod.Id + ":", StringComparison.Ordinal)) name = name[(ctx.Mod.Id.Length + 1)..];
            var h = ctx.Find(name, at) ?? throw new BlockError(ctx.Block, at, $"no block \"{name}\" in this mod; define the prop first, or say \"other-mod:{name}\"");
            var prop = PropOf(h) ?? throw new BlockError(ctx.Block, at, $"\"{name}\" is a {h.BlockType.Name.Replace("Block", "").ToLowerInvariant()}, not a prop");
            if (!prop.Provides) throw new BlockError(ctx.Block, at, $"\"{name}\" provides no crafting prop: say provides = true on it");
            var guid = CraftingPropGuid(h.Mod, prop.Was ?? h.Name);
            if (!seen.Add(guid)) continue;
            var index = WorldTablesNative.CraftingPropIndex(guid);
            if (index < 0)
            {
                waiting = $"{at}: \"{name}\" has no place in this game's table yet";
                return null;
            }
            props.Add(new RecipeProp(BlockIds.Fnv(guid), index));
        }
        return props;
    }
}
