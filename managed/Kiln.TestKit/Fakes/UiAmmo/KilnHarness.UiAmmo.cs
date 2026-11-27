// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessAmmoList? _ammoList;

    /// <summary>The fake kiln.ui.ammo of this harness: the HUD's ammunition and spell lists Ui.AmmoList adds rows to (the
    /// game's side, what shows, picks). Since 0.2.0-dev.30.6.</summary>
    public HarnessAmmoList AmmoList => _ammoList ??= new HarnessAmmoList(this);
}

/// <summary>
/// The fake kiln.ui.ammo of a harness (<see cref="KilnHarness.AmmoList"/>): Ui.AmmoList's own code runs on it. Since
/// 0.2.0-dev.30.6 (mod API 0.2.15).
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> The game's side: <see cref="Hold"/> a bow or a staff (the list is built, with the game's own
/// rows), <see cref="SetGameRows"/> (the backpack changed), <see cref="Unhold"/>, <see cref="NewSession"/>, and the
/// client's items of each kind (<see cref="KnownItems"/>: other items are refused as the game refuses them). Each
/// <c>Step</c> is a UI frame: Kiln.Ui asks the providers of a list whose context changed and publishes its snapshot,
/// which the native rules check (one owner per list, the context's serial and session, at most 64 rows, no item 0 and
/// no duplicate). <see cref="Shown"/> is the list as the HUD composes it, <see cref="Provided"/> the snapshot's rows,
/// <see cref="ModeOf"/> its mode. The selection stays on its item when the rows change (<see cref="Select"/>,
/// <see cref="Selected"/>). <see cref="Pick"/> of a mod row whose item the game does not list with stock (a row it kept
/// at count 0 is none) queues a pick, which Ui.AmmoList.Picked raises at the next step for the mod that provided the
/// row; the game equips a row it lists itself with stock.</para>
/// <para><b>Not modelled.</b> The game's hook and drawing, its equip request, the server half (a pick does nothing to any
/// inventory), the pick queue's thread. The game's rows are the backpack here, so a stack that leaves the backpack before
/// the game builds its rows again (a pick of its row reaches <c>Picked</c> in the game since 0.2.0-dev.31.10, GitHub issue
/// 145; in Append also for a mod's row kept behind the game's row of the same item, since 0.2.0-dev.31.12) is not
/// modelled; a pick after <see cref="SetGameRows"/> and before the next step is decided by the new rows, as in
/// the game, while <see cref="Shown"/> still composes the earlier snapshot with them. In the game that is so in Replace
/// (since 0.2.0-dev.31.21, GitHub issue 151: the earlier snapshot and the selection stay up for at most a second until
/// Kiln publishes for the new rows); in Append the game shows only its own rows until then.</para>
/// </remarks>
public sealed class HarnessAmmoList
{
    private readonly KilnHarness _harness;

    internal HarnessAmmoList(KilnHarness harness) => _harness = harness;

    private UiAmmoFakeState State => KitFakes.State<UiAmmoFakeState>(_harness);

    private static uint Native(AmmoListKind kind) => kind switch
    {
        AmmoListKind.Arrow => UiAmmoNative.KindArrow,
        AmmoListKind.Spell => UiAmmoNative.KindSpell,
        _ => throw new ArgumentException("one kind: Arrow or Spell", nameof(kind)),
    };

    private static UiAmmoFakeState.List ListOf(UiAmmoFakeState st, AmmoListKind kind) => st.Of(Native(kind))!;

    /// <summary>The local player the lists are built for (the context's <c>Player</c>).</summary>
    public EntityId Player
    {
        get { lock (State.Lock) return new EntityId(State.Player); }
        set { lock (State.Lock) State.Player = value.Value; }
    }

    /// <summary>The client session the lists belong to (1 at the start).</summary>
    public uint Session
    {
        get { lock (State.Lock) return State.Session; }
    }

    /// <summary>The player holds <paramref name="weapon"/> (a bow for <see cref="AmmoListKind.Arrow"/>, a staff for
    /// <see cref="AmmoListKind.Spell"/>): the game builds its list with <paramref name="gameRows"/> (the items it found in
    /// the backpack, in its order). A new context: Kiln.Ui asks the providers at the next step.</summary>
    public void Hold(AmmoListKind kind, ItemId weapon, params AmmoListRow[] gameRows)
    {
        ArgumentNullException.ThrowIfNull(gameRows);
        var st = State;
        lock (st.Lock)
        {
            var list = ListOf(st, kind);
            list.Built = true;
            list.Held = weapon.Value;
            list.Game = gameRows.Select(r => (r.Item.Value, (uint)Math.Max(0, r.Count))).ToArray();
            list.Serial = st.NextSerial++;
        }
    }

    /// <summary>The backpack changed: the game's own rows of the held weapon's list are now <paramref name="gameRows"/>.</summary>
    public void SetGameRows(AmmoListKind kind, params AmmoListRow[] gameRows)
    {
        ArgumentNullException.ThrowIfNull(gameRows);
        var st = State;
        lock (st.Lock)
        {
            var list = ListOf(st, kind);
            if (!list.Built) throw new InvalidOperationException($"no {kind} list is built: Hold a weapon first");
            list.Game = gameRows.Select(r => (r.Item.Value, (uint)Math.Max(0, r.Count))).ToArray();
            list.Serial = st.NextSerial++;
        }
    }

    /// <summary>The weapon is put away: the list is not built (no context; its snapshot stays until Kiln.Ui publishes
    /// another).</summary>
    public void Unhold(AmmoListKind kind)
    {
        var st = State;
        lock (st.Lock)
        {
            var list = ListOf(st, kind);
            list.Built = false;
            list.Held = 0;
            list.Game = [];
        }
    }

    /// <summary>A new client session (the player left and joined again): the snapshots and queued picks are cleared, as
    /// native does at a world's stop, and every built list has a new context.</summary>
    public void NewSession()
    {
        var st = State;
        lock (st.Lock)
        {
            st.Session++;
            st.Picks.Clear();
            foreach (var list in (UiAmmoFakeState.List[])[st.Arrow, st.Spell])
            {
                list.Owner = 0;
                list.Mode = UiAmmoNative.ModeGame;
                list.Rows = [];
                list.Serial = st.NextSerial++;
            }
        }
    }

    /// <summary>The client's items of this kind (the arrows, the spells): a snapshot naming any other item is refused, as
    /// the game refuses it. Without a call every item is taken.</summary>
    public void KnownItems(AmmoListKind kind, params ItemId[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (State.Lock) ListOf(State, kind).Known = items.Select(i => i.Value).ToHashSet();
    }

    /// <summary>The list as the HUD shows it now: the game's rows, then (Append) or instead (Replace) the mods' rows.</summary>
    public IReadOnlyList<AmmoListRow> Shown(AmmoListKind kind)
    {
        lock (State.Lock)
            return UiAmmoFake.Shown(ListOf(State, kind)).Select(s => new AmmoListRow(new ItemId(s.Item), (int)s.Count)).ToArray();
    }

    /// <summary>The mods' rows Kiln.Ui published for the list (composed, in order); empty while the game's list shows as
    /// it is. In Append since 0.2.0-dev.31.12 also a mod's row of an item the game lists itself, with stock too, which
    /// <see cref="Shown"/> shows as the game's row: a test asserting these rows in Append lists them as well (GitHub issue
    /// 145).</summary>
    public IReadOnlyList<AmmoListRow> Provided(AmmoListKind kind)
    {
        lock (State.Lock)
            return ListOf(State, kind).Rows.Select(r => new AmmoListRow(new ItemId(r.Item), (int)r.Count)).ToArray();
    }

    /// <summary>How the mods' rows go in (null: the game's list as it is).</summary>
    public AmmoListMode? ModeOf(AmmoListKind kind)
    {
        lock (State.Lock)
        {
            var list = ListOf(State, kind);
            return list.Owner == 0 ? null : list.Mode switch
            {
                UiAmmoNative.ModeAppend => AmmoListMode.Append,
                UiAmmoNative.ModeReplace => AmmoListMode.Replace,
                _ => null,
            };
        }
    }

    /// <summary>Snapshots published so far (a clear included).</summary>
    public int Publishes
    {
        get { lock (State.Lock) return State.Publishes; }
    }

    /// <summary>The player moves the list's selection onto the row of <paramref name="item"/>.</summary>
    public void Select(AmmoListKind kind, ItemId item)
    {
        lock (State.Lock)
        {
            var list = ListOf(State, kind);
            if (!UiAmmoFake.Shown(list).Any(s => s.Item == item.Value)) throw new ArgumentException($"no row of {item} is shown", nameof(item));
            list.Selected = item.Value;
        }
    }

    /// <summary>The item the selection rests on: it stays on its item when the rows change, and goes to the first row when
    /// its item is no longer shown (null: an empty list).</summary>
    public ItemId? Selected(AmmoListKind kind)
    {
        lock (State.Lock)
        {
            var list = ListOf(State, kind);
            var shown = UiAmmoFake.Shown(list);
            if (list.Selected is { } s && shown.Any(r => r.Item == s)) return new ItemId(s);
            return shown.Length > 0 ? new ItemId(shown[0].Item) : null;
        }
    }

    /// <summary>The player picks the row of <paramref name="item"/>. True: a mod's row whose item the game does not
    /// list with stock (not in the backpack; a game row at count 0 is none), so the game does nothing and the pick is
    /// queued for Ui.AmmoList.Picked at the next step. False: the game equips it itself (its own row, or a mod row of
    /// an item it lists with stock), or no row of it is shown. In Append since 0.2.0-dev.31.12 true also for a mod's row
    /// of an item the game listed with stock when Kiln.Ui last published, once <see cref="SetGameRows"/> set its count
    /// to 0 (GitHub issue 145; it was false).</summary>
    public bool Pick(AmmoListKind kind, ItemId item)
    {
        var st = State;
        lock (st.Lock)
        {
            var list = ListOf(st, kind);
            var shown = UiAmmoFake.Shown(list);
            var i = Array.FindIndex(shown, s => s.Item == item.Value);
            if (i < 0) return false;
            list.Selected = item.Value;
            // As native: a mod row is the game's own when the game lists its item with stock (the game's rows are the
            // backpack here); a row the game kept at count 0 for a used-up stack is not stock (GitHub issue 131).
            var key = list.Owner == 0 || list.Mode == UiAmmoNative.ModeGame ? -1 : Array.FindIndex(list.Rows, r => r.Item == item.Value);
            if (key < 0 || list.Game.Any(g => g.Item == item.Value && g.Count != 0)) return false;
            if (st.Picks.Count == UiAmmoNative.Capacity) st.Picks.Dequeue();  // the oldest wish goes
            st.Picks.Enqueue((list.Owner, new UiAmmoNative.Pick
            {
                Size = 32, Kind = Native(kind), Item = item.Value, Count = list.Rows[key].Count, Key = list.Rows[key].Key, Session = st.Session,
                Step = st.Step,
            }));
            return true;
        }
    }
}
