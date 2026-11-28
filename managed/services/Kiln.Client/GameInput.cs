// SPDX-License-Identifier: MIT
// Kiln.GameInput: the local player's game actions where the game client has resolved its own input (kiln.client.game_input,
// native/core/abi/services/kiln_client_game_input.h; docs/kiln/services/client.md "Game input"). Since Kiln
// 0.2.0-dev.31.30 (mod API 0.2.28). The public surface is the one settled for the Combat And Attack Tweaks author (the
// preview SDK 0.2.0-dev.31.30-preview.1): GameAction, GameInputHandler, GameInputSample and GameInput, member for member.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>The game's own digital actions, after its rebinding and controller mapping, decoded from the local player's
/// input record. Append only. Since Kiln 0.2.0-dev.31.30 (mod API 0.2.28).</summary>
/// <remarks>Each action is a set of the game's <c>keen::ecs::PlayerInputType</c> bits in the record's digital input
/// (docs/re/game-input.md): the bits that mean its input is down, and the bits the game derives from it (tap, hold,
/// release), which <see cref="GameInputSample.Press"/> and <see cref="GameInputSample.Release"/> clear.</remarks>
public enum GameAction
{
    /// <summary>Light attack / fire (left mouse by default): the game's <c>MainhandAction</c> and
    /// <c>RangedCombatShoot</c> (with its tap); a long hold makes the game add <c>MainhandAction_Hold</c>, its heavy
    /// attack, which a press for one sample does not.</summary>
    PrimaryAttack,
    /// <summary>Heavy attack / aim (right mouse by default, as the game maps it): the game's <c>RangedCombatArm</c>, the
    /// aim of bows and staffs.</summary>
    SecondaryAttack,
    /// <summary>Block (the game's <c>Block</c>).</summary>
    Block,
    /// <summary>Dodge (the game's <c>Evade</c>).</summary>
    Dodge,
    /// <summary>Jump (the game's <c>Jump</c>).</summary>
    Jump,
    /// <summary>Interact (the game's <c>ContextualAction</c>, with its tap and hold).</summary>
    Interact,
}

/// <summary>Called for each input sample of the local player (<see cref="GameInput.OnSample"/>), on the game's main
/// thread inside its input writer. Since Kiln 0.2.0-dev.31.30 (mod API 0.2.28).</summary>
/// <param name="sample">The sample: read it, and <see cref="GameInputSample.Press"/> or
/// <see cref="GameInputSample.Release"/> actions for it. Valid only during the call.</param>
public delegate void GameInputHandler(ref GameInputSample sample);

/// <summary>One input sample of the local player, in the game's input writer, after the game resolved it. Since Kiln
/// 0.2.0-dev.31.30 (mod API 0.2.28).</summary>
/// <remarks>A sample lives for one <see cref="GameInputHandler"/> call: <see cref="Press"/> and <see cref="Release"/>
/// outside it throw <see cref="InvalidOperationException"/>, and a default instance reads as nothing held.</remarks>
public unsafe ref struct GameInputSample
{
    private readonly GameInputNative.Sample* _sample;

    internal GameInputSample(GameInputNative.Sample* sample) => _sample = sample;

    /// <summary>The local player.</summary>
    public EntityId Player => _sample != null ? new EntityId(_sample->Player) : EntityId.None;

    /// <summary>The <see cref="ActionKind"/> of the held item (Melee, Bow, Tool), or null.</summary>
    /// <remarks>From Kiln's item catalog: the equipment slot MeleeWeapon is Melee and Tool is Tool; a RangeWeapon is Bow
    /// when the game's ranged weapon state holds it as a bow (or, before the game has it there, when its name says Bow).
    /// Staffs, wands, spells and everything else are null.</remarks>
    public ActionKind? HeldKind =>
        _sample != null && _sample->HeldKind is >= (int)ActionKind.Melee and <= (int)ActionKind.Tool ? (ActionKind)_sample->HeldKind : null;

    /// <summary>A bow draw has reached its effective charge duration (ActionSpeed included).</summary>
    /// <remarks>The game's own test: its ranged weapon state is AimAndCharge for the held bow, and the client world's
    /// time since the draw started has reached the charge duration the game took at the draw's start (with skills and
    /// <c>ActionSpeed</c>).</remarks>
    public bool BowFullyDrawn => _sample != null && (_sample->Flags & GameInputNative.FlagBowDrawn) != 0;

    /// <summary>The action as the game sampled it (before handlers).</summary>
    /// <param name="action">The action; an unknown value reads as not held.</param>
    public bool IsHeld(GameAction action) => _sample != null && (_sample->Held & GameInputNative.Bit(action)) != 0;

    /// <summary>Sets <paramref name="action"/> for this sample.</summary>
    /// <remarks>The game sees the action's input down for this sample, without the bits it derives from a hold (so a
    /// press for one sample is a tap, never a heavy attack). The later of <see cref="Press"/> and <see cref="Release"/>
    /// for the same action wins, also across handlers (they run in the order they subscribed).</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is not a <see cref="GameAction"/>.</exception>
    /// <exception cref="InvalidOperationException">Outside the sample's own handler call.</exception>
    public void Press(GameAction action)
    {
        var bit = Require(action);
        _sample->Press |= bit;
        _sample->Release &= ~bit;
    }

    /// <summary>Clears <paramref name="action"/> for this sample.</summary>
    /// <remarks>The game sees the action's input up for this sample, and none of the bits it derives from it.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is not a <see cref="GameAction"/>.</exception>
    /// <exception cref="InvalidOperationException">Outside the sample's own handler call.</exception>
    public void Release(GameAction action)
    {
        var bit = Require(action);
        _sample->Release |= bit;
        _sample->Press &= ~bit;
    }

    private uint Require(GameAction action)
    {
        var bit = GameInputNative.Bit(action);
        if (bit == 0) throw new ArgumentOutOfRangeException(nameof(action), action, "not a GameAction");
        if (_sample == null || GameInput.Live != (nint)_sample)
            throw new InvalidOperationException("the sample is not live: Press and Release work only inside its GameInput.OnSample handler");
        return bit;
    }
}

/// <summary>Reads and changes the local player's game actions where the game has resolved its own input (rebinding,
/// controllers, menus and focus included). Client only. Handlers run only on the local gameplay path: never while the
/// game's menus, inventory, map or chat or Kiln's UI have input, and never unfocused. Kiln always clears synthetic presses
/// that the outbound input message would resend after a skipped writer step. Since Kiln 0.2.0-dev.31.30 (mod API
/// 0.2.28).</summary>
/// <remarks>
/// <para>Where: the game client's local input writer, once per client world step (about 60 a second) on the game's main
/// thread, after the game applied its key bindings, controller mapping and menu handling; Kiln reads there and writes
/// there, before the simulation and the input message use the record. Nothing polls the keyboard.</para>
/// <para>The gates (Kiln's own input state, read at each client world step): a world in play, the game's in-game HUD and
/// no other game screen, neither Kiln's menu nor a Kiln window capturing input, and the game window in the foreground.
/// Reading (<see cref="IsHeld"/>, <see cref="WasPressed"/>) is not gated: it is the game's own answer.</para>
/// <para>Multiplayer: the sample is the local player's input, sent to the host or server as the game sends its own; the
/// host or server still decides what happens.</para>
/// <para>Capability row <c>client.game_input</c>. Nothing is hooked until the first use of any member.</para>
/// </remarks>
[ClientOnly]
public static unsafe class GameInput
{
    internal const string Capability = "client.game_input";
    private const int MaxFaults = 5;

    internal static readonly ServiceCapRows Rows = new(GameInputNative.Service, ReportRows);

    static GameInput() => Rows.Ensure();

    // The native sample of the handler call running now on the main thread (0: none): what makes a sample live.
    [ThreadStatic] internal static nint Live;

    /// <summary>A client with a local player, and the game's input sites resolved here (capability <c>client.game_input</c>).</summary>
    public static bool Available =>
        GameInputNative.Members != null && Capabilities.Is(Capability) && Client.IsClient && Client.HasLocalPlayer;

    /// <summary>Whether <paramref name="action"/> was held at the last sample.</summary>
    /// <remarks>The game's own sample of the local player (before any handler), fresh within 250 ms in the live client
    /// world; false otherwise, and false where <see cref="Available"/> is false. Any thread; never throws.</remarks>
    public static bool IsHeld(GameAction action) => TryLast(out var s) && (s.Held & GameInputNative.Bit(action)) != 0;

    /// <summary>Whether <paramref name="action"/> went down at the last sample.</summary>
    /// <remarks>Held at the last sample and not at the one before it in the same client world. Any thread; never
    /// throws.</remarks>
    public static bool WasPressed(GameAction action) => TryLast(out var s) && (s.Pressed & GameInputNative.Bit(action)) != 0;

    /// <summary>Calls <paramref name="handler"/> for each local input sample until the returned handle is disposed.</summary>
    /// <remarks>On the game's main thread, only while the gates are open (see the class remarks). Handlers run in the
    /// order they subscribed. A handler that throws five times in a row is removed. The handle also ends when the mod
    /// unloads. Any thread.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
    /// <exception cref="InvalidOperationException">On a dedicated server, or where GameInput is unavailable.</exception>
    public static IDisposable OnSample(GameInputHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var (t, mod) = Require(handler);
        var reg = new Registration(mod, handler, null, null, 0);
        reg.Start(t->Subscribe(mod.Owner, &OnSampleNative, (void*)reg.Handle, &reg.NativeHandle), "OnSample");
        return reg;
    }

    /// <summary>Presses and releases <paramref name="action"/> every <paramref name="interval"/> (at least 50 ms) while
    /// <paramref name="whileHeld"/> is true (default: while the action itself is held). Stops when the handle is disposed,
    /// the world ends or the mod unloads.</summary>
    /// <remarks>A press for one sample, then releases until <paramref name="interval"/> after it, then the next press:
    /// with the default condition a held button becomes a train of taps. It writes nothing while the condition is false
    /// or the gates are closed, and starts again from a press when the condition turns true. <paramref name="whileHeld"/>
    /// runs on the game's main thread at each sample. Any thread.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is not a <see cref="GameAction"/>.</exception>
    /// <exception cref="InvalidOperationException">On a dedicated server, or where GameInput is unavailable.</exception>
    public static IDisposable Repeat(GameAction action, TimeSpan interval, Func<bool>? whileHeld = null)
    {
        var bit = GameInputNative.Bit(action);
        if (bit == 0) throw new ArgumentOutOfRangeException(nameof(action), action, "not a GameAction");
        var (t, mod) = Require(whileHeld);
        var ms = interval.TotalMilliseconds;
        var intervalMs = ms < GameInputNative.MinRepeatMs ? GameInputNative.MinRepeatMs : ms >= uint.MaxValue ? uint.MaxValue : (uint)ms;
        var reg = new Registration(mod, null, whileHeld, action, bit);
        reg.Start(t->Repeat(mod.Owner, (uint)action, intervalMs, &OnConditionNative, (void*)reg.Handle, &reg.NativeHandle), "Repeat");
        return reg;
    }

    private static bool TryLast(out GameInputNative.Sample sample)
    {
        sample = default;
        var t = GameInputNative.Members;
        if (t == null || !Capabilities.Is(Capability)) return false;
        var s = new GameInputNative.Sample { Size = (uint)sizeof(GameInputNative.Sample) };
        uint reason = 0;
        if (t->Last(&s, &reason) != GameInputNative.Ok || reason != GameInputNative.ReasonNone || s.Sequence == 0) return false;
        sample = s;
        return true;
    }

    private static (GameInputNative.Table* Table, ModContext Mod) Require(Delegate? code)
    {
        Rows.Ensure();
        if (!Client.IsClient) throw new InvalidOperationException("GameInput is client only: this is a dedicated server");
        var t = GameInputNative.Members;
        if (t == null)
            throw new InvalidOperationException(GameInputNative.Header != null || Services.Get<ServiceHeader>("kiln.client", 1) != null
                ? "kiln.client.game_input is not here (a Kiln before 0.2.0-dev.31.30)"
                : "kiln.client.game_input is not here");
        if (!Capabilities.Is(Capability))
        {
            var row = Capabilities.Get(Capability);
            throw new InvalidOperationException($"GameInput is unavailable here ({Capability}: {row.ReasonText})");
        }
        var mod = ModContext.CurrentMod ?? (code != null ? ModContext.OwnerOf(code) : null)
            ?? throw new InvalidOperationException("GameInput subscriptions belong to a mod: call from mod code");
        if (!mod.Alive) throw new InvalidOperationException($"mod '{mod.Info.Id}' is unloading");
        return (t, mod);
    }

    // One OnSample or Repeat: its native subscription, the GCHandle native calls back with, and the mod it ends with.
    private sealed class Registration : IDisposable
    {
        public readonly ModContext Mod;
        public readonly GameInputHandler? Handler;
        public readonly Func<bool>? WhileHeld;
        public readonly GameAction? Action;
        public readonly uint Bit;
        public readonly nint Handle;
        public ulong NativeHandle;
        public uint Session;  // Repeat: the client world it belongs to (0 until its first sample)
        public int Faults;
        private int _disposed;

        public Registration(ModContext mod, GameInputHandler? handler, Func<bool>? whileHeld, GameAction? action, uint bit)
        {
            Mod = mod;
            Handler = handler;
            WhileHeld = whileHeld;
            Action = action;
            Bit = bit;
            Handle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        }

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public void Start(int rc, string api)
        {
            if (rc == GameInputNative.Ok && NativeHandle != 0)
            {
                lock (Gate)
                {
                    if (!Watched.Contains(Mod))
                    {
                        Watched.Add(Mod);
                        Mod.Retiring += () => Forget(Mod);
                    }
                    LiveSet.Add(this);
                }
                if (!Mod.Alive) Dispose();  // retired meanwhile: its Retiring may have run before this was in the list
                return;
            }
            GCHandle.FromIntPtr(Handle).Free();
            Interlocked.Exchange(ref _disposed, 1);
            throw rc switch
            {
                GameInputNative.Revoked => new InvalidOperationException($"mod '{Mod.Info.Id}' is unloading"),
                GameInputNative.BufferTooSmall => new InvalidOperationException(
                    $"GameInput.{api}: {GameInputNative.MaxSubscriptions} subscriptions (OnSample and Repeat together) already"),
                GameInputNative.Unavailable => new InvalidOperationException(
                    $"GameInput is unavailable here (kiln.client.game_input answered unavailable: {Capabilities.Get(Capability).ReasonText})"),
                _ => new InvalidOperationException($"kiln.client.game_input's {api} answered {rc}"),
            };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var t = GameInputNative.Members;
            if (t != null && NativeHandle != 0) t->Unsubscribe(NativeHandle);  // returns once a call in flight has ended
            lock (Gate) LiveSet.Remove(this);
            GCHandle.FromIntPtr(Handle).Free();
        }
    }

    private static readonly object Gate = new();
    private static readonly HashSet<Registration> LiveSet = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);

    private static void Forget(ModContext mod)
    {
        Registration[] mine;
        lock (Gate)
        {
            mine = LiveSet.Where(r => ReferenceEquals(r.Mod, mod)).ToArray();
            Watched.Remove(mod);
        }
        foreach (var r in mine) r.Dispose();
    }

    private static Registration? Resolve(void* user)
    {
        try
        {
            return user != null ? GCHandle.FromIntPtr((nint)user).Target as Registration : null;
        }
        catch (InvalidOperationException)
        {
            return null;  // freed meanwhile
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSampleNative(GameInputNative.Sample* s, void* user)
    {
        var reg = Resolve(user);
        if (reg?.Handler is not { } handler || reg.Disposed || !reg.Mod.Alive) return;
        var outerMod = ModContext.CurrentMod;
        var outerLive = Live;
        try
        {
            ModContext.CurrentMod = reg.Mod;
            Live = (nint)s;
            var sample = new GameInputSample(s);
            handler(ref sample);
            reg.Faults = 0;
        }
        catch (Exception e)
        {
            Fault(reg, $"GameInput.OnSample handler {handler.Method.DeclaringType?.Name}.{handler.Method.Name} threw", e);
        }
        finally
        {
            Live = outerLive;
            ModContext.CurrentMod = outerMod;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnConditionNative(GameInputNative.Sample* s, void* user)
    {
        var reg = Resolve(user);
        if (reg == null || reg.Disposed || !reg.Mod.Alive) return 0;
        // the world ends: a repeat belongs to the client world of its first sample
        if (reg.Session == 0) reg.Session = s->ClientSession;
        else if (reg.Session != s->ClientSession)
        {
            reg.Dispose();
            return 0;
        }
        if (reg.WhileHeld is not { } cond) return (s->Held & reg.Bit) != 0 ? (byte)1 : (byte)0;
        var outerMod = ModContext.CurrentMod;
        try
        {
            ModContext.CurrentMod = reg.Mod;
            var on = cond();
            reg.Faults = 0;
            return on ? (byte)1 : (byte)0;
        }
        catch (Exception e)
        {
            Fault(reg, $"GameInput.Repeat({reg.Action}) condition {cond.Method.DeclaringType?.Name}.{cond.Method.Name} threw", e);
            return 0;
        }
        finally
        {
            ModContext.CurrentMod = outerMod;
        }
    }

    private static void Fault(Registration reg, string what, Exception e)
    {
        try
        {
            reg.Mod.Log.Error(e, what);
            if (++reg.Faults < MaxFaults) return;
            reg.Mod.Log.Warn($"{what}: removed after {MaxFaults} faults in a row");
            reg.Dispose();  // from inside the call: kiln.client.game_input marks it and returns at once
        }
        catch
        {
            // nothing unwinds into the game's main thread
        }
    }

    private static void ReportRows()
    {
        if (GameInputNative.Members != null || Capabilities.TryGetRow(Capability, out _)) return;  // the core's row stands
        var (reason, text) = ServiceCaps.Cause(CapabilityReason.ServiceMissing,
            "kiln.client.game_input is not here (a Kiln before 0.2.0-dev.31.30)", GameInputNative.Service);
        ServiceCaps.Report(Capability, CapabilityState.Unavailable, reason, text);
    }
}

/// <summary>kiln.client.game_input v1 (native/core/abi/services/kiln_client_game_input.h). Byte-exact with the header:
/// Kiln.Api.Tests' GameInputTests check every size and offset.</summary>
internal static unsafe class GameInputNative
{
    public const string Service = "kiln.client.game_input";
    public const int Ok = 0, Unavailable = 3, BufferTooSmall = 5, Revoked = 6, NotFound = 2;
    public const uint ReasonNone = 0, ReasonNoSample = 1, ReasonStale = 2;
    public const uint FlagBowDrawn = 1;
    public const uint MinRepeatMs = 50;
    public const int MaxSubscriptions = 64;
    public const int ActionCount = 6;

    /// <summary>The bit of an action in the masks; 0 for a value outside <see cref="GameAction"/>.</summary>
    public static uint Bit(GameAction action) => (uint)action < ActionCount ? 1u << (int)action : 0u;

    /// <summary>KilnGameInputSample (72 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Sample
    {
        public uint Size, Player, ClientSession, Step, Held, Pressed, Press, Release;
        public int HeldKind;
        public uint HeldItem, Flags, Gate;
        public ulong Digital, Sequence, AgeNs;
    }

    /// <summary>KilnClientGameInputApi (40 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Sample*, uint*, int> Last;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<Sample*, void*, void>, void*, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, delegate* unmanaged[Cdecl]<Sample*, void*, byte>, void*, ulong*, int> Repeat;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
    }

    private sealed record Found(IKilnBackend Backend, nint Table);
    private static volatile Found? _found;

    /// <summary>kiln.client.game_input's table (any size), or null.</summary>
    public static ServiceHeader* Header
    {
        get
        {
            var backend = Backend.Current;
            if (_found is { } found && ReferenceEquals(found.Backend, backend)) return (ServiceHeader*)found.Table;
            var h = Services.Get<ServiceHeader>(Service, 1);
            if (h != null) _found = new Found(backend, (nint)h);
            return h;
        }
    }

    /// <summary>The table when it has every v1 member, else null.</summary>
    public static Table* Members
    {
        get
        {
            var h = Header;
            return h != null && h->Size >= (uint)sizeof(Table) ? (Table*)h : null;
        }
    }
}
