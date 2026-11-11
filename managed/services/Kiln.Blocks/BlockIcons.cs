// SPDX-License-Identifier: MIT
// A PNG icon of a block's own on a status or a perk. Both name a keen::UiTexture by GUID, the same kind of texture an item's icon is, so the route is
// the item icons' one: kiln.assets serves the PNG under a GUID derived from the block, and the game data names that GUID. The UI cache stops the game on
// a key it doesn't know, and it learns a registered icon's key a few frames after the registration: until then kiln.content's texture hook looks the
// key up as the starter's icon (an alias, as a new item's icon falls back to its base item's), so the buff bar and tooltips draw the starter's icon
// at worst. A server draws nothing: it only names the GUID, and players get that name through the content sync; they need the mod (clients: mod).
using System.Text.Json.Nodes;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Kiln;

internal static class BlockIcons
{
    /// <summary>The largest PNG kiln.assets takes as an icon.</summary>
    internal const int MaxSide = 1024;

    /// <summary>The icon texture's GUID: from the mod, the section and the block's oldest name, so saves and players agree.</summary>
    public static Guid TextureGuid(string mod, string section, string key) => BlockIds.Derive(mod, "icon", $"{section}:{key}");

    /// <summary>A PNG in the mod's folder, checked as kiln.assets will take it: its full path. Throws a <see cref="BlockError"/> naming the field.</summary>
    public static string CheckPng(BlockContext ctx, string icon, string path, string? directory = null)
    {
        var file = BlockFiles.InMod(ctx, icon, path, directory);
        var dir = directory ?? BlocksBridge.ModDirectory(ctx.Mod.Id);
        if (dir.Length == 0) return icon;   // a mod the loader doesn't list (a unit test): nothing to look at
        if (PngSize(file) is not var (w, h))
            throw new BlockError(ctx.Block, path, $"\"{icon}\" is not a PNG");
        if (w > MaxSide || h > MaxSide || w <= 0 || h <= 0)
            throw new BlockError(ctx.Block, path, $"\"{icon}\" is {w}x{h}: an icon is at most {MaxSide}x{MaxSide} (256x256 as the game's)");
        return file;
    }

    /// <summary>An item's icon as kiln.content takes it (a look's, a worn look's, a prop's, a material's block item's). A PNG is the file in the folder of the mod
    /// <paramref name="owner"/> (the block's by default), as its full path: kiln.content makes a relative path full only while that mod's code runs, a data file's
    /// blocks compile on a tick of Kiln's own, and kiln.assets reads a relative path against &lt;game&gt;/kiln. A full path, as a mod's code may give one, is taken
    /// when it names a file in that folder. Anything else (<c>item:&lt;name&gt;</c>, a GUID) is as written.</summary>
    public static string? ItemIcon(BlockContext ctx, string icon, string path, string? owner = null)
    {
        if (!icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return icon;
        var dir = BlocksBridge.ModDirectory(owner ?? ctx.Mod.Id);
        var file = Path.Combine(dir, icon.Replace('\\', '/'));
        try
        {
            file = Path.GetFullPath(file);
            if (dir.Length > 0 && Path.IsPathRooted(icon) && Path.GetRelativePath(dir, icon) is var inside && !Path.IsPathRooted(inside)
                && !inside.Replace('\\', '/').Split('/').Contains(".."))
                icon = inside;
            return BlockFiles.InMod(ctx, icon, path, dir);
        }
        catch (Exception e) when (e is BlockError or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An icon is optional. In particular, never turn items already in a player's save into placeholders for a bad picture.
            var text = $"block {ctx.Block}, {path}: {file}: {(e is BlockError b ? b.Problem : e.Message)}; the base icon stays";
            lock (ctx.Mod.Errors) ctx.Mod.Errors.Add(text);
            ctx.Mod.ReportDirty = true;
            BlocksBridge.Log(LogLevel.Error, $"blocks: {ctx.Mod.Id}: {text}");
            return null;
        }
    }

    /// <summary>A bounded, complete PNG (including the compressed scanlines), or null. The native decoder still makes the pixels.</summary>
    internal static (int W, int H)? PngSize(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            if (f.Length is < 45 or > 64 * 1024 * 1024) return null;
            var b = new byte[(int)f.Length];
            f.ReadExactly(b);
            ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (!b.AsSpan(0, 8).SequenceEqual(sig) || BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8)) != 13
                || !b.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return null;
            var w = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16));
            var h = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20));
            if (w <= 0 || h <= 0) return null;
            // Preserve the dimensions for the caller's size diagnostic without inflating an oversized image.
            if (w > MaxSide || h > MaxSide) return (w, h);
            var depth = b[24];
            var channels = b[25] switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
            var depths = b[25] switch { 0 => new[] { 1, 2, 4, 8, 16 }, 3 => new[] { 1, 2, 4, 8 }, _ => new[] { 8, 16 } };
            if (channels == 0 || !depths.Contains((int)depth) || b[26] != 0 || b[27] != 0 || b[28] > 1) return null;
            using var compressed = new MemoryStream();
            var palette = false;
            var ended = false;
            for (var at = 8; at <= b.Length - 12;)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
                if (length > b.Length - at - 12) return null;
                var n = (int)length;
                var type = b.AsSpan(at + 4, 4);
                uint crc = 0xffffffff;
                foreach (var v in b.AsSpan(at + 4, n + 4))
                {
                    crc ^= v;
                    for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
                }
                if (~crc != BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + n + 8))) return null;
                if (type.SequenceEqual("IDAT"u8)) compressed.Write(b, at + 8, n);
                else if (type.SequenceEqual("PLTE"u8)) palette = n is > 0 and <= 768 && n % 3 == 0;
                else if (type.SequenceEqual("IEND"u8)) { ended = n == 0 && at + 12 == b.Length; break; }
                else if (at != 8 && type.SequenceEqual("IHDR"u8)) return null;
                else if ((type[0] & 32) == 0 && !type.SequenceEqual("IHDR"u8)) return null; // unknown critical chunk
                at += n + 12;
            }
            if (!ended || compressed.Length == 0 || (b[25] == 3 && !palette)) return null;
            compressed.Position = 0;
            using var pixels = new ZLibStream(compressed, CompressionMode.Decompress);
            // Adam7 interlacing has seven separately filtered images.
            var passes = b[28] == 0 ? new[] { (0, 0, 1, 1) } : new[] { (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2) };
            foreach (var (x, y, dx, dy) in passes)
            {
                var pw = Math.Max(0, (w - x + dx - 1) / dx);
                var ph = Math.Max(0, (h - y + dy - 1) / dy);
                if (pw == 0 || ph == 0) continue;
                var row = new byte[(pw * channels * depth + 7) / 8];
                for (var line = 0; line < ph; line++)
                {
                    var filter = pixels.ReadByte();
                    if (filter is < 0 or > 4) return null;
                    pixels.ReadExactly(row);
                }
            }
            return pixels.ReadByte() == -1 ? (w, h) : null;
        }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
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
        var client = BlocksBridge.IsClient;
        return ReadyToName(icon, client, client ? Assets.IconStateOf(icon.Texture) : IconState.None,
            () => Guid(icon.Registry.Get(icon.Starter)?.GetJson("icon")?["value"]), BlocksBridge.IconAlias,
            () => mod.RulesDirty = true, problem) ? new JsonObject { ["image"] = icon.Texture.ToString() } : null;
    }

    internal static bool ReadyToName(OwnIcon icon, bool client, IconState state, Func<Guid?> starterIcon,
        Func<Guid, Guid, bool> alias, Action retry, Action<string> problem)
    {
        if (client && !icon.Aliased && state != IconState.Served)
        {
            // what the starter shows, until the cache knows ours
            if (starterIcon() is not { } fallback || fallback == System.Guid.Empty)
            {
                problem($"block {icon.Block}, icon: the game's {(icon.Section == "buffs" ? "status" : "perk")} {icon.Starter} has no icon to show until the PNG is served: the starter's stays");
                return false;
            }
            if (!alias(icon.Texture, fallback))
            {
                // without the fallback, naming a key the cache doesn't know yet could stop the game: name it only once it is served
                if (state == IconState.Pending) retry();
                return false;
            }
            else icon.Aliased = true;
        }
        return true;
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
