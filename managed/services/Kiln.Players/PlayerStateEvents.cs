// SPDX-License-Identifier: MIT
// Player state and actions on the event bus: one scan per tick while anyone subscribes (docs/kiln/events.md).
using System.Diagnostics;

namespace Kiln;

/// <summary>Any player state changed: <see cref="State"/> was entered or left. Fires for every state in
/// <see cref="PlayerStates.All"/>, and for states this Kiln has no constant for (a newer game: raw id, <see cref="Name"/>
/// has the game's name). Posted in Pre of the step after the change; on a joined client, for the local player, on the main
/// thread. Observe only.</summary>
public sealed class PlayerStateChangedEvent : KilnEvent
{
    static PlayerStateChangedEvent() => PlayerStateWatch.Start();

    internal PlayerStateChangedEvent(Player p, PlayerStateId state, bool entered, ulong step, PlayerSnapshot snapshot)
    {
        Player = p;
        State = state;
        Entered = entered;
        Step = step;
        Snapshot = snapshot;
    }

    public Player Player { get; }
    public PlayerStateId State { get; }
    /// <summary>True: the player entered <see cref="State"/>. False: left it.</summary>
    public bool Entered { get; }
    public bool Left => !Entered;
    public ulong Step { get; }
    /// <summary>Everything else at that moment (states and action; health only in a read of its own).</summary>
    public PlayerSnapshot Snapshot { get; }
    /// <summary>The game's name of the state.</summary>
    public string Name => PlayerStates.NameOf(State);
    public PlayerStateSource Source => PlayerStates.SourceOf(State);

    public override string ToString() => $"{Player.Name} {(Entered ? "entered" : "left")} {State} ({Name})";
}

/// <summary>A player started an action: anything the game runs as an action sequence. Every input that does something
/// (attack, dodge, jump, item use, interact, glide, grapple, emote, ...) and every sequence the game starts by itself
/// (hit reaction, death, respawn). <see cref="Trigger"/> says what started it.
/// <para><see cref="CancelableEvent.Cancel"/> stops the sequence before its next event, the way the game ends a sequence
/// whose keep-alive failed: nothing it would do from now on happens. Events in its first step have already run (they are
/// rare: <c>UseItem_Scroll_Meteor</c>, some costs). Server world only (dedicated server and host): on a joined client the server
/// decides, and <see cref="CanCancel"/> is false. The client that started the action played its first frames already;
/// what it shows after a cancel: docs/kiln/services/player-state.md.</para></summary>
public class PlayerActionEvent : CancelableEvent
{
    static PlayerActionEvent() => PlayerStateWatch.Start();

    internal PlayerActionEvent(Player p, PlayerAction action, ulong step, bool canCancel)
    {
        Player = p;
        Action = action;
        Step = step;
        CanCancel = canCancel;
    }

    public Player Player { get; }
    public PlayerAction Action { get; }
    public ulong Step { get; }
    /// <summary>False where a cancel has no effect: a joined client, or a build where the game's reset did not resolve.</summary>
    public bool CanCancel { get; }

    /// <summary>What started it: <see cref="PlayerStateId.ActionPrimary"/>, <see cref="PlayerStateId.ActionEvade"/>,
    /// <see cref="PlayerStateId.ActionGlider"/>, ...</summary>
    public PlayerStateId Trigger => Action.Trigger;
    /// <summary>The input behind it, when one: <see cref="PlayerStateId.InputQuickUse0"/>, ...</summary>
    public PlayerStateId Input => Action.Input;
    /// <summary>The item it uses; <see cref="PlayerItem.IsNone"/> when none.</summary>
    public PlayerItem Item => Action.Item;
    /// <summary>The game's name of what plays: "UseItem_Food_Buff", "Movement_Tool_HangGlider_Start".</summary>
    public string SequenceName => Action.SequenceName;

    public override string ToString() => $"{Player.Name} started {SequenceName} ({Trigger}), item {Item}";
}

/// <summary>What a consumable does, by the game's own sequence for it.</summary>
public enum PlayerConsumeKind
{
    Other,
    /// <summary>Food: sequences <c>UseItem_Food_Buff</c>, <c>_Heal_Buff</c>, <c>_Bad</c>.</summary>
    Food,
    /// <summary>Drink: <c>UseItem_Food_Liquid_*</c>. The game treats it as food otherwise.</summary>
    Drink,
    /// <summary>Potions and flasks: <c>UseItem_Potion_*</c>, <c>UseItem_Flask_*</c>.</summary>
    Potion,
    /// <summary><c>UseItem_Bandage_*</c>.</summary>
    Bandage,
    /// <summary><c>UseItem_Scroll_*</c>.</summary>
    Scroll,
}

/// <summary>A player started eating, drinking or using another consumable. Posted before the item is used up: food goes at
/// 0.4 s into its sequence, a drink at 0.05 s, a potion at 0.3 s. <see cref="CancelableEvent.Cancel"/>: no item
/// used, no buff. A hit or dodge can still stop it after this event: for "it really happened" use
/// <see cref="PlayerConsumedEvent"/>. <see cref="PlayerActionEvent"/> handlers get this event too.</summary>
public sealed class PlayerConsumeEvent : PlayerActionEvent
{
    static PlayerConsumeEvent() => PlayerStateWatch.Start();

    internal PlayerConsumeEvent(Player p, PlayerAction action, ulong step, bool canCancel, PlayerConsumeKind kind, IReadOnlyList<PlayerBuff> buffs)
        : base(p, action, step, canCancel)
    {
        Kind = kind;
        Buffs = buffs;
    }

    public PlayerConsumeKind Kind { get; }
    /// <summary>Buffs the item gives (its applied buff). Empty for items that work through an impact program, such as
    /// health potions.</summary>
    public IReadOnlyList<PlayerBuff> Buffs { get; }
    public bool IsFood => Kind == PlayerConsumeKind.Food;
    public bool IsDrink => Kind == PlayerConsumeKind.Drink;

    public override string ToString() => $"{Player.Name} consumes {Item} ({Kind})";
}

/// <summary>A consumable was really used: the stack in its slot went down during the use. Posted in Pre of the step after.
/// Items that are not used up (the refillable water pouch) never raise it. Observe only.</summary>
public sealed class PlayerConsumedEvent : KilnEvent
{
    static PlayerConsumedEvent() => PlayerStateWatch.Start();

    internal PlayerConsumedEvent(Player p, PlayerItem item, PlayerConsumeKind kind, IReadOnlyList<PlayerBuff> buffs, int left)
    {
        Player = p;
        Item = item;
        Kind = kind;
        Buffs = buffs;
        Left = left;
    }

    public Player Player { get; }
    public PlayerItem Item { get; }
    public PlayerConsumeKind Kind { get; }
    public IReadOnlyList<PlayerBuff> Buffs { get; }
    /// <summary>What is left in the slot.</summary>
    public int Left { get; }
}

/// <summary>A player's action ended: it finished, was replaced or was canceled. Observe only.</summary>
public sealed class PlayerActionEndedEvent : KilnEvent
{
    static PlayerActionEndedEvent() => PlayerStateWatch.Start();

    internal PlayerActionEndedEvent(Player p, PlayerAction action, TimeSpan seen, bool canceled)
    {
        Player = p;
        Action = action;
        Duration = seen;
        Canceled = canceled;
    }

    public Player Player { get; }
    public PlayerAction Action { get; }
    /// <summary>Wall time it was seen running.</summary>
    public TimeSpan Duration { get; }
    /// <summary>A handler of <see cref="PlayerActionEvent"/> canceled it.</summary>
    public bool Canceled { get; }
}

/// <summary>A player jumped. Observe only: cancel the action behind it through <see cref="PlayerActionEvent"/>.</summary>
public sealed class PlayerJumpEvent : KilnEvent
{
    static PlayerJumpEvent() => PlayerStateWatch.Start();

    internal PlayerJumpEvent(Player p, PlayerStateId type, int count)
    {
        Player = p;
        Type = type;
        Count = count;
    }

    public Player Player { get; }
    /// <summary><see cref="PlayerStateId.JumpDefault"/>, <see cref="PlayerStateId.JumpDoubleDefault"/>, ...</summary>
    public PlayerStateId Type { get; }
    /// <summary>Jumps since the last landing.</summary>
    public int Count { get; }
    public bool IsDoubleJump => Type is PlayerStateId.JumpDoubleDefault or PlayerStateId.JumpDoubleSprint or PlayerStateId.JumpDoubleMountain;
}

/// <summary>A player started or stopped gliding. To stop a glide before it starts, cancel the
/// <see cref="PlayerActionEvent"/> with <see cref="PlayerStateId.ActionGlider"/>. Observe only.</summary>
public sealed class PlayerGlideEvent : KilnEvent
{
    static PlayerGlideEvent() => PlayerStateWatch.Start();

    internal PlayerGlideEvent(Player p, bool started, PlayerItem glider, TimeSpan duration)
    {
        Player = p;
        Started = started;
        Glider = glider;
        Duration = duration;
    }

    public Player Player { get; }
    public bool Started { get; }
    public bool Ended => !Started;
    /// <summary>The glider in the player's equipment slot.</summary>
    public PlayerItem Glider { get; }
    /// <summary>How long the glide lasted (wall time); zero at the start.</summary>
    public TimeSpan Duration { get; }
}

/// <summary>Something changed in a player's equipment: seen within 4 steps. Observe only.</summary>
public sealed class PlayerEquipmentChangedEvent : KilnEvent
{
    static PlayerEquipmentChangedEvent() => PlayerStateWatch.Start();

    internal PlayerEquipmentChangedEvent(Player p, PlayerEquipSlot slot, PlayerItem before, PlayerItem now)
    {
        Player = p;
        Slot = slot;
        Before = before;
        Now = now;
    }

    public Player Player { get; }
    public PlayerEquipSlot Slot { get; }
    public PlayerItem Before { get; }
    public PlayerItem Now { get; }
    public bool Equipped => !Now.IsNone;
}

/// <summary>The selected action bar slot or the item in hand changed: seen within 4 steps. Observe only.</summary>
public sealed class PlayerHotbarChangedEvent : KilnEvent
{
    static PlayerHotbarChangedEvent() => PlayerStateWatch.Start();

    internal PlayerHotbarChangedEvent(Player p, int before, int now, PlayerItem heldBefore, PlayerItem held)
    {
        Player = p;
        Before = before;
        Now = now;
        HeldBefore = heldBefore;
        Held = held;
    }

    public Player Player { get; }
    public int Before { get; }
    public int Now { get; }
    public PlayerItem HeldBefore { get; }
    public PlayerItem Held { get; }
}

/// <summary>A client sent a versioned action: anything the game's menus and input send as a command, named by the game's own
/// field. <c>changeEquipmentAction</c>, <c>inventoryTransferAction</c>, <c>playerCraftingAction</c>, <c>teleportAction</c>,
/// <c>unlockSkillAction</c>, <c>selectDialogOptionAction</c>, <c>triggerEmoteAction</c>, <c>adminSetDaytime</c>: all of them are in
/// <see cref="PlayerStates.ClientActions"/>. Posted in Pre, before the game's program reads it.
/// <para><see cref="CancelableEvent.Cancel"/> marks it consumed, so the game's program skips it: the way the game itself
/// ignores an old version. The client is not told; what it shows depends on the action.</para></summary>
public sealed class PlayerClientActionEvent : CancelableEvent
{
    static PlayerClientActionEvent() => PlayerStateWatch.Start();

    internal PlayerClientActionEvent(Player p, PlayerClientActionInfo info, uint version, byte[] payload, bool canCancel)
    {
        Player = p;
        Action = info;
        Version = version;
        Payload = payload;
        CanCancel = canCancel;
    }

    public Player Player { get; }
    public PlayerClientActionInfo Action { get; }
    /// <summary>The game's field name: "changeEquipmentAction".</summary>
    public string Name => Action.Name;
    public uint Version { get; }
    /// <summary>The action's struct as the client sent it (layout: the game's reflection of the field's type).</summary>
    public byte[] Payload { get; }
    /// <summary>False: a joined client, or an action whose consumed version has no known field.</summary>
    public bool CanCancel { get; }

    public override string ToString() => $"{Player.Name} sent {Name} v{Version}";
}

/// <summary>The one scan behind the events: runs per tick only while a handler subscribes.</summary>
internal static unsafe class PlayerStateWatch
{
    private const int MaxPlayers = 64;
    private const int ExtraEvery = 6, EquipEvery = 4, RosterEvery = 60, ArmedMs = 60_000;

    private sealed class Slot
    {
        public uint Entity;
        public Player? Player;
        public bool HasPrev;
        public PlayerStates.Native.Snapshot Prev;
        public ushort DerivedOn, DerivedKnown, ActionBits;
        public PlayerAction? Running;
        public long RunningSinceMs;
        public long GlideSinceMs;
        public PlayerStates.Native.Equipment Equip;
        public bool HasEquip;
        // the consumable in use, to see it go
        public ulong UseSlot;
        public uint UseItem, UseStack, UseSequence;
        public long UseTime;
        public int UseGrace;
        public PlayerConsumeKind UseKind;
        public IReadOnlyList<PlayerBuff> UseBuffs = [];
    }

    private static readonly object Lock = new();
    private static IDisposable? _tick;
    private static readonly List<Slot> Slots = new();
    private static readonly HashSet<uint> Extra = new();
    private static ulong _rosterSeq = ulong.MaxValue;
    private static long _armedUntil;
    private static long _ticks;
    private static uint[] _ids = new uint[MaxPlayers];
    private static PlayerStates.Native.Snapshot[] _cur = new PlayerStates.Native.Snapshot[MaxPlayers];
    private static volatile Dictionary<uint, PlayerSnapshot>? _last;
    private static string[]? _equipCheck;
    private static bool? _cancelSupported;
    internal static long LastPumpNs, PumpRuns, PumpTotalNs;

    public static void Start()
    {
        lock (Lock)
        {
            if (_tick is not null) return;
            _tick = ServiceTick.Add("kiln.playerstate events", Pump);
        }
    }

    public static void ArmForOtherThreads()
    {
        Volatile.Write(ref _armedUntil, Environment.TickCount64 + ArmedMs);
        Start();
    }

    public static PlayerSnapshot? LastSnapshot(EntityId e) => _last?.GetValueOrDefault(e.Value);

    public static void WatchExtra(EntityId e, bool on)
    {
        lock (Lock)
        {
            if (on) Extra.Add(e.Value);
            else Extra.Remove(e.Value);
            _rosterSeq = ulong.MaxValue;
        }
        Start();
    }

    private static void Refresh()
    {
        var seq = Players.LastSequence;
        if (seq == _rosterSeq && _ticks % RosterEvery != 0) return;
        _rosterSeq = seq;
        var keep = new List<Slot>();
        var seen = new HashSet<uint>();
        IReadOnlyList<Player> all = [];
        try
        {
            if (Players.Available) all = Players.All;
        }
        catch (NotSupportedException)
        {
        }
        void Add(uint e, Player? p)
        {
            if (e == 0 || !seen.Add(e)) return;
            var s = Slots.Find(x => x.Entity == e) ?? new Slot { Entity = e };
            s.Player = p ?? s.Player ?? new Player { Entity = new EntityId(e), State = PlayerState.InGame };
            keep.Add(s);
        }
        foreach (var p in all) Add(p.Entity.Value, p);
        lock (Lock)
            foreach (var e in Extra) Add(e, null);
        var t = PlayerStates.T;
        if (t != null && t->Header.Size >= (uint)sizeof(PlayerStates.Native.Table) && t->LocalPlayer != null)
        {
            var local = t->LocalPlayer();
            if (local != 0) Add(local, null);
        }
        Slots.Clear();
        Slots.AddRange(keep);
        if (Slots.Count > MaxPlayers) Slots.RemoveRange(MaxPlayers, Slots.Count - MaxPlayers);
    }

    private static void Pump(in TickInfo tick)
    {
        var stateSubs = Events.HasSubscribers<PlayerStateChangedEvent>();
        var actionSubs = Events.HasSubscribers<PlayerActionEvent>() || Events.HasSubscribers<PlayerConsumeEvent>() ||
                         Events.HasSubscribers<PlayerActionEndedEvent>() || Events.HasSubscribers<PlayerConsumedEvent>();
        var jumpSubs = Events.HasSubscribers<PlayerJumpEvent>();
        var glideSubs = Events.HasSubscribers<PlayerGlideEvent>();
        var equipSubs = Events.HasSubscribers<PlayerEquipmentChangedEvent>() || Events.HasSubscribers<PlayerHotbarChangedEvent>();
        var clientSubs = Events.HasSubscribers<PlayerClientActionEvent>();
        var armed = Volatile.Read(ref _armedUntil) > Environment.TickCount64;
        if (!stateSubs && !actionSubs && !jumpSubs && !glideSubs && !equipSubs && !clientSubs && !armed)
        {
            if (Slots.Count > 0)
            {
                Slots.Clear();
                _rosterSeq = ulong.MaxValue;
            }
            _last = null;
            return;
        }
        var t = PlayerStates.T;
        if (t == null) return;
        var t0 = Stopwatch.GetTimestamp();
        _ticks++;
        Refresh();
        var n = Slots.Count;
        if (n == 0) return;
        for (var i = 0; i < n; i++)
        {
            _ids[i] = Slots[i].Entity;
            _cur[i].Size = (uint)sizeof(PlayerStates.Native.Snapshot);
        }
        // Only what someone listens to is read: one component per player for actions alone.
        var extra = (stateSubs || armed) && (_ticks % ExtraEvery == 0 || !Slots[0].HasPrev);
        var want = PlayerStates.Native.WantActor;
        if (stateSubs || jumpSubs || armed) want |= PlayerStates.Native.WantMotion;
        if (stateSubs || clientSubs || armed) want |= PlayerStates.Native.WantInput;
        if (extra) want |= PlayerStates.Native.WantExtra | PlayerStates.Native.WantBaseZone;
        if (armed) want |= PlayerStates.Native.WantVitals;
        int rc;
        fixed (uint* ids = _ids)
        fixed (PlayerStates.Native.Snapshot* cur = _cur)
            rc = t->Scan(ids, (uint)n, want, cur);
        if (rc != PlayerStates.Native.Ok) return;
        _cancelSupported ??= PlayerStates.Status is { Cancel: true };
        var canCancel = _cancelSupported == true && _cur[0].World == 0;
        for (var i = 0; i < n; i++)
        {
            var s = Slots[i];
            if (_cur[i].Known == 0)
            {
                s.HasPrev = false;
                continue;
            }
            if (clientSubs) PostClientActions(s, in _cur[i], canCancel, t);
            Process(s, ref _cur[i], want, extra, stateSubs, actionSubs, jumpSubs, glideSubs, canCancel, tick.Step, t);
        }
        if (equipSubs && _ticks % EquipEvery == 0) PumpEquipment(t);
        if (armed)
        {
            var d = new Dictionary<uint, PlayerSnapshot>(n);
            for (var i = 0; i < n; i++)
                if (_cur[i].Known != 0) d[_cur[i].Entity] = new PlayerSnapshot(in _cur[i]);
            _last = d;
        }
        var ns = (Stopwatch.GetTimestamp() - t0) * (1_000_000_000L / Stopwatch.Frequency);
        LastPumpNs = ns;
        PumpTotalNs += ns;
        PumpRuns++;
    }

    private static void PostClientActions(Slot s, in PlayerStates.Native.Snapshot cur, bool canCancel, PlayerStates.Native.Table* t)
    {
        var bit = 1u << PlayerStates.Native.KnownClientActions;
        if (!s.HasPrev || (cur.Known & bit) == 0 || (s.Prev.Known & bit) == 0) return;
        var all = PlayerStates.ClientActions;
        fixed (uint* now = cur.ClientVersions)
        fixed (uint* was = s.Prev.ClientVersions)
            for (var i = 0; i < all.Count; i++)
            {
                if (now[i] == was[i]) continue;
                var info = all[i];
                var payload = new byte[info.Bytes];
                fixed (byte* b = payload)
                {
                    uint n = 0;
                    t->ClientActionBytes(cur.Entity, (uint)i, b, (uint)payload.Length, &n);
                }
                var ev = Events.Post(new PlayerClientActionEvent(s.Player!, info, now[i], payload, canCancel && info.CanCancel));
                if (!ev.Canceled) continue;
                if (ev.CanCancel) t->ClientActionCancel(cur.Entity, (uint)i);
                else ModLogOnce("a handler canceled a client action where that has no effect (a joined client, or an action with no consumed version)");
            }
    }

    private static void Process(Slot s, ref PlayerStates.Native.Snapshot cur, uint want, bool extra, bool stateSubs, bool actionSubs, bool jumpSubs, bool glideSubs,
        bool canCancel, ulong step, PlayerStates.Native.Table* t)
    {
        if (!s.HasPrev)
        {
            s.ActionBits = PlayerStates.ActionBits(in cur);
            PlayerStates.ComputeDerived(in cur, s.ActionBits, out s.DerivedOn, out s.DerivedKnown);
            s.Prev = cur;
            s.HasPrev = true;
            s.Running = cur.Action.Sequence != 0 && cur.HasAction != 0 ? PlayerStates.ActionOf(in cur) : null;
            s.RunningSinceMs = Environment.TickCount64;
            return;
        }
        var player = s.Player!;
        var actionChanged = cur.HasAction != 0 && (cur.Action.Sequence != s.Prev.Action.Sequence || cur.Action.TriggerTime != s.Prev.Action.TriggerTime);
        if (actionChanged)
        {
            // what ran before has ended
            if (s.Running is { } was && actionSubs && Events.HasSubscribers<PlayerActionEndedEvent>())
                Events.Post(new PlayerActionEndedEvent(player, was, TimeSpan.FromMilliseconds(Environment.TickCount64 - s.RunningSinceMs), false));
            s.Running = null;
            if (cur.Action.Sequence != 0)
            {
                var action = PlayerStates.ActionOf(in cur);
                s.Running = action;
                s.RunningSinceMs = Environment.TickCount64;
                if (actionSubs && StartAction(s, player, action, ref cur, canCancel, step, t))
                {
                    // canceled: over at once, and the read above still shows the sequence's own state bits
                    if (Events.HasSubscribers<PlayerActionEndedEvent>())
                        Events.Post(new PlayerActionEndedEvent(player, action, TimeSpan.Zero, true));
                    s.Running = null;
                    var e = cur.Entity;
                    cur.Size = (uint)sizeof(PlayerStates.Native.Snapshot);
                    fixed (PlayerStates.Native.Snapshot* c = &cur)
                        t->Scan(&e, 1, want, c);
                }
            }
        }
        if (actionChanged) s.ActionBits = PlayerStates.ActionBits(in cur);
        if (s.UseItem != 0) WatchConsume(s, player, t);
        if (jumpSubs && cur.HasJump != 0 && cur.LastJumpTime != s.Prev.LastJumpTime && cur.LastJumpTime != 0)
        {
            ulong v;
            fixed (ulong* p = cur.Values) v = p[9];
            Events.Post(new PlayerJumpEvent(player, PlayerStates.IdOfRaw(9, v), cur.JumpCount));
        }
        // on ticks without the extra sources those keep their last value, so nothing looks changed
        if (!extra) CarryExtra(ref cur, in s.Prev);
        PlayerStates.ComputeDerived(in cur, s.ActionBits, out var derivedOn, out var derivedKnown);
        if (glideSubs) PostGlide(s, player, in cur, t);
        if (stateSubs) PostChanges(s, player, in cur, derivedOn, derivedKnown, step);
        s.DerivedOn = derivedOn;
        s.DerivedKnown = derivedKnown;
        s.Prev = cur;
    }

    private static void CarryExtra(ref PlayerStates.Native.Snapshot cur, in PlayerStates.Native.Snapshot prev)
    {
        fixed (ulong* c = cur.Values)
        fixed (ulong* p = prev.Values)
            for (var id = 12; id <= 21; id++)
                c[id] = p[id];
        const uint extraMask = 0x3FF000;  // sources 12..21
        cur.Known = (cur.Known & ~extraMask) | (prev.Known & extraMask);
        cur.InBase = prev.InBase;
        cur.HasBuffs = prev.HasBuffs;
        cur.FoodBuffs = prev.FoodBuffs;
        cur.Buffs = prev.Buffs;
        cur.Debuffs = prev.Debuffs;
        cur.RestedBuff = prev.RestedBuff;
        cur.TerrainDebuff = prev.TerrainDebuff;
        cur.Hungry = prev.Hungry;
    }

    // Posts the action's event; true when a handler canceled it and the cancel took.
    private static bool StartAction(Slot s, Player player, PlayerAction action, ref PlayerStates.Native.Snapshot cur, bool canCancel, ulong step, PlayerStates.Native.Table* t)
    {
        var consumable = action.Item.Category == "Consumables" || action.Item.SlotType == "Consumable";
        var isUse = consumable && action.Trigger is PlayerStateId.ActionPrimary or PlayerStateId.ActionPrimary_Tap or PlayerStateId.ActionPrimary_Hold;
        PlayerActionEvent ev;
        if (isUse)
        {
            var kind = KindOf(action.SequenceName);
            var buffs = PlayerStates.BuffsGivenBy(action.Item);
            ev = new PlayerConsumeEvent(player, action, step, canCancel, kind, buffs);
            // watch the stack to see the item go
            uint stack = 0, item = 0;
            if (t->SlotItem(cur.Action.UsedSlot, &item, &stack, null) == PlayerStates.Native.Ok && item == action.Item.Id && stack > 0)
            {
                s.UseSlot = cur.Action.UsedSlot;
                s.UseItem = item;
                s.UseStack = stack;
                s.UseSequence = cur.Action.Sequence;
                s.UseTime = cur.Action.TriggerTime;
                s.UseGrace = 30;
                s.UseKind = kind;
                s.UseBuffs = buffs;
            }
        }
        else
        {
            if (!Events.HasSubscribers<PlayerActionEvent>()) return false;
            ev = new PlayerActionEvent(player, action, step, canCancel);
        }
        Events.Post(ev);
        if (!ev.Canceled) return false;
        if (!canCancel)
        {
            ModLogOnce("a handler canceled a player action where that has no effect (a joined client, or the game's reset did not resolve)");
            return false;
        }
        var rc = t->CancelAction(cur.Entity, cur.Action.Sequence, cur.Action.TriggerTime);
        if (rc == PlayerStates.Native.Ok)
        {
            s.UseItem = 0;
            return true;
        }
        if (rc != PlayerStates.Native.NotFound) ModLogOnce($"stopping a player action failed ({rc})");
        return false;
    }

    private static bool _logged;

    private static void ModLogOnce(string text)
    {
        if (_logged) return;
        _logged = true;
        Backend.Current.Log(0, LogLevel.Warn, "kiln.playerstate: " + text);
    }

    private static PlayerConsumeKind KindOf(string sequence) =>
        sequence.StartsWith("UseItem_Food_Liquid", StringComparison.Ordinal) ? PlayerConsumeKind.Drink :
        sequence.StartsWith("UseItem_Food", StringComparison.Ordinal) ? PlayerConsumeKind.Food :
        sequence.StartsWith("UseItem_Potion", StringComparison.Ordinal) || sequence.StartsWith("UseItem_Flask", StringComparison.Ordinal) ? PlayerConsumeKind.Potion :
        sequence.StartsWith("UseItem_Bandage", StringComparison.Ordinal) ? PlayerConsumeKind.Bandage :
        sequence.StartsWith("UseItem_Scroll", StringComparison.Ordinal) ? PlayerConsumeKind.Scroll :
        PlayerConsumeKind.Other;

    private static void WatchConsume(Slot s, Player player, PlayerStates.Native.Table* t)
    {
        uint item = 0, stack = 0;
        var rc = t->SlotItem(s.UseSlot, &item, &stack, null);
        if (rc != PlayerStates.Native.Ok || item != s.UseItem || stack < s.UseStack)
        {
            var consumed = new PlayerItem(s.UseItem);
            var left = rc == PlayerStates.Native.Ok && item == s.UseItem ? (int)stack : 0;
            s.UseItem = 0;
            if (Events.HasSubscribers<PlayerConsumedEvent>())
                Events.Post(new PlayerConsumedEvent(player, consumed, s.UseKind, s.UseBuffs, left));
            return;
        }
        // the action is over with the stack untouched (hit, dodge, an item that is not used up); a few steps' grace
        if (s.Running is null || s.Running.Sequence != s.UseSequence || s.Running.TriggerTime != s.UseTime)
            if (--s.UseGrace <= 0) s.UseItem = 0;
    }

    private static void PostGlide(Slot s, Player player, in PlayerStates.Native.Snapshot cur, PlayerStates.Native.Table* t)
    {
        var raw = PlayerStates.RawOf(PlayerStateId.HangGliding);
        if (raw < 0 || (cur.Known & 2) == 0 || (s.Prev.Known & 2) == 0) return;
        ulong now, was;
        fixed (ulong* c = cur.Values) now = c[1];
        fixed (ulong* p = s.Prev.Values) was = p[1];
        var on = ((now >> raw) & 1) != 0;
        if (on == (((was >> raw) & 1) != 0)) return;
        var dur = TimeSpan.Zero;
        if (on) s.GlideSinceMs = Environment.TickCount64;
        else if (s.GlideSinceMs != 0) dur = TimeSpan.FromMilliseconds(Environment.TickCount64 - s.GlideSinceMs);
        var glider = PlayerStates.EquipmentOf(new EntityId(cur.Entity))?[PlayerEquipSlot.Glider] ?? default;
        Events.Post(new PlayerGlideEvent(player, on, glider, dur));
    }

    private static void PostChanges(Slot s, Player player, in PlayerStates.Native.Snapshot cur, ushort derivedOn, ushort derivedKnown, ulong step)
    {
        PlayerSnapshot? snap = null;
        fixed (ulong* cv = cur.Values)
        fixed (ulong* pv = s.Prev.Values)
            for (var src = 1; src < 22; src++)
            {
                if ((cur.Known & (1u << src)) == 0 || (s.Prev.Known & (1u << src)) == 0) continue;
                var was = pv[src];
                var now = cv[src];
                if (was == now) continue;
                snap ??= new PlayerSnapshot(in cur);
                if (IsMaskSource(src))
                {
                    var diff = was ^ now;
                    for (var bit = 0; bit < 64 && diff != 0; bit++, diff >>= 1)
                    {
                        if ((diff & 1) == 0) continue;
                        Post(player, PlayerStates.IdOfRaw(src, (ulong)bit), ((now >> bit) & 1) != 0, step, snap);
                    }
                }
                else
                {
                    PostEnum(player, src, was, false, step, snap);
                    PostEnum(player, src, now, true, step, snap);
                }
            }
        var flips = (ushort)(derivedOn ^ s.DerivedOn);
        if (flips != 0)
            for (var bit = 0; bit < 16; bit++)
            {
                if ((flips & (1 << bit)) == 0 || (derivedKnown & s.DerivedKnown & (1 << bit)) == 0) continue;
                snap ??= new PlayerSnapshot(in cur);
                Post(player, (PlayerStateId)((22 << 8) | bit), (derivedOn & (1 << bit)) != 0, step, snap);
            }
    }

    private static bool IsMaskSource(int src) => src is 1 or 3 or 4 or 5 or 6 or 7 or 18;

    // "Invalid" and "None" of an enum source are the absence of a state: no event of their own.
    private static void PostEnum(Player player, int src, ulong value, bool entered, ulong step, PlayerSnapshot snap)
    {
        var id = PlayerStates.IdOfRaw(src, value);
        var name = PlayerStates.NameOf(id);
        if (name is "Invalid" or "None") return;
        Post(player, id, entered, step, snap);
    }

    private static void Post(Player player, PlayerStateId id, bool entered, ulong step, PlayerSnapshot snap) =>
        Events.Post(new PlayerStateChangedEvent(player, id, entered, step, snap));

    private static void PumpEquipment(PlayerStates.Native.Table* t)
    {
        CheckEquipOrder(t);
        var wantEquip = Events.HasSubscribers<PlayerEquipmentChangedEvent>();
        var wantHot = Events.HasSubscribers<PlayerHotbarChangedEvent>();
        foreach (var s in Slots)
        {
            PlayerStates.Native.Equipment q = default;
            q.Size = (uint)sizeof(PlayerStates.Native.Equipment);
            if (t->GetEquipment(s.Entity, &q) != PlayerStates.Native.Ok) continue;
            if (!s.HasEquip)
            {
                s.Equip = q;
                s.HasEquip = true;
                continue;
            }
            if (wantEquip)
                for (var i = 0; i < q.Count && i < PlayerStates.Native.Slots; i++)
                {
                    var before = s.Equip.Items[i];
                    var now = q.Items[i];
                    if (before != now)
                        Events.Post(new PlayerEquipmentChangedEvent(s.Player!, (PlayerEquipSlot)i, new PlayerItem(before), new PlayerItem(now)));
                }
            if (wantHot && (s.Equip.Hotbar != q.Hotbar || s.Equip.HeldItem != q.HeldItem))
                Events.Post(new PlayerHotbarChangedEvent(s.Player!, s.Equip.Hotbar, q.Hotbar, new PlayerItem(s.Equip.HeldItem), new PlayerItem(q.HeldItem)));
            s.Equip = q;
        }
    }

    // The slots come in the game's field order: refuse to name them if that order is not PlayerEquipSlot's.
    private static void CheckEquipOrder(PlayerStates.Native.Table* t)
    {
        if (_equipCheck != null) return;
        var names = new List<string>();
        var buf = stackalloc byte[64];
        for (uint i = 0; i < PlayerStates.Native.Slots; i++)
        {
            uint len = 0;
            if (t->EquipmentSlotName(i, buf, 64, &len) != PlayerStates.Native.Ok) break;
            names.Add(System.Text.Encoding.UTF8.GetString(buf, (int)Math.Min(len, 63u)));
        }
        _equipCheck = names.ToArray();
        var expected = Enum.GetNames<PlayerEquipSlot>();
        for (var i = 0; i < Math.Min(names.Count, expected.Length); i++)
            if (!string.Equals(names[i], expected[i], StringComparison.OrdinalIgnoreCase))
                Backend.Current.Log(0, LogLevel.Warn, $"kiln.playerstate: the game's equipment slot {i} is {names[i]}, Kiln calls it {expected[i]}: a game update moved them");
    }
}
