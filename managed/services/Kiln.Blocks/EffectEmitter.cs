// SPDX-License-Identifier: MIT
// EffectBlock -> Impact bytecode. One emitter for every trigger point: a program host (the place a projectile stopped, a weapon's
// hits, a status on its target) sets up who the caster is and who the target is, and the effects are the same statements in each.
// Who an effect reaches:
//  - Area: the query forEachEntityWithFactionInRange (as an area spell does): it runs with or without a player near, so what a
//    lab proves is what a game runs. The game's own AoE reads collision events instead, which need terrain streamed near a player.
//  - A hit from an effect is marked with the attack category Other (no vanilla item uses it), and a weapon's OnHit skips marked hits:
//    an effect that deals damage cannot trigger itself.
namespace Kiln;

internal sealed class EffectEmitter
{
    private const uint CfgDamage = 0x72864524, CfgDamageScale = 0x5333fdf6;
    private static readonly Guid MagicScale = new("461946f8-b443-4dec-8dfd-3f2b63e38a60"), PhysicalScale = new("6e45319c-b82d-45b3-84d8-119e61f4c46b"),
        Health = new("61f22138-a061-4b3f-a15e-35953e731112"), Mana = new("9f15218b-a7d4-4589-8de1-d5ab745bf51a"), Stamina = new("0a0eabad-6094-4ba2-8dc5-614d500d0a34");
    private const byte Melee = 1 << 0, Magic = 1 << 2, Other = 1 << 4, AoE = 1 << 5;
    // every faction but Environment (0) and HitAll (1); isEnemy / isFriend decide who counts
    private const ushort AllFactions = 0xFFFC;

    private static readonly byte[] Up = [0, 0, 0, 0, 0, 0, 0x80, 0x3f, 0, 0, 0, 0];

    private readonly ProgramBuilder _b;
    private readonly BlockContext _ctx;
    private readonly List<(string Site, int Target, int Index, int Origin, int Radius)> _areas = new();
    private int _n, _originPosition;

    /// <summary>The variable that holds the caster.</summary>
    public int Caster { get; }

    /// <summary>Another run of effects in the same program: the caster's variable is shared, the rest is fresh.</summary>
    private EffectEmitter(ProgramBuilder b, BlockContext ctx, int caster, int firstId)
    {
        _b = b;
        _ctx = ctx;
        Caster = caster;
        _n = firstId;
    }

    /// <summary>This program's first damage is the spell's main hit: it takes the used item's damage, so the staff's power and the spell's
    /// count in a real cast, while its own value stands when nothing was used (a RunImpact). A <see cref="DamageBlock.Flat"/> one does not.</summary>
    public bool Primary { get; set; }

    /// <summary>The main hit found, for the spell's item.</summary>
    public DamageBlock? PrimaryDamage { get; private set; }
    /// <summary>The first area's radius and the first status's seconds: the tooltip's numbers.</summary>
    public float? FirstRadius { get; private set; }
    public double? FirstStatusSeconds { get; private set; }

    public EffectEmitter(ProgramBuilder b, BlockContext ctx)
    {
        _b = b;
        _ctx = ctx;
        Caster = b.V("caster", ImpactType.Entity);
    }

    /// <summary>Runs effects at a place, with no target: an area, a spawn, a sound can happen there, and what is said to be for the caster.</summary>
    public void AtPoint(IReadOnlyList<EffectRef> effects, int origin, int position, string path)
    {
        _originPosition = position;
        for (var i = 0; i < effects.Count; i++) One(_ctx.Effect(effects[i], $"{path}[{i}]"), null, origin, $"{path}[{i}]");
    }

    /// <summary>Runs effects on a target.</summary>
    public void OnTarget(IReadOnlyList<EffectRef> effects, int target, string path)
    {
        for (var i = 0; i < effects.Count; i++) One(_ctx.Effect(effects[i], $"{path}[{i}]"), target, null, $"{path}[{i}]");
    }

    /// <summary>Releases the queries (the host's shutdown code).</summary>
    public void EmitShutdown()
    {
        foreach (var (site, target, index, origin, radius) in _areas)
        {
            _b.Call("destroyFactionQuery", site, target, null, index, _b.V("factions", ImpactType.FactionMask), origin, radius);
            _b.Pop();
        }
    }

    private void One(EffectBlock e, int? target, int? origin, string path)
    {
        if (e.Vfx != null) throw Compiler.NoVfxYet(_ctx, $"{path}.vfx");
        var id = ++_n;
        var end = _b.NewLabel();
        if (e.Chance is { } chance)
        {
            if (chance is < 0 or > 1) throw new BlockError(_ctx.Block, $"{path}.chance", $"{chance} is outside 0..1");
            _b.Call("randomBranchNode", null, _b.VFloat($"chance{id}", (float)chance));
            _b.IfFalse(end);
        }
        // who it is for: what the trigger gives it, or the caster
        if (e.On == EffectOn.Caster)
        {
            target = Caster;
            origin = null;
        }
        if (e.Area is { } area)
        {
            if (area.Radius <= 0) throw new BlockError(_ctx.Block, $"{path}.area.radius", $"{area.Radius} must be more than 0");
            FirstRadius ??= area.Radius;
            Area(area, origin ?? PlaceOf(target!.Value, id), id, $"{path}.area");
        }
        if (e.Spawns is { } spawns) Spawn(spawns, origin ?? PlaceOf(target!.Value, id), id, $"{path}.spawns");
        if (e.Sound is { } sound)
        {
            if (string.IsNullOrWhiteSpace(sound.Value)) throw new BlockError(_ctx.Block, $"{path}.sound", "a sound needs a name or a GUID");
            if (!Guid.TryParse(sound.Value, out _)) _ctx.Need(ClientsLevel.Mod, $"{path}.sound", $"the sound \"{sound.Value}\" is in the mod's files");
            var tag = BlockRuntime.SoundTag(_ctx.Mod.Id, sound.Value);
            var tagVar = _b.V($"sound{id}", ImpactType.Uint32, BitConverter.GetBytes(tag));
            int at;
            if (target is { } t0)
            {
                at = _b.V($"soundAt{id}", ImpactType.Float3);
                _b.Call("getTransform", null, null, at, t0);
                _b.Pop();
            }
            else at = _originPosition;
            _b.Call("kiln.Signal", null, at, tagVar);
            _b.Pop();
        }
        var hits = e.Damage != null || e.Knockback != Knockback.None;
        if (hits || e.Heal != null || e.Restores != null || e.Removes is { Count: > 0 } || e.Applies != null)
        {
            if (target is not { } t)
                throw new BlockError(_ctx.Block, path, "this place has no target of its own: put damage, heal, restores, removes and applies inside an Area, say On = EffectOn.Caster, or use a trigger that has a target");
            if (hits) Hit(e, t, id);
            if (e.Heal is { } heal) Change("Health", Health, heal, t, id);
            if (e.Restores is { } r)
            {
                if (r.Health != 0) Change("Health", Health, r.Health, t, id);
                if (r.Mana != 0) Change("Mana", Mana, r.Mana, t, id);
                if (r.Stamina != 0) Change("Stamina", Stamina, r.Stamina, t, id);
            }
            if (e.Removes is { Count: > 0 } removes)
                for (var i = 0; i < removes.Count; i++)
                {
                    var status = _ctx.StatusVar(_b, $"remove{id}.{i}", removes[i], $"{path}.removes[{i}]", out _);
                    _b.Call("removeBuff", null, null, status, t);
                    _b.Pop();
                }
            if (e.Applies is { } a) Apply(a, t, id, path);
        }
        if (e.Then is { Count: > 0 } then)
        {
            if (target is { } t2) OnTarget(then, t2, $"{path}.then");
            else AtPoint(then, origin!.Value, _originPosition, $"{path}.then");
        }
        _b.Mark(end);
    }

    private void Change(string name, Guid attribute, int amount, int target, int id)
    {
        _b.Call("changeAttributeValue", null, _b.VGuid($"attr{name}", ImpactType.Attribute, attribute), _b.VByte("opAdd", ImpactType.MathOp, 0),
            _b.V($"old{name}{id}", ImpactType.Sint32), target, null, _b.VInt($"amount{name}{id}", amount));
        _b.Pop();
    }

    private void Spawn(TemplateRef what, int place, int id, string path)
    {
        if (string.IsNullOrWhiteSpace(what.Value)) throw new BlockError(_ctx.Block, path, "spawns needs a template: a GameTemplates constant or a GUID");
        var template = Guid.TryParse(what.Value, out var guid)
            ? _b.VGuid($"spawn{id}", ImpactType.Template, guid)
            : _b.VLate($"spawn{id}", ImpactType.Template, "template", what.Value, _ctx.Block, path);
        var yes = _b.VBool("yes", true);
        var no = _b.VBool("no", false);
        _b.Call("spawnEntity", null, null, no, no, yes, yes, template, place, null, null, null, null, null, null);
        _b.Pop();
    }

    private void Hit(EffectBlock e, int target, int id)
    {
        var d = e.Damage ?? new DamageBlock();
        var main = Primary && e.Damage != null && PrimaryDamage == null;
        if (main) PrimaryDamage = d;
        var primary = main && !d.Flat;
        var setup = new[] { d.Cut, d.Pierce, d.Blunt, d.Fire, d.Ice, d.Shroud, d.Lightning, 0f, d.Poison };
        var bytes = new byte[52];
        Buffer.BlockCopy(setup, 0, bytes, 0, 36);
        BitConverter.GetBytes(d.Wood).CopyTo(bytes, 36);
        BitConverter.GetBytes(d.Stone).CopyTo(bytes, 40);
        BitConverter.GetBytes(d.Metal).CopyTo(bytes, 44);
        BitConverter.GetBytes(d.Explosion).CopyTo(bytes, 48);
        var physical = d.Cut + d.Pierce + d.Blunt > 0;
        var category = (byte)((physical ? Melee : Magic) | (main ? AoE : Other));
        // In a program that runs with a used item the game writes the item's power into the first variable of this type, whatever its config
        // (0x192250, by way of the type scan at 0x98c9d0). So that variable is always this one: the main hit's, or else a spare nobody reads, and
        // the numbers an author wrote for every other hit stay as written.
        var damage = _b.V("itemDamage", ImpactType.Damage);
        if (primary) _b.SetVar(damage, bytes, CfgDamage);
        else damage = _b.V($"damage{id}", ImpactType.Damage, bytes);
        var pos = _b.V($"hitAt{id}", ImpactType.Float3);
        _b.Call("getTransform", null, null, pos, target);
        _b.Pop();
        var one = _b.VFloat("one", 1);
        var zero = _b.VFloat("zero", 0);
        var yes = _b.VBool("yes", true);
        var no = _b.VBool("no", false);
        _b.Call("applyDamage", null,
            _b.V($"blocked{id}", ImpactType.Bool), Caster, target, pos,
            _b.V("normal", ImpactType.Float3, Up),
            _b.VByte("direction", ImpactType.Direction, 1), one, zero, zero, null, _b.VInt("special", 0),
            _b.VGuid($"scale{id}", ImpactType.Attribute, physical ? PhysicalScale : MagicScale, primary ? CfgDamageScale : 0),
            _b.VByte($"push{id}", ImpactType.Push, (byte)e.Knockback), _b.VU16($"reaction{id}", ImpactType.Reaction, (ushort)e.Flinch),
            damage,
            _b.VByte($"category{id}", ImpactType.AttackMask, category), _b.V("weapon", ImpactType.WeaponCategory),
            _b.V("material", ImpactType.Material), null, no, yes, no, _b.VByte("visceral", ImpactType.Visceral, 0), no, no);
        _b.Pop();
    }

    private void Apply(StatusApply a, int target, int id, string path)
    {
        var status = _ctx.StatusVar(_b, $"status{id}", a.Status, $"{path}.applies", out var own);
        var seconds = a.Seconds ?? own;
        if (seconds is { } known) FirstStatusSeconds ??= known;
        // unconnected: the status's own time (a game status: its time is the game's business)
        _b.Call("applyBuff", null, status, target, null, null, seconds is { } s ? _b.VTime($"statusFor{id}", s) : null);
        _b.Pop();
    }

    private int PlaceOf(int target, int id)
    {
        var place = _b.V($"place{id}", ImpactType.Transform);
        _b.Call("getTransform", null, place, null, target);
        _b.Pop();
        return place;
    }

    private void Area(AreaBlock area, int origin, int id, string path)
    {
        var site = $"area{id}";
        var who = _b.V($"who{id}", ImpactType.Entity);
        var index = _b.V($"index{id}", ImpactType.Uint32);
        var radius = _b.VFloat($"radius{id}", area.Radius);
        var factions = _b.VU16("factions", ImpactType.FactionMask, AllFactions);
        _areas.Add((site, who, index, origin, radius));
        _b.Zero(index);
        var next = _b.NewLabel();
        var done = _b.NewLabel();
        _b.Mark(next);
        _b.Call("forEachEntityWithFactionInRange", site, who, null, index, factions, origin, radius);
        _b.IfFalse(done);
        _b.Call(area.Affects == AreaAffects.Enemies ? "isEnemy" : "isFriend", null, Caster, who);
        _b.IfFalse(next);
        // Only what has health: the query also finds a creature's child entities (weak spots, VFX links). The game keeps one hit per root
        // target and impact, and a hit kept on a VFX link is lost, so hitting those too lost the damage at random.
        var health = _b.V($"whoHealth{id}", ImpactType.Sint32);
        _b.Zero(health);
        _b.Call("fetchOptionalAttribute", null, health, _b.VGuid("attrHealth", ImpactType.Attribute, Health), who);
        _b.Pop();
        _b.Load(health);
        _b.IfFalse(next);
        OnTarget(area.Effects, who, $"{path}.effects");
        _b.Goto(next);
        _b.Mark(done);
    }

    /// <summary>What a tooltip shows, from another program of the same block.</summary>
    public void Adopt(EffectEmitter other)
    {
        PrimaryDamage ??= other.PrimaryDamage;
        FirstRadius ??= other.FirstRadius;
        FirstStatusSeconds ??= other.FirstStatusSeconds;
    }

    // ---- hosts: a program that says who the caster is and who the target is, and runs the effects ----------------------------------------

    /// <summary>The program of a place: what a projectile leaves behind (a template with this as its program), or a RunImpact. It does what
    /// the block says and nothing more: a starter's own SpawnTemplate config (Ice Bolt's slowing ground zone) is not spawned.</summary>
    public static (Guid Guid, byte[] Blob, EffectEmitter E, string Listing) PointProgram(BlockContext ctx, string key, IReadOnlyList<EffectRef> effects, string path, bool primary)
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
        e.AtPoint(effects, origin, pos, path);
        b.Halt();
        b.BeginShutdown();
        e.EmitShutdown();
        b.Halt();
        var (guid, blob) = ctx.Build(b);
        return (guid, blob, e, b.Listing.ToString());
    }

    /// <summary>Runs once on the root of whatever carries it: who used an item, who is casting (<c>ItemBlock.OnUse</c>, a template spawned by a
    /// sequence event). Caster and target are the same.</summary>
    public static (Guid Guid, byte[] Blob, EffectEmitter E, string Listing) SelfProgram(BlockContext ctx, string key, IReadOnlyList<EffectRef> effects, string path)
    {
        var b = new ProgramBuilder(key);
        var e = new EffectEmitter(b, ctx);
        b.BeginCode();
        b.Call("getRoot", null, e.Caster);
        b.Pop();
        e.OnTarget(effects, e.Caster, path);
        b.DestroySelf();     // as the game's potion program (d3c4dd63): nothing else ends the entity a use sequence's SpawnImpact spawns
        b.Halt();
        b.BeginShutdown();
        e.EmitShutdown();
        b.Halt();
        var (guid, blob) = ctx.Build(b);
        return (guid, blob, e, b.Listing.ToString());
    }

    /// <summary>The program that follows a wielder: every hit they land runs the effects on the target. Hits an effect dealt are skipped.</summary>
    public static (Guid Guid, byte[] Blob, EffectEmitter E, string Listing) OnHitProgram(BlockContext ctx, string key, IReadOnlyList<EffectRef> effects, string path) =>
        WielderProgram(ctx, key, effects, path, null, null, null, null);

    /// <summary>The program that follows whoever wields or wears something, reading the game's OnHit stream. <paramref name="onHit"/>: each hit they
    /// land, on what was hit. <paramref name="onKill"/>: each kill, on what was killed. <paramref name="onHitTaken"/>: each hit they take, on who dealt it.
    /// Hits an effect dealt are skipped, so an effect cannot trigger itself. Pass it to <see cref="BlockRuntime.AddWeapon"/>.</summary>
    public static (Guid Guid, byte[] Blob, EffectEmitter E, string Listing) WielderProgram(BlockContext ctx, string key,
        IReadOnlyList<EffectRef>? onHit, string? onHitPath, IReadOnlyList<EffectRef>? onKill, string? onKillPath, IReadOnlyList<EffectRef>? onHitTaken, string? onHitTakenPath)
    {
        var b = new ProgramBuilder(key) { Streams = 1 << 1 };
        var e = new EffectEmitter(b, ctx);
        var stream = b.V("stream", ImpactType.EventStream);
        var hitStream = b.VByte("onHit", ImpactType.StreamType, 1);
        var i = b.V("i", ImpactType.Uint32);
        var by = b.V("by", ImpactType.Entity);
        var target = b.V("target", ImpactType.Entity);
        var killed = b.V("killed", ImpactType.Bool);
        var category = b.V("hitCategory", ImpactType.AttackMask);
        var marked = b.VByte("marked", ImpactType.AttackMask, Other);
        var step = b.NewLabel();
        var next = b.NewLabel();
        var hit = b.NewLabel();
        var dealt = b.NewLabel();
        b.BeginCode();
        b.Mark(step);
        b.Call("getRoot", null, e.Caster);
        b.Pop();
        b.Call("onEvent", null, stream, hitStream);
        b.Pop();
        b.Zero(i);
        b.Mark(next);
        b.Call("forEachHitEventInStream", null, null, null, null, null, null, null, onKill is { Count: > 0 } ? killed : null, null, null, null, null, category, null, null, by, null, target, null, null, null, null, null, null, i, stream);
        b.IfTrue(hit);
        b.Yield();
        b.Goto(step);
        b.Mark(hit);
        // every argument connected, as vanilla's 14 calls: this native reads matchAll without checking it (an unconnected one faults)
        b.Call("hasAttackCategory", null, marked, b.VBool("no", false), category);
        b.IfTrue(next);
        b.Call("isSameEntity", null, by, e.Caster);
        b.IfTrue(dealt);
        if (onHitTaken is { Count: > 0 })
        {
            b.Call("isSameEntity", null, target, e.Caster);
            b.IfFalse(next);
            b.Call("isValidEntity", null, by);
            b.IfFalse(next);
            e.OnTarget(onHitTaken, by, onHitTakenPath ?? "onHitTaken");
        }
        b.Goto(next);
        b.Mark(dealt);
        b.Call("isEnemy", null, e.Caster, target);
        b.IfFalse(next);
        if (onHit is { Count: > 0 }) e.OnTarget(onHit, target, onHitPath ?? "onHit");
        if (onKill is { Count: > 0 })
        {
            b.Load(killed);
            b.IfFalse(next);
            e.OnTarget(onKill, target, onKillPath ?? "onKill");
        }
        b.Goto(next);
        b.Halt();
        b.BeginShutdown();
        e.EmitShutdown();
        b.Halt();
        var (guid, blob) = ctx.Build(b);
        return (guid, blob, e, b.Listing.ToString());
    }

    /// <summary>A projectile's flight, for one with effects of its own: <paramref name="onCast"/> when it leaves, <paramref name="onHit"/> on what it
    /// strikes, then as the game's own projectile program (its configs too: the look's template sets them): the material's reaction, the
    /// impact and second templates, the noise, gone.</summary>
    public static (Guid Guid, byte[] Blob, EffectEmitter E, string Listing) ProjectileProgram(BlockContext ctx, string key, IReadOnlyList<EffectRef>? onCast, string onCastPath,
        IReadOnlyList<EffectRef>? onHit, string onHitPath, bool primary)
    {
        const uint CfgAmmunition = 0x4baa194c, CfgUsedItem = 0x220e6b10, CfgMaterial = 0xbf09083f, CfgSpawn = 0xf6a785cd, CfgSpawn2 = 0x29aa2976, CfgNoise = 0xa19409b2;
        const uint MaterialWater = 0x69ff9049;
        var b = new ProgramBuilder(key) { Streams = 1 << 0 };
        var e = new EffectEmitter(b, ctx) { Primary = primary };
        var all = b.V("collisions", ImpactType.EventStream);
        var notFriends = b.V("notFriends", ImpactType.EventStream);
        var notOurs = b.V("notOurs", ImpactType.EventStream);
        var streamType = b.VByte("onCollision", ImpactType.StreamType, 0);
        var yes = b.VBool("yes", true);
        var no = b.VBool("no", false);
        var i = b.V("i", ImpactType.Uint32);
        var struck = b.V("struck", ImpactType.Entity);
        var pos = b.V("position", ImpactType.Float3);
        var normal = b.V("normal", ImpactType.Float3);
        var materialId = b.V("materialId", ImpactType.Material);
        var combat = b.V("isCombatTarget", ImpactType.Bool);
        var ammunition = b.V("ammunition", ImpactType.ItemId, default, CfgAmmunition);
        var used = b.V("usedItem", ImpactType.ItemId, default, CfgUsedItem);
        var material = b.V("material", ImpactType.Material, default, CfgMaterial);
        var spawn = b.V("spawnTemplate", ImpactType.Template, default, CfgSpawn);
        var spawn2 = b.V("spawnTemplate2", ImpactType.Template, default, CfgSpawn2);
        var noise = b.V("noiseRange", ImpactType.Float, default, CfgNoise);
        var water = b.V("water", ImpactType.Material, BitConverter.GetBytes(MaterialWater));
        var inMaterial = b.V("inMaterial", ImpactType.Material);
        var equalOp = b.VByte("equalOp", ImpactType.CompareOp, 0);
        var step = b.NewLabel();
        var next = b.NewLabel();
        var collision = b.NewLabel();
        var land = b.NewLabel();
        var noHit = b.NewLabel();
        b.BeginCode();
        b.Call("getRoot", null, e.Caster);
        b.Pop();
        if (onCast is { Count: > 0 })
        {
            var cast = new EffectEmitter(b, ctx, e.Caster, 1000);
            cast.OnTarget(onCast, e.Caster, onCastPath);
            e._areas.AddRange(cast._areas);
        }
        b.Mark(step);
        b.Call("onEvent", null, all, streamType);
        b.Pop();
        b.Call("filterFriends", null, notFriends, yes, all, null);
        b.Pop();
        b.Call("filterSameRoot", null, notOurs, yes, notFriends, null);
        b.Pop();
        b.Zero(i);
        b.Mark(next);
        b.Call("forEachCollisionInStream", null, null, struck, pos, normal, null, materialId, null, combat, null, i, notOurs);
        b.IfTrue(collision);
        b.Yield();
        b.Goto(step);
        b.Mark(collision);
        b.Call("itemBasedMaterialReaction", null, inMaterial, pos, normal, materialId, combat, ammunition, used, material);
        b.Pop();
        b.Call("equal", null, equalOp, water, materialId);
        b.IfFalse(land);
        b.Goto(next);
        b.Mark(land);
        if (onHit is { Count: > 0 })
        {
            b.Load(combat);
            b.IfFalse(noHit);
            e.OnTarget(onHit, struck, onHitPath);
            b.Mark(noHit);
        }
        foreach (var template in new[] { spawn, spawn2 })
        {
            b.Call("spawnEntity", null, null, no, no, yes, yes, template, null, pos, null, null, null, null, null);
            b.Pop();
        }
        b.Call("triggerNoise", null, pos, noise);
        b.Pop();
        b.DestroySelf();
        b.Yield();
        b.Goto(step);
        b.Halt();
        b.BeginShutdown();
        e.EmitShutdown();
        b.Halt();
        var (guid, blob) = ctx.Build(b);
        return (guid, blob, e, b.Listing.ToString());
    }

    /// <summary>A status's program: it runs on the status's own entity while the status lasts.</summary>
    public static (Guid Guid, byte[] Blob, string Listing) StatusProgram(BlockContext ctx, string key, StatusBlock s)
    {
        var b = new ProgramBuilder(key);
        var e = new EffectEmitter(b, ctx);
        var target = b.V("target", ImpactType.Entity);
        var modifies = new List<(string Site, int Attr, int Op, int Storage, int Sign, int Value)>();
        b.BeginCode();
        b.Call("getBuffTarget", null, target);
        b.Pop();
        b.Call("getRoot", null, e.Caster);
        b.Pop();
        foreach (var (stat, value) in s.Modifies)
        {
            var path = $"modifies[{stat.Key}]";
            if (stat.Whole && value != Math.Floor(value)) throw new BlockError(ctx.Block, path, $"{value}: {stat.Key} takes whole numbers");
            if (value == 0) throw new BlockError(ctx.Block, path, "0 changes nothing");
            var n = modifies.Count;
            var type = stat.Whole ? ImpactType.Sint32 : ImpactType.Float;
            var m = ($"modify{n}",
                b.VGuid($"modifyAttr{n}", ImpactType.Attribute, stat.Attribute),
                b.VByte($"modifyOp{n}", ImpactType.MathOp, (byte)(stat.Subtract ? 1 : 0)),
                b.V($"modifyStorage{n}", type),
                b.V($"modifySign{n}", ImpactType.Bool),
                stat.Whole ? b.VInt($"modifyValue{n}", (int)value) : b.VFloat($"modifyValue{n}", (float)value));
            modifies.Add(m);
            b.Call("applyAttributeValue", m.Item1, m.Item2, m.Item3, m.Item4, m.Item5, target, null, m.Item6);
            b.Pop();
        }
        if (s.OnApply is { Count: > 0 } onApply) e.OnTarget(onApply, target, "onApply");
        if (s.OnTick is { Count: > 0 } onTick)
        {
            if (s.TickEvery <= 0) throw new BlockError(ctx.Block, "tickEvery", $"{s.TickEvery} must be more than 0");
            var tick = b.NewLabel();
            var every = b.VTime("every", s.TickEvery);
            b.Mark(tick);
            b.Wait(every);
            e.OnTarget(onTick, target, "onTick");
            b.Goto(tick);
        }
        else if (s.OnExpire is { Count: > 0 } || modifies.Count > 0)
        {
            var idle = b.NewLabel();     // it stays alive so that its shutdown code runs when the status ends
            b.Mark(idle);
            b.Yield();
            b.Goto(idle);
        }
        b.Halt();
        b.BeginShutdown();
        if (s.OnExpire is { Count: > 0 } onExpire) e.OnTarget(onExpire, target, "onExpire");
        foreach (var m in modifies)
        {
            b.Call("revertApplyAttributeValue", m.Site, m.Attr, m.Op, m.Storage, m.Sign, target, null, m.Value);
            b.Pop();
        }
        e.EmitShutdown();
        b.Halt();
        var (guid, blob) = ctx.Build(b);
        return (guid, blob, b.Listing.ToString());
    }
}
