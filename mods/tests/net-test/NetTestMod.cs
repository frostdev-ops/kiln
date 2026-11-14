// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiln;

namespace NetTest;

// ---- messages of channel net-test:echo v1 (ids in registration order; tools/nettest/nettest.py mirrors them) ----

/// <summary>id 0: text, answered with "echo:" + text.</summary>
public sealed record Echo(string Text) : INetMessage<Echo>
{
    public void Encode(NetWriter w) => w.String(Text);
    public static Echo Decode(ref NetReader r) => new(r.String(4096));
}

/// <summary>id 1: bytes; the server answers with BlobAck and, when Reflect is set, the same bytes back.</summary>
public sealed record Blob(bool Reflect, byte[] Data) : INetMessage<Blob>
{
    public void Encode(NetWriter w) => w.Bool(Reflect).Bytes(Data);
    public static Blob Decode(ref NetReader r) => new(r.Bool(), r.Bytes());
}

/// <summary>id 2: length and CRC-32 of a received blob.</summary>
public sealed record BlobAck(int Length, uint Crc) : INetMessage<BlobAck>
{
    public void Encode(NetWriter w) => w.Int32(Length).UInt32(Crc);
    public static BlobAck Decode(ref NetReader r) => new(r.Int32(), r.UInt32());
}

/// <summary>id 3: sent to every Kiln client as "all:" + text.</summary>
public sealed record Shout(string Text) : INetMessage<Shout>
{
    public void Encode(NetWriter w) => w.String(Text);
    public static Shout Decode(ref NetReader r) => new(r.String(4096));
}

/// <summary>id 4 (JSON): answered with Count + 1.</summary>
public sealed record Counter(string Name, int Count);

/// <summary>Source-generated JSON metadata for Counter (no reflection: hot reload stays leak-free).</summary>
[JsonSerializable(typeof(Counter))]
internal sealed partial class NetTestJson : JsonSerializerContext;

/// <summary>id 5: test control from tools/nettest (lab only): "policy refuse|warn|off", "match exact|major|any",
/// "advertise on|off", "modpack &lt;url&gt; &lt;sha256&gt;|off", "advert", "dirhash &lt;folder under kiln/data/net-test&gt;",
/// "status".</summary>
public sealed record Control(string Command) : INetMessage<Control>
{
    public void Encode(NetWriter w) => w.String(Command);
    public static Control Decode(ref NetReader r) => new(r.String(256));
}

/// <summary>
/// Server half of the network tests. Opens net-test:echo v1 (required) and net-test:extra v2 (optional), answers
/// tools/nettest's fake clients, and logs PASS/FAIL for the codec unit checks and for what the fake clients did.
/// </summary>
public sealed class NetTestMod : Mod
{
    private int _pass, _fail;
    private NetChannel? _echo;
    private readonly HashSet<uint> _ready = new();

    private void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    public override void OnLoad()
    {
        var st = Network.Status;
        Log.Info($"net-test: Kiln.Network {(Network.Available ? "available" : "UNAVAILABLE")}, server role {st.ServerRole}, " +
                 $"lab {st.LabTransport}, max frame {st.MaxFrame}, policy {Network.Config.ModSync}");
        try { CodecChecks(); }
        catch (Exception e) { Check("codec", false, e.ToString()); }
        try { AdvertChecks.Run(Check, Context.DataDirectory); }
        catch (Exception e) { Check("advert", false, e.ToString()); }

        _echo = Network.Channel("net-test:echo", "1", required: true)
            .Register<Echo>((m, ctx) => ctx.Reply(new Echo("echo:" + m.Text)), NetDirection.ToServer)
            .Register<Blob>((m, ctx) =>
            {
                var crc = Crc.Of(m.Data);
                ctx.Reply(new BlobAck(m.Data.Length, crc));
                if (m.Reflect) ctx.Reply(new Blob(false, m.Data));
                Log.Info($"blob from {ctx.Sender.Name}: {m.Data.Length} bytes, crc {crc:x8}");
            }, NetDirection.ToServer)
            .Register<BlobAck>(null)
            .Register<Shout>((m, ctx) =>
            {
                var n = _echo!.SendToAll(new Shout("all:" + m.Text));
                Log.Info($"shout from {ctx.Sender.Name} sent to {n} client(s)");
            }, NetDirection.ToServer)
            .RegisterJson(NetTestJson.Default.Counter, (m, ctx) => ctx.Reply(new Counter(m.Name, m.Count + 1)), NetDirection.ToServer)
            .Register<Control>((m, ctx) => ctx.Reply(new Echo("control:" + RunControl(m.Command, ctx.Sender))), NetDirection.ToServer);
        Network.Channel("net-test:extra", "2", required: false).OnRaw((ctx, id, body) =>
            Log.Info($"extra raw message {id} ({body.Length} bytes) from {ctx.Sender.Name}"));
        Check("channel.open", Network.Channels.Any(c => c.Name == "net-test:echo") && Network.Channels.Any(c => c.Name == "net-test:extra"),
            string.Join(", ", Network.Channels));
        try
        {
            Network.Channel("net-test:echo", "9");
            Check("channel.duplicate-refused", false, "a second version opened");
        }
        catch (InvalidOperationException)
        {
            Check("channel.duplicate-refused", true);
        }
        Check("channel.same-returns-same", ReferenceEquals(Network.Channel("net-test:echo", "1"), _echo));
        Network.SetHandshakeProperty("net-test.hello", () => "server-" + Game.KilnVersion);
        Context.Tick.Pre += OnTick;
    }

    private string RunControl(string command, NetPeer sender)
    {
        var parts = command.Split(' ', 2);
        if (parts[0] == "status")
        {
            var s = Network.Status;
            return $"peers={s.Peers} ready={s.ReadyPeers} in={s.FramesIn} out={s.FramesOut} dropped={s.Dropped} pass={_pass} fail={_fail}";
        }
        if (parts[0] == "advert")
        {
            var a = Network.Advertised;
            return $"id={a.ServerId} name={a.ServerName} pending={a.Pending} modpack={a.Modpack?.Url ?? "-"} required=" +
                   string.Join(",", a.Required.Select(r => $"{r.Kind}:{r.Id}@{r.Version}={r.Hash}"));
        }
        if (parts[0].StartsWith("gate", StringComparison.Ordinal)) return RunGateControl(parts[0], parts.Length > 1 ? parts[1] : "", sender);
        if (parts.Length < 2) return "usage";
        if (parts[0] == "dirhash")
        {
            // a folder under kiln/data/net-test (tools/nettest copies schema/fixtures/dirhash/cases there)
            var rel = parts[1];
            if (rel.Contains("..")) return "bad path";
            try { return Kiln.DirHash.Compute(Path.Combine(Context.DataDirectory, rel)); }
            catch (Exception e) { return "error " + e.Message; }
        }
        if (parts[0] == "modhash")
        {
            // the same folders as a mod: its identity (a Kiln mod's assets/cooked left out)
            var rel = parts[1];
            if (rel.Contains("..")) return "bad path";
            try { return Kiln.DirHash.ModIdentity(Path.Combine(Context.DataDirectory, rel)); }
            catch (Exception e) { return "error " + e.Message; }
        }
        var path = NetworkConfig.FilePath!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var map = (File.Exists(path) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject : null) ?? new();
        if (parts[0] == "policy" && parts[1] == "default") map.Remove("modSync");
        else if (parts[0] == "policy") map["modSync"] = parts[1];
        else if (parts[0] == "match") map["versionMatch"] = parts[1];
        else if (parts[0] == "advertise") map["advertise"] = parts[1] == "on";
        else if (parts[0] == "modpack" && parts[1] == "off") map.Remove("modpack");
        else if (parts[0] == "modpack" && parts[1].Split(' ') is [var url, var sha])
            map["modpack"] = new System.Text.Json.Nodes.JsonObject { ["url"] = url, ["sha256"] = sha, ["name"] = "nettest pack", ["version"] = "7" };
        else return "unknown";
        File.WriteAllText(path, map.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Network.ReloadConfig();
        return $"{parts[0]}={parts[1]} -> {Network.Config.ModSync}/{Network.Config.VersionMatch} advertise={Network.Config.Advertise} modpack={Network.Config.Modpack?.Url ?? "-"}";
    }

    // ---- clients without Kiln (docs/kiln/network.md, tools/nettest t_vanilla) ----

    private readonly Dictionary<string, Task<EntityId>> _gateSpawns = new(StringComparer.Ordinal);

    private string RunGateControl(string op, string arg, NetPeer sender)
    {
        switch (op)
        {
            case "gate":
            {
                var g = NetNative.ReadGate();
                return $"mode={g.Mode} caps={g.Caps} held={g.Held} templates={g.KilnTemplates} slots={g.WithheldSlots} withheld={g.WithheldEntities} newer={g.NewerSlots}";
            }
            case "gate-bind":
            {
                // The sender stands for the player of a free slot: withholding and its content acks act on that slot.
                var slot = arg == "none" ? 0xFFFFFFFFul : ulong.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                return $"rc={NetNative.LabSelftest(NetNative.SelftestBind, sender.Id, slot, out _)} peer=0x{sender.Id:x}";
            }
            case "gate-q":
            {
                // "gate-q <slot> <entity>...": the slot's heap size, then the game's relevance step with each entity new in turn
                // and the size after each, all in this step.
                var a = arg.Split(' ').Select(x => ulong.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var rc = NetNative.LabSelftest(NetNative.SelftestHeap, a[0], 0, out var n);
                var sizes = new List<ulong> { n };
                foreach (var e in a.Skip(1))
                {
                    rc |= NetNative.LabSelftest(NetNative.SelftestQueue, a[0], e, out n);
                    sizes.Add(n);
                }
                return $"rc={rc} heap={string.Join(",", sizes)}";
            }
            case "gate-hotload":
                return StartHotLoad(arg);
            case "gate-hotload-state":
                return HotLoadState();
            case "gate-hotload-end":
                return EndHotLoad();
            case "gate-freeze":
                NetEngine.TestFreezeVanillaDecisions = arg == "on";
                return $"frozen={NetEngine.TestFreezeVanillaDecisions}";
            case "gate-audit":
            {
                // Every template the game's world knows, against what kiln.net says only Kiln has. A spawn by a name that misses
                // makes kiln.spawn read the game's list again: its catalog is from before the data packs' clones were registered.
                _ = Spawn.Entity("net-test-no-such-template", new WorldPosition(3805, 860, 1433)).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                var all = Spawn.Templates;
                var kiln = all.Where(t => NetNative.TemplateKilnOnly(t.Id) == 1).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var missed = all.Where(t => t.Name.StartsWith("Kiln_", StringComparison.Ordinal) && !kiln.Contains(t.Name)).Select(t => t.Name).ToList();
                return System.Text.Json.JsonSerializer.Serialize(new { total = all.Count, reported = NetNative.ReadGate().KilnTemplates, kiln, missed });
            }
            case "gate-spawn":
                _gateSpawns[arg] = Spawn.Entity(arg, new WorldPosition(3805, 860, 1433), 0, new SpawnOptions { Tag = "net-test" });
                return "started";
            case "gate-entity":
            {
                // The spawned entity, and every child of it or of anything else the game has spawned near it.
                if (!_gateSpawns.TryGetValue(arg, out var task) || !task.IsCompleted) return "pending";
                if (!task.IsCompletedSuccessfully) return "failed " + task.Exception?.GetBaseException().Message;
                var e = task.Result;
                var children = Spawn.FindByName("_", new WorldPosition(3805, 860, 1433), 60, 4096)
                    .Select(x => (Id: x, Info: Spawn.Inspect(x))).Where(x => x.Info is { Parent.IsNone: false }).ToList();
                var mine = children.Where(c => c.Info!.Parent == e).Select(c => NetNative.EntityWithheld(c.Id.Value)).ToList();
                var other = children.Where(c => c.Info!.Parent != e).Select(c => (Name: c.Info!.TemplateName ?? "?", W: NetNative.EntityWithheld(c.Id.Value))).ToList();
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = e.Value, withheld = NetNative.EntityWithheld(e.Value), children = mine,
                    otherChildren = other.Count, otherChildrenWithheld = other.Count(o => o.W == 1),
                    otherWithheldNames = other.Where(o => o.W == 1).Select(o => o.Name).Distinct().Take(8).ToList(),
                });
            }
            case "gate-hotreload":
            {
                // A template resource added and removed while the server runs. Junk bytes: nothing reads it after boot.
                var id = Guid.NewGuid();
                var key = new AssetKey(id, 0x39768775u);  // keen::ecs::TemplateResource
                var before = NetNative.ReadGate().KilnTemplates;
                var unknown = NetNative.TemplateKilnOnly(id);
                Assets.Register(key, new byte[256], mustBeNew: true);
                var added = NetNative.TemplateKilnOnly(id);
                var addedCount = NetNative.ReadGate().KilnTemplates;
                Assets.RemoveResource(key);
                return $"before={before} unknown={unknown} added={added} addedCount={addedCount} removed={NetNative.TemplateKilnOnly(id)} removedCount={NetNative.ReadGate().KilnTemplates}";
            }
            case "gate-guard":
            {
                // The game's own create reader (the server exe has the same one, unused) with a template nobody has.
                var missing = Guid.NewGuid();
                var vukah = Spawn.Lookup("Enemy_Vukah_Melee")!.Id;
                // A known template and no parent: the guard must leave it alone. The reader goes on through the call the hook moved, as
                // far as its message store, which a server world doesn't have: the game's assert there is caught and logged.
                var known = NetNative.LabSelftestCreate(vukah, 900000, 0, out _);
                var knownDropped = NetNative.ReadGate().DroppedCreates;
                var a = NetNative.LabSelftestCreate(missing, 900001, 0, out var ra);     // first: dropped, logged
                var b = NetNative.LabSelftestCreate(missing, 900002, 0, out var rb);     // second of the same template: dropped, no log
                var c = NetNative.LabSelftestCreate(vukah, 900003, 900001, out var rc);  // a known template, child of the first: dropped
                var d = NetNative.LabSelftestCreate(vukah, 900004, 900003, out var rd);  // grandchild: dropped
                var g = NetNative.ReadGate();
                return $"guid={missing} known={known},{knownDropped} rc={a},{b},{c},{d} results={ra},{rb},{rc},{rd} missing={g.MissingTemplates} dropped={g.DroppedCreates}";
            }
            case "gate-slots":
                return string.Join(",", Enumerable.Range(0, 16).Select(s => NetNative.LabSelftest(NetNative.SelftestSlot, (ulong)s, 0, out var first) == 0 ? first.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?"));
            case "gate-queue":
            {
                // The game's own relevance step on a free slot: what goes into a player's heap, kept back, then sent.
                const ulong slot = 15;
                var kiln = _gateSpawns[arg].Result.Value;
                ulong q(uint e) { NetNative.LabSelftest(NetNative.SelftestQueue, slot, e, out var n); return n; }
                var v1 = _gateSpawns["Enemy_Vukah_Melee"].Result.Value;
                NetNative.LabSelftest(NetNative.SelftestMask, 1ul << (int)slot, 0, out _);
                var start = q(v1);                 // withheld slot, a game entity: goes in
                var kept = q(kiln);                // withheld slot, a Kiln entity: stays out
                var before = NetNative.ReadGate().WithheldEntities;
                NetNative.LabSelftest(NetNative.SelftestMask, 0, 0, out _);
                // cleared: the next queueing, of another game entity, sends what was kept back
                var other = Spawn.FindByName("_", new WorldPosition(3805, 860, 1433), 200, 4096).First(e => e.Value != v1 && NetNative.EntityWithheld(e.Value) == 0).Value;
                var sent = q(other);
                return $"start={start} kept={kept} sent={sent} withheld={before}";
            }
            case "gate-noentity":
                return NetNative.EntityWithheld(uint.Parse(arg, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "gate-cleanup":
                foreach (var t in _gateSpawns.Values)
                    if (t.IsCompletedSuccessfully) Spawn.Destroy(t.Result);
                _gateSpawns.Clear();
                return "ok";
            default:
                return op.StartsWith("gate-admit", StringComparison.Ordinal) ? RunAdmissionControl(op, arg) : "unknown";
        }
    }

    // ---- NetAdmission's requirements, one of each kind (docs/kiln/network.md "Admission", tools/nettest t_admission) ----

    private AdmissionHold? _joins;
    private readonly Dictionary<string, AdmissionHold> _peerHolds = new(StringComparer.Ordinal);
    private IDisposable? _onJoin, _require;
    private bool _vanillaAllowed = true;

    private static uint Hex(string s) => uint.Parse(s.StartsWith("0x", StringComparison.Ordinal) ? s[2..] : s, System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture);

    private string RunAdmissionControl(string op, string arg)
    {
        var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (op)
            {
                case "gate-admit":
                {
                    var g = NetNative.ReadGate();
                    return $"mode={g.Mode} caps={g.Caps} held={g.Held} kilnholds={NetAdmission.CanHoldKilnPeers} active=" +
                           string.Join(";", NetAdmission.Active.Select(e => $"{e.Kind}:{e.Holder}{(e.Peer != 0 ? $"@0x{e.Peer:x}" : "")}"));
                }
                case "gate-admit-joins":  // <seconds> | end | refuse <reason>
                    if (a[0] == "end") { _joins?.End(); _joins = null; return "ok"; }
                    if (a[0] == "refuse") { _joins?.Refuse(string.Join(' ', a.Skip(1))); _joins = null; return "ok"; }
                    _joins = NetAdmission.HoldJoins("net-test", "net-test holds every join", TimeSpan.FromSeconds(double.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture)));
                    return "ok";
                case "gate-admit-hold":  // <peer> <holder> <seconds> | <peer> <holder> end
                {
                    var key = a[0] + "/" + a[1];
                    if (a[2] == "end")
                    {
                        if (_peerHolds.Remove(key, out var h)) h.End();
                        return "ok";
                    }
                    var peer = Network.Peers.FirstOrDefault(p => p.Id == Hex(a[0]));
                    if (peer is null) return "no such peer";
                    _peerHolds[key] = NetAdmission.HoldPeer(peer, a[1], $"{a[1]} waits for its acknowledgement", TimeSpan.FromSeconds(double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture)));
                    return _peerHolds[key].Ended ? "ended" : "ok";
                }
                case "gate-admit-onjoin":  // <seconds> | off
                    _onJoin?.Dispose();
                    _onJoin = null;
                    if (a[0] == "off") return "ok";
                    var secs = TimeSpan.FromSeconds(double.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture));
                    _onJoin = NetAdmission.OnJoin("net-test-join", p =>
                    {
                        var h = NetAdmission.HoldPeer(p, "net-test-join", "net-test-join waits for its acknowledgement", secs);
                        _peerHolds[$"0x{p.Id:x}/net-test-join"] = h;
                        return h;
                    });
                    return "ok";
                case "gate-admit-protocol":  // <name> <version> on|off
                {
                    var r = NetAdmission.SetMandatoryProtocol(a[0], a[1], a[2] == "on", $"{a[0]} protocol {a[1]} required (net-test)");
                    return r.Ok ? "ok" : "refused " + r.Reason;
                }
                case "gate-admit-require":  // allow | deny | off
                    _vanillaAllowed = a[0] == "allow";
                    if (a[0] == "off") { _require?.Dispose(); _require = null; }
                    else _require ??= NetAdmission.Require("net-test-vanilla", () => _vanillaAllowed, "net-test refuses clients without Kiln");
                    return "ok";
                case "gate-admit-peer":  // <peer>: its channel-12 frame counters, and kiln.net's flags for it
                {
                    var peer = Network.Peers.FirstOrDefault(p => p.Id == Hex(a[0]));
                    var flags = NetNative.Info(Hex(a[0]))?.Flags;
                    return peer is null ? "no such peer" : $"in={peer.FramesIn} out={peer.FramesOut} state={peer.State} flags={(flags is { } f ? $"0x{f:x}" : "none")}";
                }
                case "gate-admit-kickfail":  // 0 | 1 | 2: kiln.net's lab switch (1 the login failure fails, 2 the session kick too)
                {
                    var rc = NetNative.LabSelftest(NetNative.SelftestKickFail, ulong.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture), 0, out var can);
                    return $"rc={rc} disconnect={can}";
                }
                case "gate-admit-sessionkick":  // <machine handle, hex>: the game's session kick of the server's own machine index
                {
                    var rc = NetNative.LabSelftest(NetNative.SelftestSessionKick, Hex(a[0]), 0, out var queued);
                    return $"rc={rc} queued={queued}";
                }
                case "gate-admit-status":  // the Network page's admission lines
                    return string.Join(" | ", NetAdmission.StatusLines());
                case "gate-admit-clear":
                    _joins?.End();
                    _joins = null;
                    foreach (var h in _peerHolds.Values) h.End();
                    _peerHolds.Clear();
                    _onJoin?.Dispose();
                    _onJoin = null;
                    _require?.Dispose();
                    _require = null;
                    return "ok";
                default:
                    return "unknown";
            }
        }
        catch (Exception e) when (e is KilnUnavailableException or ArgumentException or FormatException or IndexOutOfRangeException)
        {
            return $"error {e.GetType().Name}: {e.Message}";
        }
    }

    // ---- a template added while Kiln clients are in, and its entity spawned at once (tools/nettest t_hotload) ----

    private string _hlMod = "";
    private Guid _hlTemplate;
    private bool _hlArmed;
    private ModOperationResult? _hlLoad;
    private long _hlRequestedMs, _hlSeenMs = -1;
    private ulong _hlSeenStep, _hlSpawnStep;
    private int _hlAttempts;
    private Task<EntityId>? _hlElite;
    private readonly List<Task<EntityId>> _hlGame = [];

    // "gate-hotload <mod folder> <template GUID>": game entities to compare with, then the mod's load; OnTick spawns the
    // template in the first step it exists.
    private string StartHotLoad(string arg)
    {
        var a = arg.Split(' ');
        if (a.Length != 2 || !Guid.TryParse(a[1], out var g)) return "usage";
        _hlMod = a[0];
        _hlTemplate = g;
        _hlLoad = null;
        _hlSeenMs = -1;
        _hlAttempts = 0;
        _hlElite = null;
        _hlGame.Clear();
        for (var i = 0; i < 3; i++)
            _hlGame.Add(Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(3805 + 3 * i, 860, 1440), 0, new SpawnOptions { Tag = "net-test", Flags = SpawnFlags.Transient }));
        var before = NetNative.TemplateKilnOnly(g);
        _hlRequestedMs = Environment.TickCount64;
        _hlArmed = true;
        Mods.Load(_hlMod, r => _hlLoad = r);
        return $"requested kilnOnly={before} templates={NetNative.ReadGate().KilnTemplates}";
    }

    private void HotLoadTick(ulong step)
    {
        if (!_hlArmed) return;
        if (_hlElite is { IsFaulted: true } && _hlAttempts < 250) _hlElite = null;  // kiln.spawn reads new templates at most every 5 s
        if (_hlElite != null || NetNative.TemplateKilnOnly(_hlTemplate) != 1) return;
        if (_hlSeenMs < 0)
        {
            _hlSeenMs = Environment.TickCount64;
            _hlSeenStep = step;
        }
        _hlAttempts++;
        _hlSpawnStep = step;
        _hlElite = Spawn.Entity(_hlTemplate, new WorldPosition(3805, 860, 1436), 0, new SpawnOptions { Tag = "net-test", Flags = SpawnFlags.Transient });
        Log.Info($"hot load: template {_hlTemplate} is there (step {step}); spawn {_hlAttempts}: {(_hlElite.IsCompletedSuccessfully ? "entity " + _hlElite.Result.Value : _hlElite.Status.ToString())}");
    }

    private string HotLoadState()
    {
        static uint Id(Task<EntityId>? t) => t is { IsCompletedSuccessfully: true } ? t.Result.Value : 0;
        return JsonSerializer.Serialize(new
        {
            load = _hlLoad is { } l ? (l.Ok ? "ok" : "failed: " + l.Message) : "pending",
            kilnOnly = NetNative.TemplateKilnOnly(_hlTemplate),
            seenAfterMs = _hlSeenMs < 0 ? -1 : _hlSeenMs - _hlRequestedMs,
            seenStep = _hlSeenStep, spawnStep = _hlSpawnStep, attempts = _hlAttempts,
            elite = Id(_hlElite), eliteError = _hlElite?.Exception?.GetBaseException().Message,
            game = _hlGame.Select(Id).ToList(),
        });
    }

    private string EndHotLoad()
    {
        _hlArmed = false;
        var n = (_hlElite is { IsCompletedSuccessfully: true } && Spawn.Destroy(_hlElite.Result) ? 1 : 0) +
                _hlGame.Count(t => t.IsCompletedSuccessfully && Spawn.Destroy(t.Result));
        _hlElite = null;
        _hlGame.Clear();
        if (_hlMod.Length > 0) Mods.Unload(_hlMod, r => Log.Info($"hot load: {_hlMod} unloaded: {r.Ok} {r.Message}"));
        return $"destroyed={n}";
    }

    private ulong _lastReport;

    private void OnTick(in TickInfo t)
    {
        HotLoadTick(t.Step);
        foreach (var p in Network.Peers)
        {
            if (p.State == NetPeerState.Ready && _ready.Add(p.Id))
                // a client that declared another version of the channel is ready but does not share it (and was warned)
                Check("handshake.ready", p.KilnVersion is not null && (p.HasChannel("net-test:echo") || p.Warnings.Any(w => w.Contains("net-test:echo"))),
                    $"{p} kiln {p.KilnVersion}, mods [{string.Join(", ", p.Mods.Select(m => m.Id + " " + m.Version))}], " +
                    $"props [{string.Join(", ", p.Properties.Select(kv => kv.Key + "=" + kv.Value))}], warnings {p.Warnings.Count}");
        }
        if (t.Step - _lastReport >= 1470)  // ~60 s
        {
            _lastReport = t.Step;
            var s = Network.Status;
            Log.Info($"net-test status: peers {s.Peers} (ready {s.ReadyPeers}), frames in/out {s.FramesIn}/{s.FramesOut}, " +
                     $"dropped {s.Dropped}, send failures {s.SendFailures}; echo in/out {_echo?.MessagesIn}/{_echo?.MessagesOut}; " +
                     $"PASS {_pass} FAIL {_fail}");
        }
    }

    [Subscribe]
    private void OnHandshake(NetClientHandshakeEvent e) =>
        Log.Info($"handshake event: {e.Peer.Name} missing [{string.Join("; ", e.Missing)}] extra [{string.Join("; ", e.Extra)}] refuse {e.Refuse}");

    [Subscribe]
    private void OnReady(NetPeerReadyEvent e) => Check("event.ready", e.Peer.State == NetPeerState.Ready, e.Peer.ToString());

    [Subscribe]
    private void OnLeft(NetPeerLeftEvent e)
    {
        _ready.Remove(e.Peer.Id);
        Log.Info($"left event: {e.Peer}");
    }

    [Subscribe]
    private void OnVanilla(NetVanillaClientEvent e) => Log.Info($"vanilla client: {e.Peer} kick {e.Kick}");

    private void CodecChecks()
    {
        var w = new NetWriter();
        w.Byte(7).Bool(true).UInt16(0xBEEF).Int32(-5).UInt64(ulong.MaxValue).Double(Math.PI).VarUInt(300).VarInt(-300)
            .String("héllo ✓").Bytes([1, 2, 3]).Guid(Guid.Parse("a56d2777-daf1-4164-90d4-b7b12749c723"))
            .Position(new WorldPosition(1.5, -2, 3e6)).Entity(new EntityId(1025));
        var r = new NetReader(w.WrittenSpan);
        var ok = r.Byte() == 7 && r.Bool() && r.UInt16() == 0xBEEF && r.Int32() == -5 && r.UInt64() == ulong.MaxValue &&
                 r.Double() == Math.PI && r.VarUInt() == 300 && r.VarInt() == -300 && r.String() == "héllo ✓" &&
                 r.Bytes().SequenceEqual(new byte[] { 1, 2, 3 }) &&
                 r.Guid() == Guid.Parse("a56d2777-daf1-4164-90d4-b7b12749c723") &&
                 r.Position3() == new WorldPosition(1.5, -2, 3e6) && r.Entity().Value == 1025 && r.End;
        Check("codec.roundtrip", ok, $"{w.Length} bytes");
        Check("codec.varint-size", new NetWriter().VarUInt(127).Length == 1 && new NetWriter().VarUInt(128).Length == 2);
        try
        {
            var bad = new NetReader(new byte[] { 0xFF, 0xFF });
            bad.String();
            Check("codec.truncated-throws", false);
        }
        catch (NetFormatException)
        {
            Check("codec.truncated-throws", true);
        }
        var c = Network.Config;
        Check("config.versions", c.VersionsMatch("1.2.0", "1.2.0") &&
                                 (c with { VersionMatch = ModVersionMatch.Major }).VersionsMatch("1.2.0", "1.9.1") &&
                                 !(c with { VersionMatch = ModVersionMatch.Major }).VersionsMatch("0.1.0", "0.2.0") &&
                                 !(c with { VersionMatch = ModVersionMatch.Major }).VersionsMatch("1.0.0", "2.0.0") &&
                                 (c with { VersionMatch = ModVersionMatch.Any }).VersionsMatch("1", "7"));
    }

    public override void OnUnload() => Log.Info($"net-test: PASS {_pass} FAIL {_fail}");
}

/// <summary>CRC-32 (IEEE, like zlib.crc32).</summary>
internal static class Crc
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Of(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
