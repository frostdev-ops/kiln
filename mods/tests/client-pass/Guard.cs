// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// Session 6: the cheats group, on a new throwaway character named KilnCheck..., hosted, with client-pass alone. The first
// check of the group, and the one the guard cannot protect because it tests the guard. The game reports an achievement
// only once its knowledge is set, and the level achievements sit at thresholds, so the check raises the character one
// level at a time until the guard sees a report, at most to GuardProofMaxLevel. For an account that already has every
// level achievement up to there this is harmless: the game asks Steam first and skips what the account has, while the
// report still reaches the guard. Until its newest run on this game build and Kiln version passed, the runner refuses
// every other cheats check. Order 601.
public sealed partial class ClientPassMod
{
    /// <summary>The highest level guard.proof raises a character to.</summary>
    private const int GuardProofMaxLevel = 30;

    private const string GuardConfirm =
        "This check raises your character one level at a time until the achievement guard sees the game report an " +
        "achievement, at most to level 30, and grants nothing else. Run it only if your Steam account already has every " +
        "level achievement up to level 30: a Steam achievement cannot be taken back. Use a new throwaway character named " +
        "KilnCheck... and delete it when the cheats group is done. If you are not sure, answer no.";

    [ClientCheck("guard.proof", "The achievement guard drops a level-up's report", Area = "Achievement guard", Since = Dev26,
        Tags = [Cheats, GuardProof], Needs = ClientCheckNeeds.Hosted, Confirm = GuardConfirm, Order = 601)]
    public async Task GuardProofLevelUp(CheckContext c)
    {
        if (!await NeedPlayer(c)) return;
        var before = SteamGuard.Status;
        c.Expect(before is { Available: true, HookInstalled: true, TargetVerified: true, Active: true },
            "the guard protects before the change",
            $"available {before.Available}, hook {before.HookInstalled}, target {before.TargetVerified}, active {before.Active}: {before.Reason}");

        var snap = Progression.Get(c.Player);
        if (snap is not { HasLevel: true })
        {
            c.Expect(false, "the character's level can be read", snap is null ? "no progression for this player" : "no level");
            c.Instruct("Kiln cannot read this character's level (see the assertion). Nothing was changed. Press fail.");
            await c.Verdict();
            return;
        }
        var from = snap.Level;
        var max = Math.Min(GuardProofMaxLevel, snap.LevelCap > 0 ? snap.LevelCap : GuardProofMaxLevel);
        if (from >= max)
        {
            c.Expect(false, $"the character starts below level {max}", $"level {from}, cap {snap.LevelCap}");
            c.Instruct($"This character is already at level {from}, at or above the check's limit of {max}. Nothing was " +
                       "changed. Use a new KilnCheck character. Press fail.");
            await c.Verdict();
            return;
        }

        // The game's own log: an unlock that reached Steam is logged as "[online] Achievement '...' unlocked.".
        var unlockLines = 0;
        void OnGameLog(LogLevel level, string text)
        {
            if (text.Contains("[online] Achievement", StringComparison.Ordinal)) unlockLines++;
        }
        c.Mod.GameLog.Line += OnGameLog;
        c.OnEnd(() => c.Mod.GameLog.Line -= OnGameLog);

        // One level at a time; most levels set no achievement knowledge and legitimately report nothing, so no timeout
        // here is an assertion. Stop at the first level whose report the guard dropped.
        var reached = from;
        var seen = false;
        for (var level = from + 1; level <= max && !seen; level++)
        {
            c.Instruct($"Raising the character: level {level} of at most {max}, no report yet. Wait and do nothing else.");
            var result = await Progression.SetLevel(c.Player, level);
            if (!result.Ok)
            {
                c.Expect(false, $"level set to {level}", result.ToString());
                break;
            }
            reached = level;
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < until)
            {
                if (SteamGuard.Status.Dropped > before.Dropped) { seen = true; break; }
                await c.Ticks();
            }
        }

        if (seen) await c.Delay(TimeSpan.FromSeconds(5));   // the game's log lines arrive a step or so late
        var after = SteamGuard.Status;
        c.Expect(seen, $"the guard saw a report by level {max}", $"level {from} -> {reached}");
        c.Expect(after.Dropped > before.Dropped, "the guard's dropped counter went up",
            $"dropped {before.Dropped} -> {after.Dropped}, level {from} -> {reached}");
        c.Expect(after.Passed == before.Passed, "no report passed to Steam during the check",
            $"passed {before.Passed} -> {after.Passed}, level {from} -> {reached}");
        c.Expect(unlockLines == 0, "no [online] Achievement line in the game's log during the check",
            $"{unlockLines} line(s), level {from} -> {reached}");

        if (!seen)
        {
            c.Instruct($"The character reached level {reached} and the guard saw no report: the proof is inconclusive. " +
                       "Delete this KilnCheck character, and never load it in a game without Kiln. Press fail.");
            await c.Verdict();
            return;
        }
        c.Instruct($"Done: level {reached}; the guard dropped {after.Dropped - before.Dropped} report(s). Open the game's " +
                   "own log (enshrouded.log in the game folder) and look at its last lines: there must be no new line " +
                   "starting with [online] Achievement. kiln.log has \"steamguard: dropped a Steam achievement/stat " +
                   "report\". Is there no new [online] Achievement line? Delete this KilnCheck character when the cheats " +
                   "group is done, and never load it in a game without Kiln.");
        await c.Verdict();
    }
}
