// SPDX-License-Identifier: MIT
// What a player is doing right now (native/services/players/playerstate.cpp, kiln_playerstate.h). Usage:
// docs/kiln/services/player-state.md, every state: docs/kiln/modding/player-states.md.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>Where a state read came from.</summary>
public enum PlayerStateOrigin
{
    /// <summary>The server world: the authoritative state, on a dedicated server and on a host.</summary>
    Server = 0,
    /// <summary>A joined client's prediction of its own player. It can differ from the server's for a moment.</summary>
    ClientPrediction = 1,
    /// <summary>A joined client's replicated copy of another player: actor flags and the running action only.</summary>
    Replicated = 2,
}

/// <summary>One state the running game has.</summary>
/// <param name="Id">Use it with <see cref="Player.Is"/>; a state only a newer game build has is a value without a name in
/// the enum.</param>
/// <param name="Source">The game's enum and field it comes from.</param>
/// <param name="Raw">Its bit number or value in that field, as the running game numbers it.</param>
/// <param name="GameName">The game's own name of the value.</param>
/// <param name="IsFlag">True: a bit, any number can be on. False: one value of several, one at a time.</param>
/// <param name="NamedByKiln">False: this build of Kiln has no constant for it (a newer game).</param>
public sealed record PlayerStateInfo(PlayerStateId Id, PlayerStateSource Source, ulong Raw, string GameName, bool IsFlag, bool NamedByKiln);

/// <summary>An item a player has or uses. Compare it with a <c>GameItems</c> constant, either the name
/// (<c>GameItems.Food_...</c>) or the id (<c>GameItems.Ids.Food_...</c>).</summary>
public readonly struct PlayerItem : IEquatable<PlayerItem>
{
    public PlayerItem(uint id) => Id = id;

    /// <summary>The game's item id (FNV-1a of its GUID); 0 = none.</summary>
    public uint Id { get; }
    public bool IsNone => Id == 0;

    private PlayerItemInfo? Info => Id == 0 ? null : PlayerStates.ItemInfoOf(Id);

    /// <summary>The game's name for it (what <c>GameItems</c> constants hold); "" when none or unreadable here.</summary>
    public string Name => Info?.Name ?? "";
    /// <summary>The game's ItemCategory name: "Consumables", "Weapons", ...</summary>
    public string Category => Info?.Category ?? "";
    /// <summary>The game's EquipmentSlotType name: "Consumable", "MeleeWeapon", "Glider", ...</summary>
    public string SlotType => Info?.SlotType ?? "";

    public bool Equals(PlayerItem other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is PlayerItem o && Equals(o);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => Id == 0 ? "(none)" : Name.Length > 0 ? Name : $"0x{Id:x8}";

    public static bool operator ==(PlayerItem a, PlayerItem b) => a.Id == b.Id;
    public static bool operator !=(PlayerItem a, PlayerItem b) => a.Id != b.Id;
    public static bool operator ==(PlayerItem a, string? name) => a.Id != 0 && string.Equals(a.Name, name, StringComparison.Ordinal);
    public static bool operator !=(PlayerItem a, string? name) => !(a == name);
    public static bool operator ==(string? name, PlayerItem a) => a == name;
    public static bool operator !=(string? name, PlayerItem a) => !(a == name);
    public static bool operator ==(PlayerItem a, uint id) => a.Id == id;
    public static bool operator !=(PlayerItem a, uint id) => a.Id != id;
    public static bool operator ==(uint id, PlayerItem a) => a.Id == id;
    public static bool operator !=(uint id, PlayerItem a) => a.Id != id;
}

internal sealed record PlayerItemInfo(string Name, string Category, string SlotType, string Ammunition, uint Buff, string BuffSlot);

/// <summary>A buff on a player, or one an item gives.</summary>
/// <param name="Id">The game's BuffTypeId (<c>GameBuffs.Ids.X</c>).</param>
/// <param name="Slot">The game's BuffSlotType name: "Food", "Buff", "Debuff", "Rested", "TerrainDebuff".</param>
/// <param name="Remaining">Time left, when known (only on a buff that is on a player).</param>
/// <param name="Level">Its level, when known.</param>
public sealed record PlayerBuff(uint Id, string Slot, TimeSpan? Remaining = null, int Level = 0)
{
    /// <summary>True for this id, given as a number or as a <c>GameBuffs.X</c> constant ("0x..").</summary>
    public bool Is(string idText) => idText is not null && idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        uint.TryParse(idText.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var v) && v == Id;
    public bool Is(uint id) => Id == id;
}

/// <summary>The action a player runs: the game's action sequence and what started it.</summary>
public sealed record PlayerAction(
    uint Sequence, string SequenceName, PlayerStateId Trigger, PlayerStateId Input, PlayerItem Item, PlayerItem Ammunition, PlayerItem CostItem,
    ulong Slot, EntityId Host, long TriggerTime, TimeSpan Played, TimeSpan Length)
{
    /// <summary>The slot's inventory entity (0 = no slot).</summary>
    public EntityId SlotInventory => new((uint)(Slot & 0xFFFFFFFF));
    /// <summary>The slot's index in that inventory.</summary>
    public int SlotIndex => (int)((Slot >> 32) & 0xFF);
}

/// <summary>What a player was doing at one moment. Values that could not be read are null.</summary>
public sealed class PlayerSnapshot
{
    internal PlayerStates.Native.Snapshot N;
    internal ushort DerivedOn, DerivedKnown;

    internal PlayerSnapshot(in PlayerStates.Native.Snapshot n)
    {
        N = n;
        PlayerStates.ComputeDerived(in N, out DerivedOn, out DerivedKnown);
    }

    public EntityId Entity => new(N.Entity);
    public PlayerStateOrigin Origin => (PlayerStateOrigin)N.World;

    /// <summary>True when the player is in this state; false when not, or when it is unknown (see <see cref="Check"/>).</summary>
    public bool Is(PlayerStateId state) => Check(state) == true;

    /// <summary>True or false, or null when this state cannot be read here: a state this game build does not have, or a
    /// source that did not resolve.</summary>
    public bool? Check(PlayerStateId state) => PlayerStates.Evaluate(in N, DerivedOn, DerivedKnown, state);

    /// <summary>The source's raw value: a bit mask or an enum value; null when unknown.</summary>
    public ulong? RawValue(PlayerStateSource source)
    {
        var s = (int)source;
        if (s <= 0 || s >= PlayerStates.Native.Sources || (N.Known & (1u << s)) == 0) return null;
        unsafe
        {
            fixed (ulong* v = N.Values) return v[s];
        }
    }

    /// <summary>Every state on right now, with raw ids for states this Kiln has no name for.</summary>
    public IEnumerable<PlayerStateId> Active => PlayerStates.ActiveIn(this);

    /// <summary>The action it runs now; null when none.</summary>
    public PlayerAction? Action => N.HasAction != 0 && N.Action.Sequence != 0 ? PlayerStates.ActionOf(in N) : null;

    public int? Health => N.HasVitals != 0 ? N.Health : null;
    public int? HealthMax => N.HasVitals != 0 ? N.HealthMax : null;
    public int? Stamina => N.HasVitals != 0 ? N.Stamina : null;
    public int? StaminaMax => N.HasVitals != 0 ? N.StaminaMax : null;
    public int? Mana => N.HasVitals != 0 ? N.Mana : null;
    public int? ManaMax => N.HasVitals != 0 ? N.ManaMax : null;
    public int? BodyHeat => N.HasVitals != 0 ? N.BodyHeat : null;
    public int? BodyHeatMax => N.HasVitals != 0 ? N.BodyHeatMax : null;
    public int? SpecialAbility => N.HasVitals != 0 ? N.Special : null;
    public int? SpecialAbilityMax => N.HasVitals != 0 ? N.SpecialMax : null;
    /// <summary>The game's FogResistance value. In the Shroud it goes down by one a second, in the deadly kind (the game's
    /// FogType.Death) by 100; outside it recovers by 25 a second.</summary>
    public int? ShroudResistance => N.HasVitals != 0 ? N.Fog : null;
    public int? ShroudResistanceMax => N.HasVitals != 0 ? N.FogMax : null;
    /// <summary>The Shroud timer the game shows: <see cref="ShroudResistance"/> in seconds.</summary>
    public TimeSpan? ShroudTimeLeft => N.HasVitals != 0 ? TimeSpan.FromSeconds(N.Fog) : null;
    public int? Oxygen => N.HasVitals != 0 ? N.Oxygen : null;
    public int? OxygenMax => N.HasVitals != 0 ? N.OxygenMax : null;
    public int? Level => N.HasVitals != 0 ? N.Level : null;
    public int? Comfort => N.HasVitals != 0 ? N.Comfort : null;
    /// <summary>Jumps since the last landing.</summary>
    public int? JumpCount => N.HasJump != 0 ? N.JumpCount : null;
    /// <summary>Water depth at the player, metres; null when unknown.</summary>
    public float? WaterDepth => N.HasJump != 0 ? N.WaterDepth : null;
    public bool? CanSwim => N.HasJump != 0 ? N.CanSwim != 0 : null;
    public bool? CanDive => N.HasJump != 0 ? N.CanDive != 0 : null;
}

/// <summary>Player state on any entity id. Prefer <see cref="Player"/>'s members; this class has the full table.</summary>
/// <remarks>
/// <para>Reads are live on the world's own thread (the tick thread on a server and a host; the main thread on a joined
/// client, where <c>Context.Tick</c> and event handlers run). From any other thread they give the snapshot of the last
/// tick, or nothing the first time: an off-thread read starts the per-tick scan for a minute.</para>
/// <para>On a game build where a source did not resolve its states read as unknown: <see cref="Check"/> is null, and
/// <see cref="Is"/> is false. <see cref="Available"/> and <see cref="Status"/> say what works; kiln.log says what
/// did not resolve.</para>
/// </remarks>
public static unsafe partial class PlayerStates
{
    // ---- native mirror (kiln_playerstate.h) ----
    internal static class Native
    {
        public const int Sources = 24, Slots = 24, ClientActions = 48, KnownClientActions = 23;

        [StructLayout(LayoutKind.Sequential)]
        public struct Action
        {
            public uint Sequence;
            public ushort Trigger;
            public byte Input, CallIndex;
            public long TriggerTime, PlayedNs, LengthNs;
            public ulong UsedSlot;
            public uint UsedItem, UsedPide, AmmoItem, CostItem, SubIndex, Host;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Snapshot
        {
            public uint Size, Entity, World, Known;
            public fixed ulong Values[Sources];
            public Action Action;
            public byte HasAction, HasVitals, HasJump, InBase;
            public ushort Health, HealthMax, Stamina, StaminaMax, Mana, ManaMax, BodyHeat, BodyHeatMax, Special, SpecialMax,
                Fog, FogMax, Oxygen, OxygenMax, Level, Comfort;
            public byte JumpCount, CanSwim, CanDive, HasBuffs;
            public byte FoodBuffs, Buffs, Debuffs, RestedBuff, TerrainDebuff, Hungry, R1, R2;
            public long LastJumpTime, ServerTime, GliderTime;
            public float WaterDepth, R3;
            public fixed uint ClientVersions[ClientActions];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ClientAction
        {
            public uint Size, Index, Bytes, Cancelable;
            public fixed byte Name[48];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HeldAction
        {
            public const int PayloadBytes = 160;
            public uint Size, Entity, Index, Version, Bytes, Reserved;
            public fixed byte Payload[PayloadBytes];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SourceInfo
        {
            public uint Size, Id, Kind, Count, Tier, Resolved;
            public fixed byte Component[64];
            public fixed byte Field[64];
            public fixed byte EnumType[64];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ItemInfo
        {
            public uint Size, Id, Category, Ammunition, Slot, Buff, BuffSlot, NameLen;
            public fixed byte Name[128];
            public fixed byte CategoryName[32];
            public fixed byte SlotName[32];
            public fixed byte AmmunitionName[32];
            public fixed byte BuffSlotName[32];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Equipment
        {
            public uint Size, Count;
            public fixed ulong Slots[PlayerStates.Native.Slots];
            public fixed uint Items[PlayerStates.Native.Slots];
            public fixed uint Stack[PlayerStates.Native.Slots];
            public ulong HeldSlot;
            public uint HeldItem;
            public int Hotbar;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Buff
        {
            public uint Id, Entity, Slot, Cleansed;
            public long LifeNs, EndNs;
            public uint Level, Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Status
        {
            public uint Size;
            public byte Reflection, Cancel, Names, Items;
            public uint Sources;
            public byte ClientHold, Aim, R2, R3;
            public ulong Scans, EntitiesRead, ScanNs;
            public fixed byte Detail[160];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Table
        {
            public ServiceHeader Header;
            public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
            public delegate* unmanaged[Cdecl]<uint, SourceInfo*, int> GetSourceInfo;
            public delegate* unmanaged[Cdecl]<uint, uint, ulong*, byte*, uint, uint*, int> EnumEntry;
            public delegate* unmanaged[Cdecl]<uint*, uint, uint, Snapshot*, int> Scan;
            public delegate* unmanaged[Cdecl]<uint, Equipment*, int> GetEquipment;
            public delegate* unmanaged[Cdecl]<uint, byte*, uint, uint*, int> EquipmentSlotName;
            public delegate* unmanaged[Cdecl]<uint, Buff*, uint, uint*, int> GetBuffs;
            public delegate* unmanaged[Cdecl]<ulong, uint*, uint*, uint*, int> SlotItem;
            public delegate* unmanaged[Cdecl]<uint, ItemInfo*, int> GetItemInfo;
            public delegate* unmanaged[Cdecl]<uint, uint, byte*, uint, uint*, int> SequenceName;
            public delegate* unmanaged[Cdecl]<uint, uint, long, int> CancelAction;
            public delegate* unmanaged[Cdecl]<uint, uint, uint, uint, ulong, uint, int> LabUseItem;
            public delegate* unmanaged[Cdecl]<uint> LocalPlayer;
            public delegate* unmanaged[Cdecl]<uint, ClientAction*, int> ClientActionInfo;
            public delegate* unmanaged[Cdecl]<uint, uint, void*, uint, uint*, int> ClientActionBytes;
            public delegate* unmanaged[Cdecl]<uint, uint, int> ClientActionCancel;
            // added later: check Has()
            public delegate* unmanaged[Cdecl]<uint*, uint, int> ClientActionHold;
            public delegate* unmanaged[Cdecl]<HeldAction*, uint, uint*, int> ClientActionHeld;
            public delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int> ClientActionDecide;
            // since 0.2.0-dev.27: check Has()
            public delegate* unmanaged[Cdecl]<uint, AimData*, int> Aim;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AimData
        {
            public uint Size, World;
            public long X, Y, Z;
            public float Qx, Qy, Qz, Qw;
            public float Distance, FovY;
            public long ServerTime;
        }

        /// <summary>The native table has the function at this field (a kiln-players.dll of an older release has fewer).</summary>
        public static bool Has(Table* t, void* field) => t != null && (byte*)field - (byte*)t + sizeof(nint) <= t->Header.Size;

        public const uint WantActor = 1, WantExtra = 2, WantVitals = 4, WantBaseZone = 8, WantMotion = 16, WantInput = 32;
        public const uint WantBase = WantActor | WantMotion | WantInput;
        public const int Ok = 0, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;
    }

    private static Native.Table* _table;
    private static bool _looked;

    internal static Native.Table* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _looked = true;
            if (sizeof(Native.Snapshot) != 544 || sizeof(Native.ClientAction) != 64 || sizeof(Native.ItemInfo) != 288 || sizeof(Native.Equipment) != 408 ||
                sizeof(Native.Buff) != 40 || sizeof(Native.Status) != 200 || sizeof(Native.SourceInfo) != 216 || sizeof(Native.HeldAction) != 184 ||
                sizeof(Native.AimData) != 64)
                return null;  // layout drift between this assembly and the header: refuse rather than misread
            _table = Services.Get<Native.Table>("kiln.playerstate", 1);
            return _table;
        }
    }

    /// <summary>True when kiln.playerstate is loaded and read at least one source from the running game.</summary>
    public static bool Available
    {
        get
        {
            var t = T;
            if (t == null) return false;
            Native.Status s = default;
            s.Size = (uint)sizeof(Native.Status);
            return t->GetStatus(&s) == Native.Ok && s.Reflection != 0;
        }
    }

    /// <summary>What works on this build. Null when the service is not loaded.</summary>
    public static PlayerStateServiceStatus? Status
    {
        get
        {
            var t = T;
            if (t == null) return null;
            Native.Status s = default;
            s.Size = (uint)sizeof(Native.Status);
            if (t->GetStatus(&s) != Native.Ok) return null;
            return new PlayerStateServiceStatus(s.Reflection != 0, s.Cancel != 0, s.Names != 0, s.Items != 0, (int)s.Sources,
                s.Scans, s.EntitiesRead, Marshal.PtrToStringUTF8((nint)s.Detail) ?? "")
            {
                ClientActionCancel = s.ClientHold != 0 && Native.Has(t, &t->ClientActionDecide),
            };
        }
    }

    // ---- the table of every state ----
    private static readonly object TableLock = new();
    private static List<PlayerStateInfo>? _all;
    private static Dictionary<ushort, PlayerStateInfo>? _byId;
    private static short[][]? _rawOf;       // [source][low byte of the id] = raw value, -1 none
    private static PlayerStateId[][]? _idOf; // [source][raw] = id
    private static uint[]? _kinds;

    private static void BuildTable()
    {
        if (_all != null) return;
        lock (TableLock)
        {
            if (_all != null) return;
            var all = new List<PlayerStateInfo>();
            var byId = new Dictionary<ushort, PlayerStateInfo>();
            var rawOf = new short[Native.Sources + 1][];
            var idOf = new PlayerStateId[Native.Sources + 1][];
            var kinds = new uint[Native.Sources + 1];
            for (var i = 0; i < rawOf.Length; i++)
            {
                rawOf[i] = new short[256];
                Array.Fill(rawOf[i], (short)-1);
                idOf[i] = new PlayerStateId[256];
            }
            var known = new Dictionary<(int, string), PlayerStateId>();
            foreach (var (id, game) in PlayerStateTable.Known) known[((ushort)id >> 8, game)] = id;
            var t = T;
            if (t != null)
            {
                var name = stackalloc byte[128];
                for (uint s = 1; s < Native.Sources; s++)
                {
                    Native.SourceInfo si = default;
                    si.Size = (uint)sizeof(Native.SourceInfo);
                    if (t->GetSourceInfo(s, &si) != Native.Ok || si.Resolved == 0) continue;
                    kinds[s] = si.Kind + 1;
                    var usedLow = new HashSet<int>();
                    foreach (var (id, game) in PlayerStateTable.Known)
                        if ((ushort)id >> 8 == s) usedLow.Add((ushort)id & 0xFF);
                    var extra = 0x80;
                    for (uint i = 0; i < si.Count; i++)
                    {
                        ulong value = 0;
                        uint len = 0;
                        if (t->EnumEntry(s, i, &value, name, 128, &len) != Native.Ok) continue;
                        var game = Encoding.UTF8.GetString(name, (int)Math.Min(len, 127u));
                        if (game == "Count" || value > 255) continue;
                        var named = known.TryGetValue(((int)s, game), out var id);
                        if (!named)
                        {
                            // A state this Kiln has no name for: its raw value is its id, unless another state took it.
                            var low = (int)value;
                            if (usedLow.Contains(low))
                            {
                                while (usedLow.Contains(extra) && extra < 255) extra++;
                                low = extra;
                            }
                            usedLow.Add(low);
                            id = (PlayerStateId)((s << 8) | (uint)low);
                        }
                        if (byId.ContainsKey((ushort)id)) continue;  // an alias value in the game's enum
                        var info = new PlayerStateInfo(id, (PlayerStateSource)s, value, game, si.Kind == 1, named);
                        all.Add(info);
                        byId[(ushort)id] = info;
                        rawOf[s][(ushort)id & 0xFF] = (short)value;
                        idOf[s][value] = id;
                    }
                }
            }
            for (var i = 0; i < PlayerStateTable.DerivedNames.Length; i++)
            {
                var id = (PlayerStateId)((22 << 8) | i);
                var info = new PlayerStateInfo(id, PlayerStateSource.Derived, (ulong)i, PlayerStateTable.DerivedNames[i], true, true);
                all.Add(info);
                byId[(ushort)id] = info;
            }
            _rawOf = rawOf;
            _idOf = idOf;
            _kinds = kinds;
            _byId = byId;
            _all = all;
        }
    }

    /// <summary>Every state the running game has, read from its reflection, with what this Kiln calls them.</summary>
    public static IReadOnlyList<PlayerStateInfo> All
    {
        get
        {
            BuildTable();
            return _all!;
        }
    }

    /// <summary>The game's name of a state, also for one this Kiln has no constant for. "" when the running game does not
    /// have it.</summary>
    public static string NameOf(PlayerStateId state)
    {
        BuildTable();
        return _byId!.TryGetValue((ushort)state, out var i) ? i.GameName : "";
    }

    /// <summary>The state's row in <see cref="All"/>; null when the running game does not have it.</summary>
    public static PlayerStateInfo? InfoOf(PlayerStateId state)
    {
        BuildTable();
        return _byId!.GetValueOrDefault((ushort)state);
    }

    /// <summary>Finds a state by the game's name, in one source or in all (the first match).</summary>
    public static PlayerStateId? Find(string gameName, PlayerStateSource source = PlayerStateSource.None)
    {
        BuildTable();
        foreach (var i in _all!)
            if ((source == PlayerStateSource.None || i.Source == source) && string.Equals(i.GameName, gameName, StringComparison.OrdinalIgnoreCase))
                return i.Id;
        return null;
    }

    /// <summary>The id of a raw bit or value of a source, with or without a constant in this Kiln.</summary>
    public static PlayerStateId Raw(PlayerStateSource source, int raw)
    {
        BuildTable();
        var s = (int)source;
        if (s <= 0 || s >= Native.Sources || raw is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(raw));
        var id = _idOf![s][raw];
        return id != PlayerStateId.None ? id : (PlayerStateId)((s << 8) | raw);
    }

    /// <summary>Which source a state belongs to.</summary>
    public static PlayerStateSource SourceOf(PlayerStateId state) => (PlayerStateSource)((ushort)state >> 8);

    private static PlayerClientActionInfo[]? _clientActions;

    /// <summary>The actions a client can send on this build, named by the game's own fields: "changeEquipmentAction",
    /// "teleportAction", "adminSetDaytime", ... Read from its reflection.</summary>
    public static IReadOnlyList<PlayerClientActionInfo> ClientActions
    {
        get
        {
            if (_clientActions != null) return _clientActions;
            var t = T;
            if (t == null) return [];
            var list = new List<PlayerClientActionInfo>();
            var hold = Native.Has(t, &t->ClientActionDecide);  // an older kiln-players.dll marked actions cancelable it could not stop
            for (uint i = 0; i < Native.ClientActions; i++)
            {
                Native.ClientAction a = default;
                a.Size = (uint)sizeof(Native.ClientAction);
                if (t->ClientActionInfo(i, &a) != Native.Ok) break;
                list.Add(new PlayerClientActionInfo((int)i, Marshal.PtrToStringUTF8((nint)a.Name) ?? "", (int)a.Bytes, hold && a.Cancelable != 0));
            }
            return _clientActions = list.ToArray();
        }
    }

    // ---- reads ----
    private static bool OnWorldThread => Backend.Current.IsTickThread;

    /// <summary>Reads a player now (the world's thread), or gives the last tick's copy (other threads; null until there is
    /// one). Null when the entity has no such components, or the service is unavailable.</summary>
    public static PlayerSnapshot? Read(EntityId player)
    {
        var t = T;
        if (t == null) return null;
        if (!OnWorldThread)
        {
            PlayerStateWatch.ArmForOtherThreads();
            return PlayerStateWatch.LastSnapshot(player);
        }
        Native.Snapshot s = default;
        s.Size = (uint)sizeof(Native.Snapshot);
        var e = player.Value;
        var rc = t->Scan(&e, 1, Native.WantBase | Native.WantExtra | Native.WantVitals | Native.WantBaseZone, &s);
        return rc == Native.Ok && s.Known != 0 ? new PlayerSnapshot(in s) : null;
    }

    /// <summary>True when the player is in the state; false when not, or when unknown.</summary>
    public static bool Is(EntityId player, PlayerStateId state) => Check(player, state) == true;

    /// <summary>True or false, or null when it cannot be read: no such player, or the state does not exist here.</summary>
    public static bool? Check(EntityId player, PlayerStateId state)
    {
        var t = T;
        if (t == null) return null;
        var src = (ushort)state >> 8;
        // Derived states may need more than the per-tick sources, so they take the full read.
        if (src == 22 || !OnWorldThread) return Read(player)?.Check(state);
        BuildTable();
        var tier = src switch
        {
            >= 12 and <= 21 => Native.WantExtra,
            2 or 3 or 4 or 9 => Native.WantMotion,
            5 or 7 or 10 => Native.WantInput,
            _ => Native.WantActor,
        };
        Native.Snapshot s = default;
        s.Size = (uint)sizeof(Native.Snapshot);
        var e = player.Value;
        if (t->Scan(&e, 1, tier, &s) != Native.Ok || s.Known == 0) return null;
        return Evaluate(in s, 0, 0, state);
    }

    /// <summary>Every buff on the player, from the game's buff slots. Server world only (a joined client has no such
    /// component: empty). World's thread.</summary>
    public static IReadOnlyList<PlayerBuff> BuffsOf(EntityId player)
    {
        var t = T;
        if (t == null) return [];
        var buf = stackalloc Native.Buff[40];
        uint n = 0;
        if (t->GetBuffs(player.Value, buf, 40, &n) != Native.Ok) return [];
        var list = new List<PlayerBuff>((int)n);
        long now = 0;
        Native.Snapshot s = default;
        s.Size = (uint)sizeof(Native.Snapshot);
        var e = player.Value;
        if (t->Scan(&e, 1, 0, &s) == Native.Ok) now = s.ServerTime;
        for (var i = 0; i < n; i++)
        {
            var b = buf[i];
            TimeSpan? left = null;
            if (b.EndNs > 0 && now > 0 && b.EndNs != long.MaxValue) left = TimeSpan.FromTicks(Math.Max(0, b.EndNs - now) / 100);
            list.Add(new PlayerBuff(b.Id, SlotName(b.Slot), left, (int)b.Level));
        }
        return list;
    }

    /// <summary>What the player has equipped and holds. Null when unreadable. World's thread.</summary>
    public static PlayerEquipment? EquipmentOf(EntityId player)
    {
        var t = T;
        if (t == null) return null;
        Native.Equipment q = default;
        q.Size = (uint)sizeof(Native.Equipment);
        return t->GetEquipment(player.Value, &q) == Native.Ok ? new PlayerEquipment(in q) : null;
    }

    private static string SlotName(uint slot) => slot switch
    {
        0 => "Food", 1 => "Buff", 2 => "Debuff", 3 => "Rested", 4 => "TerrainDebuff", _ => "",
    };

    private static readonly Dictionary<uint, PlayerItemInfo?> Items = new();

    internal static PlayerItemInfo? ItemInfoOf(uint id)
    {
        lock (Items)
            if (Items.TryGetValue(id, out var known)) return known;
        var t = T;
        if (t == null || !OnWorldThread) return null;
        Native.ItemInfo n = default;
        n.Size = (uint)sizeof(Native.ItemInfo);
        var rc = t->GetItemInfo(id, &n);
        if (rc == Native.WrongThread) return null;  // try again from the right thread
        var info = rc != Native.Ok ? null : new PlayerItemInfo(
            Encoding.UTF8.GetString(n.Name, (int)Math.Min(n.NameLen, 127u)),
            Marshal.PtrToStringUTF8((nint)n.CategoryName) ?? "", Marshal.PtrToStringUTF8((nint)n.SlotName) ?? "",
            Marshal.PtrToStringUTF8((nint)n.AmmunitionName) ?? "", n.Buff, Marshal.PtrToStringUTF8((nint)n.BuffSlotName) ?? "");
        lock (Items) Items[id] = info;
        return info;
    }

    /// <summary>The buffs an item gives through its ItemInfo.equipment.appliedBuff. Potions that heal through an impact
    /// program have none here.</summary>
    public static IReadOnlyList<PlayerBuff> BuffsGivenBy(PlayerItem item)
    {
        var i = ItemInfoOf(item.Id);
        return i is { Buff: not 0 } ? [new PlayerBuff(i.Buff, i.BuffSlot)] : [];
    }

    internal static string SequenceNameOf(uint sequence, uint sub)
    {
        var t = T;
        if (t == null || sequence == 0) return "";
        var buf = stackalloc byte[128];
        uint len = 0;
        return t->SequenceName(sequence, sub, buf, 128, &len) is Native.Ok or Native.BufferTooSmall
            ? Encoding.UTF8.GetString(buf, (int)Math.Min(len, 127u))
            : "";
    }

    internal static PlayerAction ActionOf(in Native.Snapshot s)
    {
        var a = s.Action;
        return new PlayerAction(a.Sequence, SequenceNameOf(a.Sequence, a.SubIndex), IdOfRaw(8, a.Trigger), a.Input == 0x38 ? PlayerStateId.None : IdOfRaw(7, a.Input),
            new PlayerItem(a.UsedItem), new PlayerItem(a.AmmoItem), new PlayerItem(a.CostItem), a.UsedSlot, new EntityId(a.Host), a.TriggerTime,
            TimeSpan.FromTicks(a.PlayedNs / 100), TimeSpan.FromTicks(a.LengthNs / 100));
    }

    internal static PlayerStateId IdOfRaw(int source, ulong raw)
    {
        BuildTable();
        if (raw > 255) return (PlayerStateId)((source << 8) | 0xFF);
        var id = _idOf![source][raw];
        return id != PlayerStateId.None ? id : (PlayerStateId)((source << 8) | (int)raw);
    }

    internal static short RawOf(PlayerStateId state)
    {
        BuildTable();
        return _rawOf![(ushort)state >> 8][(ushort)state & 0xFF];
    }

    internal static bool? Evaluate(in Native.Snapshot s, ushort derivedOn, ushort derivedKnown, PlayerStateId state)
    {
        BuildTable();
        var src = (ushort)state >> 8;
        var low = (ushort)state & 0xFF;
        if (src == 22) return low < 16 && (derivedKnown & (1 << low)) != 0 ? (derivedOn & (1 << low)) != 0 : null;
        if (src is <= 0 or >= Native.Sources || (s.Known & (1u << src)) == 0) return null;
        var raw = _rawOf![src][low];
        if (raw < 0) return null;
        ulong v;
        fixed (ulong* p = s.Values) v = p[src];
        return _kinds![src] == 2 ? raw < 64 && ((v >> raw) & 1) != 0 : v == (ulong)raw;
    }

    internal static void ComputeDerived(in Native.Snapshot s, out ushort on, out ushort known) => ComputeDerived(in s, ActionBits(in s), out on, out known);

    /// <summary>The action part of the derived states. It only changes with the action, so the per-tick scan keeps it.</summary>
    internal static ushort ActionBits(in Native.Snapshot s)
    {
        ushort on = 0;
        if (s.HasAction != 0 && s.Action.Sequence != 0) Classify(in s, ref on);
        return on;
    }

    internal static void ComputeDerived(in Native.Snapshot s, ushort actionBits, out ushort on, out ushort known)
    {
        BuildTable();
        on = actionBits;
        known = 0;
        const int airborne = 0, attacking = 1, casting = 2, usingItem = 3, inBase = 4, rested = 5, fed = 6, hasDebuff = 7, hungry = 8;
        if ((s.Known & (1u << 2)) != 0)
        {
            known |= 1 << airborne;
            ulong loco;
            fixed (ulong* p = s.Values) loco = p[2];
            if (loco == (ulong)RawOf(PlayerStateId.LocomotionFalling) || loco == (ulong)RawOf(PlayerStateId.LocomotionHangGliding) ||
                loco == (ulong)RawOf(PlayerStateId.LocomotionFlying))
                on |= 1 << airborne;
        }
        if (s.HasAction != 0)
        {
            known |= (1 << attacking) | (1 << casting) | (1 << usingItem);
        }
        if (s.InBase != 2)
        {
            known |= 1 << inBase;
            if (s.InBase == 1) on |= 1 << inBase;
        }
        if (s.HasBuffs != 0)
        {
            known |= (1 << rested) | (1 << fed) | (1 << hasDebuff) | (1 << hungry);
            if (s.RestedBuff != 0) on |= 1 << rested;
            if (s.FoodBuffs != 0) on |= 1 << fed;
            if (s.Debuffs != 0) on |= 1 << hasDebuff;
            if (s.Hungry != 0) on |= 1 << hungry;
        }
    }

    // Which kind of action runs: by the trigger and the item it used.
    private static void Classify(in Native.Snapshot s, ref ushort on)
    {
        const int attacking = 1, casting = 2, usingItem = 3;
        var trigger = IdOfRaw(8, s.Action.Trigger);
        var combat = trigger is PlayerStateId.ActionPrimary or PlayerStateId.ActionPrimary_Tap or PlayerStateId.ActionPrimary_Hold
            or PlayerStateId.ActionHeavy or PlayerStateId.ActionSecondary or PlayerStateId.ActionSpecialAbility;
        var use = combat || trigger is PlayerStateId.ActionTool;
        if (!use) return;
        var item = s.Action.UsedItem == 0 ? null : ItemInfoOf(s.Action.UsedItem);
        var ammo = s.Action.AmmoItem == 0 ? null : ItemInfoOf(s.Action.AmmoItem);
        var spell = ammo?.Ammunition == "Spell" || item?.Ammunition == "Spell" || item?.SlotType == "MidRangeWeapon";
        if (combat && spell) on |= 1 << casting;
        else if (combat && (item is null || item.Category == "Weapons")) on |= 1 << attacking;
        else if (item != null) on |= 1 << usingItem;
    }

    internal static IEnumerable<PlayerStateId> ActiveIn(PlayerSnapshot snap)
    {
        BuildTable();
        foreach (var i in _all!)
            if (snap.Is(i.Id)) yield return i.Id;
    }

    /// <summary>Diagnostics and testing.</summary>
    public static class Diagnostics
    {
        /// <summary>Scans an entity with no player record too (a spawned player character in a test lab), so its changes
        /// raise the events. Removed with <c>false</c>.</summary>
        public static void Watch(EntityId entity, bool on = true) => PlayerStateWatch.WatchExtra(entity, on);

        /// <summary>Test labs only (<c>KILN_PLAYERSTATE_LAB=1</c>): starts a sequence with a used item in the trigger
        /// context, as the game does for a real item use. Tick thread. False when off.</summary>
        [System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static bool LabUseItem(EntityId entity, uint sequence, PlayerStateId trigger, ulong slot, uint item)
        {
            var t = T;
            if (t == null) return false;
            return t->LabUseItem(entity.Value, sequence, (uint)RawOf(trigger), 0x38, slot, item) == Native.Ok;
        }

        /// <summary>One native read of these players, as the watch does it: nanoseconds. World's thread.</summary>
        /// <param name="motion">Also DynamicLocomotion and PlayerState.</param>
        /// <param name="input">Also PlayerInput and ActorInput.</param>
        /// <param name="extra">Also the less frequent sources and the base zones.</param>
        public static long Bench(IReadOnlyList<EntityId> players, bool motion, bool input, bool extra)
        {
            var t = T;
            if (t == null || players.Count == 0) return 0;
            var n = Math.Min(players.Count, 64);
            var ids = stackalloc uint[n];
            for (var i = 0; i < n; i++) ids[i] = players[i].Value;
            var snaps = new Native.Snapshot[n];
            for (var i = 0; i < n; i++) snaps[i].Size = (uint)sizeof(Native.Snapshot);
            var want = Native.WantActor | (motion ? Native.WantMotion : 0) | (input ? Native.WantInput : 0) | (extra ? Native.WantExtra | Native.WantBaseZone : 0);
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            fixed (Native.Snapshot* p = snaps) t->Scan(ids, (uint)n, want, p);
            return (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * (1_000_000_000L / System.Diagnostics.Stopwatch.Frequency);
        }

        /// <summary>Time of the last full pump of the events, nanoseconds (0 when nothing subscribes).</summary>
        public static long LastPumpNanoseconds => PlayerStateWatch.LastPumpNs;

        /// <summary>Ticks in which the pump ran.</summary>
        public static long PumpRuns => PlayerStateWatch.PumpRuns;

        /// <summary>The per-tick pump's total time, nanoseconds, native reads included.</summary>
        public static long PumpTotalNanoseconds => PlayerStateWatch.PumpTotalNs;
    }
}

/// <summary>An action a client can send.</summary>
/// <param name="Index">Its number on this build.</param>
/// <param name="Name">The game's field name: "changeEquipmentAction".</param>
/// <param name="Bytes">The size of its struct.</param>
/// <param name="CanCancel">A <see cref="PlayerClientActionEvent"/> for it can be canceled on this build: Kiln holds it for the
/// handlers before the game's program runs it (server world).</param>
public sealed record PlayerClientActionInfo(int Index, string Name, int Bytes, bool CanCancel);

/// <summary>What kiln.playerstate can do on this build.</summary>
/// <param name="Cancel">A <see cref="PlayerActionEvent"/> can be canceled (the game's reset of a running sequence resolved).</param>
public sealed record PlayerStateServiceStatus(bool Reflection, bool Cancel, bool SequenceNames, bool ItemDetails, int Sources,
    ulong Scans, ulong PlayersRead, string Detail)
{
    /// <summary>A <see cref="PlayerClientActionEvent"/> can be canceled: the point right before the game's programs, where
    /// Kiln holds a client's action, resolved on this build.</summary>
    public bool ClientActionCancel { get; init; }
}

/// <summary>A slot of a player's equipment, named as the game's SlotSelection.equipment fields.</summary>
public enum PlayerEquipSlot
{
    VanityHead, VanityUpperBody, VanityArms, VanityLowerBody, VanityFeet,
    ArmourHead, ArmourUpperBody, ArmourArms, ArmourLowerBody, ArmourFeet,
    Ring0, Ring1, RangedWeapon, Shield, Arrow, Spell, FishingBait, HookShot, Glider, Backpack,
}

/// <summary>What a player wears and holds at one moment.</summary>
public sealed class PlayerEquipment
{
    private readonly PlayerItem[] _items = new PlayerItem[PlayerStates.Native.Slots];
    private readonly int _count;

    internal unsafe PlayerEquipment(in PlayerStates.Native.Equipment q)
    {
        _count = (int)Math.Min(q.Count, (uint)_items.Length);
        fixed (uint* items = q.Items)
            for (var i = 0; i < _count; i++) _items[i] = new PlayerItem(items[i]);
        Held = new PlayerItem(q.HeldItem);
        HotbarIndex = q.Hotbar;
    }

    /// <summary>The item in a slot; <see cref="PlayerItem.IsNone"/> when empty. The enum follows the game's field order;
    /// it is checked against the running game when the watch starts.</summary>
    public PlayerItem this[PlayerEquipSlot slot] => (int)slot < _count ? _items[(int)slot] : default;

    /// <summary>What is in the player's hand: what the running action uses, else the selected action bar slot.</summary>
    public PlayerItem Held { get; }

    /// <summary>The selected action bar slot; -1 unknown.</summary>
    public int HotbarIndex { get; }

    internal int Count => _count;
}

public sealed partial record Player
{
    /// <summary>True when the player is in this state; false when not, or when it cannot be read (see
    /// <see cref="Check"/>). World's thread: live; other threads: the last tick's copy.</summary>
    public bool Is(PlayerStateId state) => PlayerStates.Is(Entity, state);

    /// <summary>True or false, or null when this state cannot be read here.</summary>
    public bool? Check(PlayerStateId state) => PlayerStates.Check(Entity, state);

    /// <summary>Everything at once, one read: states, action, health and the rest. Null when unreadable.</summary>
    public PlayerSnapshot? Snapshot() => PlayerStates.Read(Entity);

    public bool IsGliding => Is(PlayerStateId.Gliding);
    public bool IsAirborne => Is(PlayerStateId.Airborne);
    public bool IsGrounded => Is(PlayerStateId.Grounded);
    public bool IsJumping => Is(PlayerStateId.Jumping);
    public bool IsFalling => Is(PlayerStateId.LocomotionFalling);
    public bool IsSwimming => Is(PlayerStateId.Swimming);
    public bool IsDiving => Is(PlayerStateId.Diving);
    public bool IsUnderwater => Is(PlayerStateId.Underwater);
    public bool IsClimbing => Is(PlayerStateId.Climbing);
    public bool IsSprinting => Is(PlayerStateId.Sprinting);
    public bool IsCrouching => Is(PlayerStateId.Sneaking);
    public bool IsGrappling => Is(PlayerStateId.Grappling);
    public bool IsBlocking => Is(PlayerStateId.Blocking);
    public bool IsDodging => Is(PlayerStateId.Dodging);
    public bool IsAttacking => Is(PlayerStateId.Attacking);
    public bool IsCasting => Is(PlayerStateId.Casting);
    public bool IsUsingItem => Is(PlayerStateId.UsingItem);
    public bool IsSitting => Is(PlayerStateId.Sitting);
    public bool IsSleeping => Is(PlayerStateId.Sleeping);
    public bool IsDead => Is(PlayerStateId.Dead);
    public bool IsInBase => Is(PlayerStateId.InBase);
    public bool IsInShroud => Is(PlayerStateId.InShroud);
    public bool IsInCombat => Is(PlayerStateId.InCombat);

    public int? Health => Snapshot()?.Health;
    public int? HealthMax => Snapshot()?.HealthMax;
    public int? Mana => Snapshot()?.Mana;
    public int? ManaMax => Snapshot()?.ManaMax;
    public int? Stamina => Snapshot()?.Stamina;
    public int? StaminaMax => Snapshot()?.StaminaMax;
    /// <summary>The Shroud timer the game shows. Null when unknown.</summary>
    public TimeSpan? ShroudTimeLeft => Snapshot()?.ShroudTimeLeft;

    /// <summary>What the player wears and holds. Null when unreadable. World's thread.</summary>
    public PlayerEquipment? Equipment => PlayerStates.EquipmentOf(Entity);

    /// <summary>The item in hand. World's thread.</summary>
    public PlayerItem HeldItem => Equipment?.Held ?? default;

    /// <summary>The buffs on the player. Server world only. World's thread.</summary>
    public IReadOnlyList<PlayerBuff> Buffs => PlayerStates.BuffsOf(Entity);

    /// <summary>Where the player's camera is and where it looks (<see cref="PlayerStates.AimOf"/>); null before the
    /// player's client sent a camera. World's thread.</summary>
    /// <exception cref="KilnUnavailableException"><c>players.aim</c> is off.</exception>
    [TickThreadOnly]
    [RequiresCapability(PlayerAim.Capability)]
    public PlayerAim? Aim => PlayerStates.AimOf(Entity);

    /// <summary>The action the player runs now: null when none. World's thread.</summary>
    public PlayerAction? CurrentAction => Snapshot()?.Action;
}
