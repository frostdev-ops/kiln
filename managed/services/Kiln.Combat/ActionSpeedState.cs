// SPDX-License-Identifier: MIT
// Kiln.ActionSpeed's state: each mod's values (ModContext), their product (the effective view), the native table
// (kiln.combat.speed, which applies them) and the kiln:speed relay (ActionSpeedRelay). docs/kiln/services/combat.md,
// "Action speed".
//
// Server or host: the view is computed from the mods' values; Kiln pushes it to the native table and sends it to joined
// Kiln clients. A joined client: the view is the server's (ActionSpeedRelay), pushed to its own native table, which
// applies it to the local player's prediction and presentation only; its own mods cannot write.
namespace Kiln;

internal static unsafe class ActionSpeedState
{
    public const int Kinds = 3;

    /// <summary>An effective view: the rates every player without values of their own has, and the players with.</summary>
    internal sealed record View(double[] Defaults, IReadOnlyDictionary<uint, double[]> Players)
    {
        public static readonly View Empty = new([1, 1, 1], new Dictionary<uint, double[]>());

        public double Rate(uint player, int kind) =>
            Players.TryGetValue(player, out var r) ? r[kind] : Defaults[kind];

        public bool IsIdentity => Defaults.All(d => d == 1) && Players.Values.All(r => r.All(x => x == 1));
    }

    private sealed class Values
    {
        public readonly double?[] Default = new double?[Kinds];
        public readonly Dictionary<uint, double?[]> Players = new();
        public bool Empty => Default.All(d => d is null) && Players.Count == 0;
    }

    private static readonly object Gate = new();
    private static readonly object NoMod = new();  // values set outside any mod's code
    private static readonly Dictionary<object, Values> ByMod = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static List<(Action<ActionSpeedChanged> Fn, ModContext? Mod)> _handlers = [];
    private static View _computed = View.Empty, _remote = View.Empty, _applied = View.Empty;
    private static bool _joined, _warnedNative;
    private static IDisposable? _leftSub, _tick;

    // ---- seams (tests) ----
    internal static Func<bool> IsJoinedClient = static () => Client.Available && Client.IsRemote;
    internal static Func<IEnumerable<uint>> Roster = DefaultRoster;

    private static IEnumerable<uint> DefaultRoster()
    {
        var out_ = new List<uint>();
        try
        {
            if (Kiln_Players.Available) out_.AddRange(global::Kiln.Players.All.Select(p => p.Entity.Value));
        }
        catch (Exception)
        {
            // the roster is a convenience for Changed; a missing one only narrows it
        }
        try
        {
            if (Client.Available && Client.HasLocalPlayer) out_.Add(Client.LocalPlayer.Value);
        }
        catch (Exception)
        {
        }
        return out_;
    }

    private static class Kiln_Players
    {
        public static bool Available
        {
            get
            {
                try
                {
                    return global::Kiln.Players.Available;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }

    // ---- reads ----

    public static View Current
    {
        get
        {
            lock (Gate) return _joined ? _remote : _computed;
        }
    }

    public static double Get(EntityId player, ActionKind kind)
    {
        var k = (int)kind;
        if (k is < 0 or >= Kinds || !player.IsPlayer) return 1.0;
        return Current.Rate(player.Value, k);
    }

    public static bool Available
    {
        get
        {
            var s = SpeedNative.Read();
            const uint need = SpeedNative.Sequence | SpeedNative.Graph | SpeedNative.Wraps;
            return s is { } v && (v.Parts & need) == need;
        }
    }

    public static ActionSpeedStatus ReadStatus()
    {
        var s = SpeedNative.Read();
        if (s is not { } v)
            return new ActionSpeedStatus(false, false, false, false, false,
                SpeedNative.T == null && Services.Get<CombatNative.Table>(CombatNative.Name, CombatNative.Version) != null
                    ? "this kiln-combat.dll has no kiln.combat.speed (update Kiln)"
                    : "kiln.combat.speed is not loaded");
        var wraps = (v.Parts & SpeedNative.Wraps) != 0;
        var seq = wraps && (v.Parts & SpeedNative.Sequence) != 0;
        var graph = wraps && (v.Parts & SpeedNative.Graph) != 0;
        var bow = wraps && (v.Parts & SpeedNative.Bow) != 0;
        var client = global::Kiln.Mods.Side == ModSide.Client;
        var prediction = client && seq && (v.Parts & SpeedNative.Prediction) != 0 && (v.Parts & SpeedNative.Presentation) != 0;
        var reasons = new List<string>();
        var detail = SpeedNative.Detail(v);
        if (detail.Length > 0) reasons.Add(detail);
        if (!client) reasons.Add("a dedicated server has no prediction: its rates go to joined clients, which apply them there");
        return new ActionSpeedStatus(seq, graph, bow, false, prediction, reasons.Count == 0 ? null : string.Join("; ", reasons));
    }

    // ---- writes (server or host, tick thread) ----

    public static void Write(string api, ActionKind kind, EntityId? player, double? multiplier)
    {
        Refuse(api);
        var k = (int)kind;
        if (k is < 0 or >= Kinds) throw new ArgumentOutOfRangeException(nameof(kind), kind, $"ActionSpeed.{api}: not an action kind");
        if (multiplier is { } m && (!double.IsFinite(m) || m < ActionSpeed.Min || m > ActionSpeed.Max))
            throw new ArgumentOutOfRangeException(nameof(multiplier), m,
                $"ActionSpeed.{api}: a multiplier is {ActionSpeed.Min} to {ActionSpeed.Max} (1.0 is the game's timing)");
        if (player is { } p && !p.IsPlayer)
            throw new ArgumentException($"ActionSpeed.{api}: {p} is not a player entity (1 to 1023); action speed is for players in this version", nameof(player));
        Services.RequireTickThread($"ActionSpeed.{api}");
        var mod = ModContext.CurrentMod;
        lock (Gate)
        {
            Watch(mod);
            var key = (object?)mod ?? NoMod;
            if (!ByMod.TryGetValue(key, out var v))
            {
                if (multiplier is null) return;  // nothing of this mod to clear
                ByMod[key] = v = new Values();
            }
            if (player is { } pl)
            {
                if (!v.Players.TryGetValue(pl.Value, out var r))
                {
                    if (multiplier is null) return;
                    v.Players[pl.Value] = r = new double?[Kinds];
                }
                r[k] = multiplier;
                if (r.All(x => x is null)) v.Players.Remove(pl.Value);
            }
            else
            {
                v.Default[k] = multiplier;
            }
            if (v.Empty) ByMod.Remove(key);
        }
        Apply();
    }

    public static void ClearPlayer(EntityId player)
    {
        Refuse("Clear");
        Services.RequireTickThread("ActionSpeed.Clear");
        var mod = ModContext.CurrentMod;
        lock (Gate)
        {
            var key = (object?)mod ?? NoMod;
            if (!ByMod.TryGetValue(key, out var v) || !v.Players.Remove(player.Value)) return;
            if (v.Empty) ByMod.Remove(key);
        }
        Apply();
    }

    private static void Refuse(string api)
    {
        if (IsJoinedClient())
            throw new InvalidOperationException(
                $"ActionSpeed.{api}: a joined client cannot set action speeds; the host or the server sets them, and Kiln sends them to this client");
    }

    // ---- the product ----

    internal static View Compute()
    {
        lock (Gate) return ComputeLocked();
    }

    private static View ComputeLocked()
    {
        var defaults = new double[Kinds];
        for (var k = 0; k < Kinds; k++)
        {
            var p = 1.0;
            foreach (var v in ByMod.Values) p *= v.Default[k] ?? 1.0;
            defaults[k] = Clamp(p);
        }
        var players = new Dictionary<uint, double[]>();
        foreach (var e in ByMod.Values.SelectMany(v => v.Players.Keys).Distinct())
        {
            var r = new double[Kinds];
            for (var k = 0; k < Kinds; k++)
            {
                var p = 1.0;
                foreach (var v in ByMod.Values)
                    p *= v.Players.TryGetValue(e, out var own) && own[k] is { } x ? x : v.Default[k] ?? 1.0;
                r[k] = Clamp(p);
            }
            players[e] = r;
        }
        return new View(defaults, players);
    }

    private static double Clamp(double v) => Math.Clamp(v, ActionSpeed.Min, ActionSpeed.Max);

    /// <summary>Recomputes, pushes the view that applies here to the native table, sends a server's or host's to its
    /// clients, and raises Changed for what changed.</summary>
    internal static void Apply()
    {
        View next;
        lock (Gate)
        {
            _computed = ComputeLocked();
            _joined = IsJoinedClient();
            next = _joined ? _remote : _computed;
        }
        Publish(next);
        if (!_joined) ActionSpeedRelay.Broadcast(next);
        EnsureWatchers();
    }

    /// <summary>A joined client: the server's view arrived (or went: <see cref="View.Empty"/>).</summary>
    internal static void SetRemote(View view)
    {
        View next;
        lock (Gate)
        {
            _remote = view;
            _joined = IsJoinedClient();
            next = _joined ? _remote : _computed;
        }
        Publish(next);
    }

    private static void Publish(View next)
    {
        View before;
        lock (Gate)
        {
            before = _applied;
            _applied = next;
        }
        PushNative(next);
        RaiseChanged(before, next);
    }

    private static void PushNative(View v)
    {
        var t = SpeedNative.T;
        if (t == null || t->SetRates == null) return;
        var defaults = stackalloc float[Kinds];
        for (var k = 0; k < Kinds; k++) defaults[k] = (float)v.Defaults[k];
        var list = v.Players.Take(64).Select(kv =>
        {
            var r = new SpeedNative.Rate { Entity = kv.Key };
            for (var k = 0; k < Kinds; k++) r.Rates[k] = (float)kv.Value[k];
            return r;
        }).ToArray();
        int rc;
        fixed (SpeedNative.Rate* p = list) rc = t->SetRates(defaults, p, (uint)list.Length);
        if (rc != SpeedNative.Ok && !_warnedNative)
        {
            _warnedNative = true;
            SpeedLog.Warn(rc == SpeedNative.ErrUnavailable
                ? $"the rates are kept but nothing is scaled in this game: {ReadStatus().Reason}"
                : $"kiln.combat.speed refused the rates (error {rc})");
        }
    }

    private static void RaiseChanged(View before, View after)
    {
        List<(Action<ActionSpeedChanged> Fn, ModContext? Mod)> handlers;
        lock (Gate) handlers = _handlers;
        if (handlers.Count == 0) return;
        var players = new HashSet<uint>(before.Players.Keys);
        players.UnionWith(after.Players.Keys);
        foreach (var e in Roster()) if (e is >= 1 and <= 1023) players.Add(e);
        foreach (var e in players.Order())
            for (var k = 0; k < Kinds; k++)
            {
                var now = after.Rate(e, k);
                if (now == before.Rate(e, k)) continue;
                var ev = new ActionSpeedChanged(new EntityId(e), (ActionKind)k, now);
                foreach (var (fn, mod) in handlers)
                {
                    var outer = ModContext.CurrentMod;
                    ModContext.CurrentMod = mod;
                    try
                    {
                        fn(ev);
                    }
                    catch (Exception ex)
                    {
                        SpeedLog.Warn($"a Changed handler{(mod is null ? "" : $" of {mod.Info.Id}")} threw: {ex.Message}");
                    }
                    finally
                    {
                        ModContext.CurrentMod = outer;
                    }
                }
            }
    }

    // ---- handlers, unload, departures ----

    public static void AddHandler(Action<ActionSpeedChanged>? fn)
    {
        if (fn is null) return;
        var mod = ModContext.CurrentMod;
        lock (Gate)
        {
            Watch(mod);
            _handlers = [.. _handlers, (fn, mod)];
        }
        ActionSpeedRelay.Start();
    }

    public static void RemoveHandler(Action<ActionSpeedChanged>? fn)
    {
        if (fn is null) return;
        lock (Gate)
        {
            var i = _handlers.FindLastIndex(h => h.Fn == fn);
            if (i >= 0) _handlers = [.. _handlers.Take(i), .. _handlers.Skip(i + 1)];
        }
    }

    private static void Watch(ModContext? mod)
    {
        if (mod is null || !Watched.Add(mod)) return;
        mod.Retiring += () => Retire(mod);
    }

    /// <summary>A mod unloads: its values and handlers go.</summary>
    internal static void Retire(ModContext mod)
    {
        bool had;
        lock (Gate)
        {
            Watched.Remove(mod);
            _handlers = _handlers.Where(h => !ReferenceEquals(h.Mod, mod)).ToList();
            had = ByMod.Remove(mod);
        }
        if (had) Apply();
    }

    /// <summary>A player left: every mod's values for that entity go (a later player may get the same id).</summary>
    internal static void Departed(uint entity)
    {
        var any = false;
        lock (Gate)
            foreach (var key in ByMod.Keys.ToList())
            {
                var v = ByMod[key];
                any |= v.Players.Remove(entity);
                if (v.Empty) ByMod.Remove(key);
            }
        if (any) Apply();
    }

    private static void EnsureWatchers()
    {
        lock (Gate)
        {
            _leftSub ??= Events.On<PlayerLeftEvent>(static e => Departed(e.Player.Entity.Value));
            // a client that joins, leaves or hosts: the view that applies here changes with it
            _tick ??= ServiceTick.Add("kiln action speed", static (in TickInfo _) => CheckSide());
        }
    }

    private static void CheckSide()
    {
        bool joined;
        try
        {
            joined = IsJoinedClient();
        }
        catch (Exception)
        {
            return;
        }
        View next;
        lock (Gate)
        {
            if (joined == _joined) return;
            _joined = joined;
            if (!joined) _remote = View.Empty;
            next = joined ? _remote : _computed;
        }
        Publish(next);
    }

    /// <summary>Tests: forget everything (values, handlers, views).</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            ByMod.Clear();
            Watched.Clear();
            _handlers = [];
            _computed = _remote = _applied = View.Empty;
            _joined = _warnedNative = false;
            _leftSub?.Dispose();
            _tick?.Dispose();
            _leftSub = _tick = null;
        }
    }
}

internal static class SpeedLog
{
    public static void Info(string text) => Backend.Current.Log(0, LogLevel.Info, "action speed: " + text);
    public static void Warn(string text) => Backend.Current.Log(0, LogLevel.Warn, "action speed: " + text);
    public static void Debug(string text) => Backend.Current.Log(0, LogLevel.Debug, "action speed: " + text);
}
