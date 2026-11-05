// SPDX-License-Identifier: MIT
// Managed API over kiln.creatures (native/core/abi/services/kiln_creatures.h). Usage: docs/kiln/services/creatures.md.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>
/// What one creature or NPC is and does at runtime, server-authoritative: the creature catalog, per-creature values
/// (level, faction, scale, resistances, speed, damage, XP, loot, AI feature switches, home) and private AI settings,
/// variants (a base template plus overrides) spawned or applied to natural spawns, AI state/target events and commands.
/// </summary>
/// <remarks>
/// <para>Changes a mod makes are layers of that mod: when it unloads (or calls <see cref="CreatureRef.Reset"/>) the
/// creature goes back to the value it had before (or to another mod's layer). A creature that despawns takes its
/// layers with it. Everything that touches the world runs on the tick thread (handlers, <c>OnLoad</c>, continuations
/// after <c>await</c>); other threads get <see cref="InvalidOperationException"/>. The catalog reads from any thread.</para>
/// <para>Vanilla clients see the results through the game's own replication (health, level, faction, transforms, AI
/// state, attacks). The server marks the components Kiln writes as changed so they replicate like game writes.</para>
/// </remarks>
public static unsafe partial class Creatures
{
    static Creatures() => EnsureListener();

    /// <summary>True when kiln.creatures is loaded on this build.</summary>
    public static bool Available => CreaturesNative.T != null;

    /// <summary>What works on this build (null when unavailable). Any thread.</summary>
    public static CreaturesStatus? Status
    {
        get
        {
            var t = CreaturesNative.T;
            if (t == null) return null;
            var s = new CreaturesNative.Status { Size = (uint)sizeof(CreaturesNative.Status) };
            if (t->GetStatus(&s) != CreaturesNative.Ok) return null;
            return new CreaturesStatus(s.Slots != 0, s.Ai != 0, s.Ready != 0, s.Arsenals != 0, s.Reflection != 0,
                s.Commands != 0, s.Marks != 0, s.LabReady != 0, (int)s.Catalog, (int)s.CatalogLoaded,
                (int)s.EngineCreatures, (int)s.ArsenalCount, (int)s.Entities, (int)s.Layers, (int)s.PrivateCopies,
                (int)s.PendingFree, s.StateChanges, s.EventsDelivered, s.EventsDropped, s.CommandsSent, s.MarksWritten,
                s.CopyReads);
        }
    }

    // ------------------------------------------------------------------------------------------------ catalog ----

    private static IReadOnlyList<CreatureTemplate>? _templates;
    private static Dictionary<Guid, CreatureTemplate>? _byId;

    /// <summary>Every creature/NPC template Kiln knows (enemy AI, wildlife, animals, villagers), sorted by name. Any
    /// thread. <see cref="CreatureTemplate.IsLoaded"/> is filled in once the world runs.</summary>
    public static IReadOnlyList<CreatureTemplate> Templates
    {
        get
        {
            var list = _templates;
            var s = Status;
            // reload once the LOADED bits are known (the engine check runs at the first steps)
            if (list != null && (s is null || (list.Count == s.Catalog && list.Count(t => t.IsLoaded && !t.IsRuntime) == s.CatalogLoaded))) return list;
            var t = CreaturesNative.T;
            if (t == null) return [];
            var n = t->TemplateCount();
            var res = new List<CreatureTemplate>((int)n);
            for (uint i = 0; i < n; i++)
            {
                var nt = new CreaturesNative.Template { Size = (uint)sizeof(CreaturesNative.Template) };
                if (t->TemplateAt(i, &nt) == CreaturesNative.Ok) res.Add(Convert(in nt));
            }
            _byId = res.ToDictionary(x => x.Id);
            return _templates = res;
        }
    }

    /// <summary>A catalog template by name ("Enemy_Vukah_Melee"), display name ("Vukah") or GUID text; null if none.</summary>
    public static CreatureTemplate? Template(string nameOrGuid)
    {
        if (Guid.TryParse(nameOrGuid, out var g)) return Template(g);
        var t = CreaturesNative.T;
        if (t == null || string.IsNullOrEmpty(nameOrGuid)) return null;
        var s = new CreaturesNative.Utf8(nameOrGuid);
        var nt = new CreaturesNative.Template { Size = (uint)sizeof(CreaturesNative.Template) };
        fixed (byte* p = s.Bytes)
            if (t->TemplateFind(s.Str(p), &nt) != CreaturesNative.Ok) return null;
        return Template(nt.Guid.ToGuid()) ?? Convert(in nt);
    }

    /// <summary>A catalog template by GUID; null if it is not a creature/NPC template.</summary>
    public static CreatureTemplate? Template(Guid id)
    {
        _ = Templates;
        return _byId != null && _byId.TryGetValue(id, out var t) ? t : null;
    }

    private static CreatureTemplate Convert(in CreaturesNative.Template t)
    {
        fixed (byte* n = t.Name)
        fixed (byte* d = t.Display)
            return new CreatureTemplate(t.Guid.ToGuid(), CreaturesNative.Text(n, 128), CreaturesNative.Text(d, 64),
                (CreatureKind)t.Kind, (CreatureFlags)t.Flags, (Faction)t.Faction, (int)t.Level, (int)t.LevelMax,
                t.EnemyClass == 255 ? -1 : t.EnemyClass, t.Arsenal.ToGuid(), t.LootLabels.ToGuid(), t.LootSlotsMin,
                t.LootSlotsMax, t.XpMod, t.Scale, t.Model.ToGuid(), t.AnimationGraph.ToGuid());
    }

    // ------------------------------------------------------------------------------------------------ entities ----

    /// <summary>A handle on one creature (any entity id; check <see cref="CreatureRef.Exists"/>).</summary>
    public static CreatureRef Of(EntityId entity) => new(entity, OwnerKey.Caller);

    /// <summary>Creatures (enemy AI, villagers and animals of the catalog) within <paramref name="radius"/> metres of
    /// <paramref name="center"/>, nearest first. Tick thread.</summary>
    public static IReadOnlyList<EntityId> Near(WorldPosition center, double radius, CreatureKind? kind = null)
    {
        Services.RequireTickThread("Creatures.Near");
        var found = new List<(EntityId e, double d)>();
        foreach (var e in World.Find("Faction"))
        {
            if (e.IsPlayer || !World.TryGetPosition(e, out var p)) continue;
            var d = p.DistanceTo(center);
            if (d > radius) continue;
            var info = Kiln.Spawn.Inspect(e);
            if (info?.Template is not { } g || Template(g) is not { } t || t.Flags.HasFlag(CreatureFlags.Weakspot)) continue;
            if (kind is { } k && t.Kind != k) continue;
            found.Add((e, d));
        }
        return found.OrderBy(x => x.d).Select(x => x.e).ToList();
    }

    /// <summary>The JSON field list of a template component ("Enemy01BehaviorState", "Aggro", "EnemyScaling", ...)
    /// from the game's reflection: what <see cref="CreatureRef.Ai"/> accepts. Any thread.</summary>
    public static string AiFields(string component = "Enemy01BehaviorState")
    {
        var t = CreaturesNative.Require("Creatures.AiFields");
        var s = new CreaturesNative.Utf8(component);
        fixed (byte* p = s.Bytes)
        {
            var name = s.Str(p);
            var text = NativeText.Read((b, cap, len) => t->AiFields(name, b, cap, len), out var rc);
            if (rc != CreaturesNative.Ok) throw new ArgumentException($"{component}: {CreaturesNative.Describe(rc)}");
            return text;
        }
    }

    /// <summary>Rebuilds private AI copies from the shared template data (all entities, or one) and re-applies the
    /// overrides; Kiln.Creatures does this itself after a content (template data) change. Tick thread.</summary>
    public static int RefreshCopies(EntityId entity = default)
    {
        var t = Tick("Creatures.RefreshCopies");
        uint n = 0;
        t->AiRefresh(entity.Value, &n);
        return (int)n;
    }

    // ------------------------------------------------------------------------------------------------ owners ----

    /// <summary>Who a layer belongs to: a mod (its native owner) or a named owner such as a data pack.</summary>
    internal readonly record struct OwnerKey(ulong Native, ModContext? Mod, string Name)
    {
        public static OwnerKey Caller
        {
            get
            {
                var mod = ModContext.CurrentMod;
                if (mod is null) return default;
                Watch(mod);
                return new OwnerKey(mod.Owner, mod, mod.Info.Id);
            }
        }

        public bool IsNone => Native == 0;

        /// <summary>A synthetic owner for a name (packs: "pack:&lt;mod&gt;"); high bit set so it never meets a core owner id.</summary>
        public static OwnerKey Named(string name)
        {
            ulong h = 14695981039346656037ul;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(name)) h = (h ^ b) * 1099511628211ul;
            return new OwnerKey(h | (1ul << 63), null, name);
        }
    }

    internal static OwnerKey RequireOwner(OwnerKey o, string api) =>
        o.IsNone ? throw new InvalidOperationException($"{api}: call it from mod code (changes belong to a mod)") : o;

    private static readonly HashSet<ModContext> WatchedMods = [];

    private static void Watch(ModContext mod)
    {
        lock (WatchedMods)
            if (!WatchedMods.Add(mod))
                return;
        mod.Retiring += () => Retire(mod);
    }

    // The mod unloads: its layers and private copies, variants, spawn rules and DespawnOnUnload spawns go.
    private static void Retire(ModContext mod)
    {
        lock (WatchedMods) WatchedMods.Remove(mod);
        CreatureVariants.Retire(mod);
        try { Guarding.Retire(mod); } catch (Exception) { }
        try { Arsenals.Retire(mod); } catch (Exception) { }
        var t = CreaturesNative.T;
        if (t != null) t->ReleaseOwner(mod.Owner);
    }

    internal static CreaturesNative.Table* Tick(string api)
    {
        var t = CreaturesNative.Require(api);
        Services.RequireTickThread(api);
        return t;
    }

    internal static void Check(int rc, string what)
    {
        if (rc == CreaturesNative.Ok) return;
        if (rc == CreaturesNative.WrongThread) throw new InvalidOperationException($"{what}: {CreaturesNative.Describe(rc)}");
        if (rc == CreaturesNative.InvalidArgument) throw new ArgumentException($"{what}: {CreaturesNative.Describe(rc)}");
        if (rc == CreaturesNative.NotFound) throw new KeyNotFoundException($"{what}: {CreaturesNative.Describe(rc)}");
        throw new NotSupportedException($"{what}: {CreaturesNative.Describe(rc)}");
    }

    // ------------------------------------------------------------------------------------------------ events ----

    private static int _listening;

    [ModuleInitializer]
    internal static void ModuleInit() => EnsureListener();

    internal static void EnsureListener()
    {
        if (Volatile.Read(ref _listening) != 0) return;
        var t = CreaturesNative.T;
        if (t == null) return;
        if (Interlocked.Exchange(ref _listening, 1) != 0) return;
        t->SetListener(&OnEvents, null);
        ContentLink.Register();
    }

    private static uint Interest()
    {
        uint m = 0;
        if (Events.HasSubscribers<CreatureStateEvent>()) m |= CreaturesNative.Mask(CreaturesNative.EvState);
        if (Events.HasSubscribers<CreatureTargetEvent>()) m |= CreaturesNative.Mask(CreaturesNative.EvTarget);
        return m;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnEvents(CreaturesNative.Event* evs, uint count, void* user)
    {
        try
        {
            for (uint i = 0; i < count; i++)
            {
                ref var e = ref evs[i];
                switch (e.Kind)
                {
                    case CreaturesNative.EvState when Events.HasSubscribers<CreatureStateEvent>():
                        Events.Post(new CreatureStateEvent(new EntityId(e.Entity), (EnemyState)e.From, (EnemyState)e.To,
                            (StateCondition)e.Condition, e.Step));
                        break;
                    case CreaturesNative.EvTarget when Events.HasSubscribers<CreatureTargetEvent>():
                        Events.Post(new CreatureTargetEvent(new EntityId(e.Entity), new EntityId(e.Target),
                            new EntityId(e.Previous), (EnemyState)e.To, e.Step));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Backend.Current.Log(0, LogLevel.Error, "Kiln.Creatures: event delivery failed: " + ex.Message);
        }
        try
        {
            CreatureVariants.ResolvePending();
            CreatureVariants.UpdateGatedRules();
        }
        catch (Exception ex) { Backend.Current.Log(0, LogLevel.Warn, "Kiln.Creatures: spawn rules: " + ex.Message); }
        try { Villagers.Pump(); }
        catch (Exception ex) { Backend.Current.Log(0, LogLevel.Warn, "Kiln.Creatures: villagers: " + ex.Message); }
        try { Guarding.Pump(); }
        catch (Exception ex) { Backend.Current.Log(0, LogLevel.Warn, "Kiln.Creatures: guard orders: " + ex.Message); }
        try { RulePump.Pump(); }
        catch (Exception ex) { Backend.Current.Log(0, LogLevel.Warn, "Kiln.Creatures: rules: " + ex.Message); }
        return Interest();
    }
}
