// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>Counters of kiln.abilities (diagnostics).</summary>
/// <param name="Natives">The native registry was captured and Kiln's natives and wrappers are registered.</param>
/// <param name="Programs">The game's ImpactRegistry is reachable: program lookup and registration work.</param>
/// <param name="Instances">The hooks on instance creation and first run are on: observing, config overrides and holds
/// work.</param>
/// <param name="Attributes">Attribute reads and writes work (<see cref="Abilities.Attributes"/>).</param>
/// <param name="VanillaNatives">Natives the game registered (107 on svn 1285601).</param>
/// <param name="NativeCapacity">The native registry's capacity (256).</param>
/// <param name="KilnNatives">Live Kiln natives: the built-in and the managed ones.</param>
/// <param name="ProgramsTotal">Programs in the ImpactRegistry.</param>
/// <param name="ProgramsKiln">Programs Kiln inserted.</param>
/// <param name="HeldNow">Instances held for event handlers right now (<see cref="ImpactStartingEvent"/>).</param>
/// <param name="Interest">The event kinds the listener asks for now, as a bit mask: one bit per kind, and bit 31 asks
/// to hold new instances.</param>
/// <param name="InstancesCreated">Impact instances created.</param>
/// <param name="InstancesHeld">Instances held for the listener, counted since start.</param>
/// <param name="OverridesApplied">Config overrides written into a variable.</param>
/// <param name="OverridesMismatched">Config overrides that wrote nothing: no variable matched, or the size did not
/// fit.</param>
/// <param name="Runs">Calls of the game's run-impact function that Kiln saw.</param>
/// <param name="NativeCalls">Calls of the vanilla natives Kiln wraps.</param>
/// <param name="NativeCallsObserved">Native calls reported to the listener as events
/// (<see cref="ImpactNativeEvent"/>).</param>
/// <param name="Signals"><c>kiln.Signal</c> calls.</param>
/// <param name="ManagedCalls">Calls of managed Kiln natives handed to the listener.</param>
/// <param name="ManagedSuspends">Times a program's call of a managed native suspended again while its answer was still
/// pending.</param>
/// <param name="EventsDelivered">Events delivered to the listener.</param>
/// <param name="EventsDropped">Events lost because the event queue was full or its lock could not be taken in
/// time.</param>
/// <param name="WrapCalls">Calls handed to <see cref="Abilities.Natives.Wrap"/> handlers.</param>
/// <param name="ProgramsRefused">Program registrations refused: the verifier said no, or the GUID belongs to a game
/// program or to another Kiln program.</param>
/// <param name="Step">The server world's step when the status was read.</param>
public sealed record AbilitiesStatus(bool Natives, bool Programs, bool Instances, bool Attributes, int VanillaNatives,
    int NativeCapacity, int KilnNatives, int ProgramsTotal, int ProgramsKiln, int HeldNow, uint Interest,
    ulong InstancesCreated, ulong InstancesHeld, ulong OverridesApplied, ulong OverridesMismatched, ulong Runs,
    ulong NativeCalls, ulong NativeCallsObserved, ulong Signals, ulong ManagedCalls, ulong ManagedSuspends,
    ulong EventsDelivered, ulong EventsDropped, ulong WrapCalls, ulong ProgramsRefused, ulong Step)
{
    /// <summary>Attachments whose program and site are not checked yet (no server world's programs yet); since
    /// 0.2.0-dev.29, 0 with an older kiln-abilities.dll.</summary>
    public int AttachmentsPending { get; init; }

    /// <summary>Attachments checked and taking calls (since 0.2.0-dev.29).</summary>
    public int AttachmentsActive { get; init; }

    /// <summary>Attachments that take no call: their program is not loaded or does not call the native there (checked
    /// again whenever a program is registered; since 0.2.0-dev.29).</summary>
    public int AttachmentsOff { get; init; }
}

/// <summary>A program in the game's ImpactRegistry.</summary>
/// <param name="Id">The program's id: FNV-1a of its GUID's bytes (<see cref="Abilities.Programs.IdOf(Guid)"/>).</param>
/// <param name="Guid">The program's GUID.</param>
/// <param name="CodeWords">Words of code in the program.</param>
/// <param name="ShutdownWords">Words of shutdown code.</param>
/// <param name="Variables">The number of variables.</param>
/// <param name="DataBytes">Bytes of variable data one instance holds.</param>
/// <param name="StackSize">The program's stack size.</param>
/// <param name="Streams">The streams the program uses, as bits: OnCollision 1, OnHit 2, OnParry 4, EntityInRange
/// 8.</param>
/// <param name="FromKiln">True when Kiln inserted the program (a mod's), false for a game program.</param>
public sealed record ImpactProgramInfo(uint Id, Guid Guid, int CodeWords, int ShutdownWords, int Variables, int DataBytes,
    int StackSize, int Streams, bool FromKiln);

/// <summary>A native in the game's registry.</summary>
/// <param name="Hash">FNV-1a-32 of the native's name: what a program's call names.</param>
/// <param name="Name">The native's display name.</param>
/// <param name="Argc">The number of arguments.</param>
/// <param name="Kind">Who registered it.</param>
public sealed record ImpactNativeInfo(uint Hash, string Name, int Argc, ImpactNativeKind Kind);

/// <summary>Who registered a native in the game's registry (<see cref="ImpactNativeInfo.Kind"/>).</summary>
public enum ImpactNativeKind
{
    /// <summary>Registered by the game and untouched.</summary>
    Vanilla = 0,
    /// <summary>A vanilla native Kiln observes or wraps (the game's function still runs).</summary>
    Wrapped = 1,
    /// <summary>kiln.Log, kiln.Signal.</summary>
    Builtin = 2,
    /// <summary>Registered by a mod through <see cref="Abilities.Natives.Register"/>.</summary>
    Managed = 3,
    /// <summary>Registered by a mod through <see cref="Abilities.Natives.RegisterSync"/> or an <see cref="ImpactNativeAttribute">[ImpactNative]</see>
    /// method: answered inside the call (since 0.2.0-dev.29).</summary>
    Sync = 4,
}

/// <summary>A resource attribute an ability can cost or change.</summary>
public enum AbilityResource
{
    /// <summary>The health attribute.</summary>
    Health,
    /// <summary>The mana attribute.</summary>
    Mana,
    /// <summary>The stamina attribute.</summary>
    Stamina,
    /// <summary>The special-ability charge (the bar that fills in combat).</summary>
    SpecialAbility,
}

/// <summary>Options of <see cref="Abilities.RunImpact(string, WorldPosition, ImpactOptions)"/> / <see cref="Abilities.RunProgram"/>.</summary>
public sealed class ImpactOptions
{
    /// <summary>The caster: the impact's ImpactHierarchy root and parent (kill credit, faction and same-root filters:
    /// the caster is not hit by its own impact). None = no hierarchy component.</summary>
    public EntityId Root { get; init; }

    /// <summary>The impact's faction (FactionType: Player 7, PlayerAlly 11, Vukah 3, ...). Null = the root's faction
    /// (when there is a root), else the template's.</summary>
    public byte? Faction { get; init; }

    /// <summary>Config values for the instance (see <see cref="ImpactConfigs"/>).</summary>
    public ImpactConfigs? Configs { get; init; }

    /// <summary>The spawned entity's yaw in degrees about the up axis. Default 0.</summary>
    public float YawDegrees { get; init; }

    /// <summary>Record the entity in kiln.spawn's per-world list (default: no; impacts are short-lived and never
    /// saved: they always get DoNotSave).</summary>
    public bool Track { get; init; }

    /// <summary>More spawn components (kiln.spawn's <see cref="SpawnOptions.Components"/>).</summary>
    public IReadOnlyList<SpawnComponent>? Components { get; init; }

    /// <summary>The tag kiln.spawn stores for the spawned entity (<see cref="SpawnOptions.Tag"/>). Default
    /// <c>"abilities"</c>.</summary>
    public string? Tag { get; init; }
}

/// <summary>
/// The game's Impact VM for mods: observe its programs (instances, native calls), change them per cast (config values
/// before an instance's first run, wrappers around natives), run existing programs and templates with chosen values,
/// add natives in C#, register new programs, and script abilities on top (<see cref="Register"/>).
/// </summary>
/// <remarks>Tick thread unless noted. Events: <see cref="ImpactStartedEvent"/>, <see cref="ImpactStartingEvent"/>,
/// <see cref="ImpactEndedEvent"/>, <see cref="ImpactNativeEvent"/> (<see cref="ImpactDamageEvent"/>,
/// <see cref="ImpactBuffEvent"/>, <see cref="ImpactSpawnEvent"/>), <see cref="ImpactSignalEvent"/>,
/// <see cref="SequenceStartedEvent"/>, <see cref="SequenceEndedEvent"/>, <see cref="AbilityCastEvent"/>,
/// <see cref="AbilityCastedEvent"/>. Docs: docs/kiln/services/abilities.md.</remarks>
public static unsafe partial class Abilities
{
    /// <summary>The default template the game spawns for a SpawnImpact sequence event without its own template (an
    /// entity with only an Impact component; immediates in actor_spawn_entity).</summary>
    public static readonly Guid GenericImpactTemplate = new([0xa4, 0x20, 0x7a, 0xe9, 0xa2, 0x4e, 0x67, 0x47,
        0x89, 0xae, 0xc4, 0x1f, 0xf4, 0x58, 0x48, 0xc1]);

    /// <summary>The service runs on this build.</summary>
    [AnyThread]
    public static bool Available => AbilitiesNative.T != null;

    /// <summary>kiln.abilities' state and counters, or null when it is not loaded. Any thread.</summary>
    [AnyThread]
    public static AbilitiesStatus? Status
    {
        get
        {
            var t = AbilitiesNative.T;
            if (t == null) return null;
            AbilitiesNative.Status s = default;
            s.Size = (uint)sizeof(AbilitiesNative.Status);
            if (t->GetStatus(&s) != AbilitiesNative.Ok) return null;
            var c = AbilitiesNative.Counters();  // dev.29: the attachments' states
            return new AbilitiesStatus(s.Natives != 0, s.Programs != 0, s.Instances != 0, s.Attributes != 0,
                (int)s.VanillaNatives, (int)s.NativeCapacity, (int)s.KilnNatives, (int)s.ProgramsTotal, (int)s.ProgramsKiln,
                (int)s.HeldNow, s.Interest, s.InstancesCreated, s.InstancesHeld, s.OverridesApplied, s.OverridesMismatched,
                s.Runs, s.NativeCalls, s.NativeCallsObserved, s.Signals, s.ManagedCalls, s.ManagedSuspends,
                s.EventsDelivered, s.EventsDropped, s.WrapCalls, s.ProgramsRefused, s.Step)
            {
                AttachmentsPending = c is { } p ? (int)p.AttachPending : 0,
                AttachmentsActive = c is { } a ? (int)a.AttachActive : 0,
                AttachmentsOff = c is { } o ? (int)o.AttachOff : 0,
            };
        }
    }

    // ---------------------------------------------------------------------------------------------- instances -------

    /// <summary>A live instance by impact id (null when it does not exist). Tick thread.</summary>
    [TickThreadOnly]
    public static ImpactInstance? Instance(uint impactId)
    {
        Game.AssertTickThread();
        var i = new ImpactInstance(impactId);
        return i.Alive ? i : null;
    }

    internal static IReadOnlyList<ImpactVariable> ReadVars(uint id, bool isProgram)
    {
        var t = AbilitiesNative.T;
        if (t == null) return [];
        uint count = 0;
        var rc = isProgram ? t->ProgramVars(id, null, 0, &count) : t->InstanceVars(id, null, 0, &count);
        if (count == 0 || (rc != AbilitiesNative.Ok && rc != AbilitiesNative.BufferTooSmall)) return [];
        var buf = new AbilitiesNative.Var[count];
        fixed (AbilitiesNative.Var* p = buf)
            rc = isProgram ? t->ProgramVars(id, p, count, &count) : t->InstanceVars(id, p, count, &count);
        if (rc != AbilitiesNative.Ok) return [];
        var list = new ImpactVariable[buf.Length];
        for (var i = 0; i < buf.Length; i++)
        {
            ref var v = ref buf[i];
            string name;
            fixed (byte* n = v.Name) name = AbilitiesNative.Text(n, 64);
            list[i] = new ImpactVariable((int)v.Index, v.NameHash, v.ConfigId, v.Type, v.ValueSize, v.Offset, name);
        }
        return list;
    }

    internal static ImpactVariable? FindVar(IReadOnlyList<ImpactVariable> vars, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        foreach (var v in vars)
            if (string.Equals(v.Name, key, StringComparison.OrdinalIgnoreCase)) return v;
        foreach (var v in vars)
            if (string.Equals(v.Alias, key, StringComparison.OrdinalIgnoreCase)) return v;
        var id = ImpactConfigIds.Parse(key);
        if (id != 0)
            foreach (var v in vars)
                if (v.ConfigId == id) return v;
        return null;
    }

    // ---------------------------------------------------------------------------------------------- the runner ------

    /// <summary>Spawns an impact template (a projectile, explosion or zone that has an Impact component) at
    /// <paramref name="position"/> and binds it to a caster: its program runs with the template's configs plus
    /// <see cref="ImpactOptions.Configs"/>. Returns the entity (it never reaches the save). Tick thread.</summary>
    [TickThreadOnly]
    public static Task<EntityId> RunImpact(string template, WorldPosition position, ImpactOptions? options = null)
    {
        var info = Spawn.Lookup(template) ?? throw new SpawnException($"unknown template '{template}'", AbilitiesNative.NotFound);
        return RunImpact(info.Id, position, options);
    }

    /// <inheritdoc cref="RunImpact(string, WorldPosition, ImpactOptions?)"/>
    [TickThreadOnly]
    public static Task<EntityId> RunImpact(Guid template, WorldPosition position, ImpactOptions? options = null)
    {
        try
        {
            Game.AssertTickThread();
            options ??= new ImpactOptions();
            var comps = BaseComponents(options);
            // kiln.spawn creates the entity at once (its task is already complete)
            var e = Spawn.Entity(template, position, options.YawDegrees, new SpawnOptions
            {
                Tag = options.Tag ?? "abilities",
                Flags = SpawnFlags.Transient | (options.Track ? SpawnFlags.None : SpawnFlags.NoTrack),
                Components = comps.Count > 0 ? comps : null,
            }).GetAwaiter().GetResult();
            if (options.Configs is { Count: > 0 } cfg) SetOverrides(e, ProgramOf(e), cfg, template.ToString());
            return Task.FromResult(e);
        }
        catch (Exception ex)
        {
            return Task.FromException<EntityId>(ex);
        }
    }

    /// <summary>Runs a program (a game's or one Kiln registered) on the game's generic impact entity at
    /// <paramref name="position"/>: the entity gets an Impact component for <paramref name="program"/> (its GUID).
    /// Tick thread.</summary>
    [TickThreadOnly]
    public static Task<EntityId> RunProgram(Guid program, WorldPosition position, ImpactOptions? options = null)
    {
        try
        {
            Game.AssertTickThread();
            options ??= new ImpactOptions();
            var comps = BaseComponents(options);
            var impact = new byte[24];
            program.ToByteArray().CopyTo(impact, 0);
            BitConverter.GetBytes(AbilitiesNative.Fnv(program.ToByteArray())).CopyTo(impact, 16);
            comps.Add(new SpawnComponent(SpawnComponent.HashOf("Impact"), impact));
            var e = Spawn.Entity(GenericImpactTemplate, position, options.YawDegrees, new SpawnOptions
            {
                Tag = options.Tag ?? "abilities",
                Flags = SpawnFlags.Transient | SpawnFlags.AllowUnknown | (options.Track ? SpawnFlags.None : SpawnFlags.NoTrack),
                Components = comps,
            }).GetAwaiter().GetResult();
            if (options.Configs is { Count: > 0 } cfg)
                SetOverrides(e, AbilitiesNative.Fnv(program.ToByteArray()), cfg, program.ToString());
            return Task.FromResult(e);
        }
        catch (Exception ex)
        {
            return Task.FromException<EntityId>(ex);
        }
    }

    private static List<SpawnComponent> BaseComponents(ImpactOptions o)
    {
        var comps = new List<SpawnComponent>();
        if (!o.Root.IsNone)
        {
            // ImpactHierarchy {parentImpactId, rootImpactId, weaponPideId, permissionCheckId}
            var h = new byte[16];
            BitConverter.GetBytes(o.Root.Value).CopyTo(h, 0);
            BitConverter.GetBytes(o.Root.Value).CopyTo(h, 4);
            BitConverter.GetBytes(o.Root.Value).CopyTo(h, 12);
            comps.Add(new SpawnComponent(SpawnComponent.HashOf("ImpactHierarchy"), h));
        }
        var faction = o.Faction;
        if (faction is null && !o.Root.IsNone && World.Available && World.TryRead<byte>(o.Root, "Faction", out var f))
            faction = f;
        if (faction is { } fv) comps.Add(new SpawnComponent(SpawnComponent.HashOf("Faction"), [fv]));
        if (o.Components is { } extra) comps.AddRange(extra);
        return comps;
    }

    /// <summary>The program id of an entity's Impact component (FNV of its program GUID), 0 if none.</summary>
    public static uint ProgramOf(EntityId entity)
    {
        if (!World.Available) return 0;
        if (!World.TryRead<ImpactComponentRaw>(entity, "Impact", out var c)) return 0;
        if (c.ProgramId != 0) return c.ProgramId;
        var g = new byte[16];
        for (var i = 0; i < 16; i++) g[i] = c.Program[i];
        return g.All(b => b == 0) ? 0 : AbilitiesNative.Fnv(g);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ImpactComponentRaw
    {
        public fixed byte Program[16];
        public uint ProgramId;
        public uint ImpactId;
    }

    private static void SetOverrides(EntityId e, uint programId, ImpactConfigs cfg, string what)
    {
        var vars = programId != 0 ? ReadVars(programId, isProgram: true) : [];
        var list = new List<AbilitiesNative.Override>();
        foreach (var (key, value) in cfg)
        {
            var v = FindVar(vars, key);
            if (v is null)
            {
                var id = ImpactConfigIds.Parse(key);
                if (id == 0 || value is not float and not double and not int)
                {
                    ModLog.Warn($"abilities: {what} (program 0x{programId:x8}) has no variable '{key}'; ignored");
                    continue;
                }
                // a config id the program does not name: bind as a 4-byte value by config id
                var raw = value is int i32 ? BitConverter.GetBytes(i32) : BitConverter.GetBytes(Convert.ToSingle(value));
                list.Add(MakeOverride(AbilitiesNative.OverrideByConfig, id, raw));
                continue;
            }
            var bytes = ImpactConfigs.Encode(value, v);
            if (bytes is null)
            {
                ModLog.Warn($"abilities: value {value} ({value.GetType().Name}) does not fit {v}; ignored");
                continue;
            }
            list.Add(MakeOverride(AbilitiesNative.OverrideByIndex, (uint)v.Index, bytes));
        }
        if (list.Count == 0) return;
        var t = AbilitiesNative.Require("Abilities.RunImpact");
        var arr = list.ToArray();
        int rc;
        fixed (AbilitiesNative.Override* p = arr) rc = t->EntityOverrides(e.Value, p, (uint)arr.Length, 50);
        if (rc != AbilitiesNative.Ok)
            throw new InvalidOperationException($"Abilities: config overrides refused ({AbilitiesNative.Describe(rc)})");
    }

    private static AbilitiesNative.Override MakeOverride(uint match, uint key, byte[] data)
    {
        var o = new AbilitiesNative.Override { Match = match, Key = key, Size = (uint)Math.Min(data.Length, 64) };
        for (var i = 0; i < o.Size; i++) o.Data[i] = data[i];
        return o;
    }

    // ---------------------------------------------------------------------------------------------- attributes ------

    /// <summary>Health, mana, stamina and the special-ability charge of an entity, through the game's attribute system
    /// (writes re-run its formulas; replicated to clients like the game's own). Tick thread.</summary>
    public static class Attributes
    {
        private static (uint Component, uint Current, uint Max) Ids(AbilityResource r) => r switch
        {
            AbilityResource.Health => (0xdc101091, 0x8eb84995, 0xdc8358be),
            AbilityResource.Mana => (0x4d7229a8, 0x6aa2574e, 0x17536777),
            AbilityResource.Stamina => (0x92410664, 0x04b6aa8b, 0xf443c410),
            _ => (0x9aec7fc4, 0x046f1fb4, 0x9573465b),
        };

        /// <summary>The current value, or null when the entity has no such attribute.</summary>
        [TickThreadOnly]
        public static int? Get(EntityId entity, AbilityResource resource)
        {
            Game.AssertTickThread();
            var t = AbilitiesNative.T;
            if (t == null) return null;
            var (c, a, _) = Ids(resource);
            int v;
            return t->AttrGet(entity.Value, c, a, &v) == AbilitiesNative.Ok ? v : null;
        }

        /// <summary>The maximum, or null when unknown.</summary>
        [TickThreadOnly]
        public static int? Max(EntityId entity, AbilityResource resource)
        {
            Game.AssertTickThread();
            var t = AbilitiesNative.T;
            if (t == null) return null;
            var (c, _, m) = Ids(resource);
            int v;
            return t->AttrGet(entity.Value, c, m, &v) == AbilitiesNative.Ok ? v : null;
        }

        /// <summary>Adds <paramref name="delta"/> (negative to spend) through the game's attribute code. False when the
        /// entity has no such attribute. A change posts <see cref="CombatChangedEvent"/> (<c>Attribute.Mana</c>, ...), as
        /// Kiln.Combat's attribute writes do.</summary>
        [TickThreadOnly]
        public static bool Add(EntityId entity, AbilityResource resource, int delta)
        {
            Game.AssertTickThread();
            var t = AbilitiesNative.T;
            if (t == null) return false;
            var (c, a, _) = Ids(resource);
            // the value before, read only while someone listens for the change
            int before = 0, after = 0;
            var watched = Events.HasSubscribers<CombatChangedEvent>() && t->AttrGet(entity.Value, c, a, &before) == AbilitiesNative.Ok;
            if (t->AttrAdd(entity.Value, c, a, delta) != AbilitiesNative.Ok) return false;
            if (watched && t->AttrGet(entity.Value, c, a, &after) == AbilitiesNative.Ok && after != before)
                Combat.Changed(entity, "Attribute." + resource, before, after);
            return true;
        }

        /// <summary>Any attribute by component and attribute id (e.g. "Mana", 0x6aa2574e).</summary>
        [TickThreadOnly]
        public static int? Get(EntityId entity, string component, uint attribute)
        {
            Game.AssertTickThread();
            var t = AbilitiesNative.T;
            if (t == null) return null;
            int v;
            return t->AttrGet(entity.Value, SpawnComponent.HashOf(component), attribute, &v) == AbilitiesNative.Ok ? v : null;
        }
    }

    // ---------------------------------------------------------------------------------------------- programs --------

    /// <summary>The game's ImpactRegistry: list, inspect and add programs (keen::ImpactProgram blobs, e.g. from
    /// tools/impact). Programs that mods ship as kiln.assets resources (assets.json, mode "new") are registered by
    /// Kiln at world start; see docs/kiln/services/abilities.md for the contract.</summary>
    public static class Programs
    {
        /// <summary>The number of programs in the game's ImpactRegistry; 0 when kiln.abilities is not loaded. Tick
        /// thread.</summary>
        public static int Count => AbilitiesNative.T is var t && t != null ? (int)t->ProgramCount() : 0;

        /// <summary>The id of a program: FNV-1a of its GUID's bytes.</summary>
        public static uint IdOf(Guid program) => AbilitiesNative.Fnv(program.ToByteArray());

        /// <summary>A program's description; null when kiln.abilities is not loaded or no program has this id. Tick
        /// thread.</summary>
        public static ImpactProgramInfo? Info(uint id)
        {
            var t = AbilitiesNative.T;
            if (t == null) return null;
            AbilitiesNative.ProgramInfo p = default;
            p.Size = (uint)sizeof(AbilitiesNative.ProgramInfo);
            if (t->ProgramInfoFn(id, &p) != AbilitiesNative.Ok) return null;
            var g = new byte[16];
            for (var i = 0; i < 16; i++) g[i] = p.Guid[i];
            return new ImpactProgramInfo(p.Id, new Guid(g), (int)p.CodeWords, (int)p.ShutdownWords, (int)p.Variables,
                (int)p.DataBytes, p.StackSize, p.Streams, p.Kiln != 0);
        }

        /// <summary>The variables of a program; empty when there are none or no program has this id. Tick
        /// thread.</summary>
        public static IReadOnlyList<ImpactVariable> Variables(uint id) => ReadVars(id, isProgram: true);

        /// <summary>Verifies and inserts a program blob (copied). Returns its id. Throws <see cref="ArgumentException"/>
        /// with the verifier's reason when refused. A program that calls Kiln natives nobody registered or declared
        /// yet waits and is inserted once they are (returns its id, <paramref name="waiting"/> true). Tick thread.
        /// Advanced mods only (<c>"advanced": true</c> in kiln.mod.json): an ordinary mod gets
        /// <see cref="InvalidOperationException"/>. Programs shipped as assets (<see cref="Refresh"/>) need no flag.</summary>
        /// <remarks>Requires the tick thread and its calling mod context (OnLoad or a Kiln callback). An advanced mod
        /// on an unattributed background thread is also refused; post the registration through Context.Scheduler.</remarks>
        [AdvancedApi]
        [TickThreadOnly]
        [RequiresCapability(AbilitiesCaps.Service)]
        public static uint Register(byte[] blob, out bool waiting)
        {
            AbilitiesGate.RequireAdvanced("Abilities.Programs.Register");
            return RegisterCore(blob, out waiting);
        }

        /// <summary>Like <see cref="Register(byte[], out bool)"/>, without the waiting flag. Advanced mods only.</summary>
        /// <remarks>Requires the tick thread and its calling mod context, as the other overload does. Post work from
        /// an unattributed background thread through Context.Scheduler, even for an advanced mod.</remarks>
        [AdvancedApi]
        [TickThreadOnly]
        [RequiresCapability(AbilitiesCaps.Service)]
        public static uint Register(byte[] blob)
        {
            AbilitiesGate.RequireAdvanced("Abilities.Programs.Register");
            return RegisterCore(blob, out _);
        }

        // Kiln's own programs (the buff program of ApplyBuff, run for whichever mod uses buffs) come here past the gate.
        internal static uint RegisterCore(byte[] blob, out bool waiting)
        {
            ArgumentNullException.ThrowIfNull(blob);
            Game.AssertTickThread();
            var t = AbilitiesNative.Require("Abilities.Programs.Register");
            var err = new byte[256];
            uint id;
            int rc;
            fixed (byte* b = blob)
            fixed (byte* e = err)
                rc = t->ProgramRegister(ModContext.CurrentMod?.Owner ?? 0, b, (nuint)blob.Length, &id, e, (nuint)err.Length);
            string msg;
            fixed (byte* e = err) msg = AbilitiesNative.Text(e, err.Length);
            if (rc != AbilitiesNative.Ok) throw new ArgumentException($"program refused: {msg} ({AbilitiesNative.Describe(rc)})");
            waiting = msg.Length > 0;
            return id;
        }

        /// <summary>Lists kiln.assets' ImpactProgram resources again and registers the new ones. Tick thread.</summary>
        [TickThreadOnly]
        [RequiresCapability(AbilitiesCaps.Service)]
        public static int Refresh()
        {
            Game.AssertTickThread();
            var t = AbilitiesNative.Require("Abilities.Programs.Refresh");
            uint added;
            return t->ProgramRefresh(&added) == AbilitiesNative.Ok ? (int)added : 0;
        }
    }

    // ---------------------------------------------------------------------------------------------- sequences -------

    /// <summary>The actor's current action sequence: (sequence id = FNV-1a of the ActorSequenceResource GUID, trigger
    /// time in ECS nanoseconds, call index), or null when it has no Actor. Sequence id 0 = none. Tick thread.</summary>
    [TickThreadOnly]
    public static (uint Sequence, long TriggerTime, int CallIndex)? SequenceOf(EntityId actor)
    {
        Game.AssertTickThread();
        var t = AbilitiesNative.T;
        if (t == null) return null;
        uint seq, call;
        long time;
        return t->SequenceState(actor.Value, &seq, &time, &call) == AbilitiesNative.Ok ? (seq, time, (int)call) : null;
    }

    /// <summary>Starts an existing action sequence (its animation state, VFX/SFX notifies, timed SpawnImpacts, costs and
    /// ability locks) on a player, NPC or creature, the way the game's own programs do (emotes, death, fall damage and
    /// player actions use the same helper: ActorInput.triggerContext). The actor takes it in its next step and every
    /// client sees it like any game-started sequence (NetworkActor). <paramref name="abortCurrent"/>: abort what it is
    /// doing (ExternalAbortAndOverride), else ExternalTrigger (what the game's emotes use). Input-gated sequences
    /// (staff casts, bows: KeepAlive/Loop events) play to the end: Kiln holds their keep-alive states
    /// (<see cref="SequenceOptions.HoldInputs"/>). False when the entity has no ActorInput or the GUID is not an
    /// ActorSequenceResource the game has, and, with an error in the log, when the sequence has an event that consumes the used item
    /// (every potion and food): there is no used item here, and the game would read one that is not there and take the server down. A copy
    /// of the sequence with <c>consume</c> off plays. Tick thread.</summary>
    [TickThreadOnly]
    [RequiresCapability(AbilitiesCaps.Service)]
    [RequiresCapability(AbilitiesCaps.Sequences)]
    public static bool PlaySequence(EntityId actor, Guid sequence, bool abortCurrent = true) =>
        PlaySequence(actor, sequence, abortCurrent ? SequenceOptions.Default : SequenceOptions.Queue);

    /// <summary>Starts an existing action sequence with explicit options (see the other overload). Tick thread.</summary>
    [TickThreadOnly]
    [RequiresCapability(AbilitiesCaps.Service)]
    [RequiresCapability(AbilitiesCaps.Sequences)]
    public static bool PlaySequence(EntityId actor, Guid sequence, SequenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Game.AssertTickThread();
        var t = AbilitiesNative.Require("Abilities.PlaySequence");
        if (t->Header.Size < (uint)sizeof(AbilitiesNative.Table))
            throw new KilnUnavailableException(AbilitiesCaps.Service, "PlaySequence needs a newer kiln.abilities");
        if (SequenceGuard.ConsumingEvent(sequence) is { } consuming)
        {
            ModLog.Error($"PlaySequence: sequence {sequence} not played: its event ({consuming}) consumes the used item, and PlaySequence has none: the game would read an item that is not there and take the server down. Turn the event's consume off on a copy of the sequence");
            return false;
        }
        var trigger = options.AbortCurrent ? AbilitiesNative.TriggerAbortOverride : AbilitiesNative.TriggerExternal;
        var rc = t->SequencePlay(actor.Value, IdOf(sequence), trigger | (options.HoldInputs ? AbilitiesNative.SequenceHold : 0));
        if (rc == AbilitiesNative.InvalidArgument && options.HoldInputs)  // an older kiln.abilities: no hold flag
            rc = t->SequencePlay(actor.Value, IdOf(sequence), trigger);
        if (rc == AbilitiesNative.Unavailable)
        {
            AbilitiesCaps.Ensure();
            throw ServiceCaps.Unavailable(AbilitiesCaps.Sequences, "PlaySequence is unavailable on this build");
        }
        return rc == AbilitiesNative.Ok;
    }

    /// <summary>FNV-1a of a GUID's bytes (program, sequence, skill ids).</summary>
    [AnyThread]
    public static uint IdOf(Guid guid) => AbilitiesNative.Fnv(guid.ToByteArray());

    /// <summary>FNV-1a of a name (native hashes: "ApplyDamage" 0xb04d0e7a, "mymod.Chill").</summary>
    [AnyThread]
    public static uint HashOf(string name) => AbilitiesNative.Fnv(name);
}

/// <summary>Logs for the service itself (no mod).</summary>
internal static class ModLog
{
    public static void Info(string s) => Backend.Current.Log(0, LogLevel.Info, s);
    public static void Warn(string s) => Backend.Current.Log(0, LogLevel.Warn, s);
    public static void Error(string s) => Backend.Current.Log(0, LogLevel.Error, s);
}
