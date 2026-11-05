// SPDX-License-Identifier: MIT
// Variants: a base template plus per-creature overrides, spawned by code or applied to natural spawns (spawn rules).
// A variant's creatures keep the base template's GUID, so vanilla clients see them (with the base's name and look).
using System.Globalization;
using System.Numerics;

namespace Kiln;

/// <summary>A creature variant: a base template (a vanilla template or a kiln.content clone) and the overrides Kiln
/// applies to each of its creatures. Unset (null / empty) members keep the base template's values.</summary>
public sealed class CreatureVariant
{
    /// <summary>Unique id ("elite_vukah"); case-insensitive.</summary>
    public required string Id { get; init; }

    /// <summary>The base template: name, display name or GUID text.</summary>
    public required string Base { get; init; }

    /// <summary>A display name for logs and Kiln clients (vanilla clients show the base template's own name).</summary>
    public string? Name { get; init; }

    public int? Level { get; init; }
    public Faction? Faction { get; init; }
    public float? Scale { get; init; }
    public float? XpFactor { get; init; }
    public float? SpeedFactor { get; init; }
    public float? DamageFactor { get; init; }
    public Dictionary<DamageKind, float> Resistances { get; init; } = [];
    public Guid? LootLabels { get; init; }
    public (int Min, int Max)? LootSlots { get; init; }
    public bool? LootDisabled { get; init; }
    public EnemyFeatures EnableFeatures { get; init; }
    public EnemyFeatures DisableFeatures { get; init; }
    /// <summary>Private AI settings: reflection path (Enemy01BehaviorState by default, or "Component.path") to value
    /// text, e.g. ["aggroSetup.viewRange"] = "30", ["EnemyArsenalReference.arsenal"] = "&lt;guid&gt;".</summary>
    public Dictionary<string, string> Ai { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>For templates the game saves (villagers, tamed animals): remember the variant with the entity
    /// (Kiln.Saves EntityData of the registering mod) and apply it again after a restart. Enemies are DoNotSave.</summary>
    public bool Persist { get; init; }

    public override string ToString() => Name ?? Id;
}

/// <summary>Options for <see cref="Creatures.Spawn"/>.</summary>
public sealed class CreatureSpawnOptions
{
    public string? Tag { get; init; }
    public SpawnFlags Flags { get; init; }
    /// <summary>Destroy the creature when the spawning mod unloads (default true; false leaves it, with the
    /// variant's changes reverted).</summary>
    public bool DespawnOnUnload { get; init; } = true;
}

/// <summary>A natural-spawn rule: creatures of <see cref="Template"/> that random spawners create become
/// <see cref="Variant"/> with <see cref="Chance"/>.</summary>
public sealed record CreatureSpawnRule(string Template, string Variant, double Chance = 1, bool NightOnly = false,
    bool DayOnly = false);

/// <summary>The variant registry (per owner: a mod, or a data pack's "creatures" section).</summary>
public static class CreatureVariants
{
    internal sealed class Entry
    {
        public required CreatureVariant Variant;
        public required Creatures.OwnerKey Owner;
        public string Source = "";
        public Guid BaseId;
        public string BaseName = "";
    }

    internal sealed class Rule
    {
        public required CreatureSpawnRule Spec;
        public required Creatures.OwnerKey Owner;
        public string Source = "";
        public Guid TemplateId;
        public Guid BaseId;                 // the variant's base template
        public IDisposable? Replacement;    // kiln.gameplay swaps the spawn's template (base != template)
        public bool? ReplacementOn;         // day/night-gated replacement: the state applied now
    }

    private static readonly Dictionary<string, Entry> ById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Rule> Rules = [];
    private static readonly Dictionary<EntityId, string> Applied = [];  // entity -> variant id (this session)
    private static readonly Dictionary<ModContext, List<EntityId>> Despawn = [];
    private static readonly HashSet<ModContext> Persisting = [];
    private static readonly Random Rng = new();
    private static EventSubscription? _natural;
    private const string PersistKey = "kiln.creatures.variant";

    /// <summary>Registered variants.</summary>
    public static IReadOnlyList<CreatureVariant> All => ById.Values.Select(e => e.Variant).ToList();

    /// <summary>Natural-spawn rules.</summary>
    public static IReadOnlyList<CreatureSpawnRule> SpawnRules => Rules.Select(r => r.Spec).ToList();

    public static CreatureVariant? Get(string id) => ById.TryGetValue(id, out var e) ? e.Variant : null;

    internal static Entry? EntryOf(string id) => ById.TryGetValue(id, out var e) ? e : null;

    /// <summary>The variant applied to an entity in this session, or null.</summary>
    public static CreatureVariant? Of(EntityId entity) =>
        Applied.TryGetValue(entity, out var id) && ById.TryGetValue(id, out var e) ? e.Variant : null;

    /// <summary>Registers a variant for the calling mod (removed when it unloads, or when the result is disposed).
    /// Tick thread or OnLoad.</summary>
    public static IDisposable Register(CreatureVariant variant)
    {
        var owner = Creatures.RequireOwner(Creatures.OwnerKey.Caller, "CreatureVariants.Register");
        Add(variant, owner, "code");
        return new Registration(() => Remove(variant.Id, owner));
    }

    /// <summary>Applies a registered variant to an existing creature (layers of the variant's owner).</summary>
    public static IReadOnlyList<string> Apply(EntityId entity, string variantId)
    {
        if (!ById.TryGetValue(variantId, out var e)) throw new KeyNotFoundException($"no variant '{variantId}'");
        return ApplyTo(entity, e, natural: false);
    }

    /// <summary>Adds a natural-spawn rule for the calling mod (removed when it unloads, or when disposed).</summary>
    public static IDisposable AddSpawnRule(CreatureSpawnRule rule)
    {
        var owner = Creatures.RequireOwner(Creatures.OwnerKey.Caller, "CreatureVariants.AddSpawnRule");
        var r = AddRule(rule, owner, "code");
        return new Registration(() =>
        {
            DropRule(r);
            Rules.Remove(r);
        });
    }

    // ---------------------------------------------------------------------------------------------- internals ----

    internal static Entry Add(CreatureVariant v, Creatures.OwnerKey owner, string source)
    {
        if (string.IsNullOrWhiteSpace(v.Id)) throw new ArgumentException("a variant needs an id");
        if (string.IsNullOrWhiteSpace(v.Base)) throw new ArgumentException($"variant {v.Id} needs a base template");
        if (ById.TryGetValue(v.Id, out var old) && old.Owner.Native != owner.Native)
            throw new ArgumentException($"variant id '{v.Id}' belongs to {old.Owner.Name}");
        var e = new Entry { Variant = v, Owner = owner, Source = source };
        if (!TryResolveBase(e))
        {
            // a kiln.content clone that is not in kiln.spawn's list yet, or a typo: keep it and resolve on first use
            if (!IsKnownLater(v.Base)) throw new ArgumentException($"variant {v.Id}: unknown base template '{v.Base}'");
            Backend.Current.Log(0, LogLevel.Info, $"Kiln.Creatures: variant {v.Id}: base '{v.Base}' is not loaded yet; resolved when it appears");
        }
        ById[v.Id] = e;
        if (v.Persist && owner.Mod is { } mod) StartPersistPump(mod);
        return e;
    }

    // Base template: kiln.spawn's list (name, display name, GUID), else kiln.content's template data (clones are
    // registered there first). Sets BaseId/BaseName; false while it is not known yet.
    internal static bool TryResolveBase(Entry e)
    {
        if (e.BaseId != Guid.Empty) return true;
        if (Resolve(e.Variant.Base) is not { } t) return false;
        e.BaseId = t.Id;
        e.BaseName = t.Name;
        return true;
    }

    private static (Guid Id, string Name)? Resolve(string template)
    {
        if (Spawn.Lookup(template) is { } t) return (t.Id, t.Name);
        try
        {
            var g = Content.Templates.Get(template);
            if (g is not null && g.Guid != Guid.Empty) return (g.Guid, g.Name);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // kiln.content's template data is unavailable: only kiln.spawn's list counts
        }
        return null;
    }

    // Names that may still appear: kiln.content clones register while packs apply, possibly after this section.
    private static bool IsKnownLater(string template)
    {
        try { return Content.Templates.Get(template) is not null; }
        catch { return false; }
    }

    internal static void Remove(string id, Creatures.OwnerKey owner)
    {
        if (ById.TryGetValue(id, out var e) && e.Owner.Native == owner.Native) ById.Remove(id);
    }

    internal static Rule AddRule(CreatureSpawnRule spec, Creatures.OwnerKey owner, string source)
    {
        if (!ById.ContainsKey(spec.Variant)) throw new ArgumentException($"spawn rule: unknown variant '{spec.Variant}'");
        if (spec.Chance is < 0 or > 1) throw new ArgumentException("spawn rule: chance must be 0..1");
        if (Resolve(spec.Template) is null && !IsKnownLater(spec.Template))
            throw new ArgumentException($"spawn rule: unknown template '{spec.Template}'");
        var r = new Rule { Spec = spec, Owner = owner, Source = source };
        Rules.Add(r);
        EnsureNaturalHook();
        if (!Activate(r))
            Backend.Current.Log(0, LogLevel.Info, $"Kiln.Creatures: rule {spec.Template} -> {spec.Variant} waits for its templates to load");
        return r;
    }

    // A rule becomes active once both its template and its variant's base are known (clones can register later).
    private static bool Activate(Rule r)
    {
        if (r.TemplateId != Guid.Empty && r.BaseId != Guid.Empty) return true;
        if (Resolve(r.Spec.Template) is not { } t || !ById.TryGetValue(r.Spec.Variant, out var e) || !TryResolveBase(e)) return false;
        r.TemplateId = t.Id;
        r.BaseId = e.BaseId;
        if (r.BaseId != r.TemplateId && !r.Spec.NightOnly && !r.Spec.DayOnly) r.Replacement = Replace(r, r.Spec.Chance);
        if (r.BaseId != r.TemplateId && (r.Spec.NightOnly || r.Spec.DayOnly)) UpdateGatedRules();
        return true;
    }

    /// <summary>Polled (~every 0.4 s): rules and variants whose templates were not loaded yet.</summary>
    internal static void ResolvePending()
    {
        foreach (var r in Rules)
            if (r.TemplateId == Guid.Empty || r.BaseId == Guid.Empty)
                if (Activate(r)) Backend.Current.Log(0, LogLevel.Info, $"Kiln.Creatures: rule {r.Spec.Template} -> {r.Spec.Variant} is active now");
    }

    // A rule whose variant has another base template: kiln.gameplay swaps the spawner's template in its own spawn call.
    private static IDisposable? Replace(Rule r, double chance)
    {
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = r.Owner.Mod;  // the rule's mod owns the native rule (removed with it)
        try { return Gameplay.ReplaceNaturalSpawns(r.TemplateId, r.BaseId, chance); }
        catch (NotSupportedException e)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"Kiln.Creatures: rule {r.Spec.Template} -> {r.Spec.Variant}: {e.Message}; skipped");
            return null;
        }
        finally { ModContext.CurrentMod = outer; }
    }

    private static void DropRule(Rule r)
    {
        r.Replacement?.Dispose();
        r.Replacement = null;
    }

    // Day/night-gated replacement rules are switched in the native table when day turns to night (polled ~every 0.4 s).
    internal static void UpdateGatedRules()
    {
        if (Rules.Count == 0) return;
        bool? night = Time.Available && Time.Ready ? !Time.IsDay : null;
        foreach (var r in Rules)
        {
            if (r.TemplateId == Guid.Empty || r.BaseId == r.TemplateId || !(r.Spec.NightOnly || r.Spec.DayOnly)) continue;
            var on = night is { } n && (r.Spec.NightOnly ? n : !n);
            if (r.ReplacementOn == on) continue;
            r.ReplacementOn = on;
            DropRule(r);
            if (on) r.Replacement = Replace(r, r.Spec.Chance);
        }
    }

    /// <summary>Removes what an owner (and optionally one source) added. Returns variants + rules removed.</summary>
    internal static int RemoveOwned(Creatures.OwnerKey owner, string? source)
    {
        var ids = ById.Where(kv => kv.Value.Owner.Native == owner.Native && (source is null || kv.Value.Source == source))
            .Select(kv => kv.Key).ToList();
        foreach (var id in ids) ById.Remove(id);
        var gone = Rules.Where(r => r.Owner.Native == owner.Native && (source is null || r.Source == source)).ToList();
        foreach (var r in gone)
        {
            DropRule(r);
            Rules.Remove(r);
        }
        return ids.Count + gone.Count;
    }

    internal static void Retire(ModContext mod)
    {
        var key = new Creatures.OwnerKey(mod.Owner, mod, mod.Info.Id);
        RemoveOwned(key, null);
        Persisting.Remove(mod);
        if (Despawn.Remove(mod, out var list))
            foreach (var e in list)
                try { Spawn.Destroy(e); } catch { /* not on the tick thread or already gone */ }
    }

    internal static void TrackDespawn(ModContext mod, EntityId e)
    {
        if (!Despawn.TryGetValue(mod, out var l)) Despawn[mod] = l = [];
        l.Add(e);
    }

    internal static IReadOnlyList<string> ApplyTo(EntityId entity, Entry e, bool natural)
    {
        var v = e.Variant;
        var c = new CreatureRef(entity, e.Owner);
        var errors = new List<string>();
        void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or KeyNotFoundException)
            {
                errors.Add($"{what}: {ex.Message}");
            }
        }
        if (v.Level is { } lv) Try("level", () => c.Level = lv);
        if (v.Faction is { } f) Try("faction", () => c.Faction = f);
        if (v.Scale is { } s) Try("scale", () => c.Scale = s);
        if (v.XpFactor is { } x) Try("xp", () => c.XpFactor = x);
        if (v.SpeedFactor is { } sp) Try("speed", () => c.SpeedFactor = sp);
        if (v.DamageFactor is { } d) Try("damage", () => c.DamageFactor = d);
        foreach (var (k, val) in v.Resistances) Try($"resistance {k}", () => c.Resistance[k] = val);
        if (v.LootLabels is { } ll) Try("loot labels", () => c.Loot.Labels = ll);
        if (v.LootSlots is { } ls) Try("loot slots", () => c.Loot.Slots = ls);
        if (v.LootDisabled is { } ld) Try("loot disabled", () => c.Loot.Disabled = ld);
        if (v.EnableFeatures != 0) Try("features on", () => c.Features.Enable(v.EnableFeatures));
        if (v.DisableFeatures != 0) Try("features off", () => c.Features.Disable(v.DisableFeatures));
        foreach (var (path, val) in v.Ai) Try($"ai {path}", () => c.Ai.Set(path, val));
        Applied[entity] = v.Id;
        if (v.Persist && e.Owner.Mod is { } mod) Persist(mod, entity, v.Id);
        if (errors.Count > 0)
            Backend.Current.Log(0, LogLevel.Warn, $"Kiln.Creatures: variant {v.Id} on {entity}: " + string.Join("; ", errors));
        if (Events.HasSubscribers<CreatureVariantAppliedEvent>()) Events.Post(new CreatureVariantAppliedEvent(entity, v, natural));
        return errors;
    }

    internal static async Task<EntityId> SpawnVariant(Entry e, WorldPosition pos, float yaw, CreatureSpawnOptions o)
    {
        var mod = ModContext.CurrentMod;
        if (!TryResolveBase(e)) throw new SpawnException($"variant {e.Variant.Id}: base template '{e.Variant.Base}' is not loaded", 2);
        // by name: kiln.spawn finds kiln.content clones by name as soon as they are registered
        var id = await Spawn.Entity(e.BaseName, pos, yaw, new SpawnOptions { Tag = o.Tag, Flags = o.Flags });
        ApplyTo(id, e, natural: false);
        if (o.DespawnOnUnload && mod != null) TrackDespawn(mod, id);
        return id;
    }

    internal static async Task<EntityId> SpawnPlain(string template, WorldPosition position, float yaw, CreatureSpawnOptions o)
    {
        var mod = ModContext.CurrentMod;
        var id = await Spawn.Entity(template, position, yaw, new SpawnOptions { Tag = o.Tag, Flags = o.Flags });
        if (o.DespawnOnUnload && mod != null) TrackDespawn(mod, id);
        return id;
    }

    // ---- natural spawns ----

    private static void EnsureNaturalHook()
    {
        if (_natural != null) return;
        // one global handler (no mod context: it lives as long as Kiln.Creatures)
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try { _natural = Events.On<NaturalSpawnEvent>(OnNaturalSpawn, EventPriority.Low); }
        finally { ModContext.CurrentMod = outer; }
    }

    private static void OnNaturalSpawn(NaturalSpawnEvent ev)
    {
        if (ev.Canceled || Rules.Count == 0) return;
        if (ev.Replaced)
        {
            // kiln.gameplay already rolled the chance and swapped the template to a variant's base
            foreach (var r in Rules)
            {
                if (r.Replacement is null || r.BaseId != ev.Template || !ById.TryGetValue(r.Spec.Variant, out var ve)) continue;
                ApplyTo(ev.Entity, ve, natural: true);
                return;
            }
            return;
        }
        bool? night = null;
        foreach (var r in Rules)
        {
            if (r.TemplateId != ev.Template || r.BaseId != r.TemplateId) continue;
            if (r.Spec.NightOnly || r.Spec.DayOnly)
            {
                night ??= Time.Available && Time.Ready ? !Time.IsDay : null;
                if (night is null || (r.Spec.NightOnly && night == false) || (r.Spec.DayOnly && night == true)) continue;
            }
            if (r.Spec.Chance < 1 && Rng.NextDouble() >= r.Spec.Chance) continue;
            if (!ById.TryGetValue(r.Spec.Variant, out var e)) continue;
            ApplyTo(ev.Entity, e, natural: true);
            return;
        }
    }

    // ---- persistence (saved templates only) ----

    private sealed class VariantMark
    {
        public string Id { get; set; } = "";
    }

    private static void Persist(ModContext mod, EntityId e, string id)
    {
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = mod;
        try
        {
            if (EntityData.Available && EntityData.IsPersistent(e)) EntityData.Set(e, PersistKey, new VariantMark { Id = id });
        }
        catch (Exception ex)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"Kiln.Creatures: cannot persist variant {id} on {e}: {ex.Message}");
        }
        finally { ModContext.CurrentMod = outer; }
    }

    // Every ~2 s, in the mod's own context: entities whose saved EntityData names one of its variants get it again
    // (after a restart the entity is re-matched by kiln.saves; ids change).
    private static void StartPersistPump(ModContext mod)
    {
        if (!Persisting.Add(mod)) return;
        ulong n = 0;
        mod.Tick.Pre += (in TickInfo _) =>
        {
            if (++n % 50 != 0 || !Persisting.Contains(mod) || !EntityData.Available) return;
            foreach (var e in EntityData.Entities)
            {
                if (Applied.ContainsKey(e) || !EntityData.TryGet<VariantMark>(e, PersistKey, out var m)) continue;
                if (ById.TryGetValue(m.Id, out var entry) && entry.Owner.Mod == mod) ApplyTo(e, entry, natural: false);
            }
        };
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _d = dispose;
        public void Dispose() => Interlocked.Exchange(ref _d, null)?.Invoke();
    }
}

public static partial class Creatures
{
    /// <summary>Spawns a variant (by id) or a plain template (by name/GUID) and applies the variant's overrides in
    /// the same step, before any client sees the creature. The task is complete when it returns. Tick thread.</summary>
    public static Task<EntityId> Spawn(string variantOrTemplate, WorldPosition position, float yawDegrees = 0,
        CreatureSpawnOptions? options = null)
    {
        Services.RequireTickThread("Creatures.Spawn");
        var o = options ?? new CreatureSpawnOptions();
        if (CreatureVariants.EntryOf(variantOrTemplate) is { } e) return CreatureVariants.SpawnVariant(e, position, yawDegrees, o);
        return CreatureVariants.SpawnPlain(variantOrTemplate, position, yawDegrees, o);
    }
}
