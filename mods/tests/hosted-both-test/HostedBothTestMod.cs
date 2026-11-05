// SPDX-License-Identifier: MIT
// A side "both" mod (docs/kiln/client.md, "Mods and sides"): loaded once (a dedicated server's first step, a client's
// menu), it stays loaded across hosted worlds. Its server part runs between ServerStarted and ServerStopping (a
// dedicated server's world, or a world this client hosts): it counts server steps on the tick thread. Its client part
// counts client-world steps (Client.Tick, main thread). Lines "hosted-both-test: PASS/FAIL/INFO ..." in kiln.log.
using Kiln;

namespace HostedBothTest;

public sealed class HostedBothTestMod : Mod
{
    private int _pass, _fail, _sessions;
    private bool _serverPart;
    private ulong _serverSteps, _clientSteps, _offServerTicks;
    private readonly HashSet<int> _serverThreads = [];

    public override void OnLoad()
    {
        Info($"OnLoad: side {Mods.Side}, {(Client.Available && Client.IsClient ? $"client {Client.State}" : "dedicated server")}, " +
             $"phase {Lifecycle.Phase}, tick thread {Game.IsTickThread}");
        Context.Tick.Pre += OnPre;
        if (Client.Available && Client.IsClient) Client.Tick += OnClientTick;
        Commands.Register("hostedboth", ctx => ctx.Reply(Status()), description: "hosted-both-test status");
        if (Lifecycle.IsServerRunning) StartServerPart("loaded while the server runs");
    }

    public override void OnUnload() => Info($"OnUnload: {Status()}; summary: {_pass} PASS, {_fail} FAIL");

    private void OnPre(in TickInfo tick)
    {
        if (!_serverPart)
        {
            _offServerTicks++;  // before ServerStarted, or a client in the menu-to-world gap or a joined (remote) world
            return;
        }
        _serverSteps++;
        _serverThreads.Add(Environment.CurrentManagedThreadId);
    }

    private void OnClientTick(in TickInfo tick) => _clientSteps++;

    [Subscribe]
    private void OnStarted(ServerStartedEvent e) => StartServerPart($"ServerStarted ({e.Source}) at step {e.Step}");

    [Subscribe]
    private void OnStopping(ServerStoppingEvent e)
    {
        Info($"server part stops: ServerStopping ({e.Reason}), {Status()}");
        Check(_serverSteps > 0, $"the server part ticked in session {_sessions}: {_serverSteps} server steps on {_serverThreads.Count} thread(s)");
        _serverPart = false;
    }

    private void StartServerPart(string why)
    {
        _sessions++;
        _serverPart = true;
        _serverSteps = 0;
        _serverThreads.Clear();
        Info($"server part starts (session {_sessions}): {why}; {Status()}");
        Check(Game.IsTickThread, "the server part starts on the tick thread");
        if (Client.Available && Client.IsClient)
        {
            Check(Client.IsHosting, $"on a client the server part runs only while it hosts: {Client.State}");
            Check(Mods.IsLoaded("hosted-test") || Mods.Get("hosted-test") is null,
                $"the server mod next to it loaded for this hosted world: {Mods.Get("hosted-test")}");
        }
    }

    private string Status() =>
        $"sessions {_sessions}, server part {(_serverPart ? "on" : "off")}, server steps {_serverSteps}, client-world steps {_clientSteps}, " +
        $"ticks outside the server part {_offServerTicks}, phase {Lifecycle.Phase}" +
        (Client.Available && Client.IsClient ? $", client {Client.State}" : "") +
        $", hosted-test {(Mods.Get("hosted-test") is { } m ? m.State.ToString().ToLowerInvariant() : "absent")}";

    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"hosted-both-test: PASS {what}"); }
        else { _fail++; Log.Error($"hosted-both-test: FAIL {what}"); }
        return ok;
    }

    private void Info(string s) => Log.Info($"hosted-both-test: INFO {s}");
}
