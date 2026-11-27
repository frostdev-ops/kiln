// SPDX-License-Identifier: MIT
// Item moves that keep item state, container names, and inventory-transfer rules and events: kiln.items v3
// (native/core/abi/services/kiln_items.h "moves", "transfer events and rules").
//
// Ported from shroudlink (MIT, bjck/shroudlink v0.8.0): `move_stack`, `move_items`, `chest_name`, and the `keep` /
// `on_transfer` inventory events. Credit and what differs: docs/kiln/engines/shroudlink.md, docs/kiln/modding/inventory.md.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

public static unsafe partial class Items
{
    private static ItemsNative.TableV3* _v3;

    // The v3 table, or null when the native service is older (an older runtime). A table long enough for v3 whose move
    // function is null has no v3 parts either (kiln.items always fills it; the test kit's v8 table leaves v3 out this way).
    private static ItemsNative.TableV3* V3
    {
        get
        {
            if (_v3 != null) return _v3;
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(ItemsNative.TableV3) || ((ItemsNative.TableV3*)t)->Move == null) return null;
            return _v3 = (ItemsNative.TableV3*)t;
        }
    }

    // capability: the row the calling member needs (items.move, items.names, items.transfer): an older table lacks all
    // three, and the exception names the caller's own
    private static ItemsNative.TableV3* RequireV3(string api, string capability)
    {
        var t = V3;
        if (t == null)
            throw T == null
                ? ServiceCaps.Unavailable(Caps.Service, "kiln.items is unavailable")
                : ServiceCaps.Unavailable(capability, $"{api}: this Kiln runtime's kiln.items has no move, name or transfer functions (update Kiln)");
        return t;
    }

    /// <summary>True when moves work right now (capability <c>items.move</c>, and the kiln_items program runs in the
    /// server world): <see cref="Move"/>, <see cref="QuickStack"/> and <see cref="MoveAll"/> are Unavailable otherwise.
    /// The session part is read live; <c>items.moves</c> carries it in the registry, up to a second late.</summary>
    public static bool MovesAvailable => Can(Caps.Move) && Status3()?.Moves == true;

    /// <summary>True when the server simulation can validate source and destination witnesses inside a move.</summary>
    public static bool CheckedMovesAvailable
    {
        get
        {
            var t = V3;
            if (t == null || !Can(Caps.Move)) return false;
            ItemsNative.Status2 status = default;
            status.Size = (uint)sizeof(ItemsNative.Status2);
            return t->GetStatus2(&status) == ItemsNative.Ok && status.Moves != 0 && (status.Reserved & 1) != 0;
        }
    }

    /// <summary>True when <see cref="ContainerName"/> can read names on this game build (capability
    /// <c>items.names</c>).</summary>
    public static bool ContainerNamesAvailable => Can(Caps.Names);

    /// <summary>The v3 parts' state (moves, names, transfer hooks, counters), or null on an older runtime.</summary>
    public static ItemsV3Status? Status3()
    {
        var t = V3;
        if (t == null) return null;
        ItemsNative.Status2 s = default;
        s.Size = (uint)sizeof(ItemsNative.Status2);
        if (t->GetStatus2(&s) != ItemsNative.Ok) return null;
        return new ItemsV3Status(s.Moves != 0, s.Names != 0, s.TransferHooks != 0, s.HooksInstalled != 0, (int)s.Rules,
            (int)s.Handlers, (int)s.Reporters, s.TransfersSeen, s.TransfersCanceled, s.MovesDone, (int)s.NamesFound,
            ItemsNative.Text(s.Detail, 160));
    }

    // ------------------------------------------------------------------------------------------- container names -

    /// <summary>
    /// The name a player gave an object with the Rename button (a chest, a station), or null when it has none. Only a
    /// renamed object carries the game's name component. Tick thread. Throws <see cref="NotSupportedException"/> when
    /// <see cref="ContainerNamesAvailable"/> is false (an older Kiln, or a game build the string table is unknown on).
    /// </summary>
    /// <example><code>if (Items.ContainerName(chest) == "Ores") { /* deposit ore here */ }</code></example>
    /// <seealso cref="SetContainerName"/>
    [RequiresCapability(Caps.Names)]
    [TickThreadOnly]
    public static string? ContainerName(EntityId entity)
    {
        var t = Require3("Items.ContainerName", Caps.Names);
        var buf = stackalloc byte[512];
        uint len = 0;
        var rc = t->ContainerName(entity.Value, buf, 512, &len);
        switch (rc)
        {
            case ItemsNative.Ok:
            case ItemsNative.BufferTooSmall:
                return System.Text.Encoding.UTF8.GetString(buf, (int)Math.Min(len, 511));
            case ItemsNative.NotFound:
                return null;
            case ItemsNative.Unavailable:
                // before the world's first step, or this build has no names: tell them apart through the status
                if (Status3()?.Names == true) return null;
                throw ServiceCaps.Unavailable(Caps.Names, "Items.ContainerName: " + (Status3()?.Detail is { Length: > 0 } d ? d : "the container-name table is not available"));
            default:
                throw Failure("Items.ContainerName", rc);
        }
    }

    private static ItemsNative.TableV3* Require3(string api, string capability)
    {
        Services.RequireTickThread(api);
        return RequireV3(api, capability);
    }

    // ------------------------------------------------------------------------------------------- moves ---------

    /// <summary>
    /// Moves a stack, or part of it, from one inventory to another and keeps its state: a piece of gear arrives with
    /// its level, upgrades, gem slot and durability (the same item entity), because the game's own slot transfer hands
    /// the item entity over inside its transaction, so players and the save follow like after a drag and drop. A take
    /// followed by a give would destroy the gear and make a new one.
    /// </summary>
    /// <param name="from">The inventory owner the stack leaves (a player, chest, villager).</param>
    /// <param name="slot">The slot in <paramref name="from"/>'s inventories (a <see cref="InventorySlot.Slot"/>).</param>
    /// <param name="to">The inventory owner it enters; not <paramref name="from"/>.</param>
    /// <param name="count">How many items; 0 = the whole stack. Gear is always 1.</param>
    /// <param name="options">See <see cref="MoveOptions"/>.</param>
    /// <param name="toSlot">A slot of <paramref name="to"/> to fill first; null = Kiln's choice (partial stacks of the
    /// item first, then the first empty slot). Into a player it must be a slot the item may enter: a backpack slot (an
    /// action-bar slot only with <see cref="MoveOptions.ActionBar"/>), a currency-page slot for a currency item, or a worn
    /// equipment slot whose equipment type is the item's, to top up the worn stack (arrows, bait). An empty equipment slot
    /// or the backpack slot is refused: a move is no equip, use <see cref="Equip"/>. Any other slot is <see cref="ItemsStatus.Refused"/> with the reason in
    /// <see cref="MoveResult.Message"/>, and nothing moves. A slot the item may enter that is full, or holds another item,
    /// gives way to Kiln's choice.</param>
    /// <remarks>
    /// <para>Runs in the next sim step; the task completes on the tick thread one step later. A player's equipped or held
    /// items are refused and the action bar is left alone unless <see cref="MoveOptions"/> says otherwise. Game-side
    /// outcomes never throw: read <see cref="MoveResult.Status"/>.</para>
    /// <para>Into a player or an NPC, Kiln's choice uses only the backpack: the Generic pages of the inventory and the
    /// worn backpack's pages, in the order <see cref="Read"/> lists them; never an equipment, currency or customization
    /// page, nor the action bar without <see cref="MoveOptions.ActionBar"/> (issue #109: a player's equipment pages come
    /// first, and a stack placed there is out of the player's sight). Nothing fits: <see cref="ItemsStatus.NoRoom"/>. A
    /// chest or other container: every page, as before.</para>
    /// <para><see cref="ItemsStatus.Done"/> means the items are where the player can take them: a stack move that put any
    /// of them elsewhere, or lost some on the way, is rolled back and <see cref="ItemsStatus.Refused"/>.</para>
    /// </remarks>
    /// <example><code>
    /// var r = await Items.Move(player, sword.Slot, chest);
    /// if (r.Succeeded) Log.Info($"moved {r.Changes[0].ItemEntity}");   // the same item entity as sword.ItemEntity
    /// </code></example>
    [RequiresCapability(Caps.Move)]
    [TickThreadOnly]
    public static Task<MoveResult> Move(EntityId from, SlotId slot, EntityId to, int count = 0,
        MoveOptions options = MoveOptions.None, SlotId? toSlot = null)
    {
        if (slot.IsNone) return MoveInvalid(MoveKind.Stack, from, to, "no slot");
        if (count < 0 || count > 100000) return MoveInvalid(MoveKind.Stack, from, to, "count must be 0-100000");
        var r = NewMove(MoveKind.Stack, from, to, options);
        r.Slot = slot.Value;
        r.Count = (uint)count;
        if (toSlot is { } ts) r.ToSlot = ts.Value;
        return SubmitMove(r, "Items.Move");
    }

    /// <summary>
    /// Runs the game's own quick stack from <paramref name="from"/> into <paramref name="to"/>: every stack of
    /// <paramref name="from"/> that <paramref name="to"/> already holds a stack of (what a player's R key does). Gear
    /// moves with its item entity. Transfer rules and events (<see cref="Transfers"/>) see it, flagged
    /// <see cref="InventoryTransferEvent.ByMod"/>, unless <see cref="MoveOptions.Silent"/>.
    /// </summary>
    [RequiresCapability(Caps.Move)]
    [TickThreadOnly]
    public static Task<MoveResult> QuickStack(EntityId from, EntityId to, MoveOptions options = MoveOptions.None) =>
        SubmitMove(NewMove(MoveKind.QuickStack, from, to, options), "Items.QuickStack");

    /// <summary>
    /// Runs the game's own move all from <paramref name="from"/> into <paramref name="to"/>: every stack that fits. A
    /// player's action bar stays unless <see cref="MoveOptions.ActionBar"/>. Gear moves with its item entity. Transfer
    /// rules and events see it, unless <see cref="MoveOptions.Silent"/>.
    /// </summary>
    [RequiresCapability(Caps.Move)]
    [TickThreadOnly]
    public static Task<MoveResult> MoveAll(EntityId from, EntityId to, MoveOptions options = MoveOptions.None) =>
        SubmitMove(NewMove(MoveKind.All, from, to, options), "Items.MoveAll");

    /// <summary>Move the selected inventory slot only while its item, count and optional stamped item entity still match.
    /// When a destination slot is supplied its snapshot must also match and the full count must land in that slot.
    /// Checks and transfer run together in the server inventory transaction. A stale selection returns SlotChanged.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="expected"/> is null.</exception>
    [RequiresCapability(Caps.Move)]
    [TickThreadOnly]
    public static Task<MoveResult> MoveChecked(EntityId from, InventorySlot expected, EntityId to, int count, InventorySlot? destination = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        Services.RequireTickThread("Items.MoveChecked");
        if (!CheckedMovesAvailable)
            return Task.FromResult(MoveResult.Fail(ItemsStatus.Unavailable, MoveKind.Stack, from, to, "checked moves are unavailable"));
        if (expected.IsEmpty || expected.Slot.IsNone || count < 1 || count > 100000 || count > expected.Count ||
            destination is { Slot.IsNone: true })
            return MoveInvalid(MoveKind.Stack, from, to, "invalid source, destination or amount");
        var r = NewMove(MoveKind.Stack, from, to, MoveOptions.None);
        r.Flags |= 0x10; // KILN_ITEMS_MOVE_CHECKED: older native services refuse this flag.
        r.Slot = expected.Slot.Value;
        r.ToSlot = destination?.Slot.Value ?? 0;
        r.Count = (uint)count;
        if (!Witness(expected, out r.Source) || (destination != null && !Witness(destination, out r.Destination)))
            return MoveInvalid(MoveKind.Stack, from, to, "the selected item has no usable instance stamp");
        return SubmitMove(r, "Items.MoveChecked");
    }

    private static bool Witness(InventorySlot slot, out ItemsNative.SlotWitness witness)
    {
        witness = default;
        if (slot.IsEmpty) return true;
        witness.Item = slot.Item.Value;
        witness.Count = (uint)slot.Count;
        if (slot.ItemEntity.IsNone) return !slot.IsGear;
        if (slot.Ref.Entity.Id != slot.ItemEntity || slot.Ref.Item != slot.Item ||
            IdentitySources.PlaceOf(slot.Ref.Entity.Serial) != EntitySourceOrder.Items) return false;
        witness.Pide = slot.ItemEntity.Value;
        witness.Stamp = StampOf(slot.Ref, nameof(slot));
        return witness.Stamp != 0;
    }

    private static ItemsNative.MoveRequest NewMove(MoveKind kind, EntityId from, EntityId to, MoveOptions options)
    {
        var r = new ItemsNative.MoveRequest
        {
            Size = (uint)sizeof(ItemsNative.MoveRequest),
            Kind = (uint)kind,
            From = from.Value,
            To = to.Value,
        };
        if (options.HasFlag(MoveOptions.DryRun)) r.Flags |= ItemsNative.MoveDryRun;
        if (options.HasFlag(MoveOptions.Equipped)) r.Flags |= ItemsNative.MoveEquipped;
        if (options.HasFlag(MoveOptions.ActionBar)) r.Flags |= ItemsNative.MoveActionBar;
        if (options.HasFlag(MoveOptions.Silent)) r.Flags |= ItemsNative.MoveSilent;
        return r;
    }

    private static Task<MoveResult> MoveInvalid(MoveKind kind, EntityId from, EntityId to, string why) =>
        Task.FromResult(MoveResult.Fail(ItemsStatus.Invalid, kind, from, to, why));

    private sealed class PendingMove(TaskCompletionSource<MoveResult> tcs, ModContext? mod)
    {
        public readonly TaskCompletionSource<MoveResult> Tcs = tcs;
        public readonly ModContext? Mod = mod;
        public ulong Ticket;
        public bool Unseen;  // a lab move kept away from the item tracker (Items.LabMoveUnseen)
    }

    private static readonly object MoveGate = new();
    private static readonly Dictionary<long, PendingMove> MovesInFlight = new();
    private static readonly HashSet<ModContext> MovesWatched = new();
    private static long _nextMove;

    private static Task<MoveResult> SubmitMove(ItemsNative.MoveRequest r, string api, bool unseen = false)
    {
        var t = Require3(api, Caps.Move);
        var kind = (MoveKind)r.Kind;
        var from = new EntityId(r.From);
        var to = new EntityId(r.To);
        if (r.From == 0 || r.To == 0) return MoveInvalid(kind, from, to, "no entity");
        if (r.From == r.To) return MoveInvalid(kind, from, to, "from and to are the same entity");
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        var tcs = new TaskCompletionSource<MoveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new PendingMove(tcs, mod) { Unseen = unseen };
        long key;
        lock (MoveGate)
        {
            key = ++_nextMove;
            MovesInFlight[key] = p;
            if (mod != null && MovesWatched.Add(mod)) mod.Retiring += () => RetireMoves(mod);
        }
        ulong ticket = 0;
        var rc = t->Move(Services.CallerOwner, &r, &OnMoveDone, (void*)key, &ticket);
        if (rc != ItemsNative.Ok)
        {
            lock (MoveGate) MovesInFlight.Remove(key);
            var status = rc == ItemsNative.InvalidArgument ? ItemsStatus.Invalid : ItemsStatus.Unavailable;
            var why = rc == ItemsNative.InvalidArgument
                ? "refused: bad arguments (entities, slot, count or flags)"
                : "moves are not available: " + (Status3()?.Detail is { Length: > 0 } d ? d : Status()?.Detail ?? "kiln.items is not loaded");
            return Task.FromResult(MoveResult.Fail(status, kind, from, to, why));
        }
        lock (MoveGate)
        {
            if (MovesInFlight.TryGetValue(key, out var still)) still.Ticket = ticket;
        }
        return tcs.Task;
    }

    private static void RetireMoves(ModContext mod)
    {
        List<ulong> tickets = new();
        lock (MoveGate)
        {
            MovesWatched.Remove(mod);
            foreach (var (k, p) in MovesInFlight.Where(kv => kv.Value.Mod == mod).ToList())
            {
                MovesInFlight.Remove(k);
                if (p.Ticket != 0) tickets.Add(p.Ticket);
            }
        }
        var t = _table;
        if (t != null) foreach (var tk in tickets) t->Cancel(tk);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnMoveDone(ItemsNative.MoveResult* r, void* user)
    {
        try
        {
            PendingMove? p;
            lock (MoveGate)
            {
                if (!MovesInFlight.Remove((long)user, out p)) return;
            }
            var changes = new MoveChange[Math.Min(r->ChangeCount, 16u)];
            var c = (ItemsNative.MoveChange*)r->Changes;
            for (var i = 0; i < changes.Length; i++)
                changes[i] = new MoveChange(new SlotId(c[i].FromSlot), new SlotId(c[i].ToSlot), new ItemId(c[i].Item),
                    (int)c[i].Amount, new EntityId(c[i].Pide))
                {
                    Ref = ItemRefs.Of(c[i].Pide, c[i].Item, c[i].Reserved),
                };
            var status = Enum.IsDefined(typeof(ItemsStatus), r->Status) ? (ItemsStatus)r->Status : ItemsStatus.Refused;
            var result = new MoveResult(status, (MoveKind)r->Kind, new EntityId(r->From), new EntityId(r->To),
                (int)r->Requested, (int)r->Done, (int)r->Stacks, changes, ItemsNative.Text(r->Message, 160),
                (int)r->GameError, r->Canceled != 0, (int)r->Steps, (r->Flags & ItemsNative.MoveDryRun) != 0);
            if (!p.Unseen) ItemTracker.OnMove(result, (r->Flags & ItemsNative.MoveSilent) != 0);
            p.Tcs.TrySetResult(result);
        }
        catch
        {
            // never let an exception cross into native code
        }
    }

    // ------------------------------------------------------------------------------------------- transfers ------

    /// <summary>
    /// Rules and events for the game's quick stack and move all: what a player does with the R key or a Quick Stack
    /// Station, what <see cref="QuickStack"/> and <see cref="MoveAll"/> do, and anything else that calls those two
    /// functions. Ported from shroudlink's inventory events (<c>keep</c>, <c>on_transfer</c>).
    /// </summary>
    /// <remarks>
    /// <para>Nothing is hooked until the first rule is added or a handler subscribes to
    /// <see cref="InventoryTransferEvent"/>; with none, the game's calls are untouched. The hooks stay once installed
    /// (so another loader's hook on the same functions, kiln-shroudlink's, is never undone) and cost one flag read per
    /// call while nothing is registered.</para>
    /// <para>The game runs a transfer on a worker thread inside the sim step, where managed code cannot run. So a
    /// decision is data, not a callback: <see cref="Keep"/>, <see cref="KeepItem"/> and <see cref="Deny"/> are rules the
    /// hook reads there, and <see cref="InventoryTransferEvent"/> reports what happened on the tick thread in the next
    /// Pre. A native plugin that must decide per transfer registers a handler with <c>transfer_listen</c>
    /// (kiln_items.h). To stop a player's transfer by a decision made on the tick thread, cancel the held client
    /// action: <c>PlayerClientActionEvent</c> for <c>inventoryTransferAction</c> (Kiln.Players), where
    /// <see cref="ParseAction"/> reads its payload.</para>
    /// </remarks>
    public static class Transfers
    {
        /// <summary>True when the hooks can be installed on this game build (capability <c>items.transfer</c>).</summary>
        public static bool Available => Can(Caps.Transfer);

        /// <summary>The stack in <paramref name="slot"/> stays where it is while it lies in the source inventory of a
        /// transfer (a locked slot). Dispose the rule to end it; so does the mod's unload.</summary>
        /// <exception cref="NotSupportedException">The hooks are not available on this build.</exception>
        [RequiresCapability(Caps.Transfer)]
        public static TransferRule Keep(SlotId slot, TransferKinds kinds = TransferKinds.Any)
        {
            if (slot.IsNone) throw new ArgumentException("no slot", nameof(slot));
            return Add(new ItemsNative.TransferRule
            {
                Type = ItemsNative.RuleKeepSlot, Kinds = (uint)kinds, Slot = slot.Value,
            }, $"keep {slot}");
        }

        /// <summary>Stacks of <paramref name="item"/> stay in the source inventory of a transfer; with
        /// <paramref name="owner"/> only when that entity's inventory is the source.</summary>
        [RequiresCapability(Caps.Transfer)]
        public static TransferRule KeepItem(ItemId item, EntityId owner = default, TransferKinds kinds = TransferKinds.Any)
        {
            if (item.IsNone) throw new ArgumentException("no item", nameof(item));
            return Add(new ItemsNative.TransferRule
            {
                Type = ItemsNative.RuleKeepItem, Kinds = (uint)kinds, Item = item.Value, Entity = owner.Value,
            }, $"keep {item}");
        }

        /// <summary>A transfer into or out of <paramref name="container"/> does not run (the caller is told it moved
        /// nothing).</summary>
        [RequiresCapability(Caps.Transfer)]
        public static TransferRule Deny(EntityId container, TransferKinds kinds = TransferKinds.Any)
        {
            if (container.IsNone) throw new ArgumentException("no entity", nameof(container));
            return Add(new ItemsNative.TransferRule
            {
                Type = ItemsNative.RuleDeny, Kinds = (uint)kinds, Entity = container.Value,
            }, $"deny {container}");
        }

        /// <summary>
        /// Reads the payload of a client's <c>inventoryTransferAction</c> (32 bytes: the 4-byte version, source and target
        /// entity, source and target slot, the <c>InventoryActionType</c>, the flags and the amount), as
        /// <c>PlayerClientActionEvent.Payload</c> carries it. Null when it is not 32 bytes.
        /// </summary>
        public static ClientTransfer? ParseAction(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 32) return null;
            var flags = payload[29];
            return new ClientTransfer(new EntityId(BitConverter.ToUInt32(payload[4..])), new EntityId(BitConverter.ToUInt32(payload[8..])),
                new SlotId(BitConverter.ToUInt64(payload[12..])), new SlotId(BitConverter.ToUInt64(payload[20..])),
                (ClientTransferAmount)payload[28], BitConverter.ToUInt16(payload[30..]),
                (flags & 0x02) != 0, (flags & 0x04) != 0, (flags & 0x01) != 0);
        }

        /// <summary>The hooks' and rules' state, counters included.</summary>
        public static ItemsV3Status? Status() => Status3();

        private static TransferRule Add(ItemsNative.TransferRule r, string what)
        {
            var t = RequireV3("Items.Transfers", Caps.Transfer);
            r.Size = (uint)sizeof(ItemsNative.TransferRule);
            ulong handle = 0;
            var rc = t->TransferRuleAdd(Services.CallerOwner, &r, &handle);
            if (rc == ItemsNative.InvalidArgument) throw new ArgumentException("the rule is not valid");
            if (rc != ItemsNative.Ok)
                throw ServiceCaps.Unavailable(Caps.Transfer, "Items.Transfers: " + (Status3()?.Detail is { Length: > 0 } d ? d : "the hooks are not available (or 256 rules are active)"));
            var rule = new TransferRule(handle, what);
            var mod = ModContext.CurrentMod;
            if (mod != null)
            {
                Action retire = rule.Dispose;
                mod.Retiring += retire;
                rule.Detach = () => mod.Retiring -= retire;
            }
            return rule;
        }

        // ---- the event: a native report listener exists exactly while someone subscribes ----
        private static ulong _reporter;
        private static IDisposable? _watch;
        private static bool _failed;

        internal static void Start()
        {
            _watch ??= ServiceTickAccess.Add("kiln.items transfer reports", Sync);
        }

        private static void Sync(ulong step)
        {
            var want = Events.HasSubscribers<InventoryTransferEvent>();
            if (want == (_reporter != 0) || (want && _failed)) return;
            var t = V3;
            if (t == null) return;
            if (want)
            {
                ulong handle = 0;
                var rc = t->TransferReportAdd(0UL, &OnReport, null, &handle);
                if (rc == ItemsNative.Ok) _reporter = handle;
                else
                {
                    _failed = true;  // not retried until a restart: the build has no hooks
                    Backend.Current.Log(0, LogLevel.Warn, $"kiln.items: InventoryTransferEvent is off: {Status3()?.Detail}");
                }
            }
            else
            {
                t->TransferEnd(_reporter);
                _reporter = 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnReport(ItemsNative.TransferReport* r, void* user)
        {
            try
            {
                if (!Events.HasSubscribers<InventoryTransferEvent>()) return;
                Events.Post(new InventoryTransferEvent((TransferKind)r->Kind, new EntityId(r->From), new EntityId(r->To),
                    new EntityId(r->Player), (r->Flags & ItemsNative.TransferByMod) != 0, (r->Flags & ItemsNative.TransferCanceled) != 0,
                    (int)r->Moved, (int)r->Kept, r->Step));
            }
            catch
            {
                // never let an exception cross into native code
            }
        }

        internal static void End(ulong handle)
        {
            var t = _v3;
            if (t != null && handle != 0) t->TransferEnd(handle);
        }
    }
}

internal static class ServiceTickAccess
{
    public static IDisposable Add(string name, Action<ulong> step) => ServiceTick.Add(name, (in TickInfo t) => step(t.Step));
}

/// <summary>A transfer rule (<see cref="Items.Transfers"/>); dispose it to end it.</summary>
public sealed class TransferRule : IDisposable
{
    private ulong _handle;
    internal Action? Detach;

    internal TransferRule(ulong handle, string what)
    {
        _handle = handle;
        What = what;
    }

    /// <summary>What the rule does, for logs.</summary>
    public string What { get; }

    /// <summary>True until the rule is disposed.</summary>
    public bool IsActive => Volatile.Read(ref _handle) != 0;

    /// <summary>Ends the rule. Calling it again does nothing.</summary>
    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h == 0) return;
        Items.Transfers.End(h);
        Detach?.Invoke();
        Detach = null;
    }

    /// <summary>What the rule does, and whether it has ended, for logs.</summary>
    public override string ToString() => $"transfer rule: {What}{(IsActive ? "" : " (ended)")}";
}

/// <summary>What a mod can move: one stack, the game's quick stack, or its move all.</summary>
public enum MoveKind
{
    /// <summary>One stack from one slot to another inventory.</summary>
    Stack = 0,
    /// <summary>The game's quick stack: stacks the target already holds a stack of.</summary>
    QuickStack = 1,
    /// <summary>The game's move all: every stack that fits.</summary>
    All = 2,
}

/// <summary>Options of a move (<see cref="Items.Move"/>, <see cref="Items.QuickStack"/>,
/// <see cref="Items.MoveAll"/>). Combine them with <c>|</c>.</summary>
[Flags]
public enum MoveOptions
{
    /// <summary>No options: the defaults of each flag below apply.</summary>
    None = 0,
    /// <summary>Run and roll back: the result says what would move. Transfer rules and native handlers still run,
    /// but no <see cref="InventoryTransferEvent"/> is emitted.</summary>
    DryRun = 1,
    /// <summary>A stack move may take a slot a player has equipped or holds (default: refused).</summary>
    Equipped = 2,
    /// <summary>A stack move may take from and fill a player's action-bar pages (default: left alone); a move all takes
    /// the action bar along (default: it stays).</summary>
    ActionBar = 4,
    /// <summary>A quick stack or move all raises no transfer rules or events: the caller is itself a handler.</summary>
    Silent = 8,
}

/// <summary>One piece of a move's ledger: <see cref="Amount"/> of <see cref="Item"/> left <see cref="From"/> and arrived
/// in <see cref="To"/>. Gear carries its <see cref="ItemEntity"/> along (the same one before and after).</summary>
/// <param name="From">The slot the items left.</param>
/// <param name="To">The slot they arrived in.</param>
/// <param name="Item">The item that moved.</param>
/// <param name="Amount">How many moved.</param>
/// <param name="ItemEntity">The item entity of a piece of gear that moved whole; none for a plain stack.</param>
public readonly record struct MoveChange(SlotId From, SlotId To, ItemId Item, int Amount, EntityId ItemEntity)
{
    /// <summary>The item entity that moved, as one particular item: it moves whole, stamp and all, so this equals its
    /// <c>Ref</c> in a read before and after the move. <see cref="ItemInstanceRef.None"/> for a plain stack (no item
    /// entity) and when the capability <c>items.instances</c> is off (an older Kiln, an unknown game build). Filled by
    /// Kiln when it builds the value.</summary>
    [AnyThread]
    public ItemInstanceRef Ref { get; init; }
}

/// <summary>What a move did. <see cref="Status"/> is <see cref="ItemsStatus.Done"/> (all moved),
/// <see cref="ItemsStatus.Partial"/> (only <see cref="Done"/> of <see cref="Requested"/> fit),
/// <see cref="ItemsStatus.NoRoom"/> (nothing moved), <see cref="ItemsStatus.NotEnough"/> (the count is more than the
/// stack holds), <see cref="ItemsStatus.NoInventory"/>, <see cref="ItemsStatus.SlotChanged"/> (the slot is not the
/// source's, or empty), <see cref="ItemsStatus.Refused"/> (equipped slot; the game refused; a rule or handler canceled
/// it: <see cref="Canceled"/>), <see cref="ItemsStatus.Unavailable"/>, <see cref="ItemsStatus.Dropped"/>.</summary>
/// <param name="Status">How the move ended; see the type's summary.</param>
/// <param name="Kind">Which move it was.</param>
/// <param name="From">The inventory owner the items left.</param>
/// <param name="To">The inventory owner they went to.</param>
/// <param name="Requested">A stack move: the count asked for (the stack's size when none was given). 0 for the other
/// kinds.</param>
/// <param name="Done">How many items moved (a dry run: how many would).</param>
/// <param name="Stacks">How many source stacks gave something.</param>
/// <param name="Changes">The moves, slot by slot (the first ones only; <see cref="Done"/> counts them all).</param>
/// <param name="Message">Why it failed or fell short, in words; empty when it went through.</param>
/// <param name="GameError">The game's own error code when it refused; 0 otherwise.</param>
/// <param name="Canceled">True when a transfer rule or handler canceled the game's quick stack or move all.</param>
/// <param name="Steps">How many sim steps passed between the request and the move.</param>
/// <param name="DryRun">True when nothing moved: the result says what would.</param>
public sealed record MoveResult(
    ItemsStatus Status,
    MoveKind Kind,
    EntityId From,
    EntityId To,
    int Requested,
    int Done,
    int Stacks,
    IReadOnlyList<MoveChange> Changes,
    string Message,
    int GameError,
    bool Canceled,
    int Steps,
    bool DryRun)
{
    /// <summary>Something moved (or would, in a dry run).</summary>
    public bool Succeeded => Status is ItemsStatus.Done or ItemsStatus.Partial;

    /// <summary>The kind, the status, the counts and the two owners, for logs.</summary>
    public override string ToString() =>
        $"{Kind} {Status} {Done} item(s) in {Stacks} stack(s) {From} -> {To}{(DryRun ? " (dry run)" : "")}" + (Message.Length > 0 ? $": {Message}" : "");

    internal static MoveResult Fail(ItemsStatus status, MoveKind kind, EntityId from, EntityId to, string message) =>
        new(status, kind, from, to, 0, 0, 0, Array.Empty<MoveChange>(), message, 0, false, 0, false);
}

/// <summary>Which of the game's two inventory-wide transfers.</summary>
public enum TransferKind
{
    /// <summary>Quick stack: stacks the target already holds a stack of (the R key, a Quick Stack Station).</summary>
    QuickStack = 1,
    /// <summary>Move all: every stack that fits.</summary>
    MoveAll = 2,
}

/// <summary>Which of the game's inventory-wide transfers a rule applies to. Combine them with <c>|</c>.</summary>
[Flags]
public enum TransferKinds
{
    /// <summary>Quick stack.</summary>
    QuickStack = 1,
    /// <summary>Move all.</summary>
    MoveAll = 2,
    /// <summary>Both kinds.</summary>
    Any = 3,
}

/// <summary>What a client's <c>inventoryTransferAction</c> asks for (<see cref="Items.Transfers.ParseAction"/>).</summary>
public enum ClientTransferAmount : byte
{
    /// <summary>One item.</summary>
    One = 0,
    /// <summary>Half the stack.</summary>
    Half = 1,
    /// <summary>The whole stack.</summary>
    All = 2,
    /// <summary>The amount in <see cref="ClientTransfer.CustomAmount"/>.</summary>
    CustomAmount = 3,
    /// <summary>Destroy the items instead of moving them.</summary>
    Destroy = 4,
}

/// <summary>A client's <c>inventoryTransferAction</c>. The game reads its flags to pick the path:
/// <see cref="RefillStacks"/> is a quick stack (the R key held), <see cref="MoveAllStacks"/> a move all; neither is a
/// single stack from <see cref="SourceSlot"/> to <see cref="TargetSlot"/>.</summary>
/// <param name="Source">The inventory owner the items leave.</param>
/// <param name="Target">The inventory owner they go to.</param>
/// <param name="SourceSlot">The slot they leave (a single-stack transfer).</param>
/// <param name="TargetSlot">The slot they go to (a single-stack transfer).</param>
/// <param name="Amount">How much of the stack the client asks for.</param>
/// <param name="CustomAmount">The amount when <see cref="Amount"/> is
/// <see cref="ClientTransferAmount.CustomAmount"/>.</param>
/// <param name="RefillStacks">True for a quick stack (the R key held).</param>
/// <param name="MoveAllStacks">True for a move all.</param>
/// <param name="Gifting">True when the player gives the items to another player.</param>
public readonly record struct ClientTransfer(EntityId Source, EntityId Target, SlotId SourceSlot, SlotId TargetSlot,
    ClientTransferAmount Amount, int CustomAmount, bool RefillStacks, bool MoveAllStacks, bool Gifting);

/// <summary>The game's quick stack or move all ran and kiln.items saw it: posted on the tick thread in the Pre after
/// the step it ran in, while a handler subscribes (otherwise nothing is hooked). Not cancelable (it already ran): to
/// stop or shape a transfer add <see cref="Items.Transfers"/> rules, or cancel the held client action.</summary>
public sealed class InventoryTransferEvent : KilnEvent
{
    // Kiln runs this when a handler subscribes (Events.StartProducers): from then on a service tick registers or ends the
    // native report listener as subscribers come and go.
    static InventoryTransferEvent() => Items.Transfers.Start();

    internal InventoryTransferEvent(TransferKind kind, EntityId from, EntityId to, EntityId player, bool byMod,
        bool canceled, int moved, int kept, ulong step)
    {
        Kind = kind;
        From = from;
        To = to;
        Player = player;
        ByMod = byMod;
        Canceled = canceled;
        Moved = moved;
        Kept = kept;
        Step = step;
    }

    /// <summary>Which transfer it was: quick stack or move all.</summary>
    public TransferKind Kind { get; }

    /// <summary>The inventory owner the items left.</summary>
    public EntityId From { get; }

    /// <summary>The inventory owner they entered.</summary>
    public EntityId To { get; }

    /// <summary>Whichever of <see cref="From"/> and <see cref="To"/> is a player's inventory; none if neither.</summary>
    public EntityId Player { get; }

    /// <summary>A <see cref="Items.QuickStack"/> or <see cref="Items.MoveAll"/> of a mod, not the game's own caller.</summary>
    public bool ByMod { get; }

    /// <summary>A rule canceled the game's transfer.</summary>
    public bool Canceled { get; }

    /// <summary>Items the game's transfer moved.</summary>
    public int Moved { get; }

    /// <summary>Stacks hidden from the game's transfer by rules.</summary>
    public int Kept { get; }

    /// <summary>The sim step it ran in.</summary>
    public ulong Step { get; }

    /// <summary>The kind, the two owners and the counts, for logs.</summary>
    public override string ToString() =>
        $"{Kind} {From} -> {To}: {Moved} item(s){(Canceled ? ", canceled" : "")}{(Kept > 0 ? $", {Kept} stack(s) kept" : "")}{(ByMod ? ", by a mod" : "")}";
}

/// <summary>State of kiln.items' v3 parts.</summary>
/// <param name="Moves">True when <see cref="Items.Move"/>, <see cref="Items.QuickStack"/> and
/// <see cref="Items.MoveAll"/> work.</param>
/// <param name="Names">True when <see cref="Items.ContainerName"/> works on this game build.</param>
/// <param name="TransferHooks">True when the quick stack and move all hooks can be installed.</param>
/// <param name="HooksInstalled">True when they are installed (a rule or handler was registered once).</param>
/// <param name="Rules">Active transfer rules.</param>
/// <param name="Handlers">Active native transfer handlers.</param>
/// <param name="Reporters">Active listeners of transfer reports.</param>
/// <param name="TransfersSeen">Calls of the game's quick stack and move all since the hooks went in.</param>
/// <param name="TransfersCanceled">How many of those a rule or handler canceled.</param>
/// <param name="MovesDone">Move requests that moved something.</param>
/// <param name="NamesFound">Container name lookups that found a name.</param>
/// <param name="Detail">The reason when a part is off; empty otherwise.</param>
public sealed record ItemsV3Status(bool Moves, bool Names, bool TransferHooks, bool HooksInstalled, int Rules, int Handlers,
    int Reporters, ulong TransfersSeen, ulong TransfersCanceled, ulong MovesDone, int NamesFound, string Detail);
