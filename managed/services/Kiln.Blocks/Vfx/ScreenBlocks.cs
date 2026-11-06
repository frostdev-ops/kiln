// SPDX-License-Identifier: MIT
// What a ScreenEffectBlock becomes. A screen is drawn by each client, so nothing goes into the game's data: an effect's program or a status's program
// calls kiln.Signal with a tag and who it is for, and the server sends the effect to that player's Kiln client (ScreenEffects, Kiln.Vfx).
//   effect on a target        -> the target's screen, for the block's own length
//   effect at a place         -> players within the block's Range of the place
//   status                    -> its player's screen from when it starts until it ends (the program stays alive for its shutdown code)
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    private static BlockHandle? DefineScreenBlock<T>(BlockContext ctx, Lowered low, string name, T block) where T : class, IBlock
    {
        if (block is not ScreenEffectBlock s) return null;
        ScreenBlocks.Check(ctx, s, "");
        low.Json["screenEffect"] = ScreenBlocks.Describe(s);
        var key = s.Was ?? name;
        return new BlockHandle<ScreenEffectBlock>(ctx.Mod.Id, name, BlockIds.Derive(ctx.Mod.Id, "screen", key), $"{ctx.Mod.Id}.{name}", s);
    }
}

internal sealed partial class EffectEmitter
{
    // An effect's screen: for the target, or for the players near the place.
    private void PlayScreen(ScreenRef r, int? target, int id, string path)
    {
        var block = ScreenBlocks.Resolve(_ctx, r, path);
        if (block.Seconds == null)
            throw new BlockError(_ctx.Block, $"{path}.seconds", "an effect's screen effect needs seconds, or nothing would ever stop it (a status's screen effect lasts while it is applied)");
        if (target is { } t)
        {
            Signal(ScreenBlocks.Tag(_ctx, block, ScreenBlocks.OnTarget), t, $"screen{id}");
            return;
        }
        _b.Call("kiln.Signal", null, _originPosition, _b.V($"screen{id}", ImpactType.Uint32, BitConverter.GetBytes(ScreenBlocks.Tag(_ctx, block, ScreenBlocks.AtPlace))));
        _b.Pop();
    }

    // A status's screen: on when it starts, off in its shutdown code.
    private void ScreenWhile(ScreenRef r, int target, bool starts)
    {
        var block = ScreenBlocks.Resolve(_ctx, r, "screen");
        Signal(ScreenBlocks.Tag(_ctx, block, starts ? ScreenBlocks.WhileOn : ScreenBlocks.WhileOff), target, starts ? "screenOn" : "screenOff");
    }
}

internal static class ScreenBlocks
{
    public const uint OnTarget = 0, AtPlace = 1, WhileOn = 2, WhileOff = 3;
    private const uint TagBit = 0xB1100000;   // the low 16 bits: the effect's index << 2 | how it plays
    private const float DefaultRange = 30;

    private static readonly object Gate = new();
    private static readonly Dictionary<uint, (string Key, ScreenEffectBlock Block)> Cues = new();
    private static readonly Dictionary<(uint Entity, uint Cue), (int Count, ScreenHandle Handle)> Whiles = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static IDisposable? _tick, _signals;

    public static void Check(BlockContext ctx, ScreenEffectBlock s, string path)
    {
        var at = path.Length == 0 ? "" : path + ".";
        foreach (var (field, problem) in s.Problems()) throw new BlockError(ctx.Block, at + field, problem);
        if (s.Range is { } r && !(r > 0 && float.IsFinite(r))) throw new BlockError(ctx.Block, at + "range", $"{r} must be more than 0 metres");
    }

    /// <summary>The block a field names: inline, by handle or by name.</summary>
    public static ScreenEffectBlock Resolve(BlockContext ctx, ScreenRef r, string path)
    {
        if (r.Inline is { } inline)
        {
            Check(ctx, inline, path);
            return inline;
        }
        var name = r.Name ?? throw new BlockError(ctx.Block, path, "a screen effect needs a name or a block");
        var h = ctx.Find(name, path);
        if (h is BlockHandle<ScreenEffectBlock> screen) return screen.Block;
        if (h != null) throw new BlockError(ctx.Block, path, $"\"{name}\" is a {h.BlockType.Name.Replace("Block", "").ToLowerInvariant()}, not a screen effect");
        var own = ctx.Mod.Handles.Where(x => x.Value is BlockHandle<ScreenEffectBlock>).Select(x => x.Key).Order();
        throw new BlockError(ctx.Block, path, $"no screen effect \"{name}\" in this mod; define it first, or say \"other-mod:{name}\"; there are: {string.Join(", ", own)}");
    }

    /// <summary>The numbers kiln.screen gets, every default filled in: what a C# block and its data twin must agree on.</summary>
    public static JsonObject Describe(ScreenEffectBlock s)
    {
        var o = new JsonObject
        {
            ["order"] = s.Order ?? 0, ["strength"] = s.Strength ?? 1, ["fadeIn"] = s.FadeIn ?? 0, ["seconds"] = s.Seconds is { } sec ? sec : null, ["fadeOut"] = s.FadeOut ?? 0,
            ["exposure"] = s.Exposure ?? 0, ["contrast"] = s.Contrast ?? 1, ["saturation"] = s.Saturation ?? 1, ["gamma"] = s.Gamma ?? 1, ["temperature"] = s.Temperature ?? 0,
            ["tint"] = Colour(s.Tint?.Color ?? "#ffffff", s.Tint?.Amount ?? 0),
            ["vignette"] = new JsonObject
            {
                ["color"] = s.Vignette?.Color ?? "#000000", ["amount"] = s.Vignette is { } v ? v.Amount ?? 1 : 0, ["radius"] = s.Vignette?.Radius ?? 0.5f,
                ["softness"] = s.Vignette?.Softness ?? 0.5f,
            },
            ["fill"] = Colour(s.Fill?.Color ?? "#000000", s.Fill?.Amount ?? 0),
            ["range"] = s.Range ?? DefaultRange,
        };
        return o;
        static JsonObject Colour(string c, float a) => new() { ["color"] = c.ToLowerInvariant(), ["amount"] = a };
    }

    /// <summary>The tag a program signals for this effect, played this way. The same effect gets the same tag; a clash takes the next one.</summary>
    public static uint Tag(BlockContext ctx, ScreenEffectBlock block, uint how)
    {
        Touch();
        var key = $"{ctx.Mod.Id}|{JsonSerializer.Serialize<ScreenEffect>(block, Json)}|{block.Range}";
        var index = BlockIds.Fnv(System.Text.Encoding.UTF8.GetBytes(key)) & 0x3FFF;
        lock (Gate)
            for (;; index = (index + 1) & 0x3FFF)
            {
                var cue = TagBit | (index << 2);
                if (Cues.TryGetValue(cue, out var have) && have.Key != key) continue;
                Cues[cue] = (key, block);
                return cue | how;
            }
    }

    // From the service's own tick, so that the subscription is the service's: a mod's would end with the mod.
    private static void Touch()
    {
        lock (Gate)
            _tick ??= BlocksBridge.AddTick("kiln blocks screen", _ =>
            {
                lock (Gate) _signals ??= Events.On<ImpactSignalEvent>(OnSignal);
            });
    }

    private static void OnSignal(ImpactSignalEvent e)
    {
        if ((e.Tag & 0xFFFF0000) != TagBit) return;
        var cue = e.Tag & ~3u;
        var how = e.Tag & 3;
        ScreenEffectBlock? block;
        lock (Gate) block = Cues.TryGetValue(cue, out var c) ? c.Block : null;
        if (block == null) return;
        try
        {
            switch (how)
            {
                case OnTarget:
                    ScreenEffects.PlayFor(e.AsEntity, block);
                    break;
                case AtPlace when e.Value.Length >= 12:
                    var at = new WorldPosition(BitConverter.ToSingle(e.Value, 0), BitConverter.ToSingle(e.Value, 4), BitConverter.ToSingle(e.Value, 8));
                    ScreenEffects.PlayNear(at, block.Range ?? DefaultRange, block);
                    break;
                case WhileOn:
                {
                    var key = (e.AsEntity.Value, cue);
                    lock (Gate)
                    {
                        // a status applied again while it runs: its program starts again, and only the last end stops the screen
                        if (Whiles.TryGetValue(key, out var w))
                        {
                            Whiles[key] = (w.Count + 1, w.Handle);
                            return;
                        }
                    }
                    var h = ScreenEffects.PlayFor(e.AsEntity, block);
                    lock (Gate) Whiles[key] = (1, h);
                    break;
                }
                case WhileOff:
                {
                    var key = (e.AsEntity.Value, cue);
                    ScreenHandle stop = default;
                    lock (Gate)
                    {
                        if (!Whiles.TryGetValue(key, out var w)) return;
                        if (w.Count > 1) Whiles[key] = (w.Count - 1, w.Handle);
                        else
                        {
                            Whiles.Remove(key);
                            stop = w.Handle;
                        }
                    }
                    ScreenEffects.Stop(stop);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: a screen effect did not play: {ex.Message}");
        }
    }

    /// <summary>For tests: how many statuses' screens run, by entity.</summary>
    internal static int WhileCount(EntityId entity)
    {
        lock (Gate) return Whiles.Where(w => w.Key.Entity == entity.Value).Sum(w => w.Value.Count);
    }
}
