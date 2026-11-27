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
/// <para>Where it runs: server only, on the tick thread (<see cref="TickThreadOnlyAttribute"/>). Every call checks the thread
/// and throws <see cref="InvalidOperationException"/> off it (<see cref="Definition"/> and <see cref="Definitions"/> read on
/// any thread). A joined client has no attempts to run: send the player's choice to the server over a channel and call
/// <see cref="Taming"/> there. <see cref="Define"/> is data and may run when a mod loads; everything else needs a running
/// world.</para>
/// <para>Timing: <see cref="Begin"/> answers at once (its task is complete when it returns: a begin takes no cost).
/// <see cref="Advance"/>, <see cref="Retry"/> and <see cref="Cancel"/> return a <see cref="Task{TResult}"/> that completes at
/// a later server step, when the step's cost has settled (and the tame has bound its pet). Await it from mod code on the
/// tick thread: the continuation comes back to the tick thread. While a step is being paid the attempt is
/// <see cref="TamingState.CostPending"/> and another request for it is <c>Busy</c>. Time limits count simulation time,
/// which stands still while the world is paused.</para>
/// <para>Requests: every action carries a <see cref="TamingRequest"/>, the one request shape of Kiln.Pets and Kiln.Taming:
/// the caller's request id (new for each intended action, the same for a network retry) and the token the last answer
/// gave (<see cref="TamingProgress.Token"/>: the server's challenge, the revision, the step). The same request again is
/// the same answer and never a second cost; another action under a used id is <c>RequestConflict</c>; an old token under
/// a new id is <c>StaleStep</c>. A player may make 10 requests a second and an attempt keeps 256 request ids
/// (<c>TooManyRequests</c>).</para>
/// <para>Errors: a bad argument throws <see cref="ArgumentException"/>; an off capability throws
/// <see cref="KilnUnavailableException"/> naming it; a refusal is <c>Ok = false</c> with its reason and receipts, and a refusal
/// changes nothing the reason does not say. The reason's first word is a value, <see cref="TamingResult.Refusal"/>
/// (<see cref="TamingRefusal"/>: the same words as Kiln.Pets' <see cref="PetRefusal"/> for the same meanings). Decide by
/// it, never by the text.</para>
/// </remarks>
/// <example>
/// The whole cycle for a taming that weakens a target and then feeds it, from a command handler:
/// <code>
/// // 1. start: the player must stand within the definition's range of the target
/// TamingResult r = await Taming.Begin(player, target, "my-mod:wolf", TamingRequest.New());
/// if (!r.Ok) { Log.Info($"not started: {r.Reason}"); return; }
/// TamingProgress p = r.Progress!;
///
/// // 2. a step that takes no item (the weaken step): the token comes from the last progress
/// r = await Taming.Advance(player, p.Id, TamingRequest.New(p.Token));
/// p = r.Progress ?? p;
///
/// // 3. a step that takes an item: ask which slots can pay it, then pick one (a menu would let the player choose)
/// var choices = Taming.Choices(player, p.Id);
/// if (choices.Ok &amp;&amp; choices.Choices.Count > 0)
///     r = await Taming.Advance(player, p.Id, TamingRequest.New(choices.Token), choices.Choices[0].Selection);
/// if (r.Ok &amp;&amp; r.Progress?.State == TamingState.Committed) Log.Info($"tamed: {r.Pet?.Label}");
/// </code>
/// </example>
/// <seealso cref="TamingDefinition"/>
/// <seealso cref="NativeTaming"/>
/// <seealso cref="Pets"/>
/// <seealso cref="ItemCosts"/>
/// <seealso href="https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/services/taming.md">Taming service</seealso>
/// <seealso href="https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/taming.md">Taming: the modder's guide</seealso>
[ServerOnly]
public static class Taming
{
    /// <summary>The capability of Kiln's taming steps: <c>"taming.steps"</c>. Calls that run attempts need it; a definition that
    /// names a pet, feed or tool steps with wear, or native steps needs the capabilities of those services too (see
    /// <see cref="Begin"/>). Ask with <c>Capabilities.Is(Taming.Capability)</c> before offering taming to a player.</summary>
    public const string Capability = "taming.steps";

    /// <summary>True when attempts can run here: the capability <see cref="Capability"/> is on, which is while a server world
    /// runs. False on a joined client, and on a server before its world starts. Any thread.</summary>
    public static bool Available => TamingCaps.StepsOn();

    /// <summary>Registers a taming as the calling mod's, and answers as <see cref="Pets.Define"/> does. The same definition
    /// again is Ok; another one under the same id replaces it unless attempts of it run. A definition is data: a mod
    /// declares it when it loads, before any world runs (only the attempts need one); it holds for every world of the
    /// process until the mod unloads. Kiln keeps its own copy: changing the lists you passed afterwards changes nothing.</summary>
    /// <param name="definition">The taming: its id starts with the calling mod's id and a colon, it names 1 to 64 steps and at
    /// least one creature, and every number is in its range (see <see cref="TamingDefinition"/>).</param>
    /// <returns>Ok with the registered definition; else Ok false: <c>DefinitionInUse</c> (attempts of it run, or the id is
    /// another mod's), with the definition that stays.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">A value is out of its range, or the id is not the calling mod's
    /// (<c>&lt;mod id&gt;:&lt;name&gt;</c>).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// TamingDefinitionResult d = Taming.Define(new TamingDefinition
    /// {
    ///     Id = "my-mod:dog",
    ///     Creatures = ["game:animal_wildlife_dog_*"],        // a trailing * matches a prefix
    ///     Steps = [new NativeStep(1), new NativeStep(1)],     // the game's own taming, counted by Kiln
    ///     InactivityTimeout = TimeSpan.FromMinutes(2), MaxAttemptDuration = TimeSpan.FromMinutes(10),
    /// });
    /// if (!d.Ok) Log.Warn($"taming not defined: {d.Reason}");
    /// </code></example>
    /// <seealso cref="DefineJson"/>
    /// <seealso cref="TamingDefinition"/>
    [TickThreadOnly]
    public static TamingDefinitionResult Define(TamingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Services.RequireTickThread("Taming.Define");
        TamingCaps.Require(TamingCaps.Service);
        var mod = ModContext.CurrentMod?.Info.Id;
        if (mod is not null && !definition.Id.StartsWith(mod.ToLowerInvariant() + ":", StringComparison.Ordinal))
            throw new ArgumentException($"taming definition ids of mod {mod} start with \"{mod.ToLowerInvariant()}:\", not \"{definition.Id}\"", nameof(definition));
        return TamingRuntime.Define(definition, mod ?? "kiln");
    }

    /// <summary>Reads tamings from JSON (one definition, or an array of them; docs/kiln/modding/taming.md) and registers
    /// each as <see cref="Define"/> does. Item ids are the items registry's (<c>game:material_t1_wood_logs</c>) or an item
    /// id (<c>0xb59dfc2c</c>); creatures are creatures registry ids. Item ids resolve against the game's items when the JSON is
    /// read, so call it once <c>Items.Available</c> is true (the game's items are readable); a mod's own items are named by a
    /// <c>TamingBlock</c> of Kiln.Blocks, or in C# by their <see cref="ItemId"/>.</summary>
    /// <param name="json">One taming object, or an array of them. Fields: <c>id</c>, <c>creatures</c>, <c>steps</c>
    /// (each with a <c>kind</c> of <c>feed</c>, <c>weaken</c>, <c>tool</c> or <c>native</c>), and the optional
    /// <c>rangeMeters</c>, <c>requireThresholdUntilCommit</c>, <c>inactivitySeconds</c>, <c>maxAttemptSeconds</c>,
    /// <c>refund</c> (<c>nonRefundable</c>, <c>refundOnFail</c> or <c>escrow</c>), <c>pet</c>, <c>removeTarget</c> and
    /// <c>summonPet</c>. A field that is not in this list is an error.</param>
    /// <returns>The answer of each definition, in the JSON's order.</returns>
    /// <exception cref="ArgumentException">The JSON is not a taming, with the path of the first field that is wrong (an item
    /// id the game's items, not readable yet or not on this build, do not have: <c>ItemUnknown</c>; a mod's own items are
    /// named by a <c>TamingBlock</c> of Kiln.Blocks). The whole JSON is read and checked before the first definition is registered;
    /// one that <see cref="Define"/> then refuses (an id with another mod's prefix) leaves the ones before it registered.</exception>
    /// <exception cref="InvalidOperationException">As <see cref="Define"/>: not on the tick thread.</exception>
    /// <example><code>
    /// Taming.DefineJson("""
    ///     { "id": "my-mod:wolf", "creatures": ["game:enemy_wildbeast_wolf_earlygame"],
    ///       "steps": [ { "kind": "weaken", "maxHealthFraction": 0.4 },
    ///                  { "kind": "feed", "item": "game:food_t1_raw_bad_meat_leanmeat", "count": 2 } ] }
    ///     """);
    /// </code></example>
    /// <seealso cref="Define"/>
    [TickThreadOnly]
    public static IReadOnlyList<TamingDefinitionResult> DefineJson(string json)
    {
        var defs = TamingJson.Read(json, TamingJson.GameItems);
        var results = new List<TamingDefinitionResult>(defs.Count);
        foreach (var d in defs) results.Add(Define(d));
        return results;
    }

    /// <summary>The definition registered under <paramref name="id"/>, or null. Any thread, as Kiln.Pets'
    /// <see cref="Pets.Definition"/>.</summary>
    /// <param name="id">The definition's id (<c>my-mod:wolf</c>).</param>
    /// <returns>Kiln's copy of the registered definition, or null when none has that id (or its mod unloaded).</returns>
    /// <seealso cref="Definitions"/>
    [AnyThread]
    public static TamingDefinition? Definition(string id) => DefinitionReader().Definition(id);

    /// <summary>Every registered definition, of every mod, in no particular order. Any thread.</summary>
    /// <seealso cref="Definition"/>
    [AnyThread]
    public static IReadOnlyList<TamingDefinition> Definitions => DefinitionReader().Definitions;

    /// <summary>Starts an attempt of <paramref name="definition"/> by <paramref name="player"/> on
    /// <paramref name="target"/>: the target is claimed (another attempt on it is <c>Busy</c>), the pet's roster slot is held
    /// (a definition with <see cref="TamingDefinition.Pet"/>), and the answer's progress carries the first token.</summary>
    /// <param name="player">The player who tames: in game, within the definition's <see cref="TamingDefinition.RangeMeters"/> of
    /// the target. They own the attempt: only they may advance, retry or cancel it.</param>
    /// <param name="target">The creature to tame, as an <see cref="EntityRef"/> (<c>EntityRef.Of(entityId)</c>). It must be
    /// alive, match one of the definition's creatures, and not be claimed by another attempt.</param>
    /// <param name="definition">The id of a registered definition (<see cref="Define"/>).</param>
    /// <param name="request">A new request for every intended begin, without a token (<c>TamingRequest.New()</c>: a begin
    /// expects no step); the same one again (a network retry) gives the first answer back and starts nothing new.</param>
    /// <returns>A task, as <see cref="Advance"/>, <see cref="Retry"/> and <see cref="Cancel"/> answer (a begin takes no cost, so
    /// it is complete when it returns): Ok with the attempt (<see cref="TamingResult.Progress"/>; its
    /// <see cref="TamingProgress.Token"/> is the first token); else (<see cref="TamingResult.Refusal"/>)
    /// <c>DefinitionUnknown</c>, <c>OwnerOffline</c>, <c>BodyGone</c>, <c>TemplateMismatch</c> (the target is not one of the
    /// definition's creatures, or a native step has no tameable wild animal), <c>OutOfRange</c>, <c>Busy</c> (another
    /// attempt claims the target), <c>Capacity</c> (512 attempts run in this world, or the character has no room for the pet:
    /// <see cref="PetLimits.RetainedPerCharacter"/>), <c>RefusedByMod</c> (a mod's <see cref="TamingBeginningEvent"/>),
    /// <c>RequestConflict</c>, <c>TooManyRequests</c>, and, for a definition that names a pet, <c>NoCharacter</c> (the player's
    /// character is not known: account-only players cannot own pets) or <c>DefinitionUnknown</c> (no such pet
    /// definition).</returns>
    /// <exception cref="ArgumentException">No player, no target, an empty definition id, an empty request id, or a request
    /// with a token (a begin expects no step: <c>TamingRequest.New()</c>).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">A capability the definition's steps need is off (taming.steps,
    /// items.costs.exact for feed steps, items.costs.wear for tool steps with wear, taming.native for native steps,
    /// pets.records for a definition that names a pet).</exception>
    /// <remarks>Begin runs at once and claims the target: no second attempt on it runs until this one ends. <see cref="TamingBeginningEvent"/>
    /// is asked first (a mod may refuse), then <see cref="TamingStartedEvent"/> follows. When the definition names a pet, one
    /// roster slot of that pet is held until the attempt ends.</remarks>
    /// <example><code>
    /// TamingResult r = await Taming.Begin(player, target, "my-mod:wolf", TamingRequest.New());
    /// if (!r.Ok) Log.Info($"not started: {r.Reason}");
    /// else Log.Info($"taming {r.Progress!.Id} runs: step 0 of {r.Progress.TotalSteps}");
    /// </code></example>
    /// <seealso cref="Advance"/>
    /// <seealso cref="Cancel"/>
    /// <seealso cref="TamingBeginningEvent"/>
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
    /// <param name="player">The attempt's owner.</param>
    /// <param name="attempt">The attempt (<see cref="TamingProgress.Id"/>).</param>
    /// <param name="request">A new request on the last answer's token (<c>TamingRequest.New(progress.Token)</c>: the challenge,
    /// revision and step index). A token that does not match is <c>StaleStep</c>; the same request again gives the first
    /// answer.</param>
    /// <param name="selection">The slot that pays the step, from <see cref="Choices"/>: a plain stack for a feed step, the gear
    /// item for a tool step. Null for a weaken or native step (a selection for those is an <see cref="ArgumentException"/>).</param>
    /// <returns>The answer when the cost settled (the step after it ran); its receipts say what was taken. Refusals
    /// (<see cref="TamingResult.Refusal"/>) are <c>NotOwner</c>, <c>StaleSession</c>, <c>AttemptClosed</c>, <c>StaleStep</c>,
    /// <c>Busy</c>, <c>BodyGone</c>, <c>OutOfRange</c>, <c>Threshold</c>, <c>NativePending</c>, <c>RefusedByMod</c> (a mod),
    /// <c>StaleSelection</c> (the selection is not the owner's slot of the step's item), <c>Capacity</c> (the last step: the pet
    /// roster has no room, before the cost), <c>CommitDeferred</c> (every step is paid and the tame waits for
    /// <see cref="Retry"/>), <c>Refused</c> (the cost was refused, e.g. the slot changed before the cost ran: the receipt's
    /// <see cref="CostRefusal"/> says why, nothing was taken; or the tame was refused for good), <c>UncertainCommit</c> (a cost
    /// may or may not have been paid: the attempt fails and never refunds it), <c>PartialCostCommitted</c> (less than the
    /// step's cost moved: the attempt fails) and <c>TooManyRequests</c>; a refusal before the cost runs takes nothing and
    /// leaves the token valid.</returns>
    /// <exception cref="ArgumentException">An empty request id, a request without the token of the last answer, or a selection
    /// of the wrong shape for the step.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">A capability the step needs is off (items.costs.exact for a feed step,
    /// items.costs.wear for a tool step).</exception>
    /// <example><code>
    /// // a weaken or native step: no slot
    /// TamingResult r = await Taming.Advance(player, p.Id, TamingRequest.New(p.Token));   // p: the last TamingProgress
    /// if (r.Ok) p = r.Progress!; else Log.Info($"refused: {r.Reason}");
    /// </code></example>
    /// <seealso cref="Choices"/>
    /// <seealso cref="Retry"/>
    /// <seealso cref="TamingAdvancingEvent"/>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static Task<TamingResult> Advance(PlayerRef player, TameAttemptId attempt, TamingRequest request, ItemSelection? selection = null) =>
        Engine("Taming.Advance").Advance(player, attempt, request, selection);

    /// <summary>The cost-free retry of a deferred tame (<see cref="TamingState.AwaitingCommit"/>): checks again and binds,
    /// taking nothing.</summary>
    /// <param name="player">The attempt's owner.</param>
    /// <param name="attempt">The attempt, in <see cref="TamingState.AwaitingCommit"/>.</param>
    /// <param name="request">A new request on the last answer's token (<c>TamingRequest.New(progress.Token)</c>; all three
    /// values are checked).</param>
    /// <returns>Ok with the tame (<see cref="TamingResult.Pet"/> for a definition that names a pet); else <c>WrongState</c> (the
    /// attempt is not waiting for a retry), <c>StaleStep</c>, <c>BodyGone</c>, <c>OutOfRange</c>, <c>Threshold</c>,
    /// <c>CommitDeferred</c> (still not possible: try again later), <c>RefusedByMod</c> (a mod) or <c>Refused</c> (refused for
    /// good: the attempt fails).</returns>
    /// <exception cref="ArgumentException">An empty request id, or a request without the token of the last answer.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// // after an Advance answered "CommitDeferred" (the owner's pet roster was full), when a slot is free again
    /// TamingResult r = await Taming.Retry(player, p.Id, TamingRequest.New(p.Token));
    /// </code></example>
    /// <seealso cref="Advance"/>
    /// <seealso cref="TamingCommitDeferredEvent"/>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static Task<TamingResult> Retry(PlayerRef player, TameAttemptId attempt, TamingRequest request) =>
        Engine("Taming.Retry").Retry(player, attempt, request);

    /// <summary>Ends the attempt, named by its id: a request without a token (<c>TamingRequest.New()</c>) cancels from a
    /// stale view (a UI that missed the last step), and one with a token cancels only while that token is current
    /// (<c>StaleStep</c> otherwise). Only the attempt's owner cancels it, an attempt id is never reused (a late cancel never
    /// reaches a later attempt), the same request again gives the first answer, and a cost in flight settles first (its
    /// receipt says whether the cancel spent it). Paid steps are given back only as the definition's refund policy says
    /// (<see cref="TamingRefundPolicy"/>).</summary>
    /// <param name="player">The attempt's owner.</param>
    /// <param name="attempt">The attempt.</param>
    /// <param name="request">A new request: without a token to cancel whatever step the attempt is at, or on a token
    /// (<c>TamingRequest.New(progress.Token)</c>) to cancel only an attempt that has not moved on.</param>
    /// <returns>Ok once the attempt is <see cref="TamingState.Canceled"/>; else <c>NotOwner</c>, <c>StaleSession</c>,
    /// <c>AttemptClosed</c> (it is over already) or <c>StaleStep</c> (a token the attempt moved on from). A cancel asked while
    /// a cost is being paid waits for the cost, and its answer is <c>Canceled</c> with Ok false and the receipts of what was
    /// spent.</returns>
    /// <exception cref="ArgumentException">An empty request id.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// TamingResult r = await Taming.Cancel(player, p.Id, TamingRequest.New());   // whatever step it is at
    /// </code></example>
    /// <seealso cref="TamingCanceledEvent"/>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static Task<TamingResult> Cancel(PlayerRef player, TameAttemptId attempt, TamingRequest request) =>
        Engine("Taming.Cancel").Cancel(player, attempt, request);

    /// <summary>The attempt as it is now; null once it is gone (10 minutes of simulation after it closed).</summary>
    /// <param name="attempt">The attempt's id.</param>
    /// <returns>An immutable copy of the attempt, or null. It does not refresh any timeout.</returns>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// if (Taming.Inspect(p.Id) is { } now) Log.Info($"{now.State}: step {now.CompletedSteps}/{now.TotalSteps}, expires at {now.ExpiresAt.TotalSeconds:0} s");
    /// </code></example>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static TamingProgress? Inspect(TameAttemptId attempt) => Engine("Taming.Inspect").Inspect(attempt);

    /// <summary>The slots of the owner's inventories that can pay the next step, as selections the server read, with the
    /// token of the view they were read on (<see cref="TamingChoicesResult.Token"/>): the first 256, and
    /// <see cref="TamingChoicesResult.Truncated"/> when more could pay (never a refusal for many slots). A read only: it
    /// refreshes no timeout.</summary>
    /// <param name="player">The attempt's owner.</param>
    /// <param name="attempt">The attempt.</param>
    /// <returns>Ok with the slots (empty for a weaken or native step, which take no slot); else <c>AttemptClosed</c> or
    /// <c>WrongState</c> (the attempt is not waiting for a step). A feed step lists plain stacks of its item holding at least
    /// its count; a tool step lists gear items of its item that are not broken. Read it again just before a menu is shown:
    /// the slots change.</returns>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// var choices = Taming.Choices(player, p.Id);
    /// foreach (var c in choices.Choices) Log.Info($"can pay from: {c.Label}");
    /// if (choices.Truncated) Log.Info("and more: the first 256 are listed");
    /// </code></example>
    /// <seealso cref="Advance"/>
    [TickThreadOnly]
    [RequiresCapability("taming.steps")]
    public static TamingChoicesResult Choices(PlayerRef player, TameAttemptId attempt) => Engine("Taming.Choices").Choices(player, attempt);

    // the definitions are data: Kiln.Taming loaded is all they need (a mod's OnLoad, no world yet); read on any thread
    private static TamingEngine DefinitionReader()
    {
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

/// <summary>The answer of <see cref="Taming.Define"/>, as <see cref="PetDefinitionResult"/> is of <see cref="Pets.Define"/>.
/// Since 0.2.0-dev.32.</summary>
/// <param name="Ok">True when the definition is registered (or the same one was already).</param>
/// <param name="Reason">Why not, in words for a log: its first word is <see cref="Refusal"/> (<c>DefinitionInUse</c>).</param>
/// <param name="Definition">The registered definition (the one that stays, when refused).</param>
public sealed record TamingDefinitionResult(bool Ok, string? Reason, TamingDefinition? Definition) : IKilnResult
{
    /// <summary>Why not, as a value: <see cref="TamingRefusal.None"/> when <see cref="Ok"/>.</summary>
    public TamingRefusal Refusal => RefusalWords.Of(Ok, Reason, TamingRefusal.None, TamingRefusal.Other);
}
