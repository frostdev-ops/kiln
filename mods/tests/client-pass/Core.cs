// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// Kiln's unload at the game's exit (new in 0.2.0-dev.25). The answer is only known after the game has closed, so each
// check runs in three parts over three starts: part 1 asks the player to quit with Esc, Quit; part 2, at the next start,
// reads the previous session's kiln.log (Kiln renames it kiln.prev.log when it starts) and asks for Alt+F4 at the main
// menu; part 3 reads that exit and takes the verdict. The part reached is kept in the mod's data folder.
public sealed partial class ClientPassMod
{
    /// <summary>The kiln.json switch that turns the exit link on for a native Windows game.</summary>
    /// <remarks>PLACEHOLDER: the key's name is not known yet; the core package names it.</remarks>
    private const string WindowsUnloadKey = "<KEY: core names it in its report>";

    [ClientCheck("core.unload-quit", "Kiln unloads cleanly when the game quits", Area = "Core", Since = Dev25,
        Needs = ClientCheckNeeds.Hosted, Order = 199)]
    public Task CoreUnloadQuit(CheckContext c) => UnloadCheck(c, "unload-quit", null);

    [ClientCheck("core.unload-windows", "Kiln unloads cleanly when the game quits, on Windows", Area = "Core", Since = Dev25,
        Tags = [Windows], Needs = ClientCheckNeeds.Hosted, Order = 915)]
    public Task CoreUnloadWindows(CheckContext c) => UnloadCheck(c, "unload-windows",
        $"On a native Windows PC the exit link is off by default in this release: before part 1, set \"{WindowsUnloadKey}\" " +
        "to \"on\" in kiln/kiln.json and start the game again. ");

    private static async Task UnloadCheck(CheckContext c, string name, string? first)
    {
        var stageFile = Path.Combine(c.Mod.DataDirectory, name + ".stage");
        var (stage, since) = ReadStage(stageFile);
        const string Steam = "Watch Steam: within about 15 seconds after the window closes it must stop showing the game as running. ";

        if (stage == 0)
        {
            WriteStage(stageFile, 1);
            c.Instruct((first ?? "") + "Part 1 of 3, the session's last check. Play in this world for a minute, then press " +
                       "skip and quit to the desktop " +
                       "with Esc, Quit. " + Steam + "At the next start, run this check again: it reads what Kiln wrote at " +
                       "the exit.");
            await c.Verdict();
            return;
        }

        var exit = stage == 1 ? "Esc, Quit" : "Alt+F4 at the main menu";
        var found = ReadUnloadLines(c, since, exit);
        if (stage == 1)
        {
            WriteStage(stageFile, 2);
            c.Instruct($"Part 2 of 3. What Kiln wrote at the last exit ({exit}): {found}. Did Steam stop showing the game " +
                       "within about 15 s? Remember the answer. Run this part as the session's last check: press skip, leave this " +
                       "world to the main menu and press Alt+F4 there. " + Steam + "At the next start, run this check again.");
            await c.Verdict();
            return;
        }

        try { File.Delete(stageFile); } catch (IOException) { }
        c.Instruct($"Part 3 of 3. What Kiln wrote at the last exit ({exit}): {found}. The assertions of part 2 are in the " +
                   "ledger's previous entry of this check. Did Steam stop showing the game within about 15 s both times, " +
                   "and are all assertions of both parts ok? On a fail, give the exit code or the crash report's name in " +
                   "the note.");
        await c.Verdict();
    }

    private static (int Stage, DateTime Since) ReadStage(string file)
    {
        try
        {
            var parts = File.ReadAllText(file).Split(' ', 2, StringSplitOptions.TrimEntries);
            if (int.TryParse(parts[0], out var s) && s is 1 or 2 &&
                DateTime.TryParse(parts.ElementAtOrDefault(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                return (s, t);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (0, default);
    }

    private static void WriteStage(string file, int stage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"{stage} {DateTime.UtcNow:O}");
    }

    /// <summary>The kiln folder: Kiln.Api lives in kiln/host/ (KILN_ROOT overrides, as for Kiln itself).</summary>
    private static string KilnFolder()
    {
        var env = Environment.GetEnvironmentVariable("KILN_ROOT");
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
        var dir = Path.GetDirectoryName(typeof(Mod).Assembly.Location) ?? "";
        return string.Equals(Path.GetFileName(dir), "host", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dir) ?? dir
            : Path.Combine(dir, "kiln");
    }

    /// <summary>Reads the last lines of the previous session's kiln.log and asserts the three unload lines. Returns what
    /// was found, in a few words, for the instruction.</summary>
    private static string ReadUnloadLines(CheckContext c, DateTime since, string exit)
    {
        var prev = Path.Combine(KilnFolder(), "logs", "kiln.prev.log");
        if (!File.Exists(prev))
        {
            c.Expect(false, $"{exit}: the previous session's log is there", "kiln/logs/kiln.prev.log not found");
            return "no previous log found";
        }
        c.Expect(File.GetLastWriteTimeUtc(prev) >= since, $"{exit}: the previous log is of the session that quit",
            $"last written {File.GetLastWriteTimeUtc(prev):O}, part started {since:O}");
        string[] tail;
        try
        {
            var all = File.ReadAllLines(prev);
            tail = all.Skip(Math.Max(0, all.Length - 60)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            c.Expect(false, $"{exit}: the previous log can be read", ex.Message);
            return "the previous log could not be read";
        }

        string? Line(string needle) => tail.LastOrDefault(l => l.Contains(needle, StringComparison.Ordinal));
        var exiting = Line("unload: the process is exiting");
        var order = Line("unload: called in this order:");
        var done = Line("unload: done in");
        var abnormal = Line("not a normal exit");

        c.Expect(exiting?.Contains("exit code 0", StringComparison.Ordinal) == true,
            $"{exit}: \"unload: the process is exiting (..., exit code 0, ...)\"", exiting);
        c.Expect(order is not null, $"{exit}: \"unload: called in this order: ...\"", order);
        c.Expect(done?.Contains("(0 faulted, 0 given up, 0 not called)", StringComparison.Ordinal) == true,
            $"{exit}: \"unload: done in N ms (0 faulted, 0 given up, 0 not called)\"", done);
        if (abnormal is not null)
        {
            var code = Regex.Match(abnormal, @"exits with code (-?\w+)");
            c.Expect(false, $"{exit}: a normal exit", code.Success ? $"exit code {code.Groups[1].Value}: {abnormal}" : abnormal);
        }
        var trouble = tail.Where(l => (l.Contains("given up", StringComparison.Ordinal) || l.Contains("faulted", StringComparison.Ordinal))
                                      && !l.Contains("0 faulted, 0 given up", StringComparison.Ordinal)).ToArray();
        c.Expect(trouble.Length == 0, $"{exit}: no faulted or given-up line", trouble.FirstOrDefault());

        if (exiting is null && done is null) return abnormal is not null ? "not a normal exit (see the assertions)" : "no unload lines";
        return done is not null && done.Contains("(0 faulted, 0 given up, 0 not called)", StringComparison.Ordinal) && abnormal is null
            ? "the three unload lines, all clean"
            : "unload lines with a problem (see the assertions)";
    }
}
