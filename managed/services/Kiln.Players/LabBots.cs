// SPDX-License-Identifier: MIT
// Bot players for lab tests (native/core/abi/services/kiln_labbot.h; docs/kiln/modding/testing.md, "Bot players").
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>Lab only: where a bot player's join is (<see cref="LabBotInfo.Phase"/>).</summary>
[Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[EditorBrowsable(EditorBrowsableState.Never)]
public enum LabBotPhase
{
    /// <summary>No bot.</summary>
    Free = 0,
    /// <summary>Listed in the session's state; the game's login follows.</summary>
    Connecting = 1,
    /// <summary>The login made the player record; the character upload follows.</summary>
    LoggingIn = 2,
    /// <summary>The game searches a spawn position; then the bot says it is ready.</summary>
    Loading = 3,
    /// <summary>The record is in game; the game spawns the player entity.</summary>
    Spawning = 4,
    /// <summary>The player entity exists (<see cref="LabBotInfo.Entity"/>).</summary>
    InGame = 5,
    /// <summary>Logging out: the final save, then the game removes the entity and the record.</summary>
    Leaving = 6,
    /// <summary>Gone; <see cref="LabBotInfo.Reason"/> says how.</summary>
    Left = 7,
    /// <summary>The join failed; <see cref="LabBotInfo.Reason"/> says where.</summary>
    Failed = 8,
}

/// <summary>Lab only: which user group of enshrouded_server.json a bot joins in (its rights, as a password picks).</summary>
[Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[EditorBrowsable(EditorBrowsableState.Never)]
public enum LabBotGroup
{
    /// <summary>The group with the most rights (kick and ban first): admin commands work for the bot.</summary>
    Admin = 0,
    /// <summary>The group with the fewest rights.</summary>
    Least = 1,
    /// <summary>The n-th entry of userGroups (<c>groupIndex</c>).</summary>
    Index = 2,
}

/// <summary>Lab only: one bot player as kiln.labbot last stepped it.</summary>
/// <param name="Id">The bot's id (1 and up; never reused in a process).</param>
/// <param name="Phase">Where its join is.</param>
/// <param name="Name">The name the game, chat and logs show.</param>
/// <param name="Slot">The player record slot (0..15).</param>
/// <param name="Machine">Its machine handle (what kiln.net calls its peer).</param>
/// <param name="Connection">Its player connection handle (chat lines carry it).</param>
/// <param name="Entity">The player entity; <see cref="EntityId.None"/> until <see cref="LabBotPhase.InGame"/>.</param>
/// <param name="RecordState">The game's record state: 0 free, 1 loading, 2 loaded, 3 in game, 4/5 removing, 6 failed.</param>
/// <param name="Permissions">The record's user-group rights (bit 0 kick/ban, 1 inventories, 2 edit base, 3 edit world,
/// 4 extend base, 5 receive experience).</param>
/// <param name="Group">The user-group id it joined with.</param>
/// <param name="SavesAcked">Character saves the bot acknowledged.</param>
/// <param name="Account">Its account id (derived from the name).</param>
/// <param name="CharacterId">Its character id (derived from the name: the same name is the same character on a world).</param>
/// <param name="JoinedStep">Server step of the join.</param>
/// <param name="InGameStep">Step its entity appeared (0: not yet).</param>
/// <param name="Reason">The last event; for <see cref="LabBotPhase.Failed"/> and <see cref="LabBotPhase.Left"/>: why.</param>
[Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record LabBotInfo(int Id, LabBotPhase Phase, string Name, int Slot, uint Machine, uint Connection, EntityId Entity,
    int RecordState, uint Permissions, uint Group, int SavesAcked, ulong Account, Guid CharacterId, ulong JoinedStep,
    ulong InGameStep, string Reason)
{
    /// <summary>The bot is in the world with its player entity.</summary>
    public bool InGame => Phase == LabBotPhase.InGame;

    /// <summary>The bot is gone (left or failed).</summary>
    public bool Ended => Phase is LabBotPhase.Left or LabBotPhase.Failed or LabBotPhase.Free;
}

/// <summary>Lab only: whether bots can join here.</summary>
/// <param name="Available">Bots may join now (a lab server with a running server world).</param>
/// <param name="Bots">Bots not left or failed.</param>
/// <param name="Groups">User groups the server has.</param>
/// <param name="Publishes">Published session states the bots were added to.</param>
/// <param name="Why">When not available: why.</param>
[Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record LabBotStatus(bool Available, int Bots, int Groups, ulong Publishes, string Why);

/// <summary>
/// Lab only: bot players, for tests that need a real player in the world (a pet's owner, a tamer, a rider) and no game
/// client. A bot is a player the dedicated server hosts itself: the game's own login, character, spawn, rights, saves and
/// logout run for it, so Players, CharacterData, chat commands and kiln.net see a player like any other. It has no
/// network connection (what the server sends it goes nowhere) and it moves only when something moves it
/// (<c>World.Teleport</c> of its <see cref="LabBotInfo.Entity"/>). Its name picks its character and account: a bot that
/// joins again under the same name is the same character on that world.
/// <para>Lab servers only: capability <c>labbot.join</c> needs kiln.json's policy <c>"any"</c> and kiln.net's lab port
/// (<c>labPort</c> in <c>kiln/config/network.json</c>). No Steam account, ticket or password is used. A mod's bots drop
/// when it unloads, and every bot leaves when the world stops. Lab scripts drive bots with tools/labbot
/// (mods/tests/labbot-test's control channel).</para>
/// </summary>
[Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class LabBots
{
    /// <summary>The capability row: bots may join on this server (a lab server, the symbols resolved, hooks in).</summary>
    public const string Capability = "labbot.join";

    /// <summary>At most this many bots at once.</summary>
    public const int Max = 4;

    /// <summary>Whether bots can join now, and why not.</summary>
    /// <exception cref="KilnUnavailableException">kiln.labbot is not here (not a server, or not installed).</exception>
    public static LabBotStatus Status()
    {
        var t = Require("Status");
        LabBotNative.Status s = default;
        s.Size = (uint)sizeof(LabBotNative.Status);
        Check(t->StatusFn(&s), "Status");
        return new LabBotStatus(s.Available != 0, (int)s.Bots, (int)s.Groups, s.Publishes, Text(s.Why, 200));
    }

    /// <summary>Starts a bot's join under <paramref name="name"/>; it goes on by itself in the next server frames
    /// (<see cref="Info"/> shows where it is; about a second to <see cref="LabBotPhase.InGame"/> on a loaded world).</summary>
    /// <param name="name">1 to 32 characters of letters, digits, space, '_', '-' and '.'; no space at either end.</param>
    /// <param name="group">The user group it joins in (its rights).</param>
    /// <param name="groupIndex">With <see cref="LabBotGroup.Index"/>: the entry of userGroups.</param>
    /// <returns>The bot's id.</returns>
    /// <exception cref="ArgumentException">A bad name, or a live bot has it. The message says which.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is none of <see cref="LabBotGroup"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Max"/> bots are live, no player slot is free, no machine
    /// index is free that no bot held in this server session (each bot needs a fresh one, about 15 per server session:
    /// restart the lab), or no such user group.</exception>
    /// <exception cref="KilnUnavailableException"><c>labbot.join</c> is off (not a lab server) or no server world runs.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability("labbot.join")]
    public static int Join(string name, LabBotGroup group = LabBotGroup.Admin, int groupIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (group is < LabBotGroup.Admin or > LabBotGroup.Index) throw new ArgumentOutOfRangeException(nameof(group), group, "admin, least or index");
        Services.RequireTickThread("LabBots.Join");
        var t = Require("Join");
        var bytes = Encoding.UTF8.GetBytes(name);
        uint id = 0;
        int rc;
        fixed (byte* p = bytes)
        {
            LabBotNative.JoinRequest rq = default;
            rq.Size = (uint)sizeof(LabBotNative.JoinRequest);
            rq.Name = new NativeStr { Ptr = p, Len = (nuint)bytes.Length };
            rq.Group = (uint)group;
            rq.GroupIndex = (uint)Math.Max(0, groupIndex);
            rc = t->Join(Services.CallerOwner, &rq, &id);
        }
        switch (rc)
        {
            case LabBotNative.Ok:
                return (int)id;
            case LabBotNative.InvalidArgument:
                throw new ArgumentException($"bot '{name}': {WhyRefused(name)}", nameof(name));
            case LabBotNative.NotFound:
                throw new InvalidOperationException($"no room for bot '{name}': {Max} bots are live, no player slot is free, no machine " +
                                                    "index is free that no bot held in this server session (each bot needs a fresh one: " +
                                                    "restart the lab), or no such user group");
            case LabBotNative.Unavailable:
                throw new KilnUnavailableException(Capability, Status().Why);
            default:
                throw new InvalidOperationException($"kiln.labbot refused the join ({rc})");
        }
    }

    /// <summary>Ends a bot's game: a logout (the final save, then the game removes it), or <paramref name="drop"/> as
    /// a lost connection. A bot not yet in game always drops.</summary>
    /// <exception cref="ArgumentException">No live bot of that id.</exception>
    /// <exception cref="KilnUnavailableException"><c>labbot.join</c> is off.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability("labbot.join")]
    public static void Leave(int id, bool drop = false)
    {
        Services.RequireTickThread("LabBots.Leave");
        var t = Require("Leave");
        var rc = t->Leave((uint)id, drop ? LabBotNative.LeaveDrop : 0u);
        if (rc == LabBotNative.NotFound) throw new ArgumentException($"no live bot {id}", nameof(id));
        if (rc == LabBotNative.Unavailable) throw new KilnUnavailableException(Capability, Status().Why);
        Check(rc, "Leave");
    }

    /// <summary>Where the bot's client says its camera is and looks: what a client sends with every input and the game's
    /// own code reads from the player's input. An item whose use spawns at the "crosshair" (a thrown animal bait) starts
    /// at <paramref name="position"/> moved <paramref name="distance"/> along the view and flies along the view; the game
    /// turns its view as <paramref name="rotation"/> applied to (0, 0, 1) (yaw about +Y, then pitch: a positive pitch looks
    /// down). Kiln writes it into the bot's input every server frame from now on while the bot is in game, where a client's
    /// input lands; it stays until set again. Since dev.32 (lab only).</summary>
    /// <param name="id">A live bot.</param>
    /// <param name="position">Where the camera is (the bot's eyes are about 1.7 m above its feet).</param>
    /// <param name="rotation">Where it looks (normalised here).</param>
    /// <param name="distance">The camera's boom in metres (0 or more): the game's crosshair point is that far along the
    /// view.</param>
    /// <exception cref="ArgumentException"><paramref name="rotation"/> is no rotation (zero or not finite), or no live bot
    /// of that id.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="distance"/> is negative or not finite.</exception>
    /// <exception cref="KilnUnavailableException"><c>labbot.join</c> is off, or kiln.labbot cannot write a bot's input on
    /// this build or is older (kiln.log says why).</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability("labbot.join")]
    public static void SetCamera(int id, WorldPosition position, Quaternion rotation, float distance = 0f)
    {
        var length = rotation.Length();
        if (!float.IsFinite(length) || length < 1e-3f)
            throw new ArgumentException($"{rotation} is no rotation", nameof(rotation));
        if (!float.IsFinite(distance) || distance < 0) throw new ArgumentOutOfRangeException(nameof(distance), distance, "0 or more metres");
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
            throw new ArgumentException($"{position} is no position", nameof(position));
        var q = Quaternion.Normalize(rotation);
        LabBotNative.InputData d = default;
        d.Flags = LabBotNative.InputCamera;
        d.X = Fixed(position.X);
        d.Y = Fixed(position.Y);
        d.Z = Fixed(position.Z);
        (d.Qx, d.Qy, d.Qz, d.Qw) = (q.X, q.Y, q.Z, q.W);
        d.Distance = distance;
        Input(id, ref d, "SetCamera");
    }

    /// <summary>Whether the bot's client says it is ready for gameplay (what a client sends with every input once its
    /// world is loaded): the game marks the bot's actor ready for gameplay only then, and a use of an item, its control
    /// programs and the readiness checks of the game wait for it. A bot says nothing until this is called. Kiln writes it
    /// into the bot's input every server frame from now on while the bot is in game. Since dev.32 (lab only).</summary>
    /// <exception cref="ArgumentException">No live bot of that id.</exception>
    /// <exception cref="KilnUnavailableException"><c>labbot.join</c> is off, or kiln.labbot cannot write a bot's input on
    /// this build or is older (kiln.log says why).</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability("labbot.join")]
    public static void SetReady(int id, bool ready = true)
    {
        LabBotNative.InputData d = default;
        d.Flags = LabBotNative.InputReady;
        d.Ready = ready ? 1u : 0u;
        Input(id, ref d, "SetReady");
    }

    private static long Fixed(double metres) => checked((long)Math.Round(metres * 4294967296.0));  // keen::WorldPosition 32.32

    private static void Input(int id, ref LabBotNative.InputData d, string api)
    {
        Services.RequireTickThread("LabBots." + api);
        var t = Require(api);
        if (!LabBotNative.HasInput(t))
            throw new KilnUnavailableException(Capability, $"LabBots.{api} needs a newer kiln.labbot (one with the bots' client input)");
        d.Size = (uint)sizeof(LabBotNative.InputData);
        int rc;
        fixed (LabBotNative.InputData* p = &d) rc = ((LabBotNative.TableV2*)t)->InputFn((uint)id, p);
        switch (rc)
        {
            case LabBotNative.Ok:
                return;
            case LabBotNative.NotFound:
                throw new ArgumentException($"no live bot {id}", nameof(id));
            case LabBotNative.InvalidArgument:
                throw new ArgumentException($"kiln.labbot refused bot {id}'s input (kiln.log says why)", nameof(id));
            case LabBotNative.Unavailable:
                throw new KilnUnavailableException(Capability,
                    $"LabBots.{api}: kiln.labbot cannot write a bot's input here ({(Status() is { Available: false } s ? s.Why : "PlayerInput's client parts did not resolve on this build; kiln.log says which")})");
            default:
                throw new InvalidOperationException($"LabBots.{api}: kiln.labbot answered {rc}");
        }
    }

    /// <summary>The bot of <paramref name="id"/>, or null when none is remembered. Any thread.</summary>
    public static LabBotInfo? Info(int id)
    {
        var t = Require("Info");
        LabBotNative.Info i = default;
        i.Size = (uint)sizeof(LabBotNative.Info);
        var rc = t->InfoFn((uint)id, &i);
        if (rc == LabBotNative.NotFound) return null;
        Check(rc, "Info");
        return Convert(&i);
    }

    /// <summary>Every remembered bot (live ones and the last that left or failed in each place), newest first. Any thread.</summary>
    public static IReadOnlyList<LabBotInfo> All()
    {
        var t = Require("All");
        var buf = stackalloc LabBotNative.Info[Max * 2];
        for (var k = 0; k < Max * 2; k++) buf[k].Size = (uint)sizeof(LabBotNative.Info);
        uint count = 0;
        Check(t->List(buf, Max * 2, &count), "All");
        var list = new List<LabBotInfo>((int)Math.Min(count, (uint)(Max * 2)));
        for (var k = 0; k < count && k < Max * 2; k++) list.Add(Convert(&buf[k]));
        return list;
    }

    // kiln.labbot's KILN_ERR_INVALID_ARGUMENT for a join, told apart: its name rules (bot.cpp valid_name), a live bot of
    // that name, else kiln.log (the flags and group are this wrapper's own, checked above).
    private static string WhyRefused(string name)
    {
        var bytes = Encoding.UTF8.GetByteCount(name);
        if (bytes is 0 or > 32) return "a bot name is 1 to 32 characters";
        if (name[0] == ' ' || name[^1] == ' ') return "a bot name has no space at either end";
        if (name.Any(c => c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ' or '_' or '-' or '.')))
            return "a bot name uses letters, digits, space, '_', '-' and '.' only";
        if (All().Any(b => !b.Ended && b.Name == name)) return "a live bot has that name";
        return "kiln.labbot refused the join (kiln.log says why)";
    }

    private static LabBotInfo Convert(LabBotNative.Info* i) =>
        new((int)i->Id, (LabBotPhase)i->Phase, Text(i->Name, 40), (int)i->Slot, i->Machine, i->Player, new EntityId(i->Entity),
            (int)i->RecordState, i->Permissions, i->Group, (int)i->SavesAcked, i->Account, new Guid(new ReadOnlySpan<byte>(i->CharacterId, 16)),
            i->JoinedStep, i->InGameStep, Text(i->Reason, 200));

    private static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }

    private static void Check(int rc, string api)
    {
        if (rc != LabBotNative.Ok) throw new InvalidOperationException($"LabBots.{api}: kiln.labbot answered {rc}");
    }

    private static LabBotNative.Table* Require(string api) =>
        LabBotNative.T != null ? LabBotNative.T : throw new KilnUnavailableException("kiln.labbot", $"LabBots.{api}: kiln.labbot is not on this server");
}

// kiln_labbot.h's structs and table (layouts asserted there).
internal static unsafe class LabBotNative
{
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3;
    public const uint LeaveDrop = 1;
    public const uint InputCamera = 1, InputReady = 2;  // KILN_LABBOT_INPUT_*

    /// <summary>The table has input() (kiln.labbot since dev.32 labbot-2).</summary>
    public static bool HasInput(Table* t) => t != null && t->Header.Size >= (uint)sizeof(TableV2);

    [StructLayout(LayoutKind.Sequential)]
    public struct JoinRequest
    {
        public uint Size, Flags;
        public NativeStr Name;
        public fixed byte CharacterId[16];
        public ulong Account;
        public uint Group, GroupIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public uint Size, Id, Phase, Slot, Machine, Player, Entity, RecordState, Permissions, Group, SavesAcked;
        public ulong Account;
        public fixed byte CharacterId[16];
        public ulong JoinedStep, InGameStep;
        public fixed byte Name[40];
        public fixed byte Reason[200];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size, Available, Bots, Groups;
        public ulong Publishes;
        public fixed byte Why[200];
    }

    // kiln_labbot.h KilnLabBotInput
    [StructLayout(LayoutKind.Sequential)]
    public struct InputData
    {
        public uint Size, Flags;
        public long X, Y, Z;
        public float Qx, Qy, Qz, Qw, Distance;
        public uint Ready;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> StatusFn;
        public delegate* unmanaged[Cdecl]<ulong, JoinRequest*, uint*, int> Join;
        public delegate* unmanaged[Cdecl]<uint, uint, int> Leave;
        public delegate* unmanaged[Cdecl]<uint, Info*, int> InfoFn;
        public delegate* unmanaged[Cdecl]<Info*, uint, uint*, int> List;
    }

    // the table with input() (since dev.32 labbot-2); an older kiln-labbot.dll's header is smaller
    [StructLayout(LayoutKind.Sequential)]
    public struct TableV2
    {
        public Table Base;
        public delegate* unmanaged[Cdecl]<uint, InputData*, int> InputFn;
    }

    private static Table* _table;
    private static bool _looked;

    /// <summary>The rows found the service after a miss: look it up again at the next call.</summary>
    internal static void LookAgain()
    {
        if (_table == null) _looked = false;
    }

    /// <summary>Tests: answer from <paramref name="table"/> (null: look the service up again).</summary>
    internal static void UseTable(Table* table)
    {
        _table = table;
        _looked = table != null;
    }

    public static Table* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _looked = true;
            if (sizeof(JoinRequest) != 56 || sizeof(Info) != 328 || sizeof(Status) != 224 || sizeof(Table) != 48)
                return null;  // layout drift between this assembly and kiln_labbot.h: refuse rather than misread
            _table = Services.Get<Table>("kiln.labbot", 1);
            return _table;
        }
    }
}
