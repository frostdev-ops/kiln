// SPDX-License-Identifier: MIT
// Kick and ban lab test (dev.27): kicks each fake client of tools/nettest/kicktest.py through Network.Kick, a Kiln one
// after its handshake and a vanilla one once Kiln.Network has judged it. Prints "kick-test: PASS/FAIL/INFO" lines.
using Kiln;

namespace KickTest;

public sealed partial class KickTestMod : Mod
{
    private int _pass, _fail;
    private readonly HashSet<uint> _kicking = new();
    private readonly List<PlayerKickedEvent> _kicked = new();

    public override void OnLoad()
    {
        var n = Kiln.Testing.ClientChecks.Register(this);  // the joined player's checks (ClientChecks.cs)
        Log.Info($"kick-test: {n} guided check(s) registered");
        _ = Run();  // the lab part: it kicks lab peers only (tools/nettest/kicktest.py), never a player
    }

    private void Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"kick-test: PASS {what}"); }
        else { _fail++; Log.Error($"kick-test: FAIL {what}"); }
    }

    private void Info(string s) => Log.Info($"kick-test: INFO {s}");

    [Subscribe] private void OnKicked(PlayerKickedEvent e) => _kicked.Add(e);

    private async Task Run()
    {
        await Context.Scheduler.NextTick();
        await Context.Scheduler.NextTick();
        Check(Capabilities.Is("players.kick") && Capabilities.Is("players.bans"),
            $"capability rows: {Capabilities.Why("players.kick")} / {Capabilities.Why("players.bans")}");
        Info($"bans at start: {Bans.All.Count}");
        var started = DateTime.UtcNow;
        var done = 0;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(6) && done < 2)
        {
            await Context.Scheduler.NextTick();
            foreach (var p in Network.Peers)
            {
                if (!p.IsLab || _kicking.Contains(p.Id)) continue;
                var age = (DateTime.UtcNow - p.ConnectedAt).TotalSeconds;
                var kiln = p.State == NetPeerState.Ready && age > 1;
                // a lab peer that never says hello (no player record, so Kiln.Network never calls it vanilla): one
                // without Kiln, as far as a kick is concerned
                var vanilla = p.State is NetPeerState.Vanilla or NetPeerState.Connecting && p.KilnVersion is null && age > 3;
                if (!kiln && !vanilla) continue;
                _kicking.Add(p.Id);
                done++;
                _ = Kick(p, kiln ? "kick-test: bye, Kiln client" : "kick-test: bye, vanilla client");
            }
        }
    }

    private async Task Kick(NetPeer p, string reason)
    {
        try
        {
            try { _ = Network.Kick(p, ""); Check(false, "Network.Kick with an empty reason did not throw"); }
            catch (ArgumentException e) { Check(true, $"Network.Kick with an empty reason throws ArgumentException: {e.Message}"); }
            var state = p.State;
            var at = DateTime.UtcNow;
            var t = Network.Kick(p, reason);
            var again = Network.Kick(p, "second");
            Check(ReferenceEquals(t, again), "a second kick of the same connection gives the same task");
            var r = await t;
            var ms = (DateTime.UtcNow - at).TotalMilliseconds;
            Check(r.Ok && r.Reason is null, $"Network.Kick of lab peer 0x{p.Id:x} ({state}): {r} after {ms:0} ms");
            var plain = state != NetPeerState.Ready;
            Check(plain ? ms < 1400 : ms >= 1400, $"{(plain ? "a client without Kiln is kicked at once (no REJECT)" : "a Kiln client gets 1.5 s to read the reason")} ({ms:0} ms)");
            await Context.Scheduler.NextTick();
            var ev = _kicked.FirstOrDefault(e => e.Reason == reason);
            Check(ev is { Banned: false, By: "kick-test" }, $"PlayerKickedEvent: {ev?.Reason} by {ev?.By}, banned {ev?.Banned}");
            for (var i = 0; i < 50 && Network.Peers.Any(x => x.Id == p.Id); i++) await Context.Scheduler.NextTick();
            Check(Network.Peers.All(x => x.Id != p.Id), $"lab peer 0x{p.Id:x} is gone");
        }
        catch (Exception e)
        {
            Check(false, $"kick threw: {e}");
        }
        Info($"so far: {_pass} PASS, {_fail} FAIL");
    }
}
