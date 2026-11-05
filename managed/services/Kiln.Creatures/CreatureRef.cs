// SPDX-License-Identifier: MIT
// A handle on one creature: values with owner layers, private AI copies, readiness, commands.
using System.Globalization;

namespace Kiln;

/// <summary>
/// One creature or NPC. Setters are layers of the calling mod (reverted when it unloads or calls <see cref="Reset"/>);
/// getters read what the entity has now. Tick thread. A handle on an entity that despawned reads as missing
/// (<see cref="Exists"/> false) and its setters throw <see cref="KeyNotFoundException"/>.
/// </summary>
public readonly unsafe struct CreatureRef
{
    private readonly Creatures.OwnerKey _owner;

    internal CreatureRef(EntityId entity, Creatures.OwnerKey owner)
    {
        Entity = entity;
        _owner = owner;
    }

    public EntityId Entity { get; }

    /// <summary>The entity exists now.</summary>
    public bool Exists => Spawn.Inspect(Entity) is not null;

    /// <summary>A full snapshot, or null when the entity does not exist.</summary>
    public CreatureInfo? Inspect()
    {
        var t = Creatures.Tick("CreatureRef.Inspect");
        var i = new CreaturesNative.Info { Size = (uint)sizeof(CreaturesNative.Info) };
        var rc = t->Inspect(Entity.Value, &i);
        if (rc == CreaturesNative.NotFound) return null;
        Creatures.Check(rc, "inspect");
        var g = i.Template.ToGuid();
        var name = Creatures.Template(g)?.Name ?? Spawn.Template(g)?.Name ?? "";
        return new CreatureInfo(Entity, g, name, (CreatureKind)i.Kind, (i.Flags & 1) != 0, (EnemyState)i.State,
            i.AwarenessState, i.Awareness, (Faction)i.Faction, (int)i.Level, (int)i.LevelMax, i.Health, i.HealthMax,
            i.Scale, i.XpMod, i.SpeedFactor, i.DamageMultiplier, (EnemyFeatures)i.FeaturesTemplate,
            (EnemyFeatures)i.FeaturesEnabled, (EnemyFeatures)i.FeaturesDisabled, new EntityId(i.Target),
            new EntityId(i.Guard), new WorldPosition(i.Home.X, i.Home.Y, i.Home.Z), i.LootLabels.ToGuid(),
            i.LootSlotsMin, i.LootSlotsMax, i.LootDisabled != 0, (i.Flags & 2) != 0, (i.Flags & 4) != 0,
            (i.Flags & 8) != 0, (int)i.Layers, (int)i.PrivateCopies);
    }

    /// <summary>Its catalog template (null for non-creatures).</summary>
    public CreatureTemplate? Template => Spawn.Inspect(Entity)?.Template is { } g ? Creatures.Template(g) : null;

    /// <summary>The enemy AI state (Invalid without enemy AI).</summary>
    public EnemyState State => Inspect()?.State ?? EnemyState.Invalid;

    /// <summary>The AI's current target (SelectedTargets[0]); None when it has none.</summary>
    public EntityId Target => Inspect()?.Target ?? EntityId.None;

    // ---------------------------------------------------------------------------------------------- values ----

    internal CreaturesNative.Value GetRaw(uint field, uint index = 0)
    {
        var t = Creatures.Tick("CreatureRef");
        var v = new CreaturesNative.Value();
        Creatures.Check(t->Get(Entity.Value, field, index, &v), $"entity {Entity}: read");
        return v;
    }

    internal void SetRaw(uint field, uint index, CreaturesNative.Value v, string what)
    {
        var t = Creatures.Tick("CreatureRef");
        var o = Creatures.RequireOwner(_owner, what);
        Creatures.Check(t->Set(o.Native, Entity.Value, field, index, &v), $"entity {Entity}: {what}");
    }

    private void SetNumber(uint field, double value, string what, uint index = 0) =>
        SetRaw(field, index, new CreaturesNative.Value { Number = value }, what);

    /// <summary>Level (1..its level max). The game's enemy_scaling re-scales health and damage from it within a step
    /// (Vukah: level 1 = 119 HP, 10 = 417, 20 = about 900).</summary>
    public int Level
    {
        get => (int)GetRaw(CreaturesNative.Level).Number;
        set => SetNumber(CreaturesNative.Level, value, "Level");
    }

    /// <summary>Faction: who it fights. <see cref="Kiln.Faction.PlayerAlly"/> makes it fight on the players' side.</summary>
    public Faction Faction
    {
        get => (Faction)(byte)GetRaw(CreaturesNative.Faction).Number;
        set => SetNumber(CreaturesNative.Faction, (byte)value, "Faction");
    }

    /// <summary>Uniform scale (ScaleComponent and the transform's scale).</summary>
    public float Scale
    {
        get => (float)GetRaw(CreaturesNative.Scale).Number;
        set => SetNumber(CreaturesNative.Scale, value, "Scale");
    }

    /// <summary>XP factor of a kill (ExperienceSource.xpMod; 1 = the template's).</summary>
    public float XpFactor
    {
        get => (float)GetRaw(CreaturesNative.Xp).Number;
        set => SetNumber(CreaturesNative.Xp, value, "XpFactor");
    }

    /// <summary>Factor on all movement speeds against the values before the first change (1 = unchanged).</summary>
    public float SpeedFactor
    {
        get => (float)GetRaw(CreaturesNative.Speed).Number;
        set => SetNumber(CreaturesNative.Speed, value, "SpeedFactor");
    }

    /// <summary>Damage multiplier (DamageMod_Multiplier; DamageMod = base * multiplier + adder).</summary>
    public float DamageFactor
    {
        get => (float)GetRaw(CreaturesNative.Damage).Number;
        set => SetNumber(CreaturesNative.Damage, value, "DamageFactor");
    }

    /// <summary>Where it roams around and returns to (the AI's home zone centre). Setting it moves an idle creature
    /// there over time (roaming, return home); a vanilla behaviour, not a path command.</summary>
    public WorldPosition Home
    {
        get
        {
            var v = GetRaw(CreaturesNative.Home);
            return new WorldPosition(v.Vec.X, v.Vec.Y, v.Vec.Z);
        }
        set => SetRaw(CreaturesNative.Home, 0, new CreaturesNative.Value { Vec = new() { X = value.X, Y = value.Y, Z = value.Z } }, "Home");
    }

    /// <summary>Damage susceptibility per kind (1 = normal, 0 = immune, 2 = double).</summary>
    public ResistanceAccess Resistance => new(this);

    /// <summary>Loot labels, slots, and the random-loot switch.</summary>
    public LootAccess Loot => new(this);

    /// <summary>Enemy AI feature switches (per entity, on top of the template's).</summary>
    public FeatureAccess Features => new(this);

    /// <summary>Private template settings of this one creature (Enemy01BehaviorState by default).</summary>
    public AiAccess Ai => new(Entity, _owner);

    /// <summary>The arsenal (attacks and behaviour rules) its enemy AI uses. Setting it gives the entity a private
    /// EnemyArsenalReference copy: the AI looks the arsenal up by GUID every update. Only arsenals of the same skeleton
    /// family animate correctly (their attack sequences assume the skeleton).</summary>
    public Guid Arsenal
    {
        get
        {
            var t = Creatures.Tick("CreatureRef.Arsenal");
            var g = new CreaturesNative.GuidBytes();
            uint known = 0;
            Creatures.Check(t->ArsenalOf(Entity.Value, &g, &known), $"entity {Entity}: arsenal");
            return g.ToGuid();
        }
        set => Ai.Set("EnemyArsenalReference.arsenal", value.ToString());
    }

    /// <summary>Drops everything the calling mod changed on this creature (values and AI overrides). Returns how
    /// many changes were dropped.</summary>
    public int Reset()
    {
        var t = Creatures.Tick("CreatureRef.Reset");
        var o = Creatures.RequireOwner(_owner, "Reset");
        uint a = 0, b = 0;
        t->Reset(o.Native, Entity.Value, 0, 0, &a);
        var empty = default(NativeStr);
        t->AiReset(o.Native, Entity.Value, empty, empty, &b);
        t->SetReady(o.Native, Entity.Value, 0);
        return (int)(a + b);
    }

    /// <summary>Lab only (needs <c>kiln/data/kiln.creatures/force-ready</c>): keeps the creature "ready for gameplay"
    /// so its AI leaves Spawning without a player near. It still cannot walk (no path grid without a player).</summary>
    public void ForceReady(bool on = true)
    {
        var t = Creatures.Tick("CreatureRef.ForceReady");
        var o = Creatures.RequireOwner(_owner, "ForceReady");
        Creatures.Check(t->SetReady(o.Native, Entity.Value, on ? 1u : 0u), $"entity {Entity}: force ready");
    }

    /// <summary>Commands sent as the game's own events (applied in the next sim step).</summary>
    public CreatureCommands Command => new(Entity, _owner);

    /// <summary>The same creature, with changes owned by a named owner instead of the calling mod (data packs).</summary>
    internal CreatureRef As(Creatures.OwnerKey owner) => new(Entity, owner);

    public override string ToString() => $"creature {Entity}";

    // ---------------------------------------------------------------------------------------------- accessors ----

    public sealed class ResistanceAccess
    {
        private readonly CreatureRef _c;
        internal ResistanceAccess(CreatureRef c) => _c = c;

        public float this[DamageKind kind]
        {
            get => (float)_c.GetRaw(CreaturesNative.Resist, (uint)kind).Number;
            set => _c.SetRaw(CreaturesNative.Resist, (uint)kind, new CreaturesNative.Value { Number = value }, $"Resistance[{kind}]");
        }

        /// <summary>Drops the calling mod's change of one kind.</summary>
        public void Reset(DamageKind kind) => _c.ResetField(CreaturesNative.Resist, (uint)kind);
    }

    public sealed class LootAccess
    {
        private readonly CreatureRef _c;
        internal LootAccess(CreatureRef c) => _c = c;

        /// <summary>RandomLoot.defaultLabels: the loot label collection its drops are rolled from.</summary>
        public Guid Labels
        {
            get => _c.GetRaw(CreaturesNative.LootLabels).Guid.ToGuid();
            set => _c.SetRaw(CreaturesNative.LootLabels, 0, new CreaturesNative.Value { Guid = CreaturesNative.GuidBytes.From(value) }, "Loot.Labels");
        }

        /// <summary>How many loot slots are rolled (min, max).</summary>
        public (int Min, int Max) Slots
        {
            get
            {
                var v = _c.GetRaw(CreaturesNative.LootSlots).Vec;
                return ((int)v.X, (int)v.Y);
            }
            set => _c.SetRaw(CreaturesNative.LootSlots, 0, new CreaturesNative.Value { Vec = new() { X = value.Min, Y = value.Max } }, "Loot.Slots");
        }

        /// <summary>No random loot at all.</summary>
        public bool Disabled
        {
            get => _c.GetRaw(CreaturesNative.LootDisabled).Number != 0;
            set => _c.SetRaw(CreaturesNative.LootDisabled, 0, new CreaturesNative.Value { Number = value ? 1 : 0 }, "Loot.Disabled");
        }
    }

    public sealed class FeatureAccess
    {
        private readonly CreatureRef _c;
        internal FeatureAccess(CreatureRef c) => _c = c;

        /// <summary>The features this entity has on top of / against its template (per-entity masks).</summary>
        public EnemyFeatures Enabled => (EnemyFeatures)(uint)_c.GetRaw(CreaturesNative.FeaturesOn).Number;
        public EnemyFeatures Disabled => (EnemyFeatures)(uint)_c.GetRaw(CreaturesNative.FeaturesOff).Number;

        /// <summary>Switches features on for this entity (the calling mod's layer; layers of several mods combine).</summary>
        public void Enable(EnemyFeatures f) => _c.SetRaw(CreaturesNative.FeaturesOn, 0, new CreaturesNative.Value { Number = (uint)f }, "Features.Enable");

        /// <summary>Switches features off for this entity (e.g. FleeHome | ReturnHome: never runs home).</summary>
        public void Disable(EnemyFeatures f) => _c.SetRaw(CreaturesNative.FeaturesOff, 0, new CreaturesNative.Value { Number = (uint)f }, "Features.Disable");

        /// <summary>Drops the calling mod's feature changes.</summary>
        public void Reset()
        {
            _c.ResetField(CreaturesNative.FeaturesOn, 0);
            _c.ResetField(CreaturesNative.FeaturesOff, 0);
        }
    }

    internal void ResetField(uint field, uint index)
    {
        var t = Creatures.Tick("CreatureRef.Reset");
        var o = Creatures.RequireOwner(_owner, "Reset");
        uint n = 0;
        t->Reset(o.Native, Entity.Value, field, index, &n);
    }
}

/// <summary>Private template settings of one creature: field paths of a template component, by the game's reflection
/// ("aggroSetup.viewRange", "combatSetup.cooldownGroups[0].cooldown", "guardSetup.canGuardPlayer",
/// "fleeSetup.maxFleeDuration"). The first <see cref="Set(string, string)"/> gives the entity its own copy of that component; other
/// creatures of the template keep the shared data. Paths may name the component first
/// ("Aggro.applyEnemyValue", "EnemyScaling.enemyLevelScaling.health"); without one it is Enemy01BehaviorState.</summary>
public readonly unsafe struct AiAccess
{
    private readonly EntityId _e;
    private readonly Creatures.OwnerKey _owner;
    internal AiAccess(EntityId e, Creatures.OwnerKey owner)
    {
        _e = e;
        _owner = owner;
    }

    private static readonly string[] KnownComponents =
        ["Enemy01BehaviorState", "EnemyArsenalReference", "Aggro", "EnemyScaling", "EnemyAiming", "FleeSensor",
         "FriendSensor", "EnemyPhases", "HitReactionBehavior", "Locomotion", "PathWalker", "SubEntities"];

    internal static (string component, string path) Split(string path)
    {
        var dot = path.IndexOf('.');
        if (dot > 0)
        {
            var head = path[..dot];
            if (KnownComponents.Contains(head, StringComparer.OrdinalIgnoreCase) || (char.IsUpper(head[0]) && !head.Contains('[')))
                return (head, path[(dot + 1)..]);
        }
        return ("Enemy01BehaviorState", path);
    }

    /// <summary>Sets a field (text: numbers, true/false, enum names, flags "A|B" or "+A -B", GUIDs, seconds for
    /// keen::Time). Throws <see cref="ArgumentException"/> for unknown paths or values (kiln.log has the reason).</summary>
    public void Set(string path, string value)
    {
        var t = Creatures.Tick("Ai.Set");
        var o = Creatures.RequireOwner(_owner, "Ai.Set");
        var (c, p) = Split(path);
        var cs = new CreaturesNative.Utf8(c);
        var ps = new CreaturesNative.Utf8(p);
        var vs = new CreaturesNative.Utf8(value);
        fixed (byte* a = cs.Bytes)
        fixed (byte* b = ps.Bytes)
        fixed (byte* d = vs.Bytes)
            Creatures.Check(t->AiSet(o.Native, _e.Value, cs.Str(a), ps.Str(b), vs.Str(d)), $"entity {_e}: Ai.Set {c}.{p} = {value}");
    }

    public void Set(string path, double value) => Set(path, value.ToString("R", CultureInfo.InvariantCulture));
    public void Set(string path, bool value) => Set(path, value ? "true" : "false");

    /// <summary>The field as this entity reads it now (its copy, else the shared template).</summary>
    public string Get(string path) => Get(path, out _);

    public string Get(string path, out bool isPrivate)
    {
        var t = Creatures.Tick("Ai.Get");
        var (c, p) = Split(path);
        var cs = new CreaturesNative.Utf8(c);
        var ps = new CreaturesNative.Utf8(p);
        var buf = stackalloc byte[256];
        nuint len = 0;
        uint priv = 0;
        fixed (byte* a = cs.Bytes)
        fixed (byte* b = ps.Bytes)
            Creatures.Check(t->AiGet(_e.Value, cs.Str(a), ps.Str(b), buf, 256, &len, &priv), $"entity {_e}: Ai.Get {c}.{p}");
        isPrivate = priv != 0;
        return System.Text.Encoding.UTF8.GetString(buf, (int)len);
    }

    public double GetNumber(string path) => double.Parse(Get(path), CultureInfo.InvariantCulture);

    /// <summary>Several fields at once.</summary>
    public void Override(Action<AiAccess> edit) => edit(this);

    /// <summary>Drops the calling mod's AI overrides (one path, one component, or all). The entity reads the shared
    /// template again once no override is left.</summary>
    public int Reset(string? pathOrComponent = null)
    {
        var t = Creatures.Tick("Ai.Reset");
        var o = Creatures.RequireOwner(_owner, "Ai.Reset");
        string c = "", p = "";
        if (!string.IsNullOrEmpty(pathOrComponent))
        {
            if (pathOrComponent.Contains('.')) (c, p) = Split(pathOrComponent);
            else if (KnownComponents.Contains(pathOrComponent, StringComparer.OrdinalIgnoreCase)) c = pathOrComponent;
            else (c, p) = ("Enemy01BehaviorState", pathOrComponent);
        }
        var cs = new CreaturesNative.Utf8(c);
        var ps = new CreaturesNative.Utf8(p);
        uint n = 0;
        fixed (byte* a = cs.Bytes)
        fixed (byte* b = ps.Bytes)
            t->AiReset(o.Native, _e.Value, cs.Str(a), ps.Str(b), &n);
        return (int)n;
    }
}

/// <summary>Commands for one creature, sent as the game's own events in the next sim step. Effects need the AI to run
/// (a player near on a dedicated server): see docs/kiln/services/creatures.md.</summary>
public readonly unsafe struct CreatureCommands
{
    private readonly EntityId _e;
    private readonly Creatures.OwnerKey _owner;
    internal CreatureCommands(EntityId e, Creatures.OwnerKey owner)
    {
        _e = e;
        _owner = owner;
    }

    private void Send(uint kind, EntityId target, float value = 0, uint pool = 0)
    {
        var t = Creatures.Tick("Creature command");
        var c = new CreaturesNative.Command
        {
            Size = (uint)sizeof(CreaturesNative.Command), Kind = kind, Entity = _e.Value, Target = target.Value,
            Value = value, Pool = pool,
        };
        Creatures.Check(t->SendCommand(&c), $"entity {_e}: command {kind}");
    }

    /// <summary>"Attack that": adds threat on the target (AddThreatTargetEvent; the threat system picks it).</summary>
    public void Attack(EntityId target, float threat = 1000) => Send(CreaturesNative.CmdAttack, target, threat);

    /// <summary>Guard (follow and protect) an entity, a player for companions: the game's StartGuardingEvent (its
    /// Guard state walks to the target) kept by Kiln with <see cref="GuardOptions.Default"/> (order kept, home follows,
    /// catch-up teleport, defend and assist). The order belongs to the calling mod and ends with
    /// <see cref="StopGuarding"/>, <see cref="Stay"/>, the guard's or the target's despawn, or the mod's unload.</summary>
    public void Guard(EntityId target) => Guard(target, GuardOptions.Default);

    /// <summary>Guard with explicit options (<see cref="GuardOptions.EventOnly"/> = only the game's event).</summary>
    public void Guard(EntityId target, GuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (target.IsNone || target.Equals(_e)) throw new ArgumentException($"entity {_e}: cannot guard {target}");
        Send(CreaturesNative.CmdGuard, target);
        Guarding.Start(_e, target, options, _owner.IsNone ? Creatures.OwnerKey.Named("kiln.creatures") : _owner);
    }

    /// <summary>Stop guarding (StopGuardingEvent, sent again until the game cleared it); ends Kiln's guard order (its
    /// home goes back).</summary>
    public void StopGuarding(EntityId target)
    {
        Send(CreaturesNative.CmdStopGuard, target);
        Guarding.Stop(_e, target);
        Guarding.StopSent(_e, target);
    }

    /// <summary>Stay here: stops guarding whom it guards and sets its home to where it stands.</summary>
    public void Stay()
    {
        var c = new CreatureRef(_e, _owner);
        var info = c.Inspect();
        if (info is null) throw new KeyNotFoundException($"entity {_e}: no such entity");
        if (!info.Guard.IsNone) StopGuarding(info.Guard);
        Guarding.Stop(_e, EntityId.None);  // Kiln's order too (the game may have dropped its guard target already)
        if (World.TryGetPosition(_e, out var p)) c.Home = p;
    }

    /// <summary>Move toward a position: its home zone moves there (roaming / return home walk it over). Not a
    /// direct path command: the enemy AI has none.</summary>
    public void MoveTo(WorldPosition position) => new CreatureRef(_e, _owner).Home = position;

    /// <summary>Restore health over <paramref name="seconds"/> (RestoreEnemyHealthEvent).</summary>
    public void Heal(float seconds = 3) => Send(CreaturesNative.CmdHeal, EntityId.None, seconds);

    /// <summary>A player-controlled combat target (RegisterControlledCombatTargetEvent, as companions get).</summary>
    public void ControlTarget(EntityId target) => Send(CreaturesNative.CmdControl, target);
}
