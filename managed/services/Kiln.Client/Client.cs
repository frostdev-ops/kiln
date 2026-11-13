// SPDX-License-Identifier: MIT
// Kiln.Client: the game client a mod runs in (Forge: Dist.CLIENT / FMLEnvironment.dist, Minecraft.getInstance(),
// ClientTickEvent, Minecraft.execute). Native side: kiln.client (native/core/abi/services/kiln_client.h).
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>What runs in this process.</summary>
public enum ClientState
{
    /// <summary>No world: main menu or loading screen (a client), or a server before its world runs.</summary>
    Menu = 0,

    /// <summary>A dedicated server with its world running (never on a client).</summary>
    DedicatedServer = 1,

    /// <summary>This client hosts the world (single player or co-op host): the server world runs here too, so every
    /// server-side service works.</summary>
    Hosting = 2,

    /// <summary>This client joined another machine's world: only the client world runs here; gameplay services act on
    /// the remote server.</summary>
    Remote = 3,
}

/// <summary>
/// The game client: side, session state, the local player, and the client world's tick.
/// </summary>
/// <remarks>
/// <para>Threads. The server world (where Kiln's services and <see cref="ModContext.Tick"/> run) steps on the game's
/// server thread; on a client it exists only while hosting. The client world steps on the game's main thread while in
/// game (hosting or joined). <see cref="Tick"/> handlers and <see cref="Post"/> work run there. Do not call server-world
/// services from them; hop with <c>Context.Scheduler.Post</c>. In the other direction, use <see cref="Post"/>.</para>
/// <para>Each hosted or joined game is a new world: <see cref="ClientWorldSession"/> counts them. Do not keep world
/// state across sessions.</para>
/// </remarks>
public static unsafe class Client
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeState
    {
        public uint Size, Side, Session, Generation, LocalPlayer, Flags, ClientSession, ServerSession;
        public ulong ClientSteps, ServerSteps;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTick
    {
        public uint Size, WorldKind;
        public ulong Step;
        public uint Clock, ClockDelta;
        public long ClockPeriod;
        public double WallDeltaMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<NativeState*, int> State;
        public delegate* unmanaged[Cdecl]<ulong, int, delegate* unmanaged[Cdecl]<NativeTick*, void*, void>, void*, ulong*, int> SubscribeTick;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<void*, void>, void*, int> Post;
        public delegate* unmanaged[Cdecl]<byte> OnClientThread;
        public delegate* unmanaged[Cdecl]<nuint> ClientWorld;
    }

    private const uint SideClient = 1;
    private const uint FlagLocalPlayer = 1;
    private const int MaxFaults = 5;

    private static Table* _table;
    private static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.client", 1));

    private sealed class Handler(TickHandler fn, ModContext? mod)
    {
        public readonly TickHandler Fn = fn;
        public readonly ModContext? Mod = mod;
        public int Faults;
    }

    private static readonly object Lock = new();
    private static Handler[] _handlers = [];
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static readonly ConcurrentQueue<(Action Work, ModContext? Mod)> Queue = new();
    private static int _subscribed;  // 0 no, 1 yes, -1 unavailable
    private static readonly SynchronizationContext Sync = new ClientSynchronizationContext();

    /// <summary>True when the kiln.client service is there (any Kiln with hooks enabled).</summary>
    [AnyThread]
    public static bool Available => T != null;

    private static NativeState Read()
    {
        var s = new NativeState { Size = (uint)sizeof(NativeState) };
        if (T != null) T->State(&s);
        return s;
    }

    /// <summary>True in the game client (enshrouded.exe), false on a dedicated server.</summary>
    [AnyThread]
    public static bool IsClient => Read().Side == SideClient;

    /// <summary>True on a dedicated server (enshrouded_server.exe).</summary>
    [AnyThread]
    public static bool IsDedicatedServer => !IsClient;

    /// <summary>What runs right now. Any thread; the value can change at the next frame.</summary>
    [AnyThread]
    public static ClientState State => (ClientState)Read().Session;

    /// <summary>This client hosts the world (single player or co-op host).</summary>
    [AnyThread]
    public static bool IsHosting => State == ClientState.Hosting;

    /// <summary>This client joined a world hosted elsewhere.</summary>
    [AnyThread]
    public static bool IsRemote => State == ClientState.Remote;

    /// <summary>In game (hosting or joined).</summary>
    [AnyThread]
    public static bool InGame => State is ClientState.Hosting or ClientState.Remote;

    /// <summary>The local player's entity, <see cref="EntityId.None"/> in the menu, while loading, before the player
    /// spawned, or on a server. On a host the id is the same in the client and the server world (EMF: ids are shared
    /// by replication), so it works with server-side services there.</summary>
    [AnyThread]
    public static EntityId LocalPlayer => new(Read().LocalPlayer);

    /// <summary>Whether <see cref="LocalPlayer"/> can be known on this game build.</summary>
    [AnyThread]
    public static bool HasLocalPlayer => (Read().Flags & FlagLocalPlayer) != 0;

    /// <summary>Client worlds since the game started (one per hosted or joined game).</summary>
    [AnyThread]
    public static uint ClientWorldSession => Read().ClientSession;

    /// <summary>Server worlds since the game started (one per hosted game).</summary>
    [AnyThread]
    public static uint ServerWorldSession => Read().ServerSession;

    /// <summary>Client-world steps since the game started.</summary>
    [AnyThread]
    public static ulong ClientWorldSteps => Read().ClientSteps;

    /// <summary>True on the thread that steps the client world (the game's main thread).</summary>
    [AnyThread]
    public static bool IsClientThread => T != null && T->OnClientThread() != 0;

    /// <summary>
    /// One client-world step (about 60 a second while in game; nothing in the menu), on the game's main thread.
    /// <see cref="TickInfo.Clock"/> is the client world's own 60 Hz clock. A handler that throws five times in a row is
    /// removed. Subscriptions end when the subscribing mod unloads. Never raised on a dedicated server.
    /// </summary>
    public static event TickHandler? Tick
    {
        add
        {
            if (value is null) return;
            var mod = ModContext.CurrentMod;
            if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
            lock (Lock)
            {
                _handlers = [.. _handlers, new Handler(value, mod)];
                if (mod != null && Watched.Add(mod)) mod.Retiring += () => Forget(mod);
            }
            EnsureSubscribed();
        }
        remove
        {
            if (value is null) return;
            lock (Lock)
            {
                var i = Array.FindLastIndex(_handlers, h => h.Fn == value);
                if (i >= 0) _handlers = [.. _handlers[..i], .. _handlers[(i + 1)..]];
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the game's main thread before the client world's next step. Thread-safe.
    /// Work of a mod that unloads first is dropped; on a server (or in the menu, until a world runs) it waits.</summary>
    [AnyThread]
    public static void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Enqueue(work, work);
    }

    // A background thread has no CurrentMod: the mod comes from the delegate it handed over.
    private static void Enqueue(Action work, Delegate code)
    {
        Queue.Enqueue((work, ModContext.CurrentMod ?? ModContext.OwnerOf(code)));
        EnsureSubscribed();
    }

    /// <summary><c>await Client.NextTick()</c> continues on the main thread at the next client-world step.</summary>
    [AnyThread]
    public static NextTickAwaitable NextTick() => default;

    public readonly struct NextTickAwaitable
    {
        public Awaiter GetAwaiter() => default;

        public readonly struct Awaiter : ICriticalNotifyCompletion
        {
            public bool IsCompleted => false;
            public void OnCompleted(Action continuation) => Post(continuation);
            public void UnsafeOnCompleted(Action continuation) => Post(continuation);
            public void GetResult() { }
        }
    }

    private static void Forget(ModContext mod)
    {
        lock (Lock)
        {
            _handlers = Array.FindAll(_handlers, h => !ReferenceEquals(h.Mod, mod));
            Watched.Remove(mod);
        }
    }

    private static void EnsureSubscribed()
    {
        if (Volatile.Read(ref _subscribed) != 0 || T == null) return;
        lock (Lock)
        {
            if (_subscribed != 0) return;
            ulong handle = 0;
            var rc = T->SubscribeTick(0, 0 /* KILN_TICK_PRE */, &OnClientPre, null, &handle);
            _subscribed = rc == 0 ? 1 : -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClientPre(NativeTick* t, void* user)
    {
        var outer = ModContext.CurrentMod;
        var outerSync = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(Sync);
            for (var n = Queue.Count; n > 0 && Queue.TryDequeue(out var item); n--)  // work queued now waits a step
            {
                if (item.Mod is { Alive: false }) continue;
                ModContext.CurrentMod = item.Mod;
                try
                {
                    item.Work();
                }
                catch (Exception e)
                {
                    Report(item.Mod, "Client.Post work threw", e);
                }
            }
            var handlers = Volatile.Read(ref _handlers);
            if (handlers.Length == 0) return;
            var tick = HostAccess.Tick(t->Step, t->Clock, t->ClockDelta, t->ClockPeriod, t->WallDeltaMs);
            foreach (var h in handlers)
            {
                if (h.Mod is { Alive: false }) continue;
                ModContext.CurrentMod = h.Mod;
                try
                {
                    h.Fn(in tick);
                    h.Faults = 0;
                }
                catch (Exception e)
                {
                    Report(h.Mod, $"Client.Tick handler {h.Fn.Method.DeclaringType?.Name}.{h.Fn.Method.Name} threw", e);
                    if (++h.Faults >= MaxFaults)
                    {
                        lock (Lock) _handlers = Array.FindAll(_handlers, x => !ReferenceEquals(x, h));
                        Report(h.Mod, $"Client.Tick handler {h.Fn.Method.Name} removed after {MaxFaults} faults in a row", null);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Report(null, "Client.Tick dispatch failed", e);
        }
        finally
        {
            ModContext.CurrentMod = outer;
            SynchronizationContext.SetSynchronizationContext(outerSync);
        }
    }

    private static void Report(ModContext? mod, string what, Exception? e)
    {
        try
        {
            if (mod != null)
            {
                if (e != null) mod.Log.Error(e, what);
                else mod.Log.Warn(what);
            }
            else
            {
                Backend.Current.Log(0, e != null ? LogLevel.Error : LogLevel.Warn, e != null ? $"{what}: {e}" : what);
            }
        }
        catch
        {
            // logging must never unwind into the game's main thread
        }
    }

    /// <summary>While client handlers run: <c>await</c> comes back to the main thread at the next client step.</summary>
    private sealed class ClientSynchronizationContext : SynchronizationContext
    {
        // After an await the continuation is posted from wherever the awaited work ended; it is the state, d only unwraps it.
        public override void Post(SendOrPostCallback d, object? state) => Enqueue(() => d(state), state as Delegate ?? d);

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (!IsClientThread) throw new InvalidOperationException("Send is only allowed on the client thread");
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
