// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;

namespace ClientCheckTest;

/// <summary>
/// Guided checks for the check runner itself. On a lab server fixtures/client_checks_lab.py starts them through
/// kiln/control/checks/ and answers them; in a game client they are in the check list (Ctrl+Shift+F5) like any other.
/// The cheats check writes a marker file and a log line when its body runs: the runner must refuse it on a dedicated
/// server, and <c>cct.flag-unset</c> (and the lab script) show that it never ran.
/// </summary>
public sealed class ClientCheckTestMod : Mod
{
    private const string Area = "Check runner";
    private const string Since = "0.2.0-dev.26";

    private static bool _cheatsBodyRan;

    private string Marker => Path.Combine(Context.DataDirectory, "cheats-body-ran");

    private void LeaveMarker()
    {
        _cheatsBodyRan = true;
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(Marker, DateTime.UtcNow.ToString("O"));
    }

    public override void OnLoad()
    {
        var n = ClientChecks.Register(this);
        Log.Info($"client-check-test: {n} guided check(s) registered");
    }

    [ClientCheck("cct.assertions", "Passes on its assertions and a verdict", Area = Area, Since = Since, Order = 1)]
    public async Task Assertions(CheckContext c)
    {
        c.Instruct("Nothing to do here: answer pass.");
        c.Expect(Game.KilnVersion.Length > 0, "Kiln reports its version", Game.KilnVersion);
        c.Expect(Game.IsTickThread, "the body runs on the tick thread");
        c.Log.Info("cct.assertions: the body ran");
        await c.Verdict();
    }

    [ClientCheck("cct.fail-note", "Failed with a note", Area = Area, Since = Since, Order = 2)]
    public async Task FailNote(CheckContext c)
    {
        c.Instruct("Answer fail and type one line about what you saw.");
        await c.Verdict();
    }

    [ClientCheck("cct.skip", "Skipped", Area = Area, Since = Since, Order = 3)]
    public async Task Skip(CheckContext c)
    {
        c.Instruct("Answer skip.");
        await c.Verdict();
    }

    [ClientCheck("cct.two-steps", "Waits ticks between two instructions", Area = Area, Since = Since, Order = 4)]
    public async Task TwoSteps(CheckContext c)
    {
        c.Instruct("First instruction: wait about two seconds.");
        var start = DateTime.UtcNow;
        await c.Ticks(50);
        c.Expect(DateTime.UtcNow - start >= TimeSpan.FromSeconds(1), "50 steps took at least a second", $"{(DateTime.UtcNow - start).TotalSeconds:0.0} s");
        c.Instruct("Second instruction: now answer pass.");
        await c.Verdict();
    }

    [ClientCheck("cct.private-log", "Private data in a log line stays out of the ledger", Area = Area, Since = Since, Order = 5)]
    public async Task PrivateLog(CheckContext c)
    {
        c.Instruct("Answer pass; the entry's log must show <ip>, <steamid> and <home> in place of the values.");
        c.Log.Info(@"cct.private-log: player at 192.168.1.20 and fe80::1ff:fe23:4567:890a, steam 76561198012345678, " +
                   @"files in C:\Users\someone\saves and /home/someone/saves");
        await c.Verdict();
    }

    [ClientCheck("cct.cheats-flag", "A cheats check (refused without the guard)", Area = Area, Since = Since, Tags = [ClientCheckTags.Cheats], Order = 6)]
    public async Task CheatsFlag(CheckContext c)
    {
        LeaveMarker();
        c.Log.Warn("cct.cheats-flag: CHEATS BODY RAN");
        c.Instruct("This body ran: the runner let a cheats check through.");
        await c.Verdict();
    }

    [ClientCheck("cct.guard-proof", "The guard-proof stand-in (refused without the guard)", Area = Area, Since = Since,
        Tags = [ClientCheckTags.Cheats, ClientCheckTags.GuardProof],
        Confirm = "This check stands in for the one that writes an achievement. Answer yes only if your Steam account already has it.",
        Order = 7)]
    public async Task GuardProof(CheckContext c)
    {
        LeaveMarker();
        c.Log.Warn("cct.guard-proof: CHEATS BODY RAN");
        await c.Verdict();
    }

    [ClientCheck("cct.flag-unset", "The cheats checks' bodies never ran", Area = Area, Since = Since, Order = 8)]
    public async Task FlagUnset(CheckContext c)
    {
        c.Instruct("Answer pass when the assertion below is ok.");
        c.Expect(!_cheatsBodyRan, "no cheats body ran in this process");
        c.Expect(!File.Exists(Marker), "no cheats body ever left its marker", File.Exists(Marker) ? "found " + Path.GetFileName(Marker) : null);
        await c.Verdict();
    }

    [ClientCheck("cct.hosted-only", "Needs a hosted world", Area = Area, Since = Since, Needs = ClientCheckNeeds.Hosted, Order = 9)]
    public async Task HostedOnly(CheckContext c)
    {
        c.Instruct("This world is hosted here: answer pass.");
        await c.Verdict();
    }

    [ClientCheck("cct.cleanup", "Cleans up however it ends", Area = Area, Since = Since, Order = 10)]
    public async Task Cleanup(CheckContext c)
    {
        c.OnEnd(() => c.Log.Info("cct.cleanup: the clean-up ran"));
        c.Instruct("Answer pass (or stop: the clean-up runs either way).");
        await c.Verdict();
    }

    [ClientCheck("cct.wait", "Waits for its verdict", Area = Area, Since = Since, Order = 11)]
    public async Task Wait(CheckContext c)
    {
        c.Instruct("Wait: the lab test stops the server here, so this check gets no entry.");
        await c.Verdict();
    }
}
