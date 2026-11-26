// SPDX-License-Identifier: MIT
// Kiln.Pets' runtime: the definitions, the service tick and the rows.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

internal static partial class PetsRuntime
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (PetDefinition Definition, string? Mod)> Defs = new(StringComparer.Ordinal);
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
            Defs[d.Id] = (d, mod);
            PendingEvents.Enqueue(new PetDefinitionChangedEvent(before, d));
        }
        if (note is not null) Log(LogLevel.Info, $"Kiln.Pets: pet {d.Id}: {note}");
        EnsureTick();
        return new PetDefinitionResult(true, null, d);
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

    /// <summary>The service tick (start of each step, tick thread): queued events, then the roster's step.</summary>
    private static void Tick(in TickInfo tick)
    {
        DrainEvents();
        if (Roster is null && Lifecycle.IsServerRunning && !_testOwned) Roster = MakeRoster(new GamePetWorld(), new GamePetStore());
        Roster?.Step();
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

    private static PetRoster MakeRoster(IPetWorld world, IPetStore store)
    {
        var r = new PetRoster(world, store);
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
        foreach (var row in new[] { Pets.Caps.Follow, Pets.Caps.Fight, Pets.Caps.Downed, Pets.Caps.HudServer, Pets.Caps.HudClient })
            ServiceCaps.Report(row, CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "not in this Kiln yet");
    }
}
