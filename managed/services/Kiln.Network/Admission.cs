// SPDX-License-Identifier: MIT
// Kiln.Network's admission controller: the one place that sets kiln.net's gate, lets a held peer into the world or
// refuses a joining peer (docs/kiln/network.md "Admission"). Kiln's own services register requirements with it instead
// of deciding on their own:
//   - mandatory protocols (a channel and version every client must have),
//   - global join holds (no peer is let in while one lives),
//   - per-peer holds and OnJoin deciders (a peer is let in when every hold on it ended),
//   - vanilla conditions (a client without Kiln is let in only while each one allows it).
// Today's rules are built in, unchanged: the gate waits until the mod list is known; a client without Kiln is held
// while there is something to protect it from, then refused or let in (VanillaGate). With no requirement registered the
// gate behaves exactly as before this controller existed.
// Tick thread only, server role only: without a server role every call is a no-op that answers Ok.
namespace Kiln;

/// <summary>What a registration answered: <see cref="Ok"/>, or why not.</summary>
internal readonly record struct AdmissionResult(bool Ok, string? Reason)
{
    internal static readonly AdmissionResult Success = new(true, null);
}

internal enum AdmissionKind
{
    /// <summary>One of Kiln.Network's own rules (mod list not known, clients without Kiln).</summary>
    BuiltIn,
    MandatoryProtocol,
    JoinHold,
    PeerHold,
    OnJoin,
    VanillaCondition,
}

/// <summary>One requirement as status and tests see it. <paramref name="Peer"/> is 0 for a requirement on every join.</summary>
internal sealed record AdmissionEntry(AdmissionKind Kind, string Holder, string Reason, DateTime? Deadline, uint Peer);

/// <summary>A hold on joining peers (<see cref="NetAdmission.HoldJoins"/>) or on one peer (<see cref="NetAdmission.HoldPeer"/>).
/// It lives until <see cref="End"/>, <see cref="Refuse"/> or its deadline; Dispose ends it.</summary>
internal sealed class AdmissionHold : IDisposable
{
    internal AdmissionHold(string holder, string reason, DateTime deadline, NetPeer? peer)
    {
        Holder = holder;
        Reason = reason;
        Deadline = deadline;
        Peer = peer;
    }

    public string Holder { get; }
    public string Reason { get; }
    public DateTime Deadline { get; }

    /// <summary>The held peer; null for a hold on every join.</summary>
    public NetPeer? Peer { get; }

    public bool Ended { get; private set; }
    internal ModContext? Owner { get; } = NetModCalls.Caller;

    /// <summary>The holder no longer objects (its proof arrived). Idempotent.</summary>
    public void End()
    {
        if (Ended) return;
        Services.RequireTickThread("AdmissionHold.End");
        Ended = true;
        NetAdmission.HoldEnded(this, null);
    }

    /// <summary>The peer of a <see cref="NetAdmission.HoldPeer"/> hold is refused with <paramref name="reason"/>; a
    /// <see cref="NetAdmission.HoldJoins"/> hold refuses every peer it holds right now. The hold ends.</summary>
    public void Refuse(string reason)
    {
        if (Ended) return;
        Services.RequireTickThread("AdmissionHold.Refuse");
        Ended = true;
        NetAdmission.HoldEnded(this, string.IsNullOrWhiteSpace(reason) ? Reason : reason);
    }

    public void Dispose() => End();

    internal void MarkEnded() => Ended = true;
}

internal static class NetAdmission
{
    /// <summary>The longest a hold may last: under kiln.net's own 30 s limit for a held peer.</summary>
    internal static readonly TimeSpan MaxDeadline = TimeSpan.FromSeconds(25);

    // kiln_net.h: the gate mode that holds every peer, proved ones too, and its caps bit (kiln.net 0.2.0-dev.28)
    internal const uint GateHoldAll = NetNative.GateHoldAll, GateCanHoldAll = NetNative.GateCanHoldAll;

    private const string Me = "kiln.network";

    /// <summary>Capability rows: the controller decides joins here (a server role, kiln.net, net.gate), and kiln.net
    /// can hold Kiln clients (hold all).</summary>
    internal const string Row = "net.admission", HoldAllRow = "net.gate.hold_all";

    // refreshed every ~5 s: a game client gets its server role when it starts to host
    internal static readonly ServiceCapRows Rows = new(Row, ReportRows, everySteps: 125);

    // Composed from what is true now (kiln.net's role and gate caps), never from another row that a fresh report could
    // turn on: the members decide from the same live state, not from these rows.
    private static void ReportRows()
    {
        if (!NetNative.Available && TestGate is null)
        {
            ServiceCaps.Report(Row, CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.net is not loaded");
            ServiceCaps.Report(HoldAllRow, CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.net is not loaded");
            return;
        }
        if (!ServerRole)
        {
            ServiceCaps.Report(Row, CapabilityState.Unavailable, CapabilityReason.WrongSide, "this process serves no clients");
            ServiceCaps.Report(HoldAllRow, CapabilityState.Unavailable, CapabilityReason.WrongSide, "this process serves no clients");
            return;
        }
        var caps = Gate.Read().Caps;
        var gate = (caps & NetNative.GateCanHold) != 0;
        ServiceCaps.Static(Row, gate, CapabilityReason.SymbolMissing, "kiln.net cannot hold a joining peer on this build (net.gate)");
        ServiceCaps.Static(HoldAllRow, gate && (caps & GateCanHoldAll) != 0, gate ? CapabilityReason.ServiceOlder : CapabilityReason.SymbolMissing,
            gate ? "this kiln.net cannot hold Kiln clients (no gate mode hold-all)" : "kiln.net cannot hold a joining peer on this build (net.gate)");
    }

    /// <summary>Status lines for the Kiln menu's Network page: the requirements, each held peer and what keeps it, a
    /// refused peer the game's session kick ends or nothing can end (blocked), and the peers that wait for their content
    /// definitions (withholding, not admission).</summary>
    internal static IReadOnlyList<string> StatusLines()
    {
        var lines = new List<string>();
        var now = DateTime.UtcNow;
        var g = Gate.Read();
        lines.Add($"gate: {(g.Mode == GateHoldAll ? "hold all" : g.Mode == NetNative.GateHold ? "hold clients without Kiln" : "off")}, {g.Held} held");
        foreach (var e in Active)
            lines.Add($"{e.Kind}: {e.Holder}{(e.Peer != 0 ? $" on 0x{e.Peer:x}" : "")}: {e.Reason}" +
                      (e.Deadline is { } d ? $" ({Math.Max(0, (d - now).TotalSeconds):0} s left)" : ""));
        foreach (var p in NetEngine.ServerPeers())
        {
            if (p.HeldSince is { } since && !InWorld(p))
                lines.Add($"held: {p.Name} (0x{p.Id:x}) for {(now - since).TotalSeconds:0} s, " +
                          (p.State == NetPeerState.Rejected ? "refused" : MayRelease(p, out var h) ? "about to go in" : $"by {h}"));
            if (p.Ending)
                lines.Add($"ending: {p.Name} (0x{p.Id:x}) is refused; the login failure could not reach it, the game's session kick ends its connection");
            if (p.BlockedSince is { } blocked)
                lines.Add($"blocked: {p.Name} (0x{p.Id:x}) is refused and this server cannot end its connection: no world data until it leaves " +
                          (now - blocked < BlockedCountsFor ? $"(keeps \"hold all\" on {Math.Max(0, (BlockedCountsFor - (now - blocked)).TotalSeconds):0} s more)" : "(no longer holds the gate)"));
            if (p.DefinitionsSince is { } wait)
                lines.Add($"content: {p.Name} (0x{p.Id:x}) waits {(now - wait).TotalSeconds:0} s for its definitions (withholding)");
        }
        return lines;
    }

    private sealed class Protocol(string name, string version, string reason)
    {
        public readonly ModContext? Owner = NetModCalls.Caller;
        public readonly string Name = name, Version = version;
        public string Reason = reason;
    }

    private sealed class Decider(string holder, Func<NetPeer, AdmissionHold?> decide)
    {
        public readonly ModContext? Owner = NetModCalls.Caller;
        public readonly string Holder = holder;
        public readonly Func<NetPeer, AdmissionHold?> Decide = decide;
    }

    private sealed class Condition(string holder, Func<bool> allowed, string reason)
    {
        public readonly ModContext? Owner = NetModCalls.Caller;
        public readonly string Holder = holder, Reason = reason;
        public readonly Func<bool> Allowed = allowed;
    }

    private static readonly object Lock = new();
    private static readonly List<Protocol> Protocols = new();
    private static readonly List<AdmissionHold> JoinHolds = new();
    private static readonly List<AdmissionHold> PeerHolds = new();
    private static readonly List<Decider> Deciders = new();
    private static readonly List<Condition> Conditions = new();

    static NetAdmission()
    {
        NetModCalls.OnRetire(owner =>
        {
            AdmissionHold[] holds;
            lock (Lock)
            {
                Protocols.RemoveAll(p => ReferenceEquals(p.Owner, owner));
                Deciders.RemoveAll(d => ReferenceEquals(d.Owner, owner));
                Conditions.RemoveAll(c => ReferenceEquals(c.Owner, owner));
                holds = JoinHolds.Concat(PeerHolds).Where(h => ReferenceEquals(h.Owner, owner)).ToArray();
            }
            foreach (var h in holds) h.End();
            Changed();
        });
        var previous = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            Events.On<ServerStoppingEvent>(_ =>
            {
                _stopping = true;
                lock (Lock)
                {
                    foreach (var h in JoinHolds.Concat(PeerHolds)) h.MarkEnded();
                    JoinHolds.Clear();
                    PeerHolds.Clear();
                    PendingVanilla.Clear();
                    Released.Clear();
                    LoggedHolder.Clear();
                }
                Changed();
            });
            Events.On<ServerStartingEvent>(_ => { _stopping = false; Changed(); });
        }
        finally { ModContext.CurrentMod = previous; }
    }

    // the built-in rules as the last pass saw them (status)
    private static bool _modListKnown, _builtInRefuse;
    private static uint _kilnTemplates;
    private static uint _mode = uint.MaxValue;
    private static bool _stopping;

    // ---- the gate: every native gate, release and kick call of Kiln.Network goes through here ----

    /// <summary>kiln.net's gate as admission uses it; tests replace it.</summary>
    internal interface IGate
    {
        int Set(uint mode);
        int Release(uint peer);
        NetNative.GateInfo Read();
        int Kick(uint peer, uint reason);
    }

    private sealed class NativeGate : IGate
    {
        internal static readonly NativeGate Instance = new();
        public int Set(uint mode) => NetNative.GateSet(mode);
        public int Release(uint peer) => NetNative.GateRelease(peer);
        public NetNative.GateInfo Read() => NetNative.ReadGate();
        public int Kick(uint peer, uint reason) => NetNative.Kick(peer, reason);
    }

    /// <summary>Tests: the gate admission talks to (null: kiln.net).</summary>
    internal static IGate? TestGate { get; set; }

    private static IGate Gate => TestGate ?? NativeGate.Instance;

    internal static NetNative.GateInfo ReadGate() => Gate.Read();

    private static bool ServerRole =>
        NetEngine.TestServerRole ?? ((NetNative.ReadStatus()?.Roles ?? 0) & NetNative.RoleServer) != 0;

    /// <summary>The gate hook is there (kiln.net can hold a peer's Gameplay Ready).</summary>
    private static bool CanHold => (Gate.Read().Caps & NetNative.GateCanHold) != 0;

    /// <summary>kiln.net can hold a peer that proved it runs Kiln (gate mode "hold all"), and the gate hook is there.</summary>
    internal static bool CanHoldKilnPeers
    {
        get
        {
            if (!ServerRole) return false;
            var caps = Gate.Read().Caps;
            return (caps & NetNative.GateCanHold) != 0 && (caps & GateCanHoldAll) != 0;
        }
    }

    // ---- registration (tick thread) ----

    /// <summary>A channel and version every client must have: checked in the hello before Welcome, when a Ready client
    /// announces its channels again, and for a client that never says hello (it is refused). Not bypassed by
    /// <c>modSync</c>. Enabling it answers Ok=false while a connected peer lacks it (it is then not installed; see
    /// <see cref="PeersLacking"/>). Idempotent per (name, version); <paramref name="enabled"/> false removes it.</summary>
    internal static AdmissionResult SetMandatoryProtocol(string name, string version, bool enabled, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!ServerRole) return AdmissionResult.Success;
        Services.RequireTickThread("NetAdmission.SetMandatoryProtocol");
        NetModCalls.Watch(NetModCalls.Caller);
        reason = string.IsNullOrWhiteSpace(reason) ? $"{name} protocol {version} required" : reason;
        lock (Lock)
        {
            var have = Protocols.FirstOrDefault(p => p.Name == name && p.Version == version);
            if (!enabled)
            {
                if (have is null) return AdmissionResult.Success;
                Protocols.Remove(have);
            }
            else if (have is not null)
            {
                have.Reason = reason;
                return AdmissionResult.Success;
            }
        }
        if (enabled)
        {
            if (!CanHold) return new(false, "net.gate: kiln.net cannot hold a joining peer on this build");
            var lacking = PeersLacking(name, version);
            if (lacking.Count > 0)
                return new(false, $"peers lack it: {string.Join(", ", lacking.Take(8).Select(p => $"{p.Name} (0x{p.Id:x})"))}");
            lock (Lock) Protocols.Add(new Protocol(name, version, reason));
        }
        NetEngine.Log(LogLevel.Info, $"admission: mandatory protocol {name} v{version} {(enabled ? "on" : "off")}");
        Changed();
        return AdmissionResult.Success;
    }

    /// <summary>Connected peers that do not have channel <paramref name="name"/> at <paramref name="version"/>: clients
    /// without Kiln, and Kiln clients whose hello has not named it.</summary>
    internal static IReadOnlyList<NetPeer> PeersLacking(string name, string version)
    {
        if (!ServerRole) return [];
        Services.RequireTickThread("NetAdmission.PeersLacking");
        return NetEngine.ServerPeers().Where(p => !IsLocalMachine(p) && p.State is not (NetPeerState.Closed or NetPeerState.Rejected) &&
                                                !(p.Declared.TryGetValue(name, out var v) && v == version)).ToList();
    }

    /// <summary>No peer is let into the world while this hold lives (a held one stays held, a new one waits).</summary>
    /// <exception cref="KilnUnavailableException"><c>net.gate.hold_all</c>: kiln.net cannot hold Kiln clients.</exception>
    internal static AdmissionHold HoldJoins(string holder, string reason, TimeSpan deadline)
    {
        var h = NewHold(holder, reason, deadline, null, "NetAdmission.HoldJoins");
        if (h.Ended) return h;
        lock (Lock) JoinHolds.Add(h);
        NetEngine.Log(LogLevel.Info, $"admission: {holder} holds every join ({reason}), at most {deadline.TotalSeconds:0.#} s");
        Changed();
        return h;
    }

    /// <summary>Called once per joining peer after its hello passed, before it is let in: the decider may hold it
    /// (<see cref="HoldPeer"/>) and returns that hold, or null. Disposing the result unregisters it.</summary>
    /// <exception cref="KilnUnavailableException"><c>net.gate.hold_all</c>: kiln.net cannot hold Kiln clients.</exception>
    internal static IDisposable OnJoin(string holder, Func<NetPeer, AdmissionHold?> decide)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        ArgumentNullException.ThrowIfNull(decide);
        if (!ServerRole) return Nothing.Instance;
        Services.RequireTickThread("NetAdmission.OnJoin");
        RequireHoldAll();
        NetModCalls.Watch(NetModCalls.Caller);
        var d = new Decider(holder, decide);
        lock (Lock) Deciders.Add(d);
        Changed();
        return new Unregister(() =>
        {
            lock (Lock) Deciders.Remove(d);
            Changed();
        });
    }

    /// <summary>Keeps <paramref name="peer"/> out of the world until the hold ends; at its deadline the peer is refused
    /// with <paramref name="reason"/>. A peer already in the world is not held again (the hold answers ended).</summary>
    /// <exception cref="KilnUnavailableException"><c>net.gate.hold_all</c>: kiln.net cannot hold Kiln clients.</exception>
    internal static AdmissionHold HoldPeer(NetPeer peer, string holder, string reason, TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var h = NewHold(holder, reason, deadline, peer, "NetAdmission.HoldPeer");
        if (h.Ended) return h;
        if (IsLocalMachine(peer) || InWorld(peer) || peer.State is NetPeerState.Closed or NetPeerState.Rejected)
        {
            h.MarkEnded();  // the hosting player's own machine is never held; one in the world is not held again
            return h;
        }
        lock (Lock) PeerHolds.Add(h);
        NetEngine.Log(LogLevel.Info, $"admission: {holder} holds {peer.Name} (0x{peer.Id:x}) ({reason}), at most {deadline.TotalSeconds:0.#} s");
        Changed();
        return h;
    }

    /// <summary>A client without Kiln is let in only while <paramref name="vanillaAllowed"/> answers true; otherwise it is
    /// held on its Gameplay Ready and refused with <paramref name="reason"/>. Disposing the result removes it.</summary>
    internal static IDisposable Require(string holder, Func<bool> vanillaAllowed, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        ArgumentNullException.ThrowIfNull(vanillaAllowed);
        if (!ServerRole) return Nothing.Instance;
        Services.RequireTickThread("NetAdmission.Require");
        NetModCalls.Watch(NetModCalls.Caller);
        var c = new Condition(holder, vanillaAllowed, string.IsNullOrWhiteSpace(reason) ? $"{holder} refuses clients without Kiln" : reason);
        lock (Lock) Conditions.Add(c);
        Changed();
        return new Unregister(() =>
        {
            lock (Lock) Conditions.Remove(c);
            Changed();
        });
    }

    /// <summary>Every requirement there is now, the built-in ones first.</summary>
    internal static IReadOnlyList<AdmissionEntry> Active
    {
        get
        {
            var list = new List<AdmissionEntry>();
            if (!_modListKnown) list.Add(new(AdmissionKind.BuiltIn, Me, "the mod list is not known yet", null, 0));
            if (_builtInRefuse) list.Add(new(AdmissionKind.BuiltIn, Me, "clients without Kiln are refused", null, 0));
            else if (_kilnTemplates > 0) list.Add(new(AdmissionKind.BuiltIn, Me, "clients without Kiln wait for proof", null, 0));
            lock (Lock)
            {
                list.AddRange(Protocols.Select(p => new AdmissionEntry(AdmissionKind.MandatoryProtocol, p.Name, $"{p.Version}: {p.Reason}", null, 0)));
                list.AddRange(JoinHolds.Select(h => new AdmissionEntry(AdmissionKind.JoinHold, h.Holder, h.Reason, h.Deadline, 0)));
                list.AddRange(PeerHolds.Select(h => new AdmissionEntry(AdmissionKind.PeerHold, h.Holder, h.Reason, h.Deadline, h.Peer!.Id)));
                list.AddRange(Deciders.Select(d => new AdmissionEntry(AdmissionKind.OnJoin, d.Holder, "decides each join", null, 0)));
                list.AddRange(Conditions.Select(c => new AdmissionEntry(AdmissionKind.VanillaCondition, c.Holder, c.Reason, null, 0)));
            }
            return list;
        }
    }

    // ---- what the engine asks (tick thread) ----

    /// <summary>Whether a client without Kiln is refused: Kiln.Network's own rule (<paramref name="builtIn"/>), any
    /// mandatory protocol (it cannot have one), or a vanilla condition that answers false.</summary>
    internal static bool VanillaRefused(bool builtIn, out string? reason)
    {
        reason = null;
        if (builtIn) return true;
        lock (Lock)
        {
            if (Protocols.Count > 0)
            {
                reason = Protocols[0].Reason;
                return true;
            }
        }
        foreach (var c in ConditionsSnapshot())
        {
            bool allowed;
            allowed = false;
            NetModCalls.Invoke(c.Owner, () => allowed = c.Allowed(), $"admission: {c.Holder}'s vanilla condition");
            if (allowed) continue;
            reason = c.Reason;
            return true;
        }
        return false;
    }

    /// <summary>The reason a mandatory protocol refuses <paramref name="p"/>: the first one it does not declare at that
    /// exact version (its hello's channels); null when it has them all. The hosting player's own machine is never refused.</summary>
    internal static string? MissingProtocol(NetPeer p)
    {
        if (IsLocalMachine(p)) return null;
        lock (Lock)
            foreach (var x in Protocols)
                if (!(p.Declared.TryGetValue(x.Name, out var v) && v == x.Version)) return x.Reason;
        return null;
    }

    /// <summary>Whether some requirement needs each Kiln client decided before it enters the world, or a refused Kiln
    /// client still waits for its kick: "hold all" stays on until it is gone, so lowering the gate never lets it in. A
    /// refused peer nothing can end stops counting <see cref="BlockedCountsFor"/> after kiln.net reported it blocked:
    /// kiln.net itself keeps its Gameplay Ready out whatever the mode (KILN_NET_PEER_BLOCKED).</summary>
    internal static bool HoldsKilnPeers
    {
        get
        {
            lock (Lock)
                if (JoinHolds.Count > 0 || PeerHolds.Count > 0 || Deciders.Count > 0 || Protocols.Count > 0) return true;
            var now = DateTime.UtcNow;
            return NetEngine.ServerPeers().Any(p => (p.State == NetPeerState.Rejected || p.KickAt != null) && !NoLongerHolds(p, now));
        }
    }

    /// <summary>How long a blocked peer (refused, nothing can end its connection) keeps "hold all" on: kiln.net's own hold
    /// limit. After it only kiln.net's refusal keeps it out, which no mode lifts.</summary>
    internal static readonly TimeSpan BlockedCountsFor = TimeSpan.FromSeconds(30);

    private static bool NoLongerHolds(NetPeer p, DateTime now) => p.BlockedSince is { } b && now - b >= BlockedCountsFor;

    /// <summary>The gate mode the built-in rules and every requirement want, and sets it when it changed: "hold all"
    /// while a requirement must decide Kiln clients (when kiln.net can), else today's rule (VanillaGate.GateMode).</summary>
    internal static void UpdateGate(bool modListKnown, bool builtInRefuse, uint kilnTemplates)
    {
        _modListKnown = modListKnown;
        _builtInRefuse = builtInRefuse;
        _kilnTemplates = kilnTemplates;
        var refuse = VanillaRefused(builtInRefuse, out _);
        var mode = (_stopping || HoldsKilnPeers) && CanHoldKilnPeers ? GateHoldAll : VanillaGate.GateMode(modListKnown, refuse || _stopping, kilnTemplates);
        SetMode(mode);
    }

    /// <summary>The gate before the first server pass: hold until the mod list is known.</summary>
    internal static void Start() => UpdateGate(false, _builtInRefuse, _kilnTemplates);

    private static void SetMode(uint mode)
    {
        if (mode == _mode) return;
        if (Gate.Set(mode) == NetNative.Ok) _mode = mode;
    }

    /// <summary>A client without Kiln that waited its time in the gate: refused (REJECT, then kicked) when
    /// <paramref name="refuse"/>, else let in when it was held. One place decides it, with the reason.</summary>
    internal static void DecideVanilla(NetPeer p, bool refuse, string rejectReason, DateTime now)
    {
        if (_stopping) return;
        if (IncompleteHandshake(p))
        {
            refuse = true;
            rejectReason = "Your Kiln handshake did not complete before admission timed out. Try again.";
        }
        if (VanillaRefused(false, out var mandatoryReason))
        {
            refuse = true;
            rejectReason = mandatoryReason!;
        }
        var held = p.HeldSince != null;
        if (refuse)
        {
            // A vanilla game frees this frame unread; a Kiln build too old to prove itself shows the reason, so it gets
            // the time REJECT normally gets before the game's own login error.
            p.State = NetPeerState.Rejected;
            NetEngine.SendReject(p, rejectReason);
            p.KickAt = held ? now.AddSeconds(1.5) : now;
            NetEngine.KicksPending = true;
            Changed();
        }
        else if (held)
        {
            if (MayRelease(p, out _)) Release(p);
            else lock (Lock) PendingVanilla.Add(p.Id);  // let in once the holds that live now end
        }
    }

    private static readonly HashSet<uint> PendingVanilla = new();

    private static void Release(NetPeer p)
    {
        var rc = Gate.Release(p.Id);
        if (rc != NetNative.Ok) NetEngine.Log(LogLevel.Warn, $"letting {p.Name} (0x{p.Id:x}) in: rc {rc}");
        else
            lock (Lock)
            {
                Released.Add(p.Id);
                PendingVanilla.Remove(p.Id);
            }
    }

    // ---- joining Kiln clients (hold all) ----

    private static readonly Dictionary<uint, string> LoggedHolder = new();

    /// <summary>A Kiln client's hello passed and it got Welcome: every OnJoin decider is asked now, before it is let in.
    /// A decider that throws refuses it (fail closed). Tick thread.</summary>
    internal static void Joined(NetPeer p)
    {
        if (IsLocalMachine(p)) return;
        Decider[] ds;
        lock (Lock) ds = Deciders.ToArray();
        foreach (var d in ds)
        {
            if (!NetModCalls.Invoke(d.Owner, () => d.Decide(p), $"admission: {d.Holder}'s join decision"))
            {
                RefusePeer(p, "The server could not decide your join. Try again.", d.Holder);
                return;
            }
            if (p.State is NetPeerState.Rejected or NetPeerState.Closed || p.KickAt != null) return;
        }
    }

    /// <summary>Whether the engine must run <see cref="Step"/> this tick: a requirement holds Kiln clients, or a client
    /// without Kiln waits for holds to end.</summary>
    internal static bool StepNeeded
    {
        get
        {
            lock (Lock) return JoinHolds.Count > 0 || PeerHolds.Count > 0 || Deciders.Count > 0 || Protocols.Count > 0 || PendingVanilla.Count > 0;
        }
    }

    /// <summary>Every step while something can hold a peer: deadlines, then each held peer whose decision is made goes
    /// in (a Kiln client whose hello passed, a client without Kiln that was let in) once no hold is left on it. Under
    /// "hold all" this is the only way a held peer enters the world. Tick thread.</summary>
    internal static void Step(DateTime now)
    {
        if (_stopping) return;
        Expire(now);
        var holdAll = Gate.Read().Mode == GateHoldAll;
        foreach (var p in NetEngine.ServerPeers())
        {
            if (p.HeldSince == null || InWorld(p) || p.KickAt != null || p.State is NetPeerState.Rejected or NetPeerState.Closed) continue;
            bool pending;
            lock (Lock) pending = PendingVanilla.Contains(p.Id);
            if (!(pending || (holdAll && p.State == NetPeerState.Ready))) continue;
            if (pending && IncompleteHandshake(p))
            {
                RefusePeer(p, "Your Kiln handshake did not complete before admission timed out. Try again.", Me);
                continue;
            }
            if (pending && VanillaRefused(_builtInRefuse, out var reason))
            {
                RefusePeer(p, reason ?? VanillaGate.RefusalReason(0), Me);
                continue;
            }
            if (MayRelease(p, out var holder))
            {
                var since = p.HeldSince.Value;
                Release(p);
                if (InWorld(p)) NetEngine.Log(LogLevel.Info, $"admission: {p.Name} (0x{p.Id:x}) let in after {(now - since).TotalMilliseconds:0} ms held");
                lock (Lock) LoggedHolder.Remove(p.Id);
                continue;
            }
            bool fresh;
            lock (Lock) fresh = !LoggedHolder.TryGetValue(p.Id, out var was) || was != holder;
            if (!fresh) continue;
            lock (Lock) LoggedHolder[p.Id] = holder!;
            NetEngine.Log(LogLevel.Info, $"admission: {(p.State == NetPeerState.Ready ? "proved" : "vanilla")} peer {p.Name} (0x{p.Id:x}) held by {holder}");
        }
    }

    /// <summary>The kick of a refused peer, at its time: the one place Kiln.Network calls kiln.net's kick.</summary>
    internal static int Kick(NetPeer p, uint reason) => Gate.Kick(p.Id, reason);

    private static bool IncompleteHandshake(NetPeer p) =>
        p.State != NetPeerState.Ready && HoldsKilnPeers && ((NetNative.Info(p.Id)?.Flags ?? 0) & NetNative.PeerProved) != 0;

    /// <summary>Whether no hold keeps <paramref name="p"/> out right now (<paramref name="holder"/>: the first that does).</summary>
    internal static bool MayRelease(NetPeer p, out string? holder)
    {
        lock (Lock)
        {
            holder = JoinHolds.FirstOrDefault()?.Holder ?? PeerHolds.FirstOrDefault(h => ReferenceEquals(h.Peer, p))?.Holder;
            return holder is null;
        }
    }

    // ---- holds ----

    private static AdmissionHold NewHold(string holder, string reason, TimeSpan deadline, NetPeer? peer, string api)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        if (deadline <= TimeSpan.Zero || deadline > MaxDeadline)
            throw new ArgumentOutOfRangeException(nameof(deadline), deadline, $"a hold lasts more than 0 and at most {MaxDeadline.TotalSeconds:0} s");
        reason = string.IsNullOrWhiteSpace(reason) ? $"held by {holder}" : reason;
        var h = new AdmissionHold(holder, reason, DateTime.UtcNow + deadline, peer);
        if (!ServerRole)
        {
            h.MarkEnded();
            return h;
        }
        Services.RequireTickThread(api);
        RequireHoldAll();
        NetModCalls.Watch(h.Owner);
        return h;
    }

    private static void RequireHoldAll()
    {
        var caps = Gate.Read().Caps;
        if ((caps & NetNative.GateCanHold) == 0)
            throw new KilnUnavailableException("net.gate", "kiln.net cannot hold a joining peer on this build");
        if ((caps & GateCanHoldAll) == 0)
            throw new KilnUnavailableException("net.gate.hold_all", "kiln.net cannot hold Kiln clients on this build");
    }

    /// <summary>A hold ended (refuse: with that reason).</summary>
    internal static void HoldEnded(AdmissionHold h, string? refuse)
    {
        bool had;
        lock (Lock) had = h.Peer is null ? JoinHolds.Remove(h) : PeerHolds.Remove(h);
        if (!had) return;
        if (refuse is not null)
        {
            if (h.Peer is { } one) RefusePeer(one, refuse, h.Holder);
            else
                foreach (var p in NetEngine.ServerPeers().Where(p =>
                             ((NetNative.Info(p.Id)?.Flags ?? 0) & NetNative.PeerHeld) != 0 && !InWorld(p)))
                    RefusePeer(p, refuse, h.Holder);
        }
        else
        {
            NetEngine.Log(LogLevel.Info, h.Peer is { } p ? $"admission: {h.Holder} no longer holds {p.Name} (0x{p.Id:x})" : $"admission: {h.Holder} no longer holds joins");
        }
        Changed();
    }

    /// <summary>Refuses <paramref name="p"/>: a REJECT with the reason (a Kiln client shows it), then the kick.</summary>
    internal static void RefusePeer(NetPeer p, string reason, string holder)
    {
        if (p.State is NetPeerState.Closed || p.KickAt != null) return;
        NetEngine.Log(LogLevel.Warn, $"admission: refusing {p.Name} (0x{p.Id:x}) for {holder}: {reason}");
        p.State = NetPeerState.Rejected;
        NetEngine.SendReject(p, reason);
        p.KickAt = DateTime.UtcNow.AddSeconds(1.5);
        NetEngine.KicksPending = true;
        lock (Lock)
        {
            foreach (var h in PeerHolds.Where(h => ReferenceEquals(h.Peer, p))) h.MarkEnded();
            PeerHolds.RemoveAll(h => ReferenceEquals(h.Peer, p));
        }
        Changed();
    }

    /// <summary>Deadlines: a peer hold that passed its own refuses its peer with the holder's reason; a join hold that
    /// passed its own ends, with an error line. Never extended. Tick thread.</summary>
    internal static void Expire(DateTime now)
    {
        AdmissionHold[] due;
        lock (Lock) due = JoinHolds.Concat(PeerHolds).Where(h => now >= h.Deadline).ToArray();
        foreach (var h in due)
        {
            if (h.Peer is null)
            {
                NetEngine.Log(LogLevel.Error, $"admission: {h.Holder}'s hold on every join passed its deadline ({h.Reason}): it ends");
                h.End();
            }
            else
            {
                h.Refuse(h.Reason);
            }
        }
    }

    /// <summary>A peer left: its holds go.</summary>
    internal static void PeerLeft(NetPeer p)
    {
        lock (Lock)
        {
            foreach (var h in PeerHolds.Where(h => ReferenceEquals(h.Peer, p))) h.MarkEnded();
            PeerHolds.RemoveAll(h => ReferenceEquals(h.Peer, p));
            Released.Remove(p.Id);
            PendingVanilla.Remove(p.Id);
            LoggedHolder.Remove(p.Id);
        }
        Changed();  // a refused peer that kept "hold all" on is gone
    }

    // peers admission let into the world (gate_release answered ok)
    private static readonly HashSet<uint> Released = new();

    /// <summary>Whether <paramref name="p"/> is in the world: its player record is in game, or admission let it in.</summary>
    internal static bool InWorld(NetPeer p)
    {
        if (p.InGameSince != null) return true;
        lock (Lock) return Released.Contains(p.Id);
    }

    private static Condition[] ConditionsSnapshot()
    {
        lock (Lock) return Conditions.ToArray();
    }

    /// <summary>A requirement came or went: the gate's mode follows at once (a proved peer must not slip through before
    /// "hold all" is on), and the engine's next tick runs a full pass.</summary>
    private static void Changed()
    {
        NetEngine.AdmissionChanged = true;
        if (ServerRole) UpdateGate(_modListKnown, _builtInRefuse, _kilnTemplates);
    }

    internal static bool IsLocalMachine(NetPeer p) => !p.IsLab && !p.IsServer && (p.Id & 0x7F) == 0;

    /// <summary>Tests: forget every requirement and the mode.</summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            Protocols.Clear();
            JoinHolds.Clear();
            PeerHolds.Clear();
            Deciders.Clear();
            Conditions.Clear();
            Released.Clear();
            PendingVanilla.Clear();
            LoggedHolder.Clear();
        }
        _mode = uint.MaxValue;
        _modListKnown = _builtInRefuse = false;
        _kilnTemplates = 0;
        _stopping = false;
    }

    private sealed class Unregister(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            if (_dispose is null) return;
            Services.RequireTickThread("NetAdmission registration.Dispose");
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }

    private sealed class Nothing : IDisposable
    {
        internal static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
