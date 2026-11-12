// SPDX-License-Identifier: MIT
// kiln.players test mod. No client can join the lab, so it checks everything that needs no player:
//  - the service is up with the records source, and its self-checks (singleton map, GameServer -> world) passed;
//  - the player list is empty, and every raw record / BasicPlayerInfos entry is free (the layout reads sane values);
//  - lookups of absent players return null; reads work off the tick thread, raw reads refuse it;
//  - the game-log tracker, run over real join/leave lines (fixtures/, from a live server, names and Steam ids
//    replaced), finds the right slots, entities, names, Steam ids, permissions and events, in the log file's form and
//    in the mirror's (no prefix, the permission list as one message);
//  - no spurious Joined/Left while the server runs empty; handlers go away on unload (run `reload-test players-test 3`
//    from kiln/control to check that the mod's load context is collected);
//  - identity.players (PlayerIdentity.cs): PlayerRef.Of throws KilnUnavailableException in OnLoad and works from the
//    first tick; with KILN_PLAYERS_SIMULATE=...\fixtures\slot-reuse.log a player joins, leaves, another joins into the
//    same slot and the first rejoins elsewhere: the refs' stamps, IsOnline (through the players' entity source that
//    Kiln.Players registers at startup), IsSamePerson and Resolve.
// Output: "players-test: PASS <what>" / "players-test: FAIL <what>: <why>" lines in kiln/logs/kiln.log, then a summary.
using Kiln;

namespace PlayersTest;

public sealed class PlayersTestMod : Mod
{
    private int _pass, _fail;
    private int _joined, _left;
    private ulong _start;
    private bool _done;

    // identity.players
    private string _onLoadOf = "", _firstTickOf = "";
    private bool _slotReuse;
    private readonly List<(string Name, PlayerRef Of, PlayerRef Cast)> _refs = new();

    public override void OnLoad()
    {
        Log.Info($"players-test: loading (kiln.players available: {Players.Available})");
        _onLoadOf = TryOf(new EntityId(1));
        _slotReuse = (Environment.GetEnvironmentVariable("KILN_PLAYERS_SIMULATE") ?? "").Contains("slot-reuse", StringComparison.OrdinalIgnoreCase);
        if (Players.Available)
        {
            Players.Joined += OnJoined;
            Players.Left += OnLeft;
        }
        Context.Tick.Pre += OnPre;
    }

    private readonly List<(bool Joined, Player Player, string Check)> _seen = new();

    private void OnJoined(Player p)
    {
        _joined++;
        if (_slotReuse)
        {
            try { SlotReuseJoined(p); }
            catch (Exception e) { Fail($"identity at join of {p.Name}", e.ToString()); }
        }
        Log.Info($"players-test: event Joined {p} slot {p.Slot} pos {p.Position} perms {p.Permissions} admin {p.IsAdmin}");
        // While the handler runs the snapshot already has the player.
        var byName = Players.Find(p.Name.ToUpperInvariant());
        var bySteam = p.SteamId != 0 ? Players.FindBySteamId(p.SteamId) : null;
        var check = $"find-name {byName?.Entity}, find-steam {bySteam?.Entity}, in All {Players.All.Any(x => x.Entity == p.Entity)}, " +
                    $"IsPlayer {Players.IsPlayer(p.Entity)}";
        _seen.Add((true, p, check));
    }

    private void OnLeft(Player p)
    {
        _left++;
        Log.Info($"players-test: event Left {p} slot {p.Slot}");
        _seen.Add((false, p, $"still in All {Players.All.Any(x => x.Entity == p.Entity && x.InGame)}"));
    }

    private void OnPre(in TickInfo tick)
    {
        if (_start == 0)
        {
            _start = tick.Step;
            _firstTickOf = TryOf(new EntityId(1));
        }
        var simulated = Players.Diagnostics.Status?.Simulated == true;
        // ~4 s: the service has scanned many times by now; a simulation replays its file one line per step after 50.
        if (_done || tick.Step - _start < (simulated ? 320UL : 100UL)) return;
        // The probe needs a populated world. In a busy lab the mod can load before the ECS scene runs, so wait (up to
        // ~100 s, checking once a second) until some entity has a position.
        if (!simulated && tick.Step - _start < 2500UL && !WorldPopulated())
        {
            if ((tick.Step - _start) % 25 == 0) Log.Debug("players-test: waiting for the world to populate");
            return;
        }
        _done = true;
        try
        {
            if (simulated && _slotReuse) RunSlotReuse();
            else if (simulated) RunSimulated();
            else Run();
        }
        catch (Exception e)
        {
            Fail("run", e.ToString());
        }
        Log.Info($"players-test: {_pass} PASS, {_fail} FAIL");
    }

    private void Pass(string what) { _pass++; Log.Info($"players-test: PASS {what}"); }
    private void Fail(string what, string why) { _fail++; Log.Error($"players-test: FAIL {what}: {why}"); }
    private void Check(bool ok, string what, string why = "") { if (ok) Pass(what); else Fail(what, why); }

    private void Run()
    {
        Check(Players.Available, "service available");
        if (!Players.Available) return;
        var st = Players.Diagnostics.Status!;
        Log.Info($"players-test: status {st}");
        Check(st.Source == PlayerSource.Records, "source is the game's player records", $"source {st.Source}");
        Check(st.Records && st.AdminBit && st.Positions && st.GameLog, "records, admin bit, positions and log mirror on",
            $"records {st.Records} admin {st.AdminBit} positions {st.Positions} log {st.GameLog}");
        Check(st.SingletonMapAgrees, "world singleton map agrees with the registration hook");
        Check(st.WorldCaptured && st.ServerPlayersCaptured && st.BasicPlayerInfosCaptured, "world + both singletons captured");
        Check(st.Slots == 16, "16 player slots", $"slots {st.Slots}");
        Check(st.LayoutConsistent, "every record consistent at the last scan");
        Check(st.Scans >= 90, "scanned every step", $"scans {st.Scans}");
        Check(st.LogLines > 0, "game log mirror delivers lines", $"lines {st.LogLines}");

        // Empty server.
        Check(Players.All.Count == 0, "no players in game", $"{Players.All.Count}");
        Check(Players.Connected.Count == 0, "no connections", $"{Players.Connected.Count}");
        Check(Players.Count == 0, "Count is 0");
        Check(Players.Find("nobody") == null && Players.Find(new EntityId(1)) == null && Players.FindBySteamId(76561190000000001) == null,
            "lookups of absent players return null");
        Check(!Players.IsPlayer(new EntityId(1)) && Players.PositionOf(new EntityId(1)) == null, "entity 1 is no player now");
        Check(_joined == 0 && _left == 0, "no spurious Joined/Left while empty", $"joined {_joined} left {_left}");

        // Raw records: all 16 free; BasicPlayerInfos entries name no entity.
        var free = 0;
        var infoClean = 0;
        long nonzero = 0;
        for (var i = 0; i < st.Slots; i++)
        {
            var r = Players.Diagnostics.RawSlot(i);
            if (r is not { } raw) { Fail($"raw slot {i}", "null"); continue; }
            if (raw is { Handle: 0, State: 0, Entity: 0, NameLength: 0, AccountId: 0, Peer: 0 }) free++;
            else Log.Info($"players-test: slot {i} raw {raw}");
            if (raw is { InfoEntity: 0, InfoHandle: 0, InfoAdminByte: 0 }) infoClean++;
            nonzero += raw.InfoNonZeroBytes;
        }
        Check(free == st.Slots, "all player records free (handle, peer, state, entity, name, account = 0)", $"{free}/{st.Slots}");
        Check(infoClean == st.Slots, "BasicPlayerInfos entries name no entity, handle or admin", $"{infoClean}/{st.Slots}");
        Log.Info($"players-test: BasicPlayerInfos non-zero bytes over 16 entries: {nonzero}");
        Check(Players.Diagnostics.RawSlot(st.Slots) == null, "raw slot out of range is refused");

        // Threads: snapshot reads work off the tick thread; raw reads refuse it.
        var off = Task.Run(() =>
        {
            var all = Players.All.Count;
            try { Players.Diagnostics.RawSlot(0); return $"all {all}, raw allowed"; }
            catch (InvalidOperationException) { return $"all {all}, raw refused"; }
        }).GetAwaiter().GetResult();
        Check(off == "all 0, raw refused", "off-thread: All works, RawSlot refuses", off);

        IdentityEmpty();
        Positions();
        LogTracker();
    }

    // The player position path on world entities. The save's entities are numbered from 1025; the first few that
    // exist get their position through ProbePosition (the same code as player positions) and, when kiln.world is
    // deployed (found by reflection, so this mod builds without it), through kiln.world's own World.Position.
    private ulong _lastPopulatedCheck;

    private bool WorldPopulated()
    {
        if (_lastPopulatedCheck != 0 && Environment.TickCount64 - (long)_lastPopulatedCheck < 1000) return false;
        _lastPopulatedCheck = (ulong)Environment.TickCount64;
        for (uint id = 1025; id < 20000; id++)
            if (Players.Diagnostics.ProbePosition(new EntityId(id)) is not null) return true;
        return false;
    }

    private void Positions()
    {
        Check(Players.Diagnostics.ProbePosition(new EntityId(1)) == null, "probe: entity 1 does not exist on an empty server");
        var found = new List<(EntityId Entity, WorldPosition Position)>();
        for (uint id = 1025; id < 20000 && found.Count < 5; id++)
            if (Players.Diagnostics.ProbePosition(new EntityId(id)) is { } p) found.Add((new EntityId(id), p));
        var plausible = found.All(f => double.IsFinite(f.Position.X) && f.Position.X is > -1000 and < 12000 &&
                                       f.Position.Z is > -1000 and < 12000 && f.Position.Y is > -1000 and < 5000);
        Check(found.Count == 5 && plausible, "probe: world entities have plausible positions (metres)",
            string.Join("; ", found.Select(f => $"{f.Entity} {f.Position}")));

        var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Kiln.World") ?? TryLoad("Kiln.World", out _);
        var world = asm?.GetType("Kiln.World");
        var pos = world?.GetMethod("Position", new[] { typeof(EntityId) });
        if (world?.GetProperty("Available")?.GetValue(null) is not true || pos == null)
        {
            Log.Info($"players-test: SKIP probe vs kiln.world (not deployed: assembly {asm?.FullName ?? "-"}, type {world != null})");
            return;
        }
        var compared = 0;
        var worst = 0.0;
        foreach (var (e, mine) in found)
        {
            if ((WorldPosition?)pos.Invoke(null, new object[] { e }) is not { } theirs) continue;
            compared++;
            worst = Math.Max(worst, mine.DistanceTo(theirs));
            Log.Info($"players-test: probe entity {e}: players {mine} world {theirs}");
        }
        Check(compared == found.Count && compared > 0 && worst < 1e-6, "probe: player position path equals kiln.world",
            $"{compared}/{found.Count} compared, worst {worst} m");
    }

    private System.Reflection.Assembly? TryLoad(string name, out string why)
    {
        why = "";
        try { return System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(name)); }
        catch (Exception e) { why = e.Message; Log.Info($"players-test: loading {name}: {e.GetType().Name}: {e.Message}"); return null; }
    }

    // KILN_PLAYERS_SIMULATE=kiln\mods\players-test\fixtures\join-leave.log: the service replays the fixture into its
    // live tracker; the events must come through Kiln.Players' pump to this mod's handlers.
    private void RunSimulated()
    {
        var st = Players.Diagnostics.Status!;
        Log.Info($"players-test: SIMULATION status {st}");
        Check(st.Source == PlayerSource.GameLog, "sim: source is the game log");
        const ulong a = 76561190000000001, b = 76561190000000002;
        var all = PlayerPermissions.KickBan | PlayerPermissions.AccessInventories | PlayerPermissions.EditBase |
                  PlayerPermissions.EditWorld | PlayerPermissions.ExtendBase | PlayerPermissions.ReceiveExp;
        var order = string.Join(", ", _seen.Select(s => $"{(s.Joined ? "+" : "-")}{s.Player.Name}@{s.Player.Entity}"));
        Check(order == "+Alder@1, +Birch@2, -Birch@2, -Alder@1", "sim: Joined/Left reach the mod in order", order);
        if (_seen.Count == 4)
        {
            var (_, alder, alderCheck) = _seen[0];
            var (_, birch, birchCheck) = _seen[1];
            Check(alder.SteamId == a && birch.SteamId == b, "sim: Steam ids from the log", $"{alder.SteamId} {birch.SteamId}");
            Check(alder.Permissions == all && alder.PermissionsKnown && alder.InGame && alder.Slot == 0, "sim: Alder's permissions, state, slot", alder.ToString());
            Check(alderCheck == "find-name 1, find-steam 1, in All True, IsPlayer True", "sim: lookups inside Joined (case-insensitive name)", alderCheck);
            Check(birchCheck == "find-name 2, find-steam 2, in All True, IsPlayer True", "sim: lookups inside Joined (second player)", birchCheck);
            Check(_seen[2].Check == "still in All False" && _seen[2].Player.Name == "Birch" && _seen[2].Player.SteamId == b,
                "sim: Left carries the last-seen player and the list drops them", $"{_seen[2].Player} {_seen[2].Check}");
            Check(alder.JoinedStep > 0 && birch.JoinedStep > alder.JoinedStep, "sim: join steps recorded", $"{alder.JoinedStep} {birch.JoinedStep}");
        }
        Check(Players.All.Count == 0 && Players.Connected.Count == 0, "sim: empty after both left", $"{Players.Connected.Count}");
        Check(st.LastSequence == 4, "sim: four events in the service", $"{st.LastSequence}");
    }

    // ---- identity.players ----

    private static string TryOf(EntityId e)
    {
        try
        {
            var r = PlayerRef.Of(e);
            return r.IsNone ? "none" : r.ToString();
        }
        catch (KilnUnavailableException x)
        {
            return $"unavailable {x.Capability}";
        }
        catch (Exception x)
        {
            return $"{x.GetType().Name}: {x.Message}";
        }
    }

    private void IdentityEmpty()
    {
        Check(_onLoadOf == "unavailable identity.players", "identity: PlayerRef.Of in OnLoad throws KilnUnavailableException(identity.players)", _onLoadOf);
        Check(_firstTickOf == "none", "identity: PlayerRef.Of works from the first tick (entity 1: none)", _firstTickOf);
        var offline = new PlayerRef(76561190000000001, Guid.Empty, EntityRef.None);
        var r = offline.Resolve();
        Check(r == offline, "identity: Resolve of an absent player is offline, not an error", r.ToString());
        Check(PlayerRef.Of(new EntityId(2000)).IsNone, "identity: Of of a non-player entity is none");
        var alive = EntityRef.Of(new EntityId(1));
        Check(alive.IsNone, "identity: players' entity source: empty slot 1 is absent", alive.ToString());
        var stale = new PlayerRef(76561190000000001, Guid.Empty, new EntityRef(new EntityId(1), 1UL << 60 | 1));
        Check(!stale.IsOnline, "identity: IsOnline of a ref into an empty slot is false (entity source registered)");
    }

    private void SlotReuseJoined(Player p)
    {
        var of = PlayerRef.Of(p.Entity);
        var cast = (PlayerRef)p;
        _refs.Add((p.Name, of, cast));
        Check(of == cast && of.Entity.HasSerial && of.SteamId == p.SteamId,
            $"identity: join {_refs.Count} ({p.Name}): (PlayerRef)player == PlayerRef.Of(entity), stamped", $"of {of}, cast {cast}");
        if (_refs.Count == 1)
        {
            string what;
            try { what = $"returned {of.IsOnline}"; }
            catch (Exception x) { what = $"{x.GetType().Name}: {x.Message}"; }
            Check(what == "returned True", "identity: IsOnline answers through the registered entity source (no reflection): the first player's ref is online", what);
        }
        if (_refs.Count == 2)
        {
            // Birch in slot 0, where Alder was.
            var a = _refs[0].Of;
            var (aOnline, bOnline, aNow) = (a.IsOnline, of.IsOnline, a.Resolve());
            Check(a.Entity.Id == of.Entity.Id && a.Entity.Serial != of.Entity.Serial, "identity: same slot, different stamps",
                $"{a.Entity} vs {of.Entity}");
            Check(!aOnline, "identity: after slot reuse the first player's ref is not online");
            Check(bOnline, "identity: the second player's ref is online");
            Check(!a.IsSamePerson(of), "identity: the two players are not the same person");
            Check(aNow.Entity.IsNone && aNow.IsSamePerson(a), "identity: Resolve of the first player (offline) has no entity", aNow.ToString());
        }
        if (_refs.Count == 3)
        {
            // Alder again, now in slot 1.
            var a = _refs[0].Of;
            var (aOnline, aNow, nowOnline) = (a.IsOnline, a.Resolve(), a.Resolve().IsOnline);
            Check(!aOnline, "identity: the first session's ref stays not online after a rejoin");
            Check(a.IsSamePerson(of), "identity: the rejoined player is the same person");
            Check(aNow == of && nowOnline, "identity: Resolve gives the new session, online", $"{aNow} vs {of}");
        }
    }

    // KILN_PLAYERS_SIMULATE=kiln\mods\players-test\fixtures\slot-reuse.log: Alder joins slot 0 and leaves, Birch joins
    // slot 0, Alder joins slot 1. The checks at each join ran in SlotReuseJoined; these are the end state.
    private void RunSlotReuse()
    {
        var st = Players.Diagnostics.Status!;
        Log.Info($"players-test: SIMULATION (slot reuse) status {st}");
        var order = string.Join(", ", _seen.Select(s => $"{(s.Joined ? "+" : "-")}{s.Player.Name}@{s.Player.Entity}"));
        Check(order == "+Alder@1, -Alder@1, +Birch@1, +Alder@2", "sim slot reuse: Joined/Left in order", order);
        Check(_onLoadOf == "unavailable identity.players", "identity: PlayerRef.Of in OnLoad throws KilnUnavailableException(identity.players)", _onLoadOf);
        Check(_firstTickOf == "none", "identity: PlayerRef.Of works from the first tick (entity 1: none)", _firstTickOf);
        if (_refs.Count != 3)
        {
            Fail("sim slot reuse: three joins captured", $"{_refs.Count}");
            return;
        }
        var (a1, b, a2) = (_refs[0].Of, _refs[1].Of, _refs[2].Of);
        Log.Info($"players-test: refs {a1} | {b} | {a2}");
        var stamps = new[] { a1, b, a2 }.Select(r => r.Entity.Serial).ToList();
        Check(stamps.All(s => s != 0) && stamps.Distinct().Count() == 3, "identity: three sessions, three non-zero stamps",
            string.Join(", ", stamps.Select(s => $"0x{s:x}")));
        var (a1On, bOn, a2On) = (a1.IsOnline, b.IsOnline, a2.IsOnline);
        Check(!a1On && bOn && a2On, "identity: at the end only the two current sessions are online", $"{a1On} {bOn} {a2On}");
        Check(PlayerRef.Of(new EntityId(1)) == b && PlayerRef.Of(new EntityId(2)) == a2, "identity: Of gives the current sessions");
        Check(Players.All.Count == 2, "sim slot reuse: two players in game at the end", $"{Players.All.Count}");
    }

    private void LogTracker()
    {
        var dir = Path.Combine(Context.Info.Directory, "fixtures");
        foreach (var file in new[] { "join-leave.log", "shutdown.log" })
        {
            var lines = File.ReadAllLines(Path.Combine(dir, file));
            Replay($"{file} (file lines)", lines, full: true, shutdown: file == "shutdown.log");
            Replay($"{file} (mirror form)", MirrorForm(lines), full: true, shutdown: file == "shutdown.log");
            // Up to the second login's permission list: both players in game.
            var cut = Array.FindIndex(lines, l => l.Contains("Player 'Birch' logged in")) + 8;
            Replay($"{file} (cut after both logins)", lines[..cut], full: false, shutdown: false);
        }

        // A connection that times out before logging in: no Joined, slot freed.
        var timeout = new[]
        {
            "[online] Added peer 2(1) (steamid:76561190000000009)",
            "[online] Client '76561190000000009' authenticated by steam",
            "[session] Remote player added. Player handle: 2(0)",
            "[session] Timed out while waiting for player. Remove Player: 2(0)",
        };
        var (tp, te) = Players.Diagnostics.ReplayLog(timeout);
        Check(tp.Count == 0 && te.Count == 0, "log: timed-out connection leaves no player and no event", $"{tp.Count} players, {te.Count} events");

        // While joining: the slot is known (state Joining) with its Steam id, before the login line.
        var (jp, je) = Players.Diagnostics.ReplayLog(timeout[..3]);
        Check(jp.Count == 1 && jp[0] is { Slot: 2, State: PlayerState.Joining, SteamId: 76561190000000009 } && je.Count == 0,
            "log: joining connection has its slot and Steam id", jp.Count == 1 ? jp[0].ToString() : $"{jp.Count} players");
    }

    // The mirror hands over messages without the file prefix; the login line and its permission list are one message.
    private static List<string> MirrorForm(string[] lines)
    {
        var out_ = new List<string>();
        static string Strip(string l) => l.Length > 17 && l[0] == '[' && l[2] == ' ' && l.IndexOf("] ", StringComparison.Ordinal) is var k and > 0 and <= 20 ? l[(k + 2)..] : l;
        for (var i = 0; i < lines.Length; i++)
        {
            var s = Strip(lines[i]);
            if (s.Contains("logged in with Permissions:"))
            {
                var msg = s;
                while (i + 1 < lines.Length && Strip(lines[i + 1]) is var n && (n.StartsWith("\t - ", StringComparison.Ordinal) || n.Length == 0))
                {
                    msg += "\n" + n;
                    i++;
                    if (n.Length == 0) break;
                }
                out_.Add(msg);
            }
            else out_.Add(s);
        }
        return out_;
    }

    private void Replay(string what, IReadOnlyList<string> lines, bool full, bool shutdown)
    {
        var (players, events) = Players.Diagnostics.ReplayLog(lines);
        const ulong a = 76561190000000001, b = 76561190000000002;
        const PlayerPermissions all = PlayerPermissions.KickBan | PlayerPermissions.AccessInventories | PlayerPermissions.EditBase |
                                      PlayerPermissions.EditWorld | PlayerPermissions.ExtendBase | PlayerPermissions.ReceiveExp;
        bool Is(Player p, int slot, string name, ulong steam) =>
            p.Slot == slot && p.Entity.Value == slot + 1 && p.Name == name && p.SteamId == steam && p.Permissions == all && p.PermissionsKnown;
        var joins = events.Where(e => e.Joined).ToList();
        var leaves = events.Where(e => !e.Joined).ToList();
        var ok = joins.Count == 2 && Is(joins[0].Player, 0, "Alder", a) && Is(joins[1].Player, 1, "Birch", b);
        if (full)
        {
            // join-leave: Birch leaves first; shutdown: Alder leaves, then the server shuts down with Birch on it.
            var firstOut = shutdown ? ("Alder", 0) : ("Birch", 1);
            var secondOut = shutdown ? ("Birch", 1) : ("Alder", 0);
            ok = ok && leaves.Count == 2 && leaves[0].Player.Name == firstOut.Item1 && leaves[0].Player.Slot == firstOut.Item2 &&
                 leaves[1].Player.Name == secondOut.Item1 && leaves[1].Player.Slot == secondOut.Item2;
            ok = ok && players.Count == 0;
        }
        else
        {
            ok = ok && leaves.Count == 0 && players.Count == 2 && players.All(p => p.InGame) && Is(players[0], 0, "Alder", a) &&
                 Is(players[1], 1, "Birch", b);
        }
        Check(ok, $"log tracker: {what}",
            $"events [{string.Join(", ", events.Select(e => $"{(e.Joined ? "+" : "-")}{e.Player.Name}@{e.Player.Slot}/{e.Player.SteamId}/{e.Player.Permissions}"))}] " +
            $"players [{string.Join(", ", players.Select(p => $"{p.Name}@{p.Slot}:{p.State}"))}]");
    }

    public override void OnUnload() => Log.Info($"players-test: unloading (saw {_joined} joined, {_left} left)");
}
