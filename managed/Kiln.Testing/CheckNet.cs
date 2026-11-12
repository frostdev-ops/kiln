// SPDX-License-Identifier: MIT
namespace Kiln.Testing;

// The joined session over kiln.net: channel "kiln:checks" v1, not required (it never blocks a join). JSON messages in
// this registration order (ids 0..5); a scripted client (mods/tests/client-check-test/fixtures) speaks the same.

/// <summary>Client to server: send the list.</summary>
internal sealed record NetCatalogRequest
{
    public int Version { get; init; } = 1;
}

/// <summary>Server to client: the server's checks as a joined session sees them.</summary>
internal sealed record NetCatalog
{
    public bool Enabled { get; init; }
    public string? Reason { get; init; }
    public List<ViewItem> Checks { get; init; } = new();
}

internal sealed record NetGuard
{
    public bool Available { get; init; }
    public bool HookInstalled { get; init; }
    public bool TargetVerified { get; init; }
    public bool Active { get; init; }
    public long Dropped { get; init; }
    public long Passed { get; init; }
    public string Reason { get; init; } = "";

    public GuardStatus ToStatus() => new(Available, HookInstalled, TargetVerified, Active, Dropped, Passed, Reason);

    public static NetGuard? Of(GuardStatus? s) => s is not { } x
        ? null
        : new NetGuard
        {
            Available = x.Available, HookInstalled = x.HookInstalled, TargetVerified = x.TargetVerified, Active = x.Active, Dropped = x.Dropped,
            Passed = x.Passed, Reason = x.Reason,
        };
}

/// <summary>The joining client's own answer for a cheats check (<see cref="Attestation"/>).</summary>
internal sealed record NetAttest
{
    public bool Latched { get; init; }
    public NetGuard? Status { get; init; }
    public string Proof { get; init; } = "none";
    public string? Sha256 { get; init; }
    public string? Kiln { get; init; }
    public DateTime Taken { get; init; }
    public string? Problem { get; init; }

    public Attestation ToAttestation() => new()
    {
        Latched = Latched, Status = Status?.ToStatus(), Proof = Proof, Sha256 = Sha256, Kiln = Kiln, Taken = Taken.ToUniversalTime(), Problem = Problem,
    };

    public static NetAttest Of(Attestation a) => new()
    {
        Latched = a.Latched, Status = NetGuard.Of(a.Status), Proof = a.Proof, Sha256 = a.Sha256, Kiln = a.Kiln, Taken = a.Taken, Problem = a.Problem,
    };
}

/// <summary>Client to server: start one check (with the client's guard answer for a cheats check).</summary>
internal sealed record NetStart
{
    public string Mod { get; init; } = "";
    public string Id { get; init; } = "";
    public NetAttest? Attest { get; init; }
}

/// <summary>Server to client: what happened to the client's check (a <see cref="RunEventKind"/> in lower case).</summary>
internal sealed record NetState
{
    public string Kind { get; init; } = "";
    public string Mod { get; init; } = "";
    public string Id { get; init; } = "";
    public string Message { get; init; } = "";
    public ViewCurrent? Current { get; init; }
}

/// <summary>Client to server: pass, fail, skip, yes, no or stop; a verdict on a cheats check carries the client's guard
/// status read then.</summary>
internal sealed record NetAnswer
{
    public string Kind { get; init; } = "";
    public string? Note { get; init; }
    public NetGuard? Guard { get; init; }
}

/// <summary>Server to client: the entry the server wrote, for the client's own ledger.</summary>
internal sealed record NetEntry
{
    public LedgerEntry? Entry { get; init; }
}

/// <summary>The two halves of a joined session: on the server, requests from joining clients go to the runner; on a
/// joining client, the server's list, state and entries reach the screen and the client's own ledger.</summary>
internal sealed class CheckNet
{
    public const string ChannelName = "kiln:checks", ChannelVersion = "1";

    private readonly CheckRunner _runner;
    private readonly IRunnerEnv _env;
    private readonly NetChannel _channel;
    private readonly object _lock = new();

    // client half (main thread handlers, screen reads): under _lock
    private NetCatalog? _catalog;
    private ViewCurrent? _current;
    private string? _message;
    private readonly Queue<ViewItem> _all = new();
    private bool _runningAll;
    private NetPeer? _server;
    private string? _changed;

    private CheckNet(CheckRunner runner, IRunnerEnv env, NetChannel channel)
    {
        _runner = runner;
        _env = env;
        _channel = channel;
    }

    /// <summary>Opens the channel as Kiln's own (call outside any mod's context).</summary>
    public static CheckNet? Open(CheckRunner runner, IRunnerEnv env)
    {
        if (!Network.Available) return null;
        var channel = Network.Channel(ChannelName, ChannelVersion, required: false);
        CheckNet? net = null;
        channel.RegisterJson<NetCatalogRequest>((m, c) => net!.OnCatalogRequest(c), NetDirection.ToServer)
            .RegisterJson<NetCatalog>((m, c) => net!.OnCatalog(m), NetDirection.ToClient)
            .RegisterJson<NetStart>((m, c) => net!.OnStart(m, c), NetDirection.ToServer)
            .RegisterJson<NetState>((m, c) => net!.OnState(m), NetDirection.ToClient)
            .RegisterJson<NetAnswer>((m, c) => net!.OnAnswer(m, c), NetDirection.ToServer)
            .RegisterJson<NetEntry>((m, c) => net!.OnEntry(m), NetDirection.ToClient);
        net = new CheckNet(runner, env, channel);
        runner.Events += net.OnRunnerEvent;
        return net;
    }

    /// <summary>A server takes joined checks only while its operator keeps <c>kiln/control/checks/</c>.</summary>
    private static bool ServerEnabled => ControlFolder.Enabled(Path.Combine(KilnPaths.Control, "checks"));

    // ---- server half (handlers on the tick thread) ----

    private void OnCatalogRequest(NetContext c)
    {
        var peer = c.Sender;
        _runner.Post(() =>
        {
            var enabled = ServerEnabled;
            _channel.SendTo(peer, new NetCatalog
            {
                Enabled = enabled,
                Reason = enabled ? null : "this server does not take guided checks (its operator has no kiln/control/checks/ folder)",
                Checks = enabled ? _runner.Items(ClientCheckSession.Joined, null) : [],
            });
        });
    }

    private void OnStart(NetStart m, NetContext c)
    {
        var peer = c.Sender;
        var player = c.Player;
        _runner.Post(() =>
        {
            if (!ServerEnabled)
            {
                _channel.SendTo(peer, new NetState { Kind = "refused", Mod = m.Mod, Id = m.Id, Message = "this server does not take guided checks" });
                return;
            }
            _runner.Request(new StartRequest(m.Mod ?? "", m.Id ?? "", CheckOrigin.Remote, m.Attest?.ToAttestation(), player, peer));
        });
    }

    private void OnAnswer(NetAnswer m, NetContext c)
    {
        var peer = c.Sender;
        _runner.Post(() =>
        {
            AnswerKind? kind = m.Kind switch
            {
                "pass" => AnswerKind.Pass, "fail" => AnswerKind.Fail, "skip" => AnswerKind.Skip, "yes" => AnswerKind.Yes, "no" => AnswerKind.No,
                "stop" => AnswerKind.Stop, _ => null,
            };
            var why = kind is { } k ? _runner.Answer(k, m.Note, peer, m.Guard?.ToStatus()) : $"unknown answer '{m.Kind}'";
            if (why is not null) _channel.SendTo(peer, new NetState { Kind = "answer-refused", Message = why });
        });
    }

    private void OnRunnerEvent(RunEvent e)
    {
        if (e.Controller is not NetPeer peer) return;
        _channel.SendTo(peer, new NetState { Kind = e.Kind.ToString().ToLowerInvariant(), Mod = e.Mod, Id = e.Id, Message = e.Message, Current = e.Current });
        if (e is { Kind: RunEventKind.Finished, Entry: { } entry }) _channel.SendTo(peer, new NetEntry { Entry = entry });
    }

    // ---- client half (handlers on the main thread; the screen calls from the render thread) ----

    /// <summary>This client is joined to a server that has the channel.</summary>
    public bool Joined
    {
        get
        {
            try
            {
                return _env.IsClient && Client.IsRemote && Network.Server is { } s && s.HasChannel(ChannelName);
            }
            catch
            {
                return false;
            }
        }
    }

    public void RequestCatalog()
    {
        if (Joined) _channel.SendToServer(new NetCatalogRequest());
    }

    /// <summary>Starts a server check from the joined client's screen. For a cheats check this client's guard latches here
    /// and its answer goes with the request.</summary>
    public void Start(ViewItem item)
    {
        NetAttest? attest = null;
        if (item.Cheats)
        {
            var a = CheatsRule.Attest(item.Mod + "/" + item.Id, _env.Guard, _runner.Ledger.ReadFile, _env.Game, _env.Now);
            attest = NetAttest.Of(a);
        }
        lock (_lock) _message = $"starting {item.Mod}/{item.Id}";
        _channel.SendToServer(new NetStart { Mod = item.Mod, Id = item.Id, Attest = attest });
    }

    public void StartAll()
    {
        lock (_lock)
        {
            _all.Clear();
            foreach (var i in _catalog?.Checks ?? []) _all.Enqueue(i);
            _runningAll = _all.Count > 0;
        }
        Next();
    }

    private void Next()
    {
        ViewItem? next = null;
        lock (_lock)
        {
            if (_runningAll && !_all.TryDequeue(out next)) _runningAll = false;
        }
        if (next is not null) Start(next);
    }

    public void Answer(AnswerKind kind, string? note = null)
    {
        GuardStatus? guard = null;
        bool cheats;
        lock (_lock)
        {
            cheats = _current?.Cheats ?? false;
            if (kind == AnswerKind.Stop)
            {
                _all.Clear();
                _runningAll = false;
            }
        }
        if (cheats && kind is AnswerKind.Pass or AnswerKind.Fail or AnswerKind.Skip && _env.Guard is { } g)
            guard = CheatsRule.ReadStatus(g).Status;
        _channel.SendToServer(new NetAnswer { Kind = kind.ToString().ToLowerInvariant(), Note = note, Guard = NetGuard.Of(guard) });
    }

    private void OnCatalog(NetCatalog m)
    {
        lock (_lock)
        {
            _catalog = m;
            if (!m.Enabled) _message = m.Reason;
        }
    }

    private void OnState(NetState m)
    {
        var next = false;
        lock (_lock)
        {
            _message = m.Kind == "refused" ? $"refused {m.Mod}/{m.Id}: {m.Message}" : m.Message;
            switch (m.Kind)
            {
                case "refused" or "declined":
                    _current = null;
                    next = true;
                    break;
                case "finished":
                    _current = null;
                    next = true;
                    break;
                case "answer-refused":
                    break;
                default:
                    _current = m.Current;
                    if (m.Kind == "started" && m.Current is { Cheats: true }) _changed = CheckRunner.ChangedCharacter;
                    break;
            }
        }
        if (next)
        {
            RequestCatalog();
            Next();
        }
    }

    private void OnEntry(NetEntry m)
    {
        if (m.Entry is not { } e) return;
        var game = _env.Game;
        var mine = e with
        {
            Game = new LedgerGame { Side = game.Side, Sha256 = game.Sha256, Verified = game.Verified }, Kiln = game.Kiln, Session = "joined",
            Note = _runner.Scrubber.Line(e.Note, 600), Log = e.Log.Select(l => _runner.Scrubber.Line(l, CheckRunner.MaxLine)).ToArray(),
        };
        if (!_runner.Ledger.Append(mine)) _env.Write(LogLevel.Error, $"checks: the joined entry of {e.Mod}/{e.Id} was not written: {_runner.Ledger.WriteError}");
        _runner.MarkDirtyFromAnyThread();
    }

    /// <summary>The joined session as the screen draws it (any thread).</summary>
    public RunnerView View(RunnerView local)
    {
        lock (_lock)
        {
            var newest = _runner.Ledger.Newest();
            var items = (_catalog?.Checks ?? []).Select(i =>
                newest.TryGetValue(i.Mod + "/" + i.Id, out var e) ? i with { Verdict = e.Verdict, Note = e.Note } : i).ToList();
            return local with
            {
                Session = "joined", Items = items, Current = _current, Message = _message ?? (_catalog is null ? "asking the server for its checks" : null),
                Queued = _all.Count, Changed = _changed ?? local.Changed,
            };
        }
    }

    /// <summary>Tick thread: forget the server's state when the client left it.</summary>
    public void Step()
    {
        NetPeer? server = null;
        try
        {
            if (_env.IsClient) server = Network.Server;
        }
        catch
        {
            server = null;
        }
        lock (_lock)
        {
            if (ReferenceEquals(server, _server)) return;
            _server = server;
            _catalog = null;
            _current = null;
            _all.Clear();
            _runningAll = false;
            _message = null;
            _changed = null;  // the world was left
        }
    }
}
