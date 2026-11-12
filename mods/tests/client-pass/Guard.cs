// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// Session 6: the cheats group, on a new throwaway character named KilnCheck..., hosted, with client-pass alone. The first
// check of the group, and the one the guard cannot protect because it tests the guard: it raises the character by one
// level, which can only reach level achievements the account already has, and shows that the report was dropped. Until
// its newest run on this game build and Kiln version passed, the runner refuses every other cheats check. Order 601.
public sealed partial class ClientPassMod
{
    private const string GuardConfirm =
        "This check sets your character's level to one level above its current level, and grants nothing else. One level " +
        "can reach several level achievements at once. Run it only if your Steam account already has every level " +
        "achievement up to that level: a Steam achievement cannot be taken back. Use a new throwaway character named " +
        "KilnCheck... and delete it when the cheats group is done. If you are not sure, answer no.";

    [ClientCheck("guard.proof", "The achievement guard drops a level-up's report", Area = "Achievement guard", Since = Dev25,
        Tags = [Cheats, GuardProof], Needs = ClientCheckNeeds.Hosted, Confirm = GuardConfirm, Order = 601)]
    public async Task GuardProofLevelUp(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        var before = SteamGuard.Status;
        c.Expect(before is { Available: true, HookInstalled: true, TargetVerified: true, Active: true },
            "the guard protects before the change",
            $"available {before.Available}, hook {before.HookInstalled}, target {before.TargetVerified}, active {before.Active}: {before.Reason}");

        var snap = Progression.Get(c.Player);
        if (snap is not { HasLevel: true } || snap.AtLevelCap)
        {
            c.Expect(false, "the character's level can be read and raised",
                snap is null ? "no progression for this player" : $"level {snap.Level}, cap {snap.LevelCap}");
            c.Instruct("Kiln cannot raise this character's level (see the assertion). Nothing was changed. Press fail.");
            await c.Verdict();
            return;
        }
        var from = snap.Level;
        var to = from + 1;

        // The game's own log: an unlock that reached Steam is logged as "[online] Achievement '...' unlocked.".
        var unlockLines = 0;
        void OnGameLog(LogLevel level, string text)
        {
            if (text.Contains("[online] Achievement", StringComparison.Ordinal)) unlockLines++;
        }
        c.Mod.GameLog.Line += OnGameLog;
        c.OnEnd(() => c.Mod.GameLog.Line -= OnGameLog);

        c.Instruct($"Raising this character from level {from} to {to}. Wait about half a minute and do nothing else.");
        var result = await Progression.SetLevel(c.Player, to);
        c.Expect(result.Ok, $"level set to {to}", result.ToString());
        await c.Until(() => SteamGuard.Status.Dropped > before.Dropped, TimeSpan.FromSeconds(30), "the guard dropped at least one report");
        await c.Delay(TimeSpan.FromSeconds(5));   // the game's log lines arrive a step or so late

        var after = SteamGuard.Status;
        c.Expect(after.Dropped > before.Dropped, "the guard's dropped counter went up",
            $"dropped {before.Dropped} -> {after.Dropped}");
        c.Expect(after.Passed == before.Passed, "no report passed to Steam during the check",
            $"passed {before.Passed} -> {after.Passed}");
        c.Expect(unlockLines == 0, "no [online] Achievement line in the game's log during the check", $"{unlockLines} line(s)");

        c.Instruct($"Done: level {to}; the guard dropped {after.Dropped - before.Dropped} report(s). Open the game's own log " +
                   "(enshrouded.log in the game folder) and look at its last lines: there must be no new line starting with " +
                   "[online] Achievement. kiln.log has \"steamguard: dropped a Steam achievement/stat report\". Is there no " +
                   "new [online] Achievement line? Delete this KilnCheck character when the cheats group is done, and never " +
                   "load it in a game without Kiln.");
        await c.Verdict();
    }
}
