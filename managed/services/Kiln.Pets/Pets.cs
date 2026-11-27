// SPDX-License-Identifier: MIT
// Kiln.Pets' face (19d §5.3): definitions, owned records, bodies, orders, revives. Every call that changes a pet runs on the
// server's tick thread and is serialized per pet; the answer is a task that completes at a later step with the settled
// snapshot (or the refusal and its reason). docs/kiln/services/pets.md.
namespace Kiln;

/// <summary>
/// Pets: owned creatures that follow their owner, fight, go down and are revived. Kiln keeps each pet's durable record per
/// character in the world's save, makes and retires its body, holds its movement lease, protects it, and publishes its
/// state; every behaviour choice is the mod's (<see cref="PetDefinition"/>). Since 0.2.0-dev.32.
/// </summary>
/// <remarks>
/// <para>Definitions: <see cref="Define"/> (or a <c>PetBlock</c> through Kiln.Blocks: the same record). A pet record
/// names its definition; a record whose definition is not defined in this session stays dormant, never erased.</para>
/// <para>Where it runs: server only (<see cref="ServerOnlyAttribute"/>), on the tick thread. <see cref="Define"/>,
/// <see cref="Definition"/> and <see cref="Definitions"/> are data and may be called when a mod loads, with no world. The
/// calls that read or change records need a running server world (<c>pets.records</c>); a joined client sees its own pets
/// only through <see cref="PetsClient"/>, a read-only view the server sends.</para>
/// <para>Timing: a request is serialized per pet. The answer is a <see cref="Task{TResult}"/> that completes at a later server
/// step with the settled snapshot (or the refusal and its reason). Await it from mod code on the tick thread: the continuation
/// comes back to the tick thread. A request made while another runs on the same pet is <c>Busy</c>. After-events
/// (<see cref="PetChangeEvent"/>) are posted after the change settled, so a handler may call <c>Pets</c> again.</para>
/// <para>Requests: every change carries a <see cref="PetRequest"/> whose request id makes a retry the same request: the first
/// answer is given back for 10 minutes, and nothing runs twice. Another request under a used id is <c>RequestConflict</c>.</para>
/// <para>Error rule: a bad argument throws <see cref="ArgumentException"/>; an off capability throws
/// <see cref="KilnUnavailableException"/> naming its row; a refusal by Kiln or the game is <c>Ok = false</c> with a
/// reason (and its cost receipts, when it came after a cost was taken). Check <see cref="PetResult.Ok"/>, not only the
/// absence of an exception, and decide by the reason as a value, <see cref="PetResult.Refusal"/> (<see cref="PetRefusal"/>),
/// never by its text.</para>
/// <para>Policy: Kiln keeps the mechanism and the mod chooses (<see cref="PetDefinition"/>): who a pet may hurt, what a downed
/// pet is, what a revive costs, how it follows, how many there may be. A mod that says nothing gets safe defaults: players
/// protected from pets and pets from players, a bodyless recovery anchor when a pet goes down, a free 3 s revive.</para>
/// </remarks>
/// <example>
/// Define a pet, give one to a player's character and bring it out (inside a command handler):
/// <code>
/// Pets.Define(new PetDefinition { Id = "my-mod:hound", Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Hound" });
///
/// PetResult given = await Pets.Grant(owner, "my-mod:hound", PetRequest.New());       // owner: the player's CharacterKey
/// if (given.Ok)
/// {
///     PetResult out1 = await Pets.Summon(player, given.Pet!.Id, PetRequest.New());   // player: the same player, as a PlayerRef
///     Log.Info(out1.Ok ? $"{out1.Pet!.Label} is out" : $"refused: {out1.Reason}");
/// }
/// </code>
/// </example>
/// <seealso cref="PetDefinition"/>
/// <seealso cref="PetsClient"/>
/// <seealso cref="PetChangeEvent"/>
/// <seealso href="https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/services/pets.md">Pets service</seealso>
/// <seealso href="https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/pets.md">Pets: the modder's guide</seealso>
public static partial class Pets
{
    static Pets() => Caps.Rows.Ensure();

    /// <summary>Registers a kind of pet as the calling mod's. The same definition again is Ok (no event); another one
    /// under the same id replaces it, except while one of its pets has a live body or a revive running
    /// (<c>DefinitionInUse</c>). A creature Kiln cannot find yet (a game client before content is ready) is checked
    /// again when a body is first made.</summary>
    /// <param name="definition">The pet's definition. Its id starts with the calling mod's id and a colon; its creature is a
    /// <c>creatures</c> registry id.</param>
    /// <returns>Ok with the registered definition (the same definition again is Ok and posts no event); or Ok false:
    /// <c>DefinitionInUse</c> (another definition under the id while one of its pets has a live body or a revive running),
    /// <c>CreatureUnknown</c> (no such creature, or a creature block that is not defined) or <c>CreatureUnsupported</c> (not an
    /// enemy creature that walks, or a boss).</returns>
    /// <remarks>Threads: the tick thread, or any thread before a server world runs (a mod's <c>OnLoad</c>, a game client's
    /// too). It is not any-thread while a server world runs: whether the id's pets have live bodies is the roster's state,
    /// which only the tick thread reads (<c>Scheduler.Post</c> brings a definition there).</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">A value is out of its range (the message names the field: <c>level.scale</c>,
    /// <c>limits.livePerWorld</c>, ...), or the id is not the calling mod's (<c>&lt;mod id&gt;:&lt;name&gt;</c>).</exception>
    /// <exception cref="InvalidOperationException">Off the tick thread while a server world runs here.</exception>
    /// <example><code>
    /// PetDefinitionResult d = Pets.Define(new PetDefinition
    /// {
    ///     Id = "my-mod:duelist",
    ///     Creature = "game:enemy_wildbeast_wolf_earlygame",
    ///     Targets = new PetTargetPolicy { Pvp = PetPvpMode.ByModRule },                   // fights players the mod put at odds
    ///     Downed = new PetDownedPolicy { Outcome = PetDownedOutcome.Release },            // gone when it goes down
    ///     Revive = new PetRevivePolicy { Cost = new PetCostDefinition(twig, 3) },          // twig: an ItemId
    /// });
    /// if (!d.Ok) Log.Warn($"pet not defined: {d.Reason}");
    /// </code></example>
    /// <seealso cref="PetDefinition"/>
    [RequiresCapability("pets.definitions")]
    public static PetDefinitionResult Define(PetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (PetsRuntime.Roster is not null && !Backend.Current.IsTickThread)
            throw new InvalidOperationException("Pets.Define: a server world runs here, so define pets on the tick thread (Scheduler.Post), or in OnLoad before the world runs");
        if (ModContext.CurrentMod is { } mod && !definition.Id.StartsWith(mod.Info.Id.ToLowerInvariant() + ":", StringComparison.Ordinal))
            throw new ArgumentException($"pet definition ids of mod {mod.Info.Id} start with \"{mod.Info.Id.ToLowerInvariant()}:\", not \"{definition.Id}\"", nameof(definition));
        return PetsRuntime.Define(definition, ModContext.CurrentMod?.Info.Id);
    }

    /// <summary>Kiln.Blocks: a PetBlock's definition, as the mod that defined the block (its id is that mod's already).</summary>
    internal static PetDefinitionResult DefineAs(PetDefinition definition, string mod)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return PetsRuntime.Define(definition, mod);
    }

    /// <summary>The definition registered under <paramref name="id"/>, or null. Any thread.</summary>
    /// <param name="id">The definition's Kiln id (<c>my-mod:hound</c>).</param>
    /// <returns>The registered definition, or null when none has that id (or the mod that defined it unloaded).</returns>
    /// <example><code>
    /// if (Pets.Definition("my-mod:hound") is { } hound) Log.Info($"{hound.Id} uses {hound.Creature}, follows at {hound.Movement.FollowDistance} m");
    /// </code></example>
    /// <seealso cref="Definitions"/>
    [AnyThread]
    public static PetDefinition? Definition(string id) => PetsRuntime.Definition(id);

    /// <summary>Every registered definition, of every mod, by id. Any thread.</summary>
    /// <seealso cref="Definition"/>
    [AnyThread]
    public static IReadOnlyList<PetDefinition> Definitions => PetsRuntime.AllDefinitions();

    // ---- records ----

    /// <summary>The character's pets (released tombstones left out). The character need not be online. Server tick thread.</summary>
    /// <param name="owner">The character that owns them, as a <see cref="CharacterKey"/> (account and character id): for a
    /// player in game, <c>CharacterData.KeyOf(player.Entity)</c>.</param>
    /// <returns>A snapshot of each pet, as it is now.</returns>
    /// <exception cref="ArgumentException">No character (none, or an account-only key).</exception>
    /// <exception cref="KilnUnavailableException"><c>pets.records</c> is off (a joined client, no server world).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// foreach (PetSnapshot p in Pets.Of(owner))
    ///     Log.Info($"{p.Label} ({p.Definition}): {p.Life}, {p.Presence}, {p.Health}/{p.HealthMax}");
    /// </code></example>
    /// <seealso cref="Get"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static IReadOnlyList<PetSnapshot> Of(CharacterKey owner)
    {
        if (owner.IsNone || owner.IsAccountOnly) throw new ArgumentException("a pet owner is a character (account and character id)", nameof(owner));
        return Roster("Pets.Of").Of(owner);
    }

    /// <summary>The pets of the player's character, as <see cref="Of(CharacterKey)"/> and as the requests name their owner (a
    /// <see cref="PlayerRef"/>): the player must be in game. An offline character's pets are read by its key. Server tick
    /// thread.</summary>
    /// <exception cref="ArgumentException">No player, or the player is not in game (or has no character id here): the
    /// message starts with the reason's word (<c>OwnerOffline</c>, <c>NoCharacter</c>).</exception>
    /// <exception cref="KilnUnavailableException"><c>pets.records</c> is off (a joined client, no server world).</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static IReadOnlyList<PetSnapshot> Of(PlayerRef player)
    {
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        var r = Roster("Pets.Of");
        var who = r.World.Owner(player, out var why)
                  ?? throw new ArgumentException($"{why ?? "OwnerOffline: the player is not in game"} (an offline character's pets: Pets.Of(CharacterKey))", nameof(player));
        return r.Of(who.Key);
    }

    /// <summary>The pet, or null (unknown here, or its owner's records are not read yet: <see cref="Of(CharacterKey)"/> reads them).
    /// A released pet is returned (Life Released) until a committed save holds its release. Server tick thread.</summary>
    /// <param name="pet">The pet's id.</param>
    /// <returns>The pet as it is now, or null (<see cref="PetId.None"/>, or not known here).</returns>
    /// <exception cref="KilnUnavailableException"><c>pets.records</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// if (Pets.Get(pet) is { Life: PetLife.Downed } downed) Log.Info($"{downed.Label} is down: revive it next to its anchor");
    /// </code></example>
    /// <seealso cref="Of"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static PetSnapshot? Get(PetId pet) => pet.IsNone ? null : Roster("Pets.Get").Get(pet);

    /// <summary>Gives a character a new pet of <paramref name="definition"/>: a Ready record with no body (Summon brings it
    /// out). The mod's way to hand out pets (a reward, a quest, an admin command); a tame of a definition that names a pet (<c>TamingDefinition.Pet</c> of Kiln.Taming) makes records too.</summary>
    /// <param name="owner">The character that gets the pet; it need not be online.</param>
    /// <param name="definition">The id of a registered <see cref="PetDefinition"/>.</param>
    /// <param name="request">The request: a new request id for each grant (<see cref="PetRequest.New"/>).</param>
    /// <returns>Ok with the new pet; or Ok false: <c>DefinitionUnknown</c>, <c>Capacity</c> (the definition's
    /// RetainedPerCharacter, or 128 records), <c>LevelUnavailable</c>, <c>RecordsUnreadable</c>.</returns>
    /// <exception cref="ArgumentException">No character, an empty definition id, or an empty request id.</exception>
    /// <exception cref="KilnUnavailableException"><c>pets.records</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// PetResult r = await Pets.Grant(owner, "my-mod:hound", PetRequest.New());
    /// if (!r.Ok) Log.Info($"no pet: {r.Reason}");      // Capacity: the character has all it may keep
    /// </code></example>
    /// <seealso cref="Summon"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static Task<PetResult> Grant(CharacterKey owner, string definition, PetRequest request)
    {
        if (owner.IsNone || owner.IsAccountOnly) throw new ArgumentException("a pet owner is a character (account and character id)", nameof(owner));
        ArgumentException.ThrowIfNullOrEmpty(definition);
        Check(request);
        var r = Roster("Pets.Grant");
        return r.Submit(new PetRoster.Op
        {
            Kind = PetRoster.Kind.Grant, OperationId = request.RequestId, OwnerKey = owner, Definition = definition, Expected = 0,
            Payload = $"grant|{definition}",
        });
    }

    /// <summary>Gives the player's character a new pet of <paramref name="definition"/>, as
    /// <see cref="Grant(CharacterKey, string, PetRequest)"/> does, naming its owner as every other request does (a
    /// <see cref="PlayerRef"/> in game). A character that is offline is given a pet by its key.</summary>
    /// <returns>As the other overload; also <c>OwnerOffline</c> (not in game) or <c>NoCharacter</c>.</returns>
    /// <exception cref="ArgumentException">No player, an empty definition id, or an empty request id.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static Task<PetResult> Grant(PlayerRef owner, string definition, PetRequest request)
    {
        if (owner.IsNone) throw new ArgumentException("no owner", nameof(owner));
        ArgumentException.ThrowIfNullOrEmpty(definition);
        Check(request);
        var r = Roster("Pets.Grant");
        if (r.World.Owner(owner, out var why) is not { } who)
            return Task.FromResult(new PetResult(false, why ?? "OwnerOffline: the owner is not in game", null, request.RequestId, []));
        return Grant(who.Key, definition, request);
    }

    /// <summary>Brings the pet out: a Ready pet gets a protected body 2 to 4 m from its owner, at its kept health, following;
    /// a Downed pet's recovery anchor is put next to its owner (no body, no health). Already out: Ok, nothing changes.</summary>
    /// <param name="owner">The pet's owner, in game, as a <see cref="PlayerRef"/> (<c>PlayerRef.Of(player.Entity)</c>). Another
    /// player's pet is <c>NotOwner</c>; an owner who is not in game is <c>OwnerOffline</c>.</param>
    /// <param name="pet">The pet's id (<see cref="PetSnapshot.Id"/>).</param>
    /// <param name="request">The request: a new request id for each intended request, and optionally the pet's
    /// <see cref="PetSnapshot.StateRevision"/> it was made on (<c>StaleRevision</c> if the pet moved on).</param>
    /// <returns>Ok with the pet once its body is live (or its anchor shown); else <c>Busy</c>, <c>Capacity</c>,
    /// <c>NoSafeGround</c>, <c>BodyNotReady</c>, <c>DefinitionUnknown</c>, <c>CreatureUnknown</c>, <c>NotOwner</c>,
    /// <c>OwnerOffline</c>, ... A body that goes down before the answer is <c>BodyDowned</c> with the real downed pet.</returns>
    /// <exception cref="ArgumentException">No pet, no owner, or an empty request id.</exception>
    /// <exception cref="KilnUnavailableException">The capability the call needs is off (named in its message).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// PetResult r = await Pets.Summon(player, pet, PetRequest.New());
    /// if (!r.Ok) Log.Info($"not out: {r.Reason}");     // Capacity: the definition's LivePerCharacter is reached
    /// </code></example>
    /// <seealso cref="Dismiss"/>
    /// <seealso cref="PetSummonedEvent"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Summon)]
    public static Task<PetResult> Summon(PlayerRef owner, PetId pet, PetRequest request) => Request(PetRoster.Kind.Summon, "Pets.Summon", Caps.Summon, owner, pet, request);

    /// <summary>Puts the pet away: a live body is retired (its health kept), a downed pet's anchor is hidden (never healed);
    /// the owner's wish to have it out is cleared.</summary>
    /// <param name="owner">The pet's owner, in game, as a <see cref="PlayerRef"/> (<c>PlayerRef.Of(player.Entity)</c>). Another
    /// player's pet is <c>NotOwner</c>; an owner who is not in game is <c>OwnerOffline</c>.</param>
    /// <param name="pet">The pet's id (<see cref="PetSnapshot.Id"/>).</param>
    /// <param name="request">The request: a new request id for each intended request, and optionally the pet's
    /// <see cref="PetSnapshot.StateRevision"/> it was made on (<c>StaleRevision</c> if the pet moved on).</param>
    /// <returns>Ok with the pet (also when it was away already); else <c>Busy</c>, <c>NotOwner</c>, <c>OwnerOffline</c>,
    /// <c>StaleRevision</c>, <c>RequestConflict</c>.</returns>
    /// <exception cref="ArgumentException">No pet, no owner, or an empty request id.</exception>
    /// <exception cref="KilnUnavailableException">The capability the call needs is off (named in its message).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// await Pets.Dismiss(player, pet, PetRequest.New());
    /// </code></example>
    /// <seealso cref="Summon"/>
    /// <seealso cref="PetDismissedEvent"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Summon)]
    public static Task<PetResult> Dismiss(PlayerRef owner, PetId pet, PetRequest request) => Request(PetRoster.Kind.Dismiss, "Pets.Dismiss", Caps.Summon, owner, pet, request);

    /// <summary>Releases the pet for good: a body is retired first, then the record becomes a tombstone that goes once a
    /// committed save holds it. No wild creature is left behind.</summary>
    /// <param name="owner">The pet's owner, in game, as a <see cref="PlayerRef"/> (<c>PlayerRef.Of(player.Entity)</c>). Another
    /// player's pet is <c>NotOwner</c>; an owner who is not in game is <c>OwnerOffline</c>.</param>
    /// <param name="pet">The pet's id (<see cref="PetSnapshot.Id"/>).</param>
    /// <param name="request">The request: a new request id for each intended request, and optionally the pet's
    /// <see cref="PetSnapshot.StateRevision"/> it was made on (<c>StaleRevision</c> if the pet moved on).</param>
    /// <returns>Ok with the released pet (Life <see cref="PetLife.Released"/>); else <c>Busy</c>, <c>NotOwner</c>,
    /// <c>OwnerOffline</c>, <c>AlreadyReleased</c>, <c>StaleRevision</c>, <c>RequestConflict</c>.</returns>
    /// <exception cref="ArgumentException">No pet, no owner, or an empty request id.</exception>
    /// <exception cref="KilnUnavailableException">The capability the call needs is off (named in its message).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <remarks>A release cannot be undone. A mod that offers it should ask the player to confirm first.</remarks>
    /// <example><code>
    /// PetResult r = await Pets.Release(player, pet, PetRequest.New());
    /// </code></example>
    /// <seealso cref="PetReleasedEvent"/>
    /// <seealso cref="Pets.Grant"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static Task<PetResult> Release(PlayerRef owner, PetId pet, PetRequest request) => Request(PetRoster.Kind.Release, "Pets.Release", Caps.Records, owner, pet, request);

    /// <summary>Orders a live pet: follow, stay, or attack <paramref name="target"/> (needs <c>pets.fight</c>, and the pet's
    /// target rule must allow it: else <c>TargetProtected</c> and no threat is added).</summary>
    /// <param name="owner">The pet's owner, in game.</param>
    /// <param name="pet">The pet's id.</param>
    /// <param name="order">Follow, stay or attack.</param>
    /// <param name="target">The creature to attack; <see cref="EntityRef.None"/> for follow and stay (it is ignored).</param>
    /// <param name="request">The request (a new request id).</param>
    /// <returns>Ok with the pet and its new <see cref="PetSnapshot.RequestedOrder"/>; else <c>Busy</c>, <c>NotOwner</c>,
    /// <c>TargetProtected</c> (the pet's target rule does not allow it), <c>TargetGone</c> (the target is not there),
    /// <c>NotSummoned</c> (the pet has no body to order), <c>StaleRevision</c>, <c>RequestConflict</c>.</returns>
    /// <exception cref="ArgumentException">Attack without a target, an order that is not defined, no pet, no owner, or an empty
    /// request id.</exception>
    /// <exception cref="KilnUnavailableException"><c>pets.follow</c> is off, or <c>pets.fight</c> for an attack.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// await Pets.Command(player, pet, PetOrder.Stay, EntityRef.None, PetRequest.New());
    /// await Pets.Command(player, pet, PetOrder.Attack, target, PetRequest.New());     // target: an EntityRef
    /// </code></example>
    /// <seealso cref="PetTargetPolicy"/>
    /// <seealso cref="PetCommandChangedEvent"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Follow)]
    public static Task<PetResult> Command(PlayerRef owner, PetId pet, PetOrder order, EntityRef target, PetRequest request)
    {
        if (!Enum.IsDefined(order)) throw new ArgumentException($"{order} is not an order", nameof(order));
        if (order == PetOrder.Attack && target.IsNone) throw new ArgumentException("an attack order needs a target", nameof(target));
        if (order == PetOrder.Attack) Require(Caps.Fight);
        return Request(PetRoster.Kind.Command, "Pets.Command", Caps.Follow, owner, pet, request, o =>
        {
            o.Order = order;
            o.Target = order == PetOrder.Attack ? target : EntityRef.None;
            o.Payload += $"|{order}|{o.Target.Id.Value}:{o.Target.Serial}";
        });
    }

    /// <summary>Revives a downed pet: the owner channels next to its recovery anchor (summon it first) for the definition's
    /// channel seconds; at completion the cost is quoted again and taken exactly, and the pet gets a new protected body at the
    /// definition's health fraction, following. Leaving range, leaving the world or going down cancels; so do moving and being
    /// hurt when the definition says so. A cancel or refusal before the debit takes nothing.</summary>
    /// <param name="owner">The owner, in game.</param>
    /// <param name="pet">The downed pet.</param>
    /// <param name="request">The request (its request id names the channel for <see cref="CancelRevive"/>).</param>
    /// <param name="selection">Where the definition's cost (<see cref="PetRevivePolicy.Cost"/>, the one way a revive is paid:
    /// what it costs) is taken from: the owner's slot, as read (<see cref="ItemSelection.Of"/> of a slot of
    /// <c>Items.Read</c>): a plain stack of the cost's item holding at least the cost; only that slot pays, and it must
    /// still hold what was read when the cost runs. Null: the first slot of the owner's inventories holding a plain stack of
    /// the whole cost (<see cref="ItemCosts.Choose"/>; a cost split over two stacks is not taken).</param>
    /// <returns>Ok with the revived pet and the cost receipts; else <c>RecoveryNotSummoned</c>, <c>OutOfRange</c>,
    /// <c>ReviveNotAllowed</c>, <c>CostUnavailable</c>, <c>RefusedByMod</c> (a mod), <c>CanceledNoCost</c>,
    /// <c>CanceledCostCommitted</c> or <c>ReviveFailedCostCommitted</c> (with the receipts of what was taken), or
    /// <c>UncertainCommit</c> (with the receipt of a cost that may have been taken; the pet stays downed).</returns>
    /// <exception cref="ArgumentException">No pet, no owner, or an empty request id.</exception>
    /// <exception cref="KilnUnavailableException">The capability the call needs is off (named in its message).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <remarks>The task completes when the channel ends, seconds later (the definition's
    /// <see cref="PetRevivePolicy.ChannelDuration"/>), not when the revive starts. Watch <see cref="PetReviveStartedEvent"/> to show the
    /// channel and <see cref="PetRevivingEvent"/> to refuse a revive by the mod's own rules. Check <see cref="PetResult.Costs"/>:
    /// a cost that was taken stays taken when a later step fails.</remarks>
    /// <example><code>
    /// PetResult r = await Pets.Revive(player, pet, PetRequest.New());          // the first slot holding the whole cost pays
    /// if (!r.Ok) Log.Info($"not revived: {r.Reason}");
    /// foreach (CostReceipt c in r.Costs) Log.Info($"took {c.Taken} of {c.Item} ({c.State})");
    /// </code></example>
    /// <seealso cref="CancelRevive"/>
    /// <seealso cref="PetRevivePolicy"/>
    /// <seealso cref="PetRevivedEvent"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Downed)]
    public static Task<PetResult> Revive(PlayerRef owner, PetId pet, PetRequest request, ItemSelection? selection = null) =>
        Request(PetRoster.Kind.Revive, "Pets.Revive", Caps.Downed, owner, pet, request, o =>
        {
            o.Selection = selection;
            o.Payload += selection is null ? "|-" : $"|{selection.InventoryOwner.Id.Value}:{selection.InventoryOwner.Serial}|{selection.Slot}|{selection.ExpectedItem}|{selection.ExpectedCount}|{selection.Instance.Entity.Id.Value}:{selection.Instance.Entity.Serial}";
        });

    /// <summary>Cancels the revive <paramref name="reviveRequest"/> names (its request's id, <see cref="PetSnapshot.ReviveRequest"/>;
    /// never a later one). Before its cost is taken it
    /// spends nothing; a revive that already completed is not undone (<c>AlreadyCompleted</c>). Repeating the same cancel
    /// request gives its first answer.</summary>
    /// <param name="owner">The pet's owner, in game.</param>
    /// <param name="pet">The downed pet.</param>
    /// <param name="reviveRequest">The request id of the <see cref="Revive"/> request (<see cref="PetRequest.RequestId"/>,
    /// <see cref="PetRevivingEvent.RequestId"/> or <see cref="PetSnapshot.ReviveRequest"/>). Not empty.</param>
    /// <param name="request">The cancel's own request, with a new request id.</param>
    /// <returns>Ok when the revive was canceled; else <c>AlreadyCompleted</c> (the revive had completed), <c>AlreadyEnded</c> (it
    /// had ended another way), <c>NotOwner</c>, <c>Busy</c>, <c>RequestConflict</c>.</returns>
    /// <exception cref="ArgumentException">No revive request, no pet, no owner, or an empty request id.</exception>
    /// <exception cref="KilnUnavailableException"><c>pets.downed</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// var request = PetRequest.New();
    /// Task&lt;PetResult&gt; revive = Pets.Revive(player, pet, request);                       // the channel starts
    /// await Pets.CancelRevive(player, pet, request.RequestId, PetRequest.New());        // the owner changed their mind: nothing is taken
    /// </code></example>
    /// <seealso cref="Revive"/>
    /// <seealso cref="PetReviveCanceledEvent"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Downed)]
    public static Task<PetResult> CancelRevive(PlayerRef owner, PetId pet, Guid reviveRequest, PetRequest request)
    {
        if (reviveRequest == Guid.Empty) throw new ArgumentException("no revive request", nameof(reviveRequest));
        return Request(PetRoster.Kind.CancelRevive, "Pets.CancelRevive", Caps.Downed, owner, pet, request, o =>
        {
            o.CancelTarget = reviveRequest;
            o.Payload += $"|{reviveRequest}";
        });
    }

    /// <summary>Declares (true) or ends (false) a consensual hostility between two characters: for pets whose
    /// <see cref="PetTargetPolicy.Pvp"/> is <see cref="PetPvpMode.ByModRule"/>, each character's pets may then harm the
    /// other character and the other's pets of that mode, and be harmed by them. Who duels, and when, is the mod's rule
    /// (a challenge accepted, an arena entered); Kiln enforces the pair in every hit and order. Pets of PvP off stay
    /// protected whatever the pairs. Pairs last for this world session (the mod declares them again after a restart).</summary>
    /// <param name="a">One character.</param>
    /// <param name="b">The other character.</param>
    /// <param name="hostile">True to declare the pair hostile, false to end it.</param>
    /// <exception cref="ArgumentException">No character, or the same character twice.</exception>
    /// <exception cref="KilnUnavailableException"><c>pets.fight</c> or <c>pets.records</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <example><code>
    /// // both players agreed to the duel (the mod's own rule): their duelists may now fight each other
    /// Pets.SetHostile(owner, otherOwner, true);
    /// // ... and when it ends
    /// Pets.SetHostile(owner, otherOwner, false);
    /// </code></example>
    /// <seealso cref="IsHostile"/>
    /// <seealso cref="PetTargetPolicy"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Fight)]
    public static void SetHostile(CharacterKey a, CharacterKey b, bool hostile)
    {
        if (a.IsNone || a.IsAccountOnly || b.IsNone || b.IsAccountOnly) throw new ArgumentException("hostility is between two characters", nameof(a));
        if (a == b) throw new ArgumentException("a character is never hostile to itself", nameof(b));
        var r = Roster("Pets.SetHostile");
        Require(Caps.Fight);
        r.SetHostile(a, b, hostile);
    }

    /// <summary>Whether the two characters were declared hostile (<see cref="SetHostile"/>). Server tick thread.</summary>
    /// <param name="a">One character.</param>
    /// <param name="b">The other character.</param>
    /// <returns>True when the pair is declared hostile now (the order of <paramref name="a"/> and <paramref name="b"/> does not matter).</returns>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException"><c>pets.records</c> is off.</exception>
    /// <example><code>
    /// if (!Pets.IsHostile(owner, otherOwner)) Log.Info("no duel is on between them: their pets are protected from each other");
    /// </code></example>
    /// <seealso cref="SetHostile"/>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static bool IsHostile(CharacterKey a, CharacterKey b) => Roster("Pets.IsHostile").IsHostile(a, b);

    /// <summary>The characters declared hostile to <paramref name="character"/> (<see cref="SetHostile"/>), in no set order:
    /// a mod lists its duels (to end them, to show them), a test checks them. Server tick thread.</summary>
    /// <exception cref="ArgumentException">No character (none, or an account-only key).</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static IReadOnlyList<CharacterKey> HostileTo(CharacterKey character)
    {
        if (character.IsNone || character.IsAccountOnly) throw new ArgumentException("hostility is between characters", nameof(character));
        return Roster("Pets.HostileTo").HostileTo(character);
    }

    // ---- helpers ----

    private static PetRoster Roster(string api)
    {
        Services.RequireTickThread(api);
        return PetsRuntime.Roster ?? throw Unavailable(Caps.Records, "no server world runs here");
    }

    private static void Check(PetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty) throw new ArgumentException("a request id is a new Guid per request", nameof(request));
    }

    private static void Require(string row)
    {
        if (PetsRuntime.TestRows || Capabilities.Is(row)) return;
        throw Unavailable(row, Capabilities.Get(row).ReasonText);
    }

    private static KilnUnavailableException Unavailable(string row, string why)
    {
        Caps.Rows.Ensure();
        return ServiceCaps.Unavailable(row, why);
    }

    internal static Task<PetResult> Request(PetRoster.Kind kind, string api, string row, PlayerRef owner, PetId pet, PetRequest request, Action<PetRoster.Op>? fill = null)
    {
        if (pet.IsNone) throw new ArgumentException("no pet", nameof(pet));
        if (owner.IsNone) throw new ArgumentException("no owner", nameof(owner));
        Check(request);
        var r = Roster(api);
        Require(row);
        var who = r.World.Owner(owner, out var why);
        if (who is null) return Task.FromResult(new PetResult(false, why ?? "OwnerOffline", r.Get(pet), request.RequestId, []));
        var op = new PetRoster.Op
        {
            Kind = kind, OperationId = request.RequestId, OwnerKey = who.Value.Key, Who = owner, Pet = pet, Expected = request.ExpectedStateRevision,
            Payload = $"{kind}|{pet}|{request.ExpectedStateRevision}",
        };
        fill?.Invoke(op);
        return r.Submit(op);
    }
}

/// <summary>A pet definition was registered or replaced (<see cref="Pets.Define"/>). Posted on the tick thread, at the
/// step after it. Since 0.2.0-dev.32.</summary>
public sealed class PetDefinitionChangedEvent : KilnEvent
{
    internal PetDefinitionChangedEvent(PetDefinition? before, PetDefinition after) => (Before, After) = (before, after);
    /// <summary>The definition it replaced; null for a new one.</summary>
    public PetDefinition? Before { get; }
    /// <summary>The definition now.</summary>
    public PetDefinition After { get; }
}
