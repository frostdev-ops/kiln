// SPDX-License-Identifier: MIT
// The programs of deliveries that are not a flight, and of what follows a character: a perk, a talent. Who a delivery reaches is found by a query and a line of arithmetic rather than a
// collider, as an area is (docs/kiln/modding/blocks.md): it runs with or without a player near, so what a lab proves is what a game runs.
//
// The game has no angle native and no dot product; two lengths do it. Where a target stands at distance L from the origin, the point at distance L along the aim is 2 L sin(a / 2) from it, a
// being the angle between them. That is a cone's test as it stands, and within a beam's width it is the distance from the line.
namespace Kiln;

internal sealed partial class EffectEmitter
{
    /// <summary>The float config the game scales by a node's level, as its own levelled nodes do (the scaled config of the program 9781b46d).</summary>
    internal const uint CfgLevel = 0x0a0c87e7;
    /// <summary>Who a launcher aims at: set by the cast.</summary>
    internal static readonly uint CfgTarget = BlockIds.Fnv("kiln.blocks.Target");

    private const byte OpMul = 2, OpSub = 1, CmpLess = 1;
    private static readonly byte[] UnitZ = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x80, 0x3f];

    private void Signal(uint tag, int value, string name)
    {
        _b.Call("kiln.Signal", null, value, _b.V(name, ImpactType.Uint32, BitConverter.GetBytes(tag)));
        _b.Pop();
    }

    private int Sub(int to, int a, int b)
    {
        _b.Call("sub", null, to, _b.VByte("opSub", ImpactType.MathOp, OpSub), a, b);
        _b.Pop();
        return to;
    }

    // Unit vector along a transform's own forward: a point one metre ahead in its axes, and the direction to that point.
    private int ForwardOf(int transform, string tag)
    {
        var ahead = _b.V($"ahead{tag}", ImpactType.Transform);
        _b.Call("applyOffset", null, ahead, transform, _b.V("unitZ", ImpactType.Float3, UnitZ), null, null);
        _b.Pop();
        var forward = _b.V($"forward{tag}", ImpactType.Float3);
        _b.Call("fromTransform", null, null, null, null, forward, transform, ahead);
        _b.Pop();
        return forward;
    }

    // Where a body's chest is: on one with no such slot, where it stands.
    private int ChestOf(int entity, string name)
    {
        var at = _b.V(name, ImpactType.Float3);
        _b.Call("getTransform", null, null, at, entity);
        _b.Pop();
        _b.Call("GetSlotTransform", null, null, at, _b.VByte("slotChest", ImpactType.Slot, (byte)Slot.Chest), entity, null);
        _b.Pop();
        return at;
    }

    /// <summary>Jumps to <paramref name="skip"/> unless who stands within reach of the aim: a share of the distance (a cone), or a width (a beam).</summary>
    private void WithinAim(int who, int origin, int forward, float? share, float? width, int id, ProgramBuilder.Label skip)
    {
        var at = ChestOf(who, $"whoAt{id}");
        var rel = Sub(_b.V($"rel{id}", ImpactType.Float3), at, origin);
        var length = _b.V($"length{id}", ImpactType.Float);
        _b.Call("GetLength", null, length, rel);
        _b.Pop();
        var along = _b.V($"along{id}", ImpactType.Float3);
        _b.Call("SetLength", null, along, forward, length);
        _b.Pop();
        var off = Sub(_b.V($"off{id}", ImpactType.Float3), rel, along);
        var away = _b.V($"away{id}", ImpactType.Float);
        _b.Call("GetLength", null, away, off);
        _b.Pop();
        int limit;
        if (share is { } s)
        {
            limit = _b.V($"limit{id}", ImpactType.Float);
            _b.Call("mul", null, limit, _b.VByte("opMul", ImpactType.MathOp, OpMul), length, _b.VFloat($"share{id}", s));
            _b.Pop();
        }
        else limit = _b.VFloat($"width{id}", width!.Value / 2);
        _b.Call("less", null, _b.VByte("cmpLess", ImpactType.CompareOp, CmpLess), away, limit);
        _b.IfFalse(skip);
    }

    /// <summary>How far from the aim, as a share of the distance, the edge of a cone this wide is.</summary>
    private static float ConeShare(float degrees) => 2 * MathF.Sin(degrees * MathF.PI / 180 / 4);

    /// <summary>The program of a delivery that is placed: it sets out from where it was put, in the direction it was put, does what it says, and goes.</summary>
    public static (Guid Guid, byte[] Blob, EffectEmitter E, string Listing) DeliveryProgram(BlockContext ctx, string key, PlacedDelivery d, string path, IReadOnlyList<EffectRef>? onCast, string onCastPath, bool primary)
    {
        var b = new ProgramBuilder(key);
        var e = new EffectEmitter(b, ctx) { Primary = primary };
        var self = b.V("self", ImpactType.Entity);
        var origin = b.V("origin", ImpactType.Transform);
        var pos = b.V("position", ImpactType.Float3);
        b.BeginCode();
        b.Call("getSelf", null, self);
        b.Pop();
        b.Call("getRoot", null, e.Caster);
        b.Pop();
        b.Call("getTransform", null, origin, pos, self);
        b.Pop();
        e._originPosition = pos;
        if (onCast is { Count: > 0 })
        {
            var cast = new EffectEmitter(b, ctx, e.Caster, 1000);
            cast.OnTarget(onCast, e.Caster, onCastPath);
            e._areas.AddRange(cast._areas);
            e.Adopt(cast);
        }
        var id = ++e._n;
        switch (d)
        {
            case SelfBlock s:
                if (s.Vfx is { } selfVfx) e.PlaceVfx(selfVfx, e.Caster, null, "Delivery", $"{path}.vfx");
                e.OnTarget(s.Effects, e.Caster, $"{path}.effects");
                break;
            case AroundBlock a:
                e.FirstRadius ??= a.Radius;
                var around = e.PlaceOf(e.Caster, id);
                if (a.Vfx is { } aroundVfx) e.PlaceVfx(aroundVfx, null, around, "Delivery", $"{path}.vfx");
                e.Area(new AreaBlock { Radius = a.Radius, Affects = a.Affects, Effects = a.Effects }, around, id, path);
                break;
            case ConeBlock c:
            {
                if (c.Vfx is { } coneVfx) e.PlaceVfx(coneVfx, null, origin, "Delivery", $"{path}.vfx");
                var chest = e.ChestOf(e.Caster, "casterChest");
                var forward = e.ForwardOf(origin, "");
                var share = ConeShare(c.Angle);
                e.InRange(origin, c.Range + 1, c.Affects, id, (who, next) => e.WithinAim(who, chest, forward, share, null, id, next),
                    (who, _, _) => e.OnTarget(c.Effects, who, $"{path}.effects"));
                break;
            }
            case BeamBlock beam:
                e.Beam(beam, origin, id, path);
                break;
            case ZoneBlock z:
                e.Zone(z, origin, id, path);
                break;
            case ChainBlock chain:
                e.Chain(chain, origin, id, path);
                break;
            case SummonBlock summon:
                e.Summon(summon, origin, id, path);
                break;
            default:
                throw new BlockError(ctx.Block, path, $"{d.GetType().Name} is not a delivery Kiln can compile yet");
        }
        b.DestroySelf();
        b.Halt();
        b.BeginShutdown();
        e.EmitShutdown();
        b.Halt();
        var (guid, blob) = ctx.Build(b);
        return (guid, blob, e, b.Listing.ToString());
    }

    private void Beam(BeamBlock beam, int origin, int id, string path)
    {
        if (beam.Vfx is { } vfx) PlaceVfx(beam.Seconds > 0 && vfx.Seconds == null ? vfx with { Seconds = beam.Seconds } : vfx, null, origin, "Delivery", $"{path}.vfx");
        var chest = ChestOf(Caster, "casterChest");
        if (beam.Seconds <= 0)
        {
            var forward = ForwardOf(origin, "");
            InRange(origin, beam.Range + 1, beam.Affects, id, (who, next) => WithinAim(who, chest, forward, null, beam.Width, id, next),
                (who, _, _) => OnTarget(beam.Effects, who, $"{path}.effects"));
            return;
        }
        var end = _b.V("end", ImpactType.Time);
        _b.Deadline(end, _b.VTime("seconds", beam.Seconds));
        var every = _b.VTime("every", beam.TickEvery);
        var tick = _b.NewLabel();
        var done = _b.NewLabel();
        _b.Mark(tick);
        // while it lasts it goes where the caster faces
        _b.Call("getTransform", null, origin, null, Caster);
        _b.Pop();
        var turned = ForwardOf(origin, "");
        _b.Call("GetSlotTransform", null, null, chest, _b.VByte("slotChest", ImpactType.Slot, (byte)Slot.Chest), Caster, null);
        _b.Pop();
        InRange(origin, beam.Range + 1, beam.Affects, id, (who, next) => WithinAim(who, chest, turned, null, beam.Width, id, next),
            (who, _, _) => OnTarget(beam.Effects, who, $"{path}.effects"));
        _b.Wait(every);
        _b.IfPassed(end, done);
        _b.Goto(tick);
        _b.Mark(done);
    }

    private void Zone(ZoneBlock z, int origin, int id, string path)
    {
        FirstRadius ??= z.Radius;
        var place = origin;
        if (z.Ahead != 0)
        {
            place = _b.V("zonePlace", ImpactType.Transform);
            _b.Call("applyOffset", null, place, origin, _b.V("zoneOffset", ImpactType.Float3, Float3(0, 0, z.Ahead)), null, null);
            _b.Pop();
            _b.Call("SetTransform", null, place);
            _b.Pop();
        }
        if (z.Vfx is { } vfx) PlaceVfx(vfx.Seconds == null ? vfx with { Seconds = z.Seconds } : vfx, null, place, "Delivery", $"{path}.vfx");
        var end = _b.V("end", ImpactType.Time);
        _b.Deadline(end, _b.VTime("seconds", z.Seconds));
        var every = _b.VTime("every", z.TickEvery);
        var tick = _b.NewLabel();
        var done = _b.NewLabel();
        _b.Mark(tick);
        InRange(place, z.Radius, z.Affects, id, null, (who, _, _) => OnTarget(z.Effects, who, $"{path}.effects"));
        _b.Wait(every);
        _b.IfPassed(end, done);
        _b.Goto(tick);
        _b.Mark(done);
    }

    private void Chain(ChainBlock c, int origin, int id, string path)
    {
        var chest = ChestOf(Caster, "casterChest");
        var forward = ForwardOf(origin, "");
        var share = ConeShare(c.Angle);
        var end = _b.NewLabel();
        var hit = new List<int>();
        var from = origin;
        for (var k = 0; k <= c.Jumps; k++)
        {
            var hop = id + 100 * (k + 1);
            var before = hit.ToArray();
            var found = _b.NewLabel();
            var here = _b.V($"hopAt{k}", ImpactType.Transform);
            InRange(from, k == 0 ? c.Range + 1 : c.JumpRange, c.Affects, hop,
                (who, next) =>
                {
                    if (k == 0) WithinAim(who, chest, forward, share, null, hop, next);
                    foreach (var was in before)
                    {
                        _b.Call("isSameEntity", null, who, was);
                        _b.IfTrue(next);
                    }
                },
                (who, _, _) =>
                {
                    hit.Add(who);
                    if (c.Vfx is { } vfx) PlaceVfx(vfx, who, null, $"Hop{k}", $"{path}.vfx");
                    OnTarget(c.Effects, who, $"{path}.effects");
                    _b.Call("getTransform", null, here, null, who);
                    _b.Pop();
                    _b.Goto(found);
                });
            // the query ran out: nobody left to reach
            _b.Goto(end);
            _b.Mark(found);
            from = here;
        }
        _b.Mark(end);
    }

    private void Summon(SummonBlock s, int origin, int id, string path)
    {
        var template = Guid.TryParse(s.Template.Value, out var guid)
            ? _b.VGuid("summon", ImpactType.Template, guid)
            : _b.VLate("summon", ImpactType.Template, "template", s.Template.Value, _ctx.Block, $"{path}.template");
        var yes = _b.VBool("yes", true);
        var no = _b.VBool("no", false);
        var spawned = new List<int>();
        for (var i = 0; i < s.Count; i++)
        {
            var place = _b.V($"summonAt{i}", ImpactType.Transform);
            _b.Call("applyOffset", null, place, origin, _b.V($"summonOffset{i}", ImpactType.Float3, Float3((i - (s.Count - 1) / 2f) * 1.5f, 0, s.Ahead)), null, null);
            _b.Pop();
            if (i == 0 && s.Vfx is { } vfx) PlaceVfx(vfx, null, place, "Delivery", $"{path}.vfx");
            var entity = _b.V($"summoned{i}", ImpactType.Entity);
            spawned.Add(entity);
            _b.Call("spawnEntity", null, entity, no, no, yes, yes, template, place, null, null, null, null, null, null);
            _b.Pop();
        }
        if (s.Seconds is not { } seconds) return;
        _b.Wait(_b.VTime("seconds", seconds));
        foreach (var entity in spawned)
        {
            _b.Call("destroyEntity", null, entity);
            _b.Pop();
        }
    }

    private static byte[] Float3(float x, float y, float z)
    {
        var bytes = new byte[12];
        Buffer.BlockCopy(new[] { x, y, z }, 0, bytes, 0, 12);
        return bytes;
    }

    /// <summary>A flight's start for an ability with no motion: what a staff's event does at the hand. It sends the template along the aim it was put at, and to who the cast gave it, if anyone.</summary>
    public static (Guid Guid, byte[] Blob, string Listing) LauncherProgram(BlockContext ctx, string key, Guid template, float speed)
    {
        var b = new ProgramBuilder(key);
        var e = new EffectEmitter(b, ctx);
        var self = b.V("self", ImpactType.Entity);
        var origin = b.V("origin", ImpactType.Transform);
        var target = b.V("target", ImpactType.Entity, default, CfgTarget);
        var start = b.V("start", ImpactType.Transform);
        var velocity = b.V("velocity", ImpactType.Float3);
        var flight = b.VGuid("flight", ImpactType.Template, template);
        var yes = b.VBool("yes", true);
        var no = b.VBool("no", false);
        var free = b.NewLabel();
        var done = b.NewLabel();
        b.BeginCode();
        b.Call("getSelf", null, self);
        b.Pop();
        b.Call("getRoot", null, e.Caster);
        b.Pop();
        b.Call("getTransform", null, origin, null, self);
        b.Pop();
        var forward = e.ForwardOf(origin, "");
        b.Call("SetLength", null, velocity, forward, b.VFloat("speed", speed));
        b.Pop();
        // It leaves from the chest, in the aim's orientation: a homing flight's RandomOrientation sets its velocity from its orientation, whatever the spawn gave it.
        b.Call("applyOffset", null, start, origin, b.V("startOffset", ImpactType.Float3, Float3(0, 1.3f, 0.5f)), null, null);
        b.Pop();
        b.Call("isValidEntity", null, target);
        b.IfFalse(free);
        b.Call("spawnEntity", null, null, no, no, yes, yes, flight, start, null, velocity, null, target, null, null);
        b.Pop();
        b.Goto(done);
        b.Mark(free);
        b.Call("spawnEntity", null, null, no, no, yes, yes, flight, start, null, velocity, null, null, null, null);
        b.Pop();
        b.Mark(done);
        b.DestroySelf();
        b.Halt();
        b.BeginShutdown();
        b.Halt();
        var (guid, blob) = ctx.Build(b);
        return (guid, blob, b.Listing.ToString());
    }

    /// <summary>The program that goes with a character for as long as a perk or a talent lasts: stats changed, a status kept, what it does to what they hit. A stat's value is per level; the level is the
    /// float config <see cref="CfgLevel"/>, which the game scales for its own nodes. The game writes a new level into the running instance, as for its own levelled nodes, so the stats follow it
    /// each step.</summary>
    public static (Guid Guid, byte[] Blob, EffectEmitter E, string Listing) FollowerProgram(BlockContext ctx, string key, IDictionary<Stat, double> modifies, int maxLevel, StatusRef? keeps, string keepsPath,
        IReadOnlyList<EffectRef>? onStart, string onStartPath, IReadOnlyList<EffectRef> onHit, IReadOnlyList<EffectRef> onKill, IReadOnlyList<EffectRef> onHitTaken, bool stable = false)
    {
        List<StatVar>? stats = null;
        int? status = null, level = null, applied = null;
        return WielderProgram(ctx, key, onHit, "onHit", onKill, "onKill", onHitTaken, "onHitTaken",
            (b, e) =>
            {
                b.Stable = stable;
                b.Call("getRoot", null, e.Caster);
                b.Pop();
                stats = PrepareStats(ctx, b, modifies);
                if (maxLevel > 1 && stats.Count > 0)
                {
                    level = b.VFloat("level", 1, CfgLevel);
                    applied = b.VFloat("appliedLevel", 0);
                    b.Set(applied.Value, level.Value);
                }
                ComputeStats(b, stats, level, maxLevel);
                ApplyStats(b, e, stats, revert: false);
                if (keeps != null)
                {
                    status = ctx.StatusVar(b, "keeps", keeps, keepsPath, out _);
                    // as long as the character has it: taken off at the end, and a day is longer than any session a status has to outlive
                    b.Call("applyBuff", null, status, e.Caster, null, null, b.VTime("keepsFor", 86400));
                    b.Pop();
                }
                if (onStart is { Count: > 0 }) e.OnTarget(onStart, e.Caster, onStartPath);
            },
            (b, e) =>
            {
                if (status is { } s)
                {
                    b.Call("removeBuff", null, null, s, e.Caster);
                    b.Pop();
                }
                ApplyStats(b, e, stats!, revert: true);
            },
            (b, e) =>
            {
                if (level is not { } l || applied is not { } a) return;
                var same = b.NewLabel();
                b.Call("equal", null, b.VByte("cmpEqual", ImpactType.CompareOp, 0), l, a);
                b.IfTrue(same);
                // As the game's own levelled node does in the same instance: a share is applied again with its new value, which replaces what this site gave (its
                // stored value follows). A whole number is taken back and given again.
                var whole = stats!.Where(x => x.Stat.Whole).ToList();
                ApplyStats(b, e, whole, revert: true);
                ComputeStats(b, stats!, l, maxLevel);
                ApplyStats(b, e, stats!, revert: false);
                b.Set(a, l);
                b.Mark(same);
            });
    }

    private sealed record StatVar(Stat Stat, double Value, string Site, int Attr, int Op, int Storage, int Sign, int Amount);

    private static List<StatVar> PrepareStats(BlockContext ctx, ProgramBuilder b, IDictionary<Stat, double> modifies)
    {
        var list = new List<StatVar>();
        foreach (var (stat, value) in modifies)
        {
            var n = list.Count;
            var at = $"modifies[{stat.Key}]";
            if (stat.Whole && value != Math.Floor(value)) throw new BlockError(ctx.Block, at, $"{value}: {stat.Key} takes whole numbers");
            if (value == 0) throw new BlockError(ctx.Block, at, "0 changes nothing");
            list.Add(new StatVar(stat, value, $"modify{n}",
                b.VGuid($"modifyAttr{n}", ImpactType.Attribute, stat.Attribute),
                b.VByte($"modifyOp{n}", ImpactType.MathOp, (byte)(stat.Subtract ? 1 : 0)),
                b.V($"modifyStorage{n}", stat.Whole ? ImpactType.Sint32 : ImpactType.Float),
                b.V($"modifySign{n}", ImpactType.Bool),
                b.V($"modifyValue{n}", stat.Whole ? ImpactType.Sint32 : ImpactType.Float)));
        }
        return list;
    }

    // Each stat's amount at the level: per level times the level. A whole number has no way from the float: one branch per level.
    private static void ComputeStats(ProgramBuilder b, List<StatVar> stats, int? level, int maxLevel)
    {
        for (var n = 0; n < stats.Count; n++)
        {
            var s = stats[n];
            if (level is not { } l)
            {
                b.Set(s.Amount, s.Stat.Whole ? b.VInt($"modifyAt1_{n}", (int)s.Value) : b.VFloat($"modifyAt1_{n}", (float)s.Value));
                continue;
            }
            if (!s.Stat.Whole)
            {
                b.Call("mul", null, s.Amount, b.VByte("opMul", ImpactType.MathOp, OpMul), l, b.VFloat($"modifyPerLevel{n}", (float)s.Value));
                b.Pop();
                continue;
            }
            var done = b.NewLabel();
            for (var k = 1; k <= maxLevel; k++)
            {
                var next = b.NewLabel();
                if (k < maxLevel)
                {
                    b.Call("less", null, b.VByte("cmpLess", ImpactType.CompareOp, CmpLess), l, b.VFloat($"belowLevel{k}", k + 0.5f));
                    b.IfFalse(next);
                }
                b.Set(s.Amount, b.VInt($"modifyValue{n}at{k}", (int)s.Value * k));
                b.Goto(done);
                b.Mark(next);
            }
            b.Mark(done);
        }
    }

    private static void ApplyStats(ProgramBuilder b, EffectEmitter e, List<StatVar> stats, bool revert)
    {
        foreach (var s in stats)
        {
            b.Call(revert ? "revertApplyAttributeValue" : "applyAttributeValue", s.Site, s.Attr, s.Op, s.Storage, s.Sign, e.Caster, null, s.Amount);
            b.Pop();
        }
    }
}
