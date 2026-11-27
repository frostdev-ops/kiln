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
/// <para>Requests: every action carries a <see cref="TamingRequest"/>, the one request shape of Kiln.Pets and Kiln.Taming:
/// the caller's request id (new for each intended action, the same for a network retry) and the token the last answer
/// gave (<see cref="TamingProgress.Token"/>: the server's challenge, the revision, the step). The same request again is
/// the same answer and never a second cost; another action under a used id is <c>RequestConflict</c>; an old token under
/// a new id is <c>StaleStep</c>.</para>
/// <para>Error rule: a bad argument throws <see cref="ArgumentException"/>; an off capability throws
/// <see cref="KilnUnavailableException"/> naming it; a refusal is <c>Ok = false</c> with its reason and receipts, and the
/// reason's first word as a value, <see cref="TamingResult.Refusal"/> (<see cref="TamingRefusal"/>: the same words as
/// Kiln.Pets' <see cref="PetRefusal"/> for the same meanings). Decide by it, never by the text.</para>
/// </remarks>
[ServerOnly]
public static class Taming
{
    /// <summary>The capability of Kiln's taming steps.</summary>
    public const string Capability = "taming.steps";

    /// <summary>True when attempts can run here (a server).</summary>
    public static bool Available => TamingCaps.StepsOn();

    /// <summary>Registers a taming as the calling mod's. The same definition again is fine; another one under the same id
    /// replaces it unless attempts of it run (<c>DefinitionInUse</c>, an <see cref="InvalidOperationException"/>). A
    /// definition is data: a mod declares it when it loads, before any world runs (only the attempts need one); it holds
    /// for every world of the process until the mod unloads.</summary>
    /// <exception cref="ArgumentException">A value is out of its range, or the id is not the calling mod's
    /// (<c>&lt;mod id&gt;:&lt;name&gt;</c>).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, the id is another mod's, or attempts of it run.</exception>
    [TickThreadOnly]
    public static void Define(TamingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Services.RequireTickThread("Taming.Define");
        TamingCaps.Require(TamingCaps.Service);
        var mod = ModContext.CurrentMod?.Info.Id;
        if (mod is not null && !definition.Id.StartsWith(mod.ToLowerInvariant() + ":", StringComparison.Ordinal))
            throw new ArgumentException($"taming definition ids of mod {mod} start with \"{mod.ToLowerInvariant()}:\", not \"{definition.Id}\"", nameof(definition));
        TamingRuntime.Define(definition, mod ?? "kiln");
    }

    /// <summary>Reads tamings from JSON (one definition, or an array of them; docs/kiln/modding/taming.md) and registers
    /// each as <see cref="Define"/> does. Item ids are the items registry's (<c>game:material_t1_wood_logs</c>) or an item
    /// id (<c>0xb59dfc2c</c>); creatures are creatures registry ids.</summary>
    /// <returns>The definitions registered.</returns>
    /// <exception cref="ArgumentException">The JSON is not a taming, with the path of the first field that is wrong (an item
    /// id the game's items, not readable yet or not on this build, do not have: <c>ItemUnknown</c>; a mod's own items are
    /// named by a <c>TamingBlock</c> of Kiln.Blocks).</exception>
    [TickThreadOnly]
    public static IReadOnlyList<TamingDefinition> DefineJson(string json)
    {
        var defs = TamingJson.Read(json, TamingJson.GameItems);
        foreach (var d in defs) Define(d);
        return defs;
    }

    /// <summary>The definition registered under <paramref name="id"/>, or null.</summary>
    [TickThreadOnly]
    public static TamingDefinition? Definition(string id) => DefinitionEngine("Taming.Definition").Definition(id);

    /// <summary>Every registered definition.</summary>
    [TickThreadOnly]
    public static IReadOnlyList<TamingDefinition> Definitions => DefinitionEngine("Taming.Definitions").Definitions;

    /// <summary>Starts an attempt of <paramref name="definition"/> by <paramref name="player"/> on
    /// <paramref name="target"/>: the target is claimed (another attempt on it is <c>Busy</c>), the pet's roster slot is held
    /// (a definition with <see cref="TamingDefinition.Pet"/>), and the answer's progress carries the first token.</summary>
    /// <returns>A task, as <see cref="Advance"/>, <see cref="Retry"/> and <see cref="Cancel"/> answer (a begin takes no cost, so
    /// it is complete when it returns): Ok with the attempt; else (<see cref="TamingResult.Refusal"/>) <c>DefinitionUnknown</c>,
    /// <c>OwnerOffline</c>, <c>BodyGone</c>, <c>TemplateMismatch</c>, <c>OutOfRange</c>, <c>Busy</c>, <c>Capacity</c>,
    /// <c>RefusedByMod</c> (a mod's <see cref="TamingBeginningEvent"/>), <c>TooManyRequests</c>.</returns>
    /// <exception cref="ArgumentException">No player, no target, an empty definition id, an empty request id, or a request
    /// with a token (a begin expects no step: <c>TamingRequest.New()</c>).</exception>
    /// <exception cref="KilnUnavailableException">A capability the definition's steps need is off (taming.steps,
    /// items.costs.exact for feed steps, items.costs.wear for tool steps with wear, taming.native for native steps,
    /// pets.records for a definition that names a pet).</exception>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static Task<TamingResult> Begin(PlayerRef player, EntityRef target, string definition, TamingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Token is not null) throw new ArgumentException("a begin expects no step: TamingRequest.New()", nameof(request));
        var e = Engine("Taming.Begin");
        if (e.Definition(definition) is { } d) TamingCaps.RequireFor(d);
        // a begin settles at once (it takes no cost): the task is completed, as every other action's answer is a task
        return Task.FromResult(e.Begin(player, target, definition, request.RequestId));
    }

    /// <summary>Takes the attempt's next step: checks the request's token, the range, the target and the weaken threshold, asks the
    /// mods (<see cref="TamingAdvancingEvent"/>, and <see cref="TamingCommittingEvent"/> before the last step's cost), takes
    /// the step's exact cost from <paramref name="selection"/> (a feed step: a plain stack of its item; a tool step: the
    /// gear item; weaken and native steps: null), and applies it. The last step tames: the answer is Committed, or
    /// <c>CommitDeferred</c> (every step paid, the tame waits for <see cref="Retry"/>).</summary>
    /// <returns>The answer when the cost settled (the step after it ran); its receipts say what was taken.</returns>
    /// <exception cref="ArgumentException">An empty request id, a request without the token of the last answer, or a selection
    /// of the wrong shape for the step.</exception>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static Task<TamingResult> Advance(PlayerRef player, TameAttemptId attempt, TamingRequest request, ItemSelection? selection = null) =>
        Engine("Taming.Advance").Advance(player, attempt, request, selection);

    /// <summary>The cost-free retry of a deferred tame (<see cref="TamingState.AwaitingCommit"/>): checks again and binds,
    /// taking nothing.</summary>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static Task<TamingResult> Retry(PlayerRef player, TameAttemptId attempt, TamingRequest request) =>
        Engine("Taming.Retry").Retry(player, attempt, request);

    /// <summary>Ends the attempt. A cost in flight settles first (its receipt says whether the cancel spent it). Paid steps
    /// are given back only as the definition's refund policy says (<see cref="TamingRefundPolicy"/>).</summary>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static Task<TamingResult> Cancel(PlayerRef player, TameAttemptId attempt, TamingRequest request) =>
        Engine("Taming.Cancel").Cancel(player, attempt, request);

    /// <summary>The attempt as it is now; null once it is gone (10 minutes of simulation after it closed).</summary>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static TamingProgress? Inspect(TameAttemptId attempt) => Engine("Taming.Inspect").Inspect(attempt);

    /// <summary>The slots of the owner's inventories that can pay the next step, as selections the server read, with the
    /// token of the view they were read on (<see cref="TamingChoicesResult.Token"/>): the first 256, and
    /// <see cref="TamingChoicesResult.Truncated"/> when more could pay (never a refusal for many slots). A read only: it
    /// refreshes no timeout.</summary>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static TamingChoicesResult Choices(PlayerRef player, TameAttemptId attempt) => Engine("Taming.Choices").Choices(player, attempt);

    // the definitions are data: Kiln.Taming loaded is all they need (a mod's OnLoad, no world yet)
    private static TamingEngine DefinitionEngine(string api)
    {
        Services.RequireTickThread(api);
        TamingCaps.Require(TamingCaps.Service);
        return TamingRuntime.Engine;
    }

    private static TamingEngine Engine(string api)
    {
        Services.RequireTickThread(api);
        TamingCaps.Require(Capability);
        return TamingRuntime.Engine;
    }
}
