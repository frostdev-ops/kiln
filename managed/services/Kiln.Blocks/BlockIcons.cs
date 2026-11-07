// SPDX-License-Identifier: MIT
// A PNG icon of a block's own on a status or a perk. Both name a keen::UiTexture by GUID, the same kind of texture an item's icon is, so the route is
// the item icons' one: kiln.assets serves the PNG under a GUID derived from the block, and the game data names that GUID. The UI cache stops the game on
// a key it doesn't know, and it learns a registered icon's key a few frames after the registration: until then kiln.content's texture hook looks the
// key up as the starter's icon (an alias, as a new item's icon falls back to its base item's), so the buff bar and tooltips draw the starter's icon
// at worst. A server draws nothing: it only names the GUID, and players get that name through the content sync; they need the mod (clients: mod).
using System.Text.Json.Nodes;

namespace Kiln;

internal static class BlockIcons
{
    /// <summary>The largest PNG kiln.assets takes as an icon.</summary>
    internal const int MaxSide = 1024;

    /// <summary>The icon texture's GUID: from the mod, the section and the block's oldest name, so saves and players agree.</summary>
    public static Guid TextureGuid(string mod, string section, string key) => BlockIds.Derive(mod, "icon", $"{section}:{key}");

    /// <summary>A PNG in the mod's folder, checked as kiln.assets will take it: its full path. Throws a <see cref="BlockError"/> naming the field.</summary>
    public static string CheckPng(BlockContext ctx, string icon, string path)
    {
        if (Path.IsPathRooted(icon) || icon.Replace('\\', '/').Split('/').Contains(".."))
            throw new BlockError(ctx.Block, path, $"\"{icon}\": a PNG icon is a file in the mod's folder, named from there (icons/frost.png)");
        var dir = BlocksBridge.ModDirectory(ctx.Mod.Id);
        if (dir.Length == 0) return icon;   // a mod the loader doesn't list (a unit test): nothing to look at
        var file = Path.GetFullPath(Path.Combine(dir, icon));
        if (!File.Exists(file))
            throw new BlockError(ctx.Block, path, $"\"{icon}\" is not in the mod's folder. The build copies a mod's folders to where it runs: icons/ is one of them (KilnModFolders)");
        if (PngSize(file) is not var (w, h))
            throw new BlockError(ctx.Block, path, $"\"{icon}\" is not a PNG");
        if (w > MaxSide || h > MaxSide || w == 0 || h == 0)
            throw new BlockError(ctx.Block, path, $"\"{icon}\" is {w}x{h}: an icon is at most {MaxSide}x{MaxSide} (256x256 as the game's)");
        return file;
    }

    /// <summary>Width and height from a PNG's header, or null when it is none.</summary>
    internal static (int W, int H)? PngSize(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            Span<byte> b = stackalloc byte[24];
            if (f.Read(b) < 24) return null;
            ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (!b[..8].SequenceEqual(sig) || b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return null;
            return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b[16..]), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b[20..]));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>A status's or a perk's own PNG: checked now, and once the block is in, served (client) and named (both sides) when content is ready.</summary>
    /// <param name="section"><c>buffs</c> or <c>perks</c>.</param>
    /// <param name="entry">The clone's name.</param>
    /// <param name="starter">The game entry the clone starts from: its icon is what shows until the PNG is served.</param>
    public static Guid Own(BlockContext ctx, Lowered low, string section, string entry, GameDataRegistry registry, string starter, string icon, string key, string path)
    {
        ctx.Need(ClientsLevel.Mod, path, "a PNG icon has to be on every player's game: nothing can send it (a game " + (section == "buffs" ? "status's icon, a GameBuffs constant," : "perk's icon, a GamePerks constant,") + " asks no more than kiln)");
        var file = CheckPng(ctx, icon, path);
        var texture = TextureGuid(ctx.Mod.Id, section, key);
        low.Json["icon"] = new JsonObject { ["file"] = icon, ["texture"] = texture.ToString() };
        var mod = ctx.Mod;
        var block = ctx.Block;
        low.AfterCommit(() => BlockRuntime.AddOwnIcon(mod, new OwnIcon(section, entry, registry, starter, texture, file, block)));
        return texture;
    }

    /// <summary>On a client, kiln.assets takes the PNG under the texture GUID. The state is kiln.assets' to say: <see cref="Assets.IconStateOf"/>.</summary>
    public static void Serve(ModBlocks mod, OwnIcon icon, Action<string> problem)
    {
        if (!BlocksBridge.IsClient || icon.Served) return;
        icon.Served = true;
        var rc = Assets.RegisterIconAs(mod.Owner, icon.Texture, icon.File);
        if (rc != 0) problem($"block {icon.Block}, icon: kiln.assets refused {Path.GetFileName(icon.File)} ({rc}): the starter's icon shows");
        else BlocksBridge.Log(LogLevel.Info, $"blocks: {mod.Id}: icon {Path.GetFileName(icon.File)} of {icon.Entry} registered as UI texture {icon.Texture}");
    }

    /// <summary>The rule that names the texture (a <c>keen::UiTexture</c>: <c>{"image": guid}</c>), for content's ready: on a client only once the texture key is safe to name.</summary>
    public static JsonNode? Rule(ModBlocks mod, OwnIcon icon, Action<string> problem)
    {
        if (BlocksBridge.IsClient && !icon.Aliased)
        {
            // what the starter shows, until the cache knows ours
            if (Guid(icon.Registry.Get(icon.Starter)?.GetJson("icon")?["value"]) is not { } fallback)
            {
                problem($"block {icon.Block}, icon: the game's {(icon.Section == "buffs" ? "status" : "perk")} {icon.Starter} has no icon to show until the PNG is served: the starter's stays");
                return null;
            }
            if (!BlocksBridge.IconAlias(icon.Texture, fallback))
            {
                // without the fallback, naming a key the cache doesn't know yet could stop the game: name it only once it is served
                if (Assets.IconStateOf(icon.Texture) != IconState.Served) { mod.RulesDirty = true; return null; }
            }
            else icon.Aliased = true;
        }
        return new JsonObject { ["image"] = icon.Texture.ToString() };
    }

    private static Guid? Guid(JsonNode? v) =>
        v is JsonValue j && j.TryGetValue<string>(out var s) && System.Guid.TryParse(s, out var g) ? g
        : v is JsonObject o && o["image"] is JsonValue jg && jg.TryGetValue<string>(out var s2) && System.Guid.TryParse(s2, out var g2) ? g2 : null;
}

/// <summary>A PNG icon of a block's own, waiting for content to be ready.</summary>
internal sealed record OwnIcon(string Section, string Entry, GameDataRegistry Registry, string Starter, Guid Texture, string File, string Block)
{
    public bool Served { get; set; }
    public bool Aliased { get; set; }
}
