// SPDX-License-Identifier: MIT
// Kiln.PlayerAnim: the managed face of kiln.playeranim (native/core/abi/services/kiln_playeranim.h). Mods' states in the
// player's animation graph, merged from every enabled mod's assets/player-states.json at boot. The record a state is
// written as, the lookup (game states, mod states, the served graph's state and hash) and a definition from code.
// Both sides, any thread. Capability: anim.player_states.
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kiln;

/// <summary>A state a mod adds to the player's animation graph: the C# form of one entry of
/// <c>assets/player-states.json</c>. The mod id is the defining mod's; the file has no <c>mod</c> key.</summary>
/// <param name="Name">The state's name: 1 to 64 characters, no <c>/</c>, case-sensitive, unique in the mod. Its GUID is
/// <see cref="PlayerAnimStates.GuidOf"/>(mod, name).</param>
/// <param name="Clip">A clip of this mod's cook by name, or a GUID written out (a clip the game or another mod serves).</param>
/// <param name="Like">A state of the player's graph by its debug name (<c>State</c>, or <c>Machine/State</c> when several
/// machines have the name). The new state sits where it sits, is entered and left as it is, and plays its clip as it
/// does.</param>
[AnyThread]
public sealed record PlayerAnimState(string Name, string Clip, string Like)
{
    /// <summary>Blend-in time in seconds, 0 to 5; null: the neighbour's.</summary>
    public float? BlendIn { get; init; }

    /// <summary>Whether the clip loops; null: the clip's own flag.</summary>
    public bool? Loop { get; init; }

    /// <summary>Play speed, above 0; null: 1.</summary>
    public float? Speed { get; init; }
}

/// <summary>How a sequence enters a state.</summary>
public enum PlayerAnimStateKind
{
    /// <summary><c>SetAnimationEvent.animationName</c> = the state's GUID (full-body machines).</summary>
    State = 0,
    /// <summary><c>SetAnimationOverlayEvent.overlayName</c> = the state's GUID (overlay machines).</summary>
    Overlay = 1,
}

/// <summary>Where a mod's state stands.</summary>
public enum PlayerAnimStateStatus
{
    /// <summary>In the graph this process serves: a sequence that names it enters it.</summary>
    Live = 0,
    /// <summary>Not merged; the reason says why. The mod's other states are not affected.</summary>
    Refused = 1,
    /// <summary>Defined from code after the boot merge: kept for the next start, not in the graph now. The game reads
    /// the player's graph once per process, so a state defined or changed while the game runs takes effect only after
    /// a restart, on every side that has the mod.</summary>
    Pending = 2,
    /// <summary>The merge is off on this game build; the reason is the service's.</summary>
    Unavailable = 3,
}

/// <summary>What the player's graph is.</summary>
public enum PlayerAnimGraphState
{
    /// <summary>The service is missing or turned off, or the merge cannot be proven on this game build: the game's own
    /// graph is served.</summary>
    Off = 0,
    /// <summary>The game's resource service is not up yet: nothing is merged yet.</summary>
    Waiting = 1,
    /// <summary>No state to merge (none defined, or every one refused): the game's own graph.</summary>
    Untouched = 2,
    /// <summary>The merged graph is served.</summary>
    Merged = 3,
}

/// <summary>A state of the game's own player graph. The graph holds ids, never GUIDs.</summary>
/// <param name="Machine">The debug name of the machine it is in.</param>
/// <param name="Name">Its debug name.</param>
/// <param name="Kind">How a sequence enters it.</param>
/// <param name="Ids">What enters it: one id for most states, four for a jump-attack state (one per weapon family), none
/// for a state no id enters (such as <c>Idle_Block</c>).</param>
[AnyThread]
public sealed record PlayerAnimGameState(string Machine, string Name, PlayerAnimStateKind Kind, IReadOnlyList<uint> Ids);

/// <summary>A state a mod defines, in its <c>assets/player-states.json</c> or from code.</summary>
/// <param name="Mod">The defining mod's id.</param>
/// <param name="Name">The state's name.</param>
/// <param name="Guid"><see cref="PlayerAnimStates.GuidOf"/>(Mod, Name): what a sequence event names.</param>
/// <param name="Id"><see cref="PlayerAnimStates.IdOf"/>(Guid): what the graph compares.</param>
/// <param name="Kind">How a sequence enters it (follows from the neighbour's machine).</param>
/// <param name="Status">Live, Refused, Pending (defined from code, in the graph from the next start) or Unavailable.</param>
/// <param name="Like">The game state it is like, as written.</param>
/// <param name="Machine">The debug name of the machine it is in; empty when it was not placed.</param>
/// <param name="Reason">Why it is not live; empty when it is.</param>
[AnyThread]
public sealed record PlayerAnimStateInfo(string Mod, string Name, Guid Guid, uint Id, PlayerAnimStateKind Kind,
    PlayerAnimStateStatus Status, string Like, string Machine, string Reason);

/// <summary>The answer to <see cref="PlayerAnimStates.Define"/>. <see cref="Ok"/> is true only when the state is
/// <see cref="PlayerAnimStateStatus.Live"/>: the boot merge already has exactly this definition. <see cref="PlayerAnimStateStatus.Pending"/>
/// means the definition is kept for the next start and is not in the game now; a sequence that names the state does
/// nothing until the game (server and every client) has restarted with it.</summary>
/// <param name="Mod">The defining mod's id.</param>
/// <param name="Name">The state's name.</param>
/// <param name="Guid">The state's GUID (<see cref="PlayerAnimStates.GuidOf"/>).</param>
/// <param name="Id">The state's id in the graph (<see cref="PlayerAnimStates.IdOf"/>).</param>
/// <param name="Status">Live, Pending or Refused.</param>
/// <param name="Reason">Null when live; "kept for the next start" when pending; the service's reason when refused.</param>
[AnyThread]
public sealed record PlayerAnimDefineResult(string Mod, string Name, Guid Guid, uint Id, PlayerAnimStateStatus Status, string? Reason)
    : IKilnResult
{
    /// <summary>True only for <see cref="PlayerAnimStateStatus.Live"/>.</summary>
    public bool Ok => Status == PlayerAnimStateStatus.Live;
}

/// <summary>Mods' states in the player's animation graph (<c>kiln.playeranim</c>). Kiln merges every enabled mod's
/// <c>assets/player-states.json</c> into the player's graph once, at boot, on the server and on each client with the
/// same mods, and serves the result for the whole process; the join handshake refuses a client whose graph differs.
/// Both sides, any thread. Without the service (an older native side) <see cref="Available"/> is false, the graph is
/// <see cref="PlayerAnimGraphState.Off"/> and the lookups answer empty; asking never throws.
/// Capability: <c>anim.player_states</c>. Guide: <c>docs/kiln/services/playeranim.md</c>.</summary>
/// <remarks>Every type of this library starts with <c>PlayerAnim</c>: Kiln.Players' <c>PlayerState…</c> family
/// (<c>PlayerStates</c>, <c>PlayerState</c>, <c>PlayerStateInfo</c>, <c>PlayerStateId</c> and more: the game's player state
/// flags and the join state) means something else. These are states of the player's animation graph.</remarks>
public static unsafe class PlayerAnimStates
{
    /// <summary>The capability this class answers for.</summary>
    internal const string Capability = "anim.player_states";

    /// <summary>What <see cref="GraphHash"/> is for the game's own graph, and on a side without the service.</summary>
    internal const string NoHash = "0000000000000000";

    internal const string KeptForNextStart = "kept for the next start";
    internal const string NoDefineInBuild =
        "definitions from code are not available in this build; write the state in assets/player-states.json";

    private static readonly Guid Namespace = new("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77");

    private static PlayerAnimNative.Table* _table;
    private static PlayerAnimNative.Table* T => _table != null ? _table : (_table = PlayerAnimNative.Get());

    /// <summary>True when the native service <c>kiln.playeranim</c> is here.</summary>
    [AnyThread]
    public static bool Available => T != null;

    /// <summary>What the player's graph is on this side: <see cref="PlayerAnimGraphState.Off"/> without the service.</summary>
    [AnyThread]
    [RequiresCapability("anim.player_states")]
    public static PlayerAnimGraphState Graph
    {
        get
        {
            var t = T;
            if (t == null) return PlayerAnimGraphState.Off;
            var s = t->State();
            return s is >= 0 and <= (int)PlayerAnimGraphState.Merged ? (PlayerAnimGraphState)s : PlayerAnimGraphState.Off;
        }
    }

    /// <summary>Which graph this side serves: 16 hex digits (the first 8 bytes of SHA-256 over the served graph), all
    /// zero when it is the game's own (untouched, off, waiting) or the service is missing. Server and client must have
    /// the same to play the same states.</summary>
    [AnyThread]
    [RequiresCapability("anim.player_states")]
    public static string GraphHash
    {
        get
        {
            var t = T;
            if (t == null) return NoHash;
            var h = t->GraphHash().ToString();
            return IsHash(h) ? h : NoHash;
        }
    }

    internal static bool IsHash(string h) => h.Length == 16 && h.All(Uri.IsHexDigit);

    /// <summary>The game's own states with this name, <c>State</c> or <c>Machine/State</c>. Empty: the game's graph has
    /// no such state (or it cannot be read on this build, or the service is missing). One entry: that state. Several:
    /// the bare name is in several machines, one entry each, in the graph's order; name it as <c>Machine/State</c>.
    /// An older service table without <c>game_state_at</c> describes only the first of several in full: there the others
    /// have an empty <see cref="PlayerAnimGameState.Machine"/> and no ids.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    [AnyThread]
    [RequiresCapability("anim.player_states")]
    public static IReadOnlyList<PlayerAnimGameState> FindGameStates(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var t = T;
        if (t == null || name.Length == 0) return [];
        var bytes = Encoding.UTF8.GetBytes(name);
        PlayerAnimNative.GameState g = default;
        g.Size = (uint)sizeof(PlayerAnimNative.GameState);
        uint matches = 0;
        fixed (byte* p = bytes)
        {
            var s = new NativeStr { Ptr = p, Len = (nuint)bytes.Length };
            if (t->GameState(s, &g, &matches) != PlayerAnimNative.Ok || matches == 0) return [];
            var count = (int)Math.Min(matches, 64u);
            var list = new List<PlayerAnimGameState>(count) { Game(&g, GameIds(t, s, g.IdCount, null)) };
            var full = PlayerAnimNative.HasAt(t);
            for (uint i = 1; i < count; i++)
            {
                PlayerAnimNative.GameState gi = default;
                gi.Size = (uint)sizeof(PlayerAnimNative.GameState);
                if (full && t->GameStateAt(s, i, &gi) == PlayerAnimNative.Ok) list.Add(Game(&gi, GameIds(t, s, gi.IdCount, i)));
                else list.Add(new PlayerAnimGameState("", g.Name.ToString(), (PlayerAnimStateKind)g.Kind, []));
            }
            return list;
        }
    }

    private static PlayerAnimGameState Game(PlayerAnimNative.GameState* g, uint[] ids) =>
        new(g->Machine.ToString(), g->Name.ToString(), (PlayerAnimStateKind)g->Kind, ids);

    /// <summary>The ids of the first match (<paramref name="index"/> null: game_state_ids) or of match
    /// <paramref name="index"/> (game_state_ids_at).</summary>
    private static uint[] GameIds(PlayerAnimNative.Table* t, NativeStr name, uint hint, uint? index)
    {
        var capacity = Math.Max(hint, 4u);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var ids = new uint[capacity];
            nuint count = 0;
            int rc;
            fixed (uint* p = ids)
                rc = index is { } i ? t->GameStateIdsAt(name, i, p, capacity, &count) : t->GameStateIds(name, p, capacity, &count);
            if (rc == PlayerAnimNative.BufferTooSmall || (rc == PlayerAnimNative.Ok && count > capacity))
            {
                capacity = (uint)Math.Min((ulong)count, 4096);
                continue;
            }
            if (rc != PlayerAnimNative.Ok) return [];
            return count == capacity ? ids : ids[..(int)count];
        }
        return [];
    }

    /// <summary>Mod <paramref name="mod"/>'s state <paramref name="name"/>, from its <c>assets/player-states.json</c> or
    /// from code, with its status. Null: the mod defines no such state, or the service is missing.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> or <paramref name="name"/> is null.</exception>
    [AnyThread]
    [RequiresCapability("anim.player_states")]
    public static PlayerAnimStateInfo? Find(string mod, string name)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(name);
        var t = T;
        if (t == null || mod.Length == 0 || name.Length == 0) return null;
        var m = Encoding.UTF8.GetBytes(mod);
        var n = Encoding.UTF8.GetBytes(name);
        PlayerAnimNative.State s = default;
        s.Size = (uint)sizeof(PlayerAnimNative.State);
        int rc;
        fixed (byte* pm = m)
        fixed (byte* pn = n)
            rc = t->ModState(new NativeStr { Ptr = pm, Len = (nuint)m.Length }, new NativeStr { Ptr = pn, Len = (nuint)n.Length }, &s);
        return rc == PlayerAnimNative.Ok ? Info(&s) : null;
    }

    /// <summary>Every state mods define, by mod id, then name. A malformed file exposes the names read before the error as
    /// Refused with the file's reason; when none can be read, one Refused entry named <c>&lt;file&gt;</c> represents the file.
    /// An unreadable manifest uses its folder name as the mod identity. Empty while waiting, or without the service.</summary>
    [AnyThread]
    [RequiresCapability("anim.player_states")]
    public static IReadOnlyList<PlayerAnimStateInfo> All
    {
        get
        {
            var t = T;
            if (t == null) return [];
            var n = t->StateCount();
            var list = new List<PlayerAnimStateInfo>((int)Math.Min(n, 4096u));
            for (uint i = 0; i < n; i++)
            {
                PlayerAnimNative.State s = default;
                s.Size = (uint)sizeof(PlayerAnimNative.State);
                if (t->StateAt(i, &s) == PlayerAnimNative.Ok) list.Add(Info(&s));
            }
            return list;
        }
    }

    private static PlayerAnimStateInfo Info(PlayerAnimNative.State* s) => new(
        s->Mod.ToString(), s->Name.ToString(), new Guid(new ReadOnlySpan<byte>(s->Guid, 16)), s->Id, (PlayerAnimStateKind)s->Kind,
        s->Status <= (uint)PlayerAnimStateStatus.Unavailable ? (PlayerAnimStateStatus)s->Status : PlayerAnimStateStatus.Unavailable,
        s->Like.ToString(), s->Machine.ToString(), s->Reason.ToString());

    /// <summary>The GUID of mod <paramref name="mod"/>'s state <paramref name="name"/>, by the rule alone (no lookup):
    /// UUIDv5 of <c>&lt;mod&gt;:state:&lt;name&gt;</c> in Kiln's id namespace, as <c>BlockIds.Derive(mod, "state", name)</c>
    /// and animcook's <c>derive</c>. Game byte order is <see cref="Guid.ToByteArray()"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> or <paramref name="name"/> is null.</exception>
    [AnyThread]
    public static Guid GuidOf(string mod, string name)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> ns = stackalloc byte[16];
        Namespace.TryWriteBytes(ns, bigEndian: true, out _);
        var text = Encoding.UTF8.GetBytes($"{mod}:state:{name}");
        var all = new byte[16 + text.Length];
        ns.CopyTo(all);
        text.CopyTo(all, 16);
        var h = SHA1.HashData(all);
        h[6] = (byte)((h[6] & 0x0f) | 0x50);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>A state's id in the graph: FNV-1a-32 of its GUID's 16 bytes in the game's order
    /// (<see cref="Guid.ToByteArray()"/>), as <c>BlockIds.Fnv</c>.</summary>
    [AnyThread]
    public static uint IdOf(Guid state)
    {
        Span<byte> b = stackalloc byte[16];
        state.TryWriteBytes(b);
        uint h = 0x811c9dc5u;
        foreach (var x in b) h = (h ^ x) * 0x01000193u;
        return h;
    }

    /// <summary>Defines a player state from the calling mod's code. The game reads the player's graph once per process
    /// and a change while a player's graph is live would end the game, so a definition from code never changes the
    /// running game: when the boot merge already has exactly this definition (it is in the mod's
    /// <c>assets/player-states.json</c>, or was kept from code on an earlier run) the result is
    /// <see cref="PlayerAnimStateStatus.Live"/>; otherwise it is <see cref="PlayerAnimStateStatus.Pending"/>, "kept for the next
    /// start": the state is in the graph after a restart, on every side that runs the mod, and not before. A definition
    /// the service will not take is <see cref="PlayerAnimStateStatus.Refused"/> with its reason. Prefer
    /// <c>assets/player-states.json</c>: it is in the graph from the first start.</summary>
    /// <remarks>Call it from the mod's own code (<c>OnLoad</c>, a tick handler): that is how the defining mod is found.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> or one of its texts is null.</exception>
    /// <exception cref="ArgumentException">The name is empty, longer than 64 characters or has a <c>/</c>; the clip or
    /// <c>like</c> is empty; <see cref="PlayerAnimState.BlendIn"/> is outside 0 to 5 or <see cref="PlayerAnimState.Speed"/> is
    /// not above 0.</exception>
    /// <exception cref="KilnUnavailableException"><c>anim.player_states</c> is off: the service is missing, or the merge
    /// is off on this game build.</exception>
    /// <exception cref="InvalidOperationException">Not called from a mod's code.</exception>
    [AnyThread]
    [RequiresCapability("anim.player_states")]
    public static PlayerAnimDefineResult Define(PlayerAnimState state)
    {
        Validate(state);
        var t = T;
        if (t == null) throw new KilnUnavailableException(Capability, "the kiln.playeranim service is not loaded on this side");
        if (Graph == PlayerAnimGraphState.Off) throw new KilnUnavailableException(Capability, Capabilities.Why(Capability));
        var mod = ModContext.CurrentMod?.Info
            ?? throw new InvalidOperationException("PlayerAnimStates.Define must be called from a mod's code (OnLoad or a tick handler)");
        var guid = GuidOf(mod.Id, state.Name);
        var id = IdOf(guid);
        PlayerAnimDefineResult Result(PlayerAnimStateStatus status, string? reason) => new(mod.Id, state.Name, guid, id, status, reason);

        var json = DefinitionJson(mod.Id, mod.Directory, state);
        var (rc, report) = CallDefine(t, json);
        if (rc == PlayerAnimNative.Unavailable) return Result(PlayerAnimStateStatus.Refused, NoDefineInBuild);
        var (status, reason) = ReportEntry(report, state.Name);
        if (rc != PlayerAnimNative.Ok)
            return Result(PlayerAnimStateStatus.Refused, string.IsNullOrEmpty(reason) ? $"the service refused the definition (error {rc})" : reason);
        switch (status)
        {
            case "refused":
                return Result(PlayerAnimStateStatus.Refused, string.IsNullOrEmpty(reason) ? "refused by the service" : reason);
            case "unavailable":
                throw new KilnUnavailableException(Capability, string.IsNullOrEmpty(reason) ? "the merge is off on this game build" : reason);
            case "live":
                // Live only when the served graph has it: never claim a state the running game cannot play.
                if (Graph == PlayerAnimGraphState.Merged && Find(mod.Id, state.Name) is { Status: PlayerAnimStateStatus.Live })
                    return Result(PlayerAnimStateStatus.Live, null);
                return Result(PlayerAnimStateStatus.Pending, KeptForNextStart);
            default:
                return Result(PlayerAnimStateStatus.Pending, KeptForNextStart);
        }
    }

    internal static void Validate(PlayerAnimState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.Name, nameof(state) + ".Name");
        ArgumentNullException.ThrowIfNull(state.Clip, nameof(state) + ".Clip");
        ArgumentNullException.ThrowIfNull(state.Like, nameof(state) + ".Like");
        if (state.Name.Length is 0 or > 64 || state.Name.Contains('/'))
            throw new ArgumentException($"a player state's name is 1 to 64 characters without '/': \"{state.Name}\"", nameof(state));
        if (state.Clip.Length == 0) throw new ArgumentException($"player state {state.Name}: the clip is empty", nameof(state));
        if (state.Like.Length == 0) throw new ArgumentException($"player state {state.Name}: like is empty", nameof(state));
        if (state.BlendIn is { } b && !(b >= 0 && b <= 5))
            throw new ArgumentOutOfRangeException(nameof(state), b, $"player state {state.Name}: blendIn is seconds, 0 to 5");
        if (state.Speed is { } s && !(s > 0 && float.IsFinite(s)))
            throw new ArgumentOutOfRangeException(nameof(state), s, $"player state {state.Name}: speed must be above 0");
    }

    /// <summary>The table's define_json input: {"format": 1, "mod", "dir", "states": {name: {...}}}, the state as in the file.</summary>
    internal static byte[] DefinitionJson(string mod, string dir, PlayerAnimState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("format", 1);
            w.WriteString("mod", mod);
            w.WriteString("dir", dir);
            w.WriteStartObject("states");
            w.WriteStartObject(state.Name);
            w.WriteString("clip", state.Clip);
            w.WriteString("like", state.Like);
            if (state.BlendIn is { } b) w.WriteNumber("blendIn", b);
            if (state.Loop is { } l) w.WriteBoolean("loop", l);
            if (state.Speed is { } s) w.WriteNumber("speed", s);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static (int Rc, string Report) CallDefine(PlayerAnimNative.Table* t, byte[] json)
    {
        nuint capacity = 4096;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var report = new byte[(int)capacity];
            nuint len = 0;
            int rc;
            fixed (byte* pj = json)
            fixed (byte* pr = report)
                rc = t->DefineJson(new NativeStr { Ptr = pj, Len = (nuint)json.Length }, pr, capacity, &len);
            if (rc == PlayerAnimNative.BufferTooSmall && len > capacity && len < 16u << 20)
            {
                capacity = len;
                continue;
            }
            var n = (int)Math.Min(len, capacity);
            return (rc, Encoding.UTF8.GetString(report, 0, n));
        }
        return (PlayerAnimNative.BufferTooSmall, "");
    }

    /// <summary>The status and reason the service's report gives the state <paramref name="name"/>: under "states", an
    /// object keyed by name or an array of objects with "name"; a bare array of such objects; or a top-level
    /// "reason"/"error" for the whole call. Nulls when the report says nothing about it.</summary>
    internal static (string? Status, string? Reason) ReportEntry(string report, string name)
    {
        if (string.IsNullOrWhiteSpace(report)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(report);
            var root = doc.RootElement;
            JsonElement? entry = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("states", out var states)) entry = Pick(states, name);
            else if (root.ValueKind == JsonValueKind.Array) entry = Pick(root, name);
            if (entry is { ValueKind: JsonValueKind.Object } e)
                return (Text(e, "status")?.ToLowerInvariant(), Text(e, "reason"));
            if (root.ValueKind == JsonValueKind.Object) return (Text(root, "status")?.ToLowerInvariant(), Text(root, "reason") ?? Text(root, "error"));
        }
        catch (JsonException) { }
        return (null, null);

        static JsonElement? Pick(JsonElement list, string name)
        {
            if (list.ValueKind == JsonValueKind.Object) return list.TryGetProperty(name, out var v) ? v : null;
            if (list.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in list.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && Text(item, "name") == name) return item;
            return null;
        }

        static string? Text(JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
