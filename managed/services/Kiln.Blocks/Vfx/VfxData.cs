// SPDX-License-Identifier: MIT
// What a Vfx field becomes. The server never has a VFX: it decides that something happens, and each client draws it from its own copy of the same
// data, so a block's VFX is a reference in the game's own data routes (docs/re/vfx.md 4.2):
//   a cast        a VfxNotifierEvent of the generated sequence        Notifier
//   a projectile  VfxComponent + VfxComponentResource of its template Component
//   a status      a BuffType.whileApplied or onCreate entry           BuffEntry
//   an effect     a hidden template that carries it, spawned where the effect happens: impact programs have no "play VFX" op. Carrier
// A dedicated server has no VFX resources and needs none of this to work: it carries the GUIDs. kiln.gamedata leaves a client without kiln.vfx only
// the VFX its game has.
using System.Numerics;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A <see cref="VfxRef"/> at a place in a block: which VFX, on which slot. A game VFX by name has its GUID only once the game's data is there.</summary>
internal sealed record VfxTarget(string Text, Guid? Known, string? GameName, VfxBlock? Block, Slot Slot, Vector3 Offset, bool Attach, double? Seconds, string Owner, string Path)
{
    /// <summary>A Kiln VfxBlock's start block (VfxParameterBlock), when the reference sets its parameters: data routes write it as the game's own data does.</summary>
    public uint[]? Play { get; init; }
}

/// <summary>A game VFX's name, as the running game answers it.</summary>
internal sealed record VfxResolved(Guid Vfx, uint[]? Parameters, double? Seconds);

/// <summary>Fills in, when content is ready, what a game VFX's name stands for. <paramref name="Fill"/> writes into rules that are applied afterwards.</summary>
internal sealed record VfxLate(VfxTarget Vfx, Action<VfxResolved> Fill)
{
    public bool Done { get; set; }
}

internal static class VfxData
{
    /// <summary>What a carrier starts from: a game template that is exactly transforms, DoNotSave, LifeTime, SpawnTime and the VFX component. With no parameter component, a VFX plays its own defaults.</summary>
    internal const string CarrierBase = "VFX_NPC_Place";
    /// <summary>A carrier shorter than this may never reach a client; the game's own explosions and VFX templates start here.</summary>
    internal const double ShortestCarrier = 1;
    /// <summary>How long a game VFX's carrier lives when nothing says.</summary>
    internal const double GameCarrierSeconds = 5;

    public static Slot SlotOf(VfxSlot s) => s switch
    {
        VfxSlot.Head => Slot.Head,
        VfxSlot.Chest => Slot.Chest,
        VfxSlot.LeftHand => Slot.HandL,
        VfxSlot.RightHand or VfxSlot.Weapon => Slot.HandR,
        VfxSlot.Feet => Slot.FootR,
        _ => Slot.Root,
    };

    /// <summary>What a Vfx field names. A new VFX needs the mod on every client; a game VFX by name needs only what its data does.</summary>
    /// <param name="defaultSlot">Where it shows when the field does not say.</param>
    /// <param name="parameters">This route can set <see cref="VfxRef.Parameters"/>. Only an effect can: every other route is data that a server can't lay out.</param>
    public static VfxTarget Lower(BlockContext ctx, VfxRef r, string path, Slot defaultSlot = Slot.Root, bool parameters = false)
    {
        var name = r.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new BlockError(ctx.Block, path, "a VFX needs a name: a VfxBlock of this mod, \"other-mod:name\", or \"game:<name>\" for one of the game's");
        if (r.Seconds is <= 0) throw new BlockError(ctx.Block, $"{path}.seconds", $"{r.Seconds} must be more than 0");
        var named = r.Parameters is { Count: > 0 };
        var slot = r.Slot is { } s ? SlotOf(s) : defaultSlot;
        var game = name.StartsWith("game:", StringComparison.Ordinal) ? name[5..] : Guid.TryParse(name, out _) ? name : null;
        if (game != null)
        {
            if (named && !parameters)
                throw new BlockError(ctx.Block, $"{path}.parameters", "this field cannot set a game VFX's parameters: the game's data takes a block laid out by the VFX, which only clients have. Put the VFX on an effect, or leave them out");
            if (game.Length == 0) throw new BlockError(ctx.Block, path, "\"game:\" needs a name: a template that carries the VFX, a sequence that plays it, or a GUID");
            ctx.Need(ClientsLevel.Kiln, path, "new data that names a VFX has to reach the players' games: Kiln clients get it from the server");
            return new VfxTarget(game, Guid.TryParse(game, out var g) ? g : null, Guid.TryParse(game, out _) ? null : game, null, slot, r.Offset, r.Attach, r.Seconds, ctx.Block, path);
        }
        var handle = ctx.Find(name, path);
        if (handle is BlockHandle<VfxBlock> v)
        {
            ctx.Need(ClientsLevel.Mod, path, $"\"{name}\" is a new VFX, which has to be on every player's game: the server does not send assets");
            // what its carrier's id follows from: the same however the reference was written
            var text = v.Mod == ctx.Mod.Id ? v.Name : $"{v.Mod}:{v.Name}";
            // Every Kiln VFX has the same parameters, so their block is known here, on a server too.
            var play = named ? VfxParameterBlock.Of(r.Parameters!, (field, why) => new BlockError(ctx.Block, $"{path}.parameters.{field}", why)) : null;
            return new VfxTarget(text, v.Guid, null, v.Block, slot, r.Offset, r.Attach, r.Seconds, ctx.Block, path) { Play = play };
        }
        if (handle != null) throw new BlockError(ctx.Block, path, $"\"{name}\" is a {handle.BlockType.Name.Replace("Block", "").ToLowerInvariant()}, not a VFX");
        var own = ctx.Mod.Handles.Where(x => x.Value is BlockHandle<VfxBlock>).Select(x => x.Key).Order();
        throw new BlockError(ctx.Block, path, $"no VfxBlock \"{name}\" in this mod; define it first, say \"other-mod:{name}\", or \"game:{name}\" for one of the game's; there are: {string.Join(", ", own)}");
    }

    // keen::ecs::Offset, all zero with an identity rotation in the game's data: only what the block says is written.
    private static void Offset(JsonObject into, string path, Vector3 v)
    {
        if (v.X != 0) into[$"{path}.x"] = v.X;
        if (v.Y != 0) into[$"{path}.y"] = v.Y;
        if (v.Z != 0) into[$"{path}.z"] = v.Z;
    }

    /// <summary>The fields of a generated sequence that make its <paramref name="index"/>-th VFX event play this one. A game VFX has its GUID later.</summary>
    /// <param name="time">Seconds into the sequence; null: when the event already plays.</param>
    public static JsonObject Notifier(BlockContext ctx, VfxTarget v, string sequence, int index = 0, double? time = null)
    {
        var at = $"events[VfxNotifierEvent#{index}]";
        var fields = new JsonObject { [$"{at}.slot"] = v.Slot.ToString(), [$"{at}.attach"] = v.Attach };
        if (time is { } t) fields[$"{at}.time"] = t;
        Offset(fields, $"{at}.notifierOffset.localOffset", v.Offset);
        if (v.Play is { } play)
        {
            fields[$"{at}.hasVfxParameters"] = true;
            for (var i = 0; i < play.Length; i++) fields[$"{at}.vfxParameters.parameters.data[{i}]"] = play[i];
        }
        if (v.Known is { } g) fields[$"{at}.vfx"] = g.ToString();
        else
        {
            // until the name is known it shows nothing, rather than the event's own VFX in another's place
            fields[$"{at}.vfx"] = "none";
            ctx.Low!.Late(v, "sequences", sequence, (rules, r) =>
            {
                rules[$"{at}.vfx"] = r.Vfx.ToString();
                // Every event of the game has parameters. A VFX named after a template or sequence takes that one's; without any it plays its own defaults.
                if (r.Parameters is { } p)
                    for (var i = 0; i < p.Length; i++) rules[$"{at}.vfxParameters.parameters.data[{i}]"] = p[i];
                else rules[$"{at}.hasVfxParameters"] = false;
            });
        }
        return fields;
    }

    /// <summary>The fields of a generated template that make its VFX component play this one, while the entity lives.</summary>
    public static JsonObject Component(BlockContext ctx, VfxTarget v, string template)
    {
        if (v.Play != null)
            throw new BlockError(ctx.Block, $"{v.Path}.parameters", "a VFX that an entity carries can't take parameters yet (its template has no parameter component): leave them out, or put the VFX on an effect, a cast or a status");
        var fields = new JsonObject { ["VfxComponent.slot"] = v.Slot.ToString() };
        Offset(fields, "VfxComponent.attachmentOffset.localOffset", v.Offset);
        if (v.Known is { } g) fields["VfxComponentResource.vfx"] = g.ToString();
        else
        {
            fields["VfxComponentResource.vfx"] = "none";
            ctx.Low!.Late(v, "templates", template, (rules, r) => rules["VfxComponentResource.vfx"] = r.Vfx.ToString());
        }
        return fields;
    }

    /// <summary>A row of a status's <c>whileApplied</c> or <c>onCreate</c>. A field a row does not say is zero, so it says the rotation. The game gives these rows no default
    /// parameters, so a game VFX takes those of the template or sequence it is named after.</summary>
    public static JsonObject BuffEntry(BlockContext ctx, VfxTarget v, string buff, bool onCreate = false)
    {
        var row = new JsonObject { ["slot"] = v.Slot.ToString(), ["offset.orientationOffset.w"] = 1 };
        Offset(row, "offset.localOffset", v.Offset);
        if (onCreate) row["attach"] = v.Attach;
        if (v.Play is { } play)
        {
            if (onCreate) row["hasVfxParameters"] = true;
            for (var i = 0; i < play.Length; i++)
                if (play[i] != 0) row[$"parameters.data[{i}]"] = play[i];
        }
        if (v.Known is { } g) row["vfx"] = g.ToString();
        else
            ctx.Low!.Late(v, "buffs", buff, (_, r) =>
            {
                row["vfx"] = r.Vfx.ToString();
                if (r.Parameters is not { } p) return;
                if (onCreate) row["hasVfxParameters"] = true;
                for (var i = 0; i < p.Length; i++)
                    if (p[i] != 0) row[$"parameters.data[{i}]"] = p[i];
            });
        return row;
    }

    /// <summary>The hidden template that carries this VFX: spawned by a program, it plays for as long as it lives, and it lives as long as the VFX takes. One per VFX, offset and
    /// time, in the mod that uses it. The game's own pattern, and so what a joiner, range and replication already handle.</summary>
    public static Guid Carrier(BlockContext ctx, VfxTarget v)
    {
        if (v.Play != null)
            throw new BlockError(ctx.Block, $"{v.Path}.parameters", "a carrier can't take parameters (its template has no parameter component): an effect with parameters is played by the relay");
        double seconds;
        if (v.Seconds is { } said) seconds = said;
        else if (v.Block is { } block)
        {
            var (stop, loops) = VfxTiming.Of(block);
            if (loops)
                throw new BlockError(ctx.Block, v.Path, $"\"{v.Text}\" runs until it is stopped, and nothing stops what an effect spawns: give its particles and light an end, say how long with Seconds, or put it on a status");
            seconds = stop;
        }
        else seconds = GameCarrierSeconds;
        seconds = Math.Max(seconds, ShortestCarrier);

        var mod = ctx.Mod.Id;
        var key = $"vfx.{v.Text}.{v.Offset.X:0.###}.{v.Offset.Y:0.###}.{v.Offset.Z:0.###}.{seconds:0.###}";
        var guid = BlockIds.Derive(mod, "template", key);
        var name = $"{mod}.{new string(key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray())}";
        var low = ctx.Low!;
        var fields = new JsonObject { ["LifeTime.lifeTime"] = seconds };
        Offset(fields, "VfxComponent.attachmentOffset.localOffset", v.Offset);
        // the base's own VFX must never show in this one's place
        fields["VfxComponentResource.vfx"] = v.Known?.ToString() ?? "none";
        low.Carrier(guid, name, CarrierBase, fields);
        if (v.GameName != null)
            low.Late(v, "templates", name, (rules, r) =>
            {
                rules["VfxComponentResource.vfx"] = r.Vfx.ToString();
                if (v.Seconds == null && r.Seconds is { } own) rules["LifeTime.lifeTime"] = Math.Max(own, ShortestCarrier);
            });
        return guid;
    }

    /// <summary>A game VFX by name, on the thread that may read the game's data (a tick): a template that carries it, or a sequence that plays it. Null: the game has no such thing.</summary>
    public static VfxResolved? ResolveGame(string name)
    {
        var id = VfxId.Game(name).Guid;
        if (id == Guid.Empty) return null;
        uint[]? parameters = null;
        double? seconds = null;
        var template = Content.Templates.Get(name);
        if (template != null)
        {
            parameters = Words(() => template.GetJson("VfxParametersTemplateComponent.parameters.data")?["value"]);
            try { seconds = template.GetNumber("LifeTime.lifeTime"); }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException) { seconds = null; }
        }
        else if (Content.Sequences.Get(name) is { } sequence)
            parameters = Words(() => sequence.GetJson("events[VfxNotifierEvent#0].vfxParameters.parameters.data")?["value"]);
        return new VfxResolved(id, parameters, seconds);
    }

    /// <summary>The parameters as the relay takes them, or what is wrong by name.</summary>
    public static VfxParams Parameters(BlockContext ctx, VfxRef r, string path)
    {
        var all = new VfxParams();
        foreach (var (name, value) in r.Parameters ?? [])
        {
            if (string.IsNullOrWhiteSpace(name)) throw new BlockError(ctx.Block, $"{path}.parameters", "a parameter needs a name");
            var at = $"{path}.parameters.{name}";
            switch (value.Value)
            {
                case float f: all.Set(name, f); break;
                case int i: all.Set(name, i); break;
                case Vector3 x: all.Set(name, x); break;
                case string s: all.Set(name, ColourOf(s) ?? throw new BlockError(ctx.Block, at, $"\"{s}\" is not a colour: #rrggbb or #rrggbbaa")); break;
                default: throw new BlockError(ctx.Block, at, "a parameter is a number, a colour, three numbers or a whole number");
            }
        }
        return all;
    }

    private static Color? ColourOf(string s)
    {
        var t = s.StartsWith('#') ? s[1..] : s;
        if (t.Length is not (6 or 8) || !t.All(char.IsAsciiHexDigit)) return null;
        byte B(int i) => Convert.ToByte(t.Substring(i, 2), 16);
        return new Color(B(0), B(2), B(4), t.Length == 8 ? B(6) : (byte)255);
    }

    private static uint[]? Words(Func<JsonNode?> read)
    {
        try
        {
            return read() is JsonArray a && a.Count > 0 ? a.Select(x => x?.GetValue<uint>() ?? 0).ToArray() : null;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) { return null; }
    }
}

/// <summary>
/// The per-play parameters every Kiln VfxBlock has (KX_* in native/services/vfx/shaders/kiln_vfx_params.h), all factors on what the block says:
/// <c>color</c> (a tint, <c>"#rrggbb"</c> or <c>"#rrggbbaa"</c>; alpha multiplies alpha), <c>size</c>, <c>speed</c>, <c>brightness</c> (particles and light), <c>count</c> (burst and rate), each 0 to 16.
/// The 64-byte block is the same on every client, so a server lays it out for a sequence or a buff with no VFX of its own.
/// </summary>
public static class VfxParameterBlock
{
    /// <summary>The names, in the block's order.</summary>
    public static readonly IReadOnlyList<string> Names = ["color", "size", "speed", "brightness", "count"];
    private const uint Magic = 0x4e4c494b;   // KX_MAGIC_VALUE, "KILN"
    private const float MaxFactor = 16;

    /// <summary>The defaults: white, all factors 1, and the word that says the block is Kiln's.</summary>
    public static uint[] Defaults()
    {
        var w = new uint[16];
        w[0] = 0xffffffff;
        w[1] = w[2] = w[3] = w[4] = BitConverter.SingleToUInt32Bits(1f);
        w[15] = Magic;
        return w;
    }

    /// <summary>The block with these values over the defaults. <paramref name="error"/> makes the exception for a field and what is wrong with it.</summary>
    public static uint[] Of(IReadOnlyDictionary<string, VfxValue> values, Func<string, string, Exception> error)
    {
        var w = Defaults();
        foreach (var (name, value) in values)
        {
            var index = Names.ToList().IndexOf(name);
            if (index < 0) throw error(name, $"a Kiln VFX has no parameter \"{name}\"; there are {string.Join(", ", Names)}");
            if (index == 0)
            {
                if (value.Value is not string s || ColourWord(s) is not { } c) throw error(name, "takes a colour: \"#rrggbb\" or \"#rrggbbaa\"");
                w[0] = c;
            }
            else
            {
                if (value.Value is not float f || !float.IsFinite(f) || f < 0 || f > MaxFactor) throw error(name, $"takes a number from 0 to {MaxFactor}: it multiplies what the block says");
                w[index] = BitConverter.SingleToUInt32Bits(f);
            }
        }
        return w;
    }

    // RGBA8 as the game stores a colour parameter: red in the low byte.
    private static uint? ColourWord(string s)
    {
        var t = s.StartsWith('#') ? s[1..] : s;
        if (t.Length is not (6 or 8) || !t.All(char.IsAsciiHexDigit)) return null;
        uint B(int i) => Convert.ToUInt32(t.Substring(i, 2), 16);
        return B(0) | B(2) << 8 | B(4) << 16 | (t.Length == 8 ? B(6) : 255u) << 24;
    }
}

/// <summary>How long a block runs: the builder's rule (native/services/vfx/cook/build.cpp), since a server has to know it with no builder and no VFX.</summary>
internal static class VfxTiming
{
    private const int MaxSpawnsPerTick = 64;
    private const float BurstDuration = 0.001f, TicksPerSecond = 60;

    /// <summary>Seconds until a one-shot has spawned everything and the last particle is gone. Loops: it runs until stopped.</summary>
    public static (double Seconds, bool Loops) Of(VfxBlock block)
    {
        var stop = 0f;
        var loops = false;
        var particles = block.Particles ?? [];
        foreach (var p in particles)
        {
            var rate = p.Rate ?? 0;
            var duration = p.Duration ?? 0;
            if (p.Loop == true || (duration <= 0 && rate > 0))
            {
                loops = true;
                continue;
            }
            if (rate <= 0 && duration <= 0) duration = BurstDuration;
            var spawning = MathF.Ceiling((float)(p.Burst ?? 0) * 16 / MaxSpawnsPerTick) / TicksPerSecond;
            stop = Math.Max(stop, (p.Delay ?? 0) + duration + spawning + (p.Lifetime?.Max ?? 1));
        }
        if (block.Light is { } light)
        {
            if (light.Lifetime is > 0) stop = Math.Max(stop, light.Lifetime.Value);
            else if (particles.Count == 0) loops = true;
        }
        return (stop, loops);
    }
}
