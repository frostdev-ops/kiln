// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Shape of a <see cref="ProtectionZone"/>.</summary>
public enum ZoneShape
{
    /// <summary><see cref="ProtectionZone.Center"/> + <see cref="ProtectionZone.Radius"/>.</summary>
    Sphere = 0,
    /// <summary>Circle of <see cref="ProtectionZone.Radius"/> around the centre's x/z, from MinY to MaxY.</summary>
    Cylinder = 1,
    /// <summary><see cref="ProtectionZone.Min"/> .. <see cref="ProtectionZone.Max"/>.</summary>
    Box = 2,
}

/// <summary>A protected area: the listed actions are refused to every player who is not allowed. The game's own
/// building, terraforming and spawning code asks the zone table synchronously (no mod code can run there); crafting,
/// interaction and loot requests are checked in Pre before the game reads them.</summary>
public sealed record ProtectionZone
{
    public string Name { get; init; } = "";
    public GameplayActions Deny { get; init; } = GameplayActions.Build | GameplayActions.Break | GameplayActions.Terraform |
                                                  GameplayActions.Interact | GameplayActions.Loot;
    public ZoneShape Shape { get; init; } = ZoneShape.Sphere;
    public WorldPosition Center { get; init; }
    public double Radius { get; init; } = 16;
    public WorldPosition Min { get; init; }
    public WorldPosition Max { get; init; }
    /// <summary>Cylinder height range (sets Min.Y / Max.Y).</summary>
    public double MinY { get => Min.Y; init => Min = Min with { Y = value }; }
    public double MaxY { get => Max.Y; init => Max = Max with { Y = value }; }
    /// <summary>SteamID64s of the players allowed in the zone (the owners).</summary>
    public IReadOnlyCollection<ulong> AllowedSteamIds { get; init; } = [];
    /// <summary>Players with the game's admin bit are allowed too.</summary>
    public bool AllowAdmins { get; init; } = true;

    public bool Contains(WorldPosition p) => Shape switch
    {
        ZoneShape.Sphere => p.DistanceTo(Center) <= Radius,
        ZoneShape.Cylinder => Math.Sqrt((p.X - Center.X) * (p.X - Center.X) + (p.Z - Center.Z) * (p.Z - Center.Z)) <= Radius &&
                              p.Y >= Min.Y && p.Y <= Max.Y,
        _ => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z,
    };
}

/// <summary>A registered zone. Dispose (or unload the mod that added it) to remove it.</summary>
public sealed class ZoneHandle : IDisposable
{
    internal ZoneHandle(uint id, ProtectionZone zone, ModContext? owner)
    {
        Id = id;
        Zone = zone;
        Owner = owner;
    }

    public uint Id { get; }
    public ProtectionZone Zone { get; internal set; }
    internal ModContext? Owner { get; }
    public bool Active { get; internal set; } = true;

    /// <summary>Replaces the zone's shape, actions and owners. Tick thread.</summary>
    public void Update(ProtectionZone zone) => Gameplay.UpdateZone(this, zone);

    public void Dispose() => Gameplay.RemoveZone(this);
}

/// <summary>A recipe of the game's recipe registry. Inputs with item 0 are category inputs ("any wood").</summary>
public sealed record GameplayRecipe(uint Id, uint Workshop, IReadOnlyList<GameplayItemStack> Inputs,
    IReadOnlyList<GameplayItemStack> Outputs, TimeSpan Duration, ulong RequiredProps)
{
    public bool HasCategoryInputs => Inputs.Any(i => i.ItemId == 0);
}

/// <summary>Native status of kiln.gameplay.</summary>
public sealed record GameplayStatus(bool Intercept, bool Inject, bool CraftHook, bool BuildHooks, bool TerraformHook,
    bool SpawnHook, bool Teleports, bool WorldRunning, int Zones, int Subscribers, ulong Steps, ulong EventsSeen,
    ulong Dispatched, ulong Canceled, ulong Denied, ulong HookRecords)
{
    /// <summary>createEntity / destroyEntity are hooked (entity lifecycle events work).</summary>
    public bool EntityHooks { get; init; }
    /// <summary>Categories reported now.</summary>
    public EntityCategories EntityCategories { get; init; }
    public ulong EntitiesJoined { get; init; }
    public ulong EntitiesLeft { get; init; }
    /// <summary>Creations/destructions the hooks saw outside the categories.</summary>
    public ulong EntitiesFiltered { get; init; }
    /// <summary>Discard actions (<see cref="ItemTossEvent"/>) are read on this build.</summary>
    public bool Discard { get; init; }
    public ulong DiscardsSeen { get; init; }
    public ulong DiscardsCanceled { get; init; }
}

/// <summary>kiln.gameplay: player/world interaction events and protection zones.</summary>
/// <remarks>
/// The events (<see cref="CraftEvent"/>, <see cref="InteractEvent"/>, <see cref="BuildEvent"/>, ...) are posted on
/// Kiln's bus (<see cref="Events"/>) once this assembly is active: it activates itself the first time any of its code
/// runs (creating an event, calling <see cref="Gameplay"/>). A mod that only declares <c>[Subscribe]</c> handlers should
/// call <see cref="Enable"/> in <c>OnLoad</c>.
/// </remarks>
public static unsafe class Gameplay
{
    private const int Ok = 0;
    private static int _listening;
    private static ulong _handle;
    private static readonly object Gate = new();
    private static readonly Dictionary<uint, ZoneHandle> ZonesById = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);

    /// <summary>True when the native service is loaded on this game build.</summary>
    public static bool Available => GameplayNative.T != null;

    /// <summary>Makes sure the native service delivers events to the bus. Safe to call often.</summary>
    public static bool Enable() => EnsureListener();

#pragma warning disable CA2255 // a service assembly activates itself on first use, as Kiln.Combat does
    [ModuleInitializer]
    internal static void ModuleInit() => EnsureListener();
#pragma warning restore CA2255

    public static GameplayStatus? Status
    {
        get
        {
            var t = GameplayNative.T;
            if (t == null) return null;
            GameplayNative.Status s = default;
            s.Size = (uint)sizeof(GameplayNative.Status);
            if (t->Status(&s) != Ok) return null;
            var full = s.Size >= (uint)sizeof(GameplayNative.Status);
            return new GameplayStatus(s.Intercept != 0, s.Emit != 0, s.CraftHook != 0, s.BuildHooks != 0,
                s.TerraformHook != 0, s.SpawnHook != 0, s.Teleport != 0, s.World != 0, (int)s.Zones, (int)s.Subscribers,
                s.Steps, s.EventsSeen, s.Dispatched, s.Canceled, s.Denied, s.HookRecords)
            {
                EntityHooks = full && s.EntityHooks != 0,
                EntityCategories = full ? (EntityCategories)s.EntityCategories : EntityCategories.None,
                EntitiesJoined = full ? s.EntitiesJoined : 0,
                EntitiesLeft = full ? s.EntitiesLeft : 0,
                EntitiesFiltered = full ? s.EntitiesFiltered : 0,
                Discard = full && s.DiscardOn != 0,
                DiscardsSeen = full ? s.DiscardsSeen : 0,
                DiscardsCanceled = full ? s.DiscardsCanceled : 0,
            };
        }
    }

    // ------------------------------------------------------------------------------------------------ zones ----

    /// <summary>Adds a protection zone, owned by the calling mod (removed when it unloads). Tick thread.</summary>
    public static ZoneHandle Protect(ProtectionZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var t = Require();
        Services.RequireTickThread("Gameplay.Protect");
        var ids = new uint[1];
        var rc = WithNative(zone, z =>
        {
            fixed (uint* pid = ids) return t->ZoneAdd(Services.CallerOwner, z, pid);
        });
        var id = ids[0];
        if (rc != Ok) throw new ArgumentException($"Gameplay.Protect: the zone was refused (kiln error {rc})", nameof(zone));
        var owner = ModContext.CurrentMod;
        var h = new ZoneHandle(id, zone, owner);
        lock (Gate)
        {
            ZonesById[id] = h;
            if (owner != null && Watched.Add(owner)) owner.Retiring += () => Retire(owner);
        }
        return h;
    }

    /// <summary>Zones registered through this assembly.</summary>
    public static IReadOnlyList<ZoneHandle> Zones
    {
        get { lock (Gate) return ZonesById.Values.ToArray(); }
    }

    /// <summary>Would <paramref name="action"/> by <paramref name="player"/> at <paramref name="position"/> be denied
    /// by a zone? Uses the player list of the last step. Tick thread.</summary>
    public static bool IsDenied(EntityId player, WorldPosition position, GameplayActions action, out uint zoneId)
    {
        var t = Require();
        uint z = 0;
        var denied = t->ZoneDenies(player.Value, GameplayNative.Vec3.From(position), (uint)action, &z) != 0;
        zoneId = z;
        return denied;
    }

    internal static string ZoneName(uint id)
    {
        lock (Gate) return ZonesById.TryGetValue(id, out var h) ? h.Zone.Name : "";
    }

    internal static void UpdateZone(ZoneHandle h, ProtectionZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var t = Require();
        Services.RequireTickThread("ZoneHandle.Update");
        if (!h.Active) throw new ObjectDisposedException(nameof(ZoneHandle));
        var rc = WithNative(zone, z => t->ZoneUpdate(h.Id, z));
        if (rc != Ok) throw new ArgumentException($"ZoneHandle.Update: the zone was refused (kiln error {rc})", nameof(zone));
        h.Zone = zone;
    }

    internal static void RemoveZone(ZoneHandle h)
    {
        if (!h.Active) return;
        h.Active = false;
        lock (Gate) ZonesById.Remove(h.Id);
        var t = GameplayNative.T;
        if (t != null) t->ZoneRemove(h.Id);
    }

    private static void Retire(ModContext mod)
    {
        List<ZoneHandle> drop;
        lock (Gate)
        {
            Watched.Remove(mod);
            drop = ZonesById.Values.Where(z => ReferenceEquals(z.Owner, mod)).ToList();
            EntityWatches.RemoveAll(w => ReferenceEquals(w.Owner, mod));
        }
        foreach (var z in drop) RemoveZone(z);
        var clear = GameplayNative.SpawnReplaceClear(GameplayNative.T);
        if (clear != null && mod.Owner != 0)
        {
            uint n = 0;
            clear(mod.Owner, &n);
        }
    }

    /// <summary>
    /// Natural spawns of <paramref name="from"/> spawn <paramref name="to"/> instead, with probability
    /// <paramref name="chance"/> (0..1): decided inside the random spawner's own spawn call, so its bookkeeping follows
    /// the new entity. The <see cref="NaturalSpawnEvent"/> then reports the new template with
    /// <see cref="NaturalSpawnEvent.Replaced"/>. Owned by the calling mod (removed when it unloads, or on Dispose). Any
    /// thread. Throws <see cref="NotSupportedException"/> when this kiln.gameplay has no replacement (older DLL, or the
    /// spawn hook is off on this build).
    /// </summary>
    public static IDisposable ReplaceNaturalSpawns(Guid from, Guid to, double chance = 1)
    {
        if (chance is < 0 or > 1 || double.IsNaN(chance)) throw new ArgumentOutOfRangeException(nameof(chance));
        var t = Require();
        var set = GameplayNative.SpawnReplace(t);
        if (set == null) throw new NotSupportedException("Gameplay.ReplaceNaturalSpawns: this kiln.gameplay has no spawn replacement");
        var owner = Services.CallerOwner;
        var mod = ModContext.CurrentMod;
        Span<byte> f = stackalloc byte[16], tg = stackalloc byte[16];
        from.TryWriteBytes(f);
        to.TryWriteBytes(tg);
        int rc;
        fixed (byte* pf = f)
        fixed (byte* pt = tg)
            rc = set(owner, pf, pt, (float)chance);
        if (rc != Ok) throw new NotSupportedException($"Gameplay.ReplaceNaturalSpawns: refused (kiln error {rc})");
        if (mod != null) lock (Gate) if (Watched.Add(mod)) mod.Retiring += () => Retire(mod);
        return new SpawnReplacement(owner, from, to);
    }

    private sealed class SpawnReplacement(ulong owner, Guid from, Guid to) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            var set = GameplayNative.SpawnReplace(GameplayNative.T);
            if (set == null) return;
            Span<byte> f = stackalloc byte[16], tg = stackalloc byte[16];
            from.TryWriteBytes(f);
            to.TryWriteBytes(tg);
            fixed (byte* pf = f)
            fixed (byte* pt = tg)
                set(owner, pf, pt, 0);
        }
    }

    private delegate int ZoneCall(GameplayNative.Zone* z);

    private static int WithNative(ProtectionZone zone, ZoneCall call)
    {
        var ids = zone.AllowedSteamIds.ToArray();
        var name = System.Text.Encoding.UTF8.GetBytes(zone.Name ?? "");
        fixed (ulong* pIds = ids)
        fixed (byte* pName = name)
        {
            GameplayNative.Zone z = default;
            z.Size = (uint)sizeof(GameplayNative.Zone);
            z.Deny = (uint)zone.Deny;
            z.Shape = (uint)zone.Shape;
            z.Flags = zone.AllowAdmins ? 1u : 0u;
            z.Center = GameplayNative.Vec3.From(zone.Center);
            z.Radius = zone.Radius;
            z.Min = GameplayNative.Vec3.From(zone.Min);
            z.Max = GameplayNative.Vec3.From(zone.Max);
            z.Allowed = pIds;
            z.AllowedCount = (uint)ids.Length;
            z.Name = new NativeStr { Ptr = pName, Len = (nuint)name.Length };
            return call(&z);
        }
    }

    // ------------------------------------------------------------------------------------- entity lifecycle ----

    private sealed class EntityWatch(EntityCategories categories, ModContext? owner) : IDisposable
    {
        public readonly EntityCategories Categories = categories;
        public readonly ModContext? Owner = owner;

        public void Dispose()
        {
            lock (Gate) EntityWatches.Remove(this);
        }
    }

    private static readonly List<EntityWatch> EntityWatches = new();
    private static uint _entityFilter = uint.MaxValue;  // last value sent to the native service

    /// <summary>Which entities <see cref="EntityJoinLevelEvent"/> / <see cref="EntityLeaveLevelEvent"/> report: the
    /// union of every mod's requests (<see cref="EntityCategories.Default"/> while subscribers exist and nobody asked).
    /// Dispose the handle (or unload the mod) to drop the request. Any thread.</summary>
    public static IDisposable WatchEntities(EntityCategories categories)
    {
        EnsureListener();
        var mod = ModContext.CurrentMod;
        var w = new EntityWatch(categories & EntityCategories.All, mod);
        lock (Gate)
        {
            EntityWatches.Add(w);
            if (mod != null && Watched.Add(mod)) mod.Retiring += () => Retire(mod);
        }
        return w;
    }

    /// <summary>True when the entity lifecycle events work on this build.</summary>
    public static bool EntityEventsAvailable => GameplayNative.EntityFilter(GameplayNative.T) != null && Status is { EntityHooks: true };

    // Once per step (tick): tell the native hooks which categories to record.
    private static void SyncEntityFilter()
    {
        uint want = 0;
        if (Events.HasSubscribers<EntityJoinLevelEvent>() || Events.HasSubscribers<EntityLeaveLevelEvent>())
        {
            lock (Gate)
            {
                foreach (var w in EntityWatches) want |= (uint)w.Categories;
                if (EntityWatches.Count == 0) want = (uint)EntityCategories.Default;
            }
        }
        if (want == _entityFilter) return;
        var f = GameplayNative.EntityFilter(GameplayNative.T);
        if (f == null) return;
        if (f(want) == Ok) _entityFilter = want;
    }

    // --------------------------------------------------------------------------------------- watch / inject ----

    /// <summary>Adds (or removes) a non-player entity to the teleport watch list, so <see cref="TeleportEvent"/> and
    /// <see cref="TeleportedEvent"/> fire for it. Players are always watched. Tick thread.</summary>
    public static void WatchTeleports(EntityId entity, bool watch = true)
    {
        var t = Require();
        Services.RequireTickThread("Gameplay.WatchTeleports");
        Check(t->Watch(entity.Value, watch ? (byte)1 : (byte)0), "Gameplay.WatchTeleports");
    }

    /// <summary>Sends the game's own request events from Kiln's ECS program (next step), as a client or villager
    /// would: the game validates them as usual, and they pass through the cancelable events like the game's.</summary>
    public static class Inject
    {
        /// <summary>A CraftRecipeEvent: <paramref name="operatorEntity"/> pays and receives (a player, or a chest with
        /// an inventory), <paramref name="station"/> has <c>Crafting</c> (a workbench, a villager).</summary>
        public static void Craft(EntityId operatorEntity, EntityId station, uint recipeId, int amount = 1) =>
            Send(1, operatorEntity, station, recipeId, (uint)Math.Clamp(amount, 1, 65535), 0);

        /// <summary>An AcceptOfferedInteractionEvent (<paramref name="offerId"/> = the host's current offer).</summary>
        public static void Interact(EntityId guest, EntityId host, uint offerId) => Send(2, guest, host, offerId, 0, 0);

        /// <summary>A LootInteractionEvent.</summary>
        public static void Loot(EntityId guest, EntityId host, uint offerId = 0, bool lootAll = true, bool bypassInteraction = true) =>
            Send(3, guest, host, offerId, 0, (lootAll ? 1u : 0u) | (bypassInteraction ? 2u : 0u));

        private static void Send(uint kind, EntityId actor, EntityId target, uint id, uint amount, uint flags)
        {
            var t = Require();
            Services.RequireTickThread("Gameplay.Inject");
            var r = new GameplayNative.Inject
            {
                Size = (uint)sizeof(GameplayNative.Inject), Kind = kind, Actor = actor.Value, Target = target.Value, Id = id,
                Amount = amount, Flags = flags,
            };
            Check(t->Inject(&r), "Gameplay.Inject");
        }
    }

    // ---------------------------------------------------------------------------------------------- recipes ----

    /// <summary>A recipe by id, or null. Tick thread.</summary>
    public static GameplayRecipe? Recipe(uint id)
    {
        var t = Require();
        Services.RequireTickThread("Gameplay.Recipe");
        GameplayNative.Recipe r = default;
        r.Size = (uint)sizeof(GameplayNative.Recipe);
        var rc = t->Recipe(id, &r);
        if (rc == 2) return null;
        Check(rc, "Gameplay.Recipe");
        var ins = new GameplayItemStack[Math.Min(r.InputCount, 8u)];
        for (var i = 0; i < ins.Length; i++) ins[i] = new GameplayItemStack(r.Inputs[i * 2], (int)r.Inputs[i * 2 + 1]);
        var outs = new GameplayItemStack[Math.Min(r.OutputCount, 8u)];
        for (var i = 0; i < outs.Length; i++) outs[i] = new GameplayItemStack(r.Outputs[i * 2], (int)r.Outputs[i * 2 + 1]);
        return new GameplayRecipe(r.Id, r.Workshop, ins, outs, TimeSpan.FromTicks(r.DurationNs / 100), r.RequiredProps);
    }

    /// <summary>The recipe ids a workshop (a station kind; 0 = hand crafting) offers. Empty when unknown. Tick thread.</summary>
    public static IReadOnlyList<uint> WorkshopRecipes(uint workshop)
    {
        var t = Require();
        Services.RequireTickThread("Gameplay.WorkshopRecipes");
        var buf = new uint[512];
        uint n;
        int rc;
        fixed (uint* p = buf) rc = t->WorkshopRecipes(workshop, p, (uint)buf.Length, &n);
        if (rc == 2) return [];
        if (rc != Ok && rc != 5) Check(rc, "Gameplay.WorkshopRecipes");
        return buf.Take((int)Math.Min(n, (uint)buf.Length)).ToArray();
    }

    // ------------------------------------------------------------------------------------------- dispatcher ----

    internal static bool EnsureListener()
    {
        if (Volatile.Read(ref _listening) != 0) return true;
        var t = GameplayNative.T;
        if (t == null) return false;
        lock (Gate)
        {
            if (_listening != 0) return true;
            ulong h;
            var rc = t->Subscribe(0, GameplayNative.Mask(GameplayNative.Tick), &OnEvent, null, &h);
            if (rc != Ok)
            {
                Backend.Current.Log(0, LogLevel.Warn, $"Kiln.Gameplay: cannot subscribe to kiln.gameplay (kiln error {rc})");
                return false;
            }
            _handle = h;
            _listening = 1;
            Backend.Current.Log(0, LogLevel.Info, "Kiln.Gameplay: listening to kiln.gameplay");
            return true;
        }
    }

    private static uint Interest()
    {
        uint m = 0;
        if (Events.HasSubscribers<CraftEvent>()) m |= GameplayNative.Mask(GameplayNative.Craft);
        if (Events.HasSubscribers<ItemCraftedEvent>()) m |= GameplayNative.Mask(GameplayNative.Crafted);
        if (Events.HasSubscribers<InteractEvent>()) m |= GameplayNative.Mask(GameplayNative.Interact);
        if (Events.HasSubscribers<InteractionResultEvent>()) m |= GameplayNative.Mask(GameplayNative.Interacted);
        if (Events.HasSubscribers<LootEvent>()) m |= GameplayNative.Mask(GameplayNative.Loot);
        if (Events.HasSubscribers<ItemPickupEvent>()) m |= GameplayNative.Mask(GameplayNative.Pickup);
        if (Events.HasSubscribers<BuildEvent>()) m |= GameplayNative.Mask(GameplayNative.Build);
        if (Events.HasSubscribers<TeleportEvent>()) m |= GameplayNative.Mask(GameplayNative.Teleport);
        if (Events.HasSubscribers<TeleportedEvent>()) m |= GameplayNative.Mask(GameplayNative.Teleported);
        if (Events.HasSubscribers<NaturalSpawnEvent>()) m |= GameplayNative.Mask(GameplayNative.Spawn);
        if (Events.HasSubscribers<EntityJoinLevelEvent>()) m |= GameplayNative.Mask(GameplayNative.EntityJoined);
        if (Events.HasSubscribers<EntityLeaveLevelEvent>()) m |= GameplayNative.Mask(GameplayNative.EntityLeft);
        if (Events.HasSubscribers<ItemTossEvent>()) m |= GameplayNative.Mask(GameplayNative.Discard);
        SyncEntityFilter();
        return m;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnEvent(GameplayNative.Event* e, void* user)
    {
        try
        {
            if (e->Kind == GameplayNative.Tick) return Interest();
            return Dispatch(e);
        }
        catch (Exception ex)
        {
            try { Backend.Current.Log(0, LogLevel.Error, $"Kiln.Gameplay: dispatch of event kind {e->Kind} failed: {ex}"); }
            catch { /* logging must not throw back into native code */ }
            return GameplayNative.Keep;
        }
    }

    private static WorldPosition? Pos(in GameplayNative.Vec3 v) =>
        v.X == 0 && v.Y == 0 && v.Z == 0 ? null : v.ToPosition();

    private static uint Verdict(GameplayNative.Event* e, CancelableEvent ev)
    {
        var before = (e->Flags & GameplayNative.FCanceled) != 0;
        if (ev.Canceled == before) return GameplayNative.Keep;
        return ev.Canceled ? GameplayNative.Cancel : GameplayNative.Allow;
    }

    private static uint Dispatch(GameplayNative.Event* e)
    {
        var canceled = (e->Flags & GameplayNative.FCanceled) != 0;
        var injected = (e->Flags & GameplayNative.FInjected) != 0;
        switch (e->Kind)
        {
            case GameplayNative.Craft when Events.HasSubscribers<CraftEvent>():
            {
                var ev = new CraftEvent
                {
                    Step = e->Step, DeniedByZone = e->Zone, Injected = injected, Canceled = canceled,
                    Operator = new EntityId(e->Actor), Station = new EntityId(e->Target), RecipeId = e->Id,
                    Amount = (int)e->Amount, StationPosition = Pos(e->Position),
                };
                return Verdict(e, Events.Post(ev));
            }
            case GameplayNative.Crafted when Events.HasSubscribers<ItemCraftedEvent>():
            {
                var outs = new GameplayItemStack[Math.Min(e->OutputCount, 8u)];
                for (var i = 0; i < outs.Length; i++) outs[i] = new GameplayItemStack(e->Outputs[i * 2], (int)e->Outputs[i * 2 + 1]);
                Events.Post(new ItemCraftedEvent
                {
                    Step = e->Step, Operator = new EntityId(e->Actor), Station = new EntityId(e->Target), RecipeId = e->Id,
                    WorkshopId = e->Id2, RawResult = e->Result,
                    Result = e->Result is 0 or 2 or 4 or 5 ? (CraftResult)e->Result : CraftResult.Other,
                    Outputs = outs, StationPosition = Pos(e->Position),
                });
                return GameplayNative.Keep;
            }
            case GameplayNative.Interact when Events.HasSubscribers<InteractEvent>():
            {
                var ev = new InteractEvent
                {
                    Step = e->Step, DeniedByZone = e->Zone, Injected = injected, Canceled = canceled,
                    Guest = new EntityId(e->Actor), Host = new EntityId(e->Target), OfferId = e->Id,
                    HostPosition = Pos(e->Position),
                };
                return Verdict(e, Events.Post(ev));
            }
            case GameplayNative.Interacted when Events.HasSubscribers<InteractionResultEvent>():
                Events.Post(new InteractionResultEvent
                {
                    Step = e->Step, Guest = new EntityId(e->Actor), Host = new EntityId(e->Target), OfferId = e->Id,
                    Accepted = e->Op == 1,
                });
                return GameplayNative.Keep;
            case GameplayNative.Loot when Events.HasSubscribers<LootEvent>():
            {
                var ev = new LootEvent
                {
                    Step = e->Step, DeniedByZone = e->Zone, Injected = injected, Canceled = canceled,
                    Guest = new EntityId(e->Actor), Host = new EntityId(e->Target), OfferId = e->Id,
                    LootAll = (e->Op & 1) != 0, BypassInteraction = (e->Op & 2) != 0, HostPosition = Pos(e->Position),
                };
                return Verdict(e, Events.Post(ev));
            }
            case GameplayNative.Pickup when Events.HasSubscribers<ItemPickupEvent>():
                Events.Post(new ItemPickupEvent
                {
                    Step = e->Step, Receiver = new EntityId(e->Actor), Source = new EntityId(e->Target), ItemId = e->Id,
                    Count = (int)e->Amount, SlotInventory = new EntityId(e->Extra), SlotIndex = (int)e->Id2,
                    HiddenInUi = e->Op != 0, Position = e->Position.ToPosition(),
                });
                return GameplayNative.Keep;
            case GameplayNative.Build when Events.HasSubscribers<BuildEvent>():
                Events.Post(new BuildEvent
                {
                    Step = e->Step, Kind = (BuildKind)e->Op, Player = new EntityId(e->Actor), ItemId = e->Id,
                    Position = e->Position.ToPosition(), Target = new EntityId(e->Target), TerraformType = (int)e->Id2,
                    Outcome = e->Result > 0 ? BuildOutcome.Done : e->Result == 0 ? BuildOutcome.RefusedByGame : BuildOutcome.DeniedByZone,
                    DeniedByZone = e->Zone,
                });
                return GameplayNative.Keep;
            case GameplayNative.Teleport when Events.HasSubscribers<TeleportEvent>():
            {
                var target = e->TargetPosition.ToPosition();
                var ev = new TeleportEvent
                {
                    Step = e->Step, Canceled = canceled, Entity = new EntityId(e->Actor), From = e->Position.ToPosition(),
                    Target = target, TargetEntity = new EntityId(e->Target), SearchesGround = e->Op != 0,
                };
                Events.Post(ev);
                if (!ev.Canceled && ev.Target != target)
                {
                    e->TargetPosition = GameplayNative.Vec3.From(ev.Target);
                    e->Flags |= GameplayNative.FRetarget;
                }
                return Verdict(e, ev);
            }
            case GameplayNative.Teleported when Events.HasSubscribers<TeleportedEvent>():
                Events.Post(new TeleportedEvent
                {
                    Step = e->Step, Entity = new EntityId(e->Actor), From = e->Position.ToPosition(),
                    To = e->TargetPosition.ToPosition(), Count = (int)e->Amount,
                });
                return GameplayNative.Keep;
            case GameplayNative.Spawn when Events.HasSubscribers<NaturalSpawnEvent>():
            {
                var g = new ReadOnlySpan<byte>(e->Guid, 16);
                var ev = new NaturalSpawnEvent
                {
                    Step = e->Step, DeniedByZone = e->Zone, Canceled = canceled, Entity = new EntityId(e->Actor),
                    Template = new Guid(g), Position = e->Position.ToPosition(),
                    Replaced = (e->Flags & GameplayNative.FReplaced) != 0,
                };
                Events.Post(ev);
                // A zone-refused spawn has no entity: canceling or un-canceling it changes nothing.
                return e->Zone != 0 ? GameplayNative.Keep : Verdict(e, ev);
            }
            case GameplayNative.EntityJoined when Events.HasSubscribers<EntityJoinLevelEvent>():
            {
                var ev = new EntityJoinLevelEvent
                {
                    Step = e->Step, Canceled = canceled, Entity = new EntityId(e->Actor), Parent = new EntityId(e->Target),
                    Template = GuidOf(e), Category = (EntityCategories)e->Op, Position = Pos(e->Position), ByKiln = e->Extra != 0,
                };
                Events.Post(ev);
                return (e->Flags & GameplayNative.FCancelable) != 0 ? Verdict(e, ev) : GameplayNative.Keep;
            }
            case GameplayNative.EntityLeft when Events.HasSubscribers<EntityLeaveLevelEvent>():
                Events.Post(new EntityLeaveLevelEvent
                {
                    Step = e->Step, Entity = new EntityId(e->Actor), Parent = new EntityId(e->Target), Template = GuidOf(e),
                    Category = (EntityCategories)e->Op, Position = Pos(e->Position), ByKiln = e->Extra != 0,
                });
                return GameplayNative.Keep;
            case GameplayNative.Discard when Events.HasSubscribers<ItemTossEvent>():
            {
                var ev = new ItemTossEvent
                {
                    Step = e->Step, Canceled = canceled, Player = new EntityId(e->Actor), How = (DiscardKind)e->Op,
                    Inventory = new EntityId(e->Extra), SlotIndex = (int)e->Id2, ItemId = e->Id, Count = (int)e->Amount,
                    ItemEntity = new EntityId(e->Target), PlayerPosition = Pos(e->Position),
                };
                return Verdict(e, Events.Post(ev));
            }
            default:
                return GameplayNative.Keep;
        }
    }

    private static Guid? GuidOf(GameplayNative.Event* e)
    {
        var g = new ReadOnlySpan<byte>(e->Guid, 16);
        foreach (var b in g)
            if (b != 0) return new Guid(g);
        return null;
    }

    // -------------------------------------------------------------------------------------------------- misc ----

    private static GameplayNative.Table* Require()
    {
        var t = GameplayNative.T;
        if (t == null) throw new NotSupportedException("kiln.gameplay is unavailable on this game build");
        EnsureListener();
        return t;
    }

    private static void Check(int rc, string api)
    {
        switch (rc)
        {
            case Ok: return;
            case 1: throw new ArgumentException($"{api}: invalid argument");
            case 3: throw new NotSupportedException($"{api}: unavailable on this game build");
            case 4: throw new InvalidOperationException($"{api} must be called on the tick thread");
            case 5: throw new InvalidOperationException($"{api}: too many pending requests");
            default: throw new InvalidOperationException($"{api} failed (kiln error {rc})");
        }
    }
}
