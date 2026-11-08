// SPDX-License-Identifier: MIT
// Interaction offers ("E" prompts) on any entity, for C# mods: the equivalent of EMF's offer, offer_remove,
// spawn_offer and offer_active (EmfApi v25/v26). The registry's behaviour is ported from kiln-emf's OfferService
// (native/plugins/emf/src/v26_model.cpp), itself after EMF 0.5.1 (MIT, EMF contributors): the three run-time
// components the game puts on its own interactables (InteractionOfferRuntime, ClientInteractionOffer,
// CheckPlayerInRange) are added with kiln.gameplay's component_change, one owner per entity, only the components Kiln
// added are taken off again, and an offer that the game replaced or whose entity went away is dropped. The accept is
// the game's own InteractionAcceptedEvent, read through kiln.gameplay's INTERACTED event.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>Prompt words (loca tag ids of the game) for <see cref="Offers.Add"/>. Values from EMF's emf_api.h (MIT).</summary>
public static class OfferVerbs
{
    public const uint Use = 0x26437C13u, Open = 0x0C2E4515u, Talk = 0xD9861C4Bu, Read = 0x631937DCu, Sit = 0xDFB7E778u,
        Sleep = 0xC4E0F1CBu, Pet = 0x6D5B6297u, Touch = 0x18A9222Cu, Activate = 0x811C5D9Eu, Press = 0x6F362030u,
        Enter = 0x785FF179u, Inspect = 0x260798EBu, Take = 0x33E582F6u, Collect = 0x9095E8BDu, Rest = 0x62F329EBu,
        Release = 0xAE94F76Au;
}

/// <summary>What the player plays when accepting (interaction sequences). Values from EMF's emf_api.h (MIT).</summary>
public static class OfferSequences
{
    /// <summary>A short touch, no screen (the default).</summary>
    public const uint Touch = 0x3E5189BFu;
    public const uint ButtonPress = 0xAF614FD8u;
}

/// <summary>Optional settings of an offer.</summary>
public sealed record OfferOptions
{
    /// <summary>The interaction sequence (<see cref="OfferSequences"/>); 0 = Touch.</summary>
    public uint Sequence { get; init; }
    /// <summary>How close the player must be, in metres (1..255); 0 = 32.</summary>
    public byte RangeMeters { get; init; }
    /// <summary>Other offers nearby win the prompt (the game's reduceInteractionWeight).</summary>
    public bool LowPriority { get; init; }
    /// <summary>Usable in build mode too (allowedAsBuildingSpark).</summary>
    public bool BuildMode { get; init; }
}

/// <summary>Someone accepted an offer of yours.</summary>
public readonly record struct OfferAccepted(EntityId Player, EntityId Entity);

/// <summary>Where an offer stands.</summary>
public enum OfferStatus
{
    /// <summary>No offer of Kiln's on the entity (or not the caller's).</summary>
    None = 0,
    /// <summary>Asked for; the components land within a step or two.</summary>
    Pending = 1,
    /// <summary>The components are on the entity: players see the prompt.</summary>
    Active = 2,
    /// <summary>Being taken off.</summary>
    Removing = 3,
}

/// <summary>
/// Interaction offers on any entity: the prompt a player accepts with E, and your handler when they do. Server only,
/// tick thread. An offer belongs to the mod that added it and is taken off when that mod unloads. Kiln removes only the
/// components it added, never an offer the game made (a chest, a bench, a bed refuse an offer).
/// </summary>
[TickThreadOnly]
public static class Offers
{
    internal const uint InteractionOfferRuntime = 0x78EE0F23u, ClientInteractionOffer = 0x8598AD86u,
        CheckPlayerInRange = 0x50311280u;
    internal const int VerifySteps = 10, UpkeepSteps = 30;
    private const byte DefaultRange = 32;

    internal sealed class Spec
    {
        public uint Verb, Sequence;
        public byte Range;
        public bool LowPriority, BuildMode;

        public bool SameAs(Spec o) => Verb == o.Verb && Sequence == o.Sequence && Range == o.Range &&
                                      LowPriority == o.LowPriority && BuildMode == o.BuildMode;
    }

    private sealed class Change(uint hash, bool add)
    {
        public readonly uint Hash = hash;
        public readonly bool Add = add;
        public int Waited;
    }

    private sealed class Entry
    {
        public required uint Entity;
        public required ModContext? Owner;
        public required Spec Spec;
        public required Action<OfferAccepted> Handler;
        public readonly List<Change> Pending = [];
        public readonly HashSet<uint> Added = [];
        public bool Removing, Lost, RemovesSent;
        public long Accepts;
    }

    private static readonly Dictionary<uint, Entry> Entries = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static IDisposable? _tick;
    private static int _steps;
    private static IOfferWorld? _world;

    internal static IOfferWorld World { get => _world ??= new NativeOfferWorld(); set => _world = value; }

    /// <summary>True when offers work on this server (kiln.gameplay's component_change and kiln.world).</summary>
    public static bool Available => World.Available;

    /// <summary>
    /// Puts an offer on <paramref name="entity"/>: players near it see a prompt; <paramref name="handler"/> runs (tick
    /// thread, as your mod) each time one accepts. <paramref name="text"/> non-null makes a custom prompt word (its id
    /// is EMF's: FNV-1a of "emf.verb:" + text; a client shows it only if it has a text for that id), otherwise
    /// <paramref name="verb"/> (<see cref="OfferVerbs"/>) is used. The prompt appears a step or two later. Adding the
    /// same offer again only replaces the handler. Throws <see cref="InvalidOperationException"/> with the reason when
    /// refused (no such entity, a player, the game's own offer, another mod's offer); use <see cref="TryAdd"/> to get
    /// the reason without an exception.
    /// </summary>
    public static void Add(EntityId entity, string? text, uint verb, Action<OfferAccepted> handler, OfferOptions? options = null)
    {
        if (!TryAdd(entity, text, verb, handler, out var why, options))
            throw new InvalidOperationException($"Offers.Add({entity}): {why}");
    }

    /// <summary><see cref="Add"/> that returns false and the reason instead of throwing.</summary>
    public static bool TryAdd(EntityId entity, string? text, uint verb, Action<OfferAccepted> handler, out string reason,
        OfferOptions? options = null)
    {
        Services.RequireTickThread("Offers.Add");
        ArgumentNullException.ThrowIfNull(handler);
        var spec = SpecOf(text, verb, options);
        var owner = ModContext.CurrentMod;
        reason = Offer(entity.Value, spec, owner, handler);
        return reason.Length == 0;
    }

    /// <summary>Spawns <paramref name="template"/> (as <c>Spawn.Entity</c>) and puts an offer on it. The task completes
    /// with the entity once it exists; a refused offer throws (the entity stays: destroy it if you do not want it).</summary>
    public static async Task<EntityId> SpawnWithOffer(string template, WorldPosition position, string? text, uint verb,
        Action<OfferAccepted> handler, OfferOptions? options = null, SpawnOptions? spawn = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var e = await Spawn.Entity(template, position, 0, spawn);
        Add(e, text, verb, handler, options);
        return e;
    }

    /// <summary>Takes your offer off <paramref name="entity"/>: Kiln removes only the components it added (within a step
    /// or two). False when the calling mod has no offer there. Tick thread.</summary>
    public static bool Remove(EntityId entity)
    {
        Services.RequireTickThread("Offers.Remove");
        return RemoveAs(entity.Value, ModContext.CurrentMod);
    }

    /// <summary>The calling mod has an offer on <paramref name="entity"/> (pending or active; EMF's offer_active).</summary>
    public static bool IsActive(EntityId entity) => Status(entity) is OfferStatus.Pending or OfferStatus.Active;

    /// <summary>Where the calling mod's offer on <paramref name="entity"/> stands.</summary>
    public static OfferStatus Status(EntityId entity)
    {
        var owner = ModContext.CurrentMod;
        if (!Entries.TryGetValue(entity.Value, out var e) || !ReferenceEquals(e.Owner, owner) || e.Lost)
            return OfferStatus.None;
        if (e.Removing) return OfferStatus.Removing;
        return e.Pending.Count > 0 ? OfferStatus.Pending : OfferStatus.Active;
    }

    /// <summary>The offers of every mod: entity, owner mod id ("kiln" for none), verb id, accepts (for admin tools).</summary>
    public static IReadOnlyList<(EntityId Entity, string Owner, uint Verb, long Accepts)> List() =>
        Entries.Values.Where(e => !e.Removing && !e.Lost)
            .Select(e => (new EntityId(e.Entity), ModInvoke.Name(e.Owner), e.Spec.Verb, e.Accepts)).ToList();

    // ------------------------------------------------------------------------------------------- internals ----

    /// <summary>The prompt-word id <see cref="Add"/> uses for a custom text (EMF's: FNV-1a-32 of "emf.verb:" + text, UTF-8). Any thread.</summary>
    public static uint CustomVerbId(string text)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes("emf.verb:" + text)) h = (h ^ b) * 16777619u;
        return h;
    }

    internal static Spec SpecOf(string? text, uint verb, OfferOptions? o)
    {
        if (text != null)
        {
            var n = Encoding.UTF8.GetByteCount(text);
            if (n is 0 or > 63) throw new ArgumentException("text must be 1 to 63 bytes", nameof(text));
        }
        return new Spec
        {
            Verb = text != null ? CustomVerbId(text) : verb != 0 ? verb : OfferVerbs.Use,
            Sequence = o?.Sequence is { } s and not 0 ? s : OfferSequences.Touch,
            Range = o?.RangeMeters is { } r and not 0 ? r : DefaultRange,
            LowPriority = o?.LowPriority ?? false,
            BuildMode = o?.BuildMode ?? false,
        };
    }

    /// <summary>InteractionOfferRuntime (56 bytes): isOffered and offerId stay 0, the game's interaction_offer sets them.</summary>
    internal static byte[] RuntimeBytes(Spec s)
    {
        var b = new byte[56];
        BitConverter.TryWriteBytes(b.AsSpan(8), s.Sequence);  // defaultSequence
        BitConverter.TryWriteBytes(b.AsSpan(16), s.Verb);     // defaultVerbId
        b[47] = s.LowPriority ? (byte)1 : (byte)0;            // reduceInteractionWeight
        b[48] = s.BuildMode ? (byte)1 : (byte)0;              // allowedAsBuildingSpark
        return b;
    }

    private static bool StillOurs(uint entity, Spec spec) =>
        World.ReadRuntime(entity) is { } got && got.Sequence == spec.Sequence && got.Verb == spec.Verb;

    private static string Offer(uint entity, Spec spec, ModContext? owner, Action<OfferAccepted> handler)
    {
        var w = World;
        if (!w.Available) return "offers are not available on this server (kiln.gameplay component_change or kiln.world missing)";
        if (entity == 0) return "no entity";
        if (Entries.TryGetValue(entity, out var e))
        {
            if (e.Removing) return "an earlier offer is still being taken off it: try again in a moment";
            if (!ReferenceEquals(e.Owner, owner)) return $"another mod's offer is on it ({ModInvoke.Name(e.Owner)})";
            if (!e.Spec.SameAs(spec)) return "its offer is there already: remove it first, then offer again";
            e.Handler = handler;  // the same offer: a new handler
            return "";
        }
        if (!w.Exists(entity)) return "the entity does not exist";
        if (w.IsPlayer(entity)) return "never a player";
        if (w.Has(entity, InteractionOfferRuntime)) return "it has its own offer from the game (a chest, a bench, a bed...)";

        var entry = new Entry { Entity = entity, Owner = owner, Spec = spec, Handler = handler };
        var rc = w.Change(owner?.Owner ?? 0, entity, InteractionOfferRuntime, RuntimeBytes(spec), true);
        if (rc != 0) return $"changing components was refused (kiln error {rc})";
        entry.Pending.Add(new Change(InteractionOfferRuntime, true));
        if (!w.Has(entity, ClientInteractionOffer) && w.Change(owner?.Owner ?? 0, entity, ClientInteractionOffer, new byte[12], true) == 0)
            entry.Pending.Add(new Change(ClientInteractionOffer, true));
        if (!w.Has(entity, CheckPlayerInRange) && w.Change(owner?.Owner ?? 0, entity, CheckPlayerInRange, [spec.Range], true) == 0)
            entry.Pending.Add(new Change(CheckPlayerInRange, true));
        Entries[entity] = entry;
        Watch(owner);
        _tick ??= ServiceTick.Add("kiln offers", static (in TickInfo _) => Step());
        Gameplay.Enable();
        return "";
    }

    private static void Watch(ModContext? owner)
    {
        if (owner != null && Watched.Add(owner)) owner.Retiring += () => Retire(owner);
    }

    private static bool RemoveAs(uint entity, ModContext? owner)
    {
        if (!Entries.TryGetValue(entity, out var e) || !ReferenceEquals(e.Owner, owner) || e.Removing || e.Lost) return false;
        e.Removing = true;
        if (e.Pending.Count == 0)
        {
            e.RemovesSent = true;
            SendRemoves(e);
        }
        return true;
    }

    /// <summary>Queues the removal of what Kiln added, if the offer is still Kiln's; drops the entry when nothing is left.</summary>
    private static void SendRemoves(Entry e)
    {
        var w = World;
        if (w.Exists(e.Entity) && (!w.Has(e.Entity, InteractionOfferRuntime) || StillOurs(e.Entity, e.Spec)))
            foreach (var hash in new[] { InteractionOfferRuntime, ClientInteractionOffer, CheckPlayerInRange })
                if (e.Added.Contains(hash) && w.Change(e.Owner?.Owner ?? 0, e.Entity, hash, null, false) == 0)
                    e.Pending.Add(new Change(hash, false));
        if (e.Pending.Count == 0) Entries.Remove(e.Entity);
    }

    private static void Retire(ModContext mod)
    {
        Watched.Remove(mod);
        foreach (var e in Entries.Values.Where(x => ReferenceEquals(x.Owner, mod)).ToList())
        {
            e.Handler = static _ => { };
            RemoveAs(e.Entity, mod);
        }
    }

    /// <summary>Once per step (Kiln's service tick): checks queued changes, drops offers whose entity or offer is gone.</summary>
    internal static void Step()
    {
        var w = World;
        foreach (var e in Entries.Values.ToList())
        {
            if (e.Pending.Count == 0) continue;
            var exists = w.Exists(e.Entity);
            for (var i = e.Pending.Count - 1; i >= 0; i--)
            {
                var c = e.Pending[i];
                if (exists && w.Has(e.Entity, c.Hash) == c.Add)
                {
                    if (c.Add) e.Added.Add(c.Hash);
                    else e.Added.Remove(c.Hash);
                    e.Pending.RemoveAt(i);
                }
                else if (!exists || ++c.Waited >= VerifySteps)
                {
                    e.Pending.RemoveAt(i);
                    if (c.Add && !e.Lost)
                    {
                        e.Lost = true;
                        Log(e, exists ? "did not land: the game did not apply it" : "did not land: the entity is gone");
                    }
                }
            }
            if (e.Pending.Count > 0) continue;
            if (!exists) Entries.Remove(e.Entity);
            else if (e.Lost || e.Removing)
            {
                e.Removing = true;
                if (e.RemovesSent) Entries.Remove(e.Entity);  // the removal round is over (what stayed is the game's)
                else
                {
                    e.RemovesSent = true;
                    SendRemoves(e);
                }
            }
        }
        if (++_steps < UpkeepSteps) return;
        _steps = 0;
        foreach (var e in Entries.Values.ToList())
        {
            if (e.Pending.Count > 0 || e.Removing || e.Lost) continue;
            var here = w.Exists(e.Entity);
            if (here && StillOurs(e.Entity, e.Spec)) continue;
            Entries.Remove(e.Entity);
            Log(e, here ? "dropped: its offer is not Kiln's now" : "dropped: the entity is gone");
        }
    }

    private static void Log(Entry e, string what)
    {
        var line = $"offer on entity {e.Entity} ({ModInvoke.Name(e.Owner)}) {what}";
        if (e.Owner != null) e.Owner.Log.Warn(line);
        else Backend.Current.Log(0, LogLevel.Warn, "kiln: " + line);
    }

    /// <summary>Kiln.Gameplay asks for INTERACTED events while any offer exists.</summary>
    internal static bool Wanted => Entries.Count > 0;

    /// <summary>An accepted interaction (the game's InteractionAcceptedEvent): runs the owner's handler.</summary>
    internal static void OnAccepted(uint guest, uint host)
    {
        if (guest == 0 || !Entries.TryGetValue(host, out var e) || e.Pending.Count > 0 || e.Removing || e.Lost) return;
        if (!StillOurs(host, e.Spec)) return;  // the id is another entity's now
        e.Accepts++;
        var h = e.Handler;
        ModInvoke.Run(e.Owner, () => h(new OfferAccepted(new EntityId(guest), new EntityId(host))), "an offer handler");
    }

    /// <summary>Test hook.</summary>
    internal static void Reset()
    {
        Entries.Clear();
        Watched.Clear();
        _tick?.Dispose();
        _tick = null;
        _steps = 0;
        _world = null;
    }
}

/// <summary>What the offer registry needs from the game (a fake in unit tests).</summary>
internal interface IOfferWorld
{
    bool Available { get; }
    bool Exists(uint entity);
    bool Has(uint entity, uint hash);
    bool IsPlayer(uint entity);
    /// <summary>InteractionOfferRuntime's (defaultSequence, defaultVerbId), or null when the entity has none.</summary>
    (uint Sequence, uint Verb)? ReadRuntime(uint entity);
    /// <summary>Queues an add (data) or a remove; 0 = queued, else the kiln error.</summary>
    int Change(ulong owner, uint entity, uint hash, byte[]? data, bool add);
}

/// <summary>kiln.world for reads, kiln.gameplay's component_change for writes.</summary>
internal sealed unsafe class NativeOfferWorld : IOfferWorld
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WorldTable  // the front of KilnWorldApi up to exists
    {
        public ServiceHeader Header;
        public nint RegisterProgram, ProgramSlot, ProgramInfo, IterBegin, IterNext, CurrentEntity, CommandBuffer,
            QueuePush, QueueAdd, QueueRemove, MarkWritten, ReadOther, ComponentHash;
        public delegate* unmanaged[Cdecl]<uint, uint, uint*, void*> Component;
        public delegate* unmanaged[Cdecl]<uint, byte> Exists;
    }

    private static readonly uint PlayerInput = Hash("PlayerInput");
    private WorldTable* _w;
    private bool _looked;

    private static uint Hash(string name)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.ASCII.GetBytes("keen::ecs::" + name)) h = (h ^ b) * 16777619u;
        return h;
    }

    private WorldTable* W
    {
        get
        {
            if (_looked) return _w;
            _looked = true;
            var w = Services.Get<WorldTable>("kiln.world", 1);
            if (w != null && w->Header.Size >= (uint)sizeof(WorldTable)) _w = w;
            return _w;
        }
    }

    private static delegate* unmanaged[Cdecl]<ulong, uint, uint, void*, uint, uint, int> ChangeFn =>
        GameplayNative.ComponentChange(GameplayNative.T);

    public bool Available => W != null && ChangeFn != null;
    public bool Exists(uint entity) => W != null && entity != 0 && W->Exists(entity) != 0;

    public bool Has(uint entity, uint hash)
    {
        if (W == null) return false;
        uint size = 0;
        return W->Component(entity, hash, &size) != null;
    }

    public bool IsPlayer(uint entity) => Has(entity, PlayerInput);

    public (uint Sequence, uint Verb)? ReadRuntime(uint entity)
    {
        if (W == null) return null;
        uint size = 0;
        var p = (byte*)W->Component(entity, Offers.InteractionOfferRuntime, &size);
        if (p == null || size < 20) return null;
        return (*(uint*)(p + 8), *(uint*)(p + 16));
    }

    public int Change(ulong owner, uint entity, uint hash, byte[]? data, bool add)
    {
        var f = ChangeFn;
        if (f == null) return 3;
        if (!add) return f(owner, entity, hash, null, 0, 2);
        fixed (byte* p = data) return f(owner, entity, hash, p, (uint)(data?.Length ?? 0), 1);
    }
}
