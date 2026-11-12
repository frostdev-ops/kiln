// SPDX-License-Identifier: MIT
// The generator's own tests (tools/typegen/tests/run.py: the made-up fixture against its expected output, every
// refusal, the legacy mode), and the rules that keep game-derived files out of the tree and the RS0016 pragma out of
// hand-written code.
using Xunit;

namespace Kiln.Game.Tests;

public class GeneratorTests
{
    // tools/typegen/typegen.py MARKER: the first line of every file the generate mode writes.
    const string Marker = "// kiln-typegen: generated at build time from a game layout dump. Never commit this file.";

    [Fact]
    public void The_generator_tests_pass()
    {
        var work = Path.Combine(AppContext.BaseDirectory, "typegen-work", "run-py");
        var r = GenRepo.Python(GenRepo.Path_("tools", "typegen", "tests", "run.py"), "--work", work);
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains(" passed, 0 failed", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void No_generated_file_is_among_the_sources()
    {
        var root = GenRepo.Root();
        var expected = Path.Combine(root, "managed", "tests", "Kiln.Game.Tests", "Fixtures") + Path.DirectorySeparatorChar;
        var found = new List<string>();
        int seen = 0;
        foreach (var top in new[] { "managed", "tools", "sigdb" })
        {
            var stack = new Stack<string>([Path.Combine(root, top)]);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var n = Path.GetFileName(sub);
                    if (n is "obj" or "bin" or "node_modules" or ".git") continue;
                    if (new FileInfo(sub).LinkTarget != null) continue;
                    stack.Push(sub);
                }
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    seen++;
                    if (f.StartsWith(expected, StringComparison.Ordinal)) continue;  // the made-up expected outputs
                    if (StartsWithMarker(f)) found.Add(Path.GetRelativePath(root, f));
                }
            }
        }
        Assert.True(seen > 100, $"walked only {seen} files");
        Assert.True(found.Count == 0, "generated files among the sources (never commit them):\n" + string.Join("\n", found));
    }

    static bool StartsWithMarker(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            var buf = new byte[Marker.Length];
            int n = s.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            return n == buf.Length && System.Text.Encoding.UTF8.GetString(buf) == Marker;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    [Fact]
    public void The_marker_test_sees_a_generated_file()
    {
        var f = Path.Combine(AppContext.BaseDirectory, "typegen-work", "marker.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, Marker + "\nnamespace X;\n");
        Assert.True(StartsWithMarker(f));
        File.WriteAllText(f, "// SPDX-License-Identifier: MIT\n" + Marker + "\n");
        Assert.False(StartsWithMarker(f));
    }

    [Fact]
    public void No_hand_written_file_of_Kiln_Game_turns_off_RS0016()
    {
        var dir = GenRepo.Path_("managed", "Kiln.Game");
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(dir, f).Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin"))
            .ToList();
        Assert.Contains(files, f => Path.GetFileName(f) == "GameLayoutStamps.cs");
        var bad = files.Where(f => File.ReadAllText(f).Contains("RS0016", StringComparison.Ordinal)).ToList();
        Assert.True(bad.Count == 0, "RS0016 is named in a hand-written file of Kiln.Game: " + string.Join(", ", bad));
        // The props, targets and project may not turn the gate off either.
        foreach (var p in Directory.GetFiles(dir, "*.*proj").Concat(Directory.GetFiles(dir, "*.props")).Concat(Directory.GetFiles(dir, "*.targets")))
            Assert.DoesNotContain("RS0016", File.ReadAllText(p), StringComparison.Ordinal);
    }
}
