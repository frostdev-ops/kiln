// SPDX-License-Identifier: MIT
// Kiln.Sdk.Bake <mod folder> [--steps N] [--temp DIR] [--dry-run]
// Writes into the mod folder what its code defines and the game reads only at start (Baker.cs, docs/kiln/modding/baked.md).
// Lines for MSBuild: a warning is "<folder>/kiln.mod.json: warning KILN030: ...", which Exec turns into a build warning. Exit 0
// unless the arguments or the folder are wrong (2): a bake that cannot run leaves the mod as a build without the bake would.
using Kiln.Sdk.Bake;

string? folder = null;
var steps = 10;
string? temp = null;
var dry = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--steps" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n is > 0 and <= 1000:
            steps = n;
            i++;
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
                Console.Error.WriteLine($"Kiln.Sdk.Bake: unknown argument {args[i]}; usage: Kiln.Sdk.Bake <mod folder> [--steps N] [--temp DIR] [--dry-run]");
                return 2;
            }
            break;
    }
}
if (folder is null || !File.Exists(Path.Combine(folder, "kiln.mod.json")))
{
    Console.Error.WriteLine($"Kiln.Sdk.Bake: {(folder is null ? "no mod folder given" : $"{folder} has no kiln.mod.json")}");
    return 2;
}
var origin = Path.Combine(Path.GetFullPath(folder), "kiln.mod.json");
BakeResult result;
try
{
    result = Baker.Run(folder, new BakeOptions { Steps = steps, TempDirectory = temp, DryRun = dry });
}
catch (Exception e)
{
    Console.WriteLine($"{origin}: warning KILN030: the bake did not run ({e.GetType().Name}: {e.Message}): nothing is baked, and what the code defines is there from the next start");
    return 0;
}
foreach (var w in result.Warnings) Console.WriteLine($"{origin}: warning KILN030: {result.ModId}: {w.ReplaceLineEndings(" ")}");
foreach (var n in result.Notes) Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: {n}");
foreach (var f in result.Written) Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: {(dry ? "would write" : "baked")} {f}");
foreach (var f in result.Unchanged) Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: {f} is up to date");
foreach (var f in result.Removed) Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: {(dry ? "would remove" : "removed")} {f} (the code no longer defines any)");
if (result.Written.Count + result.Unchanged.Count + result.Removed.Count == 0 && result.Warnings.Count == 0 && result.Notes.Count == 0)
    Console.WriteLine($"Kiln.Sdk.Bake: {result.ModId}: its code defines nothing the game reads only at start");
return 0;
