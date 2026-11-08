// SPDX-License-Identifier: MIT
// A sound a mod declares as a block: clips of its own and how they play. Nothing of it reaches the game from here: the cook makes the game's sound resources and
// the cue on every player's machine (tools/soundcook), from steps that `soundcook.py blocks` writes into assets/cook.json for the blocks of blocks/*.json, and kiln.assets
// serves them at boot. This checks the block, works out the cue's GUID the cook will give it, and says what is missing. docs/kiln/modding/sounds.md.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A sound of the mod's own: one or more clips (a cue that picks one at random each time), how loud, and how far it carries. An effect plays it by its name
/// (<c>Sound = "frost-hum"</c> or the handle), as a station's running sound, or code with <c>SoundId.Named</c>. Players need the mod: <c>clients: mod</c>.</summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record SoundBlock : IBlock
{
    /// <summary>Audio files in the mod's folder (WAV, FLAC, Ogg Vorbis, MP3): <c>["audio/frost_hum.wav"]</c>. More than one: each play picks one.</summary>
    public required IReadOnlyList<string> Clips { get; init; }
    /// <summary>0..1, the cue's volume. Null: the game's default, 1.</summary>
    public double? Volume { get; init; }
    /// <summary>Metres <c>[min, max]</c>: full volume within min, silent beyond max. Null: not positional, heard alike everywhere (UI, music).</summary>
    public double[]? Range { get; init; }
    /// <summary>It plays until stopped, as a station's running sound or an ambience.</summary>
    public bool? Loop { get; init; }
    /// <summary>The player's volume slider it follows: <c>Sfx</c>, <c>Music</c>, <c>Ui</c> or <c>Voice</c>. Null: the cook's default.</summary>
    public string? Scene { get; init; }
    /// <summary>0..1: how much each play's pitch varies.</summary>
    public double? PitchRandomness { get; init; }
    /// <summary>Its sources' level, as the cook sets it: the active RMS in dBFS (the game's effects sit near -23).</summary>
    public double? Normalize { get; init; }
}

internal static partial class Compiler
{
    private static readonly string[] AudioExtensions = [".wav", ".flac", ".ogg", ".mp3"];
    private static readonly string[] Scenes = ["Sfx", "Music", "Ui", "Voice"];
    private static readonly Guid BlocksNamespace = new("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77");

    private static BlockHandle DefineSound(BlockContext ctx, Lowered low, string name, SoundBlock s)
    {
        ctx.Need(ClientsLevel.Mod, null, "a sound of the mod's own has to be on every player's game: nothing can send it");
        if (s.Clips is not { Count: > 0 }) throw new BlockError(ctx.Block, "clips", "a sound needs one or more audio files");
        if (s.Volume is { } volume && (!double.IsFinite(volume) || volume is < 0 or > 1)) throw new BlockError(ctx.Block, "volume", $"{s.Volume} is outside 0..1");
        if (s.PitchRandomness is { } pitch && (!double.IsFinite(pitch) || pitch is < 0 or > 1)) throw new BlockError(ctx.Block, "pitchRandomness", $"{s.PitchRandomness} is outside 0..1");
        if (s.Normalize is { } normalize && !double.IsFinite(normalize)) throw new BlockError(ctx.Block, "normalize", "must be finite dBFS");
        if (s.Range is { } r && (r.Length != 2 || r.Any(v => !double.IsFinite(v) || v > float.MaxValue) || r[0] < 0 || r[1] <= r[0]))
            throw new BlockError(ctx.Block, "range", $"[{string.Join(", ", r)}]: [min, max] in metres, max more than min");
        if (s.Scene is { } scene && !Scenes.Contains(scene)) throw new BlockError(ctx.Block, "scene", $"\"{scene}\" is not a scene; there are {string.Join(", ", Scenes)}");
        var dir = BlocksBridge.ModDirectory(ctx.Mod.Id);
        for (var i = 0; i < s.Clips.Count; i++)
        {
            var clip = s.Clips[i];
            if (clip == null || !AudioExtensions.Any(e => clip.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                throw new BlockError(ctx.Block, $"clips[{i}]", $"\"{clip}\" is not an audio file in the mod's folder (wav, flac, ogg, mp3), with a relative path");
            BlockFiles.InMod(ctx, clip, $"clips[{i}]", dir);
        }
        var (cue, recipe) = SoundCue(ctx.Mod.Id, dir, name);
        low.Json["sound"] = new JsonObject { ["cue"] = cue.ToString(), ["clips"] = new JsonArray(s.Clips.Select(c => (JsonNode?)c).ToArray()) };
        if (dir.Length > 0 && !recipe)
            lock (ctx.Mod.Warnings)
                ctx.Mod.Warnings.Add($"block {ctx.Block}: assets/cook.json has no cue \"{name}\", so nothing plays it yet. Run kiln cook: a sound in blocks/*.json is written into the recipe by itself "
                                     + "(tools/soundcook/soundcook.py blocks); one in code needs its steps in assets/cook.json (docs/kiln/modding/sounds.md, Sound blocks)");
        return new BlockHandle<SoundBlock>(ctx.Mod.Id, name, cue, name, s);
    }

    /// <summary>The GUID the cook gives cue <paramref name="name"/>: UUIDv5 of the recipe's guidNamespace and <c>cue:&lt;name&gt;</c>; without a recipe, of the namespace
    /// `soundcook.py blocks` puts in the recipe it makes. And whether the recipe has that cue already.</summary>
    internal static (Guid Cue, bool InRecipe) SoundCue(string mod, string dir, string name)
    {
        Guid? ns = null;
        var have = false;
        var file = dir.Length > 0 ? Path.Combine(dir, "assets", "cook.json") : "";
        if (file.Length > 0 && File.Exists(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("guidNamespace", out var g) && Guid.TryParse(g.GetString(), out var parsed)) ns = parsed;
                if (doc.RootElement.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                    have = steps.EnumerateArray().Any(st => st.TryGetProperty("op", out var op) && op.GetString() is "cue" or "sound" && st.TryGetProperty("name", out var n) && n.GetString() == name
                                                            && (op.GetString() == "cue" || st.TryGetProperty("cue", out _)));
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return (Uuid5(ns ?? Uuid5(BlocksNamespace, $"{mod}:sounds"), $"cue:{name}"), have);
    }

    private static Guid Uuid5(Guid ns, string name)
    {
        Span<byte> n = stackalloc byte[16];
        ns.TryWriteBytes(n, bigEndian: true, out _);
        var text = Encoding.UTF8.GetBytes(name);
        var all = new byte[16 + text.Length];
        n.CopyTo(all);
        text.CopyTo(all, 16);
        var h = SHA1.HashData(all);
        h[6] = (byte)((h[6] & 0x0f) | 0x50);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }
}
