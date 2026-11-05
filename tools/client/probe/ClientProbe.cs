// SPDX-License-Identifier: MIT
// Read-only: logs what Kiln sees on the client. Writes nothing into the game (no time set, no spawn, no teleport).
using Kiln;

namespace ClientProbe;

public sealed class ProbeMod : Mod
{
    private ClientState _state = (ClientState)(-1);
    private uint _clientSession;
    private EntityId _local;
    private ulong _serverSteps, _clientSteps;
    private DateTime _since = DateTime.UtcNow;
    private DateTime _nextReport = DateTime.UtcNow;
    private bool _clientThreadSeen;
    private uint _serverSession;
    private DateTime _dumpAt = DateTime.MaxValue;

    public override void OnLoad()
    {
        Log.Info($"probe: loaded on the tick thread: {Game.IsTickThread}; Client.Available {Client.Available}, " +
                 $"IsClient {Client.IsClient}, State {Client.State}, HasLocalPlayer {Client.HasLocalPlayer}");
        Context.Tick.Pre += OnServerPre;
        Client.Tick += OnClientTick;
    }

    private void OnServerPre(in TickInfo tick)
    {
        _serverSteps++;
        var now = DateTime.UtcNow;
        var session = Client.ServerWorldSession;
        if (session != _serverSession)
        {
            _serverSession = session;
            _dumpAt = now.AddSeconds(25);  // after the world loaded (kiln.saves' LOADED) and settled
            Log.Info($"probe: server world session {session} (first step {tick.Step}, clock delta {tick.ClockDelta})");
        }
        if (now >= _dumpAt)
        {
            _dumpAt = DateTime.MaxValue;
            DumpServices(session);
        }
        if (now < _nextReport) return;
        _nextReport = now.AddSeconds(10);
        var span = (now - _since).TotalSeconds;
        _since = now;
        var state = Client.State;
        var local = Client.LocalPlayer;
        var line = $"probe: state {state}, server world {_serverSteps / span:F1} steps/s (clock +{tick.ClockDelta}/step), " +
                   $"client world {_clientSteps / span:F1} steps/s, local player {local}";
        _serverSteps = 0;
        Interlocked.Exchange(ref _clientSteps, 0);
        try
        {
            if (World.Available) line += $"; world: {World.EntityCount} entities, local exists {(!local.IsNone && World.Exists(local))}" +
                                         (local.IsNone ? "" : $", local at {World.Position(local)}");
            if (Players.Available) line += $"; players: {string.Join(", ", Players.All.Where(p => p.InGame).Select(p => $"{p.Name}#{p.Entity}"))}";
            if (Time.Available && Time.Ready) line += $"; time {Time.OfDay:F2} h";
        }
        catch (Exception e)
        {
            line += $"; service read failed: {e.GetType().Name}: {e.Message}";
        }
        Log.Info(line);
    }

    private void OnClientTick(in TickInfo tick)
    {
        Interlocked.Increment(ref _clientSteps);
        if (!_clientThreadSeen)
        {
            _clientThreadSeen = true;
            Log.Info($"probe: first client-world tick: client thread {Client.IsClientThread}, server tick thread {Game.IsTickThread}, " +
                     $"step {tick.Step}, clock {tick.Clock}");
        }
        var state = Client.State;
        var session = Client.ClientWorldSession;
        var local = Client.LocalPlayer;
        if (state != _state || session != _clientSession || local != _local)
        {
            Log.Info($"probe: client state {state}, client world session {session}, server world session " +
                     $"{Client.ServerWorldSession}, local player {local}");
            (_state, _clientSession, _local) = (state, session, local);
        }
    }

    // Read-only status of the services that depend on the hosted world (Settings.Save on a client only refuses).
    private void DumpServices(uint session)
    {
        void Try(string what, Func<string> f)
        {
            try { Log.Info($"probe: session {session} {what}: {f()}"); }
            catch (Exception e) { Log.Info($"probe: session {session} {what}: {e.GetType().Name}: {e.Message}"); }
        }
        Try("saves", () => $"{Saves.Status}; world file {Saves.WorldFile ?? "-"}; save dir {Saves.SaveDirectory ?? "-"}");
        Try("settings", () => { var s = Settings.Status; return $"ready {s.Ready}, {s.Count} settings, verified {s.VerifiedSingleton}, preset {s.Preset}"; });
        if (Client.IsClient) Try("settings.save (a client must refuse)", () => Settings.Save());
        Try("spawn", () => $"world key {Spawn.WorldKey ?? "-"}");
        Try("items", () => Items.Status()?.ToString() ?? "-");
        Try("time", () => Time.TryGetState(out var st) ? st.ToString() : "not ready");
        Try("players", () => string.Join(", ", Players.All.Select(p => $"{p.Name}#{p.Entity} in game {p.InGame}")));
    }

    public override void OnUnload() => Log.Info("probe: unloaded");
}
