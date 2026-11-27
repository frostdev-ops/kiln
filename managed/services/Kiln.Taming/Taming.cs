// SPDX-License-Identifier: MIT
// Kiln.Taming's face (dossier 19e 5.1): a mod declares a taming (TamingDefinition, in C# or JSON), and drives attempts on
// the server's tick thread: Begin, Advance (one step, its cost taken exactly from the slot the player selected), Retry (the
// cost-free retry of a tame that was deferred), Cancel, Inspect and Choices. Kiln ships the mechanism; every policy (what a
// step costs, refunds, timeouts, which creatures, the pet it makes) is the definition's. docs/kiln/services/taming.md.
namespace Kiln;

/// <summary>
/// Kiln's taming steps: a server-authoritative attempt per target, its steps (feed, weaken, use a tool, the game's own
/// native step) taken in order with exact costs and receipts, replay-safe requests, before-events a mod may cancel and
/// after-events, timeouts in simulation time, and the definition's refund policy at the end. A tame of a definition that
/// names a pet makes a Kiln.Pets record. Since 0.2.0-dev.32.
/// </summary>
/// <remarks>
/// <para>Requests: every action carries the caller's request id (new for each intended action, the same for a network
/// retry) and the token the last answer gave (<see cref="TamingToken"/>: the server's challenge, the revision, the step).
/// The same request again is the same answer and never a second cost; another action under a used id is
/// <c>RequestConflict</c>; an old token under a new id is <c>StaleStep</c>.</para>
/// <para>Error rule: a bad argument throws <see cref="ArgumentException"/>; an off capability throws
/// <see cref="KilnUnavailableException"/> naming it; a refusal is <c>Ok = false</c> with its reason and receipts.</para>
/// </remarks>
[ServerOnly]
public static class Taming
{
    /// <summary>The capability of Kiln's taming steps.</summary>
    public const string Capability = "taming.steps";

    /// <summary>True when attempts can run here (a server).</summary>
    public static bool Available => TamingCaps.StepsOn();

    /// <summary>Registers a taming as the calling mod's. The same definition again is fine; another one under the same id
    /// replaces it unless attempts of it run (<c>DefinitionInUse</c>, an <see cref="InvalidOperationException"/>).</summary>
    /// <exception cref="ArgumentException">A value is out of its range, or the id is not the calling mod's
    /// (<c>&lt;mod id&gt;:&lt;name&gt;</c>).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, the id is another mod's, or attempts of it run.</exception>
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static void Define(TamingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Services.RequireTickThread("Taming.Define");
        TamingCaps.Require(Capability);
        var mod = ModContext.CurrentMod?.Info.Id;
        if (mod is not null && !definition.Id.StartsWith(mod.ToLowerInvariant() + ":", StringComparison.Ordinal))
            throw new ArgumentException($"taming definition ids of mod {mod} start with \"{mod.ToLowerInvariant()}:\", not \"{definition.Id}\"", nameof(definition));
        TamingRuntime.Define(definition, mod ?? "kiln");
    }

    /// <summary>Reads tamings from JSON (one definition, or an array of them; docs/kiln/modding/taming.md) and registers
    /// each as <see cref="Define"/> does. Item ids are the items registry's (<c>game:material_t1_wood_logs</c>) or an item
    /// id (<c>0xb59dfc2c</c>); creatures are creatures registry ids.</summary>
    /// <returns>The definitions registered.</returns>
    /// <exception cref="ArgumentException">The JSON is not a taming, with the path of the first field that is wrong.</exception>
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static IReadOnlyList<TamingDefinition> DefineJson(string json)
    {
        var defs = TamingJson.Read(json, TamingJson.GameItems);
        foreach (var d in defs) Define(d);
        return defs;
    }

    /// <summary>The definition registered under <paramref name="id"/>, or null.</summary>
    [TickThreadOnly]
    public static TamingDefinition? Definition(string id) => Engine("Taming.Definition").Definition(id);

    /// <summary>Every registered definition.</summary>
    [TickThreadOnly]
    public static IReadOnlyList<TamingDefinition> Definitions => Engine("Taming.Definitions").Definitions;

    /// <summary>Starts an attempt of <paramref name="definition"/> by <paramref name="player"/> on
    /// <paramref name="target"/>: the target is claimed (another attempt on it is <c>Busy</c>), the pet's roster slot is held
    /// (a definition with <see cref="TamingDefinition.Pet"/>), and the answer's progress carries the first token.</summary>
    /// <returns>Ok with the attempt; else <c>DefinitionUnknown</c>, <c>WrongOwner</c>, <c>BodyGone</c>,
    /// <c>TemplateMismatch</c>, <c>OutOfRange</c>, <c>Busy</c>, <c>Cap</c>, <c>Canceled</c> (a mod),
    /// <c>TooManyRequests</c>.</returns>
    /// <exception cref="ArgumentException">No player, no target, an empty definition id or an empty request id.</exception>
    /// <exception cref="KilnUnavailableException">A capability the definition's steps need is off (taming.steps,
    /// items.costs.exact for feed steps, items.costs.wear for tool steps with wear, taming.native for native steps,
    /// pets.records for a definition that names a pet).</exception>
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static TamingResult Begin(PlayerRef player, EntityRef target, string definition, Guid requestId)
    {
        var e = Engine("Taming.Begin");
        if (e.Definition(definition) is { } d) TamingCaps.RequireFor(d);
        return e.Begin(player, target, definition, requestId);
    }

    /// <summary>Takes the attempt's next step: checks the token, the range, the target and the weaken threshold, asks the
    /// mods (<see cref="TamingAdvancingEvent"/>, and <see cref="TamingCommittingEvent"/> before the last step's cost), takes
    /// the step's exact cost from <paramref name="selection"/> (a feed step: a plain stack of its item; a tool step: the
    /// gear item; weaken and native steps: null), and applies it. The last step tames: the answer is Committed, or
    /// <c>CommitDeferred</c> (every step paid, the tame waits for <see cref="Retry"/>).</summary>
    /// <returns>The answer when the cost settled (the step after it ran); its receipts say what was taken.</returns>
    /// <exception cref="ArgumentException">An empty request id, or a selection of the wrong shape for the step.</exception>
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static Task<TamingResult> Advance(PlayerRef player, TameAttemptId attempt, TamingToken token, ItemSelection? selection = null) =>
        Engine("Taming.Advance").Advance(player, attempt, token, selection);

    /// <summary>The cost-free retry of a deferred tame (<see cref="TamingState.AwaitingCommit"/>): checks again and binds,
    /// taking nothing.</summary>
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static Task<TamingResult> Retry(PlayerRef player, TameAttemptId attempt, TamingToken token) =>
        Engine("Taming.Retry").Retry(player, attempt, token);

    /// <summary>Ends the attempt. A cost in flight settles first (its receipt says whether the cancel spent it). Paid steps
    /// are given back only as the definition's refund policy says (<see cref="TamingRefundPolicy"/>).</summary>
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static Task<TamingResult> Cancel(PlayerRef player, TameAttemptId attempt, TamingToken token) =>
        Engine("Taming.Cancel").Cancel(player, attempt, token);

    /// <summary>The attempt as it is now; null once it is gone (10 minutes of simulation after it closed).</summary>
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static TamingProgress? Inspect(TameAttemptId attempt) => Engine("Taming.Inspect").Inspect(attempt);

    /// <summary>The slots of the owner's inventories that can pay the next step (at most 256), as selections the server
    /// read, with the current revision and challenge. A read only: it refreshes no timeout.</summary>
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static TamingChoicesResult Choices(PlayerRef player, TameAttemptId attempt) => Engine("Taming.Choices").Choices(player, attempt);

    private static TamingEngine Engine(string api)
    {
        Services.RequireTickThread(api);
        TamingCaps.Require(Capability);
        return TamingRuntime.Engine;
    }
}
