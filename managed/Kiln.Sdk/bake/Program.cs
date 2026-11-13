// SPDX-License-Identifier: MIT
// Kiln.Sdk.Bake <mod folder> [--steps N] [--timeout S] [--list FILE] [--temp DIR] [--dry-run]
// Writes into the mod folder what its code defines and the game reads only at start (Baker.cs, docs/kiln/modding/baked.md).
// Lines for MSBuild: "<folder>/kiln.mod.json: warning KILN030: ..." and "... error KILN032: ...", which Exec turns into a
// build warning or error. Exit codes: 0 done (baked, or a mod that does not load without the game: a note); 1 the bake
// failed (what an earlier bake wrote is removed); 2 bad arguments; 4 it did not end within --timeout seconds. The process
// always ends with Environment.Exit: a foreground thread a mod's OnLoad left running cannot keep it (or the build) alive.
using Kiln.Sdk.Bake;

string? folder = null, temp = null, list = null;
var steps = 10;
var timeout = 120;
var dry = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--steps" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n is > 0 and <= 1000:
            steps = n;
            i++;
            break;
        case "--timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t) && t is > 0 and <= 3600:
            timeout = t;
            i++;
            break;
        case "--list" when i + 1 < args.Length:
            list = args[++i];
            break;
        case "--temp" when i + 1 < args.Length:
            temp = args[++i];
            break;
        case "--dry-run":
            dry = true;
            break;
        default:
            if (folder is null && !args[i].StartsWith("--", StringComparison.Ordinal)) folder = args[i];
            else
            {
                Console.Error.WriteLine($"Kiln.Sdk.Bake: unknown argument {args[i]}; usage: Kiln.Sdk.Bake <mod folder> [--steps N] [--timeout S] [--list FILE] [--temp DIR] [--dry-run]");
                Environment.Exit(2);
            }
            break;
    }
}
if (folder is null || !File.Exists(Path.Combine(folder, "kiln.mod.json")))
{
    Console.Error.WriteLine($"Kiln.Sdk.Bake: {(folder is null ? "no mod folder given" : $"{folder} has no kiln.mod.json")}");
    Environment.Exit(2);
}
var origin = Path.Combine(Path.GetFullPath(folder), "kiln.mod.json");
Environment.Exit(Bake.Run(folder, origin, new BakeOptions { Steps = steps, TempDirectory = temp, DryRun = dry }, TimeSpan.FromSeconds(timeout), list));

namespace Kiln.Sdk.Bake
{
    internal static class Bake
    {
        /// <summary>One bake with a deadline; the exit code (see the file's head).</summary>
        public static int Run(string folder, string origin, BakeOptions options, TimeSpan deadline, string? list)
        {
            using var watchdog = new Timer(_ =>
            {
                // nothing of an earlier bake may stay behind a bake that did not end (it would win at start)
                var removed = CleanQuietly(folder, options);
                Console.WriteLine($"{origin}: error KILN032: the bake did not end within {deadline.TotalSeconds:0} s (an OnLoad or a step that does not " +
                                  "return?): stopped" + (removed.Count > 0 ? $"; removed what an earlier bake wrote: {string.Join(", ", removed)}" : "") +
                                  ". Set KilnBake=false to build without it, or KilnBakeTimeoutSeconds for more time");
                Console.Out.Flush();
                Environment.Exit(4);
            }, null, deadline, Timeout.InfiniteTimeSpan);
            BakeResult result;
            try
            {
                result = Baker.Run(folder, options);
            }
            catch (Exception e)
            {
                var removed = CleanQuietly(folder, options);
                Console.WriteLine($"{origin}: error KILN032: the bake failed ({e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")})" +
                                  (removed.Count > 0 ? $"; removed what an earlier bake wrote: {string.Join(", ", removed)}" : "") +
                                  ". Set KilnBake=false to build without it");
                return 1;
            }
            foreach (var w in result.Warnings) Console.WriteLine($"{origin}: warning KILN030: {result.ModId}: {w.ReplaceLineEndings(" ")}");
            foreach (var n in result.Notes) Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: {n}");
            var dry = options.DryRun;
            foreach (var f in result.Written) Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: {(dry ? "would write" : "baked")} {f}");
            foreach (var f in result.Unchanged) Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: {f} is up to date");
            foreach (var f in result.Removed) Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: {(dry ? "would remove" : "removed")} {f} (nothing of the code's is in it now)");
            if (result.Written.Count + result.Unchanged.Count + result.Removed.Count == 0 && result.Warnings.Count == 0 && result.Notes.Count == 0)
                Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: its code defines nothing the game reads only at start");
            if (list is not null && !dry)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(list))!);
                File.WriteAllLines(list, Baker.Outputs(result));
            }
            return 0;
        }

        /// <summary>What an earlier bake wrote, removed (the author's files stay); what was removed. Never throws.</summary>
        private static List<string> CleanQuietly(string folder, BakeOptions options)
        {
            var removed = new BakeResult();
            try
            {
                if (Baker.ModIdOf(folder) is { } id) Baker.Clean(Path.GetFullPath(folder), id, removed, options);
            }
            catch (Exception)
            {
                // the error the caller prints is what counts; KilnBake's clean-up task tries again
            }
            return removed.Removed;
        }
    }
}
