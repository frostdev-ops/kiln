// SPDX-License-Identifier: MIT
// SteamGuardRelay over Kiln.Network's real frame paths, with no game: one engine plays both roles through a loopback
// transport (what the server role sends to the client peer arrives at the client role as from the server, and the
// other way round). The handshake is the real one: the client role's hello lists the channels it has, and the server
// role decides HasChannel from that list. The guard is a stand-in (SteamGuardRelay.LatchLocal), so the native service
// is not needed.
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace Kiln.Network.Tests;

[CollectionDefinition(nameof(SteamGuardRelayTests), DisableParallelization = true)]
public sealed class SteamGuardRelayCollection;

[Collection(nameof(SteamGuardRelayTests))]
public sealed class SteamGuardRelayTests
{
    [Fact]
    public void Relay_api_requires_experimental_opt_in_and_declares_its_side_and_thread()
    {
        foreach (var type in new[] { typeof(GuardOutcome), typeof(SteamGuardRelay) })
        {
            var marker = type.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.ExperimentalAttribute");
            Assert.NotNull(marker);
            Assert.Equal("KILNX002", marker.ConstructorArguments[0].Value);
        }
        Assert.True(Attribute.IsDefined(typeof(SteamGuardRelay), typeof(ServerOnlyAttribute)));
        foreach (var method in new[] { "LatchFor", "LatchForAll" })
            Assert.True(Attribute.IsDefined(typeof(SteamGuardRelay).GetMethod(method)!, typeof(TickThreadOnlyAttribute)));
        Assert.True(Attribute.IsDefined(typeof(SteamGuardRelay).GetMethod("Note")!, typeof(AnyThreadAttribute)));
    }

    private const uint ClientPeer = 0x81, VanillaPeer = 0x82, NoGuardPeer = 0x83;
    private static readonly EntityId Player = new(0x81), VanillaPlayer = new(0x82), NoGuardPlayer = new(0x83), LocalPlayer = new(0x80);

    private sealed class Loop : INetTransport
    {
        public readonly ConcurrentQueue<(uint From, byte[] Frame)> Inbox = new();
        public readonly Dictionary<uint, uint> Players = new();  // peer -> entity
        public volatile bool Deaf;                               // the client role receives nothing (never answers)

        public int Send(uint peer, byte[] frame)
        {
            if (peer == NetNative.PeerServer) Inbox.Enqueue((ClientPeer, frame));             // client role -> server role
            else if (peer == ClientPeer && !Deaf) Inbox.Enqueue((NetNative.PeerServer, frame));  // server role -> client role
            return NetNative.Ok;                                                              // other peers: sent, unanswered
        }

        public NetNative.Status Status() => new() { Roles = NetNative.RoleServer | NetNative.RoleClient, MaxFrame = 1200 };

        public NetNative.PeerInfo? Info(uint peer) =>
            Players.TryGetValue(peer, out var e) ? new NetNative.PeerInfo { Peer = peer, Flags = NetNative.PeerHasPlayer, Entity = e } : null;

        public int Kick(uint peer, uint reason) => NetNative.Ok;

        public void Pump()
        {
            while (Inbox.TryDequeue(out var f)) NetEngine.OnFrame(f.From, f.Frame);
        }
    }

    private static int _latchCalls;
    private static string? _lastReason;

    private static Loop Connect()
    {
        var loop = new Loop();
        NetNative.TestTransport = loop;
        _latchCalls = 0;
        _lastReason = null;
        SteamGuardRelay.IsLocalPlayer = p => p == LocalPlayer;
        SteamGuardRelay.LatchLocal = r => { Interlocked.Increment(ref _latchCalls); _lastReason = r; return true; };
        SteamGuardRelay.ReplyTimeout = TimeSpan.FromSeconds(2);

        // Kiln.Network's start on the client (NetworkStartup): the channel is registered before any connection and before
        // any latch call.
        SteamGuardRelay.Start();

        // The server role sees the client connect; the client role sees the server and says hello.
        loop.Players[ClientPeer] = Player.Value;
        NetEngine.OnPeer(ClientPeer, true);
        NetEngine.OnPeer(NetNative.PeerServer, true);
        NetEngine.ServerLink!.NextHelloAt = DateTime.UtcNow.AddSeconds(-1);
        var nudge = Wire.Begin(Wire.Type.Channels, 16);  // any frame from the server runs the client role's timers
        Wire.WriteChannels(nudge, []);
        NetEngine.OnFrame(NetNative.PeerServer, nudge.WrittenSpan);
        loop.Pump();  // hello -> server role -> welcome -> client role
        return loop;
    }

    private static async Task<GuardOutcome> Run(Loop loop, EntityId player, string reason = "test")
    {
        var t = SteamGuardRelay.LatchFor(player, reason);
        loop.Pump();  // request -> client role (stand-in guard) -> reply -> server role
        return await t.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Joined_client_with_the_channel_is_latched_and_the_cases_answer_as_specified()
    {
        var loop = Connect();
        try
        {
            // The handshake decided HasChannel from the client's hello: the channel was registered at start.
            var peer = NetEngine.PeerOfPlayer(Player);
            Assert.NotNull(peer);
            Assert.Equal(NetPeerState.Ready, peer!.State);
            Assert.True(peer.HasChannel(SteamGuardRelay.ChannelName));
            Assert.Equal(NetPeerState.Ready, NetEngine.ServerLink!.State);
            Assert.True(NetEngine.ServerLink.HasChannel(SteamGuardRelay.ChannelName));
            Assert.Equal(0, _latchCalls);  // no latch call was made yet

            // Protected: the request reaches the client role, its handler runs the guard, the server gets the answer.
            Assert.Equal(GuardOutcome.Protected, await Run(loop, Player, "admin level"));
            Assert.Equal(1, _latchCalls);
            Assert.Equal("admin level", _lastReason);

            // The client answers "not protecting".
            SteamGuardRelay.LatchLocal = _ => { Interlocked.Increment(ref _latchCalls); return false; };
            Assert.Equal(GuardOutcome.UnprotectedFailed, await Run(loop, Player));
            Assert.Equal(2, _latchCalls);

            // The client's guard throws: not protecting either.
            SteamGuardRelay.LatchLocal = _ => throw new InvalidOperationException("guard faulted");
            Assert.Equal(GuardOutcome.UnprotectedFailed, await Run(loop, Player));

            // No answer within the timeout.
            SteamGuardRelay.LatchLocal = _ => { Interlocked.Increment(ref _latchCalls); return true; };
            SteamGuardRelay.ReplyTimeout = TimeSpan.FromMilliseconds(300);
            loop.Deaf = true;
            var sw = Stopwatch.StartNew();
            Assert.Equal(GuardOutcome.UnprotectedFailed, await Run(loop, Player));
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(250), $"returned after {sw.Elapsed}");
            Assert.Equal(2, _latchCalls);  // the client never saw the request
            loop.Deaf = false;

            // A client with no Kiln: it never said hello.
            loop.Players[VanillaPeer] = VanillaPlayer.Value;
            NetEngine.OnPeer(VanillaPeer, true);
            Assert.Equal(GuardOutcome.UnprotectedVanilla, await Run(loop, VanillaPlayer));

            // A Kiln client without the channel (what every client looked like before the channel was registered at
            // start): its hello lists no kiln.steamguard.
            loop.Players[NoGuardPeer] = NoGuardPlayer.Value;
            NetEngine.OnPeer(NoGuardPeer, true);
            var hello = new Wire.Hello(Wire.Protocol, "0.0.0", 1, [], [], new Dictionary<string, string>());
            NetEngine.OnFrame(NoGuardPeer, Wire.EncodeHello(hello));
            var noGuard = NetEngine.PeerOfPlayer(NoGuardPlayer);
            Assert.NotNull(noGuard);
            Assert.Equal(NetPeerState.Ready, noGuard!.State);
            Assert.False(noGuard.HasChannel(SteamGuardRelay.ChannelName));
            Assert.Equal(GuardOutcome.UnprotectedVanilla, await Run(loop, NoGuardPlayer));

            // The player is this process's own: latched here, nothing sent.
            var before = _latchCalls;
            Assert.Equal(GuardOutcome.Protected, await Run(loop, LocalPlayer));
            Assert.Equal(before + 1, _latchCalls);

            // A player who leaves while asked: answered at once, not after the timeout.
            SteamGuardRelay.ReplyTimeout = TimeSpan.FromSeconds(10);
            loop.Deaf = true;
            sw.Restart();
            var pending = SteamGuardRelay.LatchFor(Player, "leaving");
            loop.Pump();
            NetEngine.OnPeer(ClientPeer, false);
            Assert.Equal(GuardOutcome.UnprotectedFailed, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"returned after {sw.Elapsed}");

            // Not in the world (any more).
            Assert.Equal(GuardOutcome.UnprotectedFailed, await Run(loop, Player));
        }
        finally
        {
            NetEngine.OnPeer(NetNative.PeerServer, false);
            NetEngine.OnPeer(VanillaPeer, false);
            NetEngine.OnPeer(NoGuardPeer, false);
            NetNative.TestTransport = null;
        }
    }

    // ---- the admin commands' rule: after a guard request the game writes run on the tick thread ----

    // A real Kiln scheduler drained on a dedicated "tick" thread the way the host runs a mod's Pre (Kiln.Host Mods.Pre:
    // the mod's synchronization context installed, then its scheduled work run in order). Work given to Start runs on
    // that thread with no synchronization context: the case the hop has to cover.
    private sealed class TickLoop : IDisposable
    {
        private readonly ModContext _mod;
        private readonly ConcurrentQueue<Action> _start = new();
        private volatile bool _stop;
        public readonly Thread Thread;

        public TickLoop(ModContext mod)
        {
            _mod = mod;
            Thread = new Thread(Run) { IsBackground = true, Name = "test tick" };
            Thread.Start();
        }

        public void Start(Action a) => _start.Enqueue(a);

        private void Run()
        {
            while (!_stop)
            {
                SynchronizationContext.SetSynchronizationContext(null);
                while (_start.TryDequeue(out var a)) a();
                SynchronizationContext.SetSynchronizationContext(_mod.Scheduler.SynchronizationContext);
                while (true)
                {
                    Action? next;
                    lock (_mod.PendingLock)
                        if (!_mod.Pending.TryDequeue(out next)) break;
                    next();
                }
                SynchronizationContext.SetSynchronizationContext(null);
                Thread.Sleep(2);
            }
        }

        public void Dispose()
        {
            _stop = true;
            Thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static readonly ConcurrentDictionary<string, string> Seen = new();
    private static string Where() => $"{Thread.CurrentThread.Name ?? "pool"} #{Environment.CurrentManagedThreadId}";

    // The shape of every patched admin command: ask for the guard, hop, write, reply.
    private static async Task Command(EntityId player, Thread tick, ConcurrentDictionary<string, bool> onTick)
    {
        var guard = await SteamGuardRelay.LatchFor(player, "admin level");
        await Kiln.Admin.Ops.TickThread();  // mods/Kiln.Admin/OpsTick.cs, linked: the line under test
        onTick["write"] = Thread.CurrentThread == tick;  // stands for Progression.SetLevel / Knowledge.Grant
        Seen["write"] = Where();
        var written = new TaskCompletionSource();
        Kiln.Admin.Ops.TickScheduler!.Post(() => written.SetResult());  // the write's task completes on the tick thread
        await written.Task;
        onTick["reply"] = Thread.CurrentThread == tick;  // stands for c.Reply / the bridge's result
        Seen["reply"] = Where();
        onTick["protected"] = guard == GuardOutcome.Protected;
    }

    private static Dictionary<string, bool> RunCommand(TickLoop tick, Action whileWaiting)
    {
        var onTick = new ConcurrentDictionary<string, bool>();
        Task? task = null;
        tick.Start(() => Volatile.Write(ref task, Command(Player, tick.Thread, onTick)));
        var until = DateTime.UtcNow.AddSeconds(10);
        // The command has returned its task: it waits for the guard's answer. Only now is the answer let through, so it
        // completes the relay's wait from another thread (an answer that came first would finish it on the tick thread).
        while (Volatile.Read(ref task) is null && DateTime.UtcNow < until) Thread.Sleep(1);
        while (!(Volatile.Read(ref task)?.IsCompleted ?? false) && DateTime.UtcNow < until)
        {
            whileWaiting();
            Thread.Sleep(5);
        }
        Assert.True(task?.IsCompletedSuccessfully, $"the command did not finish: {task?.Exception?.GetBaseException().Message ?? "timeout"}");
        return new Dictionary<string, bool>(onTick);
    }

    private static void WithTick(Action<Loop, TickLoop> body)
    {
        var loop = Connect();
        var mod = HostAccess.CreateContext(new ModInfo("kiln-admin-test", "kiln-admin-test", "1.0.0", Path.GetTempPath()), 991,
            Path.GetTempPath());
        Kiln.Admin.Ops.TickScheduler = mod.Scheduler;
        var tick = new TickLoop(mod);
        try
        {
            body(loop, tick);
        }
        finally
        {
            tick.Dispose();
            Kiln.Admin.Ops.TickScheduler = null;
            mod.Retire();
            NetEngine.OnPeer(ClientPeer, false);
            NetEngine.OnPeer(NetNative.PeerServer, false);
            NetNative.TestTransport = null;
        }
    }

    [Fact]
    public void Writes_after_the_guard_run_on_the_tick_thread_when_the_reply_is_delivered_off_thread() => WithTick((loop, tick) =>
    {
        // The frames are pumped here, on the test's thread: the server role's reply handler completes the relay's wait
        // off the tick thread.
        var replied = RunCommand(tick, loop.Pump);
        Assert.True(replied["protected"]);
        Assert.True(replied["write"], $"the write ran off the tick thread after a reply delivered off-thread ({Seen["write"]})");
        Assert.True(replied["reply"], $"the reply ran off the tick thread after a reply delivered off-thread ({Seen["reply"]})");
    });

    [Fact]
    public void Writes_after_the_guard_run_on_the_tick_thread_after_the_timeout() => WithTick((loop, tick) =>
    {
        // The client never answers: the relay's wait ends on a thread-pool timer.
        SteamGuardRelay.ReplyTimeout = TimeSpan.FromMilliseconds(200);
        loop.Deaf = true;
        var timedOut = RunCommand(tick, () => { });
        Assert.False(timedOut["protected"]);
        Assert.True(timedOut["write"], $"the write ran off the tick thread after the timeout ({Seen["write"]})");
        Assert.True(timedOut["reply"], $"the reply ran off the tick thread after the timeout ({Seen["reply"]})");
    });

    [Fact]
    public void Admin_quest_mutations_latch_and_hop_before_touching_quests()
    {
        var source = Path.GetFullPath(Path.Combine(SourceDirectory(), "../../../mods/Kiln.Admin/Ops.Quests.cs"));
        var text = File.ReadAllText(source);
        var set = text[text.IndexOf("bridge.RegisterAsync(\"modquest_set\"", StringComparison.Ordinal)..];
        set = set[..set.IndexOf("// {signal", StringComparison.Ordinal)];
        var latch = set.IndexOf("await SteamGuardRelay.LatchForAll", StringComparison.Ordinal);
        var hop = set.IndexOf("await TickThread()", StringComparison.Ordinal);
        var write = set.IndexOf("Quests.SetStep", StringComparison.Ordinal);
        Assert.True(latch >= 0 && hop > latch && write > hop, "modquest_set must latch all potential reward recipients, then hop, before any mutation");
        Assert.Contains("Guarded(", set);
        var signal = text[text.IndexOf("// {signal", StringComparison.Ordinal)..];
        Assert.Contains("bridge.RegisterAsync(\"modquest_signal\"", signal);
        Assert.True(signal.IndexOf("await SteamGuardRelay.LatchForAll", StringComparison.Ordinal) < signal.IndexOf("Quests.Signal", StringComparison.Ordinal));
    }

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;

    [Fact]
    public void Notes_name_the_unprotected_cases()
    {
        Assert.Null(SteamGuardRelay.Note(GuardOutcome.Protected));
        Assert.Contains("no Kiln achievement guard", SteamGuardRelay.Note(GuardOutcome.UnprotectedVanilla));
        Assert.Contains("could not be turned on", SteamGuardRelay.Note(GuardOutcome.UnprotectedFailed));
    }
}
