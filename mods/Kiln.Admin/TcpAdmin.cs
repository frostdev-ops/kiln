// SPDX-License-Identifier: MIT
// KAP/1, the Kiln Admin Protocol over TCP (docs/kiln/launcher/admin-protocol.md): u32 LE length + one UTF-8 JSON
// object per frame, an HMAC-SHA256 handshake keyed by kiln/data/kiln-admin/admin-token, then req/res through the same
// op table as the file bridge (Bridge.Execute), sub/push of state and kiln.log lines, ping/pong and bye.
//
// Threads: one accept thread, one reader and one writer thread per connection, one housekeeping thread (pings, idle
// and op timeouts, log tailing). Ops never run on those: requests queue up and drain on the tick thread in Pre (at most
// 32 per step, oldest first). Every thread polls with short timeouts and is joined on unload, so a mod reload leaves
// nothing behind (reload-test kiln-admin).
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

internal sealed class TcpAdmin : IDisposable
{
    public const string Protocol = "kap/1";
    public const int MaxClientFrame = 1 << 20;
    public const int MaxServerFrame = 16 << 20;
    public const int MaxConnections = 4;
    public const int MaxInflight = 64;
    public const int DrainPerStep = 32;
    public const int PingS = 15, IdleS = 45, HandshakeS = 5;
    private const long SlowClientBytes = 4L << 20;
    private const int LogLinesPerPush = 256;
    public static readonly string[] Topics = ["state", "log"];

    private readonly Bridge _bridge;
    private readonly Logger _log;
    private readonly string _dataDir;
    private readonly Func<(string? Id, string? Name)> _ident;
    private readonly string _kiln, _admin, _logFile;
    private TcpListener? _listener;
    private Thread? _accept, _house;
    private volatile bool _stopping;
    private readonly List<Conn> _conns = [];
    private readonly ConcurrentQueue<Request> _queue = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private byte[] _token;
    private int _nextConn;
    private readonly long _started = Environment.TickCount64;
    private LogTail? _tail;
    private volatile string? _lastState;  // the latest state.json text, pushed at once to a new "state" subscriber

    public int Port { get; private set; }
    public string? EndpointFile { get; private set; }
    public int Connections { get { lock (_conns) return _conns.Count; } }

    /// <summary>Set on the tick thread while a KAP request's op runs synchronously (admin_token_rotate needs it).</summary>
    public object? CurrentCaller { get; private set; }

    public double UptimeSeconds => (Environment.TickCount64 - _started) / 1000.0;

    private sealed record Request(Conn Conn, ulong Id, string Op, JsonObject Args);

    private sealed class Flight(string op, long deadline, long started)
    {
        public readonly string Op = op;
        public readonly long Deadline = deadline, Started = started;
    }

    private sealed class Conn(int number, TcpClient client)
    {
        public readonly int Number = number;
        public readonly TcpClient Client = client;
        public readonly Socket Socket = client.Client;
        public readonly NetworkStream Stream = client.GetStream();
        public readonly object Lock = new();
        public readonly Dictionary<ulong, Flight> Inflight = new();
        public readonly Queue<byte[]> Out = new();
        public readonly AutoResetEvent Signal = new(false);
        public long OutBytes;
        public volatile bool Ready, Closed, CloseAfterFlush;
        public bool SubState, SubLog;
        public long SeqState, SeqLog;
        public string Session = "", Nonce = "", ClientName = "";
        public readonly long Connected = Environment.TickCount64;
        public long LastRecv = Environment.TickCount64, LastSend = Environment.TickCount64;
        public ulong PingId;
        public Thread? Reader, Writer;
        public override string ToString() => $"#{Number}{(ClientName.Length > 0 ? " (" + ClientName + ")" : "")}";
    }

    public TcpAdmin(Bridge bridge, Logger log, string dataDir, Func<(string? Id, string? Name)> ident, string kiln, string admin)
    {
        _bridge = bridge;
        _log = log;
        _dataDir = dataDir;
        _ident = ident;
        _kiln = kiln;
        _admin = admin;
        _token = AdminToken.LoadOrCreate(dataDir, log);
        _logFile = Path.GetFullPath(Path.Combine(dataDir, "..", "..", "logs", "kiln.log"));
    }

    // ---------------------------------------------------------------------------------------------- start / stop

    /// <summary>Binds 127.0.0.1 on <paramref name="port"/> (0: <paramref name="candidates"/> in order, then any free
    /// port), starts the threads and writes endpoint.json. False (logged) when nothing could be bound.</summary>
    public bool Start(int port, IReadOnlyList<int> candidates)
    {
        var tries = port > 0 ? new List<int> { port } : [.. candidates.Where(p => p is > 0 and < 65536)];
        tries.Add(0);
        foreach (var p in tries)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start(8);
                _listener = l;
                Port = ((IPEndPoint)l.LocalEndpoint).Port;
                break;
            }
            catch (SocketException e)
            {
                if (p == port && port > 0) _log.Warn($"KAP: port {p} is not free ({e.SocketErrorCode}); using another");
            }
        }
        if (_listener is null)
        {
            _log.Error("KAP: could not bind 127.0.0.1; TCP admin is off");
            return false;
        }
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "kiln-admin kap accept" };
        _house = new Thread(HouseLoop) { IsBackground = true, Name = "kiln-admin kap house" };
        _accept.Start();
        _house.Start();
        WriteEndpoint();
        return true;
    }

    /// <summary>Sends bye{reason} to every connection, closes them, joins every thread, deletes endpoint.json.</summary>
    public void Stop(string reason)
    {
        if (_stopping) return;
        _stopping = true;
        _stop.Set();
        try { _listener?.Stop(); } catch (Exception) { }
        Conn[] all;
        lock (_conns) all = [.. _conns];
        foreach (var c in all)
        {
            Send(c, new JsonObject { ["t"] = "bye", ["reason"] = reason });
            c.CloseAfterFlush = true;
            c.Signal.Set();
        }
        var until = Environment.TickCount64 + 600;
        foreach (var c in all)
            while (!c.Closed && Environment.TickCount64 < until) Thread.Sleep(10);
        foreach (var c in all) Close(c, null);
        Join(_accept);
        Join(_house);
        foreach (var c in all)
        {
            Join(c.Reader);
            Join(c.Writer);
            c.Signal.Dispose();
        }
        while (_queue.TryDequeue(out _)) { }
        lock (_conns) _conns.Clear();
        DeleteEndpoint();
        _stop.Dispose();
    }

    public void Dispose() => Stop("unload");

    private void Join(Thread? t)
    {
        if (t is null || t == Thread.CurrentThread) return;
        if (!t.Join(3000)) _log.Warn($"KAP: thread {t.Name} did not stop in 3 s");
    }

    // ---------------------------------------------------------------------------------------------- endpoint, token

    private string EndpointPath => Path.Combine(_dataDir, "endpoint.json");

    public void WriteEndpoint()
    {
        if (_listener is null) return;
        var (id, name) = SafeIdent();
        var ep = new JsonObject
        {
            ["format"] = 1, ["protocol"] = Protocol, ["host"] = "127.0.0.1", ["port"] = Port,
            ["pid"] = Environment.ProcessId, ["started"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["kiln"] = _kiln, ["admin"] = _admin, ["server"] = new JsonObject { ["id"] = id, ["name"] = name },
            ["token"] = AdminToken.FileName, ["bridge"] = _bridge.Directory,
        };
        try
        {
            Bridge.WriteAtomic(EndpointPath, ep.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            EndpointFile = EndpointPath;
        }
        catch (Exception e)
        {
            _log.Warn($"KAP: cannot write {EndpointPath}: {e.Message}");
        }
    }

    private void DeleteEndpoint()
    {
        try
        {
            if (File.Exists(EndpointPath) && JsonNode.Parse(File.ReadAllText(EndpointPath))?["pid"]?.GetValue<int>() == Environment.ProcessId)
                File.Delete(EndpointPath);
        }
        catch (Exception e)
        {
            _log.Warn($"KAP: cannot delete {EndpointPath}: {e.Message}");
        }
    }

    private (string? Id, string? Name) SafeIdent()
    {
        try
        {
            return _ident();
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>admin_token_rotate: a new token file; every connection but <paramref name="keep"/> gets bye{token}.</summary>
    public void RotateToken(object? keep)
    {
        var fresh = AdminToken.Rotate(_dataDir);
        Volatile.Write(ref _token, fresh);
        Conn[] all;
        lock (_conns) all = [.. _conns];
        foreach (var c in all)
        {
            if (ReferenceEquals(c, keep)) continue;
            Send(c, new JsonObject { ["t"] = "bye", ["reason"] = "token" });
            c.CloseAfterFlush = true;
            c.Signal.Set();
        }
        _log.Info($"KAP: admin token rotated; {all.Count(c => !ReferenceEquals(c, keep))} other connection(s) closed");
    }

    // ---------------------------------------------------------------------------------------------- tick thread

    /// <summary>Tick thread, every step: finishes async ops, then runs up to 32 queued requests.</summary>
    public void Drain()
    {
        _bridge.FinishPending();
        for (var n = 0; n < DrainPerStep && _queue.TryDequeue(out var r); n++)
        {
            if (r.Conn.Closed) continue;
            var req = new JsonObject { ["op"] = r.Op };
            foreach (var key in r.Args.Select(kv => kv.Key).ToList())
            {
                if (key == "op") continue;
                var v = r.Args[key];
                r.Args.Remove(key);
                req[key] = v;
            }
            var conn = r.Conn;
            var id = r.Id;
            CurrentCaller = conn;
            try
            {
                _bridge.Execute(req, result => Complete(conn, id, result));
            }
            finally
            {
                CurrentCaller = null;
            }
        }
    }

    private void Complete(Conn c, ulong id, JsonObject result)
    {
        lock (c.Lock)
            if (!c.Inflight.Remove(id)) return;  // timed out (already answered) or the connection closed
        if (c.Closed) return;
        var ok = result["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var op = result["op"] is JsonValue o && o.TryGetValue<string>(out var s) ? s : "";
        var json = Bridge.Serialize(result);  // {"op":..., ...}
        var sb = new StringBuilder(json.Length + 48);
        sb.Append("{\"t\":\"res\",\"id\":").Append(id).Append(',').Append(json, 1, json.Length - 2);
        if (!ok) sb.Append(",\"code\":\"").Append(_bridge.Has(op) ? "op_failed" : "unknown_op").Append('"');
        sb.Append('}');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        if (bytes.Length > MaxServerFrame)
        {
            Send(c, new JsonObject
            {
                ["t"] = "res", ["id"] = id, ["ok"] = false, ["op"] = op, ["ms"] = result["ms"]?.DeepClone(),
                ["error"] = $"the result is {bytes.Length} bytes, over the {MaxServerFrame}-byte frame limit", ["code"] = "too_large",
            });
            return;
        }
        Enqueue(c, bytes, push: false);
    }

    /// <summary>Tick thread, about once a second: the state.json text to every "state" subscriber.</summary>
    public void PushState(string stateJson)
    {
        _lastState = stateJson;
        Conn[] all;
        lock (_conns) all = [.. _conns];
        foreach (var c in all)
        {
            if (!c.Ready || !c.SubState || c.Closed) continue;
            var seq = Interlocked.Increment(ref c.SeqState);
            var text = "{\"t\":\"push\",\"topic\":\"state\",\"seq\":" + seq + ",\"data\":" + stateJson + "}";
            Enqueue(c, Encoding.UTF8.GetBytes(text), push: true);
        }
    }

    // ---------------------------------------------------------------------------------------------- sockets

    private void AcceptLoop()
    {
        var l = _listener!;
        while (!_stopping)
        {
            TcpClient client;
            try
            {
                if (!l.Pending())
                {
                    _stop.Wait(100);
                    continue;
                }
                client = l.AcceptTcpClient();
            }
            catch (Exception) when (_stopping)
            {
                break;
            }
            catch (Exception e)
            {
                _log.Warn($"KAP: accept failed: {e.Message}");
                _stop.Wait(500);
                continue;
            }
            Conn? conn = null;
            lock (_conns)
            {
                if (_conns.Count < MaxConnections && !_stopping)
                {
                    conn = new Conn(++_nextConn, client);
                    _conns.Add(conn);
                }
            }
            if (conn is null)
            {
                RefuseBusy(client);
                continue;
            }
            client.NoDelay = true;
            conn.Socket.SendTimeout = 30_000;
            conn.Reader = new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = $"kiln-admin kap read {conn.Number}" };
            conn.Writer = new Thread(() => WriteLoop(conn)) { IsBackground = true, Name = $"kiln-admin kap write {conn.Number}" };
            conn.Writer.Start();
            conn.Reader.Start();
        }
    }

    private static void RefuseBusy(TcpClient client)
    {
        try
        {
            client.Client.SendTimeout = 2000;
            var frame = Frame(new JsonObject { ["t"] = "error", ["code"] = "busy", ["message"] = $"at most {MaxConnections} KAP connections" });
            client.GetStream().Write(frame);
        }
        catch (Exception)
        {
        }
        finally
        {
            client.Close();
        }
    }

    private void WriteLoop(Conn c)
    {
        try
        {
            while (!c.Closed)
            {
                byte[]? next;
                lock (c.Lock)
                {
                    next = c.Out.Count > 0 ? c.Out.Dequeue() : null;
                    if (next != null) c.OutBytes -= next.Length;
                }
                if (next is null)
                {
                    if (c.CloseAfterFlush) break;
                    c.Signal.WaitOne(200);
                    continue;
                }
                c.Stream.Write(next);
                c.LastSend = Environment.TickCount64;
            }
        }
        catch (Exception) when (c.Closed || _stopping)
        {
        }
        catch (Exception e)
        {
            _log.Info($"KAP {c}: send failed ({e.GetType().Name}); closing");
        }
        Close(c, null);
    }

    private void ReadLoop(Conn c)
    {
        string? why = null;
        try
        {
            c.Nonce = RandomHex(32);
            var (id, name) = SafeIdent();
            Send(c, new JsonObject
            {
                ["t"] = "hello", ["proto"] = Protocol, ["nonce"] = c.Nonce, ["kiln"] = _kiln, ["admin"] = _admin,
                ["server"] = new JsonObject { ["id"] = id, ["name"] = name }, ["auth"] = new JsonArray((JsonNode)"hmac-sha256"),
            });
            if (!Handshake(c)) return;
            while (!c.Closed && !_stopping)
            {
                var frame = ReadFrame(c, out var fatal);
                if (frame is null)
                {
                    if (fatal is { } f) Fatal(c, f.Code, f.Message);
                    return;
                }
                Handle(c, frame);
            }
        }
        catch (Exception e) when (c.Closed || _stopping)
        {
            why = e.Message;
        }
        catch (IOException)
        {
            why = "connection lost";
        }
        catch (SocketException)
        {
            why = "connection lost";
        }
        catch (Exception e)
        {
            _log.Error(e, $"KAP {c}");
            Fatal(c, "internal", e.Message);
        }
        finally
        {
            if (!c.CloseAfterFlush) Close(c, why);
        }
    }

    private bool Handshake(Conn c)
    {
        var frame = ReadFrame(c, out var fatal);
        if (frame is null)
        {
            if (fatal is { } f) Fatal(c, f.Code, f.Message);
            return false;
        }
        if (Str(frame, "t") != "auth")
        {
            Fatal(c, "auth_required", "authenticate first (auth frame)");
            return false;
        }
        if (Str(frame, "proto") != Protocol)
        {
            Fatal(c, "version", $"this server speaks {Protocol}");
            return false;
        }
        var cnonce = Str(frame, "cnonce") ?? "";
        var mac = Str(frame, "mac") ?? "";
        var token = Volatile.Read(ref _token);
        if (!IsHex(cnonce, 64) || !IsHex(mac, 64) ||
            !CryptographicOperations.FixedTimeEquals(Mac(token, "kap1-auth", c.Nonce, cnonce), Convert.FromHexString(mac)))
        {
            _log.Warn($"KAP {c}: authentication failed");
            _stop.Wait(250);
            Fatal(c, "auth_failed", "wrong mac");
            return false;
        }
        c.ClientName = (Str(frame, "client") ?? "").Length is > 0 and <= 80 ? Str(frame, "client")! : "client";
        c.Session = RandomHex(8);
        Send(c, new JsonObject
        {
            ["t"] = "ready", ["session"] = c.Session, ["mac"] = Convert.ToHexStringLower(Mac(token, "kap1-ready", c.Nonce, cnonce)),
            ["ops"] = new JsonArray([.. _bridge.OpNames.Select(o => (JsonNode)o)]),
            ["topics"] = new JsonArray([.. Topics.Select(t => (JsonNode)t)]),
            ["limits"] = new JsonObject
            {
                ["maxFrame"] = MaxServerFrame, ["maxClientFrame"] = MaxClientFrame, ["inflight"] = MaxInflight,
                ["pingS"] = PingS, ["idleS"] = IdleS,
            },
        });
        c.Ready = true;
        _log.Info($"KAP {c}: session {c.Session} ready");
        return true;
    }

    private readonly record struct FatalError(string Code, string Message);

    /// <summary>One frame from <paramref name="c"/>; null at EOF/close (fatal set for a protocol error).</summary>
    private JsonObject? ReadFrame(Conn c, out FatalError? fatal)
    {
        fatal = null;
        Span<byte> head = stackalloc byte[4];
        if (!ReadExact(c, head)) return null;
        var len = BitConverter.ToUInt32(head);
        if (len == 0)
        {
            fatal = new("bad_frame", "zero-length frame");
            return null;
        }
        if (len > MaxClientFrame)
        {
            fatal = new("too_large", $"frames from clients are at most {MaxClientFrame} bytes");
            return null;
        }
        var payload = new byte[len];
        if (!ReadExact(c, payload)) return null;
        c.LastRecv = Environment.TickCount64;
        try
        {
            if (JsonNode.Parse(payload) is JsonObject o) return o;
        }
        catch (JsonException)
        {
        }
        fatal = new("bad_frame", "not a JSON object");
        return null;
    }

    private bool ReadExact(Conn c, Span<byte> buf)
    {
        var got = 0;
        while (got < buf.Length)
        {
            if (c.Closed || _stopping) return false;
            if (!c.Socket.Poll(200_000, SelectMode.SelectRead)) continue;
            var n = c.Stream.Read(buf[got..]);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    private void Handle(Conn c, JsonObject f)
    {
        var t = Str(f, "t");
        switch (t)
        {
            case "req":
            {
                if (Id(f) is not { } id)
                {
                    Error(c, "bad_frame", "req needs an unsigned integer id", null);
                    return;
                }
                var op = Str(f, "op");
                if (string.IsNullOrEmpty(op))
                {
                    Error(c, "bad_frame", "req needs an op", id);
                    return;
                }
                JsonObject args;
                switch (f["args"])
                {
                    case null: args = new JsonObject(); break;
                    case JsonObject a:
                        f.Remove("args");
                        args = a;
                        break;
                    default:
                        Error(c, "bad_frame", "args must be an object", id);
                        return;
                }
                if (_stopping)
                {
                    Res(c, id, op, "shutting_down", "Kiln.Admin is unloading");
                    return;
                }
                lock (c.Lock)
                {
                    if (c.Inflight.ContainsKey(id))
                    {
                        Error(c, "bad_frame", $"request {id} is already in flight", id);
                        return;
                    }
                    if (c.Inflight.Count >= MaxInflight)
                    {
                        Res(c, id, op, "busy", $"at most {MaxInflight} requests in flight");
                        return;
                    }
                    var now = Environment.TickCount64;
                    c.Inflight[id] = new Flight(op, now + BudgetMs(op), now);
                }
                _queue.Enqueue(new Request(c, id, op, args));
                return;
            }
            case "sub":
            case "unsub":
            {
                if (Id(f) is not { } id)
                {
                    Error(c, "bad_frame", $"{t} needs an unsigned integer id", null);
                    return;
                }
                var topic = Str(f, "topic") ?? "";
                if (!Topics.Contains(topic))
                {
                    Res(c, id, t, "unknown_topic", $"no topic '{topic}' (topics: {string.Join(", ", Topics)})");
                    return;
                }
                var on = t == "sub";
                bool was;
                lock (c.Lock)
                {
                    was = topic == "state" ? c.SubState : c.SubLog;
                    if (topic == "state") c.SubState = on;
                    else c.SubLog = on;
                }
                if (topic == "log" && on) EnsureTail();
                Send(c, new JsonObject { ["t"] = "res", ["id"] = id, ["ok"] = true, ["op"] = t });
                if (topic == "state" && on && !was && _lastState is { } last)
                {
                    var seq = Interlocked.Increment(ref c.SeqState);  // the latest state at once, then about every 1 s
                    Enqueue(c, Encoding.UTF8.GetBytes("{\"t\":\"push\",\"topic\":\"state\",\"seq\":" + seq + ",\"data\":" + last + "}"), push: true);
                }
                return;
            }
            case "ping":
            {
                var pong = new JsonObject { ["t"] = "pong", ["id"] = f["id"]?.DeepClone() ?? JsonValue.Create(0) };
                if (f["ts"] is { } ts) pong["ts"] = ts.DeepClone();
                Send(c, pong);
                return;
            }
            case "pong":
                return;  // LastRecv already counts it
            case "bye":
                _log.Info($"KAP {c}: bye ({Str(f, "reason") ?? "-"})");
                c.CloseAfterFlush = true;
                c.Signal.Set();
                return;
            case "auth":
                Error(c, "bad_frame", "already authenticated", null);
                return;
            default:
                Error(c, "bad_frame", t is null ? "frame without t" : $"unknown frame type '{t}'", Id(f));
                return;
        }
    }

    /// <summary>Per-op answer budget.</summary>
    internal static int BudgetMs(string op) => op switch
    {
        "mod_test" => 180_000,
        "backup_create" => 90_000,
        "save_now" => 60_000,
        _ when op.StartsWith("mod_", StringComparison.Ordinal) => 45_000,
        _ => 30_000,
    };

    // ---------------------------------------------------------------------------------------------- housekeeping

    private void HouseLoop()
    {
        while (!_stopping)
        {
            _stop.Wait(200);
            if (_stopping) break;
            try
            {
                House();
            }
            catch (Exception e)
            {
                _log.Warn($"KAP housekeeping: {e.Message}");
            }
        }
    }

    private void House()
    {
        var now = Environment.TickCount64;
        Conn[] all;
        lock (_conns) all = [.. _conns];
        var anyLog = false;
        foreach (var c in all)
        {
            if (c.Closed) continue;
            if (!c.Ready)
            {
                if (now - c.Connected > HandshakeS * 1000 && !c.CloseAfterFlush)
                    Fatal(c, "auth_required", $"the handshake must finish within {HandshakeS} s");
                continue;
            }
            if (now - c.LastRecv > IdleS * 1000)
            {
                _log.Info($"KAP {c}: silent for {IdleS} s; closing");
                Send(c, new JsonObject { ["t"] = "bye", ["reason"] = "idle" });
                c.CloseAfterFlush = true;
                c.Signal.Set();
                continue;
            }
            if (now - c.LastSend > PingS * 1000)
            {
                c.LastSend = now;  // one ping per interval, even while the writer is busy
                Send(c, new JsonObject { ["t"] = "ping", ["id"] = ++c.PingId, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            }
            List<(ulong Id, Flight F)>? late = null;
            lock (c.Lock)
            {
                foreach (var (id, fl) in c.Inflight)
                    if (now >= fl.Deadline) (late ??= []).Add((id, fl));
                if (late != null) foreach (var (id, _) in late) c.Inflight.Remove(id);
                anyLog |= c.SubLog;
            }
            if (late != null)
                foreach (var (id, fl) in late)
                    Send(c, new JsonObject
                    {
                        ["t"] = "res", ["id"] = id, ["ok"] = false, ["op"] = fl.Op, ["ms"] = (int)(now - fl.Started),
                        ["error"] = $"no answer within {BudgetMs(fl.Op) / 1000} s", ["code"] = "timeout",
                    });
        }
        if (anyLog) PumpLog(all);
    }

    private void EnsureTail()
    {
        lock (_conns) _tail ??= new LogTail(_logFile);
    }

    private void PumpLog(Conn[] all)
    {
        LogTail? tail;
        lock (_conns) tail = _tail;
        if (tail is null) return;
        var lines = tail.Read();
        if (lines.Count == 0) return;
        for (var i = 0; i < lines.Count; i += LogLinesPerPush)
        {
            var batch = new JsonArray([.. lines.Skip(i).Take(LogLinesPerPush).Select(l => (JsonNode)l)]);
            var data = batch.ToJsonString();
            foreach (var c in all)
            {
                bool sub;
                lock (c.Lock) sub = c.SubLog;
                if (!c.Ready || !sub || c.Closed) continue;
                var seq = Interlocked.Increment(ref c.SeqLog);
                Enqueue(c, Encoding.UTF8.GetBytes("{\"t\":\"push\",\"topic\":\"log\",\"seq\":" + seq + ",\"data\":{\"lines\":" + data + "}}"), push: true);
            }
        }
    }

    /// <summary>New lines of kiln.log since the first "log" subscription (restarts at 0 when the file shrinks).</summary>
    private sealed class LogTail(string path)
    {
        private long _offset = File.Exists(path) ? new FileInfo(path).Length : 0;
        private readonly List<byte> _partial = [];

        public List<string> Read()
        {
            var lines = new List<string>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _offset)
                {
                    _offset = 0;
                    _partial.Clear();
                }
                if (fs.Length == _offset) return lines;
                fs.Seek(_offset, SeekOrigin.Begin);
                var buf = new byte[(int)Math.Min(fs.Length - _offset, 256 * 1024)];
                var n = fs.Read(buf);
                _offset += n;
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] != (byte)'\n') continue;
                    _partial.AddRange(buf.AsSpan(start, i - start));
                    lines.Add(Encoding.UTF8.GetString([.. _partial]).TrimEnd('\r'));
                    _partial.Clear();
                    start = i + 1;
                }
                _partial.AddRange(buf.AsSpan(start, n - start));
                if (_partial.Count > 64 * 1024)
                {
                    lines.Add(Encoding.UTF8.GetString([.. _partial]));
                    _partial.Clear();
                }
            }
            catch (IOException)
            {
            }
            return lines;
        }
    }

    // ---------------------------------------------------------------------------------------------- sending

    private static byte[] Frame(JsonObject frame) => Frame(Encoding.UTF8.GetBytes(Bridge.Serialize(frame)));

    private static byte[] Frame(byte[] payload)
    {
        var b = new byte[payload.Length + 4];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), (uint)payload.Length);
        payload.CopyTo(b, 4);
        return b;
    }

    private void Send(Conn c, JsonObject frame) => Enqueue(c, Encoding.UTF8.GetBytes(Bridge.Serialize(frame)), push: false);

    private void Enqueue(Conn c, byte[] payload, bool push)
    {
        if (c.Closed) return;
        var frame = Frame(payload);
        lock (c.Lock)
        {
            if (push && c.OutBytes > SlowClientBytes) return;  // a slow client loses pushes (seq shows the gap), never responses
            c.Out.Enqueue(frame);
            c.OutBytes += frame.Length;
        }
        try
        {
            c.Signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Res(Conn c, ulong id, string op, string code, string error) =>
        Send(c, new JsonObject { ["t"] = "res", ["id"] = id, ["ok"] = false, ["op"] = op, ["ms"] = 0, ["error"] = error, ["code"] = code });

    private void Error(Conn c, string code, string message, ulong? id)
    {
        var e = new JsonObject { ["t"] = "error", ["code"] = code, ["message"] = message };
        if (id is { } i) e["id"] = i;
        Send(c, e);
    }

    private void Fatal(Conn c, string code, string message)
    {
        if (c.Closed) return;
        Error(c, code, message, null);
        c.CloseAfterFlush = true;
        try { c.Signal.Set(); } catch (ObjectDisposedException) { }
    }

    private void Close(Conn c, string? why)
    {
        lock (c.Lock)
        {
            if (c.Closed) return;
            c.Closed = true;
            c.Inflight.Clear();
            c.Out.Clear();
            c.OutBytes = 0;
        }
        try { c.Signal.Set(); } catch (ObjectDisposedException) { }
        try { c.Socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        try { c.Client.Close(); } catch (Exception) { }
        bool removed;
        lock (_conns) removed = _conns.Remove(c);
        if (removed && c.Ready) _log.Info($"KAP {c}: closed{(why is null ? "" : " (" + why + ")")}");
    }

    // ---------------------------------------------------------------------------------------------- helpers

    internal static byte[] Mac(byte[] token, string label, string nonce, string cnonce) =>
        HMACSHA256.HashData(token, Encoding.ASCII.GetBytes(label + "\n" + nonce + "\n" + cnonce));

    internal static string RandomHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private static bool IsHex(string s, int len) => s.Length == len && s.All(char.IsAsciiHexDigit);

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static ulong? Id(JsonObject o) => o["id"] is JsonValue v && v.TryGetValue<ulong>(out var u) ? u : null;
}

/// <summary><c>kiln/data/kiln-admin/admin-token</c>: 64 lower-case hex characters + newline = 32 random bytes, the
/// KAP/1 HMAC key. Created on first start; anyone who can read it can administer the server.</summary>
internal static class AdminToken
{
    public const string FileName = "admin-token";

    public static byte[] LoadOrCreate(string dataDir, Logger log)
    {
        var path = Path.Combine(dataDir, FileName);
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            if (text.Length == 64 && text.All(char.IsAsciiHexDigit)) return Convert.FromHexString(text);
            log.Warn($"{path} is not 64 hex characters: writing a new token");
        }
        return Write(path);
    }

    public static byte[] Rotate(string dataDir) => Write(Path.Combine(dataDir, FileName));

    private static byte[] Write(string path)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Convert.ToHexStringLower(token) + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
        return token;
    }
}
