// SPDX-License-Identifier: MIT
// Kiln.Pets' runtime: the definitions, the service tick and the rows.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

internal static partial class PetsRuntime
{
    private static readonly object Gate = new();
    // by id: the definition, its mod's id, and that mod's context when it was the one running at Define (weak: never keeps
    // an unloaded mod alive)
    private static readonly Dictionary<string, (PetDefinition Definition, string? Mod, WeakReference<ModContext>? Context)> Defs =
        new(StringComparer.Ordinal);
    private static readonly Queue<KilnEvent> PendingEvents = new();
    private static IDisposable? _tick;
    private static int _tickGeneration = -1;

    private static readonly JsonSerializerOptions Canon = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Whether a definition's pets have a live body, an attempt or a revive running: then it cannot change.
    /// PetRoster answers; tests may replace it.</summary>
    internal static Func<string, bool> InUse = _ => false;

    internal static string CanonicalJson(PetDefinition d) => JsonSerializer.Serialize(d, Canon);

    internal static PetDefinitionResult Define(PetDefinition d, string? mod)
    {
        var levelMax = (int?)null;
        string? note = null;
        if (PetCreatures.IsBlock(d.Creature) || PetCreatures.Source.Ready)
        {
            var info = PetCreatures.Resolve(d.Creature, out var why);
            if (info is null) return new PetDefinitionResult(false, why, null);
            if (info.Problem is { } problem) return new PetDefinitionResult(false, problem, null);
            levelMax = info.LevelMax;
        }
        else note = "the game's creatures are not readable yet: the creature is checked when a body is first made";
        if (PetDefinitionRules.Check(d, levelMax) is { } bad) throw new ArgumentException($"pet {d.Id}, {bad.Field}: {bad.Problem}", nameof(d));
        PetDefinition? before;
        lock (Gate)
        {
            if (Defs.TryGetValue(d.Id, out var have))
            {
                if (CanonicalJson(have.Definition) == CanonicalJson(d)) return new PetDefinitionResult(true, null, have.Definition);
                if (InUse(d.Id))
                    return new PetDefinitionResult(false, $"DefinitionInUse: a pet of {d.Id} has a live body or a revive running; the definition changes once none has", have.Definition);
                before = have.Definition;
            }
            else before = null;
            var running = ModContext.CurrentMod is { } c && string.Equals(c.Info.Id, mod, StringComparison.OrdinalIgnoreCase)
                ? new WeakReference<ModContext>(c) : null;
            Defs[d.Id] = (d, mod, running ?? (before is not null && have.Mod == mod ? have.Context : null));
            PendingEvents.Enqueue(new PetDefinitionChangedEvent(before, d));
        }
        if (note is not null) Log(LogLevel.Info, $"Kiln.Pets: pet {d.Id}: {note}");
        // the defining mod unloads: its pets' bodies are retired (records and wishes kept; the definition goes dormant)
        if (ModContext.CurrentMod is { } owner && Watched.Add(owner)) owner.Retiring += () => ModUnloaded(owner.Info.Id);
        EnsureTick();
        return new PetDefinitionResult(true, null, d);
    }

    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);

    /// <summary>A mod unloaded: its definitions go (records of them stay dormant) and the roster retires their bodies.</summary>
    internal static void ModUnloaded(string mod)
    {
        List<string> ids;
        lock (Gate)
        {
            ids = Defs.Where(kv => string.Equals(kv.Value.Mod, mod, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (var id in ids) Defs.Remove(id);
        }
        if (ids.Count > 0) Roster?.Unload(ids);
    }

    /// <summary>The loaded mod that defined <paramref name="id"/>, or null (none is loaded, or no mod defined it). Kiln.Pets
    /// makes and orders that pet's bodies as this mod: Kiln.Creatures keeps every change a mod's (a layer it reverts when
    /// the mod unloads), and Kiln.Pets' own step runs no mod's code.</summary>
    internal static ModContext? DefiningMod(string id)
    {
        string mod;
        WeakReference<ModContext>? seen;
        lock (Gate)
        {
            if (!Defs.TryGetValue(id, out var d) || d.Mod is null) return null;
            (mod, seen) = (d.Mod, d.Context);
        }
        if (seen is not null && seen.TryGetTarget(out var known) && known.Alive) return known;
        if (ModContext.CurrentMod is { Alive: true } current && string.Equals(current.Info.Id, mod, StringComparison.OrdinalIgnoreCase))
            return current;
        try { return Mods.Get(mod)?.Info is { } info ? ModContext.FindLive(info) : null; }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { return null; }
    }

    /// <summary>Runs <paramref name="action"/> as <paramref name="mod"/> (tick thread), as Kiln.Blocks runs a creature
    /// block's brain; null: as whoever runs now.</summary>
    internal static void As(ModContext? mod, Action action)
    {
        var previous = ModContext.CurrentMod;
        if (mod is not null) ModContext.CurrentMod = mod;
        try { action(); }
        finally { ModContext.CurrentMod = previous; }
    }

    internal static PetDefinition? Definition(string id)
    {
        lock (Gate) return Defs.TryGetValue(id, out var d) ? d.Definition : null;
    }

    internal static IReadOnlyList<PetDefinition> AllDefinitions()
    {
        lock (Gate) return Defs.Values.Select(v => v.Definition).OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Defs.Clear();
            PendingEvents.Clear();
        }
        InUse = _ => false;
    }

    /// <summary>Events waiting for the tick (definitions may change off the tick thread, as a game client's OnLoad).</summary>
    internal static void Enqueue(KilnEvent ev)
    {
        lock (Gate) PendingEvents.Enqueue(ev);
        EnsureTick();
    }

    internal static void Start() => EnsureTick();

    private static void EnsureTick()
    {
        lock (Gate)
        {
            if (_tick is not null && _tickGeneration == ServiceTick.Generation) return;
            _tickGeneration = ServiceTick.Generation;
            _tick = ServiceTick.Add("kiln.pets", Tick);
        }
    }

    /// <summary>The service tick (start of each step, tick thread): the pets' clock, queued events, then the roster's step.</summary>
    private static void Tick(in TickInfo tick)
    {
        GamePetWorld.Advance(in tick);
        DrainEvents();
        if (Roster is null && Lifecycle.IsServerRunning && !_testOwned)
        {
            Roster = MakeRoster(new GamePetWorld(), new GamePetStore());
            Roster.Mover = new GamePetMover();
        }
        Roster?.Step();
        if (Roster is not null && !_testOwned) Publish(Roster);
        DrainEvents();
    }

    internal static void DrainEvents()
    {
        while (true)
        {
            KilnEvent ev;
            lock (Gate)
            {
                if (PendingEvents.Count == 0) return;
                ev = PendingEvents.Dequeue();
            }
            try { Events.Post(ev); }
            catch (Exception e) { Log(LogLevel.Warn, $"Kiln.Pets: a handler of {ev.GetType().Name} threw: {e.Message}"); }
        }
    }

    /// <summary>Tests: takes the queued events without posting them.</summary>
    internal static void DrainEventsForTests(out List<KilnEvent> events)
    {
        lock (Gate)
        {
            events = [.. PendingEvents];
            PendingEvents.Clear();
        }
    }

    internal static void Log(LogLevel level, string text)
    {
        try { Backend.Current.Log(0, level, text); }
        catch (Exception) { /* no backend (tests without a kit) */ }
    }
}

internal static partial class PetsRuntime
{
    internal const string ServiceName = "kiln.pets";

    /// <summary>The world's pets (server side, once a server world runs); null on a joined client.</summary>
    internal static PetRoster? Roster { get; private set; }
    private static bool _testOwned;

    /// <summary>Tests that drive a roster without a capability registry: every row counts as on.</summary>
    internal static bool TestRows;

    /// <summary>Wires the packages' parts onto a roster (the broker, the follower, the guard, the owners' view).</summary>
    internal static Action<PetRoster>? Wire;

    /// <summary>The owners' view publisher of the world's roster.</summary>
    internal static PetViewPublisher Publisher { get; private set; } = new();

    private static void Publish(PetRoster r)
    {
        if (PetsClient.Channel is not { } channel || !Network.Available) return;
        // a game without Kiln, or without kiln:pets, never takes a view: none is built for it
        Publisher.Step(r, r.World.OnlineOwners(), r.World.WorldEpoch, r.World.Now,
            (owner, bytes) => Network.PeerOf(owner.Entity.Id) is { State: NetPeerState.Ready } peer && channel.SendRaw(peer, PetViewCodec.ViewMessage, bytes),
            owner => Network.PeerOf(owner.Entity.Id) is { State: NetPeerState.Ready } peer && peer.HasChannel(channel.Name));
    }

    private static PetRoster MakeRoster(IPetWorld world, IPetStore store)
    {
        var r = new PetRoster(world, store);
        Publisher = new PetViewPublisher();
        r.Changes += p => Publisher.MarkDirty(p.Owner);
        InUse = id => Roster?.DefinitionInUse(id) ?? false;
        Wire?.Invoke(r);
        return r;
    }

    /// <summary>Tests: a roster on a fake world and store (no game tick drives it; the test calls Step).</summary>
    internal static PetRoster UseForTests(IPetWorld world, IPetStore store)
    {
        Roster?.Detach();
        _testOwned = true;
        Roster = MakeRoster(world, store);
        return Roster;
    }

    internal static void StopForTests()
    {
        Roster?.Detach();
        Roster = null;
        _testOwned = false;
    }

    /// <summary>The world's id (anchors remember it).</summary>
    internal static string? WorldId
    {
        get
        {
            try { return Saves.WorldId; }
            catch (Exception) { return null; }
        }
    }

    /// <summary>The rows besides kiln.pets and pets.definitions.</summary>
    internal static void ReportRows()
    {
        var server = Lifecycle.IsServerRunning;
        if (Roster is not null || server)
            ServiceCaps.Report(Pets.Caps.Records, CapabilityState.Available);
        else
            ServiceCaps.Report(Pets.Caps.Records, CapabilityState.Unavailable, CapabilityReason.WrongSide, "pet records live on the server (no server world here)");
        var world = Roster?.World;
        var why = "";
        if (world is not null && world.CanMakeBodies(out why)) ServiceCaps.Report(Pets.Caps.Summon, CapabilityState.Available);
        else ServiceCaps.Report(Pets.Caps.Summon, CapabilityState.Unavailable, server ? CapabilityReason.Session : CapabilityReason.WrongSide,
            world is not null ? why : "bodies are made on the server");
        ReportLaterRows();
    }

    static partial void ReportLaterRows();

    private static unsafe bool PetsNativeAvailable()
    {
        try { return PetsNative.T != null; }
        catch (Exception) { return false; }
    }
}

internal static partial class PetsRuntime
{
    /// <summary>The rows of mechanisms that are not in this Kiln yet say so (each package fills its own).</summary>
    static partial void ReportLaterRows()
    {
        var control = Capabilities.Get("creature_control");
        if (control.State == CapabilityState.Available) ServiceCaps.Report(Pets.Caps.Control, CapabilityState.Available);
        else ServiceCaps.Report(Pets.Caps.Control, CapabilityState.Unavailable, control.Reason == CapabilityReason.None ? CapabilityReason.ServiceMissing : control.Reason,
            $"needs creature_control: {control.ReasonText}");
        var server = Lifecycle.IsServerRunning;
        var native = PetsNativeAvailable();
        var follow = Capabilities.Get(Pets.Caps.Control).State == CapabilityState.Available;
        ServiceCaps.Report(Pets.Caps.Follow, follow ? CapabilityState.Available : CapabilityState.Unavailable,
            follow ? CapabilityReason.None : CapabilityReason.ServiceMissing, follow ? "" : "needs pets.control");
        if (native && Capabilities.Is("kiln.combat")) ServiceCaps.Report(Pets.Caps.Fight, CapabilityState.Available);
        else ServiceCaps.Report(Pets.Caps.Fight, CapabilityState.Unavailable, CapabilityReason.ServiceMissing,
            native ? "needs kiln.combat (its hit seam asks the pets' target rule)" : "needs kiln.pets' native safety module (kiln-creature-control.dll of dev.32)");
        var guards = Capabilities.Get("decisions.native_guards");
        if (native && guards.State != CapabilityState.Unavailable) ServiceCaps.Report(Pets.Caps.Downed, CapabilityState.Available);
        else ServiceCaps.Report(Pets.Caps.Downed, CapabilityState.Unavailable, native ? guards.Reason : CapabilityReason.ServiceMissing,
            native ? $"needs decisions.native_guards: {guards.ReasonText}" : "needs kiln.pets' native safety module");
        var net = Network.Available && PetsClient.Channel is not null;
        if (server && net) ServiceCaps.Report(Pets.Caps.HudServer, CapabilityState.Available);
        else ServiceCaps.Report(Pets.Caps.HudServer, CapabilityState.Unavailable, server ? CapabilityReason.ServiceMissing : CapabilityReason.WrongSide,
            server ? "needs Kiln.Network (kiln.net) for the channel kiln:pets" : "owners' views are sent by the server");
        if (net && Render.Available) ServiceCaps.Report(Pets.Caps.HudClient, CapabilityState.Available);
        else ServiceCaps.Report(Pets.Caps.HudClient, CapabilityState.Unavailable, CapabilityReason.ServiceMissing,
            net ? "needs the renderer (kiln.render) for the bar" : "needs Kiln.Network (kiln.net) for the channel kiln:pets");
    }
}
